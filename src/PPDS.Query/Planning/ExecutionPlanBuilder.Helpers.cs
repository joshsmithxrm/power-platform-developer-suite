using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using PPDS.Dataverse.Query;
using PPDS.Dataverse.Query.Planning;
using PPDS.Dataverse.Query.Planning.Nodes;
using PPDS.Dataverse.Query.Planning.Partitioning;
using PPDS.Dataverse.Sql.Transpilation;
using PPDS.Query.Execution;
using PPDS.Query.Parsing;
using PPDS.Query.Planning.Nodes;

namespace PPDS.Query.Planning;

public sealed partial class ExecutionPlanBuilder
{
    // ═══════════════════════════════════════════════════════════════════
    //  Aggregate partitioning
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Determines whether an aggregate query should use parallel partitioning with ScriptDom types.
    /// Requirements:
    /// - Query must use aggregate functions (COUNT, SUM, AVG, MIN, MAX)
    /// - Pool capacity must be > 1 (partitioning needs parallelism)
    /// - Estimated record count must exceed the aggregate record limit
    /// - Date range bounds (MinDate, MaxDate) must be provided
    /// - Query must NOT contain COUNT(DISTINCT) (can't be partitioned correctly)
    /// </summary>
    private static bool ShouldPartitionAggregate(
        QuerySpecification querySpec, QueryPlanOptions options)
    {
        // Must have aggregate functions
        if (!HasAggregatesInQuerySpec(querySpec))
            return false;

        // Need pool capacity > 1 for parallelism to be worthwhile
        if (options.PoolCapacity <= 1)
            return false;

        // Need estimated record count that exceeds the limit
        if (!options.EstimatedRecordCount.HasValue
            || options.EstimatedRecordCount.Value <= options.AggregateRecordLimit)
            return false;

        // Need date range bounds for partitioning
        if (!options.MinDate.HasValue || !options.MaxDate.HasValue)
            return false;

        // COUNT(DISTINCT) cannot be parallel-partitioned because summing partial
        // distinct counts would double-count values appearing in multiple partitions.
        if (ContainsCountDistinctInQuerySpec(querySpec))
            return false;

        return true;
    }

