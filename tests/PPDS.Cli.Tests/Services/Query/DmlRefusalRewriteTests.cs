using PPDS.Auth.Profiles;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services.Query;
using Xunit;

namespace PPDS.Cli.Tests.Services.Query;

/// <summary>
/// A-N1 / D-M1: every refusal that suggests what to write instead suggests a statement the allowlist accepts. Each case
/// runs the refused statement (it must be refused with the suggestion in its message), then the suggested rewrite, which
/// must run. specs/query.md "Previously accepted, now refused" cites this test.
/// </summary>
[Trait("Category", "Unit")]
public sealed class DmlRefusalRewriteTests : IDisposable
{
    private static readonly DmlSafetyOptions Confirmed = new() { IsConfirmed = true };

    public DmlRefusalRewriteTests() => DmlTestHarness.FaithfulMode.Value = true;

    public void Dispose() => DmlTestHarness.FaithfulMode.Value = false;

    [Theory]
    // WHERE
    [InlineData("DELETE FROM account WHERE NOT (name = 'n1')", "<> instead of NOT =", "DELETE FROM account WHERE name <> 'n1'")]
    [InlineData("DELETE FROM account WHERE 'n1' = name", "column on the left", "DELETE FROM account WHERE name = 'n1'")]
    [InlineData("DELETE FROM account WHERE name = NULL", "Use IS NULL", "DELETE FROM account WHERE name IS NULL")]
    [InlineData("DELETE FROM account WHERE revenue = 2.5E0", "2.5 instead of 2.5E0", "DELETE FROM account WHERE revenue = 2.5")]
    [InlineData("DELETE FROM account WHERE numberofemployees = 5.0", "compare it with a whole number", "DELETE FROM account WHERE numberofemployees = 5")]
    [InlineData("DELETE FROM account WHERE createdon < '2024-01-01'", "Add Z (UTC)", "DELETE FROM account WHERE createdon < '2024-01-01T00:00:00Z'")]
    [InlineData("DELETE FROM account WHERE stageid = '00000000-0000-0000-0000-000000000001'", "Filter on the id column accountid", "DELETE FROM account WHERE accountid = '00000000-0000-0000-0000-000000000001'")]
    [InlineData("DELETE account FROM account a WHERE a.name = 'n1'", "Write DELETE a ...", "DELETE a FROM account a WHERE a.name = 'n1'")]
    [InlineData("UPDATE account SET name = 'x' FROM account JOIN contact ON contact.accountid = account.accountid WHERE account.name = 'n1'", "Put the condition in the WHERE clause", "UPDATE account SET name = 'x' WHERE account.name = 'n1'")]
    [InlineData("UPDATE account SET name = 'x' WHERE name = 'n1' OPTION (MAXDOP 1)", "Remove the OPTION clause", "UPDATE account SET name = 'x' WHERE name = 'n1'")]
    // SET
    [InlineData("UPDATE account SET revenue += 1 WHERE name = 'n1'", "SET revenue = revenue ...", "UPDATE account SET revenue = revenue + 1 WHERE name = 'n1'")]
    [InlineData("UPDATE account SET name = LEFT(name, 1) WHERE name = 'n1'", "SUBSTRING(text, start, length)", "UPDATE account SET name = SUBSTRING(name, 1, 1) WHERE name = 'n1'")]
    [InlineData("UPDATE account SET followupby = GETDATE() WHERE name = 'n1'", "'2024-01-15T09:30:00Z'", "UPDATE account SET followupby = '2024-01-15T09:30:00Z' WHERE name = 'n1'")]
    [InlineData("UPDATE account SET name = name + ' (old)' WHERE name = 'n1'", "Use CONCAT(...)", "UPDATE account SET name = CONCAT(name, ' (old)') WHERE name = 'n1'")]
    [InlineData("UPDATE account SET description = CONVERT(nvarchar(10), numberofemployees, 0) WHERE name = 'n1'", "Remove the style", "UPDATE account SET description = CONVERT(nvarchar(10), numberofemployees) WHERE name = 'n1'")]
    [InlineData("UPDATE account SET description = CAST(name AS nvarchar) WHERE name = 'n1'", "nvarchar(n) with a length", "UPDATE account SET description = CAST(name AS nvarchar(30)) WHERE name = 'n1'")]
    [InlineData("UPDATE account SET revenue = CAST(revenue AS decimal) WHERE name = 'n1'", "decimal(10, 2)", "UPDATE account SET revenue = CAST(revenue AS decimal(18, 0)) WHERE name = 'n1'")]
    [InlineData("UPDATE account SET name = TRIM(TRAILING FROM name) WHERE name = 'n1'", "TRIM takes one argument", "UPDATE account SET name = RTRIM(name) WHERE name = 'n1'")]
    [InlineData("UPDATE account SET name = SUBSTRING(name, 0, 3) WHERE name = 'n1'", "SUBSTRING(name, 1, 10)", "UPDATE account SET name = SUBSTRING(name, 1, 2) WHERE name = 'n1'")]
    [InlineData("UPDATE account SET revenue = ROUND(revenue, 1, 1) WHERE name = 'n1'", "ROUND(amount, 1)", "UPDATE account SET revenue = ROUND(revenue, 1) WHERE name = 'n1'")]
    [InlineData("UPDATE account SET statecode = 1 WHERE name = 'n1'", "SET statecode = 1, statuscode = 2", "UPDATE account SET statecode = 1, statuscode = 2 WHERE name = 'n1'")]
    [InlineData("UPDATE account SET description = CAST(revenue AS nvarchar(20)) WHERE name = 'n1'", "CAST(... AS int)", "UPDATE account SET description = CAST(CAST(revenue AS int) AS nvarchar(20)) WHERE name = 'n1'")]
    [InlineData("UPDATE account SET description = CONCAT(name, revenue) WHERE name = 'n1'", "CAST(... AS int)", "UPDATE account SET description = CONCAT(name, CAST(revenue AS int)) WHERE name = 'n1'")]
    [InlineData("UPDATE account SET description = UPPER(numberofemployees) WHERE name = 'n1'", "CAST(... AS nvarchar(n)) first", "UPDATE account SET description = UPPER(CAST(numberofemployees AS nvarchar(10))) WHERE name = 'n1'")]
    [InlineData("UPDATE account SET description = 5 WHERE name = 'n1'", "CAST(... AS nvarchar(n))", "UPDATE account SET description = CAST(5 AS nvarchar(10)) WHERE name = 'n1'")]
    [InlineData("UPDATE account SET revenue = '5' WHERE name = 'n1'", "Write a number", "UPDATE account SET revenue = 5 WHERE name = 'n1'")]
    // INSERT
    [InlineData("INSERT INTO account (name, numberofemployees) VALUES (UPPER('a'), 1 + 2)", "write the value you want stored as a literal", "INSERT INTO account (name, numberofemployees) VALUES ('A', 3)")]
    [InlineData("INSERT TOP (1) INTO account (name) SELECT name FROM account", "SELECT TOP (n) ... FROM table ORDER BY column", "INSERT INTO account (name) SELECT TOP (1) name FROM account ORDER BY name")]
    [InlineData("INSERT INTO account (name) SELECT TOP 2 name FROM account", "Add ORDER BY", "INSERT INTO account (name) SELECT TOP 2 name FROM account ORDER BY name")]
    [InlineData("INSERT INTO account (name) SELECT TOP 2 name FROM account ORDER BY customertypecode", "supported there", "INSERT INTO account (name) SELECT TOP 2 name FROM account ORDER BY name")]
    [InlineData("INSERT INTO account (name) SELECT UPPER(name) FROM account WHERE name = 'n1'", "needs an alias (AS name)", "INSERT INTO account (name) SELECT UPPER(name) AS n FROM account WHERE name = 'n1'")]
    [InlineData("INSERT INTO account (name, description) SELECT name AS description, UPPER(description) AS u FROM account WHERE name = 'n1'", "alias that is not a column name", "INSERT INTO account (name, description) SELECT name AS n, UPPER(description) AS u FROM account WHERE name = 'n1'")]
    [InlineData("INSERT INTO account (name) VALUES (CONCAT('a', 'b'))", "write the value you want stored as a literal", "INSERT INTO account (name) VALUES ('ab')")]
    public async Task TheSuggestedRewrite_IsOnTheAllowlist(string refused, string suggestion, string rewrite)
    {
        var h = DmlTestHarness.Create(rows: 4);
        var ex = await Assert.ThrowsAnyAsync<PpdsException>(() => h.Service.ExecuteAsync(new SqlQueryRequest { Sql = refused, DmlSafety = Confirmed }));
        Assert.Contains(suggestion, ex.Message);
        h.AssertNothingChanged();

        var ok = DmlTestHarness.Create(rows: 4);
        await ok.Service.ExecuteAsync(new SqlQueryRequest { Sql = rewrite, DmlSafety = Confirmed });
    }

