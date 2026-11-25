using PPDS.Auth.Profiles;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services.Query;
using Xunit;

namespace PPDS.Cli.Tests.Services.Query;

/// <summary>
/// DML runs only when every part of the statement is on the allowlist of shapes the engine honours; anything else is
/// refused before any change, naming the part. The harness returns only the attributes the FetchXML requests (like
/// Dataverse), and rows: name n0..n3, revenue 500/1000/1500/2000, description null/d1/null/d3.
/// </summary>
[Trait("Category", "Unit")]
public class DmlAllowlistTests
{
    private static readonly DmlSafetyOptions Confirmed = new() { IsConfirmed = true };

    private static async Task<DmlTestHarness> Run(string sql, int rows = 4)
    {
        var h = DmlTestHarness.Create(rows: rows);
        await h.Service.ExecuteAsync(new SqlQueryRequest { Sql = sql, DmlSafety = Confirmed });
        return h;
    }

    private static object?[] Values(DmlTestHarness h, string attribute)
        => h.WrittenEntities.Select(e => e.Attributes.TryGetValue(attribute, out var v) ? Normalise(v) : "<unset>").ToArray();

    private static object? Normalise(object? value) => value switch
    {
        decimal d => d.ToString(System.Globalization.CultureInfo.InvariantCulture),
        double d => d.ToString(System.Globalization.CultureInfo.InvariantCulture),
        int i => i.ToString(System.Globalization.CultureInfo.InvariantCulture),
        long l => l.ToString(System.Globalization.CultureInfo.InvariantCulture),
        Microsoft.Xrm.Sdk.Money m => m.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ => value
    };

    // ---- M2: SET values see the row's real values (interim rule: literals, columns and NULL) ----------

    [Theory]
    [InlineData("name", "n0|n1|n2|n3")]
    [InlineData("description", "<null>|d1|<null>|d3")]
    [InlineData("'lit'", "lit|lit|lit|lit")]
    [InlineData("N'naïve'", "naïve|naïve|naïve|naïve")]
    [InlineData("NULL", "<null>|<null>|<null>|<null>")]
    public async Task SetValue_ReadsTheRowsRealValues(string value, string expected)
    {
        var h = await Run($"UPDATE account SET description = {value} WHERE name IS NOT NULL");

        Assert.Equal(expected.Split('|').Select(v => v == "<null>" ? null : v), Values(h, "description"));
    }

    [Theory]
    [InlineData("revenue", "500|1000|1500|2000")]
    [InlineData("-2.5", "-2.5|-2.5|-2.5|-2.5")]
    [InlineData("-7", "-7|-7|-7|-7")]
    public async Task SetNumericValue_ReadsTheRowsRealValues(string value, string expected)
    {
        var h = await Run($"UPDATE account SET revenue = {value} WHERE name IS NOT NULL");

        var actual = Values(h, "revenue").Select(v => v is string s && s.Contains('.') ? s.TrimEnd('0').TrimEnd('.') : v);
        Assert.Equal(expected.Split('|').Select(v => v == "<null>" ? null : v), actual);
    }

    /// <summary>The expressions these tests used to write are computed values, refused until the typed engine proves them.</summary>
    [Theory]
    [InlineData("COALESCE(description, name)")]
    [InlineData("ISNULL(description, 'none')")]
    [InlineData("IIF(revenue > 1000, 'big', name)")]
    [InlineData("CASE WHEN revenue > 1000 THEN 'big' ELSE 'small' END")]
    [InlineData("UPPER(name)")]
    [InlineData("LTRIM(RTRIM(name))")]
    [InlineData("CONCAT(name, '-', description)")]
    [InlineData("SUBSTRING(name, 2, 1)")]
    [InlineData("(name)")]
    [InlineData("LEN(name)")]
    [InlineData("revenue * 2")]
    [InlineData("ABS(0 - revenue)")]
    [InlineData("-revenue")]
    public async Task SetComputedValue_IsRefusedBeforeAnythingIsRead(string expression)
    {
        var h = DmlTestHarness.Create(rows: 4);

        var ex = await Assert.ThrowsAsync<PpdsException>(() => h.Service.ExecuteAsync(
            new SqlQueryRequest { Sql = $"UPDATE account SET description = {expression} WHERE name IS NOT NULL", DmlSafety = Confirmed }));

        Assert.Equal(ErrorCodes.Query.DmlUnsupportedConstruct, ex.ErrorCode);
        Assert.Contains("Nothing was executed", ex.Message);
        h.AssertNothingChanged();
        Assert.Empty(h.Reads);
    }

