using System.CommandLine;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
            PluginsCommandGroup.OutputFormatOption
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var config = parseResult.GetValue(configOption)!;
            var profile = parseResult.GetValue(PluginsCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(PluginsCommandGroup.EnvironmentOption);
            var solution = parseResult.GetValue(PluginsCommandGroup.SolutionOption);
            var clean = parseResult.GetValue(cleanOption);
            var whatIf = parseResult.GetValue(whatIfOption);
            var outputFormat = parseResult.GetValue(PluginsCommandGroup.OutputFormatOption);

            return await ExecuteAsync(config, profile, environment, solution, clean, whatIf, outputFormat, cancellationToken);
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
        OutputFormat outputFormat,
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

            // Validate configuration
            config.Validate();

            // Connect to Dataverse
            await using var serviceProvider = await ProfileServiceFactory.CreateFromProfilesAsync(
                profile,
                environment,
                verbose: false,
                debug: false,
                ProfileServiceFactory.DefaultDeviceCodeCallback,
                cancellationToken);

            var pool = serviceProvider.GetRequiredService<IDataverseConnectionPool>();
            var logger = serviceProvider.GetRequiredService<ILogger<PluginRegistrationService>>();
            await using var client = await pool.GetClientAsync(cancellationToken: cancellationToken);
            var registrationService = new PluginRegistrationService(client, logger);

            if (outputFormat != OutputFormat.Json)
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
                    outputFormat,
                    cancellationToken);

                results.Add(result);
            }

            if (outputFormat == OutputFormat.Json)
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
        OutputFormat outputFormat,
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
            if (outputFormat != OutputFormat.Json)
                Console.WriteLine($"Deploying assembly: {assemblyConfig.Name}");

            // Resolve assembly path
            var assemblyPath = ResolveAssemblyPath(assemblyConfig, configDir);
            if (assemblyPath == null || !File.Exists(assemblyPath))
            {
                throw new FileNotFoundException($"Assembly file not found: {assemblyConfig.Path ?? assemblyConfig.PackagePath}");
            }

            // Deploy assembly or package based on type
            Guid assemblyId;
            if (assemblyConfig.Type == "Nuget")
            {
                // Extract package ID from .nuspec inside the nupkg - this is what Dataverse uses as uniquename
                var packageName = GetPackageIdFromNupkg(assemblyPath);

                // For NuGet packages, upload the entire .nupkg to pluginpackage entity
                var packageBytes = await File.ReadAllBytesAsync(assemblyPath, cancellationToken);

                Guid packageId;
                if (whatIf)
                {
                    var existingPkg = await service.GetPackageByNameAsync(packageName);
                    packageId = existingPkg?.Id ?? Guid.NewGuid();
                    if (outputFormat != OutputFormat.Json)
                        Console.WriteLine($"  [What-If] Would {(existingPkg == null ? "create" : "update")} package: {packageName}");
                }
                else
                {
                    packageId = await service.UpsertPackageAsync(packageName, packageBytes, solution);
                    if (outputFormat != OutputFormat.Json)
                        Console.WriteLine($"  Package registered: {packageId}");
                }

                // Get the assembly ID from the package (Dataverse creates it automatically)
                // Use assemblyConfig.Name here since that's the assembly name inside the package
                var pkgAssemblyId = await service.GetAssemblyIdForPackageAsync(packageId, assemblyConfig.Name);
                if (pkgAssemblyId == null && !whatIf)
                {
                    throw new InvalidOperationException($"Could not find assembly '{assemblyConfig.Name}' in package after deployment");
                }
                // In what-if mode for new packages, the assembly won't exist yet - use a placeholder ID
                assemblyId = pkgAssemblyId ?? Guid.NewGuid();
            }
            else
            {
                // For classic assemblies, upload the DLL directly
                var assemblyBytes = await File.ReadAllBytesAsync(assemblyPath, cancellationToken);

                if (whatIf)
                {
                    var existing = await service.GetAssemblyByNameAsync(assemblyConfig.Name);
                    assemblyId = existing?.Id ?? Guid.NewGuid();
                    if (outputFormat != OutputFormat.Json)
                        Console.WriteLine($"  [What-If] Would {(existing == null ? "create" : "update")} assembly");
                }
                else
                {
                    assemblyId = await service.UpsertAssemblyAsync(assemblyConfig.Name, assemblyBytes, solution);
                    if (outputFormat != OutputFormat.Json)
                        Console.WriteLine($"  Assembly registered: {assemblyId}");
                }
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
                    if (outputFormat != OutputFormat.Json)
                        Console.WriteLine($"  [What-If] Would register type: {typeConfig.TypeName}");
                }
                else
                {
                    typeId = await service.UpsertPluginTypeAsync(assemblyId, typeConfig.TypeName, solution);
                    if (outputFormat != OutputFormat.Json)
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
                        if (outputFormat != OutputFormat.Json)
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
                        if (outputFormat != OutputFormat.Json)
                            Console.WriteLine($"    [What-If] Would {(isNew ? "create" : "update")} step: {stepName}");

                        if (isNew) result.StepsCreated++;
                        else result.StepsUpdated++;
                    }
                    else
                    {
                        stepId = await service.UpsertStepAsync(typeId, stepConfig, messageId.Value, filterId, solution);
                        if (outputFormat != OutputFormat.Json)
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
                            if (outputFormat != OutputFormat.Json)
                                Console.WriteLine($"      [What-If] Would {(imageIsNew ? "create" : "update")} image: {imageConfig.Name}");

                            if (imageIsNew) result.ImagesCreated++;
                            else result.ImagesUpdated++;
                        }
                        else
                        {
                            await service.UpsertImageAsync(stepId, imageConfig);
                            if (outputFormat != OutputFormat.Json)
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
                    if (outputFormat != OutputFormat.Json)
                        Console.WriteLine($"  Cleaning {orphanedStepNames.Count} orphaned step(s)...");

                    foreach (var orphanName in orphanedStepNames)
                    {
                        // Use dictionary lookup instead of re-querying
                        if (existingStepsMap.TryGetValue(orphanName, out var orphanStep))
                        {
                            if (whatIf)
                            {
                                if (outputFormat != OutputFormat.Json)
                                    Console.WriteLine($"    [What-If] Would delete step: {orphanName}");
                                result.StepsDeleted++;
                            }
                            else
                            {
                                await service.DeleteStepAsync(orphanStep.Id);
                                if (outputFormat != OutputFormat.Json)
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

            if (outputFormat != OutputFormat.Json)
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

    /// <summary>
    /// Extracts the package ID from the .nuspec file inside a .nupkg.
    /// This is the authoritative source - Dataverse uses this as the uniquename.
    /// </summary>
    private static string GetPackageIdFromNupkg(string nupkgPath)
    {
        using var archive = ZipFile.OpenRead(nupkgPath);

        // Find the .nuspec file (there's exactly one at the root level)
        var nuspecEntry = archive.Entries.FirstOrDefault(e =>
            e.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase) &&
            !e.FullName.Contains('/'));

        if (nuspecEntry == null)
        {
            throw new InvalidOperationException($"No .nuspec file found in package: {nupkgPath}");
        }

        using var stream = nuspecEntry.Open();
        var doc = XDocument.Load(stream);

        // Nuspec namespace
        var ns = doc.Root?.GetDefaultNamespace() ?? XNamespace.None;
        var id = doc.Root?.Element(ns + "metadata")?.Element(ns + "id")?.Value;

        if (string.IsNullOrEmpty(id))
        {
            throw new InvalidOperationException($"No <id> element found in nuspec: {nupkgPath}");
        }

        return id;
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
