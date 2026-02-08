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
}
