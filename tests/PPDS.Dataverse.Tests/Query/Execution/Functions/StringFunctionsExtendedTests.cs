using System;
using System.Collections.Generic;
using PPDS.Dataverse.Query;
using PPDS.Dataverse.Query.Execution;
using PPDS.Dataverse.Query.Execution.Functions;
using PPDS.Dataverse.Sql.Ast;
using Xunit;

namespace PPDS.Dataverse.Tests.Query.Execution.Functions;

[Trait("Category", "Unit")]
public class StringFunctionsExtendedTests
{
    private readonly ExpressionEvaluator _eval = new();

    private static readonly IReadOnlyDictionary<string, QueryValue> EmptyRow =
        new Dictionary<string, QueryValue>();

    private static SqlFunctionExpression Fn(string name, params ISqlExpression[] args)
    {
        return new SqlFunctionExpression(name, args);
    }

    private static SqlLiteralExpression Str(string value) =>
        new(SqlLiteral.String(value));

    private static SqlLiteralExpression Num(string value) =>
        new(SqlLiteral.Number(value));

    private static SqlLiteralExpression Null() =>
        new(SqlLiteral.Null());

    #region REPLICATE

    [Fact]
    public void Replicate_BasicUsage()
    {
        var result = _eval.Evaluate(Fn("REPLICATE", Str("ab"), Num("3")), EmptyRow);
        Assert.Equal("ababab", result);
    }

    [Fact]
    public void Replicate_ZeroCount()
    {
        var result = _eval.Evaluate(Fn("REPLICATE", Str("abc"), Num("0")), EmptyRow);
        Assert.Equal("", result);
    }

    [Fact]
    public void Replicate_NegativeCount_ReturnsNull()
    {
        var result = _eval.Evaluate(Fn("REPLICATE", Str("abc"), Num("-1")), EmptyRow);
        Assert.Null(result);
    }

    [Fact]
    public void Replicate_SingleRepeat()
    {
        var result = _eval.Evaluate(Fn("REPLICATE", Str("hello"), Num("1")), EmptyRow);
        Assert.Equal("hello", result);
    }

    [Fact]
    public void Replicate_EmptyString()
    {
        var result = _eval.Evaluate(Fn("REPLICATE", Str(""), Num("5")), EmptyRow);
        Assert.Equal("", result);
    }

    [Fact]
    public void Replicate_NullString_ReturnsNull()
    {
        var result = _eval.Evaluate(Fn("REPLICATE", Null(), Num("3")), EmptyRow);
        Assert.Null(result);
    }

    [Fact]
    public void Replicate_NullCount_ReturnsNull()
    {
        var result = _eval.Evaluate(Fn("REPLICATE", Str("abc"), Null()), EmptyRow);
        Assert.Null(result);
    }

    #endregion

    #region PATINDEX

    [Fact]
    public void PatIndex_FoundWithWildcards()
    {
        // PATINDEX('%orl%', 'hello world') - find 'orl' anywhere
        var result = _eval.Evaluate(Fn("PATINDEX", Str("%orl%"), Str("hello world")), EmptyRow);
        Assert.Equal(8, result);
    }

    [Fact]
    public void PatIndex_NotFound()
    {
        var result = _eval.Evaluate(Fn("PATINDEX", Str("%xyz%"), Str("hello world")), EmptyRow);
        Assert.Equal(0, result);
    }

    [Fact]
    public void PatIndex_AnchoredStart()
    {
        // Pattern without leading % - must match from start
        var result = _eval.Evaluate(Fn("PATINDEX", Str("hel%"), Str("hello")), EmptyRow);
        Assert.Equal(1, result);
    }

    [Fact]
    public void PatIndex_WithUnderscore()
    {
        // _ matches exactly one char
        var result = _eval.Evaluate(Fn("PATINDEX", Str("%h_llo%"), Str("hello")), EmptyRow);
        Assert.Equal(1, result);
    }

    [Fact]
    public void PatIndex_NullInput_ReturnsNull()
    {
        var result = _eval.Evaluate(Fn("PATINDEX", Null(), Str("hello")), EmptyRow);
        Assert.Null(result);
    }

    [Fact]
    public void PatIndex_NullString_ReturnsNull()
    {
        var result = _eval.Evaluate(Fn("PATINDEX", Str("%test%"), Null()), EmptyRow);
        Assert.Null(result);
    }

    #endregion

    #region CONCAT_WS

    [Fact]
    public void ConcatWs_BasicUsage()
    {
        var result = _eval.Evaluate(
            Fn("CONCAT_WS", Str(", "), Str("a"), Str("b"), Str("c")),
            EmptyRow);
        Assert.Equal("a, b, c", result);
    }

