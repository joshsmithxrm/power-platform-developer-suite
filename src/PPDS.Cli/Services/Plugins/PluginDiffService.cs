using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Progress;
using PPDS.Cli.Plugins.Models;
using PPDS.Cli.Plugins.Registration;

namespace PPDS.Cli.Services.Plugins;

/// <summary>Read-only reconciliation of the explicitly managed registration subset.</summary>
internal sealed class PluginDiffService(IPluginRegistrationService registration)
{
    private static readonly string[] OptionalStepFields =
        ["filteringAttributes", "runAsUser", "unsecureConfiguration", "description",
         "asyncAutoDelete", "canBeBypassed", "canUseReadOnlyConnection"];
    private static readonly string[] OptionalImageFields = ["attributes", "description"];
    private static readonly HashSet<string> IdentityFields =
        new(["message", "entity", "secondaryEntity", "stage", "mode"], StringComparer.Ordinal);

    internal async Task<PluginDiffResult> CompareAsync(PluginRegistrationConfig config,
        bool includeHidden = false, IProgressReporter? progress = null, CancellationToken cancellationToken = default,
        IReadOnlyList<PluginAssemblyIdentity>? targets = null)
    {
        PluginRegistrationContract.Apply(config);
        if (targets is not null && targets.Count != config.Assemblies.Count)
            throw new PpdsException(ErrorCodes.Validation.InvalidValue,
                "Comparison target bindings must align with the configured assemblies.");
        var result = new PluginDiffResult();
        for (var index = 0; index < config.Assemblies.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.Assemblies.Add(await CompareAssemblyAsync(config.Assemblies[index], includeHidden, progress,
                cancellationToken, targets?[index]));
        }
        Unknown(config.ExtensionData, "configuration", result.Coverage);
        result.Coverage.Excluded.Add(new("scope", "assembly/package content, solution membership, Custom APIs and independent image deletion are outside this comparison"));
        return result;
    }

    internal async Task<AssemblyDrift> CompareAssemblyAsync(PluginAssemblyConfig config,
        bool includeHidden = false, IProgressReporter? progress = null, CancellationToken cancellationToken = default,
        PluginAssemblyIdentity? target = null)
    {
        try
        {
            return await CompareAssemblyCoreAsync(config, includeHidden, progress ?? NullProgressReporter.Instance, cancellationToken, target);
        }
        catch (OperationCanceledException) { throw; }
        catch (PpdsException) { throw; }
        catch (Exception ex)
        {
            // Do not echo arbitrary server exception text: it may contain configuration data.
            var code = ExceptionMapper.Map(ex).Code;
            throw new PpdsException(code, "Plugin registration comparison could not be completed.", ex);
        }
    }

