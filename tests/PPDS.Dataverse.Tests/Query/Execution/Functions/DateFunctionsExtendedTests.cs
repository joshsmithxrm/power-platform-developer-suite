using System;
using System.Collections.Generic;
using PPDS.Dataverse.Query;
using PPDS.Dataverse.Query.Execution;
using PPDS.Dataverse.Query.Execution.Functions;
using PPDS.Dataverse.Sql.Ast;
using Xunit;

namespace PPDS.Dataverse.Tests.Query.Execution.Functions;

[Trait("Category", "Unit")]
public class DateFunctionsExtendedTests
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

    private static SqlFunctionExpression Fn(string name, params ISqlExpression[] args)
    {
        return new SqlFunctionExpression(name, args);
    }

    private static SqlColumnExpression Col(string name) =>
        new(SqlColumnRef.Simple(name));

    private static SqlLiteralExpression Lit(string value) =>
        new(SqlLiteral.String(value));

    private static SqlLiteralExpression Num(string value) =>
        new(SqlLiteral.Number(value));

    private static SqlLiteralExpression Null() =>
        new(SqlLiteral.Null());

    #region DATEFROMPARTS

    [Fact]
    public void DateFromParts_BasicUsage()
    {
        var result = _eval.Evaluate(Fn("DATEFROMPARTS", Num("2024"), Num("3"), Num("15")), EmptyRow);
        Assert.Equal(new DateTime(2024, 3, 15), result);
    }

    [Fact]
    public void DateFromParts_FirstDayOfYear()
    {
        var result = _eval.Evaluate(Fn("DATEFROMPARTS", Num("2024"), Num("1"), Num("1")), EmptyRow);
        Assert.Equal(new DateTime(2024, 1, 1), result);
    }

    [Fact]
    public void DateFromParts_LastDayOfYear()
    {
        var result = _eval.Evaluate(Fn("DATEFROMPARTS", Num("2024"), Num("12"), Num("31")), EmptyRow);
        Assert.Equal(new DateTime(2024, 12, 31), result);
    }

    [Fact]
    public void DateFromParts_NullYear_ReturnsNull()
    {
        var result = _eval.Evaluate(Fn("DATEFROMPARTS", Null(), Num("3"), Num("15")), EmptyRow);
        Assert.Null(result);
    }

    [Fact]
    public void DateFromParts_NullMonth_ReturnsNull()
    {
        var result = _eval.Evaluate(Fn("DATEFROMPARTS", Num("2024"), Null(), Num("15")), EmptyRow);
        Assert.Null(result);
    }

    [Fact]
    public void DateFromParts_NullDay_ReturnsNull()
    {
        var result = _eval.Evaluate(Fn("DATEFROMPARTS", Num("2024"), Num("3"), Null()), EmptyRow);
        Assert.Null(result);
    }

    #endregion

    #region DATETIMEFROMPARTS

    [Fact]
    public void DateTimeFromParts_BasicUsage()
    {
        var result = _eval.Evaluate(
            Fn("DATETIMEFROMPARTS",
                Num("2024"), Num("3"), Num("15"),
                Num("10"), Num("30"), Num("45"), Num("500")),
            EmptyRow);
        Assert.Equal(new DateTime(2024, 3, 15, 10, 30, 45, 500), result);
    }

    [Fact]
    public void DateTimeFromParts_Midnight()
    {
        var result = _eval.Evaluate(
            Fn("DATETIMEFROMPARTS",
                Num("2024"), Num("1"), Num("1"),
                Num("0"), Num("0"), Num("0"), Num("0")),
            EmptyRow);
        Assert.Equal(new DateTime(2024, 1, 1, 0, 0, 0, 0), result);
    }

    [Fact]
    public void DateTimeFromParts_NullArg_ReturnsNull()
    {
        var result = _eval.Evaluate(
            Fn("DATETIMEFROMPARTS",
                Num("2024"), Num("3"), Num("15"),
                Num("10"), Null(), Num("45"), Num("500")),
            EmptyRow);
        Assert.Null(result);
    }

    #endregion

    #region EOMONTH

    [Fact]
    public void EoMonth_January()
    {
        var row = Row(("d", new DateTime(2024, 1, 15)));
        var result = _eval.Evaluate(Fn("EOMONTH", Col("d")), row);
        Assert.Equal(new DateTime(2024, 1, 31), result);
    }

    [Fact]
    public void EoMonth_February_LeapYear()
    {
        var row = Row(("d", new DateTime(2024, 2, 10)));
        var result = _eval.Evaluate(Fn("EOMONTH", Col("d")), row);
        Assert.Equal(new DateTime(2024, 2, 29), result);
    }

    [Fact]
    public void EoMonth_February_NonLeapYear()
    {
        var row = Row(("d", new DateTime(2023, 2, 10)));
        var result = _eval.Evaluate(Fn("EOMONTH", Col("d")), row);
        Assert.Equal(new DateTime(2023, 2, 28), result);
    }

    [Fact]
    public void EoMonth_WithPositiveOffset()
    {
        // January + 2 months offset = end of March
        var row = Row(("d", new DateTime(2024, 1, 15)));
        var result = _eval.Evaluate(Fn("EOMONTH", Col("d"), Num("2")), row);
        Assert.Equal(new DateTime(2024, 3, 31), result);
    }

    [Fact]
    public void EoMonth_WithNegativeOffset()
    {
        // March - 1 month = end of February
        var row = Row(("d", new DateTime(2024, 3, 15)));
        var result = _eval.Evaluate(Fn("EOMONTH", Col("d"), Num("-1")), row);
        Assert.Equal(new DateTime(2024, 2, 29), result);
    }

    [Fact]
    public void EoMonth_NullDate_ReturnsNull()
    {
        var row = Row(("d", null));
        var result = _eval.Evaluate(Fn("EOMONTH", Col("d")), row);
        Assert.Null(result);
    }

    #endregion

    #region DATENAME

    [Fact]
    public void DateName_Month_ReturnsMonthName()
    {
        var row = Row(("d", new DateTime(2024, 1, 15)));
        var result = _eval.Evaluate(Fn("DATENAME", Lit("month"), Col("d")), row);
        Assert.Equal("January", result);
    }

    [Fact]
    public void DateName_Month_July()
    {
        var row = Row(("d", new DateTime(2024, 7, 4)));
        var result = _eval.Evaluate(Fn("DATENAME", Lit("month"), Col("d")), row);
        Assert.Equal("July", result);
    }

    [Fact]
    public void DateName_Year()
    {
        var row = Row(("d", new DateTime(2024, 7, 4)));
        var result = _eval.Evaluate(Fn("DATENAME", Lit("year"), Col("d")), row);
        Assert.Equal("2024", result);
    }

    [Fact]
    public void DateName_Day()
    {
        var row = Row(("d", new DateTime(2024, 7, 25)));
        var result = _eval.Evaluate(Fn("DATENAME", Lit("day"), Col("d")), row);
        Assert.Equal("25", result);
    }

    [Fact]
    public void DateName_Hour()
    {
        var row = Row(("d", new DateTime(2024, 7, 4, 14, 30, 0)));
        var result = _eval.Evaluate(Fn("DATENAME", Lit("hour"), Col("d")), row);
        Assert.Equal("14", result);
    }

    [Fact]
    public void DateName_NullDate_ReturnsNull()
    {
        var row = Row(("d", null));
        var result = _eval.Evaluate(Fn("DATENAME", Lit("month"), Col("d")), row);
        Assert.Null(result);
    }

    #endregion

    #region SYSDATETIME

    [Fact]
    public void SysDateTime_ReturnsCurrentTime()
    {
        var before = DateTime.UtcNow;
        var result = _eval.Evaluate(Fn("SYSDATETIME"), EmptyRow);
        var after = DateTime.UtcNow;

        Assert.IsType<DateTime>(result);
        var dt = (DateTime)result;
        Assert.True(dt >= before && dt <= after, "SYSDATETIME should return current UTC time.");
    }

    #endregion

    #region SWITCHOFFSET

    [Fact]
    public void SwitchOffset_ChangeOffset()
    {
        var dto = new DateTimeOffset(2024, 7, 15, 10, 0, 0, TimeSpan.Zero);
        var row = Row(("d", dto));
        var result = _eval.Evaluate(Fn("SWITCHOFFSET", Col("d"), Lit("+05:30")), row);
        Assert.IsType<DateTimeOffset>(result);
        var resultDto = (DateTimeOffset)result;
        Assert.Equal(TimeSpan.FromHours(5.5), resultDto.Offset);
        Assert.Equal(15, resultDto.Hour);
        Assert.Equal(30, resultDto.Minute);
    }

    [Fact]
    public void SwitchOffset_NegativeOffset()
    {
        var dto = new DateTimeOffset(2024, 7, 15, 10, 0, 0, TimeSpan.Zero);
        var row = Row(("d", dto));
        var result = _eval.Evaluate(Fn("SWITCHOFFSET", Col("d"), Lit("-04:00")), row);
        Assert.IsType<DateTimeOffset>(result);
        var resultDto = (DateTimeOffset)result;
        Assert.Equal(TimeSpan.FromHours(-4), resultDto.Offset);
    }

    [Fact]
    public void SwitchOffset_NullInput_ReturnsNull()
    {
        var row = Row(("d", null));
        var result = _eval.Evaluate(Fn("SWITCHOFFSET", Col("d"), Lit("+05:00")), row);
        Assert.Null(result);
    }

    #endregion

    #region TODATETIMEOFFSET

    [Fact]
    public void ToDateTimeOffset_BasicUsage()
    {
        var row = Row(("d", new DateTime(2024, 7, 15, 10, 0, 0)));
        var result = _eval.Evaluate(Fn("TODATETIMEOFFSET", Col("d"), Lit("+05:00")), row);
        Assert.IsType<DateTimeOffset>(result);
        var dto = (DateTimeOffset)result;
        Assert.Equal(10, dto.Hour);
        Assert.Equal(TimeSpan.FromHours(5), dto.Offset);
    }

    [Fact]
    public void ToDateTimeOffset_NegativeOffset()
    {
        var row = Row(("d", new DateTime(2024, 7, 15, 10, 0, 0)));
        var result = _eval.Evaluate(Fn("TODATETIMEOFFSET", Col("d"), Lit("-07:00")), row);
        Assert.IsType<DateTimeOffset>(result);
        var dto = (DateTimeOffset)result;
        Assert.Equal(10, dto.Hour);
        Assert.Equal(TimeSpan.FromHours(-7), dto.Offset);
    }

    [Fact]
    public void ToDateTimeOffset_NullDate_ReturnsNull()
    {
        var row = Row(("d", null));
        var result = _eval.Evaluate(Fn("TODATETIMEOFFSET", Col("d"), Lit("+05:00")), row);
        Assert.Null(result);
    }

    #endregion

    #region Registry

    [Fact]
    public void AllNewDateFunctionsRegistered()
    {
        var registry = FunctionRegistry.CreateDefault();
        Assert.True(registry.IsRegistered("DATEFROMPARTS"));
        Assert.True(registry.IsRegistered("DATETIMEFROMPARTS"));
        Assert.True(registry.IsRegistered("EOMONTH"));
        Assert.True(registry.IsRegistered("DATENAME"));
        Assert.True(registry.IsRegistered("SYSDATETIME"));
        Assert.True(registry.IsRegistered("SWITCHOFFSET"));
        Assert.True(registry.IsRegistered("TODATETIMEOFFSET"));
    }

    #endregion
}
