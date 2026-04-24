using System.CommandLine;
using PPDS.Cli.Commands.Solutions;
using Xunit;

namespace PPDS.Cli.Tests.Commands.Solutions;

public class SolutionsCommandGroupTests
{
    private readonly Command _command;

    public SolutionsCommandGroupTests()
    {
        _command = SolutionsCommandGroup.Create();
    }

    #region Command Structure Tests

    [Fact]
    public void Create_ReturnsCommandWithCorrectName()
    {
        Assert.Equal("solutions", _command.Name);
    }

    [Fact]
    public void Create_ReturnsCommandWithDescription()
    {
        Assert.Contains("solution", _command.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Create_HasListSubcommand()
    {
        var subcommand = _command.Subcommands.FirstOrDefault(c => c.Name == "list");
        Assert.NotNull(subcommand);
    }

    [Fact]
    public void Create_HasGetSubcommand()
    {
        var subcommand = _command.Subcommands.FirstOrDefault(c => c.Name == "get");
        Assert.NotNull(subcommand);
    }

    [Fact]
    public void Create_HasExportSubcommand()
    {
        var subcommand = _command.Subcommands.FirstOrDefault(c => c.Name == "export");
        Assert.NotNull(subcommand);
    }

    [Fact]
    public void Create_HasImportSubcommand()
    {
        var subcommand = _command.Subcommands.FirstOrDefault(c => c.Name == "import");
        Assert.NotNull(subcommand);
    }

    [Fact]
    public void Create_HasComponentsSubcommand()
    {
        var subcommand = _command.Subcommands.FirstOrDefault(c => c.Name == "components");
        Assert.NotNull(subcommand);
    }

    [Fact]
    public void Create_HasPublishSubcommand()
    {
        var subcommand = _command.Subcommands.FirstOrDefault(c => c.Name == "publish");
        Assert.NotNull(subcommand);
    }

    [Fact]
    public void Create_HasUrlSubcommand()
    {
        var subcommand = _command.Subcommands.FirstOrDefault(c => c.Name == "url");
        Assert.NotNull(subcommand);
    }

    [Fact]
    public void Create_HasSevenSubcommands()
    {
        Assert.Equal(7, _command.Subcommands.Count);
    }

    #endregion

    #region Shared Options Tests

    [Fact]
    public void ProfileOption_HasCorrectName()
    {
        Assert.Equal("--profile", SolutionsCommandGroup.ProfileOption.Name);
    }

    [Fact]
    public void ProfileOption_HasShortAlias()
    {
        Assert.Contains("-p", SolutionsCommandGroup.ProfileOption.Aliases);
    }

    [Fact]
    public void EnvironmentOption_HasCorrectName()
    {
        Assert.Equal("--environment", SolutionsCommandGroup.EnvironmentOption.Name);
    }

    [Fact]
    public void EnvironmentOption_HasShortAlias()
    {
        Assert.Contains("-e", SolutionsCommandGroup.EnvironmentOption.Aliases);
    }

    #endregion

    #region L1 Regression — no duplicate short aliases

    [Fact]
    public void ListSubcommand_NoTwoOptions_ShareAShortAlias()
    {
        var listCommand = _command.Subcommands.First(c => c.Name == "list");
        var aliases = listCommand.Options
            .SelectMany(o => o.Aliases)
            .Where(a => a.StartsWith("-") && !a.StartsWith("--"))
            .ToList();
        var duplicates = aliases.GroupBy(a => a).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.Empty(duplicates);
    }

    [Fact]
    public void ListSubcommand_FilterOption_HasNoFShortAlias()
    {
        // L1: -f is reserved for --output-format; --filter uses long form only.
        var listCommand = _command.Subcommands.First(c => c.Name == "list");
        var filterOption = listCommand.Options.FirstOrDefault(o => o.Name == "--filter");
        Assert.NotNull(filterOption);
        Assert.DoesNotContain("-f", filterOption!.Aliases);
    }

    #endregion
}
