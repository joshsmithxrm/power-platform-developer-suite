using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading;

namespace PPDS.Dataverse.Query.Planning.Nodes;

/// <summary>
/// Wraps a FetchXML aggregate scan with adaptive retry. When the Dataverse
/// 50K AggregateQueryRecordLimit is hit, splits the date range in half and
/// retries both halves recursively. Guarantees convergence for any data
/// distribution.
/// </summary>
public sealed class AdaptiveAggregateScanNode : IQueryPlanNode
{
    /// <summary>The template FetchXML (without date range filter).</summary>
    public string TemplateFetchXml { get; }

    /// <summary>The entity logical name being queried.</summary>
    public string EntityLogicalName { get; }

    /// <summary>Inclusive start of this partition's date range.</summary>
    public DateTime RangeStart { get; }

    /// <summary>Exclusive end of this partition's date range.</summary>
    public DateTime RangeEnd { get; }

    /// <summary>Current recursion depth (0 = original partition).</summary>
    public int Depth { get; }

    /// <summary>Maximum recursion depth to prevent infinite splitting.</summary>
    public const int MaxDepth = 15;

    /// <inheritdoc />
    public string Description =>
        $"AdaptiveAggregateScan: {EntityLogicalName} [{RangeStart:yyyy-MM-dd} .. {RangeEnd:yyyy-MM-dd})" +
        (Depth > 0 ? $" depth={Depth}" : "");

    /// <inheritdoc />
    public long EstimatedRows => -1;

    /// <inheritdoc />
    public IReadOnlyList<IQueryPlanNode> Children => Array.Empty<IQueryPlanNode>();

    /// <summary>Initializes a new instance of the <see cref="AdaptiveAggregateScanNode"/> class.</summary>
    public AdaptiveAggregateScanNode(
        string templateFetchXml,
        string entityLogicalName,
        DateTime rangeStart,
        DateTime rangeEnd,
        int depth = 0)
    {
        TemplateFetchXml = templateFetchXml ?? throw new ArgumentNullException(nameof(templateFetchXml));
        EntityLogicalName = entityLogicalName ?? throw new ArgumentNullException(nameof(entityLogicalName));
        RangeStart = rangeStart;
        RangeEnd = rangeEnd;
        Depth = depth;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<QueryRow> ExecuteAsync(
        QueryPlanContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Inject date range filter into template FetchXML
        var fetchXml = InjectDateRangeFilter(TemplateFetchXml, RangeStart, RangeEnd);
        var scanNode = new FetchXmlScanNode(fetchXml, EntityLogicalName, autoPage: false);

        List<QueryRow>? rows = null;
        AdaptiveAggregateScanNode? leftNode = null;
        AdaptiveAggregateScanNode? rightNode = null;

        try
        {
            // Try executing the aggregate query for this date range
            rows = new List<QueryRow>();
            await foreach (var row in scanNode.ExecuteAsync(context, cancellationToken))
            {
                rows.Add(row);
            }
        }
        catch (Exception ex) when (IsAggregateLimitExceeded(ex) && Depth < MaxDepth)
        {
            // This partition is too large — split and retry
            rows = null;

            var midTicks = RangeStart.Ticks + (RangeEnd.Ticks - RangeStart.Ticks) / 2;
            var midPoint = new DateTime(midTicks, DateTimeKind.Utc);

            // Guard: if the range can't be split further (start == mid), give up
            if (midPoint <= RangeStart || midPoint >= RangeEnd)
            {
                throw new Execution.QueryExecutionException(
                    Execution.QueryErrorCode.AggregateLimitExceeded,
                    $"Aggregate query exceeded the Dataverse 50,000 record limit and the date range " +
                    $"[{RangeStart:O} .. {RangeEnd:O}) cannot be split further.",
                    ex);
            }

            leftNode = new AdaptiveAggregateScanNode(
                TemplateFetchXml, EntityLogicalName, RangeStart, midPoint, Depth + 1);
            rightNode = new AdaptiveAggregateScanNode(
                TemplateFetchXml, EntityLogicalName, midPoint, RangeEnd, Depth + 1);
        }

        // Execute both halves sequentially outside the catch block
        // (parallelism is handled by the parent ParallelPartitionNode
        // across partitions, not within one)
        if (leftNode != null)
        {
            await foreach (var row in leftNode.ExecuteAsync(context, cancellationToken))
            {
                yield return row;
            }
        }

        if (rightNode != null)
        {
            await foreach (var row in rightNode.ExecuteAsync(context, cancellationToken))
            {
                yield return row;
            }
        }

        // Yield collected rows (only reached if the initial attempt succeeded)
        if (rows != null)
        {
            foreach (var row in rows)
            {
                yield return row;
            }
        }
    }

    /// <summary>
    /// Checks whether an exception indicates the Dataverse 50K aggregate limit was exceeded.
    /// </summary>
    private static bool IsAggregateLimitExceeded(Exception ex)
    {
        var current = ex;
        while (current != null)
        {
            if (current.Message.Contains("AggregateQueryRecordLimit", StringComparison.OrdinalIgnoreCase)
                || current.Message.Contains("aggregate operation exceeded", StringComparison.OrdinalIgnoreCase)
                || current.Message.Contains("maximum record limit of 50000", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            current = current.InnerException;
        }
        return false;
    }

    /// <summary>
    /// Injects a date range filter into FetchXML for partition-based aggregate queries.
    /// </summary>
    private static string InjectDateRangeFilter(string fetchXml, DateTime start, DateTime end)
    {
        var startStr = start.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        var endStr = end.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

        var filterXml =
            $"    <filter type=\"and\">\n" +
            $"      <condition attribute=\"createdon\" operator=\"ge\" value=\"{startStr}\" />\n" +
            $"      <condition attribute=\"createdon\" operator=\"lt\" value=\"{endStr}\" />\n" +
            $"    </filter>";

        var entityCloseIndex = fetchXml.LastIndexOf("</entity>", StringComparison.Ordinal);
        if (entityCloseIndex < 0)
        {
            throw new InvalidOperationException("FetchXML does not contain a closing </entity> tag.");
        }

        return fetchXml[..entityCloseIndex] + filterXml + "\n" + fetchXml[entityCloseIndex..];
    }
}
