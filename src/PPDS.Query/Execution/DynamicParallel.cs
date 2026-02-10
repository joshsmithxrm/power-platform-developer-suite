using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using PPDS.Dataverse.Query.Planning;

namespace PPDS.Query.Execution;

/// <summary>
/// Dynamically scales thread count based on workload when executing multiple
/// plan node partitions in parallel. Replaces the fixed-pool approach of
/// <see cref="PPDS.Dataverse.Query.Planning.Nodes.ParallelPartitionNode"/>.
/// </summary>
/// <remarks>
/// Strategy:
/// <list type="bullet">
///   <item>Starts with 1 worker thread.</item>
///   <item>Monitors every <see cref="MonitorIntervalMs"/> milliseconds.</item>
///   <item>If all workers are busy and pending work exists, adds a worker (up to MaxDOP).</item>
///   <item>If workers are idle, removes a worker (down to 1).</item>
/// </list>
/// Uses <see cref="ConcurrentQueue{T}"/> for work distribution and
/// <see cref="Channel{T}"/> for result collection.
/// </remarks>
public sealed class DynamicParallel
{
    /// <summary>Maximum degree of parallelism (max number of worker threads).</summary>
    public int MaxDegreeOfParallelism { get; }

    /// <summary>Monitoring interval in milliseconds. Default 1000ms.</summary>
    public int MonitorIntervalMs { get; }

    private int _activeWorkers;
    private int _busyWorkers;
    private int _desiredWorkers;
    private int _pendingWorkItems;

    /// <summary>
    /// Initializes a new instance of the <see cref="DynamicParallel"/> class.
    /// </summary>
    /// <param name="maxDegreeOfParallelism">Maximum worker threads. Must be at least 1.</param>
    /// <param name="monitorIntervalMs">How often to check and scale workers (milliseconds).</param>
    public DynamicParallel(int maxDegreeOfParallelism, int monitorIntervalMs = 1000)
    {
        if (maxDegreeOfParallelism < 1)
            throw new ArgumentOutOfRangeException(nameof(maxDegreeOfParallelism), "Must be at least 1.");
        if (monitorIntervalMs < 100)
            throw new ArgumentOutOfRangeException(nameof(monitorIntervalMs), "Must be at least 100ms.");

        MaxDegreeOfParallelism = maxDegreeOfParallelism;
        MonitorIntervalMs = monitorIntervalMs;
    }

    /// <summary>
    /// Executes the given plan node partitions in parallel with dynamic scaling,
    /// yielding combined results as an async enumerable.
    /// </summary>
    /// <param name="partitions">The plan node partitions to execute.</param>
    /// <param name="context">The shared query plan context.</param>
    /// <param name="cancellationToken">Cancellation token for the entire operation.</param>
    /// <returns>An async enumerable of rows from all partitions.</returns>
    public async IAsyncEnumerable<QueryRow> ExecuteAsync(
        IReadOnlyList<IQueryPlanNode> partitions,
        QueryPlanContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (partitions.Count == 0) yield break;

        // If only 1 partition, no parallelism needed
        if (partitions.Count == 1)
        {
            await foreach (var row in partitions[0].ExecuteAsync(context, cancellationToken))
            {
                yield return row;
            }
            yield break;
        }

        var workQueue = new ConcurrentQueue<IQueryPlanNode>();
        foreach (var partition in partitions)
        {
            workQueue.Enqueue(partition);
        }

        var resultChannel = Channel.CreateBounded<QueryRow>(new BoundedChannelOptions(1000)
        {
            SingleWriter = false,
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait
        });

        _activeWorkers = 0;
        _busyWorkers = 0;
        _desiredWorkers = 1;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var workerTasks = new ConcurrentBag<Task>();
        _pendingWorkItems = partitions.Count;

        // Launch the monitor + initial workers
        var producerTask = Task.Run(async () =>
        {
            try
            {
                // Start the first worker
                LaunchWorker(workQueue, resultChannel, context, cts.Token, workerTasks);

                // Monitor loop
                while (!cts.Token.IsCancellationRequested)
                {
                    await Task.Delay(MonitorIntervalMs, cts.Token).ConfigureAwait(false);

                    var active = Volatile.Read(ref _activeWorkers);
                    var busy = Volatile.Read(ref _busyWorkers);

                    // Check if all work is done
                    if (workQueue.IsEmpty && busy == 0 && Volatile.Read(ref _pendingWorkItems) <= 0)
                    {
                        break;
                    }

                    // Scale up: all workers busy + pending work exists
                    if (busy >= active && !workQueue.IsEmpty && active < MaxDegreeOfParallelism)
                    {
                        Interlocked.Increment(ref _desiredWorkers);
                        LaunchWorker(workQueue, resultChannel, context, cts.Token, workerTasks);
                    }
                    // Scale down: idle workers (busy < active) and more than 1 worker
                    else if (busy < active - 1 && active > 1)
                    {
                        Interlocked.Decrement(ref _desiredWorkers);
                    }
                }

                // Wait for all workers to complete
                var allTasks = workerTasks.ToArray();
                if (allTasks.Length > 0)
                {
                    await Task.WhenAll(allTasks).ConfigureAwait(false);
                }

                resultChannel.Writer.Complete();
            }
            catch (OperationCanceledException)
            {
                resultChannel.Writer.Complete();
            }
            catch (Exception ex)
            {
                resultChannel.Writer.Complete(ex);
            }
        }, cancellationToken);

        // Read results
        await foreach (var row in resultChannel.Reader.ReadAllAsync(cancellationToken))
        {
            yield return row;
        }

        try
        {
            await producerTask.ConfigureAwait(false);
        }
        catch (Exception) when (producerTask.IsFaulted)
        {
            // Exception already surfaced through channel reader
        }
    }

    private void LaunchWorker(
        ConcurrentQueue<IQueryPlanNode> workQueue,
        Channel<QueryRow> resultChannel,
        QueryPlanContext context,
        CancellationToken cancellationToken,
        ConcurrentBag<Task> workerTasks)
    {
        Interlocked.Increment(ref _activeWorkers);

        var task = Task.Run(async () =>
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    // Check if we should shut down (desired workers decreased)
                    if (Volatile.Read(ref _activeWorkers) > Volatile.Read(ref _desiredWorkers))
                    {
                        break;
                    }

                    if (!workQueue.TryDequeue(out var partition))
                    {
                        break; // No more work
                    }

                    Interlocked.Increment(ref _busyWorkers);
                    try
                    {
                        await foreach (var row in partition.ExecuteAsync(context, cancellationToken))
                        {
                            await resultChannel.Writer.WriteAsync(row, cancellationToken).ConfigureAwait(false);
                        }
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _busyWorkers);
                        Interlocked.Decrement(ref _pendingWorkItems);
                    }
                }
            }
            finally
            {
                Interlocked.Decrement(ref _activeWorkers);
            }
        }, cancellationToken);

        workerTasks.Add(task);
    }
}