    private async Task<AssemblyDrift> CompareAssemblyCoreAsync(PluginAssemblyConfig config,
        bool includeHidden, IProgressReporter progress, CancellationToken ct, PluginAssemblyIdentity? target)
    {
        ct.ThrowIfCancellationRequested();
        progress.ReportPhase("Comparing plugin registration", config.Name);
        var drift = new AssemblyDrift { AssemblyName = config.Name };
        // A file rule, not a coverage gap: two configured entries can never both be satisfied by one row.
        PluginImageNames.EnsureUniqueConfigured(config);
        Unknown(config.ExtensionData, "assembly", drift.Coverage);
        var configured = new List<(PluginTypeConfig Type, PluginStepConfig Step)>();
        foreach (var type in config.Types)
        {
            ct.ThrowIfCancellationRequested();
            Unknown(type.ExtensionData, $"type:{type.TypeName}", drift.Coverage);
            foreach (var step in type.Steps)
            {
                ct.ThrowIfCancellationRequested();
                // Normalize before any environment lookup, including missing rows.
                var managed = PluginRegistrationNormalizer.NormalizeStep(step, DesiredIntent(step));
                var path = StepPath(type, step);
                Unknown(step.ExtensionData, path, drift.Coverage);
                var legacyCompared = PluginRegistrationNormalizer.LegacyComparedOmissions(step);
                foreach (var field in OptionalStepFields.Where(f => !managed.ContainsKey(f) && !legacyCompared.Contains(f)))
                    drift.Coverage.Unmanaged.Add(new(path, field));
                drift.Coverage.Excluded.Add(new(path, "secureConfiguration (value never read or returned)"));
                // A supplied value is written but can never be read back. Report it as excluded so the
                // comparison stays visible without making every secure-config file incomplete.
                if (step.SecureConfiguration is not null)
                    drift.Coverage.Excluded.Add(new(path, "secureConfiguration supplied but not compared (write-only; never read back)"));
                else
                    drift.Coverage.Unmanaged.Add(new(path, "secureConfiguration"));
                if (step.IsSpecified("asyncAutoDelete") &&
                    PluginRegistrationNormalizer.NormalizeMode(step.Mode) != "Asynchronous")
                    drift.Coverage.Excluded.Add(new(path, "asyncAutoDelete not applicable to synchronous step"));
                foreach (var image in step.Images)
                {
                    var managedImage = PluginRegistrationNormalizer.NormalizeImage(image, step.Message, DesiredIntent(step));
                    var imagePath = $"{path}/image:{image.Name}";
                    Unknown(image.ExtensionData, imagePath, drift.Coverage);
                    var legacyComparedImage = PluginRegistrationNormalizer.LegacyComparedOmissions(step, image);
                    foreach (var field in OptionalImageFields.Where(f => !managedImage.ContainsKey(f) && !legacyComparedImage.Contains(f)))
                        drift.Coverage.Unmanaged.Add(new(imagePath, field));
                }
                configured.Add((type, step));
            }
        }

        var assembly = target is null
            ? await registration.GetAssemblyByNameAsync(config.Name, ct)
            : await registration.GetAssemblyByIdAsync(target.AssemblyId, ct);
        if (target is not null && assembly is not null &&
            (assembly.Id != target.AssemblyId || assembly.PackageId != target.PackageId ||
             !string.Equals(assembly.Name, config.Name, StringComparison.OrdinalIgnoreCase)))
            throw new PpdsException(ErrorCodes.Validation.InvalidValue,
                "The deployed assembly identity or package ownership changed before verification.");
        if (assembly is null)
        {
            drift.AssemblyMissing = true;
            return drift;
        }
        ct.ThrowIfCancellationRequested();
        var types = await registration.ListTypesForAssemblyAsync(assembly.Id, ct);
        var existing = new List<(PluginTypeInfo Type, PluginStepInfo Step)>();
        var hiddenIdentities = new HashSet<PluginStepIdentity>();
        foreach (var type in types)
        {
            ct.ThrowIfCancellationRequested();
            // Always read the complete inventory. Filtering here makes exclusions countable/reversible.
            var steps = await registration.ListStepsForTypeAsync(type.Id, new PluginListOptions(IncludeHidden: true), ct);
            foreach (var step in steps)
            {
                ct.ThrowIfCancellationRequested();
                drift.Coverage.TotalSteps++;
                foreach (var field in step.UnavailableProperties.Where(f => IdentityFields.Contains(f) || f == "name"))
                    drift.Coverage.Unsupported.Add(new($"step:{step.Id}", $"{field} unavailable"));
                if (step.UnavailableProperties.Contains("isHidden"))
                {
                    drift.Coverage.UnknownHiddenStatusSteps++;
                    drift.Coverage.Unsupported.Add(new($"step:{step.Id}", "hidden status unavailable"));
                }
                if (step.IsHidden) drift.Coverage.HiddenSteps++;
                if (step.IsHidden && !includeHidden)
                {
                    hiddenIdentities.Add(PluginStepIdentity.FromEnvironment(type.TypeName, step));
                    drift.Coverage.ExcludedHiddenSteps++;
                    drift.Coverage.Excluded.Add(new($"step:{step.Id}", "hidden; use --include-hidden to compare"));
                    continue;
                }
                drift.Coverage.ComparedSteps++;
                existing.Add((type, step));
            }
        }
        if (drift.Coverage.ExcludedHiddenSteps > 0)
            drift.Coverage.Unsupported.Add(new("inventory", $"{drift.Coverage.ExcludedHiddenSteps} hidden steps excluded; use --include-hidden"));

        // A known hidden counterpart is excluded, not missing. The inventory remains incomplete.
        var visibleIdentities = existing.Select(e => PluginStepIdentity.FromEnvironment(e.Type.TypeName, e.Step)).ToHashSet();
        var relevantConfigured = configured.Where(c =>
        {
            var id = PluginStepIdentity.FromConfig(c.Type.TypeName, c.Step);
            return !hiddenIdentities.Contains(id) || visibleIdentities.Contains(id);
        });
        var legacyInputs = new List<LegacyPreservationInput>();
        var comparedOmissions = ComparedOmissionKinds.None;
        var matches = PluginStepMatcher.Match(relevantConfigured, existing, (id, configCount, envCount) =>
        {
            drift.Warnings.Add(PluginStepMatcher.DescribeResidualCollision(id, configCount, envCount));
            drift.Coverage.Unsupported.Add(new("identity", "ambiguous functional identity; positional pairing cannot establish equality"));
        });
        foreach (var match in matches)
        {
            ct.ThrowIfCancellationRequested();
            if (match.IsMissing)
            {
                var step = match.Config!;
                drift.MissingSteps.Add(StepDrift.FromConfig(match.TypeConfig!, step));
                if (PluginStepIdentity.IsEntitySpecified(step.Entity) || PluginStepIdentity.IsEntitySpecified(step.SecondaryEntity))
                {
                    var messageId = await registration.GetSdkMessageIdAsync(step.Message, ct);
                    ct.ThrowIfCancellationRequested();
                    if (messageId is not null && await registration.GetSdkMessageFilterIdAsync(messageId.Value, step.Entity, step.SecondaryEntity, ct) is null)
                        drift.Warnings.Add($"Missing step cannot be created by deploy: {PluginRegistrationService.DescribeMissingMessageFilter(step.Message, step.Entity, step.SecondaryEntity)}");
                }
                continue;
            }
            if (match.IsOrphaned)
            {
                drift.OrphanedSteps.Add(StepDrift.FromEnvironment(match.EnvType!, match.Env!));
                continue;
            }

            var stepConfig = match.Config!;
            var stepInfo = match.Env!;
            var name = PluginStepMatcher.ResolveConfigName(match.TypeConfig!, stepConfig);
            var path = StepPath(match.TypeConfig!, stepConfig);
            var desired = PluginRegistrationNormalizer.NormalizeStep(stepConfig, DesiredIntent(stepConfig));
            var changes = new List<PropertyChange>();
            drift.Coverage.Checked.Add(new(path, "typeName"));
            foreach (var field in IdentityFields)
            {
                if (stepInfo.UnavailableProperties.Contains(field))
                    drift.Coverage.Unsupported.Add(new(path, $"{field} unavailable"));
                else
                    drift.Coverage.Checked.Add(new(path, field));
            }
            Check("name", name, stepInfo.Name, path, stepInfo.UnavailableProperties, changes, drift.Coverage);
            foreach (var (field, expectedValue) in desired)
            {
                ct.ThrowIfCancellationRequested();
                if (IdentityFields.Contains(field) || field is "name" or "secureConfiguration")
                    continue;
                object? expected = expectedValue;
                if (field == "asyncAutoDelete" && !string.Equals(stepInfo.Mode, "Asynchronous", StringComparison.OrdinalIgnoreCase))
                {
                    drift.Coverage.Excluded.Add(new(path, "asyncAutoDelete not applicable to synchronous step"));
                    continue;
                }
                // One stored-value mapping is shared with the legacy preservation advisory.
                if (!PluginStoredValues.TryStep(field, stepInfo, out var actual))
                {
                    drift.Coverage.Unsupported.Add(new(path, field));
                    continue;
                }
                if (field == "runAsUser")
                    expected = await registration.ResolveRunAsUserAsync((string?)expectedValue, ct);
                if (actual is null && field is "canBeBypassed" or "canUseReadOnlyConnection" or "invocationSource")
                {
                    drift.Coverage.Unsupported.Add(new(path, $"{field} unavailable"));
                    continue;
                }
                Check(field, expected, actual, path, stepInfo.UnavailableProperties, changes, drift.Coverage,
                    redact: field == "unsecureConfiguration");
            }
            // Missing/1.0 parity with 1.4.2: an omitted filteringAttributes is still compared against none.
            foreach (var field in PluginRegistrationNormalizer.LegacyComparedOmissions(stepConfig))
            {
                PluginStoredValues.TryStep(field, stepInfo, out var actual);
                if (Check(field, null, actual, path, stepInfo.UnavailableProperties, changes, drift.Coverage))
                    comparedOmissions |= PluginRegistrationNormalizer.SupportsFilteringAttributes(stepConfig.Message)
                        ? ComparedOmissionKinds.FilterableStep : ComparedOmissionKinds.OtherStep;
            }
            if (changes.Count > 0)
                drift.ModifiedSteps.Add(new() { TypeName = match.TypeConfig!.TypeName, StepName = name, Changes = changes });

            var images = await registration.ListImagesForStepAsync(stepInfo.Id, ct);
            if (DesiredIntent(stepConfig) != RegistrationOperationIntent.Reconcile)
                legacyInputs.Add(new(PluginLegacyPreservationService.StepPath(config.Name, match.TypeConfig!.TypeName, name),
                    stepConfig, stepInfo, images));
            foreach (var image in images.Where(i => i.UnavailableProperties.Contains("name")))
                drift.Coverage.Unsupported.Add(new($"{path}/image:{image.Id}", "image name unavailable"));
            // One pairing rule shared with deploy and pruning: names differing only by case are the same image.
            // Configured collisions were rejected above. Stored rows whose names collide are ambiguous only when
            // a configured image would pair with them; colliding rows that are not configured are plain orphans.
            var desiredNames = stepConfig.Images.Select(i => i.Name).ToHashSet(PluginImageNames.Comparer);
            var storedCollisions = images.GroupBy(i => i.Name, PluginImageNames.Comparer).Where(g => g.Count() > 1).ToList();
            var ambiguousNames = new HashSet<string>(PluginImageNames.Comparer);
            foreach (var group in storedCollisions.Where(g => desiredNames.Contains(g.Key)))
            {
                ambiguousNames.Add(group.Key);
                var configuredName = stepConfig.Images.First(i => PluginImageNames.Same(i.Name, group.Key)).Name;
                drift.Coverage.Unsupported.Add(new(path,
                    "duplicate image names prevent unambiguous comparison: in the environment, stored images " +
                    string.Join(", ", group.Select(i => $"'{i.Name}' ({i.Id:D})")) +
                    $" on this step are equal ignoring case to configured image '{configuredName}'. " +
                    "Remove the extra image with 'ppds plugins unregister image <image-id>'."));
            }
            var collidingNames = storedCollisions.Select(g => g.Key).ToHashSet(PluginImageNames.Comparer);
            var byName = images.Where(i => !collidingNames.Contains(i.Name)).ToDictionary(i => i.Name, PluginImageNames.Comparer);
            foreach (var image in images.Where(i => !desiredNames.Contains(i.Name)))
            {
                ct.ThrowIfCancellationRequested();
                drift.OrphanedImages.Add(new() { StepName = name, ImageName = image.Name });
            }
            foreach (var image in stepConfig.Images)
            {
                ct.ThrowIfCancellationRequested();
                if (ambiguousNames.Contains(image.Name)) continue;
                if (!byName.TryGetValue(image.Name, out var envImage))
                {
                    drift.MissingImages.Add(new() { StepName = name, ImageName = image.Name });
                    continue;
                }
                var imagePath = $"{path}/image:{image.Name}";
                var imageChanges = new List<PropertyChange>();
                // Paired case-insensitively; the normalized fields include "name", compared exactly, so a case-only
                // difference is a modified-image name change (deploy writes the configured name to this row).
                foreach (var (field, expected) in PluginRegistrationNormalizer.NormalizeImage(image, stepConfig.Message, DesiredIntent(stepConfig)))
                {
                    ct.ThrowIfCancellationRequested();
                    PluginStoredValues.TryImage(field, envImage, out var actual);
                    Check(field, expected, actual, imagePath, envImage.UnavailableProperties, imageChanges, drift.Coverage);
                }
                // Missing/1.0 parity with 1.4.2: omitted image attributes are still compared against none.
                foreach (var field in PluginRegistrationNormalizer.LegacyComparedOmissions(stepConfig, image))
                {
                    PluginStoredValues.TryImage(field, envImage, out var actual);
                    if (Check(field, null, actual, imagePath, envImage.UnavailableProperties, imageChanges, drift.Coverage))
                        comparedOmissions |= ComparedOmissionKinds.Image;
                }
                if (imageChanges.Count > 0)
                    drift.ModifiedImages.Add(new() { StepName = name, ImageName = image.Name, Changes = imageChanges });
            }
        }
        ct.ThrowIfCancellationRequested();
        if (comparedOmissions != ComparedOmissionKinds.None)
        {
            drift.LegacyComparedOmissions = comparedOmissions;
            drift.ComparedOmissionWarning = PluginLegacyPreservationService.DescribeComparedOmissionWarning(comparedOmissions);
            drift.Warnings.Add(drift.ComparedOmissionWarning);
        }
        // Advisory only, computed after the comparison from rows already read; a failure omits it.
        // Values compared above are drift, so they are not repeated in the advisory.
        drift.PreservedLegacyValues = PluginLegacyPreservationService.TryCollect(legacyInputs, out _,
            excludeComparedOmissions: true);
        return drift;
    }

