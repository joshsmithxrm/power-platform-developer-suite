using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using PPDS.Dataverse.BulkOperations;
using PPDS.Dataverse.Progress;
using PPDS.Dataverse.Query.Execution;
using PPDS.Dataverse.Sql.Ast;

namespace PPDS.Dataverse.Query.Planning.Nodes;

/// <summary>
/// Executes DML operations (INSERT, UPDATE, DELETE) using BulkOperationExecutor.
/// Returns a single row with the affected row count.
/// </summary>
public sealed class DmlExecuteNode : IQueryPlanNode
{
    /// <summary>The type of DML operation.</summary>
    public DmlOperation Operation { get; }

    /// <summary>The target entity logical name.</summary>
    public string EntityLogicalName { get; }

    /// <summary>Source node that produces rows (for INSERT...SELECT, UPDATE, DELETE).</summary>
    public IQueryPlanNode? SourceNode { get; }

    /// <summary>Column names for INSERT statements.</summary>
    public IReadOnlyList<string>? InsertColumns { get; }

    /// <summary>Value rows for INSERT VALUES statements.</summary>
    public IReadOnlyList<IReadOnlyList<ISqlExpression>>? InsertValueRows { get; }

    /// <summary>SET clauses for UPDATE statements.</summary>
    public IReadOnlyList<SqlSetClause>? SetClauses { get; }

    /// <summary>Source column names for INSERT...SELECT ordinal mapping.</summary>
    public IReadOnlyList<string>? SourceColumns { get; }

    /// <summary>Row cap from DML safety guard.</summary>
    public int RowCap { get; }

    /// <inheritdoc />
    public string Description => Operation switch
    {
        DmlOperation.Insert when InsertValueRows != null =>
            $"DmlExecute: INSERT {EntityLogicalName} ({InsertValueRows.Count} rows)",
        DmlOperation.Insert =>
            $"DmlExecute: INSERT {EntityLogicalName} (from SELECT)",
        DmlOperation.Update =>
            $"DmlExecute: UPDATE {EntityLogicalName}",
        DmlOperation.Delete =>
            $"DmlExecute: DELETE {EntityLogicalName}",
        _ => $"DmlExecute: {Operation} {EntityLogicalName}"
    };

    /// <inheritdoc />
    public long EstimatedRows => 1; // Returns single row with affected count

    /// <inheritdoc />
    public IReadOnlyList<IQueryPlanNode> Children =>
        SourceNode != null ? new[] { SourceNode } : Array.Empty<IQueryPlanNode>();

    /// <summary>
    /// Creates a DmlExecuteNode for INSERT VALUES.
    /// </summary>
    public static DmlExecuteNode InsertValues(
        string entityLogicalName,
        IReadOnlyList<string> columns,
        IReadOnlyList<IReadOnlyList<ISqlExpression>> valueRows,
        int rowCap = int.MaxValue)
    {
        return new DmlExecuteNode(
            DmlOperation.Insert,
            entityLogicalName,
            insertColumns: columns,
            insertValueRows: valueRows,
            rowCap: rowCap);
    }

    /// <summary>
    /// Creates a DmlExecuteNode for INSERT SELECT.
    /// </summary>
    public static DmlExecuteNode InsertSelect(
        string entityLogicalName,
        IReadOnlyList<string> columns,
        IQueryPlanNode sourceNode,
        IReadOnlyList<string>? sourceColumns = null,
        int rowCap = int.MaxValue)
    {
        return new DmlExecuteNode(
            DmlOperation.Insert,
            entityLogicalName,
            sourceNode: sourceNode,
            insertColumns: columns,
            sourceColumns: sourceColumns,
            rowCap: rowCap);
    }

    /// <summary>
    /// Creates a DmlExecuteNode for UPDATE.
    /// </summary>
    public static DmlExecuteNode Update(
        string entityLogicalName,
        IQueryPlanNode sourceNode,
        IReadOnlyList<SqlSetClause> setClauses,
        int rowCap = int.MaxValue)
    {
        return new DmlExecuteNode(
            DmlOperation.Update,
            entityLogicalName,
            sourceNode: sourceNode,
            setClauses: setClauses,
            rowCap: rowCap);
    }

