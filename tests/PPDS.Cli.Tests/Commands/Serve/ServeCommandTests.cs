using System.CommandLine;
using PPDS.Cli.Commands.Serve;
using PPDS.Cli.Infrastructure;
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
    public void Create_OnlyHasHiddenQuietOption()
    {
        var option = Assert.Single(_command.Options);
        Assert.Equal("--quiet", option.Name);
        Assert.Contains("-q", option.Aliases);
        Assert.True(option.Hidden);
    }

    [Theory]
    [InlineData("--quiet")]
    [InlineData("-q")]
    public void QuietDaemon_ParsesAndSelectsStartupIoSuppression(string flag)
    {
        var root = new RootCommand();
        root.Subcommands.Add(_command);
        var args = new[] { "serve", flag };
        Assert.Empty(root.Parse(args).Errors);
        Assert.True(StartupUpdateNotifier.IsQuietDaemon(args));
    }

    [Fact]
    public void DefaultDaemon_StillParsesWithoutStartupSuppression()
    {
        var root = new RootCommand();
        root.Subcommands.Add(_command);
        Assert.Empty(root.Parse(new[] { "serve" }).Errors);
        Assert.False(StartupUpdateNotifier.IsQuietDaemon(new[] { "serve" }));
    }

    [Fact]
    public void Create_HasAction()
    {
        // The command should have a handler set
        Assert.NotNull(_command.Action);
    }
}
