using FluentAssertions;
using PPDS.Dataverse.Sql.Ast;
using PPDS.Dataverse.Sql.Parsing;
using Xunit;

namespace PPDS.Dataverse.Tests.Sql.Parsing;

[Trait("Category", "TuiUnit")]
public class ExpressionConditionTests
{
    [Fact]
    public void Parse_ColumnToColumnComparison()
    {
        // Arrange
        var sql = "SELECT name FROM account WHERE revenue > cost";

        // Act
        var result = SqlParser.Parse(sql);

        // Assert
        result.Where.Should().NotBeNull();
        var exprCond = (SqlExpressionCondition)result.Where!;

        var left = (SqlColumnExpression)exprCond.Left;
        left.Column.ColumnName.Should().Be("revenue");

        exprCond.Operator.Should().Be(SqlComparisonOperator.GreaterThan);

        var right = (SqlColumnExpression)exprCond.Right;
        right.Column.ColumnName.Should().Be("cost");
    }

    [Fact]
    public void Parse_ColumnToLiteral_StaysAsComparisonCondition()
    {
        // Arrange: column op literal should produce backward-compatible SqlComparisonCondition
        var sql = "SELECT name FROM account WHERE revenue > 1000";

        // Act
        var result = SqlParser.Parse(sql);

        // Assert
        result.Where.Should().NotBeNull();
        var comp = result.Where.Should().BeOfType<SqlComparisonCondition>(
            "column op literal should produce SqlComparisonCondition for FetchXML pushdown").Subject;

        comp.Column.ColumnName.Should().Be("revenue");
        comp.Operator.Should().Be(SqlComparisonOperator.GreaterThan);
        comp.Value.Value.Should().Be("1000");
    }

    [Fact]
    public void Parse_ColumnToColumnEquals()
    {
        // Arrange
        var sql = "SELECT name FROM account WHERE field1 = field2";

        // Act
        var result = SqlParser.Parse(sql);

        // Assert
        var exprCond = (SqlExpressionCondition)result.Where!;
        exprCond.Operator.Should().Be(SqlComparisonOperator.Equal);
    }

    [Fact]
    public void Parse_QualifiedColumnToColumn()
    {
        // Arrange
        var sql = "SELECT name FROM account WHERE a.revenue > a.cost";

        // Act
        var result = SqlParser.Parse(sql);

        // Assert: left side is parsed as SqlColumnRef (from ParseColumnRef), then
        // wrapped in SqlColumnExpression (from ParsePrimaryCondition)
        var exprCond = (SqlExpressionCondition)result.Where!;

        var left = (SqlColumnExpression)exprCond.Left;
        left.Column.TableName.Should().Be("a");
        left.Column.ColumnName.Should().Be("revenue");

        var right = (SqlColumnExpression)exprCond.Right;
        right.Column.TableName.Should().Be("a");
        right.Column.ColumnName.Should().Be("cost");
    }

    [Fact]
    public void Parse_ArithmeticOnLeftSideOfWhere_NotYetSupported()
    {
        // Phase 1 limitation: ParsePrimaryCondition parses left side as column ref,
        // so arithmetic on the LEFT side (WHERE revenue * 0.1 > 100) is not supported.
        // The parser sees 'revenue' as column, then '*' is not a comparison operator.
        // Right-side arithmetic is supported (WHERE revenue > cost * 2).
        var sql = "SELECT name FROM account WHERE revenue * 0.1 > 100";

        var ex = Assert.Throws<SqlParseException>(() => SqlParser.Parse(sql));
        ex.Message.Should().Contain("comparison operator");
    }

    [Fact]
    public void Parse_ColumnComparedToArithmeticExpression()
    {
        // Arrange: column op (arithmetic expression)
        var sql = "SELECT name FROM account WHERE revenue > cost * 2";

        // Act
        var result = SqlParser.Parse(sql);

        // Assert: right side is an arithmetic expression
        var exprCond = (SqlExpressionCondition)result.Where!;

        var left = (SqlColumnExpression)exprCond.Left;
        left.Column.ColumnName.Should().Be("revenue");

        exprCond.Operator.Should().Be(SqlComparisonOperator.GreaterThan);

        var right = (SqlBinaryExpression)exprCond.Right;
        right.Operator.Should().Be(SqlBinaryOperator.Multiply);

        var rightLeft = (SqlColumnExpression)right.Left;
        rightLeft.Column.ColumnName.Should().Be("cost");

        var rightRight = (SqlLiteralExpression)right.Right;
        rightRight.Value.Value.Should().Be("2");
    }

    [Fact]
    public void Parse_MixedConditions_AndWithExpressionAndLiteral()
    {
        // Arrange: mixed AND - one pushable, one client-side
        var sql = "SELECT name FROM account WHERE status = 1 AND revenue > cost";

        // Act
        var result = SqlParser.Parse(sql);

        // Assert
        var logical = (SqlLogicalCondition)result.Where!;
        logical.Operator.Should().Be(SqlLogicalOperator.And);
        logical.Conditions.Should().HaveCount(2);

        // First condition: pushable SqlComparisonCondition
        var first = (SqlComparisonCondition)logical.Conditions[0];
        first.Column.ColumnName.Should().Be("status");

        // Second condition: client-side SqlExpressionCondition
        var second = (SqlExpressionCondition)logical.Conditions[1];
        second.Operator.Should().Be(SqlComparisonOperator.GreaterThan);
    }

    [Fact]
    public void Parse_ColumnToStringLiteral_StaysAsComparisonCondition()
    {
        // Arrange: string literal should remain SqlComparisonCondition
        var sql = "SELECT name FROM account WHERE name = 'Contoso'";

        // Act
        var result = SqlParser.Parse(sql);

        // Assert
        var comp = (SqlComparisonCondition)result.Where!;
        comp.Column.ColumnName.Should().Be("name");
        comp.Value.Type.Should().Be(SqlLiteralType.String);
    }

    [Fact]
    public void Parse_ColumnToNull_StaysAsComparisonCondition()
    {
        // Arrange: NULL literal should remain SqlComparisonCondition
        var sql = "SELECT name FROM account WHERE revenue = NULL";

        // Act
        var result = SqlParser.Parse(sql);

        // Assert: NULL as literal in comparison stays as SqlComparisonCondition
        var comp = (SqlComparisonCondition)result.Where!;
        comp.Value.Type.Should().Be(SqlLiteralType.Null);
    }
}
