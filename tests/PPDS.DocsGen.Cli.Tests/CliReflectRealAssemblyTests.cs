using FluentAssertions;
using PPDS.DocsGen.Cli;
using PPDS.DocsGen.Common;
using Xunit;

namespace PPDS.DocsGen.Cli.Tests;

/// <summary>
/// Runs the generator against the real built CLI (ppds.dll) to prove generated reference pages show the same
/// short aliases as production <c>--help</c> (alias policies applied, #1536).
/// </summary>
public sealed class CliReflectRealAssemblyTests
{
    private static string RealCliPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "PPDS.Cli")))
        {
            dir = dir.Parent;
        }

        dir.Should().NotBeNull("the repository root must be above the test output directory");
        var configuration = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}")
            ? "Release" : "Debug";
        var path = Path.Combine(dir!.FullName, "src", "PPDS.Cli", "bin", configuration, "net8.0", "ppds.dll");
        File.Exists(path).Should().BeTrue($"the CLI must be built (ProjectReference) at {path}");
        return path;
    }

    private static async Task<GenerationResult> GenerateAsync() =>
        await new CliReferenceGenerator().GenerateAsync(
            new GenerationInput(RealCliPath(), Path.Combine(Path.GetTempPath(), "ppds-cli-reflect-" + Guid.NewGuid().ToString("N"))),
            CancellationToken.None);

    [Theory]
    [InlineData("views/get.md", "`-v`", "--view", "--verbose")]
    [InlineData("plugins/unregister.md", "`-f`", "--force", "--output-format")]
    public async Task GeneratedPage_ListsEachShortAliasOnce(string page, string alias, string owner, string shared)
    {
        var result = await GenerateAsync();
        var contents = result.Files.Single(f => f.RelativePath.Replace('\\', '/') == page).Contents;

        // Alias appears on exactly one row: the command-specific option that owns it.
        contents.Split('\n').Where(l => l.Contains(alias, StringComparison.Ordinal)).Should().ContainSingle()
            .Which.Should().Contain($"`{owner}`");
        var sharedRow = contents.Split('\n').Single(l => l.StartsWith($"| `{shared}`", StringComparison.Ordinal));
        sharedRow.Should().NotContain(alias);
    }
}
