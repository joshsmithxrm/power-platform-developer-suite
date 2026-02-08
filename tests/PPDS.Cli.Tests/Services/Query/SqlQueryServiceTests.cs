using Moq;
using PPDS.Cli.Services.Query;
using PPDS.Dataverse.Query;
using PPDS.Dataverse.Sql.Parsing;
using Xunit;

namespace PPDS.Cli.Tests.Services.Query;

/// <summary>
/// Unit tests for <see cref="SqlQueryService"/>.
/// </summary>
public class SqlQueryServiceTests
{
    private readonly Mock<IQueryExecutor> _mockQueryExecutor;
    private readonly SqlQueryService _service;

    public SqlQueryServiceTests()
    {
        _mockQueryExecutor = new Mock<IQueryExecutor>();
        _service = new SqlQueryService(_mockQueryExecutor.Object);
    }

    #region Constructor Tests

    [Fact]
    public void Constructor_WithNullQueryExecutor_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new SqlQueryService(null!));
    }

    #endregion

    #region TranspileSql Tests

    [Fact]
    public void TranspileSql_WithValidSql_ReturnsFetchXml()
    {
        var sql = "SELECT name FROM account";

        var fetchXml = _service.TranspileSql(sql);

        Assert.NotNull(fetchXml);
        Assert.Contains("<fetch", fetchXml);
        Assert.Contains("account", fetchXml);
        Assert.Contains("name", fetchXml);
    }

    [Fact]
    public void TranspileSql_WithTopOverride_AppliesTop()
    {
        var sql = "SELECT name FROM account";

        var fetchXml = _service.TranspileSql(sql, topOverride: 5);

        Assert.Contains("top=\"5\"", fetchXml);
    }

    [Fact]
    public void TranspileSql_WithTopInSql_UsesOriginalTop()
    {
        var sql = "SELECT TOP 10 name FROM account";

        var fetchXml = _service.TranspileSql(sql);

        Assert.Contains("top=\"10\"", fetchXml);
    }

    [Fact]
    public void TranspileSql_WithTopOverrideAndTopInSql_OverridesTop()
    {
        var sql = "SELECT TOP 10 name FROM account";

        var fetchXml = _service.TranspileSql(sql, topOverride: 5);

        Assert.Contains("top=\"5\"", fetchXml);
        Assert.DoesNotContain("top=\"10\"", fetchXml);
    }

    [Fact]
    public void TranspileSql_WithNullSql_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => _service.TranspileSql(null!));
    }

    [Fact]
    public void TranspileSql_WithEmptySql_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => _service.TranspileSql(""));
    }

    [Fact]
    public void TranspileSql_WithWhitespaceSql_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => _service.TranspileSql("   "));
    }

    [Fact]
    public void TranspileSql_WithInvalidSql_ThrowsSqlParseException()
    {
        var invalidSql = "NOT VALID SQL AT ALL";

        Assert.Throws<SqlParseException>(() => _service.TranspileSql(invalidSql));
    }

    [Fact]
    public void TranspileSql_WithSelectStar_ReturnsAllAttributes()
    {
        var sql = "SELECT * FROM account";

        var fetchXml = _service.TranspileSql(sql);

        Assert.Contains("<all-attributes", fetchXml);
    }

    [Fact]
    public void TranspileSql_WithWhereClause_IncludesFilter()
    {
        var sql = "SELECT name FROM account WHERE statecode = 0";

        var fetchXml = _service.TranspileSql(sql);

        Assert.Contains("<filter", fetchXml);
        Assert.Contains("statecode", fetchXml);
    }

    #endregion

    #region ExecuteAsync Tests

    [Fact]
    public async Task ExecuteAsync_WithValidRequest_ReturnsResult()
    {
        var request = new SqlQueryRequest { Sql = "SELECT name FROM account" };
        var expectedResult = new QueryResult
        {
            EntityLogicalName = "account",
            Columns = new List<QueryColumn> { new() { LogicalName = "name" } },
            Records = new List<IReadOnlyDictionary<string, QueryValue>>(),
            Count = 0
        };

        _mockQueryExecutor
            .Setup(x => x.ExecuteFetchXmlAsync(
                It.IsAny<string>(),
                It.IsAny<int?>(),
                It.IsAny<string?>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedResult);

        var result = await _service.ExecuteAsync(request);

        Assert.NotNull(result);
        Assert.Equal(request.Sql, result.OriginalSql);
        Assert.NotNull(result.TranspiledFetchXml);
        Assert.Equal("account", result.Result.EntityLogicalName);
        Assert.Equal(0, result.Result.Count);
        Assert.Empty(result.Result.Records);
    }

    [Fact]
    public async Task ExecuteAsync_PassesRequestParametersToExecutor()
    {
        var request = new SqlQueryRequest
        {
            Sql = "SELECT name FROM account",
            PageNumber = 2,
            PagingCookie = "test-cookie",
            IncludeCount = true
        };

        var expectedResult = new QueryResult
        {
            EntityLogicalName = "account",
            Columns = new List<QueryColumn>(),
            Records = new List<IReadOnlyDictionary<string, QueryValue>>(),
            Count = 0
        };

        _mockQueryExecutor
            .Setup(x => x.ExecuteFetchXmlAsync(
                It.IsAny<string>(),
                2,
                "test-cookie",
                true,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedResult);

        await _service.ExecuteAsync(request);

        _mockQueryExecutor.Verify(x => x.ExecuteFetchXmlAsync(
            It.IsAny<string>(),
            2,
            "test-cookie",
            true,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_WithTopOverride_AppliesTopToFetchXml()
    {
        var request = new SqlQueryRequest
        {
            Sql = "SELECT name FROM account",
            TopOverride = 5
        };

        string? capturedFetchXml = null;
        _mockQueryExecutor
            .Setup(x => x.ExecuteFetchXmlAsync(
                It.IsAny<string>(),
                It.IsAny<int?>(),
                It.IsAny<string?>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, int?, string?, bool, CancellationToken>((fx, _, _, _, _) => capturedFetchXml = fx)
            .ReturnsAsync(new QueryResult
            {
                EntityLogicalName = "account",
                Columns = new List<QueryColumn>(),
                Records = new List<IReadOnlyDictionary<string, QueryValue>>(),
                Count = 0
            });

        await _service.ExecuteAsync(request);

        Assert.NotNull(capturedFetchXml);
        Assert.Contains("top=\"5\"", capturedFetchXml);
    }

    [Fact]
    public async Task ExecuteAsync_WithNullRequest_ThrowsArgumentNullException()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => _service.ExecuteAsync(null!));
    }

    [Fact]
    public async Task ExecuteAsync_WithInvalidSql_ThrowsSqlParseException()
    {
        var request = new SqlQueryRequest { Sql = "INVALID SQL" };

        await Assert.ThrowsAsync<SqlParseException>(() => _service.ExecuteAsync(request));
    }

    [Fact]
    public async Task ExecuteAsync_RespectsCancellationToken()
    {
        var request = new SqlQueryRequest { Sql = "SELECT name FROM account" };
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        _mockQueryExecutor
            .Setup(x => x.ExecuteFetchXmlAsync(
                It.IsAny<string>(),
                It.IsAny<int?>(),
                It.IsAny<string?>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => _service.ExecuteAsync(request, cts.Token));
    }

    #endregion
}
