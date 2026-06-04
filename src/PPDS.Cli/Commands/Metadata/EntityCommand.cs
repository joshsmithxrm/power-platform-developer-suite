using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Cli.Services.Metadata.Authoring;
using PPDS.Dataverse.Metadata;
using PPDS.Dataverse.Metadata.Authoring;

namespace PPDS.Cli.Commands.Metadata;

/// <summary>
/// Gets full metadata for a specific entity, and provides authoring subcommands
/// for create/update/delete and status reason management.
/// </summary>
public static class EntityCommand
{
    /// <summary>
    /// Creates the 'entity' command with read lookup and authoring subcommands.
    /// </summary>
    public static Command Create()
    {
        var entityArgument = new Argument<string>("entity")
        {
            Description = "The entity logical name (e.g., 'account')"
        };

        var includeOption = new Option<string[]?>("--include")
        {
            Description = "Include specific metadata sections: attributes, relationships, keys, privileges (comma-separated or multiple flags)",
            AllowMultipleArgumentsPerToken = true
        };

        var command = new Command("entity", "Get full metadata for a specific entity, or create/update/delete entities and manage status reasons (see subcommands)")
        {
            entityArgument,
            MetadataCommandGroup.ProfileOption,
            MetadataCommandGroup.EnvironmentOption,
            includeOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var entity = parseResult.GetValue(entityArgument);
            var profile = parseResult.GetValue(MetadataCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(MetadataCommandGroup.EnvironmentOption);
            var include = parseResult.GetValue(includeOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            return await ExecuteAsync(entity!, profile, environment, include, globalOptions, cancellationToken);
        });

        command.Subcommands.Add(CreateCreateCommand());
        command.Subcommands.Add(CreateUpdateCommand());
        command.Subcommands.Add(CreateDeleteCommand());
        command.Subcommands.Add(CreateAddStatusReasonCommand());
        command.Subcommands.Add(CreateListStatusReasonsCommand());
        command.Subcommands.Add(CreateUpdateStatusReasonCommand());
        command.Subcommands.Add(CreateRemoveStatusReasonCommand());

        return command;
    }

    // -------------------------------------------------------------------------
    // Read lookup (existing, unchanged)
    // -------------------------------------------------------------------------

    private static async Task<int> ExecuteAsync(
        string entity,
        string? profile,
        string? environment,
        string[]? include,
        GlobalOptionValues globalOptions,
        CancellationToken cancellationToken)
    {
        var writer = ServiceFactory.CreateOutputWriter(globalOptions);

        try
        {
            await using var serviceProvider = await ProfileServiceFactory.CreateFromProfileAsync(
                profile,
                environment,
                globalOptions.Verbose,
                globalOptions.Debug,
                ProfileServiceFactory.DefaultDeviceCodeCallback,
                cancellationToken: cancellationToken);

            var metadataService = serviceProvider.GetRequiredService<IMetadataQueryService>();

            if (!globalOptions.IsJsonMode)
            {
                var connectionInfo = serviceProvider.GetRequiredService<ResolvedConnectionInfo>();
                ConsoleHeader.WriteConnectedAs(connectionInfo);
                Console.Error.WriteLine();
                Console.Error.WriteLine($"Retrieving metadata for '{entity}'...");
            }

            var (includeAttrs, includeRels, includeKeys, includePrivs) = ParseIncludeOptions(include);

            var metadata = await metadataService.GetEntityAsync(
                entity,
                includeAttrs,
                includeRels,
                includeKeys,
                includePrivs,
                cancellationToken);

            if (globalOptions.IsJsonMode)
            {
                writer.WriteSuccess(metadata);
            }
            else
            {
                Console.WriteLine();
                Console.WriteLine($"Entity: {metadata.LogicalName}");
                Console.WriteLine($"  Display Name:     {metadata.DisplayName}");
                Console.WriteLine($"  Schema Name:      {metadata.SchemaName}");
                Console.WriteLine($"  Entity Set Name:  {metadata.EntitySetName}");
                Console.WriteLine($"  Primary ID:       {metadata.PrimaryIdAttribute}");
                Console.WriteLine($"  Primary Name:     {metadata.PrimaryNameAttribute}");
                Console.WriteLine($"  Object Type Code: {metadata.ObjectTypeCode}");
                Console.WriteLine($"  Ownership Type:   {metadata.OwnershipType}");
                Console.WriteLine($"  Custom Entity:    {metadata.IsCustomEntity}");
                Console.WriteLine($"  Managed:          {metadata.IsManaged}");

                if (metadata.Attributes.Count > 0)
                {
                    Console.WriteLine();
                    Console.WriteLine($"Attributes ({metadata.Attributes.Count}):");
                    foreach (var attr in metadata.Attributes.Take(20))
                    {
                        var markers = new List<string>();
                        if (attr.IsPrimaryId) markers.Add("PK");
                        if (attr.IsPrimaryName) markers.Add("name");
                        if (attr.IsCustomAttribute) markers.Add("custom");

                        var markerText = markers.Count > 0 ? $" [{string.Join(", ", markers)}]" : "";
                        Console.WriteLine($"  {attr.LogicalName,-35} {attr.AttributeType,-15} {attr.DisplayName}{markerText}");
                    }

                    if (metadata.Attributes.Count > 20)
                    {
                        Console.WriteLine($"  ... and {metadata.Attributes.Count - 20} more attributes");
                    }
                }

                var totalRels = metadata.OneToManyRelationships.Count +
                                metadata.ManyToOneRelationships.Count +
                                metadata.ManyToManyRelationships.Count;

                if (totalRels > 0)
                {
                    Console.WriteLine();
                    Console.WriteLine($"Relationships ({totalRels}):");
                    Console.WriteLine($"  1:N: {metadata.OneToManyRelationships.Count}");
                    Console.WriteLine($"  N:1: {metadata.ManyToOneRelationships.Count}");
                    Console.WriteLine($"  N:N: {metadata.ManyToManyRelationships.Count}");
                }

                if (metadata.Keys.Count > 0)
                {
                    Console.WriteLine();
                    Console.WriteLine($"Alternate Keys ({metadata.Keys.Count}):");
                    foreach (var key in metadata.Keys)
                    {
                        Console.WriteLine($"  {key.LogicalName}: {string.Join(", ", key.KeyAttributes)}");
                    }
                }

                if (metadata.Privileges.Count > 0)
                {
                    Console.WriteLine();
                    Console.WriteLine($"Privileges ({metadata.Privileges.Count}):");
                    foreach (var priv in metadata.Privileges)
                    {
                        Console.WriteLine($"  {priv.PrivilegeType}: {priv.Name}");
                    }
                }
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: $"retrieving entity '{entity}'", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    private static (bool attrs, bool rels, bool keys, bool privs) ParseIncludeOptions(string[]? include)
    {
        // Default: include all
        if (include == null || include.Length == 0)
        {
            return (true, true, true, true);
        }

        var sections = include
            .SelectMany(i => i.Split(',', StringSplitOptions.RemoveEmptyEntries))
            .Select(s => s.Trim().ToLowerInvariant())
            .ToHashSet();

        return (
            sections.Contains("attributes") || sections.Contains("attrs"),
            sections.Contains("relationships") || sections.Contains("rels"),
            sections.Contains("keys"),
            sections.Contains("privileges") || sections.Contains("privs")
        );
    }

    // -------------------------------------------------------------------------
    // Table authoring subcommands (ported from TableCommandGroup)
    // CreateCreateCommand / CreateUpdateCommand / CreateDeleteCommand are public
    // so the deprecated TableCommandGroup shim can call them.
    // ExecuteCreateAsync / ExecuteUpdateAsync / ExecuteDeleteAsync are internal
    // so the deprecated shim can delegate to them.
    // -------------------------------------------------------------------------

    /// <summary>Creates the 'entity create' subcommand.</summary>
    public static Command CreateCreateCommand()
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

            return await ExecuteCreateAsync(
                solution, name, displayName, pluralName, ownership, description, dryRun,
                profile, environment, globalOptions, cancellationToken);
        });

        return command;
    }

    internal static async Task<int> ExecuteCreateAsync(
        string solution,
        string name,
        string displayName,
        string pluralName,
        string ownership,
        string? description,
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
                Console.Error.WriteLine(dryRun ? "Validating table creation..." : "Creating table...");
            }

            var request = new CreateTableRequest
            {
                SolutionUniqueName = solution,
                SchemaName = name,
                DisplayName = displayName,
                PluralDisplayName = pluralName,
                OwnershipType = ownership,
                Description = description ?? "",
                DryRun = dryRun
            };

            var result = await authoringService.CreateTableAsync(request, ct: cancellationToken);

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
                    Console.Error.WriteLine($"Table '{result.LogicalName}' created successfully.");
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
            var error = ExceptionMapper.Map(ex, context: "creating table", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    /// <summary>Creates the 'entity update' subcommand.</summary>
    public static Command CreateUpdateCommand()
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

            return await ExecuteUpdateAsync(
                solution, entity, displayName, pluralName, description, dryRun,
                profile, environment, globalOptions, cancellationToken);
        });

