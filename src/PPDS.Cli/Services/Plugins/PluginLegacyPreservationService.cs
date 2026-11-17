using System.Text.Json.Serialization;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Plugins.Models;
using PPDS.Cli.Plugins.Registration;

namespace PPDS.Cli.Services.Plugins;

/// <summary>
/// Read-only advisory for legacy (missing/1.0) registration files. Those files preserve omitted
/// optional properties on update and diff does not compare them. This reports the stored values
/// that registration version 2.0 would change, using records the caller already retrieved.
/// It never changes what a legacy file writes, compares or returns as an exit code.
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

    /// <summary>Notice path; includes the assembly so same-named steps in two assemblies stay distinct.</summary>
    internal static string StepPath(string assemblyName, string typeName, string stepName) =>
        $"assembly:{assemblyName}/type:{typeName}/step:{stepName}";

    internal static string ImagePath(string stepPath, string imageName) => $"{stepPath}/image:{imageName}";

    /// <summary>
    /// Computes the advisory for rows the caller already read. Any failure omits the advisory
    /// (returns null) so it can never change a write sequence, a comparison or an exit code.
    /// </summary>
    internal static List<PreservedLegacyValue>? TryCollect(IEnumerable<LegacyPreservationInput> inputs, out Exception? failure)
    {
        failure = null;
        try
        {
            var values = new List<PreservedLegacyValue>();
            foreach (var input in inputs)
            {
                values.AddRange(ForStep(input.StepPath, input.Step, input.Stored));
                values.AddRange(ForImages(input.StepPath, input.Step, input.StoredImages));
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
    internal static IReadOnlyList<PreservedLegacyValue> ForStep(string path, PluginStepConfig config, PluginStepInfo stored)
    {
        if (config.OperationIntent == RegistrationOperationIntent.Reconcile) return [];
        var values = new List<PreservedLegacyValue>();
        foreach (var (field, version2) in PluginRegistrationNormalizer.Version2ValuesForLegacyOmissions(config))
        {
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
                redact ? Redacted : field == "runAsUser" ? "CallingUser" : PluginStoredValues.Format(version2)));
        }
        return values;
    }

    /// <summary>Stored image values that differ from what version 2.0 would write for omitted fields.</summary>
    internal static IReadOnlyList<PreservedLegacyValue> ForImage(string path, PluginStepConfig step, PluginImageConfig config,
        PluginImageInfo stored)
    {
        if (step.OperationIntent == RegistrationOperationIntent.Reconcile ||
            config.OperationIntent == RegistrationOperationIntent.Reconcile) return [];
        var values = new List<PreservedLegacyValue>();
        foreach (var (field, version2) in PluginRegistrationNormalizer.Version2ValuesForLegacyOmissions(config, step.Message))
        {
            if (stored.UnavailableProperties.Contains(field)) continue;
            if (!PluginStoredValues.TryImage(field, stored, out var actual))
                throw new PpdsException(ErrorCodes.Operation.Internal, $"No stored-value mapping for resettable image field '{field}'.");
            if (Equals(actual, version2)) continue;
            values.Add(new(path, field, PluginStoredValues.Format(actual),
                field == "attributes" && version2 is null ? "(all attributes)" : PluginStoredValues.Format(version2)));
        }
        return values;
    }

    /// <summary>Stored images that can be paired by exact name (duplicate names are not compared).</summary>
    internal static IReadOnlyList<PreservedLegacyValue> ForImages(string stepPath, PluginStepConfig step,
        IEnumerable<PluginImageInfo> storedImages)
    {
        if (step.OperationIntent == RegistrationOperationIntent.Reconcile) return [];
        var unique = storedImages.GroupBy(i => i.Name, StringComparer.Ordinal).Where(g => g.Count() == 1)
            .ToDictionary(g => g.Key, g => g.Single(), StringComparer.Ordinal);
        var values = new List<PreservedLegacyValue>();
        foreach (var image in step.Images)
            if (unique.TryGetValue(image.Name, out var stored))
                values.AddRange(ForImage(ImagePath(stepPath, image.Name), step, image, stored));
        return values;
    }

    /// <summary>Text lines for the stderr notice; values are already redacted where required.</summary>
    internal static IEnumerable<string> DescribeNotice(IReadOnlyCollection<PreservedLegacyValue> values, bool includeValues)
    {
        if (values.Count == 0) yield break;
        yield return $"Notice: {values.Count} omitted optional value(s) are preserved, not managed, under registration version 1.0 " +
            "and differ from the registration version 2.0 default (not compared; exit code unaffected):";
        foreach (var group in values.GroupBy(v => v.Path, StringComparer.Ordinal))
            yield return includeValues
                ? $"  {group.Key}: " + string.Join("; ", group.Select(v => $"{v.Property} stored {v.Stored} (2.0 default {v.Version2Value})"))
                : $"  {group.Key}: " + string.Join(", ", group.Select(v => v.Property));
        yield return "  " + AdoptionGuidance;
    }
}

/// <summary>Rows a caller already retrieved for one matched legacy step.</summary>
internal sealed record LegacyPreservationInput(string StepPath, PluginStepConfig Step, PluginStepInfo Stored,
    IReadOnlyList<PluginImageInfo> StoredImages);

/// <summary>A stored value kept by a legacy file that registration version 2.0 would change.</summary>
internal sealed record PreservedLegacyValue(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("property")] string Property,
    [property: JsonPropertyName("stored")] string Stored,
    [property: JsonPropertyName("version2Value")] string Version2Value);
