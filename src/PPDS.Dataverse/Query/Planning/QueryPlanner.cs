using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using PPDS.Dataverse.Query;
using PPDS.Dataverse.Query.Planning.Nodes;
using PPDS.Dataverse.Query.Planning.Partitioning;
using PPDS.Dataverse.Query.Planning.Rewrites;
using PPDS.Dataverse.Sql.Ast;
using PPDS.Dataverse.Sql.Parsing;
using PPDS.Dataverse.Sql.Transpilation;

namespace PPDS.Dataverse.Query.Planning;

/// <summary>
/// Builds an execution plan for a parsed SQL statement.
/// Phase 0: produces FetchXmlScanNode (equivalent to current pipeline).
/// Subsequent phases add optimization rules and new node types.
/// </summary>
public sealed class QueryPlanner
{
    private readonly SqlToFetchXmlTranspiler _transpiler;

    public QueryPlanner(SqlToFetchXmlTranspiler? transpiler = null)
    {
        _transpiler = transpiler ?? new SqlToFetchXmlTranspiler();
    }

    /// <summary>
    /// Builds an execution plan for a parsed SQL statement.
    /// </summary>
    /// <param name="statement">The parsed SQL statement.</param>
    /// <param name="options">Planning options (pool capacity, row limits, etc.).</param>
    /// <returns>The root node of the execution plan.</returns>
    /// <exception cref="SqlParseException">If the statement type is not supported.</exception>
    public QueryPlanResult Plan(ISqlStatement statement, QueryPlanOptions? options = null)
    {
        if (statement is SqlUnionStatement union)
        {
            return PlanUnion(union, options ?? new QueryPlanOptions());
        }

        if (statement is not SqlSelectStatement selectStatement)
        {
            throw new SqlParseException("Only SELECT and UNION statements are currently supported.");
        }

        return PlanSelect(selectStatement, options ?? new QueryPlanOptions());
    }

