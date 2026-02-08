using PPDS.Cli.Infrastructure.Errors;
using PPDS.Dataverse.Query;
using PPDS.Dataverse.Query.Execution;
using PPDS.Dataverse.Query.Planning;
using PPDS.Dataverse.Sql.Ast;
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
    private readonly ITdsQueryExecutor? _tdsQueryExecutor;
    private readonly QueryPlanner _planner;
    private readonly PlanExecutor _planExecutor;
    private readonly ExpressionEvaluator _expressionEvaluator = new();
    private readonly DmlSafetyGuard _dmlSafetyGuard = new();

    /// <summary>
    /// Creates a new instance of <see cref="SqlQueryService"/>.
    /// </summary>
    /// <param name="queryExecutor">The query executor for FetchXML execution.</param>
    /// <param name="tdsQueryExecutor">Optional TDS Endpoint executor for direct SQL execution.</param>
    public SqlQueryService(IQueryExecutor queryExecutor, ITdsQueryExecutor? tdsQueryExecutor = null)
    {
        _queryExecutor = queryExecutor ?? throw new ArgumentNullException(nameof(queryExecutor));
        _tdsQueryExecutor = tdsQueryExecutor;
        _planner = new QueryPlanner();
        _planExecutor = new PlanExecutor();
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

        // Parse SQL into AST
        var parser = new SqlParser(request.Sql);
        var statement = parser.ParseStatement();

        // Apply TopOverride if the statement is a SELECT
        if (request.TopOverride.HasValue && statement is SqlSelectStatement selectStmt)
        {
            statement = selectStmt.WithTop(request.TopOverride.Value);
        }

        // DML safety check: validate DELETE/UPDATE/INSERT before execution.
        // When DmlSafety options are provided, the guard blocks unsafe operations
        // (DELETE/UPDATE without WHERE) and enforces row caps.
        if (request.DmlSafety != null)
        {
            var safetyResult = _dmlSafetyGuard.Check(statement, request.DmlSafety);

            if (safetyResult.IsBlocked)
            {
                throw new PpdsException(
                    safetyResult.ErrorCode ?? ErrorCodes.Query.DmlBlocked,
                    safetyResult.BlockReason ?? "DML operation blocked by safety guard.");
            }

            if (safetyResult.IsDryRun)
            {
                // Dry run: return the safety result without executing
                return new SqlQueryResult
                {
                    OriginalSql = request.Sql,
                    TranspiledFetchXml = null,
                    Result = QueryResult.Empty("dry-run"),
                    DmlSafetyResult = safetyResult
                };
            }

            if (safetyResult.RequiresConfirmation)
            {
                throw new PpdsException(
                    ErrorCodes.Query.DmlBlocked,
                    "DML operations require --confirm to execute. Use --dry-run to preview the operation.");
            }
        }

        // Build execution plan via QueryPlanner
        var planOptions = new QueryPlanOptions
        {
            MaxRows = request.TopOverride,
            PageNumber = request.PageNumber,
            PagingCookie = request.PagingCookie,
            IncludeCount = request.IncludeCount,
            UseTdsEndpoint = request.UseTdsEndpoint,
            OriginalSql = request.Sql,
            TdsQueryExecutor = _tdsQueryExecutor
        };

        var planResult = _planner.Plan(statement, planOptions);

        // Execute the plan
        var context = new QueryPlanContext(
            _queryExecutor,
            _expressionEvaluator,
            cancellationToken);

        var result = await _planExecutor.ExecuteAsync(planResult, context, cancellationToken);

        // Expand lookup, optionset, and boolean columns to include *name variants.
        // Virtual column expansion stays in the service layer because it depends on
        // SDK-specific FormattedValues metadata from the Entity objects.
        var expandedResult = SqlQueryResultExpander.ExpandFormattedValueColumns(
            result,
            planResult.VirtualColumns);

        return new SqlQueryResult
        {
            OriginalSql = request.Sql,
            TranspiledFetchXml = planResult.FetchXml,
            Result = expandedResult
        };
    }

    /// <inheritdoc />
    public Task<QueryPlanDescription> ExplainAsync(string sql, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);

        var parser = new SqlParser(sql);
        var statement = parser.ParseStatement();

        var planResult = _planner.Plan(statement);
        var description = QueryPlanDescription.FromNode(planResult.RootNode);

        return Task.FromResult(description);
    }
}
