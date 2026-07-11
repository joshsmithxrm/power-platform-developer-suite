using System.CommandLine;
using System.CommandLine.Parsing;
using PPDS.Cli.Commands.EnvironmentVariables;
using PPDS.Cli.Infrastructure;
using Xunit;

namespace PPDS.Cli.Tests.Commands.EnvironmentVariables;

public class EnvironmentVariablesCommandGroupTests
{
    private readonly Command _command;

    public EnvironmentVariablesCommandGroupTests()
    {
        _command = EnvironmentVariablesCommandGroup.Create();
    }

    #region Command Structure Tests

    [Fact]
    public void Create_ReturnsCommandWithCorrectName()
    {
        Assert.Equal("environment-variables", _command.Name);
    }

    [Fact]
    public void Create_ReturnsCommandWithDescription()
    {
        Assert.Contains("environment variable", _command.Description, StringComparison.OrdinalIgnoreCase);
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
    public void Create_HasSetSubcommand()
    {
        var subcommand = _command.Subcommands.FirstOrDefault(c => c.Name == "set");
        Assert.NotNull(subcommand);
    }

    [Fact]
    public void Create_HasExportSubcommand()
    {
        var subcommand = _command.Subcommands.FirstOrDefault(c => c.Name == "export");
        Assert.NotNull(subcommand);
    }

    [Fact]
    public void Create_HasUrlSubcommand()
    {
        var subcommand = _command.Subcommands.FirstOrDefault(c => c.Name == "url");
        Assert.NotNull(subcommand);
    }

    [Fact]
    public void Create_HasFiveSubcommands()
    {
        Assert.Equal(5, _command.Subcommands.Count);
    }

    #endregion

    #region Shared Options Tests

    [Fact]
    public void ProfileOption_HasCorrectName()
    {
        Assert.Equal("--profile", EnvironmentVariablesCommandGroup.ProfileOption.Name);
    }

    [Fact]
    public void ProfileOption_HasShortAlias()
    {
        Assert.Contains("-p", EnvironmentVariablesCommandGroup.ProfileOption.Aliases);
    }

    [Fact]
    public void EnvironmentOption_HasCorrectName()
    {
        Assert.Equal("--environment", EnvironmentVariablesCommandGroup.EnvironmentOption.Name);
    }

    [Fact]
    public void EnvironmentOption_HasShortAlias()
    {
        Assert.Contains("-e", EnvironmentVariablesCommandGroup.EnvironmentOption.Aliases);
    }

    #endregion

    #region Deprecated Alias Tests (#1246)

    [Fact]
    public void Create_OldNameIsRegisteredAsAlias()
    {
        Assert.Contains("environmentvariables", _command.Aliases);
    }

    [Fact]
    public void OldAlias_SubcommandInvocation_ResolvesToSameGroup()
    {
        var root = new RootCommand { _command };
        var parseResult = root.Parse(["environmentvariables", "list"]);

        Assert.Empty(parseResult.Errors);
        var groupResult = Assert.IsType<CommandResult>(parseResult.CommandResult.Parent);
        Assert.Same(_command, groupResult.Command);
    }

    [Fact]
    public void OldAlias_Invocation_EmitsDeprecationWarningOnStderr()
    {
        var root = new RootCommand { _command };
        var parseResult = root.Parse(["environmentvariables", "list"]);
        var writer = new StringWriter();

        CommandAliasDeprecation.WarnIfDeprecatedAliasUsed(parseResult, writer);

        Assert.Equal("warning: 'environmentvariables' is deprecated; use 'environment-variables'" + Environment.NewLine, writer.ToString());
    }

    [Fact]
    public void NewName_Invocation_EmitsNoWarning()
    {
        var root = new RootCommand { _command };
        var parseResult = root.Parse(["environment-variables", "list"]);
        var writer = new StringWriter();

        CommandAliasDeprecation.WarnIfDeprecatedAliasUsed(parseResult, writer);

        Assert.Equal(string.Empty, writer.ToString());
    }

    #endregion
}