    private QueryPlanResult PlanSelect(SqlSelectStatement statement, QueryPlanOptions options)
    {
        // COUNT(*) optimization: bare SELECT COUNT(*) FROM entity (no WHERE, JOIN, GROUP BY,
        // HAVING) uses RetrieveTotalRecordCountRequest for near-instant metadata read instead
        // of aggregate FetchXML scan. Short-circuits the entire normal plan path.
        if (IsBareCountStar(statement))
        {
            return PlanBareCountStar(statement);
        }

        // Phase 3.5: TDS Endpoint routing. When TDS is enabled and the query is
        // compatible, bypass FetchXML transpilation and send SQL directly to TDS.
        // Falls back to the FetchXML path for incompatible queries (DML, elastic
        // tables, virtual entities).
        if (options.UseTdsEndpoint
            && options.TdsQueryExecutor != null
            && !string.IsNullOrEmpty(options.OriginalSql))
        {
            var entityName = statement.GetEntityName();
            var compatibility = TdsCompatibilityChecker.CheckCompatibility(
                options.OriginalSql, entityName);

            if (compatibility == TdsCompatibility.Compatible)
            {
                return PlanTds(statement, options);
            }
        }

        // Phase 2: IN subquery rewrite. Rewrites IN (SELECT ...) conditions into
        // INNER JOINs for server-side execution. Must run before transpilation
        // because it modifies the AST (adds JOINs, removes IN subquery conditions).
        // When the rewrite can't produce a JOIN (NOT IN, complex subqueries),
        // it falls back to two-phase execution: run the subquery first, then
        // inject the results as a literal IN list.
        if (ContainsInSubquery(statement.Where))
        {
            var rewriteResult = InSubqueryToJoinRewrite.TryRewrite(statement);
            if (rewriteResult.IsRewritten)
            {
                statement = rewriteResult.RewrittenStatement!;
            }
            else if (rewriteResult.FallbackCondition != null)
            {
                // Fallback: extract IN subquery as client-side filter.
                // The subquery will be executed first, then the IN condition
                // becomes a client-side filter with the materialized values.
                statement = RemoveInSubqueryFromWhere(statement, rewriteResult.FallbackCondition);
            }
        }

        // Phase 2: EXISTS / NOT EXISTS rewrite. Rewrites EXISTS subqueries into
        // INNER JOINs and NOT EXISTS into LEFT JOIN + IS NULL for server-side
        // execution. Must run before transpilation because it modifies the AST.
        if (ContainsExistsCondition(statement.Where))
        {
            statement = ExistsToJoinRewrite.TryRewrite(statement);
        }

        // Phase 0: transpile to FetchXML and create a simple scan node.
        //
        // NOTE: Virtual column expansion (e.g., owneridname from FormattedValues) stays
        // in the service layer (SqlQueryResultExpander) rather than in ProjectNode, because
        // it depends on SDK-specific FormattedValues metadata from the Entity objects.
        // The generic QueryRow format does not carry FormattedValues, so expansion must
        // happen after the plan produces a QueryResult. See SqlQueryService.ExecuteAsync.
        var transpileResult = _transpiler.TranspileWithVirtualColumns(statement);

        // Phase 4: Aggregate partitioning. When aggregate queries might exceed the
        // 50K AggregateQueryRecordLimit, partition by date range and execute in parallel.
        // Must check before building the normal scan node flow because partitioning
        // replaces the single FetchXmlScanNode with ParallelPartitionNode + MergeAggregateNode.
        if (ShouldPartitionAggregate(statement, options))
        {
            return PlanAggregateWithPartitioning(statement, options, transpileResult);
        }

        // When caller provides a page number or paging cookie, use single-page mode
        // instead of auto-paging, so the caller controls pagination.
        var isCallerPaged = options.PageNumber.HasValue || options.PagingCookie != null;

        var scanNode = new FetchXmlScanNode(
            transpileResult.FetchXml,
            statement.GetEntityName(),
            autoPage: !isCallerPaged,
            maxRows: options.MaxRows ?? statement.Top,
            initialPageNumber: options.PageNumber,
            initialPagingCookie: options.PagingCookie,
            includeCount: options.IncludeCount);

        // Start with scan as root; apply client-side operators on top.
        IQueryPlanNode rootNode = scanNode;

        // Expression conditions in WHERE (column-to-column, computed expressions)
        // cannot be pushed to FetchXML. Extract them and evaluate client-side.
        var clientWhereCondition = ExtractExpressionConditions(statement.Where);
        if (clientWhereCondition != null)
        {
            rootNode = new ClientFilterNode(rootNode, clientWhereCondition);
        }

        // HAVING clause: add client-side filter after aggregate FetchXML scan.
        // FetchXML doesn't support HAVING natively, so we filter client-side.
        if (statement.Having != null)
        {
            rootNode = new ClientFilterNode(rootNode, statement.Having);
        }

        // Computed columns (CASE/IIF expressions): add ProjectNode to evaluate
        // expressions client-side against the scan output. Regular columns pass through,
        // computed columns are evaluated by the ExpressionEvaluator.
        if (HasComputedColumns(statement))
        {
            rootNode = BuildProjectNode(rootNode, statement.Columns);
        }

        return new QueryPlanResult
        {
            RootNode = rootNode,
            FetchXml = transpileResult.FetchXml,
            VirtualColumns = transpileResult.VirtualColumns,
            EntityLogicalName = statement.GetEntityName()
        };
    }

    /// <summary>
    /// Builds an execution plan for a UNION / UNION ALL statement.
    /// Each SELECT is planned independently, then concatenated.
    /// UNION (without ALL) adds a DistinctNode on top for deduplication.
    /// </summary>
    private QueryPlanResult PlanUnion(SqlUnionStatement union, QueryPlanOptions options)
    {
        // Validate that all queries have the same number of columns
        var firstColumnCount = GetColumnCount(union.Queries[0]);
        for (var i = 1; i < union.Queries.Count; i++)
        {
            var colCount = GetColumnCount(union.Queries[i]);
            if (colCount != firstColumnCount)
            {
                throw new SqlParseException(
                    $"All queries in a UNION must have the same number of columns. " +
                    $"Query 1 has {firstColumnCount} columns, but query {i + 1} has {colCount}.");
            }
        }

        // Plan each SELECT branch independently
        var branchNodes = new List<IQueryPlanNode>();
        var allFetchXml = new List<string>();
        string? firstEntityName = null;

        foreach (var query in union.Queries)
        {
            var branchResult = PlanSelect(query, options);
            branchNodes.Add(branchResult.RootNode);
            allFetchXml.Add(branchResult.FetchXml);
            firstEntityName ??= branchResult.EntityLogicalName;
        }

        // Build the plan tree: ConcatenateNode for all branches
        IQueryPlanNode rootNode = new ConcatenateNode(branchNodes);

        // If any boundary is UNION (not ALL), wrap with DistinctNode
        var needsDistinct = false;
        for (var i = 0; i < union.IsUnionAll.Count; i++)
        {
            if (!union.IsUnionAll[i])
            {
                needsDistinct = true;
                break;
            }
        }

        if (needsDistinct)
        {
            rootNode = new DistinctNode(rootNode);
        }

        return new QueryPlanResult
        {
            RootNode = rootNode,
            FetchXml = string.Join("\n-- UNION --\n", allFetchXml),
            VirtualColumns = new Dictionary<string, VirtualColumnInfo>(),
            EntityLogicalName = firstEntityName!
        };
    }

