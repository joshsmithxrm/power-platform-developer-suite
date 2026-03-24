using System.CommandLine;

namespace PPDS.Cli.Commands.ServiceEndpoints;

/// <summary>
/// Service endpoints command group for managing Dataverse service endpoints and webhooks.
/// </summary>
public static class ServiceEndpointsCommandGroup
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
    /// Creates the 'service-endpoints' command group with all subcommands.
    /// </summary>
    public static Command Create()
    {
        var command = new Command("service-endpoints", "Service endpoint management: list, get, register, update, unregister");

        command.Subcommands.Add(ListCommand.Create());
        command.Subcommands.Add(GetCommand.Create());
        command.Subcommands.Add(RegisterCommand.Create());
        command.Subcommands.Add(UpdateCommand.Create());
        command.Subcommands.Add(UnregisterCommand.Create());

        return command;
    }
}