    [Fact]
    public async Task SetColumns_RequestEveryColumnTheyRead()
    {
        var h = await Run("UPDATE account SET name = description, description = name, revenue = revenue WHERE name IS NOT NULL");

        var fetch = h.Reads.Single().FetchXml;
        foreach (var column in new[] { "accountid", "description", "revenue", "name" })
            Assert.Contains($"<attribute name=\"{column}\"", fetch);
    }

    // ---- supported target forms ---------------------------------------------------------------

    [Theory]
    [InlineData("UPDATE a SET name = a.description FROM account a WHERE a.name = 'x'")]
    [InlineData("UPDATE account SET name = description FROM account WHERE account.name = 'x'")]
    // T-SQL: the target names the FROM alias, so the aliased table (account) is written, not a table called contact.
    [InlineData("UPDATE contact SET name = contact.description FROM account contact WHERE contact.name = 'x'")]
    public async Task Update_TheTargetOrItsAliasInFrom_WritesTheTargetTable(string sql)
    {
        var h = await Run(sql);

        Assert.Equal(new[] { ("Update", "account", 4) }, h.Writes);
        Assert.Equal(new object?[] { null, "d1", null, "d3" }, Values(h, "name"));
        Assert.Contains("attribute=\"name\" operator=\"eq\" value=\"x\"", h.Reads.Single().FetchXml);
    }

    [Theory]
    [InlineData("DELETE a FROM account a WHERE a.name = 'x'")]
    [InlineData("DELETE account FROM account WHERE name = 'x'")]
    public async Task Delete_TheTargetOrItsAliasInFrom_DeletesFromTheTargetTable(string sql)
    {
        var h = await Run(sql);

        Assert.Equal(new[] { ("Delete", "account", 4) }, h.Writes);
        Assert.Contains("<attribute name=\"accountid\"", h.Reads.Single().FetchXml);
    }

    // ---- M3: INSERT ... SELECT maps by position -----------------------------------------------

    [Fact]
    public async Task InsertSelect_MapsValuesByPosition_IncludingAliasedColumnsAndLiterals()
    {
        var h = await Run("INSERT INTO account (description, name, revenue) SELECT name AS n, description AS d, -1 AS r FROM account WHERE name IS NOT NULL");

        Assert.Equal(new object?[] { "n0", "n1", "n2", "n3" }, Values(h, "description"));
        Assert.Equal(new object?[] { null, "d1", null, "d3" }, Values(h, "name"));
        Assert.Equal(new object?[] { "-1", "-1", "-1", "-1" }, Values(h, "revenue"));
    }

    [Fact]
    public async Task InsertSelect_SwappedColumns_MapByPositionNotByName()
    {
        var h = await Run("INSERT INTO account (description, name) SELECT name, description FROM account WHERE name IS NOT NULL");

        Assert.Equal(new object?[] { "n0", "n1", "n2", "n3" }, Values(h, "description"));
        Assert.Equal(new object?[] { null, "d1", null, "d3" }, Values(h, "name"));
    }

    // D-M2: only literals and NULL were proven live in VALUES; an expression is refused (and is outside the interim rule).
    [Theory]
    [InlineData("INSERT INTO account (name, description) VALUES (UPPER('a'), 'b')")]
    [InlineData("INSERT INTO account (name, description) VALUES ('a', CONCAT('b', 1 + 2))")]
    [InlineData("INSERT INTO account (name, numberofemployees) VALUES ('a', 1 + 2)")]
    [InlineData("INSERT INTO account (name, numberofemployees) VALUES ('a', -(5))")]
    public async Task InsertValues_WithExpressions_AreRefused(string sql)
    {
        var h = DmlTestHarness.Create(rows: 1);

        var ex = await Assert.ThrowsAsync<PpdsException>(() => h.Service.ExecuteAsync(new SqlQueryRequest { Sql = sql, DmlSafety = Confirmed }));

        Assert.Contains("VALUES takes literal values only", ex.Message);
        h.AssertNothingChanged();
    }

