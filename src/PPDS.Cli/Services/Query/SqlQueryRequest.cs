namespace PPDS.Cli.Services.Query;

/// <summary>
/// Request parameters for SQL query execution.
/// </summary>
public sealed record SqlQueryRequest
{
    /// <summary>
    /// The SQL query to execute.
    /// </summary>
    public required string Sql { get; init; }

    /// <summary>
    /// Optional TOP value to override in the query.
    /// If specified, overrides any TOP clause in the SQL.
    /// </summary>
    public int? TopOverride { get; init; }

    /// <summary>
    /// Page number for pagination (1-based).
    /// </summary>
    public int? PageNumber { get; init; }

    /// <summary>
    /// Paging cookie from a previous result for continuation.
    /// </summary>
    public string? PagingCookie { get; init; }

    /// <summary>
    /// Whether to include total record count in the result.
    /// Note: Cannot be used with TOP queries due to Dataverse limitation.
    /// </summary>
    public bool IncludeCount { get; init; }

    /// <summary>
    /// Whether to route the query through the TDS Endpoint (direct SQL)
    /// instead of transpiling to FetchXML.
    /// </summary>
    public bool UseTdsEndpoint { get; init; }

    /// <summary>
    /// DML safety options. When non-null, the service validates DML statements
    /// (DELETE, UPDATE, INSERT) before execution. When null, DML safety checks
    /// are skipped (e.g., for non-CLI callers that handle safety externally).
    /// </summary>
    public DmlSafetyOptions? DmlSafety { get; init; }

    /// <summary>
    /// Whether to enable page-ahead buffering for large result sets.
    /// When true, the next page is fetched in the background while the current page is consumed.
    /// </summary>
    public bool EnablePrefetch { get; init; }
}
