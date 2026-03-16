using System.Collections.Generic;
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

    /// <summary>
    /// Environments that contributed data. Single-env queries have one entry.
    /// Cross-env queries have multiple. Null for transpile-only results.
    /// </summary>
    public IReadOnlyList<QueryDataSource>? DataSources { get; init; }

    /// <summary>
    /// Names of query hints that were applied (e.g., ["NOLOCK", "BYPASS_PLUGINS"]).
    /// Null when no hints were active. Used for debugging and UI feedback.
    /// </summary>
    public IReadOnlyList<string>? AppliedHints { get; init; }

    /// <summary>
    /// The actual execution path used. Null for transpile-only or dry-run results.
    /// </summary>
    public QueryExecutionMode? ExecutionMode { get; init; }
}
