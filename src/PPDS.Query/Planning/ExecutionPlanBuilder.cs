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

/// <summary>
/// Builds an execution plan from a ScriptDom <see cref="TSqlFragment"/> AST.
/// Walks the ScriptDom AST directly and constructs an <see cref="IQueryPlanNode"/> tree
/// that the existing plan executor expects.
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

        // Extract entity name and TOP directly from ScriptDom AST
        var entityName = ExtractEntityNameFromQuerySpec(querySpec)
            ?? throw new QueryParseException("Cannot determine entity name from SELECT statement.");
        var top = ExtractTopFromQuerySpec(querySpec);

        // Phase 6: Metadata virtual table routing
        if (entityName.StartsWith("metadata.", StringComparison.OrdinalIgnoreCase))
        {
            return PlanMetadataQuery(querySpec, entityName);
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
                return PlanTds(entityName, top, options);
            }
        }

        // Generate FetchXML using the injected service
        var transpileResult = _fetchXmlGenerator.Generate(selectStmt);

        // Phase 4: Aggregate partitioning (now fully ScriptDom-based)
        if (HasAggregatesInQuerySpec(querySpec) && options.EstimatedRecordCount.HasValue)
        {
            if (ShouldPartitionAggregate(querySpec, options))
            {
                return PlanAggregateWithPartitioning(querySpec, options, transpileResult, entityName);
            }
        }

        // When caller provides a page number or paging cookie, use single-page mode
        var isCallerPaged = options.PageNumber.HasValue || options.PagingCookie != null;

        var scanNode = new FetchXmlScanNode(
            transpileResult.FetchXml,
            entityName,
            autoPage: !isCallerPaged,
            maxRows: options.MaxRows ?? top,
            initialPageNumber: options.PageNumber,
            initialPagingCookie: options.PagingCookie,
            includeCount: options.IncludeCount);

        // Start with scan as root; apply client-side operators on top.
        IQueryPlanNode rootNode = scanNode;

        // Wrap with PrefetchScanNode for page-ahead buffering
        if (options.EnablePrefetch && !HasAggregatesInQuerySpec(querySpec) && !isCallerPaged)
        {
            rootNode = new PrefetchScanNode(rootNode, options.PrefetchBufferSize);
        }

        // Expression conditions in WHERE — compiled directly from ScriptDom
        {
            var clientFilter = ExtractClientSideWhereFilter(querySpec.WhereClause?.SearchCondition);
            if (clientFilter != null)
            {
                var predicate = _expressionCompiler.CompilePredicate(clientFilter);
                var description = clientFilter.ToString() ?? "WHERE (client)";
                rootNode = new ClientFilterNode(rootNode, predicate, description);
            }
        }

        // HAVING clause: compile directly from ScriptDom
        if (querySpec.HavingClause?.SearchCondition != null)
        {
            var predicate = _expressionCompiler.CompilePredicate(querySpec.HavingClause.SearchCondition);
            var description = querySpec.HavingClause.SearchCondition.ToString() ?? "HAVING";
            rootNode = new ClientFilterNode(rootNode, predicate, description);
        }

        // Window functions (compiled directly from ScriptDom)
        if (HasWindowFunctionsInQuerySpec(querySpec))
        {
            rootNode = BuildWindowNodeFromScriptDom(rootNode, querySpec);
        }

        // Computed columns (CASE/IIF expressions) — compiled directly from ScriptDom
        if (HasComputedColumnsInQuerySpec(querySpec))
        {
            rootNode = BuildProjectNodeFromScriptDom(rootNode, querySpec);
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
            // INSERT SELECT: render the source SELECT as SQL, parse, and plan via ScriptDom path
            var generator = new Sql160ScriptGenerator();
            generator.GenerateScript(selectSource, out var selectSql);
            var sourceResult = ParseAndPlanSyntheticSelect(selectSql, options);

            var sourceColumns = ExtractSelectColumnNamesFromQuerySpec(selectSource);
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
    /// Builds a synthetic SQL SELECT string from the UPDATE's target, FROM/JOIN, and WHERE clauses,
    /// parses it via QueryParser, and plans through the normal ScriptDom SELECT path.
    /// </summary>
    private QueryPlanResult PlanUpdate(UpdateStatement update, QueryPlanOptions options)
    {
        // Extract entity name directly from ScriptDom
        if (update.UpdateSpecification.Target is not NamedTableReference targetNamed)
            throw new QueryParseException("UPDATE target must be a named table.");
        var entityName = GetMultiPartName(targetNamed.SchemaObject);

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
                var refCols = ExtractColumnNamesFromScriptDom(assignment.NewValue);
                referencedColumnNames.AddRange(refCols);
            }
        }

        // Build a synthetic SELECT SQL string to find matching records.
        // SELECT entityid, [referenced columns] FROM entity [JOINs] [WHERE ...]
        var selectCols = new List<string> { entityName + "id" };
        foreach (var colName in referencedColumnNames.Distinct())
        {
            if (!selectCols.Contains(colName, StringComparer.OrdinalIgnoreCase))
                selectCols.Add(colName);
        }

        var sb = new StringBuilder();
        sb.Append("SELECT ").Append(string.Join(", ", selectCols));

        // Render FROM clause — use the UPDATE's FROM clause if present (it may contain JOINs),
        // otherwise use the target table directly.
        if (update.UpdateSpecification.FromClause != null)
        {
            var generator = new Sql160ScriptGenerator();
            generator.GenerateScript(update.UpdateSpecification.FromClause, out var fromSql);
            sb.Append(' ').Append(fromSql);
        }
        else
        {
            sb.Append(" FROM ").Append(entityName);
        }

        // Render WHERE clause from ScriptDom
        if (update.UpdateSpecification.WhereClause != null)
        {
            var generator = new Sql160ScriptGenerator();
            generator.GenerateScript(update.UpdateSpecification.WhereClause, out var whereSql);
            sb.Append(' ').Append(whereSql);
        }

        // Parse and plan through normal ScriptDom path
        var selectResult = ParseAndPlanSyntheticSelect(sb.ToString(), options);

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
    /// Builds a synthetic SQL SELECT string from the DELETE's target, FROM/JOIN, and WHERE clauses,
    /// parses it via QueryParser, and plans through the normal ScriptDom SELECT path.
    /// </summary>
    private QueryPlanResult PlanDelete(DeleteStatement delete, QueryPlanOptions options)
    {
        // Extract entity name directly from ScriptDom
        if (delete.DeleteSpecification.Target is not NamedTableReference targetNamed)
            throw new QueryParseException("DELETE target must be a named table.");
        var entityName = GetMultiPartName(targetNamed.SchemaObject);

        // Build a synthetic SELECT SQL string: SELECT entityid FROM entity [JOINs] [WHERE ...]
        var sb = new StringBuilder();
        sb.Append("SELECT ").Append(entityName).Append("id");

        // Render FROM clause — use the DELETE's FROM clause if present (it may contain JOINs),
        // otherwise use the target table directly.
        if (delete.DeleteSpecification.FromClause != null)
        {
            var generator = new Sql160ScriptGenerator();
            generator.GenerateScript(delete.DeleteSpecification.FromClause, out var fromSql);
            sb.Append(' ').Append(fromSql);
        }
        else
        {
            sb.Append(" FROM ").Append(entityName);
        }

        // Render WHERE clause from ScriptDom
        if (delete.DeleteSpecification.WhereClause != null)
        {
            var generator = new Sql160ScriptGenerator();
            generator.GenerateScript(delete.DeleteSpecification.WhereClause, out var whereSql);
            sb.Append(' ').Append(whereSql);
        }

        // Parse and plan through normal ScriptDom path
        var selectResult = ParseAndPlanSyntheticSelect(sb.ToString(), options);

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
            // Route directly through PlanSelect with a synthetic SelectStatement
            var syntheticSelect = new SelectStatement { QueryExpression = querySpec };
            return PlanSelect(syntheticSelect, querySpec, options);
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
            // Route directly through PlanSelect with a synthetic SelectStatement
            var syntheticSelect = new SelectStatement { QueryExpression = querySpec };
            var branchResult = PlanSelect(syntheticSelect, querySpec, options);
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
        var scriptNode = new ScriptExecutionNode(statements, this, _expressionCompiler, _sessionContext);

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
    private QueryPlanResult PlanMetadataQuery(
        QuerySpecification querySpec, string entityName)
    {
        var metadataTable = entityName;
        if (metadataTable.StartsWith("metadata.", StringComparison.OrdinalIgnoreCase))
            metadataTable = metadataTable["metadata.".Length..];

        List<string>? requestedColumns = null;
        if (querySpec.SelectElements.Count > 0
            && !(querySpec.SelectElements.Count == 1 && querySpec.SelectElements[0] is SelectStarExpression))
        {
            requestedColumns = new List<string>();
            foreach (var element in querySpec.SelectElements)
            {
                if (element is SelectScalarExpression { Expression: ColumnReferenceExpression colRef } scalar)
                {
                    var colName = colRef.MultiPartIdentifier?.Identifiers?.Count > 0
                        ? colRef.MultiPartIdentifier.Identifiers[colRef.MultiPartIdentifier.Identifiers.Count - 1].Value
                        : "unknown";
                    requestedColumns.Add(scalar.ColumnName?.Value ?? colName);
                }
            }
        }

        CompiledPredicate? filter = null;
        if (querySpec.WhereClause?.SearchCondition != null)
        {
            filter = _expressionCompiler.CompilePredicate(querySpec.WhereClause.SearchCondition);
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
        string entityName, int? top, QueryPlanOptions options)
    {
        var tdsNode = new TdsScanNode(
            options.OriginalSql!,
            entityName,
            options.TdsQueryExecutor!,
            maxRows: options.MaxRows ?? top);

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
    private QueryPlanResult? TryPlanTableValuedFunction(
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

        // ScriptDom parses OPENJSON as a dedicated OpenJsonTableReference node
        if (tableRef is OpenJsonTableReference openJsonRef)
        {
            return PlanOpenJson(openJsonRef, querySpec, options);
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
    /// Plans an OPENJSON table-valued function from a ScriptDom <see cref="OpenJsonTableReference"/>.
    /// </summary>
    private QueryPlanResult PlanOpenJson(
        OpenJsonTableReference openJsonRef,
        QuerySpecification querySpec,
        QueryPlanOptions options)
    {
        if (openJsonRef.Variable == null)
            throw new QueryParseException("OPENJSON requires at least one argument.");

        var jsonExpr = _expressionCompiler.CompileScalar(openJsonRef.Variable);
        string? path = null;

        if (openJsonRef.RowPattern is StringLiteral pathLit)
        {
            path = pathLit.Value;
        }

        IQueryPlanNode node = new OpenJsonNode(jsonExpr, path);

        // Apply WHERE if present
        if (querySpec.WhereClause?.SearchCondition != null)
        {
            var predicate = _expressionCompiler.CompilePredicate(querySpec.WhereClause.SearchCondition);
            var description = querySpec.WhereClause.SearchCondition.ToString() ?? "filter";
            node = new ClientFilterNode(node, predicate, description);
        }

        return new QueryPlanResult
        {
            RootNode = node,
            FetchXml = "-- OPENJSON",
            VirtualColumns = new Dictionary<string, VirtualColumnInfo>(),
            EntityLogicalName = "openjson"
        };
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

        // Source query: plan the USING clause via ScriptDom path
        IQueryPlanNode sourceNode;
        if (spec.TableReference is NamedTableReference sourceTable)
        {
            // USING sourceTable - build synthetic SELECT * FROM sourceTable and plan it
            var sourceEntity = sourceTable.SchemaObject?.BaseIdentifier?.Value ?? "unknown";
            var sql = $"SELECT * FROM {sourceEntity}";
            var sourceResult = ParseAndPlanSyntheticSelect(sql, options);
            sourceNode = sourceResult.RootNode;
        }
        else if (spec.TableReference is QueryDerivedTable derivedTable
            && derivedTable.QueryExpression is QuerySpecification querySpec)
        {
            // USING (SELECT ...) AS alias — render to SQL, parse, and plan via ScriptDom
            var generator = new Sql160ScriptGenerator();
            generator.GenerateScript(querySpec, out var selectSql);
            var sourceResult = ParseAndPlanSyntheticSelect(selectSql, options);
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
                    var setClauses = new List<CompiledSetClause>();
                    foreach (var setClause in updateAction.SetClauses)
                    {
                        if (setClause is AssignmentSetClause assignment)
                        {
                            var colName = assignment.Column?.MultiPartIdentifier?.Identifiers?.Count > 0
                                ? assignment.Column.MultiPartIdentifier.Identifiers[
                                    assignment.Column.MultiPartIdentifier.Identifiers.Count - 1].Value
                                : "unknown";
                            var compiled = _expressionCompiler.CompileScalar(assignment.NewValue);
                            setClauses.Add(new CompiledSetClause(colName, compiled));
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

                    var values = new List<CompiledScalarExpression>();
                    if (insertAction.Source is ValuesInsertSource valSource
                        && valSource.RowValues?.Count > 0)
                    {
                        foreach (var val in valSource.RowValues[0].ColumnValues)
                        {
                            values.Add(_expressionCompiler.CompileScalar(val));
                        }
                    }

                    whenNotMatched = MergeWhenNotMatched.Insert(columns, values);
                }
            }
        }

        if (whenMatched != null)
        {
            throw new NotSupportedException(
                "MERGE WHEN MATCHED (UPDATE/DELETE) is not yet supported. " +
                "Target row lookup from Dataverse is required. " +
                "Use WHEN NOT MATCHED (INSERT) only, or use separate UPDATE/DELETE statements.");
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

        // HAVING clause: compile directly from ScriptDom
        if (querySpec.HavingClause?.SearchCondition != null)
        {
            var predicate = _expressionCompiler.CompilePredicate(querySpec.HavingClause.SearchCondition);
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
    /// Extracts GROUP BY column names from a ScriptDom <see cref="QuerySpecification"/>.
    /// </summary>
    private static List<string> ExtractGroupByColumnNames(QuerySpecification querySpec)
    {
        var names = new List<string>();
        if (querySpec.GroupByClause?.GroupingSpecifications == null)
            return names;

        foreach (var groupSpec in querySpec.GroupByClause.GroupingSpecifications)
        {
            if (groupSpec is ExpressionGroupingSpecification exprGroup
                && exprGroup.Expression is ColumnReferenceExpression colRef)
            {
                names.Add(GetScriptDomColumnName(colRef));
            }
        }
        return names;
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
            "STDEV" or "STDEVP" => AggregateFunction.Stdev,
            "VAR" or "VARP" => AggregateFunction.Var,
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

    /// <summary>
    /// Extracts the portion of a ScriptDom WHERE clause that requires client-side evaluation.
    /// Returns the BooleanExpression that needs client-side filtering, or null if everything
    /// can be pushed to FetchXML. Expression-to-expression comparisons (e.g., WHERE col1 > col2,
    /// WHERE revenue * 0.1 > cost) cannot be represented in FetchXML and must be evaluated
    /// on the client.
    /// </summary>
    private static BooleanExpression? ExtractClientSideWhereFilter(BooleanExpression? where)
    {
        if (where is null) return null;

        // A comparison where both sides are non-literal expressions needs client evaluation
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
    /// Returns true if the BooleanExpression is a comparison where both sides are
    /// non-literal expressions (e.g., column vs column, expression vs expression).
    /// These can't be pushed to FetchXML.
    /// </summary>
    private static bool IsExpressionComparison(BooleanExpression expr)
    {
        if (expr is not BooleanComparisonExpression comp) return false;

        // If both sides are non-literal, it's an expression condition
        return !IsSimpleLiteral(comp.FirstExpression) && !IsSimpleLiteral(comp.SecondExpression);
    }

    /// <summary>
    /// Returns true if the scalar expression is a simple literal value (string, number, null)
    /// or a variable reference — values that FetchXML can handle in condition comparisons.
    /// </summary>
    private static bool IsSimpleLiteral(ScalarExpression expr)
    {
        return expr is IntegerLiteral
            or StringLiteral
            or NullLiteral
            or NumericLiteral
            or RealLiteral
            or MoneyLiteral
            or VariableReference
            or GlobalVariableExpression;
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
    /// </summary>
    private static string? ExtractEntityNameFromQuerySpec(QuerySpecification querySpec)
    {
        if (querySpec.FromClause?.TableReferences.Count > 0
            && querySpec.FromClause.TableReferences[0] is NamedTableReference named)
        {
            return GetMultiPartName(named.SchemaObject);
        }
        return null;
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
