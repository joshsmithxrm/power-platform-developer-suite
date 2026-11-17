using System.Text.Json.Serialization;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Plugins.Models;
using PPDS.Cli.Plugins.Registration;

namespace PPDS.Cli.Services.Plugins;

/// <summary>
/// Read-only advisory for legacy (missing/1.0) registration files. Those files preserve omitted
/// optional properties on update. Diff does not compare them, except an omitted step filteringAttributes
/// or image attributes, which it compares against none as PPDS 1.4.2 did (drift, not advisory). This
/// reports the stored values that registration version 2.0 would change, using records the caller
/// already retrieved. It never changes what a legacy file writes, compares or returns as an exit code.
/// </summary>
internal static class PluginLegacyPreservationService
{
    internal const string Redacted = "(redacted)";

    /// <summary>One line pointing at the opt-in contract; shared by diff and deploy output.</summary>
    internal const string AdoptionGuidance =
        "Registration version 2.0 manages these properties: an omitted value is reset to its PPDS default on deploy and compared by diff. " +
        "To adopt it, set \"version\": \"2.0\" in the registration file (declare any value you want to keep first), or regenerate with " +
        "'ppds plugins extract --input <assembly-or-package> --registration-version 2.0' (2.0 extraction uses source attribute values and " +
        "does not inherit earlier file overrides). Under 2.0, --clean removes only owned extra images on configured steps and no longer deletes orphaned steps.";

    /// <summary>
    /// Opening of the guidance for drift that diff reports for an unset filteringAttributes or image
    /// attributes under a missing/1.0 file (compared as PPDS 1.4.2 did, while 1.0 deploy preserves the value).
    /// </summary>
    internal const string ComparedOmissionIntro =
        "Registration version 1.0 still compares step filteringAttributes and image attributes when the file omits them " +
        "or gives them a null or empty value, as PPDS 1.4.2 did. A change for these shown as '(none) -> <value>' is a stored value " +
        "that is not in the registration file; deploy under version 1.0 preserves it, so the drift remains after deploy. To resolve it:";

    // resetFields is rejected while the same property is present, even as null or empty, so each option says to remove it.
    internal const string FilterableStepOption =
        " for a step on Update or UpdateMultiple, either set filteringAttributes in the file to match the stored value, or remove " +
        "filteringAttributes from the step (an explicit null or empty value must be removed too) and add " +
        "\"resetFields\": [\"filteringAttributes\"] to the step to have deploy clear it;";

    // A non-empty filteringAttributes is rejected for any other message, so "set it to match" is not offered.
    internal const string OtherStepOption =
        " for a step on any other message, filteringAttributes cannot be set, so remove it from the step if present " +
        "(including a null or empty value) and add \"resetFields\": [\"filteringAttributes\"] to the step to have deploy clear it;";

    internal const string ImageOption =
        " for an image, either set attributes in the file to match the stored value, or remove attributes from the image " +
        "(an explicit null or empty value must be removed too) and add \"resetFields\": [\"attributes\"] to the image to have " +
        "deploy clear it (a cleared image attribute list selects all attributes);";

    internal const string ComparedOmissionClosing = " or adopt registration version 2.0.";

    /// <summary>The resolution guidance, offering only the options that apply to the kinds of drift found.</summary>
    internal static string DescribeComparedOmissionGuidance(ComparedOmissionKinds kinds) =>
        ComparedOmissionIntro +
        (kinds.HasFlag(ComparedOmissionKinds.FilterableStep) ? FilterableStepOption : "") +
        (kinds.HasFlag(ComparedOmissionKinds.OtherStep) ? OtherStepOption : "") +
        (kinds.HasFlag(ComparedOmissionKinds.Image) ? ImageOption : "") +
        ComparedOmissionClosing;

    /// <summary>Per-assembly JSON warning: the resolution guidance followed by the shared adoption pointer.</summary>
    internal static string DescribeComparedOmissionWarning(ComparedOmissionKinds kinds) =>
        DescribeComparedOmissionGuidance(kinds) + " " + AdoptionGuidance;

    /// <summary>Notice path; includes the assembly so same-named steps in two assemblies stay distinct.</summary>
    internal static string StepPath(string assemblyName, string typeName, string stepName) =>
        $"assembly:{assemblyName}/type:{typeName}/step:{stepName}";

    internal static string ImagePath(string stepPath, string imageName) => $"{stepPath}/image:{imageName}";

