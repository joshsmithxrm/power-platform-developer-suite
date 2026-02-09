using System;
using System.Collections.Generic;
using PPDS.Dataverse.Query;
using PPDS.Dataverse.Query.Execution;
using PPDS.Dataverse.Query.Execution.Functions;
using PPDS.Dataverse.Sql.Ast;
using Xunit;

namespace PPDS.Dataverse.Tests.Query.Execution.Functions;

[Trait("Category", "TuiUnit")]
public class DateFunctionTests
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

    private static SqlFunctionExpression Func(string name, params ISqlExpression[] args)
    {
        return new SqlFunctionExpression(name, args);
    }

    private static SqlColumnExpression Col(string name)
    {
        return new SqlColumnExpression(SqlColumnRef.Simple(name));
    }

    private static SqlLiteralExpression Lit(string value)
    {
        return new SqlLiteralExpression(SqlLiteral.String(value));
    }

    private static SqlLiteralExpression Num(string value)
    {
        return new SqlLiteralExpression(SqlLiteral.Number(value));
    }

    #region GETDATE / GETUTCDATE

    [Fact]
    public void GetDate_ReturnsCurrentDateTime()
    {
        var before = DateTime.UtcNow;
        var result = _eval.Evaluate(Func("GETDATE"), EmptyRow);
        var after = DateTime.UtcNow;

        Assert.IsType<DateTime>(result);
        var dt = (DateTime)result;
        Assert.True(dt >= before && dt <= after, "GETDATE should return current UTC time.");
    }

    [Fact]
    public void GetUtcDate_ReturnsCurrentDateTime()
    {
        var before = DateTime.UtcNow;
        var result = _eval.Evaluate(Func("GETUTCDATE"), EmptyRow);
        var after = DateTime.UtcNow;

        Assert.IsType<DateTime>(result);
        var dt = (DateTime)result;
        Assert.True(dt >= before && dt <= after, "GETUTCDATE should return current UTC time.");
    }

    #endregion

    #region YEAR

    [Fact]
    public void Year_ReturnsYearFromDateTime()
    {
        var row = Row(("createdon", new DateTime(2024, 3, 15)));
        var result = _eval.Evaluate(Func("YEAR", Col("createdon")), row);
        Assert.Equal(2024, result);
    }

    [Fact]
    public void Year_NullPropagation()
    {
        var row = Row(("createdon", null));
        var result = _eval.Evaluate(Func("YEAR", Col("createdon")), row);
        Assert.Null(result);
    }

    [Fact]
    public void Year_FromStringDate()
    {
        var row = Row(("createdon", "2023-06-20T10:30:00"));
        var result = _eval.Evaluate(Func("YEAR", Col("createdon")), row);
        Assert.Equal(2023, result);
    }

    #endregion

    #region MONTH

    [Fact]
    public void Month_ReturnsMonthFromDateTime()
    {
        var row = Row(("createdon", new DateTime(2024, 7, 4)));
        var result = _eval.Evaluate(Func("MONTH", Col("createdon")), row);
        Assert.Equal(7, result);
    }

    [Fact]
    public void Month_NullPropagation()
    {
        var row = Row(("createdon", null));
        var result = _eval.Evaluate(Func("MONTH", Col("createdon")), row);
        Assert.Null(result);
    }

    #endregion

    #region DAY

    [Fact]
    public void Day_ReturnsDayFromDateTime()
    {
        var row = Row(("createdon", new DateTime(2024, 12, 25)));
        var result = _eval.Evaluate(Func("DAY", Col("createdon")), row);
        Assert.Equal(25, result);
    }

    [Fact]
    public void Day_NullPropagation()
    {
        var row = Row(("createdon", null));
        var result = _eval.Evaluate(Func("DAY", Col("createdon")), row);
        Assert.Null(result);
    }

    #endregion

    #region DATEADD

    [Fact]
    public void DateAdd_Year()
    {
        var row = Row(("d", new DateTime(2024, 1, 15)));
        var result = _eval.Evaluate(Func("DATEADD", Lit("year"), Num("2"), Col("d")), row);
        Assert.Equal(new DateTime(2026, 1, 15), result);
    }

    [Fact]
    public void DateAdd_Month()
    {
        var row = Row(("d", new DateTime(2024, 1, 31)));
        var result = _eval.Evaluate(Func("DATEADD", Lit("month"), Num("1"), Col("d")), row);
        Assert.Equal(new DateTime(2024, 2, 29), result); // 2024 is a leap year
    }

    [Fact]
    public void DateAdd_Day()
    {
        var row = Row(("d", new DateTime(2024, 1, 1)));
        var result = _eval.Evaluate(Func("DATEADD", Lit("day"), Num("10"), Col("d")), row);
        Assert.Equal(new DateTime(2024, 1, 11), result);
    }

    [Fact]
    public void DateAdd_Hour()
    {
        var row = Row(("d", new DateTime(2024, 1, 1, 10, 0, 0)));
        var result = _eval.Evaluate(Func("DATEADD", Lit("hour"), Num("5"), Col("d")), row);
        Assert.Equal(new DateTime(2024, 1, 1, 15, 0, 0), result);
    }

    [Fact]
    public void DateAdd_NegativeValue()
    {
        var row = Row(("d", new DateTime(2024, 6, 15)));
        var result = _eval.Evaluate(Func("DATEADD", Lit("year"), Num("-1"), Col("d")), row);
        Assert.Equal(new DateTime(2023, 6, 15), result);
    }

    [Fact]
    public void DateAdd_NullPropagation()
    {
        var row = Row(("d", null));
        var result = _eval.Evaluate(Func("DATEADD", Lit("year"), Num("1"), Col("d")), row);
        Assert.Null(result);
    }

    [Fact]
    public void DateAdd_Quarter()
    {
        var row = Row(("d", new DateTime(2024, 1, 15)));
        var result = _eval.Evaluate(Func("DATEADD", Lit("quarter"), Num("1"), Col("d")), row);
        Assert.Equal(new DateTime(2024, 4, 15), result);
    }

    #endregion

    #region DATEDIFF

    [Fact]
    public void DateDiff_Year()
    {
        var row = Row(("start", new DateTime(2020, 1, 1)), ("end", new DateTime(2024, 6, 15)));
        var result = _eval.Evaluate(Func("DATEDIFF", Lit("year"), Col("start"), Col("end")), row);
        Assert.Equal(4, result);
    }

    [Fact]
    public void DateDiff_Month()
    {
        var row = Row(("start", new DateTime(2024, 1, 1)), ("end", new DateTime(2024, 4, 15)));
        var result = _eval.Evaluate(Func("DATEDIFF", Lit("month"), Col("start"), Col("end")), row);
        Assert.Equal(3, result);
    }

    [Fact]
    public void DateDiff_Day()
    {
        var row = Row(("start", new DateTime(2024, 1, 1)), ("end", new DateTime(2024, 1, 11)));
        var result = _eval.Evaluate(Func("DATEDIFF", Lit("day"), Col("start"), Col("end")), row);
        Assert.Equal(10, result);
    }

    [Fact]
    public void DateDiff_NullPropagation()
    {
        var row = Row(("start", null), ("end", new DateTime(2024, 1, 1)));
        var result = _eval.Evaluate(Func("DATEDIFF", Lit("year"), Col("start"), Col("end")), row);
        Assert.Null(result);
    }

    #endregion

    #region DATEPART

    [Fact]
    public void DatePart_Year()
    {
        var row = Row(("d", new DateTime(2024, 7, 4)));
        var result = _eval.Evaluate(Func("DATEPART", Lit("year"), Col("d")), row);
        Assert.Equal(2024, result);
    }

    [Fact]
    public void DatePart_Quarter()
    {
        var row = Row(("d", new DateTime(2024, 7, 4)));
        var result = _eval.Evaluate(Func("DATEPART", Lit("quarter"), Col("d")), row);
        Assert.Equal(3, result);
    }

    [Fact]
    public void DatePart_Month()
    {
        var row = Row(("d", new DateTime(2024, 7, 4)));
        var result = _eval.Evaluate(Func("DATEPART", Lit("month"), Col("d")), row);
        Assert.Equal(7, result);
    }

    [Fact]
    public void DatePart_Day()
    {
        var row = Row(("d", new DateTime(2024, 7, 4)));
        var result = _eval.Evaluate(Func("DATEPART", Lit("day"), Col("d")), row);
        Assert.Equal(4, result);
    }

    [Fact]
    public void DatePart_Hour()
    {
        var row = Row(("d", new DateTime(2024, 7, 4, 14, 30, 0)));
        var result = _eval.Evaluate(Func("DATEPART", Lit("hour"), Col("d")), row);
        Assert.Equal(14, result);
    }

    [Fact]
    public void DatePart_Minute()
    {
        var row = Row(("d", new DateTime(2024, 7, 4, 14, 30, 0)));
        var result = _eval.Evaluate(Func("DATEPART", Lit("minute"), Col("d")), row);
        Assert.Equal(30, result);
    }

    [Fact]
    public void DatePart_NullPropagation()
    {
        var row = Row(("d", null));
        var result = _eval.Evaluate(Func("DATEPART", Lit("year"), Col("d")), row);
        Assert.Null(result);
    }

    #endregion

    #region DATETRUNC

    [Fact]
    public void DateTrunc_Year()
    {
        var row = Row(("d", new DateTime(2024, 7, 15, 10, 30, 45)));
        var result = _eval.Evaluate(Func("DATETRUNC", Lit("year"), Col("d")), row);
        Assert.Equal(new DateTime(2024, 1, 1), result);
    }

    [Fact]
    public void DateTrunc_Month()
    {
        var row = Row(("d", new DateTime(2024, 7, 15, 10, 30, 45)));
        var result = _eval.Evaluate(Func("DATETRUNC", Lit("month"), Col("d")), row);
        Assert.Equal(new DateTime(2024, 7, 1), result);
    }

    [Fact]
    public void DateTrunc_Day()
    {
        var row = Row(("d", new DateTime(2024, 7, 15, 10, 30, 45)));
        var result = _eval.Evaluate(Func("DATETRUNC", Lit("day"), Col("d")), row);
        Assert.Equal(new DateTime(2024, 7, 15), result);
    }

    [Fact]
    public void DateTrunc_Hour()
    {
        var row = Row(("d", new DateTime(2024, 7, 15, 10, 30, 45)));
        var result = _eval.Evaluate(Func("DATETRUNC", Lit("hour"), Col("d")), row);
        Assert.Equal(new DateTime(2024, 7, 15, 10, 0, 0), result);
    }

    [Fact]
    public void DateTrunc_NullPropagation()
    {
        var row = Row(("d", null));
        var result = _eval.Evaluate(Func("DATETRUNC", Lit("year"), Col("d")), row);
        Assert.Null(result);
    }

    [Fact]
    public void DateTrunc_Quarter()
    {
        var row = Row(("d", new DateTime(2024, 8, 15)));
        var result = _eval.Evaluate(Func("DATETRUNC", Lit("quarter"), Col("d")), row);
        Assert.Equal(new DateTime(2024, 7, 1), result);
    }

    #endregion

    #region Datepart Abbreviations

    [Fact]
    public void DatePart_YearAbbreviation_yy()
    {
        var row = Row(("d", new DateTime(2024, 7, 4)));
        var result = _eval.Evaluate(Func("DATEPART", Lit("yy"), Col("d")), row);
        Assert.Equal(2024, result);
    }

    [Fact]
    public void DateAdd_DayAbbreviation_dd()
    {
        var row = Row(("d", new DateTime(2024, 1, 1)));
        var result = _eval.Evaluate(Func("DATEADD", Lit("dd"), Num("5"), Col("d")), row);
        Assert.Equal(new DateTime(2024, 1, 6), result);
    }

    [Fact]
    public void DatePart_MonthAbbreviation_mm()
    {
        var row = Row(("d", new DateTime(2024, 9, 1)));
        var result = _eval.Evaluate(Func("DATEPART", Lit("mm"), Col("d")), row);
        Assert.Equal(9, result);
    }

    #endregion

    #region DateTimeOffset Support

    [Fact]
    public void Year_FromDateTimeOffset()
    {
        var dto = new DateTimeOffset(2024, 3, 15, 10, 0, 0, TimeSpan.FromHours(-5));
        var row = Row(("createdon", dto));
        var result = _eval.Evaluate(Func("YEAR", Col("createdon")), row);
        Assert.Equal(2024, result);
    }

    #endregion

    #region Function Registry

    [Fact]
    public void AllDateFunctionsRegistered()
    {
        var registry = FunctionRegistry.CreateDefault();
        Assert.True(registry.IsRegistered("GETDATE"));
        Assert.True(registry.IsRegistered("GETUTCDATE"));
        Assert.True(registry.IsRegistered("YEAR"));
        Assert.True(registry.IsRegistered("MONTH"));
        Assert.True(registry.IsRegistered("DAY"));
        Assert.True(registry.IsRegistered("DATEADD"));
        Assert.True(registry.IsRegistered("DATEDIFF"));
        Assert.True(registry.IsRegistered("DATEPART"));
        Assert.True(registry.IsRegistered("DATETRUNC"));
    }

    [Fact]
    public void DateFunctions_CaseInsensitive()
    {
        var registry = FunctionRegistry.CreateDefault();
        Assert.True(registry.IsRegistered("year"));
        Assert.True(registry.IsRegistered("Year"));
        Assert.True(registry.IsRegistered("getdate"));
        Assert.True(registry.IsRegistered("dateadd"));
    }

    #endregion
}
