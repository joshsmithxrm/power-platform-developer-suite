using Moq;
using PPDS.Auth.Profiles;
using PPDS.Cli.Commands.Serve.Handlers;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services;
using PPDS.Cli.Services.Query;
using PPDS.Dataverse.Query;
using Xunit;

namespace PPDS.Cli.Tests.Services.Query;

/// <summary>
/// A DML statement affects exactly the rows its own text says, or nothing: no hint, paging argument or
/// unsupported clause may narrow or widen it, and anything the engine cannot honour is refused before any change.
/// </summary>
[Trait("Category", "Unit")]
public class DmlStatementFidelityTests
{
    private static readonly DmlSafetyOptions Confirmed = new() { IsConfirmed = true };

    private const string DeleteSql = "DELETE FROM account WHERE name = 'x'";

    // ---- MAX_ROWS hint (M1) ------------------------------------------------------------------

    [Theory]
    [InlineData("DELETE FROM account WHERE name = 'x' -- ppds:MAX_ROWS 2")]
    [InlineData("-- ppds:MAX_ROWS 2\nUPDATE account SET name = 'y' WHERE name = 'x'")]
    [InlineData("-- ppds:MAX_ROWS 2\nINSERT INTO account (name) SELECT name FROM account WHERE name = 'x'")]
    [InlineData("-- ppds:MAX_ROWS 2\nSELECT name FROM account; DELETE FROM account WHERE name = 'x'")]
    public async Task MaxRowsHint_WithDml_IsRefusedBeforeAnythingChanges(string sql)
    {
        var h = DmlTestHarness.Create(rows: 5);

        var ex = await Assert.ThrowsAsync<PpdsValidationException>(
            () => h.Service.ExecuteAsync(new SqlQueryRequest { Sql = sql, DmlSafety = Confirmed }));

        Assert.Contains("MAX_ROWS", ex.Message);
        Assert.Contains("INSERT, UPDATE or DELETE", ex.Message);
        Assert.Contains("nothing was executed", ex.Message);
        Assert.Contains("WHERE clause", ex.Message);
        Assert.DoesNotContain("--", ex.Message.Replace("-- ppds:MAX_ROWS", string.Empty)); // no CLI flag: every surface sees it
        Assert.Equal(ExitCodes.InvalidArguments, ExceptionMapper.ToExitCode(ex));
        h.AssertNothingChanged();
    }

    [Fact]
    public async Task MaxRowsHint_WithDmlDryRun_IsRefusedToo()
    {
        var h = DmlTestHarness.Create(rows: 5);

        await Assert.ThrowsAsync<PpdsValidationException>(() => h.Service.ExecuteAsync(new SqlQueryRequest
        {
            Sql = "DELETE FROM account WHERE name = 'x' -- ppds:MAX_ROWS 2",
            DmlSafety = new DmlSafetyOptions { IsDryRun = true }
        }));
    }

    [Fact]
    public async Task MaxRowsHint_WithDml_IsRefusedBeforeAskingForConfirmation()
    {
        var h = DmlTestHarness.Create(rows: 5);

        // Unconfirmed: the refusal (exit 3) comes first, so confirming would not make it run.
        await Assert.ThrowsAsync<PpdsValidationException>(() => h.Service.ExecuteAsync(new SqlQueryRequest
        {
            Sql = "DELETE FROM account WHERE name = 'x' -- ppds:MAX_ROWS 2",
            DmlSafety = new DmlSafetyOptions()
        }));
    }

    [Fact]
    public async Task MaxRowsHint_WithSelect_IsAppliedAndReported()
    {
        var h = DmlTestHarness.Create(rows: 5);

        var result = await h.Service.ExecuteAsync(new SqlQueryRequest { Sql = "SELECT name FROM account -- ppds:MAX_ROWS 2" });

        Assert.Equal(2, result.Result.Count);
        Assert.Contains("MAX_ROWS", result.AppliedHints!);
    }

    [Fact]
    public async Task MaxRowsHint_WithSelectThatHasItsOwnTop_IsNotReportedAsApplied()
    {
        var h = DmlTestHarness.Create(rows: 5);

        var result = await h.Service.ExecuteAsync(new SqlQueryRequest { Sql = "SELECT TOP 3 name FROM account -- ppds:MAX_ROWS 2" });

        Assert.Equal(3, result.Result.Count); // the statement's own TOP wins
        Assert.True(result.AppliedHints is null || !result.AppliedHints.Contains("MAX_ROWS"));
    }

