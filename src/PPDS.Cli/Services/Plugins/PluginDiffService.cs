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
                foreach (var field in OptionalStepFields.Where(f => !managed.ContainsKey(f)))
                    drift.Coverage.Unmanaged.Add(new(path, field));
                drift.Coverage.Excluded.Add(new(path, "secureConfiguration (value never read or returned)"));
                if (step.SecureConfiguration is not null)
                    drift.Coverage.Unsupported.Add(new(path, "secureConfiguration equality is unknown"));
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
                    foreach (var field in OptionalImageFields.Where(f => !managedImage.ContainsKey(f)))
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
                object? actual;
                switch (field)
                {
                    case "executionOrder": actual = stepInfo.ExecutionOrder; break;
                    case "enabled": actual = stepInfo.IsEnabled; break;
                    case "deployment": actual = stepInfo.Deployment; break;
                    case "invocationSource": actual = stepInfo.InvocationSource; break;
                    case "filteringAttributes": actual = NormalizeAttributes(stepInfo.FilteringAttributes); break;
                    case "unsecureConfiguration": actual = EmptyToNull(stepInfo.Configuration); break;
                    case "description": actual = EmptyToNull(stepInfo.Description); break;
                    case "asyncAutoDelete":
                        if (!string.Equals(stepInfo.Mode, "Asynchronous", StringComparison.OrdinalIgnoreCase))
                        {
                            drift.Coverage.Excluded.Add(new(path, "asyncAutoDelete not applicable to synchronous step"));
                            continue;
                        }
                        actual = stepInfo.AsyncAutoDelete; break;
                    case "canBeBypassed": actual = stepInfo.CanBeBypassed; break;
                    case "canUseReadOnlyConnection": actual = stepInfo.CanUseReadOnlyConnection; break;
                    case "runAsUser":
                        expected = await registration.ResolveRunAsUserAsync((string?)expectedValue, ct);
                        actual = stepInfo.ImpersonatingUserId; break;
                    default:
                        drift.Coverage.Unsupported.Add(new(path, field));
                        continue;
                }
                if (actual is null && field is "canBeBypassed" or "canUseReadOnlyConnection" or "invocationSource")
                {
                    drift.Coverage.Unsupported.Add(new(path, $"{field} unavailable"));
                    continue;
                }
                Check(field, expected, actual, path, stepInfo.UnavailableProperties, changes, drift.Coverage,
                    redact: field == "unsecureConfiguration");
            }
            if (changes.Count > 0)
                drift.ModifiedSteps.Add(new() { TypeName = match.TypeConfig!.TypeName, StepName = name, Changes = changes });

            var images = await registration.ListImagesForStepAsync(stepInfo.Id, ct);
            foreach (var image in images.Where(i => i.UnavailableProperties.Contains("name")))
                drift.Coverage.Unsupported.Add(new($"{path}/image:{image.Id}", "image name unavailable"));
            var ambiguousNames = images.GroupBy(i => i.Name, StringComparer.Ordinal)
                .Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
            if (ambiguousNames.Count > 0)
            {
                drift.Coverage.Unsupported.Add(new(path, "duplicate image names prevent unambiguous comparison"));
            }
            var byName = images.Where(i => !ambiguousNames.Contains(i.Name)).ToDictionary(i => i.Name, StringComparer.Ordinal);
            var desiredNames = stepConfig.Images.Select(i => i.Name).ToHashSet(StringComparer.Ordinal);
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
                foreach (var (field, expected) in PluginRegistrationNormalizer.NormalizeImage(image, stepConfig.Message, DesiredIntent(stepConfig)))
                {
                    ct.ThrowIfCancellationRequested();
                    object? actual = field switch
                    {
                        "name" => envImage.Name,
                        "imageType" => envImage.ImageType,
                        "attributes" => NormalizeAttributes(envImage.Attributes),
                        "entityAlias" => envImage.EntityAlias,
                        "messagePropertyName" => envImage.MessagePropertyName,
                        "description" => EmptyToNull(envImage.Description),
                        _ => null
                    };
                    Check(field, expected, actual, imagePath, envImage.UnavailableProperties, imageChanges, drift.Coverage);
                }
                if (imageChanges.Count > 0)
                    drift.ModifiedImages.Add(new() { StepName = name, ImageName = image.Name, Changes = imageChanges });
            }
        }
        ct.ThrowIfCancellationRequested();
        return drift;
    }

    private static RegistrationOperationIntent DesiredIntent(PluginConfigurationPresence config) =>
        config.OperationIntent == RegistrationOperationIntent.Reconcile
            ? RegistrationOperationIntent.Reconcile : RegistrationOperationIntent.Deploy;

    private static string StepPath(PluginTypeConfig type, PluginStepConfig step) =>
        $"type:{type.TypeName}/step:{PluginStepMatcher.ResolveConfigName(type, step)}";

    private static object? EmptyToNull(string? value) => string.IsNullOrEmpty(value) ? null : value;

    private static object? NormalizeAttributes(string? value) => PluginRegistrationNormalizer.NormalizeAttributes(value);

    private static void Check(string field, object? expected, object? actual, string path,
        HashSet<string> unavailable, List<PropertyChange> changes, ComparisonCoverage coverage, bool redact = false)
    {
        if (unavailable.Contains(field))
        {
            coverage.Unsupported.Add(new(path, $"{field} unavailable"));
            return;
        }
        coverage.Checked.Add(new(path, field));
        if (!Equals(expected, actual))
            changes.Add(new(field, redact ? "(redacted)" : Format(expected), redact ? "(redacted)" : Format(actual)));
    }

    private static string Format(object? value) => value switch
    {
        null => "(none)",
        bool boolean => boolean ? "true" : "false",
        IFormattable formatted => formatted.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "(none)"
    };

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
