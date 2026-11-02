using System.Text.Json;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Plugins.Models;
using PPDS.Cli.Plugins.Registration;

namespace PPDS.Cli.Services.Plugins;

/// <summary>Preserves existing extraction settings only across unambiguous registration identities.</summary>
internal static class PluginExtractionMergeService
{
    internal static void MergeAssemblyConfig(PluginAssemblyConfig fresh, PluginRegistrationConfig existing)
    {
        try
        {
            ValidateAssembly(fresh);
            if (existing?.Assemblies is null) throw InvalidStructure();
            foreach (var assembly in existing.Assemblies) ValidateAssembly(assembly);
            MergeCore(fresh, existing);
        }
        catch (PpdsException) { throw; }
        catch (Exception ex)
        {
            // Input may be supplied by JSON or another in-process caller. Do not leak raw failures
            // or values from malformed collection/metadata objects across the service boundary.
            throw new PpdsException(ErrorCodes.Validation.InvalidValue,
                "Extraction merge configuration could not be read safely.", ex);
        }
    }

    private static void ValidateAssembly(PluginAssemblyConfig? assembly)
    {
        if (assembly is null || string.IsNullOrWhiteSpace(assembly.Name) || assembly.Types is null)
            throw InvalidStructure();
        foreach (var type in assembly.Types)
        {
            if (type is null || string.IsNullOrWhiteSpace(type.TypeName) || type.Steps is null)
                throw InvalidStructure();
            foreach (var step in type.Steps)
            {
                if (step is null || string.IsNullOrWhiteSpace(step.Message) || step.Images is null ||
                    step.Name is not null && string.IsNullOrWhiteSpace(step.Name))
                    throw InvalidStructure();
                ValidateResetList(step);
                foreach (var image in step.Images)
                {
                    if (image is null || string.IsNullOrWhiteSpace(image.Name)) throw InvalidStructure();
                    ValidateResetList(image);
                }
            }
        }
    }

    private static PpdsException InvalidStructure() => new(ErrorCodes.Validation.InvalidValue,
        "Extraction merge requires non-null registration collections and entries with valid identity names.");

    private static void MergeCore(PluginAssemblyConfig fresh, PluginRegistrationConfig existing)
    {
        var oldAssembly = Unique(existing.Assemblies.Where(a => SameComponent(a.Name, fresh.Name)),
            "assembly name");
        if (oldAssembly is null) return;

        // Resolve every pairing before applying any inherited settings. A later collision cannot
        // leave a partly merged in-memory configuration (and the command never writes on failure).
        var operations = new List<Action>();
        operations.Add(() =>
        {
            if (string.IsNullOrEmpty(fresh.Solution)) fresh.Solution = oldAssembly.Solution;
            fresh.ExtensionData = MergeMetadata(fresh.ExtensionData, oldAssembly.ExtensionData);
        });
        foreach (var freshType in fresh.Types)
        {
            var oldType = Unique(oldAssembly.Types.Where(t => SameComponent(t.TypeName, freshType.TypeName)),
                "plugin type name");
            if (oldType is null) continue;
            operations.Add(() => freshType.ExtensionData = MergeMetadata(freshType.ExtensionData, oldType.ExtensionData));

            var oldGroups = oldType.Steps.GroupBy(s => PluginStepIdentity.FromConfig(oldType.TypeName, s))
                .ToDictionary(g => g.Key, g => g.ToList());
            foreach (var group in freshType.Steps.GroupBy(s => PluginStepIdentity.FromConfig(freshType.TypeName, s)))
            {
                if (!oldGroups.TryGetValue(group.Key, out var oldSteps)) continue;
                var freshSteps = group.ToList();
                if (freshSteps.Count == 1 && oldSteps.Count == 1)
                {
                    PlanStep(freshSteps[0], oldSteps[0], operations);
                    continue;
                }

                // Full identity alone is insufficient for siblings occupying the same coordinates.
                // An exact effective name may disambiguate; rank/order/position may not.
                var used = new HashSet<PluginStepConfig>();
                var unmatched = new List<PluginStepConfig>();
                foreach (var freshStep in freshSteps)
                {
                    var name = PluginStepMatcher.ResolveConfigName(freshType, freshStep);
                    var named = oldSteps.Where(s => string.Equals(
                        PluginStepMatcher.ResolveConfigName(oldType, s), name, StringComparison.Ordinal)).ToList();
                    if (named.Count == 0)
                    {
                        unmatched.Add(freshStep);
                        continue;
                    }
                    if (named.Count != 1 || freshSteps.Count(s => string.Equals(
                        PluginStepMatcher.ResolveConfigName(freshType, s), name, StringComparison.Ordinal)) != 1)
                        throw Ambiguous("step functional identity and effective name");
                    used.Add(named[0]);
                    PlanStep(freshStep, named[0], operations);
                }
                if (unmatched.Count > 0 && used.Count < oldSteps.Count)
                    throw Ambiguous("step functional identity");
                // Remaining fresh names have no unconsumed prior candidate; preserve fresh state.
            }
        }
        foreach (var operation in operations) operation();
    }

