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
    /// <exception cref="PPDS.Dataverse.Sql.Parsing.SqlParseException">If SQL parsing fails.</exception>
    string TranspileSql(string sql, int? topOverride = null);

    /// <summary>
    /// Executes a SQL query against Dataverse.
    /// </summary>
    /// <param name="request">The query request parameters.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The query result including the transpiled FetchXML.</returns>
    /// <exception cref="PPDS.Dataverse.Sql.Parsing.SqlParseException">If SQL parsing fails.</exception>
    Task<SqlQueryResult> ExecuteAsync(
        SqlQueryRequest request,
        CancellationToken cancellationToken = default);
}
