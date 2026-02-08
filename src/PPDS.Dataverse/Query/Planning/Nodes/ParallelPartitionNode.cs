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
    public IReadOnlyList<IQueryPlanNode> Partitions { get; }
    public int MaxParallelism { get; }

    public string Description => $"ParallelPartition: {Partitions.Count} partitions, max parallelism {MaxParallelism}";
    public long EstimatedRows => -1; // Unknown until merged
    public IReadOnlyList<IQueryPlanNode> Children => Partitions;

    public ParallelPartitionNode(IReadOnlyList<IQueryPlanNode> partitions, int maxParallelism)
    {
        Partitions = partitions ?? throw new ArgumentNullException(nameof(partitions));
        MaxParallelism = maxParallelism > 0 ? maxParallelism : throw new ArgumentOutOfRangeException(nameof(maxParallelism));
    }

    public async IAsyncEnumerable<QueryRow> ExecuteAsync(
        QueryPlanContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Use a bounded channel to collect results from all partitions
        var channel = Channel.CreateBounded<QueryRow>(new BoundedChannelOptions(1000)
        {
            SingleWriter = false,
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait
        });

        var semaphore = new SemaphoreSlim(MaxParallelism);

        // Launch all partition tasks
        var producerTask = Task.Run(async () =>
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
                    }
                }, cancellationToken);

                tasks.Add(task);
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);
            channel.Writer.Complete();
        }, cancellationToken);

        // Read results from channel
        await foreach (var row in channel.Reader.ReadAllAsync(cancellationToken))
        {
            yield return row;
        }

        await producerTask.ConfigureAwait(false);
    }
}
