using System.Collections.Generic;

namespace PPDS.Dataverse.Sql.Transpilation;

/// <summary>
/// Result of SQL to FetchXML transpilation, including virtual column metadata.
/// </summary>
public sealed class TranspileResult
{
    /// <summary>
    /// The transpiled FetchXML string.
    /// </summary>
    public required string FetchXml { get; init; }

    /// <summary>
    /// Virtual columns that were detected and need special handling.
    /// Key is the virtual column name (e.g., "owneridname"), value is the base column name (e.g., "ownerid").
    /// </summary>
    public required IReadOnlyDictionary<string, VirtualColumnInfo> VirtualColumns { get; init; }

    /// <summary>
    /// Creates a simple result with no virtual columns.
    /// </summary>
    public static TranspileResult Simple(string fetchXml) => new()
    {
        FetchXml = fetchXml,
        VirtualColumns = new Dictionary<string, VirtualColumnInfo>()
    };
}

/// <summary>
/// Information about a virtual column (e.g., "owneridname" that maps to "ownerid").
/// </summary>
public sealed class VirtualColumnInfo
{
    /// <summary>
    /// The base column name that provides the data (e.g., "ownerid").
    /// </summary>
    public required string BaseColumnName { get; init; }

    /// <summary>
    /// Whether the user also explicitly queried the base column.
    /// If false, the base column should be hidden from results.
    /// </summary>
    public bool BaseColumnExplicitlyQueried { get; init; }

    /// <summary>
    /// The alias for the virtual column, if any.
    /// </summary>
    public string? Alias { get; init; }
}
