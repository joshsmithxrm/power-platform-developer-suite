using Xunit;
using static PPDS.Cli.Tests.Services.Query.Faithful.FaithfulSelect;

namespace PPDS.Cli.Tests.Services.Query.Faithful;

/// <summary>
/// SELECT through the real engine against a fake that answers FetchXML like Dataverse (<see cref="FakeDataverse"/>).
/// The expected rows are what T-SQL returns for the statement over <see cref="SelectFixture"/> (three-valued logic, a
/// case- and accent-insensitive collation that ignores trailing spaces, NULL first in ascending order): a query returns
/// exactly what its text says. A case the engine gets wrong today names the issue that tracks the defect and pins the wrong
/// result it returns today (<see cref="FaithfulSelect.Verdict"/>); its T-SQL expectation is not changed. When the engine
/// changes, the case fails, and the marker is removed once the result is the expected one.
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
    [InlineData("description <> 'd2'", "k3,k5,k6,k7", KnownDefect.NegatedMatchesNull, "k1,k3,k4,k5,k6,k7,k8,k9")]
    [InlineData("numberofemployees != 5", "k2,k4,k5,k6,k8,k10", KnownDefect.NegatedMatchesNull, "k10,k2,k3,k4,k5,k6,k8,k9")]
    [InlineData("description NOT IN ('d2', 'x')", "k3,k6,k7", KnownDefect.NegatedMatchesNull, "k1,k3,k4,k6,k7,k8,k9")]
    [InlineData("name NOT LIKE 'a%'", "k3,k4,k5,k7,k9", KnownDefect.NegatedMatchesNull, "k3,k4,k5,k6,k7,k9")]
    [InlineData("description <> NULL", "", KnownDefect.NotEqualNull, "k1,k10,k2,k3,k4,k5,k6,k7,k8,k9")]
    // NOT (...)
    [InlineData("NOT (name = 'alpha')", "k3,k4,k5,k7,k8,k9,k10", KnownDefect.NotLost, "k1,k2")]
    [InlineData("NOT (description IS NULL)", "k2,k3,k5,k6,k7,k10", KnownDefect.NotLost, "k1,k4,k8,k9")]
    // Predicates on an expression
    [InlineData("UPPER(name) LIKE 'A%'", "k1,k2,k8,k10", KnownDefect.ExpressionDropped, "k1,k10,k2,k3,k4,k5,k6,k7,k8,k9")]
    [InlineData("LEN(name) = 4", "k3,k4,k5,k9")]
    [InlineData("numberofemployees + 1 > 10", "k2,k8,k10")]
    [InlineData("name = 'Beta' OR numberofemployees * 2 = 20", "k2,k3", KnownDefect.ExpressionInOr, "")]
    // Date-time literal without an offset (T-SQL: the stored UTC value)
    [InlineData("followupby = '2024-01-15 00:30:00'", "k1", KnownDefect.DateTimeNoOffset, "")]
    [InlineData("followupby >= '2024-01-15' AND followupby < '2024-01-16'", "k1,k2", KnownDefect.DateTimeNoOffset, "k2")]
    // Subqueries
    [InlineData("accountid IN (SELECT parentcustomerid FROM contact)", "k1,k2,k5")]
    [InlineData("accountid NOT IN (SELECT parentcustomerid FROM contact)", "", KnownDefect.NotInSubquery, "k1,k10,k2,k3,k4,k5,k6,k7,k8,k9")]
    public async Task Where_ReturnsExactlyTheMatchingRows(string where, string expected, string? defect = null, string? today = null)
    {
        var sql = $"{From} WHERE {where}";
        Verdict(sql, Sort(expected, false), await OutcomeAsync(sql, r => Keys(r, ordered: false)), defect, today);
    }

    [Fact]
    public async Task Where_WithVariable_ComparesTheVariableValue()
    {
        var sql = $"DECLARE @n nvarchar(10) = 'Beta'; {From} WHERE name = @n";
        Verdict(sql, "k3", await OutcomeAsync(sql, r => Keys(r, ordered: false)), KnownDefect.VariableAsText, "");
    }

    // ───────────── TOP and ORDER BY ─────────────

    [Theory]
    [InlineData("SELECT TOP 3 accountnumber FROM account ORDER BY numberofemployees DESC", "k8,k10,k2")]
    [InlineData("SELECT TOP (3) accountnumber FROM account ORDER BY numberofemployees DESC", "k8,k10,k2", KnownDefect.TopParenthesised, "k8,k10,k2,k6,k1,k7,k4,k5,k3,k9")]
    [InlineData("SELECT TOP 1 accountnumber FROM account WHERE LEN(name) = 4 ORDER BY numberofemployees DESC", "k4", KnownDefect.TopBeforeClientFilter, "")]
    [InlineData("SELECT accountnumber FROM account ORDER BY numberofemployees, accountnumber", "k3,k9,k5,k4,k1,k7,k6,k2,k10,k8")]
    [InlineData("SELECT accountnumber FROM account ORDER BY numberofemployees DESC, accountnumber", "k8,k10,k2,k6,k1,k7,k4,k5,k3,k9")]
    [InlineData("SELECT accountnumber FROM account ORDER BY customertypecode, accountnumber", "k4,k7,k1,k5,k9,k10,k2,k6,k3,k8", KnownDefect.ChoiceOrderByLabel, "k4,k7,k10,k2,k6,k3,k8,k1,k5,k9")]
    public async Task TopAndOrderBy_ReturnExactlyTheseRowsInOrder(string sql, string expected, string? defect = null, string? today = null)
        => Verdict(sql, expected, await OutcomeAsync(sql, r => Keys(r, ordered: true)), defect, today);

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

    [Theory]
    [InlineData("SELECT accountnumber, name AS n, UPPER(name) AS u FROM account WHERE accountnumber = 'k1'", "n,u", "Alpha,ALPHA", KnownDefect.ComputedInputsNull, "<null>,<null>")]
    [InlineData("SELECT accountnumber, numberofemployees AS e, numberofemployees + 1 AS m FROM account WHERE accountnumber = 'k1'", "e,m", "5,6", KnownDefect.ComputedInputsNull, "<null>,<null>")]
    public async Task Alias_NextToAComputedColumn_KeepsItsValue(string sql, string columns, string expected, string? defect = null, string? today = null)
        => Verdict(sql, expected, await OutcomeAsync(sql, r => Values(r, columns.Split(','))), defect, today);

    // ───────────── Joins ─────────────

    [Theory]
    [InlineData("", "k1:Smith,k1:Jones,k2:Brown,k5:Green")]
    [InlineData("WHERE c.lastname = 'Smith'", "k1:Smith", KnownDefect.JoinedCondition, "fault: 'account' entity doesn't contain attribute with Name = 'lastname' and NameMapping = 'Logical' (look up attribute by name is case-sensitive).")]
    // description exists on both tables: a condition applied to the wrong one returns other rows, not an error.
    [InlineData("WHERE c.description = 'x'", "k1:Smith", KnownDefect.JoinedCondition, "k5:Green")]
    [InlineData("WHERE c.lastname <> 'Smith' AND a.numberofemployees > 0", "k1:Jones,k2:Brown", KnownDefect.JoinedCondition, "fault: 'account' entity doesn't contain attribute with Name = 'lastname' and NameMapping = 'Logical' (look up attribute by name is case-sensitive).")]
    public async Task InnerJoin_ReturnsExactlyTheMatchingPairs(string where, string expected, string? defect = null, string? today = null)
    {
        var sql = $"SELECT a.accountnumber, c.lastname FROM account a JOIN contact c ON c.parentcustomerid = a.accountid {where}";
        Verdict(sql, SortPairs(expected), await OutcomeAsync(sql, Pairs), defect, today);
    }

    [Fact]
    public async Task LeftJoin_WhereChildIsNull_ReturnsTheAccountsWithoutContacts()
    {
        const string sql = "SELECT a.accountnumber, c.lastname FROM account a LEFT JOIN contact c ON c.parentcustomerid = a.accountid WHERE c.lastname IS NULL";
        Verdict(sql, SortPairs("k3:,k4:,k6:,k7:,k8:,k9:,k10:"), await OutcomeAsync(sql, Pairs), KnownDefect.JoinedCondition,
            "fault: 'account' entity doesn't contain attribute with Name = 'lastname' and NameMapping = 'Logical' (look up attribute by name is case-sensitive).");
    }

    private static string SortPairs(string pairs) => string.Join(",", pairs.Split(',').Order(StringComparer.Ordinal));

    private static string Pairs(PPDS.Dataverse.Query.QueryResult result)
    {
        var lastName = result.Columns.Select(c => c.Alias ?? c.LogicalName).FirstOrDefault(n => n.EndsWith("lastname", StringComparison.OrdinalIgnoreCase)) ?? "lastname";
        return string.Join(",", result.Records
            .Select(r => $"{(r.TryGetValue("accountnumber", out var k) ? k.Value : null)}:{(r.TryGetValue(lastName, out var l) ? l.Value : null)}")
            .Order(StringComparer.Ordinal));
    }
}
