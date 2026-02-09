using System;
using PPDS.Dataverse.Query;
using PPDS.Dataverse.Query.Execution;

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

    /// <summary>
    /// The original SQL text, needed for TDS Endpoint routing.
    /// When <see cref="UseTdsEndpoint"/> is true, this SQL is passed directly
    /// to the TDS Endpoint instead of being transpiled to FetchXML.
    /// </summary>
    public string? OriginalSql { get; init; }

    /// <summary>
    /// The TDS query executor for direct SQL execution (Phase 3.5).
    /// Required when <see cref="UseTdsEndpoint"/> is true.
    /// </summary>
    public ITdsQueryExecutor? TdsQueryExecutor { get; init; }

    /// <summary>
    /// Estimated total record count for the query's entity. When set for aggregate queries
    /// and the count exceeds <see cref="AggregateRecordLimit"/>, the planner partitions the
    /// query by date range and executes partitions in parallel across the connection pool.
    /// Obtained from <see cref="IQueryExecutor.GetTotalRecordCountAsync"/> by the caller.
    /// </summary>
    public long? EstimatedRecordCount { get; init; }

    /// <summary>
    /// Earliest createdon date in the target entity. Required for aggregate partitioning.
    /// </summary>
    public DateTime? MinDate { get; init; }

    /// <summary>
    /// Latest createdon date in the target entity. Required for aggregate partitioning.
    /// </summary>
    public DateTime? MaxDate { get; init; }

    /// <summary>
    /// Maximum records per partition for aggregate partitioning. Default is 40,000
    /// to stay safely below the Dataverse 50K AggregateQueryRecordLimit.
    /// </summary>
    public int AggregateRecordLimit { get; init; } = 50_000;

    /// <summary>
    /// Maximum records per partition. Default is 40,000 to provide headroom below the 50K limit.
    /// </summary>
    public int MaxRecordsPerPartition { get; init; } = 40_000;

    /// <summary>
    /// Optional variable scope for substituting @variable references in WHERE conditions
    /// with literal values before FetchXML transpilation.
    /// </summary>
    public VariableScope? VariableScope { get; init; }

    /// <summary>Row cap for DML operations. null = unlimited, default comes from DmlSafetyGuard (10,000).</summary>
    public int? DmlRowCap { get; init; }

    /// <summary>Whether to wrap scan nodes with PrefetchScanNode for page-ahead buffering. Default false (opt-in).</summary>
    public bool EnablePrefetch { get; init; } = false;

    /// <summary>Number of rows to buffer ahead for prefetch. Default 5000 (~3 FetchXML pages).</summary>
    public int PrefetchBufferSize { get; init; } = 5000;
}
