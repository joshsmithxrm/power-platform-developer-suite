using Moq;
using PPDS.Auth.Profiles;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services.Query;
using PPDS.Cli.Tests.Services.Shared;
using PPDS.Cli.Tui.Screens;
using PPDS.Dataverse.BulkOperations;
using PPDS.Dataverse.Query;
using Xunit;

namespace PPDS.Cli.Tests.Services.Query;

/// <summary>
/// A DML statement that would affect more rows than the cap is refused before anything changes; an explicit
/// --top is refused for DML; a default top is ignored for DML.
/// </summary>
[Trait("Category", "Unit")]
public class DmlRowCapTests
{
    private static DmlSafetyOptions Cap(int cap) => new() { IsConfirmed = true, RowCap = cap };

    private sealed class Harness
    {
        public required SqlQueryService Service { get; init; }
        public required Mock<IBulkOperationExecutor> Bulk { get; init; }
        public List<int> Deleted { get; } = new();
        public List<int> Updated { get; } = new();
        public List<int> Created { get; } = new();
    }

    private static QueryResult Rows(int count) => new()
    {
        EntityLogicalName = "account",
        Columns = new[] { new QueryColumn { LogicalName = "accountid" }, new QueryColumn { LogicalName = "name" } },
        Records = Enumerable.Range(0, count).Select(i => (IReadOnlyDictionary<string, QueryValue>)new Dictionary<string, QueryValue>
        {
            ["accountid"] = QueryValue.Simple(Guid.NewGuid()),
            ["contactid"] = QueryValue.Simple(Guid.NewGuid()),
            ["name"] = QueryValue.Simple("n" + i)
        }).ToArray(),
        Count = count
    };

