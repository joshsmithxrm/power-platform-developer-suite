using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace PPDS.Dataverse.Metadata.Models;

/// <summary>
/// Full entity metadata including attributes, relationships, keys, and privileges.
/// </summary>
public sealed class EntityMetadataDto
{
    /// <summary>
    /// Gets the unique metadata identifier.
    /// </summary>
    [JsonPropertyName("metadataId")]
    public Guid MetadataId { get; init; }

    /// <summary>
    /// Gets the entity logical name.
    /// </summary>
    [JsonPropertyName("logicalName")]
    public required string LogicalName { get; init; }

    /// <summary>
    /// Gets the entity display name.
    /// </summary>
    [JsonPropertyName("displayName")]
    public required string DisplayName { get; init; }

    /// <summary>
    /// Gets the entity plural display name (collection name).
    /// </summary>
    [JsonPropertyName("pluralName")]
    public string? PluralName { get; init; }

    /// <summary>
    /// Gets the entity schema name.
    /// </summary>
    [JsonPropertyName("schemaName")]
    public required string SchemaName { get; init; }

    /// <summary>
    /// Gets the entity set name (for Web API).
    /// </summary>
    [JsonPropertyName("entitySetName")]
    public string? EntitySetName { get; init; }

    /// <summary>
    /// Gets the primary ID attribute.
    /// </summary>
    [JsonPropertyName("primaryIdAttribute")]
    public string? PrimaryIdAttribute { get; init; }

    /// <summary>
    /// Gets the primary name attribute.
    /// </summary>
    [JsonPropertyName("primaryNameAttribute")]
    public string? PrimaryNameAttribute { get; init; }

    /// <summary>
    /// Gets the primary image attribute.
    /// </summary>
    [JsonPropertyName("primaryImageAttribute")]
    public string? PrimaryImageAttribute { get; init; }

    /// <summary>
    /// Gets the entity type code.
    /// </summary>
    [JsonPropertyName("objectTypeCode")]
    public int ObjectTypeCode { get; init; }

    /// <summary>
    /// Gets whether this is a custom entity.
    /// </summary>
    [JsonPropertyName("isCustomEntity")]
    public bool IsCustomEntity { get; init; }

    /// <summary>
    /// Gets whether this entity is part of a managed solution.
    /// </summary>
    [JsonPropertyName("isManaged")]
    public bool IsManaged { get; init; }

    /// <summary>
    /// Gets the ownership type (UserOwned, OrganizationOwned, None).
    /// </summary>
    [JsonPropertyName("ownershipType")]
    public string? OwnershipType { get; init; }

    /// <summary>
    /// Gets the logical collection name.
    /// </summary>
    [JsonPropertyName("logicalCollectionName")]
    public string? LogicalCollectionName { get; init; }

    /// <summary>
    /// Gets the description of the entity.
    /// </summary>
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>
    /// Gets whether the entity is activity.
    /// </summary>
    [JsonPropertyName("isActivity")]
    public bool IsActivity { get; init; }

    /// <summary>
    /// Gets whether the entity is activity party.
    /// </summary>
    [JsonPropertyName("isActivityParty")]
    public bool IsActivityParty { get; init; }

    /// <summary>
    /// Gets whether the entity supports notes (annotations).
    /// </summary>
    [JsonPropertyName("hasNotes")]
    public bool HasNotes { get; init; }

    /// <summary>
    /// Gets whether the entity supports activities.
    /// </summary>
    [JsonPropertyName("hasActivities")]
    public bool HasActivities { get; init; }

    /// <summary>
    /// Gets whether the entity is valid for Advanced Find.
    /// </summary>
    [JsonPropertyName("isValidForAdvancedFind")]
    public bool IsValidForAdvancedFind { get; init; }

    /// <summary>
    /// Gets whether audit is enabled.
    /// </summary>
    [JsonPropertyName("isAuditEnabled")]
    public bool IsAuditEnabled { get; init; }

    /// <summary>
    /// Gets whether change tracking is enabled.
    /// </summary>
    [JsonPropertyName("changeTrackingEnabled")]
    public bool ChangeTrackingEnabled { get; init; }

    /// <summary>
    /// Gets whether business process flows are enabled.
    /// </summary>
    [JsonPropertyName("isBusinessProcessEnabled")]
    public bool IsBusinessProcessEnabled { get; init; }

    /// <summary>
    /// Gets whether quick create is enabled.
    /// </summary>
    [JsonPropertyName("isQuickCreateEnabled")]
    public bool IsQuickCreateEnabled { get; init; }

    /// <summary>
    /// Gets whether duplicate detection is enabled.
    /// </summary>
    [JsonPropertyName("isDuplicateDetectionEnabled")]
    public bool IsDuplicateDetectionEnabled { get; init; }

    /// <summary>
    /// Gets whether the entity is valid for queue.
    /// </summary>
    [JsonPropertyName("isValidForQueue")]
    public bool IsValidForQueue { get; init; }

    /// <summary>
    /// Gets whether this is an intersect entity.
    /// </summary>
    [JsonPropertyName("isIntersect")]
    public bool IsIntersect { get; init; }

    /// <summary>
    /// Gets whether the entity supports the CreateMultiple bulk API.
    /// </summary>
    /// <remarks>
    /// When false, bulk create operations should fall back to single-record creates
    /// or ExecuteMultiple batching.
    /// </remarks>
    [JsonPropertyName("canCreateMultiple")]
    public bool CanCreateMultiple { get; init; }

    /// <summary>
    /// Gets whether the entity supports the UpdateMultiple bulk API.
    /// </summary>
    /// <remarks>
    /// When false, bulk update operations should fall back to single-record updates
    /// or ExecuteMultiple batching.
    /// </remarks>
    [JsonPropertyName("canUpdateMultiple")]
    public bool CanUpdateMultiple { get; init; }

    /// <summary>
    /// Gets the entity attributes.
    /// </summary>
    [JsonPropertyName("attributes")]
    public List<AttributeMetadataDto> Attributes { get; init; } = [];

    /// <summary>
    /// Gets the one-to-many relationships.
    /// </summary>
    [JsonPropertyName("oneToManyRelationships")]
    public List<RelationshipMetadataDto> OneToManyRelationships { get; init; } = [];

    /// <summary>
    /// Gets the many-to-one relationships.
    /// </summary>
    [JsonPropertyName("manyToOneRelationships")]
    public List<RelationshipMetadataDto> ManyToOneRelationships { get; init; } = [];

    /// <summary>
    /// Gets the many-to-many relationships.
    /// </summary>
    [JsonPropertyName("manyToManyRelationships")]
    public List<ManyToManyRelationshipDto> ManyToManyRelationships { get; init; } = [];

    /// <summary>
    /// Gets the entity alternate keys.
    /// </summary>
    [JsonPropertyName("keys")]
    public List<EntityKeyDto> Keys { get; init; } = [];

    /// <summary>
    /// Gets the entity privileges.
    /// </summary>
    [JsonPropertyName("privileges")]
    public List<PrivilegeDto> Privileges { get; init; } = [];
}