    // ---- hints on DML are applied exactly, or refused (S3) --------------------------------------

    [Theory]
    [InlineData("DELETE FROM account WHERE name = 'x' -- ppds:MAX_ROWS 5;")]
    [InlineData("DELETE FROM account WHERE name = 'x' -- ppds:MAX_ROWS=5")]
    [InlineData("DELETE FROM account WHERE name = 'x' -- ppds:MAX_ROWS 1,000")]
    [InlineData("DELETE FROM account WHERE name = 'x' /* ppds:MAX_ROWS 5 */")]
    [InlineData("DELETE FROM account WHERE name = 'x' -- ppds:BYPASS_PLUGIN")]
    [InlineData("/* ppds:BYPASS_PLUGINS */ DELETE FROM account WHERE name = 'x'")]
    [InlineData("DELETE FROM account WHERE name = 'x' -- run with ppds:BYPASS_FLOWS please")]
    [InlineData("DELETE FROM account WHERE name = 'x' -- ppds:BATCH_SIZE 5000")]
    public async Task HintLikeComment_ThatIsNotAnExactSupportedHint_IsRefusedForDml(string sql)
    {
        var h = DmlTestHarness.Create(rows: 3);

        var ex = await Assert.ThrowsAsync<PpdsValidationException>(
            () => h.Service.ExecuteAsync(new SqlQueryRequest { Sql = sql, DmlSafety = Confirmed }));

        Assert.Contains("ppds:", ex.Message);
        Assert.Contains("nothing was executed", ex.Message);
        h.AssertNothingChanged();
    }

    [Fact]
    public async Task HintLikeComment_OnSelect_IsStillIgnored()
    {
        var h = DmlTestHarness.Create(rows: 3);

        var result = await h.Service.ExecuteAsync(new SqlQueryRequest { Sql = "SELECT name FROM account -- ppds:MAX_ROWS=2" });

        Assert.Equal(3, result.Result.Count);
    }

    [Fact]
    public async Task BypassPluginsAndFlowsHints_AreAppliedToTheDmlWrite()
    {
        var h = DmlTestHarness.Create(rows: 2);

        var result = await h.Service.ExecuteAsync(new SqlQueryRequest
        {
            Sql = "-- ppds:BYPASS_PLUGINS\n-- ppds:BYPASS_FLOWS\nUPDATE account SET name = 'y' WHERE name = 'x'",
            DmlSafety = Confirmed
        });

        Assert.Equal(PPDS.Dataverse.BulkOperations.CustomLogicBypass.Synchronous, h.LastOptions!.BypassCustomLogic);
        Assert.True(h.LastOptions.BypassPowerAutomateFlows);
        Assert.Contains("BYPASS_PLUGINS", result.AppliedHints!);
        Assert.Contains("BYPASS_FLOWS", result.AppliedHints!);
    }

    [Fact]
    public async Task BatchSizeHint_IsAppliedToTheDmlWrite_AndReported()
    {
        var h = DmlTestHarness.Create(rows: 2);

        var result = await h.Service.ExecuteAsync(new SqlQueryRequest
        {
            Sql = "DELETE FROM account WHERE name = 'x' -- ppds:BATCH_SIZE 7",
            DmlSafety = Confirmed
        });

        Assert.Equal(7, h.LastOptions!.BatchSize);
        Assert.Contains("BATCH_SIZE", result.AppliedHints!);
    }

    [Fact]
    public async Task BatchSizeHint_OnSelect_IsNotReportedAsApplied()
    {
        var h = DmlTestHarness.Create(rows: 2);

        var result = await h.Service.ExecuteAsync(new SqlQueryRequest { Sql = "SELECT name FROM account -- ppds:BATCH_SIZE 7" });

        Assert.True(result.AppliedHints is null || !result.AppliedHints.Contains("BATCH_SIZE"));
    }

    // ---- clauses the DML planner cannot honour --------------------------------------------------

