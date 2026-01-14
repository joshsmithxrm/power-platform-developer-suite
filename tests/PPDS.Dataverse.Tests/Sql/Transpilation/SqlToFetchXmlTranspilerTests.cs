using System.Xml.Linq;
using FluentAssertions;
using PPDS.Dataverse.Sql.Parsing;
using PPDS.Dataverse.Sql.Transpilation;
using Xunit;

namespace PPDS.Dataverse.Tests.Sql.Transpilation;

public class SqlToFetchXmlTranspilerTests
{
    private readonly SqlToFetchXmlTranspiler _transpiler = new();

    private string Transpile(string sql)
    {
        var parser = new SqlParser(sql);
        var ast = parser.Parse();
        return _transpiler.Transpile(ast);
    }

    private XDocument ParseFetchXml(string fetchXml)
    {
        return XDocument.Parse(fetchXml);
    }

    #region Basic SELECT

    [Fact]
    public void Transpile_SimpleSelect_ProducesValidFetchXml()
    {
        // Arrange & Act
        var fetchXml = Transpile("SELECT name FROM account");

        // Assert
        var doc = ParseFetchXml(fetchXml);
        doc.Root!.Name.LocalName.Should().Be("fetch");
        var entity = doc.Root.Element("entity");
        entity.Should().NotBeNull();
        entity!.Attribute("name")!.Value.Should().Be("account");
    }

    [Fact]
    public void Transpile_SelectWithColumns_ProducesAttributeElements()
    {
        // Arrange & Act
        var fetchXml = Transpile("SELECT name, accountid, revenue FROM account");
        var doc = ParseFetchXml(fetchXml);

        // Assert
        var entity = doc.Root!.Element("entity");
        var attributes = entity!.Elements("attribute").ToList();
        attributes.Should().HaveCount(3);
        attributes.Select(a => a.Attribute("name")!.Value)
            .Should().Contain("name")
            .And.Contain("accountid")
            .And.Contain("revenue");
    }

    [Fact]
    public void Transpile_SelectStar_ProducesAllAttributes()
    {
        // Arrange & Act
        var fetchXml = Transpile("SELECT * FROM account");
        var doc = ParseFetchXml(fetchXml);

        // Assert
        var entity = doc.Root!.Element("entity");
        var allAttrs = entity!.Element("all-attributes");
        allAttrs.Should().NotBeNull();
    }

    [Fact]
    public void Transpile_ColumnWithAlias_ProducesAliasAttribute()
    {
        // Arrange & Act
        var fetchXml = Transpile("SELECT name AS accountname FROM account");
        var doc = ParseFetchXml(fetchXml);

        // Assert
        var entity = doc.Root!.Element("entity");
        var attr = entity!.Element("attribute");
        attr!.Attribute("alias")!.Value.Should().Be("accountname");
    }

    #endregion

    #region TOP

    [Fact]
    public void Transpile_TopClause_ProducesTopAttribute()
    {
        // Arrange & Act
        var fetchXml = Transpile("SELECT TOP 10 name FROM account");
        var doc = ParseFetchXml(fetchXml);

        // Assert
        doc.Root!.Attribute("top")!.Value.Should().Be("10");
    }

    #endregion

    #region DISTINCT

    [Fact]
    public void Transpile_Distinct_ProducesDistinctAttribute()
    {
        // Arrange & Act
        var fetchXml = Transpile("SELECT DISTINCT name FROM account");
        var doc = ParseFetchXml(fetchXml);

        // Assert
        doc.Root!.Attribute("distinct")!.Value.Should().Be("true");
    }

    #endregion

    #region WHERE Clause

    [Fact]
    public void Transpile_WhereEquals_ProducesFilter()
    {
        // Arrange & Act
        var fetchXml = Transpile("SELECT name FROM account WHERE statecode = 0");
        var doc = ParseFetchXml(fetchXml);

        // Assert
        var entity = doc.Root!.Element("entity");
        var filter = entity!.Element("filter");
        filter.Should().NotBeNull();
        var condition = filter!.Element("condition");
        condition.Should().NotBeNull();
        condition!.Attribute("attribute")!.Value.Should().Be("statecode");
        condition.Attribute("operator")!.Value.Should().Be("eq");
        condition.Attribute("value")!.Value.Should().Be("0");
    }

