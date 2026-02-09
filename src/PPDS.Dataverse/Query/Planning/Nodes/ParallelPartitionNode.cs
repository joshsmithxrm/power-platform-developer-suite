using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace PPDS.Dataverse.Query.Planning.Nodes;

/// <summary>
/// Executes child plan nodes in parallel, limited by pool capacity.
/// Used for parallel aggregate partitioning.
/// </summary>
public sealed class ParallelPartitionNode : IQueryPlanNode
{
    /// <summary>The child plan nodes representing each partition.</summary>
    public IReadOnlyList<IQueryPlanNode> Partitions { get; }

    /// <summary>The maximum number of partitions to execute concurrently.</summary>
    public int MaxParallelism { get; }

    /// <inheritdoc />
    public string Description => $"ParallelPartition: {Partitions.Count} partitions, max parallelism {MaxParallelism}";

    /// <inheritdoc />
    public long EstimatedRows => -1; // Unknown until merged

    /// <inheritdoc />
    public IReadOnlyList<IQueryPlanNode> Children => Partitions;

    /// <summary>Initializes a new instance of the <see cref="ParallelPartitionNode"/> class.</summary>
    public ParallelPartitionNode(IReadOnlyList<IQueryPlanNode> partitions, int maxParallelism)
    {
        Partitions = partitions ?? throw new ArgumentNullException(nameof(partitions));
        MaxParallelism = maxParallelism > 0 ? maxParallelism : throw new ArgumentOutOfRangeException(nameof(maxParallelism));
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<QueryRow> ExecuteAsync(
        QueryPlanContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Suppress paging metadata writes — each partition has its own paging
        // state and writing from multiple threads would be a data race.
        context.Statistics.SuppressPagingMetadata = true;

        context.ProgressReporter?.ReportPhase("Parallel Aggregation",
            $"Executing {Partitions.Count} partitions across {MaxParallelism} connections");

        // Use a bounded channel to collect results from all partitions
        var channel = Channel.CreateBounded<QueryRow>(new BoundedChannelOptions(1000)
        {
            SingleWriter = false,
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait
        });

        using var semaphore = new SemaphoreSlim(MaxParallelism);

        // Launch all partition tasks. The try/catch ensures the channel is
        // always completed (with or without an exception) so the consumer
        // never deadlocks waiting on a channel that will never close.
        var completedCount = 0;

        var producerTask = Task.Run(async () =>
        {
            try
            {
                var tasks = new List<Task>();

                foreach (var partition in Partitions)
                {
                    await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);

                    var task = Task.Run(async () =>
                    {
                        try
                        {
                            await foreach (var row in partition.ExecuteAsync(context, cancellationToken))
                            {
                                await channel.Writer.WriteAsync(row, cancellationToken).ConfigureAwait(false);
                            }
                        }
                        finally
                        {
                            semaphore.Release();
                            var completed = Interlocked.Increment(ref completedCount);
                            context.ProgressReporter?.ReportProgress(
                                completed, Partitions.Count,
                                $"Partition {completed}/{Partitions.Count} complete");
                        }
                    }, cancellationToken);

                    tasks.Add(task);
                }

                await Task.WhenAll(tasks).ConfigureAwait(false);
                channel.Writer.Complete();
            }
            catch (Exception ex)
            {
                channel.Writer.Complete(ex);
            }
        }, cancellationToken);

        // Read results from channel
        await foreach (var row in channel.Reader.ReadAllAsync(cancellationToken))
        {
            yield return row;
        }

        // The producer task's exception (if any) was already propagated through
        // channel.Writer.Complete(ex) → ReadAllAsync. Await only to observe
        // completion; suppress the duplicate throw.
        try
        {
            await producerTask.ConfigureAwait(false);
        }
        catch (Exception) when (producerTask.IsFaulted)
        {
            // Exception already surfaced through channel reader — suppress duplicate
        }
    }

}
