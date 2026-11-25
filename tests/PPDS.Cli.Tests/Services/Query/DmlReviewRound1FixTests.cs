using PPDS.Auth.Profiles;
using PPDS.Cli.Commands.Serve.Handlers;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services.Query;
using Xunit;

namespace PPDS.Cli.Tests.Services.Query;

/// <summary>
/// The first full review of the interim DML rule, end to end through <see cref="SqlQueryService"/> and the recording
/// harness: the whole-script rule (MF1), row ids compared in a condition (MF2), the values Dataverse stores
/// differently (MF4), a code per refusal class with the daemon's compatibility flag (MF5), an environment with no saved
/// type reported as Production (MF6), and a number literal PPDS cannot hold (S3). Every refusal is checked for
/// confirmed, unconfirmed and dry-run requests, with nothing read and nothing written.
/// </summary>
[Trait("Category", "Unit")]
public sealed class DmlReviewRound1FixTests
{
    private static readonly DmlSafetyOptions Confirmed = new() { IsConfirmed = true };

    private static readonly DmlSafetyOptions[] EveryRequest = [Confirmed, new DmlSafetyOptions(), new DmlSafetyOptions { IsDryRun = true }];

    private const string Guid = "00000000-0000-0000-0000-000000000002";

    private static async Task<PpdsException> RefusedEverywhereAsync(string sql, string code)
    {
        PpdsException? last = null;
        foreach (var options in EveryRequest)
        {
            var h = DmlTestHarness.Create(rows: 4, contactRows: 2);

            var ex = await Assert.ThrowsAsync<PpdsException>(() => h.Service.ExecuteAsync(new SqlQueryRequest { Sql = sql, DmlSafety = options }));

            Assert.Equal(code, ex.ErrorCode);
            Assert.Empty(h.Reads);
            h.AssertNothingChanged();
            last = ex;
        }
        return last!;
    }

    // ---- MF1: the whole-script rule ----------------------------------------------------------------------------

    /// <summary>Each case the review proved, and the rest of the decision's list.</summary>
    [Theory]
    // the IF condition is evaluated by the engine: 'a ' <> 'a' is false on SQL Server, so nothing should be deleted
    [InlineData("IF 'a ' <> 'a' DELETE FROM account WHERE name = 'n1'", "is an IF statement")]
    [InlineData("IF 'a ' = 'a' DELETE FROM account WHERE name = 'n1'", "is an IF statement")]
    [InlineData("IF N'\u00DF' = N'ss' DELETE FROM account WHERE name = 'n1'", "is an IF statement")]
    [InlineData("DELETE FROM account WHERE name = 'n1'; IF 'a ' <> 'a' DELETE FROM account WHERE name = 'n2'", "Statement 2 (line 1, column 40) is an IF statement")]
    // refused only after the first DELETE had committed, before the rule
    [InlineData("DELETE FROM account WHERE name = 'n1'; DECLARE @x int = 1; IF @x = 1 DELETE FROM account WHERE name = 'n2'", "Statement 2 (line 1, column 40) is DECLARE")]
    [InlineData("DELETE FROM account WHERE name = 'n1'; SELECT * INTO #t FROM account", "Statement 2 (line 1, column 40) is SELECT ... INTO")]
    // a SELECT with a client-side filter, and any other SELECT
    [InlineData("SELECT name FROM account WHERE revenue * 2 > 1500; DELETE FROM account WHERE name = 'n1'", "Statement 1 (line 1, column 1) is a SELECT")]
    [InlineData("SELECT name FROM account WHERE name = 'n1'; UPDATE account SET name = 'x' WHERE name = 'n1'", "is a SELECT")]
    [InlineData("DECLARE @i int = 0; WHILE @i < 1 BEGIN DELETE FROM account WHERE name = 'n1'; SET @i = @i + 1 END", "is DECLARE")]
    [InlineData("WHILE 1 = 0 DELETE FROM account WHERE name = 'n1'", "is a WHILE loop")]
    [InlineData("DELETE FROM account WHERE name = 'n1'; PRINT 'done'", "is PRINT")]
    [InlineData("DELETE FROM account WHERE name = 'n1'; THROW 50000, 'x', 1", "is THROW")]
    // an engine-raised error in TRY would drive the write in CATCH
    [InlineData("BEGIN TRY SELECT 1/0 END TRY BEGIN CATCH DELETE FROM account WHERE name = 'n1' END CATCH", "is TRY ... CATCH")]
    [InlineData("DELETE FROM account WHERE name = 'n1'; RETURN", "is RETURN")]
    [InlineData("DECLARE c CURSOR FOR SELECT name FROM account; DELETE FROM account WHERE name = 'n1'", "is a cursor statement")]
    [InlineData("EXEC WhoAmI; DELETE FROM account WHERE name = 'n1'", "is EXECUTE")]
    [InlineData("CREATE TABLE #t (name nvarchar(10)); DELETE FROM account WHERE name = 'n1'", "is CREATE TABLE")]
    [InlineData("DECLARE @t TABLE (name nvarchar(10)); DELETE FROM account WHERE name = 'n1'", "is DECLARE of a table variable")]
    [InlineData("BEGIN DELETE FROM account WHERE name = 'n1' END", "is a BEGIN ... END block")]
    public async Task AScriptThatWrites_WithAnythingButAdmittedDml_IsRefusedBeforeAnythingIsReadOrWritten(string sql, string named)
    {
        var ex = await RefusedEverywhereAsync(sql, ErrorCodes.Query.DmlUnsupportedConstruct);

        Assert.Contains(named, ex.Message);
        Assert.Contains("This script changes data", ex.Message);
        Assert.Contains("No row was read and nothing was written.", ex.Message);
    }

