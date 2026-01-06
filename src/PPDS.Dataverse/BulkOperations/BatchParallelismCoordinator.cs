using System;
using System.Threading;
using System.Threading.Tasks;
using PPDS.Dataverse.Pooling;

namespace PPDS.Dataverse.BulkOperations;

/// <summary>
/// Coordinates batch parallelism across all concurrent bulk operations.
/// Ensures total concurrent batches never exceed pool's recommended DOP.
/// </summary>
/// <remarks>
/// <para>
/// When multiple entities import in parallel, each entity's BulkOperationExecutor
/// could independently try to use the full pool DOP, leading to over-subscription
/// (e.g., 4 entities × 50 DOP = 200 concurrent batch tasks vs 50 pool connections).
/// </para>
/// <para>
/// This coordinator provides a shared semaphore that all batch executions must
/// acquire a slot from before proceeding. Capacity tracks the pool's DOP and
/// expands dynamically when throttling clears.
/// </para>
/// <para>
/// Thread Safety: All operations are thread-safe. Uses SemaphoreSlim for slot
/// management, double-check locking for capacity expansion, and Interlocked
/// operations for dispose tracking.
/// </para>
/// </remarks>
public sealed class BatchParallelismCoordinator : IDisposable
{
    private readonly IDataverseConnectionPool _pool;
    private readonly SemaphoreSlim _semaphore;
    private readonly TimeSpan _acquireTimeout;
    private int _currentCapacity;
    private readonly object _capacityLock = new();
    private int _disposed;

    /// <summary>
    /// Creates a new coordinator tied to the specified connection pool.
    /// </summary>
    /// <param name="pool">The connection pool to coordinate with.</param>
    /// <param name="acquireTimeout">
    /// Maximum time to wait for a batch slot. Defaults to 120 seconds (matching pool timeout).
    /// </param>
    public BatchParallelismCoordinator(
        IDataverseConnectionPool pool,
        TimeSpan? acquireTimeout = null)
    {
        _pool = pool ?? throw new ArgumentNullException(nameof(pool));
        _acquireTimeout = acquireTimeout ?? TimeSpan.FromSeconds(120);

        // Initialize at current pool DOP
        _currentCapacity = Math.Max(1, pool.GetTotalRecommendedParallelism());

        // Use int.MaxValue as max count to allow expansion via Release()
        _semaphore = new SemaphoreSlim(_currentCapacity, int.MaxValue);
    }

    /// <summary>
    /// Gets the current batch slot capacity.
    /// May expand during throttle recovery as pool DOP increases.
    /// </summary>
    public int CurrentCapacity => _currentCapacity;

    /// <summary>
    /// Gets the number of currently available batch slots.
    /// </summary>
    public int AvailableSlots => _semaphore.CurrentCount;

    /// <summary>
    /// Acquires a batch execution slot. Blocks until slot available or timeout.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Disposable slot that must be disposed after batch completes.</returns>
    /// <exception cref="BatchCoordinatorExhaustedException">Thrown if timeout exceeded.</exception>
    /// <exception cref="ObjectDisposedException">Thrown if coordinator is disposed.</exception>
    /// <exception cref="OperationCanceledException">Thrown if cancellation requested.</exception>
    public async Task<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        // Check for capacity increase (pool recovered from throttling)
        TryExpandCapacity();

        var acquired = await _semaphore.WaitAsync(_acquireTimeout, cancellationToken).ConfigureAwait(false);
        if (!acquired)
        {
            throw new BatchCoordinatorExhaustedException(
                _semaphore.CurrentCount,
                _currentCapacity,
                _acquireTimeout);
        }

        return new BatchSlot(this);
    }

    /// <summary>
    /// Expands capacity if pool DOP increased (throttle recovery).
    /// Thread-safe: uses double-check locking pattern.
    /// </summary>
    /// <remarks>
    /// SemaphoreSlim cannot shrink, only expand. When throttling occurs and DOP drops,
    /// batches naturally slow down (hold slots longer while waiting on throttled connections).
    /// This provides natural backpressure without needing to shrink the semaphore.
    /// </remarks>
    private void TryExpandCapacity()
    {
        var liveDop = _pool.GetTotalRecommendedParallelism();
        if (liveDop <= _currentCapacity) return;

        lock (_capacityLock)
        {
            // Double-check after acquiring lock
            liveDop = _pool.GetTotalRecommendedParallelism();
            if (liveDop <= _currentCapacity) return;

            var expansion = liveDop - _currentCapacity;
            _semaphore.Release(expansion);
            _currentCapacity = liveDop;
        }
    }

    private void ReleaseSlot()
    {
        // Only release if not disposed (semaphore may already be disposed)
        if (_disposed == 0)
        {
            _semaphore.Release();
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed != 0)
            throw new ObjectDisposedException(nameof(BatchParallelismCoordinator));
    }

    /// <summary>
    /// Disposes the coordinator and releases all resources.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _semaphore.Dispose();
        }
    }

    /// <summary>
    /// Represents a held batch slot. Dispose to release back to coordinator.
    /// </summary>
    private sealed class BatchSlot : IAsyncDisposable
    {
        private readonly BatchParallelismCoordinator _coordinator;
        private int _released;

        public BatchSlot(BatchParallelismCoordinator coordinator)
            => _coordinator = coordinator;

        public ValueTask DisposeAsync()
        {
            // Ensure single release using Interlocked
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                _coordinator.ReleaseSlot();
            }
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>
/// Thrown when batch coordinator cannot acquire a slot within timeout.
/// </summary>
/// <remarks>
/// This indicates that too many batch operations are running concurrently
/// relative to pool capacity. Consider reducing MaxParallelEntities or
/// wait for throttling to clear.
/// </remarks>
public class BatchCoordinatorExhaustedException : Exception
{
    /// <summary>
    /// Gets the number of slots that were available when timeout occurred.
    /// </summary>
    public int AvailableSlots { get; }

    /// <summary>
    /// Gets the total capacity of the coordinator.
    /// </summary>
    public int TotalCapacity { get; }

    /// <summary>
    /// Gets the timeout duration that was exceeded.
    /// </summary>
    public TimeSpan Timeout { get; }

    /// <summary>
    /// Creates a new exception with details about the exhaustion.
    /// </summary>
    public BatchCoordinatorExhaustedException(int availableSlots, int totalCapacity, TimeSpan timeout)
        : base($"Batch coordinator exhausted. Available: {availableSlots}, Capacity: {totalCapacity}, Timeout: {timeout.TotalSeconds:F1}s. " +
               "Consider reducing MaxParallelEntities or waiting for throttle recovery.")
    {
        AvailableSlots = availableSlots;
        TotalCapacity = totalCapacity;
        Timeout = timeout;
    }
}
