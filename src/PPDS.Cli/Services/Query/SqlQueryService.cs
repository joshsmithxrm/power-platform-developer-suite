using System.Runtime.CompilerServices;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Dataverse.BulkOperations;
using PPDS.Dataverse.Metadata;
using PPDS.Dataverse.Query;
using PPDS.Dataverse.Query.Execution;
using PPDS.Dataverse.Query.Planning;
using PPDS.Dataverse.Query.Planning.Nodes;
using PPDS.Dataverse.Sql.Transpilation;
using PPDS.Query.Parsing;
using PPDS.Query.Planning;

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
    private readonly ExecutionPlanBuilder _planBuilder;
    private readonly PlanExecutor _planExecutor;
    private readonly DmlSafetyGuard _dmlSafetyGuard = new();
    private readonly QueryParser _queryParser = new();
    private readonly PPDS.Query.Transpilation.FetchXmlGeneratorService _fetchXmlGeneratorService = new();

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
        _planBuilder = new ExecutionPlanBuilder(_fetchXmlGeneratorService);
        _planExecutor = new PlanExecutor();
    }

    /// <inheritdoc />
    public string TranspileSql(string sql, int? topOverride = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);

        try
        {
            var fragment = _queryParser.Parse(sql);

            if (topOverride.HasValue)
            {
                // Inject TOP override into the ScriptDom AST before transpilation
                InjectTopOverride(fragment, topOverride.Value);
            }

            // Extract the first statement from the TSqlScript wrapper.
            // QueryParser.Parse returns a TSqlScript, but FetchXmlGenerator
            // expects a SelectStatement or QuerySpecification.
            var statement = ExtractFirstStatement(fragment);
            var generator = new PPDS.Query.Transpilation.FetchXmlGenerator();
            return generator.Generate(statement);
        }
        catch (QueryParseException ex)
        {
            throw new PpdsException(ErrorCodes.Query.ParseError, ex.Message, ex);
        }
    }

    /// <summary>
    /// Extracts the first <see cref="TSqlStatement"/> from a parsed fragment.
    /// </summary>
    private static TSqlStatement ExtractFirstStatement(TSqlFragment fragment)
    {
        if (fragment is TSqlScript script)
        {
            foreach (var batch in script.Batches)
            {
                if (batch.Statements.Count > 0)
                    return batch.Statements[0];
            }
        }

        if (fragment is TSqlStatement statement)
            return statement;

        throw new PpdsException(ErrorCodes.Query.ParseError, "SQL text does not contain any statements.");
    }

    /// <inheritdoc />
    public async Task<SqlQueryResult> ExecuteAsync(
        SqlQueryRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Sql);

        TSqlFragment fragment;
        try
        {
            fragment = _queryParser.Parse(request.Sql);
        }
        catch (QueryParseException ex)
        {
            throw new PpdsException(ErrorCodes.Query.ParseError, ex.Message, ex);
        }

        // DML safety check: validate DELETE/UPDATE/INSERT before execution.
        int? dmlRowCap = null;
        DmlSafetyResult? safetyResult = null;

        if (request.DmlSafety != null)
        {
            var firstStatement = ExtractFirstStatement(fragment);

            safetyResult = _dmlSafetyGuard.Check(firstStatement, request.DmlSafety);

            if (safetyResult.IsBlocked)
            {
                throw new PpdsException(
                    safetyResult.ErrorCode ?? ErrorCodes.Query.DmlBlocked,
                    safetyResult.BlockReason ?? "DML operation blocked by safety guard.");
            }

            // Don't return yet for dry-run — we need to run the planner first
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
            await FetchAggregateMetadataAsync(fragment, cancellationToken).ConfigureAwait(false);

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

        QueryPlanResult planResult;
        try
        {
            planResult = _planBuilder.Plan(fragment, planOptions);
        }
        catch (QueryParseException ex)
        {
            throw new PpdsException(ErrorCodes.Query.ParseError, ex.Message, ex);
        }

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
        var context = new QueryPlanContext(
            _queryExecutor,
            cancellationToken,
            bulkOperationExecutor: _bulkOperationExecutor,
            metadataQueryExecutor: _metadataQueryExecutor);

        var result = await _planExecutor.ExecuteAsync(planResult, context, cancellationToken);

        // Expand lookup, optionset, and boolean columns to include *name variants.
        // Virtual column expansion stays in the service layer because it depends on
        // SDK-specific FormattedValues metadata from the Entity objects.
        // Aggregate results are excluded — their FormattedValues are locale-formatted
        // numbers, not meaningful attribute labels.
        var isAggregate = HasAggregatesInFragment(fragment);
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

        TSqlFragment fragment;
        try
        {
            fragment = _queryParser.Parse(sql);
        }
        catch (QueryParseException ex)
        {
            throw new PpdsException(ErrorCodes.Query.ParseError, ex.Message, ex);
        }

        QueryPlanResult planResult;
        try
        {
            planResult = _planBuilder.Plan(fragment);
        }
        catch (QueryParseException ex)
        {
            throw new PpdsException(ErrorCodes.Query.ParseError, ex.Message, ex);
        }

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

        TSqlFragment fragment;
        try
        {
            fragment = _queryParser.Parse(request.Sql);
        }
        catch (QueryParseException ex)
        {
            throw new PpdsException(ErrorCodes.Query.ParseError, ex.Message, ex);
        }

        // DML safety check
        int? dmlRowCap = null;

        if (request.DmlSafety != null)
        {
            var firstStatement = ExtractFirstStatement(fragment);

            var safetyResult = _dmlSafetyGuard.Check(firstStatement, request.DmlSafety);

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
            await FetchAggregateMetadataAsync(fragment, cancellationToken).ConfigureAwait(false);

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

        QueryPlanResult planResult;
        try
        {
            planResult = _planBuilder.Plan(fragment, planOptions);
        }
        catch (QueryParseException ex)
        {
            throw new PpdsException(ErrorCodes.Query.ParseError, ex.Message, ex);
        }

        // Execute the plan with streaming
        var context = new QueryPlanContext(
            _queryExecutor,
            cancellationToken,
            bulkOperationExecutor: _bulkOperationExecutor,
            metadataQueryExecutor: _metadataQueryExecutor);

        var chunkRows = new List<IReadOnlyDictionary<string, QueryValue>>(chunkSize);
        IReadOnlyList<QueryColumn>? columns = null;
        var totalRows = 0;
        var isFirstChunk = true;
        var streamIsAggregate = HasAggregatesInFragment(fragment);

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

    // ═══════════════════════════════════════════════════════════════════
    //  ScriptDom AST helpers (replace legacy ISqlStatement checks)
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Fetches estimated record count and date range for aggregate queries.
    /// Returns nulls for non-aggregate statements (no metadata fetch needed).
    /// Uses ScriptDom AST analysis instead of legacy ISqlStatement.
    /// </summary>
    private async Task<(long? EstimatedRecordCount, DateTime? MinDate, DateTime? MaxDate)> FetchAggregateMetadataAsync(
        TSqlFragment fragment,
        CancellationToken cancellationToken)
    {
        var querySpec = ExtractQuerySpecification(fragment);
        if (querySpec is null) return (null, null, null);

        if (!HasAggregateColumns(querySpec)) return (null, null, null);

        var entityName = ExtractEntityName(querySpec);
        if (entityName is null) return (null, null, null);

        var countTask = _queryExecutor.GetTotalRecordCountAsync(entityName, cancellationToken);
        var dateTask = _queryExecutor.GetMinMaxCreatedOnAsync(entityName, cancellationToken);
        await Task.WhenAll(countTask, dateTask).ConfigureAwait(false);

        var count = await countTask.ConfigureAwait(false);
        var dateRange = await dateTask.ConfigureAwait(false);
        return (count, dateRange.Min, dateRange.Max);
    }

    /// <summary>
    /// Checks if a parsed ScriptDom fragment represents a SELECT with aggregate functions.
    /// </summary>
    private static bool HasAggregatesInFragment(TSqlFragment fragment)
    {
        var querySpec = ExtractQuerySpecification(fragment);
        return querySpec is not null && HasAggregateColumns(querySpec);
    }

    /// <summary>
    /// Extracts a <see cref="QuerySpecification"/> from a parsed fragment.
    /// </summary>
    private static QuerySpecification? ExtractQuerySpecification(TSqlFragment fragment)
    {
        if (fragment is TSqlScript script)
        {
            foreach (var batch in script.Batches)
            {
                foreach (var stmt in batch.Statements)
                {
                    if (stmt is SelectStatement sel && sel.QueryExpression is QuerySpecification qs)
                        return qs;
                }
            }
            return null;
        }

        if (fragment is SelectStatement selectStmt && selectStmt.QueryExpression is QuerySpecification querySpec)
            return querySpec;

        if (fragment is QuerySpecification directQs)
            return directQs;

        return null;
    }

    /// <summary>
    /// Checks if a QuerySpecification's SELECT list contains aggregate function calls.
    /// </summary>
    private static bool HasAggregateColumns(QuerySpecification querySpec)
    {
        foreach (var elem in querySpec.SelectElements)
        {
            if (elem is SelectScalarExpression scalar && scalar.Expression is FunctionCall funcCall)
            {
                var funcName = funcCall.FunctionName?.Value;
                if (funcName is not null && IsAggregateFunction(funcName))
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Checks if a function name is a recognized aggregate function.
    /// </summary>
    private static bool IsAggregateFunction(string functionName)
    {
        return functionName.Equals("COUNT", StringComparison.OrdinalIgnoreCase)
            || functionName.Equals("SUM", StringComparison.OrdinalIgnoreCase)
            || functionName.Equals("AVG", StringComparison.OrdinalIgnoreCase)
            || functionName.Equals("MIN", StringComparison.OrdinalIgnoreCase)
            || functionName.Equals("MAX", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Extracts the primary entity name from a QuerySpecification's FROM clause.
    /// </summary>
    private static string? ExtractEntityName(QuerySpecification querySpec)
    {
        if (querySpec.FromClause is null || querySpec.FromClause.TableReferences.Count == 0)
            return null;

        var tableRef = querySpec.FromClause.TableReferences[0];

        // Drill through qualified joins to the base table
        while (tableRef is QualifiedJoin qj)
        {
            tableRef = qj.FirstTableReference;
        }

        if (tableRef is NamedTableReference named)
        {
            return named.SchemaObject.BaseIdentifier.Value;
        }

        return null;
    }

    /// <summary>
    /// Injects a TOP override into the first SelectStatement's QuerySpecification.
    /// Modifies the ScriptDom AST in place.
    /// </summary>
    private static void InjectTopOverride(TSqlFragment fragment, int topValue)
    {
        var querySpec = ExtractQuerySpecification(fragment);
        if (querySpec is null) return;

        querySpec.TopRowFilter = new TopRowFilter
        {
            Expression = new IntegerLiteral { Value = topValue.ToString() }
        };
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Result helpers
    // ═══════════════════════════════════════════════════════════════════

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
