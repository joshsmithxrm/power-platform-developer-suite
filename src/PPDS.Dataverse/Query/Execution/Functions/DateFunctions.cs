using System;
using System.Globalization;

namespace PPDS.Dataverse.Query.Execution.Functions;

/// <summary>
/// T-SQL date/time functions evaluated client-side.
/// YEAR/MONTH/DAY in GROUP BY are pushed to FetchXML dategrouping
/// for server-side performance; all other usages evaluate here.
/// </summary>
public static class DateFunctions
{
    /// <summary>
    /// Registers all date functions into the given registry.
    /// </summary>
    public static void RegisterAll(FunctionRegistry registry)
    {
        registry.Register("GETDATE", new GetDateFunction());
        registry.Register("GETUTCDATE", new GetUtcDateFunction());
        registry.Register("YEAR", new YearFunction());
        registry.Register("MONTH", new MonthFunction());
        registry.Register("DAY", new DayFunction());
        registry.Register("DATEADD", new DateAddFunction());
        registry.Register("DATEDIFF", new DateDiffFunction());
        registry.Register("DATEPART", new DatePartFunction());
        registry.Register("DATETRUNC", new DateTruncFunction());
    }

    /// <summary>
    /// Converts an argument to DateTime. Handles DateTime, string (ISO parse),
    /// and DateTimeOffset values from Dataverse.
    /// </summary>
    internal static DateTime? ToDateTime(object? value)
    {
        if (value is null) return null;
        if (value is DateTime dt) return dt;
        if (value is DateTimeOffset dto) return dto.UtcDateTime;
        if (value is string s && DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            return parsed;
        }
        return null;
    }

    /// <summary>
    /// Resolves a datepart string (including abbreviations) to a canonical name.
    /// </summary>
    internal static string NormalizeDatePart(string datepart)
    {
        return datepart.ToLowerInvariant() switch
        {
            "year" or "yy" or "yyyy" => "year",
            "quarter" or "qq" or "q" => "quarter",
            "month" or "mm" or "m" => "month",
            "dayofyear" or "dy" or "y" => "dayofyear",
            "day" or "dd" or "d" => "day",
            "week" or "wk" or "ww" => "week",
            "hour" or "hh" => "hour",
            "minute" or "mi" or "n" => "minute",
            "second" or "ss" or "s" => "second",
            "millisecond" or "ms" => "millisecond",
            _ => throw new NotSupportedException($"Unknown datepart '{datepart}'.")
        };
    }

