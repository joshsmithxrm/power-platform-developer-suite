using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using Moq;
using PPDS.Cli.Services.Query;
using PPDS.Cli.Tests.Services.Shared;
using PPDS.Dataverse.Client;
using PPDS.Dataverse.Pooling;
using PPDS.Dataverse.Query;

namespace PPDS.Cli.Tests.Services.Query.Faithful;

/// <summary>
/// An offline Dataverse for SELECT tests. It answers FetchXML the way Dataverse does (see <see cref="FetchXmlEvaluator"/>
/// for the rules and where each was observed): the filter selects the rows, <c>top</c>/<c>count</c> limit them, ORDER BY
/// orders them, and each attribute comes back under its alias. It sits behind the product's own
/// <see cref="QueryExecutor"/>, so the engine's real record mapping runs too.
/// <para>
/// There is no mode that returns every row: a test that drops a condition, a TOP or an alias gets different rows or keys
/// and fails. FetchXML the fake cannot evaluate faithfully throws <see cref="NotSupportedException"/> rather than
/// returning a guess.
/// </para>
/// </summary>
internal sealed class FakeDataverse
{
    private readonly Dictionary<string, FakeTable> _tables = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _fetchXml = new();
    private readonly object _gate = new();

    /// <summary>Every FetchXML request Dataverse received, in order (after <see cref="Rewrite"/> and the executor's paging).</summary>
    public IReadOnlyList<string> FetchXml
    {
        get { lock (_gate) return _fetchXml.ToList(); }
    }

    /// <summary>
    /// Optional change applied to the FetchXML the engine produced, before the product's <see cref="QueryExecutor"/> sees
    /// it: it stands in for an engine that drops a condition, a TOP or an alias. Used only by the tests that prove such an
    /// engine fails the faithful tests.
    /// </summary>
    public Func<string, string>? Rewrite { get; set; }

    /// <summary>
    /// The caller's offset from UTC, used to read date-time values written without an offset. The live audit ran at
    /// UTC−6 and observed that Dataverse reads such values in the caller's time zone.
    /// </summary>
    public TimeSpan CallerUtcOffset { get; set; } = TimeSpan.FromHours(-6);

    public FakeDataverse Add(FakeTable table)
    {
        _tables[table.LogicalName] = table;
        return this;
    }

    /// <summary>Evaluates one FetchXML request like Dataverse.</summary>
    public EntityCollection RetrieveMultiple(string fetchXml)
    {
        lock (_gate) _fetchXml.Add(fetchXml);
        return new FetchXmlEvaluator(_tables, CallerUtcOffset).Evaluate(fetchXml);
    }

    /// <summary>A connection pool whose clients answer from this fake.</summary>
    public IDataverseConnectionPool CreatePool()
    {
        var pool = new Mock<IDataverseConnectionPool>(MockBehavior.Strict);
        pool.Setup(p => p.GetClientAsync(It.IsAny<DataverseClientOptions?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => CreateClient());
        return pool.Object;
    }

    /// <summary>The product's <see cref="QueryExecutor"/> over this fake.</summary>
    public IQueryExecutor CreateQueryExecutor() => new RewritingExecutor(new QueryExecutor(CreatePool()), this);

    /// <summary>A <see cref="SqlQueryService"/> over this fake, as the CLI builds it for a read.</summary>
    public SqlQueryService CreateSqlQueryService() => new(CreateQueryExecutor(), guard: new InactiveFakeShakedownGuard());

    private IPooledClient CreateClient()
    {
        var client = new Mock<IPooledClient>(MockBehavior.Strict);
        client.Setup(c => c.RetrieveMultipleAsync(It.IsAny<QueryBase>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((QueryBase q, CancellationToken _) => RetrieveMultiple(FetchOf(q)));
        client.Setup(c => c.RetrieveMultipleAsync(It.IsAny<QueryBase>()))
            .ReturnsAsync((QueryBase q) => RetrieveMultiple(FetchOf(q)));
        client.Setup(c => c.ExecuteAsync(It.IsAny<OrganizationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((OrganizationRequest r, CancellationToken _) => Execute(r));
        client.Setup(c => c.DisposeAsync()).Returns(ValueTask.CompletedTask);
        client.Setup(c => c.Dispose());
        return client.Object;
    }

    private OrganizationResponse Execute(OrganizationRequest request)
    {
        switch (request)
        {
            case RetrieveMultipleRequest rm:
                var response = new RetrieveMultipleResponse();
                response.Results["EntityCollection"] = RetrieveMultiple(FetchOf(rm.Query));
                return response;
            case RetrieveTotalRecordCountRequest count:
                var counts = new EntityRecordCountCollection();
                foreach (var name in count.EntityNames)
                {
                    if (_tables.TryGetValue(name, out var table))
                        counts[name] = table.Rows.Count;
                }
                var countResponse = new RetrieveTotalRecordCountResponse();
                countResponse.Results["EntityRecordCountCollection"] = counts;
                return countResponse;
            default:
                throw new NotSupportedException($"Fake Dataverse: request {request.RequestName} is not modelled.");
        }
    }

    /// <summary>Applies <see cref="Rewrite"/> to the engine's FetchXML, then runs the product's executor.</summary>
    private sealed class RewritingExecutor(IQueryExecutor inner, FakeDataverse owner) : IQueryExecutor
    {
        private string R(string fetchXml) => owner.Rewrite?.Invoke(fetchXml) ?? fetchXml;

        public Task<QueryResult> ExecuteFetchXmlAsync(string fetchXml, int? pageNumber = null, string? pagingCookie = null,
            bool includeCount = false, CancellationToken cancellationToken = default)
            => inner.ExecuteFetchXmlAsync(R(fetchXml), pageNumber, pagingCookie, includeCount, cancellationToken);

        public Task<QueryResult> ExecuteFetchXmlAsync(string fetchXml, int? pageNumber, string? pagingCookie, bool includeCount,
            QueryExecutionOptions? executionOptions, CancellationToken cancellationToken = default)
            => inner.ExecuteFetchXmlAsync(R(fetchXml), pageNumber, pagingCookie, includeCount, executionOptions, cancellationToken);

        public Task<QueryResult> ExecuteFetchXmlAllPagesAsync(string fetchXml, int maxRecords = 5000, CancellationToken cancellationToken = default)
            => inner.ExecuteFetchXmlAllPagesAsync(R(fetchXml), maxRecords, cancellationToken);

        public Task<long?> GetTotalRecordCountAsync(string entityLogicalName, CancellationToken cancellationToken = default)
            => inner.GetTotalRecordCountAsync(entityLogicalName, cancellationToken);

        public Task<(DateTime? Min, DateTime? Max)> GetMinMaxCreatedOnAsync(string entityLogicalName, CancellationToken cancellationToken = default)
            => inner.GetMinMaxCreatedOnAsync(entityLogicalName, cancellationToken);
    }

    private static string FetchOf(QueryBase query) => query is FetchExpression fetch
        ? fetch.Query
        : throw new NotSupportedException($"Fake Dataverse: only FetchXML queries are modelled, not {query.GetType().Name}.");
}
