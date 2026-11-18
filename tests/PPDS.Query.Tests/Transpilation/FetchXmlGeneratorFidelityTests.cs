using FluentAssertions;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using PPDS.Query.Parsing;
using PPDS.Query.Transpilation;
using Xunit;

namespace PPDS.Query.Tests.Transpilation;

/// <summary>
/// A SELECT returns what its text says or fails with a clear error: TOP forms, aliases next to computed columns,
/// names that cannot be written into FetchXML safely, NOT, = NULL and variables in a pushed-down WHERE.
/// </summary>
[Trait("Category", "PlanUnit")]
public class FetchXmlGeneratorFidelityTests
{
    private static string Fetch(string sql)
    {
        var script = (TSqlScript)new QueryParser().Parse(sql);
        return new FetchXmlGenerator().Generate((SelectStatement)script.Batches[0].Statements[0]);
    }

    [Theory]
    [InlineData("SELECT TOP 5 name FROM account")]
    [InlineData("SELECT TOP (5) name FROM account")]
    [InlineData("SELECT TOP ((5)) name FROM account")]
    public void TopWithAndWithoutParentheses_AreTheSame(string sql)
        => Fetch(sql).Should().MatchRegex("(count|top)=\"5\"");

    [Theory]
    [InlineData("DECLARE @n int = 5; SELECT TOP (@n) name FROM account")]
    [InlineData("SELECT TOP (2 + 3) name FROM account")]
    [InlineData("SELECT TOP 10 PERCENT name FROM account")]
    public void TopThatIsNotAWholeNumber_IsAnError(string sql)
    {
        var script = (TSqlScript)new QueryParser().Parse(sql);
        var select = script.Batches[0].Statements.OfType<SelectStatement>().Single();
        var act = () => new FetchXmlGenerator().Generate(select);
        act.Should().Throw<QueryParseException>().WithMessage("*TOP*");
    }

    [Fact]
    public void AliasedColumnNextToAComputedColumnOfTheSameBase_StillFetchesTheBaseColumn()
    {
        var fetch = Fetch("SELECT name AS n, UPPER(name) AS d FROM account");

        fetch.Should().Contain("<attribute name=\"name\" alias=\"n\" />");
        fetch.Should().Contain("<attribute name=\"name\" />");
    }

    [Theory]
    [InlineData("SELECT name FROM account WHERE NOT (name = 'x')", "NOT")]
    [InlineData("SELECT name FROM account WHERE name = NULL", "IS NULL")]
    [InlineData("SELECT name FROM account WHERE name <> NULL", "IS NULL")]
    [InlineData("DECLARE @n nvarchar(5) = 'x'; SELECT name FROM account WHERE name = @n", "@n")]
    public void WhereThatCannotBeSentFaithfully_IsAnError(string sql, string named)
    {
        var script = (TSqlScript)new QueryParser().Parse(sql);
        var select = script.Batches[0].Statements.OfType<SelectStatement>().Single();
        var act = () => new FetchXmlGenerator().Generate(select);
        act.Should().Throw<QueryParseException>().Where(e => e.Message.Contains(named));
    }

    [Theory]
    [InlineData("SELECT [name\" operator=\"not-null\"/><!--] FROM account")]
    [InlineData("SELECT name FROM [account\"><all-attributes/></entity><!--]")]
    [InlineData("SELECT name FROM account WHERE [accountid\" operator=\"not-null\"/><!--] = 'x'")]
    [InlineData("SELECT [na\nme] FROM account")]
    [InlineData("SELECT [name]] FROM account --] FROM account")]
    public void HostileIdentifiers_AreRefused_NeverWrittenIntoFetchXml(string sql)
    {
        var act = () => Fetch(sql);
        act.Should().Throw<QueryParseException>().WithMessage("*not a valid Dataverse table or column name*");
    }

    [Fact]
    public void HostileAlias_IsEscaped()
    {
        var fetch = Fetch("SELECT name AS [a\"/><x b=\"] FROM account");

        fetch.Should().Contain("alias=\"a&quot;/&gt;&lt;x b=&quot;\"");
        fetch.Should().NotContain("<x b=");
    }

    [Fact]
    public void ControlCharactersInValues_AreEncoded()
        => Fetch("SELECT name FROM account WHERE name = 'a\tb'").Should().Contain("value=\"a&#9;b\"");

    [Theory]
    [InlineData("SELECT name FROM account WHERE name IS NOT NULL", "operator=\"not-null\"")]
    [InlineData("SELECT name FROM account WHERE name NOT IN ('a')", "operator=\"not-in\"")]
    public void SupportedNegations_StillWork(string sql, string expected)
        => Fetch(sql).Should().Contain(expected);

    // ---- S6: the shared column collector and plain SELECT ------------------------------------

    [Fact]
    public void WindowFunctionOrderColumns_AreNotFetched_SoTheResultShapeIsUnchanged()
        => Fetch("SELECT name, ROW_NUMBER() OVER (ORDER BY createdon DESC) AS rn FROM account")
            .Should().NotContain("<attribute name=\"createdon\"");

    [Theory]
    [InlineData("SELECT COALESCE(description, name) AS d FROM account", "description", "name")]
    [InlineData("SELECT NULLIF(name, 'x') AS d FROM account", "name", "name")]
    [InlineData("SELECT CASE statecode WHEN 0 THEN name END AS d FROM account", "statecode", "name")]
    [InlineData("SELECT TRY_CAST(revenue AS int) AS d FROM account", "revenue", "revenue")]
    public void ComputedColumns_FetchEveryColumnTheyRead(string sql, string first, string second)
    {
        var fetch = Fetch(sql);
        fetch.Should().Contain($"<attribute name=\"{first}\"").And.Contain($"<attribute name=\"{second}\"");
    }

    [Theory]
    [InlineData("SELECT name FROM account WHERE name <> 'x'", "ne")]
    [InlineData("SELECT name FROM account WHERE name NOT IN ('x', 'y')", "not-in")]
    [InlineData("SELECT name FROM account WHERE name NOT LIKE '%x%'", "not-like")]
    [InlineData("SELECT name FROM account WHERE name NOT LIKE 'x%'", "not-begin-with")]
    public void NegatedConditions_AlsoRequireNotNull_LikeSql(string sql, string op)
    {
        var fetch = Fetch(sql);
        fetch.Should().Contain($"operator=\"{op}\"");
        fetch.Should().Contain("<condition attribute=\"name\" operator=\"not-null\" />");
        System.Xml.Linq.XDocument.Parse(fetch); // still well-formed
    }

    [Fact]
    public void NotBetween_IsNotPaired_DataverseAlreadyExcludesNull()
        => Fetch("SELECT name FROM account WHERE revenue NOT BETWEEN 1 AND 2").Should().NotContain("not-null");
    [Fact]
    public void ComputedItemOverAGroupByColumn_ReadsTheGroupedColumn_NotAnUngroupedAttribute()
    {
        // SELECT name AS n, COUNT(*) AS c, UPPER(name) AS u ... GROUP BY name: u needs name, which an aggregate query
        // may return only grouped (an ungrouped attribute is not allowed there).
        var fetch = Fetch("SELECT name AS n, COUNT(*) AS c, UPPER(name) AS u FROM account GROUP BY name");

        fetch.Should().Contain("<attribute name=\"name\" alias=\"n\" groupby=\"true\" />");
        fetch.Should().Contain("<attribute name=\"name\" groupby=\"true\" alias=\"name\" />");
        fetch.Should().NotContain("<attribute name=\"name\" />");
    }
}