    /// <summary>
    /// Computes the advisory for rows the caller already read. Any failure omits the advisory
    /// (returns null) so it can never change a write sequence, a comparison or an exit code.
    /// </summary>
    /// <param name="excludeComparedOmissions">
    /// Diff passes true: omitted fields it compares and reports as drift
    /// (<see cref="PluginRegistrationNormalizer.IsLegacyComparedField"/>) are not repeated here.
    /// Deploy passes false and lists every preserved value.
    /// </param>
    internal static List<PreservedLegacyValue>? TryCollect(IEnumerable<LegacyPreservationInput> inputs, out Exception? failure,
        bool excludeComparedOmissions = false)
    {
        failure = null;
        try
        {
            var values = new List<PreservedLegacyValue>();
            foreach (var input in inputs)
            {
                values.AddRange(ForStep(input.StepPath, input.Step, input.Stored, excludeComparedOmissions));
                values.AddRange(ForImages(input.StepPath, input.Step, input.StoredImages, excludeComparedOmissions));
            }
            return values.Count == 0 ? null : values;
        }
        catch (Exception ex)
        {
            failure = ex;
            return null;
        }
    }

    /// <summary>Stored step values that differ from what version 2.0 would write for omitted fields.</summary>
    internal static IReadOnlyList<PreservedLegacyValue> ForStep(string path, PluginStepConfig config, PluginStepInfo stored,
        bool excludeComparedOmissions = false)
    {
        if (config.OperationIntent == RegistrationOperationIntent.Reconcile) return [];
        var values = new List<PreservedLegacyValue>();
        foreach (var (field, version2) in PluginRegistrationNormalizer.Version2ValuesForLegacyOmissions(config))
        {
            if (excludeComparedOmissions && PluginRegistrationNormalizer.IsLegacyComparedField(field, onImage: false)) continue;
            if (stored.UnavailableProperties.Contains(field)) continue; // unknown stored state is not reported as different
            if (!PluginStoredValues.TryStep(field, stored, out var actual))
                throw new PpdsException(ErrorCodes.Operation.Internal, $"No stored-value mapping for resettable step field '{field}'.");
            switch (field)
            {
                // An omitted selector means CallingUser under 2.0. Any other selector needs resolution; skip it.
                case "runAsUser" when version2 is not null: continue;
                case "asyncAutoDelete" when !string.Equals(stored.Mode, "Asynchronous", StringComparison.OrdinalIgnoreCase): continue;
                case "canBeBypassed" or "canUseReadOnlyConnection" when actual is null: continue; // unknown, not different
            }
            if (Equals(actual, version2)) continue;
            var redact = field == "unsecureConfiguration";
            values.Add(new(path, field,
                redact ? Redacted : PluginStoredValues.Format(actual),
                redact ? Redacted : field == "runAsUser" ? "CallingUser" : PluginStoredValues.Format(version2))
            {
                ReportedAsDriftByDiff = PluginRegistrationNormalizer.IsLegacyComparedField(field, onImage: false)
            });
        }
        return values;
    }

    /// <summary>Stored image values that differ from what version 2.0 would write for omitted fields.</summary>
    internal static IReadOnlyList<PreservedLegacyValue> ForImage(string path, PluginStepConfig step, PluginImageConfig config,
        PluginImageInfo stored, bool excludeComparedOmissions = false)
    {
        if (step.OperationIntent == RegistrationOperationIntent.Reconcile ||
            config.OperationIntent == RegistrationOperationIntent.Reconcile) return [];
        var values = new List<PreservedLegacyValue>();
        foreach (var (field, version2) in PluginRegistrationNormalizer.Version2ValuesForLegacyOmissions(config, step.Message))
        {
            if (excludeComparedOmissions && PluginRegistrationNormalizer.IsLegacyComparedField(field, onImage: true)) continue;
            if (stored.UnavailableProperties.Contains(field)) continue;
            if (!PluginStoredValues.TryImage(field, stored, out var actual))
                throw new PpdsException(ErrorCodes.Operation.Internal, $"No stored-value mapping for resettable image field '{field}'.");
            if (Equals(actual, version2)) continue;
            values.Add(new(path, field, PluginStoredValues.Format(actual),
                field == "attributes" && version2 is null ? "(all attributes)" : PluginStoredValues.Format(version2))
            {
                ReportedAsDriftByDiff = PluginRegistrationNormalizer.IsLegacyComparedField(field, onImage: true)
            });
        }
        return values;
    }

    /// <summary>Stored images paired by <see cref="PluginImageNames"/> (colliding names are not compared).</summary>
    internal static IReadOnlyList<PreservedLegacyValue> ForImages(string stepPath, PluginStepConfig step,
        IEnumerable<PluginImageInfo> storedImages, bool excludeComparedOmissions = false)
    {
        if (step.OperationIntent == RegistrationOperationIntent.Reconcile) return [];
        var unique = storedImages.GroupBy(i => i.Name, PluginImageNames.Comparer).Where(g => g.Count() == 1)
            .ToDictionary(g => g.Key, g => g.Single(), PluginImageNames.Comparer);
        var values = new List<PreservedLegacyValue>();
        foreach (var image in step.Images)
            if (unique.TryGetValue(image.Name, out var stored))
                values.AddRange(ForImage(ImagePath(stepPath, image.Name), step, image, stored, excludeComparedOmissions));
        return values;
    }

