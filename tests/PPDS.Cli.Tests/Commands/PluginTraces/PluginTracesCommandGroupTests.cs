using System.CommandLine;
using PPDS.Cli.Commands.PluginTraces;
using Xunit;

namespace PPDS.Cli.Tests.Commands.PluginTraces;

public class PluginTracesCommandGroupTests
{
    private readonly Command _command;

    public PluginTracesCommandGroupTests()
    {
        _command = PluginTracesCommandGroup.Create();
    }

    #region Command Structure Tests

    [Fact]
    public void Create_ReturnsCommandWithCorrectName()
    {
        Assert.Equal("plugintraces", _command.Name);
    }

    [Fact]
    public void Create_ReturnsCommandWithDescription()
    {
        Assert.Contains("plugin trace", _command.Description, StringComparison.OrdinalIgnoreCase);
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
    public void Create_HasSettingsSubcommand()
    {
        var subcommand = _command.Subcommands.FirstOrDefault(c => c.Name == "settings");
        Assert.NotNull(subcommand);
    }

    [Fact]
    public void Create_HasRelatedSubcommand()
    {
        var subcommand = _command.Subcommands.FirstOrDefault(c => c.Name == "related");
        Assert.NotNull(subcommand);
    }

    [Fact]
    public void Create_HasTimelineSubcommand()
    {
        var subcommand = _command.Subcommands.FirstOrDefault(c => c.Name == "timeline");
        Assert.NotNull(subcommand);
    }

    [Fact]
    public void Create_HasDeleteSubcommand()
    {
        var subcommand = _command.Subcommands.FirstOrDefault(c => c.Name == "delete");
        Assert.NotNull(subcommand);
    }

    [Fact]
    public void Create_HasSixSubcommands()
    {
        Assert.Equal(6, _command.Subcommands.Count);
    }

    #endregion

    #region List Subcommand Tests

    [Fact]
    public void ListSubcommand_HasTypeOption()
    {
        var listCommand = _command.Subcommands.First(c => c.Name == "list");
        var option = listCommand.Options.FirstOrDefault(o => o.Name == "--type");
        Assert.NotNull(option);
    }

    [Fact]
    public void ListSubcommand_HasMessageOption()
    {
        var listCommand = _command.Subcommands.First(c => c.Name == "list");
        var option = listCommand.Options.FirstOrDefault(o => o.Name == "--message");
        Assert.NotNull(option);
    }

    [Fact]
    public void ListSubcommand_HasEntityOption()
    {
        var listCommand = _command.Subcommands.First(c => c.Name == "list");
        var option = listCommand.Options.FirstOrDefault(o => o.Name == "--entity");
        Assert.NotNull(option);
    }

    [Fact]
    public void ListSubcommand_HasModeOption()
    {
        var listCommand = _command.Subcommands.First(c => c.Name == "list");
        var option = listCommand.Options.FirstOrDefault(o => o.Name == "--mode");
        Assert.NotNull(option);
    }

    [Fact]
    public void ListSubcommand_HasErrorsOnlyOption()
    {
        var listCommand = _command.Subcommands.First(c => c.Name == "list");
        var option = listCommand.Options.FirstOrDefault(o => o.Name == "--errors-only");
        Assert.NotNull(option);
    }

    [Fact]
    public void ListSubcommand_HasSinceOption()
    {
        var listCommand = _command.Subcommands.First(c => c.Name == "list");
        var option = listCommand.Options.FirstOrDefault(o => o.Name == "--since");
        Assert.NotNull(option);
    }

    [Fact]
    public void ListSubcommand_HasUntilOption()
    {
        var listCommand = _command.Subcommands.First(c => c.Name == "list");
        var option = listCommand.Options.FirstOrDefault(o => o.Name == "--until");
        Assert.NotNull(option);
    }

    [Fact]
    public void ListSubcommand_HasFilterOption()
    {
        var listCommand = _command.Subcommands.First(c => c.Name == "list");
        var option = listCommand.Options.FirstOrDefault(o => o.Name == "--filter");
        Assert.NotNull(option);
    }

    [Fact]
    public void ListSubcommand_HasTopOption()
    {
        var listCommand = _command.Subcommands.First(c => c.Name == "list");
        var option = listCommand.Options.FirstOrDefault(o => o.Name == "--top");
        Assert.NotNull(option);
    }

    #endregion

    #region Get Subcommand Tests

    [Fact]
    public void GetSubcommand_HasTraceIdArgument()
    {
        var getCommand = _command.Subcommands.First(c => c.Name == "get");
        Assert.Single(getCommand.Arguments);
        Assert.Equal("trace-id", getCommand.Arguments[0].Name);
    }

    #endregion

    #region Delete Subcommand Tests

    [Fact]
    public void DeleteSubcommand_HasTraceIdArgument()
    {
        var deleteCommand = _command.Subcommands.First(c => c.Name == "delete");
        Assert.Single(deleteCommand.Arguments);
        Assert.Equal("trace-id", deleteCommand.Arguments[0].Name);
    }

    [Fact]
    public void DeleteSubcommand_HasIdsOption()
    {
        var deleteCommand = _command.Subcommands.First(c => c.Name == "delete");
        var option = deleteCommand.Options.FirstOrDefault(o => o.Name == "--ids");
        Assert.NotNull(option);
    }

    [Fact]
    public void DeleteSubcommand_HasOlderThanOption()
    {
        var deleteCommand = _command.Subcommands.First(c => c.Name == "delete");
        var option = deleteCommand.Options.FirstOrDefault(o => o.Name == "--older-than");
        Assert.NotNull(option);
    }

    [Fact]
    public void DeleteSubcommand_HasAllOption()
    {
        var deleteCommand = _command.Subcommands.First(c => c.Name == "delete");
        var option = deleteCommand.Options.FirstOrDefault(o => o.Name == "--all");
        Assert.NotNull(option);
    }

    [Fact]
    public void DeleteSubcommand_HasDryRunOption()
    {
        var deleteCommand = _command.Subcommands.First(c => c.Name == "delete");
        var option = deleteCommand.Options.FirstOrDefault(o => o.Name == "--dry-run");
        Assert.NotNull(option);
    }

    [Fact]
    public void DeleteSubcommand_HasForceOption()
    {
        var deleteCommand = _command.Subcommands.First(c => c.Name == "delete");
        var option = deleteCommand.Options.FirstOrDefault(o => o.Name == "--force");
        Assert.NotNull(option);
    }

    #endregion

    #region Related Subcommand Tests

    [Fact]
    public void RelatedSubcommand_HasTraceIdArgument()
    {
        var relatedCommand = _command.Subcommands.First(c => c.Name == "related");
        Assert.Single(relatedCommand.Arguments);
        Assert.Equal("trace-id", relatedCommand.Arguments[0].Name);
    }

    [Fact]
    public void RelatedSubcommand_HasCorrelationIdOption()
    {
        var relatedCommand = _command.Subcommands.First(c => c.Name == "related");
        var option = relatedCommand.Options.FirstOrDefault(o => o.Name == "--correlation-id");
        Assert.NotNull(option);
    }

    [Fact]
    public void RelatedSubcommand_HasTopOption()
    {
        var relatedCommand = _command.Subcommands.First(c => c.Name == "related");
        var option = relatedCommand.Options.FirstOrDefault(o => o.Name == "--top");
        Assert.NotNull(option);
    }

    #endregion

    #region Timeline Subcommand Tests

    [Fact]
    public void TimelineSubcommand_HasTraceIdArgument()
    {
        var timelineCommand = _command.Subcommands.First(c => c.Name == "timeline");
        Assert.Single(timelineCommand.Arguments);
        Assert.Equal("trace-id", timelineCommand.Arguments[0].Name);
    }

    [Fact]
    public void TimelineSubcommand_HasCorrelationIdOption()
    {
        var timelineCommand = _command.Subcommands.First(c => c.Name == "timeline");
        var option = timelineCommand.Options.FirstOrDefault(o => o.Name == "--correlation-id");
        Assert.NotNull(option);
    }

    #endregion

    #region Settings Subcommand Tests

    [Fact]
    public void SettingsSubcommand_HasGetSubcommand()
    {
        var settingsCommand = _command.Subcommands.First(c => c.Name == "settings");
        var subcommand = settingsCommand.Subcommands.FirstOrDefault(c => c.Name == "get");
        Assert.NotNull(subcommand);
    }

    [Fact]
    public void SettingsSubcommand_HasSetSubcommand()
    {
        var settingsCommand = _command.Subcommands.First(c => c.Name == "settings");
        var subcommand = settingsCommand.Subcommands.FirstOrDefault(c => c.Name == "set");
        Assert.NotNull(subcommand);
    }

    [Fact]
    public void SettingsSetSubcommand_HasValueArgument()
    {
        var settingsCommand = _command.Subcommands.First(c => c.Name == "settings");
        var setCommand = settingsCommand.Subcommands.First(c => c.Name == "set");
        Assert.Single(setCommand.Arguments);
        Assert.Equal("value", setCommand.Arguments[0].Name);
    }

    #endregion

    #region Shared Options Tests

    [Fact]
    public void ProfileOption_HasCorrectName()
    {
        Assert.Equal("--profile", PluginTracesCommandGroup.ProfileOption.Name);
    }

    [Fact]
    public void ProfileOption_HasShortAlias()
    {
        Assert.Contains("-p", PluginTracesCommandGroup.ProfileOption.Aliases);
    }

    [Fact]
    public void EnvironmentOption_HasCorrectName()
    {
        Assert.Equal("--environment", PluginTracesCommandGroup.EnvironmentOption.Name);
    }

    [Fact]
    public void EnvironmentOption_HasShortAlias()
    {
        Assert.Contains("-e", PluginTracesCommandGroup.EnvironmentOption.Aliases);
    }

    #endregion
}
