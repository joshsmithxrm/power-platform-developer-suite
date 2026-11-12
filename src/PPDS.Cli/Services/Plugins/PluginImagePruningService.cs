using System.Text.Json.Serialization;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Progress;
using PPDS.Cli.Plugins.Models;
using PPDS.Cli.Plugins.Registration;

namespace PPDS.Cli.Services.Plugins;

/// <summary>Plans the complete explicitly requested v2 image-only scope before any mutation.</summary>
internal static class PluginImagePruningService
{
    private const string ResultKey = "PPDS.PluginImagePruningResult";
    private static readonly PluginListOptions InventoryOptions = new(IncludeHidden: true, IncludeMicrosoft: true);
    private static readonly StringComparer Names = StringComparer.OrdinalIgnoreCase;

    internal static void ValidateAuthority(IReadOnlyList<PluginAssemblyConfig> assemblies, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Require(assemblies.All(a => a is not null && a.OperationIntent == RegistrationOperationIntent.Reconcile),
            "Image-only cleanup requires the version 2.0 registration contract.");
        Require(assemblies.Select(a => a.Name).Distinct(Names).Count() == assemblies.Count,
            "Image cleanup requires one configuration per assembly name.");
        foreach (var assembly in assemblies)
        {
            Require(!string.IsNullOrWhiteSpace(assembly.Name), "Image cleanup requires an assembly name.");
            Require(Names.Equals(assembly.Type, "Assembly") || Names.Equals(assembly.Type, "Nuget"),
                "Image cleanup requires a supported assembly or NuGet configuration type.");
            Require(assembly.Types.Select(t => t.TypeName).Distinct(Names).Count() == assembly.Types.Count,
                "Image cleanup cannot select duplicate configured type names.");
            foreach (var type in assembly.Types)
            {
                Require(!string.IsNullOrWhiteSpace(type.TypeName), "Image cleanup requires a configured type name.");
                Require(type.Steps.Select(s => PluginStepIdentity.FromConfig(type.TypeName, s)).Distinct().Count() == type.Steps.Count,
                    "Image cleanup requires unique configured step identities.");
                foreach (var step in type.Steps)
                {
                    ct.ThrowIfCancellationRequested();
                    Require(!string.IsNullOrWhiteSpace(step.Message), "Image cleanup requires a configured step message.");
                    Require(step.IsSpecified("images") && step.Images is not null,
                        "Version 2 image cleanup requires an explicit images list on every configured step; use [] only to clear its owned images.");
                    var images = step.Images!;
                    _ = PluginRegistrationNormalizer.NormalizeStep(step, RegistrationOperationIntent.Reconcile);
                    Require(images.All(i => i is not null && !string.IsNullOrWhiteSpace(i.Name)) &&
                        images.Select(i => i.Name).Distinct(Names).Count() == images.Count,
                        "Image cleanup requires unique nonempty configured image names.");
                    foreach (var image in images)
                        _ = PluginRegistrationNormalizer.NormalizeImage(image, step.Message, RegistrationOperationIntent.Reconcile);
                }
            }
        }
    }

    internal static async Task<PluginImagePruningPlan> PlanAsync(IPluginRegistrationService service,
        IReadOnlyList<PluginAssemblyConfig> assemblies, IReadOnlyDictionary<string, PluginPackageResolution>? packages,
        CancellationToken ct, IProgressReporter? reporter = null)
    {
        try { return await BuildPlanAsync(service, assemblies, packages, ct, reporter); }
        catch (OperationCanceledException) { throw; }
        catch (PpdsException) { throw; }
        catch (Exception ex)
        {
            throw new PpdsException(ErrorCodes.Validation.InvalidValue,
                "Could not build a complete image-cleanup plan. No cleanup was dispatched.", ex);
        }
    }

