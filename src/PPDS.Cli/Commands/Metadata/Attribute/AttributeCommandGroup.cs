using System.CommandLine;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Dataverse.Metadata;
using PPDS.Dataverse.Metadata.Authoring;

using PPDS.Cli.Services.Metadata.Authoring;
namespace PPDS.Cli.Commands.Metadata.Attribute;

/// <summary>
/// Canonical command group for Dataverse attribute (column) authoring: create, update, delete.
/// </summary>
public static class AttributeCommandGroup
{
    /// <summary>
    /// Creates the 'attribute' command group with subcommands.
    /// </summary>
    public static Command Create()
    {
        var command = new Command("attribute", "Create, update, or delete columns (attributes) on Dataverse tables");

        command.Subcommands.Add(CreateCreateCommand());
        command.Subcommands.Add(CreateUpdateCommand());
        command.Subcommands.Add(CreateDeleteCommand());
        command.Subcommands.Add(CreateAddOptionCommand());
        command.Subcommands.Add(CreateUpdateOptionCommand());
        command.Subcommands.Add(CreateRemoveOptionCommand());

        return command;
    }

    public static Command CreateCreateCommand()
    {
        var solutionOption = new Option<string>("--solution", "-s")
        {
            Description = "Solution unique name containing the table",
            Required = true
        };

        var entityOption = new Option<string>("--entity", "-e")
        {
            Description = "Logical name of the entity to add the attribute to",
            Required = true
        };

        var nameOption = new Option<string>("--name")
        {
            Description = "Schema name for the new attribute (e.g., new_MyColumn)",
            Required = true
        };

        var displayNameOption = new Option<string>("--display-name")
        {
            Description = "Display name of the attribute",
            Required = true
        };

        var typeOption = new Option<SchemaColumnType>("--type")
        {
            Description = "Column type: String, Memo, Integer, BigInt, Decimal, Double, Money, Boolean, DateTime, Choice, Choices, Image, File, Lookup",
            Required = true
        };

        var descriptionOption = new Option<string?>("--description")
        {
            Description = "Description of the attribute"
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
            Description = "[Deprecated alias for --choice] Name of an existing global option set",
            Hidden = true
        };

        var choiceOption = new Option<string?>("--choice")
        {
            Description = "Attach the Choice/Choices column to an existing GLOBAL option set by name (mutually exclusive with --option/--options/--options-file)"
        };

        var optionsOption = new Option<string?>("--options")
        {
            Description = "[Legacy] Local Choice options as CSV: \"Label1=1,Label2=2\""
        };

        var optionOption = new Option<string[]?>("--option")
        {
            Description = "Local Choice option \"Label[:Value][:#Color]\" — repeatable; value derived from --solution when omitted"
        };

        var optionsFileOption = new Option<string?>("--options-file")
        {
            Description = "Path to a JSON file of local Choice options: [{\"label\":\"..\",\"value\":1,\"color\":\"#RRGGBB\"}]"
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

        var publishOption = new Option<bool>("--publish")
        {
            Description = "Publish the entity after the attribute is created",
            DefaultValueFactory = _ => false
        };

        var command = new Command("create", "Create a new attribute on a Dataverse table (for Choice/Choices columns use --option to define a local option set, or --choice to attach a global one)")
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
            choiceOption,
            optionsOption,
            optionOption,
            optionsFileOption,
            defaultValueOption,
            trueLabelOption,
            falseLabelOption,
            maxSizeInKBOption,
            dryRunOption,
            publishOption,
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
            var choice = parseResult.GetValue(choiceOption);
            var optionsRaw = parseResult.GetValue(optionsOption);
            var optionArgs = parseResult.GetValue(optionOption);
            var optionsFile = parseResult.GetValue(optionsFileOption);
            var defaultValue = parseResult.GetValue(defaultValueOption);
            var trueLabel = parseResult.GetValue(trueLabelOption);
            var falseLabel = parseResult.GetValue(falseLabelOption);
            var maxSizeInKB = parseResult.GetValue(maxSizeInKBOption);
            var dryRun = parseResult.GetValue(dryRunOption);
            var publish = parseResult.GetValue(publishOption);
            var profileVal = parseResult.GetValue(MetadataCommandGroup.ProfileOption);
            var environmentVal = parseResult.GetValue(MetadataCommandGroup.EnvironmentOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            var writer = ServiceFactory.CreateOutputWriter(globalOptions);

            // --choice (or legacy --option-set-name) attaches a GLOBAL option set; it is mutually
            // exclusive with the local-option inputs (--option/--options/--options-file). (#1161, AC-53)
            var resolvedChoice = !string.IsNullOrWhiteSpace(choice) ? choice : optionSetName;
            var hasGlobal = !string.IsNullOrWhiteSpace(resolvedChoice);
            var hasLocal = (optionArgs is { Length: > 0 })
                           || !string.IsNullOrWhiteSpace(optionsFile)
                           || !string.IsNullOrWhiteSpace(optionsRaw);

            if (hasGlobal && hasLocal)
            {
                writer.WriteError(StructuredError.Create(
                    "INVALID_CONSTRAINT",
                    "--choice attaches an existing global option set and cannot be combined with --option/--options/--options-file (which define a local option set)."));
                return ExitCodes.ValidationError;
            }

            OptionDefinition[]? options;
            try
            {
                options = ParseOptionSpecs(optionArgs, optionsFile, optionsRaw);
            }
            catch (Exception ex)
            {
                writer.WriteError(StructuredError.Create("INVALID_CONSTRAINT", ex.Message));
                return ExitCodes.ValidationError;
            }

            return await ExecuteCreateAsync(
                solution, entity, name, displayName, type, description,
                requiredLevel, maxLength, minValue, maxValue, precision, format,
                dateTimeBehavior, resolvedChoice, options, defaultValue,
                trueLabel, falseLabel, maxSizeInKB, dryRun, publish,
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

    /// <summary>
    /// Parses local Choice option definitions from repeatable --option specs ("Label[:Value][:#Color]"),
    /// a JSON --options-file, or the legacy --options CSV. Returns null when none are supplied.
    /// Throws <see cref="FormatException"/> / <see cref="FileNotFoundException"/> on malformed input.
    /// </summary>
    internal static OptionDefinition[]? ParseOptionSpecs(string[]? optionArgs, string? optionsFile, string? legacyCsv)
    {
        if (optionArgs is { Length: > 0 })
        {
            var list = optionArgs
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(ParseOptionSpec)
                .ToList();
            return list.Count > 0 ? list.ToArray() : null;
        }

        if (!string.IsNullOrWhiteSpace(optionsFile))
            return ParseOptionsFile(optionsFile);

        return ParseOptionDefinitions(legacyCsv);
    }

    /// <summary>Parses a single "Label[:Value][:#Color]" option spec.</summary>
    internal static OptionDefinition ParseOptionSpec(string spec)
    {
        var parts = spec.Split(':');
        var label = parts[0].Trim();
        if (label.Length == 0)
            throw new FormatException($"Invalid --option '{spec}': a label is required.");

        var def = new OptionDefinition { Label = label };
        for (var i = 1; i < parts.Length; i++)
        {
            var token = parts[i].Trim();
            if (token.Length == 0)
                continue;
            if (token.StartsWith('#'))
                def.Color = token;
            else if (int.TryParse(token, out var v))
                def.Value = v;
            else
                throw new FormatException($"Invalid --option '{spec}': '{token}' is neither an integer value nor a #color.");
        }
        return def;
    }

    private static OptionDefinition[]? ParseOptionsFile(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"--options-file not found: {path}");

        using var stream = File.OpenRead(path);
        var entries = JsonSerializer.Deserialize<List<OptionFileEntry>>(
            stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        if (entries == null || entries.Count == 0)
            return null;

        return entries.Select(e => new OptionDefinition
        {
            Label = e.Label ?? "",
            Value = e.Value ?? 0,
            Color = e.Color
        }).ToArray();
    }

    private sealed class OptionFileEntry
    {
        public string? Label { get; set; }
        public int? Value { get; set; }
        public string? Color { get; set; }
    }

    internal static async Task<int> ExecuteCreateAsync(
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
        bool publish,
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
                Console.Error.WriteLine(dryRun ? "Validating attribute creation..." : $"Creating {type} attribute on '{entity}'...");
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
                DryRun = dryRun,
                Publish = publish
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
                    Console.Error.WriteLine($"Attribute '{result.LogicalName}' created successfully on '{entity}'.");
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
            var error = ExceptionMapper.Map(ex, context: "creating attribute", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    public static Command CreateUpdateCommand()
    {
        var solutionOption = new Option<string>("--solution", "-s")
        {
            Description = "Solution unique name containing the table",
            Required = true
        };

        var entityOption = new Option<string>("--entity", "-e")
        {
            Description = "Logical name of the entity containing the attribute",
            Required = true
        };

        var columnOption = new Option<string>("--column", "-c")
        {
            Description = "Logical name of the attribute to update",
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

        var command = new Command("update", "Update an existing attribute on a Dataverse table")
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

    internal static async Task<int> ExecuteUpdateAsync(
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
                Console.Error.WriteLine(dryRun ? "Validating attribute update..." : $"Updating attribute '{column}' on '{entity}'...");
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

            var publishHint = $"ppds metadata publish {entity}";

            if (globalOptions.IsJsonMode)
            {
                writer.WriteSuccess(new
                {
                    entity,
                    column,
                    updated = true,
                    dryRun,
                    requiresPublish = !dryRun,
                    publishHint = dryRun ? null : publishHint
                });
            }
            else if (dryRun)
            {
                Console.Error.WriteLine("[Dry-Run] Validation passed. No changes persisted.");
            }
            else
            {
                Console.Error.WriteLine($"Attribute '{column}' on '{entity}' updated. Run '{publishHint}' to publish changes.");
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
            var error = ExceptionMapper.Map(ex, context: "updating attribute", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    public static Command CreateDeleteCommand()
    {
        var solutionOption = new Option<string>("--solution", "-s")
        {
            Description = "Solution unique name containing the table",
            Required = true
        };

        var entityOption = new Option<string>("--entity", "-e")
        {
            Description = "Logical name of the entity containing the attribute",
            Required = true
        };

        var columnOption = new Option<string>("--column", "-c")
        {
            Description = "Logical name of the attribute to delete",
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

        var command = new Command("delete", "Delete an attribute from a Dataverse table")
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

    internal static async Task<int> ExecuteDeleteAsync(
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
                    Console.Error.WriteLine($"WARNING: This will permanently delete attribute '{column}' from '{entity}'.");
                    Console.Error.WriteLine("         Existing data in this attribute will be lost.");
                    Console.ResetColor();
                    Console.Error.WriteLine();

                    var expectedConfirmation = $"DELETE ATTRIBUTE {entity}.{column}";
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
                Console.Error.WriteLine($"Deleting attribute '{column}' from '{entity}'...");
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
                Console.Error.WriteLine($"Attribute '{column}' deleted from '{entity}' successfully.");
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
            var error = ExceptionMapper.Map(ex, context: "deleting attribute", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    // -------------------------------------------------------------------------
    // Local (column-scoped) option management (#1161)
    // -------------------------------------------------------------------------

    private static (string label, int? value, string? color) ParseOptionSpecNullable(string spec)
    {
        var parts = spec.Split(':');
        var label = parts[0].Trim();
        int? value = null;
        string? color = null;
        for (var i = 1; i < parts.Length; i++)
        {
            var token = parts[i].Trim();
            if (token.Length == 0) continue;
            if (token.StartsWith('#')) color = token;
            else if (int.TryParse(token, out var v)) value = v;
            else throw new FormatException($"Invalid --option '{spec}': '{token}' is neither an integer value nor a #color.");
        }
        return (label, value, color);
    }

    internal static Command CreateAddOptionCommand()
    {
        var solutionOption = new Option<string?>("--solution", "-s") { Description = "Solution unique name (required for value derivation when --option has no explicit value)" };
        var entityOption = new Option<string>("--entity", "-e") { Description = "Logical name of the entity", Required = true };
        var columnOption = new Option<string>("--column", "-c") { Description = "Logical name of the Choice/Choices column", Required = true };
        var optionOption = new Option<string>("--option") { Description = "Option to add: \"Label[:Value][:#Color]\"", Required = true };
        var publishOption = new Option<bool>("--publish") { Description = "Publish the entity after the change", DefaultValueFactory = _ => false };
        var dryRunOption = new Option<bool>("--dry-run") { Description = "Validate only, do not persist changes", DefaultValueFactory = _ => false };

        var command = new Command("add-option", "Add an option to a column's local option set")
        {
            solutionOption, entityOption, columnOption, optionOption, publishOption, dryRunOption,
            MetadataCommandGroup.ProfileOption, MetadataCommandGroup.EnvironmentOption
        };
        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var solution = parseResult.GetValue(solutionOption);
            var entity = parseResult.GetValue(entityOption)!;
            var column = parseResult.GetValue(columnOption)!;
            var spec = parseResult.GetValue(optionOption)!;
            var publish = parseResult.GetValue(publishOption);
            var dryRun = parseResult.GetValue(dryRunOption);
            var profile = parseResult.GetValue(MetadataCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(MetadataCommandGroup.EnvironmentOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);
            var writer = ServiceFactory.CreateOutputWriter(globalOptions);

            string label; int? value; string? color;
            try { (label, value, color) = ParseOptionSpecNullable(spec); }
            catch (Exception ex) { writer.WriteError(StructuredError.Create("INVALID_CONSTRAINT", ex.Message)); return ExitCodes.ValidationError; }

            if (string.IsNullOrWhiteSpace(label))
            {
                writer.WriteError(StructuredError.Create("INVALID_CONSTRAINT", "--option requires a non-empty label."));
                return ExitCodes.ValidationError;
            }
            if (!value.HasValue && string.IsNullOrWhiteSpace(solution))
            {
                writer.WriteError(StructuredError.Create("MISSING_REQUIRED_FIELD", "Provide an explicit value (Label:Value) or --solution to derive the option value."));
                return ExitCodes.ValidationError;
            }

            try
            {
                await using var serviceProvider = await ProfileServiceFactory.CreateFromProfilesAsync(
                    profile, environment, globalOptions.Verbose, globalOptions.Debug,
                    ProfileServiceFactory.DefaultDeviceCodeCallback, cancellationToken);
                var authoringService = serviceProvider.GetRequiredService<IMetadataAuthoringService>();

                if (!globalOptions.IsJsonMode)
                {
                    ConsoleHeader.WriteConnectedAs(serviceProvider.GetRequiredService<ResolvedConnectionInfo>());
                    Console.Error.WriteLine();
                    Console.Error.WriteLine($"Adding option '{label}' to '{entity}.{column}'...");
                }

                var assigned = await authoringService.AddColumnOptionAsync(new AddColumnOptionRequest
                {
                    EntityLogicalName = entity,
                    ColumnLogicalName = column,
                    Label = label,
                    Value = value,
                    Color = color,
                    SolutionUniqueName = solution,
                    Publish = publish,
                    DryRun = dryRun
                }, cancellationToken);

                if (globalOptions.IsJsonMode)
                    writer.WriteSuccess(new { entity, column, label, value = assigned, dryRun });
                else if (dryRun)
                    Console.Error.WriteLine("[Dry-Run] Validation passed. No changes persisted.");
                else
                    Console.Error.WriteLine($"Option '{label}' added to '{entity}.{column}' with value {assigned}.");

                return ExitCodes.Success;
            }
            catch (MetadataValidationException ex)
            {
                writer.WriteError(StructuredError.Create(ex.ErrorCode, ex.Message));
                return ExitCodes.ValidationError;
            }
            catch (Exception ex)
            {
                writer.WriteError(ExceptionMapper.Map(ex, context: "adding option", debug: globalOptions.Debug));
                return ExceptionMapper.ToExitCode(ex);
            }
        });

        return command;
    }

    internal static Command CreateUpdateOptionCommand()
    {
        var solutionOption = new Option<string?>("--solution", "-s") { Description = "Solution unique name" };
        var entityOption = new Option<string>("--entity", "-e") { Description = "Logical name of the entity", Required = true };
        var columnOption = new Option<string>("--column", "-c") { Description = "Logical name of the Choice/Choices column", Required = true };
        var valueOption = new Option<int?>("--value") { Description = "Target option by value (mutually exclusive with --label)" };
        var labelOption = new Option<string?>("--label") { Description = "Target option by current label (mutually exclusive with --value)" };
        var newLabelOption = new Option<string?>("--new-label") { Description = "New label to apply" };
        var colorOption = new Option<string?>("--color") { Description = "New hex color (e.g. #FF0000)" };
        var publishOption = new Option<bool>("--publish") { Description = "Publish the entity after the change", DefaultValueFactory = _ => false };
        var dryRunOption = new Option<bool>("--dry-run") { Description = "Validate only, do not persist changes", DefaultValueFactory = _ => false };

        var command = new Command("update-option", "Update an option (label/color) on a column's local option set")
        {
            solutionOption, entityOption, columnOption, valueOption, labelOption, newLabelOption, colorOption, publishOption, dryRunOption,
            MetadataCommandGroup.ProfileOption, MetadataCommandGroup.EnvironmentOption
        };
        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var solution = parseResult.GetValue(solutionOption);
            var entity = parseResult.GetValue(entityOption)!;
            var column = parseResult.GetValue(columnOption)!;
            var value = parseResult.GetValue(valueOption);
            var label = parseResult.GetValue(labelOption);
            var newLabel = parseResult.GetValue(newLabelOption);
            var color = parseResult.GetValue(colorOption);
            var publish = parseResult.GetValue(publishOption);
            var dryRun = parseResult.GetValue(dryRunOption);
            var profile = parseResult.GetValue(MetadataCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(MetadataCommandGroup.EnvironmentOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);
            var writer = ServiceFactory.CreateOutputWriter(globalOptions);

            if (!value.HasValue && string.IsNullOrEmpty(label))
            {
                writer.WriteError(StructuredError.Create("MISSING_REQUIRED_FIELD", "Exactly one of --value or --label is required to identify the option."));
                return ExitCodes.ValidationError;
            }
            if (value.HasValue && !string.IsNullOrEmpty(label))
            {
                writer.WriteError(StructuredError.Create("INVALID_CONSTRAINT", "--value and --label are mutually exclusive."));
                return ExitCodes.ValidationError;
            }

            try
            {
                await using var serviceProvider = await ProfileServiceFactory.CreateFromProfilesAsync(
                    profile, environment, globalOptions.Verbose, globalOptions.Debug,
                    ProfileServiceFactory.DefaultDeviceCodeCallback, cancellationToken);
                var authoringService = serviceProvider.GetRequiredService<IMetadataAuthoringService>();

                if (!globalOptions.IsJsonMode)
                {
                    ConsoleHeader.WriteConnectedAs(serviceProvider.GetRequiredService<ResolvedConnectionInfo>());
                    Console.Error.WriteLine();
                    Console.Error.WriteLine($"Updating option on '{entity}.{column}'...");
                }

                await authoringService.UpdateColumnOptionAsync(new UpdateColumnOptionRequest
                {
                    EntityLogicalName = entity,
                    ColumnLogicalName = column,
                    Value = value,
                    Label = label,
                    NewLabel = newLabel,
                    Color = color,
                    SolutionUniqueName = solution,
                    Publish = publish,
                    DryRun = dryRun
                }, cancellationToken);

                if (globalOptions.IsJsonMode)
                    writer.WriteSuccess(new { entity, column, updated = true, dryRun });
                else if (dryRun)
                    Console.Error.WriteLine("[Dry-Run] Validation passed. No changes persisted.");
                else
                    Console.Error.WriteLine($"Option on '{entity}.{column}' updated successfully.");

                return ExitCodes.Success;
            }
            catch (MetadataValidationException ex)
            {
                writer.WriteError(StructuredError.Create(ex.ErrorCode, ex.Message));
                return ExitCodes.ValidationError;
            }
            catch (Exception ex)
            {
                writer.WriteError(ExceptionMapper.Map(ex, context: "updating option", debug: globalOptions.Debug));
                return ExceptionMapper.ToExitCode(ex);
            }
        });

        return command;
    }

    internal static Command CreateRemoveOptionCommand()
    {
        var solutionOption = new Option<string?>("--solution", "-s") { Description = "Solution unique name" };
        var entityOption = new Option<string>("--entity", "-e") { Description = "Logical name of the entity", Required = true };
        var columnOption = new Option<string>("--column", "-c") { Description = "Logical name of the Choice/Choices column", Required = true };
        var valueOption = new Option<int?>("--value") { Description = "Target option by value (mutually exclusive with --label)" };
        var labelOption = new Option<string?>("--label") { Description = "Target option by label (mutually exclusive with --value)" };
        var forceOption = new Option<bool>("--force") { Description = "Skip confirmation prompt", DefaultValueFactory = _ => false };
        var publishOption = new Option<bool>("--publish") { Description = "Publish the entity after the change", DefaultValueFactory = _ => false };
        var dryRunOption = new Option<bool>("--dry-run") { Description = "Validate that the target option exists, without removing it", DefaultValueFactory = _ => false };

        var command = new Command("remove-option", "Remove an option from a column's local option set")
        {
            solutionOption, entityOption, columnOption, valueOption, labelOption, forceOption, publishOption, dryRunOption,
            MetadataCommandGroup.ProfileOption, MetadataCommandGroup.EnvironmentOption
        };
        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var solution = parseResult.GetValue(solutionOption);
            var entity = parseResult.GetValue(entityOption)!;
            var column = parseResult.GetValue(columnOption)!;
            var value = parseResult.GetValue(valueOption);
            var label = parseResult.GetValue(labelOption);
            var force = parseResult.GetValue(forceOption);
            var publish = parseResult.GetValue(publishOption);
            var dryRun = parseResult.GetValue(dryRunOption);
            var profile = parseResult.GetValue(MetadataCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(MetadataCommandGroup.EnvironmentOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);
            var writer = ServiceFactory.CreateOutputWriter(globalOptions);

            if (!value.HasValue && string.IsNullOrEmpty(label))
            {
                writer.WriteError(StructuredError.Create("MISSING_REQUIRED_FIELD", "Exactly one of --value or --label is required to identify the option."));
                return ExitCodes.ValidationError;
            }
            if (value.HasValue && !string.IsNullOrEmpty(label))
            {
                writer.WriteError(StructuredError.Create("INVALID_CONSTRAINT", "--value and --label are mutually exclusive."));
                return ExitCodes.ValidationError;
            }

            var target = value.HasValue ? $"value {value}" : $"label '{label}'";

            if (!force && !dryRun)
            {
                if (!Console.IsInputRedirected)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.Error.WriteLine($"WARNING: This will remove the option ({target}) from '{entity}.{column}'.");
                    Console.Error.WriteLine("         Records using this value will lose their selection.");
                    Console.ResetColor();
                    Console.Error.Write("Continue? (y/N): ");
                    if (!string.Equals(Console.ReadLine(), "y", StringComparison.OrdinalIgnoreCase))
                    {
                        Console.Error.WriteLine("Cancelled.");
                        return ExitCodes.Success;
                    }
                }
                else
                {
                    writer.WriteError(StructuredError.Create("CONFIRMATION_REQUIRED", "Use --force to skip confirmation in non-interactive mode"));
                    return ExitCodes.ConfirmationRequired;
                }
            }

            try
            {
                await using var serviceProvider = await ProfileServiceFactory.CreateFromProfilesAsync(
                    profile, environment, globalOptions.Verbose, globalOptions.Debug,
                    ProfileServiceFactory.DefaultDeviceCodeCallback, cancellationToken);
                var authoringService = serviceProvider.GetRequiredService<IMetadataAuthoringService>();

                if (!globalOptions.IsJsonMode)
                {
                    ConsoleHeader.WriteConnectedAs(serviceProvider.GetRequiredService<ResolvedConnectionInfo>());
                    Console.Error.WriteLine();
                    Console.Error.WriteLine($"Removing option ({target}) from '{entity}.{column}'...");
                }

                await authoringService.RemoveColumnOptionAsync(new RemoveColumnOptionRequest
                {
                    EntityLogicalName = entity,
                    ColumnLogicalName = column,
                    Value = value,
                    Label = label,
                    SolutionUniqueName = solution,
                    Publish = publish,
                    DryRun = dryRun
                }, cancellationToken);

                if (globalOptions.IsJsonMode)
                    writer.WriteSuccess(new { entity, column, removed = !dryRun, dryRun });
                else if (dryRun)
                    Console.Error.WriteLine("[Dry-Run] Validation passed. Option exists and can be removed.");
                else
                    Console.Error.WriteLine($"Option ({target}) removed from '{entity}.{column}'.");

                return ExitCodes.Success;
            }
            catch (MetadataValidationException ex)
            {
                writer.WriteError(StructuredError.Create(ex.ErrorCode, ex.Message));
                return ExitCodes.ValidationError;
            }
            catch (Exception ex)
            {
                writer.WriteError(ExceptionMapper.Map(ex, context: "removing option", debug: globalOptions.Debug));
                return ExceptionMapper.ToExitCode(ex);
            }
        });

        return command;
    }
}
