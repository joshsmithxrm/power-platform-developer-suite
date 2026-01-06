using System.CommandLine;
using PPDS.Cli.Commands.Connections;
using Xunit;

namespace PPDS.Cli.Tests.Commands.Connections;

public class ConnectionsCommandGroupTests
{
    private readonly Command _command;

    public ConnectionsCommandGroupTests()
    {
        _command = ConnectionsCommandGroup.Create();
    }

    #region Command Structure Tests

    [Fact]
    public void Create_ReturnsCommandWithCorrectName()
    {
        Assert.Equal("connections", _command.Name);
    }

    [Fact]
    public void Create_ReturnsCommandWithDescription()
    {
        Assert.Contains("connection", _command.Description, StringComparison.OrdinalIgnoreCase);
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
    public void Create_HasTwoSubcommands()
    {
        Assert.Equal(2, _command.Subcommands.Count);
    }

    #endregion

    #region List Subcommand Tests

    [Fact]
    public void ListSubcommand_HasConnectorOption()
    {
        var listCommand = _command.Subcommands.First(c => c.Name == "list");
        var option = listCommand.Options.FirstOrDefault(o => o.Name == "--connector");
        Assert.NotNull(option);
    }

    #endregion

    #region Get Subcommand Tests

    [Fact]
    public void GetSubcommand_HasIdArgument()
    {
        var getCommand = _command.Subcommands.First(c => c.Name == "get");
        Assert.Single(getCommand.Arguments);
        Assert.Equal("id", getCommand.Arguments[0].Name);
    }

    #endregion

    #region Shared Options Tests

    [Fact]
    public void ProfileOption_HasCorrectName()
    {
        Assert.Equal("--profile", ConnectionsCommandGroup.ProfileOption.Name);
    }

    [Fact]
    public void ProfileOption_HasShortAlias()
    {
        Assert.Contains("-p", ConnectionsCommandGroup.ProfileOption.Aliases);
    }

    [Fact]
    public void EnvironmentOption_HasCorrectName()
    {
        Assert.Equal("--environment", ConnectionsCommandGroup.EnvironmentOption.Name);
    }

    [Fact]
    public void EnvironmentOption_HasShortAlias()
    {
        Assert.Contains("-e", ConnectionsCommandGroup.EnvironmentOption.Aliases);
    }

    #endregion
}