    [Theory]
    [InlineData("DELETE TOP (2) FROM account WHERE name = 'x'", "TOP")]
    [InlineData("UPDATE TOP (2) account SET name = 'y' WHERE name = 'x'", "TOP")]
    [InlineData("INSERT TOP (2) INTO account (name) SELECT name FROM account WHERE name = 'x'", "TOP")]
    [InlineData("DELETE FROM account OUTPUT deleted.accountid WHERE name = 'x'", "OUTPUT")]
    [InlineData("UPDATE account SET name = 'y' OUTPUT inserted.name WHERE name = 'x'", "OUTPUT")]
    [InlineData("INSERT INTO account (name) OUTPUT inserted.name VALUES ('a')", "OUTPUT")]
    [InlineData("UPDATE account SET name += 'y' WHERE name = 'x'", "+=")]
    [InlineData("DECLARE @v nvarchar(10); UPDATE account SET @v = name WHERE name = 'x'", "variable")]
    [InlineData("INSERT INTO account VALUES ('a')", "column list")]
    [InlineData("INSERT INTO account (name) VALUES ('a', 'b')", "2 value(s)")]
    [InlineData("INSERT INTO account (name, revenue) VALUES ('a')", "1 value(s)")]
    [InlineData("INSERT INTO account DEFAULT VALUES", "DEFAULT VALUES")]
    [InlineData("INSERT INTO account (name) EXEC getnames", "EXEC")]
    [InlineData("INSERT INTO account (name) SELECT name FROM account UNION SELECT name FROM contact", "UNION")]
    [InlineData("INSERT INTO account (name) SELECT * FROM account WHERE name = 'x'", "*")]
    [InlineData("INSERT INTO account (name) SELECT name, accountid FROM account WHERE name = 'x'", "2 columns")]
    [InlineData("WITH c AS (SELECT accountid FROM account) UPDATE account SET name = 'y' WHERE name = 'x'", "WITH")]
    [InlineData("MERGE account AS t USING contact AS s ON t.name = s.lastname WHEN NOT MATCHED THEN INSERT (name) VALUES (s.lastname);", "MERGE")]
    [InlineData("SELECT name FROM account; BEGIN TRY DELETE TOP (1) FROM account WHERE name = 'x' END TRY BEGIN CATCH END CATCH", "TOP")]
    public async Task UnsupportedClause_IsRefusedBeforeAnythingChanges_EvenUnconfirmed(string sql, string named)
    {
        foreach (var options in new[] { Confirmed, new DmlSafetyOptions(), new DmlSafetyOptions { IsDryRun = true } })
        {
            var h = DmlTestHarness.Create(rows: 5);

            var ex = await Assert.ThrowsAsync<PpdsException>(
                () => h.Service.ExecuteAsync(new SqlQueryRequest { Sql = sql, DmlSafety = options }));

            Assert.Equal(ErrorCodes.Query.DmlBlocked, ex.ErrorCode);
            Assert.Contains(named, ex.Message);
            Assert.Contains("Nothing was executed", ex.Message);
            h.AssertNothingChanged();
        }
    }

    [Fact]
    public async Task InsertSelectTop_TheTopBelongsToTheSelect_AndKeepsWorking()
    {
        var h = DmlTestHarness.Create(rows: 5);

        var result = await h.Service.ExecuteAsync(new SqlQueryRequest
        {
            Sql = "INSERT INTO account (name) SELECT TOP 2 name FROM account WHERE name = 'x'",
            DmlSafety = Confirmed
        });

        Assert.Equal(new[] { 2 }, h.Created);
        Assert.Equal(2, result.DmlOutcome!.AffectedRows);
    }

    [Fact]
    public async Task UpdateWithJoinedFrom_IsRefused_UntilTheJoinIsProven()
    {
        // A join can return a target row more than once and its WHERE is evaluated outside Dataverse; not on the allowlist.
        var h = DmlTestHarness.Create(rows: 3);

        var ex = await Assert.ThrowsAsync<PpdsException>(() => h.Service.ExecuteAsync(new SqlQueryRequest
        {
            Sql = "UPDATE account SET name = 'y' FROM account INNER JOIN contact ON contact.parentcustomerid = account.accountid WHERE account.name = 'x'",
            DmlSafety = Confirmed
        }));

        Assert.Equal(ErrorCodes.Query.DmlBlocked, ex.ErrorCode);
        h.AssertNothingChanged();
    }

    // ---- other statement kinds ----------------------------------------------------------------

