using System.Runtime.CompilerServices;
using PPDS.Cli.Services.Query;
using PPDS.Dataverse.Query;
using PPDS.Dataverse.Query.Planning;

namespace PPDS.Cli.Tests.Mocks;

/// <summary>
/// Fake implementation of <see cref="ISqlQueryService"/> for testing.
/// </summary>
public sealed class FakeSqlQueryService : ISqlQueryService
{
    private readonly List<SqlQueryRequest> _executedQueries = new();

    /// <summary>
    /// Gets all queries that were executed.
    /// </summary>
    public IReadOnlyList<SqlQueryRequest> ExecutedQueries => _executedQueries;

    /// <summary>
    /// Gets or sets the result to return from ExecuteAsync.
    /// </summary>
    public SqlQueryResult NextResult { get; set; } = CreateEmptyResult();

    /// <summary>
    /// Gets or sets the FetchXML to return from TranspileSql.
    /// </summary>
    public string NextFetchXml { get; set; } = "<fetch><entity name='account'/></fetch>";

    /// <summary>
    /// Gets or sets an exception to throw from ExecuteAsync.
    /// </summary>
    public Exception? ExceptionToThrow { get; set; }

    /// <summary>
    /// Gets or sets the plan description to return from ExplainAsync.
    /// When null, a default mock plan is returned.
    /// </summary>
    public QueryPlanDescription? NextExplainResult { get; set; }

    /// <inheritdoc />
    public string TranspileSql(string sql, int? topOverride = null)
    {
        if (ExceptionToThrow != null)
        {
            throw ExceptionToThrow;
        }

        return NextFetchXml;
    }

    /// <inheritdoc />
    public Task<SqlQueryResult> ExecuteAsync(SqlQueryRequest request, CancellationToken cancellationToken = default)
    {
        _executedQueries.Add(request);

        if (ExceptionToThrow != null)
        {
            throw ExceptionToThrow;
        }

        return Task.FromResult(NextResult);
    }

    /// <inheritdoc />
    public Task<QueryPlanDescription> ExplainAsync(string sql, CancellationToken cancellationToken = default)
    {
        if (ExceptionToThrow != null)
        {
            throw ExceptionToThrow;
        }

        var plan = NextExplainResult ?? new QueryPlanDescription
        {
            NodeType = "FetchXmlScanNode",
            Description = "Mock plan"
        };

        return Task.FromResult(plan);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<SqlQueryStreamChunk> ExecuteStreamingAsync(
        SqlQueryRequest request,
        int chunkSize = 100,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        _executedQueries.Add(request);

        if (ExceptionToThrow != null)
        {
            throw ExceptionToThrow;
        }

        // Yield a single chunk with all rows from NextResult
        yield return new SqlQueryStreamChunk
        {
            Rows = NextResult.Result.Records,
            Columns = NextResult.Result.Columns,
            EntityLogicalName = NextResult.Result.EntityLogicalName,
            TotalRowsSoFar = NextResult.Result.Count,
            IsComplete = true,
            TranspiledFetchXml = NextResult.TranspiledFetchXml
        };

        await Task.CompletedTask;
    }

    /// <summary>
    /// Resets the fake service state.
    /// </summary>
    public void Reset()
    {
        _executedQueries.Clear();
        NextResult = CreateEmptyResult();
        NextFetchXml = "<fetch><entity name='account'/></fetch>";
        ExceptionToThrow = null;
        NextExplainResult = null;
    }

    private static SqlQueryResult CreateEmptyResult()
    {
        return new SqlQueryResult
        {
            OriginalSql = "SELECT * FROM account",
            TranspiledFetchXml = "<fetch><entity name='account'/></fetch>",
            Result = QueryResult.Empty("account")
        };
    }
}