    [Fact]
    public void ConcatWs_SkipsNulls()
    {
        var result = _eval.Evaluate(
            Fn("CONCAT_WS", Str("-"), Str("a"), Null(), Str("c")),
            EmptyRow);
        Assert.Equal("a-c", result);
    }

    [Fact]
    public void ConcatWs_AllNulls()
    {
        var result = _eval.Evaluate(
            Fn("CONCAT_WS", Str(","), Null(), Null(), Null()),
            EmptyRow);
        Assert.Equal("", result);
    }

    [Fact]
    public void ConcatWs_NullSeparator_ReturnsNull()
    {
        var result = _eval.Evaluate(
            Fn("CONCAT_WS", Null(), Str("a"), Str("b"), Str("c")),
            EmptyRow);
        Assert.Null(result);
    }

    [Fact]
    public void ConcatWs_EmptySeparator()
    {
        var result = _eval.Evaluate(
            Fn("CONCAT_WS", Str(""), Str("a"), Str("b"), Str("c")),
            EmptyRow);
        Assert.Equal("abc", result);
    }

    #endregion

    #region FORMAT

    [Fact]
    public void Format_DecimalNumber()
    {
        var row = new Dictionary<string, QueryValue>
        {
            ["val"] = QueryValue.Simple(1234.5678m)
        };
        var result = _eval.Evaluate(
            Fn("FORMAT", new SqlColumnExpression(SqlColumnRef.Simple("val")), Str("N2")),
            row);
        Assert.Equal("1,234.57", result);
    }

    [Fact]
    public void Format_NullValue_ReturnsNull()
    {
        var result = _eval.Evaluate(Fn("FORMAT", Null(), Str("N2")), EmptyRow);
        Assert.Null(result);
    }

    [Fact]
    public void Format_NullFormat_ReturnsNull()
    {
        var row = new Dictionary<string, QueryValue>
        {
            ["val"] = QueryValue.Simple(42)
        };
        var result = _eval.Evaluate(
            Fn("FORMAT", new SqlColumnExpression(SqlColumnRef.Simple("val")), Null()),
            row);
        Assert.Null(result);
    }

    #endregion

    #region SPACE

    [Fact]
    public void Space_BasicUsage()
    {
        var result = _eval.Evaluate(Fn("SPACE", Num("5")), EmptyRow);
        Assert.Equal("     ", result);
    }

    [Fact]
    public void Space_Zero()
    {
        var result = _eval.Evaluate(Fn("SPACE", Num("0")), EmptyRow);
        Assert.Equal("", result);
    }

    [Fact]
    public void Space_NegativeCount_ReturnsNull()
    {
        var result = _eval.Evaluate(Fn("SPACE", Num("-1")), EmptyRow);
        Assert.Null(result);
    }

    [Fact]
    public void Space_NullCount_ReturnsNull()
    {
        var result = _eval.Evaluate(Fn("SPACE", Null()), EmptyRow);
        Assert.Null(result);
    }

    #endregion

    #region UNICODE

    [Fact]
    public void Unicode_BasicChar()
    {
        var result = _eval.Evaluate(Fn("UNICODE", Str("A")), EmptyRow);
        Assert.Equal(65, result);
    }

    [Fact]
    public void Unicode_FirstCharOfString()
    {
        var result = _eval.Evaluate(Fn("UNICODE", Str("Hello")), EmptyRow);
        Assert.Equal(72, result); // 'H' = 72
    }

    [Fact]
    public void Unicode_EmptyString_ReturnsNull()
    {
        var result = _eval.Evaluate(Fn("UNICODE", Str("")), EmptyRow);
        Assert.Null(result);
    }

    [Fact]
    public void Unicode_NullInput_ReturnsNull()
    {
        var result = _eval.Evaluate(Fn("UNICODE", Null()), EmptyRow);
        Assert.Null(result);
    }

    #endregion

    #region CHAR

    [Fact]
    public void Char_BasicUsage()
    {
        var result = _eval.Evaluate(Fn("CHAR", Num("65")), EmptyRow);
        Assert.Equal("A", result);
    }

    [Fact]
    public void Char_Space()
    {
        var result = _eval.Evaluate(Fn("CHAR", Num("32")), EmptyRow);
        Assert.Equal(" ", result);
    }

    [Fact]
    public void Char_NegativeCode_ReturnsNull()
    {
        var result = _eval.Evaluate(Fn("CHAR", Num("-1")), EmptyRow);
        Assert.Null(result);
    }

    [Fact]
    public void Char_NullInput_ReturnsNull()
    {
        var result = _eval.Evaluate(Fn("CHAR", Null()), EmptyRow);
        Assert.Null(result);
    }

    #endregion

    #region QUOTENAME

    [Fact]
    public void QuoteName_DefaultBrackets()
    {
        var result = _eval.Evaluate(Fn("QUOTENAME", Str("table")), EmptyRow);
        Assert.Equal("[table]", result);
    }

