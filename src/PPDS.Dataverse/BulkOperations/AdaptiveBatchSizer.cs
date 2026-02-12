using System;

namespace PPDS.Dataverse.BulkOperations;

/// <summary>
/// Dynamically adjusts DML batch size based on observed execution times.
/// Targets a configurable time per batch (default 10 seconds) to balance
/// throughput and timeout prevention.
/// </summary>
/// <remarks>
/// <para>
/// The algorithm works by measuring records-per-second from each completed batch,
/// then computing a target batch size that would take <c>targetSeconds</c> at that rate.
/// To prevent oscillation, the new size is averaged with the current size (moves halfway
/// toward the target each step).
/// </para>
/// <para>
/// Fast entities (e.g., simple reference data with few columns) will see batch sizes
/// increase toward <c>maxSize</c>, while slow entities (e.g., entities with many plugins,
/// workflows, or complex calculated fields) will see sizes decrease toward <c>minSize</c>.
/// </para>
/// </remarks>
public sealed class AdaptiveBatchSizer
{
    private readonly double _targetSeconds;
    private readonly int _minSize;
    private readonly int _maxSize;

    /// <summary>Gets the current recommended batch size.</summary>
    public int CurrentBatchSize { get; private set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="AdaptiveBatchSizer"/> class.
    /// </summary>
    /// <param name="initialSize">The starting batch size. Default: 100.</param>
    /// <param name="targetSeconds">The target execution time per batch in seconds. Default: 10.0.</param>
    /// <param name="minSize">The minimum allowed batch size. Default: 1.</param>
    /// <param name="maxSize">The maximum allowed batch size. Default: 1000.</param>
    public AdaptiveBatchSizer(
        int initialSize = 100,
        double targetSeconds = 10.0,
        int minSize = 1,
        int maxSize = 1000)
    {
        _minSize = minSize;
        _maxSize = maxSize;
        _targetSeconds = targetSeconds > 0 ? targetSeconds : 10.0;
        CurrentBatchSize = Math.Max(_minSize, Math.Min(_maxSize, initialSize));
    }

    /// <summary>
    /// Records the result of a batch execution and adjusts the batch size.
    /// </summary>
    /// <param name="batchSize">The number of records in the completed batch.</param>
    /// <param name="elapsed">The wall-clock time the batch took to execute.</param>
    /// <remarks>
    /// If <paramref name="elapsed"/> is zero or negative, or <paramref name="batchSize"/>
    /// is zero or negative, the call is a no-op to avoid division by zero.
    /// </remarks>
    public void RecordBatchResult(int batchSize, TimeSpan elapsed)
    {
        if (elapsed.TotalSeconds <= 0 || batchSize <= 0) return;

        var recordsPerSecond = batchSize / elapsed.TotalSeconds;
        var targetSize = (int)(recordsPerSecond * _targetSeconds);

        // Smooth: move halfway toward target to avoid oscillation
        CurrentBatchSize = (CurrentBatchSize + targetSize) / 2;
        CurrentBatchSize = Math.Max(_minSize, Math.Min(_maxSize, CurrentBatchSize));
    }
}
