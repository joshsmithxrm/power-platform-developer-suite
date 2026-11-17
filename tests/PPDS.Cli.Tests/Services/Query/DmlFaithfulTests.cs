using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services.Query;
using Xunit;

namespace PPDS.Cli.Tests.Services.Query;

/// <summary>
/// DML against a fake that evaluates the FetchXML filter, count/top and aliases like Dataverse, so a dropped condition,
/// a dropped TOP or a renamed key changes the rows or values the statement writes. Rows: name n0..n3,
/// revenue 500/1000/1500/2000, description null/d1/null/d3.
/// </summary>
[Trait("Category", "Unit")]
public sealed class DmlFaithfulTests : IDisposable
{
    private static readonly DmlSafetyOptions Confirmed = new() { IsConfirmed = true };

    public DmlFaithfulTests() => DmlTestHarness.FaithfulMode.Value = true;

    public void Dispose() => DmlTestHarness.FaithfulMode.Value = false;

    private static async Task<DmlTestHarness> Run(string sql)
    {
        var h = DmlTestHarness.Create(rows: 4);
        await h.Service.ExecuteAsync(new SqlQueryRequest { Sql = sql, DmlSafety = Confirmed });
        return h;
    }

    private static object?[] Values(DmlTestHarness h, string attribute)
        => h.WrittenEntities.Select(e => e.Attributes.TryGetValue(attribute, out var v) ? v : "<unset>").ToArray();

    // ---- WHERE selects exactly the rows the text says ------------------------------------------

    [Theory]
    [InlineData("name = 'n1'", 1)]
    [InlineData("name = 'N1'", 1)]
    [InlineData("revenue > 1000", 2)]
    [InlineData("revenue BETWEEN 1000 AND 1500", 2)]
    [InlineData("name IN ('n0', 'n3', 'zz')", 2)]
    [InlineData("name LIKE 'n%'", 4)]
    [InlineData("description IS NULL", 2)]
    [InlineData("name = 'n0' OR name = 'n1' AND revenue > 5000", 1)]
    [InlineData("(name = 'n0' OR name = 'n1') AND revenue > 500", 1)]
    // SQL: <>, NOT IN, NOT LIKE never match NULL (Dataverse would; the generator adds not-null).
    [InlineData("description <> 'd1'", 1)]
    [InlineData("description NOT IN ('d1')", 1)]
    [InlineData("description NOT LIKE 'd1%'", 1)]
    public async Task Where_SelectsExactlyTheMatchingRows(string where, int expected)
    {
        var h = await Run($"UPDATE account SET name = 'hit' WHERE {where}");

        Assert.Equal(expected, h.WrittenEntities.Count);
    }

    // ---- M1: TOP (n) is honoured exactly like TOP n ------------------------------------------

    [Theory]
    [InlineData("INSERT INTO account (name) SELECT TOP 2 name FROM account WHERE revenue > 0 ORDER BY revenue")]
    [InlineData("INSERT INTO account (name) SELECT TOP (2) name FROM account WHERE revenue > 0 ORDER BY revenue")]
    public async Task InsertSelectTop_WithOrWithoutParentheses_InsertsExactlyN(string sql)
    {
        var h = await Run(sql);

        Assert.Equal(new[] { ("Create", "account", 2) }, h.Writes);
    }

    [Fact]
    public async Task InsertSelect_HonoursItsWhereAndOrderBy_WithComputedItems()
    {
        var h = await Run("INSERT INTO account (name, description) SELECT name AS n, UPPER(name) AS d FROM account WHERE name = 'n1'");

        Assert.Equal(new object?[] { "n1" }, Values(h, "name"));
        Assert.Equal(new object?[] { "N1" }, Values(h, "description"));
    }

    [Fact]
    public async Task InsertSelectTopDescending_TakesTheHighestRows()
    {
        var h = await Run("INSERT INTO account (name) SELECT TOP (2) name FROM account WHERE revenue > 0 ORDER BY revenue DESC");

        Assert.Equal(new object?[] { "n3", "n2" }, Values(h, "name"));
    }

    [Theory]
    [InlineData("DELETE FROM account WHERE createdon < '2024-01-01'", "no time zone")]
    [InlineData("DELETE FROM account WHERE createdon BETWEEN '2024-01-01' AND '2024-02-01Z'", "no time zone")]
    [InlineData("UPDATE account SET followupby = '2024-01-01T00:00:00Z' WHERE revenue > 0", "date/time column")]
    [InlineData("INSERT INTO account (name, followupby) VALUES ('a', '2024-01-01Z')", "date/time column")]
    public async Task DateLiteralsWithoutAnOffset_AndDateWrites_AreRefused(string sql, string named)
    {
        var h = DmlTestHarness.Create(rows: 4);

        var ex = await Assert.ThrowsAsync<PpdsException>(() => h.Service.ExecuteAsync(new SqlQueryRequest { Sql = sql, DmlSafety = Confirmed }));

        Assert.Contains(named, ex.Message);
        h.AssertNothingChanged();
    }

