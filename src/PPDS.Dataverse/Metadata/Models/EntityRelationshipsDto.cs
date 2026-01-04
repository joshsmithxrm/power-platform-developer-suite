using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace PPDS.Dataverse.Metadata.Models;

/// <summary>
/// Container for all relationship types for an entity.
/// </summary>
public sealed class EntityRelationshipsDto
{
    /// <summary>
    /// Gets the entity logical name.
    /// </summary>
    [JsonPropertyName("entityLogicalName")]
    public required string EntityLogicalName { get; init; }

    /// <summary>
    /// Gets the one-to-many relationships where this entity is the primary (referenced) entity.
    /// </summary>
    [JsonPropertyName("oneToMany")]
    public List<RelationshipMetadataDto> OneToMany { get; init; } = [];

    /// <summary>
    /// Gets the many-to-one relationships where this entity is the related (referencing) entity.
    /// </summary>
    [JsonPropertyName("manyToOne")]
    public List<RelationshipMetadataDto> ManyToOne { get; init; } = [];

    /// <summary>
    /// Gets the many-to-many relationships.
    /// </summary>
    [JsonPropertyName("manyToMany")]
    public List<ManyToManyRelationshipDto> ManyToMany { get; init; } = [];
}
