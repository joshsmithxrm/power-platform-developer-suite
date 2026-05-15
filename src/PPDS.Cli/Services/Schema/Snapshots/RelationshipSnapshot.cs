namespace PPDS.Cli.Services.Schema.Snapshots;

/// <summary>Snapshot of a single relationship.</summary>
public sealed class RelationshipSnapshot
{
    /// <summary>Schema name (e.g. <c>account_primary_contact</c>).</summary>
    public required string SchemaName { get; init; }

    /// <summary>Relationship type: <c>OneToMany</c>, <c>ManyToOne</c>, or <c>ManyToMany</c>.</summary>
    public required string RelationshipType { get; init; }

    /// <summary>Referencing (child) entity logical name. Null for many-to-many.</summary>
    public string? ReferencingEntity { get; init; }

    /// <summary>Referenced (parent) entity logical name. Null for many-to-many.</summary>
    public string? ReferencedEntity { get; init; }
}
