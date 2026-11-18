using FluentAssertions;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using PPDS.Dataverse.Query.Execution.Functions;
using PPDS.Query.Execution;
using PPDS.Query.Transpilation;
using Xunit;

namespace PPDS.Query.Tests.Execution;

/// <summary>
/// Values the engine computes where T-SQL has a definite answer: conversions with an explicit length, precision and scale,
/// and int arithmetic that overflows. A DML statement writes exactly what T-SQL would, or fails with nothing written.
/// </summary>
[Trait("Category", "PlanUnit")]
public class TsqlValueSemanticsTests
{
    // ---- CAST / CONVERT to nvarchar(n) -----------------------------------------------------------------

    [Fact]
    public void TextLongerThanN_IsTruncatedToN()
        => CastConverter.Convert("alphabet", "nvarchar(3)").Should().Be("alp");

    [Theory]
    [InlineData(123)]
    [InlineData(-7)]
    public void WholeNumberThatDoesNotFitNVarCharN_IsAnError(int value)
    {
        var act = () => CastConverter.Convert(value, "nvarchar(1)");

        act.Should().Throw<OverflowException>();
    }

    [Fact]
    public void WholeNumberThatDoesNotFitVarCharN_IsAnAsterisk_AsInTsql()
        => CastConverter.Convert(123, "varchar(2)").Should().Be("*");

    [Fact]
    public void WholeNumberThatFits_IsItsText()
        => CastConverter.Convert(-7, "nvarchar(2)").Should().Be("-7");

    // ---- decimal(p, s) -----------------------------------------------------------------------------------

