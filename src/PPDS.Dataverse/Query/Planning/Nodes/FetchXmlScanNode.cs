using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;

namespace PPDS.Dataverse.Query.Planning.Nodes;

/// <summary>
/// Executes a FetchXML query and yields rows page-by-page.
/// Leaf node in the execution plan tree.
/// </summary>
public sealed class FetchXmlScanNode : IQueryPlanNode
{
    /// <summary>The FetchXML query to execute.</summary>
    public string FetchXml { get; }

    /// <summary>The entity logical name being queried.</summary>
    public string EntityLogicalName { get; }

    /// <summary>If true, automatically fetch all pages. If false, single page only.</summary>
    public bool AutoPage { get; }

    /// <summary>Maximum rows to return, if any.</summary>
    public int? MaxRows { get; }

    /// <summary>
    /// Starting page number for caller-controlled paging (1-based).
    /// When set with <see cref="AutoPage"/> = false, fetches only this page.
    /// </summary>
    public int? InitialPageNumber { get; }

    /// <summary>
    /// Paging cookie for caller-controlled paging continuation.
    /// </summary>
    public string? InitialPagingCookie { get; }

    /// <summary>Whether to request total record count from Dataverse.</summary>
    public bool IncludeCount { get; }

    /// <inheritdoc />
    public string Description => $"FetchXmlScan: {EntityLogicalName}" +
        (AutoPage ? " (all pages)" : " (single page)") +
        (MaxRows.HasValue ? $" top {MaxRows}" : "");

    /// <inheritdoc />
    public long EstimatedRows => MaxRows ?? -1;

    /// <inheritdoc />
    public IReadOnlyList<IQueryPlanNode> Children => Array.Empty<IQueryPlanNode>();

    public FetchXmlScanNode(
        string fetchXml,
        string entityLogicalName,
        bool autoPage = true,
        int? maxRows = null,
        int? initialPageNumber = null,
        string? initialPagingCookie = null,
        bool includeCount = false)
    {
        FetchXml = fetchXml ?? throw new ArgumentNullException(nameof(fetchXml));
        EntityLogicalName = entityLogicalName ?? throw new ArgumentNullException(nameof(entityLogicalName));
        AutoPage = autoPage;
        MaxRows = maxRows;
        InitialPageNumber = initialPageNumber;
        InitialPagingCookie = initialPagingCookie;
        IncludeCount = includeCount;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<QueryRow> ExecuteAsync(
        QueryPlanContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var rowCount = 0;
        string? pagingCookie = InitialPagingCookie;
        var pageNumber = InitialPageNumber ?? 1;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var result = await context.QueryExecutor.ExecuteFetchXmlAsync(
                FetchXml,
                pageNumber,
                pagingCookie,
                includeCount: IncludeCount,
                cancellationToken).ConfigureAwait(false);

            context.Statistics.IncrementPagesFetched();

            // Store paging metadata for caller-controlled paging scenarios
            context.Statistics.LastPagingCookie = result.PagingCookie;
            context.Statistics.LastMoreRecords = result.MoreRecords;
            context.Statistics.LastPageNumber = result.PageNumber;
            context.Statistics.LastTotalCount = result.TotalCount;

            foreach (var record in result.Records)
            {
                if (MaxRows.HasValue && rowCount >= MaxRows.Value)
                {
                    yield break;
                }

                yield return QueryRow.FromRecord(record, result.EntityLogicalName);
                rowCount++;
                context.Statistics.IncrementRowsRead();
            }

            if (!AutoPage || !result.MoreRecords)
            {
                yield break;
            }

            pagingCookie = result.PagingCookie;
            pageNumber++;
        }
    }
}
