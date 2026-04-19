using FluentAssertions;
using PPDS.DocsGen.Common;
using PPDS.DocsGen.Mcp;
using Xunit;

namespace PPDS.DocsGen.Mcp.Tests;

/// <summary>
/// Tests for <see cref="McpReferenceGenerator"/>. Covers AC-17 (byte-for-byte
/// golden compare), AC-19 (determinism), and the MCP-specific edge cases:
/// tools without examples emit no Examples section, and sibling
/// [System.ComponentModel.Description] attributes are read correctly.
/// </summary>
public class McpReflectTests
{
    private static string FixtureAssemblyPath()
    {
        // Fixture is built alongside the test project; walk up to locate it.
        var testDir = AppContext.BaseDirectory;
        // testDir = .../PPDS.DocsGen.Mcp.Tests/bin/Debug/net8.0/
        // fixture = .../PPDS.DocsGen.Mcp.Tests/Fixtures/bin/Debug/net8.0/FixtureTools.dll
        var testProjectDir = Path.GetFullPath(Path.Combine(testDir, "..", "..", ".."));
        var candidate = Path.Combine(
            testProjectDir, "Fixtures", "bin", "Debug", "net8.0", "FixtureTools.dll");
        if (!File.Exists(candidate))
        {
            // Also try Release config.
            candidate = Path.Combine(
                testProjectDir, "Fixtures", "bin", "Release", "net8.0", "FixtureTools.dll");
        }
        return candidate;
    }

    private static string ExpectedDir()
    {
        var testDir = AppContext.BaseDirectory;
        var testProjectDir = Path.GetFullPath(Path.Combine(testDir, "..", "..", ".."));
        return Path.Combine(testProjectDir, "Fixtures", "Expected");
    }

    private static async Task<Dictionary<string, string>> GenerateAsync()
    {
        var generator = new McpReferenceGenerator();
        var input = new GenerationInput(FixtureAssemblyPath(), "ignored");
        var result = await generator.GenerateAsync(input, CancellationToken.None);
        return result.Files.ToDictionary(f => f.RelativePath.Replace('\\', '/'), f => f.Contents);
    }

    [Fact]
    public async Task EmitsExpectedMarkdownForFixtureToolSet()
    {
        // AC-17: byte-compare generator output against committed golden fixtures.
        File.Exists(FixtureAssemblyPath()).Should().BeTrue(
            "fixture assembly must be built before running tests");

        var generated = await GenerateAsync();
        var expectedRoot = ExpectedDir();

        foreach (var (relPath, contents) in generated)
        {
            var expectedPath = Path.Combine(expectedRoot, relPath.Replace('/', Path.DirectorySeparatorChar));
            File.Exists(expectedPath).Should().BeTrue(
                $"expected fixture '{expectedPath}' should exist for generated file '{relPath}'");
            var expected = await File.ReadAllTextAsync(expectedPath);
            // Normalize CRLF/LF so tests pass cross-platform. Golden fixtures
            // are stored as-is; we compare logical line sequences.
            Normalize(contents).Should().Be(
                Normalize(expected),
                $"generator output for '{relPath}' must match golden fixture byte-for-byte (AC-17)");
        }
    }

    [Fact]
    public async Task DeterministicOutputAcrossRuns()
    {
        // AC-19 for MCP: generator must produce byte-identical output on repeat runs.
        var first = await GenerateAsync();
        var second = await GenerateAsync();

        first.Keys.Should().BeEquivalentTo(second.Keys);
        foreach (var key in first.Keys)
        {
            first[key].Should().Be(second[key], $"repeat run must produce identical output for '{key}'");
        }
    }

    [Fact]
    public async Task HandlesToolsWithoutExamples()
    {
        // fixture_env_list has no [McpToolExample] → generator must omit the
        // ## Examples section entirely (not placeholder-filled).
        var generated = await GenerateAsync();
        var envListMd = generated["tools/fixture_env_list.md"];
        envListMd.Should().NotContain("## Examples",
            "tools without [McpToolExample] must not emit an Examples heading");
    }

    [Fact]
    public async Task HandlesSiblingDescriptionAttribute()
    {
        // AlphaEchoTool uses [McpServerTool(Name=...)] + sibling [Description(...)]
        // — the generator must pick up the sibling attribute's payload as the
        // tool description (mirroring the PPDS016 analyzer's behavior).
        var generated = await GenerateAsync();
        var alphaMd = generated["tools/fixture_alpha_echo.md"];
        alphaMd.Should().Contain("Echoes the given message back to the caller.",
            "sibling [System.ComponentModel.Description] must be surfaced as the tool description");
    }

    private static string Normalize(string s) =>
        s.Replace("\r\n", "\n", StringComparison.Ordinal);
}
