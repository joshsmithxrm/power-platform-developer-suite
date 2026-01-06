using System.CommandLine;
using PPDS.Cli.Commands.ConnectionReferences;
using Xunit;

namespace PPDS.Cli.Tests.Commands.ConnectionReferences;

public class ConnectionReferencesCommandGroupTests
{
    private readonly Command _command;

    public ConnectionReferencesCommandGroupTests()
    {
        _command = ConnectionReferencesCommandGroup.Create();
    }

    #region Command Structure Tests

    [Fact]
    public void Create_ReturnsCommandWithCorrectName()
    {
        Assert.Equal("connectionreferences", _command.Name);
    }

    [Fact]
    public void Create_ReturnsCommandWithDescription()
    {
        Assert.Contains("connection reference", _command.Description, StringComparison.OrdinalIgnoreCase);
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
    public void Create_HasFlowsSubcommand()
    {
        var subcommand = _command.Subcommands.FirstOrDefault(c => c.Name == "flows");
        Assert.NotNull(subcommand);
    }

    [Fact]
    public void Create_HasConnectionsSubcommand()
    {
        var subcommand = _command.Subcommands.FirstOrDefault(c => c.Name == "connections");
        Assert.NotNull(subcommand);
    }

    [Fact]
    public void Create_HasAnalyzeSubcommand()
    {
        var subcommand = _command.Subcommands.FirstOrDefault(c => c.Name == "analyze");
        Assert.NotNull(subcommand);
    }

    [Fact]
    public void Create_HasFiveSubcommands()
    {
        Assert.Equal(5, _command.Subcommands.Count);
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
    public void ListSubcommand_HasUnboundOption()
    {
        var listCommand = _command.Subcommands.First(c => c.Name == "list");
        var option = listCommand.Options.FirstOrDefault(o => o.Name == "--unbound");
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

    #region Flows Subcommand Tests

    [Fact]
    public void FlowsSubcommand_HasNameArgument()
    {
        var flowsCommand = _command.Subcommands.First(c => c.Name == "flows");
        Assert.Single(flowsCommand.Arguments);
        Assert.Equal("name", flowsCommand.Arguments[0].Name);
    }

    #endregion

    #region Connections Subcommand Tests

    [Fact]
    public void ConnectionsSubcommand_HasNameArgument()
    {
        var connectionsCommand = _command.Subcommands.First(c => c.Name == "connections");
        Assert.Single(connectionsCommand.Arguments);
        Assert.Equal("name", connectionsCommand.Arguments[0].Name);
    }

    #endregion

    #region Analyze Subcommand Tests

    [Fact]
    public void AnalyzeSubcommand_HasSolutionOption()
    {
        var analyzeCommand = _command.Subcommands.First(c => c.Name == "analyze");
        var option = analyzeCommand.Options.FirstOrDefault(o => o.Name == "--solution");
        Assert.NotNull(option);
    }

    #endregion

    #region Shared Options Tests

    [Fact]
    public void ProfileOption_HasCorrectName()
    {
        Assert.Equal("--profile", ConnectionReferencesCommandGroup.ProfileOption.Name);
    }

    [Fact]
    public void ProfileOption_HasShortAlias()
    {
        Assert.Contains("-p", ConnectionReferencesCommandGroup.ProfileOption.Aliases);
    }

    [Fact]
    public void EnvironmentOption_HasCorrectName()
    {
        Assert.Equal("--environment", ConnectionReferencesCommandGroup.EnvironmentOption.Name);
    }

    [Fact]
    public void EnvironmentOption_HasShortAlias()
    {
        Assert.Contains("-e", ConnectionReferencesCommandGroup.EnvironmentOption.Aliases);
    }

    #endregion
}