    [Fact]
    public void Transpile_WhereNotEquals_ProducesNeOperator()
    {
        // Arrange & Act
        var fetchXml = Transpile("SELECT name FROM account WHERE statecode <> 1");
        var doc = ParseFetchXml(fetchXml);

        // Assert
        var condition = doc.Descendants("condition").First();
        condition.Attribute("operator")!.Value.Should().Be("ne");
    }

    [Theory]
    [InlineData("<", "lt")]
    [InlineData(">", "gt")]
    [InlineData("<=", "le")]
    [InlineData(">=", "ge")]
    public void Transpile_ComparisonOperators_ProducesCorrectOperator(string sqlOp, string fetchOp)
    {
        // Arrange & Act
        var fetchXml = Transpile($"SELECT name FROM account WHERE revenue {sqlOp} 1000");
        var doc = ParseFetchXml(fetchXml);

        // Assert
        var condition = doc.Descendants("condition").First();
        condition.Attribute("operator")!.Value.Should().Be(fetchOp);
    }

    [Fact]
    public void Transpile_WhereWithStringValue_EscapesXml()
    {
        // Arrange & Act
        var fetchXml = Transpile("SELECT name FROM account WHERE name = 'O''Brien & Co.'");
        var doc = ParseFetchXml(fetchXml);

        // Assert - the value should be XML-escaped when parsed
        var condition = doc.Descendants("condition").First();
        condition.Attribute("value")!.Value.Should().Contain("O'Brien");
    }

    [Fact]
    public void Transpile_WhereAnd_ProducesAndFilter()
    {
        // Arrange & Act
        var fetchXml = Transpile("SELECT name FROM account WHERE statecode = 0 AND revenue > 1000");
        var doc = ParseFetchXml(fetchXml);

        // Assert
        var filter = doc.Descendants("filter").First();
        filter.Attribute("type")!.Value.Should().Be("and");
        filter.Elements("condition").Should().HaveCount(2);
    }

    [Fact]
    public void Transpile_WhereOr_ProducesOrFilter()
    {
        // Arrange & Act
        var fetchXml = Transpile("SELECT name FROM account WHERE statecode = 0 OR statecode = 1");
        var doc = ParseFetchXml(fetchXml);

        // Assert
        var filter = doc.Descendants("filter").First();
        filter.Attribute("type")!.Value.Should().Be("or");
    }

    [Fact]
    public void Transpile_WhereIsNull_ProducesNullOperator()
    {
        // Arrange & Act
        var fetchXml = Transpile("SELECT name FROM account WHERE parentaccountid IS NULL");
        var doc = ParseFetchXml(fetchXml);

        // Assert
        var condition = doc.Descendants("condition").First();
        condition.Attribute("operator")!.Value.Should().Be("null");
    }

    [Fact]
    public void Transpile_WhereIsNotNull_ProducesNotNullOperator()
    {
        // Arrange & Act
        var fetchXml = Transpile("SELECT name FROM account WHERE parentaccountid IS NOT NULL");
        var doc = ParseFetchXml(fetchXml);

        // Assert
        var condition = doc.Descendants("condition").First();
        condition.Attribute("operator")!.Value.Should().Be("not-null");
    }

    [Fact]
    public void Transpile_WhereLike_ProducesLikeOperator()
    {
        // Arrange & Act
        var fetchXml = Transpile("SELECT name FROM account WHERE name LIKE '%contoso%'");
        var doc = ParseFetchXml(fetchXml);

        // Assert
        var condition = doc.Descendants("condition").First();
        condition.Attribute("operator")!.Value.Should().Be("like");
        condition.Attribute("value")!.Value.Should().Be("%contoso%");
    }

    [Fact]
    public void Transpile_WhereIn_ProducesInOperator()
    {
        // Arrange & Act
        var fetchXml = Transpile("SELECT name FROM account WHERE statecode IN (0, 1, 2)");
        var doc = ParseFetchXml(fetchXml);

        // Assert
        var condition = doc.Descendants("condition").First();
        condition.Attribute("operator")!.Value.Should().Be("in");
        condition.Elements("value").Should().HaveCount(3);
    }

    #endregion

    #region ORDER BY

    [Fact]
    public void Transpile_OrderByAsc_ProducesOrderElement()
    {
        // Arrange & Act
        var fetchXml = Transpile("SELECT name FROM account ORDER BY name ASC");
        var doc = ParseFetchXml(fetchXml);

        // Assert
        var entity = doc.Root!.Element("entity");
        var order = entity!.Element("order");
        order.Should().NotBeNull();
        order!.Attribute("attribute")!.Value.Should().Be("name");
        order.Attribute("descending")!.Value.Should().Be("false");
    }

