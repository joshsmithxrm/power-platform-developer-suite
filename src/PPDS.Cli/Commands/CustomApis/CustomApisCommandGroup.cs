using System.CommandLine;

namespace PPDS.Cli.Commands.CustomApis;

/// <summary>
/// Custom APIs command group for managing Dataverse Custom APIs and their parameters.
/// </summary>
public static class CustomApisCommandGroup
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
    /// Creates the 'custom-apis' command group with all subcommands.
    /// </summary>
    public static Command Create()
    {
        var command = new Command("custom-apis", "Custom API management: list, get, register, update, unregister, add-parameter, update-parameter, remove-parameter");

        command.Subcommands.Add(ListCommand.Create());
        command.Subcommands.Add(GetCommand.Create());
        command.Subcommands.Add(RegisterCommand.Create());
        command.Subcommands.Add(UpdateCommand.Create());
        command.Subcommands.Add(UnregisterCommand.Create());
        command.Subcommands.Add(AddParameterCommand.Create());
        command.Subcommands.Add(UpdateParameterCommand.Create());
        command.Subcommands.Add(RemoveParameterCommand.Create());
        command.Subcommands.Add(SetPluginCommand.Create());

        return command;
    }
}
