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
using PPDS.Cli.Services;

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

            // Collect custom APIs from both root-level and per-assembly sections
            var allCustomApis = new List<CustomApiConfig>();
            if (config?.CustomApis != null)
                allCustomApis.AddRange(config.CustomApis);
            if (config?.Assemblies != null)
            {
                foreach (var asm in config.Assemblies)
                {
                    if (asm.CustomApis != null)
                        allCustomApis.AddRange(asm.CustomApis);
                }
            }

            if ((config?.Assemblies == null || config.Assemblies.Count == 0) && allCustomApis.Count == 0)
            {
                writer.WriteError(new StructuredError(
                    ErrorCodes.Validation.InvalidValue,
                    "No assemblies found in configuration file.",
                    Target: configFile.Name));
                return ExitCodes.InvalidArguments;
            }

            // Validate configuration
            config!.Validate();

            // Connect to Dataverse
            await using var serviceProvider = await ProfileServiceFactory.CreateFromProfilesAsync(
                profile,
                environment,
                globalOptions.Verbose,
                globalOptions.Debug,
                ProfileServiceFactory.DefaultDeviceCodeCallback,
                cancellationToken);

            var registrationService = serviceProvider.GetRequiredService<IPluginRegistrationService>();
            var customApiService = serviceProvider.GetRequiredService<ICustomApiService>();

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

            if (config.Assemblies != null)
            {
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
            }

            // Deploy custom APIs
            if (allCustomApis.Count > 0)
            {
                await DeployCustomApisAsync(
                    registrationService,
                    customApiService,
                    allCustomApis,
                    dryRun,
                    globalOptions,
                    cancellationToken);
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

    /// <summary>
    /// Deploys a single assembly's types, steps, and images. Steps are matched to the environment by
    /// functional identity (<see cref="PluginStepMatcher"/>) rather than by mutable display name, so an
    /// environment holding two same-named steps is disambiguated instead of aborting, and each write
    /// targets the correct row.
    /// </summary>
    /// <remarks><c>internal</c> for direct unit-test access (InternalsVisibleTo PPDS.Cli.Tests).</remarks>
    internal static async Task<DeploymentResult> DeployAssemblyAsync(
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

            // Get existing types and steps
            var existingTypes = await service.ListTypesForAssemblyAsync(assemblyId);

            // Build a duplicate-aware type lookup. TypeName is typically the fully qualified
            // name, but collisions can happen (e.g., the same class name under different
            // namespaces or zombie types left from historical registrations). We surface the
            // conflict to the user instead of aborting with ArgumentException deep in LINQ.
            var existingTypeMap = new Dictionary<string, PluginTypeInfo>(StringComparer.Ordinal);
            var typeCollisions = new Dictionary<string, List<PluginTypeInfo>>(StringComparer.Ordinal);
            foreach (var typeInfo in existingTypes)
            {
                if (existingTypeMap.TryGetValue(typeInfo.TypeName, out var firstSeen))
                {
                    if (!typeCollisions.TryGetValue(typeInfo.TypeName, out var group))
                    {
                        group = [firstSeen];
                        typeCollisions[typeInfo.TypeName] = group;
                    }
                    group.Add(typeInfo);
                }
                else
                {
                    existingTypeMap[typeInfo.TypeName] = typeInfo;
                }
            }

            if (typeCollisions.Count > 0)
            {
                var details = string.Join("; ", typeCollisions.Select(c =>
                    $"'{c.Key}' appears {c.Value.Count} times (ids: {string.Join(", ", c.Value.Select(t => t.Id))})"));
                throw new PpdsException(
                    ErrorCodes.Operation.Duplicate,
                    $"Assembly '{assemblyConfig.Name}' has duplicate plugin type names in the environment. " +
                    $"Please disambiguate manually before re-deploying. Conflicts: {details}");
            }

            // Gather existing (type, step) pairs and match them to configuration by functional
            // identity rather than by mutable display name. Two same-named steps (e.g., a PreOp/PostOp
            // pair the Plugin Registration Tool auto-named identically) no longer collapse or abort.
            var existingPairs = new List<(PluginTypeInfo Type, PluginStepInfo Step)>();
            foreach (var existingType in existingTypes)
            {
                var steps = await service.ListStepsForTypeAsync(existingType.Id, cancellationToken: cancellationToken);
                foreach (var step in steps)
                    existingPairs.Add((existingType, step));
            }

            var configuredPairs = new List<(PluginTypeConfig Type, PluginStepConfig Step)>();
            foreach (var typeConfigForPair in assemblyConfig.Types)
                foreach (var stepConfigForPair in typeConfigForPair.Steps)
                    configuredPairs.Add((typeConfigForPair, stepConfigForPair));

            var matches = PluginStepMatcher.Match(
                configuredPairs,
                existingPairs,
                onResidualCollision: (id, configCount, envCount) =>
                    result.Warnings.Add(PluginStepMatcher.DescribeResidualCollision(id, configCount, envCount)));

            if (!globalOptions.IsJsonMode)
            {
                foreach (var warning in result.Warnings)
                    Console.Error.WriteLine($"  [!] Warning: {warning}");
            }

            // Map each configured step (by reference identity) to its matched environment step, if any.
            // A configured step with no match is force-created; a paired step updates that exact row.
            var envForConfigStep = new Dictionary<PluginStepConfig, PluginStepInfo?>(StepConfigReferenceComparer.Instance);
            foreach (var match in matches)
            {
                if (match.Config is not null)
                    envForConfigStep[match.Config] = match.Env;
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
                    // Resolve auto-generated name if not specified. Deploy writes this name to the
                    // matched row, so a renamed environment step converges back to the configured name.
                    stepConfig.Name ??= PluginStepMatcher.ResolveConfigName(typeConfig, stepConfig);
                    var stepName = stepConfig.Name;

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

                    var matchedEnvStep = envForConfigStep.TryGetValue(stepConfig, out var env) ? env : null;
                    var isNew = matchedEnvStep is null;

                    // Identity-based resolution: paired -> update that exact GUID; missing -> force-create
                    // (a null id skips the name lookup so a same-named different-identity row is never hijacked).
                    var resolution = new StepIdentityResolution(matchedEnvStep?.Id);

                    Guid stepId;
                    if (dryRun)
                    {
                        stepId = matchedEnvStep?.Id ?? Guid.NewGuid();
                        if (!globalOptions.IsJsonMode)
                            Console.Error.WriteLine($"    [Dry-Run] Would {(isNew ? "create" : "update")} step: {stepName}");

                        if (isNew) result.StepsCreated++;
                        else result.StepsUpdated++;
                    }
                    else
                    {
                        stepId = await service.UpsertStepAsync(typeId, "pluginType", stepConfig, messageId.Value, filterId, solution, resolution, cancellationToken);
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

            // Handle orphan cleanup if requested. Orphans are the environment steps the matcher could
            // not pair to any configured step; delete exactly those rows by GUID.
            if (clean)
            {
                var orphanedSteps = matches.Where(m => m.IsOrphaned).Select(m => m.Env!).ToList();

                if (orphanedSteps.Count > 0)
                {
                    if (!globalOptions.IsJsonMode)
                        Console.Error.WriteLine($"  Cleaning {orphanedSteps.Count} orphaned step(s)...");

                    foreach (var orphanStep in orphanedSteps)
                    {
                        if (dryRun)
                        {
                            if (!globalOptions.IsJsonMode)
                                Console.Error.WriteLine($"    [Dry-Run] Would delete step: {orphanStep.Name}");
                            result.StepsDeleted++;
                        }
                        else
                        {
                            await service.DeleteStepAsync(orphanStep.Id, cancellationToken);
                            if (!globalOptions.IsJsonMode)
                                Console.Error.WriteLine($"    Deleted step: {orphanStep.Name}");
                            result.StepsDeleted++;
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

    private static async Task DeployCustomApisAsync(
        IPluginRegistrationService registrationService,
        ICustomApiService customApiService,
        List<CustomApiConfig> customApis,
        bool dryRun,
        GlobalOptionValues globalOptions,
        CancellationToken cancellationToken)
    {
        if (!globalOptions.IsJsonMode)
            Console.Error.WriteLine($"Deploying {customApis.Count} custom API(s)...");

        foreach (var apiConfig in customApis)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Look up the plugin type by fully qualified name
            var pluginType = await registrationService.GetPluginTypeByNameAsync(
                apiConfig.PluginTypeName,
                cancellationToken);

            if (pluginType == null)
            {
                if (!globalOptions.IsJsonMode)
                    Console.Error.WriteLine($"  [Skip] Plugin type not found: {apiConfig.PluginTypeName}");
                continue;
            }

            // Build parameter registrations
            List<CustomApiParameterRegistration>? parameters = null;
            if (apiConfig.Parameters != null && apiConfig.Parameters.Count > 0)
            {
                parameters = apiConfig.Parameters
                    .Select(p => new CustomApiParameterRegistration(
                        UniqueName: p.UniqueName ?? p.Name,
                        DisplayName: p.DisplayName ?? p.Name,
                        Name: p.Name,
                        Description: p.Description,
                        Type: p.Type,
                        LogicalEntityName: p.LogicalEntityName,
                        IsOptional: p.IsOptional,
                        Direction: p.Direction))
                    .ToList();
            }

            var registration = new CustomApiRegistration(
                UniqueName: apiConfig.UniqueName,
                DisplayName: apiConfig.DisplayName,
                Name: apiConfig.Name,
                Description: apiConfig.Description,
                PluginTypeId: pluginType.Id,
                BindingType: apiConfig.BindingType,
                BoundEntity: apiConfig.BoundEntity,
                IsFunction: apiConfig.IsFunction,
                IsPrivate: apiConfig.IsPrivate,
                ExecutePrivilegeName: apiConfig.ExecutePrivilegeName,
                AllowedProcessingStepType: apiConfig.AllowedProcessingStepType,
                Parameters: parameters);

            if (dryRun)
            {
                if (!globalOptions.IsJsonMode)
                    Console.Error.WriteLine($"  [Dry-Run] Would register custom API: {apiConfig.UniqueName}");
            }
            else
            {
                var apiId = await customApiService.RegisterAsync(registration, cancellationToken: cancellationToken);
                if (!globalOptions.IsJsonMode)
                    Console.Error.WriteLine($"  Custom API registered: {apiConfig.UniqueName} ({apiId})");
            }
        }
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

    internal sealed class DeploymentResult
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

        /// <summary>
        /// Advisory messages (e.g., residual functional-identity collisions). Deployment still
        /// proceeds; these surface ambiguity for the operator to resolve.
        /// </summary>
        [JsonPropertyName("warnings")]
        public List<string> Warnings { get; set; } = [];
    }

    /// <summary>
    /// Reference-identity comparer for mapping a specific <see cref="PluginStepConfig"/> instance to its
    /// matched environment step. Two distinct config objects with identical values must remain distinct
    /// keys (the matcher already zipped them positionally), so value equality would be wrong here.
    /// </summary>
    private sealed class StepConfigReferenceComparer : IEqualityComparer<PluginStepConfig>
    {
        public static readonly StepConfigReferenceComparer Instance = new();

        public bool Equals(PluginStepConfig? x, PluginStepConfig? y) => ReferenceEquals(x, y);

        public int GetHashCode(PluginStepConfig obj) =>
            System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }

    #endregion
}
