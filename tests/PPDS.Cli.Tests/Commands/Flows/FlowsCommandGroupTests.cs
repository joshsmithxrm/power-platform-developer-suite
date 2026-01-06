using System.CommandLine;
using PPDS.Cli.Commands.Flows;
using Xunit;

namespace PPDS.Cli.Tests.Commands.Flows;

public class FlowsCommandGroupTests
{
    private readonly Command _command;

    public FlowsCommandGroupTests()
    {
        _command = FlowsCommandGroup.Create();
    }

    #region Command Structure Tests

    [Fact]
    public void Create_ReturnsCommandWithCorrectName()
    {
        Assert.Equal("flows", _command.Name);
    }

    [Fact]
    public void Create_ReturnsCommandWithDescription()
    {
        Assert.Contains("flow", _command.Description, StringComparison.OrdinalIgnoreCase);
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
    public void Create_HasUrlSubcommand()
    {
        var subcommand = _command.Subcommands.FirstOrDefault(c => c.Name == "url");
        Assert.NotNull(subcommand);
    }

    [Fact]
    public void Create_HasThreeSubcommands()
    {
        Assert.Equal(3, _command.Subcommands.Count);
    }

    #endregion

    #region List Subcommand Tests

    [Fact]
    public void ListSubcommand_HasSolutionOption()
    {
        var listCommand = _command.Subcommands.First(c => c.Name == "list");
        var option = listCommand.Options.FirstOrDefault(o => o.Name == "--solution");
        Assert.NotNull(option);
    }

    [Fact]
    public void ListSubcommand_HasStateOption()
    {
        var listCommand = _command.Subcommands.First(c => c.Name == "list");
        var option = listCommand.Options.FirstOrDefault(o => o.Name == "--state");
        Assert.NotNull(option);
    }

    #endregion

    #region Get Subcommand Tests

    [Fact]
    public void GetSubcommand_HasNameArgument()
    {
        var getCommand = _command.Subcommands.First(c => c.Name == "get");
        Assert.Single(getCommand.Arguments);
        Assert.Equal("name", getCommand.Arguments[0].Name);
    }

    #endregion

    #region Url Subcommand Tests

    [Fact]
    public void UrlSubcommand_HasNameArgument()
    {
        var urlCommand = _command.Subcommands.First(c => c.Name == "url");
        Assert.Single(urlCommand.Arguments);
        Assert.Equal("name", urlCommand.Arguments[0].Name);
    }

    #endregion

    #region Shared Options Tests

    [Fact]
    public void ProfileOption_HasCorrectName()
    {
        Assert.Equal("--profile", FlowsCommandGroup.ProfileOption.Name);
    }

    [Fact]
    public void ProfileOption_HasShortAlias()
    {
        Assert.Contains("-p", FlowsCommandGroup.ProfileOption.Aliases);
    }

    [Fact]
    public void EnvironmentOption_HasCorrectName()
    {
        Assert.Equal("--environment", FlowsCommandGroup.EnvironmentOption.Name);
    }

    [Fact]
    public void EnvironmentOption_HasShortAlias()
    {
        Assert.Contains("-e", FlowsCommandGroup.EnvironmentOption.Aliases);
    }

    #endregion
}
