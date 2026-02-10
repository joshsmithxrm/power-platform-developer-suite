using System.Runtime.CompilerServices;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Dataverse.BulkOperations;
using PPDS.Dataverse.Metadata;
using PPDS.Dataverse.Query;
using PPDS.Dataverse.Query.Execution;
using PPDS.Dataverse.Query.Planning;
using PPDS.Dataverse.Query.Planning.Nodes;
using PPDS.Dataverse.Sql.Parsing;
using PPDS.Dataverse.Sql.Transpilation;
using PPDS.Query.Parsing;
using PPDS.Query.Planning;
using PPDS.Query.Transpilation;

namespace PPDS.Cli.Services.Query;

/// <summary>
/// Implementation of <see cref="ISqlQueryService"/> that parses SQL,
/// transpiles to FetchXML, and executes against Dataverse.
/// </summary>
public sealed class SqlQueryService : ISqlQueryService
{
    private readonly IQueryExecutor _queryExecutor;
    private readonly ITdsQueryExecutor? _tdsQueryExecutor;
    private readonly IBulkOperationExecutor? _bulkOperationExecutor;
    private readonly IMetadataQueryExecutor? _metadataQueryExecutor;
    private readonly int _poolCapacity;
    private readonly QueryParser _queryParser = new();
    private readonly PlanExecutor _planExecutor;
    private readonly DmlSafetyGuard _dmlSafetyGuard = new();

    /// <summary>
    /// Creates a new instance of <see cref="SqlQueryService"/>.
    /// </summary>
    /// <param name="queryExecutor">The query executor for FetchXML execution.</param>
    /// <param name="tdsQueryExecutor">Optional TDS Endpoint executor for direct SQL execution.</param>
    /// <param name="bulkOperationExecutor">Optional bulk operation executor for DML statements.</param>
    /// <param name="metadataQueryExecutor">Optional metadata query executor for metadata virtual tables.</param>
    /// <param name="poolCapacity">Connection pool parallelism capacity for aggregate partitioning.</param>
    public SqlQueryService(
        IQueryExecutor queryExecutor,
        ITdsQueryExecutor? tdsQueryExecutor = null,
        IBulkOperationExecutor? bulkOperationExecutor = null,
        IMetadataQueryExecutor? metadataQueryExecutor = null,
        int poolCapacity = 1)
    {
        _queryExecutor = queryExecutor ?? throw new ArgumentNullException(nameof(queryExecutor));
        _tdsQueryExecutor = tdsQueryExecutor;
        _bulkOperationExecutor = bulkOperationExecutor;
        _metadataQueryExecutor = metadataQueryExecutor;
        _poolCapacity = poolCapacity;
        _planExecutor = new PlanExecutor();
    }

    /// <inheritdoc />
    public string TranspileSql(string sql, int? topOverride = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);

        var script = _queryParser.ParseScript(sql);
        var statement = QueryParser.GetFirstStatement(script)
            ?? throw new SqlParseException("No SQL statements found.");

        if (statement is not SelectStatement select)
            throw new SqlParseException("TranspileSql only supports SELECT statements.");

        // TopOverride is handled by the caller through QueryPlanOptions.MaxRows.
        // For transpile-only, apply it to the AST by setting TopRowFilter on the QuerySpecification.
        if (topOverride.HasValue && select.QueryExpression is QuerySpecification querySpec)
        {
            querySpec.TopRowFilter = new TopRowFilter
            {
                Expression = new IntegerLiteral { Value = topOverride.Value.ToString() }
            };
        }

