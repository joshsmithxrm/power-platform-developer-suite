using System.CommandLine;
using PPDS.Cli.Commands.ModelDrivenApps;
using Xunit;

namespace PPDS.Cli.Tests.Commands.ModelDrivenApps;

public class SetChartsCommandTests
{
    private readonly Command _command;

    public SetChartsCommandTests()
    {
        _command = SetChartsCommand.Create();
    }

    [Fact]
    public void Create_ReturnsCommandWithCorrectName()
    {
        Assert.Equal("set-charts", _command.Name);
    }

    [Fact]
    public void Create_HasAllChartAndEntityOptions()
    {
        Assert.Contains(_command.Options, o => o.Name == "--app");
        Assert.Contains(_command.Options, o => o.Name == "--entity");
        Assert.Contains(_command.Options, o => o.Name == "--all");
        Assert.Contains(_command.Options, o => o.Name == "--chart");
    }

    [Fact]
    public void Create_DescriptionMentionsRequirement()
    {
        Assert.True(
            _command.Description!.Contains("--all", StringComparison.OrdinalIgnoreCase) ||
            _command.Description!.Contains("chart", StringComparison.OrdinalIgnoreCase));
    }
}