    [Fact]
    public async Task InsertValues_WithLiteralsNegativeNumbersAndNull_Work()
    {
        var h = await Run("INSERT INTO account (name, numberofemployees, revenue, description) VALUES ('a', -5, -2.5, NULL)");

        Assert.Equal(new object?[] { "a" }, Values(h, "name"));
        Assert.Equal(new object?[] { "-5" }, Values(h, "numberofemployees"));
        Assert.Equal(new object?[] { "-2.5" }, Values(h, "revenue"));
        Assert.Equal(new object?[] { null }, Values(h, "description"));
    }

    // ---- refused: every shape that is not on the allowlist ------------------------------------

    [Theory]
    // values
    [InlineData("UPDATE account SET name = GETDATE() WHERE name = 'x'", "the function GETDATE")]
    [InlineData("UPDATE account SET name = LEFT(name, 1) WHERE name = 'x'", "(the function LEFT)")]
    [InlineData("UPDATE account SET name = (SELECT TOP 1 fullname FROM contact) WHERE name = 'x'", "a subquery")]
    [InlineData("DECLARE @v nvarchar(10) = 'a'; UPDATE account SET name = @v WHERE name = 'x'", "variable @v")]
    [InlineData("UPDATE account SET name = contact.fullname WHERE name = 'x'", "does not belong to the statement's table")]
    [InlineData("DECLARE @v nvarchar(10) = 'a'; INSERT INTO account (name) VALUES (@v)", "variable @v")]
    [InlineData("INSERT INTO account (name) VALUES (name)", "the column name")]
    [InlineData("INSERT INTO account (name, name) VALUES ('a', 'b')", "more than once")]
    // WHERE
    [InlineData("DELETE FROM account WHERE NOT (name = 'x')", "NOT")]
    [InlineData("DELETE FROM account WHERE UPPER(name) = 'X'", "the function UPPER")]
    [InlineData("DELETE FROM account WHERE ISNULL(name, '') = ''", "ISNULL")]
    [InlineData("DELETE FROM account WHERE name = description", "a comparison of two columns")]
    [InlineData("DELETE FROM account WHERE 'x' = name", "column on the left")]
    [InlineData("DELETE FROM account WHERE name = NULL", "comparing with NULL")]
    [InlineData("DECLARE @n nvarchar(10) = 'x'; DELETE FROM account WHERE name = @n", "@n")]
    [InlineData("DELETE FROM account WHERE name LIKE '[ab]%'", "LIKE")]
    [InlineData("DELETE FROM account WHERE accountid IN (SELECT parentcustomerid FROM contact)", "SELECT")]
    [InlineData("DELETE FROM account WHERE EXISTS (SELECT 1 FROM contact)", "EXISTS")]
    [InlineData("DELETE FROM account WHERE CURRENT OF c", "CURRENT OF")]
    [InlineData("DELETE FROM account WHERE name = 'x' OPTION (FAST 1)", "OPTION")]
    // FROM / targets (M4, S1)
    // With a WHERE: without one, the guard's own refusal (DmlBlocked) comes first, as the spec orders refusals.
    [InlineData("DELETE account FROM (SELECT accountid FROM [QA].account WHERE name = 'x') AS account WHERE account.accountid IS NOT NULL", "FROM is supported only")]
    [InlineData("DELETE account FROM [QA].account AS q WHERE q.name = 'x'", "another environment")]
    [InlineData("DELETE account FROM account a WHERE a.name = 'x'", "is not the table named in FROM")]
    [InlineData("UPDATE account SET name = 'y' FROM account INNER JOIN contact ON contact.parentcustomerid = account.accountid WHERE account.name = 'x'", "FROM is supported only")]
    [InlineData("UPDATE account SET name = q.name FROM account JOIN [QA].account q ON account.accountid = q.accountid WHERE account.name IS NOT NULL", "FROM is supported only")]
    [InlineData("DELETE FROM #temp WHERE name = 'x'", "not a valid Dataverse table name")]
    // INSERT ... SELECT (M3)
    [InlineData("INSERT INTO account (name) SELECT 'x' FROM account WHERE name = 'x'", "needs an alias")]
    [InlineData("INSERT INTO account (name, description) SELECT 'x' AS n, name AS n FROM account", "Two SELECT items are named n")]
    [InlineData("INSERT INTO account (name) SELECT UPPER(name) AS n FROM account WHERE name = 'x'", "(the function UPPER)")]
    [InlineData("INSERT INTO account (name, description) SELECT a.name, b.name FROM account a JOIN account b ON a.parentaccountid = b.accountid", "single table")]
    [InlineData("INSERT INTO account (name) SELECT name FROM [QA].account", "another environment")]
    [InlineData("INSERT INTO account (name) SELECT name FROM account WHERE UPPER(name) = 'X'", "the function UPPER")]
    [InlineData("INSERT INTO account (name) SELECT DISTINCT name FROM account", "DISTINCT")]
    [InlineData("INSERT INTO account (name) SELECT name FROM account GROUP BY name", "GROUP BY")]
    [InlineData("INSERT INTO account (name) SELECT TOP 10 PERCENT name FROM account", "TOP")]
    public async Task NotOnTheAllowlist_IsRefusedBeforeAnythingChanges_NamingThePart(string sql, string named)
    {
        foreach (var options in new[] { Confirmed, new DmlSafetyOptions(), new DmlSafetyOptions { IsDryRun = true } })
        {
            var h = DmlTestHarness.Create(rows: 4);

            var ex = await Assert.ThrowsAsync<PpdsException>(
                () => h.Service.ExecuteAsync(new SqlQueryRequest { Sql = sql, DmlSafety = options }));

            Assert.Equal(ErrorCodes.Query.DmlUnsupportedConstruct, ex.ErrorCode);
            Assert.Contains(named, ex.Message);
            Assert.Contains("Nothing was executed", ex.Message);
            h.AssertNothingChanged();
            Assert.Empty(h.Reads);
        }
    }

