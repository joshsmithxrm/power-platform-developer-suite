using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Dataverse.Metadata;
using PPDS.Dataverse.Metadata.Authoring;

using PPDS.Cli.Services.Metadata.Authoring;
namespace PPDS.Cli.Commands.Metadata.Key;

/// <summary>
/// Command group for Dataverse alternate key authoring: create, delete, reactivate.
/// </summary>
public static class KeyCommandGroup
{
    /// <summary>
    /// Creates the 'key' command group with subcommands.
    /// </summary>
    public static Command Create()
    {
        var command = new Command("key", "Create, delete, or reactivate alternate keys on Dataverse tables");

        command.Subcommands.Add(CreateCreateCommand());
        command.Subcommands.Add(CreateDeleteCommand());
        command.Subcommands.Add(CreateReactivateCommand());

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
            Description = "Logical name of the entity to add the key to",
            Required = true
        };

        var nameOption = new Option<string>("--name")
        {
            Description = "Schema name for the alternate key",
            Required = true
        };

        var displayNameOption = new Option<string>("--display-name")
        {
            Description = "Display name of the alternate key",
            Required = true
        };

        var attributesOption = new Option<string>("--attributes")
        {
            Description = "Comma-separated list of attribute logical names: \"attr1,attr2\"",
            Required = true
        };

        var dryRunOption = new Option<bool>("--dry-run")
        {
            Description = "Validate only, do not persist changes",
            DefaultValueFactory = _ => false
        };

        var command = new Command("create", "Create an alternate key on a Dataverse table")
        {
            solutionOption,
            entityOption,
            nameOption,
            displayNameOption,
            attributesOption,
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
            var attributesRaw = parseResult.GetValue(attributesOption)!;
            var dryRun = parseResult.GetValue(dryRunOption);
            var profile = parseResult.GetValue(MetadataCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(MetadataCommandGroup.EnvironmentOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            return await ExecuteCreateAsync(
                solution, entity, name, displayName, attributesRaw, dryRun,
                profile, environment, globalOptions, cancellationToken);
        });

        return command;
    }

    internal static string[] ParseAttributes(string raw)
    {
        return raw.Split(',')
            .Select(s => s.Trim())
            .Where(s => !string.IsNullOrEmpty(s))
            .ToArray();
    }

    private static async Task<int> ExecuteCreateAsync(
        string solution,
        string entity,
        string name,
        string displayName,
        string attributesRaw,
        bool dryRun,
        string? profile,
        string? environment,
        GlobalOptionValues globalOptions,
        CancellationToken cancellationToken)
    {
        var writer = ServiceFactory.CreateOutputWriter(globalOptions);

        try
        {
            var attributes = ParseAttributes(attributesRaw);

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
                    ? "Validating key creation..."
                    : $"Creating alternate key '{name}' on '{entity}'...");
            }

            var request = new CreateKeyRequest
            {
                SolutionUniqueName = solution,
                EntityLogicalName = entity,
                SchemaName = name,
                DisplayName = displayName,
                KeyAttributes = attributes,
                DryRun = dryRun
            };

            var result = await authoringService.CreateKeyAsync(request, ct: cancellationToken);

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
                    Console.Error.WriteLine($"Alternate key '{result.SchemaName}' created on '{entity}'.");
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
            var error = ExceptionMapper.Map(ex, context: "creating alternate key", debug: globalOptions.Debug);
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
            Description = "Logical name of the entity containing the key",
            Required = true
        };

        var nameOption = new Option<string>("--name")
        {
            Description = "Logical name of the key to delete",
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

        var command = new Command("delete", "Delete an alternate key from a Dataverse table")
        {
            solutionOption,
            entityOption,
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
            var entity = parseResult.GetValue(entityOption)!;
            var name = parseResult.GetValue(nameOption)!;
            var force = parseResult.GetValue(forceOption);
            var dryRun = parseResult.GetValue(dryRunOption);
            var profile = parseResult.GetValue(MetadataCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(MetadataCommandGroup.EnvironmentOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            return await ExecuteDeleteAsync(
                solution, entity, name, force, dryRun,
                profile, environment, globalOptions, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteDeleteAsync(
        string solution,
        string entity,
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
            var dryRunRequest = new DeleteKeyRequest
            {
                SolutionUniqueName = solution,
                EntityLogicalName = entity,
                KeyLogicalName = name,
                DryRun = true
            };

            await authoringService.DeleteKeyAsync(dryRunRequest, ct: cancellationToken);

            if (dryRun)
            {
                if (globalOptions.IsJsonMode)
                {
                    writer.WriteSuccess(new { entity, key = name, dryRun = true });
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
                    Console.Error.WriteLine($"WARNING: This will permanently delete alternate key '{name}' from '{entity}'.");
                    Console.ResetColor();
                    Console.Error.WriteLine();

                    var expectedConfirmation = $"DELETE KEY {entity}.{name}";
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
                Console.Error.WriteLine($"Deleting alternate key '{name}' from '{entity}'...");
            }

            var deleteRequest = new DeleteKeyRequest
            {
                SolutionUniqueName = solution,
                EntityLogicalName = entity,
                KeyLogicalName = name,
                DryRun = false
            };

            await authoringService.DeleteKeyAsync(deleteRequest, ct: cancellationToken);

            if (globalOptions.IsJsonMode)
            {
                writer.WriteSuccess(new { entity, key = name, deleted = true });
            }
            else
            {
                Console.Error.WriteLine($"Alternate key '{name}' deleted from '{entity}'.");
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
            var error = ExceptionMapper.Map(ex, context: "deleting alternate key", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    internal static Command CreateReactivateCommand()
    {
        var solutionOption = new Option<string>("--solution", "-s")
        {
            Description = "Solution unique name containing the table",
            Required = true
        };

        var entityOption = new Option<string>("--entity", "-e")
        {
            Description = "Logical name of the entity containing the key",
            Required = true
        };

        var nameOption = new Option<string>("--name")
        {
            Description = "Logical name of the key to reactivate",
            Required = true
        };

        var command = new Command("reactivate", "Reactivate a failed alternate key")
        {
            solutionOption,
            entityOption,
            nameOption,
            MetadataCommandGroup.ProfileOption,
            MetadataCommandGroup.EnvironmentOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var solution = parseResult.GetValue(solutionOption)!;
            var entity = parseResult.GetValue(entityOption)!;
            var name = parseResult.GetValue(nameOption)!;
            var profile = parseResult.GetValue(MetadataCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(MetadataCommandGroup.EnvironmentOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            return await ExecuteReactivateAsync(
                solution, entity, name,
                profile, environment, globalOptions, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteReactivateAsync(
        string solution,
        string entity,
        string name,
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
                Console.Error.WriteLine($"Reactivating alternate key '{name}' on '{entity}'...");
            }

            var request = new ReactivateKeyRequest
            {
                SolutionUniqueName = solution,
                EntityLogicalName = entity,
                KeyLogicalName = name
            };

            await authoringService.ReactivateKeyAsync(request, ct: cancellationToken);

            if (globalOptions.IsJsonMode)
            {
                writer.WriteSuccess(new { entity, key = name, reactivated = true });
            }
            else
            {
                Console.Error.WriteLine($"Alternate key '{name}' on '{entity}' reactivated successfully.");
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
            var error = ExceptionMapper.Map(ex, context: "reactivating alternate key", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }
}
