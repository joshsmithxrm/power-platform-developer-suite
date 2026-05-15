using System.Collections.Generic;

namespace PPDS.Cli.Services.Schema.Snapshots;

/// <summary>Snapshot of a single entity.</summary>
public sealed class EntitySnapshot
{
    /// <summary>Logical name (e.g. <c>account</c>).</summary>
    public required string LogicalName { get; init; }

    /// <summary>Display name (e.g. <c>Account</c>).</summary>
    public string? DisplayName { get; init; }

    /// <summary>Attributes on the entity.</summary>
    public required IReadOnlyList<AttributeSnapshot> Attributes { get; init; }

    /// <summary>Relationships involving this entity (referencing side).</summary>
    public required IReadOnlyList<RelationshipSnapshot> Relationships { get; init; }
}
