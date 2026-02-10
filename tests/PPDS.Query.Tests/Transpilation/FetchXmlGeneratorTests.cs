using FluentAssertions;
using PPDS.Query.Parsing;
using PPDS.Query.Transpilation;
using Xunit;

namespace PPDS.Query.Tests.Transpilation;

[Trait("Category", "Unit")]
public class FetchXmlGeneratorTests
{
    private readonly QueryParser _parser = new();

    private string GenerateFetchXml(string sql)
    {
        var fragment = _parser.ParseStatement(sql);
        var generator = new FetchXmlGenerator();
        return generator.Generate(fragment);
    }

    private TranspileResult GenerateWithVirtualColumns(string sql)
    {
        var fragment = _parser.ParseStatement(sql);
        var generator = new FetchXmlGenerator();
        return generator.GenerateWithVirtualColumns(fragment);
    }

    // ────────────────────────────────────────────
    //  Simple SELECT: specific attributes
    // ────────────────────────────────────────────

    [Fact]
    public void Generate_SimpleSelectColumns_ContainsEntityAndAttributes()
    {
        var fetchXml = GenerateFetchXml("SELECT name, revenue FROM account");

        fetchXml.Should().Contain("<entity name=\"account\">");
        fetchXml.Should().Contain("<attribute name=\"name\"");
        fetchXml.Should().Contain("<attribute name=\"revenue\"");
        fetchXml.Should().Contain("</entity>");
        fetchXml.Should().Contain("</fetch>");
    }

    // ────────────────────────────────────────────
    //  Wildcard: generates all-attributes
    // ────────────────────────────────────────────

    [Fact]
    public void Generate_SelectStar_ContainsAllAttributes()
    {
        var fetchXml = GenerateFetchXml("SELECT * FROM account");

        fetchXml.Should().Contain("<all-attributes />");
    }

    // ────────────────────────────────────────────
    //  WHERE: eq operator
    // ────────────────────────────────────────────

    [Fact]
    public void Generate_WhereEquals_ContainsEqCondition()
    {
        var fetchXml = GenerateFetchXml(
            "SELECT name FROM account WHERE statecode = 0");

        fetchXml.Should().Contain("operator=\"eq\"");
        fetchXml.Should().Contain("attribute=\"statecode\"");
        fetchXml.Should().Contain("value=\"0\"");
    }

    // ────────────────────────────────────────────
    //  WHERE: ne operator
    // ────────────────────────────────────────────

    [Fact]
    public void Generate_WhereNotEquals_ContainsNeCondition()
    {
        var fetchXml = GenerateFetchXml(
            "SELECT name FROM account WHERE statecode <> 1");

        fetchXml.Should().Contain("operator=\"ne\"");
    }

    // ────────────────────────────────────────────
    //  WHERE: gt operator
    // ────────────────────────────────────────────

    [Fact]
    public void Generate_WhereGreaterThan_ContainsGtCondition()
    {
        var fetchXml = GenerateFetchXml(
            "SELECT name FROM account WHERE revenue > 1000");

        fetchXml.Should().Contain("operator=\"gt\"");
        fetchXml.Should().Contain("attribute=\"revenue\"");
    }

    // ────────────────────────────────────────────
    //  WHERE: lt operator
    // ────────────────────────────────────────────

    [Fact]
    public void Generate_WhereLessThan_ContainsLtCondition()
    {
        var fetchXml = GenerateFetchXml(
            "SELECT name FROM account WHERE revenue < 5000");

        fetchXml.Should().Contain("operator=\"lt\"");
    }

    // ────────────────────────────────────────────
    //  WHERE: LIKE with begins-with pattern
    // ────────────────────────────────────────────

    [Fact]
    public void Generate_WhereLikeBeginsWith_ContainsBeginsWithOperator()
    {
        var fetchXml = GenerateFetchXml(
            "SELECT name FROM account WHERE name LIKE 'Contoso%'");

        fetchXml.Should().Contain("operator=\"begins-with\"");
        fetchXml.Should().Contain("value=\"Contoso\"");
    }

