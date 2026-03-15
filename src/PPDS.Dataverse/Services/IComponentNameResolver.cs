using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PPDS.Dataverse.Services;

/// <summary>
/// Resolved name fields for a solution component.
/// </summary>
public record ComponentNames(
    string? LogicalName,
    string? SchemaName,
    string? DisplayName);

/// <summary>
/// Resolves component objectId GUIDs to human-readable names
/// by querying type-specific Dataverse tables.
/// </summary>
public interface IComponentNameResolver
{
    Task<IReadOnlyDictionary<Guid, ComponentNames>> ResolveAsync(
        int componentType,
        IReadOnlyList<Guid> objectIds,
        CancellationToken cancellationToken = default);
}