    [Theory]
    [InlineData("EXEC WhoAmI")]
    [InlineData("EXEC sp_executesql N'DELETE FROM account WHERE name = ''x'''")]
    [InlineData("EXEC('DELETE FROM account WHERE name = ''x''')")]
    [InlineData("TRUNCATE TABLE account")]
    [InlineData("DROP TABLE account")]
    [InlineData("CREATE TABLE account (name nvarchar(10))")]
    [InlineData("ALTER TABLE account ADD x int")]
    [InlineData("DECLARE c CURSOR FOR SELECT name FROM account")]
    public async Task OtherStatements_NeverChangeData(string sql)
    {
        var h = DmlTestHarness.Create(rows: 3);

        await Assert.ThrowsAnyAsync<Exception>(
            () => h.Service.ExecuteAsync(new SqlQueryRequest { Sql = sql, DmlSafety = Confirmed }));

        h.AssertNothingChanged();
        h.Bulk.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("EXECUTE AS USER = 'someone@contoso.com'; DELETE FROM account WHERE name = 'x'")]
    [InlineData("BEGIN TRY EXECUTE AS USER = 'someone@contoso.com' END TRY BEGIN CATCH END CATCH; DELETE FROM account WHERE name = 'x'")]
    [InlineData("REVERT")]
    public async Task Impersonation_IsRefusedBeforeAnything_SoNoStatementRunsAsTheWrongUser(string sql)
    {
        var h = DmlTestHarness.Create(rows: 3);

        var ex = await Assert.ThrowsAsync<PpdsException>(
            () => h.Service.ExecuteAsync(new SqlQueryRequest { Sql = sql, DmlSafety = Confirmed }));

        Assert.Equal(ErrorCodes.Query.DmlBlocked, ex.ErrorCode);
        Assert.Contains("not supported", ex.Message);
        h.AssertNothingChanged();
    }

    // ---- writes to another environment ----------------------------------------------------------

    private static DmlTestHarness WithRemote(string label, CrossEnvironmentDmlPolicy policy, EnvironmentType type = EnvironmentType.Development)
    {
        var h = DmlTestHarness.Create(rows: 2);
        var remote = new Mock<IQueryExecutor>(MockBehavior.Loose);
        remote.Setup(x => x.ExecuteFetchXmlAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DmlTestHarness.Rows(2));
        remote.Setup(x => x.ExecuteFetchXmlAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<QueryExecutionOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DmlTestHarness.Rows(2));
        h.Service.RemoteExecutorFactory = l => l == label ? remote.Object : null;
        h.Service.ProfileResolver = new ProfileResolutionService(
        [
            new EnvironmentConfig
            {
                Url = "https://qa.crm.dynamics.com/",
                Label = label,
                Type = type,
                SafetySettings = new QuerySafetySettings { CrossEnvironmentDmlPolicy = policy }
            }
        ]);
        return h;
    }

    [Theory]
    [InlineData("INSERT INTO [QA].dbo.account (name) VALUES ('a')")]
    [InlineData("DELETE FROM [QA].dbo.account WHERE name = 'x'")]
    [InlineData("DELETE FROM [QA].account WHERE name = 'x'")]
    [InlineData("UPDATE [QA].account SET name = 'y' WHERE name = 'x'")]
    [InlineData("UPDATE qa SET name = 'y' FROM [QA].account AS qa WHERE qa.name = 'x'")]
    public async Task WriteToAnotherEnvironment_IsRefused_InsteadOfRunningAgainstTheConnectedOne(string sql)
    {
        foreach (var policy in new[] { CrossEnvironmentDmlPolicy.Allow, CrossEnvironmentDmlPolicy.Prompt })
        {
            var h = WithRemote("QA", policy);

            var ex = await Assert.ThrowsAsync<PpdsException>(
                () => h.Service.ExecuteAsync(new SqlQueryRequest { Sql = sql, DmlSafety = Confirmed }));

            Assert.Equal(ErrorCodes.Query.DmlBlocked, ex.ErrorCode);
            Assert.Contains("[QA]", ex.Message);
            Assert.Contains("not supported", ex.Message);
            Assert.Contains("Nothing was executed", ex.Message);
            h.AssertNothingChanged();
        }
    }

    [Fact]
    public async Task ReadFromAnotherEnvironment_IntoTheConnectedOne_IsRefused()
    {
        // The remote read path fetches every row and attribute and drops the WHERE, so INSERT ... SELECT FROM [QA]
        // would copy every QA row. Not on the allowlist until the remote read honours its WHERE.
        var h = WithRemote("QA", CrossEnvironmentDmlPolicy.ReadOnly);

        var ex = await Assert.ThrowsAsync<PpdsException>(() => h.Service.ExecuteAsync(new SqlQueryRequest
        {
            Sql = "INSERT INTO account (name) SELECT name FROM [QA].account WHERE name = 'x'",
            DmlSafety = Confirmed
        }));

        Assert.Equal(ErrorCodes.Query.DmlBlocked, ex.ErrorCode);
        Assert.Contains("another environment", ex.Message);
        h.AssertNothingChanged();
    }

