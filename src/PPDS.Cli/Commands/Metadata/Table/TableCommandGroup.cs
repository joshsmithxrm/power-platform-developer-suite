using System.CommandLine;
using PPDS.Cli.Infrastructure;
using PPDS.Dataverse.Metadata.Authoring;

namespace PPDS.Cli.Commands.Metadata.Table;

/// <summary>
/// Deprecation shim for the 'table' command — delegates to 'entity' canonical commands.
/// </summary>
public static class TableCommandGroup
{
    /// <summary>
    /// Creates the 'table' command group with subcommands (deprecated — use 'entity').
    /// </summary>
    public static Command Create()
    {
        var command = new Command("table", "Create, update, or delete Dataverse tables (deprecated — use 'entity')");

        command.Subcommands.Add(CreateCreateCommand());
        command.Subcommands.Add(CreateUpdateCommand());
        command.Subcommands.Add(CreateDeleteCommand());

        return command;
    }

    internal static Command CreateCreateCommand()
    {
        var solutionOption = new Option<string>("--solution", "-s")
        {
            Description = "Solution unique name to add the table to",
            Required = true
        };

        var nameOption = new Option<string>("--name")
        {
            Description = "Schema name for the new table (e.g., new_MyTable)",
            Required = true
        };

        var displayNameOption = new Option<string>("--display-name")
        {
            Description = "Display name of the table",
            Required = true
        };

        var pluralNameOption = new Option<string>("--plural-name")
        {
            Description = "Plural display name of the table",
            Required = true
        };

        var ownershipOption = new Option<string>("--ownership")
        {
            Description = "Ownership type: UserOwned or OrganizationOwned",
            Required = true
        };
        ownershipOption.AcceptOnlyFromAmong("UserOwned", "OrganizationOwned");

        var descriptionOption = new Option<string?>("--description")
        {
            Description = "Description of the table"
        };

        var dryRunOption = new Option<bool>("--dry-run")
        {
            Description = "Validate only, do not persist changes",
            DefaultValueFactory = _ => false
        };

        var command = new Command("create", "Create a new Dataverse table (entity)")
        {
            solutionOption,
            nameOption,
            displayNameOption,
            pluralNameOption,
            ownershipOption,
            descriptionOption,
            dryRunOption,
            MetadataCommandGroup.ProfileOption,
            MetadataCommandGroup.EnvironmentOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var solution = parseResult.GetValue(solutionOption)!;
            var name = parseResult.GetValue(nameOption)!;
            var displayName = parseResult.GetValue(displayNameOption)!;
            var pluralName = parseResult.GetValue(pluralNameOption)!;
            var ownership = parseResult.GetValue(ownershipOption)!;
            var description = parseResult.GetValue(descriptionOption);
            var dryRun = parseResult.GetValue(dryRunOption);
            var profile = parseResult.GetValue(MetadataCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(MetadataCommandGroup.EnvironmentOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            DeprecationWarning.Write("ppds metadata table create", "ppds metadata entity create");
            return await Metadata.EntityCommand.ExecuteCreateAsync(
                solution, name, displayName, pluralName, ownership, description, dryRun,
                profile, environment, globalOptions, cancellationToken);
        });

        return command;
    }

    internal static Command CreateUpdateCommand()
    {
        var solutionOption = new Option<string>("--solution", "-s")
        {
            Description = "Solution unique name containing the table",
            Required = true
        };

        var entityOption = new Option<string>("--entity", "-e")
        {
            Description = "Logical name of the entity to update",
            Required = true
        };

        var displayNameOption = new Option<string?>("--display-name")
        {
            Description = "Updated display name"
        };

        var pluralNameOption = new Option<string?>("--plural-name")
        {
            Description = "Updated plural display name"
        };

        var descriptionOption = new Option<string?>("--description")
        {
            Description = "Updated description"
        };

        var dryRunOption = new Option<bool>("--dry-run")
        {
            Description = "Validate only, do not persist changes",
            DefaultValueFactory = _ => false
        };

        var command = new Command("update", "Update an existing Dataverse table (entity)")
        {
            solutionOption,
            entityOption,
            displayNameOption,
            pluralNameOption,
            descriptionOption,
            dryRunOption,
            MetadataCommandGroup.ProfileOption,
            MetadataCommandGroup.EnvironmentOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var solution = parseResult.GetValue(solutionOption)!;
            var entity = parseResult.GetValue(entityOption)!;
            var displayName = parseResult.GetValue(displayNameOption);
            var pluralName = parseResult.GetValue(pluralNameOption);
            var description = parseResult.GetValue(descriptionOption);
            var dryRun = parseResult.GetValue(dryRunOption);
            var profile = parseResult.GetValue(MetadataCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(MetadataCommandGroup.EnvironmentOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            DeprecationWarning.Write("ppds metadata table update", "ppds metadata entity update");
            return await Metadata.EntityCommand.ExecuteUpdateAsync(
                solution, entity, displayName, pluralName, description, dryRun,
                profile, environment, globalOptions, cancellationToken);
        });

        return command;
    }

    internal static Command CreateDeleteCommand()
    {
        var solutionOption = new Option<string>("--solution", "-s")
        {
            Description = "Solution unique name containing the table",
            Required = true
        };

        var entityOption = new Option<string>("--entity", "-e")
        {
            Description = "Logical name of the entity to delete",
            Required = true
        };

        var forceOption = new Option<bool>("--force")
        {
            Description = "Skip confirmation prompt",
            DefaultValueFactory = _ => false
        };

        var dryRunOption = new Option<bool>("--dry-run")
        {
            Description = "Show dependencies without deleting",
            DefaultValueFactory = _ => false
        };

        var command = new Command("delete", "Delete a Dataverse table (entity)")
        {
            solutionOption,
            entityOption,
            forceOption,
            dryRunOption,
            MetadataCommandGroup.ProfileOption,
            MetadataCommandGroup.EnvironmentOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var solution = parseResult.GetValue(solutionOption)!;
            var entity = parseResult.GetValue(entityOption)!;
            var force = parseResult.GetValue(forceOption);
            var dryRun = parseResult.GetValue(dryRunOption);
            var profile = parseResult.GetValue(MetadataCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(MetadataCommandGroup.EnvironmentOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            DeprecationWarning.Write("ppds metadata table delete", "ppds metadata entity delete");
            return await Metadata.EntityCommand.ExecuteDeleteAsync(
                solution, entity, force, dryRun,
                profile, environment, globalOptions, cancellationToken);
        });

        return command;
    }
}
