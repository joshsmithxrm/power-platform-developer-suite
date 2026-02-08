using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace PPDS.Dataverse.Query.Planning.Nodes;

/// <summary>
/// Wraps any IQueryPlanNode to speculatively prefetch rows into a bounded buffer.
/// The background producer fetches from the source node while the consumer reads ahead.
/// Uses System.Threading.Channels for efficient producer-consumer with backpressure.
/// </summary>
public sealed class PrefetchScanNode : IQueryPlanNode
{
    /// <summary>The source node to prefetch from.</summary>
    public IQueryPlanNode Source { get; }

    /// <summary>Number of rows to buffer ahead (controls memory usage).</summary>
    public int BufferSize { get; }

    /// <inheritdoc />
    public string Description => $"Prefetch: buffer {BufferSize} from [{Source.Description}]";

    /// <inheritdoc />
    public long EstimatedRows => Source.EstimatedRows;

    /// <inheritdoc />
    public IReadOnlyList<IQueryPlanNode> Children => new[] { Source };

    /// <summary>
    /// Creates a prefetch wrapper around a source node.
    /// </summary>
    /// <param name="source">The source node to prefetch from.</param>
    /// <param name="bufferSize">Maximum rows to buffer ahead (default: 5000 = ~3 FetchXML pages).</param>
    public PrefetchScanNode(IQueryPlanNode source, int bufferSize = 5000)
    {
        Source = source ?? throw new ArgumentNullException(nameof(source));
        BufferSize = bufferSize > 0 ? bufferSize : throw new ArgumentOutOfRangeException(nameof(bufferSize));
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<QueryRow> ExecuteAsync(
        QueryPlanContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var channel = Channel.CreateBounded<QueryRow>(new BoundedChannelOptions(BufferSize)
        {
            SingleWriter = true,
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait
        });

        // Background producer: reads from source and writes to channel
        var producerTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var row in Source.ExecuteAsync(context, cancellationToken))
                {
                    await channel.Writer.WriteAsync(row, cancellationToken).ConfigureAwait(false);
                }
                channel.Writer.Complete();
            }
            catch (Exception ex)
            {
                channel.Writer.Complete(ex);
            }
        }, cancellationToken);

        // Consumer: reads from channel and yields to caller
        await foreach (var row in channel.Reader.ReadAllAsync(cancellationToken))
        {
            yield return row;
        }

        // Await producer to propagate any exceptions
        await producerTask.ConfigureAwait(false);
    }
}
