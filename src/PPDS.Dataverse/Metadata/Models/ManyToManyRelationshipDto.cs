using System;
using System.Text.Json.Serialization;

namespace PPDS.Dataverse.Metadata.Models;

/// <summary>
/// Represents a many-to-many relationship.
/// </summary>
public sealed class ManyToManyRelationshipDto
{
    /// <summary>
    /// Gets the unique metadata identifier.
    /// </summary>
    [JsonPropertyName("metadataId")]
    public Guid MetadataId { get; init; }

    /// <summary>
    /// Gets the schema name of the relationship.
    /// </summary>
    [JsonPropertyName("schemaName")]
    public required string SchemaName { get; init; }

    /// <summary>
    /// Gets the intersect entity logical name.
    /// </summary>
    [JsonPropertyName("intersectEntityName")]
    public required string IntersectEntityName { get; init; }

    /// <summary>
    /// Gets the first entity logical name.
    /// </summary>
    [JsonPropertyName("entity1LogicalName")]
    public required string Entity1LogicalName { get; init; }

    /// <summary>
    /// Gets the first entity intersect attribute.
    /// </summary>
    [JsonPropertyName("entity1IntersectAttribute")]
    public required string Entity1IntersectAttribute { get; init; }

    /// <summary>
    /// Gets the first entity navigation property name.
    /// </summary>
    [JsonPropertyName("entity1NavigationPropertyName")]
    public string? Entity1NavigationPropertyName { get; init; }

    /// <summary>
    /// Gets the second entity logical name.
    /// </summary>
    [JsonPropertyName("entity2LogicalName")]
    public required string Entity2LogicalName { get; init; }

    /// <summary>
    /// Gets the second entity intersect attribute.
    /// </summary>
    [JsonPropertyName("entity2IntersectAttribute")]
    public required string Entity2IntersectAttribute { get; init; }

    /// <summary>
    /// Gets the second entity navigation property name.
    /// </summary>
    [JsonPropertyName("entity2NavigationPropertyName")]
    public string? Entity2NavigationPropertyName { get; init; }

    /// <summary>
    /// Gets whether this is a custom relationship.
    /// </summary>
    [JsonPropertyName("isCustomRelationship")]
    public bool IsCustomRelationship { get; init; }

    /// <summary>
    /// Gets whether this relationship is part of a managed solution.
    /// </summary>
    [JsonPropertyName("isManaged")]
    public bool IsManaged { get; init; }

    /// <summary>
    /// Gets the security types for this relationship.
    /// </summary>
    [JsonPropertyName("securityTypes")]
    public string? SecurityTypes { get; init; }

    /// <summary>
    /// Gets whether this is a reflexive (self-referencing) relationship.
    /// </summary>
    [JsonPropertyName("isReflexive")]
    public bool IsReflexive { get; init; }
}