    [Fact]
    public async Task AScriptOfAdmittedWritesOnly_StillRuns()
    {
        var h = DmlTestHarness.Create(rows: 2, contactRows: 1);

        var result = await h.Service.ExecuteAsync(new SqlQueryRequest
        {
            Sql = "DELETE FROM account WHERE name = 'n1'; UPDATE contact SET lastname = 'b' WHERE lastname = 'y'; INSERT INTO account (name) VALUES (N'z')",
            DmlSafety = Confirmed
        });

        Assert.Equal(new[] { ("Delete", "account", 2), ("Update", "contact", 1), ("Create", "account", 1) }, h.Writes);
        Assert.NotNull(result.DmlOutcome);
    }

    [Fact]
    public async Task AScriptWithNoWrite_IsNotAffected()
    {
        var h = DmlTestHarness.Create(rows: 3);

        var result = await h.Service.ExecuteAsync(new SqlQueryRequest
        {
            Sql = "IF 1 = 1 SELECT name FROM account WHERE name = 'n1'",
            DmlSafety = Confirmed
        });

        Assert.Equal(3, result.Result.Count);
        h.AssertNothingChanged();
    }

    /// <summary>The guard's own refusal (a DELETE without WHERE) comes first, as the spec orders refusals.</summary>
    [Fact]
    public async Task TheGuardsOwnRefusal_ComesBeforeTheWholeScriptRule()
    {
        var ex = await RefusedEverywhereAsync("DELETE FROM account; PRINT 'x'", ErrorCodes.Query.DmlBlocked);

        Assert.DoesNotContain("This script changes data", ex.Message);
    }

    // ---- MF2: a row id or lookup compared in the condition of a write ---------------------------------------------

    [Theory]
    [InlineData("accountid", "' " + Guid + "'")]
    [InlineData("accountid", "'" + Guid + " '")]
    [InlineData("accountid", "'\t" + Guid + "'")]
    [InlineData("accountid", "N' " + Guid + "'")]
    [InlineData("accountid", "N'" + Guid + " '")]
    [InlineData("parentaccountid", "' " + Guid + "'")]
    [InlineData("parentaccountid", "'" + Guid + " '")]
    [InlineData("parentaccountid", "'" + Guid + "\t'")]
    [InlineData("parentaccountid", "N' " + Guid + "'")]
    public async Task AGuidWithWhiteSpaceAroundIt_InTheConditionOfAWrite_IsRefused_AsSqlServerRaisesAConversionError(string column, string literal)
    {
        foreach (var sql in new[] { $"DELETE FROM account WHERE {column} = {literal}", $"UPDATE account SET name = N'x' WHERE {column} = {literal}" })
        {
            var ex = await RefusedEverywhereAsync(sql, ErrorCodes.Query.DmlUnsupportedConstruct);
            Assert.Contains("with dashes and without spaces", ex.Message);
        }
    }

