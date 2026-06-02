using System.CommandLine;
using PPDS.Cli.Commands.ModelDrivenApps;
using Xunit;

namespace PPDS.Cli.Tests.Commands.ModelDrivenApps;

public class HelpTests
{
    private readonly Command _commandGroup;

    public HelpTests()
    {
        _commandGroup = ModelDrivenAppCommandGroup.Create();
    }

    [Fact]
    public void Help_ListsSubcommands()
    {
        var names = _commandGroup.Subcommands.Select(c => c.Name).ToList();

        Assert.Contains("list", names);
        Assert.Contains("get", names);
        Assert.Contains("sitemap", names);
        Assert.Contains("set-sitemap-xml", names);
        Assert.Contains("add-table", names);
        Assert.Contains("remove-table", names);
        Assert.Contains("set-forms", names);
        Assert.Contains("set-views", names);
        Assert.Contains("set-charts", names);
        Assert.Equal(9, names.Count);
    }

    [Fact]
    public void Help_CommandGroupHasDescription()
    {
        Assert.NotNull(_commandGroup.Description);
        Assert.NotEmpty(_commandGroup.Description);
    }

    [Fact]
    public void Help_SubcommandHelp()
    {
        foreach (var sub in _commandGroup.Subcommands)
        {
            Assert.False(string.IsNullOrEmpty(sub.Description),
                $"Subcommand '{sub.Name}' has no description.");
        }
    }

    [Fact]
    public void Help_SetComponentsRequirement()
    {
        var setForms = _commandGroup.Subcommands.First(c => c.Name == "set-forms");
        var setViews = _commandGroup.Subcommands.First(c => c.Name == "set-views");
        var setCharts = _commandGroup.Subcommands.First(c => c.Name == "set-charts");

        // Description should mention --all or the specific component option requirement
        Assert.True(
            setForms.Description!.Contains("--all", StringComparison.OrdinalIgnoreCase) ||
            setForms.Description!.Contains("form", StringComparison.OrdinalIgnoreCase),
            "set-forms description should mention --all or form requirement.");

        Assert.True(
            setViews.Description!.Contains("--all", StringComparison.OrdinalIgnoreCase) ||
            setViews.Description!.Contains("view", StringComparison.OrdinalIgnoreCase),
            "set-views description should mention --all or view requirement.");

        Assert.True(
            setCharts.Description!.Contains("--all", StringComparison.OrdinalIgnoreCase) ||
            setCharts.Description!.Contains("chart", StringComparison.OrdinalIgnoreCase),
            "set-charts description should mention --all or chart requirement.");
    }
}
