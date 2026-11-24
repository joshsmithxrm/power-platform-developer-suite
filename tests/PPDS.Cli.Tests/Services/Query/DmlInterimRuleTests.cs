using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services.Query;
using Xunit;

namespace PPDS.Cli.Tests.Services.Query;

/// <summary>
/// The interim rule for values written by DML, end to end through <see cref="SqlQueryService"/> (guard, allowlist and
/// column validator): the admitted forms write exactly their value, and anything else is refused with
/// <c>Query.DmlBlocked</c> before anything is read or written, on every request kind (confirmed, unconfirmed, dry run).
/// The harness rows: name n0..n3, revenue 500/1000/1500/2000, description null/d1/null/d3.
/// </summary>
[Trait("Category", "Unit")]
public sealed class DmlInterimRuleTests : IDisposable
{
    private static readonly DmlSafetyOptions Confirmed = new() { IsConfirmed = true };

    // The harness applies the statement's FetchXML filter to its rows (as Dataverse does) only in this mode.
    public DmlInterimRuleTests() => DmlTestHarness.FaithfulMode.Value = true;

    public void Dispose() => DmlTestHarness.FaithfulMode.Value = false;
    private const string Guid = "6F9619FF-8B86-D011-B42D-00C04FC964FF";

    private static object? Written(DmlTestHarness h, string attribute) => Assert.Single(h.WrittenEntities)[attribute] switch
    {
        Microsoft.Xrm.Sdk.EntityReference r => r.Id.ToString(),
        Microsoft.Xrm.Sdk.Money m => m.Value,
        var v => v
    };

    [Theory]
    [InlineData("name = 'x'", "name", "x")]
    [InlineData("name = N'naïve'", "name", "naïve")]
    [InlineData("revenue = 2.5", "revenue", "2.5")]
    [InlineData("revenue = -2.5", "revenue", "-2.5")]
    [InlineData("numberofemployees = -5", "numberofemployees", "-5")]
    [InlineData("numberofemployees = 7", "numberofemployees", "7")]
    [InlineData("description = NULL", "description", null)]
    [InlineData("description = name", "description", "n1")]
    [InlineData("revenue = revenue", "revenue", "1000")]
    [InlineData("parentaccountid = CAST('" + Guid + "' AS uniqueidentifier)", "parentaccountid", "6f9619ff-8b86-d011-b42d-00c04fc964ff")]
    [InlineData("parentaccountid = CAST('{" + Guid + "}' AS uniqueidentifier)", "parentaccountid", "6f9619ff-8b86-d011-b42d-00c04fc964ff")]
    [InlineData("parentaccountid = CAST('6f9619ff-8b86-d011-b42d-00c04fc964ff' AS uniqueidentifier)", "parentaccountid", "6f9619ff-8b86-d011-b42d-00c04fc964ff")]
    [InlineData("parentaccountid = '{" + Guid + "}'", "parentaccountid", "6f9619ff-8b86-d011-b42d-00c04fc964ff")]
    public async Task AnAdmittedValue_WritesExactlyThatValue(string assignment, string attribute, string? expected)
    {
        var h = DmlTestHarness.Create(rows: 4);

        await h.Service.ExecuteAsync(new SqlQueryRequest { Sql = $"UPDATE account SET {assignment} WHERE name = 'n1'", DmlSafety = Confirmed });

        var value = Written(h, attribute);
        Assert.Equal(expected, value switch
        {
            null => null,
            decimal d => d.ToString("G29", System.Globalization.CultureInfo.InvariantCulture),
            int i => i.ToString(System.Globalization.CultureInfo.InvariantCulture),
            _ => value.ToString()
        });
    }

    [Fact]
    public async Task InsertValues_WithEachAdmittedLiteralForm_WritesThem()
    {
        var h = DmlTestHarness.Create(rows: 1);

        await h.Service.ExecuteAsync(new SqlQueryRequest
        {
            Sql = $"INSERT INTO account (name, revenue, numberofemployees, description, parentaccountid) VALUES (N'a', -2.5, -5, NULL, CAST('{{{Guid}}}' AS uniqueidentifier))",
            DmlSafety = Confirmed
        });

        var row = Assert.Single(h.WrittenEntities);
        Assert.Equal("a", row["name"]);
        Assert.Equal(-2.5m, row["revenue"] is Microsoft.Xrm.Sdk.Money m ? m.Value : row["revenue"]);
        Assert.Equal(-5, row["numberofemployees"]);
        Assert.Null(row["description"]);
        Assert.Equal(System.Guid.Parse(Guid), ((Microsoft.Xrm.Sdk.EntityReference)row["parentaccountid"]).Id);
    }

