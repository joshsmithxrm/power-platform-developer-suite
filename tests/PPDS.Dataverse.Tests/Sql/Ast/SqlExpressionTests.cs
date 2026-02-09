using PPDS.Dataverse.Sql.Ast;
using Xunit;

namespace PPDS.Dataverse.Tests.Sql.Ast;

[Trait("Category", "TuiUnit")]
public class SqlExpressionTests
{
    [Fact]
    public void SqlLiteralExpression_WrapsLiteral()
    {
        var literal = SqlLiteral.Number("42");
        var expr = new SqlLiteralExpression(literal);

        Assert.Same(literal, expr.Value);
        Assert.IsAssignableFrom<ISqlExpression>(expr);
    }

    [Fact]
    public void SqlColumnExpression_WrapsColumnRef()
    {
        var col = SqlColumnRef.Simple("revenue");
        var expr = new SqlColumnExpression(col);

        Assert.Same(col, expr.Column);
        Assert.IsAssignableFrom<ISqlExpression>(expr);
    }

    [Fact]
    public void SqlBinaryExpression_StoresOperands()
    {
        var left = new SqlLiteralExpression(SqlLiteral.Number("10"));
        var right = new SqlLiteralExpression(SqlLiteral.Number("3"));
        var expr = new SqlBinaryExpression(left, SqlBinaryOperator.Multiply, right);

        Assert.Same(left, expr.Left);
        Assert.Same(right, expr.Right);
        Assert.Equal(SqlBinaryOperator.Multiply, expr.Operator);
    }

    [Fact]
    public void SqlUnaryExpression_StoresOperand()
    {
        var operand = new SqlLiteralExpression(SqlLiteral.Number("5"));
        var expr = new SqlUnaryExpression(SqlUnaryOperator.Negate, operand);

        Assert.Same(operand, expr.Operand);
        Assert.Equal(SqlUnaryOperator.Negate, expr.Operator);
    }

    [Fact]
    public void SqlFunctionExpression_StoresNameAndArguments()
    {
        var arg = new SqlColumnExpression(SqlColumnRef.Simple("name"));
        var expr = new SqlFunctionExpression("UPPER", new ISqlExpression[] { arg });

        Assert.Equal("UPPER", expr.FunctionName);
        Assert.Single(expr.Arguments);
        Assert.Same(arg, expr.Arguments[0]);
    }

    [Fact]
    public void SqlCaseExpression_StoresClauses()
    {
        var condition = new SqlComparisonCondition(
            SqlColumnRef.Simple("status"),
            SqlComparisonOperator.Equal,
            SqlLiteral.Number("1"));
        var result = new SqlLiteralExpression(SqlLiteral.String("Active"));
        var elseResult = new SqlLiteralExpression(SqlLiteral.String("Inactive"));
        var whenClause = new SqlWhenClause(condition, result);

        var expr = new SqlCaseExpression(
            new[] { whenClause },
            elseResult);

        Assert.Single(expr.WhenClauses);
        Assert.Same(condition, expr.WhenClauses[0].Condition);
        Assert.Same(result, expr.WhenClauses[0].Result);
        Assert.Same(elseResult, expr.ElseExpression);
    }

    [Fact]
    public void SqlCaseExpression_ElseIsOptional()
    {
        var condition = new SqlComparisonCondition(
            SqlColumnRef.Simple("x"),
            SqlComparisonOperator.Equal,
            SqlLiteral.Number("1"));
        var result = new SqlLiteralExpression(SqlLiteral.String("yes"));

        var expr = new SqlCaseExpression(new[] { new SqlWhenClause(condition, result) });

        Assert.Null(expr.ElseExpression);
    }

    [Fact]
    public void SqlIifExpression_StoresComponents()
    {
        var condition = new SqlComparisonCondition(
            SqlColumnRef.Simple("amount"),
            SqlComparisonOperator.GreaterThan,
            SqlLiteral.Number("0"));
        var trueVal = new SqlLiteralExpression(SqlLiteral.String("positive"));
        var falseVal = new SqlLiteralExpression(SqlLiteral.String("non-positive"));

        var expr = new SqlIifExpression(condition, trueVal, falseVal);

        Assert.Same(condition, expr.Condition);
        Assert.Same(trueVal, expr.TrueValue);
        Assert.Same(falseVal, expr.FalseValue);
    }

    [Fact]
    public void SqlCastExpression_StoresExpressionAndType()
    {
        var inner = new SqlColumnExpression(SqlColumnRef.Simple("revenue"));
        var expr = new SqlCastExpression(inner, "int");

        Assert.Same(inner, expr.Expression);
        Assert.Equal("int", expr.TargetType);
    }

    [Fact]
    public void SqlAggregateExpression_CountStar()
    {
        var expr = new SqlAggregateExpression(SqlAggregateFunction.Count);

        Assert.Equal(SqlAggregateFunction.Count, expr.Function);
        Assert.Null(expr.Operand);
        Assert.False(expr.IsDistinct);
    }

    [Fact]
    public void SqlAggregateExpression_SumWithDistinct()
    {
        var operand = new SqlColumnExpression(SqlColumnRef.Simple("revenue"));
        var expr = new SqlAggregateExpression(SqlAggregateFunction.Sum, operand, isDistinct: true);

        Assert.Equal(SqlAggregateFunction.Sum, expr.Function);
        Assert.Same(operand, expr.Operand);
        Assert.True(expr.IsDistinct);
    }

    [Fact]
    public void SqlSubqueryExpression_WrapsSelectStatement()
    {
        var subquery = new SqlSelectStatement(
            new ISqlSelectColumn[] { SqlColumnRef.Simple("id") },
            new SqlTableRef("account"));
        var expr = new SqlSubqueryExpression(subquery);

        Assert.Same(subquery, expr.Subquery);
    }

    [Fact]
    public void NestedBinaryExpressions_FormTree()
    {
        // (a + b) * c
        var a = new SqlColumnExpression(SqlColumnRef.Simple("a"));
        var b = new SqlColumnExpression(SqlColumnRef.Simple("b"));
        var c = new SqlColumnExpression(SqlColumnRef.Simple("c"));

        var sum = new SqlBinaryExpression(a, SqlBinaryOperator.Add, b);
        var product = new SqlBinaryExpression(sum, SqlBinaryOperator.Multiply, c);

        Assert.IsType<SqlBinaryExpression>(product.Left);
        var innerSum = (SqlBinaryExpression)product.Left;
        Assert.Same(a, innerSum.Left);
        Assert.Same(b, innerSum.Right);
        Assert.Same(c, product.Right);
    }
}
