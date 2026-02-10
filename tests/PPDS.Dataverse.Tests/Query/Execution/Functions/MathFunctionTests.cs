using System;
using System.Collections.Generic;
using PPDS.Dataverse.Query;
using PPDS.Dataverse.Query.Execution;
using PPDS.Dataverse.Query.Execution.Functions;
using PPDS.Dataverse.Sql.Ast;
using Xunit;

namespace PPDS.Dataverse.Tests.Query.Execution.Functions;

[Trait("Category", "Unit")]
public class MathFunctionTests
{
    private readonly ExpressionEvaluator _eval = new();

    private static readonly IReadOnlyDictionary<string, QueryValue> EmptyRow =
        new Dictionary<string, QueryValue>();

    private static SqlFunctionExpression Fn(string name, params ISqlExpression[] args)
    {
        return new SqlFunctionExpression(name, args);
    }

    private static SqlLiteralExpression Num(string value) =>
        new(SqlLiteral.Number(value));

    private static SqlLiteralExpression Null() =>
        new(SqlLiteral.Null());

    #region ABS

    [Fact]
    public void Abs_PositiveNumber()
    {
        var result = _eval.Evaluate(Fn("ABS", Num("5")), EmptyRow);
        Assert.Equal(5, result);
    }

    [Fact]
    public void Abs_NegativeNumber()
    {
        var result = _eval.Evaluate(Fn("ABS", Num("-5")), EmptyRow);
        Assert.Equal(5, result);
    }

    [Fact]
    public void Abs_Zero()
    {
        var result = _eval.Evaluate(Fn("ABS", Num("0")), EmptyRow);
        Assert.Equal(0, result);
    }

    [Fact]
    public void Abs_Null_ReturnsNull()
    {
        var result = _eval.Evaluate(Fn("ABS", Null()), EmptyRow);
        Assert.Null(result);
    }

    [Fact]
    public void Abs_Decimal()
    {
        var result = _eval.Evaluate(Fn("ABS", Num("-3.14")), EmptyRow);
        Assert.Equal(3.14m, result);
    }

    #endregion

    #region CEILING

    [Fact]
    public void Ceiling_PositiveDecimal()
    {
        var result = _eval.Evaluate(Fn("CEILING", Num("4.1")), EmptyRow);
        Assert.Equal(5m, result);
    }

    [Fact]
    public void Ceiling_NegativeDecimal()
    {
        var result = _eval.Evaluate(Fn("CEILING", Num("-4.9")), EmptyRow);
        Assert.Equal(-4m, result);
    }

    [Fact]
    public void Ceiling_WholeNumber()
    {
        var result = _eval.Evaluate(Fn("CEILING", Num("5")), EmptyRow);
        Assert.Equal(5, result);
    }

    [Fact]
    public void Ceiling_Null_ReturnsNull()
    {
        var result = _eval.Evaluate(Fn("CEILING", Null()), EmptyRow);
        Assert.Null(result);
    }

    #endregion

    #region FLOOR

    [Fact]
    public void Floor_PositiveDecimal()
    {
        var result = _eval.Evaluate(Fn("FLOOR", Num("4.9")), EmptyRow);
        Assert.Equal(4m, result);
    }

    [Fact]
    public void Floor_NegativeDecimal()
    {
        var result = _eval.Evaluate(Fn("FLOOR", Num("-4.1")), EmptyRow);
        Assert.Equal(-5m, result);
    }

    [Fact]
    public void Floor_WholeNumber()
    {
        var result = _eval.Evaluate(Fn("FLOOR", Num("5")), EmptyRow);
        Assert.Equal(5, result);
    }

    [Fact]
    public void Floor_Null_ReturnsNull()
    {
        var result = _eval.Evaluate(Fn("FLOOR", Null()), EmptyRow);
        Assert.Null(result);
    }

    #endregion

    #region ROUND

    [Fact]
    public void Round_TwoDecimals()
    {
        var result = _eval.Evaluate(Fn("ROUND", Num("3.14159"), Num("2")), EmptyRow);
        Assert.Equal(3.14m, result);
    }

    [Fact]
    public void Round_ZeroDecimals()
    {
        var result = _eval.Evaluate(Fn("ROUND", Num("3.5"), Num("0")), EmptyRow);
        // AwayFromZero: 3.5 => 4
        Assert.Equal(4m, result);
    }

