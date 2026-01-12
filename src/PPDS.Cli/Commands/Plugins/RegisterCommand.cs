using System.CommandLine;
using System.IO.Compression;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Cli.Plugins.Models;
using PPDS.Cli.Plugins.Registration;

namespace PPDS.Cli.Commands.Plugins;

/// <summary>
/// Register plugin components imperatively without a configuration file.
/// </summary>
public static class RegisterCommand
{
    /// <summary>
    /// Creates the 'register' command with all subcommands.
    /// </summary>
    public static Command Create()
    {
        var command = new Command("register", "Register plugin components: assembly, package, type, step, image");

        command.Subcommands.Add(CreateAssemblyCommand());
        command.Subcommands.Add(CreatePackageCommand());
        command.Subcommands.Add(CreateTypeCommand());
        command.Subcommands.Add(CreateStepCommand());
        command.Subcommands.Add(CreateImageCommand());

        return command;
    }

    #region Assembly Subcommand

    private static Command CreateAssemblyCommand()
    {
        var pathArgument = new Argument<FileInfo>("path")
        {
            Description = "Path to the plugin assembly DLL file"
        }.AcceptExistingOnly();

        var command = new Command("assembly", "Register a plugin assembly (DLL)")
        {
            pathArgument,
            PluginsCommandGroup.ProfileOption,
            PluginsCommandGroup.EnvironmentOption,
            PluginsCommandGroup.SolutionOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var path = parseResult.GetValue(pathArgument)!;
            var profile = parseResult.GetValue(PluginsCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(PluginsCommandGroup.EnvironmentOption);
            var solution = parseResult.GetValue(PluginsCommandGroup.SolutionOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            return await ExecuteAssemblyAsync(path, profile, environment, solution, globalOptions, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteAssemblyAsync(
        FileInfo assemblyFile,
        string? profile,
        string? environment,
        string? solution,
        GlobalOptionValues globalOptions,
        CancellationToken cancellationToken)
    {
        var writer = ServiceFactory.CreateOutputWriter(globalOptions);

        try
        {
            var assemblyName = Path.GetFileNameWithoutExtension(assemblyFile.Name);
            var assemblyBytes = await File.ReadAllBytesAsync(assemblyFile.FullName, cancellationToken);

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
                Console.Error.WriteLine($"Registering assembly: {assemblyName}");
            }

            var assemblyId = await registrationService.UpsertAssemblyAsync(assemblyName, assemblyBytes, solution, cancellationToken);

            // Count discovered types
            var types = await registrationService.ListTypesForAssemblyAsync(assemblyId, cancellationToken);

            var result = new RegisterAssemblyResult
            {
                Success = true,
                Operation = "register-assembly",
                Id = assemblyId,
                Name = assemblyName,
                PluginTypesDiscovered = types.Count,
                Solution = solution
            };

            if (globalOptions.IsJsonMode)
            {
                writer.WriteSuccess(result);
            }
            else
            {
                Console.Error.WriteLine($"  Assembly registered: {assemblyId}");
                Console.Error.WriteLine($"  Plugin types discovered: {types.Count}");
                if (!string.IsNullOrEmpty(solution))
                    Console.Error.WriteLine($"  Solution: {solution}");
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: "registering assembly", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    #endregion

    #region Package Subcommand

    private static Command CreatePackageCommand()
    {
        var pathArgument = new Argument<FileInfo>("path")
        {
            Description = "Path to the plugin package (.nupkg) file"
        }.AcceptExistingOnly();

        var command = new Command("package", "Register a plugin package (NuGet)")
        {
            pathArgument,
            PluginsCommandGroup.ProfileOption,
            PluginsCommandGroup.EnvironmentOption,
            PluginsCommandGroup.SolutionOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var path = parseResult.GetValue(pathArgument)!;
            var profile = parseResult.GetValue(PluginsCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(PluginsCommandGroup.EnvironmentOption);
            var solution = parseResult.GetValue(PluginsCommandGroup.SolutionOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            return await ExecutePackageAsync(path, profile, environment, solution, globalOptions, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecutePackageAsync(
        FileInfo packageFile,
        string? profile,
        string? environment,
        string? solution,
        GlobalOptionValues globalOptions,
        CancellationToken cancellationToken)
    {
        var writer = ServiceFactory.CreateOutputWriter(globalOptions);

        try
        {
            var packageName = GetPackageIdFromNupkg(packageFile.FullName);
            var packageBytes = await File.ReadAllBytesAsync(packageFile.FullName, cancellationToken);

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
                Console.Error.WriteLine($"Registering package: {packageName}");
            }

            var packageId = await registrationService.UpsertPackageAsync(packageName, packageBytes, solution, cancellationToken);

            // Count discovered types
            var types = await registrationService.ListTypesForPackageAsync(packageId, cancellationToken);

            var result = new RegisterPackageResult
            {
                Success = true,
                Operation = "register-package",
                Id = packageId,
                Name = packageName,
                PluginTypesDiscovered = types.Count,
                Solution = solution
            };

            if (globalOptions.IsJsonMode)
            {
                writer.WriteSuccess(result);
            }
            else
            {
                Console.Error.WriteLine($"  Package registered: {packageId}");
                Console.Error.WriteLine($"  Plugin types discovered: {types.Count}");
                if (!string.IsNullOrEmpty(solution))
                    Console.Error.WriteLine($"  Solution: {solution}");
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: "registering package", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    #endregion

    #region Type Subcommand

    private static Command CreateTypeCommand()
    {
        var assemblyArgument = new Argument<string>("assembly")
        {
            Description = "Assembly name containing the plugin type"
        };

        var typenameOption = new Option<string>("--typename")
        {
            Description = "Fully qualified type name (namespace.classname)",
            Required = true
        };

        var command = new Command("type", "Register a plugin type within an assembly")
        {
            assemblyArgument,
            typenameOption,
            PluginsCommandGroup.ProfileOption,
            PluginsCommandGroup.EnvironmentOption,
            PluginsCommandGroup.SolutionOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var assembly = parseResult.GetValue(assemblyArgument)!;
            var typename = parseResult.GetValue(typenameOption)!;
            var profile = parseResult.GetValue(PluginsCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(PluginsCommandGroup.EnvironmentOption);
            var solution = parseResult.GetValue(PluginsCommandGroup.SolutionOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            return await ExecuteTypeAsync(assembly, typename, profile, environment, solution, globalOptions, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteTypeAsync(
        string assemblyName,
        string typeName,
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
                Console.Error.WriteLine($"Registering type: {typeName}");
            }

            // Find the assembly
            var assembly = await registrationService.GetAssemblyByNameAsync(assemblyName, cancellationToken);
            if (assembly == null)
            {
                writer.WriteError(new StructuredError(
                    ErrorCodes.Operation.NotFound,
                    $"Assembly not found: {assemblyName}",
                    Target: assemblyName));
                return ExitCodes.NotFoundError;
            }

            var typeId = await registrationService.UpsertPluginTypeAsync(assembly.Id, typeName, solution, cancellationToken);

            var result = new RegisterTypeResult
            {
                Success = true,
                Operation = "register-type",
                Id = typeId,
                TypeName = typeName,
                AssemblyName = assemblyName,
                Solution = solution
            };

            if (globalOptions.IsJsonMode)
            {
                writer.WriteSuccess(result);
            }
            else
            {
                Console.Error.WriteLine($"  Type registered: {typeId}");
                Console.Error.WriteLine($"  Assembly: {assemblyName}");
                if (!string.IsNullOrEmpty(solution))
                    Console.Error.WriteLine($"  Solution: {solution}");
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: "registering type", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    #endregion

    #region Step Subcommand

    private static Command CreateStepCommand()
    {
        var typeArgument = new Argument<string>("type")
        {
            Description = "Plugin type name (fully qualified)"
        };

        var messageOption = new Option<string>("--message")
        {
            Description = "SDK message name (Create, Update, Delete, etc.)",
            Required = true
        };

        var entityOption = new Option<string>("--entity")
        {
            Description = "Primary entity logical name",
            Required = true
        };

        var stageOption = new Option<string>("--stage")
        {
            Description = "Pipeline stage: PreValidation, PreOperation, or PostOperation",
            Required = true
        };

        var modeOption = new Option<string>("--mode")
        {
            Description = "Execution mode: Sync or Async",
            DefaultValueFactory = _ => "Sync"
        };

        var rankOption = new Option<int>("--rank")
        {
            Description = "Execution order (1-999999)",
            DefaultValueFactory = _ => 1
        };

        var filteringAttributesOption = new Option<string?>("--filtering-attributes")
        {
            Description = "Comma-separated list of attributes that trigger this step (for Update message)"
        };

        var nameOption = new Option<string?>("--name")
        {
            Description = "Step display name (auto-generated if not specified)"
        };

        var command = new Command("step", "Register a processing step for a plugin type")
        {
            typeArgument,
            messageOption,
            entityOption,
            stageOption,
            modeOption,
            rankOption,
            filteringAttributesOption,
            nameOption,
            PluginsCommandGroup.ProfileOption,
            PluginsCommandGroup.EnvironmentOption,
            PluginsCommandGroup.SolutionOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var typeName = parseResult.GetValue(typeArgument)!;
            var message = parseResult.GetValue(messageOption)!;
            var entity = parseResult.GetValue(entityOption)!;
            var stage = parseResult.GetValue(stageOption)!;
            var mode = parseResult.GetValue(modeOption)!;
            var rank = parseResult.GetValue(rankOption);
            var filteringAttributes = parseResult.GetValue(filteringAttributesOption);
            var name = parseResult.GetValue(nameOption);
            var profile = parseResult.GetValue(PluginsCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(PluginsCommandGroup.EnvironmentOption);
            var solution = parseResult.GetValue(PluginsCommandGroup.SolutionOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            return await ExecuteStepAsync(typeName, message, entity, stage, mode, rank, filteringAttributes, name, profile, environment, solution, globalOptions, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteStepAsync(
        string typeName,
        string message,
        string entity,
        string stage,
        string mode,
        int rank,
        string? filteringAttributes,
        string? name,
        string? profile,
        string? environment,
        string? solution,
        GlobalOptionValues globalOptions,
        CancellationToken cancellationToken)
    {
        var writer = ServiceFactory.CreateOutputWriter(globalOptions);

        try
        {
            // Normalize mode
            var normalizedMode = mode.ToLowerInvariant() switch
            {
                "sync" => "Synchronous",
                "async" => "Asynchronous",
                _ => mode
            };

            // Auto-generate step name if not specified
            var stepName = name ?? $"{typeName}: {message} of {entity}";

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
                Console.Error.WriteLine($"Registering step: {stepName}");
            }

            // Find the plugin type
            var pluginType = await registrationService.GetPluginTypeByNameAsync(typeName, cancellationToken);
            if (pluginType == null)
            {
                writer.WriteError(new StructuredError(
                    ErrorCodes.Operation.NotFound,
                    $"Plugin type not found: {typeName}",
                    Target: typeName));
                return ExitCodes.NotFoundError;
            }

            // Get message ID
            var messageId = await registrationService.GetSdkMessageIdAsync(message, cancellationToken);
            if (messageId == null)
            {
                writer.WriteError(new StructuredError(
                    ErrorCodes.Operation.NotFound,
                    $"SDK message not found: {message}",
                    Target: message));
                return ExitCodes.NotFoundError;
            }

            // Get message filter ID
            var filterId = await registrationService.GetSdkMessageFilterIdAsync(messageId.Value, entity, null, cancellationToken);

            // Build step config
            var stepConfig = new PluginStepConfig
            {
                Name = stepName,
                Message = message,
                Entity = entity,
                Stage = stage,
                Mode = normalizedMode,
                ExecutionOrder = rank,
                FilteringAttributes = filteringAttributes
            };

            var stepId = await registrationService.UpsertStepAsync(pluginType.Id, stepConfig, messageId.Value, filterId, solution, cancellationToken);

            var result = new RegisterStepResult
            {
                Success = true,
                Operation = "register-step",
                Id = stepId,
                Name = stepName,
                TypeName = typeName,
                Message = message,
                Entity = entity,
                Stage = stage,
                Mode = normalizedMode,
                Rank = rank,
                Solution = solution
            };

            if (globalOptions.IsJsonMode)
            {
                writer.WriteSuccess(result);
            }
            else
            {
                Console.Error.WriteLine($"  Step registered: {stepId}");
                Console.Error.WriteLine($"  Message: {message}, Entity: {entity}, Stage: {stage}");
                if (!string.IsNullOrEmpty(solution))
                    Console.Error.WriteLine($"  Solution: {solution}");
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: "registering step", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    #endregion

    #region Image Subcommand

    private static Command CreateImageCommand()
    {
        var stepArgument = new Argument<string>("step")
        {
            Description = "Step name to attach the image to"
        };

        var nameOption = new Option<string>("--name")
        {
            Description = "Image name",
            Required = true
        };

        var typeOption = new Option<string>("--type")
        {
            Description = "Image type: pre, post, or both",
            Required = true
        };

        var attributesOption = new Option<string?>("--attributes")
        {
            Description = "Comma-separated list of attributes to include (all if not specified)"
        };

        var command = new Command("image", "Register an image for a processing step")
        {
            stepArgument,
            nameOption,
            typeOption,
            attributesOption,
            PluginsCommandGroup.ProfileOption,
            PluginsCommandGroup.EnvironmentOption,
            PluginsCommandGroup.SolutionOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var stepName = parseResult.GetValue(stepArgument)!;
            var name = parseResult.GetValue(nameOption)!;
            var type = parseResult.GetValue(typeOption)!;
            var attributes = parseResult.GetValue(attributesOption);
            var profile = parseResult.GetValue(PluginsCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(PluginsCommandGroup.EnvironmentOption);
            var solution = parseResult.GetValue(PluginsCommandGroup.SolutionOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            return await ExecuteImageAsync(stepName, name, type, attributes, profile, environment, solution, globalOptions, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteImageAsync(
        string stepName,
        string imageName,
        string imageType,
        string? attributes,
        string? profile,
        string? environment,
        string? solution,
        GlobalOptionValues globalOptions,
        CancellationToken cancellationToken)
    {
        var writer = ServiceFactory.CreateOutputWriter(globalOptions);

        try
        {
            // Normalize image type
            var normalizedImageType = imageType.ToLowerInvariant() switch
            {
                "pre" => "PreImage",
                "post" => "PostImage",
                "both" => "Both",
                _ => imageType
            };

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
                Console.Error.WriteLine($"Registering image: {imageName} on step: {stepName}");
            }

            // Find the step
            var step = await registrationService.GetStepByNameAsync(stepName, cancellationToken);
            if (step == null)
            {
                writer.WriteError(new StructuredError(
                    ErrorCodes.Operation.NotFound,
                    $"Processing step not found: {stepName}",
                    Target: stepName));
                return ExitCodes.NotFoundError;
            }

            // Build image config
            var imageConfig = new PluginImageConfig
            {
                Name = imageName,
                ImageType = normalizedImageType,
                Attributes = attributes
            };

            var imageId = await registrationService.UpsertImageAsync(step.Id, imageConfig, step.Message, cancellationToken);

            var result = new RegisterImageResult
            {
                Success = true,
                Operation = "register-image",
                Id = imageId,
                Name = imageName,
                ImageType = normalizedImageType,
                StepName = stepName,
                Attributes = attributes
            };

            if (globalOptions.IsJsonMode)
            {
                writer.WriteSuccess(result);
            }
            else
            {
                Console.Error.WriteLine($"  Image registered: {imageId}");
                Console.Error.WriteLine($"  Type: {normalizedImageType}");
                if (!string.IsNullOrEmpty(attributes))
                    Console.Error.WriteLine($"  Attributes: {attributes}");
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: "registering image", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Extracts the package ID from the .nuspec file inside a .nupkg.
    /// </summary>
    private static string GetPackageIdFromNupkg(string nupkgPath)
    {
        using var archive = ZipFile.OpenRead(nupkgPath);

        var nuspecEntry = archive.Entries.FirstOrDefault(e =>
            e.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase) &&
            !e.FullName.Contains('/'));

        if (nuspecEntry == null)
        {
            throw new InvalidOperationException($"No .nuspec file found in package: {nupkgPath}");
        }

        using var stream = nuspecEntry.Open();
        var doc = XDocument.Load(stream);

        var ns = doc.Root?.GetDefaultNamespace() ?? XNamespace.None;
        var id = doc.Root?.Element(ns + "metadata")?.Element(ns + "id")?.Value;

        if (string.IsNullOrEmpty(id))
        {
            throw new InvalidOperationException($"No <id> element found in nuspec: {nupkgPath}");
        }

        return id;
    }

    #endregion

    #region Result Models

    private sealed class RegisterAssemblyResult
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("operation")]
        public string Operation { get; set; } = string.Empty;

        [JsonPropertyName("id")]
        public Guid Id { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("pluginTypesDiscovered")]
        public int PluginTypesDiscovered { get; set; }

        [JsonPropertyName("solution")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Solution { get; set; }
    }

    private sealed class RegisterPackageResult
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("operation")]
        public string Operation { get; set; } = string.Empty;

        [JsonPropertyName("id")]
        public Guid Id { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("pluginTypesDiscovered")]
        public int PluginTypesDiscovered { get; set; }

        [JsonPropertyName("solution")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Solution { get; set; }
    }

    private sealed class RegisterTypeResult
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("operation")]
        public string Operation { get; set; } = string.Empty;

        [JsonPropertyName("id")]
        public Guid Id { get; set; }

        [JsonPropertyName("typeName")]
        public string TypeName { get; set; } = string.Empty;

        [JsonPropertyName("assemblyName")]
        public string AssemblyName { get; set; } = string.Empty;

        [JsonPropertyName("solution")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Solution { get; set; }
    }

    private sealed class RegisterStepResult
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("operation")]
        public string Operation { get; set; } = string.Empty;

        [JsonPropertyName("id")]
        public Guid Id { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("typeName")]
        public string TypeName { get; set; } = string.Empty;

        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;

        [JsonPropertyName("entity")]
        public string Entity { get; set; } = string.Empty;

        [JsonPropertyName("stage")]
        public string Stage { get; set; } = string.Empty;

        [JsonPropertyName("mode")]
        public string Mode { get; set; } = string.Empty;

        [JsonPropertyName("rank")]
        public int Rank { get; set; }

        [JsonPropertyName("solution")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Solution { get; set; }
    }

    private sealed class RegisterImageResult
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("operation")]
        public string Operation { get; set; } = string.Empty;

        [JsonPropertyName("id")]
        public Guid Id { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("imageType")]
        public string ImageType { get; set; } = string.Empty;

        [JsonPropertyName("stepName")]
        public string StepName { get; set; } = string.Empty;

        [JsonPropertyName("attributes")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Attributes { get; set; }
    }

    #endregion
}
