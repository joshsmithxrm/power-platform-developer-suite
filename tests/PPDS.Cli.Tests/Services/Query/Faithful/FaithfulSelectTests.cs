using Xunit;
using static PPDS.Cli.Tests.Services.Query.Faithful.FaithfulSelect;

namespace PPDS.Cli.Tests.Services.Query.Faithful;

/// <summary>
/// SELECT through the real engine against a fake that answers FetchXML like Dataverse (<see cref="FakeDataverse"/>).
/// The expected rows are what T-SQL returns for the statement over <see cref="SelectFixture"/> (three-valued logic, a
/// case- and accent-insensitive collation that ignores trailing spaces, NULL first in ascending order): a query returns
/// exactly what its text says. A case the engine gets wrong today is skipped with the issue that tracks the defect; its
/// expectation is not changed.
/// </summary>
[Trait("Category", "Unit")]
public class FaithfulSelectTests
{
    private const string From = "SELECT accountnumber FROM account";

    // ───────────── WHERE: conditions the engine sends to Dataverse ─────────────

    [Theory]
    [InlineData("name = 'alpha'", "k1,k2")]
    [InlineData("name = 'cafe'", "k4,k5")]
    [InlineData("name = 'trail'", "k7")]
    [InlineData("numberofemployees > 5", "k2,k6,k8,k10")]
    [InlineData("numberofemployees BETWEEN 0 AND 10", "k1,k2,k4,k6,k7")]
    [InlineData("numberofemployees NOT BETWEEN 0 AND 10", "k5,k8,k10")]
    [InlineData("numberofemployees IN (5, 100)", "k1,k7,k8")]
    [InlineData("name LIKE 'a%'", "k1,k2,k8,k10")]
    [InlineData("name LIKE 'a[_]c'", "k8")]
    [InlineData("name LIKE '[ab]%'", "k1,k2,k3,k8,k10")]
    [InlineData("description IS NULL", "k1,k4,k8,k9")]
    [InlineData("description IS NOT NULL", "k2,k3,k5,k6,k7,k10")]
    [InlineData("name = 'alpha' OR numberofemployees = 100", "k1,k2,k8")]
    [InlineData("(name LIKE 'a%' OR name = 'Beta') AND numberofemployees > 5", "k2,k8,k10")]
    [InlineData("revenue >= 100", "k1,k3,k5,k10")]
    [InlineData("customertypecode = 2", "k2,k6,k10")]
    [InlineData("donotemail = 1", "k1,k5,k7,k10")]
    [InlineData("ppds_startdate = '2024-01-15'", "k1")]
    [InlineData("followupby = '2024-01-15T00:30:00Z'", "k1")]
    [InlineData("parentaccountid IS NOT NULL", "k3")]
    [InlineData("accountid = '133f5e40-0000-0000-0000-000000000004'", "k4")]
    [InlineData("description = NULL", "")]
    // Negated operators: T-SQL never matches NULL; Dataverse's ne / not-in / not-like do.
    [InlineData("description <> 'd2'", "k3,k5,k6,k7", Skip = KnownDefect.NegatedMatchesNull)]
    [InlineData("numberofemployees != 5", "k2,k4,k5,k6,k8,k10", Skip = KnownDefect.NegatedMatchesNull)]
    [InlineData("description NOT IN ('d2', 'x')", "k3,k6,k7", Skip = KnownDefect.NegatedMatchesNull)]
    [InlineData("name NOT LIKE 'a%'", "k3,k4,k5,k7,k9", Skip = KnownDefect.NegatedMatchesNull)]
    [InlineData("description <> NULL", "", Skip = KnownDefect.NotEqualNull)]
    // NOT (...)
    [InlineData("NOT (name = 'alpha')", "k3,k4,k5,k7,k8,k9,k10", Skip = KnownDefect.NotLost)]
    [InlineData("NOT (description IS NULL)", "k2,k3,k5,k6,k7,k10", Skip = KnownDefect.NotLost)]
    // Predicates on an expression
    [InlineData("UPPER(name) LIKE 'A%'", "k1,k2,k8,k10", Skip = KnownDefect.ExpressionDropped)]
    [InlineData("LEN(name) = 4", "k3,k4,k5,k9")]
    [InlineData("numberofemployees + 1 > 10", "k2,k8,k10")]
    [InlineData("name = 'Beta' OR numberofemployees * 2 = 20", "k2,k3", Skip = KnownDefect.ExpressionInOr)]
    // Date-time literal without an offset (T-SQL: the stored UTC value)
    [InlineData("followupby = '2024-01-15 00:30:00'", "k1", Skip = KnownDefect.DateTimeNoOffset)]
    [InlineData("followupby >= '2024-01-15' AND followupby < '2024-01-16'", "k1,k2", Skip = KnownDefect.DateTimeNoOffset)]
    // Subqueries
    [InlineData("accountid IN (SELECT parentcustomerid FROM contact)", "k1,k2,k5")]
    [InlineData("accountid NOT IN (SELECT parentcustomerid FROM contact)", "", Skip = KnownDefect.NotInSubquery)]
    public Task Where_ReturnsExactlyTheMatchingRows(string where, string expected)
        => AssertKeysAsync($"{From} WHERE {where}", expected);

    [Fact(Skip = KnownDefect.VariableAsText)]
    public Task Where_WithVariable_ComparesTheVariableValue()
        => AssertKeysAsync($"DECLARE @n nvarchar(10) = 'Beta'; {From} WHERE name = @n", "k3");

    // ───────────── TOP and ORDER BY ─────────────