    /// <summary>
    /// Checks if the SELECT list contains any COUNT(DISTINCT ...) aggregate.
    /// </summary>
    private static bool ContainsCountDistinctInQuerySpec(QuerySpecification querySpec)
    {
        foreach (var element in querySpec.SelectElements)
        {
            if (element is SelectScalarExpression { Expression: FunctionCall func }
                && func.OverClause == null
                && string.Equals(func.FunctionName?.Value, "COUNT", StringComparison.OrdinalIgnoreCase)
                && func.UniqueRowFilter == UniqueRowFilter.Distinct)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Builds a partitioned aggregate plan (ParallelPartitionNode + MergeAggregateNode).
    /// Works entirely with ScriptDom types, no legacy AST dependency.
    /// </summary>
    private QueryPlanResult PlanAggregateWithPartitioning(
        QuerySpecification querySpec,
        QueryPlanOptions options,
        TranspileResult transpileResult,
        string entityName)
    {
        var partitioner = new DateRangePartitioner();
        var partitions = partitioner.CalculatePartitions(
            options.EstimatedRecordCount!.Value,
            options.MinDate!.Value,
            options.MaxDate!.Value,
            options.MaxRecordsPerPartition);

        var mergeColumns = BuildMergeAggregateColumnsFromQuerySpec(querySpec);
        var groupByColumns = ExtractGroupByColumnNames(querySpec);

        var enrichedFetchXml = InjectAvgCompanionCounts(
            transpileResult.FetchXml, mergeColumns);

        var partitionNodes = new List<IQueryPlanNode>();
        foreach (var partition in partitions)
        {
            var adaptiveNode = new AdaptiveAggregateScanNode(
                enrichedFetchXml,
                entityName,
                partition.Start,
                partition.End);

            partitionNodes.Add(adaptiveNode);
        }

        var parallelNode = new ParallelPartitionNode(partitionNodes, options.PoolCapacity);
        IQueryPlanNode rootNode = new MergeAggregateNode(parallelNode, mergeColumns, groupByColumns);

        // HAVING clause: compile directly from ScriptDom (partitioned path).
        // Same aggregate alias resolution as non-partitioned path above.
        if (querySpec.HavingClause?.SearchCondition != null)
        {
            var aggMap = BuildAggregateAliasMap(querySpec);
            var predicate = _expressionCompiler.CompilePredicate(querySpec.HavingClause.SearchCondition, aggMap);
            var description = querySpec.HavingClause.SearchCondition.ToString() ?? "HAVING";
            rootNode = new ClientFilterNode(rootNode, predicate, description);
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
    /// Builds merge aggregate column descriptors from a ScriptDom QuerySpecification.
    /// </summary>
    private static IReadOnlyList<MergeAggregateColumn> BuildMergeAggregateColumnsFromQuerySpec(
        QuerySpecification querySpec)
    {
        var columns = new List<MergeAggregateColumn>();
        var aliasCounter = 0;

        foreach (var element in querySpec.SelectElements)
        {
            if (element is not SelectScalarExpression { Expression: FunctionCall func } scalar
                || func.OverClause != null)
                continue;

            var funcName = func.FunctionName?.Value;
            if (!IsAggregateFunctionName(funcName))
                continue;

            aliasCounter++;
            var alias = scalar.ColumnName?.Value
                ?? $"{funcName!.ToLowerInvariant()}_{aliasCounter}";

            var function = MapToMergeFunctionFromName(funcName!);

            // For AVG, we need a companion COUNT column to compute weighted averages.
            string? countAlias = function == AggregateFunction.Avg
                ? $"{alias}_count"
                : null;

            columns.Add(new MergeAggregateColumn(alias, function, countAlias));
        }

        return columns;
    }

    /// <summary>
    /// Builds a mapping from aggregate function signatures to their output column aliases.
    /// Used by <see cref="ExpressionCompiler"/> to resolve aggregate references in HAVING/ORDER BY.
    /// </summary>
    private static Dictionary<string, string> BuildAggregateAliasMap(QuerySpecification querySpec)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var aliasCounter = 0;

        foreach (var element in querySpec.SelectElements)
        {
            if (element is not SelectScalarExpression { Expression: FunctionCall func } scalar
                || func.OverClause != null)
                continue;

            var funcName = func.FunctionName?.Value;
            if (!IsAggregateFunctionName(funcName))
                continue;

            aliasCounter++;
            var alias = scalar.ColumnName?.Value
                ?? $"{funcName!.ToLowerInvariant()}_{aliasCounter}";

            var sig = ExpressionCompiler.GetAggregateSignature(func);
            map.TryAdd(sig, alias);
        }

        return map;
    }

    /// <summary>
    /// Extracts GROUP BY column names from a ScriptDom <see cref="QuerySpecification"/>.
    /// Handles simple column references, date function calls (YEAR, MONTH, DAY, etc.),
    /// and other expressions by matching against SELECT aliases.
    /// </summary>
    private static List<string> ExtractGroupByColumnNames(QuerySpecification querySpec)
    {
        var names = new List<string>();
        if (querySpec.GroupByClause?.GroupingSpecifications == null)
            return names;

        foreach (var groupSpec in querySpec.GroupByClause.GroupingSpecifications)
        {
            if (groupSpec is not ExpressionGroupingSpecification exprGroup)
                continue;

            switch (exprGroup.Expression)
            {
                case ColumnReferenceExpression colRef:
                    names.Add(GetScriptDomColumnName(colRef));
                    break;

                case FunctionCall funcCall:
                {
                    // First check if there's a matching SELECT alias for this expression
                    var selectAlias = FindSelectAliasForExpression(querySpec, exprGroup.Expression);
                    if (selectAlias != null)
                    {
                        names.Add(selectAlias);
                    }
                    else
                    {
                        // Generate synthetic name matching FetchXml convention: {funcname}_{columnname}
                        var funcName = funcCall.FunctionName?.Value?.ToLowerInvariant();
                        var columnName = GetExpressionColumnName(
                            funcCall.Parameters?.Count == 1 ? funcCall.Parameters[0] : null);
                        if (funcName != null && columnName != null)
                        {
                            names.Add($"{funcName}_{columnName}");
                        }
                        else
                        {
                            names.Add(exprGroup.Expression.ToString() ?? "expr");
                        }
                    }
                    break;
                }

                default:
                {
                    // Other expression types: try to match SELECT alias as fallback
                    var selectAlias = FindSelectAliasForExpression(querySpec, exprGroup.Expression);
                    names.Add(selectAlias ?? exprGroup.Expression.ToString() ?? "expr");
                    break;
                }
            }
        }
        return names;
    }

    /// <summary>
    /// Finds a SELECT alias that matches the given expression by comparing SQL text representations.
    /// Uses <see cref="Sql160ScriptGenerator"/> to produce canonical SQL text for comparison,
    /// since ScriptDom's <c>ToString()</c> returns the type name rather than SQL text.
    /// </summary>
    private static string? FindSelectAliasForExpression(QuerySpecification querySpec, ScalarExpression targetExpr)
    {
        var targetText = ScriptDomToSql(targetExpr);
        if (string.IsNullOrEmpty(targetText))
            return null;

        foreach (var element in querySpec.SelectElements)
        {
            if (element is not SelectScalarExpression scalar || scalar.ColumnName == null)
                continue;

            if (scalar.Expression == null)
                continue;

            var selectExprText = ScriptDomToSql(scalar.Expression);
            if (string.Equals(targetText, selectExprText, StringComparison.OrdinalIgnoreCase))
            {
                return scalar.ColumnName.Value;
            }
        }

        return null;
    }

    /// <summary>
    /// Extracts the column name from a scalar expression.
    /// For <see cref="ColumnReferenceExpression"/>, returns the last identifier.
    /// For other expressions, returns the generated SQL text.
    /// </summary>
    private static string? GetExpressionColumnName(ScalarExpression? expr)
    {
        if (expr == null)
            return null;

        if (expr is ColumnReferenceExpression colRef)
        {
            return GetScriptDomColumnName(colRef);
        }

        return ScriptDomToSql(expr);
    }

    /// <summary>
    /// Generates canonical SQL text from a ScriptDom fragment using <see cref="Sql160ScriptGenerator"/>.
    /// ScriptDom's <c>ToString()</c> returns the type name, not SQL text.
    /// </summary>
    private static string ScriptDomToSql(TSqlFragment fragment)
    {
        var generator = s_scriptGenerator;
        generator.GenerateScript(fragment, out var sql);
        return sql.Trim();
    }

    /// <summary>
    /// Maps an aggregate function name to the <see cref="AggregateFunction"/> enum.
    /// </summary>
    private static AggregateFunction MapToMergeFunctionFromName(string funcName)
    {
        return funcName.ToUpperInvariant() switch
        {
            "COUNT" or "COUNT_BIG" => AggregateFunction.Count,
            "SUM" => AggregateFunction.Sum,
            "AVG" => AggregateFunction.Avg,
            "MIN" => AggregateFunction.Min,
            "MAX" => AggregateFunction.Max,
            "STDEV" => AggregateFunction.Stdev,
            "STDEVP" => AggregateFunction.StdevP,
            "VAR" => AggregateFunction.Var,
            "VARP" => AggregateFunction.VarP,
            _ => throw new QueryParseException($"Unsupported aggregate function for partitioning: {funcName}")
        };
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Internal helpers for plan construction
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Parses a synthetic SQL SELECT string via <see cref="QueryParser"/> and plans it through
    /// the normal ScriptDom SELECT path. Used by DML planning (UPDATE/DELETE/INSERT SELECT/MERGE)
    /// to build the internal SELECT that finds matching records.
    /// </summary>
    private QueryPlanResult ParseAndPlanSyntheticSelect(string sql, QueryPlanOptions options)
    {
        var parser = new QueryParser();
        var fragment = parser.Parse(sql);
        var script = (TSqlScript)fragment;
        var selectStmt = (SelectStatement)script.Batches[0].Statements[0];
        var querySpec = (QuerySpecification)selectStmt.QueryExpression;
        return PlanSelect(selectStmt, querySpec, options);
    }

    /// <summary>
    /// Extracts output column names from a ScriptDom <see cref="QuerySpecification"/>.
    /// Used for ordinal mapping in INSERT ... SELECT.
    /// </summary>
    private static List<string> ExtractSelectColumnNamesFromQuerySpec(QuerySpecification querySpec)
    {
        var names = new List<string>();
        foreach (var element in querySpec.SelectElements)
        {
            switch (element)
            {
                case SelectStarExpression:
                    names.Add("*");
                    break;
                case SelectScalarExpression { Expression: ColumnReferenceExpression colRef } scalar:
                    names.Add(scalar.ColumnName?.Value ?? GetScriptDomColumnName(colRef));
                    break;
                case SelectScalarExpression { Expression: FunctionCall func } scalar:
                    names.Add(scalar.ColumnName?.Value ?? func.FunctionName?.Value ?? "aggregate");
                    break;
                case SelectScalarExpression scalar:
                    names.Add(scalar.ColumnName?.Value ?? "computed");
                    break;
                default:
                    names.Add("unknown");
                    break;
            }
        }
        return names;
    }

    /// <summary>
    /// Checks if a BeginEndBlockStatement contains a TryCatchStatement (ScriptDom sometimes
    /// wraps TRY/CATCH inside a BEGIN...END block).
    /// </summary>
    private static bool ContainsTryCatch(BeginEndBlockStatement block)
    {
        if (block.StatementList?.Statements == null) return false;
        foreach (var stmt in block.StatementList.Statements)
        {
            if (stmt is TryCatchStatement) return true;
        }
        return false;
    }

    /// <summary>
    /// Extracts statements from a BeginEndBlockStatement that contains TryCatchStatement(s),
    /// keeping them as individual TSqlStatements for proper PlanScript handling.
    /// </summary>
    private static TSqlStatement[] ConvertTryCatchBlock(BeginEndBlockStatement block)
    {
        return block.StatementList.Statements.Cast<TSqlStatement>().ToArray();
    }

    /// <summary>
    /// Returns true if a CreateTableStatement is for a temp table (name starts with #).
    /// </summary>
    private static bool IsTempTable(CreateTableStatement createTable)
    {
        var tableName = createTable.SchemaObjectName?.BaseIdentifier?.Value;
        return tableName != null && tableName.StartsWith("#");
    }


    /// <summary>
    /// Extracts the portion of a ScriptDom WHERE clause that requires client-side evaluation.
    /// Returns the BooleanExpression that needs client-side filtering, or null if everything
    /// can be pushed to FetchXML. Any comparison that is not column-vs-literal/variable
    /// (including computed-vs-literal, column-vs-column, and expression-vs-expression)
    /// must be evaluated on the client.
    /// </summary>
    private static BooleanExpression? ExtractClientSideWhereFilter(BooleanExpression? where)
    {
        if (where is null) return null;

        // Any non-pushdown comparison needs client evaluation
        if (IsExpressionComparison(where)) return where;

        // For AND: extract only the parts that need client-side evaluation
        if (where is BooleanBinaryExpression { BinaryExpressionType: BooleanBinaryExpressionType.And } andExpr)
        {
            var leftClient = ExtractClientSideWhereFilter(andExpr.FirstExpression);
            var rightClient = ExtractClientSideWhereFilter(andExpr.SecondExpression);

            if (leftClient != null && rightClient != null)
            {
                // Both sides have client conditions — keep the AND
                return where;
            }
            return leftClient ?? rightClient;
        }

        // For OR: if any part needs client-side, the whole OR needs client-side
        if (where is BooleanBinaryExpression { BinaryExpressionType: BooleanBinaryExpressionType.Or } orExpr)
        {
            if (ContainsExpressionComparison(orExpr.FirstExpression)
                || ContainsExpressionComparison(orExpr.SecondExpression))
            {
                return where;
            }
            return null;
        }

        // Parenthesized expression — recurse
        if (where is BooleanParenthesisExpression parenExpr)
        {
            return ExtractClientSideWhereFilter(parenExpr.Expression);
        }

        // NOT containing an expression comparison
        if (where is BooleanNotExpression notExpr
            && ContainsExpressionComparison(notExpr.Expression))
        {
            return where;
        }

        return null;
    }

    /// <summary>
    /// Returns true if the BooleanExpression is a comparison that cannot be pushed
    /// to FetchXML. Pushdown-safe comparisons are strictly column-vs-literal/variable
    /// (in either order).
    /// </summary>
    private static bool IsExpressionComparison(BooleanExpression expr)
    {
        if (expr is not BooleanComparisonExpression comp) return false;

        return !IsPushdownSafeComparison(comp);
    }

    /// <summary>
    /// Returns true if the scalar expression is a simple literal value (string, number, null)
    /// or a variable reference — values that FetchXML can handle in condition comparisons.
    /// </summary>
    private static bool IsSimpleLiteral(ScalarExpression expr)
    {
        if (expr is Literal or VariableReference or GlobalVariableExpression)
        {
            return true;
        }

        return expr is UnaryExpression
        {
            UnaryExpressionType: UnaryExpressionType.Negative,
            Expression: Literal
        };
    }

    /// <summary>
    /// Returns true when a comparison can be represented in FetchXML: column compared
    /// to literal/variable value, in either order.
    /// </summary>
    private static bool IsPushdownSafeComparison(BooleanComparisonExpression comparison)
    {
        return (comparison.FirstExpression is ColumnReferenceExpression
                && IsSimpleLiteral(comparison.SecondExpression))
            || (comparison.SecondExpression is ColumnReferenceExpression
                && IsSimpleLiteral(comparison.FirstExpression));
    }

    /// <summary>
    /// Recursively checks whether a BooleanExpression contains any expression-to-expression comparisons.
    /// </summary>
    private static bool ContainsExpressionComparison(BooleanExpression expr)
    {
        return expr switch
        {
            BooleanComparisonExpression => IsExpressionComparison(expr),
            BooleanBinaryExpression bin => ContainsExpressionComparison(bin.FirstExpression)
                                          || ContainsExpressionComparison(bin.SecondExpression),
            BooleanParenthesisExpression paren => ContainsExpressionComparison(paren.Expression),
            BooleanNotExpression not => ContainsExpressionComparison(not.Expression),
            _ => false
        };
    }


    /// <summary>
    /// Returns true if the boolean expression tree contains an EXISTS predicate.
    /// </summary>
    private static bool ContainsExistsPredicate(BooleanExpression expr)
    {
        return expr switch
        {
            ExistsPredicate => true,
            BooleanBinaryExpression bin => ContainsExistsPredicate(bin.FirstExpression)
                                           || ContainsExistsPredicate(bin.SecondExpression),
            BooleanParenthesisExpression paren => ContainsExistsPredicate(paren.Expression),
            BooleanNotExpression not => ContainsExistsPredicate(not.Expression),
            _ => false
        };
    }

    /// <summary>
    /// Returns true if the boolean expression tree contains an IN predicate with a subquery.
    /// </summary>
    private static bool ContainsInSubqueryPredicate(BooleanExpression expr)
    {
        return expr switch
        {
            InPredicate inPred => inPred.Subquery != null,
            BooleanBinaryExpression bin => ContainsInSubqueryPredicate(bin.FirstExpression)
                                           || ContainsInSubqueryPredicate(bin.SecondExpression),
            BooleanParenthesisExpression paren => ContainsInSubqueryPredicate(paren.Expression),
            BooleanNotExpression not => ContainsInSubqueryPredicate(not.Expression),
            _ => false
        };
    }

    // ── ScriptDom QuerySpecification analysis helpers ──────────────

    /// <summary>
    /// Checks whether the SELECT list of a <see cref="QuerySpecification"/> contains
    /// aggregate function calls (COUNT, SUM, AVG, MIN, MAX, etc.) without an OVER clause.
    /// </summary>
    private static bool HasAggregatesInQuerySpec(QuerySpecification querySpec)
    {
        foreach (var element in querySpec.SelectElements)
        {
            if (element is SelectScalarExpression { Expression: FunctionCall func }
                && func.OverClause == null
                && IsAggregateFunctionName(func.FunctionName?.Value))
            {
                return true;
            }
        }
        return querySpec.GroupByClause?.GroupingSpecifications.Count > 0;
    }

    /// <summary>
    /// Checks whether the SELECT list contains any computed expressions
    /// (non-column, non-aggregate, non-window expressions such as CASE, IIF, arithmetic).
    /// </summary>
    private static bool HasComputedColumnsInQuerySpec(QuerySpecification querySpec)
    {
        foreach (var element in querySpec.SelectElements)
        {
            if (element is SelectScalarExpression scalar
                && scalar.Expression is not ColumnReferenceExpression
                && !IsAggregateOrWindowFunction(scalar.Expression))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Checks whether the SELECT list contains any window function expressions
    /// (functions with an OVER clause).
    /// </summary>
    private static bool HasWindowFunctionsInQuerySpec(QuerySpecification querySpec)
    {
        foreach (var element in querySpec.SelectElements)
        {
            if (element is SelectScalarExpression { Expression: FunctionCall func }
                && func.OverClause != null)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Extracts the entity (table) name from the FROM clause of a <see cref="QuerySpecification"/>.
    /// For JOINs, drills into the leftmost table reference to find the primary entity.
    /// </summary>
    private static string? ExtractEntityNameFromQuerySpec(QuerySpecification querySpec)
    {
        if (querySpec.FromClause?.TableReferences.Count > 0)
        {
            return ExtractEntityNameFromTableReference(querySpec.FromClause.TableReferences[0]);
        }
        return null;
    }

    private static string? ExtractEntityNameFromTableReference(TableReference tableRef)
    {
        return tableRef switch
        {
            NamedTableReference named => GetMultiPartName(named.SchemaObject),
            QualifiedJoin join => ExtractEntityNameFromTableReference(join.FirstTableReference),
            UnqualifiedJoin unqualified => ExtractEntityNameFromTableReference(unqualified.FirstTableReference),
            QueryDerivedTable derived => derived.Alias?.Value ?? "derived",
            _ => null
        };
    }

    /// <summary>
    /// Extracts the TOP value from a <see cref="QuerySpecification"/>, if present.
    /// Returns null if no TOP clause exists or the value is not a literal integer.
    /// </summary>
    private static int? ExtractTopFromQuerySpec(QuerySpecification querySpec)
    {
        if (querySpec.TopRowFilter?.Expression is IntegerLiteral lit
            && int.TryParse(lit.Value, out var top))
        {
            return top;
        }
        return null;
    }

    private static bool IsAggregateFunctionName(string? name)
    {
        if (name == null) return false;
        return name.Equals("COUNT", StringComparison.OrdinalIgnoreCase)
            || name.Equals("SUM", StringComparison.OrdinalIgnoreCase)
            || name.Equals("AVG", StringComparison.OrdinalIgnoreCase)
            || name.Equals("MIN", StringComparison.OrdinalIgnoreCase)
            || name.Equals("MAX", StringComparison.OrdinalIgnoreCase)
            || name.Equals("COUNT_BIG", StringComparison.OrdinalIgnoreCase)
            || name.Equals("STDEV", StringComparison.OrdinalIgnoreCase)
            || name.Equals("STDEVP", StringComparison.OrdinalIgnoreCase)
            || name.Equals("VAR", StringComparison.OrdinalIgnoreCase)
            || name.Equals("VARP", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAggregateOrWindowFunction(ScalarExpression expr)
    {
        return expr is FunctionCall func
            && (func.OverClause != null || IsAggregateFunctionName(func.FunctionName?.Value));
    }

    private static bool HasAggregateSelectElements(QuerySpecification querySpec)
    {
        foreach (var element in querySpec.SelectElements)
        {
            if (element is SelectScalarExpression scalar
                && scalar.Expression is FunctionCall fc
                && IsAggregateFunctionName(fc.FunctionName?.Value))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Builds a ClientWindowNode from ScriptDom <see cref="QuerySpecification"/> select elements.
    /// Iterates SelectElements looking for FunctionCall expressions with an OverClause,
    /// compiling operand, partition-by, and order-by into executable delegates.
    /// </summary>
    private IQueryPlanNode BuildWindowNodeFromScriptDom(IQueryPlanNode input, QuerySpecification querySpec)
    {
        var windows = new List<Dataverse.Query.Planning.Nodes.WindowDefinition>();

        foreach (var element in querySpec.SelectElements)
        {
            if (element is not SelectScalarExpression { Expression: FunctionCall funcCall } scalar
                || funcCall.OverClause == null)
            {
                continue;
            }

            var functionName = funcCall.FunctionName.Value;

            // Detect COUNT(*): parameter is a ColumnReferenceExpression with Wildcard type
            var isCountStar = false;
            CompiledScalarExpression? compiledOperand = null;

            if (funcCall.Parameters != null && funcCall.Parameters.Count > 0)
            {
                var firstParam = funcCall.Parameters[0];
                if (firstParam is ColumnReferenceExpression { ColumnType: ColumnType.Wildcard })
                {
                    isCountStar = true;
                }
                else
                {
                    compiledOperand = _expressionCompiler.CompileScalar(firstParam);
                }
            }
            else if (functionName.Equals("COUNT", StringComparison.OrdinalIgnoreCase))
            {
                // COUNT with no parameters treated as COUNT(*)
                isCountStar = true;
            }

            // Compile partition-by expressions
            IReadOnlyList<CompiledScalarExpression>? compiledPartitionBy = null;
            if (funcCall.OverClause.Partitions != null && funcCall.OverClause.Partitions.Count > 0)
            {
                var partList = new List<CompiledScalarExpression>(funcCall.OverClause.Partitions.Count);
                foreach (var partExpr in funcCall.OverClause.Partitions)
                {
                    partList.Add(_expressionCompiler.CompileScalar(partExpr));
                }
                compiledPartitionBy = partList;
            }

            // Compile order-by items
            IReadOnlyList<CompiledOrderByItem>? compiledOrderBy = null;
            if (funcCall.OverClause.OrderByClause?.OrderByElements != null
                && funcCall.OverClause.OrderByClause.OrderByElements.Count > 0)
            {
                var orderList = new List<CompiledOrderByItem>(
                    funcCall.OverClause.OrderByClause.OrderByElements.Count);
                foreach (var orderElem in funcCall.OverClause.OrderByClause.OrderByElements)
                {
                    // Extract column name for value lookup in ClientWindowNode
                    string colName;
                    if (orderElem.Expression is ColumnReferenceExpression orderCol)
                    {
                        colName = GetScriptDomColumnName(orderCol);
                    }
                    else
                    {
                        colName = orderElem.Expression.ToString() ?? "expr";
                    }

                    var compiledVal = _expressionCompiler.CompileScalar(orderElem.Expression);
                    var descending = orderElem.SortOrder == SortOrder.Descending;
                    orderList.Add(new CompiledOrderByItem(colName, compiledVal, descending));
                }
                compiledOrderBy = orderList;
            }

            // Get output column name from alias or function name
            var outputName = scalar.ColumnName?.Value ?? functionName;

            windows.Add(new Dataverse.Query.Planning.Nodes.WindowDefinition(
                outputName,
                functionName,
                compiledOperand,
                compiledPartitionBy,
                compiledOrderBy,
                isCountStar));
        }

        if (windows.Count == 0)
        {
            return input;
        }

        return new ClientWindowNode(input, windows);
    }

    /// <summary>
    /// Builds a ProjectNode from ScriptDom <see cref="QuerySpecification"/> select elements.
    /// Handles pass-through columns, renames, computed expressions (CASE/IIF/arithmetic),
    /// and skips window functions (handled by BuildWindowNodeFromScriptDom) and star expressions.
    /// </summary>
    private IQueryPlanNode BuildProjectNodeFromScriptDom(IQueryPlanNode input, QuerySpecification querySpec)
    {
        var projections = new List<ProjectColumn>();

        foreach (var element in querySpec.SelectElements)
        {
            switch (element)
            {
                case SelectStarExpression:
                    // SELECT * — pass-through, no projection needed
                    break;

                case SelectScalarExpression { Expression: ColumnReferenceExpression colRef } scalar:
                {
                    // Simple column reference — pass through or rename
                    var sourceName = GetScriptDomColumnName(colRef);
                    var alias = scalar.ColumnName?.Value;
                    if (alias != null && !string.Equals(alias, sourceName, StringComparison.OrdinalIgnoreCase))
                    {
                        projections.Add(ProjectColumn.Rename(sourceName, alias));
                    }
                    else
                    {
                        projections.Add(ProjectColumn.PassThrough(alias ?? sourceName));
                    }
                    break;
                }

                case SelectScalarExpression { Expression: FunctionCall func } scalar
                    when func.OverClause != null:
                {
                    // Window function — handled by BuildWindowNodeFromScriptDom, pass through result
                    var alias = scalar.ColumnName?.Value ?? func.FunctionName.Value;
                    projections.Add(ProjectColumn.PassThrough(alias));
                    break;
                }

                case SelectScalarExpression { Expression: FunctionCall func } scalar
                    when IsAggregateFunctionName(func.FunctionName?.Value):
                {
                    // Aggregate function (without OVER) — FetchXML handles computation, pass through
                    var alias = scalar.ColumnName?.Value ?? func.FunctionName?.Value ?? "aggregate";
                    projections.Add(ProjectColumn.PassThrough(alias));
                    break;
                }

                case SelectScalarExpression scalar:
                {
                    // Computed expression (CASE, IIF, arithmetic, function without OVER)
                    var alias = scalar.ColumnName?.Value ?? "computed";
                    projections.Add(ProjectColumn.Computed(
                        alias, _expressionCompiler.CompileScalar(scalar.Expression)));
                    break;
                }
            }
        }

        if (projections.Count == 0)
        {
            return input;
        }

        return new ProjectNode(input, projections);
    }

    /// <summary>
    /// Checks whether the SELECT list is <c>SELECT *</c> (no column filtering needed).
    /// </summary>
    private static bool IsSelectStar(QuerySpecification querySpec)
    {
        return querySpec.SelectElements.Count == 1
            && querySpec.SelectElements[0] is SelectStarExpression;
    }

    /// <summary>
    /// Builds a <see cref="ProjectNode"/> that filters the output to only the columns
    /// specified in the SELECT list. Used by the client-side join path where all-attributes
    /// are fetched from each table but only specific columns are requested.
    /// Unlike <see cref="BuildProjectNodeFromScriptDom"/> (which only handles computed columns),
    /// this method creates a projection for all select elements including simple column references.
    /// </summary>
    private IQueryPlanNode BuildSelectListProjection(IQueryPlanNode input, QuerySpecification querySpec)
    {
        var projections = new List<ProjectColumn>();

        foreach (var element in querySpec.SelectElements)
        {
            switch (element)
            {
                case SelectStarExpression:
                    // SELECT * — no projection needed
                    return input;

                case SelectScalarExpression { Expression: ColumnReferenceExpression colRef } scalar:
                {
                    var sourceName = GetScriptDomColumnName(colRef);
                    var alias = scalar.ColumnName?.Value;
                    if (alias != null && !string.Equals(alias, sourceName, StringComparison.OrdinalIgnoreCase))
                    {
                        projections.Add(ProjectColumn.Rename(sourceName, alias));
                    }
                    else
                    {
                        projections.Add(ProjectColumn.PassThrough(alias ?? sourceName));
                    }
                    break;
                }

                case SelectScalarExpression scalar:
                {
                    // Computed or aggregate — pass through by alias or "computed"
                    var alias = scalar.ColumnName?.Value ?? "computed";
                    projections.Add(ProjectColumn.PassThrough(alias));
                    break;
                }
            }
        }

        if (projections.Count == 0)
        {
            return input;
        }

        return new ProjectNode(input, projections);
    }

    /// <summary>
    /// Builds a <see cref="ClientSortNode"/> from a ScriptDom <see cref="OrderByClause"/>.
    /// Compiles each ORDER BY element into a <see cref="CompiledOrderByItem"/>.
    /// </summary>
    private IQueryPlanNode BuildClientSortNode(IQueryPlanNode input, OrderByClause orderByClause)
    {
        var orderItems = new List<CompiledOrderByItem>(orderByClause.OrderByElements.Count);

        foreach (var orderElem in orderByClause.OrderByElements)
        {
            string colName;
            if (orderElem.Expression is ColumnReferenceExpression col)
            {
                colName = GetScriptDomColumnName(col);
            }
            else
            {
                colName = orderElem.Expression.ToString() ?? "expr";
            }

            var compiled = _expressionCompiler.CompileScalar(orderElem.Expression);
            var descending = orderElem.SortOrder == SortOrder.Descending;
            orderItems.Add(new CompiledOrderByItem(colName, compiled, descending));
        }

        return new ClientSortNode(input, orderItems);
    }

    /// <summary>
    /// Gets the column count for a QuerySpecification (for UNION validation).
    /// </summary>
    private static int GetColumnCount(QuerySpecification querySpec)
    {
        if (querySpec.SelectElements.Count == 1
            && querySpec.SelectElements[0] is SelectStarExpression)
        {
            return -1; // Wildcard: can't validate count at plan time
        }
        return querySpec.SelectElements.Count;
    }

    /// <summary>
    /// Extracts the first TSqlStatement from a TSqlFragment.
    /// </summary>
    private static TSqlStatement ExtractFirstStatement(TSqlFragment fragment)
    {
        if (fragment is TSqlScript script)
        {
            foreach (var batch in script.Batches)
            {
                if (batch.Statements.Count > 0)
                {
                    if (batch.Statements.Count > 1)
                    {
                        var block = new BeginEndBlockStatement();
                        block.StatementList = new StatementList();
                        foreach (var s in batch.Statements)
                            block.StatementList.Statements.Add(s);
                        return block;
                    }
                    return batch.Statements[0];
                }
            }
            throw new QueryParseException("SQL text does not contain any statements.");
        }

        if (fragment is TSqlStatement stmt)
            return stmt;

        throw new QueryParseException(
            $"Unsupported TSqlFragment type: {fragment.GetType().Name}");
    }

    /// <summary>
    /// Extracts column names referenced in a ScriptDom <see cref="ScalarExpression"/>.
    /// Used for UPDATE SET clause dependency detection without converting to legacy AST.
    /// </summary>
    private static List<string> ExtractColumnNamesFromScriptDom(ScalarExpression expr)
    {
        var columns = new List<string>();
        ExtractColumnNamesFromScriptDomRecursive(expr, columns);
        return columns;
    }

    private static void ExtractColumnNamesFromScriptDomRecursive(ScalarExpression expr, List<string> columns)
    {
        switch (expr)
        {
            case ColumnReferenceExpression col:
                columns.Add(GetScriptDomColumnName(col));
                break;
            case BinaryExpression bin:
                ExtractColumnNamesFromScriptDomRecursive(bin.FirstExpression, columns);
                ExtractColumnNamesFromScriptDomRecursive(bin.SecondExpression, columns);
                break;
            case UnaryExpression unary:
                ExtractColumnNamesFromScriptDomRecursive(unary.Expression, columns);
                break;
            case ParenthesisExpression paren:
                ExtractColumnNamesFromScriptDomRecursive(paren.Expression, columns);
                break;
            case FunctionCall func:
                if (func.Parameters != null)
                    foreach (var p in func.Parameters)
                        ExtractColumnNamesFromScriptDomRecursive(p, columns);
                break;
            case CastCall cast:
                ExtractColumnNamesFromScriptDomRecursive(cast.Parameter, columns);
                break;
            case SearchedCaseExpression caseExpr:
                foreach (var w in caseExpr.WhenClauses)
                    ExtractColumnNamesFromScriptDomRecursive(w.ThenExpression, columns);
                if (caseExpr.ElseExpression != null)
                    ExtractColumnNamesFromScriptDomRecursive(caseExpr.ElseExpression, columns);
                break;
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Aggregate partitioning helpers (ported from legacy planner
    //  because InjectAvgCompanionCounts is internal to PPDS.Dataverse)
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// For each AVG aggregate attribute in the FetchXML, injects a companion
    /// countcolumn aggregate attribute so that MergeAggregateNode can compute
    /// weighted averages across partitions.
    /// </summary>
    private static string InjectAvgCompanionCounts(
        string fetchXml, IReadOnlyList<MergeAggregateColumn> mergeColumns)
    {
        var avgColumns = mergeColumns
            .Where(c => c.Function == AggregateFunction.Avg && c.CountAlias != null)
            .ToList();
        if (avgColumns.Count == 0) return fetchXml;

        var doc = XDocument.Parse(fetchXml);
        var entityElement = doc.Root?.Element("entity");
        if (entityElement == null) return fetchXml;

        foreach (var avgCol in avgColumns)
        {
            var avgAttr = entityElement.Elements("attribute")
                .FirstOrDefault(a => string.Equals(
                    a.Attribute("alias")?.Value, avgCol.Alias, StringComparison.OrdinalIgnoreCase));

            if (avgAttr == null) continue;

            var attrName = avgAttr.Attribute("name")?.Value;
            if (attrName == null) continue;

            var countElement = new XElement("attribute",
                new XAttribute("name", attrName),
                new XAttribute("alias", avgCol.CountAlias!),
                new XAttribute("aggregate", "countcolumn"));

            avgAttr.AddAfterSelf(countElement);
        }

        return doc.ToString(SaveOptions.DisableFormatting);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  DML property extraction helpers
    // ═══════════════════════════════════════════════════════════════════

    private static string GetInsertTargetEntity(InsertStatement insert)
    {
        if (insert.InsertSpecification.Target is NamedTableReference named)
        {
            return GetMultiPartName(named.SchemaObject);
        }
        throw new QueryParseException("INSERT target must be a named table.");
    }

    private static List<string> GetInsertColumns(InsertStatement insert)
    {
        var columns = new List<string>();
        if (insert.InsertSpecification.Columns != null)
        {
            foreach (var col in insert.InsertSpecification.Columns)
            {
                columns.Add(GetScriptDomColumnName(col));
            }
        }
        return columns;
    }

    private static QuerySpecification? GetInsertSelectSource(InsertStatement insert)
    {
        if (insert.InsertSpecification.InsertSource is SelectInsertSource selectSource
            && selectSource.Select is QuerySpecification querySpec)
        {
            return querySpec;
        }
        return null;
    }

    // ═══════════════════════════════════════════════════════════════════
    //  ScriptDom utility helpers
    // ═══════════════════════════════════════════════════════════════════

    private static string GetScriptDomColumnName(ColumnReferenceExpression colRef)
    {
        var ids = colRef.MultiPartIdentifier?.Identifiers;
        if (ids == null || ids.Count == 0)
            return "*";
        return ids[ids.Count - 1].Value;
    }

    private static string GetMultiPartName(SchemaObjectName schemaObject)
    {
        var schema = schemaObject.SchemaIdentifier?.Value;
        var baseName = schemaObject.BaseIdentifier?.Value ?? "unknown";

        // Preserve meaningful schemas (e.g., "metadata.entity") but strip "dbo"
        // which is just the default SQL Server schema and not a Dataverse concept.
        if (schema != null && !schema.Equals("dbo", StringComparison.OrdinalIgnoreCase))
        {
            return $"{schema}.{baseName}";
        }

        return baseName;
    }

    private static void FlattenUnion(
        BinaryQueryExpression binaryQuery,
        List<QuerySpecification> queries,
        List<bool> isUnionAll)
    {
        if (binaryQuery.FirstQueryExpression is BinaryQueryExpression leftBinary)
        {
            FlattenUnion(leftBinary, queries, isUnionAll);
        }
        else if (binaryQuery.FirstQueryExpression is QuerySpecification leftSpec)
        {
            queries.Add(leftSpec);
        }

        isUnionAll.Add(binaryQuery.All);

        if (binaryQuery.SecondQueryExpression is BinaryQueryExpression rightBinary)
        {
            FlattenUnion(rightBinary, queries, isUnionAll);
        }
        else if (binaryQuery.SecondQueryExpression is QuerySpecification rightSpec)
        {
            queries.Add(rightSpec);
        }
    }
}
