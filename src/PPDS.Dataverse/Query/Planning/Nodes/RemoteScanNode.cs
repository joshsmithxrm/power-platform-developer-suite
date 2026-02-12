using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;

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
    public RemoteScanNode(
        string fetchXml,
        string entityLogicalName,
        string remoteLabel,
        IQueryExecutor remoteExecutor)
    {
        _fetchXml = fetchXml ?? throw new ArgumentNullException(nameof(fetchXml));
        EntityLogicalName = entityLogicalName ?? throw new ArgumentNullException(nameof(entityLogicalName));
        RemoteLabel = remoteLabel ?? throw new ArgumentNullException(nameof(remoteLabel));
        _remoteExecutor = remoteExecutor ?? throw new ArgumentNullException(nameof(remoteExecutor));
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<QueryRow> ExecuteAsync(
        QueryPlanContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Execute FetchXML against the REMOTE executor (not context.QueryExecutor).
        // No auto-paging: single page execution for cross-environment scan.
        var result = await _remoteExecutor.ExecuteFetchXmlAsync(
            _fetchXml,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        foreach (var record in result.Records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return QueryRow.FromRecord(record, result.EntityLogicalName);
        }
    }
}
