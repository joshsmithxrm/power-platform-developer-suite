using System;
using System.Collections.Generic;
using PPDS.Dataverse.Query.Planning.Nodes;
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
        if (statement is not SqlSelectStatement selectStatement)
        {
            throw new SqlParseException("Only SELECT statements are currently supported.");
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

        // Phase 0: transpile to FetchXML and create a simple scan node.
        //
        // NOTE: Virtual column expansion (e.g., owneridname from FormattedValues) stays
        // in the service layer (SqlQueryResultExpander) rather than in ProjectNode, because
        // it depends on SDK-specific FormattedValues metadata from the Entity objects.
        // The generic QueryRow format does not carry FormattedValues, so expansion must
        // happen after the plan produces a QueryResult. See SqlQueryService.ExecuteAsync.
        var transpileResult = _transpiler.TranspileWithVirtualColumns(statement);

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

        // HAVING clause: add client-side filter after aggregate FetchXML scan.
        // FetchXML doesn't support HAVING natively, so we filter client-side.
        if (statement.Having != null)
        {
            rootNode = new ClientFilterNode(rootNode, statement.Having);
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
