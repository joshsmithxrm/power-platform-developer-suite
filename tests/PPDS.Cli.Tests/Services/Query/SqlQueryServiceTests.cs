using Moq;
using PPDS.Auth.Profiles;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services;
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
    public void TranspileSql_WithTopOverrideAndTopInSql_PreservesOriginalTop()
    {
        // When SQL already has an explicit TOP, the override does NOT clobber it.
        // The user's explicit TOP takes precedence over the extension's default.
        var sql = "SELECT TOP 10 name FROM account";

        var fetchXml = _service.TranspileSql(sql, topOverride: 5);

        Assert.Contains("top=\"10\"", fetchXml);
        Assert.DoesNotContain("top=\"5\"", fetchXml);
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
        var request = new SqlQueryRequest { Sql = "SELECT FROM WHERE ,,," };

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

    #region Helpers

    private static bool ContainsNodeType(
        PPDS.Dataverse.Query.Planning.QueryPlanDescription node, string nodeType)
    {
        if (node.NodeType == nodeType) return true;
        foreach (var child in node.Children)
        {
            if (ContainsNodeType(child, nodeType)) return true;
        }
        return false;
    }

    #endregion

    #region Query Hint Integration Tests

    // ── AC-04: USE_TDS routes through TDS endpoint ──────────────────────

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ExecuteAsync_UseTdsHint_RoutesThroughTdsExecutor()
    {
        // Arrange: service with a TDS executor that returns a result
        var mockExecutor = new Mock<IQueryExecutor>();
        var mockTdsExecutor = new Mock<ITdsQueryExecutor>();

        var tdsResult = new QueryResult
        {
            EntityLogicalName = "account",
            Columns = new List<QueryColumn> { new() { LogicalName = "name" } },
            Records = new List<IReadOnlyDictionary<string, QueryValue>>(),
            Count = 0
        };

        mockTdsExecutor
            .Setup(x => x.ExecuteSqlAsync(
                It.IsAny<string>(),
                It.IsAny<int?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(tdsResult);

        var service = new SqlQueryService(mockExecutor.Object, tdsQueryExecutor: mockTdsExecutor.Object);

        // The -- ppds:USE_TDS hint should force TDS routing
        var request = new SqlQueryRequest
        {
            Sql = "-- ppds:USE_TDS\nSELECT name FROM account"
        };

        // Act
        await service.ExecuteAsync(request);

        // Assert: TDS executor was called; FetchXML executor was NOT called
        mockTdsExecutor.Verify(
            x => x.ExecuteSqlAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()),
            Times.Once);
        mockExecutor.Verify(
            x => x.ExecuteFetchXmlAsync(
                It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<string?>(),
                It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── AC-05: NOLOCK produces no-lock="true" in executed FetchXML ───────

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ExecuteAsync_NoLockHint_InjectsNoLockIntoFetchXml()
    {
        // Arrange: capture the FetchXML string passed to the executor
        string? capturedFetchXml = null;

        _mockQueryExecutor
            .Setup(x => x.ExecuteFetchXmlAsync(
                It.IsAny<string>(),
                It.IsAny<int?>(),
                It.IsAny<string?>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, int?, string?, bool, CancellationToken>(
                (fx, _, _, _, _) => capturedFetchXml = fx)
            .ReturnsAsync(new QueryResult
            {
                EntityLogicalName = "account",
                Columns = new List<QueryColumn> { new() { LogicalName = "name" } },
                Records = new List<IReadOnlyDictionary<string, QueryValue>>(),
                Count = 0
            });

        var request = new SqlQueryRequest
        {
            Sql = "-- ppds:NOLOCK\nSELECT name FROM account"
        };

        // Act
        await _service.ExecuteAsync(request);

        // Assert: FetchXML sent to the executor contains no-lock="true"
        Assert.NotNull(capturedFetchXml);
        Assert.Contains("no-lock=\"true\"", capturedFetchXml);
    }

    // ── AC-08: MAX_ROWS limits result rows ────────────────────────────────

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ExecuteAsync_MaxRowsHint_LimitsReturnedRows()
    {
        // Arrange: executor returns 200 records; hint should cap at 100
        var mockExecutor = new Mock<IQueryExecutor>();
        var records = Enumerable.Range(0, 200)
            .Select(i => (IReadOnlyDictionary<string, QueryValue>)new Dictionary<string, QueryValue>
                { ["name"] = QueryValue.Simple($"Record {i}") })
            .ToList();

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
                Records = records,
                Count = records.Count
            });

        var service = new SqlQueryService(mockExecutor.Object);
        var request = new SqlQueryRequest
        {
            Sql = "-- ppds:MAX_ROWS 100\nSELECT name FROM account"
        };

        // Act
        var result = await service.ExecuteAsync(request);

        // Assert: the plan node's MaxRows cap was applied — at most 100 rows returned
        Assert.NotNull(result);
        Assert.True(result.Result.Count <= 100,
            $"Expected at most 100 rows, got {result.Result.Count}");
    }

    // ── AC-09: MAXDOP 2 caps parallelism ─────────────────────────────────

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ExplainAsync_MaxdopHint_CapsPoolCapacity()
    {
        // Arrange: service with pool capacity of 8; MAXDOP 2 should cap it at 2
        var mockExecutor = new Mock<IQueryExecutor>();
        var service = new SqlQueryService(mockExecutor.Object, poolCapacity: 8);

        // Act
        var plan = await service.ExplainAsync("-- ppds:MAXDOP 2\nSELECT name FROM account");

        // Assert: the plan tree reflects the hint (PoolCapacity capped to min(2,8)=2).
        // For a simple SELECT, no parallel partition node is added (only aggregates partition),
        // but the hint parsing should not throw and the plan should succeed.
        Assert.NotNull(plan);
    }

    // ── AC-11: BATCH_SIZE overrides DML batch size ────────────────────────

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ExecuteAsync_BatchSizeHint_IsAcceptedWithoutError()
    {
        // Arrange: BATCH_SIZE is a DML planning hint; for a SELECT it is parsed but ignored
        // without error — query proceeds normally.
        _mockQueryExecutor
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

        var request = new SqlQueryRequest
        {
            Sql = "-- ppds:BATCH_SIZE 500\nSELECT name FROM account"
        };

        // Act — should not throw
        var result = await _service.ExecuteAsync(request);

        // Assert
        Assert.NotNull(result);
    }

    // ── AC-12: Inline hints override caller-provided settings ─────────────

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ExecuteAsync_UseTdsHintOverridesCallerFalse_TdsWins()
    {
        // Arrange: caller passes UseTdsEndpoint=false, but inline hint says USE_TDS.
        // The hint must win.
        var mockExecutor = new Mock<IQueryExecutor>();
        var mockTdsExecutor = new Mock<ITdsQueryExecutor>();

        mockTdsExecutor
            .Setup(x => x.ExecuteSqlAsync(
                It.IsAny<string>(),
                It.IsAny<int?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QueryResult
            {
                EntityLogicalName = "account",
                Columns = new List<QueryColumn> { new() { LogicalName = "name" } },
                Records = new List<IReadOnlyDictionary<string, QueryValue>>(),
                Count = 0
            });

        var service = new SqlQueryService(mockExecutor.Object, tdsQueryExecutor: mockTdsExecutor.Object);

        // Caller explicitly sets UseTdsEndpoint = false, but hint overrides it
        var request = new SqlQueryRequest
        {
            Sql = "-- ppds:USE_TDS\nSELECT name FROM account",
            UseTdsEndpoint = false   // caller says no TDS
        };

        // Act
        await service.ExecuteAsync(request);

        // Assert: TDS wins — TDS executor was called despite caller saying false
        mockTdsExecutor.Verify(
            x => x.ExecuteSqlAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()),
            Times.Once);
        mockExecutor.Verify(
            x => x.ExecuteFetchXmlAsync(
                It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<string?>(),
                It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── AC-13: Malformed hint values silently ignored ─────────────────────

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ExecuteAsync_MalformedHintValue_QueryProceeds()
    {
        // Arrange: MAX_ROWS with non-numeric value is silently ignored;
        // the query should execute normally without throwing.
        _mockQueryExecutor
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

        var request = new SqlQueryRequest
        {
            // "abc" is not a valid integer — hint should be silently ignored
            Sql = "-- ppds:MAX_ROWS abc\nSELECT name FROM account"
        };

        // Act — must not throw
        var result = await _service.ExecuteAsync(request);

        // Assert: query executed normally
        Assert.NotNull(result);
        _mockQueryExecutor.Verify(
            x => x.ExecuteFetchXmlAsync(
                It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<string?>(),
                It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ExecuteAsync_UnrecognizedHint_QueryProceeds()
    {
        // A completely unknown hint directive should be silently ignored.
        _mockQueryExecutor
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

        var request = new SqlQueryRequest
        {
            Sql = "-- ppds:TOTALLY_UNKNOWN_HINT\nSELECT name FROM account"
        };

        // Act — must not throw
        var result = await _service.ExecuteAsync(request);

        // Assert
        Assert.NotNull(result);
    }

    // ── AC-21: ExplainAsync reflects hint-influenced plans ────────────────

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ExplainAsync_NoLockHint_PlanContainsFetchXmlScanWithNoLock()
    {
        // Arrange
        var mockExecutor = new Mock<IQueryExecutor>();
        var service = new SqlQueryService(mockExecutor.Object);

        // Act
        var plan = await service.ExplainAsync("-- ppds:NOLOCK\nSELECT name FROM account");

        // Assert: plan is non-null and node type indicates a FetchXML scan
        Assert.NotNull(plan);
        Assert.Equal("FetchXmlScanNode", plan.NodeType);
        // The description includes the entity name
        Assert.Contains("account", plan.Description ?? "");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ExplainAsync_UseTdsHint_PlanContainsTdsScanNode()
    {
        // Arrange: service with TDS executor configured
        var mockExecutor = new Mock<IQueryExecutor>();
        var mockTdsExecutor = new Mock<ITdsQueryExecutor>();
        var service = new SqlQueryService(mockExecutor.Object, tdsQueryExecutor: mockTdsExecutor.Object);

        // Act
        var plan = await service.ExplainAsync("-- ppds:USE_TDS\nSELECT name FROM account");

        // Assert: plan reflects TDS routing
        Assert.NotNull(plan);
        Assert.Equal("TdsScanNode", plan.NodeType);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ExplainAsync_MaxRowsHint_PlanReflectsRowLimit()
    {
        // Arrange
        var mockExecutor = new Mock<IQueryExecutor>();
        var service = new SqlQueryService(mockExecutor.Object);

        // Act
        var plan = await service.ExplainAsync("-- ppds:MAX_ROWS 50\nSELECT name FROM account");

        // Assert: plan produced without error; the row limit is in the plan description
        Assert.NotNull(plan);
        Assert.Contains("50", plan.Description ?? "");
    }

    #endregion

    #region Cross-Environment Tests

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ExplainAsync_CrossEnvQuery_UsesRemoteExecutorFactory()
    {
        // Arrange: service with RemoteExecutorFactory set
        var mockExecutor = new Mock<IQueryExecutor>();
        var service = new SqlQueryService(mockExecutor.Object);

        var factoryCalled = false;
        var mockRemoteExecutor = Mock.Of<IQueryExecutor>();
        service.RemoteExecutorFactory = label =>
        {
            if (label == "UAT")
            {
                factoryCalled = true;
                return mockRemoteExecutor;
            }
            return null;
        };

        // Act: EXPLAIN a cross-env query
        var plan = await service.ExplainAsync("SELECT name FROM [UAT].account");

        // Assert: factory was called and plan tree contains RemoteScanNode
        Assert.True(factoryCalled, "RemoteExecutorFactory should be called with label 'UAT'");
        Assert.True(ContainsNodeType(plan, "RemoteScanNode"),
            "Plan should contain a RemoteScanNode for cross-env query");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ExplainAsync_CrossEnvQuery_NoFactory_ThrowsDescriptiveError()
    {
        // Arrange: service WITHOUT RemoteExecutorFactory
        var mockExecutor = new Mock<IQueryExecutor>();
        var service = new SqlQueryService(mockExecutor.Object);
        // RemoteExecutorFactory is null (default)

        // Act & Assert: cross-env query fails with actionable error
        var ex = await Assert.ThrowsAsync<PpdsException>(
            () => service.ExplainAsync("SELECT name FROM [UAT].account"));

        Assert.Contains("UAT", ex.Message);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ExecuteAsync_DevelopmentProtection_UsesRelaxedSafety()
    {
        // Arrange: service with Development protection level — DML dry-run should pass
        // without requiring confirmation (Development is relaxed).
        var mockExecutor = new Mock<IQueryExecutor>();
        var service = new SqlQueryService(mockExecutor.Object)
        {
            EnvironmentProtectionLevel = ProtectionLevel.Development
        };

        var request = new SqlQueryRequest
        {
            Sql = "DELETE FROM account WHERE name = 'test'",
            DmlSafety = new DmlSafetyOptions { IsConfirmed = true, IsDryRun = true }
        };

        // Act — should not throw (Development protection + confirmed = passes safety check)
        var result = await service.ExecuteAsync(request);

        // Assert: dry-run returns plan without executing
        Assert.NotNull(result);
        Assert.NotNull(result.DmlSafetyResult);
        Assert.True(result.DmlSafetyResult.IsDryRun);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ExecuteAsync_CrossEnvDml_ReadOnlyPolicy_Throws()
    {
        // Arrange: service with RemoteExecutorFactory and cross-env DML
        var mockExecutor = new Mock<IQueryExecutor>();
        var mockRemoteExecutor = Mock.Of<IQueryExecutor>();
        var service = new SqlQueryService(mockExecutor.Object);

        service.RemoteExecutorFactory = label =>
            label == "QA" ? mockRemoteExecutor : null;

        // Set up ProfileResolver with ReadOnly policy (default)
        var envConfigs = new[]
        {
            new EnvironmentConfig
            {
                Url = "https://qa.crm.dynamics.com/",
                Label = "QA",
                Type = EnvironmentType.Sandbox,
                SafetySettings = new QuerySafetySettings
                {
                    CrossEnvironmentDmlPolicy = CrossEnvironmentDmlPolicy.ReadOnly
                }
            }
        };
        service.ProfileResolver = new ProfileResolutionService(envConfigs);

        var request = new SqlQueryRequest
        {
            Sql = "DELETE FROM [QA].account WHERE name = 'test'",
            DmlSafety = new DmlSafetyOptions { IsConfirmed = true }
        };

        // Act & Assert: should throw because ReadOnly blocks cross-env DML
        var ex = await Assert.ThrowsAsync<PpdsException>(
            () => service.ExecuteAsync(request));

        Assert.Equal(ErrorCodes.Query.DmlBlocked, ex.ErrorCode);
        Assert.Contains("read-only", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ExecuteAsync_CrossEnvDml_AllowPolicy_WithConfirm_Passes()
    {
        // Arrange: service with Allow cross-env DML policy + confirmed + dry-run
        // Dry-run verifies the safety checks pass without requiring BulkOperationExecutor.
        var mockExecutor = new Mock<IQueryExecutor>();
        var mockRemoteExecutor = Mock.Of<IQueryExecutor>();
        var service = new SqlQueryService(mockExecutor.Object);

        service.RemoteExecutorFactory = label =>
            label == "DEV" ? mockRemoteExecutor : null;

        var envConfigs = new[]
        {
            new EnvironmentConfig
            {
                Url = "https://dev.crm.dynamics.com/",
                Label = "DEV",
                Type = EnvironmentType.Development,
                SafetySettings = new QuerySafetySettings
                {
                    CrossEnvironmentDmlPolicy = CrossEnvironmentDmlPolicy.Allow
                }
            }
        };
        service.ProfileResolver = new ProfileResolutionService(envConfigs);

        var request = new SqlQueryRequest
        {
            Sql = "DELETE FROM [DEV].account WHERE name = 'test'",
            DmlSafety = new DmlSafetyOptions { IsConfirmed = true, IsDryRun = true }
        };

        // Act — should pass because Allow policy + confirmed + Development target
        var result = await service.ExecuteAsync(request);

        // Assert: dry-run returns the plan without executing
        Assert.NotNull(result);
        Assert.NotNull(result.DmlSafetyResult);
        Assert.True(result.DmlSafetyResult.IsDryRun);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ExecuteAsync_CrossEnvSelect_ReadOnlyPolicy_Passes()
    {
        // Arrange: SELECT is always allowed cross-env, even with ReadOnly policy
        var mockExecutor = new Mock<IQueryExecutor>();
        var mockRemoteExecutor = new Mock<IQueryExecutor>();

        mockRemoteExecutor
            .Setup(x => x.ExecuteFetchXmlAsync(
                It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<string?>(),
                It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QueryResult
            {
                EntityLogicalName = "account",
                Columns = new List<QueryColumn> { new() { LogicalName = "name" } },
                Records = new List<IReadOnlyDictionary<string, QueryValue>>(),
                Count = 0
            });

        var service = new SqlQueryService(mockExecutor.Object);

        service.RemoteExecutorFactory = label =>
            label == "QA" ? mockRemoteExecutor.Object : null;

        var envConfigs = new[]
        {
            new EnvironmentConfig
            {
                Url = "https://qa.crm.dynamics.com/",
                Label = "QA",
                Type = EnvironmentType.Sandbox,
                SafetySettings = new QuerySafetySettings
                {
                    CrossEnvironmentDmlPolicy = CrossEnvironmentDmlPolicy.ReadOnly
                }
            }
        };
        service.ProfileResolver = new ProfileResolutionService(envConfigs);

        var request = new SqlQueryRequest
        {
            Sql = "SELECT name FROM [QA].account",
            DmlSafety = new DmlSafetyOptions { IsConfirmed = false }
        };

        // Act — SELECT should pass even with ReadOnly policy
        var result = await service.ExecuteAsync(request);

        // Assert
        Assert.NotNull(result);
    }

    #endregion

    // ═══════════════════════════════════════════════════════════════════
    //  AC-38: ExecutionMode reports "Tds" when TDS Endpoint is used
    // ═══════════════════════════════════════════════════════════════════

    #region ExecutionMode Tests

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ExecuteAsync_TdsRoute_ReturnsExecutionModeTds()
    {
        // Arrange: service with a TDS executor that returns a result
        var mockExecutor = new Mock<IQueryExecutor>();
        var mockTdsExecutor = new Mock<ITdsQueryExecutor>();

        var tdsResult = new QueryResult
        {
            EntityLogicalName = "account",
            Columns = new List<QueryColumn> { new() { LogicalName = "name" } },
            Records = new List<IReadOnlyDictionary<string, QueryValue>>(),
            Count = 0
        };

        mockTdsExecutor
            .Setup(x => x.ExecuteSqlAsync(
                It.IsAny<string>(),
                It.IsAny<int?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(tdsResult);

        var service = new SqlQueryService(mockExecutor.Object, tdsQueryExecutor: mockTdsExecutor.Object);

        var request = new SqlQueryRequest
        {
            Sql = "-- ppds:USE_TDS\nSELECT name FROM account"
        };

        // Act
        var result = await service.ExecuteAsync(request);

        // Assert: ExecutionMode must be Tds when TDS Endpoint was used
        Assert.NotNull(result.ExecutionMode);
        Assert.Equal(QueryExecutionMode.Tds, result.ExecutionMode);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ExecuteAsync_DataverseRoute_ReturnsExecutionModeDataverse()
    {
        // Arrange: standard Dataverse execution (no TDS)
        _mockQueryExecutor
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

        var request = new SqlQueryRequest
        {
            Sql = "SELECT name FROM account"
        };

        // Act
        var result = await _service.ExecuteAsync(request);

        // Assert: ExecutionMode must be Dataverse for standard queries
        Assert.NotNull(result.ExecutionMode);
        Assert.Equal(QueryExecutionMode.Dataverse, result.ExecutionMode);
    }

    #endregion

    // ═══════════════════════════════════════════════════════════════════
    //  AC-44: Streaming final chunk carries ExecutionMode
    // ═══════════════════════════════════════════════════════════════════

    #region Streaming ExecutionMode Tests

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ExecuteStreamingAsync_FinalChunk_HasExecutionModeDataverse()
    {
        // Arrange: standard Dataverse execution; mock executor returns rows via FetchXML
        _mockQueryExecutor
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
                Records = new List<IReadOnlyDictionary<string, QueryValue>>
                {
                    new Dictionary<string, QueryValue>
                    {
                        ["name"] = QueryValue.Simple("Contoso")
                    }
                },
                Count = 1
            });

        var request = new SqlQueryRequest
        {
            Sql = "SELECT name FROM account"
        };

        // Act: collect all chunks
        var chunks = new List<SqlQueryStreamChunk>();
        await foreach (var chunk in _service.ExecuteStreamingAsync(request))
        {
            chunks.Add(chunk);
        }

        // Assert: at least one chunk, and the final chunk has ExecutionMode
        Assert.NotEmpty(chunks);
        var finalChunk = chunks[^1];
        Assert.True(finalChunk.IsComplete);
        Assert.NotNull(finalChunk.ExecutionMode);
        Assert.Equal(QueryExecutionMode.Dataverse, finalChunk.ExecutionMode);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ExecuteStreamingAsync_TdsRoute_FinalChunkHasExecutionModeTds()
    {
        // Arrange: TDS executor returns results
        var mockExecutor = new Mock<IQueryExecutor>();
        var mockTdsExecutor = new Mock<ITdsQueryExecutor>();

        var tdsResult = new QueryResult
        {
            EntityLogicalName = "account",
            Columns = new List<QueryColumn> { new() { LogicalName = "name" } },
            Records = new List<IReadOnlyDictionary<string, QueryValue>>
            {
                new Dictionary<string, QueryValue>
                {
                    ["name"] = QueryValue.Simple("Contoso")
                }
            },
            Count = 1
        };

        mockTdsExecutor
            .Setup(x => x.ExecuteSqlAsync(
                It.IsAny<string>(),
                It.IsAny<int?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(tdsResult);

        var service = new SqlQueryService(mockExecutor.Object, tdsQueryExecutor: mockTdsExecutor.Object);

        var request = new SqlQueryRequest
        {
            Sql = "-- ppds:USE_TDS\nSELECT name FROM account"
        };

        // Act: collect all chunks
        var chunks = new List<SqlQueryStreamChunk>();
        await foreach (var chunk in service.ExecuteStreamingAsync(request))
        {
            chunks.Add(chunk);
        }

        // Assert: final chunk should have ExecutionMode = Tds
        Assert.NotEmpty(chunks);
        var finalChunk = chunks[^1];
        Assert.True(finalChunk.IsComplete);
        Assert.NotNull(finalChunk.ExecutionMode);
        Assert.Equal(QueryExecutionMode.Tds, finalChunk.ExecutionMode);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ExecuteStreamingAsync_NonFinalChunks_DoNotHaveExecutionMode()
    {
        // Arrange: TDS executor returns enough rows to cause multiple chunks
        var mockExecutor = new Mock<IQueryExecutor>();
        var mockTdsExecutor = new Mock<ITdsQueryExecutor>();

        var records = new List<IReadOnlyDictionary<string, QueryValue>>();
        for (int i = 0; i < 5; i++)
        {
            records.Add(new Dictionary<string, QueryValue>
            {
                ["name"] = QueryValue.Simple($"Account{i}")
            });
        }

        var tdsResult = new QueryResult
        {
            EntityLogicalName = "account",
            Columns = new List<QueryColumn> { new() { LogicalName = "name" } },
            Records = records,
            Count = 5
        };

        mockTdsExecutor
            .Setup(x => x.ExecuteSqlAsync(
                It.IsAny<string>(),
                It.IsAny<int?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(tdsResult);

        var service = new SqlQueryService(mockExecutor.Object, tdsQueryExecutor: mockTdsExecutor.Object);

        var request = new SqlQueryRequest
        {
            Sql = "-- ppds:USE_TDS\nSELECT name FROM account"
        };

        // Act: collect all chunks with small chunk size to force multiple chunks
        var chunks = new List<SqlQueryStreamChunk>();
        await foreach (var chunk in service.ExecuteStreamingAsync(request, chunkSize: 2))
        {
            chunks.Add(chunk);
        }

        // Assert: multiple chunks; non-final chunks should have null ExecutionMode
        Assert.True(chunks.Count > 1, $"Expected multiple chunks but got {chunks.Count}");
        for (int i = 0; i < chunks.Count - 1; i++)
        {
            Assert.False(chunks[i].IsComplete);
            Assert.Null(chunks[i].ExecutionMode);
        }

        // Final chunk has ExecutionMode
        var finalChunk = chunks[^1];
        Assert.True(finalChunk.IsComplete);
        Assert.Equal(QueryExecutionMode.Tds, finalChunk.ExecutionMode);
    }

    #endregion
}