    /// <summary>Contact queries return <paramref name="contactRows"/> rows; everything else returns <paramref name="rows"/>.</summary>
    private static Harness Create(int rows, int? contactRows = null)
    {
        var executor = new Mock<IQueryExecutor>(MockBehavior.Loose);
        QueryResult Pick(string fetchXml) => fetchXml.Contains("contact") ? Rows(contactRows ?? rows) : Rows(rows);
        executor.Setup(x => x.ExecuteFetchXmlAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string f, int? _, string? _, bool _, CancellationToken _) => Pick(f));
        executor.Setup(x => x.ExecuteFetchXmlAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<QueryExecutionOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string f, int? _, string? _, bool _, QueryExecutionOptions? _, CancellationToken _) => Pick(f));

        var bulk = new Mock<IBulkOperationExecutor>(MockBehavior.Loose);
        var harness = new Harness
        {
            Bulk = bulk,
            Service = new SqlQueryService(executor.Object, guard: new InactiveFakeShakedownGuard(), bulkOperationExecutor: bulk.Object, metadataProvider: PPDS.Cli.Tests.Services.Query.TestMetadata.Provider)
            {
                EnvironmentProtectionLevel = ProtectionLevel.Development
            }
        };
        bulk.Setup(b => b.DeleteMultipleAsync(It.IsAny<string>(), It.IsAny<IEnumerable<Guid>>(), It.IsAny<BulkOperationOptions?>(), It.IsAny<PPDS.Dataverse.Client.DataverseClientOptions?>(), It.IsAny<IProgress<PPDS.Dataverse.Progress.ProgressSnapshot>?>(), It.IsAny<CancellationToken>()))
            .Returns((string _, IEnumerable<Guid> ids, BulkOperationOptions? _, PPDS.Dataverse.Client.DataverseClientOptions? _, IProgress<PPDS.Dataverse.Progress.ProgressSnapshot>? _, CancellationToken _) =>
            {
                var n = ids.Count(); harness.Deleted.Add(n);
                return Task.FromResult(new BulkOperationResult { SuccessCount = n });
            });
        bulk.Setup(b => b.UpdateMultipleAsync(It.IsAny<string>(), It.IsAny<IEnumerable<Microsoft.Xrm.Sdk.Entity>>(), It.IsAny<BulkOperationOptions?>(), It.IsAny<PPDS.Dataverse.Client.DataverseClientOptions?>(), It.IsAny<IProgress<PPDS.Dataverse.Progress.ProgressSnapshot>?>(), It.IsAny<CancellationToken>()))
            .Returns((string _, IEnumerable<Microsoft.Xrm.Sdk.Entity> e, BulkOperationOptions? _, PPDS.Dataverse.Client.DataverseClientOptions? _, IProgress<PPDS.Dataverse.Progress.ProgressSnapshot>? _, CancellationToken _) =>
            {
                var n = e.Count(); harness.Updated.Add(n);
                return Task.FromResult(new BulkOperationResult { SuccessCount = n });
            });
        bulk.Setup(b => b.CreateMultipleAsync(It.IsAny<string>(), It.IsAny<IEnumerable<Microsoft.Xrm.Sdk.Entity>>(), It.IsAny<BulkOperationOptions?>(), It.IsAny<PPDS.Dataverse.Client.DataverseClientOptions?>(), It.IsAny<IProgress<PPDS.Dataverse.Progress.ProgressSnapshot>?>(), It.IsAny<CancellationToken>()))
            .Returns((string _, IEnumerable<Microsoft.Xrm.Sdk.Entity> e, BulkOperationOptions? _, PPDS.Dataverse.Client.DataverseClientOptions? _, IProgress<PPDS.Dataverse.Progress.ProgressSnapshot>? _, CancellationToken _) =>
            {
                var n = e.Count(); harness.Created.Add(n);
                return Task.FromResult(new BulkOperationResult { SuccessCount = n });
            });
        return harness;
    }

    private const string DeleteSql = "DELETE FROM account WHERE name = 'x'";
    private const string UpdateSql = "UPDATE account SET name = 'y' WHERE name = 'x'";
    private const string InsertSelectSql = "INSERT INTO account (name) SELECT name FROM account WHERE name = 'x'";
    private const string InsertValuesSql = "INSERT INTO account (name) VALUES ('a'), ('b'), ('c')";

    private static void AssertNothingChanged(Harness h)
    {
        Assert.Empty(h.Deleted);
        Assert.Empty(h.Updated);
        Assert.Empty(h.Created);
    }

    // ---- row cap -------------------------------------------------------------

    [Theory]
    [InlineData(DeleteSql)]
    [InlineData(UpdateSql)]
    [InlineData(InsertSelectSql)]
    [InlineData(InsertValuesSql)]
    public async Task OverTheCap_IsRefusedBeforeAnythingChanges_WithTheCapAndTheWayForward(string sql)
    {
        var h = Create(rows: 5);

        var ex = await Assert.ThrowsAsync<PpdsDmlRowCapExceededException>(
            () => h.Service.ExecuteAsync(new SqlQueryRequest { Sql = sql, DmlSafety = Cap(2) }));

        Assert.Equal(ErrorCodes.Query.DmlRowCapExceeded, ex.ErrorCode);
        Assert.Contains("row cap of 2", ex.Message);
        // INSERT VALUES knows its exact row count; the others only know that at least cap + 1 rows match.
        Assert.Contains(sql == InsertValuesSql ? "it would insert 3 rows" : "at least 3 rows", ex.Message);
        Assert.Contains("changed nothing", ex.Message);
        Assert.Contains(sql == InsertValuesSql ? "Split the INSERT" : "Narrow the WHERE clause", ex.Message);
        Assert.DoesNotContain("--", ex.Message); // surface-neutral: TUI and RPC users cannot pass CLI flags
        Assert.DoesNotContain("5", ex.Message); // never claims a total it did not count
        Assert.Equal(2, ex.RowCap);
        Assert.Equal("account", ex.EntityLogicalName);
        Assert.Contains(ex.Operation, new[] { "DELETE", "UPDATE", "INSERT" });
        AssertNothingChanged(h);
        Assert.Equal(ExitCodes.Failure, ExceptionMapper.ToExitCode(ex));

        // The CLI adds its own remedy.
        var cli = PPDS.Cli.Commands.Query.DmlSafetyOptionSet.WithCliWording(ExceptionMapper.Map(ex), ex);
        Assert.EndsWith("re-run with --no-limit.", cli.Message);
    }

    [Fact]
    public async Task AStatementWithExactlyTheCapUsableRows_PlusRowsWithoutAnId_Runs()
    {
        // Rows the statement would skip (no id) do not count toward the cap.
        var executor = new Mock<IQueryExecutor>(MockBehavior.Loose);
        var records = Enumerable.Range(0, 3).Select(i => (IReadOnlyDictionary<string, QueryValue>)new Dictionary<string, QueryValue>
        {
            ["accountid"] = i < 2 ? QueryValue.Simple(Guid.NewGuid()) : QueryValue.Null,
        }).ToArray();
        var fetched = new QueryResult { EntityLogicalName = "account", Columns = new[] { new QueryColumn { LogicalName = "accountid" } }, Records = records, Count = 3 };
        executor.Setup(x => x.ExecuteFetchXmlAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(fetched);
        executor.Setup(x => x.ExecuteFetchXmlAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<QueryExecutionOptions?>(), It.IsAny<CancellationToken>())).ReturnsAsync(fetched);
        var deleted = new List<int>();
        var bulk = new Mock<IBulkOperationExecutor>(MockBehavior.Loose);
        bulk.Setup(b => b.DeleteMultipleAsync(It.IsAny<string>(), It.IsAny<IEnumerable<Guid>>(), It.IsAny<BulkOperationOptions?>(), It.IsAny<PPDS.Dataverse.Client.DataverseClientOptions?>(), It.IsAny<IProgress<PPDS.Dataverse.Progress.ProgressSnapshot>?>(), It.IsAny<CancellationToken>()))
            .Returns((string _, IEnumerable<Guid> ids, BulkOperationOptions? _, PPDS.Dataverse.Client.DataverseClientOptions? _, IProgress<PPDS.Dataverse.Progress.ProgressSnapshot>? _, CancellationToken _) =>
            {
                deleted.Add(ids.Count());
                return Task.FromResult(new BulkOperationResult { SuccessCount = ids.Count() });
            });
        var service = new SqlQueryService(executor.Object, guard: new InactiveFakeShakedownGuard(), bulkOperationExecutor: bulk.Object, metadataProvider: PPDS.Cli.Tests.Services.Query.TestMetadata.Provider)
        {
            EnvironmentProtectionLevel = ProtectionLevel.Development
        };

        await service.ExecuteAsync(new SqlQueryRequest { Sql = DeleteSql, DmlSafety = Cap(2) });

        Assert.Equal(new[] { 2 }, deleted);
    }

    [Fact]
    public async Task RowCapMessage_UsesInvariantDigitGrouping()
    {
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            var h = Create(rows: 1001);

            var ex = await Assert.ThrowsAsync<PpdsDmlRowCapExceededException>(() => h.Service.ExecuteAsync(
                new SqlQueryRequest { Sql = DeleteSql, DmlSafety = Cap(1000) }));

            Assert.Contains("row cap of 1,000 rows (at least 1,001 rows match)", ex.Message);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }

    [Theory]
    [InlineData(DeleteSql)]
    [InlineData(UpdateSql)]
    [InlineData(InsertSelectSql)]
    [InlineData(InsertValuesSql)]
    public async Task ExactlyAtTheCap_Succeeds(string sql)
    {
        var h = Create(rows: 3);

        var result = await h.Service.ExecuteAsync(new SqlQueryRequest { Sql = sql, DmlSafety = Cap(3) });

        Assert.False(result.DmlOutcome!.HasFailures);
        Assert.Equal(3, result.DmlOutcome.AffectedRows);
    }

    [Fact]
    public async Task NoLimit_RemovesTheCap()
    {
        var h = Create(rows: 5);

        var result = await h.Service.ExecuteAsync(new SqlQueryRequest
        {
            Sql = DeleteSql,
            DmlSafety = new DmlSafetyOptions { IsConfirmed = true, NoLimit = true }
        });

        Assert.Equal(new[] { 5 }, h.Deleted);
        Assert.Equal(5, result.DmlOutcome!.AffectedRows);
    }

    [Fact]
    public async Task Streaming_OverTheCap_IsRefused()
    {
        var h = Create(rows: 5);

        var ex = await Assert.ThrowsAsync<PpdsDmlRowCapExceededException>(async () =>
        {
            await foreach (var _ in h.Service.ExecuteStreamingAsync(new SqlQueryRequest { Sql = DeleteSql, DmlSafety = Cap(2) })) { }
        });

        Assert.Equal(ErrorCodes.Query.DmlRowCapExceeded, ex.ErrorCode);
        AssertNothingChanged(h);
    }

    [Fact]
    public async Task Script_EarlierStatementsStayCommitted_AndAreReportedOnTheRefusal()
    {
        var h = Create(rows: 2, contactRows: 5);

        var ex = await Assert.ThrowsAsync<PpdsDmlRowCapExceededException>(() => h.Service.ExecuteAsync(new SqlQueryRequest
        {
            Sql = "DELETE FROM account WHERE name = 'x'; DELETE FROM contact WHERE lastname = 'y'",
            DmlSafety = Cap(2)
        }));

        Assert.Equal(ErrorCodes.Query.DmlRowCapExceeded, ex.ErrorCode);
        Assert.Equal(new[] { 2 }, h.Deleted); // statement 1 ran; statement 2 changed nothing
        var committed = DmlOutcome.From(ex)!;
        Assert.Contains("#1 DELETE account: 2 affected, 0 failed", committed.DescribeCommitted());
        Assert.Contains("not rolled back", committed.DescribeCommitted());
    }

    [Fact]
    public async Task DryRun_DoesNotCountRows_ItReportsTheCapAndExecutesNothing()
    {
        var h = Create(rows: 5);

        var result = await h.Service.ExecuteAsync(new SqlQueryRequest
        {
            Sql = DeleteSql,
            DmlSafety = new DmlSafetyOptions { IsDryRun = true, RowCap = 2 }
        });

        Assert.True(result.DmlSafetyResult!.IsDryRun);
        Assert.Equal(2, result.DmlSafetyResult.RowCap);
        Assert.False(result.DmlSafetyResult.ExceedsRowCap); // unknowable without running the statement
        AssertNothingChanged(h);
    }

    // RPC error data for the row cap is asserted through the handler's real mapping in
    // DmlInterruptedRunTests.Rpc_RowCapRefusal_GoesThroughTheHandlerMapping_WithItsFlagAndWhatAlreadyRan.

    // ---- --top ----------------------------------------------------------------

    [Theory]
    [InlineData(DeleteSql)]
    [InlineData("DELETE FROM account WHERE name = 'x'; DELETE FROM contact WHERE lastname = 'y'")]
    [InlineData("SELECT name FROM account; UPDATE account SET name = 'y' WHERE name = 'x'")]
    public async Task ExplicitTop_WithDml_IsRefusedBeforeExecuting_ExitCode3(string sql)
    {
        var h = Create(rows: 3);

        var ex = await Assert.ThrowsAsync<PpdsValidationException>(() => h.Service.ExecuteAsync(new SqlQueryRequest
        {
            Sql = sql,
            TopOverride = 5,
            TopIsUserSpecified = true,
            DmlSafety = new DmlSafetyOptions { IsConfirmed = true }
        }));

        Assert.Contains("--top limits returned rows only", ex.Message);
        Assert.Contains("INSERT, UPDATE or DELETE", ex.Message);
        Assert.Contains("WHERE clause", ex.Message);
        Assert.Equal(ExitCodes.InvalidArguments, ExceptionMapper.ToExitCode(ex));
        AssertNothingChanged(h);
    }

    [Fact]
    public async Task ExplicitTop_WithDmlDryRun_IsRefusedToo()
    {
        var h = Create(rows: 3);

        await Assert.ThrowsAsync<PpdsValidationException>(() => h.Service.ExecuteAsync(new SqlQueryRequest
        {
            Sql = DeleteSql,
            TopOverride = 5,
            TopIsUserSpecified = true,
            DmlSafety = new DmlSafetyOptions { IsDryRun = true }
        }));
    }

    [Fact]
    public async Task ExplicitTop_WithSelect_IsNotRefused()
    {
        var h = Create(rows: 3);

        var result = await h.Service.ExecuteAsync(new SqlQueryRequest
        {
            Sql = "SELECT name FROM account",
            TopOverride = 1,
            TopIsUserSpecified = true
        });

        Assert.NotNull(result.Result);
    }

    [Fact]
    public async Task DefaultTop_NotAskedForByTheUser_IsIgnoredForDml()
    {
        var h = Create(rows: 3);

        await h.Service.ExecuteAsync(new SqlQueryRequest
        {
            Sql = DeleteSql,
            TopOverride = 1, // e.g. the extension's default top
            DmlSafety = new DmlSafetyOptions { IsConfirmed = true }
        });

        Assert.Equal(new[] { 3 }, h.Deleted);
    }

    // ---- TUI -----------------------------------------------------------------

    [Fact]
    public async Task Tui_LoadMoreRequest_CanNeverRunDml()
    {
        var request = SqlQueryPresenter.BuildLoadMoreRequest(DeleteSql, 2, "cookie");

        Assert.NotNull(request.DmlSafety);
        Assert.False(request.DmlSafety!.IsConfirmed);
        Assert.False(request.DmlSafety.IsDryRun);
        Assert.Equal(2, request.PageNumber);
        Assert.Equal("cookie", request.PagingCookie);

        var h = Create(rows: 3);
        var ex = await Assert.ThrowsAsync<PpdsException>(() => h.Service.ExecuteAsync(request));
        Assert.Equal(ErrorCodes.Query.DmlBlocked, ex.ErrorCode); // a further page never runs DML, confirmed or not
        Assert.Contains("further page", ex.Message);
        AssertNothingChanged(h);
    }
}
