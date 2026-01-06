using System.CommandLine;
using PPDS.Cli.Commands.DeploymentSettings;
using Xunit;

namespace PPDS.Cli.Tests.Commands.DeploymentSettings;

public class DeploymentSettingsCommandGroupTests
{
    private readonly Command _command;

    public DeploymentSettingsCommandGroupTests()
    {
        _command = DeploymentSettingsCommandGroup.Create();
    }

    #region Command Structure Tests

    [Fact]
    public void Create_ReturnsCommandWithCorrectName()
    {
        Assert.Equal("deployment-settings", _command.Name);
    }

    [Fact]
    public void Create_ReturnsCommandWithDescription()
    {
        Assert.Contains("deployment settings", _command.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Create_HasGenerateSubcommand()
    {
        var subcommand = _command.Subcommands.FirstOrDefault(c => c.Name == "generate");
        Assert.NotNull(subcommand);
    }

    [Fact]
    public void Create_HasSyncSubcommand()
    {
        var subcommand = _command.Subcommands.FirstOrDefault(c => c.Name == "sync");
        Assert.NotNull(subcommand);
    }

    [Fact]
    public void Create_HasValidateSubcommand()
    {
        var subcommand = _command.Subcommands.FirstOrDefault(c => c.Name == "validate");
        Assert.NotNull(subcommand);
    }

    [Fact]
    public void Create_HasThreeSubcommands()
    {
        Assert.Equal(3, _command.Subcommands.Count);
    }

    #endregion

    #region Generate Subcommand Tests

    [Fact]
    public void GenerateSubcommand_HasSolutionOption()
    {
        var generateCommand = _command.Subcommands.First(c => c.Name == "generate");
        var option = generateCommand.Options.FirstOrDefault(o => o.Name == "--solution");
        Assert.NotNull(option);
    }

    [Fact]
    public void GenerateSubcommand_HasOutputOption()
    {
        var generateCommand = _command.Subcommands.First(c => c.Name == "generate");
        var option = generateCommand.Options.FirstOrDefault(o => o.Name == "--output");
        Assert.NotNull(option);
    }

    #endregion

    #region Sync Subcommand Tests

    [Fact]
    public void SyncSubcommand_HasSolutionOption()
    {
        var syncCommand = _command.Subcommands.First(c => c.Name == "sync");
        var option = syncCommand.Options.FirstOrDefault(o => o.Name == "--solution");
        Assert.NotNull(option);
    }

    [Fact]
    public void SyncSubcommand_HasFileOption()
    {
        var syncCommand = _command.Subcommands.First(c => c.Name == "sync");
        var option = syncCommand.Options.FirstOrDefault(o => o.Name == "--file");
        Assert.NotNull(option);
    }

    #endregion

    #region Validate Subcommand Tests

    [Fact]
    public void ValidateSubcommand_HasSolutionOption()
    {
        var validateCommand = _command.Subcommands.First(c => c.Name == "validate");
        var option = validateCommand.Options.FirstOrDefault(o => o.Name == "--solution");
        Assert.NotNull(option);
    }

    [Fact]
    public void ValidateSubcommand_HasFileOption()
    {
        var validateCommand = _command.Subcommands.First(c => c.Name == "validate");
        var option = validateCommand.Options.FirstOrDefault(o => o.Name == "--file");
        Assert.NotNull(option);
    }

    #endregion

    #region Shared Options Tests

    [Fact]
    public void ProfileOption_HasCorrectName()
    {
        Assert.Equal("--profile", DeploymentSettingsCommandGroup.ProfileOption.Name);
    }

    [Fact]
    public void ProfileOption_HasShortAlias()
    {
        Assert.Contains("-p", DeploymentSettingsCommandGroup.ProfileOption.Aliases);
    }

    [Fact]
    public void EnvironmentOption_HasCorrectName()
    {
        Assert.Equal("--environment", DeploymentSettingsCommandGroup.EnvironmentOption.Name);
    }

    [Fact]
    public void EnvironmentOption_HasShortAlias()
    {
        Assert.Contains("-e", DeploymentSettingsCommandGroup.EnvironmentOption.Aliases);
    }

    #endregion
}
