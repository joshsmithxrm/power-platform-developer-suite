using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace PPDS.Dataverse.Metadata.Models;

/// <summary>
/// Detailed attribute metadata for entity browsing.
/// </summary>
public sealed class AttributeMetadataDto
{
    /// <summary>
    /// Gets the attribute logical name.
    /// </summary>
    [JsonPropertyName("logicalName")]
    public required string LogicalName { get; init; }

    /// <summary>
    /// Gets the attribute display name.
    /// </summary>
    [JsonPropertyName("displayName")]
    public required string DisplayName { get; init; }

    /// <summary>
    /// Gets the attribute schema name.
    /// </summary>
    [JsonPropertyName("schemaName")]
    public required string SchemaName { get; init; }

    /// <summary>
    /// Gets the attribute type (String, Integer, Lookup, etc.).
    /// </summary>
    [JsonPropertyName("attributeType")]
    public required string AttributeType { get; init; }

    /// <summary>
    /// Gets the attribute type name for virtual attributes.
    /// </summary>
    [JsonPropertyName("attributeTypeName")]
    public string? AttributeTypeName { get; init; }

    /// <summary>
    /// Gets whether this is a custom attribute.
    /// </summary>
    [JsonPropertyName("isCustomAttribute")]
    public bool IsCustomAttribute { get; init; }

    /// <summary>
    /// Gets whether this attribute is part of a managed solution.
    /// </summary>
    [JsonPropertyName("isManaged")]
    public bool IsManaged { get; init; }

    /// <summary>
    /// Gets whether this attribute is the primary ID.
    /// </summary>
    [JsonPropertyName("isPrimaryId")]
    public bool IsPrimaryId { get; init; }

    /// <summary>
    /// Gets whether this attribute is the primary name.
    /// </summary>
    [JsonPropertyName("isPrimaryName")]
    public bool IsPrimaryName { get; init; }

    /// <summary>
    /// Gets the required level (None, Recommended, ApplicationRequired, SystemRequired).
    /// </summary>
    [JsonPropertyName("requiredLevel")]
    public string? RequiredLevel { get; init; }

    /// <summary>
    /// Gets whether the attribute is valid for create.
    /// </summary>
    [JsonPropertyName("isValidForCreate")]
    public bool IsValidForCreate { get; init; }

    /// <summary>
    /// Gets whether the attribute is valid for update.
    /// </summary>
    [JsonPropertyName("isValidForUpdate")]
    public bool IsValidForUpdate { get; init; }

    /// <summary>
    /// Gets whether the attribute is valid for read.
    /// </summary>
    [JsonPropertyName("isValidForRead")]
    public bool IsValidForRead { get; init; }

    /// <summary>
    /// Gets whether the attribute is searchable.
    /// </summary>
    [JsonPropertyName("isSearchable")]
    public bool IsSearchable { get; init; }

    /// <summary>
    /// Gets whether the attribute is filterable.
    /// </summary>
    [JsonPropertyName("isFilterable")]
    public bool IsFilterable { get; init; }

    /// <summary>
    /// Gets whether the attribute is sortable.
    /// </summary>
    [JsonPropertyName("isSortable")]
    public bool IsSortable { get; init; }

    /// <summary>
    /// Gets the maximum length for string attributes.
    /// </summary>
    [JsonPropertyName("maxLength")]
    public int? MaxLength { get; init; }

    /// <summary>
    /// Gets the minimum value for numeric attributes.
    /// </summary>
    [JsonPropertyName("minValue")]
    public double? MinValue { get; init; }

    /// <summary>
    /// Gets the maximum value for numeric attributes.
    /// </summary>
    [JsonPropertyName("maxValue")]
    public double? MaxValue { get; init; }

    /// <summary>
    /// Gets the precision for decimal/money attributes.
    /// </summary>
    [JsonPropertyName("precision")]
    public int? Precision { get; init; }

    /// <summary>
    /// Gets the lookup targets for lookup attributes (pipe-delimited for polymorphic).
    /// </summary>
    [JsonPropertyName("targets")]
    public List<string>? Targets { get; init; }

    /// <summary>
    /// Gets the option set name for picklist attributes.
    /// </summary>
    [JsonPropertyName("optionSetName")]
    public string? OptionSetName { get; init; }

    /// <summary>
    /// Gets whether this is a global option set.
    /// </summary>
    [JsonPropertyName("isGlobalOptionSet")]
    public bool IsGlobalOptionSet { get; init; }

    /// <summary>
    /// Gets the date time behavior for datetime attributes.
    /// </summary>
    [JsonPropertyName("dateTimeBehavior")]
    public string? DateTimeBehavior { get; init; }

    /// <summary>
    /// Gets the format for datetime attributes.
    /// </summary>
    [JsonPropertyName("format")]
    public string? Format { get; init; }

    /// <summary>
    /// Gets the description of the attribute.
    /// </summary>
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>
    /// Gets the inline option set values for local picklist attributes.
    /// </summary>
    [JsonPropertyName("options")]
    public List<OptionValueDto>? Options { get; init; }
}
