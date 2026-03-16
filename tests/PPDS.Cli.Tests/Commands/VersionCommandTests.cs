using System.CommandLine;
using PPDS.Cli.Commands;
using Xunit;

namespace PPDS.Cli.Tests.Commands;

/// <summary>
/// Tests for the structure of the 'version' command.
/// </summary>
public class VersionCommandTests
{
    private readonly Command _command;

    public VersionCommandTests()
    {
        _command = VersionCommand.Create();
    }

    [Fact]
    public void Create_ReturnsCommandWithCorrectName()
    {
        Assert.Equal("version", _command.Name);
    }

    [Fact]
    public void Create_ReturnsCommandWithDescription()
    {
        Assert.NotNull(_command.Description);
        Assert.NotEmpty(_command.Description);
    }

    [Fact]
    public void Create_HasNoSubcommands()
    {
        // version is a standalone command, not a command group
        Assert.Empty(_command.Subcommands);
    }

    [Fact]
    public void Create_HasCheckOption()
    {
        var checkOption = _command.Options.FirstOrDefault(o => o.Name == "--check");
        Assert.NotNull(checkOption);
    }

    [Fact]
    public void Create_CheckOption_IsNotRequired()
    {
        var checkOption = _command.Options.FirstOrDefault(o => o.Name == "--check");
        Assert.NotNull(checkOption);
        Assert.False(checkOption!.Required);
    }

    [Fact]
    public void Create_HasAction()
    {
        // The command should have a handler set
        Assert.NotNull(_command.Action);
    }

    [Fact]
    public void VersionCommand_HasUpdateOption()
    {
        var command = VersionCommand.Create();
        Assert.Contains(command.Options, o => o.Name == "--update");
    }

    [Fact]
    public void VersionCommand_HasStableOption()
    {
        var command = VersionCommand.Create();
        Assert.Contains(command.Options, o => o.Name == "--stable");
    }

    [Fact]
    public void VersionCommand_HasPreReleaseOption()
    {
        var command = VersionCommand.Create();
        Assert.Contains(command.Options, o => o.Name == "--prerelease");
    }

    [Fact]
    public void VersionCommand_HasYesOption()
    {
        var command = VersionCommand.Create();
        Assert.Contains(command.Options, o => o.Name == "--yes");
    }
}
