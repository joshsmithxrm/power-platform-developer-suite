using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;

namespace PPDS.Dataverse.Query.Planning.Nodes;

/// <summary>
/// Executes a SQL query directly against the Dataverse TDS Endpoint.
/// Leaf node in the execution plan tree — bypasses FetchXML transpilation
/// and sends the original SQL over the TDS wire protocol (port 5558).
/// </summary>
public sealed class TdsScanNode : IQueryPlanNode
{
    /// <summary>The original SQL query to execute via TDS.</summary>
    public string Sql { get; }

    /// <summary>The entity logical name being queried.</summary>
    public string EntityLogicalName { get; }

    /// <summary>Maximum rows to return, if any.</summary>
    public int? MaxRows { get; }

    /// <summary>The TDS query executor for SQL execution.</summary>
    public ITdsQueryExecutor TdsExecutor { get; }

    /// <inheritdoc />
    public string Description => $"TdsScan: {EntityLogicalName}" +
        (MaxRows.HasValue ? $" top {MaxRows}" : "");

    /// <inheritdoc />
    public long EstimatedRows => MaxRows ?? -1;

    /// <inheritdoc />
    public IReadOnlyList<IQueryPlanNode> Children => Array.Empty<IQueryPlanNode>();

    /// <summary>Initializes a new instance of the <see cref="TdsScanNode"/> class.</summary>
    public TdsScanNode(
        string sql,
        string entityLogicalName,
        ITdsQueryExecutor tdsExecutor,
        int? maxRows = null)
    {
        Sql = sql ?? throw new ArgumentNullException(nameof(sql));
        EntityLogicalName = entityLogicalName ?? throw new ArgumentNullException(nameof(entityLogicalName));
        TdsExecutor = tdsExecutor ?? throw new ArgumentNullException(nameof(tdsExecutor));
        MaxRows = maxRows;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<QueryRow> ExecuteAsync(
        QueryPlanContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var result = await TdsExecutor.ExecuteSqlAsync(
            Sql,
            MaxRows,
            cancellationToken).ConfigureAwait(false);

        context.Statistics.IncrementPagesFetched();

        foreach (var record in result.Records)
        {
            yield return QueryRow.FromRecord(record, result.EntityLogicalName);
            context.Statistics.IncrementRowsRead();
        }
    }
}
