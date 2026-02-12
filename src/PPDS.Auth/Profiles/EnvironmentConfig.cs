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
    /// Environment type classification (e.g., "Production", "Sandbox", "UAT", "Gold").
    /// Free-text string — built-in types have default colors, custom types use typeDefaults.
    /// </summary>
    [JsonPropertyName("type")]
    public string? Type { get; set; }

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
