using System.CommandLine;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Commands.Metadata.Attribute;
using PPDS.Dataverse.Metadata;
using PPDS.Dataverse.Metadata.Authoring;

namespace PPDS.Cli.Commands.Metadata.Column;

/// <summary>
/// Deprecation shim for the 'column' command — delegates to 'attribute' canonical commands.
/// </summary>
public static class ColumnCommandGroup
{
    /// <summary>
    /// Creates the 'column' command group with subcommands (deprecated — use 'attribute').
    /// </summary>
    public static Command Create()
    {
        var command = new Command("column", "Create, update, or delete columns (deprecated — use 'attribute')");

        command.Subcommands.Add(CreateCreateCommand());
        command.Subcommands.Add(CreateUpdateCommand());
        command.Subcommands.Add(CreateDeleteCommand());

        return command;
    }

    internal static Command CreateCreateCommand()
    {
        var solutionOption = new Option<string>("--solution", "-s")
        {
            Description = "Solution unique name containing the table",
            Required = true
        };

        var entityOption = new Option<string>("--entity", "-e")
        {
            Description = "Logical name of the entity to add the column to",
            Required = true
        };

        var nameOption = new Option<string>("--name")
        {
            Description = "Schema name for the new column (e.g., new_MyColumn)",
            Required = true
        };

        var displayNameOption = new Option<string>("--display-name")
        {
            Description = "Display name of the column",
            Required = true
        };

        var typeOption = new Option<SchemaColumnType>("--type")
        {
            Description = "Column type: String, Memo, Integer, BigInt, Decimal, Double, Money, Boolean, DateTime, Choice, Choices, Image, File, Lookup",
            Required = true
        };

        var descriptionOption = new Option<string?>("--description")
        {
            Description = "Description of the column"
        };

        var requiredLevelOption = new Option<string?>("--required-level")
        {
            Description = "Requirement level: None, Recommended, or Required"
        };

        var maxLengthOption = new Option<int?>("--max-length")
        {
            Description = "Maximum length for String/Memo columns"
        };

        var minValueOption = new Option<double?>("--min-value")
        {
            Description = "Minimum value for numeric columns"
        };

        var maxValueOption = new Option<double?>("--max-value")
        {
            Description = "Maximum value for numeric columns"
        };

        var precisionOption = new Option<int?>("--precision")
        {
            Description = "Precision for Decimal, Double, and Money columns"
        };

        var formatOption = new Option<string?>("--format")
        {
            Description = "Format for String, Integer, or DateTime columns"
        };

        var dateTimeBehaviorOption = new Option<string?>("--date-time-behavior")
        {
            Description = "DateTime behavior: UserLocal, DateOnly, or TimeZoneIndependent"
        };

        var optionSetNameOption = new Option<string?>("--option-set-name")
        {
            Description = "Name of an existing global option set for Choice/Choices columns"
        };

        var optionsOption = new Option<string?>("--options")
        {
            Description = "Option definitions for Choice/Choices columns: \"Label1=1,Label2=2\""
        };

        var defaultValueOption = new Option<int?>("--default-value")
        {
            Description = "Default value for Choice or Boolean columns"
        };

        var trueLabelOption = new Option<string?>("--true-label")
        {
            Description = "Label for the true value of a Boolean column"
        };

        var falseLabelOption = new Option<string?>("--false-label")
        {
            Description = "Label for the false value of a Boolean column"
        };

        var maxSizeInKBOption = new Option<int?>("--max-size-kb")
        {
            Description = "Maximum file size in KB for Image/File columns"
        };

        var dryRunOption = new Option<bool>("--dry-run")
        {
            Description = "Validate only, do not persist changes",
            DefaultValueFactory = _ => false
        };

        var command = new Command("create", "Create a new column (attribute) on a Dataverse table")
        {
            solutionOption,
            entityOption,
            nameOption,
            displayNameOption,
            typeOption,
            descriptionOption,
            requiredLevelOption,
            maxLengthOption,
            minValueOption,
            maxValueOption,
            precisionOption,
            formatOption,
            dateTimeBehaviorOption,
            optionSetNameOption,
            optionsOption,
            defaultValueOption,
            trueLabelOption,
            falseLabelOption,
            maxSizeInKBOption,
            dryRunOption,
            MetadataCommandGroup.ProfileOption,
            MetadataCommandGroup.EnvironmentOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var solution = parseResult.GetValue(solutionOption)!;
            var entity = parseResult.GetValue(entityOption)!;
            var name = parseResult.GetValue(nameOption)!;
            var displayName = parseResult.GetValue(displayNameOption)!;
            var type = parseResult.GetValue(typeOption);
            var description = parseResult.GetValue(descriptionOption);
            var requiredLevel = parseResult.GetValue(requiredLevelOption);
            var maxLength = parseResult.GetValue(maxLengthOption);
            var minValue = parseResult.GetValue(minValueOption);
            var maxValue = parseResult.GetValue(maxValueOption);
            var precision = parseResult.GetValue(precisionOption);
            var format = parseResult.GetValue(formatOption);
            var dateTimeBehavior = parseResult.GetValue(dateTimeBehaviorOption);
            var optionSetName = parseResult.GetValue(optionSetNameOption);
            var optionsRaw = parseResult.GetValue(optionsOption);
            var defaultValue = parseResult.GetValue(defaultValueOption);
            var trueLabel = parseResult.GetValue(trueLabelOption);
            var falseLabel = parseResult.GetValue(falseLabelOption);
            var maxSizeInKB = parseResult.GetValue(maxSizeInKBOption);
            var dryRun = parseResult.GetValue(dryRunOption);
            var profileVal = parseResult.GetValue(MetadataCommandGroup.ProfileOption);
            var environmentVal = parseResult.GetValue(MetadataCommandGroup.EnvironmentOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            var options = AttributeCommandGroup.ParseOptionDefinitions(optionsRaw);

            DeprecationWarning.Write("ppds metadata column create", "ppds metadata attribute create");
            return await AttributeCommandGroup.ExecuteCreateAsync(
                solution, entity, name, displayName, type, description,
                requiredLevel, maxLength, minValue, maxValue, precision, format,
                dateTimeBehavior, optionSetName, options, defaultValue,
                trueLabel, falseLabel, maxSizeInKB, dryRun, publish: false,
                profileVal, environmentVal, globalOptions, cancellationToken);
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
            Description = "Logical name of the entity containing the column",
            Required = true
        };

        var columnOption = new Option<string>("--column", "-c")
        {
            Description = "Logical name of the column to update",
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

        var requiredLevelOption = new Option<string?>("--required-level")
        {
            Description = "Updated requirement level: None, Recommended, or Required"
        };

        var maxLengthOption = new Option<int?>("--max-length")
        {
            Description = "Updated maximum length"
        };

        var dryRunOption = new Option<bool>("--dry-run")
        {
            Description = "Validate only, do not persist changes",
            DefaultValueFactory = _ => false
        };

        var command = new Command("update", "Update an existing column (attribute) on a Dataverse table")
        {
            solutionOption,
            entityOption,
            columnOption,
            displayNameOption,
            descriptionOption,
            requiredLevelOption,
            maxLengthOption,
            dryRunOption,
            MetadataCommandGroup.ProfileOption,
            MetadataCommandGroup.EnvironmentOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var solution = parseResult.GetValue(solutionOption)!;
            var entity = parseResult.GetValue(entityOption)!;
            var column = parseResult.GetValue(columnOption)!;
            var displayName = parseResult.GetValue(displayNameOption);
            var description = parseResult.GetValue(descriptionOption);
            var requiredLevel = parseResult.GetValue(requiredLevelOption);
            var maxLength = parseResult.GetValue(maxLengthOption);
            var dryRun = parseResult.GetValue(dryRunOption);
            var profileVal = parseResult.GetValue(MetadataCommandGroup.ProfileOption);
            var environmentVal = parseResult.GetValue(MetadataCommandGroup.EnvironmentOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            DeprecationWarning.Write("ppds metadata column update", "ppds metadata attribute update");
            return await AttributeCommandGroup.ExecuteUpdateAsync(
                solution, entity, column, displayName, description, requiredLevel, maxLength,
                dryRun, profileVal, environmentVal, globalOptions, cancellationToken);
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
            Description = "Logical name of the entity containing the column",
            Required = true
        };

        var columnOption = new Option<string>("--column", "-c")
        {
            Description = "Logical name of the column to delete",
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

        var command = new Command("delete", "Delete a column (attribute) from a Dataverse table")
        {
            solutionOption,
            entityOption,
            columnOption,
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
            var column = parseResult.GetValue(columnOption)!;
            var force = parseResult.GetValue(forceOption);
            var dryRun = parseResult.GetValue(dryRunOption);
            var profileVal = parseResult.GetValue(MetadataCommandGroup.ProfileOption);
            var environmentVal = parseResult.GetValue(MetadataCommandGroup.EnvironmentOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            DeprecationWarning.Write("ppds metadata column delete", "ppds metadata attribute delete");
            return await AttributeCommandGroup.ExecuteDeleteAsync(
                solution, entity, column, force, dryRun,
                profileVal, environmentVal, globalOptions, cancellationToken);
        });

        return command;
    }
}