    [Fact]
    public async Task TheConnectedEnvironmentsOwnLabel_SuggestsTheUnlabelledTable_WhichRuns()
    {
        var h = DmlTestHarness.Create(rows: 4);
        h.Service.ConfigureEnvironment("https://dev.crm.dynamics.com/", new EnvironmentConfig { Url = "https://dev.crm.dynamics.com/", Label = "DEV", Type = EnvironmentType.Development });
        h.Service.ProfileResolver = new PPDS.Cli.Services.ProfileResolutionService(
            [new EnvironmentConfig { Url = "https://dev.crm.dynamics.com/", Label = "DEV" }]);

        var ex = await Assert.ThrowsAsync<PpdsException>(() => h.Service.ExecuteAsync(new SqlQueryRequest { Sql = "UPDATE [DEV].account SET name = 'x' WHERE name = 'n1'", DmlSafety = Confirmed }));
        Assert.Contains("UPDATE account ... instead of UPDATE [DEV].account", ex.Message);

        await h.Service.ExecuteAsync(new SqlQueryRequest { Sql = "UPDATE account SET name = 'x' WHERE name = 'n1'", DmlSafety = Confirmed });
        Assert.Single(h.Writes);
    }

    [Fact]
    public async Task TheCrossEnvironmentWriteRefusal_NoLongerSuggestsAReadThatIsRefused()
    {
        var h = DmlTestHarness.Create(rows: 4);
        h.Service.ProfileResolver = new PPDS.Cli.Services.ProfileResolutionService(
            [new EnvironmentConfig { Url = "https://qa.crm.dynamics.com/", Label = "QA", Type = EnvironmentType.Test,
                SafetySettings = new QuerySafetySettings { CrossEnvironmentDmlPolicy = CrossEnvironmentDmlPolicy.Allow } }]);

        var ex = await Assert.ThrowsAsync<PpdsException>(() => h.Service.ExecuteAsync(new SqlQueryRequest { Sql = "UPDATE [QA].account SET name = 'x' WHERE name = 'n1'", DmlSafety = Confirmed }));

        Assert.DoesNotContain("is supported", ex.Message);
        Assert.Contains("Connect to that environment", ex.Message);
    }
}
