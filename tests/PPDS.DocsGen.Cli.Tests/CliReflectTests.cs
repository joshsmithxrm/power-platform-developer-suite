using System.Reflection;
using FluentAssertions;
using PPDS.DocsGen.Cli;
using PPDS.DocsGen.Common;
using Xunit;

namespace PPDS.DocsGen.Cli.Tests;

public class CliReflectTests
{
    /// <summary>
    /// Path to the built fixture assembly. The fixture is a ProjectReference
    /// in the .csproj, so it's always built before tests run and lives next
    /// to the test assembly on disk.
    /// </summary>
    private static string FixtureAssemblyPath
    {
        get
        {
            var testAssemblyDir = Path.GetDirectoryName(typeof(CliReflectTests).Assembly.Location)!;
            return Path.Combine(testAssemblyDir, "PPDS.DocsGen.Cli.Tests.FixtureCli.dll");
        }
    }

    private static string ExpectedDir
    {
        get
        {
            var testAssemblyDir = Path.GetDirectoryName(typeof(CliReflectTests).Assembly.Location)!;
            return Path.Combine(testAssemblyDir, "Fixtures", "Expected");
        }
    }

    private static async Task<GenerationResult> GenerateAsync()
    {
        File.Exists(FixtureAssemblyPath).Should().BeTrue(
            because: $"the fixture assembly should have been built at {FixtureAssemblyPath}");

        var generator = new CliReferenceGenerator();
        var tempOut = Path.Combine(Path.GetTempPath(), "ppds-cli-reflect-" + Guid.NewGuid().ToString("N"));
        var result = await generator.GenerateAsync(
            new GenerationInput(FixtureAssemblyPath, tempOut),
            CancellationToken.None);
        return result;
    }

    /// <summary>
    /// AC-15: emits one markdown file per System.CommandLine leaf command (plus
    /// per-group index) that matches the committed golden-file fixture byte-for-byte.
    /// </summary>
    [Fact]
    public async Task EmitsExpectedMarkdownForFixtureCommandTree()
    {
        var result = await GenerateAsync();

        result.Files.Should().NotBeEmpty();

        Directory.Exists(ExpectedDir).Should().BeTrue(
            because: $"expected-output fixtures should be committed under {ExpectedDir}");

        foreach (var file in result.Files)
        {
            var expectedPath = Path.Combine(ExpectedDir, file.RelativePath);
            File.Exists(expectedPath).Should().BeTrue(
                because: $"missing expected fixture for {file.RelativePath} at {expectedPath}");

            // Normalize line endings when reading from disk so a Windows
            // checkout with autocrlf=true does not spuriously fail this byte
            // comparison. The generator always emits LF (see Lf constant in
            // CliReferenceGenerator).
            var expected = File.ReadAllText(expectedPath).Replace("\r\n", "\n");
            file.Contents.Should().Be(
                expected,
                because: $"generated content for {file.RelativePath} must match the committed golden file byte-for-byte");
        }

        // Also verify: no stray expected files that the generator didn't produce
        // (catches renames that would otherwise silently succeed).
        var producedPaths = result.Files
            .Select(f => f.RelativePath.Replace('\\', '/'))
            .ToHashSet(StringComparer.Ordinal);

        var expectedFiles = Directory.EnumerateFiles(ExpectedDir, "*.md", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(ExpectedDir, p).Replace('\\', '/'))
            .ToList();

        expectedFiles.Should().OnlyContain(
            p => producedPaths.Contains(p),
            because: "every committed expected fixture should correspond to a generated file");
    }

    /// <summary>
    /// AC-19 (CLI surface): byte-identical output across two runs on the same
    /// assembly. Guards against any incidental ordering non-determinism
    /// (reflection enumeration, hashing, DateTime.Now etc.).
    /// </summary>
    [Fact]
    public async Task DeterministicOutputAcrossRuns()
    {
        var first = await GenerateAsync();
        var second = await GenerateAsync();

        first.Files.Should().HaveSameCount(second.Files);

        for (var i = 0; i < first.Files.Count; i++)
        {
            first.Files[i].RelativePath.Should().Be(second.Files[i].RelativePath,
                because: "file ordering must be deterministic");
            first.Files[i].Contents.Should().Be(second.Files[i].Contents,
                because: $"contents of {first.Files[i].RelativePath} must be byte-identical across runs");
        }
    }

    /// <summary>
    /// Leaf commands with <c>Hidden = true</c> must not appear in any emitted
    /// file — including the group index.
    /// </summary>
    [Fact]
    public async Task SkipsHiddenCommands()
    {
        var result = await GenerateAsync();

        result.Files.Should().NotContain(
            f => f.RelativePath.Equals("query/hidden.md", StringComparison.Ordinal),
            because: "the hidden fixture command is marked Hidden=true and must be excluded");

        var queryIndex = result.Files.Single(f => f.RelativePath == "query/_index.md");
        queryIndex.Contents.Should().NotContain(
            "`hidden`",
            because: "the group index must not list the hidden command");
        queryIndex.Contents.Should().NotContain(
            "This command should never appear",
            because: "the hidden command's description must not leak into any index");
    }
}
