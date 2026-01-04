using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PPDS.Dataverse.Metadata.Models;

namespace PPDS.Dataverse.Metadata;

/// <summary>
/// Provides access to Dataverse metadata for entity browsing and discovery.
/// </summary>
public interface IMetadataService
{
    /// <summary>
    /// Gets a list of all entities with basic information.
    /// </summary>
    /// <param name="customOnly">If true, only return custom entities.</param>
    /// <param name="filter">Optional filter pattern to match entity logical names (supports * wildcard).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of entity summaries.</returns>
    Task<IReadOnlyList<EntitySummary>> GetEntitiesAsync(
        bool customOnly = false,
        string? filter = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets full metadata for a specific entity including attributes, relationships, keys, and privileges.
    /// </summary>
    /// <param name="logicalName">The entity logical name.</param>
    /// <param name="includeAttributes">Include attributes in the response.</param>
    /// <param name="includeRelationships">Include relationships in the response.</param>
    /// <param name="includeKeys">Include alternate keys in the response.</param>
    /// <param name="includePrivileges">Include privileges in the response.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Full entity metadata.</returns>
    Task<EntityMetadataDto> GetEntityAsync(
        string logicalName,
        bool includeAttributes = true,
        bool includeRelationships = true,
        bool includeKeys = true,
        bool includePrivileges = true,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all attributes for an entity.
    /// </summary>
    /// <param name="entityLogicalName">The entity logical name.</param>
    /// <param name="attributeType">Optional filter by attribute type (e.g., "Lookup", "String").</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of attribute metadata.</returns>
    Task<IReadOnlyList<AttributeMetadataDto>> GetAttributesAsync(
        string entityLogicalName,
        string? attributeType = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all relationships for an entity.
    /// </summary>
    /// <param name="entityLogicalName">The entity logical name.</param>
    /// <param name="relationshipType">Optional filter by type (OneToMany, ManyToOne, ManyToMany).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Entity relationships grouped by type.</returns>
    Task<EntityRelationshipsDto> GetRelationshipsAsync(
        string entityLogicalName,
        string? relationshipType = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all global option sets.
    /// </summary>
    /// <param name="filter">Optional filter pattern to match option set names (supports * wildcard).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of option set summaries.</returns>
    Task<IReadOnlyList<OptionSetSummary>> GetGlobalOptionSetsAsync(
        string? filter = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a specific global option set with all values.
    /// </summary>
    /// <param name="name">The option set name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Full option set metadata with values.</returns>
    Task<OptionSetMetadataDto> GetOptionSetAsync(
        string name,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all alternate keys for an entity.
    /// </summary>
    /// <param name="entityLogicalName">The entity logical name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of entity key metadata.</returns>
    Task<IReadOnlyList<EntityKeyDto>> GetKeysAsync(
        string entityLogicalName,
        CancellationToken cancellationToken = default);
}
