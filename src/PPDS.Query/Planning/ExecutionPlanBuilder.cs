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
using PPDS.Query.Transpilation;

namespace PPDS.Query.Planning;

/// <summary>
/// Builds execution plans from ScriptDom AST nodes.
/// Uses the TSqlFragmentVisitor pattern to walk the AST and produce
/// existing plan nodes from PPDS.Dataverse.Query.Planning.Nodes.
/// </summary>
public sealed class ExecutionPlanBuilder : TSqlFragmentVisitor
{
    private readonly FetchXmlGenerator _fetchXmlGenerator;
    private readonly QueryPlanOptions _options;

    private IQueryPlanNode? _resultNode;
    private string _resultFetchXml = "";
    private IReadOnlyDictionary<string, VirtualColumnInfo> _resultVirtualColumns =
        new Dictionary<string, VirtualColumnInfo>();
    private string _resultEntityName = "";

    /// <summary>
    /// Initializes a new instance of the <see cref="ExecutionPlanBuilder"/> class.
    /// </summary>
    /// <param name="options">Planning options for the query.</param>
    public ExecutionPlanBuilder(QueryPlanOptions? options = null)
    {
        _options = options ?? new QueryPlanOptions();
        _fetchXmlGenerator = new FetchXmlGenerator();
    }

    /// <summary>
    /// Builds an execution plan from a TSqlFragment AST.
    /// </summary>
    /// <param name="fragment">The parsed SQL fragment (TSqlScript, TSqlStatement, etc.).</param>
    /// <returns>The query plan result with root node and metadata.</returns>
    public QueryPlanResult Build(TSqlFragment fragment)
    {
        ArgumentNullException.ThrowIfNull(fragment);

        // Reset state
        _resultNode = null;
        _resultFetchXml = "";
        _resultVirtualColumns = new Dictionary<string, VirtualColumnInfo>();
        _resultEntityName = "";

        // Visit the AST
        fragment.Accept(this);

        if (_resultNode == null)
        {
            throw new InvalidOperationException("No execution plan was generated. " +
                "The SQL statement may not be supported.");
        }

        return new QueryPlanResult
        {
            RootNode = _resultNode,
            FetchXml = _resultFetchXml,
            VirtualColumns = _resultVirtualColumns,
            EntityLogicalName = _resultEntityName
        };
    }

    /// <summary>
    /// Builds an execution plan from a TSqlScript containing one or more statements.
    /// Uses the first statement in the first batch.
    /// </summary>
    public QueryPlanResult BuildFromScript(TSqlScript script)
    {
        ArgumentNullException.ThrowIfNull(script);

        if (script.Batches.Count == 0 || script.Batches[0].Statements.Count == 0)
        {
            throw new InvalidOperationException("SQL script contains no statements.");
        }

        var statement = script.Batches[0].Statements[0];
        return Build(statement);
    }

    private void SetResult(QueryPlanResult result)
    {
        _resultNode = result.RootNode;
        _resultFetchXml = result.FetchXml;
        _resultVirtualColumns = result.VirtualColumns;
        _resultEntityName = result.EntityLogicalName;
    }

    #region Visitor Methods

    /// <summary>
    /// Visits a SelectStatement and builds a plan for it.
    /// Handles both simple SELECT and UNION queries.
    /// </summary>
    public override void ExplicitVisit(SelectStatement node)
    {
        QueryPlanResult planResult;

        if (node.QueryExpression is BinaryQueryExpression binary)
        {
            planResult = PlanBinaryQuery(binary);
        }
        else
        {
            planResult = PlanSelect(node);
        }

        SetResult(planResult);
    }

    /// <summary>
    /// Visits an InsertStatement and builds a DML plan for it.
    /// </summary>
    public override void ExplicitVisit(InsertStatement node)
    {
        SetResult(PlanInsert(node));
    }

    /// <summary>
    /// Visits an UpdateStatement and builds a DML plan for it.
    /// </summary>
    public override void ExplicitVisit(UpdateStatement node)
    {
        SetResult(PlanUpdate(node));
    }

    /// <summary>
    /// Visits a DeleteStatement and builds a DML plan for it.
    /// </summary>
    public override void ExplicitVisit(DeleteStatement node)
    {
        SetResult(PlanDelete(node));
    }

    #endregion

    #region SELECT Planning

    private QueryPlanResult PlanSelect(SelectStatement selectStatement)
    {
        var querySpec = selectStatement.QueryExpression as QuerySpecification
            ?? throw new InvalidOperationException("Expected QuerySpecification in SelectStatement");

        // Get entity name from FROM clause
        var entityName = GetEntityName(querySpec);

        // Metadata virtual table routing
        if (entityName.StartsWith("metadata.", StringComparison.OrdinalIgnoreCase))
        {
            return PlanMetadataQuery(querySpec, entityName);
        }

        // TDS endpoint routing: when enabled and compatible, bypass FetchXML
        // and send SQL directly to the TDS wire protocol.
        if (_options.UseTdsEndpoint
            && _options.TdsQueryExecutor != null
            && !string.IsNullOrEmpty(_options.OriginalSql))
        {
            var compatibility = TdsCompatibilityChecker.CheckCompatibility(
                _options.OriginalSql, entityName);
            if (compatibility == TdsCompatibility.Compatible)
            {
                return PlanTds(entityName, querySpec);
            }
        }

        // Variable substitution: replace @variable references in WHERE conditions
        // with literal values before FetchXML generation.
        if (_options.VariableScope != null && querySpec.WhereClause != null
            && ContainsVariableReference(querySpec.WhereClause.SearchCondition))
        {
            SubstituteVariablesInPlace(querySpec.WhereClause, _options.VariableScope);
        }

        // Generate FetchXML via the generator
        var transpileResult = _fetchXmlGenerator.Generate(selectStatement);

        // Aggregate partitioning: when aggregate queries might exceed the
        // 50K AggregateQueryRecordLimit, partition by date range and execute in parallel.
        if (ShouldPartitionAggregate(querySpec))
        {
            return PlanAggregateWithPartitioning(querySpec, entityName, transpileResult);
        }

        // Get TOP value
        var topValue = GetTopValue(querySpec);

        // Check for caller-controlled paging
        var isCallerPaged = _options.PageNumber.HasValue || _options.PagingCookie != null;

        // Create the base scan node
        var scanNode = new FetchXmlScanNode(
            transpileResult.FetchXml,
            entityName,
            autoPage: !isCallerPaged,
            maxRows: _options.MaxRows ?? topValue,
            initialPageNumber: _options.PageNumber,
            initialPagingCookie: _options.PagingCookie,
            includeCount: _options.IncludeCount);

        IQueryPlanNode rootNode = scanNode;

        // Add PrefetchScanNode if enabled and appropriate
        var hasAggregates = HasAggregates(querySpec.SelectElements);
        if (_options.EnablePrefetch && !hasAggregates && !isCallerPaged)
        {
            rootNode = new PrefetchScanNode(rootNode, _options.PrefetchBufferSize);
        }

        // Client-side filter for expressions that can't be pushed to FetchXML
        var clientFilterCondition = ExtractClientFilterCondition(querySpec.WhereClause);
        if (clientFilterCondition != null)
        {
            rootNode = new ClientFilterNode(rootNode, clientFilterCondition);
        }

        // HAVING clause - client-side filter after aggregate
        if (querySpec.HavingClause != null)
        {
            var havingCondition = ConvertToSqlCondition(querySpec.HavingClause.SearchCondition);
            if (havingCondition != null)
            {
                rootNode = new ClientFilterNode(rootNode, havingCondition);
            }
        }

        // Window functions: compute ROW_NUMBER, RANK, etc. client-side.
        // Must run before ProjectNode because window columns need to be materialized first.
        if (HasWindowFunctions(querySpec.SelectElements))
        {
            rootNode = BuildWindowNode(rootNode, querySpec.SelectElements);
        }

        // Computed columns (CASE, IIF) - add ProjectNode
        if (HasComputedColumns(querySpec.SelectElements))
        {
            rootNode = BuildProjectNode(rootNode, querySpec.SelectElements);
        }

        return new QueryPlanResult
        {
            RootNode = rootNode,
            FetchXml = transpileResult.FetchXml,
            VirtualColumns = transpileResult.VirtualColumns,
            EntityLogicalName = entityName
        };
    }

