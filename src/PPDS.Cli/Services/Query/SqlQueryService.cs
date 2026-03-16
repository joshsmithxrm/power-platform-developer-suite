using System.Runtime.CompilerServices;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using PPDS.Auth.Profiles;
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
    /// Optional factory that resolves a profile label to a remote <see cref="IQueryExecutor"/>.
    /// Set by the TUI's <c>InteractiveSession</c> to enable cross-environment queries
    /// like <c>SELECT * FROM [QA].account</c>.
    /// </summary>
    public Func<string, IQueryExecutor?>? RemoteExecutorFactory { get; set; }

    /// <summary>
    /// Safety settings for the current environment. When null, uses defaults.
    /// </summary>
    public QuerySafetySettings? EnvironmentSafetySettings { get; set; }

    /// <summary>
    /// Protection level for the current environment. Defaults to Production (safest).
    /// </summary>
    public ProtectionLevel EnvironmentProtectionLevel { get; set; } = ProtectionLevel.Production;

    /// <summary>
    /// Optional profile resolution service for cross-environment DML checks.
    /// </summary>
    public ProfileResolutionService? ProfileResolver { get; set; }

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
                // Only inject TOP override if the SQL doesn't already have one —
                // don't clobber the user's explicit TOP with the extension's default
                var querySpec = ExtractQuerySpecification(fragment);
                if (querySpec?.TopRowFilter == null)
                {
                    InjectTopOverride(fragment, topOverride.Value);
                }
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
        var (fragment, planResult, safetyResult, executionOptions, hints) =
            await PrepareExecutionAsync(request, cancellationToken).ConfigureAwait(false);

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
            metadataQueryExecutor: _metadataQueryExecutor,
            executionOptions: executionOptions);

        QueryResult result;
        try
        {
            result = await _planExecutor.ExecuteAsync(planResult, context, cancellationToken);
        }
        catch (Exception ex) when (
            request.UseTdsEndpoint
            && ContainsTdsScanNode(planResult.RootNode)
            && ex is not OperationCanceledException
            && ex is not PpdsException)
        {
            throw new PpdsException(
                ErrorCodes.Query.TdsConnectionFailed,
                $"TDS Endpoint connection failed: {ex.Message}. The TDS Endpoint may be disabled " +
                "on this environment. Disable TDS mode to query via Dataverse, or ask your Power " +
                "Platform admin to enable the TDS Endpoint.",
                ex);
        }

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

        var dataSources = CollectDataSources(planResult.RootNode, "Local");
        var appliedHints = CollectAppliedHints(hints);

        return new SqlQueryResult
        {
            OriginalSql = request.Sql,
            TranspiledFetchXml = planResult.FetchXml,
            Result = expandedResult,
            DataSources = dataSources,
            AppliedHints = appliedHints,
            ExecutionMode = DetectExecutionMode(planResult.RootNode)
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

        // Parse query hints for EXPLAIN path too
        var hints = QueryHintParser.Parse(fragment);

        var effectivePoolCapacity = hints.MaxParallelism.HasValue
            ? Math.Min(hints.MaxParallelism.Value, _poolCapacity)
            : _poolCapacity;

        // Don't override explicit TOP with hints.MaxResultRows (same guard as PrepareExecutionAsync)
        var sqlHasExplicitTop = ExtractQuerySpecification(fragment)?.TopRowFilter != null;
        var effectiveMaxRows = sqlHasExplicitTop ? null : hints.MaxResultRows;

        var planOptions = new QueryPlanOptions
        {
            RemoteExecutorFactory = RemoteExecutorFactory,
            PoolCapacity = effectivePoolCapacity,
            ForceClientAggregation = hints.ForceClientAggregation == true,
            NoLock = hints.NoLock == true,
            UseTdsEndpoint = hints.UseTdsEndpoint == true,
            MaxRows = effectiveMaxRows,
            TdsQueryExecutor = _tdsQueryExecutor,
            OriginalSql = sql
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
        if (chunkSize <= 0) chunkSize = 100;

        var (fragment, planResult, safetyResult, executionOptions, hints) =
            await PrepareExecutionAsync(request, cancellationToken).ConfigureAwait(false);

        var streamDataSources = CollectDataSources(planResult.RootNode, "Local");
        var streamAppliedHints = CollectAppliedHints(hints);

        // Dry-run: yield empty completion chunk
        if (safetyResult?.IsDryRun == true)
        {
            yield return new SqlQueryStreamChunk
            {
                Rows = new List<IReadOnlyDictionary<string, QueryValue>>(),
                Columns = Array.Empty<QueryColumn>(),
                EntityLogicalName = planResult.EntityLogicalName,
                TotalRowsSoFar = 0,
                IsComplete = true,
                TranspiledFetchXml = planResult.FetchXml
            };
            yield break;
        }

        // Execute the plan with streaming
        var context = new QueryPlanContext(
            _queryExecutor,
            cancellationToken,
            bulkOperationExecutor: _bulkOperationExecutor,
            metadataQueryExecutor: _metadataQueryExecutor,
            executionOptions: executionOptions);

        var chunkRows = new List<IReadOnlyDictionary<string, QueryValue>>(chunkSize);
        IReadOnlyList<QueryColumn>? columns = null;
        var totalRows = 0;
        var isFirstChunk = true;
        var streamIsAggregate = HasAggregatesInFragment(fragment);

        // Wrap streaming enumeration in TDS connection failure catch.
        // Cannot use try/catch around yield return, so we use a helper that
        // catches during MoveNextAsync and rethrows as PpdsException.
        var streamEnumerator = _planExecutor
            .ExecuteStreamingAsync(planResult, context, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);

        try
        {
            while (true)
            {
                QueryRow row;
                try
                {
                    if (!await streamEnumerator.MoveNextAsync().ConfigureAwait(false))
                        break;
                    row = streamEnumerator.Current;
                }
                catch (Exception ex) when (
                    request.UseTdsEndpoint
                    && ContainsTdsScanNode(planResult.RootNode)
                    && ex is not OperationCanceledException
                    && ex is not PpdsException)
                {
                    throw new PpdsException(
                        ErrorCodes.Query.TdsConnectionFailed,
                        $"TDS Endpoint connection failed: {ex.Message}. The TDS Endpoint may be disabled " +
                        "on this environment. Disable TDS mode to query via Dataverse, or ask your Power " +
                        "Platform admin to enable the TDS Endpoint.",
                        ex);
                }

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
        }
        finally
        {
            await streamEnumerator.DisposeAsync().ConfigureAwait(false);
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
            TranspiledFetchXml = isFirstChunk ? planResult.FetchXml : null,
            DataSources = streamDataSources,
            AppliedHints = streamAppliedHints,
            ExecutionMode = DetectExecutionMode(planResult.RootNode)
        };
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Shared execution pipeline
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Shared setup for <see cref="ExecuteAsync"/> and <see cref="ExecuteStreamingAsync"/>:
    /// parse, DML safety check, aggregate metadata, plan build, cross-env check.
    /// </summary>
    private async Task<(TSqlFragment fragment, QueryPlanResult planResult, DmlSafetyResult? safetyResult, QueryExecutionOptions? executionOptions, QueryHintOverrides hints)>
        PrepareExecutionAsync(SqlQueryRequest request, CancellationToken cancellationToken)
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

        // Parse query hints (-- ppds:* comments and OPTION() clauses)
        var hints = QueryHintParser.Parse(fragment);

        // Apply plan-level overrides: hints win over caller settings
        if (hints.UseTdsEndpoint == true)
        {
            request = request with { UseTdsEndpoint = true };
        }
        if (hints.MaxResultRows.HasValue)
        {
            request = request with { TopOverride = hints.MaxResultRows.Value };
        }

        // TDS compatibility pre-check — fail fast before planning.
        // When the user explicitly requests TDS, they get TDS or an error,
        // never a silent substitution to Dataverse.
        if (request.UseTdsEndpoint)
        {
            var querySpec = ExtractQuerySpecification(fragment);
            var entityName = querySpec != null ? ExtractEntityName(querySpec) : null;
            var tdsCompatibility = TdsCompatibilityChecker.CheckCompatibility(
                request.Sql, entityName);

            if (tdsCompatibility != TdsCompatibility.Compatible)
            {
                var reason = tdsCompatibility switch
                {
                    TdsCompatibility.IncompatibleDml =>
                        "Cannot execute via TDS: DML statements (DELETE, UPDATE, INSERT) are not supported by the TDS Endpoint. Disable TDS mode to execute this query against Dataverse.",
                    TdsCompatibility.IncompatibleEntity =>
                        $"Cannot execute via TDS: The target entity is not available via the TDS Endpoint (elastic/virtual table). Disable TDS mode to query via Dataverse.",
                    _ =>
                        "Cannot execute via TDS: This query uses features not supported by the TDS Endpoint. Disable TDS mode to execute via Dataverse."
                };

                throw new PpdsException(ErrorCodes.Query.TdsIncompatible, reason);
            }
        }

        // DML safety check
        int? dmlRowCap = null;
        DmlSafetyResult? safetyResult = null;

        if (request.DmlSafety != null)
        {
            var firstStatement = ExtractFirstStatement(fragment);

            safetyResult = _dmlSafetyGuard.Check(
                firstStatement, request.DmlSafety,
                EnvironmentSafetySettings, EnvironmentProtectionLevel);

            if (safetyResult.IsBlocked)
            {
                throw new PpdsException(
                    safetyResult.ErrorCode ?? ErrorCodes.Query.DmlBlocked,
                    safetyResult.BlockReason ?? "DML operation blocked by safety guard.");
            }

            if (safetyResult.RequiresConfirmation)
            {
                throw new PpdsException(
                    ErrorCodes.Query.DmlConfirmationRequired,
                    "DML operations require --confirm to execute. Use --dry-run to preview the operation.");
            }

            dmlRowCap = safetyResult.RowCap;
        }

        // For aggregate queries, fetch metadata needed for partitioning decisions.
        var (estimatedRecordCount, minDate, maxDate) =
            await FetchAggregateMetadataAsync(fragment, cancellationToken).ConfigureAwait(false);

        // Apply hint-level overrides to pool capacity
        var effectivePoolCapacity = hints.MaxParallelism.HasValue
            ? Math.Min(hints.MaxParallelism.Value, _poolCapacity)
            : _poolCapacity;

        // Build execution options from bypass hints
        var executionOptions = (hints.BypassPlugins == true || hints.BypassFlows == true)
            ? new QueryExecutionOptions
            {
                BypassPlugins = hints.BypassPlugins == true,
                BypassFlows = hints.BypassFlows == true
            }
            : null;

        // Only apply TopOverride as MaxRows if the SQL itself does not contain a TOP clause.
        // The extension sends a default top (100) on every query — we must not clobber the
        // user's explicit TOP 5 with the default 100.
        var sqlHasExplicitTop = ExtractQuerySpecification(fragment)?.TopRowFilter != null;
        var effectiveMaxRows = sqlHasExplicitTop ? null : request.TopOverride;

        // Build execution plan
        var planOptions = new QueryPlanOptions
        {
            MaxRows = effectiveMaxRows,
            PageNumber = request.PageNumber,
            PagingCookie = request.PagingCookie,
            IncludeCount = request.IncludeCount,
            UseTdsEndpoint = request.UseTdsEndpoint,
            OriginalSql = request.Sql,
            TdsQueryExecutor = _tdsQueryExecutor,
            DmlRowCap = dmlRowCap,
            EnablePrefetch = request.EnablePrefetch,
            PoolCapacity = effectivePoolCapacity,
            EstimatedRecordCount = estimatedRecordCount,
            MinDate = minDate,
            MaxDate = maxDate,
            RemoteExecutorFactory = RemoteExecutorFactory,
            ForceClientAggregation = hints.ForceClientAggregation == true,
            NoLock = hints.NoLock == true
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

        // Check cross-environment DML policy after planning
        CheckCrossEnvironmentDmlPolicy(fragment, planResult, request.DmlSafety);

        return (fragment, planResult, safetyResult, executionOptions, hints);
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

    /// <summary>
    /// Collects all data sources (local + remote) that participated in the query.
    /// </summary>
    private static List<QueryDataSource> CollectDataSources(
        IQueryPlanNode rootNode,
        string localLabel)
    {
        var sources = new List<QueryDataSource>
        {
            new() { Label = localLabel, IsRemote = false }
        };
        CollectRemoteLabels(rootNode, sources);
        return sources;
    }

    private static void CollectRemoteLabels(IQueryPlanNode node, List<QueryDataSource> sources)
    {
        if (node is RemoteScanNode remote)
        {
            if (!sources.Any(s => s.Label == remote.RemoteLabel))
                sources.Add(new QueryDataSource { Label = remote.RemoteLabel, IsRemote = true });
        }
        foreach (var child in node.Children)
            CollectRemoteLabels(child, sources);
    }

    /// <summary>
    /// Collects the names of query hints that were actively applied.
    /// Returns null when no hints were active.
    /// </summary>
    private static List<string>? CollectAppliedHints(QueryHintOverrides hints)
    {
        var applied = new List<string>();
        if (hints.UseTdsEndpoint == true) applied.Add("USE_TDS");
        if (hints.NoLock == true) applied.Add("NOLOCK");
        if (hints.BypassPlugins == true) applied.Add("BYPASS_PLUGINS");
        if (hints.BypassFlows == true) applied.Add("BYPASS_FLOWS");
        if (hints.MaxResultRows.HasValue) applied.Add("MAX_ROWS");
        if (hints.MaxParallelism.HasValue) applied.Add("MAXDOP");
        if (hints.ForceClientAggregation == true) applied.Add("HASH_GROUP");
        if (hints.DmlBatchSize.HasValue) applied.Add("BATCH_SIZE");
        return applied.Count > 0 ? applied : null;
    }

    /// <summary>
    /// Detects if a DML statement targets a remote environment by checking for RemoteScanNode in the plan.
    /// Returns the remote label if found, null otherwise (SELECT or local-only DML).
    /// </summary>
    private static string? DetectCrossEnvironmentDmlTarget(TSqlFragment fragment, QueryPlanResult planResult)
    {
        var stmt = ExtractFirstStatement(fragment);
        if (stmt is SelectStatement) return null;

        return FindRemoteLabel(planResult.RootNode);
    }

    private static string? FindRemoteLabel(IQueryPlanNode node)
    {
        if (node is RemoteScanNode remote) return remote.RemoteLabel;
        foreach (var child in node.Children)
        {
            var label = FindRemoteLabel(child);
            if (label != null) return label;
        }
        return null;
    }

    /// <summary>
    /// Checks cross-environment DML policy after planning. Throws if blocked or requires unconfirmed confirmation.
    /// </summary>
    private void CheckCrossEnvironmentDmlPolicy(
        TSqlFragment fragment, QueryPlanResult planResult, DmlSafetyOptions? dmlSafety)
    {
        if (dmlSafety == null) return;

        var targetLabel = DetectCrossEnvironmentDmlTarget(fragment, planResult);
        if (targetLabel == null) return;

        var targetConfig = ProfileResolver?.ResolveByLabel(targetLabel);
        var targetType = targetConfig?.Type ?? EnvironmentType.Production;
        var targetProtection = targetConfig?.Protection
            ?? DmlSafetyGuard.DetectProtectionLevel(targetType);

        var crossEnvResult = _dmlSafetyGuard.CheckCrossEnvironmentDml(
            ExtractFirstStatement(fragment),
            targetConfig?.SafetySettings,
            "local",
            targetLabel,
            targetProtection);

        if (crossEnvResult.IsBlocked)
        {
            throw new PpdsException(
                crossEnvResult.ErrorCode ?? ErrorCodes.Query.DmlBlocked,
                crossEnvResult.BlockReason ?? "Cross-environment DML blocked.");
        }

        if (crossEnvResult.RequiresConfirmation && !dmlSafety.IsConfirmed)
        {
            throw new PpdsException(
                ErrorCodes.Query.DmlBlocked,
                crossEnvResult.ConfirmationMessage
                    ?? "Cross-environment DML requires confirmation.");
        }
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

    /// <summary>
    /// Detects the execution mode by walking the plan tree for TdsScanNode.
    /// </summary>
    private static QueryExecutionMode DetectExecutionMode(IQueryPlanNode rootNode)
    {
        if (ContainsTdsScanNode(rootNode))
            return QueryExecutionMode.Tds;
        return QueryExecutionMode.Dataverse;
    }

    private static bool ContainsTdsScanNode(IQueryPlanNode node)
    {
        if (node is TdsScanNode)
            return true;
        foreach (var child in node.Children)
        {
            if (ContainsTdsScanNode(child))
                return true;
        }
        return false;
    }
}
