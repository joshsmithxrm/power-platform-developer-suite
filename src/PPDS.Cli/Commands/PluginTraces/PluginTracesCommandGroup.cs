using System.CommandLine;

namespace PPDS.Cli.Commands.PluginTraces;

/// <summary>
/// PluginTraces command group for querying and managing plugin trace logs.
/// </summary>
public static class PluginTracesCommandGroup
{
    /// <summary>
    /// Profile option for authentication.
    /// </summary>
    public static readonly Option<string?> ProfileOption = new("--profile", "-p")
    {
        Description = "Authentication profile name"
    };

    /// <summary>
    /// Environment option for target environment.
    /// </summary>
    public static readonly Option<string?> EnvironmentOption = new("--environment", "-e")
    {
        Description = "Override the environment URL. Takes precedence over profile's bound environment."
    };

    /// <summary>
    /// Creates the 'plugintraces' command group with all subcommands.
    /// </summary>
    public static Command Create()
    {
        var command = new Command("plugintraces", "Query and manage plugin trace logs: list, get, related, timeline, settings, delete");

        command.Subcommands.Add(ListCommand.Create());
        command.Subcommands.Add(GetCommand.Create());
        command.Subcommands.Add(SettingsCommand.Create());
        command.Subcommands.Add(RelatedCommand.Create());
        command.Subcommands.Add(TimelineCommand.Create());
        command.Subcommands.Add(DeleteCommand.Create());

        return command;
    }
}
