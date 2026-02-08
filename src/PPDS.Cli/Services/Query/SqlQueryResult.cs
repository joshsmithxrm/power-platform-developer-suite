using PPDS.Dataverse.Query;

namespace PPDS.Cli.Services.Query;

/// <summary>
/// Result of SQL query execution, combining the original SQL,
/// transpiled FetchXML, and query results.
/// </summary>
public sealed class SqlQueryResult
{
    /// <summary>
    /// The original SQL query that was executed.
    /// </summary>
    public required string OriginalSql { get; init; }

    /// <summary>
    /// The FetchXML that the SQL was transpiled to. Null for dry-run results.
    /// </summary>
    public required string? TranspiledFetchXml { get; init; }

    /// <summary>
    /// The query result from Dataverse.
    /// </summary>
    public required QueryResult Result { get; init; }

    /// <summary>
    /// DML safety check result, if DML safety was evaluated. Null for SELECT queries
    /// or when DML safety options were not provided.
    /// </summary>
    public DmlSafetyResult? DmlSafetyResult { get; init; }
}
