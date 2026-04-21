using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using PPDS.Dataverse.Query;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using PPDS.Query.Parsing;
using PPDS.Query.Transpilation;
using PPDS.Mcp.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;

namespace PPDS.Mcp.Tools;

/// <summary>
/// MCP tool that executes SQL queries against Dataverse.
/// </summary>
[McpServerToolType]
public sealed class QuerySqlTool : McpToolBase
{
    /// <summary>
    /// Initializes a new instance of the <see cref="QuerySqlTool"/> class.
    /// </summary>
    /// <param name="context">The MCP tool context.</param>
    public QuerySqlTool(McpToolContext context) : base(context) { }

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
        try
        {
            // Cap maxRows to prevent runaway queries.
            maxRows = Math.Clamp(maxRows, 1, 5000);

            // Validate required params and create service provider.
            await using var serviceProvider = await CreateScopeAsync(cancellationToken, (nameof(sql), sql)).ConfigureAwait(false);

            // Parse and transpile SQL to FetchXML.
            var parser = new QueryParser();
            var stmt = parser.ParseStatement(sql);

            // Block DML operations in read-only sessions.
            if (Context.IsReadOnly && stmt is not SelectStatement)
            {
                throw new InvalidOperationException(
                    "DML operations (INSERT, UPDATE, DELETE) are disabled. This MCP session was started with --read-only.");
            }

            // Apply row limit only if the user hasn't already specified a TOP clause.
            if (stmt is SelectStatement selectStmt
                && selectStmt.QueryExpression is QuerySpecification querySpec
                && querySpec.TopRowFilter == null)
            {
                querySpec.TopRowFilter = new TopRowFilter
                {
                    Expression = new IntegerLiteral { Value = maxRows.ToString() }
                };
            }

            var generator = new FetchXmlGenerator();
            var fetchXml = generator.Generate(stmt);

            var queryExecutor = serviceProvider.GetRequiredService<IQueryExecutor>();

            var result = await queryExecutor.ExecuteFetchXmlAsync(
                fetchXml,
                pageNumber: null,
                pagingCookie: null,
                includeCount: false,
                cancellationToken).ConfigureAwait(false);

            return QueryResultMapper.MapToResult(result, fetchXml);
        }
        catch (PpdsException ex)
        {
            McpToolErrorHelper.ThrowStructuredError(ex);
            throw; // unreachable — ThrowStructuredError always throws
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not ArgumentException)
        {
            McpToolErrorHelper.ThrowStructuredError(ex);
            throw; // unreachable — ThrowStructuredError always throws
        }
    }
}
