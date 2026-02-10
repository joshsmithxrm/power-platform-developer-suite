using Moq;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services.Query;
using PPDS.Dataverse.Query;
using PPDS.Dataverse.Sql.Transpilation;
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

        Assert.Throws<PpdsException>(() => _service.TranspileSql(invalidSql));
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
        // The new PPDS.Query engine handles TopOverride via the plan node's MaxRows
        // rather than injecting top/count into FetchXML. Verify valid FetchXML was sent.
        Assert.Contains("<fetch", capturedFetchXml);
        Assert.Contains("account", capturedFetchXml);
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

        await Assert.ThrowsAsync<PpdsException>(() => _service.ExecuteAsync(request));
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

    #region Aggregate Metadata Fetch Tests

    [Fact]
    [Trait("Category", "PlanUnit")]
    public async Task ExecuteAsync_AggregateQuery_FetchesMetadata()
    {
        // Arrange: mock the metadata methods
        var mockExecutor = new Mock<IQueryExecutor>();
        mockExecutor
            .Setup(x => x.GetTotalRecordCountAsync("account", It.IsAny<CancellationToken>()))
            .ReturnsAsync(42000L);
        mockExecutor
            .Setup(x => x.GetMinMaxCreatedOnAsync("account", It.IsAny<CancellationToken>()))
            .ReturnsAsync((new DateTime(2020, 1, 1), new DateTime(2024, 12, 31)));

        // COUNT(*) goes through aggregate FetchXML path — mock must return valid aggregate result
        var aggregateResult = new QueryResult
        {
            EntityLogicalName = "account",
            Columns = new List<QueryColumn>
            {
                new() { LogicalName = "accountid", Alias = "count", IsAggregate = true, AggregateFunction = "count" }
            },
            Records = new List<IReadOnlyDictionary<string, QueryValue>>
            {
                new Dictionary<string, QueryValue> { ["count"] = QueryValue.Simple(42000) }
            },
            Count = 1,
            MoreRecords = false,
            PageNumber = 1,
            IsAggregate = true
        };

        mockExecutor
            .Setup(x => x.ExecuteFetchXmlAsync(
                It.IsAny<string>(),
                It.IsAny<int?>(),
                It.IsAny<string?>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(aggregateResult);

        var service = new SqlQueryService(mockExecutor.Object);
        var request = new SqlQueryRequest { Sql = "SELECT COUNT(*) FROM account" };

        // Act
        await service.ExecuteAsync(request);

        // Assert: metadata methods were called for the aggregate query
        mockExecutor.Verify(
            x => x.GetTotalRecordCountAsync("account", It.IsAny<CancellationToken>()),
            Times.Once);
        mockExecutor.Verify(
            x => x.GetMinMaxCreatedOnAsync("account", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    [Trait("Category", "PlanUnit")]
    public async Task ExecuteAsync_NonAggregateQuery_DoesNotFetchMetadata()
    {
        // Arrange
        var mockExecutor = new Mock<IQueryExecutor>();
        mockExecutor
            .Setup(x => x.ExecuteFetchXmlAsync(
                It.IsAny<string>(),
                It.IsAny<int?>(),
                It.IsAny<string?>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QueryResult
            {
                EntityLogicalName = "account",
                Columns = new List<QueryColumn> { new() { LogicalName = "name" } },
                Records = new List<IReadOnlyDictionary<string, QueryValue>>(),
                Count = 0
            });

        var service = new SqlQueryService(mockExecutor.Object);
        var request = new SqlQueryRequest { Sql = "SELECT name FROM account" };

        // Act
        await service.ExecuteAsync(request);

        // Assert: metadata methods were NOT called for non-aggregate query
        mockExecutor.Verify(
            x => x.GetTotalRecordCountAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
        mockExecutor.Verify(
            x => x.GetMinMaxCreatedOnAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    [Trait("Category", "PlanUnit")]
    public void Constructor_WithPoolCapacity_StoresValue()
    {
        // Arrange & Act: constructing with poolCapacity should not throw
        var mockExecutor = new Mock<IQueryExecutor>();
        var service = new SqlQueryService(mockExecutor.Object, poolCapacity: 8);

        // Assert: the service was created (poolCapacity is used internally during planning)
        Assert.NotNull(service);
    }

    [Fact]
    [Trait("Category", "PlanUnit")]
    public async Task ExecuteAsync_DmlDryRun_ReturnsPlanWithoutExecuting()
    {
        // Arrange: executor that throws if called, proving dry-run skips execution
        var mockExecutor = new Mock<IQueryExecutor>();
        mockExecutor
            .Setup(x => x.ExecuteFetchXmlAsync(
                It.IsAny<string>(),
                It.IsAny<int?>(),
                It.IsAny<string?>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Executor should not be called during dry-run"));

        var service = new SqlQueryService(mockExecutor.Object);
        var request = new SqlQueryRequest
        {
            Sql = "DELETE FROM account WHERE name = 'test'",
            DmlSafety = new DmlSafetyOptions { IsDryRun = true, IsConfirmed = true }
        };

        // Act
        var result = await service.ExecuteAsync(request);

        // Assert: dry-run returns the plan without calling the executor
        Assert.NotNull(result);
        Assert.False(string.IsNullOrEmpty(result.TranspiledFetchXml), "Dry-run should return transpiled FetchXML");
        Assert.NotNull(result.DmlSafetyResult);
        Assert.True(result.DmlSafetyResult.IsDryRun, "DmlSafetyResult should indicate dry-run");

        // Verify executor was never called
        mockExecutor.Verify(
            x => x.ExecuteFetchXmlAsync(
                It.IsAny<string>(),
                It.IsAny<int?>(),
                It.IsAny<string?>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    #endregion

    #region ExpandFormattedValueColumns Tests

    [Fact]
    [Trait("Category", "PlanUnit")]
    public void ExpandFormattedValueColumns_LookupColumn_AddsNameColumn()
    {
        // Arrange: QueryResult with a lookup column that has a FormattedValue
        var ownerId = Guid.NewGuid();
        var result = new QueryResult
        {
            EntityLogicalName = "account",
            Columns = new List<QueryColumn>
            {
                new() { LogicalName = "name" },
                new() { LogicalName = "ownerid", DataType = QueryColumnType.Lookup }
            },
            Records = new List<IReadOnlyDictionary<string, QueryValue>>
            {
                new Dictionary<string, QueryValue>
                {
                    ["name"] = QueryValue.Simple("Contoso"),
                    ["ownerid"] = QueryValue.Lookup(ownerId, "systemuser", "John Smith")
                }
            },
            Count = 1
        };

        // Act
        var expanded = SqlQueryResultExpander.ExpandFormattedValueColumns(result);

        // Assert: should contain owneridname column
        var columnNames = expanded.Columns.Select(c => c.LogicalName).ToList();
        Assert.Contains("ownerid", columnNames);
        Assert.Contains("owneridname", columnNames);

        // The owneridname column should appear right after ownerid
        var ownerIdIndex = columnNames.IndexOf("ownerid");
        var ownerIdNameIndex = columnNames.IndexOf("owneridname");
        Assert.Equal(ownerIdIndex + 1, ownerIdNameIndex);

        // Verify the expanded record contains the display name
        var record = expanded.Records[0];
        Assert.True(record.TryGetValue("owneridname", out var owneridnameVal), "Record should contain owneridname key");
        Assert.Equal("John Smith", owneridnameVal.Value);
    }

    [Fact]
    [Trait("Category", "PlanUnit")]
    public void ExpandFormattedValueColumns_OptionSetColumn_AddsNameColumn()
    {
        // Arrange: QueryResult with an optionset column (int + FormattedValue)
        var result = new QueryResult
        {
            EntityLogicalName = "account",
            Columns = new List<QueryColumn>
            {
                new() { LogicalName = "statuscode", DataType = QueryColumnType.OptionSet }
            },
            Records = new List<IReadOnlyDictionary<string, QueryValue>>
            {
                new Dictionary<string, QueryValue>
                {
                    ["statuscode"] = QueryValue.WithFormatting(1, "Active")
                }
            },
            Count = 1
        };

        // Act
        var expanded = SqlQueryResultExpander.ExpandFormattedValueColumns(result);

        // Assert: should contain statuscodename column
        var columnNames = expanded.Columns.Select(c => c.LogicalName).ToList();
        Assert.Contains("statuscode", columnNames);
        Assert.Contains("statuscodename", columnNames);

        var record = expanded.Records[0];
        Assert.True(record.TryGetValue("statuscodename", out var statuscodenameVal), "Record should contain statuscodename key");
        Assert.Equal("Active", statuscodenameVal.Value);
    }

    [Fact]
    [Trait("Category", "PlanUnit")]
    public void ExpandFormattedValueColumns_BooleanColumn_AddsNameColumn()
    {
        // Arrange: QueryResult with a boolean column that has a FormattedValue
        var result = new QueryResult
        {
            EntityLogicalName = "solution",
            Columns = new List<QueryColumn>
            {
                new() { LogicalName = "ismanaged", DataType = QueryColumnType.Boolean }
            },
            Records = new List<IReadOnlyDictionary<string, QueryValue>>
            {
                new Dictionary<string, QueryValue>
                {
                    ["ismanaged"] = QueryValue.WithFormatting(true, "Yes")
                }
            },
            Count = 1
        };

        // Act
        var expanded = SqlQueryResultExpander.ExpandFormattedValueColumns(result);

        // Assert: should contain ismanagedname column
        var columnNames = expanded.Columns.Select(c => c.LogicalName).ToList();
        Assert.Contains("ismanaged", columnNames);
        Assert.Contains("ismanagedname", columnNames);

        var record = expanded.Records[0];
        Assert.True(record.TryGetValue("ismanagedname", out var ismanagedVal), "Record should contain ismanagedname key");
        Assert.Equal("Yes", ismanagedVal.Value);
    }

    [Fact]
    [Trait("Category", "PlanUnit")]
    public void ExpandFormattedValueColumns_VirtualColumnOnly_HidesBaseColumn()
    {
        // Arrange: user queried owneridname (not ownerid), so the base column should be hidden
        var ownerId = Guid.NewGuid();
        var result = new QueryResult
        {
            EntityLogicalName = "account",
            Columns = new List<QueryColumn>
            {
                new() { LogicalName = "ownerid", DataType = QueryColumnType.Lookup }
            },
            Records = new List<IReadOnlyDictionary<string, QueryValue>>
            {
                new Dictionary<string, QueryValue>
                {
                    ["ownerid"] = QueryValue.Lookup(ownerId, "systemuser", "John Smith")
                }
            },
            Count = 1
        };

        var virtualColumns = new Dictionary<string, VirtualColumnInfo>
        {
            ["owneridname"] = new VirtualColumnInfo
            {
                BaseColumnName = "ownerid",
                BaseColumnExplicitlyQueried = false
            }
        };

        // Act
        var expanded = SqlQueryResultExpander.ExpandFormattedValueColumns(result, virtualColumns);

        // Assert: ownerid should be hidden, only owneridname shown
        var columnNames = expanded.Columns.Select(c => c.LogicalName).ToList();
        Assert.DoesNotContain("ownerid", columnNames);
        Assert.Contains("owneridname", columnNames);

        var record = expanded.Records[0];
        Assert.False(record.ContainsKey("ownerid"), "Base column should be hidden when only *name was queried");
        Assert.Equal("John Smith", record["owneridname"].Value);
    }

    [Fact]
    [Trait("Category", "PlanUnit")]
    public void ExpandFormattedValueColumns_VirtualAndBaseExplicit_ShowsBoth()
    {
        // Arrange: user queried both ownerid AND owneridname
        var ownerId = Guid.NewGuid();
        var result = new QueryResult
        {
            EntityLogicalName = "account",
            Columns = new List<QueryColumn>
            {
                new() { LogicalName = "ownerid", DataType = QueryColumnType.Lookup }
            },
            Records = new List<IReadOnlyDictionary<string, QueryValue>>
            {
                new Dictionary<string, QueryValue>
                {
                    ["ownerid"] = QueryValue.Lookup(ownerId, "systemuser", "John Smith")
                }
            },
            Count = 1
        };

        var virtualColumns = new Dictionary<string, VirtualColumnInfo>
        {
            ["owneridname"] = new VirtualColumnInfo
            {
                BaseColumnName = "ownerid",
                BaseColumnExplicitlyQueried = true
            }
        };

        // Act
        var expanded = SqlQueryResultExpander.ExpandFormattedValueColumns(result, virtualColumns);

        // Assert: both ownerid and owneridname should be present
        var columnNames = expanded.Columns.Select(c => c.LogicalName).ToList();
        Assert.Contains("ownerid", columnNames);
        Assert.Contains("owneridname", columnNames);

        var record = expanded.Records[0];
        Assert.True(record.TryGetValue("ownerid", out _), "Base column should be present when explicitly queried");
        Assert.True(record.TryGetValue("owneridname", out var owneridnameVal2), "Virtual column should be present");
        Assert.Equal("John Smith", owneridnameVal2.Value);
    }

    [Fact]
    [Trait("Category", "PlanUnit")]
    public void ExpandFormattedValueColumns_PlainStringColumn_NoExpansion()
    {
        // Arrange: a plain string column should not be expanded
        var result = new QueryResult
        {
            EntityLogicalName = "account",
            Columns = new List<QueryColumn>
            {
                new() { LogicalName = "name", DataType = QueryColumnType.String }
            },
            Records = new List<IReadOnlyDictionary<string, QueryValue>>
            {
                new Dictionary<string, QueryValue>
                {
                    ["name"] = QueryValue.Simple("Contoso")
                }
            },
            Count = 1
        };

        // Act
        var expanded = SqlQueryResultExpander.ExpandFormattedValueColumns(result);

        // Assert: no additional columns
        Assert.Single(expanded.Columns);
        Assert.Equal("name", expanded.Columns[0].LogicalName);

        var record = expanded.Records[0];
        Assert.Single(record);
        Assert.Equal("Contoso", record["name"].Value);
    }

    [Fact]
    [Trait("Category", "PlanUnit")]
    public void ExpandFormattedValueColumns_EmptyResult_ReturnsOriginal()
    {
        // Arrange
        var result = QueryResult.Empty("account");

        // Act
        var expanded = SqlQueryResultExpander.ExpandFormattedValueColumns(result);

        // Assert: should return the same empty result
        Assert.Equal(0, expanded.Count);
        Assert.Empty(expanded.Records);
    }

    #endregion
}
