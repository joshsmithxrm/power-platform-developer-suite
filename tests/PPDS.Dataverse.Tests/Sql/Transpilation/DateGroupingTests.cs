using System.Linq;
using System.Xml.Linq;
using FluentAssertions;
using PPDS.Dataverse.Sql.Ast;
using PPDS.Dataverse.Sql.Parsing;
using PPDS.Dataverse.Sql.Transpilation;
using Xunit;

namespace PPDS.Dataverse.Tests.Sql.Transpilation;

[Trait("Category", "TuiUnit")]
public class DateGroupingTests
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

    #region Parser: Function calls in expressions

    [Fact]
    public void Parser_FunctionCallInSelect_ProducesComputedColumn()
    {
        var ast = SqlParser.Parse("SELECT YEAR(createdon) AS yr FROM account");
        ast.Columns.Should().HaveCount(1);
        ast.Columns[0].Should().BeOfType<SqlComputedColumn>();

        var computed = (SqlComputedColumn)ast.Columns[0];
        computed.Alias.Should().Be("yr");
        computed.Expression.Should().BeOfType<SqlFunctionExpression>();

        var func = (SqlFunctionExpression)computed.Expression;
        func.FunctionName.Should().Be("YEAR");
        func.Arguments.Should().HaveCount(1);
        func.Arguments[0].Should().BeOfType<SqlColumnExpression>();
    }

    [Fact]
    public void Parser_ZeroArgFunction_ParsesCorrectly()
    {
        var ast = SqlParser.Parse("SELECT GETDATE() AS now FROM account");
        ast.Columns.Should().HaveCount(1);
        ast.Columns[0].Should().BeOfType<SqlComputedColumn>();

        var computed = (SqlComputedColumn)ast.Columns[0];
        var func = (SqlFunctionExpression)computed.Expression;
        func.FunctionName.Should().Be("GETDATE");
        func.Arguments.Should().BeEmpty();
    }

    [Fact]
    public void Parser_MultiArgFunction_ParsesCorrectly()
    {
        var ast = SqlParser.Parse("SELECT DATEADD(year, 1, createdon) AS nextyr FROM account");
        ast.Columns.Should().HaveCount(1);
        ast.Columns[0].Should().BeOfType<SqlComputedColumn>();

        var computed = (SqlComputedColumn)ast.Columns[0];
        var func = (SqlFunctionExpression)computed.Expression;
        func.FunctionName.Should().Be("DATEADD");
        func.Arguments.Should().HaveCount(3);

        // First arg is the datepart: parsed as string literal "year"
        func.Arguments[0].Should().BeOfType<SqlLiteralExpression>();
        var datepartArg = (SqlLiteralExpression)func.Arguments[0];
        datepartArg.Value.Type.Should().Be(SqlLiteralType.String);
        datepartArg.Value.Value.Should().Be("year");
    }

    #endregion

    #region Parser: GROUP BY with function expressions

    [Fact]
    public void Parser_GroupByYearFunction_StoresAsGroupByExpression()
    {
        var ast = SqlParser.Parse(
            "SELECT YEAR(createdon) AS yr, COUNT(*) AS cnt FROM account GROUP BY YEAR(createdon)");

        ast.GroupBy.Should().BeEmpty("Function GROUP BY items should not appear in GroupBy");
        ast.GroupByExpressions.Should().HaveCount(1);

        var expr = ast.GroupByExpressions[0].Should().BeOfType<SqlFunctionExpression>().Subject;
        expr.FunctionName.Should().Be("YEAR");
        expr.Arguments.Should().HaveCount(1);
    }

    [Fact]
    public void Parser_GroupByMixedColumnsAndFunctions()
    {
        var ast = SqlParser.Parse(
            "SELECT name, YEAR(createdon) AS yr, COUNT(*) AS cnt FROM account GROUP BY name, YEAR(createdon)");

        ast.GroupBy.Should().HaveCount(1);
        ast.GroupBy[0].ColumnName.Should().Be("name");

        ast.GroupByExpressions.Should().HaveCount(1);
        var expr = ast.GroupByExpressions[0].Should().BeOfType<SqlFunctionExpression>().Subject;
        expr.FunctionName.Should().Be("YEAR");
    }

    [Fact]
    public void Parser_GroupByMultipleDateFunctions()
    {
        var ast = SqlParser.Parse(
            "SELECT YEAR(createdon) AS yr, MONTH(createdon) AS mo, COUNT(*) AS cnt " +
            "FROM account GROUP BY YEAR(createdon), MONTH(createdon)");

        ast.GroupBy.Should().BeEmpty();
        ast.GroupByExpressions.Should().HaveCount(2);

        var yearFunc = (SqlFunctionExpression)ast.GroupByExpressions[0];
        yearFunc.FunctionName.Should().Be("YEAR");

        var monthFunc = (SqlFunctionExpression)ast.GroupByExpressions[1];
        monthFunc.FunctionName.Should().Be("MONTH");
    }

    #endregion

    #region Transpiler: dategrouping FetchXML

    [Fact]
    public void Transpile_GroupByYear_EmitsDateGroupingAttribute()
    {
        var fetchXml = Transpile(
            "SELECT YEAR(createdon) AS yr, COUNT(*) AS cnt FROM account GROUP BY YEAR(createdon)");

        var doc = ParseFetchXml(fetchXml);
        doc.Root!.Attribute("aggregate")!.Value.Should().Be("true");

        var entity = doc.Root.Element("entity")!;
        var attrs = entity.Elements("attribute").ToList();

        // Should have the dategrouping attribute
        var dateGroupAttr = attrs.FirstOrDefault(a =>
            a.Attribute("dategrouping")?.Value == "year");
        dateGroupAttr.Should().NotBeNull("Expected dategrouping=\"year\" attribute");
        dateGroupAttr!.Attribute("name")!.Value.Should().Be("createdon");
        dateGroupAttr.Attribute("groupby")!.Value.Should().Be("true");
        dateGroupAttr.Attribute("alias")!.Value.Should().Be("yr");
    }

    [Fact]
    public void Transpile_GroupByMonth_EmitsDateGroupingAttribute()
    {
        var fetchXml = Transpile(
            "SELECT MONTH(createdon) AS mo, COUNT(*) AS cnt FROM account GROUP BY MONTH(createdon)");

        var doc = ParseFetchXml(fetchXml);
        var entity = doc.Root!.Element("entity")!;
        var attrs = entity.Elements("attribute").ToList();

        var dateGroupAttr = attrs.FirstOrDefault(a =>
            a.Attribute("dategrouping")?.Value == "month");
        dateGroupAttr.Should().NotBeNull("Expected dategrouping=\"month\" attribute");
        dateGroupAttr!.Attribute("name")!.Value.Should().Be("createdon");
        dateGroupAttr.Attribute("alias")!.Value.Should().Be("mo");
    }

    [Fact]
    public void Transpile_GroupByDay_EmitsDateGroupingAttribute()
    {
        var fetchXml = Transpile(
            "SELECT DAY(createdon) AS d, COUNT(*) AS cnt FROM account GROUP BY DAY(createdon)");

        var doc = ParseFetchXml(fetchXml);
        var entity = doc.Root!.Element("entity")!;
        var attrs = entity.Elements("attribute").ToList();

        var dateGroupAttr = attrs.FirstOrDefault(a =>
            a.Attribute("dategrouping")?.Value == "day");
        dateGroupAttr.Should().NotBeNull("Expected dategrouping=\"day\" attribute");
        dateGroupAttr!.Attribute("name")!.Value.Should().Be("createdon");
    }

    [Fact]
    public void Transpile_GroupByYearAndMonth_EmitsBothDateGroupingAttributes()
    {
        var fetchXml = Transpile(
            "SELECT YEAR(createdon) AS yr, MONTH(createdon) AS mo, COUNT(*) AS cnt " +
            "FROM account GROUP BY YEAR(createdon), MONTH(createdon)");

        var doc = ParseFetchXml(fetchXml);
        var entity = doc.Root!.Element("entity")!;
        var attrs = entity.Elements("attribute").ToList();

        var yearAttr = attrs.FirstOrDefault(a =>
            a.Attribute("dategrouping")?.Value == "year");
        yearAttr.Should().NotBeNull();
        yearAttr!.Attribute("alias")!.Value.Should().Be("yr");

        var monthAttr = attrs.FirstOrDefault(a =>
            a.Attribute("dategrouping")?.Value == "month");
        monthAttr.Should().NotBeNull();
        monthAttr!.Attribute("alias")!.Value.Should().Be("mo");
    }

    [Fact]
    public void Transpile_GroupByMixedColumnAndDateFunction()
    {
        var fetchXml = Transpile(
            "SELECT name, YEAR(createdon) AS yr, COUNT(*) AS cnt " +
            "FROM account GROUP BY name, YEAR(createdon)");

        var doc = ParseFetchXml(fetchXml);
        var entity = doc.Root!.Element("entity")!;
        var attrs = entity.Elements("attribute").ToList();

        // Regular column with groupby
        var nameAttr = attrs.FirstOrDefault(a =>
            a.Attribute("name")?.Value == "name" && a.Attribute("groupby")?.Value == "true");
        nameAttr.Should().NotBeNull("Expected name attribute with groupby=\"true\"");

        // Date grouping attribute
        var yearAttr = attrs.FirstOrDefault(a =>
            a.Attribute("dategrouping")?.Value == "year");
        yearAttr.Should().NotBeNull("Expected dategrouping=\"year\" attribute");
        yearAttr!.Attribute("name")!.Value.Should().Be("createdon");
    }

    [Fact]
    public void Transpile_GroupByDateFunction_GeneratesAutoAlias_WhenNoSelectAlias()
    {
        // When there's no alias match in SELECT, the transpiler should generate a default alias
        var ast = new SqlSelectStatement(
            new ISqlSelectColumn[]
            {
                new SqlAggregateColumn(SqlAggregateFunction.Count, null, false, "cnt")
            },
            new SqlTableRef("account", null),
            groupBy: new SqlColumnRef[] { },
            groupByExpressions: new ISqlExpression[]
            {
                new SqlFunctionExpression("YEAR", new ISqlExpression[]
                {
                    new SqlColumnExpression(SqlColumnRef.Simple("createdon"))
                })
            });

        var fetchXml = _transpiler.Transpile(ast);
        var doc = ParseFetchXml(fetchXml);
        var entity = doc.Root!.Element("entity")!;
        var attrs = entity.Elements("attribute").ToList();

        var dateGroupAttr = attrs.FirstOrDefault(a =>
            a.Attribute("dategrouping")?.Value == "year");
        dateGroupAttr.Should().NotBeNull();
        dateGroupAttr!.Attribute("alias")!.Value.Should().Be("year_createdon");
    }

    #endregion

    #region End-to-end: parse + transpile

    [Fact]
    public void EndToEnd_SelectYearCountGroupByYear()
    {
        var sql = "SELECT YEAR(createdon), COUNT(*) FROM account GROUP BY YEAR(createdon)";
        var fetchXml = Transpile(sql);

        fetchXml.Should().Contain("aggregate=\"true\"");
        fetchXml.Should().Contain("dategrouping=\"year\"");
        fetchXml.Should().Contain("groupby=\"true\"");
        fetchXml.Should().Contain("name=\"createdon\"");
    }

    #endregion
}
