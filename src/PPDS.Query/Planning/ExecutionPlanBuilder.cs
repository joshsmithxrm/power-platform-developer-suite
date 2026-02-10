using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using PPDS.Dataverse.Query;
using PPDS.Dataverse.Query.Execution;
using PPDS.Dataverse.Query.Planning;
using PPDS.Dataverse.Query.Planning.Nodes;
using PPDS.Dataverse.Query.Planning.Partitioning;
using PPDS.Dataverse.Sql.Ast;
using PPDS.Dataverse.Sql.Transpilation;
using PPDS.Query.Execution;
using PPDS.Query.Parsing;
using PPDS.Query.Planning.Nodes;

namespace PPDS.Query.Planning;

/// <summary>
/// Builds an execution plan from a ScriptDom <see cref="TSqlFragment"/> AST.
/// This is the v3 replacement for the legacy <see cref="QueryPlanner"/> that worked with
/// the custom PPDS SQL AST. It walks the ScriptDom AST directly and bridges to legacy AST
/// types where plan nodes still require them, producing the same
/// <see cref="IQueryPlanNode"/> tree that the existing executor expects.
/// </summary>
public sealed class ExecutionPlanBuilder
{
    private readonly IFetchXmlGeneratorService _fetchXmlGenerator;
    private readonly SessionContext? _sessionContext;
    private readonly ExpressionCompiler _expressionCompiler;

    /// <summary>
    /// Initializes a new instance of the <see cref="ExecutionPlanBuilder"/> class.
    /// </summary>
    /// <param name="fetchXmlGenerator">
    /// Service that generates FetchXML from ScriptDom fragments. Injected to decouple
    /// plan construction from FetchXML transpilation (wired in a later phase).
    /// </param>
    /// <param name="sessionContext">
    /// Optional session context for cursor, impersonation, and temp table state.
    /// When null, cursor and impersonation statements will throw at plan time.
    /// </param>
    public ExecutionPlanBuilder(IFetchXmlGeneratorService fetchXmlGenerator, SessionContext? sessionContext = null)
    {
        _fetchXmlGenerator = fetchXmlGenerator
            ?? throw new ArgumentNullException(nameof(fetchXmlGenerator));
        _sessionContext = sessionContext;
        _expressionCompiler = new ExpressionCompiler();
    }

    /// <summary>
    /// Builds an execution plan for a parsed ScriptDom AST.
    /// </summary>
    /// <param name="fragment">The parsed ScriptDom AST fragment (from <see cref="QueryParser"/>).</param>
    /// <param name="options">Planning options (pool capacity, row limits, etc.).</param>
    /// <returns>The execution plan result containing the root node and metadata.</returns>
    /// <exception cref="QueryParseException">If the statement type is not supported.</exception>
    public QueryPlanResult Plan(TSqlFragment fragment, QueryPlanOptions? options = null)
    {
        var opts = options ?? new QueryPlanOptions();

        // Extract the first statement from the script
        var statement = ExtractFirstStatement(fragment);

        return PlanStatement(statement, opts);
    }

    /// <summary>
    /// Plans a single TSqlStatement. Entry point for recursive planning (used by script execution).
    /// </summary>
    public QueryPlanResult PlanStatement(TSqlStatement statement, QueryPlanOptions options)
    {
        return statement switch
        {
            SelectStatement selectStmt => PlanSelectStatement(selectStmt, options),
            InsertStatement insertStmt => PlanInsert(insertStmt, options),
            UpdateStatement updateStmt => PlanUpdate(updateStmt, options),
            DeleteStatement deleteStmt => PlanDelete(deleteStmt, options),
            IfStatement ifStmt => PlanScript(new[] { ifStmt }, options),
            WhileStatement whileStmt => PlanScript(new[] { whileStmt }, options),
            DeclareVariableStatement declareStmt => PlanScript(new[] { declareStmt }, options),
            BeginEndBlockStatement blockStmt when ContainsTryCatch(blockStmt) => PlanScript(ConvertTryCatchBlock(blockStmt), options),
            BeginEndBlockStatement blockStmt => PlanScript(blockStmt.StatementList.Statements.Cast<TSqlStatement>().ToArray(), options),
            TryCatchStatement tryCatchStmt => PlanScript(new TSqlStatement[] { tryCatchStmt }, options),
            CreateTableStatement createTable when IsTempTable(createTable) => PlanScript(new[] { createTable }, options),
            DropTableStatement dropTable => PlanScript(new[] { dropTable }, options),
            DeclareCursorStatement declareCursor => PlanDeclareCursor(declareCursor, options),
            OpenCursorStatement openCursor => PlanOpenCursor(openCursor),
            FetchCursorStatement fetchCursor => PlanFetchCursor(fetchCursor),
            CloseCursorStatement closeCursor => PlanCloseCursor(closeCursor),
            DeallocateCursorStatement deallocateCursor => PlanDeallocateCursor(deallocateCursor),
            ExecuteAsStatement executeAs => PlanExecuteAs(executeAs),
            RevertStatement revert => PlanRevert(revert),
            ExecuteStatement exec => PlanExecuteMessage(exec),
            MergeStatement mergeStmt => PlanMerge(mergeStmt, options),
            _ => throw new QueryParseException($"Unsupported statement type: {statement.GetType().Name}")
        };
    }

    // ═══════════════════════════════════════════════════════════════════
    //  SELECT planning
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Plans a ScriptDom SelectStatement, handling simple SELECT, UNION, INTERSECT, EXCEPT,
    /// CTEs, and OFFSET/FETCH queries.
    /// </summary>
    private QueryPlanResult PlanSelectStatement(SelectStatement selectStmt, QueryPlanOptions options)
    {
        // CTEs: WITH cte AS (...) SELECT ...
        if (selectStmt.WithCtesAndXmlNamespaces?.CommonTableExpressions?.Count > 0)
        {
            return PlanWithCtes(selectStmt, options);
        }

        // UNION / UNION ALL / INTERSECT / EXCEPT
        if (selectStmt.QueryExpression is BinaryQueryExpression binaryQuery)
        {
            return PlanBinaryQuery(selectStmt, binaryQuery, options);
        }

        // Regular SELECT (may have OFFSET/FETCH)
        if (selectStmt.QueryExpression is QuerySpecification querySpec)
        {
            return PlanSelect(selectStmt, querySpec, options);
        }

        throw new QueryParseException(
            $"Unsupported query expression type: {selectStmt.QueryExpression?.GetType().Name ?? "null"}");
    }

    /// <summary>
    /// Plans a regular SELECT query (non-UNION).
    /// Mirrors the existing QueryPlanner.PlanSelect behavior.
    /// </summary>
    private QueryPlanResult PlanSelect(
        SelectStatement selectStmt,
        QuerySpecification querySpec,
        QueryPlanOptions options)
    {
        // Table-valued function routing: STRING_SPLIT
        var tvfResult = TryPlanTableValuedFunction(querySpec, options);
        if (tvfResult != null)
        {
            return tvfResult;
        }

        // Convert to legacy AST for analysis helpers
        var legacySelect = ConvertToLegacySelect(selectStmt, querySpec);
        var entityName = legacySelect.GetEntityName();

        // Phase 6: Metadata virtual table routing
        if (entityName.StartsWith("metadata.", StringComparison.OrdinalIgnoreCase))
        {
            return PlanMetadataQuery(legacySelect, entityName);
        }

        // Phase 3.5: TDS Endpoint routing
        if (options.UseTdsEndpoint
            && options.TdsQueryExecutor != null
            && !string.IsNullOrEmpty(options.OriginalSql))
        {
            var compatibility = TdsCompatibilityChecker.CheckCompatibility(
                options.OriginalSql, entityName);

            if (compatibility == TdsCompatibility.Compatible)
            {
                return PlanTds(legacySelect, options);
            }
        }

        // Generate FetchXML using the injected service
        var transpileResult = _fetchXmlGenerator.Generate(selectStmt);

        // Phase 4: Aggregate partitioning
        if (ShouldPartitionAggregate(legacySelect, options))
        {
            return PlanAggregateWithPartitioning(legacySelect, options, transpileResult);
        }

        // When caller provides a page number or paging cookie, use single-page mode
        var isCallerPaged = options.PageNumber.HasValue || options.PagingCookie != null;

        var scanNode = new FetchXmlScanNode(
            transpileResult.FetchXml,
            entityName,
            autoPage: !isCallerPaged,
            maxRows: options.MaxRows ?? legacySelect.Top,
            initialPageNumber: options.PageNumber,
            initialPagingCookie: options.PagingCookie,
            includeCount: options.IncludeCount);

        // Start with scan as root; apply client-side operators on top.
        IQueryPlanNode rootNode = scanNode;

        // Wrap with PrefetchScanNode for page-ahead buffering
        if (options.EnablePrefetch && !legacySelect.HasAggregates() && !isCallerPaged)
        {
            rootNode = new PrefetchScanNode(rootNode, options.PrefetchBufferSize);
        }

        // Expression conditions in WHERE (column-to-column, computed expressions)
        var clientWhereCondition = ExtractExpressionConditions(legacySelect.Where);
        if (clientWhereCondition != null)
        {
            var predicate = CompileLegacyCondition(clientWhereCondition);
            var description = DescribeLegacyCondition(clientWhereCondition);
            rootNode = new ClientFilterNode(rootNode, predicate, description);
        }

        // HAVING clause: compile from ScriptDom if available, else bridge from legacy
        if (querySpec.HavingClause?.SearchCondition != null)
        {
            var predicate = _expressionCompiler.CompilePredicate(querySpec.HavingClause.SearchCondition);
            var description = querySpec.HavingClause.SearchCondition.ToString() ?? "HAVING";
            rootNode = new ClientFilterNode(rootNode, predicate, description);
        }
        else if (legacySelect.Having != null)
        {
            var predicate = CompileLegacyCondition(legacySelect.Having);
            var description = DescribeLegacyCondition(legacySelect.Having);
            rootNode = new ClientFilterNode(rootNode, predicate, description);
        }

        // Window functions
        if (HasWindowFunctions(legacySelect))
        {
            rootNode = BuildWindowNode(rootNode, legacySelect.Columns);
        }

        // Computed columns (CASE/IIF expressions)
        if (HasComputedColumns(legacySelect))
        {
            rootNode = BuildProjectNode(rootNode, legacySelect.Columns);
        }

        // OFFSET/FETCH paging
        if (querySpec.OffsetClause != null)
        {
            rootNode = BuildOffsetFetchNode(rootNode, querySpec.OffsetClause);
        }

        return new QueryPlanResult
        {
            RootNode = rootNode,
            FetchXml = transpileResult.FetchXml,
            VirtualColumns = transpileResult.VirtualColumns,
            EntityLogicalName = entityName
        };
    }

