using System;
using System.Globalization;

namespace PPDS.Dataverse.Query.Execution.Functions;

/// <summary>
/// Implements T-SQL CAST/CONVERT type conversion semantics.
/// Supports common Dataverse types: int, bigint, decimal, float,
/// nvarchar, varchar, datetime, date, bit, uniqueidentifier, money.
/// </summary>
public static class CastConverter
{
    /// <summary>
    /// Converts a value to the specified SQL type.
    /// </summary>
    /// <param name="value">The value to convert. Null propagates as null.</param>
    /// <param name="targetType">The target SQL type name (case-insensitive), e.g. "int", "nvarchar(100)".</param>
    /// <param name="style">Optional CONVERT style code for datetime/string formatting.</param>
    /// <returns>The converted value, or null if the input is null.</returns>
    /// <exception cref="InvalidCastException">If the conversion is not supported or the value cannot be converted.</exception>
    public static object? Convert(object? value, string targetType, int? style = null)
    {
        if (value is null)
        {
            return null;
        }

        // Normalize the base type name (strip parameters like nvarchar(100) -> nvarchar)
        var (baseType, maxLength, precision, scale) = ParseTargetType(targetType);

        return baseType switch
        {
            "int" => ConvertToInt(value),
            "bigint" => ConvertToBigInt(value),
            "decimal" or "numeric" => ConvertToDecimal(value, precision, scale),
            "float" => ConvertToFloat(value),
            "real" => ConvertToFloat(value),
            "nvarchar" or "varchar" or "nchar" or "char" => ConvertToString(value, maxLength, style),
            "datetime" => ConvertToDateTime(value, style),
            "date" => ConvertToDate(value, style),
            "bit" => ConvertToBit(value),
            "uniqueidentifier" => ConvertToGuid(value),
            "money" or "smallmoney" => ConvertToMoney(value),
            _ => throw new InvalidCastException($"Unsupported target type: {targetType}")
        };
    }

    #region Type Name Parsing

    /// <summary>
    /// Parses a target type string into base name and parameters.
    /// E.g., "nvarchar(100)" -> ("nvarchar", 100, null, null)
    ///        "decimal(18,2)" -> ("decimal", null, 18, 2)
    /// </summary>
    private static (string baseType, int? maxLength, int? precision, int? scale) ParseTargetType(string targetType)
    {
        var normalized = targetType.Trim().ToLowerInvariant();
        var parenIdx = normalized.IndexOf('(');
        if (parenIdx < 0)
        {
            return (normalized, null, null, null);
        }

        var baseType = normalized.Substring(0, parenIdx);
        var paramStr = normalized.Substring(parenIdx + 1, normalized.Length - parenIdx - 2);

        if (baseType is "decimal" or "numeric")
        {
            var parts = paramStr.Split(',');
            var precision = int.Parse(parts[0].Trim(), CultureInfo.InvariantCulture);
            var scale = parts.Length > 1 ? int.Parse(parts[1].Trim(), CultureInfo.InvariantCulture) : 0;
            return (baseType, null, precision, scale);
        }

        // For string types: nvarchar(100), varchar(max)
        if (paramStr == "max")
        {
            return (baseType, int.MaxValue, null, null);
        }

        var maxLen = int.Parse(paramStr.Trim(), CultureInfo.InvariantCulture);
        return (baseType, maxLen, null, null);
    }

    #endregion

    #region Conversion Methods

    private static int ConvertToInt(object value)
    {
        return value switch
        {
            int i => i,
            long l => checked((int)l),
            decimal d => checked((int)decimal.Truncate(d)),
            double dbl => checked((int)Math.Truncate(dbl)),
            float f => checked((int)Math.Truncate(f)),
            bool b => b ? 1 : 0,
            string s => int.Parse(s.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture),
            DateTime => throw new InvalidCastException("Cannot convert datetime to int."),
            Guid => throw new InvalidCastException("Cannot convert uniqueidentifier to int."),
            _ => System.Convert.ToInt32(value, CultureInfo.InvariantCulture)
        };
    }

    private static long ConvertToBigInt(object value)
    {
        return value switch
        {
            int i => i,
            long l => l,
            decimal d => checked((long)decimal.Truncate(d)),
            double dbl => checked((long)Math.Truncate(dbl)),
            float f => checked((long)Math.Truncate(f)),
            bool b => b ? 1L : 0L,
            string s => long.Parse(s.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture),
            DateTime => throw new InvalidCastException("Cannot convert datetime to bigint."),
            Guid => throw new InvalidCastException("Cannot convert uniqueidentifier to bigint."),
            _ => System.Convert.ToInt64(value, CultureInfo.InvariantCulture)
        };
    }