    [Fact]
    public void Transpile_OrderByDesc_ProducesDescendingOrder()
    {
        // Arrange & Act
        var fetchXml = Transpile("SELECT name FROM account ORDER BY revenue DESC");
        var doc = ParseFetchXml(fetchXml);

        // Assert
        var order = doc.Descendants("order").First();
        order.Attribute("descending")!.Value.Should().Be("true");
    }

    [Fact]
    public void Transpile_OrderByMultiple_ProducesMultipleOrders()
    {
        // Arrange & Act
        var fetchXml = Transpile("SELECT name FROM account ORDER BY statecode ASC, name DESC");
        var doc = ParseFetchXml(fetchXml);

        // Assert
        var orders = doc.Descendants("order").ToList();
        orders.Should().HaveCount(2);
    }

    #endregion

    #region JOIN

    [Fact]
    public void Transpile_InnerJoin_ProducesLinkEntity()
    {
        // Arrange & Act
        var fetchXml = Transpile(@"
            SELECT a.name
            FROM account a
            INNER JOIN contact c ON a.primarycontactid = c.contactid");
        var doc = ParseFetchXml(fetchXml);

        // Assert
        var entity = doc.Root!.Element("entity");
        var linkEntity = entity!.Element("link-entity");
        linkEntity.Should().NotBeNull();
        linkEntity!.Attribute("name")!.Value.Should().Be("contact");
        linkEntity.Attribute("alias")!.Value.Should().Be("c");
        linkEntity.Attribute("link-type")!.Value.Should().Be("inner");
    }

    [Fact]
    public void Transpile_LeftJoin_ProducesOuterLinkEntity()
    {
        // Arrange & Act
        var fetchXml = Transpile(@"
            SELECT a.name
            FROM account a
            LEFT JOIN contact c ON a.primarycontactid = c.contactid");
        var doc = ParseFetchXml(fetchXml);

        // Assert
        var linkEntity = doc.Descendants("link-entity").First();
        linkEntity.Attribute("link-type")!.Value.Should().Be("outer");
    }

    [Fact]
    public void Transpile_JoinWithFromTo_SetsAttributes()
    {
        // Arrange & Act
        var fetchXml = Transpile(@"
            SELECT a.name
            FROM account a
            INNER JOIN contact c ON a.primarycontactid = c.contactid");
        var doc = ParseFetchXml(fetchXml);

        // Assert
        var linkEntity = doc.Descendants("link-entity").First();
        linkEntity.Attribute("from")!.Value.Should().Be("contactid");
        linkEntity.Attribute("to")!.Value.Should().Be("primarycontactid");
    }

    #endregion

    #region Aggregates

    [Fact]
    public void Transpile_CountStar_ProducesAggregateQuery()
    {
        // Arrange & Act
        var fetchXml = Transpile("SELECT COUNT(*) FROM account");
        var doc = ParseFetchXml(fetchXml);

        // Assert
        doc.Root!.Attribute("aggregate")!.Value.Should().Be("true");
        var attr = doc.Descendants("attribute").First();
        attr.Attribute("aggregate")!.Value.Should().Be("count");
    }

    [Fact]
    public void Transpile_CountColumn_ProducesCountAggregate()
    {
        // Arrange & Act
        var fetchXml = Transpile("SELECT COUNT(accountid) AS cnt FROM account");
        var doc = ParseFetchXml(fetchXml);

        // Assert
        var attr = doc.Descendants("attribute").First();
        attr.Attribute("name")!.Value.Should().Be("accountid");
        attr.Attribute("aggregate")!.Value.Should().Be("countcolumn"); // FetchXML uses countcolumn for COUNT(column)
        attr.Attribute("alias")!.Value.Should().Be("cnt");
    }

    [Fact]
    public void Transpile_CountDistinct_ProducesDistinctAggregate()
    {
        // Arrange & Act
        var fetchXml = Transpile("SELECT COUNT(DISTINCT ownerid) FROM account");
        var doc = ParseFetchXml(fetchXml);

        // Assert
        var attr = doc.Descendants("attribute").First();
        attr.Attribute("distinct")!.Value.Should().Be("true");
    }

    [Theory]
    [InlineData("SUM(revenue)", "sum")]
    [InlineData("AVG(revenue)", "avg")]
    [InlineData("MIN(revenue)", "min")]
    [InlineData("MAX(revenue)", "max")]
    public void Transpile_AggregateFunction_ProducesCorrectAggregate(string sqlAggregate, string expectedAggregate)
    {
        // Arrange & Act
        var fetchXml = Transpile($"SELECT {sqlAggregate} FROM account");
        var doc = ParseFetchXml(fetchXml);

        // Assert
        var attr = doc.Descendants("attribute").First();
        attr.Attribute("aggregate")!.Value.Should().Be(expectedAggregate);
    }

    #endregion

    #region GROUP BY

    [Fact]
    public void Transpile_GroupBy_ProducesGroupByAttribute()
    {
        // Arrange & Act
        var fetchXml = Transpile("SELECT statecode, COUNT(*) FROM account GROUP BY statecode");
        var doc = ParseFetchXml(fetchXml);

        // Assert
        var attrs = doc.Descendants("attribute").ToList();
        var groupByAttr = attrs.FirstOrDefault(a => a.Attribute("groupby") != null);
        groupByAttr.Should().NotBeNull();
        groupByAttr!.Attribute("groupby")!.Value.Should().Be("true");
    }

    #endregion

    #region Complex Queries

    [Fact]
    public void Transpile_ComplexQuery_ProducesValidFetchXml()
    {
        // Arrange
        var sql = @"
            SELECT TOP 50
                a.name,
                a.revenue,
                c.fullname
            FROM account a
            INNER JOIN contact c ON a.primarycontactid = c.contactid
            WHERE a.statecode = 0
              AND a.revenue > 1000000
            ORDER BY a.revenue DESC";

        // Act
        var fetchXml = Transpile(sql);
        var doc = ParseFetchXml(fetchXml);

        // Assert - should parse without errors
        doc.Should().NotBeNull();
        doc.Root!.Attribute("top")!.Value.Should().Be("50");
        doc.Descendants("link-entity").Should().HaveCount(1);
        doc.Descendants("filter").Should().NotBeEmpty();
        doc.Descendants("order").Should().NotBeEmpty();
    }

    [Fact]
    public void Transpile_AggregateWithGroupBy_ProducesValidFetchXml()
    {
        // Arrange
        var sql = @"
            SELECT statecode, COUNT(*) AS cnt, SUM(revenue) AS total
            FROM account
            GROUP BY statecode
            ORDER BY cnt DESC";

        // Act
        var fetchXml = Transpile(sql);
        var doc = ParseFetchXml(fetchXml);

        // Assert
        doc.Root!.Attribute("aggregate")!.Value.Should().Be("true");
        var attrs = doc.Descendants("attribute").ToList();
        attrs.Should().HaveCount(3);
    }

    #endregion

    #region XML Safety

    [Fact]
    public void Transpile_SpecialCharactersInValues_AreXmlEscaped()
    {
        // Arrange & Act
        var fetchXml = Transpile("SELECT name FROM account WHERE name = '<script>alert(1)</script>'");

        // Assert - should produce valid XML
        var doc = ParseFetchXml(fetchXml);
        doc.Should().NotBeNull();
    }

    [Fact]
    public void Transpile_UnicodeValues_AreHandled()
    {
        // Arrange & Act
        var fetchXml = Transpile("SELECT name FROM account WHERE name = '日本語'");

        // Assert
        var doc = ParseFetchXml(fetchXml);
        var condition = doc.Descendants("condition").First();
        condition.Attribute("value")!.Value.Should().Be("日本語");
    }

    #endregion

    #region Virtual Column Support

    private TranspileResult TranspileWithVirtualColumns(string sql)
    {
        var parser = new SqlParser(sql);
        var ast = parser.Parse();
        return _transpiler.TranspileWithVirtualColumns(ast);
    }

    [Fact]
    public void TranspileWithVirtualColumns_VirtualLookupNameColumn_TranspilesBaseColumn()
    {
        // Arrange & Act
        var result = TranspileWithVirtualColumns("SELECT owneridname FROM account");

        // Assert - FetchXML should have ownerid, not owneridname
        var doc = ParseFetchXml(result.FetchXml);
        var entity = doc.Root!.Element("entity");
        var attributes = entity!.Elements("attribute").ToList();
        attributes.Should().HaveCount(1);
        attributes[0].Attribute("name")!.Value.Should().Be("ownerid");
    }

    [Fact]
    public void TranspileWithVirtualColumns_VirtualLookupNameColumn_DetectsVirtualColumn()
    {
        // Arrange & Act
        var result = TranspileWithVirtualColumns("SELECT owneridname FROM account");

        // Assert
        result.VirtualColumns.Should().HaveCount(1);
        result.VirtualColumns.Should().ContainKey("owneridname");
        result.VirtualColumns["owneridname"].BaseColumnName.Should().Be("ownerid");
        result.VirtualColumns["owneridname"].BaseColumnExplicitlyQueried.Should().BeFalse();
    }

    [Fact]
    public void TranspileWithVirtualColumns_BothBaseAndVirtualColumn_DetectsBothQueried()
    {
        // Arrange & Act
        var result = TranspileWithVirtualColumns("SELECT ownerid, owneridname FROM account");

        // Assert
        result.VirtualColumns.Should().HaveCount(1);
        result.VirtualColumns["owneridname"].BaseColumnExplicitlyQueried.Should().BeTrue();

        // FetchXML should have only one ownerid attribute (no duplicate)
        var doc = ParseFetchXml(result.FetchXml);
        var entity = doc.Root!.Element("entity");
        var attributes = entity!.Elements("attribute").ToList();
        attributes.Should().HaveCount(1);
        attributes[0].Attribute("name")!.Value.Should().Be("ownerid");
    }

    [Fact]
    public void TranspileWithVirtualColumns_VirtualStatusCodeName_TranspilesBaseColumn()
    {
        // Arrange & Act
        var result = TranspileWithVirtualColumns("SELECT statuscodename FROM account");

        // Assert
        var doc = ParseFetchXml(result.FetchXml);
        var entity = doc.Root!.Element("entity");
        var attr = entity!.Element("attribute");
        attr!.Attribute("name")!.Value.Should().Be("statuscode");

        result.VirtualColumns.Should().ContainKey("statuscodename");
        result.VirtualColumns["statuscodename"].BaseColumnName.Should().Be("statuscode");
    }

    [Fact]
    public void TranspileWithVirtualColumns_VirtualStateCodeName_TranspilesBaseColumn()
    {
        // Arrange & Act
        var result = TranspileWithVirtualColumns("SELECT statecodename FROM account");

        // Assert
        result.VirtualColumns.Should().ContainKey("statecodename");
        result.VirtualColumns["statecodename"].BaseColumnName.Should().Be("statecode");
    }

    [Fact]
    public void TranspileWithVirtualColumns_VirtualBooleanName_TranspilesBaseColumn()
    {
        // Arrange & Act
        var result = TranspileWithVirtualColumns("SELECT ismanagedname FROM solution");

        // Assert
        var doc = ParseFetchXml(result.FetchXml);
        var entity = doc.Root!.Element("entity");
        var attr = entity!.Element("attribute");
        attr!.Attribute("name")!.Value.Should().Be("ismanaged");

        result.VirtualColumns.Should().ContainKey("ismanagedname");
        result.VirtualColumns["ismanagedname"].BaseColumnName.Should().Be("ismanaged");
    }

    [Fact]
    public void TranspileWithVirtualColumns_RegularNameColumn_NotDetectedAsVirtual()
    {
        // "name" is a real column, not a virtual column
        // Arrange & Act
        var result = TranspileWithVirtualColumns("SELECT name FROM account");

        // Assert
        result.VirtualColumns.Should().BeEmpty();
    }

    [Fact]
    public void TranspileWithVirtualColumns_MixedColumns_HandlesCorrectly()
    {
        // Arrange & Act
        var result = TranspileWithVirtualColumns("SELECT name, owneridname, statuscode FROM account");

        // Assert
        result.VirtualColumns.Should().HaveCount(1);
        result.VirtualColumns.Should().ContainKey("owneridname");

        // FetchXML should have: name, ownerid (from owneridname), statuscode
        var doc = ParseFetchXml(result.FetchXml);
        var entity = doc.Root!.Element("entity");
        var attributes = entity!.Elements("attribute").Select(a => a.Attribute("name")!.Value).ToList();
        attributes.Should().Contain("name");
        attributes.Should().Contain("ownerid");
        attributes.Should().Contain("statuscode");
        attributes.Should().NotContain("owneridname");
    }

    #endregion
}
