using PPDS.Dataverse.Query;
using PPDS.Dataverse.Sql.Parsing;
using PPDS.Dataverse.Sql.Transpilation;

namespace PPDS.Cli.Services.Query;

/// <summary>
/// Implementation of <see cref="ISqlQueryService"/> that parses SQL,
/// transpiles to FetchXML, and executes against Dataverse.
/// </summary>
public sealed class SqlQueryService : ISqlQueryService
{
    private readonly IQueryExecutor _queryExecutor;

    /// <summary>
    /// Creates a new instance of <see cref="SqlQueryService"/>.
    /// </summary>
    /// <param name="queryExecutor">The query executor for FetchXML execution.</param>
    public SqlQueryService(IQueryExecutor queryExecutor)
    {
        _queryExecutor = queryExecutor ?? throw new ArgumentNullException(nameof(queryExecutor));
    }

    /// <inheritdoc />
    public string TranspileSql(string sql, int? topOverride = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);

        var parser = new SqlParser(sql);
        var ast = parser.Parse();

        if (topOverride.HasValue)
        {
            ast = ast.WithTop(topOverride.Value);
        }

        var transpiler = new SqlToFetchXmlTranspiler();
        return transpiler.Transpile(ast);
    }

    /// <inheritdoc />
    public async Task<SqlQueryResult> ExecuteAsync(
        SqlQueryRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Sql);

        // Parse and transpile with virtual column detection
        var parser = new SqlParser(request.Sql);
        var ast = parser.Parse();

        if (request.TopOverride.HasValue)
        {
            ast = ast.WithTop(request.TopOverride.Value);
        }

        var transpiler = new SqlToFetchXmlTranspiler();
        var transpileResult = transpiler.TranspileWithVirtualColumns(ast);

        var result = await _queryExecutor.ExecuteFetchXmlAsync(
            transpileResult.FetchXml,
            request.PageNumber,
            request.PagingCookie,
            request.IncludeCount,
            cancellationToken);

        // Expand lookup, optionset, and boolean columns to include *name variants
        // Pass virtual column info so we can handle explicitly queried *name columns
        var expandedResult = SqlQueryResultExpander.ExpandFormattedValueColumns(
            result,
            transpileResult.VirtualColumns);

        return new SqlQueryResult
        {
            OriginalSql = request.Sql,
            TranspiledFetchXml = transpileResult.FetchXml,
            Result = expandedResult
        };
    }
}