    private static decimal ConvertToDecimal(object value, int? precision, int? scale)
    {
        decimal result = value switch
        {
            int i => i,
            long l => l,
            decimal d => d,
            double dbl => (decimal)dbl,
            float f => (decimal)f,
            bool b => b ? 1m : 0m,
            string s => decimal.Parse(s.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture),
            DateTime => throw new InvalidCastException("Cannot convert datetime to decimal."),
            Guid => throw new InvalidCastException("Cannot convert uniqueidentifier to decimal."),
            _ => System.Convert.ToDecimal(value, CultureInfo.InvariantCulture)
        };

        // Apply scale if specified
        if (scale.HasValue)
        {
            result = Math.Round(result, scale.Value, MidpointRounding.AwayFromZero);
        }

        return result;
    }

    private static double ConvertToFloat(object value)
    {
        return value switch
        {
            int i => i,
            long l => l,
            decimal d => (double)d,
            double dbl => dbl,
            float f => f,
            bool b => b ? 1.0 : 0.0,
            string s => double.Parse(s.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture),
            DateTime => throw new InvalidCastException("Cannot convert datetime to float."),
            Guid => throw new InvalidCastException("Cannot convert uniqueidentifier to float."),
            _ => System.Convert.ToDouble(value, CultureInfo.InvariantCulture)
        };
    }

    private static string ConvertToString(object value, int? maxLength, int? style)
    {
        string result;

        if (value is DateTime dt && style.HasValue)
        {
            result = FormatDateTimeWithStyle(dt, style.Value);
        }
        else
        {
            result = value switch
            {
                string s => s,
                bool b => b ? "1" : "0",
                DateTime dateTime => dateTime.ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture),
                Guid g => g.ToString().ToUpperInvariant(),
                decimal d => d.ToString(CultureInfo.InvariantCulture),
                double dbl => dbl.ToString(CultureInfo.InvariantCulture),
                float f => f.ToString(CultureInfo.InvariantCulture),
                _ => System.Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""
            };
        }

        // Truncate to max length if specified
        if (maxLength.HasValue && maxLength.Value != int.MaxValue && result.Length > maxLength.Value)
        {
            result = result.Substring(0, maxLength.Value);
        }

