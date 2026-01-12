using System.CommandLine;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Cli.Plugins.Registration;

namespace PPDS.Cli.Commands.Plugins;

/// <summary>
/// Update existing plugin registrations in a Dataverse environment.
/// </summary>
public static class UpdateCommand
{
    public static Command Create()
    {
        var command = new Command("update", "Update existing plugin registrations")
        {
            CreateAssemblySubcommand(),
            CreatePackageSubcommand(),
            CreateStepSubcommand(),
            CreateImageSubcommand()
        };

        return command;
    }

    #region Assembly Subcommand

    private static Command CreateAssemblySubcommand()
    {
        var nameArgument = new Argument<string>("name")
        {
            Description = "Assembly name"
        };
        var pathArgument = new Argument<FileInfo>("path")
        {
            Description = "Path to the assembly DLL"
        }.AcceptExistingOnly();

        var command = new Command("assembly", "Update assembly content with new DLL")
        {
            nameArgument,
            pathArgument,
            PluginsCommandGroup.ProfileOption,
            PluginsCommandGroup.EnvironmentOption,
            PluginsCommandGroup.SolutionOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var name = parseResult.GetValue(nameArgument)!;
            var path = parseResult.GetValue(pathArgument)!;
            var profile = parseResult.GetValue(PluginsCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(PluginsCommandGroup.EnvironmentOption);
            var solution = parseResult.GetValue(PluginsCommandGroup.SolutionOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            return await ExecuteAssemblyUpdateAsync(name, path, profile, environment, solution, globalOptions, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteAssemblyUpdateAsync(
        string name,
        FileInfo path,
        string? profile,
        string? environment,
        string? solution,
        GlobalOptionValues globalOptions,
        CancellationToken cancellationToken)
    {
        var writer = ServiceFactory.CreateOutputWriter(globalOptions);

        try
        {
            await using var serviceProvider = await ProfileServiceFactory.CreateFromProfilesAsync(
                profile,
                environment,
                globalOptions.Verbose,
                globalOptions.Debug,
                ProfileServiceFactory.DefaultDeviceCodeCallback,
                cancellationToken);

            var registrationService = serviceProvider.GetRequiredService<IPluginRegistrationService>();

            if (!globalOptions.IsJsonMode)
            {
                var connectionInfo = serviceProvider.GetRequiredService<ResolvedConnectionInfo>();
                ConsoleHeader.WriteConnectedAs(connectionInfo);
                Console.Error.WriteLine();
            }

            // Check if assembly exists
            var existing = await registrationService.GetAssemblyByNameAsync(name, cancellationToken);
            if (existing == null)
            {
                writer.WriteError(new StructuredError(
                    ErrorCodes.Operation.NotFound,
                    $"Assembly '{name}' not found.",
                    Target: name));
                return ExitCodes.NotFoundError;
            }

            // Read and upload assembly
            var content = await File.ReadAllBytesAsync(path.FullName, cancellationToken);
            var assemblyId = await registrationService.UpsertAssemblyAsync(name, content, solution, cancellationToken);

            var result = new UpdateResult
            {
                Type = "assembly",
                Name = name,
                Id = assemblyId,
                Changes = ["content updated"]
            };

            if (globalOptions.IsJsonMode)
            {
                writer.WriteSuccess(result);
            }
            else
            {
                Console.Error.WriteLine($"[check] Assembly updated: {name}");
                Console.Error.WriteLine($"  Changes: content updated from {path.Name}");
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: "updating assembly", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    #endregion

    #region Package Subcommand

    private static Command CreatePackageSubcommand()
    {
        var nameArgument = new Argument<string>("name")
        {
            Description = "Package name or unique name"
        };
        var pathArgument = new Argument<FileInfo>("path")
        {
            Description = "Path to the .nupkg file"
        }.AcceptExistingOnly();

        var command = new Command("package", "Update package content with new nupkg")
        {
            nameArgument,
            pathArgument,
            PluginsCommandGroup.ProfileOption,
            PluginsCommandGroup.EnvironmentOption,
            PluginsCommandGroup.SolutionOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var name = parseResult.GetValue(nameArgument)!;
            var path = parseResult.GetValue(pathArgument)!;
            var profile = parseResult.GetValue(PluginsCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(PluginsCommandGroup.EnvironmentOption);
            var solution = parseResult.GetValue(PluginsCommandGroup.SolutionOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            return await ExecutePackageUpdateAsync(name, path, profile, environment, solution, globalOptions, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecutePackageUpdateAsync(
        string name,
        FileInfo path,
        string? profile,
        string? environment,
        string? solution,
        GlobalOptionValues globalOptions,
        CancellationToken cancellationToken)
    {
        var writer = ServiceFactory.CreateOutputWriter(globalOptions);

        try
        {
            await using var serviceProvider = await ProfileServiceFactory.CreateFromProfilesAsync(
                profile,
                environment,
                globalOptions.Verbose,
                globalOptions.Debug,
                ProfileServiceFactory.DefaultDeviceCodeCallback,
                cancellationToken);

            var registrationService = serviceProvider.GetRequiredService<IPluginRegistrationService>();

            if (!globalOptions.IsJsonMode)
            {
                var connectionInfo = serviceProvider.GetRequiredService<ResolvedConnectionInfo>();
                ConsoleHeader.WriteConnectedAs(connectionInfo);
                Console.Error.WriteLine();
            }

            // Check if package exists
            var existing = await registrationService.GetPackageByNameAsync(name, cancellationToken);
            if (existing == null)
            {
                writer.WriteError(new StructuredError(
                    ErrorCodes.Operation.NotFound,
                    $"Package '{name}' not found.",
                    Target: name));
                return ExitCodes.NotFoundError;
            }

            // Read and upload package
            var content = await File.ReadAllBytesAsync(path.FullName, cancellationToken);
            var packageId = await registrationService.UpsertPackageAsync(name, content, solution, cancellationToken);

            var result = new UpdateResult
            {
                Type = "package",
                Name = name,
                Id = packageId,
                Changes = ["content updated"]
            };

            if (globalOptions.IsJsonMode)
            {
                writer.WriteSuccess(result);
            }
            else
            {
                Console.Error.WriteLine($"[check] Package updated: {name}");
                Console.Error.WriteLine($"  Changes: content updated from {path.Name}");
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: "updating package", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    #endregion

    #region Step Subcommand

    private static Command CreateStepSubcommand()
    {
        var nameOrIdArgument = new Argument<string>("name-or-id")
        {
            Description = "Step name or GUID"
        };

        var modeOption = new Option<string?>("--mode")
        {
            Description = "Execution mode: Sync or Async"
        };

        var stageOption = new Option<string?>("--stage")
        {
            Description = "Pipeline stage: PreValidation, PreOperation, or PostOperation"
        };

        var rankOption = new Option<int?>("--rank")
        {
            Description = "Execution order (1-999999)"
        };

        var filteringAttributesOption = new Option<string?>("--filtering-attributes")
        {
            Description = "Comma-separated list of attributes that trigger this step"
        };

        var descriptionOption = new Option<string?>("--description")
        {
            Description = "Step description"
        };

        var command = new Command("step", "Update step properties")
        {
            nameOrIdArgument,
            modeOption,
            stageOption,
            rankOption,
            filteringAttributesOption,
            descriptionOption,
            PluginsCommandGroup.ProfileOption,
            PluginsCommandGroup.EnvironmentOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var nameOrId = parseResult.GetValue(nameOrIdArgument)!;
            var mode = parseResult.GetValue(modeOption);
            var stage = parseResult.GetValue(stageOption);
            var rank = parseResult.GetValue(rankOption);
            var filteringAttributes = parseResult.GetValue(filteringAttributesOption);
            var description = parseResult.GetValue(descriptionOption);
            var profile = parseResult.GetValue(PluginsCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(PluginsCommandGroup.EnvironmentOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            return await ExecuteStepUpdateAsync(
                nameOrId, mode, stage, rank, filteringAttributes, description,
                profile, environment, globalOptions, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteStepUpdateAsync(
        string nameOrId,
        string? mode,
        string? stage,
        int? rank,
        string? filteringAttributes,
        string? description,
        string? profile,
        string? environment,
        GlobalOptionValues globalOptions,
        CancellationToken cancellationToken)
    {
        var writer = ServiceFactory.CreateOutputWriter(globalOptions);

        try
        {
            await using var serviceProvider = await ProfileServiceFactory.CreateFromProfilesAsync(
                profile,
                environment,
                globalOptions.Verbose,
                globalOptions.Debug,
                ProfileServiceFactory.DefaultDeviceCodeCallback,
                cancellationToken);

            var registrationService = serviceProvider.GetRequiredService<IPluginRegistrationService>();

            if (!globalOptions.IsJsonMode)
            {
                var connectionInfo = serviceProvider.GetRequiredService<ResolvedConnectionInfo>();
                ConsoleHeader.WriteConnectedAs(connectionInfo);
                Console.Error.WriteLine();
            }

            // Normalize mode values
            var normalizedMode = NormalizeMode(mode);

            // Normalize stage values
            var normalizedStage = NormalizeStage(stage);

            // Check if step exists
            var existing = await registrationService.GetStepByNameOrIdAsync(nameOrId, cancellationToken);
            if (existing == null)
            {
                writer.WriteError(new StructuredError(
                    ErrorCodes.Operation.NotFound,
                    $"Step '{nameOrId}' not found.",
                    Target: nameOrId));
                return ExitCodes.NotFoundError;
            }

            // Build update request
            var request = new StepUpdateRequest(
                Mode: normalizedMode,
                Stage: normalizedStage,
                Rank: rank,
                FilteringAttributes: filteringAttributes,
                Description: description);

            // Check if any changes were specified
            if (request.Mode == null && request.Stage == null && request.Rank == null &&
                request.FilteringAttributes == null && request.Description == null)
            {
                writer.WriteError(new StructuredError(
                    ErrorCodes.Validation.InvalidArguments,
                    "No changes specified. Use --mode, --stage, --rank, --filtering-attributes, or --description.",
                    Target: nameOrId));
                return ExitCodes.InvalidArguments;
            }

            await registrationService.UpdateStepAsync(existing.Id, request, cancellationToken);

            // Build list of changes
            var changes = new List<string>();
            if (normalizedMode != null) changes.Add($"mode -> {normalizedMode}");
            if (normalizedStage != null) changes.Add($"stage -> {normalizedStage}");
            if (rank != null) changes.Add($"rank -> {rank}");
            if (filteringAttributes != null) changes.Add($"filteringAttributes -> {filteringAttributes}");
            if (description != null) changes.Add($"description -> {description}");

            var result = new UpdateResult
            {
                Type = "step",
                Name = existing.Name,
                Id = existing.Id,
                Changes = changes
            };

            if (globalOptions.IsJsonMode)
            {
                writer.WriteSuccess(result);
            }
            else
            {
                Console.Error.WriteLine($"[check] Step updated: {existing.Name}");
                Console.Error.WriteLine($"  Changes: {string.Join(", ", changes)}");
            }

            return ExitCodes.Success;
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("is managed"))
        {
            writer.WriteError(new StructuredError(
                ErrorCodes.Operation.NotSupported,
                ex.Message,
                Target: nameOrId));
            return ExitCodes.Failure;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: "updating step", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    #endregion

    #region Image Subcommand

    private static Command CreateImageSubcommand()
    {
        var nameOrIdArgument = new Argument<string>("name-or-id")
        {
            Description = "Image name or GUID"
        };

        var attributesOption = new Option<string?>("--attributes")
        {
            Description = "Comma-separated list of attributes to include in the image"
        };

        var nameOption = new Option<string?>("--name")
        {
            Description = "New name for the image"
        };

        var command = new Command("image", "Update image attributes")
        {
            nameOrIdArgument,
            attributesOption,
            nameOption,
            PluginsCommandGroup.ProfileOption,
            PluginsCommandGroup.EnvironmentOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var nameOrId = parseResult.GetValue(nameOrIdArgument)!;
            var attributes = parseResult.GetValue(attributesOption);
            var newName = parseResult.GetValue(nameOption);
            var profile = parseResult.GetValue(PluginsCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(PluginsCommandGroup.EnvironmentOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            return await ExecuteImageUpdateAsync(
                nameOrId, attributes, newName,
                profile, environment, globalOptions, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteImageUpdateAsync(
        string nameOrId,
        string? attributes,
        string? newName,
        string? profile,
        string? environment,
        GlobalOptionValues globalOptions,
        CancellationToken cancellationToken)
    {
        var writer = ServiceFactory.CreateOutputWriter(globalOptions);

        try
        {
            await using var serviceProvider = await ProfileServiceFactory.CreateFromProfilesAsync(
                profile,
                environment,
                globalOptions.Verbose,
                globalOptions.Debug,
                ProfileServiceFactory.DefaultDeviceCodeCallback,
                cancellationToken);

            var registrationService = serviceProvider.GetRequiredService<IPluginRegistrationService>();

            if (!globalOptions.IsJsonMode)
            {
                var connectionInfo = serviceProvider.GetRequiredService<ResolvedConnectionInfo>();
                ConsoleHeader.WriteConnectedAs(connectionInfo);
                Console.Error.WriteLine();
            }

            // Check if image exists
            var existing = await registrationService.GetImageByNameOrIdAsync(nameOrId, cancellationToken);
            if (existing == null)
            {
                writer.WriteError(new StructuredError(
                    ErrorCodes.Operation.NotFound,
                    $"Image '{nameOrId}' not found.",
                    Target: nameOrId));
                return ExitCodes.NotFoundError;
            }

            // Build update request
            var request = new ImageUpdateRequest(
                Attributes: attributes,
                Name: newName);

            // Check if any changes were specified
            if (request.Attributes == null && request.Name == null)
            {
                writer.WriteError(new StructuredError(
                    ErrorCodes.Validation.InvalidArguments,
                    "No changes specified. Use --attributes or --name.",
                    Target: nameOrId));
                return ExitCodes.InvalidArguments;
            }

            await registrationService.UpdateImageAsync(existing.Id, request, cancellationToken);

            // Build list of changes
            var changes = new List<string>();
            if (attributes != null) changes.Add($"attributes -> {attributes}");
            if (newName != null) changes.Add($"name -> {newName}");

            var result = new UpdateResult
            {
                Type = "image",
                Name = existing.Name,
                Id = existing.Id,
                Changes = changes
            };

            if (globalOptions.IsJsonMode)
            {
                writer.WriteSuccess(result);
            }
            else
            {
                Console.Error.WriteLine($"[check] Image updated: {existing.Name}");
                Console.Error.WriteLine($"  Changes: {string.Join(", ", changes)}");
            }

            return ExitCodes.Success;
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("is managed"))
        {
            writer.WriteError(new StructuredError(
                ErrorCodes.Operation.NotSupported,
                ex.Message,
                Target: nameOrId));
            return ExitCodes.Failure;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: "updating image", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    #endregion

    #region Helpers

    private static string? NormalizeMode(string? mode)
    {
        if (mode == null) return null;

        return mode.ToLowerInvariant() switch
        {
            "sync" or "synchronous" => "Synchronous",
            "async" or "asynchronous" => "Asynchronous",
            _ => mode
        };
    }

    private static string? NormalizeStage(string? stage)
    {
        if (stage == null) return null;

        return stage.ToLowerInvariant() switch
        {
            "prevalidation" or "pre-validation" => "PreValidation",
            "preoperation" or "pre-operation" or "pre" => "PreOperation",
            "postoperation" or "post-operation" or "post" => "PostOperation",
            _ => stage
        };
    }

    #endregion

    #region Result Models

    private sealed class UpdateResult
    {
        [JsonPropertyName("type")]
        public string Type { get; init; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; init; } = string.Empty;

        [JsonPropertyName("id")]
        public Guid Id { get; init; }

        [JsonPropertyName("changes")]
        public List<string> Changes { get; init; } = [];
    }

    #endregion
}
