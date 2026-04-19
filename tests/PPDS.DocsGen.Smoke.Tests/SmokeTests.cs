using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;
using PPDS.DocsGen.Smoke;
using Xunit;

namespace PPDS.DocsGen.Smoke.Tests;

/// <summary>
/// End-to-end smoke tests. Each test invokes the compiled <c>smoke</c>
/// executable as a subprocess against a committed markdown fixture and a
/// runtime-assembly-only reference directory, then parses the JSON summary
/// on stdout. Running the real binary (rather than calling
/// <see cref="SmokeRunner.Run"/> in-process) also covers
/// <c>Program.cs</c> arg parsing, exit code selection, and I1 stdout/stderr
/// channel separation.
/// </summary>
public class SmokeTests
{
    private static string TestAssemblyDir =>
        Path.GetDirectoryName(typeof(SmokeTests).Assembly.Location)!;

    /// <summary>Directory containing the built <c>smoke.dll</c>.</summary>
    private static string SmokeDllPath
    {
        get
        {
            // ProjectReference copies smoke's output next to the test assembly.
            var path = Path.Combine(TestAssemblyDir, "smoke.dll");
            if (File.Exists(path))
            {
                return path;
            }

            // Fallback: resolve via smoke's own bin dir relative to the repo
            // so the test remains runnable outside `dotnet test`.
            var fallback = Path.GetFullPath(Path.Combine(
                TestAssemblyDir,
                "..", "..", "..", "..", "..",
                "scripts", "docs-gen", "smoke", "bin", "Debug", "net8.0", "smoke.dll"));
            return fallback;
        }
    }

    /// <summary>Runtime-only reference directory. Every .NET install keeps its
    /// runtime assemblies beside <c>System.Runtime.dll</c>; the smoke tool
    /// also pulls in <c>TRUSTED_PLATFORM_ASSEMBLIES</c>, so this is a
    /// minimal-but-sufficient reference set for fixtures that depend only on
    /// <c>System.*</c>.</summary>
    private static string RuntimeAssembliesDir =>
        Path.GetDirectoryName(typeof(object).Assembly.Location)!;

    private static string FixtureRoot(string name) =>
        Path.Combine(TestAssemblyDir, "Fixtures", name);

    private record ProcessResult(int ExitCode, string Stdout, string Stderr);

    private record SmokeSummary(
        int total,
        int passed,
        int failed,
        int skipped,
        SmokeFailure[] failures);

    private record SmokeFailure(string file, int line, string message);

    private static ProcessResult RunSmoke(string docsPath, string assembliesDir)
    {
        File.Exists(SmokeDllPath).Should().BeTrue(
            because: $"smoke.dll should be on disk at {SmokeDllPath}");

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("exec");
        psi.ArgumentList.Add(SmokeDllPath);
        psi.ArgumentList.Add("--docs-path");
        psi.ArgumentList.Add(docsPath);
        psi.ArgumentList.Add("--assemblies");
        psi.ArgumentList.Add(assembliesDir);

        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit(60_000);
        return new ProcessResult(p.ExitCode, stdout, stderr);
    }

    private static SmokeSummary ParseSummary(string stdout)
    {
        // JSON summary is printed as the final line; other stdout is empty.
        var trimmed = stdout.Trim();
        trimmed.Should().NotBeEmpty(
            because: "smoke should emit a JSON summary on stdout");

        return JsonSerializer.Deserialize<SmokeSummary>(trimmed, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        })!;
    }

    /// <summary>AC-20: a valid fenced block compiles successfully against the
    /// supplied reference set and the summary reports exactly one passed
    /// block.</summary>
    [Fact]
    public void CompilesValidFencedBlock()
    {
        var result = RunSmoke(FixtureRoot("ValidBlock"), RuntimeAssembliesDir);

        result.ExitCode.Should().Be(0, because: $"stderr was: {result.Stderr}");

        var summary = ParseSummary(result.Stdout);
        summary.total.Should().Be(1);
        summary.passed.Should().Be(1);
        summary.failed.Should().Be(0);
        summary.skipped.Should().Be(0);
        summary.failures.Should().BeEmpty();
    }

    /// <summary>AC-21: a block referencing a nonexistent type fails with a
    /// CS-prefixed diagnostic. The failure must carry the original markdown
    /// file path and a line number that falls within the block's line range.
    /// </summary>
    [Fact]
    public void ReportsCompileErrorWithLocation()
    {
        var fixtureDir = FixtureRoot("InvalidBlock");
        var result = RunSmoke(fixtureDir, RuntimeAssembliesDir);

        result.ExitCode.Should().Be(1, because: $"stderr was: {result.Stderr}");

        var summary = ParseSummary(result.Stdout);
        summary.total.Should().Be(1);
        summary.failed.Should().Be(1);
        summary.failures.Should().HaveCount(c => c >= 1);

        var failure = summary.failures[0];
        failure.file.Should().EndWith("invalid.md");
        failure.line.Should().BeGreaterThan(0);
        failure.message.Should().MatchRegex(@"^CS\d{4}:",
            because: "the diagnostic id should be a CS-prefixed code");
        failure.message.Should().Contain(
            "ThisTypeDoesNotExist",
            because: "the diagnostic should name the unresolved identifier");
    }

    /// <summary>AC-22: an opening fence carrying <c>// ignore-smoke</c> in its
    /// info string skips the block without attempting compilation.</summary>
    [Fact]
    public void HonorsIgnoreMarker()
    {
        var result = RunSmoke(FixtureRoot("IgnoredBlock"), RuntimeAssembliesDir);

        result.ExitCode.Should().Be(0, because: $"stderr was: {result.Stderr}");

        var summary = ParseSummary(result.Stdout);
        summary.total.Should().Be(1);
        summary.passed.Should().Be(0);
        summary.failed.Should().Be(0);
        summary.skipped.Should().Be(1);
    }

    /// <summary>All three wrapping forms compile successfully in a single
    /// run — verifies the content-based wrapping heuristic is correct for
    /// each shape (complete file, top-level statements, method body).
    /// </summary>
    [Fact]
    public void HandlesAllThreeWrappingForms()
    {
        var result = RunSmoke(FixtureRoot("AllThreeForms"), RuntimeAssembliesDir);

        result.ExitCode.Should().Be(0, because: $"stderr was: {result.Stderr}");

        var summary = ParseSummary(result.Stdout);
        summary.total.Should().Be(3);
        summary.passed.Should().Be(3);
        summary.failed.Should().Be(0);
        summary.skipped.Should().Be(0);
    }
}
