using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Xml;
using System.Xml.Linq;
using PPDS.Dataverse.Query.Execution;

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

    /// <summary>
    /// Indicates whether the FetchXML query contains link-entity elements.
    /// When true, pagination may split a parent record's children across page boundaries,
    /// requiring merge logic to avoid duplicate parents with incomplete child sets.
    /// </summary>
    public bool HasLinkedEntity { get; }

    /// <summary>The prepared FetchXML for execution (top converted to count for paging compatibility).</summary>
    private readonly string _effectiveFetchXml;

    /// <summary>
    /// Tracks the primary key of the last parent record on the most recently fetched page.
    /// Used to detect page boundary splits when linked entities are present.
    /// </summary>
    private Guid? _lastParentId;

    /// <inheritdoc />
    public string Description => $"FetchXmlScan: {EntityLogicalName}" +
        (AutoPage ? " (all pages)" : " (single page)") +
        (MaxRows.HasValue ? $" top {MaxRows}" : "");

    /// <inheritdoc />
    public long EstimatedRows => MaxRows ?? -1;

    /// <inheritdoc />
    public IReadOnlyList<IQueryPlanNode> Children => Array.Empty<IQueryPlanNode>();

    /// <summary>Initializes a new instance of the <see cref="FetchXmlScanNode"/> class.</summary>
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
        HasLinkedEntity = DetectLinkedEntities(fetchXml);
        _effectiveFetchXml = PrepareFetchXmlForExecution(fetchXml);
    }

    /// <summary>
    /// Records the primary key of the last parent record on the current page.
    /// Called at the end of each page when <see cref="HasLinkedEntity"/> is true
    /// to enable boundary split detection on the next page.
    /// </summary>
    /// <param name="parentId">The primary key of the last parent record on the current page.</param>
    public void SetLastParentId(Guid parentId)
    {
        _lastParentId = parentId;
    }

    /// <summary>
    /// Determines whether the first record on a new page should be merged with the
    /// last parent from the previous page, rather than treated as a new parent.
    /// This occurs when a parent record's child rows span a page boundary.
    /// </summary>
    /// <param name="firstParentId">The primary key of the first parent record on the new page.</param>
    /// <returns>
    /// <c>true</c> if the record is a continuation of a parent from the previous page
    /// and its child records should be merged; <c>false</c> otherwise.
    /// </returns>
    public bool ShouldMergeWithPreviousPage(Guid firstParentId)
    {
        return HasLinkedEntity
            && _lastParentId.HasValue
            && _lastParentId.Value == firstParentId;
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

            QueryResult result;
            try
            {
                result = await context.QueryExecutor.ExecuteFetchXmlAsync(
                    _effectiveFetchXml,
                    pageNumber,
                    pagingCookie,
                    includeCount: IncludeCount,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw; // Respect cancellation
            }
            catch (Exception ex) when (IsAggregateLimitExceeded(ex))
            {
                throw new QueryExecutionException(
                    QueryErrorCode.AggregateLimitExceeded,
                    "Aggregate query exceeded the Dataverse 50,000 record limit. " +
                    "Consider adding more restrictive filters or partitioning by date range.",
                    ex);
            }

            context.Statistics.IncrementPagesFetched();

            // Store paging metadata for caller-controlled paging scenarios.
            // Suppressed during parallel execution to avoid thread-unsafe writes.
            if (!context.Statistics.SuppressPagingMetadata)
            {
                context.Statistics.LastPagingCookie = result.PagingCookie;
                context.Statistics.LastMoreRecords = result.MoreRecords;
                context.Statistics.LastPageNumber = result.PageNumber;
                context.Statistics.LastTotalCount = result.TotalCount;
            }

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

    /// <summary>
    /// Detects Dataverse AggregateQueryRecordLimit errors from exception messages.
    /// </summary>
    private static bool IsAggregateLimitExceeded(Exception ex)
    {
        var current = ex;
        while (current != null)
        {
            if (current.Message.Contains("AggregateQueryRecordLimit", StringComparison.OrdinalIgnoreCase)
                || current.Message.Contains("aggregate operation exceeded", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            current = current.InnerException;
        }
        return false;
    }

    /// <summary>
    /// Detects whether the FetchXML contains link-entity elements.
    /// Uses a fast string check before falling back to XML parsing for accuracy.
    /// </summary>
    private static bool DetectLinkedEntities(string fetchXml)
    {
        // Quick string check to avoid XML parsing overhead for simple queries
        if (!fetchXml.Contains("link-entity", StringComparison.OrdinalIgnoreCase))
            return false;

        try
        {
            return XDocument.Parse(fetchXml)
                .Descendants("link-entity")
                .Any();
        }
        catch (XmlException)
        {
            return false; // If FetchXML is malformed, assume no linked entities
        }
    }

    /// <summary>
    /// Prepares FetchXML for execution by resolving the top/page attribute conflict.
    /// Dataverse rejects FetchXML with both 'top' and 'page' attributes.
    /// When 'top' is present, converts it to 'count' (page size) so paging works.
    /// Client-side MaxRows limiting handles the actual row cap.
    /// </summary>
    private static string PrepareFetchXmlForExecution(string fetchXml)
    {
        // Quick check to avoid XML parsing overhead for non-top queries
        if (!fetchXml.Contains("top=", StringComparison.OrdinalIgnoreCase))
            return fetchXml;

        try
        {
            var doc = XDocument.Parse(fetchXml);
            var fetchElement = doc.Root;
            if (fetchElement == null) return fetchXml;

            var topAttr = fetchElement.Attribute("top");
            if (topAttr == null) return fetchXml;

            if (int.TryParse(topAttr.Value, out var topValue))
            {
                topAttr.Remove();
                // Use the top value as count (page size), capped at 5000 (Dataverse max page size)
                fetchElement.SetAttributeValue("count", Math.Min(topValue, 5000).ToString());
            }

            return doc.ToString(SaveOptions.DisableFormatting);
        }
        catch (XmlException)
        {
            return fetchXml; // If FetchXML is malformed, return original and let Dataverse report the error
        }
    }
}
