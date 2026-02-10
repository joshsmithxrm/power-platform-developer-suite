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
        registry.Register("DATEFROMPARTS", new DateFromPartsFunction());
        registry.Register("DATETIMEFROMPARTS", new DateTimeFromPartsFunction());
        registry.Register("EOMONTH", new EoMonthFunction());
        registry.Register("DATENAME", new DateNameFunction());
        registry.Register("SYSDATETIME", new SysDateTimeFunction());
        registry.Register("SWITCHOFFSET", new SwitchOffsetFunction());
        registry.Register("TODATETIMEOFFSET", new ToDateTimeOffsetFunction());
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

    // ── DATEFROMPARTS ─────────────────────────────────────────────────
    /// <summary>
    /// DATEFROMPARTS(year, month, day) - constructs a date from parts.
    /// </summary>
    private sealed class DateFromPartsFunction : IScalarFunction
    {
        public int MinArgs => 3;
        public int MaxArgs => 3;

        public object? Execute(object?[] args)
        {
            if (HasNull(args)) return null;
            var year = Convert.ToInt32(args[0], CultureInfo.InvariantCulture);
            var month = Convert.ToInt32(args[1], CultureInfo.InvariantCulture);
            var day = Convert.ToInt32(args[2], CultureInfo.InvariantCulture);
            return new DateTime(year, month, day);
        }
    }

    // ── DATETIMEFROMPARTS ──────────────────────────────────────────────
    /// <summary>
    /// DATETIMEFROMPARTS(year, month, day, hour, minute, second, millisecond) - constructs a datetime from parts.
    /// </summary>
    private sealed class DateTimeFromPartsFunction : IScalarFunction
    {
        public int MinArgs => 7;
        public int MaxArgs => 7;

        public object? Execute(object?[] args)
        {
            if (HasNull(args)) return null;
            var year = Convert.ToInt32(args[0], CultureInfo.InvariantCulture);
            var month = Convert.ToInt32(args[1], CultureInfo.InvariantCulture);
            var day = Convert.ToInt32(args[2], CultureInfo.InvariantCulture);
            var hour = Convert.ToInt32(args[3], CultureInfo.InvariantCulture);
            var minute = Convert.ToInt32(args[4], CultureInfo.InvariantCulture);
            var second = Convert.ToInt32(args[5], CultureInfo.InvariantCulture);
            var ms = Convert.ToInt32(args[6], CultureInfo.InvariantCulture);
            return new DateTime(year, month, day, hour, minute, second, ms);
        }
    }

    // ── EOMONTH ───────────────────────────────────────────────────────
    /// <summary>
    /// EOMONTH(date [, month_offset]) - returns last day of month.
    /// </summary>
    private sealed class EoMonthFunction : IScalarFunction
    {
        public int MinArgs => 1;
        public int MaxArgs => 2;

        public object? Execute(object?[] args)
        {
            if (args[0] is null) return null;
            var dt = ToDateTime(args[0]);
            if (dt is null) return null;

            int offset = 0;
            if (args.Length >= 2 && args[1] is not null)
            {
                offset = Convert.ToInt32(args[1], CultureInfo.InvariantCulture);
            }

            var adjusted = dt.Value.AddMonths(offset);
            var daysInMonth = DateTime.DaysInMonth(adjusted.Year, adjusted.Month);
            return new DateTime(adjusted.Year, adjusted.Month, daysInMonth);
        }
    }

    // ── DATENAME ──────────────────────────────────────────────────────
    /// <summary>
    /// DATENAME(datepart, date) - returns the name of the datepart as a string.
    /// For example, DATENAME(month, '2024-01-15') returns 'January'.
    /// </summary>
    private sealed class DateNameFunction : IScalarFunction
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
                "year" => dt.Value.Year.ToString(CultureInfo.InvariantCulture),
                "quarter" => ((dt.Value.Month - 1) / 3 + 1).ToString(CultureInfo.InvariantCulture),
                "month" => CultureInfo.InvariantCulture.DateTimeFormat.GetMonthName(dt.Value.Month),
                "dayofyear" => dt.Value.DayOfYear.ToString(CultureInfo.InvariantCulture),
                "day" => dt.Value.Day.ToString(CultureInfo.InvariantCulture),
                "week" => CultureInfo.InvariantCulture.Calendar.GetWeekOfYear(
                    dt.Value, CalendarWeekRule.FirstFourDayWeek, DayOfWeek.Monday).ToString(CultureInfo.InvariantCulture),
                "hour" => dt.Value.Hour.ToString(CultureInfo.InvariantCulture),
                "minute" => dt.Value.Minute.ToString(CultureInfo.InvariantCulture),
                "second" => dt.Value.Second.ToString(CultureInfo.InvariantCulture),
                "millisecond" => dt.Value.Millisecond.ToString(CultureInfo.InvariantCulture),
                _ => throw new NotSupportedException($"DATENAME does not support datepart '{datepart}'.")
            };
        }
    }

    // ── SYSDATETIME ───────────────────────────────────────────────────
    /// <summary>
    /// SYSDATETIME() - returns current UTC datetime with high precision.
    /// </summary>
    private sealed class SysDateTimeFunction : IScalarFunction
    {
        public int MinArgs => 0;
        public int MaxArgs => 0;

        public object? Execute(object?[] args)
        {
            return DateTime.UtcNow;
        }
    }

    // ── SWITCHOFFSET ──────────────────────────────────────────────────
    /// <summary>
    /// SWITCHOFFSET(datetimeoffset, timezone) - converts to specified offset.
    /// timezone should be in format "+HH:MM" or "-HH:MM".
    /// </summary>
    private sealed class SwitchOffsetFunction : IScalarFunction
    {
        public int MinArgs => 2;
        public int MaxArgs => 2;

        public object? Execute(object?[] args)
        {
            if (HasNull(args)) return null;

            DateTimeOffset dto;
            if (args[0] is DateTimeOffset existing)
            {
                dto = existing;
            }
            else
            {
                var dt = ToDateTime(args[0]);
                if (dt is null) return null;
                dto = new DateTimeOffset(dt.Value, TimeSpan.Zero);
            }

            var tzString = args[1]!.ToString()!;
            var offset = ParseTimeZoneOffset(tzString);
            if (offset is null) return null;

            return dto.ToOffset(offset.Value);
        }
    }

    // ── TODATETIMEOFFSET ──────────────────────────────────────────────
    /// <summary>
    /// TODATETIMEOFFSET(datetime, timezone) - attaches a timezone offset to a datetime.
    /// </summary>
    private sealed class ToDateTimeOffsetFunction : IScalarFunction
    {
        public int MinArgs => 2;
        public int MaxArgs => 2;

        public object? Execute(object?[] args)
        {
            if (HasNull(args)) return null;

            var dt = ToDateTime(args[0]);
            if (dt is null) return null;

            var tzString = args[1]!.ToString()!;
            var offset = ParseTimeZoneOffset(tzString);
            if (offset is null) return null;

            return new DateTimeOffset(DateTime.SpecifyKind(dt.Value, DateTimeKind.Unspecified), offset.Value);
        }
    }

    /// <summary>
    /// Parses a timezone offset string like "+05:30" or "-04:00" to a TimeSpan.
    /// </summary>
    private static TimeSpan? ParseTimeZoneOffset(string tz)
    {
        if (string.IsNullOrWhiteSpace(tz)) return null;
        tz = tz.Trim();

        bool negative = false;
        if (tz.StartsWith("+", StringComparison.Ordinal))
        {
            tz = tz.Substring(1);
        }
        else if (tz.StartsWith("-", StringComparison.Ordinal))
        {
            negative = true;
            tz = tz.Substring(1);
        }

        var parts = tz.Split(':');
        if (parts.Length != 2) return null;

        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var hours)) return null;
        if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes)) return null;

        var span = new TimeSpan(hours, minutes, 0);
        return negative ? span.Negate() : span;
    }
}
