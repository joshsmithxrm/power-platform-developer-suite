using System.CommandLine;
using PPDS.Cli.Commands.ModelDrivenApps;
using Xunit;

namespace PPDS.Cli.Tests.Commands.ModelDrivenApps;

public class RemoveTableCommandTests
{
    private readonly Command _command;

    public RemoveTableCommandTests()
    {
        _command = RemoveTableCommand.Create();
    }

    [Fact]
    public void Create_ReturnsCommandWithCorrectName()
    {
        Assert.Equal("remove-table", _command.Name);
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
    public void Create_HasEntityOption()
    {
        Assert.Contains(_command.Options, o => o.Name == "--entity");
    }

    [Fact]
    public void Create_HasSolutionAndPublishOptions()
    {
        Assert.Contains(_command.Options, o => o.Name == "--solution");
        Assert.Contains(_command.Options, o => o.Name == "--publish");
    }
}