    [Fact]
    public void Round_Null_ReturnsNull()
    {
        var result = _eval.Evaluate(Fn("ROUND", Null(), Num("2")), EmptyRow);
        Assert.Null(result);
    }

    #endregion

    #region POWER

    [Fact]
    public void Power_BasicUsage()
    {
        var result = _eval.Evaluate(Fn("POWER", Num("2"), Num("10")), EmptyRow);
        Assert.Equal(1024.0, result);
    }

    [Fact]
    public void Power_ZeroExponent()
    {
        var result = _eval.Evaluate(Fn("POWER", Num("5"), Num("0")), EmptyRow);
        Assert.Equal(1.0, result);
    }

    [Fact]
    public void Power_NegativeExponent()
    {
        var result = _eval.Evaluate(Fn("POWER", Num("2"), Num("-1")), EmptyRow);
        Assert.Equal(0.5, result);
    }

    [Fact]
    public void Power_Null_ReturnsNull()
    {
        var result = _eval.Evaluate(Fn("POWER", Null(), Num("2")), EmptyRow);
        Assert.Null(result);
    }

    #endregion

    #region LOG

    [Fact]
    public void Log_NaturalLog()
    {
        var result = _eval.Evaluate(Fn("LOG", Num("1")), EmptyRow);
        Assert.Equal(0.0, (double)result!, 10);
    }

    [Fact]
    public void Log_E()
    {
        // LOG(e) = 1
        var result = _eval.Evaluate(Fn("LOG", Num("2.718281828459045")), EmptyRow);
        Assert.Equal(1.0, (double)result!, 5);
    }

    [Fact]
    public void Log_WithBase()
    {
        // LOG(8, 2) = 3
        var result = _eval.Evaluate(Fn("LOG", Num("8"), Num("2")), EmptyRow);
        Assert.Equal(3.0, (double)result!, 10);
    }

    [Fact]
    public void Log_Null_ReturnsNull()
    {
        var result = _eval.Evaluate(Fn("LOG", Null()), EmptyRow);
        Assert.Null(result);
    }

    #endregion

    #region LOG10

    [Fact]
    public void Log10_Hundred()
    {
        var result = _eval.Evaluate(Fn("LOG10", Num("100")), EmptyRow);
        Assert.Equal(2.0, (double)result!, 10);
    }

    [Fact]
    public void Log10_One()
    {
        var result = _eval.Evaluate(Fn("LOG10", Num("1")), EmptyRow);
        Assert.Equal(0.0, (double)result!, 10);
    }

    [Fact]
    public void Log10_Null_ReturnsNull()
    {
        var result = _eval.Evaluate(Fn("LOG10", Null()), EmptyRow);
        Assert.Null(result);
    }

    #endregion

    #region SQRT

    [Fact]
    public void Sqrt_PerfectSquare()
    {
        var result = _eval.Evaluate(Fn("SQRT", Num("25")), EmptyRow);
        Assert.Equal(5.0, result);
    }

    [Fact]
    public void Sqrt_Zero()
    {
        var result = _eval.Evaluate(Fn("SQRT", Num("0")), EmptyRow);
        Assert.Equal(0.0, result);
    }

    [Fact]
    public void Sqrt_Two()
    {
        var result = _eval.Evaluate(Fn("SQRT", Num("2")), EmptyRow);
        Assert.Equal(Math.Sqrt(2.0), (double)result!, 10);
    }

    [Fact]
    public void Sqrt_Null_ReturnsNull()
    {
        var result = _eval.Evaluate(Fn("SQRT", Null()), EmptyRow);
        Assert.Null(result);
    }

    #endregion

    #region EXP

    [Fact]
    public void Exp_Zero()
    {
        var result = _eval.Evaluate(Fn("EXP", Num("0")), EmptyRow);
        Assert.Equal(1.0, result);
    }

    [Fact]
    public void Exp_One()
    {
        var result = _eval.Evaluate(Fn("EXP", Num("1")), EmptyRow);
        Assert.Equal(Math.E, (double)result!, 10);
    }

    [Fact]
    public void Exp_Null_ReturnsNull()
    {
        var result = _eval.Evaluate(Fn("EXP", Null()), EmptyRow);
        Assert.Null(result);
    }

    #endregion

    #region Trigonometric Functions

