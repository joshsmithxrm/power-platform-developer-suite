using System.Text.Json.Serialization;

namespace PPDS.Cli.Services.Schema.Models;

/// <summary>
/// A single schema difference between source and target snapshots.
/// </summary>
public sealed record SchemaDifference
{
    /// <summary>Severity classification.</summary>
    [JsonPropertyName("severity")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public required DiffSeverity Severity { get; init; }

    /// <summary>Diff kind for programmatic handling.</summary>
    [JsonPropertyName("kind")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public required DiffKind Kind { get; init; }

    /// <summary>Entity logical name the diff applies to (null for cross-entity).</summary>
    [JsonPropertyName("entity")]
    public string? Entity { get; init; }

    /// <summary>Attribute logical name the diff applies to (null when not attribute-scoped).</summary>
    [JsonPropertyName("attribute")]
    public string? Attribute { get; init; }

    /// <summary>Human-readable description.</summary>
    [JsonPropertyName("message")]
    public required string Message { get; init; }

    /// <summary>Source-side value (for type/length/precision diffs).</summary>
    [JsonPropertyName("sourceValue")]
    public string? SourceValue { get; init; }

    /// <summary>Target-side value (for type/length/precision diffs).</summary>
    [JsonPropertyName("targetValue")]
    public string? TargetValue { get; init; }
}
