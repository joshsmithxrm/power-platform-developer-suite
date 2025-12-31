using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Plugins.Models;
using PPDS.Cli.Plugins.Registration;
using PPDS.Dataverse.Pooling;

namespace PPDS.Cli.Commands.Plugins;

/// <summary>
/// Deploy plugin registrations to a Dataverse environment.
/// </summary>
public static class DeployCommand
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

        var cleanOption = new Option<bool>("--clean")
        {
            Description = "Also remove orphaned registrations not in config",
            DefaultValueFactory = _ => false
        };

        var whatIfOption = new Option<bool>("--what-if")
        {
            Description = "Preview changes without applying",
            DefaultValueFactory = _ => false
        };

        var command = new Command("deploy", "Deploy plugin registrations to environment")
        {
            configOption,
            PluginsCommandGroup.ProfileOption,
            PluginsCommandGroup.EnvironmentOption,
            PluginsCommandGroup.SolutionOption,
            cleanOption,
            whatIfOption,
            PluginsCommandGroup.JsonOption
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var config = parseResult.GetValue(configOption)!;
            var profile = parseResult.GetValue(PluginsCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(PluginsCommandGroup.EnvironmentOption);
            var solution = parseResult.GetValue(PluginsCommandGroup.SolutionOption);
            var clean = parseResult.GetValue(cleanOption);
            var whatIf = parseResult.GetValue(whatIfOption);
            var json = parseResult.GetValue(PluginsCommandGroup.JsonOption);

            return await ExecuteAsync(config, profile, environment, solution, clean, whatIf, json, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        FileInfo configFile,
        string? profile,
        string? environment,
        string? solutionOverride,
        bool clean,
        bool whatIf,
        bool json,
        CancellationToken cancellationToken)
    {
        try
        {
            // Load configuration
            var configJson = await File.ReadAllTextAsync(configFile.FullName, cancellationToken);
            var config = JsonSerializer.Deserialize<PluginRegistrationConfig>(configJson, JsonReadOptions);

            if (config?.Assemblies == null || config.Assemblies.Count == 0)
            {
                Console.Error.WriteLine("No assemblies found in configuration file.");
                return ExitCodes.Failure;
            }

            // Connect to Dataverse
            await using var serviceProvider = await ProfileServiceFactory.CreateFromProfilesAsync(
                profile,
                environment,
                verbose: false,
                debug: false,
                ProfileServiceFactory.DefaultDeviceCodeCallback,
                cancellationToken);

            var pool = serviceProvider.GetRequiredService<IDataverseConnectionPool>();
            await using var client = await pool.GetClientAsync(cancellationToken: cancellationToken);
            var registrationService = new PluginRegistrationService(client);

            if (!json)
            {
                var connectionInfo = serviceProvider.GetRequiredService<ResolvedConnectionInfo>();
                ConsoleHeader.WriteConnectedAs(connectionInfo);
                Console.WriteLine();

                if (whatIf)
                {
                    Console.WriteLine("[What-If Mode] No changes will be applied.");
                    Console.WriteLine();
                }
            }

            var configDir = configFile.DirectoryName ?? ".";
            var results = new List<DeploymentResult>();

            foreach (var assemblyConfig in config.Assemblies)
            {
                var result = await DeployAssemblyAsync(
                    registrationService,
                    assemblyConfig,
                    configDir,
                    solutionOverride,
                    clean,
                    whatIf,
                    json,
                    cancellationToken);

                results.Add(result);
            }

            if (json)
            {
                Console.WriteLine(JsonSerializer.Serialize(results, JsonWriteOptions));
            }
            else
            {
                Console.WriteLine();
                var totalCreated = results.Sum(r => r.StepsCreated + r.ImagesCreated);
                var totalUpdated = results.Sum(r => r.StepsUpdated + r.ImagesUpdated);
                var totalDeleted = results.Sum(r => r.StepsDeleted + r.ImagesDeleted);

                Console.WriteLine($"Deployment complete: {totalCreated} created, {totalUpdated} updated, {totalDeleted} deleted");
            }

            return results.Any(r => !r.Success) ? ExitCodes.Failure : ExitCodes.Success;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error deploying plugins: {ex.Message}");
            return ExitCodes.Failure;
        }
    }

    private static async Task<DeploymentResult> DeployAssemblyAsync(
        PluginRegistrationService service,
        PluginAssemblyConfig assemblyConfig,
        string configDir,
        string? solutionOverride,
        bool clean,
        bool whatIf,
        bool json,
        CancellationToken cancellationToken)
    {
        var result = new DeploymentResult
        {
            AssemblyName = assemblyConfig.Name,
            Success = true
        };

        var solution = solutionOverride ?? assemblyConfig.Solution;

        try
        {
            if (!json)
                Console.WriteLine($"Deploying assembly: {assemblyConfig.Name}");

            // Resolve assembly path
            var assemblyPath = ResolveAssemblyPath(assemblyConfig, configDir);
            if (assemblyPath == null || !File.Exists(assemblyPath))
            {
                throw new FileNotFoundException($"Assembly file not found: {assemblyConfig.Path ?? assemblyConfig.PackagePath}");
            }

            // Read assembly bytes
            byte[] assemblyBytes;
            if (assemblyConfig.Type == "Nuget")
            {
                // For NuGet packages, we need to extract the DLL
                assemblyBytes = ExtractDllFromNupkg(assemblyPath, assemblyConfig.Name);
            }
            else
            {
                assemblyBytes = await File.ReadAllBytesAsync(assemblyPath, cancellationToken);
            }

            // Upsert assembly
            Guid assemblyId;
            if (whatIf)
            {
                var existing = await service.GetAssemblyByNameAsync(assemblyConfig.Name);
                assemblyId = existing?.Id ?? Guid.NewGuid();
                if (!json)
                    Console.WriteLine($"  [What-If] Would {(existing == null ? "create" : "update")} assembly");
            }
            else
            {
                assemblyId = await service.UpsertAssemblyAsync(assemblyConfig.Name, assemblyBytes, solution);
                if (!json)
                    Console.WriteLine($"  Assembly registered: {assemblyId}");
            }

            // Track existing steps for orphan detection - use dictionary for O(1) lookup during cleanup
            var existingStepsMap = new Dictionary<string, PluginStepInfo>();
            var configuredStepNames = new HashSet<string>();

            // Get existing types and steps
            var existingTypes = await service.ListTypesForAssemblyAsync(assemblyId);
            var existingTypeMap = existingTypes.ToDictionary(t => t.TypeName, t => t);

            foreach (var existingType in existingTypes)
            {
                var steps = await service.ListStepsForTypeAsync(existingType.Id);
                foreach (var step in steps)
                {
                    existingStepsMap[step.Name] = step;
                }
            }

            // Deploy each type
            foreach (var typeConfig in assemblyConfig.Types)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Upsert plugin type
                Guid typeId;
                if (whatIf)
                {
                    typeId = existingTypeMap.TryGetValue(typeConfig.TypeName, out var existing)
                        ? existing.Id
                        : Guid.NewGuid();
                    if (!json)
                        Console.WriteLine($"  [What-If] Would register type: {typeConfig.TypeName}");
                }
                else
                {
                    typeId = await service.UpsertPluginTypeAsync(assemblyId, typeConfig.TypeName, solution);
                    if (!json)
                        Console.WriteLine($"  Type registered: {typeConfig.TypeName}");
                }

                // Deploy each step
                foreach (var stepConfig in typeConfig.Steps)
                {
                    var stepName = stepConfig.Name ?? $"{typeConfig.TypeName}: {stepConfig.Message} of {stepConfig.Entity}";
                    configuredStepNames.Add(stepName);

                    // Lookup message and filter
                    var messageId = await service.GetSdkMessageIdAsync(stepConfig.Message);
                    if (messageId == null)
                    {
                        if (!json)
                            Console.WriteLine($"    [Skip] Unknown message: {stepConfig.Message}");
                        continue;
                    }

                    var filterId = await service.GetSdkMessageFilterIdAsync(
                        messageId.Value,
                        stepConfig.Entity,
                        stepConfig.SecondaryEntity);

                    var isNew = !existingStepsMap.ContainsKey(stepName);

                    Guid stepId;
                    if (whatIf)
                    {
                        stepId = Guid.NewGuid();
                        if (!json)
                            Console.WriteLine($"    [What-If] Would {(isNew ? "create" : "update")} step: {stepName}");

                        if (isNew) result.StepsCreated++;
                        else result.StepsUpdated++;
                    }
                    else
                    {
                        stepId = await service.UpsertStepAsync(typeId, stepConfig, messageId.Value, filterId, solution);
                        if (!json)
                            Console.WriteLine($"    Step {(isNew ? "created" : "updated")}: {stepName}");

                        if (isNew) result.StepsCreated++;
                        else result.StepsUpdated++;
                    }

                    // Deploy images (skip query in what-if mode or for new steps since stepId doesn't exist)
                    var existingImages = whatIf || isNew ? [] : await service.ListImagesForStepAsync(stepId);
                    var existingImageNames = existingImages.Select(i => i.Name).ToHashSet();

                    foreach (var imageConfig in stepConfig.Images)
                    {
                        var imageIsNew = !existingImageNames.Contains(imageConfig.Name);

                        if (whatIf)
                        {
                            if (!json)
                                Console.WriteLine($"      [What-If] Would {(imageIsNew ? "create" : "update")} image: {imageConfig.Name}");

                            if (imageIsNew) result.ImagesCreated++;
                            else result.ImagesUpdated++;
                        }
                        else
                        {
                            await service.UpsertImageAsync(stepId, imageConfig);
                            if (!json)
                                Console.WriteLine($"      Image {(imageIsNew ? "created" : "updated")}: {imageConfig.Name}");

                            if (imageIsNew) result.ImagesCreated++;
                            else result.ImagesUpdated++;
                        }
                    }
                }
            }

            // Handle orphan cleanup if requested
            if (clean)
            {
                var orphanedStepNames = existingStepsMap.Keys.Except(configuredStepNames).ToList();

                if (orphanedStepNames.Count > 0)
                {
                    if (!json)
                        Console.WriteLine($"  Cleaning {orphanedStepNames.Count} orphaned step(s)...");

                    foreach (var orphanName in orphanedStepNames)
                    {
                        // Use dictionary lookup instead of re-querying
                        if (existingStepsMap.TryGetValue(orphanName, out var orphanStep))
                        {
                            if (whatIf)
                            {
                                if (!json)
                                    Console.WriteLine($"    [What-If] Would delete step: {orphanName}");
                                result.StepsDeleted++;
                            }
                            else
                            {
                                await service.DeleteStepAsync(orphanStep.Id);
                                if (!json)
                                    Console.WriteLine($"    Deleted step: {orphanName}");
                                result.StepsDeleted++;
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Error = ex.Message;

            if (!json)
                Console.Error.WriteLine($"  Error: {ex.Message}");
        }

        return result;
    }

    private static string? ResolveAssemblyPath(PluginAssemblyConfig config, string configDir)
    {
        if (config.Type == "Nuget" && !string.IsNullOrEmpty(config.PackagePath))
        {
            return Path.GetFullPath(Path.Combine(configDir, config.PackagePath));
        }

        if (!string.IsNullOrEmpty(config.Path))
        {
            return Path.GetFullPath(Path.Combine(configDir, config.Path));
        }

        return null;
    }

    private static byte[] ExtractDllFromNupkg(string nupkgPath, string assemblyName)
    {
        using var archive = System.IO.Compression.ZipFile.OpenRead(nupkgPath);

        // Look for the DLL in lib/net462 (preferred) or any lib folder
        var possiblePaths = new[]
        {
            $"lib/net462/{assemblyName}.dll",
            $"lib/net48/{assemblyName}.dll",
            $"lib/netstandard2.0/{assemblyName}.dll"
        };

        foreach (var path in possiblePaths)
        {
            var entry = archive.GetEntry(path) ?? archive.GetEntry(path.Replace('/', '\\'));
            if (entry != null)
            {
                using var stream = entry.Open();
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                return ms.ToArray();
            }
        }

        // Fallback: find any DLL matching the assembly name
        var matchingEntry = archive.Entries
            .FirstOrDefault(e => e.FullName.EndsWith($"{assemblyName}.dll", StringComparison.OrdinalIgnoreCase));

        if (matchingEntry != null)
        {
            using var stream = matchingEntry.Open();
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            return ms.ToArray();
        }

        throw new InvalidOperationException($"Could not find {assemblyName}.dll in NuGet package");
    }

    #region Result Models

    private sealed class DeploymentResult
    {
        [JsonPropertyName("assemblyName")]
        public string AssemblyName { get; set; } = string.Empty;

        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("error")]
        public string? Error { get; set; }

        [JsonPropertyName("stepsCreated")]
        public int StepsCreated { get; set; }

        [JsonPropertyName("stepsUpdated")]
        public int StepsUpdated { get; set; }

        [JsonPropertyName("stepsDeleted")]
        public int StepsDeleted { get; set; }

        [JsonPropertyName("imagesCreated")]
        public int ImagesCreated { get; set; }

        [JsonPropertyName("imagesUpdated")]
        public int ImagesUpdated { get; set; }

        [JsonPropertyName("imagesDeleted")]
        public int ImagesDeleted { get; set; }
    }

    #endregion
}
