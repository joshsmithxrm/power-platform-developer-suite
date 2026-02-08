using System.Collections.Concurrent;

namespace PPDS.Dataverse.Query.Planning;

/// <summary>
/// Mutable statistics collected during plan execution.
/// Thread-safe for parallel node execution.
/// </summary>
public sealed class QueryPlanStatistics
{
    /// <summary>Total rows read from data sources.</summary>
    public long RowsRead { get; set; }

    /// <summary>Total rows output by the plan root.</summary>
    public long RowsOutput { get; set; }

    /// <summary>Total FetchXML pages fetched.</summary>
    public int PagesFetched { get; set; }

    /// <summary>Total execution time in milliseconds.</summary>
    public long ExecutionTimeMs { get; set; }

    /// <summary>Per-node statistics keyed by node description.</summary>
    public ConcurrentDictionary<string, NodeStatistics> NodeStats { get; } = new();
}

/// <summary>
/// Statistics for a single plan node.
/// </summary>
public sealed class NodeStatistics
{
    /// <summary>Rows produced by this node.</summary>
    public long RowsProduced { get; set; }

    /// <summary>Time spent in this node (ms).</summary>
    public long TimeMs { get; set; }
}
