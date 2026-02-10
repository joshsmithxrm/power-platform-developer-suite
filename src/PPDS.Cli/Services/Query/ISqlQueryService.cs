using PPDS.Dataverse.Query.Planning;

namespace PPDS.Cli.Services.Query;

/// <summary>
/// Application service for SQL query operations.
/// Provides business logic for parsing, transpiling, and executing SQL queries against Dataverse.
/// </summary>
/// <remarks>
/// This service is the single source of truth for SQL query execution logic,
/// consumed by CLI commands, TUI wizards, and daemon RPC handlers.
/// </remarks>
public interface ISqlQueryService
{
    /// <summary>
    /// Transpiles SQL to FetchXML without executing.
    /// </summary>
    /// <param name="sql">The SQL query to transpile.</param>
    /// <param name="topOverride">Optional TOP value to override in the query.</param>
    /// <returns>The transpiled FetchXML.</returns>
    /// <exception cref="PPDS.Query.Parsing.QueryParseException">If SQL parsing fails.</exception>
    string TranspileSql(string sql, int? topOverride = null);

    /// <summary>
    /// Executes a SQL query against Dataverse.
    /// </summary>
    /// <param name="request">The query request parameters.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The query result including the transpiled FetchXML.</returns>
    /// <exception cref="PPDS.Query.Parsing.QueryParseException">If SQL parsing fails.</exception>
    Task<SqlQueryResult> ExecuteAsync(
        SqlQueryRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the execution plan for a SQL query without executing it.
    /// </summary>
    /// <param name="sql">The SQL query to explain.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Description of the execution plan.</returns>
    /// <exception cref="PPDS.Query.Parsing.QueryParseException">If SQL parsing fails.</exception>
    Task<QueryPlanDescription> ExplainAsync(string sql, CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes a SQL query and streams results back in chunks as they become available.
    /// The first chunk includes column metadata; subsequent chunks carry only rows.
    /// </summary>
    /// <param name="request">The query request parameters.</param>
    /// <param name="chunkSize">Number of rows per chunk (default 100).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An async stream of result chunks.</returns>
    /// <exception cref="PPDS.Query.Parsing.QueryParseException">If SQL parsing fails.</exception>
    IAsyncEnumerable<SqlQueryStreamChunk> ExecuteStreamingAsync(
        SqlQueryRequest request,
        int chunkSize = 100,
        CancellationToken cancellationToken = default);
}