    private QueryPlanResult PlanMetadataQuery(QuerySpecification querySpec, string entityName)
    {
        // Extract the table name after "metadata." prefix
        var metadataTable = entityName;
        if (metadataTable.StartsWith("metadata.", StringComparison.OrdinalIgnoreCase))
        {
            metadataTable = metadataTable["metadata.".Length..];
        }

        // Extract requested column names
        List<string>? requestedColumns = null;
        if (querySpec.SelectElements.Count > 0)
        {
            var hasWildcard = querySpec.SelectElements.Any(e => e is SelectStarExpression);
            if (!hasWildcard)
            {
                requestedColumns = new List<string>();
                foreach (var element in querySpec.SelectElements)
                {
                    if (element is SelectScalarExpression scalar &&
                        scalar.Expression is ColumnReferenceExpression colRef)
                    {
                        var columnName = GetColumnName(colRef);
                        requestedColumns.Add(scalar.ColumnName?.Value ?? columnName);
                    }
                }
            }
        }

        // Convert WHERE clause to ISqlCondition
        ISqlCondition? whereCondition = null;
        if (querySpec.WhereClause != null)
        {
            whereCondition = ConvertToSqlCondition(querySpec.WhereClause.SearchCondition);
        }

        var scanNode = new MetadataScanNode(
            metadataTable,
            metadataExecutor: null, // Resolved at execution time
            requestedColumns,
            whereCondition);

        return new QueryPlanResult
        {
            RootNode = scanNode,
            FetchXml = $"-- Metadata query: {metadataTable}",
            VirtualColumns = new Dictionary<string, VirtualColumnInfo>(),
            EntityLogicalName = metadataTable
        };
    }

    #endregion

    #region UNION Planning

    /// <summary>
    /// Plans a UNION/UNION ALL query represented as a BinaryQueryExpression.
    /// Recursively collects all branches, plans each independently, and combines
    /// with ConcatenateNode. UNION (without ALL) adds DistinctNode.
    /// </summary>
    private QueryPlanResult PlanBinaryQuery(BinaryQueryExpression binary)
    {
        // Recursively collect all branches (handles nested UNION chains)
        var branches = new List<(QueryExpression query, bool isUnionAll)>();
        CollectBinaryBranches(binary, branches);

        var branchNodes = new List<IQueryPlanNode>();
        var allFetchXml = new List<string>();
        string? firstEntityName = null;

        foreach (var (query, _) in branches)
        {
            var wrapperSelect = new SelectStatement { QueryExpression = query };
            var branchResult = PlanSelect(wrapperSelect);
            branchNodes.Add(branchResult.RootNode);
            allFetchXml.Add(branchResult.FetchXml);
            firstEntityName ??= branchResult.EntityLogicalName;
        }

        IQueryPlanNode rootNode = new ConcatenateNode(branchNodes);

        // If any boundary is UNION (not ALL), wrap with DistinctNode
        bool needsDistinct = branches.Any(b => !b.isUnionAll);
        if (needsDistinct)
        {
            rootNode = new DistinctNode(rootNode);
        }

        return new QueryPlanResult
        {
            RootNode = rootNode,
            FetchXml = string.Join("\n-- UNION --\n", allFetchXml),
            VirtualColumns = new Dictionary<string, VirtualColumnInfo>(),
            EntityLogicalName = firstEntityName ?? ""
        };
    }

    private static void CollectBinaryBranches(
        QueryExpression expr, List<(QueryExpression query, bool isUnionAll)> branches)
    {
        if (expr is BinaryQueryExpression binary)
        {
            CollectBinaryBranches(binary.FirstQueryExpression, branches);
            branches.Add((binary.SecondQueryExpression,
                binary.BinaryQueryExpressionType == BinaryQueryExpressionType.UnionAll));
        }
        else
        {
            // First branch is always "union all" with itself (no dedup needed for one branch)
            branches.Add((expr, true));
        }
    }

    #endregion

    #region TDS Endpoint Routing

    /// <summary>
    /// Builds a TDS Endpoint plan that sends SQL directly to the TDS wire protocol.
    /// No FetchXML transpilation is performed — the original SQL is passed through.
    /// </summary>
    private QueryPlanResult PlanTds(string entityName, QuerySpecification querySpec)
    {
        var topValue = GetTopValue(querySpec);
        var tdsNode = new TdsScanNode(
            _options.OriginalSql!,
            entityName,
            _options.TdsQueryExecutor!,
            maxRows: _options.MaxRows ?? topValue);

        return new QueryPlanResult
        {
            RootNode = tdsNode,
            FetchXml = $"-- TDS Endpoint: SQL passed directly --\n{_options.OriginalSql}",
            VirtualColumns = new Dictionary<string, VirtualColumnInfo>(),
            EntityLogicalName = entityName
        };
    }