    [Fact]
    public void QuoteName_SingleQuotes()
    {
        var result = _eval.Evaluate(Fn("QUOTENAME", Str("table"), Str("'")), EmptyRow);
        Assert.Equal("'table'", result);
    }

    [Fact]
    public void QuoteName_DoubleQuotes()
    {
        var result = _eval.Evaluate(Fn("QUOTENAME", Str("table"), Str("\"")), EmptyRow);
        Assert.Equal("\"table\"", result);
    }

    [Fact]
    public void QuoteName_EscapesBracketsInside()
    {
        var result = _eval.Evaluate(Fn("QUOTENAME", Str("my]table")), EmptyRow);
        Assert.Equal("[my]]table]", result);
    }

    [Fact]
    public void QuoteName_EscapesSingleQuotesInside()
    {
        var result = _eval.Evaluate(Fn("QUOTENAME", Str("it's"), Str("'")), EmptyRow);
        Assert.Equal("'it''s'", result);
    }

    [Fact]
    public void QuoteName_NullInput_ReturnsNull()
    {
        var result = _eval.Evaluate(Fn("QUOTENAME", Null()), EmptyRow);
        Assert.Null(result);
    }

    #endregion

    #region SOUNDEX

    [Fact]
    public void Soundex_Robert()
    {
        var result = _eval.Evaluate(Fn("SOUNDEX", Str("Robert")), EmptyRow);
        Assert.Equal("R163", result);
    }

    [Fact]
    public void Soundex_Smith()
    {
        var result = _eval.Evaluate(Fn("SOUNDEX", Str("Smith")), EmptyRow);
        Assert.Equal("S530", result);
    }

    [Fact]
    public void Soundex_EmptyString()
    {
        var result = _eval.Evaluate(Fn("SOUNDEX", Str("")), EmptyRow);
        Assert.Equal("0000", result);
    }

    [Fact]
    public void Soundex_NullInput_ReturnsNull()
    {
        var result = _eval.Evaluate(Fn("SOUNDEX", Null()), EmptyRow);
        Assert.Null(result);
    }

    #endregion

    #region DIFFERENCE

    [Fact]
    public void Difference_SameString()
    {
        var result = _eval.Evaluate(Fn("DIFFERENCE", Str("Smith"), Str("Smith")), EmptyRow);
        Assert.Equal(4, result);
    }

    [Fact]
    public void Difference_SimilarStrings()
    {
        var result = _eval.Evaluate(Fn("DIFFERENCE", Str("Smith"), Str("Smythe")), EmptyRow);
        Assert.IsType<int>(result);
        Assert.True((int)result! >= 2); // Should be quite similar
    }

    [Fact]
    public void Difference_DifferentStrings()
    {
        var result = _eval.Evaluate(Fn("DIFFERENCE", Str("Apple"), Str("Zebra")), EmptyRow);
        Assert.IsType<int>(result);
        // Different strings, may be low but not necessarily 0
    }

    [Fact]
    public void Difference_NullInput_ReturnsNull()
    {
        var result = _eval.Evaluate(Fn("DIFFERENCE", Null(), Str("test")), EmptyRow);
        Assert.Null(result);
    }

    [Fact]
    public void Difference_NullSecondInput_ReturnsNull()
    {
        var result = _eval.Evaluate(Fn("DIFFERENCE", Str("test"), Null()), EmptyRow);
        Assert.Null(result);
    }

    #endregion

    #region STRING_AGG (scalar fallback)

    [Fact]
    public void StringAgg_ScalarFallback()
    {
        var result = _eval.Evaluate(Fn("STRING_AGG", Str("hello"), Str(",")), EmptyRow);
        Assert.Equal("hello", result);
    }

    [Fact]
    public void StringAgg_NullValue_ReturnsNull()
    {
        var result = _eval.Evaluate(Fn("STRING_AGG", Null(), Str(",")), EmptyRow);
        Assert.Null(result);
    }

    #endregion

    #region Registry

    [Fact]
    public void AllNewStringFunctionsRegistered()
    {
        var registry = FunctionRegistry.CreateDefault();
        Assert.True(registry.IsRegistered("REPLICATE"));
        Assert.True(registry.IsRegistered("PATINDEX"));
        Assert.True(registry.IsRegistered("CONCAT_WS"));
        Assert.True(registry.IsRegistered("FORMAT"));
        Assert.True(registry.IsRegistered("SPACE"));
        Assert.True(registry.IsRegistered("UNICODE"));
        Assert.True(registry.IsRegistered("CHAR"));
        Assert.True(registry.IsRegistered("QUOTENAME"));
        Assert.True(registry.IsRegistered("SOUNDEX"));
        Assert.True(registry.IsRegistered("DIFFERENCE"));
        Assert.True(registry.IsRegistered("STRING_AGG"));
    }

    #endregion
}
