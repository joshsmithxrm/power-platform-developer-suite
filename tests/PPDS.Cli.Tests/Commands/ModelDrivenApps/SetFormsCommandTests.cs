using System.CommandLine;
using PPDS.Cli.Commands.ModelDrivenApps;
using Xunit;

namespace PPDS.Cli.Tests.Commands.ModelDrivenApps;

public class SetFormsCommandTests
{
    private readonly Command _command;

    public SetFormsCommandTests()
    {
        _command = SetFormsCommand.Create();
    }

    [Fact]
    public void Create_ReturnsCommandWithCorrectName()
    {
        Assert.Equal("set-forms", _command.Name);
    }

    [Fact]
    public void Create_HasDescription()
    {
        Assert.False(string.IsNullOrEmpty(_command.Description));
    }

    [Fact]
    public void Create_HasAppEntityAllAndFormOptions()
    {
        Assert.Contains(_command.Options, o => o.Name == "--app");
        Assert.Contains(_command.Options, o => o.Name == "--entity");
        Assert.Contains(_command.Options, o => o.Name == "--all");
        Assert.Contains(_command.Options, o => o.Name == "--form");
    }

    [Fact]
    public void Create_DescriptionMentionsAllOrFormRequirement()
    {
        Assert.True(
            _command.Description!.Contains("--all", StringComparison.OrdinalIgnoreCase) ||
            _command.Description!.Contains("form", StringComparison.OrdinalIgnoreCase),
            "Description should mention --all or --form requirement.");
    }
}
