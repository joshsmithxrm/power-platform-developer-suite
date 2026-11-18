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
    [InlineData("UPDATE account SET revenue = TRY_CAST(revenue AS decimal(38, 30)) WHERE name = 'n1'", "precision at most 28")]
    [InlineData("UPDATE account SET revenue = TRY_CONVERT(decimal(30, 29), revenue) WHERE name = 'n1'", "precision at most 28")]
    [InlineData("UPDATE account SET revenue = CAST(revenue AS decimal(29, 2)) WHERE name = 'n1'", "at most 28")]
    [InlineData("UPDATE account SET revenue = CONVERT(decimal(38, 30), revenue) WHERE name = 'n1'", "at most 28")]
    // ROUND with more places than the live run proved
    [InlineData("UPDATE account SET revenue = ROUND(revenue, 11) WHERE name = 'n1'", "from 0 to 10")]
    [InlineData("UPDATE account SET revenue = ROUND(revenue, 29) WHERE name = 'n1'", "from 0 to 10")]
    // D-2: currency to a whole number (SQL Server rounds, the engine truncated)
    [InlineData("UPDATE account SET numberofemployees = creditlimit WHERE name = 'n1'", "SQL Server rounds currency")]
    [InlineData("UPDATE account SET numberofemployees = creditlimit * 2 WHERE name = 'n1'", "SQL Server rounds currency")]
    [InlineData("UPDATE account SET numberofemployees = CAST(creditlimit AS int) WHERE name = 'n1'", "SQL Server rounds currency")]
    [InlineData("UPDATE account SET numberofemployees = CONVERT(int, creditlimit) WHERE name = 'n1'", "SQL Server rounds currency")]
    [InlineData("UPDATE account SET numberofemployees = TRY_CAST(creditlimit AS int) WHERE name = 'n1'", "SQL Server rounds currency")]
    [InlineData("UPDATE account SET numberofemployees = TRY_CONVERT(int, creditlimit) WHERE name = 'n1'", "SQL Server rounds currency")]
    [InlineData("UPDATE account SET description = CAST(CAST(creditlimit AS int) AS nvarchar(20)) WHERE name = 'n1'", "SQL Server rounds currency")]
    [InlineData("INSERT INTO account (name, numberofemployees) SELECT TOP 1 name, creditlimit FROM account ORDER BY name", "SQL Server rounds currency")]
    // floating point to decimal or currency (digits kept differ)
    [InlineData("UPDATE account SET revenue = address1_latitude WHERE name = 'n1'", "17 significant digits")]
    [InlineData("UPDATE account SET creditlimit = address1_latitude * 2 WHERE name = 'n1'", "17 significant digits")]
    [InlineData("UPDATE account SET revenue = CAST(address1_latitude AS decimal(10, 2)) WHERE name = 'n1'", "17 significant digits")]
    // C-M1: % on floating point (SQL Server refuses it) and on currency (not proven)
    [InlineData("UPDATE account SET address1_latitude = address1_latitude % 2 WHERE name = 'n1'", "% on a floating-point value")]
    [InlineData("UPDATE account SET creditlimit = creditlimit % 2 WHERE name = 'n1'", "% on a currency value")]
    [InlineData("UPDATE account SET numberofemployees = numberofemployees % address1_latitude WHERE name = 'n1'", "% on a floating-point value")]
    // ISNULL narrowing its second value to the first's type
    [InlineData("UPDATE account SET revenue = ISNULL(numberofemployees, 2.5) WHERE name = 'n1'", "ISNULL gives the type of its first value")]
    [InlineData("UPDATE account SET revenue = ISNULL(revenue, creditlimit) WHERE name = 'n1'", "ISNULL gives the type of its first value")]
    [InlineData("UPDATE account SET revenue = ISNULL(NULL, revenue) WHERE name = 'n1'", "NULL as its first value")]
    // mixing decimal, currency and floating point (only whole numbers mix with the other number types)
    [InlineData("UPDATE account SET revenue = revenue + creditlimit WHERE name = 'n1'", "combining a decimal and a currency value")]
    [InlineData("UPDATE account SET revenue = COALESCE(revenue, creditlimit) WHERE name = 'n1'", "combining a decimal and a currency value")]
    [InlineData("UPDATE account SET address1_latitude = address1_latitude * revenue WHERE name = 'n1'", "combining a floating-point and a decimal value")]
    [InlineData("UPDATE account SET revenue = IIF(numberofemployees > 2, revenue, creditlimit) WHERE name = 'n1'", "combining a decimal and a currency value")]
    // B-F4: ORDER BY without TOP does not say which rows are copied, and paging over it was not proven
    [InlineData("INSERT INTO account (name) SELECT name FROM account ORDER BY name", "ORDER BY without TOP")]
    // D-3: the id column is named from metadata (activity tables use activityid)
    [InlineData("DELETE FROM task WHERE stageid = '00000000-0000-0000-0000-000000000001'", "Filter on the id column activityid")]
    public async Task IsRefusedBeforeAnythingIsReadOrWritten(string sql, string named)
    {
        foreach (var options in new[] { Confirmed, new DmlSafetyOptions(), new DmlSafetyOptions { IsDryRun = true } })
        {
            var h = DmlTestHarness.Create(rows: 4);

            var ex = await Assert.ThrowsAsync<PpdsException>(() => h.Service.ExecuteAsync(new SqlQueryRequest { Sql = sql, DmlSafety = options }));

            Assert.Equal(ErrorCodes.Query.DmlBlocked, ex.ErrorCode);
            Assert.Contains(named, ex.Message);
            Assert.Contains("Nothing was executed.", ex.Message);
            h.AssertNothingChanged();
            Assert.Empty(h.Reads);
        }
    }

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

        Assert.Equal(ErrorCodes.Query.DmlBlocked, ex.ErrorCode);
        Assert.Contains("Nothing was executed.", ex.Message);
        Assert.Contains("Name the target table alone in FROM", ex.Message);
        h.AssertNothingChanged();
    }
}
