using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using PPDS.Dataverse.Query.Execution;

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
                // Detect Dataverse AggregateQueryRecordLimit (50K) failures
                // and wrap in a structured QueryExecutionException so the CLI
                // can map to ErrorCodes.Query.AggregateLimitExceeded.
                var wrapped = WrapIfAggregateLimitExceeded(ex);
                channel.Writer.Complete(wrapped);
            }
        }, cancellationToken);

        // Read results from channel
        await foreach (var row in channel.Reader.ReadAllAsync(cancellationToken))
        {
            yield return row;
        }

        await producerTask.ConfigureAwait(false);
    }

    /// <summary>
    /// Detects Dataverse AggregateQueryRecordLimit errors and wraps them
    /// in a <see cref="QueryExecutionException"/> with a structured error code.
    /// Returns the original exception if it is not an aggregate limit error.
    /// </summary>
    private static Exception WrapIfAggregateLimitExceeded(Exception ex)
    {
        // Check the exception chain for aggregate limit messages from Dataverse.
        // The Dataverse SDK throws FaultException with "AggregateQueryRecordLimit"
        // in the message text when an aggregate query scans over 50,000 records.
        var current = ex;
        while (current != null)
        {
            if (current.Message.Contains("AggregateQueryRecordLimit", StringComparison.OrdinalIgnoreCase)
                || current.Message.Contains("aggregate operation exceeded", StringComparison.OrdinalIgnoreCase))
            {
                return new QueryExecutionException(
                    QueryErrorCode.AggregateLimitExceeded,
                    "Aggregate query exceeded the Dataverse 50,000 record limit. " +
                    "Consider partitioning the query by date range or adding more restrictive filters.",
                    ex);
            }
            current = current.InnerException;
        }

        return ex;
    }
}