    [Fact]
    public async Task DateLiteralWithAnOffset_IsAllowed()
    {
        var h = await Run("UPDATE account SET name = 'x' WHERE createdon < '2024-01-01T00:00:00Z'");
        Assert.NotNull(h);
    }

    [Fact]
    public async Task SelectTopInParentheses_ReturnsExactlyN()
    {
        var h = DmlTestHarness.Create(rows: 4);

        var result = await h.Service.ExecuteAsync(new SqlQueryRequest { Sql = "SELECT TOP (2) name FROM account" });

        Assert.Equal(2, result.Result.Count);
    }

    // ---- M3: aliased plain columns next to computed items ------------------------------------

    [Fact]
    public async Task InsertSelect_AliasedColumnNextToAComputedOne_MapsBothByPosition()
    {
        var h = await Run("INSERT INTO account (name, description) SELECT name AS n, UPPER(description) AS d FROM account WHERE revenue > 0");

        Assert.Equal(new object?[] { "n0", "n1", "n2", "n3" }, Values(h, "name"));
        Assert.Equal(new object?[] { null, "D1", null, "D3" }, Values(h, "description"));
    }

    [Fact]
    public async Task InsertSelect_AliasedColumnAndAComputedItemOfTheSameColumn_BothRead()
    {
        var h = await Run("INSERT INTO account (name, description) SELECT name AS n, UPPER(name) AS d FROM account WHERE revenue > 0");

        Assert.Equal(new object?[] { "n0", "n1", "n2", "n3" }, Values(h, "name"));
        Assert.Equal(new object?[] { "N0", "N1", "N2", "N3" }, Values(h, "description"));
    }

    [Fact]
    public async Task Select_AliasedColumnNextToAComputedOne_ReturnsTheValue()
    {
        var h = DmlTestHarness.Create(rows: 2);

        var result = await h.Service.ExecuteAsync(new SqlQueryRequest { Sql = "SELECT name AS n, UPPER(description) AS d FROM account" });

        Assert.Equal(new object?[] { "n0", "n1" }, result.Result.Records.Select(r => r["n"].Value));
    }

    // ---- M2 / S8: columns are checked against metadata; a missing value is an error ----------

    [Theory]
    [InlineData("UPDATE account SET description = owneridname WHERE revenue > 0", "display column")]
    [InlineData("UPDATE account SET description = CONCAT('Owner: ', owneridname) WHERE revenue > 0", "display column")]
    [InlineData("INSERT INTO account (name, description) SELECT name, owneridname FROM account WHERE revenue > 0", "display column")]
    [InlineData("UPDATE account SET description = nosuchcolumn WHERE revenue > 0", "has no column 'nosuchcolumn'")]
    [InlineData("UPDATE account SET nosuchcolumn = 'x' WHERE revenue > 0", "has no column 'nosuchcolumn'")]
    [InlineData("DELETE FROM account WHERE nosuchcolumn = 'x'", "has no column 'nosuchcolumn'")]
    [InlineData("INSERT INTO account (createdon) VALUES ('2024-01-01')", "cannot be set by INSERT")]
    [InlineData("UPDATE account SET accountid = 'x' WHERE revenue > 0", "cannot be set by UPDATE")]
    [InlineData("UPDATE account SET revenue = revenue + '100' WHERE revenue > 0", "+ with text")]
    [InlineData("UPDATE account SET revenue = revenue + description WHERE revenue > 0", "Arithmetic")]
    [InlineData("UPDATE account SET description = name * 2 WHERE revenue > 0", "Arithmetic")]
    public async Task ColumnsNotProvenToReadOrWriteCorrectly_AreRefused(string sql, string named)
    {
        var h = DmlTestHarness.Create(rows: 4);

        var ex = await Assert.ThrowsAsync<PpdsException>(() => h.Service.ExecuteAsync(new SqlQueryRequest { Sql = sql, DmlSafety = Confirmed }));

        Assert.Equal(ErrorCodes.Query.DmlBlocked, ex.ErrorCode);
        Assert.Contains(named, ex.Message);
        h.AssertNothingChanged();
    }

