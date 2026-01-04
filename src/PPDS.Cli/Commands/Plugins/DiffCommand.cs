using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Cli.Plugins.Models;
using PPDS.Cli.Plugins.Registration;
using PPDS.Dataverse.Pooling;

namespace PPDS.Cli.Commands.Plugins;

/// <summary>
/// Compare plugin configuration against Dataverse environment state.
/// </summary>
public static class DiffCommand
{
    private static readonly JsonSerializerOptions JsonReadOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly JsonSerializerOptions JsonWriteOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static Command Create()
    {
        var configOption = new Option<FileInfo>("--config", "-c")
        {
            Description = "Path to registrations.json",
            Required = true
        }.AcceptExistingOnly();

        var command = new Command("diff", "Compare configuration against environment state")
        {
            configOption,
            PluginsCommandGroup.ProfileOption,
            PluginsCommandGroup.EnvironmentOption,
            PluginsCommandGroup.OutputFormatOption
        };

        // Add global options for verbosity and correlation
        GlobalOptions.AddToCommand(command, includeOutputFormat: false);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var config = parseResult.GetValue(configOption)!;
            var profile = parseResult.GetValue(PluginsCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(PluginsCommandGroup.EnvironmentOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            return await ExecuteAsync(config, profile, environment, globalOptions, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        FileInfo configFile,
        string? profile,
        string? environment,
        GlobalOptionValues globalOptions,
        CancellationToken cancellationToken)
    {
        var writer = ServiceFactory.CreateOutputWriter(globalOptions);

        try
        {
            // Load configuration
            var configJson = await File.ReadAllTextAsync(configFile.FullName, cancellationToken);
            var config = JsonSerializer.Deserialize<PluginRegistrationConfig>(configJson, JsonReadOptions);

            if (config?.Assemblies == null || config.Assemblies.Count == 0)
            {
                writer.WriteError(new StructuredError(
                    ErrorCodes.Validation.InvalidValue,
                    "No assemblies found in configuration file.",
                    Target: configFile.Name));
                return ExitCodes.InvalidArguments;
            }

            // Validate configuration
            config.Validate();

            // Connect to Dataverse
            await using var serviceProvider = await ProfileServiceFactory.CreateFromProfilesAsync(
                profile,
                environment,
                globalOptions.Verbose,
                globalOptions.Debug,
                ProfileServiceFactory.DefaultDeviceCodeCallback,
                cancellationToken);

            var pool = serviceProvider.GetRequiredService<IDataverseConnectionPool>();
            var logger = serviceProvider.GetRequiredService<ILogger<PluginRegistrationService>>();
            var registrationService = new PluginRegistrationService(pool, logger);

            if (!globalOptions.IsJsonMode)
            {
                var connectionInfo = serviceProvider.GetRequiredService<ResolvedConnectionInfo>();
                ConsoleHeader.WriteConnectedAs(connectionInfo);
                Console.Error.WriteLine();
            }

            var allDrifts = new List<AssemblyDrift>();
            var hasDrift = false;

            foreach (var assemblyConfig in config.Assemblies)
            {
                var drift = await ComputeDriftAsync(registrationService, assemblyConfig);
                allDrifts.Add(drift);

                if (drift.HasDrift)
                    hasDrift = true;
            }

            if (globalOptions.IsJsonMode)
            {
                writer.WriteSuccess(allDrifts);
            }
            else
            {
                if (!hasDrift)
                {
                    Console.Error.WriteLine("No drift detected. Environment matches configuration.");
                    return ExitCodes.Success;
                }

                foreach (var drift in allDrifts)
                {
                    if (!drift.HasDrift)
                        continue;

                    Console.Error.WriteLine($"Assembly: {drift.AssemblyName}");

                    if (drift.AssemblyMissing)
                    {
                        Console.Error.WriteLine("  [MISSING] Assembly not registered in environment");
                        continue;
                    }

                    foreach (var missing in drift.MissingSteps)
                    {
                        Console.Error.WriteLine($"  [+] Missing step: {missing.StepName}");
                        Console.Error.WriteLine($"      {missing.Message} on {missing.Entity} ({missing.Stage}, {missing.Mode})");
                    }

                    foreach (var orphan in drift.OrphanedSteps)
                    {
                        Console.Error.WriteLine($"  [-] Orphaned step: {orphan.StepName}");
                        Console.Error.WriteLine($"      {orphan.Message} on {orphan.Entity} ({orphan.Stage}, {orphan.Mode})");
                    }

                    foreach (var modified in drift.ModifiedSteps)
                    {
                        Console.Error.WriteLine($"  [~] Modified step: {modified.StepName}");
                        foreach (var change in modified.Changes)
                        {
                            Console.Error.WriteLine($"      {change.Property}: {change.Expected} -> {change.Actual}");
                        }
                    }

                    foreach (var missingImage in drift.MissingImages)
                    {
                        Console.Error.WriteLine($"  [+] Missing image: {missingImage.ImageName} on step {missingImage.StepName}");
                    }

                    foreach (var orphanImage in drift.OrphanedImages)
                    {
                        Console.Error.WriteLine($"  [-] Orphaned image: {orphanImage.ImageName} on step {orphanImage.StepName}");
                    }

                    foreach (var modifiedImage in drift.ModifiedImages)
                    {
                        Console.Error.WriteLine($"  [~] Modified image: {modifiedImage.ImageName} on step {modifiedImage.StepName}");
                        foreach (var change in modifiedImage.Changes)
                        {
                            Console.Error.WriteLine($"      {change.Property}: {change.Expected} -> {change.Actual}");
                        }
                    }

                    Console.Error.WriteLine();
                }
            }

            return hasDrift ? 1 : ExitCodes.Success; // Return 1 if drift detected (useful for CI)
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: "comparing plugins", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    private static async Task<AssemblyDrift> ComputeDriftAsync(
        PluginRegistrationService service,
        PluginAssemblyConfig config)
    {
        var drift = new AssemblyDrift
        {
            AssemblyName = config.Name
        };

        // Check if assembly exists
        var assembly = await service.GetAssemblyByNameAsync(config.Name);
        if (assembly == null)
        {
            drift.AssemblyMissing = true;
            return drift;
        }

        // Get all types for this assembly
        var existingTypes = await service.ListTypesForAssemblyAsync(assembly.Id);

        // Build a map of all configured steps
        var configuredSteps = new Dictionary<string, (PluginTypeConfig Type, PluginStepConfig Step)>();
        foreach (var typeConfig in config.Types)
        {
            foreach (var stepConfig in typeConfig.Steps)
            {
                var key = stepConfig.Name ?? $"{typeConfig.TypeName}: {stepConfig.Message} of {stepConfig.Entity}";
                configuredSteps[key] = (typeConfig, stepConfig);
            }
        }

        // Get all existing steps
        var existingSteps = new Dictionary<string, (PluginTypeInfo Type, PluginStepInfo Step)>();
        foreach (var existingType in existingTypes)
        {
            var steps = await service.ListStepsForTypeAsync(existingType.Id);
            foreach (var step in steps)
            {
                existingSteps[step.Name] = (existingType, step);
            }
        }

        // Find missing steps (in config, not in environment)
        foreach (var (stepName, (typeConfig, stepConfig)) in configuredSteps)
        {
            if (!existingSteps.ContainsKey(stepName))
            {
                drift.MissingSteps.Add(new StepDrift
                {
                    TypeName = typeConfig.TypeName,
                    StepName = stepName,
                    Message = stepConfig.Message,
                    Entity = stepConfig.Entity,
                    Stage = stepConfig.Stage,
                    Mode = stepConfig.Mode
                });
            }
        }

        // Find orphaned steps (in environment, not in config)
        foreach (var (stepName, (typeInfo, stepInfo)) in existingSteps)
        {
            if (!configuredSteps.ContainsKey(stepName))
            {
                drift.OrphanedSteps.Add(new StepDrift
                {
                    TypeName = typeInfo.TypeName,
                    StepName = stepName,
                    Message = stepInfo.Message,
                    Entity = stepInfo.PrimaryEntity,
                    Stage = stepInfo.Stage,
                    Mode = stepInfo.Mode
                });
            }
        }

        // Find modified steps and check images
        foreach (var (stepName, (typeConfig, stepConfig)) in configuredSteps)
        {
            if (!existingSteps.TryGetValue(stepName, out var existing))
                continue;

            var (_, stepInfo) = existing;

            // Compare step properties
            var changes = new List<PropertyChange>();

            if (!string.Equals(stepConfig.Stage, stepInfo.Stage, StringComparison.OrdinalIgnoreCase))
                changes.Add(new PropertyChange("stage", stepConfig.Stage, stepInfo.Stage));

            if (!string.Equals(stepConfig.Mode, stepInfo.Mode, StringComparison.OrdinalIgnoreCase))
                changes.Add(new PropertyChange("mode", stepConfig.Mode, stepInfo.Mode));

            if (stepConfig.ExecutionOrder != stepInfo.ExecutionOrder)
                changes.Add(new PropertyChange("executionOrder", stepConfig.ExecutionOrder.ToString(), stepInfo.ExecutionOrder.ToString()));

            var configFiltering = NormalizeAttributes(stepConfig.FilteringAttributes);
            var envFiltering = NormalizeAttributes(stepInfo.FilteringAttributes);
            if (!string.Equals(configFiltering, envFiltering, StringComparison.OrdinalIgnoreCase))
                changes.Add(new PropertyChange("filteringAttributes", configFiltering ?? "(none)", envFiltering ?? "(none)"));

            if (changes.Count > 0)
            {
                drift.ModifiedSteps.Add(new ModifiedStepDrift
                {
                    TypeName = typeConfig.TypeName,
                    StepName = stepName,
                    Changes = changes
                });
            }

            // Compare images
            var existingImages = await service.ListImagesForStepAsync(stepInfo.Id);
            var existingImageMap = existingImages.ToDictionary(i => i.Name, i => i);
            var configImageMap = stepConfig.Images.ToDictionary(i => i.Name, i => i);

            // Missing images
            foreach (var imageConfig in stepConfig.Images)
            {
                if (!existingImageMap.ContainsKey(imageConfig.Name))
                {
                    drift.MissingImages.Add(new ImageDrift
                    {
                        StepName = stepName,
                        ImageName = imageConfig.Name
                    });
                }
            }

            // Orphaned images
            foreach (var imageInfo in existingImages)
            {
                if (!configImageMap.ContainsKey(imageInfo.Name))
                {
                    drift.OrphanedImages.Add(new ImageDrift
                    {
                        StepName = stepName,
                        ImageName = imageInfo.Name
                    });
                }
            }

            // Modified images
            foreach (var imageConfig in stepConfig.Images)
            {
                if (!existingImageMap.TryGetValue(imageConfig.Name, out var imageInfo))
                    continue;

                var imageChanges = new List<PropertyChange>();

                if (!string.Equals(imageConfig.ImageType, imageInfo.ImageType, StringComparison.OrdinalIgnoreCase))
                    imageChanges.Add(new PropertyChange("imageType", imageConfig.ImageType, imageInfo.ImageType));

                var configAttrs = NormalizeAttributes(imageConfig.Attributes);
                var envAttrs = NormalizeAttributes(imageInfo.Attributes);
                if (!string.Equals(configAttrs, envAttrs, StringComparison.OrdinalIgnoreCase))
                    imageChanges.Add(new PropertyChange("attributes", configAttrs ?? "(all)", envAttrs ?? "(all)"));

                if (imageChanges.Count > 0)
                {
                    drift.ModifiedImages.Add(new ModifiedImageDrift
                    {
                        StepName = stepName,
                        ImageName = imageConfig.Name,
                        Changes = imageChanges
                    });
                }
            }
        }

        return drift;
    }

    private static string? NormalizeAttributes(string? attributes)
    {
        if (string.IsNullOrWhiteSpace(attributes))
            return null;

        // Sort attributes for consistent comparison
        var sorted = attributes.Split(',')
            .Select(a => a.Trim().ToLowerInvariant())
            .Where(a => !string.IsNullOrEmpty(a))
            .OrderBy(a => a)
            .ToArray();

        return sorted.Length > 0 ? string.Join(",", sorted) : null;
    }

    #region Drift Models

    private sealed class AssemblyDrift
    {
        [JsonPropertyName("assemblyName")]
        public string AssemblyName { get; set; } = string.Empty;

        [JsonPropertyName("assemblyMissing")]
        public bool AssemblyMissing { get; set; }

        [JsonPropertyName("missingSteps")]
        public List<StepDrift> MissingSteps { get; set; } = [];

        [JsonPropertyName("orphanedSteps")]
        public List<StepDrift> OrphanedSteps { get; set; } = [];

        [JsonPropertyName("modifiedSteps")]
        public List<ModifiedStepDrift> ModifiedSteps { get; set; } = [];

        [JsonPropertyName("missingImages")]
        public List<ImageDrift> MissingImages { get; set; } = [];

        [JsonPropertyName("orphanedImages")]
        public List<ImageDrift> OrphanedImages { get; set; } = [];

        [JsonPropertyName("modifiedImages")]
        public List<ModifiedImageDrift> ModifiedImages { get; set; } = [];

        [JsonIgnore]
        public bool HasDrift => AssemblyMissing ||
                                MissingSteps.Count > 0 ||
                                OrphanedSteps.Count > 0 ||
                                ModifiedSteps.Count > 0 ||
                                MissingImages.Count > 0 ||
                                OrphanedImages.Count > 0 ||
                                ModifiedImages.Count > 0;
    }

    private sealed class StepDrift
    {
        [JsonPropertyName("typeName")]
        public string TypeName { get; set; } = string.Empty;

        [JsonPropertyName("stepName")]
        public string StepName { get; set; } = string.Empty;

        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;

        [JsonPropertyName("entity")]
        public string Entity { get; set; } = string.Empty;

        [JsonPropertyName("stage")]
        public string Stage { get; set; } = string.Empty;

        [JsonPropertyName("mode")]
        public string Mode { get; set; } = string.Empty;
    }

    private sealed class ModifiedStepDrift
    {
        [JsonPropertyName("typeName")]
        public string TypeName { get; set; } = string.Empty;

        [JsonPropertyName("stepName")]
        public string StepName { get; set; } = string.Empty;

        [JsonPropertyName("changes")]
        public List<PropertyChange> Changes { get; set; } = [];
    }

    private sealed class ImageDrift
    {
        [JsonPropertyName("stepName")]
        public string StepName { get; set; } = string.Empty;

        [JsonPropertyName("imageName")]
        public string ImageName { get; set; } = string.Empty;
    }

    private sealed class ModifiedImageDrift
    {
        [JsonPropertyName("stepName")]
        public string StepName { get; set; } = string.Empty;

        [JsonPropertyName("imageName")]
        public string ImageName { get; set; } = string.Empty;

        [JsonPropertyName("changes")]
        public List<PropertyChange> Changes { get; set; } = [];
    }

    private sealed class PropertyChange
    {
        [JsonPropertyName("property")]
        public string Property { get; set; }

        [JsonPropertyName("expected")]
        public string Expected { get; set; }

        [JsonPropertyName("actual")]
        public string Actual { get; set; }

        public PropertyChange(string property, string expected, string actual)
        {
            Property = property;
            Expected = expected;
            Actual = actual;
        }
    }

    #endregion
}
