using System.CommandLine;

namespace PPDS.Cli.Commands.DataSources;

/// <summary>
/// Data sources command group for managing Dataverse virtual entity data sources.
/// </summary>
public static class DataSourcesCommandGroup
{
    /// <summary>
    /// Profile option for specifying which authentication profile to use.
    /// </summary>
    public static readonly Option<string?> ProfileOption = new("--profile", "-p")
    {
        Description = "Authentication profile name"
    };

    /// <summary>
    /// Environment option for overriding the profile's bound environment.
    /// </summary>
    public static readonly Option<string?> EnvironmentOption = new("--environment", "-env")
    {
        Description = "Override the environment URL. Takes precedence over profile's bound environment."
    };

    /// <summary>
    /// Creates the 'data-sources' command group with all subcommands.
    /// </summary>
    public static Command Create()
    {
        var command = new Command("data-sources", "Data source management: list, get, register, update, unregister");

        command.Subcommands.Add(ListCommand.Create());
        command.Subcommands.Add(GetCommand.Create());
        command.Subcommands.Add(RegisterCommand.Create());
        command.Subcommands.Add(UpdateCommand.Create());
        command.Subcommands.Add(UnregisterCommand.Create());

        return command;
    }
}