    /// <summary>
    /// Gets the number of columns in a SELECT statement.
    /// For wildcard (*), returns -1 to indicate "any" (cannot validate at plan time).
    /// </summary>
    private static int GetColumnCount(SqlSelectStatement statement)
    {
        if (statement.Columns.Count == 1 && statement.Columns[0] is SqlColumnRef { IsWildcard: true })
        {
            return -1; // Wildcard: can't validate count at plan time
        }
        return statement.Columns.Count;
    }

    /// <summary>
    /// Builds a TDS Endpoint plan that sends SQL directly to the TDS wire protocol.
    /// No FetchXML transpilation is performed — the original SQL is passed through.
    /// </summary>
    private static QueryPlanResult PlanTds(SqlSelectStatement statement, QueryPlanOptions options)
    {
        var entityName = statement.GetEntityName();
        var tdsNode = new TdsScanNode(
            options.OriginalSql!,
            entityName,
            options.TdsQueryExecutor!,
            maxRows: options.MaxRows ?? statement.Top);

        return new QueryPlanResult
        {
            RootNode = tdsNode,
            FetchXml = $"-- TDS Endpoint: SQL passed directly --\n{options.OriginalSql}",
            VirtualColumns = new Dictionary<string, VirtualColumnInfo>(),
            EntityLogicalName = entityName
        };
    }

    /// <summary>
    /// Builds an optimized plan for bare COUNT(*) queries using
    /// RetrieveTotalRecordCountRequest with FetchXML as fallback.
    /// </summary>
    private QueryPlanResult PlanBareCountStar(SqlSelectStatement statement)
    {
        var countAlias = GetCountAlias(statement);
        var transpileResult = _transpiler.TranspileWithVirtualColumns(statement);
        var fallbackNode = new FetchXmlScanNode(
            transpileResult.FetchXml,
            statement.GetEntityName(),
            autoPage: false);
        var countNode = new CountOptimizedNode(statement.GetEntityName(), countAlias, fallbackNode);

        return new QueryPlanResult
        {
            RootNode = countNode,
            FetchXml = transpileResult.FetchXml,
            VirtualColumns = transpileResult.VirtualColumns,
            EntityLogicalName = statement.GetEntityName()
        };
    }

    /// <summary>
    /// Detects whether a SELECT statement is a bare COUNT(*) query with no
    /// WHERE, JOIN, GROUP BY, or HAVING clauses — eligible for the optimized
    /// RetrieveTotalRecordCountRequest path.
    /// </summary>
    private static bool IsBareCountStar(SqlSelectStatement statement)
    {
        return statement.Columns.Count == 1
            && statement.Columns[0] is SqlAggregateColumn agg
            && agg.IsCountAll
            && statement.Where == null
            && statement.Joins.Count == 0
            && statement.GroupBy.Count == 0
            && statement.Having == null;
    }

    /// <summary>
    /// Gets the alias for the COUNT(*) column, defaulting to "count" if unaliased.
    /// </summary>
    private static string GetCountAlias(SqlSelectStatement statement)
    {
        var agg = (SqlAggregateColumn)statement.Columns[0];
        return agg.Alias ?? "count";
    }

