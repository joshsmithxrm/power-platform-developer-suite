using System.Collections.Generic;

namespace PPDS.Cli.Services.Schema.Snapshots;

/// <summary>
/// Snapshot of a schema (entities + attributes + relationships) for use by
/// <see cref="PPDS.Cli.Services.Schema.SchemaComparisonService"/>.
/// </summary>
/// <remarks>
/// Decoupled from <c>EntityMetadataDto</c> and <c>MigrationSchema</c> so the
/// comparison service is pure (no Dataverse / no CMT zip dependencies) and the
/// same shape can be loaded from any source (live env, data package, future
/// snapshot file format).
/// </remarks>
public sealed class SchemaSnapshot
{
    /// <summary>
    /// Descriptor identifying where the snapshot came from
    /// (e.g. <c>data:path.zip</c> or <c>env:https://...</c>).
    /// </summary>
    public required string Source { get; init; }

    /// <summary>Entities included in the snapshot.</summary>
    public required IReadOnlyList<EntitySnapshot> Entities { get; init; }

    /// <summary>
    /// Indicates whether the snapshot includes attribute-level option-set value lists.
    /// Data packages don't carry option-set values, so option-set diffs are skipped
    /// when either side reports <c>false</c>.
    /// </summary>
    public bool IncludesOptionSetValues { get; init; }
}