    // ═══════════════════════════════════════════════════════════════════
    //  INSERT planning
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Plans an INSERT statement. Handles both INSERT VALUES and INSERT SELECT.
    /// </summary>
    private QueryPlanResult PlanInsert(InsertStatement insert, QueryPlanOptions options)
    {
        var targetEntity = GetInsertTargetEntity(insert);
        var columns = GetInsertColumns(insert);

        IQueryPlanNode rootNode;

        // Check if this is INSERT ... SELECT
        var selectSource = GetInsertSelectSource(insert);
        if (selectSource != null)
        {
            // INSERT SELECT: plan the source SELECT, wrap with DmlExecuteNode
            var sourceSelectStmt = ConvertSelectStatement(selectSource);
            var sourceResult = PlanSelectFromLegacy(sourceSelectStmt, options);

            var sourceColumns = ExtractSelectColumnNames(sourceSelectStmt);
            rootNode = DmlExecuteNode.InsertSelect(
                targetEntity,
                columns,
                sourceResult.RootNode,
                sourceColumns: sourceColumns,
                rowCap: options.DmlRowCap ?? int.MaxValue);
        }
        else
        {
            // INSERT VALUES: compile ScriptDom expressions directly to delegates
            var compiledRows = new List<IReadOnlyList<CompiledScalarExpression>>();
            if (insert.InsertSpecification.InsertSource is ValuesInsertSource valuesSource)
            {
                foreach (var rowValue in valuesSource.RowValues)
                {
                    var compiledRow = new List<CompiledScalarExpression>();
                    foreach (var colVal in rowValue.ColumnValues)
                    {
                        compiledRow.Add(_expressionCompiler.CompileScalar(colVal));
                    }
                    compiledRows.Add(compiledRow);
                }
            }

            rootNode = DmlExecuteNode.InsertValues(
                targetEntity,
                columns,
                compiledRows,
                rowCap: options.DmlRowCap ?? int.MaxValue);
        }

        return new QueryPlanResult
        {
            RootNode = rootNode,
            FetchXml = $"-- DML: INSERT INTO {targetEntity}",
            VirtualColumns = new Dictionary<string, VirtualColumnInfo>(),
            EntityLogicalName = targetEntity
        };
    }

