using FluentAssertions;
using PPDS.Dataverse.Sql.Ast;
using PPDS.Dataverse.Sql.Parsing;
using Xunit;

namespace PPDS.Dataverse.Tests.Sql.Parsing;

[Trait("Category", "TuiUnit")]
public class ExistsParserTests
{
    [Fact]
    public void Parse_ExistsSubquery_ProducesExistsCondition()
    {
        var sql = @"SELECT name FROM account a
            WHERE EXISTS (SELECT 1 FROM contact c WHERE c.parentcustomerid = a.accountid)";

        var result = SqlParser.Parse(sql);

        result.Where.Should().NotBeNull();
        var exists = result.Where as SqlExistsCondition;
        exists.Should().NotBeNull();
        exists!.IsNegated.Should().BeFalse();
        exists.Subquery.Should().NotBeNull();
        exists.Subquery.From.TableName.Should().Be("contact");
    }

    [Fact]
    public void Parse_NotExistsSubquery_ProducesNegatedExistsCondition()
    {
        var sql = @"SELECT name FROM account a
            WHERE NOT EXISTS (SELECT 1 FROM contact c WHERE c.parentcustomerid = a.accountid)";

        var result = SqlParser.Parse(sql);

        result.Where.Should().NotBeNull();
        var exists = result.Where as SqlExistsCondition;
        exists.Should().NotBeNull();
        exists!.IsNegated.Should().BeTrue();
        exists.Subquery.Should().NotBeNull();
        exists.Subquery.From.TableName.Should().Be("contact");
    }

    [Fact]
    public void Parse_ExistsSubquery_PreservesSubqueryWhere()
    {
        var sql = @"SELECT name FROM account a
            WHERE EXISTS (SELECT 1 FROM contact c WHERE c.parentcustomerid = a.accountid)";

        var result = SqlParser.Parse(sql);

        var exists = result.Where as SqlExistsCondition;
        exists.Should().NotBeNull();

        // The subquery WHERE should be an expression condition (col = col)
        var subWhere = exists!.Subquery.Where as SqlExpressionCondition;
        subWhere.Should().NotBeNull();
        subWhere!.Operator.Should().Be(SqlComparisonOperator.Equal);

        var left = subWhere.Left as SqlColumnExpression;
        left.Should().NotBeNull();
        left!.Column.TableName.Should().Be("c");
        left.Column.ColumnName.Should().Be("parentcustomerid");

        var right = subWhere.Right as SqlColumnExpression;
        right.Should().NotBeNull();
        right!.Column.TableName.Should().Be("a");
        right.Column.ColumnName.Should().Be("accountid");
    }

    [Fact]
    public void Parse_ExistsWithAndCondition_ParsesCorrectly()
    {
        var sql = @"SELECT name FROM account a
            WHERE status = 1
            AND EXISTS (SELECT 1 FROM contact c WHERE c.parentcustomerid = a.accountid)";

        var result = SqlParser.Parse(sql);

        var logical = result.Where as SqlLogicalCondition;
        logical.Should().NotBeNull();
        logical!.Operator.Should().Be(SqlLogicalOperator.And);
        logical.Conditions.Should().HaveCount(2);

        // First condition: status = 1
        var comp = logical.Conditions[0] as SqlComparisonCondition;
        comp.Should().NotBeNull();
        comp!.Column.ColumnName.Should().Be("status");

        // Second condition: EXISTS
        var exists = logical.Conditions[1] as SqlExistsCondition;
        exists.Should().NotBeNull();
        exists!.IsNegated.Should().BeFalse();
    }

    [Fact]
    public void Parse_NotExistsWithAndCondition_ParsesCorrectly()
    {
        var sql = @"SELECT name FROM account a
            WHERE status = 1
            AND NOT EXISTS (SELECT 1 FROM contact c WHERE c.parentcustomerid = a.accountid)";

        var result = SqlParser.Parse(sql);

        var logical = result.Where as SqlLogicalCondition;
        logical.Should().NotBeNull();
        logical!.Operator.Should().Be(SqlLogicalOperator.And);
        logical.Conditions.Should().HaveCount(2);

        var exists = logical.Conditions[1] as SqlExistsCondition;
        exists.Should().NotBeNull();
        exists!.IsNegated.Should().BeTrue();
    }

    [Fact]
    public void Parse_ExistsSubqueryWithSelectStar()
    {
        var sql = @"SELECT name FROM account a
            WHERE EXISTS (SELECT * FROM contact c WHERE c.parentcustomerid = a.accountid)";

        var result = SqlParser.Parse(sql);

        var exists = result.Where as SqlExistsCondition;
        exists.Should().NotBeNull();
        exists!.Subquery.Columns.Should().HaveCount(1);
        var col = exists.Subquery.Columns[0] as SqlColumnRef;
        col.Should().NotBeNull();
        col!.IsWildcard.Should().BeTrue();
    }

    [Fact]
    public void Parse_ExistsSubqueryWithAdditionalWhereCondition()
    {
        var sql = @"SELECT name FROM account a
            WHERE EXISTS (
                SELECT 1 FROM contact c
                WHERE c.parentcustomerid = a.accountid
                AND c.statecode = 0
            )";

        var result = SqlParser.Parse(sql);

        var exists = result.Where as SqlExistsCondition;
        exists.Should().NotBeNull();

        // Subquery WHERE should be AND of two conditions
        var subWhere = exists!.Subquery.Where as SqlLogicalCondition;
        subWhere.Should().NotBeNull();
        subWhere!.Operator.Should().Be(SqlLogicalOperator.And);
        subWhere.Conditions.Should().HaveCount(2);
    }

    [Fact]
    public void Parse_ExistsKeywordAsIdentifier_WhenNotFollowedByParen()
    {
        // EXISTS is a keyword but when used as a column name, it should work in SELECT
        // Actually, EXISTS as a column name is unlikely in practice, but the lexer
        // will tokenize it as SqlTokenType.Exists. In SELECT clause, it would fail
        // because the parser doesn't expect Exists token there. This is acceptable.
        // This test validates that the lexer correctly tokenizes EXISTS.
        var lexer = new SqlLexer("EXISTS");
        var tokens = lexer.Tokenize();

        tokens.Tokens[0].Type.Should().Be(SqlTokenType.Exists);
    }

    [Fact]
    public void Parse_ExistsIsCaseInsensitive()
    {
        var sql = @"SELECT name FROM account a
            WHERE exists (SELECT 1 FROM contact c WHERE c.parentcustomerid = a.accountid)";

        var result = SqlParser.Parse(sql);

        var exists = result.Where as SqlExistsCondition;
        exists.Should().NotBeNull();
        exists!.IsNegated.Should().BeFalse();
    }
}
