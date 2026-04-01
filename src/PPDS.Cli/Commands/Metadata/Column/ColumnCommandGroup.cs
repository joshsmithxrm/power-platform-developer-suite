using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Dataverse.Metadata;
using PPDS.Dataverse.Metadata.Authoring;

namespace PPDS.Cli.Commands.Metadata.Column;

/// <summary>
/// Command group for Dataverse column (attribute) authoring: create, update, delete.
/// </summary>
public static class ColumnCommandGroup
{
    /// <summary>
    /// Creates the 'column' command group with subcommands.
    /// </summary>
    public static Command Create()
    {
        var command = new Command("column", "Create, update, or delete columns on Dataverse tables");

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

            var options = ParseOptionDefinitions(optionsRaw);

            return await ExecuteCreateAsync(
                solution, entity, name, displayName, type, description,
                requiredLevel, maxLength, minValue, maxValue, precision, format,
                dateTimeBehavior, optionSetName, options, defaultValue,
                trueLabel, falseLabel, maxSizeInKB, dryRun,
                profileVal, environmentVal, globalOptions, cancellationToken);
        });

        return command;
    }

    internal static OptionDefinition[]? ParseOptionDefinitions(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var result = new List<OptionDefinition>();

        foreach (var pair in raw.Split(','))
        {
            var trimmed = pair.Trim();
            var lastEquals = trimmed.LastIndexOf('=');
            if (lastEquals > 0 && int.TryParse(trimmed[(lastEquals + 1)..].Trim(), out var value))
            {
                result.Add(new OptionDefinition { Label = trimmed[..lastEquals].Trim(), Value = value });
            }
        }

        return result.Count > 0 ? result.ToArray() : null;
    }

    private static async Task<int> ExecuteCreateAsync(
        string solution,
        string entity,
        string name,
        string displayName,
        SchemaColumnType type,
        string? description,
        string? requiredLevel,
        int? maxLength,
        double? minValue,
        double? maxValue,
        int? precision,
        string? format,
        string? dateTimeBehavior,
        string? optionSetName,
        OptionDefinition[]? options,
        int? defaultValue,
        string? trueLabel,
        string? falseLabel,
        int? maxSizeInKB,
        bool dryRun,
        string? profile,
        string? environment,
        GlobalOptionValues globalOptions,
        CancellationToken cancellationToken)
    {
        var writer = ServiceFactory.CreateOutputWriter(globalOptions);

        try
        {
            await using var serviceProvider = await ProfileServiceFactory.CreateFromProfilesAsync(
                profile, environment,
                globalOptions.Verbose, globalOptions.Debug,
                ProfileServiceFactory.DefaultDeviceCodeCallback,
                cancellationToken);

            var authoringService = serviceProvider.GetRequiredService<IMetadataAuthoringService>();

            if (!globalOptions.IsJsonMode)
            {
                var connectionInfo = serviceProvider.GetRequiredService<ResolvedConnectionInfo>();
                ConsoleHeader.WriteConnectedAs(connectionInfo);
                Console.Error.WriteLine();
                Console.Error.WriteLine(dryRun ? "Validating column creation..." : $"Creating {type} column on '{entity}'...");
            }

            var request = new CreateColumnRequest
            {
                SolutionUniqueName = solution,
                EntityLogicalName = entity,
                SchemaName = name,
                DisplayName = displayName,
                ColumnType = type,
                Description = description ?? "",
                RequiredLevel = requiredLevel,
                MaxLength = maxLength,
                MinValue = minValue,
                MaxValue = maxValue,
                Precision = precision,
                Format = format,
                DateTimeBehavior = dateTimeBehavior,
                OptionSetName = optionSetName,
                Options = options,
                DefaultValue = defaultValue,
                TrueLabel = trueLabel,
                FalseLabel = falseLabel,
                MaxSizeInKB = maxSizeInKB,
                DryRun = dryRun
            };

            var result = await authoringService.CreateColumnAsync(request, ct: cancellationToken);

            if (globalOptions.IsJsonMode)
            {
                writer.WriteSuccess(result);
            }
            else
            {
                if (dryRun)
                {
                    Console.Error.WriteLine("[Dry-Run] Validation passed. No changes persisted.");
                }
                else
                {
                    Console.Error.WriteLine($"Column '{result.LogicalName}' created successfully on '{entity}'.");
                }

                foreach (var msg in result.ValidationMessages)
                {
                    Console.Error.WriteLine($"  [{msg.Rule}] {msg.Field}: {msg.Message}");
                }
            }

            return ExitCodes.Success;
        }
        catch (MetadataValidationException ex)
        {
            var error = StructuredError.Create(ex.ErrorCode, ex.Message);
            writer.WriteError(error);
            return ExitCodes.ValidationError;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: "creating column", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
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

            return await ExecuteUpdateAsync(
                solution, entity, column, displayName, description, requiredLevel, maxLength,
                dryRun, profileVal, environmentVal, globalOptions, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteUpdateAsync(
        string solution,
        string entity,
        string column,
        string? displayName,
        string? description,
        string? requiredLevel,
        int? maxLength,
        bool dryRun,
        string? profile,
        string? environment,
        GlobalOptionValues globalOptions,
        CancellationToken cancellationToken)
    {
        var writer = ServiceFactory.CreateOutputWriter(globalOptions);

        try
        {
            await using var serviceProvider = await ProfileServiceFactory.CreateFromProfilesAsync(
                profile, environment,
                globalOptions.Verbose, globalOptions.Debug,
                ProfileServiceFactory.DefaultDeviceCodeCallback,
                cancellationToken);

            var authoringService = serviceProvider.GetRequiredService<IMetadataAuthoringService>();

            if (!globalOptions.IsJsonMode)
            {
                var connectionInfo = serviceProvider.GetRequiredService<ResolvedConnectionInfo>();
                ConsoleHeader.WriteConnectedAs(connectionInfo);
                Console.Error.WriteLine();
                Console.Error.WriteLine(dryRun ? "Validating column update..." : $"Updating column '{column}' on '{entity}'...");
            }

            var request = new UpdateColumnRequest
            {
                SolutionUniqueName = solution,
                EntityLogicalName = entity,
                ColumnLogicalName = column,
                DisplayName = displayName,
                Description = description,
                RequiredLevel = requiredLevel,
                MaxLength = maxLength,
                DryRun = dryRun
            };

            await authoringService.UpdateColumnAsync(request, ct: cancellationToken);

            if (globalOptions.IsJsonMode)
            {
                writer.WriteSuccess(new { entity, column, updated = true, dryRun });
            }
            else
            {
                Console.Error.WriteLine(dryRun
                    ? "[Dry-Run] Validation passed. No changes persisted."
                    : $"Column '{column}' on '{entity}' updated successfully.");
            }

            return ExitCodes.Success;
        }
        catch (MetadataValidationException ex)
        {
            var error = StructuredError.Create(ex.ErrorCode, ex.Message);
            writer.WriteError(error);
            return ExitCodes.ValidationError;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: "updating column", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
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

            return await ExecuteDeleteAsync(
                solution, entity, column, force, dryRun,
                profileVal, environmentVal, globalOptions, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteDeleteAsync(
        string solution,
        string entity,
        string column,
        bool force,
        bool dryRun,
        string? profile,
        string? environment,
        GlobalOptionValues globalOptions,
        CancellationToken cancellationToken)
    {
        var writer = ServiceFactory.CreateOutputWriter(globalOptions);

        try
        {
            await using var serviceProvider = await ProfileServiceFactory.CreateFromProfilesAsync(
                profile, environment,
                globalOptions.Verbose, globalOptions.Debug,
                ProfileServiceFactory.DefaultDeviceCodeCallback,
                cancellationToken);

            var authoringService = serviceProvider.GetRequiredService<IMetadataAuthoringService>();

            if (!globalOptions.IsJsonMode)
            {
                var connectionInfo = serviceProvider.GetRequiredService<ResolvedConnectionInfo>();
                ConsoleHeader.WriteConnectedAs(connectionInfo);
                Console.Error.WriteLine();
            }

            // Dry-run first
            var dryRunRequest = new DeleteColumnRequest
            {
                SolutionUniqueName = solution,
                EntityLogicalName = entity,
                ColumnLogicalName = column,
                DryRun = true
            };

            await authoringService.DeleteColumnAsync(dryRunRequest, ct: cancellationToken);

            if (dryRun)
            {
                if (globalOptions.IsJsonMode)
                {
                    writer.WriteSuccess(new { entity, column, dryRun = true });
                }
                else
                {
                    Console.Error.WriteLine("[Dry-Run] Validation passed. No changes persisted.");
                }

                return ExitCodes.Success;
            }

            // Confirmation prompt
            if (!force)
            {
                if (!Console.IsInputRedirected)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.Error.WriteLine($"WARNING: This will permanently delete column '{column}' from '{entity}'.");
                    Console.Error.WriteLine("         Existing data in this column will be lost.");
                    Console.ResetColor();
                    Console.Error.WriteLine();

                    var expectedConfirmation = $"DELETE COLUMN {entity}.{column}";
                    Console.Error.Write($"Type '{expectedConfirmation}' to confirm, or Ctrl+C to cancel: ");
                    var confirmation = Console.ReadLine();

                    if (confirmation != expectedConfirmation)
                    {
                        Console.Error.WriteLine("Cancelled.");
                        return ExitCodes.Success;
                    }

                    Console.Error.WriteLine();
                }
                else
                {
                    writer.WriteError(StructuredError.Create(
                        "CONFIRMATION_REQUIRED",
                        "Use --force to skip confirmation in non-interactive mode"));
                    return ExitCodes.ConfirmationRequired;
                }
            }

            if (!globalOptions.IsJsonMode)
            {
                Console.Error.WriteLine($"Deleting column '{column}' from '{entity}'...");
            }

            var deleteRequest = new DeleteColumnRequest
            {
                SolutionUniqueName = solution,
                EntityLogicalName = entity,
                ColumnLogicalName = column,
                DryRun = false
            };

            await authoringService.DeleteColumnAsync(deleteRequest, ct: cancellationToken);

            if (globalOptions.IsJsonMode)
            {
                writer.WriteSuccess(new { entity, column, deleted = true });
            }
            else
            {
                Console.Error.WriteLine($"Column '{column}' deleted from '{entity}' successfully.");
            }

            return ExitCodes.Success;
        }
        catch (MetadataValidationException ex)
        {
            var error = StructuredError.Create(ex.ErrorCode, ex.Message);
            writer.WriteError(error);
            return ExitCodes.ValidationError;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: "deleting column", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }
}
