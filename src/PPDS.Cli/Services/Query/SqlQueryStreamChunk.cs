using PPDS.Dataverse.Query;

namespace PPDS.Cli.Services.Query;

/// <summary>
/// A chunk of streaming query results. Sent incrementally as rows are produced
/// by the plan executor. The first chunk includes column metadata; subsequent
/// chunks carry only rows.
/// </summary>
public sealed class SqlQueryStreamChunk
{
    /// <summary>
    /// The rows in this chunk.
    /// </summary>
    public required IReadOnlyList<IReadOnlyDictionary<string, QueryValue>> Rows { get; init; }

    /// <summary>
    /// Column metadata. Non-null only on the first chunk (when columns are first discovered).
    /// </summary>
    public IReadOnlyList<QueryColumn>? Columns { get; init; }

    /// <summary>
    /// The entity logical name for the query.
    /// Non-null only on the first chunk.
    /// </summary>
    public string? EntityLogicalName { get; init; }

    /// <summary>
    /// Running total of rows yielded so far (across all chunks).
    /// </summary>
    public int TotalRowsSoFar { get; init; }

    /// <summary>
    /// True when this is the final chunk and no more rows will follow.
    /// </summary>
    public bool IsComplete { get; init; }

    /// <summary>
    /// The transpiled FetchXML, if available. Non-null only on the first chunk.
    /// </summary>
    public string? TranspiledFetchXml { get; init; }
}
