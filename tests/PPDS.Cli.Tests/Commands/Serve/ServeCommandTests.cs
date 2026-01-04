using System.CommandLine;
using PPDS.Cli.Commands.Serve;
using Xunit;

namespace PPDS.Cli.Tests.Commands.Serve;

public class ServeCommandTests
{
    private readonly Command _command;

    public ServeCommandTests()
    {
        _command = ServeCommand.Create();
    }

    [Fact]
    public void Create_ReturnsCommandWithCorrectName()
    {
        Assert.Equal("serve", _command.Name);
    }

    [Fact]
    public void Create_ReturnsCommandWithDescription()
    {
        Assert.Contains("daemon", _command.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Create_HasNoSubcommands()
    {
        // serve is a standalone command, not a command group
        Assert.Empty(_command.Subcommands);
    }

    [Fact]
    public void Create_HasNoOptions()
    {
        // Currently serve has no options - it just starts the daemon
        Assert.Empty(_command.Options);
    }

    [Fact]
    public void Create_HasAction()
    {
        // The command should have a handler set
        Assert.NotNull(_command.Action);
    }
}