    // ═══════════════════════════════════════════════════════════════════
    //  UPDATE planning
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Plans an UPDATE statement. Creates a SELECT to find matching records, wraps with DmlExecuteNode.
    /// </summary>
    private QueryPlanResult PlanUpdate(UpdateStatement update, QueryPlanOptions options)
    {
        var targetTable = GetUpdateTargetTable(update);
        var entityName = targetTable.TableName;
        var where = GetUpdateWhere(update);

        // Compile SET clauses directly from ScriptDom AST to delegates
        var compiledClauses = new List<CompiledSetClause>();
        var referencedColumnNames = new List<string>();

        foreach (var setClause in update.UpdateSpecification.SetClauses)
        {
            if (setClause is AssignmentSetClause assignment)
            {
                var colName = assignment.Column?.MultiPartIdentifier?.Identifiers?.Count > 0
                    ? assignment.Column.MultiPartIdentifier.Identifiers[
                        assignment.Column.MultiPartIdentifier.Identifiers.Count - 1].Value
                    : "unknown";
                var compiled = _expressionCompiler.CompileScalar(assignment.NewValue);
                compiledClauses.Add(new CompiledSetClause(colName, compiled));

                // Also extract column names referenced in the expression for the SELECT
                var legacyValue = ConvertExpression(assignment.NewValue);
                var refCols = ExtractColumnNames(legacyValue);
                referencedColumnNames.AddRange(refCols);
            }
        }

        // Build a SELECT to find records matching the WHERE clause.
        // SELECT entityid FROM entity WHERE ...
        var idColumn = SqlColumnRef.Simple(entityName + "id");
        var selectColumns = new List<ISqlSelectColumn> { idColumn };

        // Also include any columns referenced in SET clause expressions
        foreach (var colName in referencedColumnNames)
        {
            if (!selectColumns.Exists(c => c is SqlColumnRef cr && cr.ColumnName == colName))
            {
                selectColumns.Add(SqlColumnRef.Simple(colName));
            }
        }

        // Extract JOINs from the UPDATE's FROM clause
        var fromClause = update.UpdateSpecification.FromClause;
        var joins = fromClause != null
            ? ConvertJoins(fromClause)
            : new List<SqlJoin>();

        var selectStatement = new SqlSelectStatement(
            selectColumns,
            targetTable,
            joins,
            where);

        var selectResult = PlanSelectFromLegacy(selectStatement, options);

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

    // ═══════════════════════════════════════════════════════════════════
    //  DELETE planning
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Plans a DELETE statement. Creates a SELECT to find matching record IDs, wraps with DmlExecuteNode.
    /// </summary>
    private QueryPlanResult PlanDelete(DeleteStatement delete, QueryPlanOptions options)
    {
        var targetTable = GetDeleteTargetTable(delete);
        var entityName = targetTable.TableName;
        var where = GetDeleteWhere(delete);

        // Build a SELECT to find record IDs matching the WHERE clause.
        var idColumn = SqlColumnRef.Simple(entityName + "id");

        // Extract JOINs from the DELETE's FROM clause
        var deleteFromClause = delete.DeleteSpecification.FromClause;
        var joins = deleteFromClause != null
            ? ConvertJoins(deleteFromClause)
            : new List<SqlJoin>();

        var selectStatement = new SqlSelectStatement(
            new ISqlSelectColumn[] { idColumn },
            targetTable,
            joins,
            where);

        var selectResult = PlanSelectFromLegacy(selectStatement, options);

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

    // ═══════════════════════════════════════════════════════════════════
    //  UNION / INTERSECT / EXCEPT planning
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Plans a binary query expression (UNION, UNION ALL, INTERSECT, EXCEPT).
    /// Routes INTERSECT/EXCEPT to dedicated plan nodes; UNION follows existing logic.
    /// </summary>
    private QueryPlanResult PlanBinaryQuery(
        SelectStatement selectStmt,
        BinaryQueryExpression binaryQuery,
        QueryPlanOptions options)
    {
        // For INTERSECT and EXCEPT, handle as two-branch operations (no flattening)
        if (binaryQuery.BinaryQueryExpressionType == BinaryQueryExpressionType.Intersect)
        {
            return PlanIntersectOrExcept(binaryQuery, options, isIntersect: true);
        }

        if (binaryQuery.BinaryQueryExpressionType == BinaryQueryExpressionType.Except)
        {
            return PlanIntersectOrExcept(binaryQuery, options, isIntersect: false);
        }

        // UNION / UNION ALL
        return PlanUnion(selectStmt, binaryQuery, options);
    }

    /// <summary>
    /// Plans an INTERSECT or EXCEPT query. Plans left and right branches independently,
    /// then wraps with IntersectNode or ExceptNode.
    /// </summary>
    private QueryPlanResult PlanIntersectOrExcept(
        BinaryQueryExpression binaryQuery,
        QueryPlanOptions options,
        bool isIntersect)
    {
        var leftNode = PlanQueryExpression(binaryQuery.FirstQueryExpression, options);
        var rightNode = PlanQueryExpression(binaryQuery.SecondQueryExpression, options);

        // Validate column count
        ValidateBranchColumnCount(binaryQuery.FirstQueryExpression, binaryQuery.SecondQueryExpression);

        IQueryPlanNode rootNode = isIntersect
            ? new IntersectNode(leftNode.RootNode, rightNode.RootNode)
            : new ExceptNode(leftNode.RootNode, rightNode.RootNode);

        var operatorName = isIntersect ? "INTERSECT" : "EXCEPT";

        return new QueryPlanResult
        {
            RootNode = rootNode,
            FetchXml = $"{leftNode.FetchXml}\n-- {operatorName} --\n{rightNode.FetchXml}",
            VirtualColumns = new Dictionary<string, VirtualColumnInfo>(),
            EntityLogicalName = leftNode.EntityLogicalName
        };
    }

    /// <summary>
    /// Plans a single QueryExpression (either a QuerySpecification or a nested BinaryQueryExpression).
    /// Used to plan individual branches of INTERSECT/EXCEPT/UNION.
    /// </summary>
    private QueryPlanResult PlanQueryExpression(QueryExpression queryExpr, QueryPlanOptions options)
    {
        if (queryExpr is QuerySpecification querySpec)
        {
            var legacySelect = ConvertSelectStatement(querySpec);
            return PlanSelectFromLegacy(legacySelect, options);
        }

        if (queryExpr is BinaryQueryExpression nestedBinary)
        {
            // Create a synthetic SelectStatement for nested binary expressions
            var syntheticSelect = new SelectStatement { QueryExpression = nestedBinary };
            return PlanBinaryQuery(syntheticSelect, nestedBinary, options);
        }

        throw new QueryParseException(
            $"Unsupported query expression type in set operation: {queryExpr.GetType().Name}");
    }

    /// <summary>
    /// Validates that two query expressions have compatible column counts.
    /// </summary>
    private static void ValidateBranchColumnCount(
        QueryExpression left, QueryExpression right)
    {
        if (left is QuerySpecification leftSpec && right is QuerySpecification rightSpec)
        {
            var leftCount = GetColumnCount(leftSpec);
            var rightCount = GetColumnCount(rightSpec);
            if (leftCount >= 0 && rightCount >= 0 && leftCount != rightCount)
            {
                throw new QueryParseException(
                    $"All queries in a set operation must have the same number of columns. " +
                    $"Left side has {leftCount} columns, but right side has {rightCount}.");
            }
        }
    }

    /// <summary>
    /// Plans a UNION / UNION ALL query. Each SELECT branch is planned independently,
    /// then concatenated. UNION (without ALL) adds DistinctNode for deduplication.
    /// </summary>
    private QueryPlanResult PlanUnion(
        SelectStatement selectStmt,
        BinaryQueryExpression binaryQuery,
        QueryPlanOptions options)
    {
        // Flatten the union tree into a list of queries
        var querySpecs = new List<QuerySpecification>();
        var isUnionAll = new List<bool>();
        FlattenUnion(binaryQuery, querySpecs, isUnionAll);

        // Validate column count consistency
        var firstColumnCount = GetColumnCount(querySpecs[0]);
        for (var i = 1; i < querySpecs.Count; i++)
        {
            var colCount = GetColumnCount(querySpecs[i]);
            if (firstColumnCount >= 0 && colCount >= 0 && colCount != firstColumnCount)
            {
                throw new QueryParseException(
                    $"All queries in a UNION must have the same number of columns. " +
                    $"Query 1 has {firstColumnCount} columns, but query {i + 1} has {colCount}.");
            }
        }

        // Plan each SELECT branch independently
        var branchNodes = new List<IQueryPlanNode>();
        var allFetchXml = new List<string>();
        string? firstEntityName = null;

        foreach (var querySpec in querySpecs)
        {
            var legacySelect = ConvertSelectStatement(querySpec);
            var branchResult = PlanSelectFromLegacy(legacySelect, options);
            branchNodes.Add(branchResult.RootNode);
            allFetchXml.Add(branchResult.FetchXml);
            firstEntityName ??= branchResult.EntityLogicalName;
        }

        // Build the plan tree: ConcatenateNode for all branches
        IQueryPlanNode rootNode = new ConcatenateNode(branchNodes);

        // If any boundary is UNION (not ALL), wrap with DistinctNode
        var needsDistinct = isUnionAll.Any(isAll => !isAll);
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

    // ═══════════════════════════════════════════════════════════════════
    //  CTE planning
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Plans a SELECT statement that has one or more Common Table Expressions (CTEs).
    /// Non-recursive CTEs are materialized first, then the outer query is planned
    /// with CTE references available as <see cref="CteScanNode"/> instances.
    /// </summary>
    private QueryPlanResult PlanWithCtes(SelectStatement selectStmt, QueryPlanOptions options)
    {
        var ctes = selectStmt.WithCtesAndXmlNamespaces.CommonTableExpressions;
        var cteNames = new List<string>();

        foreach (CommonTableExpression cte in ctes)
        {
            cteNames.Add(cte.ExpressionName.Value);
        }

        // For now, plan as non-recursive CTEs by materializing each CTE's query.
        // The CTE data will be made available to the outer query via CteScanNode.
        // Recursive CTE detection: a CTE is recursive if its query body references
        // the CTE's own name in a FROM clause.
        // TODO: Phase 2d - detect and handle recursive CTEs with RecursiveCteNode.

        // Plan the outer query. We wrap the result: the CTE planning is structural
        // at this point and will be fully realized during execution.
        // For non-recursive CTEs, we create CteScanNode placeholders.

        // Strip the CTE clause and plan the outer query normally
        var outerResult = PlanQueryExpressionAsSelect(selectStmt.QueryExpression, options);

        // Wrap with CTE metadata for the plan result
        return new QueryPlanResult
        {
            RootNode = outerResult.RootNode,
            FetchXml = $"-- CTE: {string.Join(", ", cteNames)} --\n{outerResult.FetchXml}",
            VirtualColumns = outerResult.VirtualColumns,
            EntityLogicalName = outerResult.EntityLogicalName
        };
    }

    /// <summary>
    /// Plans a QueryExpression as if it were a standalone SELECT statement.
    /// Used for planning the outer query of a CTE.
    /// </summary>
    private QueryPlanResult PlanQueryExpressionAsSelect(
        QueryExpression queryExpr, QueryPlanOptions options)
    {
        if (queryExpr is QuerySpecification querySpec)
        {
            var syntheticSelect = new SelectStatement { QueryExpression = querySpec };
            return PlanSelect(syntheticSelect, querySpec, options);
        }

        if (queryExpr is BinaryQueryExpression binaryQuery)
        {
            var syntheticSelect = new SelectStatement { QueryExpression = binaryQuery };
            return PlanBinaryQuery(syntheticSelect, binaryQuery, options);
        }

        throw new QueryParseException(
            $"Unsupported CTE outer query expression type: {queryExpr.GetType().Name}");
    }

    // ═══════════════════════════════════════════════════════════════════
    //  OFFSET/FETCH planning
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Builds an <see cref="OffsetFetchNode"/> from a ScriptDom <see cref="OffsetClause"/>.
    /// Extracts the integer literal values for OFFSET and FETCH.
    /// </summary>
    private static IQueryPlanNode BuildOffsetFetchNode(
        IQueryPlanNode input, OffsetClause offsetClause)
    {
        var offset = ExtractIntegerLiteral(offsetClause.OffsetExpression, "OFFSET");
        var fetch = -1;

        if (offsetClause.FetchExpression != null)
        {
            fetch = ExtractIntegerLiteral(offsetClause.FetchExpression, "FETCH");
        }

        return new OffsetFetchNode(input, offset, fetch);
    }

    /// <summary>
    /// Extracts an integer value from a ScriptDom scalar expression (for OFFSET/FETCH values).
    /// Supports integer literals and unary minus expressions.
    /// </summary>
    private static int ExtractIntegerLiteral(ScalarExpression expression, string context)
    {
        if (expression is IntegerLiteral intLiteral)
        {
            if (int.TryParse(intLiteral.Value, out var value))
                return value;
        }

        if (expression is UnaryExpression unary
            && unary.UnaryExpressionType == UnaryExpressionType.Negative
            && unary.Expression is IntegerLiteral negLiteral)
        {
            if (int.TryParse(negLiteral.Value, out var value))
                return -value;
        }

        throw new QueryParseException(
            $"{context} value must be an integer literal, got: {expression.GetType().Name}");
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Script (IF/ELSE, block) planning
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Plans a multi-statement script (IF/ELSE blocks, DECLARE/SET, etc.).
    /// Wraps the statements in a <see cref="ScriptExecutionNode"/> that works directly
    /// with ScriptDom types and uses this builder for inner statement planning.
    /// </summary>
    private QueryPlanResult PlanScript(
        IReadOnlyList<TSqlStatement> statements, QueryPlanOptions options)
    {
        var scriptNode = new ScriptExecutionNode(statements, this, _expressionCompiler);

        return new QueryPlanResult
        {
            RootNode = scriptNode,
            FetchXml = "-- Script: multi-statement execution",
            VirtualColumns = new Dictionary<string, VirtualColumnInfo>(),
            EntityLogicalName = "script"
        };
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Metadata query planning
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Plans a metadata virtual table query (e.g., FROM metadata.entity).
    /// Bypasses FetchXML transpilation entirely.
    /// </summary>
    private static QueryPlanResult PlanMetadataQuery(
        SqlSelectStatement statement, string entityName)
    {
        var metadataTable = entityName;
        if (metadataTable.StartsWith("metadata.", StringComparison.OrdinalIgnoreCase))
            metadataTable = metadataTable["metadata.".Length..];

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

        CompiledPredicate? filter = null;
        if (statement.Where != null)
        {
            filter = CompileLegacyCondition(statement.Where);
        }

        var scanNode = new MetadataScanNode(
            metadataTable,
            metadataExecutor: null,
            requestedColumns,
            filter);

        return new QueryPlanResult
        {
            RootNode = scanNode,
            FetchXml = $"-- Metadata query: {metadataTable}",
            VirtualColumns = new Dictionary<string, VirtualColumnInfo>(),
            EntityLogicalName = metadataTable
        };
    }

    // ═══════════════════════════════════════════════════════════════════
    //  TDS Endpoint planning
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Plans a TDS Endpoint query that sends SQL directly over the TDS wire protocol.
    /// </summary>
    private static QueryPlanResult PlanTds(
        SqlSelectStatement statement, QueryPlanOptions options)
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

    // ═══════════════════════════════════════════════════════════════════
    //  Cursor planning
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Plans a DECLARE cursor_name CURSOR FOR SELECT ... statement.
    /// </summary>
    private QueryPlanResult PlanDeclareCursor(DeclareCursorStatement declareCursor, QueryPlanOptions options)
    {
        var session = _sessionContext
            ?? throw new QueryParseException("Cursor operations require a SessionContext.");

        var cursorName = declareCursor.Name.Value;

        // Plan the cursor's SELECT query
        var selectStmt = declareCursor.CursorDefinition.Select;
        var queryResult = PlanStatement(selectStmt, options);

        var node = new DeclareCursorNode(cursorName, queryResult.RootNode, session);

        return new QueryPlanResult
        {
            RootNode = node,
            FetchXml = $"-- DECLARE CURSOR {cursorName}",
            VirtualColumns = new Dictionary<string, VirtualColumnInfo>(),
            EntityLogicalName = "cursor"
        };
    }

    /// <summary>
    /// Plans an OPEN cursor_name statement.
    /// </summary>
    private QueryPlanResult PlanOpenCursor(OpenCursorStatement openCursor)
    {
        var session = _sessionContext
            ?? throw new QueryParseException("Cursor operations require a SessionContext.");

        var cursorName = openCursor.Cursor.Name.Value;
        var node = new OpenCursorNode(cursorName, session);

        return new QueryPlanResult
        {
            RootNode = node,
            FetchXml = $"-- OPEN CURSOR {cursorName}",
            VirtualColumns = new Dictionary<string, VirtualColumnInfo>(),
            EntityLogicalName = "cursor"
        };
    }

    /// <summary>
    /// Plans a FETCH NEXT FROM cursor_name INTO @var1, @var2, ... statement.
    /// </summary>
    private QueryPlanResult PlanFetchCursor(FetchCursorStatement fetchCursor)
    {
        var session = _sessionContext
            ?? throw new QueryParseException("Cursor operations require a SessionContext.");

        var cursorName = fetchCursor.Cursor.Name.Value;

        var intoVariables = new List<string>();
        if (fetchCursor.IntoVariables != null)
        {
            foreach (var variable in fetchCursor.IntoVariables)
            {
                var varName = variable.Name;
                if (!varName.StartsWith("@"))
                    varName = "@" + varName;
                intoVariables.Add(varName);
            }
        }

        var node = new FetchCursorNode(cursorName, intoVariables, session);

        return new QueryPlanResult
        {
            RootNode = node,
            FetchXml = $"-- FETCH NEXT FROM {cursorName}",
            VirtualColumns = new Dictionary<string, VirtualColumnInfo>(),
            EntityLogicalName = "cursor"
        };
    }

    /// <summary>
    /// Plans a CLOSE cursor_name statement.
    /// </summary>
    private QueryPlanResult PlanCloseCursor(CloseCursorStatement closeCursor)
    {
        var session = _sessionContext
            ?? throw new QueryParseException("Cursor operations require a SessionContext.");

        var cursorName = closeCursor.Cursor.Name.Value;
        var node = new CloseCursorNode(cursorName, session);

        return new QueryPlanResult
        {
            RootNode = node,
            FetchXml = $"-- CLOSE CURSOR {cursorName}",
            VirtualColumns = new Dictionary<string, VirtualColumnInfo>(),
            EntityLogicalName = "cursor"
        };
    }

    /// <summary>
    /// Plans a DEALLOCATE cursor_name statement.
    /// </summary>
    private QueryPlanResult PlanDeallocateCursor(DeallocateCursorStatement deallocateCursor)
    {
        var session = _sessionContext
            ?? throw new QueryParseException("Cursor operations require a SessionContext.");

        var cursorName = deallocateCursor.Cursor.Name.Value;
        var node = new DeallocateCursorNode(cursorName, session);

        return new QueryPlanResult
        {
            RootNode = node,
            FetchXml = $"-- DEALLOCATE CURSOR {cursorName}",
            VirtualColumns = new Dictionary<string, VirtualColumnInfo>(),
            EntityLogicalName = "cursor"
        };
    }

    // ═══════════════════════════════════════════════════════════════════
    //  EXECUTE AS / REVERT planning (impersonation)
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Plans an EXECUTE AS USER = 'user@domain.com' statement.
    /// </summary>
    private QueryPlanResult PlanExecuteAs(ExecuteAsStatement executeAs)
    {
        var session = _sessionContext
            ?? throw new QueryParseException("Impersonation operations require a SessionContext.");

        string? userName = (executeAs.ExecuteContext?.Principal as StringLiteral)?.Value;

        if (string.IsNullOrEmpty(userName))
            throw new QueryParseException("EXECUTE AS requires a user name string literal.");

        var node = new ExecuteAsNode(userName, session);

        return new QueryPlanResult
        {
            RootNode = node,
            FetchXml = $"-- EXECUTE AS USER = '{userName}'",
            VirtualColumns = new Dictionary<string, VirtualColumnInfo>(),
            EntityLogicalName = "impersonation"
        };
    }

    /// <summary>
    /// Plans a REVERT statement.
    /// </summary>
    private QueryPlanResult PlanRevert(RevertStatement revert)
    {
        var session = _sessionContext
            ?? throw new QueryParseException("Impersonation operations require a SessionContext.");

        var node = new RevertNode(session);

        return new QueryPlanResult
        {
            RootNode = node,
            FetchXml = "-- REVERT",
            VirtualColumns = new Dictionary<string, VirtualColumnInfo>(),
            EntityLogicalName = "impersonation"
        };
    }

    // ═══════════════════════════════════════════════════════════════════
    //  EXECUTE message planning
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Plans an EXEC message_name @param1 = value1, @param2 = value2 statement.
    /// </summary>
    private QueryPlanResult PlanExecuteMessage(ExecuteStatement exec)
    {
        var execSpec = exec.ExecuteSpecification;
        if (execSpec?.ExecutableEntity is not ExecutableProcedureReference procRef)
            throw new QueryParseException("EXEC statement must reference a procedure or message name.");

        var messageName = procRef.ProcedureReference?.ProcedureReference?.Name?.BaseIdentifier?.Value;
        if (string.IsNullOrEmpty(messageName))
            throw new QueryParseException("EXEC statement must specify a message name.");

        var parameters = new List<MessageParameter>();
        if (execSpec.ExecutableEntity is ExecutableProcedureReference procRefWithParams
            && procRefWithParams.Parameters != null)
        {
            foreach (var param in procRefWithParams.Parameters)
            {
                var paramName = param.Variable?.Name ?? $"param{parameters.Count}";
                if (paramName.StartsWith("@"))
                    paramName = paramName.Substring(1);

                string? paramValue = null;
                if (param.ParameterValue is StringLiteral strLit)
                {
                    paramValue = strLit.Value;
                }
                else if (param.ParameterValue is IntegerLiteral intLit)
                {
                    paramValue = intLit.Value;
                }
                else if (param.ParameterValue is NullLiteral)
                {
                    paramValue = null;
                }

                parameters.Add(new MessageParameter(paramName, paramValue));
            }
        }

        var node = new ExecuteMessageNode(messageName, parameters);

        return new QueryPlanResult
        {
            RootNode = node,
            FetchXml = $"-- EXEC {messageName}",
            VirtualColumns = new Dictionary<string, VirtualColumnInfo>(),
            EntityLogicalName = "message"
        };
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Table-valued function planning (STRING_SPLIT)
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Checks if the FROM clause references a table-valued function (e.g., STRING_SPLIT)
    /// and returns a plan for it if found.
    /// </summary>
    private static QueryPlanResult? TryPlanTableValuedFunction(
        QuerySpecification querySpec, QueryPlanOptions options)
    {
        if (querySpec.FromClause?.TableReferences == null
            || querySpec.FromClause.TableReferences.Count == 0)
        {
            return null;
        }

        var tableRef = querySpec.FromClause.TableReferences[0];

        if (tableRef is SchemaObjectFunctionTableReference funcRef)
        {
            var funcName = funcRef.SchemaObject?.BaseIdentifier?.Value;
            if (string.Equals(funcName, "STRING_SPLIT", StringComparison.OrdinalIgnoreCase))
            {
                return PlanStringSplitFromSchemaFunc(funcRef);
            }
        }

        // ScriptDom parses built-in TVFs like STRING_SPLIT as GlobalFunctionTableReference
        if (tableRef is GlobalFunctionTableReference globalFuncRef)
        {
            var funcName = globalFuncRef.Name?.Value;
            if (string.Equals(funcName, "STRING_SPLIT", StringComparison.OrdinalIgnoreCase))
            {
                return PlanStringSplitFromGlobalFunc(globalFuncRef);
            }
        }

        return null;
    }

    /// <summary>
    /// Plans a STRING_SPLIT from a SchemaObjectFunctionTableReference.
    /// </summary>
    private static QueryPlanResult PlanStringSplitFromSchemaFunc(
        SchemaObjectFunctionTableReference funcRef)
    {
        if (funcRef.Parameters == null || funcRef.Parameters.Count < 2)
        {
            throw new QueryParseException(
                "STRING_SPLIT requires at least 2 arguments: STRING_SPLIT(string, separator)");
        }

        var inputString = ExtractStringArgument(funcRef.Parameters[0]);
        var separator = ExtractStringArgument(funcRef.Parameters[1]);

        var enableOrdinal = false;
        if (funcRef.Parameters.Count >= 3 && funcRef.Parameters[2] is IntegerLiteral intLit)
        {
            enableOrdinal = intLit.Value == "1";
        }

        return BuildStringSplitResult(inputString, separator, enableOrdinal);
    }

    /// <summary>
    /// Plans a STRING_SPLIT from a GlobalFunctionTableReference.
    /// ScriptDom parses built-in TVFs like STRING_SPLIT as GlobalFunctionTableReference.
    /// </summary>
    private static QueryPlanResult PlanStringSplitFromGlobalFunc(
        GlobalFunctionTableReference globalFuncRef)
    {
        if (globalFuncRef.Parameters == null || globalFuncRef.Parameters.Count < 2)
        {
            throw new QueryParseException(
                "STRING_SPLIT requires at least 2 arguments: STRING_SPLIT(string, separator)");
        }

        var inputString = ExtractStringArgument(globalFuncRef.Parameters[0]);
        var separator = ExtractStringArgument(globalFuncRef.Parameters[1]);

        var enableOrdinal = false;
        if (globalFuncRef.Parameters.Count >= 3 && globalFuncRef.Parameters[2] is IntegerLiteral intLit)
        {
            enableOrdinal = intLit.Value == "1";
        }

        return BuildStringSplitResult(inputString, separator, enableOrdinal);
    }

    /// <summary>
    /// Builds the plan result for STRING_SPLIT.
    /// </summary>
    private static QueryPlanResult BuildStringSplitResult(
        string inputString, string separator, bool enableOrdinal)
    {
        IQueryPlanNode rootNode = new StringSplitNode(inputString, separator, enableOrdinal);

        return new QueryPlanResult
        {
            RootNode = rootNode,
            FetchXml = $"-- STRING_SPLIT('{inputString}', '{separator}')",
            VirtualColumns = new Dictionary<string, VirtualColumnInfo>(),
            EntityLogicalName = "string_split"
        };
    }

    /// <summary>
    /// Extracts a string value from a ScriptDom scalar expression (for function arguments).
    /// </summary>
    private static string ExtractStringArgument(ScalarExpression expr)
    {
        return expr switch
        {
            StringLiteral strLit => strLit.Value,
            IntegerLiteral intLit => intLit.Value,
            NullLiteral => "",
            _ => expr.ToString() ?? ""
        };
    }

    // ═══════════════════════════════════════════════════════════════════
    //  MERGE planning
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Plans a MERGE statement. Extracts source query, ON condition, and WHEN clauses,
    /// then creates a MergeNode to execute the merge logic.
    /// </summary>
    private QueryPlanResult PlanMerge(MergeStatement merge, QueryPlanOptions options)
    {
        var spec = merge.MergeSpecification;

        // Target entity
        string targetEntity;
        if (spec.Target is NamedTableReference targetTable)
        {
            targetEntity = targetTable.SchemaObject?.BaseIdentifier?.Value ?? "unknown";
        }
        else
        {
            throw new QueryParseException("MERGE target must be a named table.");
        }

        // Source query: plan the USING clause
        IQueryPlanNode sourceNode;
        if (spec.TableReference is NamedTableReference sourceTable)
        {
            // USING sourceTable - create a scan of the source table
            var sourceEntity = sourceTable.SchemaObject?.BaseIdentifier?.Value ?? "unknown";
            var sourceSelect = new SqlSelectStatement(
                new ISqlSelectColumn[] { SqlColumnRef.Wildcard() },
                new SqlTableRef(sourceEntity, sourceTable.Alias?.Value),
                new List<SqlJoin>(),
                null);
            var sourceResult = PlanSelectFromLegacy(sourceSelect, options);
            sourceNode = sourceResult.RootNode;
        }
        else if (spec.TableReference is QueryDerivedTable derivedTable
            && derivedTable.QueryExpression is QuerySpecification querySpec)
        {
            // USING (SELECT ...) AS alias
            var legacySelect = ConvertSelectStatement(querySpec);
            var sourceResult = PlanSelectFromLegacy(legacySelect, options);
            sourceNode = sourceResult.RootNode;
        }
        else
        {
            throw new QueryParseException(
                $"Unsupported MERGE source type: {spec.TableReference?.GetType().Name ?? "null"}");
        }

        // ON condition: extract match columns
        var matchColumns = ExtractMergeMatchColumns(spec.SearchCondition);

        // WHEN clauses
        MergeWhenMatched? whenMatched = null;
        MergeWhenNotMatched? whenNotMatched = null;

        if (spec.ActionClauses != null)
        {
            foreach (MergeActionClause clause in spec.ActionClauses)
            {
                if (clause.Condition == MergeCondition.Matched && clause.Action is UpdateMergeAction updateAction)
                {
                    var setClauses = new List<SqlSetClause>();
                    foreach (var setClause in updateAction.SetClauses)
                    {
                        if (setClause is AssignmentSetClause assignment)
                        {
                            var colName = assignment.Column?.MultiPartIdentifier?.Identifiers?.Count > 0
                                ? assignment.Column.MultiPartIdentifier.Identifiers[
                                    assignment.Column.MultiPartIdentifier.Identifiers.Count - 1].Value
                                : "unknown";
                            var value = ConvertExpression(assignment.NewValue);
                            setClauses.Add(new SqlSetClause(colName, value));
                        }
                    }
                    whenMatched = MergeWhenMatched.Update(setClauses);
                }
                else if (clause.Condition == MergeCondition.Matched && clause.Action is DeleteMergeAction)
                {
                    whenMatched = MergeWhenMatched.Delete();
                }
                else if (clause.Condition == MergeCondition.NotMatched && clause.Action is InsertMergeAction insertAction)
                {
                    var columns = new List<string>();
                    if (insertAction.Columns != null)
                    {
                        foreach (var col in insertAction.Columns)
                        {
                            var ids = col.MultiPartIdentifier?.Identifiers;
                            if (ids != null && ids.Count > 0)
                            {
                                columns.Add(ids[ids.Count - 1].Value);
                            }
                        }
                    }

                    var values = new List<ISqlExpression>();
                    if (insertAction.Source is ValuesInsertSource valSource
                        && valSource.RowValues?.Count > 0)
                    {
                        foreach (var val in valSource.RowValues[0].ColumnValues)
                        {
                            values.Add(ConvertExpression(val));
                        }
                    }

                    whenNotMatched = MergeWhenNotMatched.Insert(columns, values);
                }
            }
        }

        var mergeNode = new MergeNode(sourceNode, targetEntity, matchColumns, whenMatched, whenNotMatched);

        return new QueryPlanResult
        {
            RootNode = mergeNode,
            FetchXml = $"-- MERGE INTO {targetEntity}",
            VirtualColumns = new Dictionary<string, VirtualColumnInfo>(),
            EntityLogicalName = targetEntity
        };
    }

    /// <summary>
    /// Extracts match columns from a MERGE ON condition.
    /// Supports simple equality conditions (target.col = source.col).
    /// </summary>
    private static IReadOnlyList<MergeMatchColumn> ExtractMergeMatchColumns(BooleanExpression? searchCondition)
    {
        var matchColumns = new List<MergeMatchColumn>();

        if (searchCondition is BooleanComparisonExpression comp
            && comp.ComparisonType == BooleanComparisonType.Equals)
        {
            var left = GetColumnNameFromExpression(comp.FirstExpression);
            var right = GetColumnNameFromExpression(comp.SecondExpression);
            if (left != null && right != null)
            {
                matchColumns.Add(new MergeMatchColumn(right, left));
            }
        }
        else if (searchCondition is BooleanBinaryExpression binBool
            && binBool.BinaryExpressionType == BooleanBinaryExpressionType.And)
        {
            // Multiple AND conditions
            var leftMatches = ExtractMergeMatchColumns(binBool.FirstExpression);
            var rightMatches = ExtractMergeMatchColumns(binBool.SecondExpression);
            matchColumns.AddRange(leftMatches);
            matchColumns.AddRange(rightMatches);
        }

        return matchColumns;
    }

    private static string? GetColumnNameFromExpression(ScalarExpression expr)
    {
        if (expr is ColumnReferenceExpression colRef)
        {
            var ids = colRef.MultiPartIdentifier?.Identifiers;
            if (ids != null && ids.Count > 0)
            {
                return ids[ids.Count - 1].Value;
            }
        }
        return null;
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Aggregate partitioning
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Determines whether an aggregate query should be partitioned for parallel execution.
    /// Delegates to the existing <see cref="QueryPlanner.ShouldPartitionAggregate"/> logic.
    /// </summary>
    private static bool ShouldPartitionAggregate(
        SqlSelectStatement statement, QueryPlanOptions options)
    {
        return QueryPlanner.ShouldPartitionAggregate(statement, options);
    }

    /// <summary>
    /// Builds a partitioned aggregate plan (ParallelPartitionNode + MergeAggregateNode).
    /// </summary>
    private static QueryPlanResult PlanAggregateWithPartitioning(
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

        var mergeColumns = QueryPlanner.BuildMergeAggregateColumns(statement);
        var groupByColumns = statement.GroupBy.Select(g => g.Alias ?? g.ColumnName).ToList();

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

        // HAVING clause: apply after merging
        if (statement.Having != null)
        {
            var predicate = CompileLegacyCondition(statement.Having);
            var description = DescribeLegacyCondition(statement.Having);
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

    // ═══════════════════════════════════════════════════════════════════
    //  Internal helpers for plan construction
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Plans a SELECT from a legacy <see cref="SqlSelectStatement"/> AST.
    /// Used when DML statements need an internal SELECT (e.g., UPDATE needs SELECT to find records).
    /// </summary>
    private QueryPlanResult PlanSelectFromLegacy(
        SqlSelectStatement statement, QueryPlanOptions options)
    {
        var entityName = statement.GetEntityName();

        // Phase 6: Metadata virtual table routing
        if (entityName.StartsWith("metadata.", StringComparison.OrdinalIgnoreCase))
        {
            return PlanMetadataQuery(statement, entityName);
        }

        // Phase 3.5: TDS Endpoint routing
        if (options.UseTdsEndpoint
            && options.TdsQueryExecutor != null
            && !string.IsNullOrEmpty(options.OriginalSql))
        {
            var compatibility = TdsCompatibilityChecker.CheckCompatibility(
                options.OriginalSql, entityName);

            if (compatibility == TdsCompatibility.Compatible)
            {
                return PlanTds(statement, options);
            }
        }

        // Variable substitution
        if (options.VariableScope != null && statement.Where != null
            && ContainsVariableExpression(statement.Where))
        {
            var newWhere = SubstituteVariables(statement.Where, options.VariableScope);
            statement = ReplaceWhere(statement, newWhere);
        }

        // Use the legacy planner's transpiler for internal SELECTs
        // (these are synthetic SELECTs built by DML planning, not from user SQL)
        var legacyPlanner = new QueryPlanner();
        return legacyPlanner.Plan(statement, options);
    }

    /// <summary>
    /// Converts a ScriptDom SelectStatement + QuerySpecification to a legacy SqlSelectStatement.
    /// </summary>
    private static SqlSelectStatement ConvertToLegacySelect(
        SelectStatement selectStmt, QuerySpecification querySpec)
    {
        var baseSelect = ConvertSelectStatement(querySpec);

        // Apply ORDER BY from the SelectStatement level (ScriptDom sometimes has it here)
        var orderBy = new List<SqlOrderByItem>();
        if (selectStmt.QueryExpression is QuerySpecification qs && qs.OrderByClause != null)
        {
            foreach (var orderElem in qs.OrderByClause.OrderByElements)
            {
                if (orderElem.Expression is ColumnReferenceExpression orderCol)
                {
                    var direction = orderElem.SortOrder == SortOrder.Descending
                        ? SqlSortDirection.Descending
                        : SqlSortDirection.Ascending;
                    orderBy.Add(new SqlOrderByItem(ConvertColumnRef(orderCol), direction));
                }
            }
        }

        if (orderBy.Count > 0)
        {
            return new SqlSelectStatement(
                baseSelect.Columns, baseSelect.From, baseSelect.Joins, baseSelect.Where,
                orderBy, baseSelect.Top, baseSelect.Distinct, baseSelect.GroupBy,
                baseSelect.Having, baseSelect.SourcePosition, baseSelect.GroupByExpressions);
        }

        return baseSelect;
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
    /// Formats a DataTypeReference to a string for legacy DECLARE statements.
    /// </summary>
    private static string FormatDataTypeReference(DataTypeReference dataType)
    {
        if (dataType is SqlDataTypeReference sqlType)
        {
            var name = sqlType.SqlDataTypeOption.ToString().ToUpperInvariant();
            if (sqlType.Parameters.Count > 0)
            {
                var parms = string.Join(", ", sqlType.Parameters.Select(p => p.Value));
                return $"{name}({parms})";
            }
            return name;
        }
        return "NVARCHAR";
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Condition analysis helpers (ported from QueryPlanner)
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Extracts expression conditions from a WHERE clause that must be evaluated client-side.
    /// </summary>
    private static ISqlCondition? ExtractExpressionConditions(ISqlCondition? where)
    {
        if (where is null) return null;

        if (where is SqlExpressionCondition) return where;

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
                    exprConditions.Add(child);
                }
            }

            if (exprConditions.Count == 0) return null;

            return exprConditions.Count == 1
                ? exprConditions[0]
                : new SqlLogicalCondition(SqlLogicalOperator.And, exprConditions);
        }

        if (ContainsExpressionCondition(where)) return where;

        return null;
    }

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
    /// Checks whether the SELECT list contains any computed columns (CASE/IIF expressions),
    /// excluding window function expressions.
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
    /// Builds a ClientWindowNode that computes window function values.
    /// </summary>
    private static ClientWindowNode BuildWindowNode(
        IQueryPlanNode input, IReadOnlyList<ISqlSelectColumn> columns)
    {
        var windows = new List<Dataverse.Query.Planning.Nodes.WindowDefinition>();

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
                IReadOnlyList<Dataverse.Query.Planning.Nodes.CompiledOrderByItem>? compiledOrderBy = null;
                if (windowExpr.OrderBy != null && windowExpr.OrderBy.Count > 0)
                {
                    var orderList = new List<Dataverse.Query.Planning.Nodes.CompiledOrderByItem>(windowExpr.OrderBy.Count);
                    foreach (var orderItem in windowExpr.OrderBy)
                    {
                        var colName = orderItem.Column.GetFullName();
                        var compiledVal = CompileLegacyExpression(
                            new SqlColumnExpression(orderItem.Column));
                        orderList.Add(new Dataverse.Query.Planning.Nodes.CompiledOrderByItem(
                            colName, compiledVal, orderItem.Direction == SqlSortDirection.Descending));
                    }
                    compiledOrderBy = orderList;
                }

                windows.Add(new Dataverse.Query.Planning.Nodes.WindowDefinition(
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
    /// Builds a ProjectNode that passes through regular columns and evaluates computed columns.
    /// </summary>
    private static ProjectNode BuildProjectNode(
        IQueryPlanNode input, IReadOnlyList<ISqlSelectColumn> columns)
    {
        var projections = new List<ProjectColumn>();

        foreach (var column in columns)
        {
            switch (column)
            {
                case SqlColumnRef { IsWildcard: true }:
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
                    // If the batch has multiple statements, treat as a script
                    if (batch.Statements.Count > 1)
                    {
                        // Return a synthetic block for multi-statement batches
                        // For now, return the first statement
                        return batch.Statements[0];
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
    /// Extracts the output column names from a SELECT statement for ordinal mapping.
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
    /// Recursively checks if a condition tree contains any SqlVariableExpression nodes.
    /// </summary>
    private static bool ContainsVariableExpression(ISqlCondition? condition)
    {
        return condition switch
        {
            null => false,
            SqlComparisonCondition => false,
            SqlExpressionCondition exprCond =>
                ContainsVariableInExpression(exprCond.Left) || ContainsVariableInExpression(exprCond.Right),
            SqlLogicalCondition logical => logical.Conditions.Any(ContainsVariableExpression),
            _ => false
        };
    }

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
    /// Substitutes @variable references in a condition tree with their literal values.
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

    private static ISqlCondition SubstituteExpressionConditionVariables(
        SqlExpressionCondition exprCond, VariableScope scope)
    {
        var newLeft = SubstituteExpressionVariables(exprCond.Left, scope);
        var newRight = SubstituteExpressionVariables(exprCond.Right, scope);

        if (newLeft is SqlColumnExpression colExpr && newRight is SqlLiteralExpression litExpr)
        {
            return new SqlComparisonCondition(colExpr.Column, exprCond.Operator, litExpr.Value);
        }

        return new SqlExpressionCondition(newLeft, exprCond.Operator, newRight);
    }

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

    // ═══════════════════════════════════════════════════════════════════
    //  Aggregate partitioning helpers (duplicated from QueryPlanner
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
    //  Legacy AST → Compiled Delegate Helpers
    //  Bridge methods that wrap legacy ISqlCondition/ISqlExpression in
    //  CompiledPredicate/CompiledScalarExpression delegates via the
    //  ExpressionEvaluator. These will be removed when the legacy AST
    //  types are deleted and all callers use ScriptDom compilation.
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Compiles a legacy <see cref="ISqlCondition"/> into a <see cref="CompiledPredicate"/>
    /// by wrapping the <see cref="Dataverse.Query.Execution.ExpressionEvaluator"/>.
    /// </summary>
    private static CompiledPredicate CompileLegacyCondition(ISqlCondition condition)
    {
        var evaluator = new Dataverse.Query.Execution.ExpressionEvaluator();
        return row => evaluator.EvaluateCondition(condition, row);
    }

    /// <summary>
    /// Compiles a legacy <see cref="ISqlExpression"/> into a <see cref="CompiledScalarExpression"/>
    /// by wrapping the <see cref="Dataverse.Query.Execution.ExpressionEvaluator"/>.
    /// </summary>
    private static CompiledScalarExpression CompileLegacyExpression(ISqlExpression expression)
    {
        var evaluator = new Dataverse.Query.Execution.ExpressionEvaluator();
        return row => evaluator.Evaluate(expression, row);
    }

    /// <summary>
    /// Produces a human-readable description of a legacy <see cref="ISqlCondition"/>.
    /// </summary>
    private static string DescribeLegacyCondition(ISqlCondition condition)
    {
        return condition switch
        {
            SqlComparisonCondition comp => $"{comp.Column.GetFullName()} {comp.Operator} {comp.Value.Value}",
            SqlExpressionCondition expr => $"expr {expr.Operator} expr",
            SqlLogicalCondition logical => $"({logical.Operator} with {logical.Conditions.Count} conditions)",
            _ => condition.GetType().Name
        };
    }

    // ═══════════════════════════════════════════════════════════════════
    //  ScriptDom → Legacy AST bridge methods
    //  (formerly in ScriptDomAdapter; kept as private methods until
    //  legacy AST types are fully removed)
    // ═══════════════════════════════════════════════════════════════════

    // ── DML property extraction ──────────────────────────────────────

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

    private static SqlTableRef GetUpdateTargetTable(UpdateStatement update)
    {
        if (update.UpdateSpecification.Target is NamedTableReference named)
        {
            return ConvertNamedTable(named);
        }
        throw new QueryParseException("UPDATE target must be a named table.");
    }

    private static ISqlCondition? GetUpdateWhere(UpdateStatement update)
    {
        return update.UpdateSpecification.WhereClause?.SearchCondition != null
            ? ConvertBooleanExpression(update.UpdateSpecification.WhereClause.SearchCondition)
            : null;
    }

    private static SqlTableRef GetDeleteTargetTable(DeleteStatement delete)
    {
        if (delete.DeleteSpecification.Target is NamedTableReference named)
        {
            return ConvertNamedTable(named);
        }
        throw new QueryParseException("DELETE target must be a named table.");
    }

    private static ISqlCondition? GetDeleteWhere(DeleteStatement delete)
    {
        return delete.DeleteSpecification.WhereClause?.SearchCondition != null
            ? ConvertBooleanExpression(delete.DeleteSpecification.WhereClause.SearchCondition)
            : null;
    }

    // ── Expression conversion ────────────────────────────────────────

    private static ISqlExpression ConvertExpression(ScalarExpression expr)
    {
        if (expr is null)
            throw new ArgumentNullException(nameof(expr));

        return expr switch
        {
            IntegerLiteral intLit =>
                new SqlLiteralExpression(SqlLiteral.Number(intLit.Value)),

            NumericLiteral numLit =>
                new SqlLiteralExpression(SqlLiteral.Number(numLit.Value)),

            RealLiteral realLit =>
                new SqlLiteralExpression(SqlLiteral.Number(realLit.Value)),

            MoneyLiteral moneyLit =>
                new SqlLiteralExpression(SqlLiteral.Number(moneyLit.Value)),

            StringLiteral strLit =>
                new SqlLiteralExpression(SqlLiteral.String(strLit.Value)),

            NullLiteral =>
                new SqlLiteralExpression(SqlLiteral.Null()),

            ColumnReferenceExpression colRef =>
                new SqlColumnExpression(ConvertColumnRef(colRef)),

            BinaryExpression binExpr =>
                new SqlBinaryExpression(
                    ConvertExpression(binExpr.FirstExpression),
                    ConvertBinaryOperator(binExpr.BinaryExpressionType),
                    ConvertExpression(binExpr.SecondExpression)),

            UnaryExpression unaryExpr =>
                new SqlUnaryExpression(
                    ConvertUnaryOperator(unaryExpr.UnaryExpressionType),
                    ConvertExpression(unaryExpr.Expression)),

            ParenthesisExpression parenExpr =>
                ConvertExpression(parenExpr.Expression),

            FunctionCall funcCall =>
                ConvertFunctionCall(funcCall),

            SearchedCaseExpression caseExpr =>
                ConvertSearchedCase(caseExpr),

            IIfCall iifCall =>
                new SqlIifExpression(
                    ConvertBooleanExpression(iifCall.Predicate),
                    ConvertExpression(iifCall.ThenExpression),
                    ConvertExpression(iifCall.ElseExpression)),

            CastCall castCall =>
                new SqlCastExpression(
                    ConvertExpression(castCall.Parameter),
                    FormatDataType(castCall.DataType)),

            ConvertCall convertCall =>
                ConvertConvertCall(convertCall),

            VariableReference varRef =>
                new SqlVariableExpression(varRef.Name.StartsWith("@") ? varRef.Name : "@" + varRef.Name),

            GlobalVariableExpression globalVar =>
                new SqlVariableExpression(globalVar.Name),

            ScalarSubquery subquery =>
                ConvertScalarSubquery(subquery),

            _ => throw new QueryParseException(
                $"Unsupported ScriptDom expression type: {expr.GetType().Name}")
        };
    }

    private static ISqlExpression ConvertFunctionCall(FunctionCall funcCall)
    {
        if (funcCall.OverClause != null)
        {
            return ConvertWindowFunction(funcCall);
        }

        var funcName = funcCall.FunctionName.Value.ToUpperInvariant();
        if (funcName is "COUNT" or "SUM" or "AVG" or "MIN" or "MAX" or "STDEV" or "STDEVP" or "VAR" or "VARP")
        {
            return ConvertAggregateExpression(funcCall);
        }

        var args = new List<ISqlExpression>();
        if (funcCall.Parameters != null)
        {
            foreach (var param in funcCall.Parameters)
            {
                args.Add(ConvertExpression(param));
            }
        }

        return new SqlFunctionExpression(funcCall.FunctionName.Value, args);
    }

    private static SqlAggregateExpression ConvertAggregateExpression(FunctionCall funcCall)
    {
        var funcName = funcCall.FunctionName.Value.ToUpperInvariant();
        var aggFunc = funcName switch
        {
            "COUNT" => SqlAggregateFunction.Count,
            "SUM" => SqlAggregateFunction.Sum,
            "AVG" => SqlAggregateFunction.Avg,
            "MIN" => SqlAggregateFunction.Min,
            "MAX" => SqlAggregateFunction.Max,
            "STDEV" or "STDEVP" => SqlAggregateFunction.Stdev,
            "VAR" or "VARP" => SqlAggregateFunction.Var,
            _ => throw new QueryParseException($"Unknown aggregate function: {funcName}")
        };

        var isDistinct = funcCall.UniqueRowFilter == UniqueRowFilter.Distinct;
        ISqlExpression? operand = null;

        if (funcCall.Parameters != null && funcCall.Parameters.Count > 0)
        {
            var firstParam = funcCall.Parameters[0];
            if (firstParam is ColumnReferenceExpression { ColumnType: ColumnType.Wildcard })
            {
                operand = null; // COUNT(*)
            }
            else
            {
                operand = ConvertExpression(firstParam);
            }
        }

        return new SqlAggregateExpression(aggFunc, operand, isDistinct);
    }

    private static SqlWindowExpression ConvertWindowFunction(FunctionCall funcCall)
    {
        var funcName = funcCall.FunctionName.Value.ToUpperInvariant();
        ISqlExpression? operand = null;
        var isCountStar = false;

        if (funcCall.Parameters != null && funcCall.Parameters.Count > 0)
        {
            var firstParam = funcCall.Parameters[0];
            if (firstParam is ColumnReferenceExpression { ColumnType: ColumnType.Wildcard })
            {
                isCountStar = true;
            }
            else
            {
                operand = ConvertExpression(firstParam);
            }
        }

        List<ISqlExpression>? partitionBy = null;
        if (funcCall.OverClause.Partitions != null && funcCall.OverClause.Partitions.Count > 0)
        {
            partitionBy = new List<ISqlExpression>();
            foreach (var partition in funcCall.OverClause.Partitions)
            {
                partitionBy.Add(ConvertExpression(partition));
            }
        }

        List<SqlOrderByItem>? orderByItems = null;
        if (funcCall.OverClause.OrderByClause?.OrderByElements != null)
        {
            orderByItems = new List<SqlOrderByItem>();
            foreach (var orderElem in funcCall.OverClause.OrderByClause.OrderByElements)
            {
                if (orderElem.Expression is ColumnReferenceExpression orderCol)
                {
                    var direction = orderElem.SortOrder == SortOrder.Descending
                        ? SqlSortDirection.Descending
                        : SqlSortDirection.Ascending;
                    orderByItems.Add(new SqlOrderByItem(ConvertColumnRef(orderCol), direction));
                }
            }
        }

        return new SqlWindowExpression(funcName, operand, partitionBy, orderByItems, isCountStar);
    }

    private static SqlCaseExpression ConvertSearchedCase(SearchedCaseExpression caseExpr)
    {
        var whenClauses = new List<SqlWhenClause>();
        foreach (var when in caseExpr.WhenClauses)
        {
            if (when is SearchedWhenClause searched)
            {
                whenClauses.Add(new SqlWhenClause(
                    ConvertBooleanExpression(searched.WhenExpression),
                    ConvertExpression(searched.ThenExpression)));
            }
        }

        ISqlExpression? elseExpr = null;
        if (caseExpr.ElseExpression != null)
        {
            elseExpr = ConvertExpression(caseExpr.ElseExpression);
        }

        return new SqlCaseExpression(whenClauses, elseExpr);
    }

    private static SqlCastExpression ConvertConvertCall(ConvertCall convertCall)
    {
        int? style = null;
        if (convertCall.Style != null)
        {
            if (convertCall.Style is IntegerLiteral intStyle &&
                int.TryParse(intStyle.Value, out var styleVal))
            {
                style = styleVal;
            }
        }

        return new SqlCastExpression(
            ConvertExpression(convertCall.Parameter),
            FormatDataType(convertCall.DataType),
            style);
    }

    private static SqlSubqueryExpression ConvertScalarSubquery(ScalarSubquery subquery)
    {
        if (subquery.QueryExpression is QuerySpecification querySpec)
        {
            return new SqlSubqueryExpression(ConvertSelectStatement(querySpec));
        }
        throw new QueryParseException("Unsupported scalar subquery type.");
    }

    // ── Boolean expression conversion ────────────────────────────────

    private static ISqlCondition ConvertBooleanExpression(BooleanExpression boolExpr)
    {
        if (boolExpr is null)
            throw new ArgumentNullException(nameof(boolExpr));

        return boolExpr switch
        {
            BooleanComparisonExpression compExpr =>
                ConvertComparison(compExpr),

            BooleanBinaryExpression binBool =>
                ConvertLogicalCondition(binBool),

            BooleanParenthesisExpression parenBool =>
                ConvertBooleanExpression(parenBool.Expression),

            BooleanNotExpression notExpr =>
                ConvertNotExpression(notExpr),

            BooleanIsNullExpression isNullExpr =>
                ConvertIsNullExpression(isNullExpr),

            LikePredicate likePred =>
                ConvertLikePredicate(likePred),

            InPredicate inPred =>
                ConvertInPredicate(inPred),

            ExistsPredicate existsPred =>
                ConvertExistsPredicate(existsPred),

            BooleanTernaryExpression ternary =>
                ConvertBetweenExpression(ternary),

            _ => throw new QueryParseException(
                $"Unsupported ScriptDom boolean expression type: {boolExpr.GetType().Name}")
        };
    }

    private static ISqlCondition ConvertComparison(BooleanComparisonExpression compExpr)
    {
        var op = ConvertComparisonOperator(compExpr.ComparisonType);

        if (compExpr.FirstExpression is ColumnReferenceExpression leftCol
            && IsLiteralExpression(compExpr.SecondExpression))
        {
            return new SqlComparisonCondition(
                ConvertColumnRef(leftCol),
                op,
                ExtractLiteral(compExpr.SecondExpression));
        }

        if (IsLiteralExpression(compExpr.FirstExpression)
            && compExpr.SecondExpression is ColumnReferenceExpression rightCol)
        {
            return new SqlComparisonCondition(
                ConvertColumnRef(rightCol),
                ReverseOperator(op),
                ExtractLiteral(compExpr.FirstExpression));
        }

        return new SqlExpressionCondition(
            ConvertExpression(compExpr.FirstExpression),
            op,
            ConvertExpression(compExpr.SecondExpression));
    }

    private static SqlLogicalCondition ConvertLogicalCondition(BooleanBinaryExpression binBool)
    {
        var op = binBool.BinaryExpressionType == BooleanBinaryExpressionType.And
            ? SqlLogicalOperator.And
            : SqlLogicalOperator.Or;

        var conditions = new List<ISqlCondition>();
        FlattenLogical(binBool, op, conditions);

        return new SqlLogicalCondition(op, conditions);
    }

    private static void FlattenLogical(
        BooleanExpression expr, SqlLogicalOperator targetOp, List<ISqlCondition> conditions)
    {
        if (expr is BooleanBinaryExpression binBool)
        {
            var exprOp = binBool.BinaryExpressionType == BooleanBinaryExpressionType.And
                ? SqlLogicalOperator.And
                : SqlLogicalOperator.Or;

            if (exprOp == targetOp)
            {
                FlattenLogical(binBool.FirstExpression, targetOp, conditions);
                FlattenLogical(binBool.SecondExpression, targetOp, conditions);
                return;
            }
        }

        conditions.Add(ConvertBooleanExpression(expr));
    }

    private static ISqlCondition ConvertNotExpression(BooleanNotExpression notExpr)
    {
        var inner = notExpr.Expression;

        if (inner is BooleanIsNullExpression isNull)
        {
            return new SqlNullCondition(
                ConvertColumnRef((ColumnReferenceExpression)isNull.Expression),
                isNegated: true);
        }

        if (inner is LikePredicate like)
        {
            return ConvertLikePredicate(like, forceNegate: true);
        }

        if (inner is InPredicate inPred)
        {
            return ConvertInPredicate(inPred, forceNegate: true);
        }

        if (inner is ExistsPredicate existsPred)
        {
            return ConvertExistsPredicate(existsPred, forceNegate: true);
        }

        var innerCondition = ConvertBooleanExpression(inner);
        return innerCondition;
    }

    private static SqlNullCondition ConvertIsNullExpression(BooleanIsNullExpression isNullExpr)
    {
        if (isNullExpr.Expression is ColumnReferenceExpression colRef)
        {
            return new SqlNullCondition(ConvertColumnRef(colRef), isNullExpr.IsNot);
        }

        throw new QueryParseException(
            "IS NULL on complex expressions is not yet supported. Use a column reference.");
    }

    private static SqlLikeCondition ConvertLikePredicate(LikePredicate likePred, bool forceNegate = false)
    {
        if (likePred.FirstExpression is not ColumnReferenceExpression colRef)
        {
            throw new QueryParseException(
                "LIKE predicate must have a column reference on the left side.");
        }

        var pattern = ExtractStringValue(likePred.SecondExpression);
        var isNegated = likePred.NotDefined || forceNegate;

        return new SqlLikeCondition(ConvertColumnRef(colRef), pattern, isNegated);
    }

    private static ISqlCondition ConvertInPredicate(InPredicate inPred, bool forceNegate = false)
    {
        if (inPred.Expression is not ColumnReferenceExpression colRef)
        {
            throw new QueryParseException(
                "IN predicate must have a column reference on the left side.");
        }

        var isNegated = inPred.NotDefined || forceNegate;

        if (inPred.Subquery != null)
        {
            if (inPred.Subquery.QueryExpression is QuerySpecification querySpec)
            {
                var subSelect = ConvertSelectStatement(querySpec);
                return new SqlInSubqueryCondition(
                    ConvertColumnRef(colRef), subSelect, isNegated);
            }
            throw new QueryParseException("Unsupported IN subquery type.");
        }

        var values = new List<SqlLiteral>();
        foreach (var val in inPred.Values)
        {
            values.Add(ExtractLiteral(val));
        }

        return new SqlInCondition(ConvertColumnRef(colRef), values, isNegated);
    }

    private static SqlExistsCondition ConvertExistsPredicate(
        ExistsPredicate existsPred, bool forceNegate = false)
    {
        if (existsPred.Subquery?.QueryExpression is QuerySpecification querySpec)
        {
            var subSelect = ConvertSelectStatement(querySpec);
            return new SqlExistsCondition(subSelect, forceNegate);
        }

        throw new QueryParseException("EXISTS predicate must contain a SELECT subquery.");
    }

    private static ISqlCondition ConvertBetweenExpression(BooleanTernaryExpression ternary)
    {
        if (ternary.TernaryExpressionType == BooleanTernaryExpressionType.Between)
        {
            var col = ConvertExpression(ternary.FirstExpression);
            var low = ConvertExpression(ternary.SecondExpression);
            var high = ConvertExpression(ternary.ThirdExpression);

            var geCond = new SqlExpressionCondition(col, SqlComparisonOperator.GreaterThanOrEqual, low);
            var leCond = new SqlExpressionCondition(col, SqlComparisonOperator.LessThanOrEqual, high);

            return SqlLogicalCondition.And(geCond, leCond);
        }

        if (ternary.TernaryExpressionType == BooleanTernaryExpressionType.NotBetween)
        {
            var col = ConvertExpression(ternary.FirstExpression);
            var low = ConvertExpression(ternary.SecondExpression);
            var high = ConvertExpression(ternary.ThirdExpression);

            var ltCond = new SqlExpressionCondition(col, SqlComparisonOperator.LessThan, low);
            var gtCond = new SqlExpressionCondition(col, SqlComparisonOperator.GreaterThan, high);

            return SqlLogicalCondition.Or(ltCond, gtCond);
        }

        throw new QueryParseException(
            $"Unsupported ternary expression type: {ternary.TernaryExpressionType}");
    }

    // ── SELECT element conversion ────────────────────────────────────

    private static ISqlSelectColumn ConvertSelectElement(SelectElement element)
    {
        return element switch
        {
            SelectStarExpression star =>
                ConvertSelectStar(star),

            SelectScalarExpression scalar =>
                ConvertSelectScalar(scalar),

            _ => throw new QueryParseException(
                $"Unsupported SELECT element type: {element.GetType().Name}")
        };
    }

    private static SqlColumnRef ConvertSelectStar(SelectStarExpression star)
    {
        string? tableName = null;
        if (star.Qualifier != null && star.Qualifier.Identifiers.Count > 0)
        {
            tableName = star.Qualifier.Identifiers[star.Qualifier.Identifiers.Count - 1].Value;
        }
        return SqlColumnRef.Wildcard(tableName);
    }

    private static ISqlSelectColumn ConvertSelectScalar(SelectScalarExpression scalar)
    {
        var alias = scalar.ColumnName?.Value;

        if (scalar.Expression is ColumnReferenceExpression colRef
            && colRef.ColumnType != ColumnType.Wildcard)
        {
            var converted = ConvertColumnRef(colRef);
            return new SqlColumnRef(converted.TableName, converted.ColumnName, alias, false);
        }

        if (scalar.Expression is FunctionCall funcCall && funcCall.OverClause == null)
        {
            var funcName = funcCall.FunctionName.Value.ToUpperInvariant();
            if (funcName is "COUNT" or "SUM" or "AVG" or "MIN" or "MAX")
            {
                return ConvertAggregateColumn(funcCall, alias);
            }
        }

        var expression = ConvertExpression(scalar.Expression);
        return new SqlComputedColumn(expression, alias);
    }

    private static SqlAggregateColumn ConvertAggregateColumn(FunctionCall funcCall, string? alias)
    {
        var funcName = funcCall.FunctionName.Value.ToUpperInvariant();
        var aggFunc = funcName switch
        {
            "COUNT" => SqlAggregateFunction.Count,
            "SUM" => SqlAggregateFunction.Sum,
            "AVG" => SqlAggregateFunction.Avg,
            "MIN" => SqlAggregateFunction.Min,
            "MAX" => SqlAggregateFunction.Max,
            "STDEV" or "STDEVP" => SqlAggregateFunction.Stdev,
            "VAR" or "VARP" => SqlAggregateFunction.Var,
            _ => throw new QueryParseException($"Unknown aggregate function: {funcName}")
        };

        var isDistinct = funcCall.UniqueRowFilter == UniqueRowFilter.Distinct;
        SqlColumnRef? column = null;

        if (funcCall.Parameters != null && funcCall.Parameters.Count > 0)
        {
            var firstParam = funcCall.Parameters[0];
            if (firstParam is ColumnReferenceExpression { ColumnType: ColumnType.Wildcard })
            {
                column = null; // COUNT(*)
            }
            else if (firstParam is ColumnReferenceExpression paramCol)
            {
                column = ConvertColumnRef(paramCol);
            }
            else
            {
                column = null;
            }
        }

        return new SqlAggregateColumn(aggFunc, column, isDistinct, alias);
    }

    // ── Statement conversion ─────────────────────────────────────────

    private static SqlSelectStatement ConvertSelectStatement(QuerySpecification querySpec)
    {
        var columns = new List<ISqlSelectColumn>();
        foreach (var elem in querySpec.SelectElements)
        {
            columns.Add(ConvertSelectElement(elem));
        }

        var from = ConvertFromClause(querySpec.FromClause);
        var joins = ConvertJoins(querySpec.FromClause);

        ISqlCondition? where = null;
        if (querySpec.WhereClause?.SearchCondition != null)
        {
            where = ConvertBooleanExpression(querySpec.WhereClause.SearchCondition);
        }

        var selectOrderBy = new List<SqlOrderByItem>();

        int? top = null;
        if (querySpec.TopRowFilter != null && querySpec.TopRowFilter.Expression is IntegerLiteral topLit)
        {
            if (int.TryParse(topLit.Value, out var topVal))
            {
                top = topVal;
            }
        }

        var distinct = querySpec.UniqueRowFilter == UniqueRowFilter.Distinct;

        var groupBy = new List<SqlColumnRef>();
        var groupByExpressions = new List<ISqlExpression>();
        if (querySpec.GroupByClause?.GroupingSpecifications != null)
        {
            foreach (var groupSpec in querySpec.GroupByClause.GroupingSpecifications)
            {
                if (groupSpec is ExpressionGroupingSpecification exprGroup)
                {
                    if (exprGroup.Expression is ColumnReferenceExpression groupCol)
                    {
                        groupBy.Add(ConvertColumnRef(groupCol));
                    }
                    else
                    {
                        groupByExpressions.Add(ConvertExpression(exprGroup.Expression));
                    }
                }
            }
        }

        ISqlCondition? having = null;
        if (querySpec.HavingClause?.SearchCondition != null)
        {
            having = ConvertBooleanExpression(querySpec.HavingClause.SearchCondition);
        }

        return new SqlSelectStatement(
            columns, from, joins, where, selectOrderBy, top, distinct, groupBy, having,
            sourcePosition: 0, groupByExpressions: groupByExpressions);
    }

    // ── FROM / JOIN conversion ───────────────────────────────────────

    private static SqlTableRef ConvertFromClause(FromClause? fromClause)
    {
        if (fromClause == null || fromClause.TableReferences.Count == 0)
        {
            throw new QueryParseException("FROM clause is required.");
        }

        return ExtractPrimaryTable(fromClause.TableReferences[0]);
    }

    private static SqlTableRef ExtractPrimaryTable(TableReference tableRef)
    {
        return tableRef switch
        {
            NamedTableReference named => ConvertNamedTable(named),
            QualifiedJoin join => ExtractPrimaryTable(join.FirstTableReference),
            _ => throw new QueryParseException(
                $"Unsupported table reference type: {tableRef.GetType().Name}")
        };
    }

    private static SqlTableRef ConvertNamedTable(NamedTableReference named)
    {
        var tableName = GetMultiPartName(named.SchemaObject);
        var alias = named.Alias?.Value;
        return new SqlTableRef(tableName, alias);
    }

    private static List<SqlJoin> ConvertJoins(FromClause? fromClause)
    {
        var joins = new List<SqlJoin>();
        if (fromClause == null) return joins;

        foreach (var tableRef in fromClause.TableReferences)
        {
            CollectJoins(tableRef, joins);
        }

        return joins;
    }

    private static void CollectJoins(TableReference tableRef, List<SqlJoin> joins)
    {
        if (tableRef is QualifiedJoin qualifiedJoin)
        {
            CollectJoins(qualifiedJoin.FirstTableReference, joins);

            var joinType = qualifiedJoin.QualifiedJoinType switch
            {
                QualifiedJoinType.Inner => SqlJoinType.Inner,
                QualifiedJoinType.LeftOuter => SqlJoinType.Left,
                QualifiedJoinType.RightOuter => SqlJoinType.Right,
                QualifiedJoinType.FullOuter => SqlJoinType.Left,
                _ => SqlJoinType.Inner
            };

            SqlTableRef joinedTable;
            if (qualifiedJoin.SecondTableReference is NamedTableReference joinNamed)
            {
                joinedTable = ConvertNamedTable(joinNamed);
            }
            else
            {
                CollectJoins(qualifiedJoin.SecondTableReference, joins);
                return;
            }

            if (qualifiedJoin.SearchCondition is BooleanComparisonExpression onCondition
                && onCondition.FirstExpression is ColumnReferenceExpression leftOnCol
                && onCondition.SecondExpression is ColumnReferenceExpression rightOnCol)
            {
                joins.Add(new SqlJoin(
                    joinType,
                    joinedTable,
                    ConvertColumnRef(leftOnCol),
                    ConvertColumnRef(rightOnCol)));
            }
            else
            {
                var colRefs = new List<ColumnReferenceExpression>();
                ExtractColumnRefsFromBoolExpr(qualifiedJoin.SearchCondition, colRefs);

                if (colRefs.Count >= 2)
                {
                    joins.Add(new SqlJoin(
                        joinType,
                        joinedTable,
                        ConvertColumnRef(colRefs[0]),
                        ConvertColumnRef(colRefs[1])));
                }
                else
                {
                    throw new QueryParseException(
                        "JOIN ON clause must reference at least two columns.");
                }
            }
        }
    }

    private static void ExtractColumnRefsFromBoolExpr(
        BooleanExpression boolExpr, List<ColumnReferenceExpression> refs)
    {
        switch (boolExpr)
        {
            case BooleanComparisonExpression comp:
                if (comp.FirstExpression is ColumnReferenceExpression left) refs.Add(left);
                if (comp.SecondExpression is ColumnReferenceExpression right) refs.Add(right);
                break;
            case BooleanBinaryExpression bin:
                ExtractColumnRefsFromBoolExpr(bin.FirstExpression, refs);
                ExtractColumnRefsFromBoolExpr(bin.SecondExpression, refs);
                break;
            case BooleanParenthesisExpression paren:
                ExtractColumnRefsFromBoolExpr(paren.Expression, refs);
                break;
        }
    }

    // ── Column reference conversion ──────────────────────────────────

    private static SqlColumnRef ConvertColumnRef(ColumnReferenceExpression colRef)
    {
        if (colRef.ColumnType == ColumnType.Wildcard)
        {
            return SqlColumnRef.Wildcard();
        }

        var identifiers = colRef.MultiPartIdentifier?.Identifiers;
        if (identifiers == null || identifiers.Count == 0)
        {
            return SqlColumnRef.Simple("*");
        }

        if (identifiers.Count == 1)
        {
            return SqlColumnRef.Simple(identifiers[0].Value);
        }

        var tableName = identifiers[identifiers.Count - 2].Value;
        var columnName = identifiers[identifiers.Count - 1].Value;
        return SqlColumnRef.Qualified(tableName, columnName);
    }

    private static string GetScriptDomColumnName(ColumnReferenceExpression colRef)
    {
        var ids = colRef.MultiPartIdentifier?.Identifiers;
        if (ids == null || ids.Count == 0)
            return "*";
        return ids[ids.Count - 1].Value;
    }

    // ── UNION flattening ─────────────────────────────────────────────

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

    // ── Literal & type utilities ─────────────────────────────────────

    private static bool IsLiteralExpression(ScalarExpression expr)
    {
        return expr is IntegerLiteral or NumericLiteral or RealLiteral or MoneyLiteral
            or StringLiteral or NullLiteral;
    }

    private static SqlLiteral ExtractLiteral(ScalarExpression expr)
    {
        return expr switch
        {
            IntegerLiteral intLit => SqlLiteral.Number(intLit.Value),
            NumericLiteral numLit => SqlLiteral.Number(numLit.Value),
            RealLiteral realLit => SqlLiteral.Number(realLit.Value),
            MoneyLiteral moneyLit => SqlLiteral.Number(moneyLit.Value),
            StringLiteral strLit => SqlLiteral.String(strLit.Value),
            NullLiteral => SqlLiteral.Null(),
            _ => SqlLiteral.String(expr.ToString() ?? "")
        };
    }

    private static string ExtractStringValue(ScalarExpression expr)
    {
        return expr switch
        {
            StringLiteral strLit => strLit.Value,
            IntegerLiteral intLit => intLit.Value,
            _ => expr.ToString() ?? ""
        };
    }

    private static string FormatDataType(DataTypeReference dataType)
    {
        if (dataType is SqlDataTypeReference sqlType)
        {
            var name = sqlType.SqlDataTypeOption.ToString().ToLowerInvariant();
            if (sqlType.Parameters.Count > 0)
            {
                var parms = string.Join(", ", sqlType.Parameters.Select(p => p.Value));
                return $"{name}({parms})";
            }
            return name;
        }

        if (dataType is XmlDataTypeReference)
            return "xml";

        if (dataType.Name?.Identifiers != null && dataType.Name.Identifiers.Count > 0)
        {
            return string.Join(".", dataType.Name.Identifiers.Select(i => i.Value));
        }

        return "varchar";
    }

    private static string GetMultiPartName(SchemaObjectName schemaObject)
    {
        var parts = new List<string>();
        if (schemaObject.SchemaIdentifier != null)
        {
            parts.Add(schemaObject.SchemaIdentifier.Value);
        }
        if (schemaObject.BaseIdentifier != null)
        {
            parts.Add(schemaObject.BaseIdentifier.Value);
        }

        return parts.Count > 0 ? string.Join(".", parts) : "unknown";
    }

    // ── Operator conversion ──────────────────────────────────────────

    private static SqlComparisonOperator ConvertComparisonOperator(BooleanComparisonType type)
    {
        return type switch
        {
            BooleanComparisonType.Equals => SqlComparisonOperator.Equal,
            BooleanComparisonType.NotEqualToBrackets => SqlComparisonOperator.NotEqual,
            BooleanComparisonType.NotEqualToExclamation => SqlComparisonOperator.NotEqual,
            BooleanComparisonType.LessThan => SqlComparisonOperator.LessThan,
            BooleanComparisonType.GreaterThan => SqlComparisonOperator.GreaterThan,
            BooleanComparisonType.LessThanOrEqualTo => SqlComparisonOperator.LessThanOrEqual,
            BooleanComparisonType.GreaterThanOrEqualTo => SqlComparisonOperator.GreaterThanOrEqual,
            _ => SqlComparisonOperator.Equal
        };
    }

    private static SqlComparisonOperator ReverseOperator(SqlComparisonOperator op)
    {
        return op switch
        {
            SqlComparisonOperator.LessThan => SqlComparisonOperator.GreaterThan,
            SqlComparisonOperator.GreaterThan => SqlComparisonOperator.LessThan,
            SqlComparisonOperator.LessThanOrEqual => SqlComparisonOperator.GreaterThanOrEqual,
            SqlComparisonOperator.GreaterThanOrEqual => SqlComparisonOperator.LessThanOrEqual,
            _ => op
        };
    }

    private static SqlBinaryOperator ConvertBinaryOperator(BinaryExpressionType type)
    {
        return type switch
        {
            BinaryExpressionType.Add => SqlBinaryOperator.Add,
            BinaryExpressionType.Subtract => SqlBinaryOperator.Subtract,
            BinaryExpressionType.Multiply => SqlBinaryOperator.Multiply,
            BinaryExpressionType.Divide => SqlBinaryOperator.Divide,
            BinaryExpressionType.Modulo => SqlBinaryOperator.Modulo,
            _ => SqlBinaryOperator.Add
        };
    }

    private static SqlUnaryOperator ConvertUnaryOperator(UnaryExpressionType type)
    {
        return type switch
        {
            UnaryExpressionType.Negative => SqlUnaryOperator.Negate,
            UnaryExpressionType.Positive => SqlUnaryOperator.Negate,
            _ => SqlUnaryOperator.Negate
        };
    }
}
