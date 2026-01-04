using System.Text.Json.Serialization;

namespace PPDS.Dataverse.Metadata.Models;

/// <summary>
/// Represents a single option value in an option set.
/// </summary>
public sealed class OptionValueDto
{
    /// <summary>
    /// Gets the numeric value of the option.
    /// </summary>
    [JsonPropertyName("value")]
    public int Value { get; init; }

    /// <summary>
    /// Gets the display label of the option.
    /// </summary>
    [JsonPropertyName("label")]
    public required string Label { get; init; }

    /// <summary>
    /// Gets the description of the option.
    /// </summary>
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>
    /// Gets the color associated with the option (for status options).
    /// </summary>
    [JsonPropertyName("color")]
    public string? Color { get; init; }

    /// <summary>
    /// Gets the external value for integration scenarios.
    /// </summary>
    [JsonPropertyName("externalValue")]
    public string? ExternalValue { get; init; }

    /// <summary>
    /// Gets the parent value for dependent option sets (status linked to state).
    /// </summary>
    [JsonPropertyName("parentValue")]
    public int? ParentValue { get; init; }

    /// <summary>
    /// Gets the state (0=Active, 1=Inactive) for status options.
    /// </summary>
    [JsonPropertyName("state")]
    public int? State { get; init; }

    /// <summary>
    /// Gets whether this option is the default value.
    /// </summary>
    [JsonPropertyName("isDefault")]
    public bool IsDefault { get; init; }

    /// <summary>
    /// Gets whether this option is part of a managed solution.
    /// </summary>
    [JsonPropertyName("isManaged")]
    public bool IsManaged { get; init; }
}
