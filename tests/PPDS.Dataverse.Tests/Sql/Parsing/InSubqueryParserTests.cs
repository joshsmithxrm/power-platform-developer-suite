using FluentAssertions;
using PPDS.Dataverse.Sql.Ast;
using PPDS.Dataverse.Sql.Parsing;
using Xunit;

namespace PPDS.Dataverse.Tests.Sql.Parsing;

[Trait("Category", "TuiUnit")]
public class InSubqueryParserTests
{
    [Fact]
    public void Parse_InSubquery_SimpleSelect()
    {
        var sql = "SELECT name FROM account WHERE accountid IN (SELECT accountid FROM opportunity)";

        var result = SqlParser.Parse(sql);

        result.Where.Should().NotBeNull();
        var inSub = result.Where as SqlInSubqueryCondition;
        inSub.Should().NotBeNull();
        inSub!.Column.ColumnName.Should().Be("accountid");
        inSub.IsNegated.Should().BeFalse();
        inSub.Subquery.Should().NotBeNull();
        inSub.Subquery.GetEntityName().Should().Be("opportunity");
        inSub.Subquery.Columns.Should().HaveCount(1);
        var subCol = inSub.Subquery.Columns[0] as SqlColumnRef;
        subCol.Should().NotBeNull();
        subCol!.ColumnName.Should().Be("accountid");
    }

    [Fact]
    public void Parse_NotInSubquery()
    {
        var sql = "SELECT name FROM account WHERE accountid NOT IN (SELECT accountid FROM opportunity)";

        var result = SqlParser.Parse(sql);

        var inSub = result.Where as SqlInSubqueryCondition;
        inSub.Should().NotBeNull();
        inSub!.IsNegated.Should().BeTrue();
        inSub.Column.ColumnName.Should().Be("accountid");
        inSub.Subquery.GetEntityName().Should().Be("opportunity");
    }

    [Fact]
    public void Parse_InSubqueryWithWhere()
    {
        var sql = "SELECT name FROM account WHERE accountid IN (SELECT accountid FROM opportunity WHERE statecode = 0)";

        var result = SqlParser.Parse(sql);

        var inSub = result.Where as SqlInSubqueryCondition;
        inSub.Should().NotBeNull();
        inSub!.Subquery.Where.Should().NotBeNull();
        var subWhere = inSub.Subquery.Where as SqlComparisonCondition;
        subWhere.Should().NotBeNull();
        subWhere!.Column.ColumnName.Should().Be("statecode");
        subWhere.Value.Value.Should().Be("0");
    }

    [Fact]
    public void Parse_InSubqueryWithQualifiedColumn()
    {
        var sql = "SELECT a.name FROM account a WHERE a.accountid IN (SELECT o.accountid FROM opportunity o)";

        var result = SqlParser.Parse(sql);

        var inSub = result.Where as SqlInSubqueryCondition;
        inSub.Should().NotBeNull();
        inSub!.Column.TableName.Should().Be("a");
        inSub.Column.ColumnName.Should().Be("accountid");
        var subCol = inSub.Subquery.Columns[0] as SqlColumnRef;
        subCol.Should().NotBeNull();
        subCol!.TableName.Should().Be("o");
        subCol.ColumnName.Should().Be("accountid");
    }

    [Fact]
    public void Parse_InSubqueryWithAndCondition()
    {
        var sql = "SELECT name FROM account WHERE statecode = 0 AND accountid IN (SELECT accountid FROM opportunity)";

        var result = SqlParser.Parse(sql);

        var logical = result.Where as SqlLogicalCondition;
        logical.Should().NotBeNull();
        logical!.Operator.Should().Be(SqlLogicalOperator.And);
        logical.Conditions.Should().HaveCount(2);

        var comp = logical.Conditions[0] as SqlComparisonCondition;
        comp.Should().NotBeNull();
        comp!.Column.ColumnName.Should().Be("statecode");

        var inSub = logical.Conditions[1] as SqlInSubqueryCondition;
        inSub.Should().NotBeNull();
        inSub!.Subquery.GetEntityName().Should().Be("opportunity");
    }

    [Fact]
    public void Parse_InLiteralList_StillWorks()
    {
        // Verify that regular IN (value list) still works
        var sql = "SELECT name FROM account WHERE statecode IN (0, 1, 2)";

        var result = SqlParser.Parse(sql);

        var inCond = result.Where as SqlInCondition;
        inCond.Should().NotBeNull();
        inCond!.Values.Should().HaveCount(3);
        inCond.Column.ColumnName.Should().Be("statecode");
    }

    [Fact]
    public void Parse_InSubqueryWithDistinct()
    {
        var sql = "SELECT name FROM account WHERE accountid IN (SELECT DISTINCT accountid FROM opportunity)";

        var result = SqlParser.Parse(sql);

        var inSub = result.Where as SqlInSubqueryCondition;
        inSub.Should().NotBeNull();
        inSub!.Subquery.Distinct.Should().BeTrue();
    }

    [Fact]
    public void Parse_InSubqueryWithTop()
    {
        var sql = "SELECT name FROM account WHERE accountid IN (SELECT TOP 10 accountid FROM opportunity)";

        var result = SqlParser.Parse(sql);

        var inSub = result.Where as SqlInSubqueryCondition;
        inSub.Should().NotBeNull();
        inSub!.Subquery.Top.Should().Be(10);
    }

    [Fact]
    public void Parse_InSubqueryWithSubqueryWhere_MultipleConditions()
    {
        var sql = "SELECT name FROM account WHERE accountid IN (SELECT accountid FROM opportunity WHERE statecode = 0 AND revenue > 1000)";

        var result = SqlParser.Parse(sql);

        var inSub = result.Where as SqlInSubqueryCondition;
        inSub.Should().NotBeNull();
        var subWhere = inSub!.Subquery.Where as SqlLogicalCondition;
        subWhere.Should().NotBeNull();
        subWhere!.Operator.Should().Be(SqlLogicalOperator.And);
        subWhere.Conditions.Should().HaveCount(2);
    }
}