    // ────────────────────────────────────────────
    //  WHERE: LIKE with ends-with pattern
    // ────────────────────────────────────────────

    [Fact]
    public void Generate_WhereLikeEndsWith_ContainsEndsWithOperator()
    {
        var fetchXml = GenerateFetchXml(
            "SELECT name FROM account WHERE name LIKE '%Inc'");

        fetchXml.Should().Contain("operator=\"ends-with\"");
        fetchXml.Should().Contain("value=\"Inc\"");
    }

    // ────────────────────────────────────────────
    //  WHERE: LIKE with contains pattern
    // ────────────────────────────────────────────

    [Fact]
    public void Generate_WhereLikeContains_ContainsLikeOperator()
    {
        var fetchXml = GenerateFetchXml(
            "SELECT name FROM account WHERE name LIKE '%fabrikam%'");

        fetchXml.Should().Contain("operator=\"like\"");
    }

    // ────────────────────────────────────────────
    //  WHERE: IN operator
    // ────────────────────────────────────────────

    [Fact]
    public void Generate_WhereIn_ContainsInCondition()
    {
        var fetchXml = GenerateFetchXml(
            "SELECT name FROM account WHERE statecode IN (0, 1)");

        fetchXml.Should().Contain("operator=\"in\"");
        fetchXml.Should().Contain("<value>0</value>");
        fetchXml.Should().Contain("<value>1</value>");
    }

    // ────────────────────────────────────────────
    //  WHERE: IS NULL
    // ────────────────────────────────────────────

    [Fact]
    public void Generate_WhereIsNull_ContainsNullOperator()
    {
        var fetchXml = GenerateFetchXml(
            "SELECT name FROM account WHERE revenue IS NULL");

        fetchXml.Should().Contain("operator=\"null\"");
        fetchXml.Should().Contain("attribute=\"revenue\"");
    }

    // ────────────────────────────────────────────
    //  WHERE: IS NOT NULL
    // ────────────────────────────────────────────

    [Fact]
    public void Generate_WhereIsNotNull_ContainsNotNullOperator()
    {
        var fetchXml = GenerateFetchXml(
            "SELECT name FROM account WHERE revenue IS NOT NULL");

        fetchXml.Should().Contain("operator=\"not-null\"");
    }

    // ────────────────────────────────────────────
    //  WHERE: BETWEEN
    // ────────────────────────────────────────────

    [Fact]
    public void Generate_WhereBetween_ContainsBetweenCondition()
    {
        var fetchXml = GenerateFetchXml(
            "SELECT name FROM account WHERE revenue BETWEEN 1000 AND 5000");

        fetchXml.Should().Contain("operator=\"between\"");
        fetchXml.Should().Contain("<value>1000</value>");
        fetchXml.Should().Contain("<value>5000</value>");
    }

    [Fact]
    public void Generate_WhereExists_ThrowsNotSupportedException()
    {
        var act = () => GenerateFetchXml(
            "SELECT name FROM account WHERE EXISTS (SELECT 1 FROM contact)");

        act.Should().Throw<NotSupportedException>()
            .WithMessage("*EXISTS*");
    }

    [Fact]
    public void Generate_WhereInSubquery_ThrowsNotSupportedException()
    {
        var act = () => GenerateFetchXml(
            "SELECT name FROM account WHERE accountid IN (SELECT parentcustomerid FROM contact)");

        act.Should().Throw<NotSupportedException>()
            .WithMessage("*IN (SELECT*");
    }

    [Fact]
    public void Generate_WhereNotInSubquery_ThrowsNotSupportedException()
    {
        var act = () => GenerateFetchXml(
            "SELECT name FROM account WHERE accountid NOT IN (SELECT parentcustomerid FROM contact)");

        act.Should().Throw<NotSupportedException>()
            .WithMessage("*IN (SELECT*");
    }

