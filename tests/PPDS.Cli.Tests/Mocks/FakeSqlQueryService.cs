using PPDS.Cli.Services.Query;
using PPDS.Dataverse.Query;

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

    /// <summary>
    /// Resets the fake service state.
    /// </summary>
    public void Reset()
    {
        _executedQueries.Clear();
        NextResult = CreateEmptyResult();
        NextFetchXml = "<fetch><entity name='account'/></fetch>";
        ExceptionToThrow = null;
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