    [Theory]
    [InlineData("accountid", "'" + Guid + "'")]
    [InlineData("accountid", "'{" + Guid + "}'")]
    [InlineData("parentaccountid", "N'" + Guid + "'")]
    public async Task ACanonicalGuid_InTheConditionOfAWrite_IsSent(string column, string literal)
    {
        var h = DmlTestHarness.Create(rows: 1);

        await h.Service.ExecuteAsync(new SqlQueryRequest { Sql = $"DELETE FROM account WHERE {column} = {literal}", DmlSafety = Confirmed });

        Assert.Equal(new[] { 1 }, h.Deleted);
    }

    // ---- MF4: values Dataverse stores differently ---------------------------------------------------------------

    [Theory]
    [InlineData("UPDATE account SET name = '' WHERE name = 'n1'")]
    [InlineData("UPDATE account SET name = N'' WHERE name = 'n1'")]
    [InlineData("INSERT INTO account (name) VALUES ('')")]
    [InlineData("INSERT INTO account (name) SELECT '' AS v FROM account WHERE name = 'n1'")]
    public async Task EmptyText_IsRefused_WithTheMessageToWriteNull(string sql)
    {
        var ex = await RefusedEverywhereAsync(sql, ErrorCodes.Query.DmlValueStoredDifferently);

        Assert.Contains("Write NULL instead.", ex.Message);
    }

    [Theory]
    [InlineData("UPDATE account SET name = ' ' WHERE name = 'n1'")]
    [InlineData("UPDATE account SET name = 'a ' WHERE name = 'n1'")]
    [InlineData("UPDATE account SET description = N' a' WHERE name = 'n1'")]
    [InlineData("INSERT INTO account (name) VALUES ('   ')")]
    [InlineData("INSERT INTO account (name) VALUES (N'a\u00A0')")]
    [InlineData("INSERT INTO account (name) SELECT ' a' AS v FROM account WHERE name = 'n1'")]
    public async Task TextThatIsWhiteSpaceOrHasItAtEitherEnd_IsRefused_UntilTheRowsComeIntoForce(string sql)
    {
        var ex = await RefusedEverywhereAsync(sql, ErrorCodes.Query.DmlValueStoredDifferently);

        Assert.Contains("Dataverse has not been shown to store such text unchanged", ex.Message);
    }

    [Fact]
    public async Task TextWithASpaceInside_IsWrittenAsTyped()
    {
        var h = DmlTestHarness.Create(rows: 1);

        await h.Service.ExecuteAsync(new SqlQueryRequest { Sql = "UPDATE account SET name = 'a b' WHERE name = 'n1'", DmlSafety = Confirmed });

        Assert.Equal(new[] { 1 }, h.Updated);
    }

    // ---- MF5: a code per refusal class, and the daemon's compatibility flag ---------------------------------------

    [Theory]
    [InlineData("UPDATE account SET name = UPPER(name) WHERE name = 'n1'", ErrorCodes.Query.DmlUnsupportedConstruct)]
    [InlineData("IF 1 = 1 DELETE FROM account WHERE name = 'n1'", ErrorCodes.Query.DmlUnsupportedConstruct)]
    [InlineData("UPDATE account SET numberofemployees = 'x' WHERE name = 'n1'", ErrorCodes.Query.DmlUnsupportedConstruct)]
    [InlineData("UPDATE account SET nosuchcolumn = 1 WHERE name = 'n1'", ErrorCodes.Query.DmlUnsupportedConstruct)]
    [InlineData("UPDATE account SET name = '' WHERE name = 'n1'", ErrorCodes.Query.DmlValueStoredDifferently)]
    [InlineData("DELETE FROM account", ErrorCodes.Query.DmlBlocked)]
    [InlineData("UPDATE account SET name = 'x'", ErrorCodes.Query.DmlBlocked)]
    public async Task EachRefusalClass_HasItsOwnCode(string sql, string code)
        => await RefusedEverywhereAsync(sql, code);

    [Fact]
    public async Task AHintThatWasNotProvenForDml_IsAnUnsupportedConstruct()
    {
        var h = DmlTestHarness.Create(rows: 1);

        var ex = await Assert.ThrowsAsync<PpdsException>(() => h.Service.ExecuteAsync(new SqlQueryRequest
        {
            Sql = "-- ppds:NOLOCK\nDELETE FROM account WHERE name = 'n1'",
            DmlSafety = Confirmed
        }));

        Assert.Equal(ErrorCodes.Query.DmlUnsupportedConstruct, ex.ErrorCode);
        Assert.Empty(h.Reads);
    }

