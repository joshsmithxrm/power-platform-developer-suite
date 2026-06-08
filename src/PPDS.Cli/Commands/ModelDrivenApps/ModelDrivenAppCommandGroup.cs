using System.CommandLine;

namespace PPDS.Cli.Commands.ModelDrivenApps;

/// <summary>
/// Command group for model-driven app navigation management.
/// </summary>
public static class ModelDrivenAppCommandGroup
{
    /// <summary>Shared --app option.</summary>
    public static readonly Option<string?> AppOption = new("--app")
    {
        Description = "[Required] App display name or unique name"
    };

    /// <summary>Shared --profile option.</summary>
    public static readonly Option<string?> ProfileOption = new("--profile", "-p")
    {
        Description = "Authentication profile name"
    };

    /// <summary>Shared --environment option.</summary>
    public static readonly Option<string?> EnvironmentOption = new("--environment", "-e")
    {
        Description = "Environment URL override"
    };

    /// <summary>Shared --solution option.</summary>
    public static readonly Option<string?> SolutionOption = new("--solution")
    {
        Description = "Add app (type 80) and sitemap (type 62) to this solution"
    };

    /// <summary>Shared --publish flag.</summary>
    public static readonly Option<bool> PublishOption = new("--publish")
    {
        Description = "Publish the app after modification"
    };

    /// <summary>Shared --confirm flag. Bypasses production write protection (issue #1195).</summary>
    public static readonly Option<bool> ConfirmOption = new("--confirm")
    {
        Description = "Bypass write protection on production-flagged environments"
    };

    /// <summary>
    /// Creates the model-driven-app command group.
    /// </summary>
    public static Command Create()
    {
        var command = new Command("model-driven-app", "Manage model-driven app navigation and components");

        command.Subcommands.Add(ListCommand.Create());
        command.Subcommands.Add(GetCommand.Create());
        command.Subcommands.Add(SitemapCommand.Create());
        command.Subcommands.Add(SetSitemapXmlCommand.Create());
        command.Subcommands.Add(AddTableCommand.Create());
        command.Subcommands.Add(RemoveTableCommand.Create());
        command.Subcommands.Add(SetFormsCommand.Create());
        command.Subcommands.Add(SetViewsCommand.Create());
        command.Subcommands.Add(SetChartsCommand.Create());
        command.Subcommands.Add(AddCopilotCommand.Create());
        command.Subcommands.Add(RemoveCopilotCommand.Create());
        command.Subcommands.Add(ListCopilotsCommand.Create());

        return command;
    }
}
