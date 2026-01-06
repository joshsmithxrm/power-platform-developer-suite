using System.CommandLine;

namespace PPDS.Cli.Commands.Connections;

/// <summary>
/// Command group for Power Platform connection operations.
/// </summary>
/// <remarks>
/// Connections are managed through the Power Apps Admin API, not Dataverse.
/// This command group queries the Power Apps API to list and get connection details.
/// </remarks>
public static class ConnectionsCommandGroup
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

    /// <summary>
    /// Creates the connections command group.
    /// </summary>
    public static Command Create()
    {
        var command = new Command("connections", "Manage Power Platform connections (Power Apps Admin API)");

        command.Subcommands.Add(ListCommand.Create());
        command.Subcommands.Add(GetCommand.Create());

        return command;
    }
}