    private static async Task<PluginImagePruningPlan> BuildPlanAsync(IPluginRegistrationService service,
        IReadOnlyList<PluginAssemblyConfig> assemblies, IReadOnlyDictionary<string, PluginPackageResolution>? packages,
        CancellationToken ct, IProgressReporter? reporter)
    {
        ValidateAuthority(assemblies, ct);
        var scopes = new List<PluginImagePruningScope>();
        var images = new List<PluginImagePruningTarget>();
        var inventories = new List<PluginImagePruningInventory>();
        foreach (var config in assemblies)
        {
            reporter?.ReportPhase("Planning", "Reading image-cleanup ownership and complete scoped inventories");
            ct.ThrowIfCancellationRequested();
            string? packageName = null;
            Guid? packageId = null;
            PluginPackageResolution? packageResolution = null;
            if (string.Equals(config.Type, "Nuget", StringComparison.OrdinalIgnoreCase))
            {
                Require(packages is not null && packages.TryGetValue(config.Name, out packageResolution),
                    "Package image cleanup requires a resolved registration identity.");
                await ReadAsync(async () =>
                {
                    await service.ValidatePackageResolutionAsync(packageResolution!, ct, reporter);
                    return true;
                }, ct);
                packageName = packageResolution!.RegisteredName;
                if (packageResolution.ExistingPackageId is not { } selectedPackageId)
                {
                    inventories.Add(new(config.Name, null, null, 0, 0, 0, 0, "no-existing-package-observed"));
                    continue;
                }
                var package = await ReadAsync(() => service.GetPackageByIdAsync(selectedPackageId, ct), ct);
                Require(package is not null && package.Id == selectedPackageId && Names.Equals(package.Name, packageName) &&
                    Names.Equals(package.UniqueName, packageResolution.RegisteredUniqueName) && !package.IsManaged,
                    "Image cleanup could not verify an unmanaged selected package by ID.");
                packageId = package!.Id;
            }
            Guid? selectedAssemblyId;
            if (packageId is { } selectedParentId)
                selectedAssemblyId = await ReadAsync(() => service.GetAssemblyIdForPackageAsync(selectedParentId, config.Name, ct), ct);
            else
            {
                var assembliesFound = await ReadAsync(() => service.ListAssembliesAsync(config.Name, InventoryOptions, ct), ct);
                UniqueIds(assembliesFound.Select(a => a.Id));
                Require(assembliesFound.All(a => Names.Equals(a.Name, config.Name)) && assembliesFound.Count <= 1,
                    "Image cleanup requires a unique exact assembly inventory.");
                selectedAssemblyId = assembliesFound.Count == 0 ? null : assembliesFound[0].Id;
            }
            if (selectedAssemblyId is null)
            {
                inventories.Add(new(config.Name, null, packageId, 0, 0, 0, 0, "no-existing-assembly-observed"));
                continue;
            }
            var assembly = await ReadAsync(() => service.GetAssemblyByIdAsync(selectedAssemblyId.Value, ct), ct);
            Require(assembly is not null && assembly.Id == selectedAssemblyId && Names.Equals(assembly.Name, config.Name) &&
                assembly.PackageId == packageId && !assembly.IsManaged && !assembly.IsHidden,
                "Image cleanup cannot use a hidden or managed assembly or unverified package ownership.");
            var types = await ReadAsync(() => service.ListTypesForAssemblyAsync(assembly!.Id, ct), ct);
            UniqueIds(types.Select(t => t.Id));
            Require(types.All(t => !string.IsNullOrWhiteSpace(t.TypeName)), "Image cleanup received an incomplete type inventory.");
            var unconfiguredTypes = types.Count(t => !config.Types.Any(c => Names.Equals(c.TypeName, t.TypeName)));
            var observedSteps = 0; var retainedSteps = 0; var observedImages = 0;
            foreach (var typeConfig in config.Types)
            {
                var selectedTypes = types.Where(t => Names.Equals(t.TypeName, typeConfig.TypeName)).ToList();
                Require(selectedTypes.Count <= 1, "Image cleanup cannot select an ambiguous configured type.");
                if (selectedTypes.Count == 0) continue;
                var type = await ReadAsync(() => service.GetPluginTypeByNameOrIdAsync(selectedTypes[0].Id.ToString("D"), ct), ct);
                Require(type is not null && type.Id == selectedTypes[0].Id && type.AssemblyId == assembly!.Id &&
                    Names.Equals(type.TypeName, typeConfig.TypeName) && !type.IsManaged && !selectedTypes[0].IsManaged,
                    "Image cleanup could not verify a selected type's assembly ownership by ID.");
                var steps = await ReadAsync(() => service.ListStepsForTypeAsync(type!.Id, InventoryOptions, ct), ct);
                UniqueIds(steps.Select(s => s.Id));
                foreach (var step in steps) ValidateStepIdentity(step);
                observedSteps += steps.Count;
                retainedSteps += steps.Count(s => !typeConfig.Steps.Any(c => PluginStepIdentity.FromConfig(typeConfig.TypeName, c) ==
                    PluginStepIdentity.FromEnvironment(typeConfig.TypeName, s)));
                foreach (var stepConfig in typeConfig.Steps)
                {
                    var identity = PluginStepIdentity.FromConfig(typeConfig.TypeName, stepConfig);
                    var matchingSteps = steps.Where(s => PluginStepIdentity.FromEnvironment(typeConfig.TypeName, s) == identity).ToList();
                    Require(matchingSteps.Count <= 1, "Image cleanup cannot select an ambiguous functional step identity.");
                    if (matchingSteps.Count == 0) continue;
                    var listedStep = matchingSteps[0];
                    Require(listedStep.PluginTypeId is null || listedStep.PluginTypeId == type!.Id,
                        "Image cleanup received a foreign step in its scoped inventory.");
                    var step = await ReadAsync(() => service.GetStepByNameOrIdAsync(listedStep.Id.ToString("D"), ct), ct);
                    Require(step is not null && step.Id == listedStep.Id && step.PluginTypeId == type!.Id,
                        "Image cleanup could not verify a selected step's type ownership by ID.");
                    ValidateStepIdentity(step!);
                    Require(PluginStepIdentity.FromEnvironment(typeConfig.TypeName, step!) == identity &&
                        !listedStep.IsHidden && !step!.IsHidden && !step.IsManaged && step.IsCustomizable,
                        "Image cleanup cannot use a changed, hidden, managed or noncustomizable step.");
                    var scope = new PluginImagePruningScope(config.Name, assembly!.Id, packageId, packageName,
                        type!.Id, type.TypeName, step!.Id, identity, packageResolution);
                    scopes.Add(scope);
                    var listedImages = await ReadAsync(() => service.ListImagesForStepAsync(step.Id, ct), ct);
                    ValidateImageInventory(listedImages);
                    observedImages += listedImages.Count;
                    foreach (var listed in listedImages)
                    {
                        Require(listed.StepId is null || listed.StepId == step.Id,
                            "Image cleanup received a foreign image in its scoped inventory.");
                        var image = await ReadAsync(() => service.GetImageByNameOrIdAsync(listed.Id.ToString("D"), ct), ct);
                        Require(image is not null && image.Id == listed.Id && image.StepId == step.Id,
                            "Image cleanup could not verify image ownership by ID.");
                        ValidateImageIdentity(image!);
                        Require(Names.Equals(image!.Name, listed.Name) && image.ImageType == listed.ImageType,
                            "Image cleanup image identity changed during planning.");
                        if (stepConfig.Images.Any(c => Names.Equals(c.Name, image.Name))) continue;
                        Require(!image.IsManaged && image.IsCustomizable,
                            "Image cleanup cannot delete a managed or noncustomizable image.");
                        images.Add(new(scope, image.Id, image.Name, image.ImageType, image.ModifiedOn));
                    }
                }
            }
            inventories.Add(new(config.Name, assembly!.Id, packageId, types.Count, unconfiguredTypes,
                observedSteps, retainedSteps, "planned", observedImages));
        }
        ct.ThrowIfCancellationRequested();
        Require(images.Select(i => i.ImageId).Distinct().Count() == images.Count,
            "An image cannot be adopted by more than one cleanup scope.");
        return new(Array.AsReadOnly(scopes.ToArray()), Array.AsReadOnly(images.ToArray()), Array.AsReadOnly(inventories.ToArray()));
    }

