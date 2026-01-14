using System;
using System.Text.Json.Serialization;

namespace PPDS.Dataverse.Query;

/// <summary>
/// Represents a value in a query result, including both the raw value
/// and its formatted display representation.
/// </summary>
public sealed class QueryValue
{
    /// <summary>
    /// The raw value. Can be null, a primitive (string, int, bool, etc.),
    /// a Guid, a DateTime, or for lookups an EntityReference.
    /// </summary>
    [JsonPropertyName("value")]
    public object? Value { get; init; }

    /// <summary>
    /// The formatted display value as a string.
    /// For lookups, this is the display name.
    /// For option sets, this is the label.
    /// For money, this includes currency formatting.
    /// </summary>
    [JsonPropertyName("formatted")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FormattedValue { get; init; }

    /// <summary>
    /// For lookup values, the target entity logical name.
    /// </summary>
    [JsonPropertyName("entityType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LookupEntityType { get; init; }

    /// <summary>
    /// For lookup values, the target record ID.
    /// </summary>
    [JsonPropertyName("entityId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? LookupEntityId { get; init; }

    /// <summary>
    /// Creates a simple value with no formatting.
    /// </summary>
    public static QueryValue Simple(object? value) =>
        new() { Value = value };

    /// <summary>
    /// Creates a value with formatted display text.
    /// </summary>
    public static QueryValue WithFormatting(object? value, string? formatted) =>
        new() { Value = value, FormattedValue = formatted };

    /// <summary>
    /// Creates a lookup value with entity reference details.
    /// </summary>
    public static QueryValue Lookup(Guid id, string entityType, string? displayName) =>
        new()
        {
            Value = id,
            FormattedValue = displayName,
            LookupEntityType = entityType,
            LookupEntityId = id
        };

    /// <summary>
    /// Creates a null value.
    /// </summary>
    public static QueryValue Null => new() { Value = null };

    /// <summary>
    /// Returns true if this value represents a lookup (EntityReference).
    /// </summary>
    [JsonIgnore]
    public bool IsLookup => LookupEntityId.HasValue;

    /// <summary>
    /// Returns true if this value represents an optionset (integer with formatted label).
    /// </summary>
    [JsonIgnore]
    public bool IsOptionSet => Value is int && FormattedValue != null;

    /// <summary>
    /// Returns true if this value represents a boolean with formatted value.
    /// </summary>
    [JsonIgnore]
    public bool IsBoolean => Value is bool;

    /// <summary>
    /// Returns true if this value has a formatted representation that differs from the raw value.
    /// Used to determine if a *name column should be expanded.
    /// </summary>
    [JsonIgnore]
    public bool HasFormattedValue => !string.IsNullOrEmpty(FormattedValue);
}
