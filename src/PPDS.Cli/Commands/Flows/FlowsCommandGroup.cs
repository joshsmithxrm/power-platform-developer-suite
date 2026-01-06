using System.CommandLine;

namespace PPDS.Cli.Commands.Flows;

/// <summary>
/// Command group for cloud flow operations.
/// </summary>
public static class FlowsCommandGroup
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
    /// Creates the flows command group.
    /// </summary>
    public static Command Create()
    {
        var command = new Command("flows", "Manage cloud flows (Power Automate)");

        command.Subcommands.Add(ListCommand.Create());
        command.Subcommands.Add(GetCommand.Create());
        command.Subcommands.Add(UrlCommand.Create());

        return command;
    }
}
