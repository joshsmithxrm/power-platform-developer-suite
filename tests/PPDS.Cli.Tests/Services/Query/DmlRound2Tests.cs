using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services.Query;
using Xunit;

namespace PPDS.Cli.Tests.Services.Query;

/// <summary>
/// Shapes the second independent review found admitted without proof, or computed differently from T-SQL. Each is refused
/// before anything is read or written; the forms kept next to them run.
/// </summary>
[Trait("Category", "Unit")]
public sealed class DmlRound2Tests
{
    private static readonly DmlSafetyOptions Confirmed = new() { IsConfirmed = true };

    [Theory]
    // B-F1: decimal(p, s) above 28 digits (TRY_CAST wrote NULL, CAST failed with a raw exception)
    [InlineData("UPDATE account SET revenue = TRY_CAST(revenue AS decimal(38, 30)) WHERE name = 'n1'", "(TRY_CAST)")]
    [InlineData("UPDATE account SET revenue = TRY_CONVERT(decimal(30, 29), revenue) WHERE name = 'n1'", "(TRY_CONVERT)")]
    [InlineData("UPDATE account SET revenue = CAST(revenue AS decimal(29, 2)) WHERE name = 'n1'", "(a CAST other than")]
    [InlineData("UPDATE account SET revenue = CONVERT(decimal(38, 30), revenue) WHERE name = 'n1'", "(CONVERT)")]
    // ROUND with more places than the live run proved
    [InlineData("UPDATE account SET revenue = ROUND(revenue, 11) WHERE name = 'n1'", "(the function ROUND)")]
    [InlineData("UPDATE account SET revenue = ROUND(revenue, 29) WHERE name = 'n1'", "(the function ROUND)")]
    // D-2: currency to a whole number (SQL Server rounds, the engine truncated)
    [InlineData("UPDATE account SET numberofemployees = creditlimit WHERE name = 'n1'", "SQL Server rounds currency")]
    [InlineData("UPDATE account SET numberofemployees = creditlimit * 2 WHERE name = 'n1'", "(arithmetic)")]
    [InlineData("UPDATE account SET numberofemployees = CAST(creditlimit AS int) WHERE name = 'n1'", "(a CAST other than")]
    [InlineData("UPDATE account SET numberofemployees = CONVERT(int, creditlimit) WHERE name = 'n1'", "(CONVERT)")]
    [InlineData("UPDATE account SET numberofemployees = TRY_CAST(creditlimit AS int) WHERE name = 'n1'", "(TRY_CAST)")]
    [InlineData("UPDATE account SET numberofemployees = TRY_CONVERT(int, creditlimit) WHERE name = 'n1'", "(TRY_CONVERT)")]
    [InlineData("UPDATE account SET description = CAST(CAST(creditlimit AS int) AS nvarchar(20)) WHERE name = 'n1'", "(a CAST other than")]
    [InlineData("INSERT INTO account (name, numberofemployees) SELECT TOP 1 name, creditlimit FROM account ORDER BY name", "SQL Server rounds currency")]
    // floating point to decimal or currency (digits kept differ)
    [InlineData("UPDATE account SET revenue = address1_latitude WHERE name = 'n1'", "17 significant digits")]
    [InlineData("UPDATE account SET creditlimit = address1_latitude * 2 WHERE name = 'n1'", "(arithmetic)")]
    [InlineData("UPDATE account SET revenue = CAST(address1_latitude AS decimal(10, 2)) WHERE name = 'n1'", "(a CAST other than")]
    // C-M1: % on floating point (SQL Server refuses it) and on currency (not proven)
    [InlineData("UPDATE account SET address1_latitude = address1_latitude % 2 WHERE name = 'n1'", "(arithmetic)")]
    [InlineData("UPDATE account SET creditlimit = creditlimit % 2 WHERE name = 'n1'", "(arithmetic)")]
    [InlineData("UPDATE account SET numberofemployees = numberofemployees % address1_latitude WHERE name = 'n1'", "(arithmetic)")]
    // ISNULL narrowing its second value to the first's type
    [InlineData("UPDATE account SET revenue = ISNULL(numberofemployees, 2.5) WHERE name = 'n1'", "(the function ISNULL)")]
    [InlineData("UPDATE account SET revenue = ISNULL(revenue, creditlimit) WHERE name = 'n1'", "(the function ISNULL)")]
    [InlineData("UPDATE account SET revenue = ISNULL(NULL, revenue) WHERE name = 'n1'", "(the function ISNULL)")]
    // mixing decimal, currency and floating point (only whole numbers mix with the other number types)
    [InlineData("UPDATE account SET revenue = revenue + creditlimit WHERE name = 'n1'", "(arithmetic)")]
    [InlineData("UPDATE account SET revenue = COALESCE(revenue, creditlimit) WHERE name = 'n1'", "(COALESCE)")]
    [InlineData("UPDATE account SET address1_latitude = address1_latitude * revenue WHERE name = 'n1'", "(arithmetic)")]
    [InlineData("UPDATE account SET revenue = IIF(numberofemployees > 2, revenue, creditlimit) WHERE name = 'n1'", "(IIF)")]
    // found by the admission matrix: the NULL constant where SQL Server refuses it, or where no case proved it
    [InlineData("UPDATE account SET description = UPPER(NULL) WHERE name = 'n1'", "(the function UPPER)")]
    [InlineData("UPDATE account SET description = CONCAT(name, NULL) WHERE name = 'n1'", "(the function CONCAT)")]
    [InlineData("UPDATE account SET numberofemployees = CAST(NULL AS int) WHERE name = 'n1'", "(a CAST other than")]
    [InlineData("UPDATE account SET numberofemployees = TRY_CONVERT(int, NULL) WHERE name = 'n1'", "(TRY_CONVERT)")]
    [InlineData("UPDATE account SET description = COALESCE(NULL, NULL) WHERE name = 'n1'", "(COALESCE)")]
    [InlineData("UPDATE account SET description = IIF(numberofemployees > 2, NULL, NULL) WHERE name = 'n1'", "(IIF)")]
    [InlineData("UPDATE account SET description = CASE WHEN numberofemployees > 2 THEN NULL END WHERE name = 'n1'", "(CASE)")]
    [InlineData("UPDATE account SET revenue = NULLIF(revenue, NULL) WHERE name = 'n1'", "(NULLIF)")]
    [InlineData("UPDATE account SET numberofemployees = NULLIF(numberofemployees, revenue) WHERE name = 'n1'", "(NULLIF)")]
    // B-F4: ORDER BY without TOP does not say which rows are copied, and paging over it was not proven
    [InlineData("INSERT INTO account (name) SELECT name FROM account ORDER BY name", "ORDER BY without TOP")]
    // D-3 named the id column of an activity table (activityid); a write to an activity table is now refused as such (P2)
    [InlineData("DELETE FROM task WHERE stageid = '00000000-0000-0000-0000-000000000001'", "'task' is an activity table")]
    public async Task IsRefusedBeforeAnythingIsReadOrWritten(string sql, string named)
    {
        foreach (var options in new[] { Confirmed, new DmlSafetyOptions(), new DmlSafetyOptions { IsDryRun = true } })
        {
            var h = DmlTestHarness.Create(rows: 4);

            var ex = await Assert.ThrowsAsync<PpdsException>(() => h.Service.ExecuteAsync(new SqlQueryRequest { Sql = sql, DmlSafety = options }));

            Assert.Equal(ErrorCodes.Query.DmlUnsupportedConstruct, ex.ErrorCode);
            Assert.Contains(named, ex.Message);
            Assert.Contains("Nothing was executed.", ex.Message);
            h.AssertNothingChanged();
            Assert.Empty(h.Reads);
        }
    }