    [Theory]
    [InlineData("10.555", "decimal(10, 2)", "10.56")]
    [InlineData("-10.555", "decimal(10, 2)", "-10.56")]
    [InlineData("40.25", "decimal(3, 1)", "40.3")]
    [InlineData("9.99", "decimal(3, 2)", "9.99")]
    public void Decimal_IsRoundedToTheScale_HalfAwayFromZero(string value, string type, string expected)
        => ((decimal)CastConverter.Convert(decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture), type)!)
            .Should().Be(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture));

    [Theory]
    [InlineData("10.5", "decimal(3, 2)")]
    [InlineData("-10.5", "decimal(3, 2)")]
    [InlineData("9.995", "decimal(3, 2)")] // rounds to 10.00, which does not fit
    [InlineData("100", "decimal(4, 2)")]
    public void DecimalThatDoesNotFitPrecision_IsAnOverflow(string value, string type)
    {
        var act = () => CastConverter.Convert(decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture), type);

        act.Should().Throw<OverflowException>();
    }

    [Theory]
    [InlineData("10.5", "decimal(3, 2)")]
    [InlineData("9.995", "decimal(3, 2)")]
    public void TryConvert_GivesNull_WhereTsqlOverflows(string value, string type)
        => CastConverter.TryConvert(decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture), type).Should().BeNull();

    // B-F1 (round 2): a precision the engine cannot compute exactly is an engine limitation, which must never become NULL.
    [Theory]
    [InlineData("decimal(38, 30)")]
    [InlineData("decimal(30, 29)")]
    [InlineData("decimal(29, 2)")]
    public void TryConvert_ToAPrecisionAbove28_IsAnError_NotNull(string type)
    {
        var act = () => CastConverter.TryConvert(10.55m, type);

        act.Should().Throw<NotSupportedException>();
    }

    [Theory]
    [InlineData("10.55", "decimal(28, 26)", "10.55")]
    [InlineData("0.5", "decimal(28, 28)", "0.5")]
    [InlineData("123456789012345678901234567", "decimal(28, 0)", "123456789012345678901234567")]
    public void Decimal28_IsComputedExactly(string value, string type, string expected)
        => ((decimal)CastConverter.Convert(decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture), type)!)
            .Should().Be(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture));

    [Fact]
    public void TryConvert_ToInt_GivesNull_OutsideTheIntRange()
        => CastConverter.TryConvert(3000000000m, "int").Should().BeNull();

    // ---- int arithmetic ---------------------------------------------------------------------------------

    [Theory]
    [InlineData(BinaryExpressionType.Multiply, 5000, 1000000)]
    [InlineData(BinaryExpressionType.Add, int.MaxValue, 1)]
    [InlineData(BinaryExpressionType.Subtract, int.MinValue, 1)]
    public void IntArithmeticThatOverflows_IsAnError_NotAWiderValue(BinaryExpressionType op, int left, int right)
    {
        var act = () => ExpressionCompiler.EvaluateArithmetic(left, op, right);

        act.Should().Throw<OverflowException>();
    }

    [Fact]
    public void IntArithmeticThatFits_IsAnInt()
        => ExpressionCompiler.EvaluateArithmetic(5000, BinaryExpressionType.Multiply, 1000).Should().Be(5000000);

    // B-F2 (round 2): int / int and int % int are int in T-SQL, so a later operation that overflows int is an error.
    [Theory]
    [InlineData(BinaryExpressionType.Divide, 7, 2, 3)]
    [InlineData(BinaryExpressionType.Divide, -7, 2, -3)]
    [InlineData(BinaryExpressionType.Modulo, 7, 2, 1)]
    [InlineData(BinaryExpressionType.Modulo, -7, 2, -1)]
    [InlineData(BinaryExpressionType.Modulo, int.MinValue, -1, 0)]
    public void IntDivisionAndModulo_AreInts(BinaryExpressionType op, int left, int right, int expected)
        => ExpressionCompiler.EvaluateArithmetic(left, op, right).Should().BeOfType<int>().And.Be(expected);

    [Fact]
    public void IntDivision_ThenAnOverflowingMultiplication_IsAnError()
    {
        var act = () => ExpressionCompiler.EvaluateArithmetic(
            ExpressionCompiler.EvaluateArithmetic(100000, BinaryExpressionType.Divide, 2)!, BinaryExpressionType.Multiply, 100000);

        act.Should().Throw<OverflowException>();
    }

    [Fact]
    public void IntMinValueDividedByMinusOne_IsAnOverflow()
    {
        var act = () => ExpressionCompiler.EvaluateArithmetic(int.MinValue, BinaryExpressionType.Divide, -1);

        act.Should().Throw<OverflowException>();
    }

    [Fact]
    public void NegatingTheSmallestInt_IsAnOverflow()
    {
        var act = () => ExpressionCompiler.NegateValue(int.MinValue);

        act.Should().Throw<OverflowException>();
    }

    [Fact]
    public void NegatingTheSmallestBigInt_IsAnOverflow()
    {
        var act = () => ExpressionCompiler.NegateValue(long.MinValue);

        act.Should().Throw<OverflowException>();
    }

    [Fact]
    public void BigIntArithmeticThatOverflows_IsAnError()
    {
        var act = () => ExpressionCompiler.EvaluateArithmetic(long.MaxValue, BinaryExpressionType.Add, 1L);

        act.Should().Throw<OverflowException>();
    }

    // ---- LIKE: begins-with / ends-with only for plain text ----------------------------------------------

    [Theory]
    [InlineData("al%", "begins-with", "al")]
    [InlineData("%a", "ends-with", "a")]
    [InlineData("a_c%", "like", "a_c%")]
    [InlineData("%l_ha", "like", "%l_ha")]
    [InlineData("ab%cd%", "like", "ab%cd%")]
    [InlineData("%a%b", "like", "%a%b")]
    [InlineData("%pad%", "like", "%pad%")]
    [InlineData("%", "like", "%")]
    [InlineData("trail", "like", "trail")]
    public void LikePattern_IsSentWholeUnlessTheRestIsPlainText(string pattern, string op, string value)
    {
        FetchXmlGenerator.LikeCondition(pattern, negated: false).Should().Be((op, value));
        FetchXmlGenerator.LikeCondition(pattern, negated: true).Operator.Should().Be(op switch
        {
            "begins-with" => "not-begin-with",
            "ends-with" => "not-end-with",
            _ => "not-like"
        });
    }
}
