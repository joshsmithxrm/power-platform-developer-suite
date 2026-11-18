using System.Collections.Concurrent;
using System.Collections.Generic;
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

    private readonly ConcurrentQueue<DmlStatementOutcome> _dmlOutcomes = new();

    /// <summary>
    /// One outcome per executed DML statement, in execution order. Unlike the paging metadata
    /// below, this is safe to append to from concurrent nodes.
    /// </summary>
    public IReadOnlyList<DmlStatementOutcome> DmlOutcomes => _dmlOutcomes.ToArray();

    /// <summary>Records the outcome of one executed DML statement.</summary>
    public void AddDmlOutcome(DmlStatementOutcome outcome) => _dmlOutcomes.Enqueue(outcome);

    // Script statements run sequentially, so the position stack needs no synchronisation.
    private readonly List<int> _statementPath = new();

    /// <summary>Enters statement <paramref name="position"/> (1-based) of the script block being executed.</summary>
    public void PushStatement(int position) => _statementPath.Add(position);

    /// <summary>Leaves the statement entered by the matching <see cref="PushStatement"/>.</summary>
    public void PopStatement()
    {
        if (_statementPath.Count > 0) _statementPath.RemoveAt(_statementPath.Count - 1);
    }

    /// <summary>
    /// Position of the executing statement within its script ("3", or "2.1" inside a block), or null when
    /// the plan is not a script.
    /// </summary>
    public string? CurrentStatementPosition => _statementPath.Count == 0 ? null : string.Join(".", _statementPath);

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

/// <summary>
/// Result of one executed DML statement (INSERT, UPDATE or DELETE).
/// </summary>
/// <param name="Operation">The DML operation.</param>
/// <param name="EntityLogicalName">The target entity.</param>
/// <param name="AffectedRows">Rows that succeeded.</param>
/// <param name="FailedRows">Rows that failed.</param>
/// <param name="ErrorMessage">
/// A representative error message when <paramref name="FailedRows"/> is greater than zero. Batches may run in
/// parallel, so this is not necessarily the error of the first row.
/// </param>
/// <param name="Position">Position of the statement in its script ("3", "2.1" in a block); null outside scripts.</param>
/// <param name="Interrupted">
/// True when the statement's bulk call threw or was cancelled part-way: some of its rows may be committed. The counts
/// are what had been reported before the interruption (a lower bound), not the final result.
/// </param>
public sealed record DmlStatementOutcome(
    Nodes.DmlOperation Operation,
    string EntityLogicalName,
    long AffectedRows,
    long FailedRows,
    string? ErrorMessage,
    string? Position = null,
    bool Interrupted = false);