    [Theory]
    [InlineData("SELECT TOP 3 accountnumber FROM account ORDER BY numberofemployees DESC", "k8,k10,k2")]
    [InlineData("SELECT TOP (3) accountnumber FROM account ORDER BY numberofemployees DESC", "k8,k10,k2", Skip = KnownDefect.TopParenthesised)]
    [InlineData("SELECT TOP 1 accountnumber FROM account WHERE LEN(name) = 4 ORDER BY numberofemployees DESC", "k4", Skip = KnownDefect.TopBeforeClientFilter)]
    [InlineData("SELECT accountnumber FROM account ORDER BY numberofemployees, accountnumber", "k3,k9,k5,k4,k1,k7,k6,k2,k10,k8")]
    [InlineData("SELECT accountnumber FROM account ORDER BY numberofemployees DESC, accountnumber", "k8,k10,k2,k6,k1,k7,k4,k5,k3,k9")]
    [InlineData("SELECT accountnumber FROM account ORDER BY customertypecode, accountnumber", "k4,k7,k1,k5,k9,k10,k2,k6,k3,k8", Skip = KnownDefect.ChoiceOrderByLabel)]
    public Task TopAndOrderBy_ReturnExactlyTheseRowsInOrder(string sql, string expected)
        => AssertKeysAsync(sql, expected, ordered: true);

    [Fact]
    public async Task MaxRowsHint_ReturnsExactlyThatManyRows()
    {
        var result = await RunAsync("-- ppds:MAX_ROWS 4\nSELECT accountnumber FROM account ORDER BY accountnumber");

        AssertKeys(result, "k1,k10,k2,k3", ordered: true, "MAX_ROWS 4");
    }

    // ───────────── Aliases and projection ─────────────

    [Fact]
    public async Task Alias_ColumnsComeBackUnderTheirAlias()
    {
        var result = await RunAsync("SELECT accountnumber AS k, name AS n FROM account WHERE accountnumber = 'k1'");

        Assert.Equal(new object?[] { "k1" }, Column(result, "k"));
        Assert.Equal(new object?[] { "Alpha" }, Column(result, "n"));
    }

    [Fact(Skip = KnownDefect.ComputedInputsNull)]
    public async Task Alias_NextToAComputedColumn_KeepsItsValue()
    {
        var result = await RunAsync("SELECT accountnumber, name AS n, UPPER(name) AS u FROM account WHERE accountnumber = 'k1'");

        Assert.Equal(new object?[] { "Alpha" }, Column(result, "n"));
        Assert.Equal(new object?[] { "ALPHA" }, Column(result, "u"));
    }

    [Fact(Skip = KnownDefect.ComputedInputsNull)]
    public async Task Alias_OfAColumnAlsoUsedInAComputedColumn_KeepsBothValues()
    {
        var result = await RunAsync("SELECT accountnumber, numberofemployees AS e, numberofemployees + 1 AS m FROM account WHERE accountnumber = 'k1'");

        Assert.Equal(new object?[] { 5 }, Column(result, "e").Select(v => v is null ? null : (object)Convert.ToInt32(v)));
        Assert.Equal(new object?[] { 6 }, Column(result, "m").Select(v => v is null ? null : (object)Convert.ToInt32(v)));
    }

    // ───────────── Joins ─────────────

    [Theory]
    [InlineData("", "k1:Smith,k1:Jones,k2:Brown,k5:Green")]
    [InlineData("WHERE c.lastname = 'Smith'", "k1:Smith", Skip = KnownDefect.JoinedCondition)]
    // description exists on both tables: a condition applied to the wrong one returns other rows, not an error.
    [InlineData("WHERE c.description = 'x'", "k1:Smith", Skip = KnownDefect.JoinedCondition)]
    [InlineData("WHERE c.lastname <> 'Smith' AND a.numberofemployees > 0", "k1:Jones,k2:Brown", Skip = KnownDefect.JoinedCondition)]
    public async Task InnerJoin_ReturnsExactlyTheMatchingPairs(string where, string expected)
    {
        var result = await RunAsync($"SELECT a.accountnumber, c.lastname FROM account a JOIN contact c ON c.parentcustomerid = a.accountid {where}");

        AssertPairs(result, expected);
    }

    [Fact(Skip = KnownDefect.JoinedCondition)]
    public async Task LeftJoin_WhereChildIsNull_ReturnsTheAccountsWithoutContacts()
    {
        var result = await RunAsync("SELECT a.accountnumber, c.lastname FROM account a LEFT JOIN contact c ON c.parentcustomerid = a.accountid WHERE c.lastname IS NULL");

        AssertPairs(result, "k3:,k4:,k6:,k7:,k8:,k9:,k10:");
    }

    private static void AssertPairs(PPDS.Dataverse.Query.QueryResult result, string expected)
    {
        var lastName = result.Columns.Select(c => c.Alias ?? c.LogicalName).FirstOrDefault(n => n.EndsWith("lastname", StringComparison.OrdinalIgnoreCase)) ?? "lastname";
        var actual = result.Records
            .Select(r => $"{(r.TryGetValue("accountnumber", out var k) ? k.Value : null)}:{(r.TryGetValue(lastName, out var l) ? l.Value : null)}")
            .Order(StringComparer.Ordinal).ToList();
        var wanted = expected.Split(',').Order(StringComparer.Ordinal).ToList();
        Assert.True(wanted.SequenceEqual(actual), $"expected [{string.Join(",", wanted)}]\nactual   [{string.Join(",", actual)}]");
    }
}
