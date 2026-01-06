using System.CommandLine;

namespace PPDS.Cli.Commands.DeploymentSettings;

/// <summary>
/// Command group for deployment settings file operations.
/// </summary>
public static class DeploymentSettingsCommandGroup
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

    /// <summary>Shared solution option.</summary>
    public static readonly Option<string> SolutionOption = new("--solution", "-s")
    {
        Description = "Solution unique name",
        Required = true
    };

    /// <summary>
    /// Creates the deployment-settings command group.
    /// </summary>
    public static Command Create()
    {
        var command = new Command("deployment-settings", "Generate, sync, and validate deployment settings files");

        command.Subcommands.Add(GenerateCommand.Create());
        command.Subcommands.Add(SyncCommand.Create());
        command.Subcommands.Add(ValidateCommand.Create());

        return command;
    }
}
