namespace PPDS.Dataverse.Query.Planning;

/// <summary>
/// Options for query plan construction.
/// </summary>
public sealed class QueryPlanOptions
{
    /// <summary>Pool capacity from pool.GetTotalRecommendedParallelism().</summary>
    public int PoolCapacity { get; init; }

    /// <summary>Whether to use the TDS Endpoint (Phase 3.5).</summary>
    public bool UseTdsEndpoint { get; init; }

    /// <summary>If true, build plan for explanation only — don't execute.</summary>
    public bool ExplainOnly { get; init; }

    /// <summary>Global row limit, if any.</summary>
    public int? MaxRows { get; init; }

    /// <summary>
    /// Page number for caller-controlled paging (1-based).
    /// When set, the plan fetches only this single page instead of auto-paging.
    /// </summary>
    public int? PageNumber { get; init; }

    /// <summary>
    /// Paging cookie from a previous result for continuation.
    /// Used with <see cref="PageNumber"/> for caller-controlled paging.
    /// </summary>
    public string? PagingCookie { get; init; }

    /// <summary>Whether to include total record count in the result.</summary>
    public bool IncludeCount { get; init; }
}
