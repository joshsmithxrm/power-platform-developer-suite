using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Dataverse.Metadata;
using PPDS.Dataverse.Metadata.Authoring;

using PPDS.Cli.Services.Metadata.Authoring;
namespace PPDS.Cli.Commands.Metadata.Choice;

/// <summary>
/// Command group for Dataverse global choice (option set) authoring.
/// </summary>
public static class ChoiceCommandGroup
{
    /// <summary>
    /// Creates the 'choice' command group with subcommands.
    /// </summary>
    public static Command Create()
    {
        var command = new Command("choice", "Create, update, or delete global choices (option sets)");

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

            return await ExecuteCreateAsync(
                solution, name, displayName, optionsRaw, description, dryRun,
                profile, environment, globalOptions, cancellationToken);
        });

        return command;
    }

    internal static OptionDefinition[] ParseOptionDefinitions(string raw)
    {
        var result = new List<OptionDefinition>();

        foreach (var pair in raw.Split(','))
        {
            var trimmed = pair.Trim();
            // Split on the LAST '=' so labels can contain '=' characters
            var lastEquals = trimmed.LastIndexOf('=');
            if (lastEquals > 0 && int.TryParse(trimmed[(lastEquals + 1)..].Trim(), out var value))
            {
                result.Add(new OptionDefinition { Label = trimmed[..lastEquals].Trim(), Value = value });
            }
        }

        return result.ToArray();
    }

    private static async Task<int> ExecuteCreateAsync(
        string solution,
        string name,
        string displayName,
        string optionsRaw,
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
            var options = ParseOptionDefinitions(optionsRaw);

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
                Console.Error.WriteLine(dryRun ? "Validating choice creation..." : "Creating global choice...");
            }

            var request = new CreateGlobalChoiceRequest
            {
                SolutionUniqueName = solution,
                SchemaName = name,
                DisplayName = displayName,
                Description = description ?? "",
                Options = options,
                DryRun = dryRun
            };

            var result = await authoringService.CreateGlobalChoiceAsync(request, ct: cancellationToken);

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
                    Console.Error.WriteLine($"Global choice '{result.Name}' created successfully.");
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
            var error = ExceptionMapper.Map(ex, context: "creating global choice", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
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

            return await ExecuteUpdateAsync(
                solution, name, displayName, description, dryRun,
                profile, environment, globalOptions, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteUpdateAsync(
        string solution,
        string name,
        string? displayName,
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
                Console.Error.WriteLine(dryRun ? "Validating choice update..." : $"Updating global choice '{name}'...");
            }

            var request = new UpdateGlobalChoiceRequest
            {
                SolutionUniqueName = solution,
                Name = name,
                DisplayName = displayName,
                Description = description,
                DryRun = dryRun
            };

            await authoringService.UpdateGlobalChoiceAsync(request, ct: cancellationToken);

            if (globalOptions.IsJsonMode)
            {
                writer.WriteSuccess(new { name, updated = true, dryRun });
            }
            else
            {
                Console.Error.WriteLine(dryRun
                    ? "[Dry-Run] Validation passed. No changes persisted."
                    : $"Global choice '{name}' updated successfully.");
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
            var error = ExceptionMapper.Map(ex, context: "updating global choice", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
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
            var dryRunRequest = new DeleteGlobalChoiceRequest
            {
                SolutionUniqueName = solution,
                Name = name,
                DryRun = true
            };

            await authoringService.DeleteGlobalChoiceAsync(dryRunRequest, ct: cancellationToken);

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
                    Console.Error.WriteLine($"WARNING: This will permanently delete global choice '{name}'.");
                    Console.ResetColor();
                    Console.Error.WriteLine();

                    var expectedConfirmation = $"DELETE CHOICE {name}";
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
                Console.Error.WriteLine($"Deleting global choice '{name}'...");
            }

            var deleteRequest = new DeleteGlobalChoiceRequest
            {
                SolutionUniqueName = solution,
                Name = name,
                DryRun = false
            };

            await authoringService.DeleteGlobalChoiceAsync(deleteRequest, ct: cancellationToken);

            if (globalOptions.IsJsonMode)
            {
                writer.WriteSuccess(new { name, deleted = true });
            }
            else
            {
                Console.Error.WriteLine($"Global choice '{name}' deleted successfully.");
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
            var error = ExceptionMapper.Map(ex, context: "deleting global choice", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
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

            return await ExecuteAddOptionAsync(
                solution, name, label, value, color,
                profile, environment, globalOptions, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteAddOptionAsync(
        string solution,
        string name,
        string label,
        int? value,
        string? color,
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
                Console.Error.WriteLine($"Adding option '{label}' to '{name}'...");
            }

            var request = new AddOptionValueRequest
            {
                SolutionUniqueName = solution,
                OptionSetName = name,
                Label = label,
                Value = value,
                Color = color
            };

            var assignedValue = await authoringService.AddOptionValueAsync(request, ct: cancellationToken);

            if (globalOptions.IsJsonMode)
            {
                writer.WriteSuccess(new { optionSetName = name, label, value = assignedValue });
            }
            else
            {
                Console.Error.WriteLine($"Option '{label}' added with value {assignedValue}.");
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
            var error = ExceptionMapper.Map(ex, context: "adding option value", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
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

            return await ExecuteUpdateOptionAsync(
                solution, name, value, label,
                profile, environment, globalOptions, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteUpdateOptionAsync(
        string solution,
        string name,
        int value,
        string label,
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
                Console.Error.WriteLine($"Updating option value {value} in '{name}'...");
            }

            var request = new UpdateOptionValueRequest
            {
                SolutionUniqueName = solution,
                OptionSetName = name,
                Value = value,
                Label = label
            };

            await authoringService.UpdateOptionValueAsync(request, ct: cancellationToken);

            if (globalOptions.IsJsonMode)
            {
                writer.WriteSuccess(new { optionSetName = name, value, label, updated = true });
            }
            else
            {
                Console.Error.WriteLine($"Option value {value} updated to '{label}'.");
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
            var error = ExceptionMapper.Map(ex, context: "updating option value", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
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

            return await ExecuteRemoveOptionAsync(
                solution, name, value, force,
                profile, environment, globalOptions, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteRemoveOptionAsync(
        string solution,
        string name,
        int value,
        bool force,
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

            // Confirmation prompt
            if (!force)
            {
                if (!Console.IsInputRedirected)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.Error.WriteLine($"WARNING: This will remove option value {value} from '{name}'.");
                    Console.Error.WriteLine("         Records using this value will lose their selection.");
                    Console.ResetColor();
                    Console.Error.WriteLine();

                    Console.Error.Write("Continue? (y/N): ");
                    var confirmation = Console.ReadLine();

                    if (!string.Equals(confirmation, "y", StringComparison.OrdinalIgnoreCase))
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
                Console.Error.WriteLine($"Removing option value {value} from '{name}'...");
            }

            var request = new DeleteOptionValueRequest
            {
                SolutionUniqueName = solution,
                OptionSetName = name,
                Value = value
            };

            await authoringService.DeleteOptionValueAsync(request, ct: cancellationToken);

            if (globalOptions.IsJsonMode)
            {
                writer.WriteSuccess(new { optionSetName = name, value, removed = true });
            }
            else
            {
                Console.Error.WriteLine($"Option value {value} removed from '{name}'.");
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
            var error = ExceptionMapper.Map(ex, context: "removing option value", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
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

            return await ExecuteReorderAsync(
                solution, name, orderRaw,
                profile, environment, globalOptions, cancellationToken);
        });

        return command;
    }

    internal static int[] ParseOrder(string raw)
    {
        return raw.Split(',')
            .Select(s => s.Trim())
            .Where(s => int.TryParse(s, out _))
            .Select(int.Parse)
            .ToArray();
    }

    private static async Task<int> ExecuteReorderAsync(
        string solution,
        string name,
        string orderRaw,
        string? profile,
        string? environment,
        GlobalOptionValues globalOptions,
        CancellationToken cancellationToken)
    {
        var writer = ServiceFactory.CreateOutputWriter(globalOptions);

        try
        {
            var order = ParseOrder(orderRaw);

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
                Console.Error.WriteLine($"Reordering options in '{name}'...");
            }

            var request = new ReorderOptionsRequest
            {
                SolutionUniqueName = solution,
                OptionSetName = name,
                Order = order
            };

            await authoringService.ReorderOptionsAsync(request, ct: cancellationToken);

            if (globalOptions.IsJsonMode)
            {
                writer.WriteSuccess(new { optionSetName = name, order, reordered = true });
            }
            else
            {
                Console.Error.WriteLine($"Options in '{name}' reordered successfully.");
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
            var error = ExceptionMapper.Map(ex, context: "reordering options", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }
}