    /// <summary>
    /// Returns NULL if any argument is NULL.
    /// </summary>
    private static bool HasNull(object?[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] is null) return true;
        }
        return false;
    }

    // ── GETDATE ───────────────────────────────────────────────────────
    /// <summary>
    /// GETDATE() - returns current UTC datetime (Dataverse uses UTC).
    /// </summary>
    private sealed class GetDateFunction : IScalarFunction
    {
        public int MinArgs => 0;
        public int MaxArgs => 0;

        public object? Execute(object?[] args)
        {
            return DateTime.UtcNow;
        }
    }

    // ── GETUTCDATE ────────────────────────────────────────────────────
    /// <summary>
    /// GETUTCDATE() - returns current UTC datetime.
    /// </summary>
    private sealed class GetUtcDateFunction : IScalarFunction
    {
        public int MinArgs => 0;
        public int MaxArgs => 0;

        public object? Execute(object?[] args)
        {
            return DateTime.UtcNow;
        }
    }

    // ── YEAR ──────────────────────────────────────────────────────────
    /// <summary>
    /// YEAR(date) - returns the year as an integer.
    /// </summary>
    private sealed class YearFunction : IScalarFunction
    {
        public int MinArgs => 1;
        public int MaxArgs => 1;

        public object? Execute(object?[] args)
        {
            if (HasNull(args)) return null;
            var dt = ToDateTime(args[0]);
            return dt?.Year;
        }
    }

    // ── MONTH ─────────────────────────────────────────────────────────
    /// <summary>
    /// MONTH(date) - returns the month (1-12) as an integer.
    /// </summary>
    private sealed class MonthFunction : IScalarFunction
    {
        public int MinArgs => 1;
        public int MaxArgs => 1;

        public object? Execute(object?[] args)
        {
            if (HasNull(args)) return null;
            var dt = ToDateTime(args[0]);
            return dt?.Month;
        }
    }

    // ── DAY ───────────────────────────────────────────────────────────
    /// <summary>
    /// DAY(date) - returns the day of month (1-31) as an integer.
    /// </summary>
    private sealed class DayFunction : IScalarFunction
    {
        public int MinArgs => 1;
        public int MaxArgs => 1;

        public object? Execute(object?[] args)
        {
            if (HasNull(args)) return null;
            var dt = ToDateTime(args[0]);
            return dt?.Day;
        }
    }

    // ── DATEADD ───────────────────────────────────────────────────────
    /// <summary>
    /// DATEADD(datepart, number, date) - adds interval to date.
    /// datepart is passed as a string literal (parser converts the unquoted keyword).
    /// </summary>
    private sealed class DateAddFunction : IScalarFunction
    {
        public int MinArgs => 3;
        public int MaxArgs => 3;

        public object? Execute(object?[] args)
        {
            if (args[1] is null || args[2] is null) return null;
            if (args[0] is not string datepart) return null;

            var number = Convert.ToInt32(args[1], CultureInfo.InvariantCulture);
            var dt = ToDateTime(args[2]);
            if (dt is null) return null;

            var part = NormalizeDatePart(datepart);
            return part switch
            {
                "year" => dt.Value.AddYears(number),
                "quarter" => dt.Value.AddMonths(number * 3),
                "month" => dt.Value.AddMonths(number),
                "day" => dt.Value.AddDays(number),
                "dayofyear" => dt.Value.AddDays(number),
                "week" => dt.Value.AddDays((double)number * 7),
                "hour" => dt.Value.AddHours(number),
                "minute" => dt.Value.AddMinutes(number),
                "second" => dt.Value.AddSeconds(number),
                "millisecond" => dt.Value.AddMilliseconds(number),
                _ => throw new NotSupportedException($"DATEADD does not support datepart '{datepart}'.")
            };
        }
    }

    // ── DATEDIFF ──────────────────────────────────────────────────────
    /// <summary>
    /// DATEDIFF(datepart, startdate, enddate) - returns count of datepart boundaries crossed.
    /// </summary>
    private sealed class DateDiffFunction : IScalarFunction
    {
        public int MinArgs => 3;
        public int MaxArgs => 3;

        public object? Execute(object?[] args)
        {
            if (args[1] is null || args[2] is null) return null;
            if (args[0] is not string datepart) return null;

            var start = ToDateTime(args[1]);
            var end = ToDateTime(args[2]);
            if (start is null || end is null) return null;

            var part = NormalizeDatePart(datepart);
            return part switch
            {
                "year" => end.Value.Year - start.Value.Year,
                "quarter" => ((end.Value.Year - start.Value.Year) * 4) + ((end.Value.Month - 1) / 3) - ((start.Value.Month - 1) / 3),
                "month" => ((end.Value.Year - start.Value.Year) * 12) + end.Value.Month - start.Value.Month,
                "day" or "dayofyear" => (int)(end.Value.Date - start.Value.Date).TotalDays,
                "week" => (int)(end.Value.Date - start.Value.Date).TotalDays / 7,
                "hour" => (int)(end.Value - start.Value).TotalHours,
                "minute" => (int)(end.Value - start.Value).TotalMinutes,
                "second" => (int)(end.Value - start.Value).TotalSeconds,
                "millisecond" => checked((int)(end.Value - start.Value).TotalMilliseconds),
                _ => throw new NotSupportedException($"DATEDIFF does not support datepart '{datepart}'.")
            };
        }
    }

    // ── DATEPART ──────────────────────────────────────────────────────
    /// <summary>
    /// DATEPART(datepart, date) - returns integer value of the specified part.
    /// </summary>
    private sealed class DatePartFunction : IScalarFunction
    {
        public int MinArgs => 2;
        public int MaxArgs => 2;

        public object? Execute(object?[] args)
        {
            if (args[1] is null) return null;
            if (args[0] is not string datepart) return null;

            var dt = ToDateTime(args[1]);
            if (dt is null) return null;

            var part = NormalizeDatePart(datepart);
            return part switch
            {
                "year" => dt.Value.Year,
                "quarter" => (dt.Value.Month - 1) / 3 + 1,
                "month" => dt.Value.Month,
                "dayofyear" => dt.Value.DayOfYear,
                "day" => dt.Value.Day,
                "week" => CultureInfo.InvariantCulture.Calendar.GetWeekOfYear(
                    dt.Value, CalendarWeekRule.FirstFourDayWeek, DayOfWeek.Monday),
                "hour" => dt.Value.Hour,
                "minute" => dt.Value.Minute,
                "second" => dt.Value.Second,
                "millisecond" => dt.Value.Millisecond,
                _ => throw new NotSupportedException($"DATEPART does not support datepart '{datepart}'.")
            };
        }
    }

    // ── DATETRUNC ─────────────────────────────────────────────────────
    /// <summary>
    /// DATETRUNC(datepart, date) - truncates date to specified precision.
    /// </summary>
    private sealed class DateTruncFunction : IScalarFunction
    {
        public int MinArgs => 2;
        public int MaxArgs => 2;

        public object? Execute(object?[] args)
        {
            if (args[1] is null) return null;
            if (args[0] is not string datepart) return null;

            var dt = ToDateTime(args[1]);
            if (dt is null) return null;

            var part = NormalizeDatePart(datepart);
            return part switch
            {
                "year" => new DateTime(dt.Value.Year, 1, 1, 0, 0, 0, dt.Value.Kind),
                "quarter" =>
                    new DateTime(dt.Value.Year, ((dt.Value.Month - 1) / 3) * 3 + 1, 1, 0, 0, 0, dt.Value.Kind),
                "month" => new DateTime(dt.Value.Year, dt.Value.Month, 1, 0, 0, 0, dt.Value.Kind),
                "day" or "dayofyear" => dt.Value.Date,
                "week" => dt.Value.Date.AddDays(-(int)dt.Value.DayOfWeek),
                "hour" => new DateTime(dt.Value.Year, dt.Value.Month, dt.Value.Day, dt.Value.Hour, 0, 0, dt.Value.Kind),
                "minute" => new DateTime(dt.Value.Year, dt.Value.Month, dt.Value.Day, dt.Value.Hour, dt.Value.Minute, 0, dt.Value.Kind),
                "second" => new DateTime(dt.Value.Year, dt.Value.Month, dt.Value.Day, dt.Value.Hour, dt.Value.Minute, dt.Value.Second, dt.Value.Kind),
                _ => throw new NotSupportedException($"DATETRUNC does not support datepart '{datepart}'.")
            };
        }
    }
}