        var generator = new FetchXmlGenerator();
        var result = generator.Generate(select);
        return result.FetchXml;
    }

    /// <inheritdoc />
    public async Task<SqlQueryResult> ExecuteAsync(
        SqlQueryRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Sql);

        // Parse SQL into ScriptDom AST
        var script = _queryParser.ParseScript(request.Sql);
        var statement = QueryParser.GetFirstStatement(script)
            ?? throw new SqlParseException("No SQL statements found.");

        // TopOverride is handled via QueryPlanOptions.MaxRows — no AST mutation needed.
        // The ExecutionPlanBuilder already respects MaxRows.

        // DML safety check: validate DELETE/UPDATE/INSERT before execution.
        // When DmlSafety options are provided, the guard blocks unsafe operations
        // (DELETE/UPDATE without WHERE) and enforces row caps.
        int? dmlRowCap = null;
        DmlSafetyResult? safetyResult = null;

        if (request.DmlSafety != null)
        {
            safetyResult = _dmlSafetyGuard.Check(statement, request.DmlSafety);

            if (safetyResult.IsBlocked)
            {
                throw new PpdsException(
                    safetyResult.ErrorCode ?? ErrorCodes.Query.DmlBlocked,
                    safetyResult.BlockReason ?? "DML operation blocked by safety guard.");
            }

            // Don't return yet for dry-run -- we need to run the planner first
            // so the user sees the execution plan. The dry-run check moves
            // to after planning.

            if (safetyResult.RequiresConfirmation)
            {
                throw new PpdsException(
                    ErrorCodes.Query.DmlBlocked,
                    "DML operations require --confirm to execute. Use --dry-run to preview the operation.");
            }

            dmlRowCap = safetyResult.RowCap;
        }

        // For aggregate queries, fetch metadata needed for partitioning decisions.
        // This enables the planner to partition large aggregates across the pool.
        var (estimatedRecordCount, minDate, maxDate) =
            await FetchAggregateMetadataAsync(statement, cancellationToken).ConfigureAwait(false);

        // Build execution plan via ExecutionPlanBuilder
        var planOptions = new QueryPlanOptions
        {
            MaxRows = request.TopOverride,
            PageNumber = request.PageNumber,
            PagingCookie = request.PagingCookie,
            IncludeCount = request.IncludeCount,
            UseTdsEndpoint = request.UseTdsEndpoint,
            OriginalSql = request.Sql,
            TdsQueryExecutor = _tdsQueryExecutor,
            DmlRowCap = dmlRowCap,
            EnablePrefetch = request.EnablePrefetch,
            PoolCapacity = _poolCapacity,
            EstimatedRecordCount = estimatedRecordCount,
            MinDate = minDate,
            MaxDate = maxDate
        };

        var planResult = new ExecutionPlanBuilder(planOptions).Build(statement);

        // Dry-run: return the plan without executing. The planner is side-effect-free,
        // so running it gives the user the FetchXML and execution plan for review.
        if (safetyResult?.IsDryRun == true)
        {
            return new SqlQueryResult
            {
                OriginalSql = request.Sql,
                TranspiledFetchXml = planResult.FetchXml,
                Result = QueryResult.Empty("dry-run"),
                DmlSafetyResult = safetyResult
            };
        }

        // Execute the plan
        var expressionEvaluator = new ExpressionEvaluator();
        var context = new QueryPlanContext(
            _queryExecutor,
            expressionEvaluator,
            cancellationToken,
            bulkOperationExecutor: _bulkOperationExecutor,
            metadataQueryExecutor: _metadataQueryExecutor);

        var result = await _planExecutor.ExecuteAsync(planResult, context, cancellationToken);

        // Expand lookup, optionset, and boolean columns to include *name variants.
        // Virtual column expansion stays in the service layer because it depends on
        // SDK-specific FormattedValues metadata from the Entity objects.
        // Aggregate results are excluded -- their FormattedValues are locale-formatted
        // numbers, not meaningful attribute labels.
        var isAggregate = HasAggregates(statement);
        var expandedResult = SqlQueryResultExpander.ExpandFormattedValueColumns(
            result,
            planResult.VirtualColumns,
            isAggregate);

        return new SqlQueryResult
        {
            OriginalSql = request.Sql,
            TranspiledFetchXml = planResult.FetchXml,
            Result = expandedResult
        };
    }

    /// <inheritdoc />
    public Task<QueryPlanDescription> ExplainAsync(string sql, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);

        var script = _queryParser.ParseScript(sql);
        var statement = QueryParser.GetFirstStatement(script)
            ?? throw new SqlParseException("No SQL statements found.");

        var planResult = new ExecutionPlanBuilder().Build(statement);
        var description = QueryPlanDescription.FromNode(planResult.RootNode);

        // Extract parallelism metadata from plan tree
        description.PoolCapacity = ExtractPoolCapacity(planResult.RootNode);
        description.EffectiveParallelism = ExtractEffectiveParallelism(planResult.RootNode);

        return Task.FromResult(description);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<SqlQueryStreamChunk> ExecuteStreamingAsync(
        SqlQueryRequest request,
        int chunkSize = 100,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Sql);

        if (chunkSize <= 0) chunkSize = 100;

        // Parse SQL into ScriptDom AST
        var script = _queryParser.ParseScript(request.Sql);
        var statement = QueryParser.GetFirstStatement(script)
            ?? throw new SqlParseException("No SQL statements found.");

        // TopOverride is handled via QueryPlanOptions.MaxRows -- no AST mutation needed.

        int? dmlRowCap = null;

        if (request.DmlSafety != null)
        {
            var safetyResult = _dmlSafetyGuard.Check(statement, request.DmlSafety);

            if (safetyResult.IsBlocked)
            {
                throw new PpdsException(
                    safetyResult.ErrorCode ?? ErrorCodes.Query.DmlBlocked,
                    safetyResult.BlockReason ?? "DML operation blocked by safety guard.");
            }

            if (safetyResult.RequiresConfirmation)
            {
                throw new PpdsException(
                    ErrorCodes.Query.DmlBlocked,
                    "DML operations require --confirm to execute. Use --dry-run to preview the operation.");
            }

            dmlRowCap = safetyResult.RowCap;
        }

        // For aggregate queries, fetch metadata needed for partitioning decisions.
        var (estimatedRecordCount, minDate, maxDate) =
            await FetchAggregateMetadataAsync(statement, cancellationToken).ConfigureAwait(false);

        // Build execution plan via ExecutionPlanBuilder
        var planOptions = new QueryPlanOptions
        {
            MaxRows = request.TopOverride,
            PageNumber = request.PageNumber,
            PagingCookie = request.PagingCookie,
            IncludeCount = request.IncludeCount,
            UseTdsEndpoint = request.UseTdsEndpoint,
            OriginalSql = request.Sql,
            TdsQueryExecutor = _tdsQueryExecutor,
            DmlRowCap = dmlRowCap,
            EnablePrefetch = request.EnablePrefetch,
            PoolCapacity = _poolCapacity,
            EstimatedRecordCount = estimatedRecordCount,
            MinDate = minDate,
            MaxDate = maxDate
        };

        var planResult = new ExecutionPlanBuilder(planOptions).Build(statement);

        // Execute the plan with streaming
        var expressionEvaluator = new ExpressionEvaluator();
        var context = new QueryPlanContext(
            _queryExecutor,
            expressionEvaluator,
            cancellationToken,
            bulkOperationExecutor: _bulkOperationExecutor,
            metadataQueryExecutor: _metadataQueryExecutor);

        var chunkRows = new List<IReadOnlyDictionary<string, QueryValue>>(chunkSize);
        IReadOnlyList<QueryColumn>? columns = null;
        var totalRows = 0;
        var isFirstChunk = true;
        var streamIsAggregate = HasAggregates(statement);

        await foreach (var row in _planExecutor.ExecuteStreamingAsync(planResult, context, cancellationToken))
        {
            // Infer columns from first row
            if (columns == null)
            {
                columns = InferColumnsFromRow(row);
            }

            chunkRows.Add(row.Values);
            totalRows++;

            if (chunkRows.Count >= chunkSize)
            {
                // Expand virtual columns (owneridname, statuscodename, etc.)
                var expandedChunk = ExpandStreamingChunk(
                    chunkRows, columns!, planResult.VirtualColumns, streamIsAggregate);

                yield return new SqlQueryStreamChunk
                {
                    Rows = expandedChunk.rows,
                    Columns = isFirstChunk ? expandedChunk.columns : null,
                    EntityLogicalName = isFirstChunk ? planResult.EntityLogicalName : null,
                    TotalRowsSoFar = totalRows,
                    IsComplete = false,
                    TranspiledFetchXml = isFirstChunk ? planResult.FetchXml : null
                };

                isFirstChunk = false;
                chunkRows.Clear();
            }
        }

        // Yield final chunk with any remaining rows
        var finalExpanded = ExpandStreamingChunk(
            chunkRows, columns ?? Array.Empty<QueryColumn>(), planResult.VirtualColumns, streamIsAggregate);

        yield return new SqlQueryStreamChunk
        {
            Rows = finalExpanded.rows,
            Columns = isFirstChunk ? finalExpanded.columns : null,
            EntityLogicalName = isFirstChunk ? planResult.EntityLogicalName : null,
            TotalRowsSoFar = totalRows,
            IsComplete = true,
            TranspiledFetchXml = isFirstChunk ? planResult.FetchXml : null
        };
    }

    /// <summary>
    /// Checks if a ScriptDom statement contains aggregate functions in the SELECT list.
    /// </summary>
    private static bool HasAggregates(TSqlStatement statement)
    {
        if (statement is not SelectStatement select) return false;
        if (select.QueryExpression is not QuerySpecification spec) return false;
        return spec.SelectElements.OfType<SelectScalarExpression>()
            .Any(s => s.Expression is FunctionCall func && IsAggregateFunction(func));
    }

    private static bool IsAggregateFunction(FunctionCall func)
    {
        var name = func.FunctionName.Value;
        return name.Equals("COUNT", StringComparison.OrdinalIgnoreCase)
            || name.Equals("SUM", StringComparison.OrdinalIgnoreCase)
            || name.Equals("AVG", StringComparison.OrdinalIgnoreCase)
            || name.Equals("MIN", StringComparison.OrdinalIgnoreCase)
            || name.Equals("MAX", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Gets the entity name from a ScriptDom statement's FROM clause.
    /// </summary>
    private static string GetEntityNameFromStatement(TSqlStatement statement)
    {
        if (statement is not SelectStatement select) return "";
        if (select.QueryExpression is not QuerySpecification spec) return "";
        if (spec.FromClause?.TableReferences.Count > 0)
        {
            if (spec.FromClause.TableReferences[0] is NamedTableReference named)
                return named.SchemaObject.BaseIdentifier?.Value ?? "";
            if (spec.FromClause.TableReferences[0] is QualifiedJoin join
                && join.FirstTableReference is NamedTableReference joinNamed)
                return joinNamed.SchemaObject.BaseIdentifier?.Value ?? "";
        }
        return "";
    }

    /// <summary>
    /// Fetches estimated record count and date range for aggregate queries.
    /// Returns nulls for non-aggregate statements (no metadata fetch needed).
    /// The two metadata calls run in parallel via Task.WhenAll.
    /// </summary>
    private async Task<(long? EstimatedRecordCount, DateTime? MinDate, DateTime? MaxDate)> FetchAggregateMetadataAsync(
        TSqlStatement statement,
        CancellationToken cancellationToken)
    {
        if (HasAggregates(statement))
        {
            var entityName = GetEntityNameFromStatement(statement);
            if (!string.IsNullOrEmpty(entityName))
            {
                var countTask = _queryExecutor.GetTotalRecordCountAsync(entityName, cancellationToken);
                var dateTask = _queryExecutor.GetMinMaxCreatedOnAsync(entityName, cancellationToken);
                await Task.WhenAll(countTask, dateTask).ConfigureAwait(false);

                var count = await countTask.ConfigureAwait(false);
                var dateRange = await dateTask.ConfigureAwait(false);
                return (count, dateRange.Min, dateRange.Max);
            }
        }

        return (null, null, null);
    }

    private static IReadOnlyList<QueryColumn> InferColumnsFromRow(QueryRow row)
    {
        var columns = new List<QueryColumn>();
        foreach (var kvp in row.Values)
        {
            var value = kvp.Value;
            var dataType = value.IsLookup ? QueryColumnType.Lookup
                : value.IsOptionSet ? QueryColumnType.OptionSet
                : value.IsBoolean ? QueryColumnType.Boolean
                : QueryColumnType.Unknown;

            columns.Add(new QueryColumn
            {
                LogicalName = kvp.Key,
                DataType = dataType
            });
        }
        return columns;
    }

    private static (List<IReadOnlyDictionary<string, QueryValue>> rows, IReadOnlyList<QueryColumn> columns) ExpandStreamingChunk(
        List<IReadOnlyDictionary<string, QueryValue>> chunkRows,
        IReadOnlyList<QueryColumn> columns,
        IReadOnlyDictionary<string, VirtualColumnInfo> virtualColumns,
        bool isAggregate = false)
    {
        // Build a mini QueryResult for the chunk so we can reuse the expander
        var chunkResult = new QueryResult
        {
            EntityLogicalName = "chunk",
            Columns = columns.ToList(),
            Records = chunkRows,
            Count = chunkRows.Count,
            MoreRecords = false,
            PageNumber = 1
        };

        var expanded = SqlQueryResultExpander.ExpandFormattedValueColumns(
            chunkResult, virtualColumns, isAggregate);

        return (expanded.Records.ToList(), expanded.Columns);
    }

    private static int? ExtractPoolCapacity(IQueryPlanNode node)
    {
        if (node is ParallelPartitionNode ppn)
            return ppn.MaxParallelism;

        foreach (var child in node.Children)
        {
            var result = ExtractPoolCapacity(child);
            if (result.HasValue) return result;
        }

        return null;
    }

    private static int? ExtractEffectiveParallelism(IQueryPlanNode node)
    {
        if (node is ParallelPartitionNode ppn)
            return ppn.Partitions.Count;

        foreach (var child in node.Children)
        {
            var result = ExtractEffectiveParallelism(child);
            if (result.HasValue) return result;
        }

        return null;
    }
}
