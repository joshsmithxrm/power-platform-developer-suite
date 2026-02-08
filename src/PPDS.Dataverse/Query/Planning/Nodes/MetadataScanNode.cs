using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using PPDS.Dataverse.Metadata;
using PPDS.Dataverse.Sql.Ast;

namespace PPDS.Dataverse.Query.Planning.Nodes;

/// <summary>
/// Scan node that queries Dataverse metadata API instead of entity data.
/// Used for metadata.entity, metadata.attribute, etc. virtual tables.
/// Leaf node in the execution plan tree.
/// </summary>
public sealed class MetadataScanNode : IQueryPlanNode
{
    /// <summary>The metadata virtual table name (e.g., "entity", "attribute").</summary>
    public string MetadataTable { get; }

    /// <summary>Columns to return from the metadata table (null = all).</summary>
    public IReadOnlyList<string>? RequestedColumns { get; }

    /// <summary>Optional client-side filter condition.</summary>
    public ISqlCondition? Filter { get; }

    /// <summary>The metadata query executor.</summary>
    public IMetadataQueryExecutor MetadataExecutor { get; }

    /// <inheritdoc />
    public string Description => $"MetadataScan: metadata.{MetadataTable}" +
        (Filter != null ? " (filtered)" : "");

    /// <inheritdoc />
    public long EstimatedRows => -1;

    /// <inheritdoc />
    public IReadOnlyList<IQueryPlanNode> Children => Array.Empty<IQueryPlanNode>();

    public MetadataScanNode(
        string metadataTable,
        IMetadataQueryExecutor? metadataExecutor,
        IReadOnlyList<string>? requestedColumns = null,
        ISqlCondition? filter = null)
    {
        MetadataTable = metadataTable ?? throw new ArgumentNullException(nameof(metadataTable));
        MetadataExecutor = metadataExecutor!; // May be null at plan time; resolved from context at execution
        RequestedColumns = requestedColumns;
        Filter = filter;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<QueryRow> ExecuteAsync(
        QueryPlanContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Use the executor provided at construction, or fall back to the context's executor
        var executor = MetadataExecutor ?? context.MetadataQueryExecutor
            ?? throw new InvalidOperationException(
                "No metadata query executor available. Ensure a MetadataQueryExecutor is configured.");

        var records = await executor.QueryMetadataAsync(
            MetadataTable,
            RequestedColumns,
            cancellationToken).ConfigureAwait(false);

        foreach (var record in records)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Apply client-side filter if present
            if (Filter != null)
            {
                if (!context.ExpressionEvaluator.EvaluateCondition(Filter, record))
                {
                    continue;
                }
            }

            yield return new QueryRow(record, $"metadata.{MetadataTable}");
            context.Statistics.IncrementRowsRead();
        }
    }
}