        return result;
    }

    private static DateTime ConvertToDateTime(object value, int? style)
    {
        return value switch
        {
            DateTime dt => dt,
            string s => ParseDateTimeString(s, style),
            int => throw new InvalidCastException("Cannot convert int to datetime."),
            long => throw new InvalidCastException("Cannot convert bigint to datetime."),
            decimal => throw new InvalidCastException("Cannot convert decimal to datetime."),
            double => throw new InvalidCastException("Cannot convert float to datetime."),
            Guid => throw new InvalidCastException("Cannot convert uniqueidentifier to datetime."),
            bool => throw new InvalidCastException("Cannot convert bit to datetime."),
            _ => throw new InvalidCastException($"Cannot convert {value.GetType().Name} to datetime.")
        };
    }

    private static DateTime ConvertToDate(object value, int? style)
    {
        return value switch
        {
            DateTime dt => dt.Date,
            string s => ParseDateTimeString(s, style).Date,
            _ => throw new InvalidCastException($"Cannot convert {value.GetType().Name} to date.")
        };
    }

    private static bool ConvertToBit(object value)
    {
        return value switch
        {
            bool b => b,
            int i => i != 0,
            long l => l != 0,
            decimal d => d != 0m,
            double dbl => dbl != 0.0, // CodeQL [cs/equality-on-floats] SQL BIT cast: exact zero check is correct
            float f => f != 0f, // CodeQL [cs/equality-on-floats] SQL BIT cast: exact zero check is correct
            string s => s.Trim() switch
            {
                "1" or "true" or "TRUE" or "True" => true,
                "0" or "false" or "FALSE" or "False" => false,
                _ => throw new InvalidCastException($"Cannot convert string '{s}' to bit.")
            },
            DateTime => throw new InvalidCastException("Cannot convert datetime to bit."),
            Guid => throw new InvalidCastException("Cannot convert uniqueidentifier to bit."),
            _ => System.Convert.ToBoolean(value, CultureInfo.InvariantCulture)
        };
    }

    private static Guid ConvertToGuid(object value)
    {
        return value switch
        {
            Guid g => g,
            string s => Guid.Parse(s.Trim()),
            _ => throw new InvalidCastException($"Cannot convert {value.GetType().Name} to uniqueidentifier.")
        };
    }

    private static decimal ConvertToMoney(object value)
    {
        decimal result = value switch
        {
            int i => i,
            long l => l,
            decimal d => d,
            double dbl => (decimal)dbl,
            float f => (decimal)f,
            bool b => b ? 1m : 0m,
            string s => decimal.Parse(s.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture),
            DateTime => throw new InvalidCastException("Cannot convert datetime to money."),
            Guid => throw new InvalidCastException("Cannot convert uniqueidentifier to money."),
            _ => System.Convert.ToDecimal(value, CultureInfo.InvariantCulture)
        };

        // Money has 4 decimal places
        return Math.Round(result, 4, MidpointRounding.AwayFromZero);
    }

    #endregion

    #region DateTime Formatting

    /// <summary>
    /// Formats a DateTime using T-SQL CONVERT style codes.
    /// </summary>
    private static string FormatDateTimeWithStyle(DateTime dt, int style)
    {
        return style switch
        {
            // USA: mm/dd/yy
            1 => dt.ToString("MM/dd/yy", CultureInfo.InvariantCulture),
            // ANSI: yy.mm.dd
            2 => dt.ToString("yy.MM.dd", CultureInfo.InvariantCulture),
            // British/French: dd/mm/yy
            3 => dt.ToString("dd/MM/yy", CultureInfo.InvariantCulture),
            // German: dd.mm.yy
            4 => dt.ToString("dd.MM.yy", CultureInfo.InvariantCulture),
            // Italian: dd-mm-yy
            5 => dt.ToString("dd-MM-yy", CultureInfo.InvariantCulture),
            // mon dd yyyy hh:mmAM
            100 => dt.ToString("MMM dd yyyy hh:mmtt", CultureInfo.InvariantCulture),
            // USA: mm/dd/yyyy
            101 => dt.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture),
            // ANSI: yyyy.mm.dd
            102 => dt.ToString("yyyy.MM.dd", CultureInfo.InvariantCulture),
            // British/French: dd/mm/yyyy
            103 => dt.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture),
            // German: dd.mm.yyyy
            104 => dt.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture),
            // Italian: dd-mm-yyyy
            105 => dt.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture),
            // mon dd, yyyy
            106 => dt.ToString("dd MMM yyyy", CultureInfo.InvariantCulture),
            // mon dd, yyyy
            107 => dt.ToString("MMM dd, yyyy", CultureInfo.InvariantCulture),
            // hh:mm:ss
            108 => dt.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
            // ISO8601: yyyy-mm-ddThh:mm:ss.mmm
            126 => dt.ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture),
            // ODBC canonical: yyyy-mm-dd hh:mm:ss
            120 => dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            // ODBC canonical with milliseconds
            121 => dt.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
            // ISO 8601 with timezone
            127 => dt.ToString("yyyy-MM-ddTHH:mm:ss.fffK", CultureInfo.InvariantCulture),
            // Default
            _ => dt.ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture)
        };
    }

    /// <summary>
    /// Parses a datetime string, optionally using a CONVERT style hint.
    /// </summary>
    private static DateTime ParseDateTimeString(string s, int? style)
    {
        var trimmed = s.Trim();

        if (style.HasValue)
        {
            // Try style-specific parsing first
            string? format = style.Value switch
            {
                1 => "MM/dd/yy",
                2 => "yy.MM.dd",
                3 => "dd/MM/yy",
                4 => "dd.MM.yy",
                5 => "dd-MM-yy",
                101 => "MM/dd/yyyy",
                102 => "yyyy.MM.dd",
                103 => "dd/MM/yyyy",
                104 => "dd.MM.yyyy",
                105 => "dd-MM-yyyy",
                108 => "HH:mm:ss",
                120 => "yyyy-MM-dd HH:mm:ss",
                121 => "yyyy-MM-dd HH:mm:ss.fff",
                126 => "yyyy-MM-ddTHH:mm:ss.fff",
                _ => null
            };

            if (format != null && DateTime.TryParseExact(trimmed, format,
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            {
                return parsed;
            }
        }

        // Fallback: general parsing
        return DateTime.Parse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.None);
    }

    #endregion
}
