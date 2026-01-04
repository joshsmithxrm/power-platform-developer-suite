using System.Text.Json.Serialization;

namespace PPDS.Dataverse.Metadata.Models;

/// <summary>
/// Summary information for an entity in list views.
/// </summary>
public sealed class EntitySummary
{
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
}