    // ---- S9: the connected environment's own label ---------------------------------------------

    [Theory]
    [InlineData("UPDATE [DEV].dbo.account SET name = 'y' WHERE name = 'x'")]
    [InlineData("DELETE FROM [DEV].account WHERE name = 'x'")]
    public async Task ConnectedEnvironmentsOwnLabel_IsRefusedWithAnAccurateMessage(string sql)
    {
        var h = DmlTestHarness.Create(rows: 1);
        h.Service.ConfigureEnvironment("https://dev.crm.dynamics.com/", new EnvironmentConfig { Url = "https://dev.crm.dynamics.com/", Label = "DEV" });
        h.Service.ProfileResolver = new PPDS.Cli.Services.ProfileResolutionService(
            [new EnvironmentConfig { Url = "https://dev.crm.dynamics.com/", Label = "DEV" }]);

        var ex = await Assert.ThrowsAsync<PpdsException>(() => h.Service.ExecuteAsync(new SqlQueryRequest { Sql = sql, DmlSafety = Confirmed }));

        Assert.Equal(ErrorCodes.Query.DmlBlocked, ex.ErrorCode);
        Assert.Contains("[DEV] is the connected environment", ex.Message);
        Assert.DoesNotContain("Connect to that environment", ex.Message);
        h.AssertNothingChanged();
    }

    // ---- S11: a target row is changed once ---------------------------------------------------

    [Fact]
    public async Task DuplicateSourceRows_AreChangedOnce_AndCountOnceTowardTheCap()
    {
        var h = DmlTestHarness.Create(rows: 3, duplicateRows: true); // every row comes back twice

        var result = await h.Service.ExecuteAsync(new SqlQueryRequest
        {
            Sql = "DELETE FROM account WHERE name = 'x'",
            DmlSafety = new DmlSafetyOptions { IsConfirmed = true, RowCap = 3 }
        });

        Assert.Equal(3, h.DeletedIds.Distinct().Count());
        Assert.Equal(3, h.DeletedIds.Count);
        Assert.Equal(3, result.DmlOutcome!.AffectedRows);
    }

    // ---- S8: a refusal inside TRY inside WHILE ------------------------------------------------

