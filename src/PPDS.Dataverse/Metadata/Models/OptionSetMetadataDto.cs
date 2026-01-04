using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace PPDS.Dataverse.Metadata.Models;

/// <summary>
/// Full option set metadata including all values.
/// </summary>
public sealed class OptionSetMetadataDto
{
    /// <summary>
    /// Gets the unique metadata identifier.
    /// </summary>
    [JsonPropertyName("metadataId")]
    public Guid MetadataId { get; init; }

    /// <summary>
    /// Gets the option set name.
    /// </summary>
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>
    /// Gets the option set display name.
    /// </summary>
    [JsonPropertyName("displayName")]
    public required string DisplayName { get; init; }

    /// <summary>
    /// Gets the option set type (Picklist, State, Status, Boolean).
    /// </summary>
    [JsonPropertyName("optionSetType")]
    public required string OptionSetType { get; init; }

    /// <summary>
    /// Gets whether this option set is global.
    /// </summary>
    [JsonPropertyName("isGlobal")]
    public bool IsGlobal { get; init; }

    /// <summary>
    /// Gets whether this option set is custom.
    /// </summary>
    [JsonPropertyName("isCustomOptionSet")]
    public bool IsCustomOptionSet { get; init; }

    /// <summary>
    /// Gets whether this option set is part of a managed solution.
    /// </summary>
    [JsonPropertyName("isManaged")]
    public bool IsManaged { get; init; }

    /// <summary>
    /// Gets the description of the option set.
    /// </summary>
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>
    /// Gets the external type name for virtual entity integration.
    /// </summary>
    [JsonPropertyName("externalTypeName")]
    public string? ExternalTypeName { get; init; }

    /// <summary>
    /// Gets the option values.
    /// </summary>
    [JsonPropertyName("options")]
    public List<OptionValueDto> Options { get; init; } = [];
}
