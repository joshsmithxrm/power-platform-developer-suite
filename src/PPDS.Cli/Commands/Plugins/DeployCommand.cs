using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Cli.Plugins.Extraction;
using PPDS.Cli.Plugins.Models;
using PPDS.Cli.Plugins.Registration;
using PPDS.Cli.Services;
using PPDS.Cli.Services.Plugins;

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

    internal static async Task<int> ExecuteAsync(
        FileInfo configFile,
        string? profile,
        string? environment,
        string? solutionOverride,
        bool clean,
        bool dryRun,
        GlobalOptionValues globalOptions,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task<ServiceProvider>>? serviceProviderFactory = null,
        Func<string, CancellationToken, Task<byte[]>>? preflightPackageContentReader = null)
    {
        var writer = ServiceFactory.CreateOutputWriter(globalOptions);

        try
        {
            // Load configuration
            var configJson = await File.ReadAllTextAsync(configFile.FullName, cancellationToken);
            var config = JsonSerializer.Deserialize<PluginRegistrationConfig>(configJson, JsonReadOptions);

            var configuredCustomApiCount = (config?.CustomApis?.Count ?? 0)
                + (config?.Assemblies?.Sum(assembly => assembly.CustomApis?.Count ?? 0) ?? 0);

            if ((config?.Assemblies == null || config.Assemblies.Count == 0) && configuredCustomApiCount == 0)
            {
                writer.WriteError(new StructuredError(
                    ErrorCodes.Validation.InvalidValue,
                    "No assemblies found in configuration file.",
                    Target: configFile.Name));
                return ExitCodes.InvalidArguments;
            }

            // Validate configuration
            config!.Validate();

            var configDir = configFile.DirectoryName ?? ".";

            // Validate and buffer every configured artifact before authentication, environment
            // resolution, or any Dataverse request. The retained package bytes are the exact
            // bytes later inspected and uploaded, so one failing assembly aborts the entire
            // configuration without partially applying a later assembly or Custom API.
            var deploymentPreflights = await PreflightAssembliesAsync(
                config.Assemblies,
                configDir,
                cancellationToken,
                preflightPackageContentReader);

            // Connect to Dataverse
            await using var serviceProvider = serviceProviderFactory == null
                ? await ProfileServiceFactory.CreateFromProfilesAsync(
                    profile,
                    environment,
                    globalOptions.Verbose,
                    globalOptions.Debug,
                    ProfileServiceFactory.DefaultDeviceCodeCallback,
                    cancellationToken)
                : await serviceProviderFactory(cancellationToken);

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

            var results = new List<DeploymentResult>();

            if (config.Assemblies != null)
            {
                for (var index = 0; index < config.Assemblies.Count; index++)
                {
                    var assemblyConfig = config.Assemblies[index];
                    var result = await DeployAssemblyAsync(
                        registrationService,
                        assemblyConfig,
                        configDir,
                        solutionOverride,
                        clean,
                        dryRun,
                        globalOptions,
                        cancellationToken,
                        deploymentPreflight: deploymentPreflights[index]);

                    results.Add(result);
                }
            }

            // Root-level APIs can intentionally target an already registered type. APIs nested
            // under an assembly are owned by that deployment and must not bind to a stale
            // Dataverse type when the corresponding preflight or deployment failed.
            var allCustomApis = new List<CustomApiConfig>(config.CustomApis ?? []);
            allCustomApis.AddRange(SelectAssemblyCustomApisForDeployment(config.Assemblies, results));

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
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: "deploying plugins", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    /// <summary>
    /// Resolves and buffers every configured deployment artifact as one local, fail-closed phase.
    /// </summary>
    internal static async Task<IReadOnlyList<AssemblyDeploymentPreflight>> PreflightAssembliesAsync(
        IReadOnlyList<PluginAssemblyConfig>? assemblies,
        string configDir,
        CancellationToken cancellationToken,
        Func<string, CancellationToken, Task<byte[]>>? packageContentReader = null)
    {
        if (assemblies == null || assemblies.Count == 0)
            return [];

        var pathComparer = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var configuredPaths = new HashSet<string>(pathComparer);
        var packagePathsById = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var preflights = new List<AssemblyDeploymentPreflight>(assemblies.Count);

        foreach (var assembly in assemblies)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var assemblyPath = ResolveAssemblyPath(assembly, configDir);
            if (assemblyPath == null || !File.Exists(assemblyPath))
            {
                throw new FileNotFoundException(
                    $"Assembly file not found: {assembly.Path ?? assembly.PackagePath}");
            }

            var canonicalPath = Path.GetFullPath(assemblyPath);
            if (!configuredPaths.Add(canonicalPath))
            {
                throw new PpdsException(
                    ErrorCodes.Validation.InvalidValue,
                    $"Deployment artifact '{canonicalPath}' is configured more than once. " +
                    "Each package or assembly path must appear exactly once. No changes were applied.");
            }

            var artifactBytes = packageContentReader == null
                ? await File.ReadAllBytesAsync(canonicalPath, cancellationToken)
                : await packageContentReader(canonicalPath, cancellationToken);

            if (!string.Equals(assembly.Type, "Nuget", StringComparison.OrdinalIgnoreCase))
            {
                preflights.Add(new AssemblyDeploymentPreflight(assembly, canonicalPath, artifactBytes));
                continue;
            }

            var packageName = PluginPackageMetadataReader.Read(artifactBytes).Id;
            var packageAssemblyName = NupkgExtractor.InspectConfiguredAssemblyIdentity(
                artifactBytes,
                canonicalPath,
                assembly);

            if (!string.Equals(assembly.Name, packageAssemblyName, StringComparison.OrdinalIgnoreCase))
            {
                throw new PpdsException(
                    ErrorCodes.Plugin.PackageAssemblyMismatch,
                    $"Configured assembly '{assembly.Name}' does not match the package's primary assembly " +
                    $"'{packageAssemblyName}' in '{Path.GetFileName(canonicalPath)}'. No package was uploaded. " +
                    "Re-run 'ppds plugins extract' for this package or correct assemblies[].name before deploying.");
            }

            if (packagePathsById.TryGetValue(packageName, out var existingPath))
            {
                throw new PpdsException(
                    ErrorCodes.Validation.InvalidValue,
                    $"NuGet package ID '{packageName}' is configured from both '{existingPath}' and " +
                    $"'{canonicalPath}'. Each package ID must have one deployment source. No changes were applied.");
            }

            packagePathsById.Add(packageName, canonicalPath);
            preflights.Add(new AssemblyDeploymentPreflight(
                assembly,
                canonicalPath,
                artifactBytes,
                packageName,
                packageAssemblyName));
        }

        return preflights;
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
        CancellationToken cancellationToken,
        Func<string, CancellationToken, Task<byte[]>>? packageContentReader = null,
        AssemblyDeploymentPreflight? deploymentPreflight = null)
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

            deploymentPreflight ??= (await PreflightAssembliesAsync(
                [assemblyConfig],
                configDir,
                cancellationToken,
                packageContentReader))[0];
            if (!ReferenceEquals(deploymentPreflight.Assembly, assemblyConfig))
            {
                throw new ArgumentException(
                    "The deployment preflight does not belong to the supplied assembly configuration.",
                    nameof(deploymentPreflight));
            }

            var assemblyPath = deploymentPreflight.AssemblyPath;

            // Deploy assembly or package based on type
            Guid assemblyId;
            if (assemblyConfig.Type == "Nuget")
            {
                // For NuGet packages, upload the entire .nupkg to pluginpackage entity
                var packageBytes = deploymentPreflight.ArtifactBytes;
                var packageName = deploymentPreflight.PackageName!;
                var packageAssemblyName = deploymentPreflight.PackageAssemblyName!;

                Guid packageId;
                if (dryRun)
                {
                    var existingPkg = await service.GetPackageByNameAsync(packageName, cancellationToken);
                    packageId = existingPkg?.Id ?? Guid.NewGuid();
                    if (!globalOptions.IsJsonMode)
                        Console.Error.WriteLine($"  [Dry-Run] Would {(existingPkg == null ? "create" : "update")} package: {packageName}");
                }
                else
                {
                    packageId = await service.UpsertPackageAsync(packageName, packageBytes, solution, cancellationToken);
                    if (!globalOptions.IsJsonMode)
                        Console.Error.WriteLine($"  Package registered: {packageId}");
                }

                // Get the assembly ID from the package (Dataverse creates it automatically)
                // Use the inspected manifest name so config casing cannot affect lookup.
                var pkgAssemblyId = await service.GetAssemblyIdForPackageAsync(
                    packageId,
                    packageAssemblyName,
                    cancellationToken);
                if (pkgAssemblyId == null && !dryRun)
                {
                    var recoveryGuidance =
                        $"The uploaded package ID is {packageId}. Inspect it with " +
                        $"'ppds plugins get package {packageName}'. " +
                        $"Use 'ppds plugins list --package {packageName}' to inspect materialized assemblies. " +
                        "If Dataverse has materialized the assembly, re-run deploy. If the package is incomplete " +
                        "and cleanup is appropriate, 'ppds plugins unregister package' is destructive and must " +
                        "be run separately with normal environment confirmation. PPDS did not attempt automatic cleanup.";
                    throw new PpdsException(
                        ErrorCodes.Plugin.PackageAssemblyUnavailableAfterUpload,
                        $"Package '{packageName}' ({packageId}) was uploaded, but Dataverse did not return its " +
                        $"expected assembly '{packageAssemblyName}'. The package may now be partially deployed.",
                        new Dictionary<string, object>
                        {
                            ["recoveryGuidance"] = recoveryGuidance,
                            ["packageName"] = packageName,
                            ["packageId"] = packageId,
                            ["assemblyName"] = packageAssemblyName
                        });
                }
                // In dry-run mode for new packages, the assembly won't exist yet - use a placeholder ID
                assemblyId = pkgAssemblyId ?? Guid.NewGuid();
            }
            else
            {
                // For classic assemblies, upload the DLL directly
                var assemblyBytes = deploymentPreflight.ArtifactBytes;

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

            // Per-step configuration errors (e.g., an entity with no SDK message filter). These fail
            // the assembly result — and thus the command exit code — without aborting the remaining
            // steps, mirroring how a thrown exception fails the whole assembly.
            var stepErrors = new List<string>();

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

                    // A specified entity that resolves no SDK message filter is a configuration error
                    // (typo, or unsupported message/entity combo) — the step can never be registered
                    // against that entity. Creating it anyway would produce a GLOBAL step whose
                    // read-back identity ("none") never matches this config identity, so every later
                    // deploy would force-create yet another duplicate (#1332). Only an intentionally
                    // global step (entity empty/"none") may proceed with a null filter.
                    if (filterId == null &&
                        (PluginStepIdentity.IsEntitySpecified(stepConfig.Entity) ||
                         PluginStepIdentity.IsEntitySpecified(stepConfig.SecondaryEntity)))
                    {
                        var stepError = $"Step '{stepName}' was not deployed: " +
                            PluginRegistrationService.DescribeMissingMessageFilter(
                                stepConfig.Message, stepConfig.Entity, stepConfig.SecondaryEntity);
                        stepErrors.Add(stepError);

                        if (!globalOptions.IsJsonMode)
                            Console.Error.WriteLine($"    [Error] {stepError}");
                        continue;
                    }

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
            else
            {
                // Without --clean we must not delete anything. But because a stage/mode change is now
                // delete+create, the old step lingers in the environment and STILL fires — with no other
                // signal. Surface orphans loudly so the operator knows to re-run with --clean.
                var orphanedSteps = matches.Where(m => m.IsOrphaned).Select(m => m.Env!).ToList();

                if (orphanedSteps.Count > 0)
                {
                    var descriptions = string.Join(
                        ", ",
                        orphanedSteps.Select(o => $"{o.Name} ({o.Stage}, {o.Mode})"));
                    var warning =
                        $"{orphanedSteps.Count} step(s) in the environment no longer match the configuration and were " +
                        $"left in place (they remain active): {descriptions}. Re-run with --clean to remove them.";

                    result.Warnings.Add(warning);

                    if (!globalOptions.IsJsonMode)
                        Console.Error.WriteLine($"  [!] Warning: {warning}");
                }
            }

            // Per-step configuration errors fail the assembly (and the command exit code) even though
            // the remaining steps were still deployed.
            if (stepErrors.Count > 0)
            {
                result.Success = false;
                result.Error = string.Join("; ", stepErrors);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Error = ex.Message;
            if (ex is PpdsException ppdsException)
            {
                result.ErrorCode = ppdsException.ErrorCode;
                if (ppdsException.Context?.TryGetValue("recoveryGuidance", out var recovery) == true)
                    result.RecoveryGuidance = recovery?.ToString();
            }

            if (!globalOptions.IsJsonMode)
            {
                Console.Error.WriteLine($"  Error: {ex.Message}");
                if (!string.IsNullOrWhiteSpace(result.RecoveryGuidance))
                    Console.Error.WriteLine($"  Recovery: {result.RecoveryGuidance}");
            }
        }

        return result;
    }

    /// <summary>
    /// Returns only the Custom APIs owned by assemblies whose deployment completed successfully.
    /// The lists are positional because <see cref="ExecuteAsync"/> records one result for each
    /// configured assembly in order.
    /// </summary>
    internal static IReadOnlyList<CustomApiConfig> SelectAssemblyCustomApisForDeployment(
        IReadOnlyList<PluginAssemblyConfig>? assemblies,
        IReadOnlyList<DeploymentResult> results)
    {
        if (assemblies == null || assemblies.Count == 0)
            return [];

        if (assemblies.Count != results.Count)
        {
            throw new ArgumentException(
                "Assembly deployment results must align with the configured assemblies.",
                nameof(results));
        }

        return assemblies
            .Zip(results)
            .Where(pair => pair.Second.Success)
            .SelectMany(pair => pair.First.CustomApis ?? [])
            .ToList();
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

    #region Result Models

    internal sealed record AssemblyDeploymentPreflight(
        PluginAssemblyConfig Assembly,
        string AssemblyPath,
        byte[] ArtifactBytes,
        string? PackageName = null,
        string? PackageAssemblyName = null);

    internal sealed class DeploymentResult
    {
        [JsonPropertyName("assemblyName")]
        public string AssemblyName { get; set; } = string.Empty;

        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("error")]
        public string? Error { get; set; }

        [JsonPropertyName("errorCode")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ErrorCode { get; set; }

        [JsonPropertyName("recoveryGuidance")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? RecoveryGuidance { get; set; }

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
