using System.Text.Json.Serialization;

namespace PPDS.Auth.Profiles;

/// <summary>
/// User configuration for a specific Dataverse environment.
/// Stores label, type classification, and color override.
/// </summary>
public sealed class EnvironmentConfig
{
    /// <summary>
    /// Normalized environment URL (lowercase, trailing slash). This is the key.
    /// </summary>
    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    /// <summary>
    /// Short label for status bar and tab display (e.g., "Contoso Dev").
    /// Null means use the environment's DisplayName.
    /// </summary>
    [JsonPropertyName("label")]
    public string? Label { get; set; }

    /// <summary>
    /// User-configured environment type override.
    /// Drives protection levels and default color theming.
    /// Null means auto-detect from DiscoveredType or URL heuristics.
    /// </summary>
    [JsonPropertyName("type")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public EnvironmentType? Type { get; set; }

    /// <summary>
    /// Raw environment type from the Discovery API (e.g., "Sandbox", "Developer", "Production").
    /// Stored separately from user Type override. Not user-editable.
    /// </summary>
    [JsonPropertyName("discovered_type")]
    public string? DiscoveredType { get; set; }

    /// <summary>
    /// Explicit color override for this specific environment.
    /// Takes priority over type-based color. Null means use type default.
    /// </summary>
    [JsonPropertyName("color")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public EnvironmentColor? Color { get; set; }

    /// <summary>
    /// Per-environment query safety settings (DML thresholds, execution options).
    /// Null means use defaults for all settings.
    /// </summary>
    [JsonPropertyName("safety_settings")]
    public QuerySafetySettings? SafetySettings { get; set; }

    /// <summary>
    /// Explicit protection level override. Null means auto-detect from Type.
    /// </summary>
    [JsonPropertyName("protection")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ProtectionLevel? Protection { get; set; }

    /// <summary>
    /// Normalizes a URL for use as a lookup key (lowercase, ensures trailing slash).
    /// </summary>
    public static string NormalizeUrl(string url)
    {
        var normalized = url.Trim().ToLowerInvariant();
        if (!normalized.EndsWith('/'))
            normalized += '/';
        return normalized;
    }
}
