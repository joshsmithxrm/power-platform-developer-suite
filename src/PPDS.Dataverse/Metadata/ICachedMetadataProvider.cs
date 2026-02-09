using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PPDS.Dataverse.Metadata.Models;

namespace PPDS.Dataverse.Metadata;

/// <summary>
/// Provides cached access to Dataverse metadata for IntelliSense and entity browsing.
/// Wraps <see cref="IMetadataService"/> with per-session caching to avoid redundant
/// round-trips to Dataverse for metadata that rarely changes.
/// </summary>
public interface ICachedMetadataProvider
{
    /// <summary>
    /// Gets all entities from cache. The entity list is populated by <see cref="PreloadAsync"/>
    /// and cached indefinitely for the session lifetime.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of entity summaries.</returns>
    Task<IReadOnlyList<EntitySummary>> GetEntitiesAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets attributes for an entity. Lazy-loaded on first access and cached with a 5-minute TTL.
    /// </summary>
    /// <param name="entityLogicalName">The entity logical name.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of attribute metadata.</returns>
    Task<IReadOnlyList<AttributeMetadataDto>> GetAttributesAsync(string entityLogicalName, CancellationToken ct = default);

    /// <summary>
    /// Gets relationships for an entity. Lazy-loaded on first access and cached with a 5-minute TTL.
    /// </summary>
    /// <param name="entityLogicalName">The entity logical name.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Entity relationships grouped by type.</returns>
    Task<EntityRelationshipsDto> GetRelationshipsAsync(string entityLogicalName, CancellationToken ct = default);

    /// <summary>
    /// Eagerly loads the entity list into cache. Call this early (e.g., on environment connect)
    /// so that entity names are available for IntelliSense without delay.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    Task PreloadAsync(CancellationToken ct = default);

    /// <summary>
    /// Invalidates all cached metadata. The next access will re-fetch from Dataverse.
    /// </summary>
    void InvalidateAll();

    /// <summary>
    /// Invalidates cached metadata for a specific entity (attributes and relationships).
    /// The entity list cache is not affected.
    /// </summary>
    /// <param name="entityLogicalName">The entity logical name to invalidate.</param>
    void InvalidateEntity(string entityLogicalName);
}
