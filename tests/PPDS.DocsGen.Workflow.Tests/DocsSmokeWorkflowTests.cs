using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace PPDS.DocsGen.Workflow.Tests;

/// <summary>
/// Tests the Phase-4 smoke tool as invoked by
/// <c>.github/workflows/docs-smoke.yml</c>. The workflow orchestration is a
/// thin checkout/build/run shell glue, so we drive the underlying
/// <c>smoke.dll</c> with the same arguments the workflow passes and assert
/// the failure mode.
/// </summary>
public class DocsSmokeWorkflowTests
{
    private static string TestAssemblyDir =>
        Path.GetDirectoryName(typeof(DocsSmokeWorkflowTests).Assembly.Location)!;

    /// <summary>Smoke copies its output next to the test assembly thanks to
    /// the ProjectReference; we fall back to the build output dir if
    /// necessary.</summary>
    private static string SmokeDllPath
    {
        get
        {
            var local = Path.Combine(TestAssemblyDir, "smoke.dll");
            if (File.Exists(local))
            {
                return local;
            }

            var fallback = Path.GetFullPath(Path.Combine(
                TestAssemblyDir, "..", "..", "..", "..", "..",
                "scripts", "docs-gen", "smoke", "bin", "Debug", "net8.0", "smoke.dll"));
            return fallback;
        }
    }

    /// <summary>Runtime-only reference dir — matches what the workflow would
    /// supply as <c>--assemblies artifacts/bin</c> minus the PPDS DLLs. The
    /// smoke tool also unions in <c>TRUSTED_PLATFORM_ASSEMBLIES</c>, so this
    /// set is sufficient for a block that only names built-ins.</summary>
    private static string RuntimeAssembliesDir =>
        Path.GetDirectoryName(typeof(object).Assembly.Location)!;

    private record ProcessResult(int ExitCode, string Stdout, string Stderr);

    private record SmokeSummary(
        int total,
        int passed,
        int failed,
        int skipped,
        SmokeFailure[] failures);

    private record SmokeFailure(string file, int line, string message);

    /// <summary>
    /// AC-30: a bad fenced block in a fixture "docs" tree causes the smoke
    /// tool to exit non-zero with the offending file path + a line number
    /// located within the fence block.
    /// </summary>
    [Fact]
    public void FailsOnBadBlockViaWorkflowCall()
    {
        File.Exists(SmokeDllPath).Should().BeTrue(
            because: $"smoke.dll should be built alongside the test at {SmokeDllPath}");

        var fixtureRoot = Path.Combine(TestAssemblyDir, "Fixtures", "smoke");
        Directory.Exists(Path.Combine(fixtureRoot, "docs")).Should().BeTrue(
            because: "the smoke fixture tree ships with the test output");

        // Use an empty temp directory for --assemblies. The smoke tool
        // already pulls in TRUSTED_PLATFORM_ASSEMBLIES, so this is
        // sufficient to resolve System.Console for the block we wrap, and
        // it avoids scanning the runtime directory for native DLLs which
        // would produce spurious CS0009 noise.
        var emptyAssembliesDir = Path.Combine(
            Path.GetTempPath(),
            "ppds-docsgen-smoke-empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(emptyAssembliesDir);

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
        psi.ArgumentList.Add(fixtureRoot);
        psi.ArgumentList.Add("--assemblies");
        psi.ArgumentList.Add(emptyAssembliesDir);

        using var p = Process.Start(psi)!;
        // Drain stdout and stderr concurrently — the smoke tool emits a
        // large JSON summary plus one stderr line per CS diagnostic, and
        // synchronous ReadToEnd on one stream while the other fills can
        // deadlock the child on Windows.
        var stdoutTask = p.StandardOutput.ReadToEndAsync();
        var stderrTask = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(60_000))
        {
            try { p.Kill(entireProcessTree: true); }
            catch { /* best effort */ }
            throw new TimeoutException("smoke subprocess did not exit in 60s");
        }

        var stdout = stdoutTask.GetAwaiter().GetResult();
        var stderr = stderrTask.GetAwaiter().GetResult();

        p.ExitCode.Should().Be(1,
            because: $"bad block should cause non-zero exit. stderr: {stderr}");

        // The JSON summary on stdout carries the failure record — the
        // workflow parses it to surface a check-run annotation.
        var trimmed = stdout.Trim();
        trimmed.Should().NotBeEmpty();
        var summary = JsonSerializer.Deserialize<SmokeSummary>(
            trimmed,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        summary.total.Should().Be(1);
        summary.failed.Should().Be(1);
        summary.failures.Should().HaveCountGreaterThan(0);

        var failure = summary.failures[0];
        failure.file.Should().EndWith("bad-block.md");
        failure.line.Should().BeGreaterThan(0);

        // The workflow's check-run annotation pastes the stderr line verbatim
        // into its "details" — confirm stderr carries the file path + a
        // location within the block body.
        stderr.Should().Contain("bad-block.md");
        stderr.Should().Contain("ThisTypeDoesNotExistInTheAssemblies");

        try { Directory.Delete(emptyAssembliesDir, recursive: true); }
        catch { /* best effort */ }
    }
}
