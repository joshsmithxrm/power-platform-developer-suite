using System.CommandLine;
using PPDS.Cli.Commands.ModelDrivenApps;
using Xunit;

namespace PPDS.Cli.Tests.Commands.ModelDrivenApps;

public class SetViewsCommandTests
{
    private readonly Command _command;

    public SetViewsCommandTests()
    {
        _command = SetViewsCommand.Create();
    }

    [Fact]
    public void Create_ReturnsCommandWithCorrectName()
    {
        Assert.Equal("set-views", _command.Name);
    }

    [Fact]
    public void Create_HasAllViewAndEntityOptions()
    {
        Assert.Contains(_command.Options, o => o.Name == "--app");
        Assert.Contains(_command.Options, o => o.Name == "--entity");
        Assert.Contains(_command.Options, o => o.Name == "--all");
        Assert.Contains(_command.Options, o => o.Name == "--view");
    }

    [Fact]
    public void Create_DescriptionMentionsRequirement()
    {
        Assert.True(
            _command.Description!.Contains("--all", StringComparison.OrdinalIgnoreCase) ||
            _command.Description!.Contains("view", StringComparison.OrdinalIgnoreCase));
    }
}
