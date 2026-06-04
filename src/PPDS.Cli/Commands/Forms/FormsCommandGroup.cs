using System.CommandLine;

namespace PPDS.Cli.Commands.Forms;

/// <summary>
/// Command group for Dataverse systemform operations.
/// </summary>
public static class FormsCommandGroup
{
    /// <summary>Authentication profile option shared across all forms subcommands.</summary>
    public static readonly Option<string?> ProfileOption = new("--profile", "-p")
    {
        Description = "Authentication profile name"
    };

    /// <summary>Environment URL option shared across all forms subcommands.</summary>
    public static readonly Option<string?> EnvironmentOption = new("--environment", "-env")
    {
        Description = "Override the environment URL. Takes precedence over profile's bound environment."
    };

    /// <summary>
    /// Creates the 'forms' command group with all subcommands registered.
    /// </summary>
    public static Command Create()
    {
        var command = new Command("forms",
            "Inspect and modify Dataverse systemform records: list forms, view form structure, and manage tabs, sections, fields, and sub-grids");

        command.Subcommands.Add(ListCommand.Create());
        command.Subcommands.Add(GetCommand.Create());
        command.Subcommands.Add(SetXmlCommand.Create());
        command.Subcommands.Add(AddTabCommand.Create());
        command.Subcommands.Add(UpdateTabCommand.Create());
        command.Subcommands.Add(RemoveTabCommand.Create());
        command.Subcommands.Add(FindTabCommand.Create());
        command.Subcommands.Add(AddSectionCommand.Create());
        command.Subcommands.Add(UpdateSectionCommand.Create());
        command.Subcommands.Add(RemoveSectionCommand.Create());
        command.Subcommands.Add(FindSectionCommand.Create());
        command.Subcommands.Add(AddFieldCommand.Create());
        command.Subcommands.Add(RemoveFieldCommand.Create());
        command.Subcommands.Add(ReorderFieldsCommand.Create());
        command.Subcommands.Add(AddSubgridCommand.Create());
        command.Subcommands.Add(RemoveSubgridCommand.Create());

        return command;
    }
}
