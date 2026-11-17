using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using PPDS.Dataverse.BulkOperations;
using PPDS.Dataverse.Metadata.Models;
using PPDS.Dataverse.Progress;
using PPDS.Dataverse.Query.Execution;

namespace PPDS.Dataverse.Query.Planning.Nodes;

/// <summary>A compiled SET clause for UPDATE statements.</summary>
public sealed record CompiledSetClause(string ColumnName, CompiledScalarExpression Value);

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

    /// <summary>Value rows for INSERT VALUES statements (compiled delegates).</summary>
    public IReadOnlyList<IReadOnlyList<CompiledScalarExpression>>? InsertValueRows { get; }

    /// <summary>SET clauses for UPDATE statements (compiled delegates).</summary>
    public IReadOnlyList<CompiledSetClause>? SetClauses { get; }

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
        IReadOnlyList<IReadOnlyList<CompiledScalarExpression>> valueRows,
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
        IReadOnlyList<CompiledSetClause> setClauses,
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
        IReadOnlyList<IReadOnlyList<CompiledScalarExpression>>? insertValueRows = null,
        IReadOnlyList<CompiledSetClause>? setClauses = null,
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

        (long successCount, long failureCount) dmlResult;

        switch (Operation)
        {
            case DmlOperation.Insert when InsertValueRows != null:
                dmlResult = await ExecuteInsertValuesAsync(context, cancellationToken)
                    .ConfigureAwait(false);
                break;

            case DmlOperation.Insert when SourceNode != null:
                dmlResult = await ExecuteInsertSelectAsync(context, cancellationToken)
                    .ConfigureAwait(false);
                break;

            case DmlOperation.Update:
                dmlResult = await ExecuteUpdateAsync(context, cancellationToken)
                    .ConfigureAwait(false);
                break;

            case DmlOperation.Delete:
                dmlResult = await ExecuteDeleteAsync(context, cancellationToken)
                    .ConfigureAwait(false);
                break;

            default:
                throw new QueryExecutionException(
                    QueryErrorCode.ExecutionFailed,
                    $"Unsupported DML operation: {Operation}");
        }

        var values = new Dictionary<string, QueryValue>
        {
            ["affected_rows"] = QueryValue.Simple(dmlResult.successCount),
            ["failed_rows"] = QueryValue.Simple(dmlResult.failureCount)
        };
        yield return new QueryRow(values, EntityLogicalName);
    }

    /// <summary>
    /// A statement that would affect more rows than the cap is refused before anything is changed: this is
    /// raised while the rows are still being collected, ahead of the first bulk call. Only "at least cap + 1"
    /// is known; the matching rows beyond that are not counted.
    /// </summary>
    private DmlRowCapExceededException RowCapExceeded() => new(
        Operation.ToString().ToUpperInvariant(),
        EntityLogicalName,
        RowCap,
        $"{Operation.ToString().ToUpperInvariant()} {EntityLogicalName} was refused: it would affect more than the " +
        $"row cap of {RowCap:N0} rows (at least {(long)RowCap + 1:N0} rows match). This statement changed nothing. " +
        "Use --no-limit to remove the cap, or narrow the WHERE clause.");

    /// <summary>A statement that matched no rows still counts as an executed DML statement (keeps positions real).</summary>
    private (long Success, long Failure) NoRows(QueryPlanContext context)
    {
        context.Statistics.AddDmlOutcome(new DmlStatementOutcome(
            Operation, EntityLogicalName, 0, 0, null, context.Statistics.CurrentStatementPosition));
        return (0, 0);
    }

    private (long Success, long Failure) Summarize(
        QueryPlanContext context,
        BulkOperationResult result)
    {
        string? message = null;
        if (result.FailureCount > 0)
        {
            // Parallel batches add errors in arbitrary order, so this is a representative error, not the first.
            message = result.Errors.FirstOrDefault(e => !string.IsNullOrWhiteSpace(e.Message))?.Message
                ?? "The operation failed without an error message.";
        }

        context.Statistics.AddDmlOutcome(new DmlStatementOutcome(
            Operation, EntityLogicalName, result.SuccessCount, result.FailureCount, message,
            context.Statistics.CurrentStatementPosition));
        return (result.SuccessCount, result.FailureCount);
    }

    /// <summary>
    /// Runs the bulk call that changes rows. If it throws or is cancelled part-way, rows of this statement may already be
    /// committed (batches run in parallel and are not rolled back), so the statement is recorded as interrupted with the
    /// counts the executor had reported so far, before the exception propagates.
    /// </summary>
    private async System.Threading.Tasks.Task<BulkOperationResult> RunBulkAsync(
        QueryPlanContext context,
        Func<IProgress<ProgressSnapshot>, System.Threading.Tasks.Task<BulkOperationResult>> bulkCall)
    {
        var tracker = new ProgressTracker(CreateProgressAdapter(context));
        try
        {
            return await bulkCall(tracker).ConfigureAwait(false);
        }
        catch (Exception) when (RecordInterrupted(context, tracker))
        {
            throw; // never reached: the filter records the interrupted statement and returns false
        }
    }

    private bool RecordInterrupted(QueryPlanContext context, ProgressTracker tracker)
    {
        context.Statistics.AddDmlOutcome(new DmlStatementOutcome(
            Operation, EntityLogicalName, tracker.Succeeded, tracker.Failed, null,
            context.Statistics.CurrentStatementPosition, Interrupted: true));
        return false;
    }

    /// <summary>Remembers the latest counts the bulk executor reported, and forwards each report.</summary>
    private sealed class ProgressTracker : IProgress<ProgressSnapshot>
    {
        private readonly IProgress<ProgressSnapshot>? _inner;
        private long _succeeded;
        private long _failed;

        public ProgressTracker(IProgress<ProgressSnapshot>? inner) => _inner = inner;

        public long Succeeded => Interlocked.Read(ref _succeeded);
        public long Failed => Interlocked.Read(ref _failed);

        public void Report(ProgressSnapshot value)
        {
            // Parallel batches can report out of order; counts only grow, so keep the largest seen.
            InterlockedMax(ref _succeeded, value.Succeeded);
            InterlockedMax(ref _failed, value.Failed);
            _inner?.Report(value);
        }

        private static void InterlockedMax(ref long target, long value)
        {
            long current;
            while (value > (current = Interlocked.Read(ref target))
                && Interlocked.CompareExchange(ref target, value, current) != current)
            {
            }
        }
    }

    private static readonly IReadOnlyDictionary<string, QueryValue> EmptyRow =
        new Dictionary<string, QueryValue>();

    private async System.Threading.Tasks.Task<(long Success, long Failure)> ExecuteInsertValuesAsync(
        QueryPlanContext context,
        CancellationToken cancellationToken)
    {
        var entities = new List<Microsoft.Xrm.Sdk.Entity>();
        var attributeMap = await LoadAttributeMapAsync(context, cancellationToken).ConfigureAwait(false);

        foreach (var row in InsertValueRows!)
        {
            if (entities.Count >= RowCap)
            {
                throw RowCapExceeded();
            }

            var entity = new Microsoft.Xrm.Sdk.Entity(EntityLogicalName);
            for (var i = 0; i < InsertColumns!.Count; i++)
            {
                var value = row[i](EmptyRow);
                entity[InsertColumns[i]] = CoerceForColumn(InsertColumns[i], value, attributeMap);
            }
            entities.Add(entity);
        }

        var result = await RunBulkAsync(context, progress => context.BulkOperationExecutor!.CreateMultipleAsync(
            EntityLogicalName,
            entities,
            progress: progress,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return Summarize(context, result);
    }

    private async System.Threading.Tasks.Task<(long Success, long Failure)> ExecuteInsertSelectAsync(
        QueryPlanContext context,
        CancellationToken cancellationToken)
    {
        var entities = new List<Microsoft.Xrm.Sdk.Entity>();
        var attributeMap = await LoadAttributeMapAsync(context, cancellationToken).ConfigureAwait(false);

        await foreach (var row in SourceNode!.ExecuteAsync(context, cancellationToken))
        {
            if (entities.Count >= RowCap)
            {
                throw RowCapExceeded();
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
                        entity[InsertColumns[i]] = CoerceForColumn(InsertColumns[i], qv.Value, attributeMap);
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
                        entity[columnName] = CoerceForColumn(columnName, qv.Value, attributeMap);
                    }
                }
            }

            entities.Add(entity);
        }

        if (entities.Count == 0) return NoRows(context);

        var result = await RunBulkAsync(context, progress => context.BulkOperationExecutor!.CreateMultipleAsync(
            EntityLogicalName,
            entities,
            progress: progress,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return Summarize(context, result);
    }

    private async System.Threading.Tasks.Task<(long Success, long Failure)> ExecuteUpdateAsync(
        QueryPlanContext context,
        CancellationToken cancellationToken)
    {
        var entities = new List<Microsoft.Xrm.Sdk.Entity>();
        var idColumn = EntityLogicalName + "id";
        var attributeMap = await LoadAttributeMapAsync(context, cancellationToken).ConfigureAwait(false);

        await foreach (var row in SourceNode!.ExecuteAsync(context, cancellationToken))
        {
            if (entities.Count >= RowCap)
            {
                throw RowCapExceeded();
            }

            // Get the record ID from the source row
            if (!row.Values.TryGetValue(idColumn, out var idValue) || idValue.Value == null)
            {
                continue;
            }

            var recordId = idValue.Value is Guid guid ? guid : Guid.Parse(idValue.Value.ToString()!);
            var entity = new Microsoft.Xrm.Sdk.Entity(EntityLogicalName, recordId);

            // Evaluate compiled SET clauses against the source row values
            foreach (var clause in SetClauses!)
            {
                var value = clause.Value(row.Values);
                entity[clause.ColumnName] = CoerceForColumn(clause.ColumnName, value, attributeMap);
            }

            entities.Add(entity);
        }

        if (entities.Count == 0) return NoRows(context);

        var result = await RunBulkAsync(context, progress => context.BulkOperationExecutor!.UpdateMultipleAsync(
            EntityLogicalName,
            entities,
            progress: progress,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return Summarize(context, result);
    }

    private async System.Threading.Tasks.Task<(long Success, long Failure)> ExecuteDeleteAsync(
        QueryPlanContext context,
        CancellationToken cancellationToken)
    {
        var ids = new List<Guid>();
        var idColumn = EntityLogicalName + "id";

        await foreach (var row in SourceNode!.ExecuteAsync(context, cancellationToken))
        {
            if (ids.Count >= RowCap)
            {
                throw RowCapExceeded();
            }

            if (!row.Values.TryGetValue(idColumn, out var idValue) || idValue.Value == null)
            {
                continue;
            }

            var recordId = idValue.Value is Guid guid ? guid : Guid.Parse(idValue.Value.ToString()!);
            ids.Add(recordId);
        }

        if (ids.Count == 0) return NoRows(context);

        var result = await RunBulkAsync(context, progress => context.BulkOperationExecutor!.DeleteMultipleAsync(
            EntityLogicalName,
            ids,
            progress: progress,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return Summarize(context, result);
    }

    /// <summary>
    /// Loads the attribute metadata map for <see cref="EntityLogicalName"/> from
    /// <see cref="QueryPlanContext.MetadataProvider"/>. Returns null when no provider
    /// is configured (callers fall back to raw CLR values, matching pre-coercion behavior).
    /// </summary>
    private async System.Threading.Tasks.Task<IReadOnlyDictionary<string, AttributeMetadataDto>?> LoadAttributeMapAsync(
        QueryPlanContext context,
        CancellationToken cancellationToken)
    {
        if (context.MetadataProvider == null)
            return null;

        var attrs = await context.MetadataProvider
            .GetAttributesAsync(EntityLogicalName, cancellationToken)
            .ConfigureAwait(false);

        var map = new Dictionary<string, AttributeMetadataDto>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in attrs)
        {
            map[a.LogicalName] = a;
        }
        return map;
    }

    /// <summary>
    /// Coerces <paramref name="value"/> using metadata for <paramref name="columnName"/>
    /// when available. Unknown columns and missing maps pass through unchanged.
    /// </summary>
    private static object? CoerceForColumn(
        string columnName,
        object? value,
        IReadOnlyDictionary<string, AttributeMetadataDto>? attributeMap)
    {
        if (attributeMap == null)
            return value;
        attributeMap.TryGetValue(columnName, out var attr);
        return DmlValueCoercer.Coerce(value, attr);
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
