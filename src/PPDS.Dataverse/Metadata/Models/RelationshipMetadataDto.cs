using System;
using System.Text.Json.Serialization;

namespace PPDS.Dataverse.Metadata.Models;

/// <summary>
/// Represents a one-to-many or many-to-one relationship.
/// </summary>
public sealed class RelationshipMetadataDto
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
    /// Gets the relationship type (OneToMany or ManyToOne).
    /// </summary>
    [JsonPropertyName("relationshipType")]
    public required string RelationshipType { get; init; }

    /// <summary>
    /// Gets the referenced (primary) entity logical name.
    /// </summary>
    [JsonPropertyName("referencedEntity")]
    public required string ReferencedEntity { get; init; }

    /// <summary>
    /// Gets the referenced entity navigation property name.
    /// </summary>
    [JsonPropertyName("referencedEntityNavigationPropertyName")]
    public string? ReferencedEntityNavigationPropertyName { get; init; }

    /// <summary>
    /// Gets the referenced attribute (primary key).
    /// </summary>
    [JsonPropertyName("referencedAttribute")]
    public required string ReferencedAttribute { get; init; }

    /// <summary>
    /// Gets the referencing (child) entity logical name.
    /// </summary>
    [JsonPropertyName("referencingEntity")]
    public required string ReferencingEntity { get; init; }

    /// <summary>
    /// Gets the referencing entity navigation property name.
    /// </summary>
    [JsonPropertyName("referencingEntityNavigationPropertyName")]
    public string? ReferencingEntityNavigationPropertyName { get; init; }

    /// <summary>
    /// Gets the referencing attribute (foreign key/lookup).
    /// </summary>
    [JsonPropertyName("referencingAttribute")]
    public required string ReferencingAttribute { get; init; }

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
    /// Gets whether this is a hierarchical relationship (1:N only).
    /// </summary>
    [JsonPropertyName("isHierarchical")]
    public bool IsHierarchical { get; init; }

    /// <summary>
    /// Gets the security types for this relationship.
    /// </summary>
    [JsonPropertyName("securityTypes")]
    public string? SecurityTypes { get; init; }

    /// <summary>
    /// Gets the cascade configuration for assign operations.
    /// </summary>
    [JsonPropertyName("cascadeAssign")]
    public string? CascadeAssign { get; init; }

    /// <summary>
    /// Gets the cascade configuration for delete operations.
    /// </summary>
    [JsonPropertyName("cascadeDelete")]
    public string? CascadeDelete { get; init; }

    /// <summary>
    /// Gets the cascade configuration for merge operations.
    /// </summary>
    [JsonPropertyName("cascadeMerge")]
    public string? CascadeMerge { get; init; }

    /// <summary>
    /// Gets the cascade configuration for reparent operations.
    /// </summary>
    [JsonPropertyName("cascadeReparent")]
    public string? CascadeReparent { get; init; }

    /// <summary>
    /// Gets the cascade configuration for share operations.
    /// </summary>
    [JsonPropertyName("cascadeShare")]
    public string? CascadeShare { get; init; }

    /// <summary>
    /// Gets the cascade configuration for unshare operations.
    /// </summary>
    [JsonPropertyName("cascadeUnshare")]
    public string? CascadeUnshare { get; init; }
}