    // ────────────────────────────────────────────
    //  JOIN generates link-entity
    // ────────────────────────────────────────────

    [Fact]
    public void Generate_InnerJoin_ContainsLinkEntity()
    {
        var fetchXml = GenerateFetchXml(
            "SELECT a.name, c.fullname FROM account a " +
            "INNER JOIN contact c ON c.parentcustomerid = a.accountid");

        fetchXml.Should().Contain("<link-entity name=\"contact\"");
        fetchXml.Should().Contain("link-type=\"inner\"");
        fetchXml.Should().Contain("from=\"parentcustomerid\"");
        fetchXml.Should().Contain("to=\"accountid\"");
    }

    [Fact]
    public void Generate_LeftJoin_ContainsOuterLinkEntity()
    {
        var fetchXml = GenerateFetchXml(
            "SELECT a.name, c.fullname FROM account a " +
            "LEFT JOIN contact c ON c.parentcustomerid = a.accountid");

        fetchXml.Should().Contain("link-type=\"outer\"");
    }

    // ────────────────────────────────────────────
    //  TOP generates fetch/@top
    // ────────────────────────────────────────────

    [Fact]
    public void Generate_Top_ContainsFetchTopAttribute()
    {
        var fetchXml = GenerateFetchXml("SELECT TOP 10 name FROM account");

        fetchXml.Should().Contain("top=\"10\"");
    }

    // ────────────────────────────────────────────
    //  DISTINCT generates fetch/@distinct
    // ────────────────────────────────────────────

    [Fact]
    public void Generate_Distinct_ContainsFetchDistinctAttribute()
    {
        var fetchXml = GenerateFetchXml("SELECT DISTINCT name FROM account");

        fetchXml.Should().Contain("distinct=\"true\"");
    }

    // ────────────────────────────────────────────
    //  Aggregate: COUNT
    // ────────────────────────────────────────────

    [Fact]
    public void Generate_CountStar_ContainsAggregateCountAttribute()
    {
        var fetchXml = GenerateFetchXml(
            "SELECT COUNT(*) AS cnt FROM account");

        fetchXml.Should().Contain("aggregate=\"true\"");
        fetchXml.Should().Contain("aggregate=\"count\"");
        fetchXml.Should().Contain("alias=\"cnt\"");
    }

    // ────────────────────────────────────────────
    //  Aggregate: SUM with GROUP BY
    // ────────────────────────────────────────────

    [Fact]
    public void Generate_SumWithGroupBy_ContainsAggregateAndGroupBy()
    {
        var fetchXml = GenerateFetchXml(
            "SELECT statecode, SUM(revenue) AS total_revenue FROM account GROUP BY statecode");

        fetchXml.Should().Contain("aggregate=\"true\"");
        fetchXml.Should().Contain("aggregate=\"sum\"");
        fetchXml.Should().Contain("alias=\"total_revenue\"");
        fetchXml.Should().Contain("groupby=\"true\"");
    }

    // ────────────────────────────────────────────
    //  Aggregate: AVG
    // ────────────────────────────────────────────

    [Fact]
    public void Generate_Avg_ContainsAggregateAvg()
    {
        var fetchXml = GenerateFetchXml(
            "SELECT AVG(revenue) AS avg_revenue FROM account");

        fetchXml.Should().Contain("aggregate=\"avg\"");
        fetchXml.Should().Contain("alias=\"avg_revenue\"");
    }

    // ────────────────────────────────────────────
    //  ORDER BY generates order elements
    // ────────────────────────────────────────────

    [Fact]
    public void Generate_OrderByAsc_ContainsOrderElement()
    {
        var fetchXml = GenerateFetchXml(
            "SELECT name FROM account ORDER BY name ASC");

        fetchXml.Should().Contain("<order attribute=\"name\" descending=\"false\"");
    }

