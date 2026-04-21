using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Dataverse.Metadata;
using PPDS.Dataverse.Metadata.Authoring;

using PPDS.Cli.Services.Metadata.Authoring;
namespace PPDS.Cli.Commands.Metadata.Relationship;

/// <summary>
/// Command group for Dataverse relationship authoring: create, update, delete.
/// </summary>
public static class RelationshipCommandGroup
{
    /// <summary>
    /// Creates the 'relationship' command group with subcommands.
    /// </summary>
    public static Command Create()
    {
        var command = new Command("relationship", "Create, update, or delete Dataverse relationships");

        command.Subcommands.Add(CreateCreateCommand());
        command.Subcommands.Add(CreateUpdateCommand());
        command.Subcommands.Add(CreateDeleteCommand());

        return command;
    }

    internal static Command CreateCreateCommand()
    {
        var solutionOption = new Option<string>("--solution", "-s")
        {
            Description = "Solution unique name to add the relationship to",
            Required = true
        };

        var fromOption = new Option<string>("--from")
        {
            Description = "Source entity logical name (referenced/parent for 1:N, entity1 for N:N)",
            Required = true
        };

        var toOption = new Option<string>("--to")
        {
            Description = "Target entity logical name (referencing/child for 1:N, entity2 for N:N)",
            Required = true
        };

        var typeOption = new Option<string>("--type")
        {
            Description = "Relationship type: one-to-many or many-to-many",
            Required = true
        };
        typeOption.AcceptOnlyFromAmong("one-to-many", "many-to-many");

        var nameOption = new Option<string>("--name")
        {
            Description = "Schema name for the relationship",
            Required = true
        };

        var lookupNameOption = new Option<string?>("--lookup-name")
        {
            Description = "Schema name of the lookup column (one-to-many only)"
        };

        var lookupDisplayNameOption = new Option<string?>("--lookup-display-name")
        {
            Description = "Display name of the lookup column (one-to-many only)"
        };

        var cascadeDeleteOption = new Option<CascadeBehavior?>("--cascade-delete")
        {
            Description = "Cascade behavior for delete: Cascade, Active, NoCascade, RemoveLink, Restrict"
        };

        var cascadeAssignOption = new Option<CascadeBehavior?>("--cascade-assign")
        {
            Description = "Cascade behavior for assign: Cascade, Active, NoCascade, UserOwned"
        };

        var dryRunOption = new Option<bool>("--dry-run")
        {
            Description = "Validate only, do not persist changes",
            DefaultValueFactory = _ => false
        };

        var command = new Command("create", "Create a new Dataverse relationship")
        {
            solutionOption,
            fromOption,
            toOption,
            typeOption,
            nameOption,
            lookupNameOption,
            lookupDisplayNameOption,
            cascadeDeleteOption,
            cascadeAssignOption,
            dryRunOption,
            MetadataCommandGroup.ProfileOption,
            MetadataCommandGroup.EnvironmentOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var solution = parseResult.GetValue(solutionOption)!;
            var from = parseResult.GetValue(fromOption)!;
            var to = parseResult.GetValue(toOption)!;
            var type = parseResult.GetValue(typeOption)!;
            var name = parseResult.GetValue(nameOption)!;
            var lookupName = parseResult.GetValue(lookupNameOption);
            var lookupDisplayName = parseResult.GetValue(lookupDisplayNameOption);
            var cascadeDelete = parseResult.GetValue(cascadeDeleteOption);
            var cascadeAssign = parseResult.GetValue(cascadeAssignOption);
            var dryRun = parseResult.GetValue(dryRunOption);
            var profile = parseResult.GetValue(MetadataCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(MetadataCommandGroup.EnvironmentOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            return await ExecuteCreateAsync(
                solution, from, to, type, name,
                lookupName, lookupDisplayName,
                cascadeDelete, cascadeAssign, dryRun,
                profile, environment, globalOptions, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteCreateAsync(
        string solution,
        string from,
        string to,
        string type,
        string name,
        string? lookupName,
        string? lookupDisplayName,
        CascadeBehavior? cascadeDelete,
        CascadeBehavior? cascadeAssign,
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
                    ? "Validating relationship creation..."
                    : $"Creating {type} relationship '{name}'...");
            }

            CascadeConfigurationDto? cascade = null;
            if (cascadeDelete.HasValue || cascadeAssign.HasValue)
            {
                cascade = new CascadeConfigurationDto
                {
                    Delete = cascadeDelete,
                    Assign = cascadeAssign
                };
            }

            CreateRelationshipResult result;

            if (type == "many-to-many")
            {
                var request = new CreateManyToManyRequest
                {
                    SolutionUniqueName = solution,
                    Entity1LogicalName = from,
                    Entity2LogicalName = to,
                    SchemaName = name,
                    DryRun = dryRun
                };

                result = await authoringService.CreateManyToManyAsync(request, ct: cancellationToken);
            }
            else
            {
                var request = new CreateOneToManyRequest
                {
                    SolutionUniqueName = solution,
                    ReferencedEntity = from,
                    ReferencingEntity = to,
                    SchemaName = name,
                    LookupSchemaName = lookupName ?? "",
                    LookupDisplayName = lookupDisplayName ?? "",
                    CascadeConfiguration = cascade,
                    DryRun = dryRun
                };

                result = await authoringService.CreateOneToManyAsync(request, ct: cancellationToken);
            }

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
                    Console.Error.WriteLine($"Relationship '{result.SchemaName}' created successfully.");
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
            var error = ExceptionMapper.Map(ex, context: "creating relationship", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    internal static Command CreateUpdateCommand()
    {
        var solutionOption = new Option<string>("--solution", "-s")
        {
            Description = "Solution unique name containing the relationship",
            Required = true
        };

        var nameOption = new Option<string>("--name")
        {
            Description = "Schema name of the relationship to update",
            Required = true
        };

        var cascadeDeleteOption = new Option<CascadeBehavior?>("--cascade-delete")
        {
            Description = "Updated cascade behavior for delete"
        };

        var cascadeAssignOption = new Option<CascadeBehavior?>("--cascade-assign")
        {
            Description = "Updated cascade behavior for assign"
        };

        var cascadeMergeOption = new Option<CascadeBehavior?>("--cascade-merge")
        {
            Description = "Updated cascade behavior for merge"
        };

        var cascadeReparentOption = new Option<CascadeBehavior?>("--cascade-reparent")
        {
            Description = "Updated cascade behavior for reparent"
        };

        var cascadeShareOption = new Option<CascadeBehavior?>("--cascade-share")
        {
            Description = "Updated cascade behavior for share"
        };

        var cascadeUnshareOption = new Option<CascadeBehavior?>("--cascade-unshare")
        {
            Description = "Updated cascade behavior for unshare"
        };

        var dryRunOption = new Option<bool>("--dry-run")
        {
            Description = "Validate only, do not persist changes",
            DefaultValueFactory = _ => false
        };

        var command = new Command("update", "Update an existing Dataverse relationship")
        {
            solutionOption,
            nameOption,
            cascadeDeleteOption,
            cascadeAssignOption,
            cascadeMergeOption,
            cascadeReparentOption,
            cascadeShareOption,
            cascadeUnshareOption,
            dryRunOption,
            MetadataCommandGroup.ProfileOption,
            MetadataCommandGroup.EnvironmentOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var solution = parseResult.GetValue(solutionOption)!;
            var name = parseResult.GetValue(nameOption)!;
            var cascadeDelete = parseResult.GetValue(cascadeDeleteOption);
            var cascadeAssign = parseResult.GetValue(cascadeAssignOption);
            var cascadeMerge = parseResult.GetValue(cascadeMergeOption);
            var cascadeReparent = parseResult.GetValue(cascadeReparentOption);
            var cascadeShare = parseResult.GetValue(cascadeShareOption);
            var cascadeUnshare = parseResult.GetValue(cascadeUnshareOption);
            var dryRun = parseResult.GetValue(dryRunOption);
            var profile = parseResult.GetValue(MetadataCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(MetadataCommandGroup.EnvironmentOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            return await ExecuteUpdateAsync(
                solution, name,
                cascadeDelete, cascadeAssign, cascadeMerge,
                cascadeReparent, cascadeShare, cascadeUnshare,
                dryRun, profile, environment, globalOptions, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteUpdateAsync(
        string solution,
        string name,
        CascadeBehavior? cascadeDelete,
        CascadeBehavior? cascadeAssign,
        CascadeBehavior? cascadeMerge,
        CascadeBehavior? cascadeReparent,
        CascadeBehavior? cascadeShare,
        CascadeBehavior? cascadeUnshare,
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
                Console.Error.WriteLine(dryRun ? "Validating relationship update..." : $"Updating relationship '{name}'...");
            }

            CascadeConfigurationDto? cascade = null;
            if (cascadeDelete.HasValue || cascadeAssign.HasValue || cascadeMerge.HasValue ||
                cascadeReparent.HasValue || cascadeShare.HasValue || cascadeUnshare.HasValue)
            {
                cascade = new CascadeConfigurationDto
                {
                    Delete = cascadeDelete,
                    Assign = cascadeAssign,
                    Merge = cascadeMerge,
                    Reparent = cascadeReparent,
                    Share = cascadeShare,
                    Unshare = cascadeUnshare
                };
            }

            var request = new UpdateRelationshipRequest
            {
                SolutionUniqueName = solution,
                SchemaName = name,
                CascadeConfiguration = cascade,
                DryRun = dryRun
            };

            await authoringService.UpdateRelationshipAsync(request, ct: cancellationToken);

            if (globalOptions.IsJsonMode)
            {
                writer.WriteSuccess(new { name, updated = true, dryRun });
            }
            else
            {
                Console.Error.WriteLine(dryRun
                    ? "[Dry-Run] Validation passed. No changes persisted."
                    : $"Relationship '{name}' updated successfully.");
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
            var error = ExceptionMapper.Map(ex, context: "updating relationship", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    internal static Command CreateDeleteCommand()
    {
        var solutionOption = new Option<string>("--solution", "-s")
        {
            Description = "Solution unique name containing the relationship",
            Required = true
        };

        var nameOption = new Option<string>("--name")
        {
            Description = "Schema name of the relationship to delete",
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

        var command = new Command("delete", "Delete a Dataverse relationship")
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

            return await ExecuteDeleteAsync(
                solution, name, force, dryRun,
                profile, environment, globalOptions, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteDeleteAsync(
        string solution,
        string name,
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
            var dryRunRequest = new DeleteRelationshipRequest
            {
                SolutionUniqueName = solution,
                SchemaName = name,
                DryRun = true
            };

            await authoringService.DeleteRelationshipAsync(dryRunRequest, ct: cancellationToken);

            if (dryRun)
            {
                if (globalOptions.IsJsonMode)
                {
                    writer.WriteSuccess(new { name, dryRun = true });
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
                    Console.Error.WriteLine($"WARNING: This will permanently delete relationship '{name}'.");
                    Console.ResetColor();
                    Console.Error.WriteLine();

                    var expectedConfirmation = $"DELETE RELATIONSHIP {name}";
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
                Console.Error.WriteLine($"Deleting relationship '{name}'...");
            }

            var deleteRequest = new DeleteRelationshipRequest
            {
                SolutionUniqueName = solution,
                SchemaName = name,
                DryRun = false
            };

            await authoringService.DeleteRelationshipAsync(deleteRequest, ct: cancellationToken);

            if (globalOptions.IsJsonMode)
            {
                writer.WriteSuccess(new { name, deleted = true });
            }
            else
            {
                Console.Error.WriteLine($"Relationship '{name}' deleted successfully.");
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
            var error = ExceptionMapper.Map(ex, context: "deleting relationship", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }
}
