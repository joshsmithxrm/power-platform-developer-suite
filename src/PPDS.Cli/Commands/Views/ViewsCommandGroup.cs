using System.CommandLine;

namespace PPDS.Cli.Commands.Views;

/// <summary>
/// Views command group for managing Dataverse savedqueries views.
/// </summary>
public static class ViewsCommandGroup
{
    public static readonly Option<string?> ProfileOption = new("--profile", "-p")
    {
        Description = "Authentication profile name"
    };

    public static readonly Option<string?> EnvironmentOption = new("--environment", "-e")
    {
        Description = "Override the environment URL."
    };

    public static readonly Option<string> EntityOption = new("--entity")
    {
        Description = "[Required] Entity logical name (e.g., account)",
        Required = true
    };

    public static readonly Option<string?> SolutionOption = new("--solution")
    {
        Description = "Add the view to this solution after the operation"
    };

    public static readonly Option<bool> PublishOption = new("--publish")
    {
        Description = "Publish the entity after the operation"
    };

    public static Command Create()
    {
        var cmd = new Command("views",
            "Manage Dataverse savedqueries views: list, get, add-column, remove-column, update-column, reorder-columns, set-sort, clear-sort, set-filter, clear-filter, set-fetchxml");

        cmd.Subcommands.Add(ListCommand.Create());
        cmd.Subcommands.Add(GetCommand.Create());
        cmd.Subcommands.Add(AddColumnCommand.Create());
        cmd.Subcommands.Add(RemoveColumnCommand.Create());
        cmd.Subcommands.Add(UpdateColumnCommand.Create());
        cmd.Subcommands.Add(ReorderColumnsCommand.Create());
        cmd.Subcommands.Add(SetSortCommand.Create());
        cmd.Subcommands.Add(ClearSortCommand.Create());
        cmd.Subcommands.Add(SetFilterCommand.Create());
        cmd.Subcommands.Add(ClearFilterCommand.Create());
        cmd.Subcommands.Add(SetFetchXmlCommand.Create());

        return cmd;
    }
}