    /// <summary>
    /// Creates a DmlExecuteNode for DELETE.
    /// </summary>
    public static DmlExecuteNode Delete(
        string entityLogicalName,
        IQueryPlanNode sourceNode,
        int rowCap = int.MaxValue)
    {
        return new DmlExecuteNode(
            DmlOperation.Delete,
            entityLogicalName,
            sourceNode: sourceNode,
            rowCap: rowCap);
    }

    private DmlExecuteNode(
        DmlOperation operation,
        string entityLogicalName,
        IQueryPlanNode? sourceNode = null,
        IReadOnlyList<string>? insertColumns = null,
        IReadOnlyList<IReadOnlyList<ISqlExpression>>? insertValueRows = null,
        IReadOnlyList<SqlSetClause>? setClauses = null,
        IReadOnlyList<string>? sourceColumns = null,
        int rowCap = int.MaxValue)
    {
        Operation = operation;
        EntityLogicalName = entityLogicalName ?? throw new ArgumentNullException(nameof(entityLogicalName));
        SourceNode = sourceNode;
        InsertColumns = insertColumns;
        InsertValueRows = insertValueRows;
        SetClauses = setClauses;
        SourceColumns = sourceColumns;
        RowCap = rowCap;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<QueryRow> ExecuteAsync(
        QueryPlanContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (context.BulkOperationExecutor == null)
        {
            throw new QueryExecutionException(
                QueryErrorCode.ExecutionFailed,
                "BulkOperationExecutor is required for DML operations. " +
                "Provide it via QueryPlanContext.");
        }

        long affectedCount;

        switch (Operation)
        {
            case DmlOperation.Insert when InsertValueRows != null:
                affectedCount = await ExecuteInsertValuesAsync(context, cancellationToken)
                    .ConfigureAwait(false);
                break;

            case DmlOperation.Insert when SourceNode != null:
                affectedCount = await ExecuteInsertSelectAsync(context, cancellationToken)
                    .ConfigureAwait(false);
                break;

            case DmlOperation.Update:
                affectedCount = await ExecuteUpdateAsync(context, cancellationToken)
                    .ConfigureAwait(false);
                break;

            case DmlOperation.Delete:
                affectedCount = await ExecuteDeleteAsync(context, cancellationToken)
                    .ConfigureAwait(false);
                break;

            default:
                throw new QueryExecutionException(
                    QueryErrorCode.ExecutionFailed,
                    $"Unsupported DML operation: {Operation}");
        }

        // Return a single row with the affected count
        var values = new Dictionary<string, QueryValue>
        {
            ["affected_rows"] = QueryValue.Simple(affectedCount)
        };
        yield return new QueryRow(values, EntityLogicalName);
    }

    private static readonly IReadOnlyDictionary<string, QueryValue> EmptyRow =
        new Dictionary<string, QueryValue>();

    private async System.Threading.Tasks.Task<long> ExecuteInsertValuesAsync(
        QueryPlanContext context,
        CancellationToken cancellationToken)
    {
        var entities = new List<Microsoft.Xrm.Sdk.Entity>();

        foreach (var row in InsertValueRows!)
        {
            if (entities.Count >= RowCap)
            {
                break;
            }

            var entity = new Microsoft.Xrm.Sdk.Entity(EntityLogicalName);
            for (var i = 0; i < InsertColumns!.Count; i++)
            {
                var value = context.ExpressionEvaluator.Evaluate(row[i], EmptyRow);
                entity[InsertColumns[i]] = value;
            }
            entities.Add(entity);
        }

        var progress = CreateProgressAdapter(context);
        var result = await context.BulkOperationExecutor!.CreateMultipleAsync(
            EntityLogicalName,
            entities,
            progress: progress,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return result.SuccessCount;
    }

    private async System.Threading.Tasks.Task<long> ExecuteInsertSelectAsync(
        QueryPlanContext context,
        CancellationToken cancellationToken)
    {
        var entities = new List<Microsoft.Xrm.Sdk.Entity>();

        await foreach (var row in SourceNode!.ExecuteAsync(context, cancellationToken))
        {
            if (entities.Count >= RowCap)
            {
                break;
            }

            var entity = new Microsoft.Xrm.Sdk.Entity(EntityLogicalName);

            if (SourceColumns != null && SourceColumns.Count == InsertColumns!.Count)
            {
                // Ordinal mapping: map source column[i] value to insert column[i]
                for (var i = 0; i < InsertColumns.Count; i++)
                {
                    var sourceKey = SourceColumns[i];
                    if (row.Values.TryGetValue(sourceKey, out var qv))
                    {
                        entity[InsertColumns[i]] = qv.Value;
                    }
                }
            }
            else
            {
                // Fallback: try matching by INSERT column name (same-name case)
                for (var i = 0; i < InsertColumns!.Count; i++)
                {
                    var columnName = InsertColumns[i];
                    if (row.Values.TryGetValue(columnName, out var qv))
                    {
                        entity[columnName] = qv.Value;
                    }
                }
            }

            entities.Add(entity);
        }

        if (entities.Count == 0) return 0;

        var progress = CreateProgressAdapter(context);
        var result = await context.BulkOperationExecutor!.CreateMultipleAsync(
            EntityLogicalName,
            entities,
            progress: progress,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return result.SuccessCount;
    }

    private async System.Threading.Tasks.Task<long> ExecuteUpdateAsync(
        QueryPlanContext context,
        CancellationToken cancellationToken)
    {
        var entities = new List<Microsoft.Xrm.Sdk.Entity>();
        var idColumn = EntityLogicalName + "id";

        await foreach (var row in SourceNode!.ExecuteAsync(context, cancellationToken))
        {
            if (entities.Count >= RowCap)
            {
                break;
            }

            // Get the record ID from the source row
            if (!row.Values.TryGetValue(idColumn, out var idValue) || idValue.Value == null)
            {
                continue;
            }

            var recordId = idValue.Value is Guid guid ? guid : Guid.Parse(idValue.Value.ToString()!);
            var entity = new Microsoft.Xrm.Sdk.Entity(EntityLogicalName, recordId);

            // Evaluate SET clauses against the source row values
            foreach (var clause in SetClauses!)
            {
                var value = context.ExpressionEvaluator.Evaluate(clause.Value, row.Values);
                entity[clause.ColumnName] = value;
            }

            entities.Add(entity);
        }

        if (entities.Count == 0) return 0;

        var progress = CreateProgressAdapter(context);
        var result = await context.BulkOperationExecutor!.UpdateMultipleAsync(
            EntityLogicalName,
            entities,
            progress: progress,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return result.SuccessCount;
    }

    private async System.Threading.Tasks.Task<long> ExecuteDeleteAsync(
        QueryPlanContext context,
        CancellationToken cancellationToken)
    {
        var ids = new List<Guid>();
        var idColumn = EntityLogicalName + "id";

        await foreach (var row in SourceNode!.ExecuteAsync(context, cancellationToken))
        {
            if (ids.Count >= RowCap)
            {
                break;
            }

            if (!row.Values.TryGetValue(idColumn, out var idValue) || idValue.Value == null)
            {
                continue;
            }

            var recordId = idValue.Value is Guid guid ? guid : Guid.Parse(idValue.Value.ToString()!);
            ids.Add(recordId);
        }

        if (ids.Count == 0) return 0;

        var progress = CreateProgressAdapter(context);
        var result = await context.BulkOperationExecutor!.DeleteMultipleAsync(
            EntityLogicalName,
            ids,
            progress: progress,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return result.SuccessCount;
    }

    /// <summary>
    /// Creates an IProgress adapter that bridges ProgressSnapshot to IQueryProgressReporter.
    /// </summary>
    private static IProgress<ProgressSnapshot>? CreateProgressAdapter(QueryPlanContext context)
    {
        if (context.ProgressReporter == null) return null;

        return new Progress<ProgressSnapshot>(snapshot =>
        {
            context.ProgressReporter.ReportProgress(
                (int)snapshot.Processed,
                (int)snapshot.Total,
                $"{snapshot.Succeeded} succeeded, {snapshot.Failed} failed");
        });
    }
}

/// <summary>
/// The type of DML operation.
/// </summary>
public enum DmlOperation
{
    /// <summary>INSERT operation.</summary>
    Insert,
    /// <summary>UPDATE operation.</summary>
    Update,
    /// <summary>DELETE operation.</summary>
    Delete
}