        return command;
    }

    internal static async Task<int> ExecuteUpdateAsync(
        string solution,
        string entity,
        string? displayName,
        string? pluralName,
        string? description,
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
                Console.Error.WriteLine(dryRun ? "Validating table update..." : $"Updating table '{entity}'...");
            }

            var request = new UpdateTableRequest
            {
                SolutionUniqueName = solution,
                EntityLogicalName = entity,
                DisplayName = displayName,
                PluralDisplayName = pluralName,
                Description = description,
                DryRun = dryRun
            };

            await authoringService.UpdateTableAsync(request, ct: cancellationToken);

            var publishHint = $"ppds metadata publish {entity}";

            if (globalOptions.IsJsonMode)
            {
                writer.WriteSuccess(new
                {
                    entity,
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
                Console.Error.WriteLine($"Table '{entity}' updated. Run '{publishHint}' to publish changes.");
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
            var error = ExceptionMapper.Map(ex, context: "updating table", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    /// <summary>Creates the 'entity delete' subcommand.</summary>
    public static Command CreateDeleteCommand()
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

            return await ExecuteDeleteAsync(
                solution, entity, force, dryRun,
                profile, environment, globalOptions, cancellationToken);
        });

        return command;
    }

    internal static async Task<int> ExecuteDeleteAsync(
        string solution,
        string entity,
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

            // Always do a dry-run first to show dependencies
            var dryRunRequest = new DeleteTableRequest
            {
                SolutionUniqueName = solution,
                EntityLogicalName = entity,
                DryRun = true
            };

            await authoringService.DeleteTableAsync(dryRunRequest, ct: cancellationToken);

            if (dryRun)
            {
                if (globalOptions.IsJsonMode)
                {
                    writer.WriteSuccess(new { entity, dryRun = true });
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
                    Console.Error.WriteLine($"WARNING: This will permanently delete table '{entity}' and all its data.");
                    Console.Error.WriteLine("         This operation cannot be undone.");
                    Console.ResetColor();
                    Console.Error.WriteLine();

                    var expectedConfirmation = $"DELETE TABLE {entity}";
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
                Console.Error.WriteLine($"Deleting table '{entity}'...");
            }

            var deleteRequest = new DeleteTableRequest
            {
                SolutionUniqueName = solution,
                EntityLogicalName = entity,
                DryRun = false
            };

            await authoringService.DeleteTableAsync(deleteRequest, ct: cancellationToken);

            if (globalOptions.IsJsonMode)
            {
                writer.WriteSuccess(new { entity, deleted = true });
            }
            else
            {
                Console.Error.WriteLine($"Table '{entity}' deleted successfully.");
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
            var error = ExceptionMapper.Map(ex, context: "deleting table", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    // -------------------------------------------------------------------------
    // Status reason subcommands (Phase 4)
    // -------------------------------------------------------------------------

    private static Command CreateAddStatusReasonCommand()
    {
        var entityOption = new Option<string>("--entity", "-e")
        {
            Description = "Logical name of the entity",
            Required = true
        };

        var labelOption = new Option<string>("--label")
        {
            Description = "Display label for the new status reason",
            Required = true
        };

        var valueOption = new Option<int?>("--value")
        {
            Description = "Explicit option value (mutually exclusive with --solution for derivation)"
        };

        var solutionOption = new Option<string?>("--solution", "-s")
        {
            Description = "[Required when --value is omitted] Solution unique name; publisher prefix × 10,000 is used to derive the option value"
        };

        var stateOption = new Option<string?>("--state")
        {
            Description = "State the reason belongs to: Active or Inactive (use this OR --state-code, not both)"
        };
        stateOption.AcceptOnlyFromAmong("Active", "Inactive");

        var stateCodeOption = new Option<int?>("--state-code")
        {
            Description = "State code directly: 0 (Active) or 1 (Inactive) (use this OR --state, not both)"
        };

        var colorOption = new Option<string?>("--color")
        {
            Description = "Hex color for the status reason (e.g., #FF0000)"
        };

        var publishOption = new Option<bool>("--publish")
        {
            Description = "Publish the entity after the change",
            DefaultValueFactory = _ => false
        };

        var dryRunOption = new Option<bool>("--dry-run")
        {
            Description = "Validate only, do not persist changes",
            DefaultValueFactory = _ => false
        };

        var command = new Command("add-statusreason", "Add a new status reason to an entity's statuscode attribute")
        {
            entityOption,
            labelOption,
            valueOption,
            solutionOption,
            stateOption,
            stateCodeOption,
            colorOption,
            publishOption,
            dryRunOption,
            MetadataCommandGroup.ProfileOption,
            MetadataCommandGroup.EnvironmentOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var entity = parseResult.GetValue(entityOption)!;
            var label = parseResult.GetValue(labelOption)!;
            var value = parseResult.GetValue(valueOption);
            var solution = parseResult.GetValue(solutionOption);
            var state = parseResult.GetValue(stateOption);
            var stateCode = parseResult.GetValue(stateCodeOption);
            var color = parseResult.GetValue(colorOption);
            var publish = parseResult.GetValue(publishOption);
            var dryRun = parseResult.GetValue(dryRunOption);
            var profile = parseResult.GetValue(MetadataCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(MetadataCommandGroup.EnvironmentOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            var writer = ServiceFactory.CreateOutputWriter(globalOptions);

            // Exactly one of --state / --state-code required
            var hasState = state != null;
            var hasStateCode = stateCode.HasValue;

            if (!hasState && !hasStateCode)
            {
                writer.WriteError(StructuredError.Create(
                    "MISSING_REQUIRED_FIELD",
                    "Exactly one of --state or --state-code is required."));
                return ExitCodes.ValidationError;
            }

            if (hasState && hasStateCode)
            {
                writer.WriteError(StructuredError.Create(
                    "INVALID_CONSTRAINT",
                    "--state and --state-code are mutually exclusive; provide exactly one."));
                return ExitCodes.ValidationError;
            }

            // --state-code, when supplied directly, must be a valid state code (0 or 1).
            if (hasStateCode && stateCode!.Value != 0 && stateCode.Value != 1)
            {
                writer.WriteError(StructuredError.Create(
                    "INVALID_CONSTRAINT",
                    "--state-code must be 0 (Active) or 1 (Inactive)."));
                return ExitCodes.ValidationError;
            }

            // Exactly one of --value / --solution required
            var hasValue = value.HasValue;
            var hasSolution = !string.IsNullOrWhiteSpace(solution);

            if (!hasValue && !hasSolution)
            {
                writer.WriteError(StructuredError.Create(
                    "MISSING_REQUIRED_FIELD",
                    "Exactly one of --value or --solution is required to determine the option value."));
                return ExitCodes.ValidationError;
            }

            if (hasValue && hasSolution)
            {
                writer.WriteError(StructuredError.Create(
                    "INVALID_CONSTRAINT",
                    "--value and --solution are mutually exclusive; provide exactly one."));
                return ExitCodes.ValidationError;
            }

            // Resolve state code
            int resolvedStateCode = hasStateCode
                ? stateCode!.Value
                : state == "Active" ? 0 : 1;

            return await ExecuteAddStatusReasonAsync(
                entity, label, value, solution, resolvedStateCode, color, publish, dryRun,
                profile, environment, globalOptions, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteAddStatusReasonAsync(
        string entity,
        string label,
        int? value,
        string? solution,
        int stateCode,
        string? color,
        bool publish,
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
                Console.Error.WriteLine(dryRun
                    ? $"Validating status reason addition for '{entity}'..."
                    : $"Adding status reason '{label}' to '{entity}'...");
            }

            var request = new AddStatusReasonRequest
            {
                EntityLogicalName = entity,
                Label = label,
                StateCode = stateCode,
                Value = value,
                SolutionUniqueName = solution,
                Color = color,
                Publish = publish,
                DryRun = dryRun
            };

            var assignedValue = await authoringService.AddStatusReasonAsync(request, reporter: null, ct: cancellationToken);

            if (globalOptions.IsJsonMode)
            {
                writer.WriteSuccess(new
                {
                    entity,
                    label,
                    value = assignedValue,
                    stateCode,
                    color,
                    dryRun
                });
            }
            else
            {
                if (dryRun)
                {
                    Console.Error.WriteLine("[Dry-Run] Validation passed. No changes persisted.");
                }
                else
                {
                    Console.Error.WriteLine($"Status reason '{label}' added to '{entity}' with value {assignedValue}.");
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
            var error = ExceptionMapper.Map(ex, context: $"adding status reason to '{entity}'", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    private static Command CreateListStatusReasonsCommand()
    {
        var entityOption = new Option<string>("--entity", "-e")
        {
            Description = "Logical name of the entity",
            Required = true
        };

        var command = new Command("list-statusreasons", "List all status reasons for an entity's statuscode attribute")
        {
            entityOption,
            MetadataCommandGroup.ProfileOption,
            MetadataCommandGroup.EnvironmentOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var entity = parseResult.GetValue(entityOption)!;
            var profile = parseResult.GetValue(MetadataCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(MetadataCommandGroup.EnvironmentOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            return await ExecuteListStatusReasonsAsync(
                entity, profile, environment, globalOptions, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteListStatusReasonsAsync(
        string entity,
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
                Console.Error.WriteLine($"Retrieving status reasons for '{entity}'...");
            }

            var reasons = await authoringService.ListStatusReasonsAsync(entity, cancellationToken);

            if (globalOptions.IsJsonMode)
            {
                writer.WriteSuccess(reasons);
            }
            else
            {
                // I1: the status-reason listing is the requested DATA — write it to stdout so it is
                // pipeable. Only the "Retrieving..." preamble (above) stays on stderr.
                Console.WriteLine();
                Console.WriteLine($"Status Reasons for '{entity}' ({reasons.Count}):");
                Console.WriteLine();
                Console.WriteLine($"  {"Value",-8} {"State",-10} {"Label",-40} {"Color"}");
                Console.WriteLine($"  {new string('-', 8),-8} {new string('-', 10),-10} {new string('-', 40),-40} {new string('-', 10)}");

                foreach (var reason in reasons.OrderBy(r => r.StateCode).ThenBy(r => r.Value))
                {
                    var colorDisplay = reason.Color ?? "";
                    Console.WriteLine($"  {reason.Value,-8} {reason.StateLabel,-10} {reason.Label,-40} {colorDisplay}");
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
            var error = ExceptionMapper.Map(ex, context: $"listing status reasons for '{entity}'", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    private static Command CreateUpdateStatusReasonCommand()
    {
        var entityOption = new Option<string>("--entity", "-e")
        {
            Description = "Logical name of the entity",
            Required = true
        };

        var valueOption = new Option<int?>("--value")
        {
            Description = "Target status reason by option value (mutually exclusive with --label)"
        };

        var labelOption = new Option<string?>("--label")
        {
            Description = "Target status reason by current label (mutually exclusive with --value)"
        };

        var newLabelOption = new Option<string?>("--new-label")
        {
            Description = "New label to apply to the status reason"
        };

        var colorOption = new Option<string?>("--color")
        {
            Description = "New hex color for the status reason (e.g., #FF0000)"
        };

        var solutionOption = new Option<string?>("--solution", "-s")
        {
            Description = "Solution unique name"
        };

        var publishOption = new Option<bool>("--publish")
        {
            Description = "Publish the entity after the change",
            DefaultValueFactory = _ => false
        };

        var command = new Command("update-statusreason", "Update an existing status reason on an entity")
        {
            entityOption,
            valueOption,
            labelOption,
            newLabelOption,
            colorOption,
            solutionOption,
            publishOption,
            MetadataCommandGroup.ProfileOption,
            MetadataCommandGroup.EnvironmentOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var entity = parseResult.GetValue(entityOption)!;
            var value = parseResult.GetValue(valueOption);
            var label = parseResult.GetValue(labelOption);
            var newLabel = parseResult.GetValue(newLabelOption);
            var color = parseResult.GetValue(colorOption);
            var solution = parseResult.GetValue(solutionOption);
            var publish = parseResult.GetValue(publishOption);
            var profile = parseResult.GetValue(MetadataCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(MetadataCommandGroup.EnvironmentOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            var writer = ServiceFactory.CreateOutputWriter(globalOptions);

            // Exactly one of --value / --label required for targeting
            var hasValue = value.HasValue;
            var hasLabel = label != null;

            if (!hasValue && !hasLabel)
            {
                writer.WriteError(StructuredError.Create(
                    "MISSING_REQUIRED_FIELD",
                    "Exactly one of --value or --label is required to identify the status reason to update."));
                return ExitCodes.ValidationError;
            }

            if (hasValue && hasLabel)
            {
                writer.WriteError(StructuredError.Create(
                    "INVALID_CONSTRAINT",
                    "--value and --label are mutually exclusive; provide exactly one to identify the target."));
                return ExitCodes.ValidationError;
            }

            return await ExecuteUpdateStatusReasonAsync(
                entity, value, label, newLabel, color, solution, publish,
                profile, environment, globalOptions, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteUpdateStatusReasonAsync(
        string entity,
        int? value,
        string? label,
        string? newLabel,
        string? color,
        string? solution,
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

            var target = value.HasValue ? $"value {value}" : $"label '{label}'";

            if (!globalOptions.IsJsonMode)
            {
                var connectionInfo = serviceProvider.GetRequiredService<ResolvedConnectionInfo>();
                ConsoleHeader.WriteConnectedAs(connectionInfo);
                Console.Error.WriteLine();
                Console.Error.WriteLine($"Updating status reason ({target}) on '{entity}'...");
            }

            var request = new UpdateStatusReasonRequest
            {
                EntityLogicalName = entity,
                Value = value,
                Label = label,
                NewLabel = newLabel,
                Color = color,
                SolutionUniqueName = solution,
                Publish = publish
            };

            await authoringService.UpdateStatusReasonAsync(request, cancellationToken);

            if (globalOptions.IsJsonMode)
            {
                writer.WriteSuccess(new
                {
                    entity,
                    target = value.HasValue ? (object)value.Value : label!,
                    updated = true
                });
            }
            else
            {
                Console.Error.WriteLine($"Status reason ({target}) on '{entity}' updated successfully.");
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
            var error = ExceptionMapper.Map(ex, context: $"updating status reason on '{entity}'", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    private static Command CreateRemoveStatusReasonCommand()
    {
        var entityOption = new Option<string>("--entity", "-e")
        {
            Description = "Logical name of the entity",
            Required = true
        };

        var valueOption = new Option<int?>("--value")
        {
            Description = "Target status reason by option value (mutually exclusive with --label)"
        };

        var labelOption = new Option<string?>("--label")
        {
            Description = "Target status reason by label (mutually exclusive with --value)"
        };

        var solutionOption = new Option<string?>("--solution", "-s")
        {
            Description = "Solution unique name"
        };

        var forceOption = new Option<bool>("--force")
        {
            Description = "Skip confirmation prompt",
            DefaultValueFactory = _ => false
        };

        var publishOption = new Option<bool>("--publish")
        {
            Description = "Publish the entity after the change",
            DefaultValueFactory = _ => false
        };

        var command = new Command("remove-statusreason", "Remove a status reason from an entity's statuscode attribute")
        {
            entityOption,
            valueOption,
            labelOption,
            solutionOption,
            forceOption,
            publishOption,
            MetadataCommandGroup.ProfileOption,
            MetadataCommandGroup.EnvironmentOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var entity = parseResult.GetValue(entityOption)!;
            var value = parseResult.GetValue(valueOption);
            var label = parseResult.GetValue(labelOption);
            var solution = parseResult.GetValue(solutionOption);
            var force = parseResult.GetValue(forceOption);
            var publish = parseResult.GetValue(publishOption);
            var profile = parseResult.GetValue(MetadataCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(MetadataCommandGroup.EnvironmentOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            var writer = ServiceFactory.CreateOutputWriter(globalOptions);

            // Exactly one of --value / --label required for targeting
            var hasValue = value.HasValue;
            var hasLabel = label != null;

            if (!hasValue && !hasLabel)
            {
                writer.WriteError(StructuredError.Create(
                    "MISSING_REQUIRED_FIELD",
                    "Exactly one of --value or --label is required to identify the status reason to remove."));
                return ExitCodes.ValidationError;
            }

            if (hasValue && hasLabel)
            {
                writer.WriteError(StructuredError.Create(
                    "INVALID_CONSTRAINT",
                    "--value and --label are mutually exclusive; provide exactly one to identify the target."));
                return ExitCodes.ValidationError;
            }

            return await ExecuteRemoveStatusReasonAsync(
                entity, value, label, solution, force, publish,
                profile, environment, globalOptions, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteRemoveStatusReasonAsync(
        string entity,
        int? value,
        string? label,
        string? solution,
        bool force,
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
            }

            // Confirmation prompt (non-force, interactive)
            if (!force)
            {
                if (!Console.IsInputRedirected)
                {
                    var target = value.HasValue ? $"value {value}" : $"label '{label}'";
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.Error.WriteLine($"WARNING: This will permanently remove the status reason ({target}) from '{entity}'.");
                    Console.ResetColor();
                    Console.Error.WriteLine();
                    Console.Error.Write("Type 'yes' to confirm, or Ctrl+C to cancel: ");
                    var confirmation = Console.ReadLine();

                    if (!string.Equals(confirmation, "yes", StringComparison.OrdinalIgnoreCase))
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

            var targetDisplay = value.HasValue ? $"value {value}" : $"label '{label}'";

            if (!globalOptions.IsJsonMode)
            {
                Console.Error.WriteLine($"Removing status reason ({targetDisplay}) from '{entity}'...");
            }

            var request = new RemoveStatusReasonRequest
            {
                EntityLogicalName = entity,
                Value = value,
                Label = label,
                SolutionUniqueName = solution,
                Publish = publish
            };

            await authoringService.RemoveStatusReasonAsync(request, cancellationToken);

            if (globalOptions.IsJsonMode)
            {
                writer.WriteSuccess(new
                {
                    entity,
                    target = value.HasValue ? (object)value.Value : label!,
                    removed = true
                });
            }
            else
            {
                Console.Error.WriteLine($"Status reason ({targetDisplay}) removed from '{entity}' successfully.");
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
            var error = ExceptionMapper.Map(ex, context: $"removing status reason from '{entity}'", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }
}
