using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace PPDS.Auth.Profiles;

/// <summary>
/// Root object for environments.json — holds per-environment configs and custom type defaults.
/// </summary>
public sealed class EnvironmentConfigCollection
{
    /// <summary>
    /// Schema version for forward compatibility.
    /// </summary>
    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    /// <summary>
    /// Custom type definitions with default colors.
    /// Key is the EnvironmentType enum value, value is the default color.
    /// Built-in types (Production, Sandbox, Development, Test, Trial) do not need entries here
    /// unless overriding the built-in color.
    /// </summary>
    [JsonPropertyName("typeDefaults")]
    public Dictionary<EnvironmentType, EnvironmentColor> TypeDefaults { get; set; } = new();

    /// <summary>
    /// Per-environment configurations keyed by normalized URL.
    /// </summary>
    [JsonPropertyName("environments")]
    public List<EnvironmentConfig> Environments { get; set; } = new();
}