    private static void PlanStep(PluginStepConfig fresh, PluginStepConfig existing, List<Action> operations)
    {
        operations.Add(() =>
        {
            MergeResetIntent(fresh, existing);
            if (Inherit(fresh, existing, "deployment")) fresh.Deployment = existing.Deployment;
            if (Inherit(fresh, existing, "runAsUser")) fresh.RunAsUser = existing.RunAsUser;
            if (Inherit(fresh, existing, "description")) fresh.Description = existing.Description;
            if (Inherit(fresh, existing, "asyncAutoDelete")) fresh.AsyncAutoDelete = existing.AsyncAutoDelete;
            fresh.ExtensionData = MergeMetadata(fresh.ExtensionData, existing.ExtensionData);
        });

        foreach (var image in fresh.Images)
        {
            // Image identity is exact name, consistent with deploy/diff. Never attach metadata from
            // a differently cased sibling or pick the first duplicate.
            var oldImage = Unique(existing.Images.Where(i => string.Equals(i.Name, image.Name, StringComparison.Ordinal)),
                "image name");
            if (oldImage is null) continue;
            if (fresh.Images.Count(i => string.Equals(i.Name, image.Name, StringComparison.Ordinal)) != 1)
                throw Ambiguous("fresh image name");
            operations.Add(() =>
            {
                MergeResetIntent(image, oldImage);
                image.ExtensionData = MergeMetadata(image.ExtensionData, oldImage.ExtensionData);
            });
        }
    }

    private static bool Inherit(PluginConfigurationPresence fresh, PluginConfigurationPresence existing, string field) =>
        !fresh.IsSpecified(field) && !fresh.ResetFields.Contains(field) && existing.IsSpecified(field);

    private static void MergeResetIntent(PluginConfigurationPresence fresh, PluginConfigurationPresence existing)
    {
        foreach (var field in existing.ResetFields)
            if (!fresh.IsSpecified(field) && !fresh.ResetFields.Contains(field))
                fresh.ResetFields.Add(field);
    }

    private static void ValidateResetList(PluginConfigurationPresence config)
    {
        if (config.ResetFields is null || config.ResetFields.Any(string.IsNullOrWhiteSpace))
            throw new PpdsException(ErrorCodes.Validation.InvalidValue,
                "Extraction merge requires resetFields to be an array of field names.");
    }

    private static Dictionary<string, JsonElement>? MergeMetadata(
        Dictionary<string, JsonElement>? fresh, Dictionary<string, JsonElement>? existing)
    {
        if (fresh is null && existing is null) return null;
        var merged = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (existing is not null)
            foreach (var (key, value) in existing) merged[key] = value.Clone();
        if (fresh is not null)
            foreach (var (key, value) in fresh) merged[key] = value.Clone();
        return merged;
    }

    private static T? Unique<T>(IEnumerable<T> matches, string coordinate) where T : class
    {
        var candidates = matches.Take(2).ToList();
        return candidates.Count switch { 0 => null, 1 => candidates[0], _ => throw Ambiguous(coordinate) };
    }

    private static bool SameComponent(string first, string second) =>
        string.Equals(first.Trim(), second.Trim(), StringComparison.OrdinalIgnoreCase);

    private static PpdsException Ambiguous(string coordinate) => new(ErrorCodes.Validation.InvalidValue,
        $"Extraction cannot safely merge settings: ambiguous {coordinate}. Disambiguate existing registration names before merging.");
}