    [Fact]
    public async Task AColumnTheScanDoesNotReturn_StopsTheStatement_InsteadOfReadingNull()
    {
        // domainname is a real column, but the transpiler fetches it as "domain" (it looks like a *name display column).
        var h = DmlTestHarness.Create(rows: 4);

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => h.Service.ExecuteAsync(new SqlQueryRequest
        {
            Sql = "UPDATE account SET description = domainname WHERE revenue > 0",
            DmlSafety = Confirmed
        }));

        Assert.Contains("was not returned", ex.Message);
        h.AssertNothingChanged();
    }

    // ---- M4, M5, S2, N4, N5: refused shapes ---------------------------------------------------

    [Theory]
    [InlineData("DELETE FROM account WHERE name = 'ABC' COLLATE Latin1_General_CS_AS", "COLLATE")]
    [InlineData("DELETE FROM account WHERE name COLLATE Latin1_General_CS_AS = 'ABC'", "COLLATE")]
    [InlineData("UPDATE account SET name = 'x' WHERE name LIKE 'A%' COLLATE Latin1_General_CS_AS", "COLLATE")]
    [InlineData("UPDATE account SET description = name COLLATE Latin1_General_CS_AS WHERE revenue > 0", "COLLATE")]
    [InlineData("UPDATE account SET description = COALESCE([name]] FROM account --], 'x') WHERE name = 'never'", "not a valid Dataverse column name")]
    [InlineData("DELETE FROM account WHERE [accountid\" operator=\"not-null\"/><!--] = 'x'", "not a valid Dataverse column name")]
    [InlineData("DELETE FROM [account'x] WHERE name = 'x'", "not a valid Dataverse table name")]
    [InlineData("INSERT INTO account ([na me]) VALUES ('x')", "plain column name")]
    [InlineData("INSERT INTO account (name) SELECT name AS [n x] FROM account", "not a valid name")]
    [InlineData("UPDATE account SET revenue = +revenue WHERE revenue > 0", "+revenue")]
    [InlineData("UPDATE account SET description = CONVERT(varchar(8), revenue, 112) WHERE revenue > 0", "CONVERT style")]
    [InlineData("UPDATE account SET description = IIF(NOT (revenue > 1000), 'a', 'b') WHERE revenue > 0", "NOT")]
    [InlineData("UPDATE account SET name = 'a', name = 'b' WHERE revenue > 0", "more than once")]
    [InlineData("INSERT INTO account (contact.name) VALUES ('x')", "plain column name")]
    [InlineData("INSERT INTO account (name, description) SELECT TOP 5 description AS name, name AS description FROM account ORDER BY name", "also the alias")]
    public async Task NotOnTheAllowlist_IsRefused(string sql, string named)
    {
        var h = DmlTestHarness.Create(rows: 4);

        var ex = await Assert.ThrowsAsync<PpdsException>(() => h.Service.ExecuteAsync(new SqlQueryRequest { Sql = sql, DmlSafety = Confirmed }));

        Assert.Equal(ErrorCodes.Query.DmlBlocked, ex.ErrorCode);
        Assert.Contains(named, ex.Message);
        h.AssertNothingChanged();
        Assert.Empty(h.Reads);
    }

    [Fact]
    public async Task RTrim_RemovesTrailingSpacesOnly_LikeTsql()
    {
        var h = await Run("UPDATE account SET description = RTRIM(CONCAT('a', '\t  ')) WHERE name = 'n1'");

        Assert.Equal(new object?[] { "a\t" }, Values(h, "description"));
    }
}

/// <summary>Bypass hints, hint parsing and hint reporting (S3, S6, N3, N7).</summary>
[Trait("Category", "Unit")]
public class DmlHintGateTests
{
    private const string Update = "UPDATE account SET name = 'y' WHERE name = 'x'";