    [Fact]
    public void Sin_Zero()
    {
        var result = _eval.Evaluate(Fn("SIN", Num("0")), EmptyRow);
        Assert.Equal(0.0, (double)result!, 10);
    }

    [Fact]
    public void Cos_Zero()
    {
        var result = _eval.Evaluate(Fn("COS", Num("0")), EmptyRow);
        Assert.Equal(1.0, (double)result!, 10);
    }

    [Fact]
    public void Tan_Zero()
    {
        var result = _eval.Evaluate(Fn("TAN", Num("0")), EmptyRow);
        Assert.Equal(0.0, (double)result!, 10);
    }

    [Fact]
    public void Sin_Null_ReturnsNull()
    {
        var result = _eval.Evaluate(Fn("SIN", Null()), EmptyRow);
        Assert.Null(result);
    }

    [Fact]
    public void Cos_Null_ReturnsNull()
    {
        var result = _eval.Evaluate(Fn("COS", Null()), EmptyRow);
        Assert.Null(result);
    }

    [Fact]
    public void Tan_Null_ReturnsNull()
    {
        var result = _eval.Evaluate(Fn("TAN", Null()), EmptyRow);
        Assert.Null(result);
    }

    #endregion

    #region Inverse Trigonometric Functions

    [Fact]
    public void Asin_Zero()
    {
        var result = _eval.Evaluate(Fn("ASIN", Num("0")), EmptyRow);
        Assert.Equal(0.0, (double)result!, 10);
    }

    [Fact]
    public void Asin_One()
    {
        var result = _eval.Evaluate(Fn("ASIN", Num("1")), EmptyRow);
        Assert.Equal(Math.PI / 2.0, (double)result!, 10);
    }

    [Fact]
    public void Acos_One()
    {
        var result = _eval.Evaluate(Fn("ACOS", Num("1")), EmptyRow);
        Assert.Equal(0.0, (double)result!, 10);
    }

    [Fact]
    public void Acos_Zero()
    {
        var result = _eval.Evaluate(Fn("ACOS", Num("0")), EmptyRow);
        Assert.Equal(Math.PI / 2.0, (double)result!, 10);
    }

    [Fact]
    public void Atan_Zero()
    {
        var result = _eval.Evaluate(Fn("ATAN", Num("0")), EmptyRow);
        Assert.Equal(0.0, (double)result!, 10);
    }

    [Fact]
    public void Atan_One()
    {
        var result = _eval.Evaluate(Fn("ATAN", Num("1")), EmptyRow);
        Assert.Equal(Math.PI / 4.0, (double)result!, 10);
    }

    [Fact]
    public void Asin_Null_ReturnsNull()
    {
        var result = _eval.Evaluate(Fn("ASIN", Null()), EmptyRow);
        Assert.Null(result);
    }

    #endregion

    #region ATN2 / ATAN2

    [Fact]
    public void Atan2_BasicUsage()
    {
        var result = _eval.Evaluate(Fn("ATAN2", Num("1"), Num("1")), EmptyRow);
        Assert.Equal(Math.PI / 4.0, (double)result!, 10);
    }

    [Fact]
    public void Atn2_BasicUsage()
    {
        // T-SQL name ATN2
        var result = _eval.Evaluate(Fn("ATN2", Num("0"), Num("1")), EmptyRow);
        Assert.Equal(0.0, (double)result!, 10);
    }

    [Fact]
    public void Atan2_Null_ReturnsNull()
    {
        var result = _eval.Evaluate(Fn("ATAN2", Null(), Num("1")), EmptyRow);
        Assert.Null(result);
    }

    #endregion

    #region DEGREES / RADIANS

    [Fact]
    public void Degrees_Pi()
    {
        var result = _eval.Evaluate(Fn("DEGREES", Num("3.14159265358979")), EmptyRow);
        Assert.Equal(180.0, (double)result!, 3);
    }

    [Fact]
    public void Degrees_Null_ReturnsNull()
    {
        var result = _eval.Evaluate(Fn("DEGREES", Null()), EmptyRow);
        Assert.Null(result);
    }

    [Fact]
    public void Radians_180()
    {
        var result = _eval.Evaluate(Fn("RADIANS", Num("180")), EmptyRow);
        Assert.Equal(Math.PI, (double)result!, 10);
    }

    [Fact]
    public void Radians_Null_ReturnsNull()
    {
        var result = _eval.Evaluate(Fn("RADIANS", Null()), EmptyRow);
        Assert.Null(result);
    }

