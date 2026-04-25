using System.CommandLine;

namespace PPDS.Cli.Commands.WebResources;

/// <summary>
/// Web resources command group for managing Dataverse web resources.
/// </summary>
public static class WebResourcesCommandGroup
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
    /// Creates the 'webresources' command group with all subcommands.
    /// </summary>
    public static Command Create()
    {
        var command = new Command("webresources", "Manage Dataverse web resources: list, get, url, publish, pull, push");

        command.Subcommands.Add(ListCommand.Create());
        command.Subcommands.Add(GetCommand.Create());
        command.Subcommands.Add(UrlCommand.Create());
        command.Subcommands.Add(PublishAliasCommand.Create());
        command.Subcommands.Add(PullCommand.Create());
        command.Subcommands.Add(PushCommand.Create());

        return command;
    }
}