    private static RegistrationOperationIntent DesiredIntent(PluginConfigurationPresence config) =>
        config.OperationIntent == RegistrationOperationIntent.Reconcile
            ? RegistrationOperationIntent.Reconcile : RegistrationOperationIntent.Deploy;

    private static string StepPath(PluginTypeConfig type, PluginStepConfig step) =>
        $"type:{type.TypeName}/step:{PluginStepMatcher.ResolveConfigName(type, step)}";

    /// <returns>True when a change was recorded.</returns>
    private static bool Check(string field, object? expected, object? actual, string path,
        HashSet<string> unavailable, List<PropertyChange> changes, ComparisonCoverage coverage, bool redact = false)
    {
        if (unavailable.Contains(field))
        {
            coverage.Unsupported.Add(new(path, $"{field} unavailable"));
            return false;
        }
        coverage.Checked.Add(new(path, field));
        if (Equals(expected, actual)) return false;
        changes.Add(new(field, redact ? "(redacted)" : PluginStoredValues.Format(expected),
            redact ? "(redacted)" : PluginStoredValues.Format(actual)));
        return true;
    }

    private static void Unknown(Dictionary<string, JsonElement>? extensions, string path, ComparisonCoverage coverage)
    {
        if (extensions is null) return;
        foreach (var key in extensions.Keys)
        {
            // Preserve extension data, identify the unsupported field, and never emit its value.
            coverage.Unsupported.Add(new(path, $"unknown registration property '{key}'"));
        }
    }
}

