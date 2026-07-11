using System.CommandLine;
using System.CommandLine.Parsing;
using PPDS.Cli.Commands.ImportJobs;
using PPDS.Cli.Infrastructure;
using Xunit;

namespace PPDS.Cli.Tests.Commands.ImportJobs;

public class ImportJobsCommandGroupTests
{
    private readonly Command _command;

    public ImportJobsCommandGroupTests()
    {
        _command = ImportJobsCommandGroup.Create();
    }

    #region Command Structure Tests

    [Fact]
    public void Create_ReturnsCommandWithCorrectName()
    {
        Assert.Equal("import-jobs", _command.Name);
    }

    [Fact]
    public void Create_ReturnsCommandWithDescription()
    {
        Assert.Contains("import", _command.Description, StringComparison.OrdinalIgnoreCase);
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
    public void Create_HasDataSubcommand()
    {
        var subcommand = _command.Subcommands.FirstOrDefault(c => c.Name == "data");
        Assert.NotNull(subcommand);
    }

    [Fact]
    public void Create_HasWaitSubcommand()
    {
        var subcommand = _command.Subcommands.FirstOrDefault(c => c.Name == "wait");
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
        Assert.Equal("--profile", ImportJobsCommandGroup.ProfileOption.Name);
    }

    [Fact]
    public void ProfileOption_HasShortAlias()
    {
        Assert.Contains("-p", ImportJobsCommandGroup.ProfileOption.Aliases);
    }

    [Fact]
    public void EnvironmentOption_HasCorrectName()
    {
        Assert.Equal("--environment", ImportJobsCommandGroup.EnvironmentOption.Name);
    }

    [Fact]
    public void EnvironmentOption_HasShortAlias()
    {
        Assert.Contains("-e", ImportJobsCommandGroup.EnvironmentOption.Aliases);
    }

    #endregion

    #region Deprecated Alias Tests (#1246)

    [Fact]
    public void Create_OldNameIsRegisteredAsAlias()
    {
        Assert.Contains("importjobs", _command.Aliases);
    }

    [Fact]
    public void OldAlias_SubcommandInvocation_ResolvesToSameGroup()
    {
        var root = new RootCommand { _command };
        var parseResult = root.Parse(["importjobs", "list"]);

        Assert.Empty(parseResult.Errors);
        var groupResult = Assert.IsType<CommandResult>(parseResult.CommandResult.Parent);
        Assert.Same(_command, groupResult.Command);
    }

    [Fact]
    public void OldAlias_Invocation_EmitsDeprecationWarningOnStderr()
    {
        var root = new RootCommand { _command };
        var parseResult = root.Parse(["importjobs", "list"]);
        using var writer = new StringWriter();

        CommandAliasDeprecation.WarnIfDeprecatedAliasUsed(parseResult, writer);

        Assert.Equal("warning: 'importjobs' is deprecated; use 'import-jobs'" + Environment.NewLine, writer.ToString());
    }

    [Fact]
    public void NewName_Invocation_EmitsNoWarning()
    {
        var root = new RootCommand { _command };
        var parseResult = root.Parse(["import-jobs", "list"]);
        using var writer = new StringWriter();

        CommandAliasDeprecation.WarnIfDeprecatedAliasUsed(parseResult, writer);

        Assert.Equal(string.Empty, writer.ToString());
    }

    #endregion
}
