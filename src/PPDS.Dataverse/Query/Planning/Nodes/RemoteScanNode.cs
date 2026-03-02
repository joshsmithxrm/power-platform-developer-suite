using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using PPDS.Dataverse.Query.Execution;

namespace PPDS.Dataverse.Query.Planning.Nodes;

/// <summary>
/// Executes a FetchXML query against a remote environment's connection pool.
/// Used for cross-environment queries where bracket syntax ([LABEL].entity)
/// references a different Dataverse environment.
/// </summary>
public sealed class RemoteScanNode : IQueryPlanNode
{
    private readonly string _fetchXml;
    private readonly IQueryExecutor _remoteExecutor;
    private readonly int _maxPages;

    /// <summary>The entity being queried on the remote environment.</summary>
    public string EntityLogicalName { get; }

    /// <summary>The label of the remote environment (e.g., "UAT", "PROD").</summary>
    public string RemoteLabel { get; }

    /// <inheritdoc />
    public string Description => $"RemoteScan: [{RemoteLabel}].{EntityLogicalName}";

    /// <inheritdoc />
    public long EstimatedRows => 1000;

    /// <inheritdoc />
    public IReadOnlyList<IQueryPlanNode> Children => Array.Empty<IQueryPlanNode>();

    /// <summary>Initializes a new instance of the <see cref="RemoteScanNode"/> class.</summary>
    /// <param name="fetchXml">The FetchXML query to execute.</param>
    /// <param name="entityLogicalName">The entity being queried.</param>
    /// <param name="remoteLabel">The label of the remote environment.</param>
    /// <param name="remoteExecutor">The query executor for the remote environment.</param>
    /// <param name="maxPages">Maximum number of pages to fetch before throwing (default 200).</param>
    public RemoteScanNode(
        string fetchXml,
        string entityLogicalName,
        string remoteLabel,
        IQueryExecutor remoteExecutor,
        int maxPages = 200)
    {
        _fetchXml = fetchXml ?? throw new ArgumentNullException(nameof(fetchXml));
        EntityLogicalName = entityLogicalName ?? throw new ArgumentNullException(nameof(entityLogicalName));
        RemoteLabel = remoteLabel ?? throw new ArgumentNullException(nameof(remoteLabel));
        _remoteExecutor = remoteExecutor ?? throw new ArgumentNullException(nameof(remoteExecutor));
        _maxPages = maxPages;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<QueryRow> ExecuteAsync(
        QueryPlanContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Execute FetchXML against the REMOTE executor (not context.QueryExecutor)
        // with auto-paging to retrieve all pages.
        string? pagingCookie = null;
        var pageNumber = 1;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await _remoteExecutor.ExecuteFetchXmlAsync(
                _fetchXml, pageNumber, pagingCookie,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            context.Statistics.IncrementPagesFetched();

            foreach (var record in result.Records)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return QueryRow.FromRecord(record, result.EntityLogicalName);
                context.Statistics.IncrementRowsRead();
            }

            if (!result.MoreRecords) yield break;
            pagingCookie = result.PagingCookie;
            pageNumber++;

            if (pageNumber > _maxPages)
            {
                throw new QueryExecutionException(
                    QueryErrorCode.ExecutionFailed,
                    $"Remote query against [{RemoteLabel}] exceeded maximum page limit ({_maxPages}). " +
                    "Add a WHERE clause to narrow results or increase the limit.");
            }
        }
    }
}
