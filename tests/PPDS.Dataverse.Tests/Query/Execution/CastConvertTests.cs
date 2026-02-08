using System;
using System.Collections.Generic;
using PPDS.Dataverse.Query;
using PPDS.Dataverse.Query.Execution;
using PPDS.Dataverse.Query.Execution.Functions;
using PPDS.Dataverse.Sql.Ast;
using PPDS.Dataverse.Sql.Parsing;
using Xunit;

namespace PPDS.Dataverse.Tests.Query.Execution;

[Trait("Category", "TuiUnit")]
public class CastConvertTests
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

    #region Parser Tests - CAST

    [Fact]
    public void Parse_CastIntSimple()
    {
        var stmt = SqlParser.Parse("SELECT CAST(revenue AS int) FROM account");
        var col = Assert.IsType<SqlComputedColumn>(stmt.Columns[0]);
        var cast = Assert.IsType<SqlCastExpression>(col.Expression);
        Assert.IsType<SqlColumnExpression>(cast.Expression);
        Assert.Equal("int", cast.TargetType);
        Assert.Null(cast.Style);
    }

    [Fact]
    public void Parse_CastNvarcharWithLength()
    {
        var stmt = SqlParser.Parse("SELECT CAST(amount AS nvarchar(100)) FROM account");
        var col = Assert.IsType<SqlComputedColumn>(stmt.Columns[0]);
        var cast = Assert.IsType<SqlCastExpression>(col.Expression);
        Assert.Equal("nvarchar(100)", cast.TargetType);
    }

    [Fact]
    public void Parse_CastDecimalWithPrecisionAndScale()
    {
        var stmt = SqlParser.Parse("SELECT CAST(price AS decimal(18,2)) FROM product");
        var col = Assert.IsType<SqlComputedColumn>(stmt.Columns[0]);
        var cast = Assert.IsType<SqlCastExpression>(col.Expression);
        Assert.Equal("decimal(18,2)", cast.TargetType);
    }

    [Fact]
    public void Parse_CastWithStringLiteral()
    {
        var stmt = SqlParser.Parse("SELECT CAST('42' AS int) FROM account");
        var col = Assert.IsType<SqlComputedColumn>(stmt.Columns[0]);
        var cast = Assert.IsType<SqlCastExpression>(col.Expression);
        var lit = Assert.IsType<SqlLiteralExpression>(cast.Expression);
        Assert.Equal("42", lit.Value.Value);
        Assert.Equal("int", cast.TargetType);
    }

    [Fact]
    public void Parse_CastWithExpression()
    {
        var stmt = SqlParser.Parse("SELECT CAST(revenue * 0.1 AS int) FROM account");
        var col = Assert.IsType<SqlComputedColumn>(stmt.Columns[0]);
        var cast = Assert.IsType<SqlCastExpression>(col.Expression);
        Assert.IsType<SqlBinaryExpression>(cast.Expression);
        Assert.Equal("int", cast.TargetType);
    }

    [Fact]
    public void Parse_CastWithAlias()
    {
        var stmt = SqlParser.Parse("SELECT CAST(revenue AS nvarchar) AS rev_text FROM account");
        var col = Assert.IsType<SqlComputedColumn>(stmt.Columns[0]);
        Assert.Equal("rev_text", col.Alias);
        var cast = Assert.IsType<SqlCastExpression>(col.Expression);
        Assert.Equal("nvarchar", cast.TargetType);
    }

    #endregion

    #region Parser Tests - CONVERT

    [Fact]
    public void Parse_ConvertSimple()
    {
        var stmt = SqlParser.Parse("SELECT CONVERT(int, revenue) FROM account");
        var col = Assert.IsType<SqlComputedColumn>(stmt.Columns[0]);
        var cast = Assert.IsType<SqlCastExpression>(col.Expression);
        Assert.Equal("int", cast.TargetType);
        Assert.Null(cast.Style);
    }

    [Fact]
    public void Parse_ConvertWithStyle()
    {
        var stmt = SqlParser.Parse("SELECT CONVERT(nvarchar, createdon, 120) FROM account");
        var col = Assert.IsType<SqlComputedColumn>(stmt.Columns[0]);
        var cast = Assert.IsType<SqlCastExpression>(col.Expression);
        Assert.Equal("nvarchar", cast.TargetType);
        Assert.Equal(120, cast.Style);
    }

    [Fact]
    public void Parse_ConvertWithParameterizedType()
    {
        var stmt = SqlParser.Parse("SELECT CONVERT(nvarchar(50), revenue) FROM account");
        var col = Assert.IsType<SqlComputedColumn>(stmt.Columns[0]);
        var cast = Assert.IsType<SqlCastExpression>(col.Expression);
        Assert.Equal("nvarchar(50)", cast.TargetType);
    }

    #endregion

    #region Lexer Tests

    [Fact]
    public void Lexer_RecognizesCastKeyword()
    {
        var lexer = new SqlLexer("CAST");
        var result = lexer.Tokenize();
        Assert.Equal(SqlTokenType.Cast, result.Tokens[0].Type);
    }

    [Fact]
    public void Lexer_RecognizesConvertKeyword()
    {
        var lexer = new SqlLexer("CONVERT");
        var result = lexer.Tokenize();
        Assert.Equal(SqlTokenType.Convert, result.Tokens[0].Type);
    }

    [Fact]
    public void Lexer_CastIsCaseInsensitive()
    {
        var lexer = new SqlLexer("cast");
        var result = lexer.Tokenize();
        Assert.Equal(SqlTokenType.Cast, result.Tokens[0].Type);
    }

    #endregion

    #region CastConverter - int

    [Fact]
    public void CastToInt_FromDecimal_Truncates()
    {
        var result = CastConverter.Convert(3.7m, "int");
        Assert.Equal(3, result);
    }

    [Fact]
    public void CastToInt_FromString()
    {
        var result = CastConverter.Convert("42", "int");
        Assert.Equal(42, result);
    }

    [Fact]
    public void CastToInt_FromBool_True()
    {
        var result = CastConverter.Convert(true, "int");
        Assert.Equal(1, result);
    }

    [Fact]
    public void CastToInt_FromBool_False()
    {
        var result = CastConverter.Convert(false, "int");
        Assert.Equal(0, result);
    }

    [Fact]
    public void CastToInt_FromLong()
    {
        var result = CastConverter.Convert(42L, "int");
        Assert.Equal(42, result);
    }

    [Fact]
    public void CastToInt_FromDouble_Truncates()
    {
        var result = CastConverter.Convert(9.9, "int");
        Assert.Equal(9, result);
    }

    [Fact]
    public void CastToInt_NullReturnsNull()
    {
        var result = CastConverter.Convert(null, "int");
        Assert.Null(result);
    }

    [Fact]
    public void CastToInt_Overflow_Throws()
    {
        Assert.Throws<OverflowException>(() => CastConverter.Convert(3000000000L, "int"));
    }

    #endregion

    #region CastConverter - bigint

    [Fact]
    public void CastToBigInt_FromInt()
    {
        var result = CastConverter.Convert(42, "bigint");
        Assert.Equal(42L, result);
    }

    [Fact]
    public void CastToBigInt_FromString()
    {
        var result = CastConverter.Convert("9999999999", "bigint");
        Assert.Equal(9999999999L, result);
    }

    [Fact]
    public void CastToBigInt_FromDecimal_Truncates()
    {
        var result = CastConverter.Convert(123.456m, "bigint");
        Assert.Equal(123L, result);
    }

    #endregion

    #region CastConverter - decimal

    [Fact]
    public void CastToDecimal_FromInt()
    {
        var result = CastConverter.Convert(42, "decimal(10,2)");
        Assert.Equal(42.00m, result);
    }

    [Fact]
    public void CastToDecimal_FromString()
    {
        var result = CastConverter.Convert("3.14159", "decimal(10,2)");
        Assert.Equal(3.14m, result);
    }

    [Fact]
    public void CastToDecimal_FromDouble()
    {
        var result = CastConverter.Convert(1.5, "decimal");
        Assert.Equal(1.5m, result);
    }

    [Fact]
    public void CastToDecimal_ScaleRounding()
    {
        var result = CastConverter.Convert(1.999m, "decimal(5,2)");
        Assert.Equal(2.00m, result);
    }

    #endregion

    #region CastConverter - float

    [Fact]
    public void CastToFloat_FromInt()
    {
        var result = CastConverter.Convert(42, "float");
        Assert.Equal(42.0, result);
    }

    [Fact]
    public void CastToFloat_FromString()
    {
        var result = CastConverter.Convert("3.14", "float");
        Assert.Equal(3.14, result);
    }

    [Fact]
    public void CastToFloat_FromDecimal()
    {
        var result = CastConverter.Convert(1.5m, "float");
        Assert.Equal(1.5, result);
    }

    #endregion

    #region CastConverter - nvarchar / varchar

    [Fact]
    public void CastToNvarchar_FromInt()
    {
        var result = CastConverter.Convert(42, "nvarchar");
        Assert.Equal("42", result);
    }

    [Fact]
    public void CastToNvarchar_FromDecimal()
    {
        var result = CastConverter.Convert(3.14m, "nvarchar");
        Assert.Equal("3.14", result);
    }

    [Fact]
    public void CastToNvarchar_WithMaxLength_Truncates()
    {
        var result = CastConverter.Convert("Hello World", "nvarchar(5)");
        Assert.Equal("Hello", result);
    }

    [Fact]
    public void CastToNvarchar_FromBool()
    {
        Assert.Equal("1", CastConverter.Convert(true, "nvarchar"));
        Assert.Equal("0", CastConverter.Convert(false, "nvarchar"));
    }

    [Fact]
    public void CastToNvarchar_FromGuid()
    {
        var guid = Guid.Parse("12345678-1234-1234-1234-123456789abc");
        var result = CastConverter.Convert(guid, "nvarchar");
        Assert.Equal("12345678-1234-1234-1234-123456789ABC", result);
    }

    [Fact]
    public void CastToVarchar_FromInt()
    {
        var result = CastConverter.Convert(42, "varchar");
        Assert.Equal("42", result);
    }

    #endregion

    #region CastConverter - datetime

    [Fact]
    public void CastToDatetime_FromString()
    {
        var result = CastConverter.Convert("2024-01-15 10:30:00", "datetime");
        Assert.IsType<DateTime>(result);
        var dt = (DateTime)result;
        Assert.Equal(2024, dt.Year);
        Assert.Equal(1, dt.Month);
        Assert.Equal(15, dt.Day);
        Assert.Equal(10, dt.Hour);
        Assert.Equal(30, dt.Minute);
    }

    [Fact]
    public void CastToDatetime_FromDatetime_ReturnsOriginal()
    {
        var original = new DateTime(2024, 6, 15, 12, 0, 0);
        var result = CastConverter.Convert(original, "datetime");
        Assert.Equal(original, result);
    }

    [Fact]
    public void CastToDatetime_FromInt_Throws()
    {
        Assert.Throws<InvalidCastException>(() => CastConverter.Convert(42, "datetime"));
    }

    #endregion

    #region CastConverter - date

    [Fact]
    public void CastToDate_FromDatetime_StripsTime()
    {
        var original = new DateTime(2024, 6, 15, 14, 30, 0);
        var result = CastConverter.Convert(original, "date");
        Assert.IsType<DateTime>(result);
        var dt = (DateTime)result;
        Assert.Equal(new DateTime(2024, 6, 15), dt);
    }

    [Fact]
    public void CastToDate_FromString()
    {
        var result = CastConverter.Convert("2024-06-15", "date");
        Assert.IsType<DateTime>(result);
        var dt = (DateTime)result;
        Assert.Equal(2024, dt.Year);
        Assert.Equal(6, dt.Month);
        Assert.Equal(15, dt.Day);
        Assert.Equal(0, dt.Hour);
    }

    #endregion

    #region CastConverter - bit

    [Fact]
    public void CastToBit_FromInt_NonZero()
    {
        Assert.Equal(true, CastConverter.Convert(1, "bit"));
        Assert.Equal(true, CastConverter.Convert(42, "bit"));
    }

    [Fact]
    public void CastToBit_FromInt_Zero()
    {
        Assert.Equal(false, CastConverter.Convert(0, "bit"));
    }

    [Fact]
    public void CastToBit_FromString()
    {
        Assert.Equal(true, CastConverter.Convert("1", "bit"));
        Assert.Equal(false, CastConverter.Convert("0", "bit"));
        Assert.Equal(true, CastConverter.Convert("true", "bit"));
        Assert.Equal(false, CastConverter.Convert("false", "bit"));
    }

    [Fact]
    public void CastToBit_FromDecimal()
    {
        Assert.Equal(true, CastConverter.Convert(1.5m, "bit"));
        Assert.Equal(false, CastConverter.Convert(0m, "bit"));
    }

    [Fact]
    public void CastToBit_FromBool()
    {
        Assert.Equal(true, CastConverter.Convert(true, "bit"));
        Assert.Equal(false, CastConverter.Convert(false, "bit"));
    }

    #endregion

    #region CastConverter - uniqueidentifier

    [Fact]
    public void CastToGuid_FromString()
    {
        var result = CastConverter.Convert("12345678-1234-1234-1234-123456789abc", "uniqueidentifier");
        Assert.IsType<Guid>(result);
        Assert.Equal(Guid.Parse("12345678-1234-1234-1234-123456789abc"), result);
    }

    [Fact]
    public void CastToGuid_FromGuid()
    {
        var guid = Guid.NewGuid();
        var result = CastConverter.Convert(guid, "uniqueidentifier");
        Assert.Equal(guid, result);
    }

    [Fact]
    public void CastToGuid_FromInt_Throws()
    {
        Assert.Throws<InvalidCastException>(() => CastConverter.Convert(42, "uniqueidentifier"));
    }

    #endregion

    #region CastConverter - money

    [Fact]
    public void CastToMoney_FromInt()
    {
        var result = CastConverter.Convert(42, "money");
        Assert.Equal(42.0000m, result);
    }

    [Fact]
    public void CastToMoney_FromDecimal_Rounds4Places()
    {
        var result = CastConverter.Convert(99.99999m, "money");
        Assert.Equal(100.0000m, result);
    }

    [Fact]
    public void CastToMoney_FromString()
    {
        var result = CastConverter.Convert("123.45", "money");
        Assert.Equal(123.4500m, result);
    }

    #endregion

    #region CastConverter - CONVERT style codes

    [Fact]
    public void ConvertToNvarchar_DatetimeWithStyle120()
    {
        var dt = new DateTime(2024, 3, 15, 14, 30, 0);
        var result = CastConverter.Convert(dt, "nvarchar", 120);
        Assert.Equal("2024-03-15 14:30:00", result);
    }

    [Fact]
    public void ConvertToNvarchar_DatetimeWithStyle101()
    {
        var dt = new DateTime(2024, 3, 15, 14, 30, 0);
        var result = CastConverter.Convert(dt, "nvarchar", 101);
        Assert.Equal("03/15/2024", result);
    }

    [Fact]
    public void ConvertToNvarchar_DatetimeWithStyle103()
    {
        var dt = new DateTime(2024, 3, 15, 14, 30, 0);
        var result = CastConverter.Convert(dt, "nvarchar", 103);
        Assert.Equal("15/03/2024", result);
    }

    [Fact]
    public void ConvertToNvarchar_DatetimeWithStyle108()
    {
        var dt = new DateTime(2024, 3, 15, 14, 30, 45);
        var result = CastConverter.Convert(dt, "nvarchar", 108);
        Assert.Equal("14:30:45", result);
    }

    [Fact]
    public void ConvertToNvarchar_DatetimeWithStyle126_ISO8601()
    {
        var dt = new DateTime(2024, 3, 15, 14, 30, 45, 123);
        var result = CastConverter.Convert(dt, "nvarchar", 126);
        Assert.Equal("2024-03-15T14:30:45.123", result);
    }

    #endregion

    #region ExpressionEvaluator Integration

    [Fact]
    public void Evaluator_CastColumnToInt()
    {
        var cast = new SqlCastExpression(
            new SqlColumnExpression(SqlColumnRef.Simple("amount")),
            "int");

        var row = Row(("amount", 3.7m));
        var result = _eval.Evaluate(cast, row);
        Assert.Equal(3, result);
    }

    [Fact]
    public void Evaluator_CastLiteralToNvarchar()
    {
        var cast = new SqlCastExpression(
            new SqlLiteralExpression(SqlLiteral.Number("42")),
            "nvarchar");

        var row = Row();
        var result = _eval.Evaluate(cast, row);
        Assert.Equal("42", result);
    }

    [Fact]
    public void Evaluator_CastNullReturnsNull()
    {
        var cast = new SqlCastExpression(
            new SqlColumnExpression(SqlColumnRef.Simple("amount")),
            "int");

        var row = Row(("amount", (object?)null));
        var result = _eval.Evaluate(cast, row);
        Assert.Null(result);
    }

    [Fact]
    public void Evaluator_ConvertWithStyle()
    {
        var cast = new SqlCastExpression(
            new SqlColumnExpression(SqlColumnRef.Simple("createdon")),
            "nvarchar",
            120);

        var dt = new DateTime(2024, 3, 15, 14, 30, 0);
        var row = Row(("createdon", dt));
        var result = _eval.Evaluate(cast, row);
        Assert.Equal("2024-03-15 14:30:00", result);
    }

    [Fact]
    public void Evaluator_CastExpressionToDecimal()
    {
        // CAST(revenue * 0.1 AS decimal(10,2))
        var cast = new SqlCastExpression(
            new SqlBinaryExpression(
                new SqlColumnExpression(SqlColumnRef.Simple("revenue")),
                SqlBinaryOperator.Multiply,
                new SqlLiteralExpression(SqlLiteral.Number("0.1"))),
            "decimal(10,2)");

        var row = Row(("revenue", 1000m));
        var result = _eval.Evaluate(cast, row);
        Assert.Equal(100.00m, result);
    }

    #endregion

    #region Unsupported conversion pairs

    [Fact]
    public void CastToInt_FromDatetime_Throws()
    {
        Assert.Throws<InvalidCastException>(() =>
            CastConverter.Convert(new DateTime(2024, 1, 1), "int"));
    }

    [Fact]
    public void CastToInt_FromGuid_Throws()
    {
        Assert.Throws<InvalidCastException>(() =>
            CastConverter.Convert(Guid.NewGuid(), "int"));
    }

    [Fact]
    public void CastToDatetime_FromGuid_Throws()
    {
        Assert.Throws<InvalidCastException>(() =>
            CastConverter.Convert(Guid.NewGuid(), "datetime"));
    }

    [Fact]
    public void CastToBit_FromDatetime_Throws()
    {
        Assert.Throws<InvalidCastException>(() =>
            CastConverter.Convert(new DateTime(2024, 1, 1), "bit"));
    }

    [Fact]
    public void CastToUnsupportedType_Throws()
    {
        Assert.Throws<InvalidCastException>(() =>
            CastConverter.Convert(42, "xml"));
    }

    #endregion

    #region End-to-End: Parse + Evaluate

    [Fact]
    public void EndToEnd_CastInSelect()
    {
        var stmt = SqlParser.Parse("SELECT CAST(amount AS int) FROM account");
        var col = Assert.IsType<SqlComputedColumn>(stmt.Columns[0]);
        var cast = Assert.IsType<SqlCastExpression>(col.Expression);

        var row = Row(("amount", 99.7m));
        var result = _eval.Evaluate(cast, row);
        Assert.Equal(99, result);
    }

    [Fact]
    public void EndToEnd_ConvertInSelect()
    {
        var stmt = SqlParser.Parse("SELECT CONVERT(nvarchar, createdon, 120) FROM account");
        var col = Assert.IsType<SqlComputedColumn>(stmt.Columns[0]);
        var cast = Assert.IsType<SqlCastExpression>(col.Expression);

        var dt = new DateTime(2024, 6, 15, 10, 30, 0);
        var row = Row(("createdon", dt));
        var result = _eval.Evaluate(cast, row);
        Assert.Equal("2024-06-15 10:30:00", result);
    }

    #endregion
}