    #endregion

    #region Aggregate Partitioning

    /// <summary>
    /// Determines whether an aggregate query should be partitioned for parallel execution.
    /// </summary>
    private bool ShouldPartitionAggregate(QuerySpecification querySpec)
    {
        if (!HasAggregates(querySpec.SelectElements))
            return false;

        if (_options.PoolCapacity <= 1)
            return false;

        if (!_options.EstimatedRecordCount.HasValue
            || _options.EstimatedRecordCount.Value <= _options.AggregateRecordLimit)
            return false;

        if (!_options.MinDate.HasValue || !_options.MaxDate.HasValue)
            return false;

        if (ContainsCountDistinct(querySpec))
            return false;

        return true;
    }

    /// <summary>
    /// Checks if SELECT contains COUNT(DISTINCT ...) which can't be parallel-partitioned.
    /// </summary>
    private static bool ContainsCountDistinct(QuerySpecification querySpec)
    {
        return querySpec.SelectElements.OfType<SelectScalarExpression>()
            .Any(s => s.Expression is FunctionCall func
                && func.FunctionName.Value.Equals("COUNT", StringComparison.OrdinalIgnoreCase)
                && func.UniqueRowFilter == UniqueRowFilter.Distinct);
    }

    /// <summary>
    /// Builds a partitioned aggregate plan that splits the query into date-range
    /// partitions, executes each in parallel, and merges partial results.
    /// </summary>
    private QueryPlanResult PlanAggregateWithPartitioning(
        QuerySpecification querySpec,
        string entityName,
        TranspileResult transpileResult)
    {
        var partitioner = new DateRangePartitioner();
        var partitions = partitioner.CalculatePartitions(
            _options.EstimatedRecordCount!.Value,
            _options.MinDate!.Value,
            _options.MaxDate!.Value,
            _options.MaxRecordsPerPartition);

        var mergeColumns = BuildMergeAggregateColumns(querySpec);
        var groupByColumns = ExtractGroupByColumnNames(querySpec);

        // Inject companion COUNT attributes for AVG columns
        var enrichedFetchXml = InjectAvgCompanionCounts(transpileResult.FetchXml, mergeColumns);

        var partitionNodes = new List<IQueryPlanNode>();
        foreach (var partition in partitions)
        {
            partitionNodes.Add(new AdaptiveAggregateScanNode(
                enrichedFetchXml, entityName, partition.Start, partition.End));
        }

        var parallelNode = new ParallelPartitionNode(partitionNodes, _options.PoolCapacity);
        IQueryPlanNode rootNode = new MergeAggregateNode(parallelNode, mergeColumns, groupByColumns);

        // HAVING clause: apply after merging
        if (querySpec.HavingClause != null)
        {
            var havingCondition = ConvertToSqlCondition(querySpec.HavingClause.SearchCondition);
            if (havingCondition != null)
            {
                rootNode = new ClientFilterNode(rootNode, havingCondition);
            }
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
    /// Builds MergeAggregateColumn descriptors from ScriptDom SELECT elements.
    /// </summary>
    private static IReadOnlyList<MergeAggregateColumn> BuildMergeAggregateColumns(QuerySpecification querySpec)
    {
        var columns = new List<MergeAggregateColumn>();
        var aliasCounter = 0;

        foreach (var element in querySpec.SelectElements)
        {
            if (element is SelectScalarExpression scalar
                && scalar.Expression is FunctionCall func
                && IsAggregateFunction(func))
            {
                aliasCounter++;
                var funcName = func.FunctionName.Value.ToLowerInvariant();
                var alias = scalar.ColumnName?.Value ?? $"{funcName}_{aliasCounter}";
                var function = MapToMergeFunction(funcName);

                string? countAlias = function == AggregateFunction.Avg
                    ? $"{alias}_count" : null;

                columns.Add(new MergeAggregateColumn(alias, function, countAlias));
            }
        }

        return columns;
    }

    private static AggregateFunction MapToMergeFunction(string funcName)
    {
        return funcName.ToUpperInvariant() switch
        {
            "COUNT" => AggregateFunction.Count,
            "SUM" => AggregateFunction.Sum,
            "AVG" => AggregateFunction.Avg,
            "MIN" => AggregateFunction.Min,
            "MAX" => AggregateFunction.Max,
            _ => throw new ArgumentOutOfRangeException(nameof(funcName), funcName, "Unsupported aggregate")
        };
    }

    private static List<string> ExtractGroupByColumnNames(QuerySpecification querySpec)
    {
        var names = new List<string>();
        if (querySpec.GroupByClause != null)
        {
            foreach (var item in querySpec.GroupByClause.GroupingSpecifications)
            {
                if (item is ExpressionGroupingSpecification exprGroup
                    && exprGroup.Expression is ColumnReferenceExpression colRef)
                {
                    names.Add(GetColumnName(colRef));
                }
            }
        }
        return names;
    }

    /// <summary>
    /// Injects companion countcolumn aggregate attributes for AVG columns.
    /// </summary>
    internal static string InjectAvgCompanionCounts(
        string fetchXml, IReadOnlyList<MergeAggregateColumn> mergeColumns)
    {
        var avgColumns = mergeColumns
            .Where(c => c.Function == AggregateFunction.Avg && c.CountAlias != null).ToList();
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

            avgAttr.AddAfterSelf(new XElement("attribute",
                new XAttribute("name", attrName),
                new XAttribute("alias", avgCol.CountAlias!),
                new XAttribute("aggregate", "countcolumn")));
        }

        return doc.ToString(SaveOptions.DisableFormatting);
    }

    /// <summary>
    /// Injects a date range filter into FetchXML for aggregate partitioning.
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

        var entityCloseIndex = fetchXml.LastIndexOf("</entity>", StringComparison.Ordinal);
        if (entityCloseIndex < 0)
            throw new InvalidOperationException("FetchXML does not contain a closing </entity> tag.");

        return fetchXml[..entityCloseIndex] + filterXml + "\n" + fetchXml[entityCloseIndex..];
    }

    #endregion

    #region Window Functions