    internal static async Task<PluginImagePruningResult> ApplyAsync(IPluginRegistrationService service,
        PluginImagePruningPlan plan, CancellationToken ct, IProgressReporter? reporter = null)
    {
        var result = new PluginImagePruningResult(plan);
        var journal = new PluginMutationJournal("imagePruning");
        try
        {
            // Validate the entire frozen scope before the first delete, including later scopes.
            foreach (var scope in plan.Scopes)
            {
                await VerifyUniquenessAsync(service, scope, ct);
                await VerifyScopeAsync(service, scope, ct);
                var current = await ReadAsync(() => service.ListImagesForStepAsync(scope.StepId, ct), ct);
                ValidateImageInventory(current);
                var planned = plan.Images.Where(i => i.Scope == scope).ToArray();
                foreach (var image in planned)
                    Require(current.Any(i => i.Id == image.ImageId && Names.Equals(i.Name, image.ImageName) && i.ImageType == image.ImageType),
                        "The image-cleanup plan changed before mutation; rebuild the plan.");
                result.RecordRetained(scope.AssemblyName, current.Count(i => !plan.Images.Any(p => p.Scope == scope && p.ImageId == i.Id)));
            }
            foreach (var target in plan.Images)
            {
                await VerifyScopeAsync(service, target.Scope, ct);
                var image = await ReadAsync(() => service.GetImageByNameOrIdAsync(target.ImageId.ToString("D"), ct), ct);
                Require(image is not null && image.Id == target.ImageId && image.StepId == target.Scope.StepId &&
                    Names.Equals(image.Name, target.ImageName) && image.ImageType == target.ImageType &&
                    (target.ModifiedOn is null || target.ModifiedOn == image.ModifiedOn),
                    "Image cleanup ownership or identity changed before dispatch; rebuild the plan.");
                ValidateImageIdentity(image!);
                Require(!image!.IsManaged && image.IsCustomizable, "Image cleanup target is protected.");
            }
            foreach (var target in plan.Images)
            {
                // A final narrow read immediately precedes each exact-ID dispatch. This is not a conditional delete.
                await VerifyScopeAsync(service, target.Scope, ct);
                var image = await ReadAsync(() => service.GetImageByNameOrIdAsync(target.ImageId.ToString("D"), ct), ct);
                Require(image is not null && image.Id == target.ImageId && image.StepId == target.Scope.StepId &&
                    Names.Equals(image.Name, target.ImageName) && image.ImageType == target.ImageType &&
                    !image.IsManaged && image.IsCustomizable && (target.ModifiedOn is null || target.ModifiedOn == image.ModifiedOn),
                    "Image cleanup target changed before dispatch; remaining deletes were stopped.");
                ValidateImageIdentity(image!);
                reporter?.ReportPhase("Deleting", "Deleting one planned owned image by ID");
                ct.ThrowIfCancellationRequested();
                await journal.RunAsync("sdkmessageprocessingstepimage", "delete", "imageCleanup", target.ImageId,
                    () => service.DeleteImageAsync(target.ImageId, ct), ct);
            }
            result.Success = true;
            result.Status = "completed";
            return result;
        }
        catch (OperationCanceledException ex)
        {
            result.Status = "cancelled";
            Retain(ex);
            throw;
        }
        catch (PpdsException ex)
        {
            result.Status = "failed";
            Retain(ex);
            throw;
        }
        catch (Exception ex)
        {
            result.Status = "failed";
            var error = NormalizeFailure(ex,
                "Image cleanup stopped. Retained acknowledgements and unknown effects do not imply rollback.");
            Retain(error);
            throw error;
        }
        finally
        {
            result.Outcomes.AddRange(journal.Outcomes);
        }

        void Retain(Exception ex)
        {
            journal.Attach(ex);
            ex.Data[ResultKey] = result;
        }
    }

