using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using PPDS.Dataverse.Query;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using PPDS.Query.Parsing;
using PPDS.Query.Transpilation;
using PPDS.Mcp.Infrastructure;

namespace PPDS.Mcp.Tools;

/// <summary>
/// MCP tool that executes SQL queries against Dataverse.
/// </summary>
[McpServerToolType]
public sealed class QuerySqlTool
{
    private readonly McpToolContext _context;

    /// <summary>
    /// Initializes a new instance of the <see cref="QuerySqlTool"/> class.
    /// </summary>
    /// <param name="context">The MCP tool context.</param>
    public QuerySqlTool(McpToolContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    /// <summary>
    /// Executes a SQL query against Dataverse.
    /// </summary>
    /// <param name="sql">SQL SELECT statement to execute.</param>
    /// <param name="maxRows">Maximum number of rows to return (default 100).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Query results with columns and records.</returns>
    [McpServerTool(Name = "ppds_query_sql")]
    [Description("Execute a SQL SELECT query against Dataverse. The SQL is transpiled to FetchXML internally. Supports JOINs, WHERE, ORDER BY, TOP, and aggregate functions. Example: SELECT name, revenue FROM account WHERE statecode = 0 ORDER BY revenue DESC")]
    public async Task<QueryResult> ExecuteAsync(
        [Description("SQL SELECT statement (e.g., 'SELECT name, revenue FROM account WHERE statecode = 0')")]
        string sql,
        [Description("Maximum rows to return (default 100, max 5000)")]
        int maxRows = 100,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            throw new ArgumentException("The 'sql' parameter is required.", nameof(sql));
        }

        // Cap maxRows to prevent runaway queries.
        maxRows = Math.Clamp(maxRows, 1, 5000);

        // Parse and transpile SQL to FetchXML.
        TSqlStatement stmt;
        try
        {
            var parser = new QueryParser();
            stmt = parser.ParseStatement(sql);
        }
        catch (QueryParseException ex)
        {
            throw new InvalidOperationException($"SQL parse error: {ex.Message}", ex);
        }

        // Apply row limit.
        if (stmt is SelectStatement selectStmt
            && selectStmt.QueryExpression is QuerySpecification querySpec)
        {
            querySpec.TopRowFilter = new TopRowFilter
            {
                Expression = new IntegerLiteral { Value = maxRows.ToString() }
            };
        }

        var generator = new FetchXmlGenerator();
        var fetchXml = generator.Generate(stmt);

        // Execute query.
        await using var serviceProvider = await _context.CreateServiceProviderAsync(cancellationToken).ConfigureAwait(false);
        var queryExecutor = serviceProvider.GetRequiredService<IQueryExecutor>();

        var result = await queryExecutor.ExecuteFetchXmlAsync(
            fetchXml,
            pageNumber: null,
            pagingCookie: null,
            includeCount: false,
            cancellationToken).ConfigureAwait(false);

        return QueryResultMapper.MapToResult(result, fetchXml);
    }
}