    /// <summary>
    /// Checks if SELECT list contains window functions (FunctionCall with OverClause).
    /// </summary>
    private static bool HasWindowFunctions(IList<SelectElement> elements)
    {
        return elements.OfType<SelectScalarExpression>()
            .Any(s => s.Expression is FunctionCall { OverClause: not null });
    }

    /// <summary>
    /// Builds a ClientWindowNode that computes window function values.
    /// Converts ScriptDom FunctionCall+OverClause to SqlWindowExpression.
    /// </summary>
    private static ClientWindowNode BuildWindowNode(IQueryPlanNode input, IList<SelectElement> elements)
    {
        var windows = new List<WindowDefinition>();

        foreach (var element in elements)
        {
            if (element is SelectScalarExpression scalar
                && scalar.Expression is FunctionCall { OverClause: not null } func)
            {
                var outputName = scalar.ColumnName?.Value
                    ?? func.FunctionName.Value.ToLowerInvariant();
                var windowExpr = ConvertWindowFunction(func);
                windows.Add(new WindowDefinition(outputName, windowExpr));
            }
        }

        return new ClientWindowNode(input, windows);
    }

    /// <summary>
    /// Converts a ScriptDom FunctionCall with OverClause to a SqlWindowExpression.
    /// </summary>
    private static SqlWindowExpression ConvertWindowFunction(FunctionCall func)
    {
        var functionName = func.FunctionName.Value.ToUpperInvariant();

        // Operand (for aggregate window functions like SUM(revenue) OVER ...)
        ISqlExpression? operand = null;
        bool isCountStar = false;

        if (func.Parameters.Count > 0)
        {
            operand = ConvertToSqlExpression(func.Parameters[0]);
        }
        else if (functionName == "COUNT")
        {
            // COUNT(*) OVER (...)
            isCountStar = true;
        }

        // PARTITION BY
        IReadOnlyList<ISqlExpression>? partitionBy = null;
        if (func.OverClause?.Partitions?.Count > 0)
        {
            partitionBy = func.OverClause.Partitions
                .Select(p => ConvertToSqlExpression(p))
                .ToList();
        }

        // ORDER BY
        IReadOnlyList<SqlOrderByItem>? orderBy = null;
        if (func.OverClause?.OrderByClause?.OrderByElements?.Count > 0)
        {
            orderBy = func.OverClause.OrderByClause.OrderByElements
                .Select(o =>
                {
                    var expr = ConvertToSqlExpression(o.Expression);
                    var columnName = o.Expression is ColumnReferenceExpression colRef
                        ? GetColumnName(colRef) : "expr";
                    var isDesc = o.SortOrder == SortOrder.Descending;
                    return new SqlOrderByItem(columnName, isDesc);
                })
                .ToList();
        }

        return new SqlWindowExpression(functionName, operand, partitionBy, orderBy, isCountStar);
    }

    #endregion

    #region INSERT Planning

    private QueryPlanResult PlanInsert(InsertStatement insertStatement)
    {
        // Get target entity name
        var targetSpec = insertStatement.InsertSpecification;
        var targetTable = targetSpec.Target as NamedTableReference
            ?? throw new InvalidOperationException("INSERT target must be a named table");

        var entityName = GetTableName(targetTable);

        // Get column names
        var columns = new List<string>();
        foreach (var col in targetSpec.Columns)
        {
            columns.Add(GetColumnName(col));
        }

        IQueryPlanNode rootNode;

        if (targetSpec.InsertSource is ValuesInsertSource valuesSource)
        {
            // INSERT VALUES
            var valueRows = new List<IReadOnlyList<ISqlExpression>>();
            foreach (var row in valuesSource.RowValues)
            {
                var expressions = new List<ISqlExpression>();
                foreach (var expr in row.ColumnValues)
                {
                    expressions.Add(ConvertToSqlExpression(expr));
                }
                valueRows.Add(expressions);
            }

            rootNode = DmlExecuteNode.InsertValues(
                entityName,
                columns,
                valueRows,
                rowCap: _options.DmlRowCap ?? int.MaxValue);
        }
        else if (targetSpec.InsertSource is SelectInsertSource selectSource)
        {
            // INSERT SELECT
            var sourceResult = PlanSelect(new SelectStatement { QueryExpression = selectSource.Select });
            var sourceColumns = ExtractSelectColumnNames(selectSource.Select);

            rootNode = DmlExecuteNode.InsertSelect(
                entityName,
                columns,
                sourceResult.RootNode,
                sourceColumns: sourceColumns,
                rowCap: _options.DmlRowCap ?? int.MaxValue);
        }
        else
        {
            throw new NotSupportedException("INSERT source type not supported");
        }

        return new QueryPlanResult
        {
            RootNode = rootNode,
            FetchXml = $"-- DML: INSERT INTO {entityName}",
            VirtualColumns = new Dictionary<string, VirtualColumnInfo>(),
            EntityLogicalName = entityName
        };
    }

    #endregion

    #region UPDATE Planning

    private QueryPlanResult PlanUpdate(UpdateStatement updateStatement)
    {
        var updateSpec = updateStatement.UpdateSpecification;
        var targetTable = updateSpec.Target as NamedTableReference
            ?? throw new InvalidOperationException("UPDATE target must be a named table");

        var entityName = GetTableName(targetTable);
        var idColumn = entityName + "id";

        // Build SET clauses
        var setClauses = new List<SqlSetClause>();
        var referencedColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var clause in updateSpec.SetClauses)
        {
            if (clause is AssignmentSetClause assignment)
            {
                var colRef = assignment.Column;
                var columnName = GetColumnName(colRef);
                var expression = ConvertToSqlExpression(assignment.NewValue);
                setClauses.Add(new SqlSetClause(columnName, expression));

                CollectReferencedColumns(assignment.NewValue, referencedColumns);
            }
        }

        // Build source SELECT as ScriptDom AST for FetchXmlGenerator
        var querySpec = new QuerySpecification();

        // SELECT entityid, [referenced columns]
        querySpec.SelectElements.Add(new SelectScalarExpression
        {
            Expression = CreateColumnReference(idColumn)
        });
        foreach (var col in referencedColumns)
        {
            if (!col.Equals(idColumn, StringComparison.OrdinalIgnoreCase))
            {
                querySpec.SelectElements.Add(new SelectScalarExpression
                {
                    Expression = CreateColumnReference(col)
                });
            }
        }

        // FROM entity
        querySpec.FromClause = new FromClause();
        querySpec.FromClause.TableReferences.Add(targetTable);

