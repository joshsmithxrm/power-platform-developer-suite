using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;

namespace PPDS.Dataverse.Query.Planning.Nodes;

/// <summary>
/// Optimized plan node for bare COUNT(*) queries that uses
/// RetrieveTotalRecordCountRequest for near-instant results.
/// Falls back to aggregate FetchXML if the optimized path fails.
/// </summary>
public sealed class CountOptimizedNode : IQueryPlanNode
{
    /// <summary>The entity to count records for.</summary>
    public string EntityLogicalName { get; }

    /// <summary>The alias for the count column in the result.</summary>
    public string CountAlias { get; }

    /// <summary>
    /// Fallback FetchXML scan node to use if the optimized count fails
    /// (e.g., entity doesn't support RetrieveTotalRecordCountRequest).
    /// </summary>
    public FetchXmlScanNode? FallbackNode { get; }

    /// <inheritdoc />
    public string Description => $"CountOptimized: {EntityLogicalName}";

    /// <inheritdoc />
    public long EstimatedRows => 1;

    /// <inheritdoc />
    public IReadOnlyList<IQueryPlanNode> Children =>
        FallbackNode != null ? new IQueryPlanNode[] { FallbackNode } : Array.Empty<IQueryPlanNode>();

    public CountOptimizedNode(string entityLogicalName, string countAlias, FetchXmlScanNode? fallbackNode = null)
    {
        EntityLogicalName = entityLogicalName ?? throw new ArgumentNullException(nameof(entityLogicalName));
        CountAlias = countAlias ?? throw new ArgumentNullException(nameof(countAlias));
        FallbackNode = fallbackNode;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<QueryRow> ExecuteAsync(
        QueryPlanContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Try the optimized count first
        long? count = null;
        try
        {
            count = await context.QueryExecutor.GetTotalRecordCountAsync(
                EntityLogicalName, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Swallow: some entities don't support RetrieveTotalRecordCountRequest.
            // We'll fall through to the fallback node below.
        }

        if (count.HasValue)
        {
            context.Statistics.IncrementRowsRead();
            var values = new Dictionary<string, QueryValue>(StringComparer.OrdinalIgnoreCase)
            {
                [CountAlias] = QueryValue.Simple(count.Value)
            };
            yield return new QueryRow(values, EntityLogicalName);
        }
        else if (FallbackNode != null)
        {
            // Fall back to aggregate FetchXML
            await foreach (var row in FallbackNode.ExecuteAsync(context, cancellationToken))
            {
                yield return row;
            }
        }
        // else: no fallback available, yield empty result
    }
}
