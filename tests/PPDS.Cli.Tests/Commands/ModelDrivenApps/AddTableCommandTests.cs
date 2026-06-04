using System.CommandLine;
using PPDS.Cli.Commands.ModelDrivenApps;
using Xunit;

namespace PPDS.Cli.Tests.Commands.ModelDrivenApps;

public class AddTableCommandTests
{
    private readonly Command _command;

    public AddTableCommandTests()
    {
        _command = AddTableCommand.Create();
    }

    [Fact]
    public void Create_ReturnsCommandWithCorrectName()
    {
        Assert.Equal("add-table", _command.Name);
    }

    [Fact]
    public void Create_HasDescription()
    {
        Assert.False(string.IsNullOrEmpty(_command.Description));
    }

    [Fact]
    public void Create_HasAppOption()
    {
        Assert.Contains(_command.Options, o => o.Name == "--app");
    }

    [Fact]
    public void Create_HasGroupOption()
    {
        Assert.Contains(_command.Options, o => o.Name == "--group");
    }

    [Fact]
    public void Create_HasAreaOption()
    {
        Assert.Contains(_command.Options, o => o.Name == "--area");
    }

    [Fact]
    public void Create_HasTitleOption()
    {
        Assert.Contains(_command.Options, o => o.Name == "--title");
    }

    [Fact]
    public void Create_HasSolutionOption()
    {
        Assert.Contains(_command.Options, o => o.Name == "--solution");
    }

    [Fact]
    public void Create_HasPublishOption()
    {
        Assert.Contains(_command.Options, o => o.Name == "--publish");
    }

    [Fact]
    public void Create_HasEntitiesArgument()
    {
        Assert.Single(_command.Arguments);
        Assert.Equal("entities", _command.Arguments[0].Name);
    }
}
