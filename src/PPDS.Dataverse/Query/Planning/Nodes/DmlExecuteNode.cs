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
/// <param name="ColumnName">Logical name of the attribute to set on each updated record (the last part of the SET target's column reference).</param>
/// <param name="Value">The right-hand-side expression compiled to a delegate, evaluated against each source row's values; when attribute metadata is available, the result is coerced to the column's attribute type before it is written.</param>
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
        int rowCap = int.MaxValue,
        IReadOnlyList<string>? requiredColumns = null)
    {
        return new DmlExecuteNode(
            DmlOperation.Update,
            entityLogicalName,
            sourceNode: sourceNode,
            setClauses: setClauses,
            rowCap: rowCap)
        {
            RequiredColumns = requiredColumns ?? Array.Empty<string>()
        };
    }

    /// <summary>
    /// UPDATE: the columns the SET expressions read. Each must be present in every source row (the scan returns every
    /// requested column, NULL included); a missing one is a mapping error and stops the statement before anything is
    /// written, instead of evaluating as NULL.
    /// </summary>
    public IReadOnlyList<string> RequiredColumns { get; private init; } = Array.Empty<string>();

    private static QueryExecutionException MissingSourceColumn(string column) => new(
        QueryErrorCode.ExecutionFailed,
        $"The value of '{column}' was not returned for a source row, so nothing was changed. The column may be " +
        "computed by the query engine (such as a *name display column), which INSERT, UPDATE and DELETE cannot read.");

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
    /// <remarks>
    /// The message names no surface-specific remedy (such as a CLI flag); each surface adds its own from the
    /// structured properties of <see cref="DmlRowCapExceededException"/>.
    /// </remarks>
    private DmlRowCapExceededException RowCapExceeded(long? exactCount = null)
    {
        var operation = Operation.ToString().ToUpperInvariant();
        var cap = RowCap.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
        var matched = exactCount.HasValue
            ? $"it would insert {exactCount.Value.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} rows"
            : $"at least {((long)RowCap + 1).ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} rows match";
        return new DmlRowCapExceededException(
            operation,
            EntityLogicalName,
            RowCap,
            $"{operation} {EntityLogicalName} was refused: it would affect more than the row cap of {cap} rows " +
            $"({matched}). This statement changed nothing. " +
            (exactCount.HasValue ? "Split the INSERT into smaller statements." : "Narrow the WHERE clause to affect fewer rows."));
    }

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
        int submitted,
        CancellationToken cancellationToken,
        Func<IProgress<ProgressSnapshot>, System.Threading.Tasks.Task<BulkOperationResult>> bulkCall)
    {
        var tracker = new ProgressTracker(CreateProgressAdapter(context));
        BulkOperationResult result;
        try
        {
            result = await bulkCall(tracker).ConfigureAwait(false);
        }
        catch (Exception) when (RecordInterrupted(context, tracker))
        {
            throw; // never reached: the filter records the interrupted statement and returns false
        }

        // The sequential path returns normally when cancelled: the batches not yet sent report nothing, so the result
        // covers fewer rows than were submitted. That is an interrupted statement (some rows may be written, the rest
        // were not), never a completed one.
        if (cancellationToken.IsCancellationRequested || result.SuccessCount + result.FailureCount < submitted)
        {
            var error = result.Errors.FirstOrDefault(e => !string.IsNullOrWhiteSpace(e.Message))?.Message;
            context.Statistics.AddDmlOutcome(new DmlStatementOutcome(
                Operation, EntityLogicalName, result.SuccessCount, result.FailureCount, error,
                context.Statistics.CurrentStatementPosition, Interrupted: true));
            cancellationToken.ThrowIfCancellationRequested();
            throw new QueryExecutionException(
                QueryErrorCode.ExecutionFailed,
                $"{Operation.ToString().ToUpperInvariant()} {EntityLogicalName} stopped before it finished: the bulk operation reported " +
                $"{(result.SuccessCount + result.FailureCount).ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} of " +
                $"{submitted.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} rows" +
                (error != null ? $" ({error})." : "."));
        }

        return result;
    }

    /// <summary>
    /// The DML hints (BYPASS_PLUGINS, BYPASS_FLOWS, BATCH_SIZE) as bulk options, so the write does what the statement
    /// asked; null (executor defaults) when no hint was given.
    /// </summary>
    private static BulkOperationOptions? BulkOptions(QueryPlanContext context)
    {
        var hints = context.ExecutionOptions;
        if (hints == null)
            return null;

        // Overlay the hints on the configured options; everything else (parallelism, error handling) stays as configured.
        var options = (context.BulkOperationExecutor?.ConfiguredOptions ?? new BulkOperationOptions()).Clone();
        if (hints.BypassPlugins)
            options.BypassCustomLogic |= CustomLogicBypass.Synchronous;
        if (hints.BypassFlows)
            options.BypassPowerAutomateFlows = true;
        if (hints.DmlBatchSize.HasValue)
            options.BatchSize = hints.DmlBatchSize.Value;
        return options;
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

        if (InsertValueRows!.Count > RowCap)
        {
            throw RowCapExceeded(InsertValueRows.Count);
        }

        foreach (var row in InsertValueRows)
        {

            var entity = new Microsoft.Xrm.Sdk.Entity(EntityLogicalName);
            for (var i = 0; i < InsertColumns!.Count; i++)
            {
                var value = row[i](EmptyRow);
                entity[InsertColumns[i]] = CoerceForColumn(InsertColumns[i], value, attributeMap);
            }
            entities.Add(entity);
        }

        var result = await RunBulkAsync(context, entities.Count, cancellationToken, progress => context.BulkOperationExecutor!.CreateMultipleAsync(
            EntityLogicalName,
            entities,
            options: BulkOptions(context),
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
                // Ordinal mapping: map source column[i] value to insert column[i]. A missing key is a mapping error.
                for (var i = 0; i < InsertColumns.Count; i++)
                {
                    var sourceKey = SourceColumns[i];
                    if (!row.Values.TryGetValue(sourceKey, out var qv))
                        throw MissingSourceColumn(sourceKey);
                    entity[InsertColumns[i]] = CoerceForColumn(InsertColumns[i], qv.Value, attributeMap);
                }
            }
            else
            {
                // Fallback: try matching by INSERT column name (same-name case)
                for (var i = 0; i < InsertColumns!.Count; i++)
                {
                    var columnName = InsertColumns[i];
                    if (!row.Values.TryGetValue(columnName, out var qv))
                        throw MissingSourceColumn(columnName);
                    entity[columnName] = CoerceForColumn(columnName, qv.Value, attributeMap);
                }
            }

            entities.Add(entity);
        }

        if (entities.Count == 0) return NoRows(context);

        var result = await RunBulkAsync(context, entities.Count, cancellationToken, progress => context.BulkOperationExecutor!.CreateMultipleAsync(
            EntityLogicalName,
            entities,
            options: BulkOptions(context),
            progress: progress,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return Summarize(context, result);
    }

    private async System.Threading.Tasks.Task<(long Success, long Failure)> ExecuteUpdateAsync(
        QueryPlanContext context,
        CancellationToken cancellationToken)
    {
        var entities = new List<Microsoft.Xrm.Sdk.Entity>();
        var seen = new HashSet<Guid>();
        var idColumn = EntityLogicalName + "id";
        var attributeMap = await LoadAttributeMapAsync(context, cancellationToken).ConfigureAwait(false);

        await foreach (var row in SourceNode!.ExecuteAsync(context, cancellationToken))
        {
            // Get the record ID from the source row (a row without one is skipped and does not count toward the cap)
            if (!row.Values.TryGetValue(idColumn, out var idValue) || idValue.Value == null)
            {
                continue;
            }

            var recordId = idValue.Value is Guid guid ? guid : Guid.Parse(idValue.Value.ToString()!);
            if (seen.Contains(recordId))
                continue; // the same target row again: it is changed once and counted once

            if (entities.Count >= RowCap)
            {
                throw RowCapExceeded();
            }

            seen.Add(recordId);

            var entity = new Microsoft.Xrm.Sdk.Entity(EntityLogicalName, recordId);

            foreach (var required in RequiredColumns)
            {
                if (!row.Values.Keys.Any(k => string.Equals(k, required, StringComparison.OrdinalIgnoreCase)))
                    throw MissingSourceColumn(required);
            }

            // Evaluate compiled SET clauses against the source row values
            foreach (var clause in SetClauses!)
            {
                var value = clause.Value(row.Values);
                entity[clause.ColumnName] = CoerceForColumn(clause.ColumnName, value, attributeMap);
            }

            entities.Add(entity);
        }

        if (entities.Count == 0) return NoRows(context);

        var result = await RunBulkAsync(context, entities.Count, cancellationToken, progress => context.BulkOperationExecutor!.UpdateMultipleAsync(
            EntityLogicalName,
            entities,
            options: BulkOptions(context),
            progress: progress,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return Summarize(context, result);
    }

    private async System.Threading.Tasks.Task<(long Success, long Failure)> ExecuteDeleteAsync(
        QueryPlanContext context,
        CancellationToken cancellationToken)
    {
        var ids = new List<Guid>();
        var seen = new HashSet<Guid>();
        var idColumn = EntityLogicalName + "id";

        await foreach (var row in SourceNode!.ExecuteAsync(context, cancellationToken))
        {
            // A row without an id is skipped and does not count toward the cap.
            if (!row.Values.TryGetValue(idColumn, out var idValue) || idValue.Value == null)
            {
                continue;
            }

            var recordId = idValue.Value is Guid guid ? guid : Guid.Parse(idValue.Value.ToString()!);
            if (seen.Contains(recordId))
                continue; // the same target row again: it is deleted once and counted once

            if (ids.Count >= RowCap)
            {
                throw RowCapExceeded();
            }

            seen.Add(recordId);
            ids.Add(recordId);
        }

        if (ids.Count == 0) return NoRows(context);

        var result = await RunBulkAsync(context, ids.Count, cancellationToken, progress => context.BulkOperationExecutor!.DeleteMultipleAsync(
            EntityLogicalName,
            ids,
            options: BulkOptions(context),
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
        // Without metadata a value cannot be given the column's type (a lookup id, a choice, money, an instant), so the
        // statement is refused instead of sending raw values.
        if (context.MetadataProvider == null)
        {
            throw new DmlSafetyRefusalException(
                QueryErrorCode.DmlMetadataUnavailable,
                $"{Operation.ToString().ToUpperInvariant()} {EntityLogicalName} needs the table's column metadata to write values, and none " +
                "is available here. Nothing was executed.");
        }

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
        if (attributeMap == null || !attributeMap.TryGetValue(columnName, out var attr))
        {
            throw new DmlSafetyRefusalException(
                QueryErrorCode.DmlUnsupportedConstruct,
                $"The column '{columnName}' is not in the table's metadata, so its value cannot be written with the right type. Nothing was executed.");
        }
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