    #endregion

    #region RAND

    [Fact]
    public void Rand_ReturnsValueBetweenZeroAndOne()
    {
        var result = _eval.Evaluate(Fn("RAND"), EmptyRow);
        Assert.IsType<double>(result);
        var d = (double)result;
        Assert.True(d >= 0.0 && d < 1.0);
    }

    [Fact]
    public void Rand_WithSeed_Deterministic()
    {
        var result1 = _eval.Evaluate(Fn("RAND", Num("42")), EmptyRow);
        var result2 = _eval.Evaluate(Fn("RAND", Num("42")), EmptyRow);
        Assert.Equal(result1, result2);
    }

    #endregion

    #region PI

    [Fact]
    public void Pi_ReturnsCorrectValue()
    {
        var result = _eval.Evaluate(Fn("PI"), EmptyRow);
        Assert.Equal(Math.PI, result);
    }

    #endregion

    #region SQUARE

    [Fact]
    public void Square_BasicUsage()
    {
        var result = _eval.Evaluate(Fn("SQUARE", Num("5")), EmptyRow);
        Assert.Equal(25.0, result);
    }

    [Fact]
    public void Square_Negative()
    {
        var result = _eval.Evaluate(Fn("SQUARE", Num("-3")), EmptyRow);
        Assert.Equal(9.0, result);
    }

    [Fact]
    public void Square_Zero()
    {
        var result = _eval.Evaluate(Fn("SQUARE", Num("0")), EmptyRow);
        Assert.Equal(0.0, result);
    }

    [Fact]
    public void Square_Null_ReturnsNull()
    {
        var result = _eval.Evaluate(Fn("SQUARE", Null()), EmptyRow);
        Assert.Null(result);
    }

    #endregion

    #region SIGN

    [Fact]
    public void Sign_Positive()
    {
        var result = _eval.Evaluate(Fn("SIGN", Num("42")), EmptyRow);
        Assert.Equal(1, result);
    }

    [Fact]
    public void Sign_Negative()
    {
        var result = _eval.Evaluate(Fn("SIGN", Num("-7")), EmptyRow);
        Assert.Equal(-1, result);
    }

    [Fact]
    public void Sign_Zero()
    {
        var result = _eval.Evaluate(Fn("SIGN", Num("0")), EmptyRow);
        Assert.Equal(0, result);
    }

    [Fact]
    public void Sign_Null_ReturnsNull()
    {
        var result = _eval.Evaluate(Fn("SIGN", Null()), EmptyRow);
        Assert.Null(result);
    }

    [Fact]
    public void Sign_Decimal_Positive()
    {
        var result = _eval.Evaluate(Fn("SIGN", Num("3.14")), EmptyRow);
        Assert.Equal(1, result);
    }

    #endregion

    #region Registry

    [Fact]
    public void AllMathFunctionsRegistered()
    {
        var registry = FunctionRegistry.CreateDefault();
        Assert.True(registry.IsRegistered("ABS"));
        Assert.True(registry.IsRegistered("CEILING"));
        Assert.True(registry.IsRegistered("FLOOR"));
        Assert.True(registry.IsRegistered("ROUND"));
        Assert.True(registry.IsRegistered("POWER"));
        Assert.True(registry.IsRegistered("LOG"));
        Assert.True(registry.IsRegistered("LOG10"));
        Assert.True(registry.IsRegistered("SQRT"));
        Assert.True(registry.IsRegistered("EXP"));
        Assert.True(registry.IsRegistered("SIN"));
        Assert.True(registry.IsRegistered("COS"));
        Assert.True(registry.IsRegistered("TAN"));
        Assert.True(registry.IsRegistered("ASIN"));
        Assert.True(registry.IsRegistered("ACOS"));
        Assert.True(registry.IsRegistered("ATAN"));
        Assert.True(registry.IsRegistered("ATN2"));
        Assert.True(registry.IsRegistered("ATAN2"));
        Assert.True(registry.IsRegistered("DEGREES"));
        Assert.True(registry.IsRegistered("RADIANS"));
        Assert.True(registry.IsRegistered("RAND"));
        Assert.True(registry.IsRegistered("PI"));
        Assert.True(registry.IsRegistered("SQUARE"));
        Assert.True(registry.IsRegistered("SIGN"));
    }

    #endregion
}
