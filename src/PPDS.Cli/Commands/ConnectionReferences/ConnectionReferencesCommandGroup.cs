using System.CommandLine;

namespace PPDS.Cli.Commands.ConnectionReferences;

/// <summary>
/// Command group for connection reference operations.
/// </summary>
public static class ConnectionReferencesCommandGroup
{
    /// <summary>Shared profile option.</summary>
    public static readonly Option<string?> ProfileOption = new("--profile", "-p")
    {
        Description = "Authentication profile name"
    };

    /// <summary>Shared environment option.</summary>
    public static readonly Option<string?> EnvironmentOption = new("--environment", "-e")
    {
        Description = "Environment URL override"
    };

    /// <summary>Shared solution filter option.</summary>
    public static readonly Option<string?> SolutionOption = new("--solution", "-s")
    {
        Description = "Filter by solution unique name"
    };

    /// <summary>
    /// Creates the connectionreferences command group.
    /// </summary>
    public static Command Create()
    {
        var command = new Command("connectionreferences", "Manage connection references");

        command.Subcommands.Add(ListCommand.Create());
        command.Subcommands.Add(GetCommand.Create());
        command.Subcommands.Add(FlowsCommand.Create());
        command.Subcommands.Add(ConnectionsCommand.Create());
        command.Subcommands.Add(AnalyzeCommand.Create());

        return command;
    }
}