    /// <summary>The forms that used to run next to the refused ones are computed values too, refused under the interim rule.</summary>
    [Theory]
    [InlineData("UPDATE account SET numberofemployees = CAST(CAST(creditlimit AS decimal(19, 4)) AS int) WHERE name = 'n1'")]
    [InlineData("UPDATE account SET numberofemployees = TRY_CAST(CAST(creditlimit AS decimal(19, 4)) AS int) WHERE name = 'n1'")]
    [InlineData("UPDATE account SET revenue = ISNULL(revenue, 0) WHERE name = 'n1'")]
    [InlineData("UPDATE account SET revenue = revenue * numberofemployees WHERE name = 'n1'")]
    [InlineData("UPDATE account SET revenue = CAST(revenue AS decimal(28, 2)) WHERE name = 'n1'")]
    [InlineData("UPDATE account SET revenue = TRY_CAST(revenue AS decimal(28, 28)) WHERE name = 'n1'")]
    [InlineData("UPDATE account SET revenue = ROUND(revenue, 10) WHERE name = 'n1'")]
    [InlineData("UPDATE account SET creditlimit = creditlimit * numberofemployees WHERE name = 'n1'")]
    [InlineData("UPDATE account SET numberofemployees = numberofemployees % 3 WHERE name = 'n1'")]
    public async Task TheComputedFormsNextToThem_AreRefusedToo(string sql)
    {
        var h = DmlTestHarness.Create(rows: 4);

        var ex = await Assert.ThrowsAsync<PpdsException>(() => h.Service.ExecuteAsync(new SqlQueryRequest { Sql = sql, DmlSafety = Confirmed }));

        Assert.Equal(ErrorCodes.Query.DmlUnsupportedConstruct, ex.ErrorCode);
        h.AssertNothingChanged();
    }

    [Theory]
    [InlineData("UPDATE account SET revenue = numberofemployees WHERE name = 'n1'")]
    [InlineData("UPDATE account SET creditlimit = numberofemployees WHERE name = 'n1'")]
    [InlineData("UPDATE account SET revenue = -2.5 WHERE name = 'n1'")]
    [InlineData("UPDATE account SET revenue = NULL WHERE name = 'n1'")]
    [InlineData("INSERT INTO account (name) SELECT TOP 2 name FROM account ORDER BY name")]
    public async Task TheFormsNextToThem_StillRun(string sql)
    {
        var h = DmlTestHarness.Create(rows: 4);

        await h.Service.ExecuteAsync(new SqlQueryRequest { Sql = sql, DmlSafety = Confirmed });

        Assert.NotEmpty(h.Writes);
    }

    // D should-fix: the ambiguous FROM binding refusal says nothing ran and what to write instead.
    [Fact]
    public async Task AmbiguousFromBinding_SaysNothingRan_AndWhatToWrite()
    {
        var h = DmlTestHarness.Create(rows: 4);

        var ex = await Assert.ThrowsAsync<PpdsException>(() => h.Service.ExecuteAsync(new SqlQueryRequest
        {
            Sql = "UPDATE account SET name = 'x' FROM account, account WHERE account.name = 'n1'",
            DmlSafety = Confirmed
        }));

        Assert.Equal(ErrorCodes.Query.DmlUnsupportedConstruct, ex.ErrorCode);
        Assert.Contains("Nothing was executed.", ex.Message);
        Assert.Contains("Name the target table alone in FROM", ex.Message);
        h.AssertNothingChanged();
    }
}