    /// <summary>Text lines for the stderr notice; values are already redacted where required.</summary>
    internal static IEnumerable<string> DescribeNotice(IReadOnlyCollection<PreservedLegacyValue> values, bool includeValues) =>
        DescribeNoticeCore(values, includeValues, includeAdoption: true);

    /// <summary>
    /// Diff's stderr guidance, printed once per run: the resolution guidance when a compared legacy
    /// omission was reported as drift, then the preserved-value notice, then the adoption pointer once.
    /// </summary>
    internal static IEnumerable<string> DescribeDiffGuidance(IReadOnlyCollection<PreservedLegacyValue> values,
        ComparedOmissionKinds comparedOmissions)
    {
        var drift = comparedOmissions != ComparedOmissionKinds.None;
        if (drift) yield return "Notice: " + DescribeComparedOmissionGuidance(comparedOmissions);
        foreach (var line in DescribeNoticeCore(values, includeValues: true, includeAdoption: false))
            yield return line;
        if (drift || values.Count > 0) yield return "  " + AdoptionGuidance;
    }

    /// <summary>Deploy's heading for preserved values that <c>plugins diff</c> compares and reports as drift.</summary>
    internal const string ReportedAsDriftHeading =
        "omitted value(s) are preserved by deploy under registration version 1.0 but are compared by 'ppds plugins diff', " +
        "which reports them as drift (exit 1) and prints how to resolve it (deploy's exit code is unaffected):";

    private static IEnumerable<string> DescribeNoticeCore(IReadOnlyCollection<PreservedLegacyValue> values, bool includeValues,
        bool includeAdoption)
    {
        if (values.Count == 0) yield break;
        var advisory = values.Where(v => !v.ReportedAsDriftByDiff).ToList();
        var drift = values.Where(v => v.ReportedAsDriftByDiff).ToList();
        if (advisory.Count > 0)
        {
            yield return $"Notice: {advisory.Count} omitted optional value(s) are preserved, not managed, under registration version 1.0 " +
                "and differ from the registration version 2.0 default (not compared; exit code unaffected):";
            foreach (var line in DescribeValues(advisory, includeValues)) yield return line;
        }
        if (drift.Count > 0)
        {
            yield return $"Notice: {drift.Count} {ReportedAsDriftHeading}";
            foreach (var line in DescribeValues(drift, includeValues)) yield return line;
        }
        if (includeAdoption) yield return "  " + AdoptionGuidance;
    }

    private static IEnumerable<string> DescribeValues(IEnumerable<PreservedLegacyValue> values, bool includeValues) =>
        values.GroupBy(v => v.Path, StringComparer.Ordinal).Select(group => includeValues
            ? $"  {group.Key}: " + string.Join("; ", group.Select(v => $"{v.Property} stored {v.Stored} (2.0 default {v.Version2Value})"))
            : $"  {group.Key}: " + string.Join(", ", group.Select(v => v.Property)));
}

/// <summary>Which kinds of 1.0 compared-omission drift a comparison found; selects the guidance options.</summary>
[Flags]
internal enum ComparedOmissionKinds
{
    None = 0,
    /// <summary>filteringAttributes on an Update/UpdateMultiple step (it can be set in the file).</summary>
    FilterableStep = 1,
    /// <summary>filteringAttributes on a step of any other message (it cannot be set in the file).</summary>
    OtherStep = 2,
    /// <summary>Image attributes.</summary>
    Image = 4
}

/// <summary>Rows a caller already retrieved for one matched legacy step.</summary>
internal sealed record LegacyPreservationInput(string StepPath, PluginStepConfig Step, PluginStepInfo Stored,
    IReadOnlyList<PluginImageInfo> StoredImages);

/// <summary>A stored value kept by a legacy file that registration version 2.0 would change.</summary>
internal sealed record PreservedLegacyValue(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("property")] string Property,
    [property: JsonPropertyName("stored")] string Stored,
    [property: JsonPropertyName("version2Value")] string Version2Value)
{
    /// <summary>
    /// Missing/1.0 filteringAttributes or image attributes: preserved by deploy but compared by diff (drift).
    /// Selects deploy's notice heading only; not part of the JSON contract.
    /// </summary>
    [JsonIgnore]
    public bool ReportedAsDriftByDiff { get; init; }
}