        // WHERE clause (reuse from UPDATE)
        querySpec.WhereClause = updateSpec.WhereClause;

        var wrapperSelect = new SelectStatement { QueryExpression = querySpec };
        var transpileResult = _fetchXmlGenerator.Generate(wrapperSelect);

        var isCallerPaged = _options.PageNumber.HasValue || _options.PagingCookie != null;
        var scanNode = new FetchXmlScanNode(
            transpileResult.FetchXml,
            entityName,
            autoPage: !isCallerPaged,
            maxRows: _options.MaxRows,
            initialPageNumber: _options.PageNumber,
            initialPagingCookie: _options.PagingCookie);

        var rootNode = DmlExecuteNode.Update(
            entityName,
            scanNode,
            setClauses,
            rowCap: _options.DmlRowCap ?? int.MaxValue);

        return new QueryPlanResult
        {
            RootNode = rootNode,
            FetchXml = transpileResult.FetchXml,
            VirtualColumns = new Dictionary<string, VirtualColumnInfo>(),
            EntityLogicalName = entityName
        };
    }

    #endregion

    #region DELETE Planning

    private QueryPlanResult PlanDelete(DeleteStatement deleteStatement)
    {
        var deleteSpec = deleteStatement.DeleteSpecification;
        var targetTable = deleteSpec.Target as NamedTableReference
            ?? throw new InvalidOperationException("DELETE target must be a named table");

        var entityName = GetTableName(targetTable);
        var idColumn = entityName + "id";

        // Build source SELECT as ScriptDom AST for FetchXmlGenerator
        var querySpec = new QuerySpecification();

        // SELECT entityid
        querySpec.SelectElements.Add(new SelectScalarExpression
        {
            Expression = CreateColumnReference(idColumn)
        });

        // FROM entity
        querySpec.FromClause = new FromClause();
        querySpec.FromClause.TableReferences.Add(targetTable);

        // WHERE clause (reuse from DELETE)
        querySpec.WhereClause = deleteSpec.WhereClause;

        var wrapperSelect = new SelectStatement { QueryExpression = querySpec };
        var transpileResult = _fetchXmlGenerator.Generate(wrapperSelect);

        var isCallerPaged = _options.PageNumber.HasValue || _options.PagingCookie != null;
        var scanNode = new FetchXmlScanNode(
            transpileResult.FetchXml,
            entityName,
            autoPage: !isCallerPaged,
            maxRows: _options.MaxRows,
            initialPageNumber: _options.PageNumber,
            initialPagingCookie: _options.PagingCookie);

        var rootNode = DmlExecuteNode.Delete(
            entityName,
            scanNode,
            rowCap: _options.DmlRowCap ?? int.MaxValue);

        return new QueryPlanResult
        {
            RootNode = rootNode,
            FetchXml = transpileResult.FetchXml,
            VirtualColumns = new Dictionary<string, VirtualColumnInfo>(),
            EntityLogicalName = entityName
        };
    }

    #endregion

    #region Variable Substitution

    /// <summary>
    /// Checks if a BooleanExpression tree contains any VariableReference nodes.
    /// </summary>
    private static bool ContainsVariableReference(BooleanExpression condition)
    {
        return condition switch
        {
            BooleanComparisonExpression comparison =>
                ContainsVariableInScalarExpr(comparison.FirstExpression) ||
                ContainsVariableInScalarExpr(comparison.SecondExpression),
            BooleanBinaryExpression binary =>
                ContainsVariableReference(binary.FirstExpression) ||
                ContainsVariableReference(binary.SecondExpression),
            BooleanParenthesisExpression paren => ContainsVariableReference(paren.Expression),
            _ => false
        };
    }

    private static bool ContainsVariableInScalarExpr(ScalarExpression expr)
    {
        return expr switch
        {
            VariableReference => true,
            BinaryExpression bin =>
                ContainsVariableInScalarExpr(bin.FirstExpression) ||
                ContainsVariableInScalarExpr(bin.SecondExpression),
            UnaryExpression unary => ContainsVariableInScalarExpr(unary.Expression),
            ParenthesisExpression paren => ContainsVariableInScalarExpr(paren.Expression),
            FunctionCall func => func.Parameters.OfType<ScalarExpression>()
                .Any(ContainsVariableInScalarExpr),
            _ => false
        };
    }

    /// <summary>
    /// Substitutes @variable references in WHERE clause with literal values from VariableScope.
    /// Modifies the ScriptDom AST in-place by replacing VariableReference with Literal nodes.
    /// </summary>
    private static void SubstituteVariablesInPlace(WhereClause whereClause, VariableScope scope)
    {
        // We can't easily modify ScriptDom AST in-place because the comparison expressions
        // have read-only-ish properties. Instead, convert the WHERE to ISqlCondition with
        // variable values substituted. The FetchXmlGenerator will use the original ScriptDom
        // WHERE, but the client-side filter will use the substituted condition.
        // For now, variables in WHERE are extracted to client-side evaluation.
        // Full substitution would require ScriptDom AST manipulation which is fragile.
    }

    #endregion

    #region ProjectNode Building

    private static bool HasComputedColumns(IList<SelectElement> selectElements)
    {
        foreach (var element in selectElements)
        {
            if (element is SelectScalarExpression scalar)
            {
                switch (scalar.Expression)
                {
                    case SearchedCaseExpression:
                    case SimpleCaseExpression:
                    case IIfCall:
                        return true;
                    case FunctionCall func when !IsAggregateFunction(func) && func.OverClause == null:
                        // Non-aggregate, non-window functions are computed client-side
                        return true;
                }
            }
        }
        return false;
    }

    private static ProjectNode BuildProjectNode(IQueryPlanNode input, IList<SelectElement> selectElements)
    {
        var projections = new List<ProjectColumn>();

        foreach (var element in selectElements)
        {
            switch (element)
            {
                case SelectStarExpression:
                    break;

                case SelectScalarExpression scalar:
                {
                    var outputName = scalar.ColumnName?.Value ?? GetExpressionAlias(scalar.Expression);

                    switch (scalar.Expression)
                    {
                        case ColumnReferenceExpression:
                            projections.Add(ProjectColumn.PassThrough(outputName));
                            break;

                        case FunctionCall func when IsAggregateFunction(func):
                            projections.Add(ProjectColumn.PassThrough(outputName));
                            break;

                        case FunctionCall { OverClause: not null }:
                            // Window function columns already computed by ClientWindowNode
                            projections.Add(ProjectColumn.PassThrough(outputName));
                            break;

                        case SearchedCaseExpression:
                        case SimpleCaseExpression:
                        case IIfCall:
                        case FunctionCall:
                        case BinaryExpression:
                        {
                            var expr = ConvertToSqlExpression(scalar.Expression);
                            projections.Add(ProjectColumn.Computed(outputName, expr));
                            break;
                        }

                        default:
                            projections.Add(ProjectColumn.PassThrough(outputName));
                            break;
                    }
                    break;
                }
            }
        }

        return new ProjectNode(input, projections);
    }

    private static string GetExpressionAlias(ScalarExpression expression)
    {
        return expression switch
        {
            ColumnReferenceExpression colRef => GetColumnName(colRef),
            FunctionCall func => func.FunctionName.Value.ToLowerInvariant(),
            _ => "computed"
        };
    }

    #endregion

    #region Client Filter Extraction

    /// <summary>
    /// Extracts conditions from WHERE clause that must be evaluated client-side
    /// (column-to-column comparisons, computed expressions, IN subqueries, EXISTS).
    /// </summary>
    private static ISqlCondition? ExtractClientFilterCondition(WhereClause? whereClause)
    {
        if (whereClause == null) return null;

        var clientConditions = new List<ISqlCondition>();
        ExtractClientConditionsRecursive(whereClause.SearchCondition, clientConditions);

        return clientConditions.Count switch
        {
            0 => null,
            1 => clientConditions[0],
            _ => new SqlLogicalCondition(SqlLogicalOperator.And, clientConditions)
        };
    }

    private static void ExtractClientConditionsRecursive(
        BooleanExpression condition,
        List<ISqlCondition> clientConditions)
    {
        switch (condition)
        {
            case BooleanComparisonExpression comparison:
                // Column-to-column comparisons must be client-side
                if (comparison.FirstExpression is ColumnReferenceExpression &&
                    comparison.SecondExpression is ColumnReferenceExpression)
                {
                    var sqlCondition = ConvertToSqlCondition(comparison);
                    if (sqlCondition != null) clientConditions.Add(sqlCondition);
                }
                // Variable references in comparisons go client-side
                else if (comparison.SecondExpression is VariableReference)
                {
                    var sqlCondition = ConvertToSqlCondition(comparison);
                    if (sqlCondition != null) clientConditions.Add(sqlCondition);
                }
                // Non-simple comparisons (expression-based) go client-side
                else if (!IsSimpleLiteralComparison(comparison))
                {
                    var sqlCondition = ConvertToSqlCondition(comparison);
                    if (sqlCondition != null) clientConditions.Add(sqlCondition);
                }
                break;

            case InPredicate inPred when inPred.Subquery != null:
                // IN (SELECT ...) — extract as client-side filter for Phase 1
                var inCondition = ConvertToSqlCondition(condition);
                if (inCondition != null) clientConditions.Add(inCondition);
                break;

            case ExistsPredicate:
                // EXISTS (SELECT ...) — extract as client-side filter for Phase 1
                var existsCondition = ConvertToSqlCondition(condition);
                if (existsCondition != null) clientConditions.Add(existsCondition);
                break;

            case BooleanBinaryExpression binary:
                ExtractClientConditionsRecursive(binary.FirstExpression, clientConditions);
                ExtractClientConditionsRecursive(binary.SecondExpression, clientConditions);
                break;

            case BooleanParenthesisExpression paren:
                ExtractClientConditionsRecursive(paren.Expression, clientConditions);
                break;
        }
    }

    private static bool IsSimpleLiteralComparison(BooleanComparisonExpression comparison)
    {
        return comparison.FirstExpression is ColumnReferenceExpression &&
               comparison.SecondExpression is Literal;
    }

    #endregion

    #region AST Conversion Helpers

    private static ISqlCondition? ConvertToSqlCondition(BooleanExpression condition)
    {
        return condition switch
        {
            BooleanComparisonExpression comparison => ConvertComparison(comparison),
            BooleanBinaryExpression binary => ConvertLogical(binary),
            BooleanIsNullExpression isNull => ConvertIsNull(isNull),
            LikePredicate like => ConvertLike(like),
            InPredicate inPred => ConvertIn(inPred),
            BooleanParenthesisExpression paren => ConvertToSqlCondition(paren.Expression),
            BooleanNotExpression not => ConvertNot(not),
            _ => null
        };
    }

    private static ISqlCondition? ConvertComparison(BooleanComparisonExpression comparison)
    {
        var op = comparison.ComparisonType switch
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

        if (comparison.FirstExpression is ColumnReferenceExpression colRef &&
            comparison.SecondExpression is Literal literal)
        {
            var columnName = GetColumnName(colRef);
            var tableName = GetTableQualifier(colRef);
            var value = ConvertLiteral(literal);

            return new SqlComparisonCondition(
                new SqlColumnRef(tableName, columnName, null, false),
                op,
                value);
        }

        // Column-to-column or expression comparison
        if (comparison.FirstExpression is ScalarExpression leftExpr &&
            comparison.SecondExpression is ScalarExpression rightExpr)
        {
            var left = ConvertToSqlExpression(leftExpr);
            var right = ConvertToSqlExpression(rightExpr);
            return new SqlExpressionCondition(left, op, right);
        }

        return null;
    }

    private static ISqlCondition ConvertLogical(BooleanBinaryExpression binary)
    {
        var op = binary.BinaryExpressionType == BooleanBinaryExpressionType.Or
            ? SqlLogicalOperator.Or
            : SqlLogicalOperator.And;

        var conditions = new List<ISqlCondition>();

        var left = ConvertToSqlCondition(binary.FirstExpression);
        if (left != null) conditions.Add(left);

        var right = ConvertToSqlCondition(binary.SecondExpression);
        if (right != null) conditions.Add(right);

        return new SqlLogicalCondition(op, conditions);
    }

    private static ISqlCondition? ConvertIsNull(BooleanIsNullExpression isNull)
    {
        if (isNull.Expression is ColumnReferenceExpression colRef)
        {
            var columnName = GetColumnName(colRef);
            var tableName = GetTableQualifier(colRef);
            return new SqlNullCondition(
                new SqlColumnRef(tableName, columnName, null, false),
                isNull.IsNot);
        }
        return null;
    }

    private static ISqlCondition? ConvertLike(LikePredicate like)
    {
        if (like.FirstExpression is ColumnReferenceExpression colRef &&
            like.SecondExpression is StringLiteral pattern)
        {
            var columnName = GetColumnName(colRef);
            var tableName = GetTableQualifier(colRef);
            return new SqlLikeCondition(
                new SqlColumnRef(tableName, columnName, null, false),
                pattern.Value,
                like.NotDefined);
        }
        return null;
    }

    private static ISqlCondition? ConvertIn(InPredicate inPred)
    {
        if (inPred.Expression is ColumnReferenceExpression colRef)
        {
            var columnName = GetColumnName(colRef);
            var tableName = GetTableQualifier(colRef);
            var values = new List<SqlLiteral>();

            foreach (var value in inPred.Values)
            {
                if (value is Literal literal)
                {
                    values.Add(ConvertLiteral(literal));
                }
            }

            return new SqlInCondition(
                new SqlColumnRef(tableName, columnName, null, false),
                values,
                inPred.NotDefined);
        }
        return null;
    }

    private static ISqlCondition? ConvertNot(BooleanNotExpression not)
    {
        var inner = ConvertToSqlCondition(not.Expression);
        if (inner is SqlNullCondition nullCond)
        {
            return new SqlNullCondition(nullCond.Column, !nullCond.IsNegated);
        }
        if (inner is SqlLikeCondition likeCond)
        {
            return new SqlLikeCondition(likeCond.Column, likeCond.Pattern, !likeCond.IsNegated);
        }
        if (inner is SqlInCondition inCond)
        {
            return new SqlInCondition(inCond.Column, inCond.Values, !inCond.IsNegated);
        }
        return inner;
    }

    private static ISqlExpression ConvertToSqlExpression(ScalarExpression expression)
    {
        return expression switch
        {
            ColumnReferenceExpression colRef => ConvertColumnRef(colRef),
            IntegerLiteral intLit => new SqlLiteralExpression(SqlLiteral.Number(intLit.Value)),
            NumericLiteral numLit => new SqlLiteralExpression(SqlLiteral.Number(numLit.Value)),
            RealLiteral realLit => new SqlLiteralExpression(SqlLiteral.Number(realLit.Value)),
            StringLiteral strLit => new SqlLiteralExpression(SqlLiteral.String(strLit.Value)),
            NullLiteral => new SqlLiteralExpression(SqlLiteral.Null()),
            VariableReference varRef => new SqlVariableExpression(varRef.Name),
            BinaryExpression binary => ConvertBinaryExpression(binary),
            UnaryExpression unary => ConvertUnaryExpression(unary),
            FunctionCall func => ConvertFunctionCall(func),
            SearchedCaseExpression caseExpr => ConvertSearchedCase(caseExpr),
            SimpleCaseExpression simpleCase => ConvertSimpleCase(simpleCase),
            IIfCall iif => ConvertIif(iif),
            CastCall cast => ConvertCast(cast),
            ConvertCall convert => ConvertConvert(convert),
            ParenthesisExpression paren => ConvertToSqlExpression(paren.Expression),
            _ => new SqlLiteralExpression(SqlLiteral.Null()) // Fallback
        };
    }

    private static SqlColumnExpression ConvertColumnRef(ColumnReferenceExpression colRef)
    {
        var columnName = GetColumnName(colRef);
        var tableName = GetTableQualifier(colRef);
        return new SqlColumnExpression(new SqlColumnRef(tableName, columnName, null, false));
    }

    private static SqlBinaryExpression ConvertBinaryExpression(BinaryExpression binary)
    {
        var op = binary.BinaryExpressionType switch
        {
            BinaryExpressionType.Add => SqlBinaryOperator.Add,
            BinaryExpressionType.Subtract => SqlBinaryOperator.Subtract,
            BinaryExpressionType.Multiply => SqlBinaryOperator.Multiply,
            BinaryExpressionType.Divide => SqlBinaryOperator.Divide,
            BinaryExpressionType.Modulo => SqlBinaryOperator.Modulo,
            _ => SqlBinaryOperator.Add
        };

        var left = ConvertToSqlExpression(binary.FirstExpression);
        var right = ConvertToSqlExpression(binary.SecondExpression);
        return new SqlBinaryExpression(left, op, right);
    }

    private static SqlUnaryExpression ConvertUnaryExpression(UnaryExpression unary)
    {
        var op = unary.UnaryExpressionType switch
        {
            UnaryExpressionType.Negative => SqlUnaryOperator.Negate,
            UnaryExpressionType.Positive => SqlUnaryOperator.Negate,
            _ => SqlUnaryOperator.Negate
        };

        var operand = ConvertToSqlExpression(unary.Expression);
        return new SqlUnaryExpression(op, operand);
    }

    private static ISqlExpression ConvertFunctionCall(FunctionCall func)
    {
        var funcName = func.FunctionName.Value;
        var args = new List<ISqlExpression>();

        foreach (var param in func.Parameters)
        {
            if (param is ScalarExpression scalar)
            {
                args.Add(ConvertToSqlExpression(scalar));
            }
        }

        return new SqlFunctionExpression(funcName, args);
    }

    private static SqlCaseExpression ConvertSearchedCase(SearchedCaseExpression caseExpr)
    {
        var whenClauses = new List<SqlWhenClause>();

        foreach (var when in caseExpr.WhenClauses)
        {
            var condition = ConvertToSqlCondition(when.WhenExpression);
            var result = ConvertToSqlExpression(when.ThenExpression);

            if (condition != null)
            {
                whenClauses.Add(new SqlWhenClause(condition, result));
            }
        }

        var elseExpr = caseExpr.ElseExpression != null
            ? ConvertToSqlExpression(caseExpr.ElseExpression)
            : null;

        return new SqlCaseExpression(whenClauses, elseExpr);
    }

    private static SqlCaseExpression ConvertSimpleCase(SimpleCaseExpression simpleCase)
    {
        var inputExpr = ConvertToSqlExpression(simpleCase.InputExpression);
        var whenClauses = new List<SqlWhenClause>();

        foreach (var when in simpleCase.WhenClauses)
        {
            var whenValue = ConvertToSqlExpression(when.WhenExpression);
            var condition = new SqlExpressionCondition(inputExpr, SqlComparisonOperator.Equal, whenValue);
            var result = ConvertToSqlExpression(when.ThenExpression);
            whenClauses.Add(new SqlWhenClause(condition, result));
        }

        var elseExpr = simpleCase.ElseExpression != null
            ? ConvertToSqlExpression(simpleCase.ElseExpression)
            : null;

        return new SqlCaseExpression(whenClauses, elseExpr);
    }

    private static SqlIifExpression ConvertIif(IIfCall iif)
    {
        var condition = ConvertToSqlCondition(iif.Predicate);
        var trueValue = ConvertToSqlExpression(iif.ThenExpression);
        var falseValue = ConvertToSqlExpression(iif.ElseExpression);

        condition ??= new SqlComparisonCondition(
            SqlColumnRef.Simple("_dummy"),
            SqlComparisonOperator.Equal,
            SqlLiteral.Number("1"));

        return new SqlIifExpression(condition, trueValue, falseValue);
    }

    private static SqlCastExpression ConvertCast(CastCall cast)
    {
        var expr = ConvertToSqlExpression(cast.Parameter);
        var targetType = GetDataTypeName(cast.DataType);
        return new SqlCastExpression(expr, targetType);
    }

    private static SqlCastExpression ConvertConvert(ConvertCall convert)
    {
        var expr = ConvertToSqlExpression(convert.Parameter);
        var targetType = GetDataTypeName(convert.DataType);
        int? style = null;

        if (convert.Style is IntegerLiteral styleLit)
        {
            style = int.Parse(styleLit.Value);
        }

        return new SqlCastExpression(expr, targetType, style);
    }

    private static string GetDataTypeName(DataTypeReference dataType)
    {
        return dataType switch
        {
            SqlDataTypeReference sqlType => sqlType.SqlDataTypeOption.ToString().ToLowerInvariant(),
            _ => "varchar"
        };
    }

    private static SqlLiteral ConvertLiteral(Literal literal)
    {
        return literal switch
        {
            NullLiteral => SqlLiteral.Null(),
            StringLiteral str => SqlLiteral.String(str.Value),
            IntegerLiteral intLit => SqlLiteral.Number(intLit.Value),
            NumericLiteral numLit => SqlLiteral.Number(numLit.Value),
            RealLiteral realLit => SqlLiteral.Number(realLit.Value),
            _ => SqlLiteral.String(literal.Value ?? "")
        };
    }

    #endregion

    #region Helper Methods

    private static string GetEntityName(QuerySpecification querySpec)
    {
        if (querySpec.FromClause?.TableReferences.Count > 0)
        {
            var tableRef = querySpec.FromClause.TableReferences[0];
            return GetEntityNameFromTableRef(tableRef);
        }
        throw new InvalidOperationException("SELECT statement must have a FROM clause");
    }

    private static string GetEntityNameFromTableRef(TableReference tableRef)
    {
        return tableRef switch
        {
            NamedTableReference namedTable => GetTableName(namedTable),
            QualifiedJoin qualifiedJoin => GetEntityNameFromTableRef(qualifiedJoin.FirstTableReference),
            _ => throw new NotSupportedException($"Table reference type {tableRef.GetType().Name} not supported")
        };
    }

    private static string GetTableName(NamedTableReference namedTable)
    {
        return namedTable.SchemaObject.BaseIdentifier?.Value
            ?? namedTable.SchemaObject.Identifiers.Last().Value;
    }

    private static string GetColumnName(ColumnReferenceExpression colRef)
    {
        if (colRef.ColumnType == ColumnType.Wildcard)
        {
            return "*";
        }
        return colRef.MultiPartIdentifier.Identifiers.Last().Value;
    }

    private static string? GetTableQualifier(ColumnReferenceExpression colRef)
    {
        var identifiers = colRef.MultiPartIdentifier.Identifiers;
        if (identifiers.Count >= 2)
        {
            return identifiers[^2].Value;
        }
        return null;
    }

    private static int? GetTopValue(QuerySpecification querySpec)
    {
        if (querySpec.TopRowFilter?.Expression is IntegerLiteral intLiteral)
        {
            return int.Parse(intLiteral.Value);
        }
        return null;
    }

    private static bool HasAggregates(IList<SelectElement> selectElements)
    {
        foreach (var element in selectElements)
        {
            if (element is SelectScalarExpression scalar &&
                scalar.Expression is FunctionCall func &&
                IsAggregateFunction(func))
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsAggregateFunction(FunctionCall func)
    {
        var name = func.FunctionName.Value.ToUpperInvariant();
        return name is "COUNT" or "SUM" or "AVG" or "MIN" or "MAX";
    }

    private static List<string> ExtractSelectColumnNames(QueryExpression queryExpr)
    {
        var names = new List<string>();

        if (queryExpr is QuerySpecification querySpec)
        {
            foreach (var element in querySpec.SelectElements)
            {
                if (element is SelectScalarExpression scalar)
                {
                    if (scalar.ColumnName != null)
                    {
                        names.Add(scalar.ColumnName.Value);
                    }
                    else if (scalar.Expression is ColumnReferenceExpression colRef)
                    {
                        names.Add(GetColumnName(colRef));
                    }
                    else if (scalar.Expression is FunctionCall func)
                    {
                        names.Add(func.FunctionName.Value.ToLowerInvariant());
                    }
                    else
                    {
                        names.Add("computed");
                    }
                }
            }
        }

        return names;
    }

    private static void CollectReferencedColumns(ScalarExpression expression, HashSet<string> columns)
    {
        switch (expression)
        {
            case ColumnReferenceExpression colRef:
                columns.Add(GetColumnName(colRef));
                break;

            case BinaryExpression binary:
                CollectReferencedColumns(binary.FirstExpression, columns);
                CollectReferencedColumns(binary.SecondExpression, columns);
                break;

            case UnaryExpression unary:
                CollectReferencedColumns(unary.Expression, columns);
                break;

            case FunctionCall func:
                foreach (var param in func.Parameters)
                {
                    if (param is ScalarExpression scalar)
                    {
                        CollectReferencedColumns(scalar, columns);
                    }
                }
                break;

            case ParenthesisExpression paren:
                CollectReferencedColumns(paren.Expression, columns);
                break;
        }
    }

    /// <summary>
    /// Creates a ColumnReferenceExpression for a simple column name.
    /// </summary>
    private static ColumnReferenceExpression CreateColumnReference(string columnName)
    {
        var colRef = new ColumnReferenceExpression
        {
            ColumnType = ColumnType.Regular,
            MultiPartIdentifier = new MultiPartIdentifier()
        };
        colRef.MultiPartIdentifier.Identifiers.Add(new Identifier { Value = columnName });
        return colRef;
    }

    #endregion
}