    /// <summary>
    /// Extracts expression conditions from a WHERE clause that must be evaluated
    /// client-side. Returns null if there are no expression conditions.
    /// For flat AND conditions, extracts only the SqlExpressionCondition entries.
    /// For other structures (OR with mixed types), returns the entire condition
    /// if it contains any expression conditions.
    /// </summary>
    private static ISqlCondition? ExtractExpressionConditions(ISqlCondition? where)
    {
        if (where is null)
        {
            return null;
        }

        // Single expression condition
        if (where is SqlExpressionCondition)
        {
            return where;
        }

        // AND of conditions: extract only the expression conditions
        if (where is SqlLogicalCondition { Operator: SqlLogicalOperator.And } logical)
        {
            var exprConditions = new List<ISqlCondition>();
            foreach (var child in logical.Conditions)
            {
                if (child is SqlExpressionCondition)
                {
                    exprConditions.Add(child);
                }
                else if (child is SqlLogicalCondition nested && ContainsExpressionCondition(nested))
                {
                    // Nested logical with expression conditions: include the whole subtree
                    exprConditions.Add(child);
                }
            }

            if (exprConditions.Count == 0)
            {
                return null;
            }

            return exprConditions.Count == 1
                ? exprConditions[0]
                : new SqlLogicalCondition(SqlLogicalOperator.And, exprConditions);
        }

        // OR or other: if it contains expression conditions anywhere, the whole
        // thing must go client-side (can't partially push an OR to FetchXML).
        if (ContainsExpressionCondition(where))
        {
            return where;
        }

        return null;
    }

    /// <summary>
    /// Recursively checks if a condition tree contains any SqlExpressionCondition nodes.
    /// </summary>
    private static bool ContainsExpressionCondition(ISqlCondition condition)
    {
        return condition switch
        {
            SqlExpressionCondition => true,
            SqlLogicalCondition logical => logical.Conditions.Any(ContainsExpressionCondition),
            _ => false
        };
    }

    /// <summary>
    /// Recursively checks if a condition tree contains any SqlExistsCondition nodes.
    /// </summary>
    private static bool ContainsExistsCondition(ISqlCondition? condition)
    {
        return condition switch
        {
            null => false,
            SqlExistsCondition => true,
            SqlLogicalCondition logical => logical.Conditions.Any(ContainsExistsCondition),
            _ => false
        };
    }

    /// <summary>
    /// Recursively checks if a condition tree contains any SqlInSubqueryCondition nodes.
    /// </summary>
    private static bool ContainsInSubquery(ISqlCondition? condition)
    {
        return condition switch
        {
            null => false,
            SqlInSubqueryCondition => true,
            SqlLogicalCondition logical => logical.Conditions.Any(ContainsInSubquery),
            _ => false
        };
    }

    /// <summary>
    /// Creates a new statement with the IN subquery condition removed from WHERE.
    /// The removed condition is returned via the rewrite result for client-side fallback.
    /// </summary>
    private static SqlSelectStatement RemoveInSubqueryFromWhere(
        SqlSelectStatement statement, SqlInSubqueryCondition conditionToRemove)
    {
        var newWhere = RemoveCondition(statement.Where, conditionToRemove);

        var newStatement = new SqlSelectStatement(
            statement.Columns,
            statement.From,
            statement.Joins,
            newWhere,
            statement.OrderBy,
            statement.Top,
            statement.Distinct,
            statement.GroupBy,
            statement.Having,
            statement.SourcePosition,
            statement.GroupByExpressions);
        newStatement.LeadingComments.AddRange(statement.LeadingComments);
        return newStatement;
    }

    /// <summary>
    /// Removes a specific condition from a condition tree.
    /// Returns null if the entire tree is removed.
    /// </summary>
    private static ISqlCondition? RemoveCondition(ISqlCondition? condition, ISqlCondition toRemove)
    {
        if (condition == null || ReferenceEquals(condition, toRemove))
        {
            return null;
        }

        if (condition is SqlLogicalCondition logical)
        {
            var remaining = new List<ISqlCondition>();
            foreach (var child in logical.Conditions)
            {
                var result = RemoveCondition(child, toRemove);
                if (result != null)
                {
                    remaining.Add(result);
                }
            }

            return remaining.Count switch
            {
                0 => null,
                1 => remaining[0],
                _ => new SqlLogicalCondition(logical.Operator, remaining)
            };
        }

        return condition;
    }

    /// <summary>
    /// Checks whether the SELECT list contains any computed columns (CASE/IIF expressions).
    /// </summary>
    private static bool HasComputedColumns(SqlSelectStatement statement)
    {
        return statement.Columns.Any(col => col is SqlComputedColumn);
    }