    [Fact]
    public async Task BypassHint_IsNamedInTheConfirmation_OnEverySurface()
    {
        var h = DmlTestHarness.Create(rows: 1);

        var ex = await Assert.ThrowsAsync<PpdsDmlConfirmationRequiredException>(() => h.Service.ExecuteAsync(
            new SqlQueryRequest { Sql = "-- ppds:BYPASS_PLUGINS\n" + Update, DmlSafety = new DmlSafetyOptions() }));

        Assert.Equal(new[] { "BYPASS_PLUGINS" }, ex.Bypasses);
        Assert.Contains("bypasses synchronous custom plugins", ex.UserMessage);
        var cli = PPDS.Cli.Commands.Query.DmlSafetyOptionSet.WithCliWording(ExceptionMapper.Map(ex), ex);
        Assert.Contains("--confirm", cli.Message);
        Assert.Contains("bypasses synchronous custom plugins", cli.Message);
        Assert.True(PPDS.Cli.Commands.Serve.Handlers.RpcMethodHandler.TryMapQuerySqlFailure(ex, Update, out var rpc));
        var data = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(rpc!.ErrorData, rpc.ErrorData!.GetType())).RootElement;
        Assert.Equal("BYPASS_PLUGINS", data.GetProperty("dmlBypass")[0].GetString());
    }

    [Theory]
    [InlineData("-- ppds:BYPASS_PLUGINS\n")]
    [InlineData("-- ppds:BYPASS_FLOWS\n")]
    public async Task BypassHint_InProduction_IsRefused_UnlessTheEnvironmentAlreadyBypasses(string hint)
    {
        var h = DmlTestHarness.Create(rows: 1, protection: PPDS.Auth.Profiles.ProtectionLevel.Production);

        var ex = await Assert.ThrowsAsync<PpdsException>(() => h.Service.ExecuteAsync(
            new SqlQueryRequest { Sql = hint + Update, DmlSafety = new DmlSafetyOptions { IsConfirmed = true } }));
        Assert.Equal(ErrorCodes.Query.DmlBlocked, ex.ErrorCode);
        Assert.Contains("Production", ex.Message);
        h.AssertNothingChanged();

        h.Service.EnvironmentSafetySettings = new PPDS.Auth.Profiles.QuerySafetySettings
        {
            BypassCustomPlugins = PPDS.Auth.Profiles.BypassPluginMode.Synchronous,
            BypassPowerAutomateFlows = true
        };
        await h.Service.ExecuteAsync(new SqlQueryRequest { Sql = hint + Update, DmlSafety = new DmlSafetyOptions { IsConfirmed = true } });
    }

    [Fact]
    public async Task BypassHint_InAScript_IsRefused_SoItCannotSpreadToOtherStatements()
    {
        var h = DmlTestHarness.Create(rows: 1);

        var ex = await Assert.ThrowsAsync<PpdsException>(() => h.Service.ExecuteAsync(new SqlQueryRequest
        {
            Sql = "UPDATE contact SET lastname = 'b' WHERE lastname = 'y';\n-- ppds:BYPASS_PLUGINS\n" + Update,
            DmlSafety = new DmlSafetyOptions { IsConfirmed = true }
        }));

        Assert.Contains("single", ex.Message);
        h.AssertNothingChanged();
    }

    [Fact]
    public async Task HintOptions_OverlayTheConfiguredBulkOptions()
    {
        var h = DmlTestHarness.Create(rows: 1);
        var configured = new PPDS.Dataverse.BulkOperations.BulkOperationOptions { ContinueOnError = false, MaxParallelBatches = 3 };
        Moq.Mock.Get(h.Bulk.Object).Setup(b => b.ConfiguredOptions).Returns(configured);

        await h.Service.ExecuteAsync(new SqlQueryRequest { Sql = Update + " -- ppds:BATCH_SIZE 7", DmlSafety = new DmlSafetyOptions { IsConfirmed = true } });

        Assert.Equal(7, h.LastOptions!.BatchSize);
        Assert.False(h.LastOptions.ContinueOnError);
        Assert.Equal(3, h.LastOptions.MaxParallelBatches);
        Assert.Equal(100, configured.BatchSize); // the configured instance is not changed
    }

    [Theory]
    [InlineData("SELECT name FROM account -- ppds:NOLOCK (avoid blocking)")]
    [InlineData("SELECT name FROM account -- ppds:MAX_ROWS +2")]
    public async Task SelectHints_KeepTheirLenientParsing(string sql)
    {
        var h = DmlTestHarness.Create(rows: 3);

        var result = await h.Service.ExecuteAsync(new SqlQueryRequest { Sql = sql });

        Assert.NotNull(result.AppliedHints);
    }

    [Fact]
    public async Task AUrlThatContainsPpdsColon_IsNotAHint()
    {
        var h = DmlTestHarness.Create(rows: 1);

        await h.Service.ExecuteAsync(new SqlQueryRequest
        {
            Sql = Update + " -- see github.com/org/ppds: cleanup",
            DmlSafety = new DmlSafetyOptions { IsConfirmed = true }
        });

        Assert.Single(h.Writes);
    }

    [Fact]
    public async Task HashGroupOnDml_IsNotReportedAsApplied()
    {
        var h = DmlTestHarness.Create(rows: 1);

        var result = await h.Service.ExecuteAsync(new SqlQueryRequest { Sql = "-- ppds:HASH_GROUP\n" + Update, DmlSafety = new DmlSafetyOptions { IsConfirmed = true } });

        Assert.True(result.AppliedHints is null || !result.AppliedHints.Contains("HASH_GROUP"));
    }
}
