using System.CommandLine;
using PPDS.Cli.Infrastructure;

namespace PPDS.Cli.Commands.Metadata.Choice;

/// <summary>
/// Deprecation shim for the 'choice' command — delegates to 'optionset' canonical commands.
/// </summary>
public static class ChoiceCommandGroup
{
    /// <summary>
    /// Creates the 'choice' command group with subcommands (deprecated — use 'optionset').
    /// </summary>
    public static Command Create()
    {
        var command = new Command("choice", "Manage global choices/option sets (deprecated — use 'optionset')");

        command.Subcommands.Add(CreateCreateCommand());
        command.Subcommands.Add(CreateUpdateCommand());
        command.Subcommands.Add(CreateDeleteCommand());
        command.Subcommands.Add(CreateAddOptionCommand());
        command.Subcommands.Add(CreateUpdateOptionCommand());
        command.Subcommands.Add(CreateRemoveOptionCommand());
        command.Subcommands.Add(CreateReorderCommand());

        return command;
    }

    internal static Command CreateCreateCommand()
    {
        var solutionOption = new Option<string>("--solution", "-s")
        {
            Description = "Solution unique name to add the choice to",
            Required = true
        };

        var nameOption = new Option<string>("--name")
        {
            Description = "Schema name for the global choice",
            Required = true
        };

        var displayNameOption = new Option<string>("--display-name")
        {
            Description = "Display name of the global choice",
            Required = true
        };

        var optionsOption = new Option<string>("--options")
        {
            Description = "Option definitions: \"Label1=1,Label2=2\"",
            Required = true
        };

        var descriptionOption = new Option<string?>("--description")
        {
            Description = "Description of the global choice"
        };

        var dryRunOption = new Option<bool>("--dry-run")
        {
            Description = "Validate only, do not persist changes",
            DefaultValueFactory = _ => false
        };

        var command = new Command("create", "Create a new global choice (option set)")
        {
            solutionOption,
            nameOption,
            displayNameOption,
            optionsOption,
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
            var optionsRaw = parseResult.GetValue(optionsOption)!;
            var description = parseResult.GetValue(descriptionOption);
            var dryRun = parseResult.GetValue(dryRunOption);
            var profile = parseResult.GetValue(MetadataCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(MetadataCommandGroup.EnvironmentOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            DeprecationWarning.Write("ppds metadata choice create", "ppds metadata optionset create");
            return await Metadata.OptionSetCommand.ExecuteCreateAsync(
                solution, name, displayName, optionsRaw, description, dryRun,
                profile, environment, globalOptions, cancellationToken);
        });

        return command;
    }

    internal static Command CreateUpdateCommand()
    {
        var solutionOption = new Option<string>("--solution", "-s")
        {
            Description = "Solution unique name containing the global choice",
            Required = true
        };

        var nameOption = new Option<string>("--name")
        {
            Description = "Name of the global choice to update",
            Required = true
        };

        var displayNameOption = new Option<string?>("--display-name")
        {
            Description = "Updated display name"
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

        var command = new Command("update", "Update an existing global choice (option set)")
        {
            solutionOption,
            nameOption,
            displayNameOption,
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
            var displayName = parseResult.GetValue(displayNameOption);
            var description = parseResult.GetValue(descriptionOption);
            var dryRun = parseResult.GetValue(dryRunOption);
            var profile = parseResult.GetValue(MetadataCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(MetadataCommandGroup.EnvironmentOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            DeprecationWarning.Write("ppds metadata choice update", "ppds metadata optionset update");
            return await Metadata.OptionSetCommand.ExecuteUpdateAsync(
                solution, name, displayName, description, dryRun,
                profile, environment, globalOptions, cancellationToken);
        });

        return command;
    }

    internal static Command CreateDeleteCommand()
    {
        var solutionOption = new Option<string>("--solution", "-s")
        {
            Description = "Solution unique name containing the global choice",
            Required = true
        };

        var nameOption = new Option<string>("--name")
        {
            Description = "Name of the global choice to delete",
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

        var command = new Command("delete", "Delete a global choice (option set)")
        {
            solutionOption,
            nameOption,
            forceOption,
            dryRunOption,
            MetadataCommandGroup.ProfileOption,
            MetadataCommandGroup.EnvironmentOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var solution = parseResult.GetValue(solutionOption)!;
            var name = parseResult.GetValue(nameOption)!;
            var force = parseResult.GetValue(forceOption);
            var dryRun = parseResult.GetValue(dryRunOption);
            var profile = parseResult.GetValue(MetadataCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(MetadataCommandGroup.EnvironmentOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            DeprecationWarning.Write("ppds metadata choice delete", "ppds metadata optionset delete");
            return await Metadata.OptionSetCommand.ExecuteDeleteAsync(
                solution, name, force, dryRun,
                profile, environment, globalOptions, cancellationToken);
        });

        return command;
    }

    internal static Command CreateAddOptionCommand()
    {
        var solutionOption = new Option<string>("--solution", "-s")
        {
            Description = "Solution unique name containing the option set",
            Required = true
        };

        var nameOption = new Option<string>("--name")
        {
            Description = "Name of the option set",
            Required = true
        };

        var labelOption = new Option<string>("--label")
        {
            Description = "Label for the new option",
            Required = true
        };

        var valueOption = new Option<int?>("--value")
        {
            Description = "Numeric value for the option (auto-assigned if not specified)"
        };

        var colorOption = new Option<string?>("--color")
        {
            Description = "Color for the option (hex string, e.g., #FF0000)"
        };

        var command = new Command("add-option", "Add a new option value to an existing option set")
        {
            solutionOption,
            nameOption,
            labelOption,
            valueOption,
            colorOption,
            MetadataCommandGroup.ProfileOption,
            MetadataCommandGroup.EnvironmentOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var solution = parseResult.GetValue(solutionOption)!;
            var name = parseResult.GetValue(nameOption)!;
            var label = parseResult.GetValue(labelOption)!;
            var value = parseResult.GetValue(valueOption);
            var color = parseResult.GetValue(colorOption);
            var profile = parseResult.GetValue(MetadataCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(MetadataCommandGroup.EnvironmentOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            DeprecationWarning.Write("ppds metadata choice add-option", "ppds metadata optionset add-option");
            return await Metadata.OptionSetCommand.ExecuteAddOptionAsync(
                solution, name, label, value, color,
                profile, environment, globalOptions, cancellationToken);
        });

        return command;
    }

    internal static Command CreateUpdateOptionCommand()
    {
        var solutionOption = new Option<string>("--solution", "-s")
        {
            Description = "Solution unique name containing the option set",
            Required = true
        };

        var nameOption = new Option<string>("--name")
        {
            Description = "Name of the option set",
            Required = true
        };

        var valueOption = new Option<int>("--value")
        {
            Description = "Numeric value of the option to update",
            Required = true
        };

        var labelOption = new Option<string>("--label")
        {
            Description = "New label for the option",
            Required = true
        };

        var command = new Command("update-option", "Update an existing option value in an option set")
        {
            solutionOption,
            nameOption,
            valueOption,
            labelOption,
            MetadataCommandGroup.ProfileOption,
            MetadataCommandGroup.EnvironmentOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var solution = parseResult.GetValue(solutionOption)!;
            var name = parseResult.GetValue(nameOption)!;
            var value = parseResult.GetValue(valueOption);
            var label = parseResult.GetValue(labelOption)!;
            var profile = parseResult.GetValue(MetadataCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(MetadataCommandGroup.EnvironmentOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            DeprecationWarning.Write("ppds metadata choice update-option", "ppds metadata optionset update-option");
            return await Metadata.OptionSetCommand.ExecuteUpdateOptionAsync(
                solution, name, value, label,
                profile, environment, globalOptions, cancellationToken);
        });

        return command;
    }

    internal static Command CreateRemoveOptionCommand()
    {
        var solutionOption = new Option<string>("--solution", "-s")
        {
            Description = "Solution unique name containing the option set",
            Required = true
        };

        var nameOption = new Option<string>("--name")
        {
            Description = "Name of the option set",
            Required = true
        };

        var valueOption = new Option<int>("--value")
        {
            Description = "Numeric value of the option to remove",
            Required = true
        };

        var forceOption = new Option<bool>("--force")
        {
            Description = "Skip confirmation prompt",
            DefaultValueFactory = _ => false
        };

        var command = new Command("remove-option", "Remove an option value from an option set")
        {
            solutionOption,
            nameOption,
            valueOption,
            forceOption,
            MetadataCommandGroup.ProfileOption,
            MetadataCommandGroup.EnvironmentOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var solution = parseResult.GetValue(solutionOption)!;
            var name = parseResult.GetValue(nameOption)!;
            var value = parseResult.GetValue(valueOption);
            var force = parseResult.GetValue(forceOption);
            var profile = parseResult.GetValue(MetadataCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(MetadataCommandGroup.EnvironmentOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            DeprecationWarning.Write("ppds metadata choice remove-option", "ppds metadata optionset remove-option");
            return await Metadata.OptionSetCommand.ExecuteRemoveOptionAsync(
                solution, name, value, force,
                profile, environment, globalOptions, cancellationToken);
        });

        return command;
    }

    internal static Command CreateReorderCommand()
    {
        var solutionOption = new Option<string>("--solution", "-s")
        {
            Description = "Solution unique name containing the option set",
            Required = true
        };

        var nameOption = new Option<string>("--name")
        {
            Description = "Name of the option set",
            Required = true
        };

        var orderOption = new Option<string>("--order")
        {
            Description = "Comma-separated list of option values in the desired order: \"1,3,2,4\"",
            Required = true
        };

        var command = new Command("reorder", "Reorder option values in an option set")
        {
            solutionOption,
            nameOption,
            orderOption,
            MetadataCommandGroup.ProfileOption,
            MetadataCommandGroup.EnvironmentOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var solution = parseResult.GetValue(solutionOption)!;
            var name = parseResult.GetValue(nameOption)!;
            var orderRaw = parseResult.GetValue(orderOption)!;
            var profile = parseResult.GetValue(MetadataCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(MetadataCommandGroup.EnvironmentOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            DeprecationWarning.Write("ppds metadata choice reorder", "ppds metadata optionset reorder");
            return await Metadata.OptionSetCommand.ExecuteReorderAsync(
                solution, name, orderRaw,
                profile, environment, globalOptions, cancellationToken);
        });

        return command;
    }
}