    [Fact]
    public void Generate_OrderByDesc_ContainsDescendingOrder()
    {
        var fetchXml = GenerateFetchXml(
            "SELECT name FROM account ORDER BY name DESC");

        fetchXml.Should().Contain("<order attribute=\"name\" descending=\"true\"");
    }

    // ────────────────────────────────────────────
    //  Virtual column detection
    // ────────────────────────────────────────────

    [Fact]
    public void GenerateWithVirtualColumns_OwneridName_DetectedAsVirtual()
    {
        var result = GenerateWithVirtualColumns(
            "SELECT ownerid, owneridname FROM account");

        result.VirtualColumns.Should().ContainKey("owneridname");
        result.VirtualColumns["owneridname"].BaseColumnName.Should().Be("ownerid");
        result.VirtualColumns["owneridname"].BaseColumnExplicitlyQueried.Should().BeTrue();
    }

    [Fact]
    public void GenerateWithVirtualColumns_StatecodeName_DetectedAsVirtual()
    {
        var result = GenerateWithVirtualColumns(
            "SELECT statecodename FROM account");

        result.VirtualColumns.Should().ContainKey("statecodename");
        result.VirtualColumns["statecodename"].BaseColumnName.Should().Be("statecode");
    }

    // ────────────────────────────────────────────
    //  Entity/attribute names normalized to lowercase
    // ────────────────────────────────────────────

    [Fact]
    public void Generate_MixedCaseNames_NormalizedToLowercase()
    {
        var fetchXml = GenerateFetchXml("SELECT Name FROM Account");

        fetchXml.Should().Contain("<entity name=\"account\">");
        fetchXml.Should().Contain("name=\"name\"");
    }

    // ────────────────────────────────────────────
    //  WHERE: AND / OR
    // ────────────────────────────────────────────

    [Fact]
    public void Generate_WhereWithAnd_ContainsAndFilter()
    {
        var fetchXml = GenerateFetchXml(
            "SELECT name FROM account WHERE statecode = 0 AND revenue > 1000");

        fetchXml.Should().Contain("filter type=\"and\"");
    }

    [Fact]
    public void Generate_WhereWithOr_ContainsOrFilter()
    {
        var fetchXml = GenerateFetchXml(
            "SELECT name FROM account WHERE statecode = 0 OR statecode = 1");

        fetchXml.Should().Contain("filter type=\"or\"");
    }

    // ────────────────────────────────────────────
    //  String value XML-escaping
    // ────────────────────────────────────────────

    [Fact]
    public void Generate_StringWithSpecialChars_EscapedInFetchXml()
    {
        var fetchXml = GenerateFetchXml(
            "SELECT name FROM account WHERE name = 'A&B'");

        fetchXml.Should().Contain("A&amp;B");
    }

    // ────────────────────────────────────────────
    //  JOIN columns from linked entity
    // ────────────────────────────────────────────

    [Fact]
    public void Generate_JoinWithColumns_ContainsLinkEntityAttributes()
    {
        var fetchXml = GenerateFetchXml(
            "SELECT a.name, c.fullname FROM account a " +
            "INNER JOIN contact c ON c.parentcustomerid = a.accountid");

        fetchXml.Should().Contain("<attribute name=\"fullname\"");
    }

    // ────────────────────────────────────────────
    //  ge / le operators
    // ────────────────────────────────────────────

    [Fact]
    public void Generate_WhereGreaterThanOrEqual_ContainsGeCondition()
    {
        var fetchXml = GenerateFetchXml(
            "SELECT name FROM account WHERE revenue >= 1000");

        fetchXml.Should().Contain("operator=\"ge\"");
    }

    [Fact]
    public void Generate_WhereLessThanOrEqual_ContainsLeCondition()
    {
        var fetchXml = GenerateFetchXml(
            "SELECT name FROM account WHERE revenue <= 5000");

        fetchXml.Should().Contain("operator=\"le\"");
    }
}