    internal static PluginImagePruningResult? InterruptedResult(Exception ex) => ex.Data[ResultKey] as PluginImagePruningResult;

    private static async Task VerifyUniquenessAsync(IPluginRegistrationService service, PluginImagePruningScope scope, CancellationToken ct)
    {
        if (scope.PackageId is { } packageId)
        {
            Require(scope.PackageResolution?.ExistingPackageId == packageId,
                "Package cleanup lost its resolved ownership binding.");
            await service.ValidatePackageResolutionAsync(scope.PackageResolution!, ct, null);
            var assemblyId = await ReadAsync(() => service.GetAssemblyIdForPackageAsync(packageId, scope.AssemblyName, ct), ct);
            Require(assemblyId == scope.AssemblyId, "Package-scoped assembly identity changed before image cleanup.");
        }
        else
        {
            var assemblies = await ReadAsync(() => service.ListAssembliesAsync(scope.AssemblyName, InventoryOptions, ct), ct);
            UniqueIds(assemblies.Select(a => a.Id));
            Require(assemblies.Count == 1 && assemblies[0].Id == scope.AssemblyId && Names.Equals(assemblies[0].Name, scope.AssemblyName),
                "Assembly selection became ambiguous before image cleanup.");
        }
        var types = await ReadAsync(() => service.ListTypesForAssemblyAsync(scope.AssemblyId, ct), ct);
        UniqueIds(types.Select(t => t.Id));
        var selectedTypes = types.Where(t => Names.Equals(t.TypeName, scope.TypeName)).ToArray();
        Require(selectedTypes.Length == 1 && selectedTypes[0].Id == scope.TypeId,
            "Type selection became ambiguous before image cleanup.");
        var steps = await ReadAsync(() => service.ListStepsForTypeAsync(scope.TypeId, InventoryOptions, ct), ct);
        UniqueIds(steps.Select(s => s.Id));
        foreach (var step in steps) ValidateStepIdentity(step);
        var selectedSteps = steps.Where(s => PluginStepIdentity.FromEnvironment(scope.TypeName, s) == scope.Identity).ToArray();
        Require(selectedSteps.Length == 1 && selectedSteps[0].Id == scope.StepId,
            "Step selection became ambiguous before image cleanup.");
    }

