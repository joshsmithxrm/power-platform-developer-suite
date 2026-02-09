using System.Collections.Generic;
using PPDS.Dataverse.Query;
using PPDS.Dataverse.Query.Execution;
using PPDS.Dataverse.Sql.Ast;
using Xunit;

namespace PPDS.Dataverse.Tests.Query.Execution;

[Trait("Category", "PlanUnit")]
public class ExpressionConditionEvalTests
{
    private readonly ExpressionEvaluator _eval = new();

    private static IReadOnlyDictionary<string, QueryValue> Row(params (string key, object? value)[] pairs)
    {
        var dict = new Dictionary<string, QueryValue>();
        foreach (var (key, value) in pairs)
        {
            dict[key] = QueryValue.Simple(value);
        }
        return dict;
    }

    [Fact]
    public void EvaluateExpressionCondition_ColumnToColumn_GreaterThan_True()
    {
        // revenue > cost where revenue=5000, cost=3000
        var condition = new SqlExpressionCondition(
            new SqlColumnExpression(SqlColumnRef.Simple("revenue")),
            SqlComparisonOperator.GreaterThan,
            new SqlColumnExpression(SqlColumnRef.Simple("cost")));

        var row = Row(("revenue", 5000m), ("cost", 3000m));

        Assert.True(_eval.EvaluateCondition(condition, row));
    }

    [Fact]
    public void EvaluateExpressionCondition_ColumnToColumn_GreaterThan_False()
    {
        // revenue > cost where revenue=1000, cost=3000
        var condition = new SqlExpressionCondition(
            new SqlColumnExpression(SqlColumnRef.Simple("revenue")),
            SqlComparisonOperator.GreaterThan,
            new SqlColumnExpression(SqlColumnRef.Simple("cost")));

        var row = Row(("revenue", 1000m), ("cost", 3000m));

        Assert.False(_eval.EvaluateCondition(condition, row));
    }

    [Fact]
    public void EvaluateExpressionCondition_ColumnToColumn_Equal()
    {
        var condition = new SqlExpressionCondition(
            new SqlColumnExpression(SqlColumnRef.Simple("field1")),
            SqlComparisonOperator.Equal,
            new SqlColumnExpression(SqlColumnRef.Simple("field2")));

        var row = Row(("field1", 42), ("field2", 42));

        Assert.True(_eval.EvaluateCondition(condition, row));
    }

    [Fact]
    public void EvaluateExpressionCondition_WithNullLeft_ReturnsFalse()
    {
        // SQL semantics: NULL comparison always returns false
        var condition = new SqlExpressionCondition(
            new SqlColumnExpression(SqlColumnRef.Simple("revenue")),
            SqlComparisonOperator.GreaterThan,
            new SqlColumnExpression(SqlColumnRef.Simple("cost")));

        var row = Row(("revenue", null), ("cost", 3000m));

        Assert.False(_eval.EvaluateCondition(condition, row));
    }

    [Fact]
    public void EvaluateExpressionCondition_WithNullRight_ReturnsFalse()
    {
        // SQL semantics: NULL comparison always returns false
        var condition = new SqlExpressionCondition(
            new SqlColumnExpression(SqlColumnRef.Simple("revenue")),
            SqlComparisonOperator.GreaterThan,
            new SqlColumnExpression(SqlColumnRef.Simple("cost")));

        var row = Row(("revenue", 5000m), ("cost", null));

        Assert.False(_eval.EvaluateCondition(condition, row));
    }

    [Fact]
    public void EvaluateExpressionCondition_WithBothNull_ReturnsFalse()
    {
        var condition = new SqlExpressionCondition(
            new SqlColumnExpression(SqlColumnRef.Simple("revenue")),
            SqlComparisonOperator.Equal,
            new SqlColumnExpression(SqlColumnRef.Simple("cost")));

        var row = Row(("revenue", null), ("cost", null));

        // SQL: NULL = NULL is false (not true)
        Assert.False(_eval.EvaluateCondition(condition, row));
    }

    [Fact]
    public void EvaluateExpressionCondition_ArithmeticExpression()
    {
        // revenue > cost * 2 where revenue=7000, cost=3000
        var condition = new SqlExpressionCondition(
            new SqlColumnExpression(SqlColumnRef.Simple("revenue")),
            SqlComparisonOperator.GreaterThan,
            new SqlBinaryExpression(
                new SqlColumnExpression(SqlColumnRef.Simple("cost")),
                SqlBinaryOperator.Multiply,
                new SqlLiteralExpression(SqlLiteral.Number("2"))));

        var row = Row(("revenue", 7000m), ("cost", 3000m));

        // 7000 > 3000 * 2 = 6000 → true
        Assert.True(_eval.EvaluateCondition(condition, row));
    }

    [Fact]
    public void EvaluateExpressionCondition_ArithmeticExpression_False()
    {
        // revenue > cost * 2 where revenue=5000, cost=3000
        var condition = new SqlExpressionCondition(
            new SqlColumnExpression(SqlColumnRef.Simple("revenue")),
            SqlComparisonOperator.GreaterThan,
            new SqlBinaryExpression(
                new SqlColumnExpression(SqlColumnRef.Simple("cost")),
                SqlBinaryOperator.Multiply,
                new SqlLiteralExpression(SqlLiteral.Number("2"))));

        var row = Row(("revenue", 5000m), ("cost", 3000m));

        // 5000 > 3000 * 2 = 6000 → false
        Assert.False(_eval.EvaluateCondition(condition, row));
    }

    [Fact]
    public void EvaluateExpressionCondition_StringComparison()
    {
        // name1 = name2
        var condition = new SqlExpressionCondition(
            new SqlColumnExpression(SqlColumnRef.Simple("name1")),
            SqlComparisonOperator.Equal,
            new SqlColumnExpression(SqlColumnRef.Simple("name2")));

        var row = Row(("name1", "Contoso"), ("name2", "contoso"));

        // Case-insensitive like SQL Server default
        Assert.True(_eval.EvaluateCondition(condition, row));
    }

    [Fact]
    public void EvaluateExpressionCondition_LessThanOrEqual()
    {
        var condition = new SqlExpressionCondition(
            new SqlColumnExpression(SqlColumnRef.Simple("a")),
            SqlComparisonOperator.LessThanOrEqual,
            new SqlColumnExpression(SqlColumnRef.Simple("b")));

        var row = Row(("a", 5), ("b", 5));

        Assert.True(_eval.EvaluateCondition(condition, row));
    }

    [Fact]
    public void EvaluateExpressionCondition_MissingColumn_ReturnsNull_ThenFalse()
    {
        // Column not in row → evaluates to null → comparison returns false
        var condition = new SqlExpressionCondition(
            new SqlColumnExpression(SqlColumnRef.Simple("missing")),
            SqlComparisonOperator.Equal,
            new SqlColumnExpression(SqlColumnRef.Simple("present")));

        var row = Row(("present", 42));

        Assert.False(_eval.EvaluateCondition(condition, row));
    }
}
