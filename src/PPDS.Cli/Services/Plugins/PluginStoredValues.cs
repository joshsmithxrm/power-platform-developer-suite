using System.Globalization;
using PPDS.Cli.Plugins.Registration;

namespace PPDS.Cli.Services.Plugins;

/// <summary>
/// One mapping from a managed registration field to the normalized stored value read from Dataverse,
/// shared by diff and the legacy preservation advisory so the two cannot drift apart.
/// </summary>
internal static class PluginStoredValues
{
    /// <summary>The normalized stored value for a managed step field; false when the field has no mapping.</summary>
    internal static bool TryStep(string field, PluginStepInfo stored, out object? value)
    {
        value = field switch
        {
            "executionOrder" => stored.ExecutionOrder,
            "enabled" => stored.IsEnabled,
            "deployment" => stored.Deployment,
            "invocationSource" => stored.InvocationSource,
            "filteringAttributes" => PluginRegistrationNormalizer.NormalizeAttributes(stored.FilteringAttributes),
            "unsecureConfiguration" => EmptyToNull(stored.Configuration),
            "description" => EmptyToNull(stored.Description),
            "asyncAutoDelete" => stored.AsyncAutoDelete,
            "canBeBypassed" => stored.CanBeBypassed,
            "canUseReadOnlyConnection" => stored.CanUseReadOnlyConnection,
            "runAsUser" => stored.ImpersonatingUserId,
            _ => Unmapped
        };
        if (ReferenceEquals(value, Unmapped)) { value = null; return false; }
        return true;
    }

    /// <summary>The normalized stored value for a managed image field; false when the field has no mapping.</summary>
    internal static bool TryImage(string field, PluginImageInfo stored, out object? value)
    {
        value = field switch
        {
            "name" => stored.Name,
            "imageType" => stored.ImageType,
            "attributes" => PluginRegistrationNormalizer.NormalizeAttributes(stored.Attributes),
            "entityAlias" => stored.EntityAlias,
            "messagePropertyName" => stored.MessagePropertyName,
            "description" => EmptyToNull(stored.Description),
            _ => Unmapped
        };
        if (ReferenceEquals(value, Unmapped)) { value = null; return false; }
        return true;
    }

    internal static string Format(object? value) => value switch
    {
        null => "(none)",
        bool boolean => boolean ? "true" : "false",
        IFormattable formatted => formatted.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "(none)"
    };

    private static object? EmptyToNull(string? value) => string.IsNullOrEmpty(value) ? null : value;

    private static readonly object Unmapped = new();
}