    // ---- paging arguments (S4) ----------------------------------------------------------------

    [Theory]
    [InlineData(1, null)]
    [InlineData(null, "cookie")]
    public async Task ExplicitPaging_WithDml_IsRefusedBeforeExecuting_ExitCode3(int? page, string? cookie)
    {
        var h = DmlTestHarness.Create(rows: 2, pages: 3);

        var ex = await Assert.ThrowsAsync<PpdsValidationException>(() => h.Service.ExecuteAsync(new SqlQueryRequest
        {
            Sql = DeleteSql,
            PageNumber = page,
            PagingCookie = cookie,
            PagingIsUserSpecified = true,
            DmlSafety = Confirmed
        }));

        Assert.Contains("--page", ex.Message);
        Assert.Contains("INSERT, UPDATE or DELETE", ex.Message);
        Assert.Equal(ExitCodes.InvalidArguments, ExceptionMapper.ToExitCode(ex));
        h.AssertNothingChanged();
    }

    [Fact]
    public async Task FirstPageArgument_NotAskedForByTheUser_NeverReachesDml_EveryPageIsAffected()
    {
        var h = DmlTestHarness.Create(rows: 2, pages: 3);

        await h.Service.ExecuteAsync(new SqlQueryRequest { Sql = DeleteSql, PageNumber = 1, DmlSafety = Confirmed });

        Assert.Equal(new[] { 6 }, h.Deleted); // all three pages of matches, not one
    }

    [Theory]
    [InlineData(2, null)]
    [InlineData(1, "cookie")]
    [InlineData(null, "cookie")]
    public async Task LoadMoreRequest_CanNeverExecuteDml_EvenConfirmed(int? page, string? cookie)
    {
        var h = DmlTestHarness.Create(rows: 2, pages: 3);
        var request = RpcMethodHandler.BuildSqlRequest(new QuerySqlRequest
        {
            Sql = DeleteSql,
            Page = page,
            PagingCookie = cookie,
            DmlSafety = new DmlSafetyRpcOptions { IsConfirmed = true }
        });

        var ex = await Assert.ThrowsAsync<PpdsException>(() => h.Service.ExecuteAsync(request));

        Assert.Equal(ErrorCodes.Query.DmlBlocked, ex.ErrorCode);
        Assert.Contains("further page", ex.Message);
        h.AssertNothingChanged();
    }

    [Fact]
    public async Task Paging_WithSelect_StillPassesThePage()
    {
        var h = DmlTestHarness.Create(rows: 2, pages: 3);

        var result = await h.Service.ExecuteAsync(new SqlQueryRequest
        {
            Sql = "SELECT name FROM account",
            PageNumber = 2,
            PagingCookie = "cookie",
            PagingIsUserSpecified = true
        });

        Assert.Equal(2, result.Result.Count);
        Assert.Contains(h.Reads, r => r.Page == 2 && r.Cookie == "cookie");
    }

    // ---- guard refusals are not catchable by script code (S1) --------------------------------

    [Fact]
    public async Task RowCapRefusal_InsideTry_EndsTheScript_KeepsItsCode_AndRunsNothingAfter()
    {
        var h = DmlTestHarness.Create(rows: 5, contactRows: 1); // the UPDATE after the CATCH is within the cap

        var ex = await Assert.ThrowsAsync<PpdsDmlRowCapExceededException>(() => h.Service.ExecuteAsync(new SqlQueryRequest
        {
            Sql = "BEGIN TRY DELETE FROM account WHERE name = 'x' END TRY BEGIN CATCH PRINT 'swallowed' END CATCH; UPDATE contact SET lastname = 'b' WHERE lastname = 'y'",
            DmlSafety = new DmlSafetyOptions { IsConfirmed = true, RowCap = 2 }
        }));

        Assert.Equal(ErrorCodes.Query.DmlRowCapExceeded, ex.ErrorCode);
        h.AssertNothingChanged();
    }

    [Fact]
    public async Task OrdinaryErrors_InsideTry_AreStillCaught()
    {
        var h = DmlTestHarness.Create(rows: 2);

        await h.Service.ExecuteAsync(new SqlQueryRequest
        {
            Sql = "BEGIN TRY THROW 50001, 'boom', 1; END TRY BEGIN CATCH PRINT 'caught' END CATCH; DELETE FROM account WHERE name = 'x'",
            DmlSafety = Confirmed
        });

        Assert.Equal(new[] { 2 }, h.Deleted);
    }
}
