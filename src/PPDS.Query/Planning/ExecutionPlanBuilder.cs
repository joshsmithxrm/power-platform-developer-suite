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
using PPDS.Query.Parsing;

namespace PPDS.Query.Planning;

/// <summary>
/// Builds an execution plan from a ScriptDom <see cref="TSqlFragment"/> AST.
/// This is the v3 replacement for the legacy <see cref="QueryPlanner"/> that worked with
/// the custom PPDS SQL AST. It walks the ScriptDom AST, uses <see cref="ScriptDomAdapter"/>
/// to bridge to the legacy AST types where plan nodes require them, and produces the same
/// <see cref="IQueryPlanNode"/> tree that the existing executor expects.
/// </summary>
public sealed class ExecutionPlanBuilder
{
    private readonly IFetchXmlGeneratorService _fetchXmlGenerator;

    /// <summary>
    /// Initializes a new instance of the <see cref="ExecutionPlanBuilder"/> class.
    /// </summary>
    /// <param name="fetchXmlGenerator">
    /// Service that generates FetchXML from ScriptDom fragments. Injected to decouple
    /// plan construction from FetchXML transpilation (wired in a later phase).
    /// </param>
    public ExecutionPlanBuilder(IFetchXmlGeneratorService fetchXmlGenerator)
    {
        _fetchXmlGenerator = fetchXmlGenerator
            ?? throw new ArgumentNullException(nameof(fetchXmlGenerator));
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
    internal QueryPlanResult PlanStatement(TSqlStatement statement, QueryPlanOptions options)
    {
        return statement switch
        {
            SelectStatement selectStmt => PlanSelectStatement(selectStmt, options),
            InsertStatement insertStmt => PlanInsert(insertStmt, options),
            UpdateStatement updateStmt => PlanUpdate(updateStmt, options),
            DeleteStatement deleteStmt => PlanDelete(deleteStmt, options),
            IfStatement ifStmt => PlanScript(new[] { ifStmt }, options),
            BeginEndBlockStatement blockStmt => PlanScript(blockStmt.StatementList.Statements.Cast<TSqlStatement>().ToArray(), options),
            _ => throw new QueryParseException($"Unsupported statement type: {statement.GetType().Name}")
        };
    }

    // ═══════════════════════════════════════════════════════════════════
    //  SELECT planning
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Plans a ScriptDom SelectStatement, handling both simple SELECT and UNION queries.
    /// </summary>
    private QueryPlanResult PlanSelectStatement(SelectStatement selectStmt, QueryPlanOptions options)
    {
        // UNION / UNION ALL
        if (selectStmt.QueryExpression is BinaryQueryExpression binaryQuery)
        {
            return PlanUnion(selectStmt, binaryQuery, options);
        }

        // Regular SELECT
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
            rootNode = new ClientFilterNode(rootNode, clientWhereCondition);
        }

        // HAVING clause
        if (legacySelect.Having != null)
        {
            rootNode = new ClientFilterNode(rootNode, legacySelect.Having);
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
        var targetEntity = ScriptDomAdapter.GetInsertTargetEntity(insert);
        var columns = ScriptDomAdapter.GetInsertColumns(insert);

        IQueryPlanNode rootNode;

        // Check if this is INSERT ... SELECT
        var selectSource = ScriptDomAdapter.GetInsertSelectSource(insert);
        if (selectSource != null)
        {
            // INSERT SELECT: plan the source SELECT, wrap with DmlExecuteNode
            var sourceSelectStmt = ScriptDomAdapter.ConvertSelectStatement(selectSource);
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
            // INSERT VALUES
            var valueRows = ScriptDomAdapter.GetInsertValueRows(insert);
            var typedRows = valueRows
                .Select(r => (IReadOnlyList<ISqlExpression>)r.AsReadOnly())
                .ToList();

            rootNode = DmlExecuteNode.InsertValues(
                targetEntity,
                columns,
                typedRows,
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
        var targetTable = ScriptDomAdapter.GetUpdateTargetTable(update);
        var entityName = targetTable.TableName;
        var setClauses = ScriptDomAdapter.GetUpdateSetClauses(update);
        var where = ScriptDomAdapter.GetUpdateWhere(update);

        // Build a SELECT to find records matching the WHERE clause.
        // SELECT entityid FROM entity WHERE ...
        var idColumn = SqlColumnRef.Simple(entityName + "id");
        var selectColumns = new List<ISqlSelectColumn> { idColumn };

        // Also include any columns referenced in SET clause expressions
        foreach (var clause in setClauses)
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

        // Extract JOINs from the UPDATE's FROM clause
        var fromClause = ScriptDomAdapter.GetUpdateFromClause(update);
        var joins = fromClause != null
            ? ScriptDomAdapter.ConvertJoins(fromClause)
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
            setClauses,
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
        var targetTable = ScriptDomAdapter.GetDeleteTargetTable(delete);
        var entityName = targetTable.TableName;
        var where = ScriptDomAdapter.GetDeleteWhere(delete);

        // Build a SELECT to find record IDs matching the WHERE clause.
        var idColumn = SqlColumnRef.Simple(entityName + "id");

        // Extract JOINs from the DELETE's FROM clause
        var fromClause = ScriptDomAdapter.GetDeleteFromClause(delete);
        var joins = fromClause != null
            ? ScriptDomAdapter.ConvertJoins(fromClause)
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
    //  UNION planning
    // ═══════════════════════════════════════════════════════════════════

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
        ScriptDomAdapter.FlattenUnion(binaryQuery, querySpecs, isUnionAll);

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
            var legacySelect = ScriptDomAdapter.ConvertSelectStatement(querySpec);
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
    //  Script (IF/ELSE, block) planning
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Plans a multi-statement script (IF/ELSE blocks, DECLARE/SET, etc.).
    /// Wraps the statements in a ScriptExecutionNode. The ScriptExecutionNode still
    /// uses the legacy QueryPlanner internally for inner statement execution.
    /// </summary>
    private QueryPlanResult PlanScript(
        IReadOnlyList<TSqlStatement> statements, QueryPlanOptions options)
    {
        // Convert ScriptDom statements to legacy AST statements for ScriptExecutionNode
        var legacyStatements = new List<ISqlStatement>();
        foreach (var stmt in statements)
        {
            legacyStatements.Add(ConvertToLegacyStatement(stmt));
        }

        // ScriptExecutionNode uses the legacy QueryPlanner for inner statement execution.
        // TODO: In a future phase, create a v3 ScriptExecutionNode that uses ExecutionPlanBuilder directly.
        var legacyPlanner = new QueryPlanner();
        var scriptNode = new ScriptExecutionNode(legacyStatements, legacyPlanner);

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

        var scanNode = new MetadataScanNode(
            metadataTable,
            metadataExecutor: null,
            requestedColumns,
            statement.Where);

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
        var baseSelect = ScriptDomAdapter.ConvertSelectStatement(querySpec);

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
                    orderBy.Add(new SqlOrderByItem(ScriptDomAdapter.ConvertColumnRef(orderCol), direction));
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
    /// Converts a ScriptDom TSqlStatement to a legacy ISqlStatement (for script execution).
    /// </summary>
    private static ISqlStatement ConvertToLegacyStatement(TSqlStatement statement)
    {
        return statement switch
        {
            SelectStatement selectStmt when selectStmt.QueryExpression is QuerySpecification qs =>
                ConvertToLegacySelect(selectStmt, qs),

            InsertStatement insert => ConvertInsertToLegacy(insert),

            UpdateStatement update => ConvertUpdateToLegacy(update),

            DeleteStatement delete => ConvertDeleteToLegacy(delete),

            IfStatement ifStmt => ConvertIfToLegacy(ifStmt),

            BeginEndBlockStatement block => ConvertBlockToLegacy(block),

            DeclareVariableStatement declare => ConvertDeclareToLegacy(declare),

            SetVariableStatement setVar => ConvertSetVariableToLegacy(setVar),

            _ => throw new QueryParseException(
                $"Unsupported statement type for script conversion: {statement.GetType().Name}")
        };
    }

    /// <summary>
    /// Converts a ScriptDom InsertStatement to a legacy SqlInsertStatement.
    /// </summary>
    private static SqlInsertStatement ConvertInsertToLegacy(InsertStatement insert)
    {
        var targetEntity = ScriptDomAdapter.GetInsertTargetEntity(insert);
        var columns = ScriptDomAdapter.GetInsertColumns(insert);

        SqlSelectStatement? sourceQuery = null;
        List<IReadOnlyList<ISqlExpression>>? valueRows = null;

        var selectSource = ScriptDomAdapter.GetInsertSelectSource(insert);
        if (selectSource != null)
        {
            sourceQuery = ScriptDomAdapter.ConvertSelectStatement(selectSource);
        }
        else
        {
            var rawRows = ScriptDomAdapter.GetInsertValueRows(insert);
            valueRows = rawRows.Select(r => (IReadOnlyList<ISqlExpression>)r.AsReadOnly()).ToList();
        }

        return new SqlInsertStatement(targetEntity, columns, valueRows, sourceQuery, 0);
    }

    /// <summary>
    /// Converts a ScriptDom UpdateStatement to a legacy SqlUpdateStatement.
    /// </summary>
    private static SqlUpdateStatement ConvertUpdateToLegacy(UpdateStatement update)
    {
        var targetTable = ScriptDomAdapter.GetUpdateTargetTable(update);
        var setClauses = ScriptDomAdapter.GetUpdateSetClauses(update);
        var where = ScriptDomAdapter.GetUpdateWhere(update);

        var fromClause = ScriptDomAdapter.GetUpdateFromClause(update);
        SqlTableRef? fromTable = null;
        List<SqlJoin>? joins = null;
        if (fromClause != null)
        {
            fromTable = ScriptDomAdapter.ConvertFromClause(fromClause);
            joins = ScriptDomAdapter.ConvertJoins(fromClause);
        }

        return new SqlUpdateStatement(targetTable, setClauses, where, fromTable, joins);
    }

    /// <summary>
    /// Converts a ScriptDom DeleteStatement to a legacy SqlDeleteStatement.
    /// </summary>
    private static SqlDeleteStatement ConvertDeleteToLegacy(DeleteStatement delete)
    {
        var targetTable = ScriptDomAdapter.GetDeleteTargetTable(delete);
        var where = ScriptDomAdapter.GetDeleteWhere(delete);

        var fromClause = ScriptDomAdapter.GetDeleteFromClause(delete);
        SqlTableRef? fromTable = null;
        List<SqlJoin>? joins = null;
        if (fromClause != null)
        {
            fromTable = ScriptDomAdapter.ConvertFromClause(fromClause);
            joins = ScriptDomAdapter.ConvertJoins(fromClause);
        }

        return new SqlDeleteStatement(targetTable, where, fromTable, joins);
    }

    /// <summary>
    /// Converts a ScriptDom IfStatement to a legacy SqlIfStatement.
    /// </summary>
    private static SqlIfStatement ConvertIfToLegacy(IfStatement ifStmt)
    {
        var condition = ScriptDomAdapter.ConvertBooleanExpression(ifStmt.Predicate);

        var thenStatements = new List<ISqlStatement>();
        if (ifStmt.ThenStatement is BeginEndBlockStatement thenBlock)
        {
            foreach (TSqlStatement s in thenBlock.StatementList.Statements)
            {
                thenStatements.Add(ConvertToLegacyStatement(s));
            }
        }
        else
        {
            thenStatements.Add(ConvertToLegacyStatement(ifStmt.ThenStatement));
        }
        var thenBlockLegacy = new SqlBlockStatement(thenStatements, 0);

        SqlBlockStatement? elseBlockLegacy = null;
        if (ifStmt.ElseStatement != null)
        {
            var elseStatements = new List<ISqlStatement>();
            if (ifStmt.ElseStatement is BeginEndBlockStatement elseBlock)
            {
                foreach (TSqlStatement s in elseBlock.StatementList.Statements)
                {
                    elseStatements.Add(ConvertToLegacyStatement(s));
                }
            }
            else
            {
                elseStatements.Add(ConvertToLegacyStatement(ifStmt.ElseStatement));
            }
            elseBlockLegacy = new SqlBlockStatement(elseStatements, 0);
        }

        return new SqlIfStatement(condition, thenBlockLegacy, elseBlockLegacy, 0);
    }

    /// <summary>
    /// Converts a ScriptDom BeginEndBlockStatement to a legacy SqlBlockStatement.
    /// </summary>
    private static SqlBlockStatement ConvertBlockToLegacy(BeginEndBlockStatement block)
    {
        var statements = new List<ISqlStatement>();
        foreach (TSqlStatement stmt in block.StatementList.Statements)
        {
            statements.Add(ConvertToLegacyStatement(stmt));
        }
        return new SqlBlockStatement(statements, 0);
    }

    /// <summary>
    /// Converts a ScriptDom DeclareVariableStatement to a legacy SqlDeclareStatement.
    /// </summary>
    private static ISqlStatement ConvertDeclareToLegacy(DeclareVariableStatement declare)
    {
        // DeclareVariableStatement can have multiple declarations, take the first
        if (declare.Declarations.Count == 0)
            throw new QueryParseException("DECLARE statement has no declarations.");

        var decl = declare.Declarations[0];
        var varName = decl.VariableName.Value;
        if (!varName.StartsWith("@"))
            varName = "@" + varName;

        var typeName = FormatDataTypeReference(decl.DataType);

        ISqlExpression? initialValue = null;
        if (decl.Value != null)
        {
            initialValue = ScriptDomAdapter.ConvertExpression(decl.Value);
        }

        return new SqlDeclareStatement(varName, typeName, initialValue, 0);
    }

    /// <summary>
    /// Converts a ScriptDom SetVariableStatement to a legacy SqlSetVariableStatement.
    /// </summary>
    private static SqlSetVariableStatement ConvertSetVariableToLegacy(SetVariableStatement setVar)
    {
        var varName = setVar.Variable.Name;
        if (!varName.StartsWith("@"))
            varName = "@" + varName;

        var value = ScriptDomAdapter.ConvertExpression(setVar.Expression);
        return new SqlSetVariableStatement(varName, value, 0);
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
                windows.Add(new Dataverse.Query.Planning.Nodes.WindowDefinition(outputName, windowExpr));
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
                    projections.Add(ProjectColumn.Computed(compAlias, computed.Expression));
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
}
