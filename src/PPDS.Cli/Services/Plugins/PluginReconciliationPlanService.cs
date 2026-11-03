using System.Text.Json.Serialization;
using PPDS.Cli.Plugins.Models;

namespace PPDS.Cli.Services.Plugins;

/// <summary>Value-free preview of the same normalized field plan used for reconciliation.</summary>
internal static class PluginReconciliationPlanService
{
    internal static IReadOnlyList<PluginRegistrationFieldPlan> Describe(
        PluginAssemblyConfig assembly, CancellationToken cancellationToken = default)
    {
        var result = new List<PluginRegistrationFieldPlan>();
        for (var typeIndex = 0; typeIndex < assembly.Types.Count; typeIndex++)
        for (var stepIndex = 0; stepIndex < assembly.Types[typeIndex].Steps.Count; stepIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var step = assembly.Types[typeIndex].Steps[stepIndex];
            var path = $"types[{typeIndex}].steps[{stepIndex}]";
            Add(path, step, PluginRegistrationNormalizer.NormalizeStep(step, RegistrationOperationIntent.Reconcile));
            if (step.SecureConfiguration is not null)
                result.Add(new(path, "secureConfiguration", "write-only; equality unknown"));
            for (var imageIndex = 0; imageIndex < step.Images.Count; imageIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var image = step.Images[imageIndex];
                Add($"{path}.images[{imageIndex}]", image,
                    PluginRegistrationNormalizer.NormalizeImage(image, step.Message, RegistrationOperationIntent.Reconcile));
            }
        }
        return result;

        void Add(string path, PluginConfigurationPresence config, IReadOnlyDictionary<string, object?> fields)
        {
            foreach (var (field, value) in fields.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                var intent = field == "attributes" && value is null ? "all attributes"
                    : field == "filteringAttributes" && value is null ? "no filtering"
                    : field == "runAsUser" && value is null ? "CallingUser"
                    : value is null && field != "name" ? "clear"
                    : !config.IsSpecified(field) ? "default" : "set";
                result.Add(new(path, field, intent));
            }
        }
    }
}

internal sealed record PluginRegistrationFieldPlan(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("field")] string Field,
    [property: JsonPropertyName("intent")] string Intent);
