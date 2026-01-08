using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using PPDS.Dataverse.Query;
using PPDS.Mcp.Infrastructure;

namespace PPDS.Mcp.Tools;

/// <summary>
/// MCP tool that executes FetchXML queries against Dataverse.
/// </summary>
[McpServerToolType]
public sealed class QueryFetchTool
{
    private readonly McpToolContext _context;

    /// <summary>
    /// Initializes a new instance of the <see cref="QueryFetchTool"/> class.
    /// </summary>
    /// <param name="context">The MCP tool context.</param>
    public QueryFetchTool(McpToolContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    /// <summary>
    /// Executes a FetchXML query against Dataverse.
    /// </summary>
    /// <param name="fetchXml">FetchXML query to execute.</param>
    /// <param name="maxRows">Maximum number of rows to return (default 100).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Query results with columns and records.</returns>
    [McpServerTool(Name = "ppds_query_fetch")]
    [Description("Execute a FetchXML query against Dataverse. Use this when you have raw FetchXML or need advanced query features not available in SQL. Prefer ppds_query_sql for simpler queries.")]
    public async Task<QueryResult> ExecuteAsync(
        [Description("FetchXML query string")]
        string fetchXml,
        [Description("Maximum rows to return (default 100, max 5000)")]
        int maxRows = 100,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(fetchXml))
        {
            throw new ArgumentException("The 'fetchXml' parameter is required.", nameof(fetchXml));
        }

        // Cap maxRows to prevent runaway queries.
        maxRows = Math.Clamp(maxRows, 1, 5000);

        // Inject top attribute if not already present.
        var query = InjectTopAttribute(fetchXml, maxRows);

        // Execute query.
        await using var serviceProvider = await _context.CreateServiceProviderAsync(cancellationToken).ConfigureAwait(false);
        var queryExecutor = serviceProvider.GetRequiredService<IQueryExecutor>();

        var result = await queryExecutor.ExecuteFetchXmlAsync(
            query,
            pageNumber: null,
            pagingCookie: null,
            includeCount: false,
            cancellationToken).ConfigureAwait(false);

        return QueryResultMapper.MapToResult(result, query);
    }

    private static string InjectTopAttribute(string fetchXml, int top)
    {
        var fetchIndex = fetchXml.IndexOf("<fetch", StringComparison.OrdinalIgnoreCase);
        if (fetchIndex < 0) return fetchXml;

        var endOfFetch = fetchXml.IndexOf('>', fetchIndex);
        if (endOfFetch < 0) return fetchXml;

        var fetchElement = fetchXml.Substring(fetchIndex, endOfFetch - fetchIndex);

        if (fetchElement.Contains("top=", StringComparison.OrdinalIgnoreCase))
        {
            return fetchXml; // Already has top, don't override.
        }

        var insertPoint = fetchIndex + "<fetch".Length;
        return fetchXml.Substring(0, insertPoint) + $" top=\"{top}\"" + fetchXml.Substring(insertPoint);
    }
}