    private static async Task VerifyScopeAsync(IPluginRegistrationService service, PluginImagePruningScope scope, CancellationToken ct)
    {
        var assembly = await ReadAsync(() => service.GetAssemblyByIdAsync(scope.AssemblyId, ct), ct);
        Require(assembly is not null && assembly.Id == scope.AssemblyId && Names.Equals(assembly.Name, scope.AssemblyName) &&
            assembly.PackageId == scope.PackageId && !assembly.IsManaged && !assembly.IsHidden, "Selected assembly is hidden or its ownership/protection changed before image cleanup.");
        if (scope.PackageId is { } packageId)
        {
            var package = await ReadAsync(() => service.GetPackageByIdAsync(packageId, ct), ct);
            Require(package is not null && package.Id == packageId && Names.Equals(package.Name, scope.PackageName) &&
                Names.Equals(package.UniqueName, scope.PackageResolution?.RegisteredUniqueName) && !package.IsManaged,
                "Selected package ownership changed before image cleanup.");
        }
        var type = await ReadAsync(() => service.GetPluginTypeByNameOrIdAsync(scope.TypeId.ToString("D"), ct), ct);
        Require(type is not null && type.Id == scope.TypeId && type.AssemblyId == scope.AssemblyId && Names.Equals(type.TypeName, scope.TypeName) && !type.IsManaged,
            "Selected type ownership changed before image cleanup.");
        var step = await ReadAsync(() => service.GetStepByNameOrIdAsync(scope.StepId.ToString("D"), ct), ct);
        Require(step is not null && step.Id == scope.StepId && step.PluginTypeId == scope.TypeId &&
            PluginStepIdentity.FromEnvironment(scope.TypeName, step) == scope.Identity && !step.IsHidden && !step.IsManaged && step.IsCustomizable,
            "Selected step ownership changed before image cleanup.");
        ValidateStepIdentity(step!);
    }

    private static void ValidateStepIdentity(PluginStepInfo step)
    {
        Require(!string.IsNullOrWhiteSpace(step.Message) && !string.IsNullOrWhiteSpace(step.Stage) && !string.IsNullOrWhiteSpace(step.Mode) &&
            !step.UnavailableProperties.Overlaps(["message", "stage", "mode", "isHidden", "isManaged", "isCustomizable"]),
            "Image cleanup cannot use an incomplete step identity or protection metadata.");
        _ = PluginRegistrationNormalizer.NormalizeStage(step.Stage);
        _ = PluginRegistrationNormalizer.NormalizeMode(step.Mode);
    }