internal sealed class PluginDiffResult
{
    public List<AssemblyDrift> Assemblies { get; } = [];
    public ComparisonCoverage Coverage { get; } = new();
    public bool HasDrift => Assemblies.Any(a => a.HasDrift);
    public bool IsComplete => Coverage.Unsupported.Count == 0 && Assemblies.All(a => a.IsComplete);
    public string Status => !IsComplete ? "incomplete" : HasDrift ? "drift" : "match";
    [JsonIgnore] public int ExitCode => !IsComplete ? ExitCodes.Failure : HasDrift ? ExitCodes.PartialSuccess : ExitCodes.Success;
}

internal sealed class ComparisonCoverage
{
    public List<CoverageItem> Checked { get; } = [];
    public List<CoverageItem> Unmanaged { get; } = [];
    public List<CoverageItem> Unsupported { get; } = [];
    public List<CoverageItem> Excluded { get; } = [];
    public int TotalSteps { get; set; }
    public int ComparedSteps { get; set; }
    public int HiddenSteps { get; set; }
    public int UnknownHiddenStatusSteps { get; set; }
    public int ExcludedHiddenSteps { get; set; }
}
internal sealed record CoverageItem(string Path, string Field);

internal sealed class AssemblyDrift
{
    public string AssemblyName { get; set; } = "";
    public bool AssemblyMissing { get; set; }
    public List<StepDrift> MissingSteps { get; set; } = [];
    public List<StepDrift> OrphanedSteps { get; set; } = [];
    public List<ModifiedStepDrift> ModifiedSteps { get; set; } = [];
    public List<ImageDrift> MissingImages { get; set; } = [];
    public List<ImageDrift> OrphanedImages { get; set; } = [];
    public List<ModifiedImageDrift> ModifiedImages { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
    public ComparisonCoverage Coverage { get; } = new();
    /// <summary>
    /// Legacy (missing/1.0) files only: omitted optional values that are preserved and not compared,
    /// but differ from what registration version 2.0 would write. Advisory; never drift or incompleteness.
    /// Omitted filteringAttributes and image attributes are compared (drift) and so are not listed here.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<PreservedLegacyValue>? PreservedLegacyValues { get; set; }
    /// <summary>
    /// Which missing/1.0 unset filteringAttributes or image attributes differed from none and were reported
    /// as drift; when any, <see cref="Warnings"/> also carries <see cref="ComparedOmissionWarning"/>.
    /// </summary>
    [JsonIgnore]
    public ComparedOmissionKinds LegacyComparedOmissions { get; set; }
    /// <summary>The resolution guidance added to <see cref="Warnings"/>; text output prints it once per run instead.</summary>
    [JsonIgnore]
    public string? ComparedOmissionWarning { get; set; }
    public bool HasDrift => AssemblyMissing || MissingSteps.Count > 0 || OrphanedSteps.Count > 0 ||
        ModifiedSteps.Count > 0 || MissingImages.Count > 0 || OrphanedImages.Count > 0 || ModifiedImages.Count > 0;
    public bool IsComplete => Coverage.Unsupported.Count == 0;
    public string Status => !IsComplete ? "incomplete" : HasDrift ? "drift" : "match";
}
internal sealed class StepDrift
{
    public string TypeName { get; set; } = "";
    public string StepName { get; set; } = "";
    public string Message { get; set; } = "";
    public string Entity { get; set; } = "";
    public string? SecondaryEntity { get; set; }
    public string Stage { get; set; } = "";
    public string Mode { get; set; } = "";
    internal static StepDrift FromConfig(PluginTypeConfig type, PluginStepConfig step) => new()
    {
        TypeName = type.TypeName, StepName = PluginStepMatcher.ResolveConfigName(type, step),
        Message = step.Message, Entity = step.Entity, SecondaryEntity = step.SecondaryEntity, Stage = step.Stage, Mode = step.Mode
    };
    internal static StepDrift FromEnvironment(PluginTypeInfo type, PluginStepInfo step) => new()
    {
        TypeName = type.TypeName, StepName = step.Name, Message = step.Message, Entity = step.PrimaryEntity,
        SecondaryEntity = step.SecondaryEntity, Stage = step.Stage, Mode = step.Mode
    };
}
internal sealed class ModifiedStepDrift
{
    public string TypeName { get; set; } = "";
    public string StepName { get; set; } = "";
    public List<PropertyChange> Changes { get; set; } = [];
}
internal sealed class ImageDrift
{
    public string StepName { get; set; } = "";
    public string ImageName { get; set; } = "";
}
internal sealed class ModifiedImageDrift
{
    public string StepName { get; set; } = "";
    public string ImageName { get; set; } = "";
    public List<PropertyChange> Changes { get; set; } = [];
}
internal sealed record PropertyChange(string Property, string Expected, string Actual);

/// <summary>Observed deployment target; comparison never falls back to a same-named assembly.</summary>
internal sealed record PluginAssemblyIdentity(Guid AssemblyId, Guid? PackageId);
