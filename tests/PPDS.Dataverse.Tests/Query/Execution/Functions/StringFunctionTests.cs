using System;
using System.Collections.Generic;
using PPDS.Dataverse.Query;
using PPDS.Dataverse.Query.Execution;
using PPDS.Dataverse.Query.Execution.Functions;
using PPDS.Dataverse.Sql.Ast;
using Xunit;

namespace PPDS.Dataverse.Tests.Query.Execution.Functions;

[Trait("Category", "TuiUnit")]
public class StringFunctionTests
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

    /// <summary>Helper: creates a function expression with literal string arguments.</summary>
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

    #region UPPER

    [Fact]
    public void Upper_BasicString()
    {
        var result = _eval.Evaluate(Fn("UPPER", Str("hello")), EmptyRow);
        Assert.Equal("HELLO", result);
    }

    [Fact]
    public void Upper_MixedCase()
    {
        var result = _eval.Evaluate(Fn("UPPER", Str("Hello World")), EmptyRow);
        Assert.Equal("HELLO WORLD", result);
    }

    [Fact]
    public void Upper_AlreadyUpperCase()
    {
        var result = _eval.Evaluate(Fn("UPPER", Str("ABC")), EmptyRow);
        Assert.Equal("ABC", result);
    }

    [Fact]
    public void Upper_Null_ReturnsNull()
    {
        var result = _eval.Evaluate(Fn("UPPER", Null()), EmptyRow);
        Assert.Null(result);
    }

    [Fact]
    public void Upper_EmptyString()
    {
        var result = _eval.Evaluate(Fn("UPPER", Str("")), EmptyRow);
        Assert.Equal("", result);
    }

    [Fact]
    public void Upper_CaseInsensitiveFunctionName()
    {
        var result = _eval.Evaluate(Fn("upper", Str("hello")), EmptyRow);
        Assert.Equal("HELLO", result);
    }

    [Fact]
    public void Upper_WithColumnValue()
    {
        var row = Row(("name", "contoso"));
        var result = _eval.Evaluate(
            Fn("UPPER", new SqlColumnExpression(SqlColumnRef.Simple("name"))),
            row);
        Assert.Equal("CONTOSO", result);
    }

    #endregion

    #region LOWER

    [Fact]
    public void Lower_BasicString()
    {
        var result = _eval.Evaluate(Fn("LOWER", Str("HELLO")), EmptyRow);
        Assert.Equal("hello", result);
    }

    [Fact]
    public void Lower_MixedCase()
    {
        var result = _eval.Evaluate(Fn("LOWER", Str("Hello World")), EmptyRow);
        Assert.Equal("hello world", result);
    }

    [Fact]
    public void Lower_Null_ReturnsNull()
    {
        var result = _eval.Evaluate(Fn("LOWER", Null()), EmptyRow);
        Assert.Null(result);
    }

    [Fact]
    public void Lower_EmptyString()
    {
        var result = _eval.Evaluate(Fn("LOWER", Str("")), EmptyRow);
        Assert.Equal("", result);
    }

    #endregion

    #region LEN

    [Fact]
    public void Len_BasicString()
    {
        var result = _eval.Evaluate(Fn("LEN", Str("hello")), EmptyRow);
        Assert.Equal(5, result);
    }

    [Fact]
    public void Len_TrailingSpaces_Excluded()
    {
        // T-SQL LEN excludes trailing spaces
        var result = _eval.Evaluate(Fn("LEN", Str("hello   ")), EmptyRow);
        Assert.Equal(5, result);
    }

    [Fact]
    public void Len_LeadingSpaces_Included()
    {
        var result = _eval.Evaluate(Fn("LEN", Str("  hello")), EmptyRow);
        Assert.Equal(7, result);
    }

    [Fact]
    public void Len_EmptyString()
    {
        var result = _eval.Evaluate(Fn("LEN", Str("")), EmptyRow);
        Assert.Equal(0, result);
    }

    [Fact]
    public void Len_Null_ReturnsNull()
    {
        var result = _eval.Evaluate(Fn("LEN", Null()), EmptyRow);
        Assert.Null(result);
    }

    #endregion

    #region LEFT

    [Fact]
    public void Left_BasicUsage()
    {
        var result = _eval.Evaluate(Fn("LEFT", Str("hello world"), Num("5")), EmptyRow);
        Assert.Equal("hello", result);
    }

    [Fact]
    public void Left_LengthExceedsString()
    {
        var result = _eval.Evaluate(Fn("LEFT", Str("hi"), Num("10")), EmptyRow);
        Assert.Equal("hi", result);
    }

    [Fact]
    public void Left_Zero()
    {
        var result = _eval.Evaluate(Fn("LEFT", Str("hello"), Num("0")), EmptyRow);
        Assert.Equal("", result);
    }

    [Fact]
    public void Left_Null_ReturnsNull()
    {
        var result = _eval.Evaluate(Fn("LEFT", Null(), Num("3")), EmptyRow);
        Assert.Null(result);
    }

    [Fact]
    public void Left_NullLength_ReturnsNull()
    {
        var result = _eval.Evaluate(Fn("LEFT", Str("hello"), Null()), EmptyRow);
        Assert.Null(result);
    }

    #endregion

    #region RIGHT

    [Fact]
    public void Right_BasicUsage()
    {
        var result = _eval.Evaluate(Fn("RIGHT", Str("hello world"), Num("5")), EmptyRow);
        Assert.Equal("world", result);
    }

    [Fact]
    public void Right_LengthExceedsString()
    {
        var result = _eval.Evaluate(Fn("RIGHT", Str("hi"), Num("10")), EmptyRow);
        Assert.Equal("hi", result);
    }

    [Fact]
    public void Right_Zero()
    {
        var result = _eval.Evaluate(Fn("RIGHT", Str("hello"), Num("0")), EmptyRow);
        Assert.Equal("", result);
    }

    [Fact]
    public void Right_Null_ReturnsNull()
    {
        var result = _eval.Evaluate(Fn("RIGHT", Null(), Num("3")), EmptyRow);
        Assert.Null(result);
    }

    #endregion

    #region SUBSTRING

    [Fact]
    public void Substring_BasicUsage()
    {
        // SUBSTRING('hello', 2, 3) => 'ell' (1-based)
        var result = _eval.Evaluate(Fn("SUBSTRING", Str("hello"), Num("2"), Num("3")), EmptyRow);
        Assert.Equal("ell", result);
    }

    [Fact]
    public void Substring_FromStart()
    {
        var result = _eval.Evaluate(Fn("SUBSTRING", Str("hello"), Num("1"), Num("3")), EmptyRow);
        Assert.Equal("hel", result);
    }

    [Fact]
    public void Substring_LengthExceedsRemaining()
    {
        var result = _eval.Evaluate(Fn("SUBSTRING", Str("hello"), Num("3"), Num("100")), EmptyRow);
        Assert.Equal("llo", result);
    }

    [Fact]
    public void Substring_StartBeyondLength()
    {
        var result = _eval.Evaluate(Fn("SUBSTRING", Str("hello"), Num("10"), Num("3")), EmptyRow);
        Assert.Equal("", result);
    }

    [Fact]
    public void Substring_StartAtZero_AdjustsLength()
    {
        // T-SQL: SUBSTRING('hello', 0, 3) is like SUBSTRING('hello', 1, 2) => 'he'
        var result = _eval.Evaluate(Fn("SUBSTRING", Str("hello"), Num("0"), Num("3")), EmptyRow);
        Assert.Equal("he", result);
    }

    [Fact]
    public void Substring_Null_ReturnsNull()
    {
        var result = _eval.Evaluate(Fn("SUBSTRING", Null(), Num("1"), Num("3")), EmptyRow);
        Assert.Null(result);
    }

    #endregion

    #region TRIM

    [Fact]
    public void Trim_BothSides()
    {
        var result = _eval.Evaluate(Fn("TRIM", Str("  hello  ")), EmptyRow);
        Assert.Equal("hello", result);
    }

    [Fact]
    public void Trim_NoWhitespace()
    {
        var result = _eval.Evaluate(Fn("TRIM", Str("hello")), EmptyRow);
        Assert.Equal("hello", result);
    }

    [Fact]
    public void Trim_Null_ReturnsNull()
    {
        var result = _eval.Evaluate(Fn("TRIM", Null()), EmptyRow);
        Assert.Null(result);
    }

    #endregion

    #region LTRIM

    [Fact]
    public void Ltrim_LeadingSpaces()
    {
        var result = _eval.Evaluate(Fn("LTRIM", Str("  hello  ")), EmptyRow);
        Assert.Equal("hello  ", result);
    }

    [Fact]
    public void Ltrim_Null_ReturnsNull()
    {
        var result = _eval.Evaluate(Fn("LTRIM", Null()), EmptyRow);
        Assert.Null(result);
    }

    #endregion

    #region RTRIM

    [Fact]
    public void Rtrim_TrailingSpaces()
    {
        var result = _eval.Evaluate(Fn("RTRIM", Str("  hello  ")), EmptyRow);
        Assert.Equal("  hello", result);
    }

    [Fact]
    public void Rtrim_Null_ReturnsNull()
    {
        var result = _eval.Evaluate(Fn("RTRIM", Null()), EmptyRow);
        Assert.Null(result);
    }

    #endregion

    #region REPLACE

    [Fact]
    public void Replace_BasicUsage()
    {
        var result = _eval.Evaluate(
            Fn("REPLACE", Str("hello world"), Str("world"), Str("there")),
            EmptyRow);
        Assert.Equal("hello there", result);
    }

    [Fact]
    public void Replace_CaseInsensitive()
    {
        var result = _eval.Evaluate(
            Fn("REPLACE", Str("Hello WORLD"), Str("world"), Str("there")),
            EmptyRow);
        Assert.Equal("Hello there", result);
    }

    [Fact]
    public void Replace_MultipleOccurrences()
    {
        var result = _eval.Evaluate(
            Fn("REPLACE", Str("aaa"), Str("a"), Str("bb")),
            EmptyRow);
        Assert.Equal("bbbbbb", result);
    }

    [Fact]
    public void Replace_NotFound()
    {
        var result = _eval.Evaluate(
            Fn("REPLACE", Str("hello"), Str("xyz"), Str("abc")),
            EmptyRow);
        Assert.Equal("hello", result);
    }

    [Fact]
    public void Replace_EmptyFind_ReturnsOriginal()
    {
        var result = _eval.Evaluate(
            Fn("REPLACE", Str("hello"), Str(""), Str("x")),
            EmptyRow);
        Assert.Equal("hello", result);
    }

    [Fact]
    public void Replace_Null_ReturnsNull()
    {
        var result = _eval.Evaluate(
            Fn("REPLACE", Null(), Str("a"), Str("b")),
            EmptyRow);
        Assert.Null(result);
    }

    [Fact]
    public void Replace_NullFind_ReturnsNull()
    {
        var result = _eval.Evaluate(
            Fn("REPLACE", Str("hello"), Null(), Str("b")),
            EmptyRow);
        Assert.Null(result);
    }

    #endregion

    #region CHARINDEX

    [Fact]
    public void CharIndex_Found()
    {
        // CHARINDEX('lo', 'hello') => 4 (1-based)
        var result = _eval.Evaluate(Fn("CHARINDEX", Str("lo"), Str("hello")), EmptyRow);
        Assert.Equal(4, result);
    }

    [Fact]
    public void CharIndex_NotFound()
    {
        var result = _eval.Evaluate(Fn("CHARINDEX", Str("xyz"), Str("hello")), EmptyRow);
        Assert.Equal(0, result);
    }

    [Fact]
    public void CharIndex_CaseInsensitive()
    {
        var result = _eval.Evaluate(Fn("CHARINDEX", Str("LO"), Str("hello")), EmptyRow);
        Assert.Equal(4, result);
    }

    [Fact]
    public void CharIndex_WithStart()
    {
        // CHARINDEX('l', 'hello world', 5) => find 'l' starting at position 5
        var result = _eval.Evaluate(
            Fn("CHARINDEX", Str("l"), Str("hello world"), Num("5")),
            EmptyRow);
        Assert.Equal(10, result);
    }

    [Fact]
    public void CharIndex_WithStart_NotFound()
    {
        var result = _eval.Evaluate(
            Fn("CHARINDEX", Str("h"), Str("hello"), Num("3")),
            EmptyRow);
        Assert.Equal(0, result);
    }

    [Fact]
    public void CharIndex_AtBeginning()
    {
        var result = _eval.Evaluate(Fn("CHARINDEX", Str("he"), Str("hello")), EmptyRow);
        Assert.Equal(1, result);
    }

    [Fact]
    public void CharIndex_Null_ReturnsNull()
    {
        var result = _eval.Evaluate(Fn("CHARINDEX", Null(), Str("hello")), EmptyRow);
        Assert.Null(result);
    }

    [Fact]
    public void CharIndex_NullExpr_ReturnsNull()
    {
        var result = _eval.Evaluate(Fn("CHARINDEX", Str("a"), Null()), EmptyRow);
        Assert.Null(result);
    }

    #endregion

    #region CONCAT

    [Fact]
    public void Concat_TwoStrings()
    {
        var result = _eval.Evaluate(Fn("CONCAT", Str("hello"), Str(" world")), EmptyRow);
        Assert.Equal("hello world", result);
    }

    [Fact]
    public void Concat_MultipleArgs()
    {
        var result = _eval.Evaluate(
            Fn("CONCAT", Str("a"), Str("b"), Str("c"), Str("d")),
            EmptyRow);
        Assert.Equal("abcd", result);
    }

    [Fact]
    public void Concat_NullSafe_TreatsNullAsEmpty()
    {
        var result = _eval.Evaluate(
            Fn("CONCAT", Str("hello"), Null(), Str(" world")),
            EmptyRow);
        Assert.Equal("hello world", result);
    }

    [Fact]
    public void Concat_AllNulls()
    {
        var result = _eval.Evaluate(Fn("CONCAT", Null(), Null()), EmptyRow);
        Assert.Equal("", result);
    }

    [Fact]
    public void Concat_MixedTypes()
    {
        var row = Row(("count", 42));
        var result = _eval.Evaluate(
            Fn("CONCAT",
                Str("Count: "),
                new SqlColumnExpression(SqlColumnRef.Simple("count"))),
            row);
        Assert.Equal("Count: 42", result);
    }

    #endregion

    #region STUFF

    [Fact]
    public void Stuff_BasicUsage()
    {
        // STUFF('hello world', 7, 5, 'there') => 'hello there'
        // Position 7 = 'w', delete 5 chars 'world', insert 'there'
        var result = _eval.Evaluate(
            Fn("STUFF", Str("hello world"), Num("7"), Num("5"), Str("there")),
            EmptyRow);
        Assert.Equal("hello there", result);
    }

    [Fact]
    public void Stuff_InsertWithoutDelete()
    {
        // STUFF('hello', 6, 0, ' world') => 'hello world'
        var result = _eval.Evaluate(
            Fn("STUFF", Str("hello"), Num("6"), Num("0"), Str(" world")),
            EmptyRow);
        Assert.Equal("hello world", result);
    }

    [Fact]
    public void Stuff_DeleteOnly()
    {
        // STUFF('hello world', 6, 6, '') => 'hello'
        var result = _eval.Evaluate(
            Fn("STUFF", Str("hello world"), Num("6"), Num("6"), Str("")),
            EmptyRow);
        Assert.Equal("hello", result);
    }

    [Fact]
    public void Stuff_StartAtOne()
    {
        // STUFF('abcdef', 1, 3, 'XY') => 'XYdef'
        var result = _eval.Evaluate(
            Fn("STUFF", Str("abcdef"), Num("1"), Num("3"), Str("XY")),
            EmptyRow);
        Assert.Equal("XYdef", result);
    }

    [Fact]
    public void Stuff_StartBeyondLength_ReturnsNull()
    {
        var result = _eval.Evaluate(
            Fn("STUFF", Str("hello"), Num("10"), Num("1"), Str("x")),
            EmptyRow);
        Assert.Null(result);
    }

    [Fact]
    public void Stuff_StartZero_ReturnsNull()
    {
        var result = _eval.Evaluate(
            Fn("STUFF", Str("hello"), Num("0"), Num("1"), Str("x")),
            EmptyRow);
        Assert.Null(result);
    }

    [Fact]
    public void Stuff_NegativeLength_ReturnsNull()
    {
        var result = _eval.Evaluate(
            Fn("STUFF", Str("hello"), Num("1"), Num("-1"), Str("x")),
            EmptyRow);
        Assert.Null(result);
    }

    [Fact]
    public void Stuff_Null_ReturnsNull()
    {
        var result = _eval.Evaluate(
            Fn("STUFF", Null(), Num("1"), Num("1"), Str("x")),
            EmptyRow);
        Assert.Null(result);
    }

    [Fact]
    public void Stuff_DeleteLengthExceedsRemaining()
    {
        // STUFF('hello', 3, 100, 'x') => 'hex'
        var result = _eval.Evaluate(
            Fn("STUFF", Str("hello"), Num("3"), Num("100"), Str("x")),
            EmptyRow);
        Assert.Equal("hex", result);
    }

    #endregion

    #region REVERSE

    [Fact]
    public void Reverse_BasicString()
    {
        var result = _eval.Evaluate(Fn("REVERSE", Str("hello")), EmptyRow);
        Assert.Equal("olleh", result);
    }

    [Fact]
    public void Reverse_Palindrome()
    {
        var result = _eval.Evaluate(Fn("REVERSE", Str("racecar")), EmptyRow);
        Assert.Equal("racecar", result);
    }

    [Fact]
    public void Reverse_EmptyString()
    {
        var result = _eval.Evaluate(Fn("REVERSE", Str("")), EmptyRow);
        Assert.Equal("", result);
    }

    [Fact]
    public void Reverse_SingleChar()
    {
        var result = _eval.Evaluate(Fn("REVERSE", Str("a")), EmptyRow);
        Assert.Equal("a", result);
    }

    [Fact]
    public void Reverse_Null_ReturnsNull()
    {
        var result = _eval.Evaluate(Fn("REVERSE", Null()), EmptyRow);
        Assert.Null(result);
    }

    #endregion

    #region FunctionRegistry

    [Fact]
    public void Registry_UnknownFunction_Throws()
    {
        Assert.Throws<NotSupportedException>(() =>
            _eval.Evaluate(Fn("NONEXISTENT", Str("test")), EmptyRow));
    }

    [Fact]
    public void Registry_TooFewArgs_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            _eval.Evaluate(Fn("LEFT", Str("hello")), EmptyRow));
    }

    [Fact]
    public void Registry_TooManyArgs_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            _eval.Evaluate(Fn("UPPER", Str("a"), Str("b")), EmptyRow));
    }

    #endregion

    #region Nested Functions

    [Fact]
    public void Nested_UpperOfLeft()
    {
        // UPPER(LEFT('hello world', 5)) => 'HELLO'
        var result = _eval.Evaluate(
            Fn("UPPER", Fn("LEFT", Str("hello world"), Num("5"))),
            EmptyRow);
        Assert.Equal("HELLO", result);
    }

    [Fact]
    public void Nested_ConcatWithUpper()
    {
        // CONCAT(UPPER('hello'), ' ', LOWER('WORLD'))
        var result = _eval.Evaluate(
            Fn("CONCAT",
                Fn("UPPER", Str("hello")),
                Str(" "),
                Fn("LOWER", Str("WORLD"))),
            EmptyRow);
        Assert.Equal("HELLO world", result);
    }

    [Fact]
    public void Nested_ReplaceInSubstring()
    {
        // REPLACE(SUBSTRING('hello world', 1, 5), 'ello', 'i')
        var result = _eval.Evaluate(
            Fn("REPLACE",
                Fn("SUBSTRING", Str("hello world"), Num("1"), Num("5")),
                Str("ello"),
                Str("i")),
            EmptyRow);
        Assert.Equal("hi", result);
    }

    #endregion
}
