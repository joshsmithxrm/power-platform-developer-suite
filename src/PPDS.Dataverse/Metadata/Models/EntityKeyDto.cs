using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace PPDS.Dataverse.Metadata.Models;

/// <summary>
/// Represents an alternate key definition for an entity.
/// </summary>
public sealed class EntityKeyDto
{
    /// <summary>
    /// Gets the unique metadata identifier.
    /// </summary>
    [JsonPropertyName("metadataId")]
    public Guid MetadataId { get; init; }

    /// <summary>
    /// Gets the schema name of the key.
    /// </summary>
    [JsonPropertyName("schemaName")]
    public required string SchemaName { get; init; }

    /// <summary>
    /// Gets the logical name of the key.
    /// </summary>
    [JsonPropertyName("logicalName")]
    public required string LogicalName { get; init; }

    /// <summary>
    /// Gets the display name of the key.
    /// </summary>
    [JsonPropertyName("displayName")]
    public required string DisplayName { get; init; }

    /// <summary>
    /// Gets the attributes that make up this key.
    /// </summary>
    [JsonPropertyName("keyAttributes")]
    public List<string> KeyAttributes { get; init; } = [];

    /// <summary>
    /// Gets whether this is a custom key.
    /// </summary>
    [JsonPropertyName("isCustomizable")]
    public bool IsCustomizable { get; init; }

    /// <summary>
    /// Gets whether this key is part of a managed solution.
    /// </summary>
    [JsonPropertyName("isManaged")]
    public bool IsManaged { get; init; }

    /// <summary>
    /// Gets the entity key index status.
    /// </summary>
    [JsonPropertyName("entityKeyIndexStatus")]
    public string? EntityKeyIndexStatus { get; init; }
}
