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
    /// True when a user explicitly asked for <see cref="TopOverride"/> (CLI <c>--top</c>). The service then
    /// refuses the request if it contains DML (<c>--top</c> limits returned rows only). When false, a
    /// <see cref="TopOverride"/> is a default the caller applies to every query (for example the extension's
    /// default top) and is ignored for DML.
    /// </summary>
    public bool TopIsUserSpecified { get; init; }

    /// <summary>
    /// Page number for pagination (1-based).
    /// </summary>
    public int? PageNumber { get; init; }

    /// <summary>
    /// Paging cookie from a previous result for continuation.
    /// </summary>
    public string? PagingCookie { get; init; }

    /// <summary>
    /// True when a user explicitly asked for <see cref="PageNumber"/> or <see cref="PagingCookie"/> (CLI
    /// <c>--page</c>, <c>--paging-cookie</c>). Paging selects returned rows only, so the service then refuses the
    /// request if it contains DML. When false, paging values come from a caller: a first-page value is ignored for
    /// DML, and a request for a further page (a page after the first, or a paging cookie) never runs DML.
    /// </summary>
    public bool PagingIsUserSpecified { get; init; }

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
    /// DML safety options. The service always validates DML statements (DELETE, UPDATE, INSERT)
    /// before execution. When null, the defaults apply: not confirmed, not a dry-run, default row cap,
    /// so DML is refused with <c>Query.DmlConfirmationRequired</c>. Callers must pass explicit options
    /// (for example <c>IsConfirmed</c>) to run DML. SELECT-only requests are unaffected.
    /// </summary>
    public DmlSafetyOptions? DmlSafety { get; init; }

    /// <summary>
    /// Whether to enable page-ahead buffering for large result sets.
    /// When true, the next page is fetched in the background while the current page is consumed.
    /// </summary>
    public bool EnablePrefetch { get; init; }
}
