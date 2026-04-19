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
    /// AC-15: options must be ordered alphabetically by long name, independent
    /// of source-declaration order or System.CommandLine enumeration order.
    /// The fixture declares <c>--tenant</c> before <c>--force</c>; the
    /// generator must emit them in the reverse (alphabetical) order.
    /// </summary>
    [Fact]
    public async Task OptionsAreAlphabeticalByLongName()
    {
        var result = await GenerateAsync();

        var login = result.Files.Single(f => f.RelativePath == "auth/login.md").Contents;

        var forceIndex = login.IndexOf("`--force`", StringComparison.Ordinal);
        var tenantIndex = login.IndexOf("`--tenant`", StringComparison.Ordinal);

        forceIndex.Should().BeGreaterThan(0, because: "--force must appear in login.md");
        tenantIndex.Should().BeGreaterThan(0, because: "--tenant must appear in login.md");
        forceIndex.Should().BeLessThan(tenantIndex,
            because: "options must be ordered alphabetically by long name (AC-15), "
                + "even though the fixture declares --tenant before --force");
    }

    /// <summary>
    /// Options with <c>Hidden = true</c> must be excluded from the generated
    /// markdown, and the remaining options must still be emitted in
    /// alphabetical order. The fixture declares a hidden <c>--secret</c>
    /// option whose long name sorts between <c>--force</c> and <c>--tenant</c>
    /// — if the filter ran AFTER the sort, the hidden item would still be
    /// dropped, but the filter-before-sort optimization (Gemini PR #828
    /// review) avoids comparing it at all. This test guards both the
    /// exclusion contract AND the alphabetical order it must preserve.
    /// </summary>
    [Fact]
    public async Task ExcludesHiddenOptionsAndPreservesAlphabeticalOrder()
    {
        var result = await GenerateAsync();

        var login = result.Files.Single(f => f.RelativePath == "auth/login.md").Contents;

        login.Should().NotContain(
            "`--secret`",
            because: "options with Hidden=true must be excluded from generated markdown");
        login.Should().NotContain(
            "Internal flag",
            because: "the hidden option's description must not leak into output");

        var forceIndex = login.IndexOf("`--force`", StringComparison.Ordinal);
        var tenantIndex = login.IndexOf("`--tenant`", StringComparison.Ordinal);

        forceIndex.Should().BeGreaterThan(0, because: "--force must still appear after filtering");
        tenantIndex.Should().BeGreaterThan(0, because: "--tenant must still appear after filtering");
        forceIndex.Should().BeLessThan(tenantIndex,
            because: "the surviving non-hidden options must remain in alphabetical order");
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