    /// <summary>
    /// A write inside TRY inside WHILE can no longer run at all (the whole-script rule): the script is refused before its
    /// first statement, so no CATCH can swallow a refusal and the earlier UPDATE does not run either.
    /// </summary>
    [Fact]
    public async Task AWriteInsideTryInsideWhile_IsRefusedBeforeAnyStatementRuns()
    {
        var h = DmlTestHarness.Create(rows: 5, contactRows: 1);

        var ex = await Assert.ThrowsAsync<PpdsException>(() => h.Service.ExecuteAsync(new SqlQueryRequest
        {
            Sql = "UPDATE contact SET lastname = 'b' WHERE lastname = 'y'; " +
                  "WHILE 1 = 1 BEGIN BEGIN TRY DELETE FROM account WHERE name = 'x' END TRY BEGIN CATCH PRINT 'caught' END CATCH END",
            DmlSafety = new DmlSafetyOptions { IsConfirmed = true, RowCap = 2 }
        }));

        Assert.Equal(ErrorCodes.Query.DmlUnsupportedConstruct, ex.ErrorCode);
        Assert.Contains("Statement 2 (line 1, column 57) is a WHILE loop", ex.Message);
        h.AssertNothingChanged();
        Assert.Empty(h.Reads);
    }

    /// <summary>In a script of admitted writes, the row cap stops the statement over it and reports the earlier one.</summary>
    [Fact]
    public async Task RowCapRefusal_InAScriptOfWrites_KeepsItsCode_AndReportsTheEarlierStatement()
    {
        var h = DmlTestHarness.Create(rows: 5, contactRows: 1);

        var ex = await Assert.ThrowsAsync<PpdsDmlRowCapExceededException>(() => h.Service.ExecuteAsync(new SqlQueryRequest
        {
            Sql = "UPDATE contact SET lastname = 'b' WHERE lastname = 'y'; DELETE FROM account WHERE name = 'x'",
            DmlSafety = new DmlSafetyOptions { IsConfirmed = true, RowCap = 2 }
        }));

        Assert.Equal(new[] { ("Update", "contact", 1) }, h.Writes);
        Assert.Contains("#1 UPDATE contact: 1 affected", DmlOutcome.From(ex)!.DescribeCommitted());
    }

    // ---- S4: batches after GO -----------------------------------------------------------------

    [Theory]
    [InlineData("UPDATE account SET name = 'y' WHERE name = 'x'\nGO\nDELETE FROM contact WHERE lastname = 'z'")]
    [InlineData("SELECT name FROM account\nGO\nSELECT fullname FROM contact")]
    public async Task MoreThanOneBatch_IsRefusedInsteadOfDroppingTheRest(string sql)
    {
        var h = DmlTestHarness.Create(rows: 2);

        var ex = await Assert.ThrowsAsync<PpdsValidationException>(
            () => h.Service.ExecuteAsync(new SqlQueryRequest { Sql = sql, DmlSafety = Confirmed }));

        Assert.Contains("more than one batch", ex.Message);
        Assert.Equal(ExitCodes.InvalidArguments, ExceptionMapper.ToExitCode(ex));
        h.AssertNothingChanged();
        Assert.Empty(h.Reads);
    }

    // ---- S7: temp tables need a session, and the error is specific ----------------------------

    [Theory]
    [InlineData("CREATE TABLE #t (id int)", "CREATE TABLE #t needs temporary table support")]
    [InlineData("DROP TABLE #t", "DROP TABLE needs temporary table support")]
    [InlineData("SELECT 1 AS a; DROP TABLE account", "DROP TABLE account is not supported")]
    public async Task TempTableStatements_FailWithASpecificError_AndNeverRecurse(string sql, string message)
    {
        var h = DmlTestHarness.Create(rows: 1);

        // Wrapped in a PpdsException with the engine's code (no raw exception leaves the service).
        var ex = await Assert.ThrowsAsync<PpdsException>(
            () => h.Service.ExecuteAsync(new SqlQueryRequest { Sql = sql, DmlSafety = Confirmed }));

        Assert.Equal(ErrorCodes.Query.ExecutionFailed, ex.ErrorCode);
        Assert.Contains(message, ex.Message);
        h.AssertNothingChanged();
    }
}