    /// <summary>A written row id, as a string literal or in the GUID cast, takes the canonical spelling only.</summary>
    [Theory]
    [InlineData("' " + Guid + "'")]
    [InlineData("'" + Guid + " '")]
    [InlineData("'6F9619FF8B86D011B42D00C04FC964FF'")]
    [InlineData("'(" + Guid + ")'")]
    [InlineData("CAST(' " + Guid + "' AS uniqueidentifier)")]
    [InlineData("CAST('6F9619FF8B86D011B42D00C04FC964FF' AS uniqueidentifier)")]
    [InlineData("CAST('" + Guid + "junk' AS uniqueidentifier)")]
    public async Task ALookupIdInAnotherSpelling_IsRefused(string value)
    {
        await AssertRefusedEverywhere($"UPDATE account SET parentaccountid = {value} WHERE name = 'n1'", "without spaces");
    }

    [Theory]
    [InlineData("UPDATE account SET revenue = revenue * 2 WHERE name = 'n1'", "(arithmetic)")]
    [InlineData("UPDATE account SET name = name + 'x' WHERE name = 'n1'", "(+ with text)")]
    [InlineData("UPDATE account SET name = UPPER(name) WHERE name = 'n1'", "(the function UPPER)")]
    [InlineData("UPDATE account SET revenue = ROUND(revenue, 1) WHERE name = 'n1'", "(the function ROUND)")]
    [InlineData("UPDATE account SET followupby = GETDATE() WHERE name = 'n1'", "(the function GETDATE)")]
    [InlineData("UPDATE account SET name = COALESCE(description, name) WHERE name = 'n1'", "(COALESCE)")]
    [InlineData("UPDATE account SET name = ISNULL(description, name) WHERE name = 'n1'", "(the function ISNULL)")]
    [InlineData("UPDATE account SET name = IIF(revenue > 1, 'a', 'b') WHERE name = 'n1'", "(IIF)")]
    [InlineData("UPDATE account SET name = CASE WHEN revenue > 1 THEN 'a' END WHERE name = 'n1'", "(CASE)")]
    [InlineData("UPDATE account SET numberofemployees = CAST(revenue AS int) WHERE name = 'n1'", "(a CAST other than")]
    [InlineData("UPDATE account SET name = (SELECT TOP 1 name FROM account) WHERE name = 'n1'", "(a subquery)")]
    [InlineData("DECLARE @v nvarchar(10) = 'a'; UPDATE account SET name = @v WHERE name = 'n1'", "the variable @v")]
    [InlineData("UPDATE account SET revenue = -revenue WHERE name = 'n1'", "(a minus sign on something other than a number literal)")]
    [InlineData("UPDATE account SET revenue = (5) WHERE name = 'n1'", "(parentheses)")]
    [InlineData("INSERT INTO account (name) VALUES (UPPER('a'))", "(the function UPPER)")]
    [InlineData("INSERT INTO account (name, description) SELECT name, CONCAT(name, '!') AS c FROM account WHERE name = 'n1'", "(the function CONCAT)")]
    public async Task AComputedValue_IsRefusedBeforeAnythingIsReadOrWritten(string sql, string construct)
    {
        await AssertRefusedEverywhere(sql, construct);
    }

    /// <summary>The guard checks every statement of a script first, so an admitted statement before a refused one never runs.</summary>
    [Theory]
    [InlineData("INSERT INTO account (name) VALUES ('a'); UPDATE account SET name = UPPER(name) WHERE name = 'n1'")]
    [InlineData("UPDATE account SET name = 'b' WHERE name = 'n1'; UPDATE account SET revenue = revenue + 1 WHERE name = 'b'")]
    [InlineData("IF 1 = 1 DELETE FROM account WHERE name = 'n1' ELSE UPDATE account SET name = LOWER(name) WHERE name = 'n2'")]
    public async Task AScriptWithARefusedValue_RunsNoneOfItsStatements(string sql)
    {
        await AssertRefusedEverywhere(sql, "Nothing was executed.");
    }

    private static async Task AssertRefusedEverywhere(string sql, string named)
    {
        foreach (var options in new[] { Confirmed, new DmlSafetyOptions(), new DmlSafetyOptions { IsDryRun = true } })
        {
            var h = DmlTestHarness.Create(rows: 4);

            var ex = await Assert.ThrowsAsync<PpdsException>(() => h.Service.ExecuteAsync(new SqlQueryRequest { Sql = sql, DmlSafety = options }));

            Assert.Equal(ErrorCodes.Query.DmlBlocked, ex.ErrorCode);
            Assert.Contains(named, ex.Message);
            h.AssertNothingChanged();
            Assert.Empty(h.Reads);
        }
    }
}
