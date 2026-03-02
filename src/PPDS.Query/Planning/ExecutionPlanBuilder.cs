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
public sealed partial class ExecutionPlanBuilder
{
    private static readonly Sql160ScriptGenerator s_scriptGenerator = new();

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

        // CTE self-reference: use bound node instead of FetchXML
        if (options.CteBindings?.TryGetValue(entityName, out var cteNode) == true)
        {
            return PlanCteSelfReference(querySpec, cteNode, entityName, options);
        }

        // Phase 6: Metadata virtual table routing
        if (entityName.StartsWith("metadata.", StringComparison.OrdinalIgnoreCase))
        {
            return PlanMetadataQuery(querySpec, entityName);
        }

        // Cross-environment references ([LABEL].dbo.entity) must ALWAYS route to client-side
        // planning — TDS and FetchXML only target the current environment's connection.
        if (ContainsCrossEnvironmentReference(querySpec.FromClause))
        {
            return PlanClientSideJoin(selectStmt, querySpec, options);
        }

        // TDS Endpoint routing — only when explicitly requested (Ctrl+T, profile setting,
        // or OPTION(USE_TDS) hint). TDS is read-only; DML is rejected by compatibility check.
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

        // CROSS JOIN, CROSS APPLY, and OUTER APPLY are represented as UnqualifiedJoin
        // in ScriptDom. FetchXML does not support these — route directly to client-side join.
        if (ContainsUnqualifiedJoin(querySpec.FromClause))
        {
            return PlanClientSideJoin(selectStmt, querySpec, options);
        }

        // Derived tables (subqueries in FROM) cannot be transpiled to FetchXML —
        // route to client-side planning which handles them via PlanTableReference.
        if (ContainsDerivedTable(querySpec.FromClause))
        {
            return PlanClientSideJoin(selectStmt, querySpec, options);
        }

        // EXISTS / NOT EXISTS — route to client-side semi-join
        if (querySpec.WhereClause?.SearchCondition != null
            && ContainsExistsPredicate(querySpec.WhereClause.SearchCondition))
        {
            return PlanExistsSubquery(selectStmt, querySpec, options);
        }

        // IN (subquery) / NOT IN (subquery) — route to client-side semi-join
        if (querySpec.WhereClause?.SearchCondition != null
            && ContainsInSubqueryPredicate(querySpec.WhereClause.SearchCondition))
        {
            return PlanInSubquery(selectStmt, querySpec, options);
        }

        // Generate FetchXML using the injected service.
        // If the query contains join types unsupported by FetchXML (RIGHT, FULL OUTER),
        // fall back to client-side join planning.
        TranspileResult transpileResult;
        try
        {
            transpileResult = _fetchXmlGenerator.Generate(selectStmt);
        }
        catch (NotSupportedException)
        {
            return PlanClientSideJoin(selectStmt, querySpec, options);
        }

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

        // HAVING clause: compile directly from ScriptDom.
        // Aggregate alias map lets COUNT(*)/SUM(x)/etc. resolve to output column aliases.
        // ORDER BY aggregate references are handled by FetchXML pushdown, not this map.
        if (querySpec.HavingClause?.SearchCondition != null)
        {
            var aggMap = BuildAggregateAliasMap(querySpec);
            var predicate = _expressionCompiler.CompilePredicate(querySpec.HavingClause.SearchCondition, aggMap);
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
}
