using System.Collections.Generic;

namespace PPDS.Cli.Services.Schema.Snapshots;

/// <summary>Snapshot of a single attribute.</summary>
public sealed class AttributeSnapshot
{
    /// <summary>Logical name (e.g. <c>name</c>).</summary>
    public required string LogicalName { get; init; }

    /// <summary>
    /// Normalized attribute type identifier (e.g. <c>string</c>, <c>integer</c>,
    /// <c>lookup</c>, <c>picklist</c>, <c>datetime</c>, <c>decimal</c>).
    /// </summary>
    public required string AttributeType { get; init; }

    /// <summary>
    /// Required level — one of <c>None</c>, <c>Recommended</c>,
    /// <c>ApplicationRequired</c>, <c>SystemRequired</c>. Null when unknown.
    /// </summary>
    public string? RequiredLevel { get; init; }

    /// <summary>Maximum length for string types.</summary>
    public int? MaxLength { get; init; }

    /// <summary>Precision for decimal/money types.</summary>
    public int? Precision { get; init; }

    /// <summary>Allowed lookup targets, lowercase, for lookup attributes.</summary>
    public IReadOnlyList<string>? LookupTargets { get; init; }

    /// <summary>Option-set values for picklist attributes (null if unknown).</summary>
    public IReadOnlyList<int>? OptionValues { get; init; }
}
