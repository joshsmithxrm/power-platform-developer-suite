using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;
using PPDS.Dataverse.Query;
using PPDS.Dataverse.Query.Execution;
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

    /// <summary>Initializes a new instance of the <see cref="QueryPlanner"/> class.</summary>
    /// <param name="transpiler">Optional SQL-to-FetchXML transpiler; creates a default instance if null.</param>
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
        var opts = options ?? new QueryPlanOptions();

        if (statement is SqlUnionStatement union)
        {
            return PlanUnion(union, opts);
        }

        if (statement is SqlInsertStatement insert)
        {
            return PlanInsert(insert, opts);
        }

        if (statement is SqlUpdateStatement update)
        {
            return PlanUpdate(update, opts);
        }

        if (statement is SqlDeleteStatement delete)
        {
            return PlanDelete(delete, opts);
        }

        if (statement is SqlIfStatement ifStmt)
        {
            return PlanScript(new[] { ifStmt }, opts);
        }

        if (statement is SqlBlockStatement block)
        {
            return PlanScript(block.Statements, opts);
        }

        if (statement is not SqlSelectStatement selectStatement)
        {
            throw new SqlParseException("Unsupported statement type.");
        }

        return PlanSelect(selectStatement, opts);
    }

    /// <summary>
    /// Builds an execution plan for a multi-statement script (block or IF/ELSE).
    /// Wraps the statements in a ScriptExecutionNode that handles variable scope,
    /// DECLARE/SET, and conditional branching.
    /// </summary>
    private QueryPlanResult PlanScript(IReadOnlyList<ISqlStatement> statements, QueryPlanOptions options)
    {
        var scriptNode = new ScriptExecutionNode(statements, this);

        return new QueryPlanResult
        {
            RootNode = scriptNode,
            FetchXml = "-- Script: multi-statement execution",
            VirtualColumns = new Dictionary<string, VirtualColumnInfo>(),
            EntityLogicalName = "script"
        };
    }

    private QueryPlanResult PlanSelect(SqlSelectStatement statement, QueryPlanOptions options)
    {
        // Phase 6: Metadata virtual table routing. When the FROM clause references
        // a metadata.* table (e.g., metadata.entity, metadata.attribute), bypass
        // FetchXML transpilation and route to MetadataScanNode instead.
        var fromEntityName = statement.GetEntityName();
        if (fromEntityName.StartsWith("metadata.", StringComparison.OrdinalIgnoreCase))
        {
            return PlanMetadataQuery(statement, fromEntityName);
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

        // Phase 7.2: Variable substitution. Replaces @variable references in WHERE
        // conditions with their literal values so they can be pushed to FetchXML.
        // Must run before transpilation because FetchXML doesn't understand variables.
        if (options.VariableScope != null && statement.Where != null
            && ContainsVariableExpression(statement.Where))
        {
            var newWhere = SubstituteVariables(statement.Where, options.VariableScope);
            statement = ReplaceWhere(statement, newWhere);
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

        // Wrap with PrefetchScanNode for page-ahead buffering when:
        // - Prefetch is enabled
        // - Query is not an aggregate (aggregates return few rows — prefetch overhead not worthwhile)
        // - Query is auto-paging (single-page queries don't benefit from prefetch)
        if (options.EnablePrefetch && !statement.HasAggregates() && !isCallerPaged)
        {
            rootNode = new PrefetchScanNode(rootNode, options.PrefetchBufferSize);
        }

        // Expression conditions in WHERE (column-to-column, computed expressions)
        // cannot be pushed to FetchXML. Extract them and evaluate client-side.
        var clientWhereCondition = ExtractExpressionConditions(statement.Where);
        if (clientWhereCondition != null)
        {
            var predicate = CompileLegacyCondition(clientWhereCondition);
            var description = DescribeLegacyCondition(clientWhereCondition);
            rootNode = new ClientFilterNode(rootNode, predicate, description, clientWhereCondition);
        }

        // HAVING clause: add client-side filter after aggregate FetchXML scan.
        // FetchXML doesn't support HAVING natively, so we filter client-side.
        if (statement.Having != null)
        {
            var predicate = CompileLegacyCondition(statement.Having);
            var description = DescribeLegacyCondition(statement.Having);
            rootNode = new ClientFilterNode(rootNode, predicate, description, statement.Having);
        }

        // Window functions: add ClientWindowNode to compute window values client-side.
        // Must run before ProjectNode because window columns need to be materialized first.
        if (HasWindowFunctions(statement))
        {
            rootNode = BuildWindowNode(rootNode, statement.Columns);
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
    /// Builds an execution plan for an INSERT statement.
    /// INSERT VALUES: wraps value rows in a DmlExecuteNode directly.
    /// INSERT SELECT: plans the source SELECT, wraps with DmlExecuteNode.
    /// </summary>
    private QueryPlanResult PlanInsert(SqlInsertStatement insert, QueryPlanOptions options)
    {
        IQueryPlanNode rootNode;

        if (insert.ValueRows != null)
        {
            // INSERT VALUES: wrap legacy ISqlExpression values in compiled delegates
            var evaluator = new ExpressionEvaluator();
            var compiledRows = insert.ValueRows.Select(row =>
                (IReadOnlyList<CompiledScalarExpression>)row.Select(expr =>
                    (CompiledScalarExpression)(r => evaluator.Evaluate(expr, r))
                ).ToList()
            ).ToList();

            rootNode = DmlExecuteNode.InsertValues(
                insert.TargetEntity,
                insert.Columns,
                compiledRows,
                rowCap: options.DmlRowCap ?? int.MaxValue);
        }
        else if (insert.SourceQuery != null)
        {
            // INSERT SELECT: plan the source SELECT, wrap with DmlExecuteNode
            var sourceResult = PlanSelect(insert.SourceQuery, options);
            var sourceColumns = ExtractSelectColumnNames(insert.SourceQuery);
            rootNode = DmlExecuteNode.InsertSelect(
                insert.TargetEntity,
                insert.Columns,
                sourceResult.RootNode,
                sourceColumns: sourceColumns,
                rowCap: options.DmlRowCap ?? int.MaxValue);
        }
        else
        {
            throw new SqlParseException("INSERT statement must have VALUES or SELECT source.");
        }

        return new QueryPlanResult
        {
            RootNode = rootNode,
            FetchXml = $"-- DML: INSERT INTO {insert.TargetEntity}",
            VirtualColumns = new Dictionary<string, VirtualColumnInfo>(),
            EntityLogicalName = insert.TargetEntity
        };
    }

    /// <summary>
    /// Builds an execution plan for an UPDATE statement.
    /// Creates a FetchXML scan to find matching records, then wraps with DmlExecuteNode.
    /// </summary>
    private QueryPlanResult PlanUpdate(SqlUpdateStatement update, QueryPlanOptions options)
    {
        var entityName = update.TargetTable.TableName;

        // Build a SELECT to find records matching the WHERE clause.
        // SELECT entityid FROM entity WHERE ...
        var idColumn = SqlColumnRef.Simple(entityName + "id");
        var selectColumns = new List<ISqlSelectColumn> { idColumn };

        // Also include any columns referenced in SET clause expressions
        // so they're available for evaluation (e.g., SET revenue = revenue * 1.1)
        foreach (var clause in update.SetClauses)
        {
            var referencedColumns = ExtractColumnNames(clause.Value);
            foreach (var colName in referencedColumns)
            {
                if (!selectColumns.Exists(c => c is SqlColumnRef cr && cr.ColumnName == colName))
                {
                    selectColumns.Add(SqlColumnRef.Simple(colName));
                }
            }
        }

        var selectStatement = new SqlSelectStatement(
            selectColumns,
            update.TargetTable,
            update.Joins,
            update.Where);

        var selectResult = PlanSelect(selectStatement, options);

        // Wrap legacy SqlSetClause values in compiled delegates
        var evaluator = new ExpressionEvaluator();
        var compiledClauses = update.SetClauses.Select(clause =>
            new CompiledSetClause(
                clause.ColumnName,
                (CompiledScalarExpression)(r => evaluator.Evaluate(clause.Value, r)))
        ).ToList();

        var rootNode = DmlExecuteNode.Update(
            entityName,
            selectResult.RootNode,
            compiledClauses,
            rowCap: options.DmlRowCap ?? int.MaxValue);

        return new QueryPlanResult
        {
            RootNode = rootNode,
            FetchXml = selectResult.FetchXml,
            VirtualColumns = new Dictionary<string, VirtualColumnInfo>(),
            EntityLogicalName = entityName
        };
    }

    /// <summary>
    /// Builds an execution plan for a DELETE statement.
    /// Creates a FetchXML scan to find matching record IDs, then wraps with DmlExecuteNode.
    /// </summary>
    private QueryPlanResult PlanDelete(SqlDeleteStatement delete, QueryPlanOptions options)
    {
        var entityName = delete.TargetTable.TableName;

        // Build a SELECT to find record IDs matching the WHERE clause.
        // SELECT entityid FROM entity WHERE ...
        var idColumn = SqlColumnRef.Simple(entityName + "id");
        var selectStatement = new SqlSelectStatement(
            new ISqlSelectColumn[] { idColumn },
            delete.TargetTable,
            delete.Joins,
            delete.Where);

        var selectResult = PlanSelect(selectStatement, options);

        var rootNode = DmlExecuteNode.Delete(
            entityName,
            selectResult.RootNode,
            rowCap: options.DmlRowCap ?? int.MaxValue);

        return new QueryPlanResult
        {
            RootNode = rootNode,
            FetchXml = selectResult.FetchXml,
            VirtualColumns = new Dictionary<string, VirtualColumnInfo>(),
            EntityLogicalName = entityName
        };
    }

    /// <summary>
    /// Extracts column names referenced in an expression (for UPDATE SET clause dependency detection).
    /// </summary>
    private static List<string> ExtractColumnNames(ISqlExpression expression)
    {
        var columns = new List<string>();
        ExtractColumnNamesRecursive(expression, columns);
        return columns;
    }

    private static void ExtractColumnNamesRecursive(ISqlExpression expression, List<string> columns)
    {
        switch (expression)
        {
            case SqlColumnExpression col:
                if (col.Column.ColumnName != null)
                {
                    columns.Add(col.Column.ColumnName);
                }
                break;
            case SqlBinaryExpression bin:
                ExtractColumnNamesRecursive(bin.Left, columns);
                ExtractColumnNamesRecursive(bin.Right, columns);
                break;
            case SqlUnaryExpression unary:
                ExtractColumnNamesRecursive(unary.Operand, columns);
                break;
            case SqlFunctionExpression func:
                foreach (var arg in func.Arguments)
                {
                    ExtractColumnNamesRecursive(arg, columns);
                }
                break;
            case SqlCastExpression cast:
                ExtractColumnNamesRecursive(cast.Expression, columns);
                break;
        }
    }

    /// <summary>
    /// Extracts the output column names from a SELECT statement for ordinal mapping in INSERT...SELECT.
    /// </summary>
    private static List<string> ExtractSelectColumnNames(SqlSelectStatement select)
    {
        var names = new List<string>();
        foreach (var col in select.Columns)
        {
            switch (col)
            {
                case SqlColumnRef colRef:
                    names.Add(colRef.Alias ?? colRef.ColumnName);
                    break;
                case SqlAggregateColumn agg:
                    names.Add(agg.Alias ?? agg.GetColumnName() ?? "count");
                    break;
                case SqlComputedColumn computed:
                    names.Add(computed.Alias ?? "computed");
                    break;
                default:
                    names.Add(col.ToString() ?? "unknown");
                    break;
            }
        }
        return names;
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
    /// Recursively checks if a condition tree contains any SqlVariableExpression nodes
    /// (inside comparison conditions or expression conditions).
    /// </summary>
    private static bool ContainsVariableExpression(ISqlCondition? condition)
    {
        return condition switch
        {
            null => false,
            SqlComparisonCondition => false, // Literal right side, no variable possible
            SqlExpressionCondition exprCond =>
                ContainsVariableInExpression(exprCond.Left) || ContainsVariableInExpression(exprCond.Right),
            SqlLogicalCondition logical => logical.Conditions.Any(ContainsVariableExpression),
            _ => false
        };
    }

    /// <summary>
    /// Recursively checks if an expression tree contains any SqlVariableExpression nodes.
    /// </summary>
    private static bool ContainsVariableInExpression(ISqlExpression expression)
    {
        return expression switch
        {
            SqlVariableExpression => true,
            SqlBinaryExpression bin =>
                ContainsVariableInExpression(bin.Left) || ContainsVariableInExpression(bin.Right),
            SqlUnaryExpression unary => ContainsVariableInExpression(unary.Operand),
            SqlFunctionExpression func => func.Arguments.Any(ContainsVariableInExpression),
            _ => false
        };
    }

    /// <summary>
    /// Substitutes @variable references in a condition tree with their literal values
    /// from the VariableScope. This enables FetchXML pushdown for conditions that
    /// reference declared variables.
    /// </summary>
    private static ISqlCondition SubstituteVariables(ISqlCondition condition, VariableScope scope)
    {
        return condition switch
        {
            SqlExpressionCondition exprCond => SubstituteExpressionConditionVariables(exprCond, scope),
            SqlLogicalCondition logical => new SqlLogicalCondition(
                logical.Operator,
                logical.Conditions.Select(c => SubstituteVariables(c, scope)).ToList()),
            _ => condition
        };
    }

    /// <summary>
    /// Substitutes variables in an expression condition. If both sides resolve to literals
    /// after substitution, produces a SqlComparisonCondition for FetchXML pushdown.
    /// </summary>
    private static ISqlCondition SubstituteExpressionConditionVariables(
        SqlExpressionCondition exprCond, VariableScope scope)
    {
        var newLeft = SubstituteExpressionVariables(exprCond.Left, scope);
        var newRight = SubstituteExpressionVariables(exprCond.Right, scope);

        // If left is a column and right resolved to a literal, produce SqlComparisonCondition
        // for FetchXML pushdown compatibility
        if (newLeft is SqlColumnExpression colExpr && newRight is SqlLiteralExpression litExpr)
        {
            return new SqlComparisonCondition(colExpr.Column, exprCond.Operator, litExpr.Value);
        }

        return new SqlExpressionCondition(newLeft, exprCond.Operator, newRight);
    }

    /// <summary>
    /// Substitutes @variable references in an expression with literal values.
    /// </summary>
    private static ISqlExpression SubstituteExpressionVariables(ISqlExpression expression, VariableScope scope)
    {
        return expression switch
        {
            SqlVariableExpression varExpr => VariableValueToLiteral(scope.Get(varExpr.VariableName)),
            SqlBinaryExpression bin => new SqlBinaryExpression(
                SubstituteExpressionVariables(bin.Left, scope),
                bin.Operator,
                SubstituteExpressionVariables(bin.Right, scope)),
            SqlUnaryExpression unary => new SqlUnaryExpression(
                unary.Operator,
                SubstituteExpressionVariables(unary.Operand, scope)),
            _ => expression
        };
    }

    /// <summary>
    /// Converts a variable value to a SqlLiteralExpression for AST substitution.
    /// </summary>
    private static SqlLiteralExpression VariableValueToLiteral(object? value)
    {
        if (value is null)
        {
            return new SqlLiteralExpression(SqlLiteral.Null());
        }

        return value switch
        {
            string s => new SqlLiteralExpression(SqlLiteral.String(s)),
            int i => new SqlLiteralExpression(SqlLiteral.Number(i.ToString(CultureInfo.InvariantCulture))),
            long l => new SqlLiteralExpression(SqlLiteral.Number(l.ToString(CultureInfo.InvariantCulture))),
            decimal d => new SqlLiteralExpression(SqlLiteral.Number(d.ToString(CultureInfo.InvariantCulture))),
            double d => new SqlLiteralExpression(SqlLiteral.Number(d.ToString(CultureInfo.InvariantCulture))),
            float f => new SqlLiteralExpression(SqlLiteral.Number(f.ToString(CultureInfo.InvariantCulture))),
            _ => new SqlLiteralExpression(SqlLiteral.String(
                Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""))
        };
    }

    /// <summary>
    /// Creates a new SELECT statement with a replaced WHERE clause.
    /// </summary>
    private static SqlSelectStatement ReplaceWhere(SqlSelectStatement statement, ISqlCondition? newWhere)
    {
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
    /// Checks whether the SELECT list contains any computed columns (CASE/IIF expressions),
    /// excluding window function expressions which are handled by ClientWindowNode.
    /// </summary>
    private static bool HasComputedColumns(SqlSelectStatement statement)
    {
        return statement.Columns.Any(col =>
            col is SqlComputedColumn computed && computed.Expression is not SqlWindowExpression);
    }

    /// <summary>
    /// Checks whether the SELECT list contains any window function expressions.
    /// </summary>
    private static bool HasWindowFunctions(SqlSelectStatement statement)
    {
        return statement.Columns.Any(col =>
            col is SqlComputedColumn computed && computed.Expression is SqlWindowExpression);
    }

    /// <summary>
    /// Builds a ClientWindowNode that computes window function values for all matching rows.
    /// </summary>
    private static ClientWindowNode BuildWindowNode(IQueryPlanNode input, IReadOnlyList<ISqlSelectColumn> columns)
    {
        var windows = new List<WindowDefinition>();

        foreach (var column in columns)
        {
            if (column is SqlComputedColumn { Expression: SqlWindowExpression windowExpr } computed)
            {
                var outputName = computed.Alias ?? "window_" + windows.Count;

                // Compile operand
                CompiledScalarExpression? compiledOperand = null;
                if (windowExpr.Operand != null)
                {
                    compiledOperand = CompileLegacyExpression(windowExpr.Operand);
                }

                // Compile partition-by expressions
                IReadOnlyList<CompiledScalarExpression>? compiledPartitionBy = null;
                if (windowExpr.PartitionBy != null && windowExpr.PartitionBy.Count > 0)
                {
                    var partList = new List<CompiledScalarExpression>(windowExpr.PartitionBy.Count);
                    foreach (var partExpr in windowExpr.PartitionBy)
                    {
                        partList.Add(CompileLegacyExpression(partExpr));
                    }
                    compiledPartitionBy = partList;
                }

                // Compile order-by items
                IReadOnlyList<CompiledOrderByItem>? compiledOrderBy = null;
                if (windowExpr.OrderBy != null && windowExpr.OrderBy.Count > 0)
                {
                    var orderList = new List<CompiledOrderByItem>(windowExpr.OrderBy.Count);
                    foreach (var orderItem in windowExpr.OrderBy)
                    {
                        var colName = orderItem.Column.GetFullName();
                        var compiled = CompileLegacyExpression(
                            new SqlColumnExpression(orderItem.Column));
                        orderList.Add(new CompiledOrderByItem(
                            colName, compiled, orderItem.Direction == SqlSortDirection.Descending));
                    }
                    compiledOrderBy = orderList;
                }

                windows.Add(new WindowDefinition(
                    outputName,
                    windowExpr.FunctionName,
                    compiledOperand,
                    compiledPartitionBy,
                    compiledOrderBy,
                    windowExpr.IsCountStar));
            }
        }

        return new ClientWindowNode(input, windows);
    }

    /// <summary>
    /// Builds a plan for querying metadata virtual tables (metadata.entity, metadata.attribute, etc.).
    /// Bypasses FetchXML transpilation entirely — routes to MetadataScanNode which calls
    /// the Dataverse metadata API.
    /// </summary>
    private static QueryPlanResult PlanMetadataQuery(SqlSelectStatement statement, string entityName)
    {
        // Extract the table name after "metadata." prefix
        var metadataTable = entityName;
        if (metadataTable.StartsWith("metadata.", StringComparison.OrdinalIgnoreCase))
            metadataTable = metadataTable["metadata.".Length..];

        // Extract requested column names from SELECT list
        List<string>? requestedColumns = null;
        if (statement.Columns.Count > 0 &&
            !(statement.Columns.Count == 1 && statement.Columns[0] is SqlColumnRef { IsWildcard: true }))
        {
            requestedColumns = new List<string>();
            foreach (var col in statement.Columns)
            {
                if (col is SqlColumnRef colRef)
                {
                    requestedColumns.Add(colRef.Alias ?? colRef.ColumnName);
                }
            }
        }

        CompiledPredicate? metadataFilter = null;
        if (statement.Where != null)
        {
            metadataFilter = CompileLegacyCondition(statement.Where);
        }

        var scanNode = new MetadataScanNode(
            metadataTable,
            metadataExecutor: null, // Will be resolved from context at execution time
            requestedColumns,
            metadataFilter);

        return new QueryPlanResult
        {
            RootNode = scanNode,
            FetchXml = $"-- Metadata query: {metadataTable}",
            VirtualColumns = new Dictionary<string, VirtualColumnInfo>(),
            EntityLogicalName = metadataTable
        };
    }

    /// <summary>
    /// Builds a ProjectNode that passes through regular columns and evaluates computed columns.
    /// Window function columns are passed through (already computed by ClientWindowNode).
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
                case SqlComputedColumn computed when computed.Expression is SqlWindowExpression:
                    // Window columns already computed by ClientWindowNode; pass through
                    var windowAlias = computed.Alias ?? "window";
                    projections.Add(ProjectColumn.PassThrough(windowAlias));
                    break;
                case SqlComputedColumn computed:
                    var compAlias = computed.Alias ?? "computed";
                    projections.Add(ProjectColumn.Computed(compAlias, CompileLegacyExpression(computed.Expression)));
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

        // Build MergeAggregateColumn descriptors from the SQL AST
        var mergeColumns = BuildMergeAggregateColumns(statement);
        var groupByColumns = statement.GroupBy.Select(g => g.Alias ?? g.ColumnName).ToList();

        // Inject companion COUNT attributes for AVG columns into the FetchXML
        // so each partition returns the row count needed for weighted average merging.
        var enrichedFetchXml = InjectAvgCompanionCounts(transpileResult.FetchXml, mergeColumns);

        // Create an AdaptiveAggregateScanNode per partition. Each node stores the
        // template FetchXML and its date range, enabling recursive retry with
        // binary splitting if a partition exceeds the 50K aggregate limit.
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

        // Wrap in ParallelPartitionNode for concurrent execution
        var parallelNode = new ParallelPartitionNode(partitionNodes, options.PoolCapacity);

        // Add MergeAggregateNode on top to combine partial results
        IQueryPlanNode rootNode = new MergeAggregateNode(parallelNode, mergeColumns, groupByColumns);

        // HAVING clause: apply after merging (filters on merged aggregate results)
        if (statement.Having != null)
        {
            var predicate = CompileLegacyCondition(statement.Having);
            var description = DescribeLegacyCondition(statement.Having);
            rootNode = new ClientFilterNode(rootNode, predicate, description, statement.Having);
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

        // Alias counter must match SqlToFetchXmlTranspiler.GenerateAlias:
        // when no explicit alias is provided, the transpiler generates
        // "{function}_{counter}" (e.g., "count_1", "sum_2"). The merge
        // node must use the same aliases to find values in the row.
        var aliasCounter = 0;

        foreach (var agg in statement.GetAggregateColumns())
        {
            aliasCounter++;
            var alias = agg.Alias ?? $"{agg.Function.ToString().ToLowerInvariant()}_{aliasCounter}";
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
            SqlAggregateFunction.Stdev => AggregateFunction.Stdev,
            SqlAggregateFunction.Var => AggregateFunction.Var,
            _ => throw new ArgumentOutOfRangeException(nameof(sqlFunc), sqlFunc, "Unsupported aggregate function")
        };
    }

    /// <summary>
    /// For each AVG aggregate attribute in the FetchXML, injects a companion
    /// countcolumn aggregate attribute so that MergeAggregateNode can compute
    /// weighted averages across partitions.
    /// </summary>
    internal static string InjectAvgCompanionCounts(string fetchXml, IReadOnlyList<MergeAggregateColumn> mergeColumns)
    {
        // Only process if there are AVG columns that need companion counts
        var avgColumns = mergeColumns.Where(c => c.Function == AggregateFunction.Avg && c.CountAlias != null).ToList();
        if (avgColumns.Count == 0) return fetchXml;

        var doc = XDocument.Parse(fetchXml);
        var entityElement = doc.Root?.Element("entity");
        if (entityElement == null) return fetchXml;

        foreach (var avgCol in avgColumns)
        {
            // Find the matching AVG attribute element by alias
            var avgAttr = entityElement.Elements("attribute")
                .FirstOrDefault(a => string.Equals(a.Attribute("alias")?.Value, avgCol.Alias, StringComparison.OrdinalIgnoreCase));

            if (avgAttr == null) continue;

            // Get the attribute name from the AVG element
            var attrName = avgAttr.Attribute("name")?.Value;
            if (attrName == null) continue;

            // Inject companion countcolumn attribute right after the AVG attribute
            var countElement = new XElement("attribute",
                new XAttribute("name", attrName),
                new XAttribute("alias", avgCol.CountAlias!),
                new XAttribute("aggregate", "countcolumn"));

            avgAttr.AddAfterSelf(countElement);
        }

        return doc.ToString(SaveOptions.DisableFormatting);
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

    // ═══════════════════════════════════════════════════════════════════
    //  Legacy AST bridge helpers
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Compiles a legacy <see cref="ISqlCondition"/> into a <see cref="CompiledPredicate"/>
    /// by wrapping the <see cref="ExpressionEvaluator"/>.
    /// </summary>
    internal static CompiledPredicate CompileLegacyCondition(Sql.Ast.ISqlCondition condition)
    {
        var evaluator = new ExpressionEvaluator();
        return row => evaluator.EvaluateCondition(condition, row);
    }

    /// <summary>
    /// Compiles a legacy <see cref="ISqlExpression"/> into a <see cref="CompiledScalarExpression"/>
    /// by wrapping the <see cref="ExpressionEvaluator"/>.
    /// </summary>
    internal static CompiledScalarExpression CompileLegacyExpression(Sql.Ast.ISqlExpression expression)
    {
        var evaluator = new ExpressionEvaluator();
        return row => evaluator.Evaluate(expression, row);
    }

    /// <summary>
    /// Produces a human-readable description of a legacy <see cref="ISqlCondition"/>.
    /// </summary>
    internal static string DescribeLegacyCondition(Sql.Ast.ISqlCondition condition)
    {
        return condition switch
        {
            Sql.Ast.SqlComparisonCondition comp => $"{comp.Column.GetFullName()} {comp.Operator} {comp.Value.Value}",
            Sql.Ast.SqlExpressionCondition expr => $"expr {expr.Operator} expr",
            Sql.Ast.SqlLogicalCondition logical => $"({logical.Operator} with {logical.Conditions.Count} conditions)",
            _ => condition.GetType().Name
        };
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