    private static void ValidateImageInventory(IReadOnlyList<PluginImageInfo> images)
    {
        UniqueIds(images.Select(i => i.Id));
        foreach (var image in images) ValidateImageIdentity(image);
        Require(images.Select(i => i.Name).Distinct(Names).Count() == images.Count,
            "Image cleanup cannot select duplicate image names within a step.");
    }

    private static void ValidateImageIdentity(PluginImageInfo image)
    {
        Require(image.Id != Guid.Empty && !string.IsNullOrWhiteSpace(image.Name) && !string.IsNullOrWhiteSpace(image.ImageType) &&
            !image.UnavailableProperties.Overlaps(["name", "imageType", "isManaged", "isCustomizable"]),
            "Image cleanup cannot use an incomplete image identity or protection metadata.");
        _ = PluginRegistrationNormalizer.NormalizeImageType(image.ImageType);
    }

    private static void UniqueIds(IEnumerable<Guid> values)
    {
        var ids = values.ToArray();
        Require(ids.All(id => id != Guid.Empty) && ids.Distinct().Count() == ids.Length,
            "Image cleanup received missing or duplicate record IDs.");
    }

    private static async Task<T> ReadAsync<T>(Func<Task<T>> read, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            var value = await read();
            ct.ThrowIfCancellationRequested();
            if (value is null) return value!; // Callers distinguish missing identities from valid empty inventories.
            return value;
        }
        catch (OperationCanceledException) { throw; }
        catch (PpdsException) { throw; }
        catch (Exception ex)
        {
            throw NormalizeFailure(ex,
                "Could not read a complete image-cleanup inventory or ownership record. No further cleanup was dispatched.");
        }
    }

    private static PpdsException NormalizeFailure(Exception ex, string userMessage)
    {
        // Retain the mapper's classification, fixed public message and original cause.
        // Provider message/target must not become public cleanup error details.
        var (mapped, exitCode) = ExceptionMapper.MapWithExitCode(ex);
        if (exitCode == ExitCodes.AuthError)
            return new PpdsAuthException(mapped.Code, userMessage, ex)
            {
                RequiresReauthentication = ex is PPDS.Dataverse.Resilience.DataverseAuthenticationException { RequiresReauthentication: true }
            };

        var code = exitCode == ExitCodes.ConnectionError ? mapped.Code : ErrorCodes.Validation.InvalidValue;
        return new PpdsException(code, userMessage, ex);
    }

    private static void Require(bool ok, string message)
    {
        if (!ok) throw new PpdsException(ErrorCodes.Validation.InvalidValue, message);
    }
}

internal sealed record PluginImagePruningScope(
    [property: JsonPropertyName("assemblyName")] string AssemblyName,
    [property: JsonPropertyName("assemblyId")] Guid AssemblyId,
    [property: JsonPropertyName("packageId")] Guid? PackageId,
    [property: JsonPropertyName("packageName")] string? PackageName,
    [property: JsonPropertyName("typeId")] Guid TypeId,
    [property: JsonPropertyName("typeName")] string TypeName,
    [property: JsonPropertyName("stepId")] Guid StepId,
    [property: JsonPropertyName("identity")] PluginStepIdentity Identity,
    [property: JsonIgnore] PluginPackageResolution? PackageResolution = null);

internal sealed record PluginImagePruningTarget(
    [property: JsonPropertyName("scope")] PluginImagePruningScope Scope,
    [property: JsonPropertyName("imageId")] Guid ImageId,
    [property: JsonPropertyName("imageName")] string ImageName,
    [property: JsonPropertyName("imageType")] string ImageType,
    [property: JsonIgnore] DateTime? ModifiedOn);

internal sealed record PluginImagePruningInventory(
    [property: JsonPropertyName("assemblyName")] string AssemblyName,
    [property: JsonPropertyName("assemblyId")] Guid? AssemblyId,
    [property: JsonPropertyName("packageId")] Guid? PackageId,
    [property: JsonPropertyName("observedTypes")] int ObservedTypes,
    [property: JsonPropertyName("unconfiguredTypesRetained")] int UnconfiguredTypesRetained,
    [property: JsonPropertyName("observedSteps")] int ObservedSteps,
    [property: JsonPropertyName("unconfiguredStepsRetained")] int UnconfiguredStepsRetained,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("observedImages")] int ObservedImages = 0);

