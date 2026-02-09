using PPDS.Dataverse.Sql.Ast;
using PPDS.Dataverse.Sql.Parsing;
using Xunit;

namespace PPDS.Dataverse.Tests.Sql.Parsing;

[Trait("Category", "TuiUnit")]
public class BetweenParserTests
{
    [Fact]
    public void ParsesBetween_NumberLiterals()
    {
        var sql = "SELECT name FROM account WHERE revenue BETWEEN 100 AND 1000";
        var statement = SqlParser.Parse(sql);

        // BETWEEN desugars to: revenue >= 100 AND revenue <= 1000
        var logical = Assert.IsType<SqlLogicalCondition>(statement.Where);
        Assert.Equal(SqlLogicalOperator.And, logical.Operator);
        Assert.Equal(2, logical.Conditions.Count);

        var ge = Assert.IsType<SqlComparisonCondition>(logical.Conditions[0]);
        Assert.Equal("revenue", ge.Column.ColumnName);
        Assert.Equal(SqlComparisonOperator.GreaterThanOrEqual, ge.Operator);
        Assert.Equal("100", ge.Value.Value);

        var le = Assert.IsType<SqlComparisonCondition>(logical.Conditions[1]);
        Assert.Equal("revenue", le.Column.ColumnName);
        Assert.Equal(SqlComparisonOperator.LessThanOrEqual, le.Operator);
        Assert.Equal("1000", le.Value.Value);
    }

    [Fact]
    public void ParsesBetween_StringLiterals()
    {
        var sql = "SELECT name FROM account WHERE name BETWEEN 'A' AND 'M'";
        var statement = SqlParser.Parse(sql);

        var logical = Assert.IsType<SqlLogicalCondition>(statement.Where);
        Assert.Equal(SqlLogicalOperator.And, logical.Operator);

        var ge = Assert.IsType<SqlComparisonCondition>(logical.Conditions[0]);
        Assert.Equal("A", ge.Value.Value);

        var le = Assert.IsType<SqlComparisonCondition>(logical.Conditions[1]);
        Assert.Equal("M", le.Value.Value);
    }

    [Fact]
    public void ParsesNotBetween()
    {
        var sql = "SELECT name FROM account WHERE revenue NOT BETWEEN 100 AND 1000";
        var statement = SqlParser.Parse(sql);

        // NOT BETWEEN desugars to: revenue < 100 OR revenue > 1000
        var logical = Assert.IsType<SqlLogicalCondition>(statement.Where);
        Assert.Equal(SqlLogicalOperator.Or, logical.Operator);
        Assert.Equal(2, logical.Conditions.Count);

        var lt = Assert.IsType<SqlComparisonCondition>(logical.Conditions[0]);
        Assert.Equal("revenue", lt.Column.ColumnName);
        Assert.Equal(SqlComparisonOperator.LessThan, lt.Operator);
        Assert.Equal("100", lt.Value.Value);

        var gt = Assert.IsType<SqlComparisonCondition>(logical.Conditions[1]);
        Assert.Equal("revenue", gt.Column.ColumnName);
        Assert.Equal(SqlComparisonOperator.GreaterThan, gt.Operator);
        Assert.Equal("1000", gt.Value.Value);
    }

    [Fact]
    public void ParsesBetween_WithOtherConditions()
    {
        var sql = "SELECT name FROM account WHERE statecode = 0 AND revenue BETWEEN 100 AND 1000";
        var statement = SqlParser.Parse(sql);

        // Should be AND of: statecode = 0 AND (revenue >= 100 AND revenue <= 1000)
        var logical = Assert.IsType<SqlLogicalCondition>(statement.Where);
        Assert.Equal(SqlLogicalOperator.And, logical.Operator);
    }

    [Fact]
    public void ParsesBetween_CaseInsensitive()
    {
        var sql = "SELECT name FROM account WHERE revenue between 100 and 1000";
        var statement = SqlParser.Parse(sql);

        var logical = Assert.IsType<SqlLogicalCondition>(statement.Where);
        Assert.Equal(SqlLogicalOperator.And, logical.Operator);
    }

    [Fact]
    public void LexerTokenizesBetweenKeyword()
    {
        var lexer = new SqlLexer("BETWEEN");
        var result = lexer.Tokenize();

        Assert.Equal(SqlTokenType.Between, result.Tokens[0].Type);
    }
}