    /// <summary>
    /// Builds a ProjectNode that passes through regular columns and evaluates computed columns.
    /// </summary>
    private static ProjectNode BuildProjectNode(IQueryPlanNode input, IReadOnlyList<ISqlSelectColumn> columns)
    {
        var projections = new List<ProjectColumn>();

        foreach (var column in columns)
        {
            switch (column)
            {
                case SqlColumnRef { IsWildcard: true }:
                    // Wildcard: handled at scan level, not projected through here.
                    // For now skip — ProjectNode only applies when there are computed columns
                    // mixed with regular columns, and wildcard + computed is unusual.
                    break;
                case SqlColumnRef colRef:
                    var outputName = colRef.Alias ?? colRef.ColumnName;
                    projections.Add(ProjectColumn.PassThrough(outputName));
                    break;
                case SqlAggregateColumn agg:
                    var aggOutput = agg.Alias ?? agg.GetColumnName() ?? "count";
                    projections.Add(ProjectColumn.PassThrough(aggOutput));
                    break;
                case SqlComputedColumn computed:
                    var compAlias = computed.Alias ?? "computed";
                    projections.Add(ProjectColumn.Computed(compAlias, computed.Expression));
                    break;
            }
        }

        return new ProjectNode(input, projections);
    }

    /// <summary>
    /// Determines whether an aggregate query should be partitioned for parallel execution.
    /// Requirements:
    /// - Query must use aggregate functions (COUNT, SUM, AVG, MIN, MAX)
    /// - Pool capacity must be > 1 (partitioning needs parallelism)
    /// - Estimated record count must exceed the aggregate record limit
    /// - Date range bounds (MinDate, MaxDate) must be provided
    /// - Query must NOT contain COUNT(DISTINCT) (can't be partitioned correctly)
    /// </summary>
    public static bool ShouldPartitionAggregate(SqlSelectStatement statement, QueryPlanOptions options)
    {
        // Must have aggregate functions
        if (!statement.HasAggregates())
        {
            return false;
        }

        // Need pool capacity > 1 for parallelism to be worthwhile
        if (options.PoolCapacity <= 1)
        {
            return false;
        }

        // Need estimated record count that exceeds the limit
        if (!options.EstimatedRecordCount.HasValue
            || options.EstimatedRecordCount.Value <= options.AggregateRecordLimit)
        {
            return false;
        }

        // Need date range bounds for partitioning
        if (!options.MinDate.HasValue || !options.MaxDate.HasValue)
        {
            return false;
        }

        // COUNT(DISTINCT) cannot be parallel-partitioned because summing partial
        // distinct counts would double-count values appearing in multiple partitions.
        if (ContainsCountDistinct(statement))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Checks if the SELECT list contains any COUNT(DISTINCT ...) aggregate.
    /// COUNT(DISTINCT) cannot be parallel-partitioned because partial distinct counts
    /// from different date ranges may contain overlapping values.
    /// </summary>
    public static bool ContainsCountDistinct(SqlSelectStatement statement)
    {
        return statement.GetAggregateColumns().Any(agg =>
            agg.Function == SqlAggregateFunction.Count && agg.IsDistinct);
    }

    /// <summary>
    /// Builds a partitioned aggregate plan that splits the query into date-range
    /// partitions, executes each partition in parallel, and merges the partial
    /// aggregate results.
    ///
    /// Plan tree:
    ///   MergeAggregateNode
    ///     ParallelPartitionNode (max parallelism = PoolCapacity)
    ///       FetchXmlScanNode (partition 0: [minDate, boundary1))
    ///       FetchXmlScanNode (partition 1: [boundary1, boundary2))
    ///       ...
    ///       FetchXmlScanNode (partition N: [boundaryN, maxDate+1s))
    /// </summary>
    private QueryPlanResult PlanAggregateWithPartitioning(
        SqlSelectStatement statement,
        QueryPlanOptions options,
        TranspileResult transpileResult)
    {
        var entityName = statement.GetEntityName();
        var partitioner = new DateRangePartitioner();
        var partitions = partitioner.CalculatePartitions(
            options.EstimatedRecordCount!.Value,
            options.MinDate!.Value,
            options.MaxDate!.Value,
            options.MaxRecordsPerPartition);

        // Create a FetchXmlScanNode per partition, each with date range filter injected
        var partitionNodes = new List<IQueryPlanNode>();
        foreach (var partition in partitions)
        {
            var partitionedFetchXml = InjectDateRangeFilter(
                transpileResult.FetchXml, partition.Start, partition.End);

            var scanNode = new FetchXmlScanNode(
                partitionedFetchXml,
                entityName,
                autoPage: false); // Aggregate queries return a single page

            partitionNodes.Add(scanNode);
        }

        // Wrap in ParallelPartitionNode for concurrent execution
        var parallelNode = new ParallelPartitionNode(partitionNodes, options.PoolCapacity);

        // Build MergeAggregateColumn descriptors from the SQL AST
        var mergeColumns = BuildMergeAggregateColumns(statement);
        var groupByColumns = statement.GroupBy.Select(g => g.Alias ?? g.ColumnName).ToList();

        // Add MergeAggregateNode on top to combine partial results
        IQueryPlanNode rootNode = new MergeAggregateNode(parallelNode, mergeColumns, groupByColumns);

        // HAVING clause: apply after merging (filters on merged aggregate results)
        if (statement.Having != null)
        {
            rootNode = new ClientFilterNode(rootNode, statement.Having);
        }

        return new QueryPlanResult
        {
            RootNode = rootNode,
            FetchXml = transpileResult.FetchXml,
            VirtualColumns = transpileResult.VirtualColumns,
            EntityLogicalName = entityName
        };
    }

    /// <summary>
    /// Builds <see cref="MergeAggregateColumn"/> descriptors from the SQL SELECT columns.
    /// Maps <see cref="SqlAggregateFunction"/> to <see cref="AggregateFunction"/> and
    /// generates companion COUNT aliases for AVG columns to enable weighted average merging.
    /// </summary>
    public static IReadOnlyList<MergeAggregateColumn> BuildMergeAggregateColumns(SqlSelectStatement statement)
    {
        var columns = new List<MergeAggregateColumn>();

        foreach (var agg in statement.GetAggregateColumns())
        {
            var alias = agg.Alias ?? agg.GetColumnName() ?? "count";
            var function = MapToMergeFunction(agg.Function);

            // For AVG, we need a companion COUNT column to compute weighted averages.
            // The FetchXML aggregate will return the partition's average, and we need
            // the partition's row count to properly weight them during merge.
            // Convention: companion count alias = "{alias}_count"
            string? countAlias = function == AggregateFunction.Avg
                ? $"{alias}_count"
                : null;

            columns.Add(new MergeAggregateColumn(alias, function, countAlias));
        }

        return columns;
    }

    /// <summary>
    /// Maps SQL AST aggregate function to the plan node aggregate function enum.
    /// </summary>
    private static AggregateFunction MapToMergeFunction(SqlAggregateFunction sqlFunc)
    {
        return sqlFunc switch
        {
            SqlAggregateFunction.Count => AggregateFunction.Count,
            SqlAggregateFunction.Sum => AggregateFunction.Sum,
            SqlAggregateFunction.Avg => AggregateFunction.Avg,
            SqlAggregateFunction.Min => AggregateFunction.Min,
            SqlAggregateFunction.Max => AggregateFunction.Max,
            _ => throw new ArgumentOutOfRangeException(nameof(sqlFunc), sqlFunc, "Unsupported aggregate function")
        };
    }

    /// <summary>
    /// Injects a date range filter (createdon ge start AND createdon lt end) into FetchXML.
    /// The filter is added just before the closing &lt;/entity&gt; tag as an AND filter.
    /// </summary>
    public static string InjectDateRangeFilter(string fetchXml, DateTime start, DateTime end)
    {
        var startStr = start.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        var endStr = end.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

        var filterXml =
            $"    <filter type=\"and\">\n" +
            $"      <condition attribute=\"createdon\" operator=\"ge\" value=\"{startStr}\" />\n" +
            $"      <condition attribute=\"createdon\" operator=\"lt\" value=\"{endStr}\" />\n" +
            $"    </filter>";

        // Insert the filter before the closing </entity> tag
        var entityCloseIndex = fetchXml.LastIndexOf("</entity>", StringComparison.Ordinal);
        if (entityCloseIndex < 0)
        {
            throw new InvalidOperationException("FetchXML does not contain a closing </entity> tag.");
        }

        return fetchXml[..entityCloseIndex] + filterXml + "\n" + fetchXml[entityCloseIndex..];
    }
}

/// <summary>
/// Result of query planning, including the plan tree and metadata needed for execution.
/// </summary>
public sealed class QueryPlanResult
{
    /// <summary>The root node of the execution plan.</summary>
    public required IQueryPlanNode RootNode { get; init; }

    /// <summary>The generated FetchXML (for backward compatibility with SqlQueryResult).</summary>
    public required string FetchXml { get; init; }

    /// <summary>Virtual columns detected during transpilation.</summary>
    public required IReadOnlyDictionary<string, VirtualColumnInfo> VirtualColumns { get; init; }

    /// <summary>The primary entity logical name.</summary>
    public required string EntityLogicalName { get; init; }
}
