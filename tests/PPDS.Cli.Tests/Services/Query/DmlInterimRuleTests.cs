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

    // ---- text without N (maintainer decision, 2026-11-24, "Refuse it") --------------------------------------------
    // SQL Server converts a text literal without N to the database's code page and stores what the code page lacks as
    // question marks; the engine cannot tell the code page, so text without N is written only when it is 7-bit ASCII.

    private const string Cyrillic = "\u041F\u0440\u0438\u0432\u0435\u0442";
    private const string Accented = "na\u00EFve";

    [Theory]
    [InlineData("UPDATE account SET name = '" + Cyrillic + "' WHERE name = 'n1'", "N'" + Cyrillic + "'")]
    [InlineData("INSERT INTO account (name) VALUES ('" + Cyrillic + "')", "N'" + Cyrillic + "'")]
    [InlineData("INSERT INTO account (name, description) SELECT name, '" + Cyrillic + "' AS d FROM account WHERE name = 'n1'", "N'" + Cyrillic + "'")]
    public async Task TextWithoutN_InANonLatinScript_IsRefusedBeforeAnythingIsReadOrWritten_AndNamesTheNForm(string sql, string nForm)
        => await AssertRefusedEverywhere(sql, $"Write it with the N prefix: {nForm}.");

    [Theory]
    [InlineData("UPDATE account SET name = '" + Accented + "' WHERE name = 'n1'")]
    [InlineData("INSERT INTO account (name) VALUES ('" + Accented + "')")]
    [InlineData("INSERT INTO account (name, description) SELECT name, '" + Accented + "' AS d FROM account WHERE name = 'n1'")]
    public async Task TextWithoutN_WithOneAccentedLatinCharacter_IsRefused_TheConservativeReading(string sql)
        // A Latin code page (1252) would store this character; the engine cannot tell the code page, so it is refused too.
        => await AssertRefusedEverywhere(sql, $"Write it with the N prefix: N'{Accented}'.");

    [Fact]
    public async Task AScriptWithTextWithoutN_RunsNoneOfItsStatements()
        => await AssertRefusedEverywhere(
            $"INSERT INTO account (name) VALUES ('a'); UPDATE account SET name = '{Accented}' WHERE name = 'n1'", "Nothing was executed.");

    [Theory]
    [InlineData(Cyrillic)]
    [InlineData(Accented)]
    public async Task TheSameTextWithN_IsWrittenAsTyped(string text)
    {
        var update = DmlTestHarness.Create(rows: 4);
        await update.Service.ExecuteAsync(new SqlQueryRequest { Sql = $"UPDATE account SET name = N'{text}' WHERE name = 'n1'", DmlSafety = Confirmed });
        Assert.Equal(text, Written(update, "name"));

        var insert = DmlTestHarness.Create(rows: 1);
        await insert.Service.ExecuteAsync(new SqlQueryRequest { Sql = $"INSERT INTO account (name) VALUES (N'{text}')", DmlSafety = Confirmed });
        Assert.Equal(text, Written(insert, "name"));

        var copy = DmlTestHarness.Create(rows: 4);
        await copy.Service.ExecuteAsync(new SqlQueryRequest
        {
            Sql = $"INSERT INTO account (name, description) SELECT name, N'{text}' AS d FROM account WHERE name = 'n1'", DmlSafety = Confirmed
        });
        Assert.Equal(text, Written(copy, "description"));
    }

    [Fact]
    public async Task AsciiTextWithoutN_IsWrittenAsTyped()
    {
        var h = DmlTestHarness.Create(rows: 4);
        await h.Service.ExecuteAsync(new SqlQueryRequest { Sql = "UPDATE account SET name = 'a ~!@#$%^&*()' WHERE name = 'n1'", DmlSafety = Confirmed });
        Assert.Equal("a ~!@#$%^&*()", Written(h, "name"));

        var insert = DmlTestHarness.Create(rows: 1);
        await insert.Service.ExecuteAsync(new SqlQueryRequest { Sql = "INSERT INTO account (name) VALUES ('plain')", DmlSafety = Confirmed });
        Assert.Equal("plain", Written(insert, "name"));
    }

    /// <summary>
    /// Every WHERE of an UPDATE, DELETE or INSERT ... SELECT is evaluated by Dataverse (a plan that needs PPDS to filter
    /// rows is refused), so a WHERE literal is not a written value: it is sent in the FetchXML as typed, with or without N,
    /// as before this decision. SQL Server would compare a literal without N after converting it to the code page.
    /// </summary>
    [Theory]
    [InlineData("UPDATE account SET description = 'x' WHERE name = '" + Cyrillic + "'")]
    [InlineData("DELETE FROM account WHERE name = '" + Accented + "'")]
    [InlineData("UPDATE account SET description = 'x' WHERE name = N'" + Cyrillic + "'")]
    public async Task AWhereLiteral_IsSentToDataverseAsTyped_WithOrWithoutN(string sql)
    {
        var h = DmlTestHarness.Create(rows: 4);

        await h.Service.ExecuteAsync(new SqlQueryRequest { Sql = sql, DmlSafety = Confirmed });

        var typed = sql.Contains(Cyrillic) ? Cyrillic : Accented;
        Assert.Contains(h.Reads, r => r.FetchXml.Contains($"value=\"{typed}\""));
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
