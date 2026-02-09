using System.Collections.Generic;
using PPDS.Dataverse.Query;
using PPDS.Dataverse.Query.Execution;
using PPDS.Dataverse.Sql.Ast;
using Xunit;

namespace PPDS.Dataverse.Tests.Query.Execution;

[Trait("Category", "PlanUnit")]
public class ExpressionEvaluatorTests
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

    private static readonly IReadOnlyDictionary<string, QueryValue> EmptyRow =
        new Dictionary<string, QueryValue>();

    #region Literal Evaluation

    [Fact]
    public void Literal_Number_Integer()
    {
        var expr = new SqlLiteralExpression(SqlLiteral.Number("42"));
        var result = _eval.Evaluate(expr, EmptyRow);
        Assert.Equal(42, result);
    }

    [Fact]
    public void Literal_Number_Decimal()
    {
        var expr = new SqlLiteralExpression(SqlLiteral.Number("3.14"));
        var result = _eval.Evaluate(expr, EmptyRow);
        Assert.Equal(3.14m, result);
    }

    [Fact]
    public void Literal_String()
    {
        var expr = new SqlLiteralExpression(SqlLiteral.String("hello"));
        var result = _eval.Evaluate(expr, EmptyRow);
        Assert.Equal("hello", result);
    }

    [Fact]
    public void Literal_Null()
    {
        var expr = new SqlLiteralExpression(SqlLiteral.Null());
        var result = _eval.Evaluate(expr, EmptyRow);
        Assert.Null(result);
    }

    #endregion

    #region Column Evaluation

    [Fact]
    public void Column_ReturnsValue()
    {
        var expr = new SqlColumnExpression(SqlColumnRef.Simple("revenue"));
        var row = Row(("revenue", 1000000m));
        Assert.Equal(1000000m, _eval.Evaluate(expr, row));
    }

    [Fact]
    public void Column_CaseInsensitive()
    {
        var expr = new SqlColumnExpression(SqlColumnRef.Simple("Revenue"));
        var row = Row(("revenue", 500));
        Assert.Equal(500, _eval.Evaluate(expr, row));
    }

    [Fact]
    public void Column_MissingReturnsNull()
    {
        var expr = new SqlColumnExpression(SqlColumnRef.Simple("nonexistent"));
        var result = _eval.Evaluate(expr, EmptyRow);
        Assert.Null(result);
    }

    #endregion

    #region Arithmetic

    [Fact]
    public void Add_Integers()
    {
        var expr = new SqlBinaryExpression(
            new SqlLiteralExpression(SqlLiteral.Number("2")),
            SqlBinaryOperator.Add,
            new SqlLiteralExpression(SqlLiteral.Number("3")));
        Assert.Equal(5L, _eval.Evaluate(expr, EmptyRow));
    }

    [Fact]
    public void Subtract_Integers()
    {
        var expr = new SqlBinaryExpression(
            new SqlLiteralExpression(SqlLiteral.Number("10")),
            SqlBinaryOperator.Subtract,
            new SqlLiteralExpression(SqlLiteral.Number("3")));
        Assert.Equal(7L, _eval.Evaluate(expr, EmptyRow));
    }

    [Fact]
    public void Multiply_Integers()
    {
        var expr = new SqlBinaryExpression(
            new SqlLiteralExpression(SqlLiteral.Number("4")),
            SqlBinaryOperator.Multiply,
            new SqlLiteralExpression(SqlLiteral.Number("5")));
        Assert.Equal(20L, _eval.Evaluate(expr, EmptyRow));
    }

    [Fact]
    public void Divide_Integers()
    {
        // Integer division: 10 / 3 = 3 (SQL Server integer division)
        var expr = new SqlBinaryExpression(
            new SqlLiteralExpression(SqlLiteral.Number("10")),
            SqlBinaryOperator.Divide,
            new SqlLiteralExpression(SqlLiteral.Number("3")));
        Assert.Equal(3L, _eval.Evaluate(expr, EmptyRow));
    }

    [Fact]
    public void Divide_Decimals()
    {
        var expr = new SqlBinaryExpression(
            new SqlLiteralExpression(SqlLiteral.Number("10.0")),
            SqlBinaryOperator.Divide,
            new SqlLiteralExpression(SqlLiteral.Number("3")));
        var result = _eval.Evaluate(expr, EmptyRow);
        Assert.IsType<decimal>(result);
        // 10.0 / 3 = 3.333...
        Assert.True((decimal)result! > 3.33m && (decimal)result < 3.34m);
    }

    [Fact]
    public void Modulo_Integers()
    {
        var expr = new SqlBinaryExpression(
            new SqlLiteralExpression(SqlLiteral.Number("10")),
            SqlBinaryOperator.Modulo,
            new SqlLiteralExpression(SqlLiteral.Number("3")));
        Assert.Equal(1L, _eval.Evaluate(expr, EmptyRow));
    }

    [Fact]
    public void Divide_ByZero_Throws()
    {
        var expr = new SqlBinaryExpression(
            new SqlLiteralExpression(SqlLiteral.Number("10")),
            SqlBinaryOperator.Divide,
            new SqlLiteralExpression(SqlLiteral.Number("0")));
        Assert.Throws<System.DivideByZeroException>(() => _eval.Evaluate(expr, EmptyRow));
    }

    [Fact]
    public void Arithmetic_WithColumnValues()
    {
        // revenue * 0.1
        var expr = new SqlBinaryExpression(
            new SqlColumnExpression(SqlColumnRef.Simple("revenue")),
            SqlBinaryOperator.Multiply,
            new SqlLiteralExpression(SqlLiteral.Number("0.1")));
        var row = Row(("revenue", 1000m));
        Assert.Equal(100.0m, _eval.Evaluate(expr, row));
    }

    #endregion

    #region Type Promotion

    [Fact]
    public void TypePromotion_IntPlusDecimal_ReturnsDecimal()
    {
        var row = Row(("a", 5));
        var expr = new SqlBinaryExpression(
            new SqlColumnExpression(SqlColumnRef.Simple("a")),
            SqlBinaryOperator.Add,
            new SqlLiteralExpression(SqlLiteral.Number("2.5")));
        var result = _eval.Evaluate(expr, row);
        Assert.IsType<decimal>(result);
        Assert.Equal(7.5m, result);
    }

    [Fact]
    public void TypePromotion_IntPlusDouble_ReturnsDouble()
    {
        var row = Row(("a", 5), ("b", 2.5d));
        var expr = new SqlBinaryExpression(
            new SqlColumnExpression(SqlColumnRef.Simple("a")),
            SqlBinaryOperator.Add,
            new SqlColumnExpression(SqlColumnRef.Simple("b")));
        var result = _eval.Evaluate(expr, row);
        Assert.IsType<double>(result);
        Assert.Equal(7.5d, result);
    }

    #endregion

    #region NULL Propagation

    [Fact]
    public void Null_Plus_Number_ReturnsNull()
    {
        var expr = new SqlBinaryExpression(
            new SqlLiteralExpression(SqlLiteral.Null()),
            SqlBinaryOperator.Add,
            new SqlLiteralExpression(SqlLiteral.Number("5")));
        Assert.Null(_eval.Evaluate(expr, EmptyRow));
    }

    [Fact]
    public void Number_Plus_Null_ReturnsNull()
    {
        var expr = new SqlBinaryExpression(
            new SqlLiteralExpression(SqlLiteral.Number("5")),
            SqlBinaryOperator.Add,
            new SqlLiteralExpression(SqlLiteral.Null()));
        Assert.Null(_eval.Evaluate(expr, EmptyRow));
    }

    [Fact]
    public void Negate_Null_ReturnsNull()
    {
        var expr = new SqlUnaryExpression(
            SqlUnaryOperator.Negate,
            new SqlLiteralExpression(SqlLiteral.Null()));
        Assert.Null(_eval.Evaluate(expr, EmptyRow));
    }

    #endregion

    #region String Concatenation

    [Fact]
    public void StringConcat_WithAdd()
    {
        var expr = new SqlBinaryExpression(
            new SqlLiteralExpression(SqlLiteral.String("hello")),
            SqlBinaryOperator.Add,
            new SqlLiteralExpression(SqlLiteral.String(" world")));
        Assert.Equal("hello world", _eval.Evaluate(expr, EmptyRow));
    }

    [Fact]
    public void StringConcat_MixedTypes()
    {
        // 'Count: ' + 42
        var row = Row(("count", 42));
        var expr = new SqlBinaryExpression(
            new SqlLiteralExpression(SqlLiteral.String("Count: ")),
            SqlBinaryOperator.Add,
            new SqlColumnExpression(SqlColumnRef.Simple("count")));
        Assert.Equal("Count: 42", _eval.Evaluate(expr, row));
    }

    [Fact]
    public void StringConcat_Null_ReturnsNull()
    {
        var expr = new SqlBinaryExpression(
            new SqlLiteralExpression(SqlLiteral.String("hello")),
            SqlBinaryOperator.Add,
            new SqlLiteralExpression(SqlLiteral.Null()));
        Assert.Null(_eval.Evaluate(expr, EmptyRow));
    }

    #endregion

    #region Unary

    [Fact]
    public void Negate_Integer()
    {
        var expr = new SqlUnaryExpression(
            SqlUnaryOperator.Negate,
            new SqlLiteralExpression(SqlLiteral.Number("5")));
        Assert.Equal(-5, _eval.Evaluate(expr, EmptyRow));
    }

    [Fact]
    public void Not_Boolean()
    {
        var row = Row(("active", true));
        var expr = new SqlUnaryExpression(
            SqlUnaryOperator.Not,
            new SqlColumnExpression(SqlColumnRef.Simple("active")));
        Assert.Equal(false, _eval.Evaluate(expr, row));
    }

    #endregion

    #region Condition Evaluation

    [Fact]
    public void Comparison_Equal_True()
    {
        var cond = new SqlComparisonCondition(
            SqlColumnRef.Simple("status"),
            SqlComparisonOperator.Equal,
            SqlLiteral.Number("1"));
        Assert.True(_eval.EvaluateCondition(cond, Row(("status", 1))));
    }

    [Fact]
    public void Comparison_Equal_False()
    {
        var cond = new SqlComparisonCondition(
            SqlColumnRef.Simple("status"),
            SqlComparisonOperator.Equal,
            SqlLiteral.Number("1"));
        Assert.False(_eval.EvaluateCondition(cond, Row(("status", 2))));
    }

    [Fact]
    public void Comparison_Null_AlwaysFalse()
    {
        // SQL: NULL = NULL is false
        var cond = new SqlComparisonCondition(
            SqlColumnRef.Simple("x"),
            SqlComparisonOperator.Equal,
            SqlLiteral.Null());
        Assert.False(_eval.EvaluateCondition(cond, Row(("x", null))));
    }

    [Fact]
    public void Comparison_GreaterThan()
    {
        var cond = new SqlComparisonCondition(
            SqlColumnRef.Simple("revenue"),
            SqlComparisonOperator.GreaterThan,
            SqlLiteral.Number("1000000"));
        Assert.True(_eval.EvaluateCondition(cond, Row(("revenue", 2000000m))));
        Assert.False(_eval.EvaluateCondition(cond, Row(("revenue", 500000m))));
    }

    [Fact]
    public void Comparison_String()
    {
        var cond = new SqlComparisonCondition(
            SqlColumnRef.Simple("name"),
            SqlComparisonOperator.Equal,
            SqlLiteral.String("Contoso"));
        Assert.True(_eval.EvaluateCondition(cond, Row(("name", "Contoso"))));
        Assert.True(_eval.EvaluateCondition(cond, Row(("name", "contoso")))); // case-insensitive
    }

    [Fact]
    public void IsNull_True()
    {
        var cond = new SqlNullCondition(SqlColumnRef.Simple("email"), isNegated: false);
        Assert.True(_eval.EvaluateCondition(cond, Row(("email", null))));
    }

    [Fact]
    public void IsNull_False()
    {
        var cond = new SqlNullCondition(SqlColumnRef.Simple("email"), isNegated: false);
        Assert.False(_eval.EvaluateCondition(cond, Row(("email", "test@test.com"))));
    }

    [Fact]
    public void IsNotNull_True()
    {
        var cond = new SqlNullCondition(SqlColumnRef.Simple("email"), isNegated: true);
        Assert.True(_eval.EvaluateCondition(cond, Row(("email", "test@test.com"))));
    }

    [Fact]
    public void Like_PercentWildcard()
    {
        var cond = new SqlLikeCondition(SqlColumnRef.Simple("name"), "%soft%", isNegated: false);
        Assert.True(_eval.EvaluateCondition(cond, Row(("name", "Microsoft"))));
        Assert.False(_eval.EvaluateCondition(cond, Row(("name", "Google"))));
    }

    [Fact]
    public void Like_UnderscoreWildcard()
    {
        var cond = new SqlLikeCondition(SqlColumnRef.Simple("code"), "A_C", isNegated: false);
        Assert.True(_eval.EvaluateCondition(cond, Row(("code", "ABC"))));
        Assert.False(_eval.EvaluateCondition(cond, Row(("code", "ABBC"))));
    }

    [Fact]
    public void NotLike()
    {
        var cond = new SqlLikeCondition(SqlColumnRef.Simple("name"), "%test%", isNegated: true);
        Assert.True(_eval.EvaluateCondition(cond, Row(("name", "production"))));
        Assert.False(_eval.EvaluateCondition(cond, Row(("name", "test-env"))));
    }

    [Fact]
    public void In_Match()
    {
        var cond = new SqlInCondition(
            SqlColumnRef.Simple("status"),
            new[] { SqlLiteral.Number("1"), SqlLiteral.Number("2"), SqlLiteral.Number("3") },
            isNegated: false);
        Assert.True(_eval.EvaluateCondition(cond, Row(("status", 2))));
    }

    [Fact]
    public void In_NoMatch()
    {
        var cond = new SqlInCondition(
            SqlColumnRef.Simple("status"),
            new[] { SqlLiteral.Number("1"), SqlLiteral.Number("2") },
            isNegated: false);
        Assert.False(_eval.EvaluateCondition(cond, Row(("status", 5))));
    }

    [Fact]
    public void NotIn()
    {
        var cond = new SqlInCondition(
            SqlColumnRef.Simple("status"),
            new[] { SqlLiteral.Number("1"), SqlLiteral.Number("2") },
            isNegated: true);
        Assert.True(_eval.EvaluateCondition(cond, Row(("status", 5))));
        Assert.False(_eval.EvaluateCondition(cond, Row(("status", 1))));
    }

    [Fact]
    public void And_BothTrue()
    {
        var cond = SqlLogicalCondition.And(
            new SqlComparisonCondition(SqlColumnRef.Simple("a"), SqlComparisonOperator.Equal, SqlLiteral.Number("1")),
            new SqlComparisonCondition(SqlColumnRef.Simple("b"), SqlComparisonOperator.Equal, SqlLiteral.Number("2")));
        Assert.True(_eval.EvaluateCondition(cond, Row(("a", 1), ("b", 2))));
    }

    [Fact]
    public void And_OneFalse()
    {
        var cond = SqlLogicalCondition.And(
            new SqlComparisonCondition(SqlColumnRef.Simple("a"), SqlComparisonOperator.Equal, SqlLiteral.Number("1")),
            new SqlComparisonCondition(SqlColumnRef.Simple("b"), SqlComparisonOperator.Equal, SqlLiteral.Number("2")));
        Assert.False(_eval.EvaluateCondition(cond, Row(("a", 1), ("b", 99))));
    }

    [Fact]
    public void Or_OneTrue()
    {
        var cond = SqlLogicalCondition.Or(
            new SqlComparisonCondition(SqlColumnRef.Simple("a"), SqlComparisonOperator.Equal, SqlLiteral.Number("1")),
            new SqlComparisonCondition(SqlColumnRef.Simple("b"), SqlComparisonOperator.Equal, SqlLiteral.Number("2")));
        Assert.True(_eval.EvaluateCondition(cond, Row(("a", 1), ("b", 99))));
    }

    [Fact]
    public void Or_NoneTrue()
    {
        var cond = SqlLogicalCondition.Or(
            new SqlComparisonCondition(SqlColumnRef.Simple("a"), SqlComparisonOperator.Equal, SqlLiteral.Number("1")),
            new SqlComparisonCondition(SqlColumnRef.Simple("b"), SqlComparisonOperator.Equal, SqlLiteral.Number("2")));
        Assert.False(_eval.EvaluateCondition(cond, Row(("a", 99), ("b", 99))));
    }

    #endregion
}
