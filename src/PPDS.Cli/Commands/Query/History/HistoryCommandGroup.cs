using System.CommandLine;

namespace PPDS.Cli.Commands.Query.History;

/// <summary>
/// History command group for managing SQL query history.
/// </summary>
public static class HistoryCommandGroup
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
    public static readonly Option<string?> EnvironmentOption = new("--environment", "-env")
    {
        Description = "Override the environment URL. Takes precedence over profile's bound environment."
    };

    /// <summary>
    /// Creates the 'history' command group with all subcommands.
    /// </summary>
    public static Command Create()
    {
        var command = new Command("history", "Manage SQL query history: list, get, execute, delete, clear");

        command.Subcommands.Add(ListCommand.Create());
        command.Subcommands.Add(GetCommand.Create());
        command.Subcommands.Add(ExecuteCommand.Create());
        command.Subcommands.Add(DeleteCommand.Create());
        command.Subcommands.Add(ClearCommand.Create());

        return command;
    }
}
