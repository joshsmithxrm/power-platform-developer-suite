using System.CommandLine;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Cli.Plugins.Registration;

namespace PPDS.Cli.Commands.Plugins;

/// <summary>
/// Get details for a specific plugin entity (assembly, package, type, step, or image).
/// </summary>
public static class GetCommand
{
    private static readonly string[] ValidTypes = ["assembly", "package", "type", "step", "image"];

    public static Command Create()
    {
        var typeArgument = new Argument<string>("type")
        {
            Description = "Entity type: assembly, package, type, step, image"
        };

        var nameOrIdArgument = new Argument<string>("name-or-id")
        {
            Description = "Entity name or GUID"
        };

        var command = new Command("get", "Get details for a specific plugin entity")
        {
            typeArgument,
            nameOrIdArgument,
            PluginsCommandGroup.ProfileOption,
            PluginsCommandGroup.EnvironmentOption
        };

        GlobalOptions.AddToCommand(command);

        // Add validator for type argument
        command.Validators.Add(result =>
        {
            var typeValue = result.GetValue(typeArgument);
            if (!string.IsNullOrEmpty(typeValue) && !ValidTypes.Contains(typeValue.ToLowerInvariant()))
            {
                result.AddError($"Invalid type '{typeValue}'. Must be one of: {string.Join(", ", ValidTypes)}");
            }
        });

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var type = parseResult.GetValue(typeArgument)!;
            var nameOrId = parseResult.GetValue(nameOrIdArgument)!;
            var profile = parseResult.GetValue(PluginsCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(PluginsCommandGroup.EnvironmentOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            return await ExecuteAsync(type, nameOrId, profile, environment, globalOptions, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        string type,
        string nameOrId,
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
            var connectionInfo = serviceProvider.GetRequiredService<ResolvedConnectionInfo>();

            if (!globalOptions.IsJsonMode)
            {
                ConsoleHeader.WriteConnectedAs(connectionInfo);
                Console.Error.WriteLine();
            }

            return type.ToLowerInvariant() switch
            {
                "assembly" => await GetAssemblyAsync(nameOrId, registrationService, globalOptions, writer, cancellationToken),
                "package" => await GetPackageAsync(nameOrId, registrationService, globalOptions, writer, cancellationToken),
                "type" => await GetPluginTypeAsync(nameOrId, registrationService, globalOptions, writer, cancellationToken),
                "step" => await GetStepAsync(nameOrId, registrationService, globalOptions, writer, cancellationToken),
                "image" => await GetImageAsync(nameOrId, registrationService, globalOptions, writer, cancellationToken),
                _ => throw new InvalidOperationException($"Unknown type: {type}")
            };
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: $"getting plugin {type} '{nameOrId}'", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    private static async Task<int> GetAssemblyAsync(
        string nameOrId,
        IPluginRegistrationService registrationService,
        GlobalOptionValues globalOptions,
        IOutputWriter writer,
        CancellationToken cancellationToken)
    {
        PluginAssemblyInfo? assembly;

        assembly = Guid.TryParse(nameOrId, out var id)
            ? await registrationService.GetAssemblyByIdAsync(id, cancellationToken)
            : await registrationService.GetAssemblyByNameAsync(nameOrId, cancellationToken);

        if (assembly == null)
        {
            var error = new StructuredError(
                ErrorCodes.Operation.NotFound,
                $"Plugin assembly '{nameOrId}' not found.",
                null,
                nameOrId);
            writer.WriteError(error);
            return ExitCodes.NotFoundError;
        }

        // Get counts of related entities (parallelized to avoid N+1 queries)
        var types = await registrationService.ListTypesForAssemblyAsync(assembly.Id, cancellationToken);
        var stepCountTasks = types.Select(async type =>
        {
            var steps = await registrationService.ListStepsForTypeAsync(type.Id, null, cancellationToken);
            return steps.Count;
        });
        var stepCounts = await Task.WhenAll(stepCountTasks);
        var stepCount = stepCounts.Sum();

        if (globalOptions.IsJsonMode)
        {
            var output = new AssemblyDetailOutput
            {
                Id = assembly.Id,
                Name = assembly.Name,
                Version = assembly.Version,
                IsolationMode = MapIsolationMode(assembly.IsolationMode),
                SourceType = MapSourceType(assembly.SourceType),
                IsManaged = assembly.IsManaged,
                PackageId = assembly.PackageId,
                PublicKeyToken = assembly.PublicKeyToken,
                CreatedOn = assembly.CreatedOn,
                ModifiedOn = assembly.ModifiedOn,
                PluginTypeCount = types.Count,
                ActiveStepCount = stepCount
            };
            writer.WriteSuccess(output);
        }
        else
        {
            WritePropertyTable(new Dictionary<string, string?>
            {
                ["Name"] = assembly.Name,
                ["ID"] = assembly.Id.ToString(),
                ["Version"] = assembly.Version ?? "-",
                ["Isolation Mode"] = MapIsolationMode(assembly.IsolationMode),
                ["Source Type"] = MapSourceType(assembly.SourceType),
                ["Is Managed"] = assembly.IsManaged ? "Yes" : "No",
                ["Package"] = assembly.PackageId?.ToString() ?? "(none)",
                ["Public Key Token"] = assembly.PublicKeyToken ?? "-",
                ["Created"] = assembly.CreatedOn?.ToString("g") ?? "-",
                ["Modified"] = assembly.ModifiedOn?.ToString("g") ?? "-",
                ["Plugin Types"] = types.Count.ToString(),
                ["Active Steps"] = stepCount.ToString()
            });
        }

        return ExitCodes.Success;
    }

    private static async Task<int> GetPackageAsync(
        string nameOrId,
        IPluginRegistrationService registrationService,
        GlobalOptionValues globalOptions,
        IOutputWriter writer,
        CancellationToken cancellationToken)
    {
        PluginPackageInfo? package;

        package = Guid.TryParse(nameOrId, out var id)
            ? await registrationService.GetPackageByIdAsync(id, cancellationToken)
            : await registrationService.GetPackageByNameAsync(nameOrId, cancellationToken);

        if (package == null)
        {
            var error = new StructuredError(
                ErrorCodes.Operation.NotFound,
                $"Plugin package '{nameOrId}' not found.",
                null,
                nameOrId);
            writer.WriteError(error);
            return ExitCodes.NotFoundError;
        }

        // Get counts of related entities (parallelized to avoid N+1 queries)
        var assemblies = await registrationService.ListAssembliesForPackageAsync(package.Id, cancellationToken);
        var listTypesTasks = assemblies.Select(assembly => registrationService.ListTypesForAssemblyAsync(assembly.Id, cancellationToken));
        var typesPerAssembly = await Task.WhenAll(listTypesTasks);
        var allTypes = typesPerAssembly.SelectMany(t => t).ToList();

        var listStepsTasks = allTypes.Select(type => registrationService.ListStepsForTypeAsync(type.Id, null, cancellationToken));
        var stepsPerType = await Task.WhenAll(listStepsTasks);

        var typeCount = allTypes.Count;
        var stepCount = stepsPerType.Sum(s => s.Count);

        if (globalOptions.IsJsonMode)
        {
            var output = new PackageDetailOutput
            {
                Id = package.Id,
                Name = package.Name,
                UniqueName = package.UniqueName,
                Version = package.Version,
                IsManaged = package.IsManaged,
                CreatedOn = package.CreatedOn,
                ModifiedOn = package.ModifiedOn,
                AssemblyCount = assemblies.Count,
                PluginTypeCount = typeCount,
                ActiveStepCount = stepCount
            };
            writer.WriteSuccess(output);
        }
        else
        {
            WritePropertyTable(new Dictionary<string, string?>
            {
                ["Name"] = package.Name,
                ["Unique Name"] = package.UniqueName ?? package.Name,
                ["ID"] = package.Id.ToString(),
                ["Version"] = package.Version ?? "-",
                ["Is Managed"] = package.IsManaged ? "Yes" : "No",
                ["Created"] = package.CreatedOn?.ToString("g") ?? "-",
                ["Modified"] = package.ModifiedOn?.ToString("g") ?? "-",
                ["Assemblies"] = assemblies.Count.ToString(),
                ["Plugin Types"] = typeCount.ToString(),
                ["Active Steps"] = stepCount.ToString()
            });
        }

        return ExitCodes.Success;
    }

    private static async Task<int> GetPluginTypeAsync(
        string nameOrId,
        IPluginRegistrationService registrationService,
        GlobalOptionValues globalOptions,
        IOutputWriter writer,
        CancellationToken cancellationToken)
    {
        var pluginType = await registrationService.GetPluginTypeByNameOrIdAsync(nameOrId, cancellationToken);

        if (pluginType == null)
        {
            var error = new StructuredError(
                ErrorCodes.Operation.NotFound,
                $"Plugin type '{nameOrId}' not found.",
                null,
                nameOrId);
            writer.WriteError(error);
            return ExitCodes.NotFoundError;
        }

        // Get step count
        var steps = await registrationService.ListStepsForTypeAsync(pluginType.Id, null, cancellationToken);

        if (globalOptions.IsJsonMode)
        {
            var output = new PluginTypeDetailOutput
            {
                Id = pluginType.Id,
                TypeName = pluginType.TypeName,
                FriendlyName = pluginType.FriendlyName,
                AssemblyId = pluginType.AssemblyId,
                AssemblyName = pluginType.AssemblyName,
                CreatedOn = pluginType.CreatedOn,
                ModifiedOn = pluginType.ModifiedOn,
                StepCount = steps.Count
            };
            writer.WriteSuccess(output);
        }
        else
        {
            WritePropertyTable(new Dictionary<string, string?>
            {
                ["Type Name"] = pluginType.TypeName,
                ["ID"] = pluginType.Id.ToString(),
                ["Friendly Name"] = pluginType.FriendlyName ?? "-",
                ["Assembly"] = pluginType.AssemblyName ?? "-",
                ["Assembly ID"] = pluginType.AssemblyId?.ToString() ?? "-",
                ["Created"] = pluginType.CreatedOn?.ToString("g") ?? "-",
                ["Modified"] = pluginType.ModifiedOn?.ToString("g") ?? "-",
                ["Steps"] = steps.Count.ToString()
            });
        }

        return ExitCodes.Success;
    }

    private static async Task<int> GetStepAsync(
        string nameOrId,
        IPluginRegistrationService registrationService,
        GlobalOptionValues globalOptions,
        IOutputWriter writer,
        CancellationToken cancellationToken)
    {
        var step = await registrationService.GetStepByNameOrIdAsync(nameOrId, cancellationToken);

        if (step == null)
        {
            var error = new StructuredError(
                ErrorCodes.Operation.NotFound,
                $"Plugin step '{nameOrId}' not found.",
                null,
                nameOrId);
            writer.WriteError(error);
            return ExitCodes.NotFoundError;
        }

        // Get image count
        var images = await registrationService.ListImagesForStepAsync(step.Id, cancellationToken);

        if (globalOptions.IsJsonMode)
        {
            var output = new StepDetailOutput
            {
                Id = step.Id,
                Name = step.Name,
                Message = step.Message,
                PrimaryEntity = step.PrimaryEntity,
                SecondaryEntity = step.SecondaryEntity,
                Stage = step.Stage,
                Mode = step.Mode,
                ExecutionOrder = step.ExecutionOrder,
                IsEnabled = step.IsEnabled,
                Deployment = step.Deployment,
                FilteringAttributes = step.FilteringAttributes,
                Description = step.Description,
                UnsecureConfiguration = step.Configuration,
                RunAsUser = step.ImpersonatingUserName,
                AsyncAutoDelete = step.AsyncAutoDelete,
                PluginTypeId = step.PluginTypeId,
                PluginTypeName = step.PluginTypeName,
                CreatedOn = step.CreatedOn,
                ModifiedOn = step.ModifiedOn,
                ImageCount = images.Count
            };
            writer.WriteSuccess(output);
        }
        else
        {
            var properties = new Dictionary<string, string?>
            {
                ["Name"] = step.Name,
                ["ID"] = step.Id.ToString(),
                ["Message"] = step.Message,
                ["Entity"] = step.PrimaryEntity
            };

            if (!string.IsNullOrEmpty(step.SecondaryEntity))
            {
                properties["Secondary Entity"] = step.SecondaryEntity;
            }

            properties["Stage"] = step.Stage;
            properties["Mode"] = step.Mode;
            properties["Execution Order"] = step.ExecutionOrder.ToString();
            properties["Enabled"] = step.IsEnabled ? "Yes" : "No";
            properties["Deployment"] = step.Deployment;

            if (!string.IsNullOrEmpty(step.FilteringAttributes))
            {
                properties["Filtering Attributes"] = step.FilteringAttributes;
            }

            if (!string.IsNullOrEmpty(step.Description))
            {
                properties["Description"] = step.Description;
            }

            if (!string.IsNullOrEmpty(step.ImpersonatingUserName))
            {
                properties["Run As User"] = step.ImpersonatingUserName;
            }

            if (step.AsyncAutoDelete && step.Mode == "Asynchronous")
            {
                properties["Auto-Delete"] = "Yes";
            }

            properties["Plugin Type"] = step.PluginTypeName ?? "-";
            properties["Created"] = step.CreatedOn?.ToString("g") ?? "-";
            properties["Modified"] = step.ModifiedOn?.ToString("g") ?? "-";
            properties["Images"] = images.Count.ToString();

            WritePropertyTable(properties);
        }

        return ExitCodes.Success;
    }

    private static async Task<int> GetImageAsync(
        string nameOrId,
        IPluginRegistrationService registrationService,
        GlobalOptionValues globalOptions,
        IOutputWriter writer,
        CancellationToken cancellationToken)
    {
        var image = await registrationService.GetImageByNameOrIdAsync(nameOrId, cancellationToken);

        if (image == null)
        {
            var error = new StructuredError(
                ErrorCodes.Operation.NotFound,
                $"Plugin image '{nameOrId}' not found.",
                null,
                nameOrId);
            writer.WriteError(error);
            return ExitCodes.NotFoundError;
        }

        if (globalOptions.IsJsonMode)
        {
            var output = new ImageDetailOutput
            {
                Id = image.Id,
                Name = image.Name,
                EntityAlias = image.EntityAlias ?? image.Name,
                ImageType = image.ImageType,
                Attributes = image.Attributes,
                MessagePropertyName = image.MessagePropertyName,
                StepId = image.StepId,
                StepName = image.StepName,
                CreatedOn = image.CreatedOn,
                ModifiedOn = image.ModifiedOn
            };
            writer.WriteSuccess(output);
        }
        else
        {
            WritePropertyTable(new Dictionary<string, string?>
            {
                ["Name"] = image.Name,
                ["ID"] = image.Id.ToString(),
                ["Entity Alias"] = image.EntityAlias ?? image.Name,
                ["Image Type"] = image.ImageType,
                ["Attributes"] = string.IsNullOrEmpty(image.Attributes) ? "All" : image.Attributes,
                ["Message Property"] = image.MessagePropertyName ?? "-",
                ["Step"] = image.StepName ?? "-",
                ["Step ID"] = image.StepId?.ToString() ?? "-",
                ["Created"] = image.CreatedOn?.ToString("g") ?? "-",
                ["Modified"] = image.ModifiedOn?.ToString("g") ?? "-"
            });
        }

        return ExitCodes.Success;
    }

    private static void WritePropertyTable(Dictionary<string, string?> properties)
    {
        if (properties.Count == 0) return;

        var maxKeyLength = properties.Keys.Max(k => k.Length);

        foreach (var kvp in properties)
        {
            var key = kvp.Key.PadRight(maxKeyLength);
            Console.Error.WriteLine($"  {key}  {kvp.Value}");
        }
    }

    private static string MapIsolationMode(int value) => value switch
    {
        1 => "None",
        2 => "Sandbox",
        3 => "External",
        _ => value.ToString()
    };

    private static string MapSourceType(int value) => value switch
    {
        0 => "Database",
        1 => "Disk",
        2 => "Normal",
        3 => "AzureWebApp",
        4 => "FileStore",
        _ => value.ToString()
    };

    #region Output Models

    private sealed class AssemblyDetailOutput
    {
        [JsonPropertyName("id")]
        public Guid Id { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("version")]
        public string? Version { get; set; }

        [JsonPropertyName("isolationMode")]
        public string IsolationMode { get; set; } = string.Empty;

        [JsonPropertyName("sourceType")]
        public string SourceType { get; set; } = string.Empty;

        [JsonPropertyName("isManaged")]
        public bool IsManaged { get; set; }

        [JsonPropertyName("packageId")]
        public Guid? PackageId { get; set; }

        [JsonPropertyName("publicKeyToken")]
        public string? PublicKeyToken { get; set; }

        [JsonPropertyName("createdOn")]
        public DateTime? CreatedOn { get; set; }

        [JsonPropertyName("modifiedOn")]
        public DateTime? ModifiedOn { get; set; }

        [JsonPropertyName("pluginTypeCount")]
        public int PluginTypeCount { get; set; }

        [JsonPropertyName("activeStepCount")]
        public int ActiveStepCount { get; set; }
    }

    private sealed class PackageDetailOutput
    {
        [JsonPropertyName("id")]
        public Guid Id { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("uniqueName")]
        public string? UniqueName { get; set; }

        [JsonPropertyName("version")]
        public string? Version { get; set; }

        [JsonPropertyName("isManaged")]
        public bool IsManaged { get; set; }

        [JsonPropertyName("createdOn")]
        public DateTime? CreatedOn { get; set; }

        [JsonPropertyName("modifiedOn")]
        public DateTime? ModifiedOn { get; set; }

        [JsonPropertyName("assemblyCount")]
        public int AssemblyCount { get; set; }

        [JsonPropertyName("pluginTypeCount")]
        public int PluginTypeCount { get; set; }

        [JsonPropertyName("activeStepCount")]
        public int ActiveStepCount { get; set; }
    }

    private sealed class PluginTypeDetailOutput
    {
        [JsonPropertyName("id")]
        public Guid Id { get; set; }

        [JsonPropertyName("typeName")]
        public string TypeName { get; set; } = string.Empty;

        [JsonPropertyName("friendlyName")]
        public string? FriendlyName { get; set; }

        [JsonPropertyName("assemblyId")]
        public Guid? AssemblyId { get; set; }

        [JsonPropertyName("assemblyName")]
        public string? AssemblyName { get; set; }

        [JsonPropertyName("createdOn")]
        public DateTime? CreatedOn { get; set; }

        [JsonPropertyName("modifiedOn")]
        public DateTime? ModifiedOn { get; set; }

        [JsonPropertyName("stepCount")]
        public int StepCount { get; set; }
    }

    private sealed class StepDetailOutput
    {
        [JsonPropertyName("id")]
        public Guid Id { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;

        [JsonPropertyName("primaryEntity")]
        public string PrimaryEntity { get; set; } = string.Empty;

        [JsonPropertyName("secondaryEntity")]
        public string? SecondaryEntity { get; set; }

        [JsonPropertyName("stage")]
        public string Stage { get; set; } = string.Empty;

        [JsonPropertyName("mode")]
        public string Mode { get; set; } = string.Empty;

        [JsonPropertyName("executionOrder")]
        public int ExecutionOrder { get; set; }

        [JsonPropertyName("isEnabled")]
        public bool IsEnabled { get; set; }

        [JsonPropertyName("deployment")]
        public string Deployment { get; set; } = string.Empty;

        [JsonPropertyName("filteringAttributes")]
        public string? FilteringAttributes { get; set; }

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("unsecureConfiguration")]
        public string? UnsecureConfiguration { get; set; }

        [JsonPropertyName("runAsUser")]
        public string? RunAsUser { get; set; }

        [JsonPropertyName("asyncAutoDelete")]
        public bool AsyncAutoDelete { get; set; }

        [JsonPropertyName("pluginTypeId")]
        public Guid? PluginTypeId { get; set; }

        [JsonPropertyName("pluginTypeName")]
        public string? PluginTypeName { get; set; }

        [JsonPropertyName("createdOn")]
        public DateTime? CreatedOn { get; set; }

        [JsonPropertyName("modifiedOn")]
        public DateTime? ModifiedOn { get; set; }

        [JsonPropertyName("imageCount")]
        public int ImageCount { get; set; }
    }

    private sealed class ImageDetailOutput
    {
        [JsonPropertyName("id")]
        public Guid Id { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("entityAlias")]
        public string EntityAlias { get; set; } = string.Empty;

        [JsonPropertyName("imageType")]
        public string ImageType { get; set; } = string.Empty;

        [JsonPropertyName("attributes")]
        public string? Attributes { get; set; }

        [JsonPropertyName("messagePropertyName")]
        public string? MessagePropertyName { get; set; }

        [JsonPropertyName("stepId")]
        public Guid? StepId { get; set; }

        [JsonPropertyName("stepName")]
        public string? StepName { get; set; }

        [JsonPropertyName("createdOn")]
        public DateTime? CreatedOn { get; set; }

        [JsonPropertyName("modifiedOn")]
        public DateTime? ModifiedOn { get; set; }
    }

    #endregion
}