    [Theory]
    [InlineData(ErrorCodes.Query.DmlBlocked)]
    [InlineData(ErrorCodes.Query.DmlUnsupportedConstruct)]
    [InlineData(ErrorCodes.Query.DmlValueStoredDifferently)]
    [InlineData(ErrorCodes.Query.DmlMetadataUnavailable)]
    public void TheDaemon_SendsEachRefusalCode_WithDmlBlockedTrue(string code)
    {
        var refusal = new PpdsException(code, "Refused. Nothing was executed.");

        Assert.True(RpcMethodHandler.TryMapQuerySqlFailure(refusal, "sql", out var rpc));

        Assert.Equal(code, rpc!.StructuredErrorCode);
        var data = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(rpc.ErrorData, rpc.ErrorData!.GetType())).RootElement;
        Assert.True(data.GetProperty("dmlBlocked").GetBoolean());
        Assert.Equal(code, data.GetProperty("code").GetString());
    }

    [Fact]
    public void TheRefusalCodes_AreTheGuardsAndTheThreeClassesOnThisBranch()
        => Assert.Equal(
            new[] { "Query.DmlBlocked", "Query.DmlMetadataUnavailable", "Query.DmlUnsupportedConstruct", "Query.DmlValueStoredDifferently" },
            ErrorCodes.Query.DmlRefusalCodes.OrderBy(c => c, StringComparer.Ordinal));

    // ---- MF6: an environment with no saved type is Production, and reported so ---------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData(EnvironmentType.Unknown)]
    public async Task ASavedEntryWithNoTypeAndNoProtection_IsReportedAsProduction_InTheConfirmationTarget(EnvironmentType? type)
    {
        var h = DmlTestHarness.Create(rows: 1, protection: ProtectionLevel.Development);
        h.Service.ConfigureEnvironment("https://org.crm.dynamics.com", new EnvironmentConfig { Url = "https://org.crm.dynamics.com", Label = "ORG", Type = type });

        var ex = await Assert.ThrowsAsync<PpdsDmlConfirmationRequiredException>(() => h.Service.ExecuteAsync(new SqlQueryRequest
        {
            Sql = "DELETE FROM account WHERE name = 'n1'",
            DmlSafety = new DmlSafetyOptions()
        }));

        var target = Assert.Single(ex.Targets);
        Assert.Equal("Production", target.Protection);
        Assert.Equal(ProtectionLevel.Production, h.Service.EnvironmentProtectionLevel);
        Assert.Empty(h.Reads);
    }

    [Theory]
    [InlineData(EnvironmentType.Development, null, "Development")]
    [InlineData(EnvironmentType.Production, null, "Production")]
    [InlineData(null, ProtectionLevel.Test, "Test")]
    public void ASavedTypeOrProtection_IsUsedAsSaved(EnvironmentType? type, ProtectionLevel? protection, string expected)
    {
        var h = DmlTestHarness.Create(rows: 1);

        h.Service.ConfigureEnvironment("https://org.crm.dynamics.com", new EnvironmentConfig { Url = "https://org.crm.dynamics.com", Type = type, Protection = protection });

        Assert.Equal(expected, h.Service.EnvironmentProtectionLevel.ToString());
    }

    // ---- S3: a number literal PPDS cannot hold --------------------------------------------------------------------

    [Fact]
    public async Task AWrittenNumberWithMoreDigitsThanPpdsCanHold_IsRefusedWithACode_BeforeAnythingIsRead()
    {
        var ex = await RefusedEverywhereAsync("UPDATE account SET revenue = 100000000000000000000000000000 WHERE name = 'n1'",
            ErrorCodes.Query.DmlUnsupportedConstruct);

        Assert.Contains("a number with more digits than PPDS can hold", ex.Message);
    }

    [Fact]
    public async Task ASelectedNumberWithMoreDigitsThanPpdsCanHold_IsAPpdsException_WithACode_BeforeAnythingIsRead()
    {
        var h = DmlTestHarness.Create(rows: 1);

        var ex = await Assert.ThrowsAsync<PpdsException>(() => h.Service.ExecuteAsync(new SqlQueryRequest
        {
            Sql = "SELECT name, 100000000000000000000000000000 AS big FROM account"
        }));

        Assert.Equal(ErrorCodes.Query.ExecutionFailed, ex.ErrorCode);
        Assert.Contains("more digits than PPDS can hold", ex.Message);
        Assert.Empty(h.Reads);
    }
}
