using System.CommandLine;

namespace PPDS.Cli.Commands.Plugins;

/// <summary>
/// Plugin command group for managing plugin registrations.
/// </summary>
public static class PluginsCommandGroup
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
    /// Solution option for specifying which solution to add components to.
    /// </summary>
    public static readonly Option<string?> SolutionOption = new("--solution", "-s")
    {
        Description = "Solution unique name. Overrides value in configuration file."
    };

    /// <summary>
    /// Creates the 'plugins' command group with all subcommands.
    /// </summary>
    public static Command Create()
    {
        var command = new Command("plugins", "Plugin registration management: extract, deploy, diff, list, clean");

        command.Subcommands.Add(ExtractCommand.Create());
        command.Subcommands.Add(DeployCommand.Create());
        command.Subcommands.Add(DiffCommand.Create());
        command.Subcommands.Add(ListCommand.Create());
        command.Subcommands.Add(CleanCommand.Create());

        return command;
    }
}
