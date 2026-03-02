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

        // Check each CTE for recursion. A CTE is recursive if its query body is a
        // UNION ALL where one branch references the CTE's own name in a FROM clause.
        // When a recursive CTE is detected, route to PlanRecursiveCte which produces
        // a RecursiveCteNode. Non-recursive CTEs continue through the normal path.
        foreach (CommonTableExpression cte in ctes)
        {
            var cteName = cte.ExpressionName.Value;
            if (IsRecursiveCte(cte, cteName))
            {
                return PlanRecursiveCte(cte, cteName, selectStmt, options, cteNames);
            }
        }

        // Non-recursive path: plan the outer query normally.
        // The CTE data will be made available to the outer query via CteScanNode.
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
    /// Determines whether a CTE is recursive by checking if its query body is a
    /// <see cref="BinaryQueryExpression"/> with UNION ALL where at least one branch
    /// references the CTE's own name in a FROM clause.
    /// </summary>
    private static bool IsRecursiveCte(CommonTableExpression cte, string cteName)
    {
        if (cte.QueryExpression is not BinaryQueryExpression binary)
            return false;
        if (binary.BinaryQueryExpressionType != BinaryQueryExpressionType.Union || !binary.All)
            return false;
        return ReferencesTable(binary.SecondQueryExpression, cteName)
            || ReferencesTable(binary.FirstQueryExpression, cteName);
    }

    /// <summary>
    /// Checks whether a <see cref="QueryExpression"/> (which must be a <see cref="QuerySpecification"/>)
    /// references the given table name in its FROM clause.
    /// </summary>
    private static bool ReferencesTable(QueryExpression expr, string tableName)
    {
        if (expr is not QuerySpecification spec) return false;
        if (spec.FromClause?.TableReferences == null) return false;
        return spec.FromClause.TableReferences.Any(t => ReferencesTableName(t, tableName));
    }

    /// <summary>
    /// Recursively checks whether a <see cref="TableReference"/> references a table with
    /// the given name. Handles <see cref="NamedTableReference"/> and <see cref="QualifiedJoin"/>.
    /// </summary>
    private static bool ReferencesTableName(TableReference tableRef, string name)
    {
        return tableRef switch
        {
            NamedTableReference named =>
                string.Equals(named.SchemaObject?.BaseIdentifier?.Value, name, StringComparison.OrdinalIgnoreCase),
            QualifiedJoin join =>
                ReferencesTableName(join.FirstTableReference, name) || ReferencesTableName(join.SecondTableReference, name),
            _ => false
        };
    }

    /// <summary>
    /// Plans a recursive CTE. Separates the anchor member (non-self-referencing branch)
    /// from the recursive member (self-referencing branch) within the UNION ALL, plans
    /// the anchor, and wraps the result in a <see cref="RecursiveCteNode"/>.
    /// </summary>
    /// <param name="cte">The CTE definition.</param>
    /// <param name="cteName">The CTE's name.</param>
    /// <param name="selectStmt">The enclosing SELECT statement.</param>
    /// <param name="options">Planning options.</param>
    /// <param name="cteNames">All CTE names in the WITH clause (for metadata).</param>
    private QueryPlanResult PlanRecursiveCte(
        CommonTableExpression cte,
        string cteName,
        SelectStatement selectStmt,
        QueryPlanOptions options,
        List<string> cteNames)
    {
        var binary = (BinaryQueryExpression)cte.QueryExpression;

        // Determine which branch is the anchor (non-self-referencing) and which is recursive.
        // By convention the anchor is usually first, but we detect based on self-reference.
        var isSecondRecursive = ReferencesTable(binary.SecondQueryExpression, cteName);
        var anchorExpr = isSecondRecursive ? binary.FirstQueryExpression : binary.SecondQueryExpression;
        var recursiveExpr = isSecondRecursive ? binary.SecondQueryExpression : binary.FirstQueryExpression;

        // Plan the anchor member. Constant-value anchors (SELECT with no FROM clause)
        // cannot be planned through the normal FetchXML path, so we use a CteScanNode
        // placeholder for the anchor and compile the SELECT expressions for execution.
        IQueryPlanNode anchorNode;
        string anchorFetchXml;
        IReadOnlyDictionary<string, VirtualColumnInfo> anchorVirtualColumns;
        string anchorEntityLogicalName;

        var isConstantAnchor = anchorExpr is QuerySpecification anchorSpec
            && (anchorSpec.FromClause == null || anchorSpec.FromClause.TableReferences.Count == 0);

        if (isConstantAnchor)
        {
            // Constant-value anchor (e.g., SELECT 1 AS level, 'root' AS name).
            // Build a ProjectNode over an empty CteScanNode to produce the constant row.
            var spec = (QuerySpecification)anchorExpr;
            var projections = new List<ProjectColumn>();

            foreach (var element in spec.SelectElements)
            {
                if (element is SelectScalarExpression scalar)
                {
                    var alias = scalar.ColumnName?.Value ?? "column";
                    var compiled = _expressionCompiler.CompileScalar(scalar.Expression);
                    projections.Add(ProjectColumn.Computed(alias, compiled));
                }
            }

            // The ProjectNode needs at least one input row to project.
            // Seed the CteScanNode with one empty row so the projections evaluate once.
            var seedRow = new QueryRow(
                new Dictionary<string, QueryValue>(StringComparer.OrdinalIgnoreCase),
                cteName);
            var emptyInput = new CteScanNode($"{cteName}_anchor", new List<QueryRow> { seedRow });

            anchorNode = projections.Count > 0
                ? new ProjectNode(emptyInput, projections)
                : (IQueryPlanNode)emptyInput;
            anchorFetchXml = $"-- ConstantAnchor: {cteName} --";
            anchorVirtualColumns = new Dictionary<string, VirtualColumnInfo>();
            anchorEntityLogicalName = cteName;
        }
        else
        {
            var anchorResult = PlanQueryExpressionAsSelect(anchorExpr, options);
            anchorNode = anchorResult.RootNode;
            anchorFetchXml = anchorResult.FetchXml;
            anchorVirtualColumns = anchorResult.VirtualColumns;
            anchorEntityLogicalName = anchorResult.EntityLogicalName;
        }

        // Create a RecursiveCteNode. The recursive node factory receives the previous
        // iteration's rows and re-plans the recursive member with a fresh CteScanNode
        // bound to the CTE name, so the self-reference resolves correctly.
        Func<List<QueryRow>, IQueryPlanNode> recursiveNodeFactory = previousRows =>
        {
            var cteScanNode = new CteScanNode(cteName, previousRows);
            var recursiveOptions = new QueryPlanOptions
            {
                PoolCapacity = options.PoolCapacity,
                UseTdsEndpoint = options.UseTdsEndpoint,
                OriginalSql = options.OriginalSql,
                VariableScope = options.VariableScope,
                RemoteExecutorFactory = options.RemoteExecutorFactory,
                CteBindings = new Dictionary<string, IQueryPlanNode>(StringComparer.OrdinalIgnoreCase)
                {
                    [cteName] = cteScanNode
                }
            };
            var result = PlanQueryExpressionAsSelect(recursiveExpr, recursiveOptions);
            return result.RootNode;
        };

        var recursiveCteNode = new RecursiveCteNode(
            cteName,
            anchorNode,
            recursiveNodeFactory);

        return new QueryPlanResult
        {
            RootNode = recursiveCteNode,
            FetchXml = $"-- RecursiveCTE: {string.Join(", ", cteNames)} --\n{anchorFetchXml}",
            VirtualColumns = anchorVirtualColumns,
            EntityLogicalName = anchorEntityLogicalName
        };
    }

    /// <summary>
    /// Plans a CTE self-reference within a recursive member. The source node is a
    /// <see cref="CteScanNode"/> holding the previous iteration's rows, with optional
    /// WHERE filtering and SELECT projection applied client-side.
    /// </summary>
    private QueryPlanResult PlanCteSelfReference(
        QuerySpecification querySpec,
        IQueryPlanNode cteNode,
        string entityName,
        QueryPlanOptions options)
    {
        IQueryPlanNode rootNode = cteNode;

        // Apply WHERE filter client-side
        if (querySpec.WhereClause?.SearchCondition != null)
        {
            var predicate = _expressionCompiler.CompilePredicate(querySpec.WhereClause.SearchCondition);
            var description = querySpec.WhereClause.SearchCondition.ToString() ?? "WHERE (cte)";
            rootNode = new ClientFilterNode(rootNode, predicate, description);
        }

        // Apply SELECT list projection
        if (HasComputedColumnsInQuerySpec(querySpec))
        {
            rootNode = BuildProjectNodeFromScriptDom(rootNode, querySpec);
        }
        else if (!IsSelectStar(querySpec))
        {
            rootNode = BuildSelectListProjection(rootNode, querySpec);
        }

        return new QueryPlanResult
        {
            RootNode = rootNode,
            FetchXml = $"-- CteSelfReference: {entityName} --",
            VirtualColumns = new Dictionary<string, VirtualColumnInfo>(),
            EntityLogicalName = entityName
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
        var scriptNode = new ScriptExecutionNode(statements, this, _expressionCompiler, _sessionContext, options);

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
}
