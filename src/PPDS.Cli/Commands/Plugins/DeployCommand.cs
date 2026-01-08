using System.CommandLine;
using System.IO.Compression;
using System.Text.Json;
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

        var dryRunOption = new Option<bool>("--dry-run")
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
            dryRunOption
        };

        // Add global options including output format
        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var config = parseResult.GetValue(configOption)!;
            var profile = parseResult.GetValue(PluginsCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(PluginsCommandGroup.EnvironmentOption);
            var solution = parseResult.GetValue(PluginsCommandGroup.SolutionOption);
            var clean = parseResult.GetValue(cleanOption);
            var dryRun = parseResult.GetValue(dryRunOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            return await ExecuteAsync(config, profile, environment, solution, clean, dryRun, globalOptions, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        FileInfo configFile,
        string? profile,
        string? environment,
        string? solutionOverride,
        bool clean,
        bool dryRun,
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

            var registrationService = serviceProvider.GetRequiredService<IPluginRegistrationService>();

            if (!globalOptions.IsJsonMode)
            {
                var connectionInfo = serviceProvider.GetRequiredService<ResolvedConnectionInfo>();
                ConsoleHeader.WriteConnectedAs(connectionInfo);
                Console.Error.WriteLine();

                if (dryRun)
                {
                    Console.Error.WriteLine("[Dry-Run Mode] No changes will be applied.");
                    Console.Error.WriteLine();
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
                    dryRun,
                    globalOptions,
                    cancellationToken);

                results.Add(result);
            }

            if (globalOptions.IsJsonMode)
            {
                writer.WriteSuccess(results);
            }
            else
            {
                Console.Error.WriteLine();
                var totalCreated = results.Sum(r => r.StepsCreated + r.ImagesCreated);
                var totalUpdated = results.Sum(r => r.StepsUpdated + r.ImagesUpdated);
                var totalDeleted = results.Sum(r => r.StepsDeleted + r.ImagesDeleted);

                Console.Error.WriteLine($"Deployment complete: {totalCreated} created, {totalUpdated} updated, {totalDeleted} deleted");
            }

            return results.Any(r => !r.Success) ? ExitCodes.Failure : ExitCodes.Success;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: "deploying plugins", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    private static async Task<DeploymentResult> DeployAssemblyAsync(
        IPluginRegistrationService service,
        PluginAssemblyConfig assemblyConfig,
        string configDir,
        string? solutionOverride,
        bool clean,
        bool dryRun,
        GlobalOptionValues globalOptions,
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
            if (!globalOptions.IsJsonMode)
                Console.Error.WriteLine($"Deploying assembly: {assemblyConfig.Name}");

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
                if (dryRun)
                {
                    var existingPkg = await service.GetPackageByNameAsync(packageName);
                    packageId = existingPkg?.Id ?? Guid.NewGuid();
                    if (!globalOptions.IsJsonMode)
                        Console.Error.WriteLine($"  [Dry-Run] Would {(existingPkg == null ? "create" : "update")} package: {packageName}");
                }
                else
                {
                    packageId = await service.UpsertPackageAsync(packageName, packageBytes, solution);
                    if (!globalOptions.IsJsonMode)
                        Console.Error.WriteLine($"  Package registered: {packageId}");
                }

                // Get the assembly ID from the package (Dataverse creates it automatically)
                // Use assemblyConfig.Name here since that's the assembly name inside the package
                var pkgAssemblyId = await service.GetAssemblyIdForPackageAsync(packageId, assemblyConfig.Name);
                if (pkgAssemblyId == null && !dryRun)
                {
                    throw new InvalidOperationException($"Could not find assembly '{assemblyConfig.Name}' in package after deployment");
                }
                // In dry-run mode for new packages, the assembly won't exist yet - use a placeholder ID
                assemblyId = pkgAssemblyId ?? Guid.NewGuid();
            }
            else
            {
                // For classic assemblies, upload the DLL directly
                var assemblyBytes = await File.ReadAllBytesAsync(assemblyPath, cancellationToken);

                if (dryRun)
                {
                    var existing = await service.GetAssemblyByNameAsync(assemblyConfig.Name);
                    assemblyId = existing?.Id ?? Guid.NewGuid();
                    if (!globalOptions.IsJsonMode)
                        Console.Error.WriteLine($"  [Dry-Run] Would {(existing == null ? "create" : "update")} assembly");
                }
                else
                {
                    assemblyId = await service.UpsertAssemblyAsync(assemblyConfig.Name, assemblyBytes, solution);
                    if (!globalOptions.IsJsonMode)
                        Console.Error.WriteLine($"  Assembly registered: {assemblyId}");
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
                if (dryRun)
                {
                    typeId = existingTypeMap.TryGetValue(typeConfig.TypeName, out var existing)
                        ? existing.Id
                        : Guid.NewGuid();
                    if (!globalOptions.IsJsonMode)
                        Console.Error.WriteLine($"  [Dry-Run] Would register type: {typeConfig.TypeName}");
                }
                else
                {
                    typeId = await service.UpsertPluginTypeAsync(assemblyId, typeConfig.TypeName, solution);
                    if (!globalOptions.IsJsonMode)
                        Console.Error.WriteLine($"  Type registered: {typeConfig.TypeName}");
                }

                // Deploy each step
                foreach (var stepConfig in typeConfig.Steps)
                {
                    // Resolve auto-generated name if not specified
                    stepConfig.Name ??= $"{typeConfig.TypeName}: {stepConfig.Message} of {stepConfig.Entity}";
                    var stepName = stepConfig.Name;
                    configuredStepNames.Add(stepName);

                    // Lookup message and filter
                    var messageId = await service.GetSdkMessageIdAsync(stepConfig.Message);
                    if (messageId == null)
                    {
                        if (!globalOptions.IsJsonMode)
                            Console.Error.WriteLine($"    [Skip] Unknown message: {stepConfig.Message}");
                        continue;
                    }

                    var filterId = await service.GetSdkMessageFilterIdAsync(
                        messageId.Value,
                        stepConfig.Entity,
                        stepConfig.SecondaryEntity);

                    var isNew = !existingStepsMap.ContainsKey(stepName);

                    Guid stepId;
                    if (dryRun)
                    {
                        stepId = Guid.NewGuid();
                        if (!globalOptions.IsJsonMode)
                            Console.Error.WriteLine($"    [Dry-Run] Would {(isNew ? "create" : "update")} step: {stepName}");

                        if (isNew) result.StepsCreated++;
                        else result.StepsUpdated++;
                    }
                    else
                    {
                        stepId = await service.UpsertStepAsync(typeId, stepConfig, messageId.Value, filterId, solution);
                        if (!globalOptions.IsJsonMode)
                            Console.Error.WriteLine($"    Step {(isNew ? "created" : "updated")}: {stepName}");

                        if (isNew) result.StepsCreated++;
                        else result.StepsUpdated++;
                    }

                    // Deploy images (skip query in dry-run mode or for new steps since stepId doesn't exist)
                    var existingImages = dryRun || isNew ? [] : await service.ListImagesForStepAsync(stepId);
                    var existingImageNames = existingImages.Select(i => i.Name).ToHashSet();

                    foreach (var imageConfig in stepConfig.Images)
                    {
                        var imageIsNew = !existingImageNames.Contains(imageConfig.Name);

                        if (dryRun)
                        {
                            if (!globalOptions.IsJsonMode)
                                Console.Error.WriteLine($"      [Dry-Run] Would {(imageIsNew ? "create" : "update")} image: {imageConfig.Name}");

                            if (imageIsNew) result.ImagesCreated++;
                            else result.ImagesUpdated++;
                        }
                        else
                        {
                            await service.UpsertImageAsync(stepId, imageConfig, stepConfig.Message);
                            if (!globalOptions.IsJsonMode)
                                Console.Error.WriteLine($"      Image {(imageIsNew ? "created" : "updated")}: {imageConfig.Name}");

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
                    if (!globalOptions.IsJsonMode)
                        Console.Error.WriteLine($"  Cleaning {orphanedStepNames.Count} orphaned step(s)...");

                    foreach (var orphanName in orphanedStepNames)
                    {
                        // Use dictionary lookup instead of re-querying
                        if (existingStepsMap.TryGetValue(orphanName, out var orphanStep))
                        {
                            if (dryRun)
                            {
                                if (!globalOptions.IsJsonMode)
                                    Console.Error.WriteLine($"    [Dry-Run] Would delete step: {orphanName}");
                                result.StepsDeleted++;
                            }
                            else
                            {
                                await service.DeleteStepAsync(orphanStep.Id);
                                if (!globalOptions.IsJsonMode)
                                    Console.Error.WriteLine($"    Deleted step: {orphanName}");
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

            if (!globalOptions.IsJsonMode)
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