internal sealed record PluginImagePruningPlan(
    [property: JsonPropertyName("scopes")] IReadOnlyList<PluginImagePruningScope> Scopes,
    [property: JsonPropertyName("images")] IReadOnlyList<PluginImagePruningTarget> Images,
    [property: JsonPropertyName("inventories")] IReadOnlyList<PluginImagePruningInventory> Inventories)
{
    internal PluginImagePruningPlan WithVerifiedPackages(IEnumerable<PluginPackageResolution> packages)
    {
        var byId = packages.Where(p => p.ExistingPackageId.HasValue)
            .ToDictionary(p => p.ExistingPackageId!.Value);
        var rebound = Scopes.ToDictionary(scope => scope, scope =>
        {
            if (scope.PackageId is not { } id || !byId.TryGetValue(id, out var verified)) return scope;
            if (scope.PackageResolution is not { } original ||
                original.ArtifactId != verified.ArtifactId || original.ArtifactSha256 != verified.ArtifactSha256 ||
                original.RegisteredName != verified.RegisteredName || original.RegisteredUniqueName != verified.RegisteredUniqueName)
                throw new PpdsException(ErrorCodes.Validation.InvalidValue,
                    "A verified upload cannot change the package ownership of an image-cleanup plan.");
            return scope with { PackageResolution = verified };
        });
        return new(Array.AsReadOnly(Scopes.Select(s => rebound[s]).ToArray()),
            Array.AsReadOnly(Images.Select(i => i with { Scope = rebound[i.Scope] }).ToArray()), Inventories);
    }

    internal PluginImagePruningPlan ForAssembly(string name) => new(
        Array.AsReadOnly(Scopes.Where(s => s.AssemblyName == name).ToArray()),
        Array.AsReadOnly(Images.Where(i => i.Scope.AssemblyName == name).ToArray()),
        Array.AsReadOnly(Inventories.Where(i => i.AssemblyName == name).ToArray()));
}

internal sealed class PluginImagePruningResult(PluginImagePruningPlan plan)
{
    private readonly Dictionary<string, int> _retainedByAssembly = new(StringComparer.Ordinal);
    [JsonPropertyName("plan")] public PluginImagePruningPlan Plan { get; } = plan;
    [JsonPropertyName("success")] public bool Success { get; internal set; }
    [JsonPropertyName("status")] public string Status { get; internal set; } = "planned";
    [JsonPropertyName("outcomes")] public List<PluginMutationOutcome> Outcomes { get; } = [];
    [JsonPropertyName("imagesDeleted")] public int ImagesDeleted => Outcomes.Count(o => o.Outcome == "acknowledged");
    [JsonPropertyName("newOrUnplannedImagesRetained")] public int NewImagesRetained => _retainedByAssembly.Values.Sum();
    [JsonPropertyName("partial")] public bool Partial => !Success && Outcomes.Any(o => o.Outcome == "acknowledged");
    [JsonPropertyName("coverage")] public string Coverage =>
        "Exact preplanned owned image IDs only. Parent registrations and unplanned images are retained. Ownership rechecks are not atomic conditional deletes; no rollback is implied.";

    internal void RecordRetained(string assemblyName, int count) =>
        _retainedByAssembly[assemblyName] = _retainedByAssembly.GetValueOrDefault(assemblyName) + count;

    internal PluginImagePruningResult ForAssembly(string name)
    {
        var result = new PluginImagePruningResult(Plan.ForAssembly(name)) { Success = Success, Status = Status };
        var ids = result.Plan.Images.Select(i => i.ImageId).ToHashSet();
        result.Outcomes.AddRange(Outcomes.Where(o => o.RecordId is { } id && ids.Contains(id)));
        result.RecordRetained(name, _retainedByAssembly.GetValueOrDefault(name));
        return result;
    }
}
