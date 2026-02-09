using System.Collections.Concurrent;
using System.Threading;

namespace PPDS.Dataverse.Query.Planning;

/// <summary>
/// Mutable statistics collected during plan execution.
/// Thread-safe for parallel node execution via Interlocked operations.
/// </summary>
public sealed class QueryPlanStatistics
{
    private long _rowsRead;
    private long _rowsOutput;
    private long _pagesFetched;
    private long _executionTimeMs;

    /// <summary>Total rows read from data sources.</summary>
    public long RowsRead => Interlocked.Read(ref _rowsRead);

    /// <summary>Total rows output by the plan root.</summary>
    public long RowsOutput => Interlocked.Read(ref _rowsOutput);

    /// <summary>Total FetchXML pages fetched.</summary>
    public long PagesFetched => Interlocked.Read(ref _pagesFetched);

    /// <summary>Total execution time in milliseconds.</summary>
    public long ExecutionTimeMs => Interlocked.Read(ref _executionTimeMs);

    /// <summary>Per-node statistics keyed by node description.</summary>
    public ConcurrentDictionary<string, NodeStatistics> NodeStats { get; } = new();

    // Paging metadata: only valid for single-scan-node plans (Phase 0).
    // Phase 4 parallel partitioning will need a different approach since each
    // partition has its own paging state. Not protected by Interlocked.

    /// <summary>Paging cookie from the last fetched page (for caller-controlled paging).</summary>
    public string? LastPagingCookie { get; set; }

    /// <summary>Whether more records are available after the last fetched page.</summary>
    public bool LastMoreRecords { get; set; }

    /// <summary>Page number of the last fetched page.</summary>
    public int LastPageNumber { get; set; }

    /// <summary>Total record count from the last fetched page, if requested.</summary>
    public int? LastTotalCount { get; set; }

    /// <summary>When true, paging metadata writes are suppressed (parallel execution).</summary>
    public bool SuppressPagingMetadata { get; set; }

    /// <summary>Increments <see cref="RowsRead"/> by one.</summary>
    public void IncrementRowsRead() => Interlocked.Increment(ref _rowsRead);

    /// <summary>Adds <paramref name="count"/> to <see cref="RowsRead"/>.</summary>
    public void AddRowsRead(long count) => Interlocked.Add(ref _rowsRead, count);

    /// <summary>Increments <see cref="RowsOutput"/> by one.</summary>
    public void IncrementRowsOutput() => Interlocked.Increment(ref _rowsOutput);

    /// <summary>Adds <paramref name="count"/> to <see cref="RowsOutput"/>.</summary>
    public void AddRowsOutput(long count) => Interlocked.Add(ref _rowsOutput, count);

    /// <summary>Increments <see cref="PagesFetched"/> by one.</summary>
    public void IncrementPagesFetched() => Interlocked.Increment(ref _pagesFetched);

    /// <summary>Adds <paramref name="ms"/> to <see cref="ExecutionTimeMs"/>.</summary>
    public void AddExecutionTimeMs(long ms) => Interlocked.Add(ref _executionTimeMs, ms);
}

/// <summary>
/// Statistics for a single plan node. Thread-safe via Interlocked operations.
/// </summary>
public sealed class NodeStatistics
{
    private long _rowsProduced;
    private long _timeMs;

    /// <summary>Rows produced by this node.</summary>
    public long RowsProduced => Interlocked.Read(ref _rowsProduced);

    /// <summary>Time spent in this node (ms).</summary>
    public long TimeMs => Interlocked.Read(ref _timeMs);

    /// <summary>Increments <see cref="RowsProduced"/> by one.</summary>
    public void IncrementRowsProduced() => Interlocked.Increment(ref _rowsProduced);

    /// <summary>Adds <paramref name="count"/> to <see cref="RowsProduced"/>.</summary>
    public void AddRowsProduced(long count) => Interlocked.Add(ref _rowsProduced, count);

    /// <summary>Adds <paramref name="ms"/> to <see cref="TimeMs"/>.</summary>
    public void AddTimeMs(long ms) => Interlocked.Add(ref _timeMs, ms);
}
