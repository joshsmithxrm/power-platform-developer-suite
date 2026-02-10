using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PPDS.Dataverse.Query.Execution.Functions;

/// <summary>
/// T-SQL string functions evaluated client-side.
/// All functions propagate NULL (return NULL if any argument is NULL),
/// except CONCAT which treats NULL as empty string.
/// </summary>
public static class StringFunctions
{
    /// <summary>
    /// Registers all string functions into the given registry.
    /// </summary>
    public static void RegisterAll(FunctionRegistry registry)
    {
        registry.Register("UPPER", new UpperFunction());
        registry.Register("LOWER", new LowerFunction());
        registry.Register("LEN", new LenFunction());
        registry.Register("LEFT", new LeftFunction());
        registry.Register("RIGHT", new RightFunction());
        registry.Register("SUBSTRING", new SubstringFunction());
        registry.Register("TRIM", new TrimFunction());
        registry.Register("LTRIM", new LtrimFunction());
        registry.Register("RTRIM", new RtrimFunction());
        registry.Register("REPLACE", new ReplaceFunction());
        registry.Register("CHARINDEX", new CharIndexFunction());
        registry.Register("CONCAT", new ConcatFunction());
        registry.Register("STUFF", new StuffFunction());
        registry.Register("REVERSE", new ReverseFunction());
        registry.Register("REPLICATE", new ReplicateFunction());
        registry.Register("PATINDEX", new PatIndexFunction());
        registry.Register("CONCAT_WS", new ConcatWsFunction());
        registry.Register("FORMAT", new FormatFunction());
        registry.Register("SPACE", new SpaceFunction());
        registry.Register("UNICODE", new UnicodeFunction());
        registry.Register("CHAR", new CharFunction());
        registry.Register("QUOTENAME", new QuoteNameFunction());
        registry.Register("SOUNDEX", new SoundexFunction());
        registry.Register("DIFFERENCE", new DifferenceFunction());
        registry.Register("STRING_AGG", new StringAggFunction());
    }

    /// <summary>
    /// Helper: converts an argument to string using invariant culture.
    /// </summary>
    private static string? AsString(object? value)
    {
        if (value is null) return null;
        return Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Helper: converts an argument to int.
    /// </summary>
    private static int ToInt(object value)
    {
        return Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Returns NULL if any argument is NULL. Used by most functions except CONCAT.
    /// </summary>
    private static bool HasNull(object?[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] is null) return true;
        }
        return false;
    }

    // ── UPPER ──────────────────────────────────────────────────────────
    private sealed class UpperFunction : IScalarFunction
    {
        public int MinArgs => 1;
        public int MaxArgs => 1;

        public object? Execute(object?[] args)
        {
            if (HasNull(args)) return null;
            return AsString(args[0])!.ToUpperInvariant();
        }
    }

    // ── LOWER ──────────────────────────────────────────────────────────
    private sealed class LowerFunction : IScalarFunction
    {
        public int MinArgs => 1;
        public int MaxArgs => 1;

        public object? Execute(object?[] args)
        {
            if (HasNull(args)) return null;
            return AsString(args[0])!.ToLowerInvariant();
        }
    }

    // ── LEN ────────────────────────────────────────────────────────────
    /// <summary>
    /// LEN(expr) returns the number of characters, excluding trailing spaces (T-SQL behavior).
    /// </summary>
    private sealed class LenFunction : IScalarFunction
    {
        public int MinArgs => 1;
        public int MaxArgs => 1;

        public object? Execute(object?[] args)
        {
            if (HasNull(args)) return null;
            var str = AsString(args[0])!;
            return str.TrimEnd().Length;
        }
    }

    // ── LEFT ───────────────────────────────────────────────────────────
    private sealed class LeftFunction : IScalarFunction
    {
        public int MinArgs => 2;
        public int MaxArgs => 2;

        public object? Execute(object?[] args)
        {
            if (HasNull(args)) return null;
            var str = AsString(args[0])!;
            var n = ToInt(args[1]!);
            if (n < 0) return null;
            if (n >= str.Length) return str;
            return str.Substring(0, n);
        }
    }

    // ── RIGHT ──────────────────────────────────────────────────────────
    private sealed class RightFunction : IScalarFunction
    {
        public int MinArgs => 2;
        public int MaxArgs => 2;

        public object? Execute(object?[] args)
        {
            if (HasNull(args)) return null;
            var str = AsString(args[0])!;
            var n = ToInt(args[1]!);
            if (n < 0) return null;
            if (n >= str.Length) return str;
            return str.Substring(str.Length - n);
        }
    }

    // ── SUBSTRING ──────────────────────────────────────────────────────
    /// <summary>
    /// SUBSTRING(expr, start, length) — 1-based start per T-SQL.
    /// SUBSTRING('hello', 2, 3) returns 'ell'.
    /// </summary>
    private sealed class SubstringFunction : IScalarFunction
    {
        public int MinArgs => 3;
        public int MaxArgs => 3;

        public object? Execute(object?[] args)
        {
            if (HasNull(args)) return null;
            var str = AsString(args[0])!;
            var start = ToInt(args[1]!);  // 1-based
            var length = ToInt(args[2]!);

            // T-SQL: if start < 1, reduce length accordingly
            // SUBSTRING('hello', 0, 3) => SUBSTRING('hello', 1, 2) => 'he'
            if (start < 1)
            {
                length = length + start - 1;
                start = 1;
            }

            if (length < 0) return "";

            // Convert to 0-based
            int zeroStart = start - 1;
            if (zeroStart >= str.Length) return "";

            int available = str.Length - zeroStart;
            int take = Math.Min(length, available);
            return str.Substring(zeroStart, take);
        }
    }

    // ── TRIM ───────────────────────────────────────────────────────────
    private sealed class TrimFunction : IScalarFunction
    {
        public int MinArgs => 1;
        public int MaxArgs => 1;

        public object? Execute(object?[] args)
        {
            if (HasNull(args)) return null;
            return AsString(args[0])!.Trim();
        }
    }

    // ── LTRIM ──────────────────────────────────────────────────────────
    private sealed class LtrimFunction : IScalarFunction
    {
        public int MinArgs => 1;
        public int MaxArgs => 1;

        public object? Execute(object?[] args)
        {
            if (HasNull(args)) return null;
            return AsString(args[0])!.TrimStart();
        }
    }

    // ── RTRIM ──────────────────────────────────────────────────────────
    private sealed class RtrimFunction : IScalarFunction
    {
        public int MinArgs => 1;
        public int MaxArgs => 1;

        public object? Execute(object?[] args)
        {
            if (HasNull(args)) return null;
            return AsString(args[0])!.TrimEnd();
        }
    }

    // ── REPLACE ────────────────────────────────────────────────────────
    /// <summary>
    /// REPLACE(expr, find, replacement) — case-insensitive per SQL Server default collation.
    /// </summary>
    private sealed class ReplaceFunction : IScalarFunction
    {
        public int MinArgs => 3;
        public int MaxArgs => 3;

        public object? Execute(object?[] args)
        {
            if (HasNull(args)) return null;
            var str = AsString(args[0])!;
            var find = AsString(args[1])!;
            var replace = AsString(args[2])!;

            if (string.IsNullOrEmpty(find)) return str;

            // Case-insensitive replace matching SQL Server default collation
            var sb = new StringBuilder();
            int idx = 0;
            while (idx < str.Length)
            {
                int found = str.IndexOf(find, idx, StringComparison.OrdinalIgnoreCase);
                if (found < 0)
                {
                    sb.Append(str, idx, str.Length - idx);
                    break;
                }
                sb.Append(str, idx, found - idx);
                sb.Append(replace);
                idx = found + find.Length;
            }
            return sb.ToString();
        }
    }

    // ── CHARINDEX ──────────────────────────────────────────────────────
    /// <summary>
    /// CHARINDEX(find, expr [, start]) — returns 1-based position, 0 if not found.
    /// Optional start is 1-based.
    /// </summary>
    private sealed class CharIndexFunction : IScalarFunction
    {
        public int MinArgs => 2;
        public int MaxArgs => 3;

        public object? Execute(object?[] args)
        {
            if (HasNull(args)) return null;
            var find = AsString(args[0])!;
            var str = AsString(args[1])!;

            int startIndex = 0; // 0-based internal
            if (args.Length == 3)
            {
                var start1Based = ToInt(args[2]!);
                if (start1Based < 1) return 0;
                startIndex = start1Based - 1;
                if (startIndex >= str.Length) return 0;
            }

            if (string.IsNullOrEmpty(find))
            {
                // T-SQL: CHARINDEX('', ...) returns start position
                return startIndex + 1;
            }

            int pos = str.IndexOf(find, startIndex, StringComparison.OrdinalIgnoreCase);
            return pos < 0 ? 0 : pos + 1; // Convert to 1-based
        }
    }

    // ── CONCAT ─────────────────────────────────────────────────────────
    /// <summary>
    /// CONCAT(expr, expr, ...) — variadic, NULL-safe: NULL becomes empty string.
    /// </summary>
    private sealed class ConcatFunction : IScalarFunction
    {
        public int MinArgs => 2;
        public int MaxArgs => int.MaxValue;

        public object? Execute(object?[] args)
        {
            var sb = new StringBuilder();
            foreach (var arg in args)
            {
                sb.Append(AsString(arg) ?? "");
            }
            return sb.ToString();
        }
    }

    // ── STUFF ──────────────────────────────────────────────────────────
    /// <summary>
    /// STUFF(expr, start, length, replacement) — 1-based start.
    /// Deletes 'length' characters at 'start' and inserts replacement.
    /// </summary>
    private sealed class StuffFunction : IScalarFunction
    {
        public int MinArgs => 4;
        public int MaxArgs => 4;

        public object? Execute(object?[] args)
        {
            if (HasNull(args)) return null;
            var str = AsString(args[0])!;
            var start = ToInt(args[1]!);     // 1-based
            var length = ToInt(args[2]!);
            var replacement = AsString(args[3])!;

            // T-SQL: start < 1 or start > len+1 returns NULL
            if (start < 1 || start > str.Length + 1) return null;
            if (length < 0) return null;

            int zeroStart = start - 1;
            int deleteCount = Math.Min(length, str.Length - zeroStart);

            var sb = new StringBuilder(str.Length - deleteCount + replacement.Length);
            sb.Append(str, 0, zeroStart);
            sb.Append(replacement);
            if (zeroStart + deleteCount < str.Length)
            {
                sb.Append(str, zeroStart + deleteCount, str.Length - zeroStart - deleteCount);
            }
            return sb.ToString();
        }
    }

    // ── REVERSE ────────────────────────────────────────────────────────
    private sealed class ReverseFunction : IScalarFunction
    {
        public int MinArgs => 1;
        public int MaxArgs => 1;

        public object? Execute(object?[] args)
        {
            if (HasNull(args)) return null;
            var str = AsString(args[0])!;
            var chars = str.ToCharArray();
            Array.Reverse(chars);
            return new string(chars);
        }
    }

    // ── REPLICATE ─────────────────────────────────────────────────────
    /// <summary>
    /// REPLICATE(string, count) - repeats a string a specified number of times.
    /// </summary>
    private sealed class ReplicateFunction : IScalarFunction
    {
        public int MinArgs => 2;
        public int MaxArgs => 2;

        public object? Execute(object?[] args)
        {
            if (HasNull(args)) return null;
            var str = AsString(args[0])!;
            var count = ToInt(args[1]!);
            if (count < 0) return null;
            if (count == 0) return "";
            var sb = new StringBuilder(str.Length * count);
            for (int i = 0; i < count; i++)
            {
                sb.Append(str);
            }
            return sb.ToString();
        }
    }

    // ── PATINDEX ──────────────────────────────────────────────────────
    /// <summary>
    /// PATINDEX(pattern, string) - returns 1-based position of first match.
    /// Pattern uses SQL LIKE syntax (% and _). Returns 0 if no match.
    /// </summary>
    private sealed class PatIndexFunction : IScalarFunction
    {
        public int MinArgs => 2;
        public int MaxArgs => 2;

        public object? Execute(object?[] args)
        {
            if (HasNull(args)) return null;
            var pattern = AsString(args[0])!;
            var str = AsString(args[1])!;

            // Strip leading/trailing % for search pattern
            var searchPattern = pattern;
            bool anchorStart = true;
            bool anchorEnd = true;

            if (searchPattern.StartsWith("%", StringComparison.Ordinal))
            {
                searchPattern = searchPattern.Substring(1);
                anchorStart = false;
            }
            if (searchPattern.EndsWith("%", StringComparison.Ordinal))
            {
                searchPattern = searchPattern.Substring(0, searchPattern.Length - 1);
                anchorEnd = false;
            }

            // Convert SQL LIKE pattern to regex
            var regexPattern = new StringBuilder("(?i)");
            if (anchorStart) regexPattern.Append('^');
            foreach (char c in searchPattern)
            {
                switch (c)
                {
                    case '_':
                        regexPattern.Append('.');
                        break;
                    case '%':
                        regexPattern.Append(".*");
                        break;
                    case '.':
                    case '^':
                    case '$':
                    case '(':
                    case ')':
                    case '{':
                    case '}':
                    case '|':
                    case '\\':
                    case '+':
                    case '*':
                    case '?':
                    case '[':
                    case ']':
                        regexPattern.Append('\\');
                        regexPattern.Append(c);
                        break;
                    default:
                        regexPattern.Append(c);
                        break;
                }
            }
            if (anchorEnd) regexPattern.Append('$');

            var match = Regex.Match(str, regexPattern.ToString(), RegexOptions.None, TimeSpan.FromSeconds(1));
            return match.Success ? match.Index + 1 : 0;
        }
    }

    // ── CONCAT_WS ─────────────────────────────────────────────────────
    /// <summary>
    /// CONCAT_WS(separator, val1, val2, ...) - concatenates with separator, skipping NULLs.
    /// </summary>
    private sealed class ConcatWsFunction : IScalarFunction
    {
        public int MinArgs => 3;
        public int MaxArgs => int.MaxValue;

        public object? Execute(object?[] args)
        {
            // First arg is the separator - if NULL, result is NULL
            if (args[0] is null) return null;
            var separator = AsString(args[0])!;

            var sb = new StringBuilder();
            bool first = true;
            for (int i = 1; i < args.Length; i++)
            {
                if (args[i] is null) continue;
                if (!first) sb.Append(separator);
                sb.Append(AsString(args[i]));
                first = false;
            }
            return sb.ToString();
        }
    }

    // ── FORMAT ─────────────────────────────────────────────────────────
    /// <summary>
    /// FORMAT(value, format_string) - formats a value using .NET format string.
    /// Supports basic numeric and date formatting.
    /// </summary>
    private sealed class FormatFunction : IScalarFunction
    {
        public int MinArgs => 2;
        public int MaxArgs => 2;

        public object? Execute(object?[] args)
        {
            if (HasNull(args)) return null;
            var value = args[0]!;
            var format = AsString(args[1])!;

            if (value is DateTime dt)
                return dt.ToString(format, CultureInfo.InvariantCulture);
            if (value is DateTimeOffset dto)
                return dto.ToString(format, CultureInfo.InvariantCulture);
            if (value is IFormattable formattable)
                return formattable.ToString(format, CultureInfo.InvariantCulture);
            return AsString(value);
        }
    }

    // ── SPACE ──────────────────────────────────────────────────────────
    /// <summary>
    /// SPACE(count) - returns a string of repeated spaces.
    /// </summary>
    private sealed class SpaceFunction : IScalarFunction
    {
        public int MinArgs => 1;
        public int MaxArgs => 1;

        public object? Execute(object?[] args)
        {
            if (HasNull(args)) return null;
            var count = ToInt(args[0]!);
            if (count < 0) return null;
            return new string(' ', count);
        }
    }

    // ── UNICODE ────────────────────────────────────────────────────────
    /// <summary>
    /// UNICODE(char) - returns the Unicode code point of the first character.
    /// </summary>
    private sealed class UnicodeFunction : IScalarFunction
    {
        public int MinArgs => 1;
        public int MaxArgs => 1;

        public object? Execute(object?[] args)
        {
            if (HasNull(args)) return null;
            var str = AsString(args[0])!;
            if (str.Length == 0) return null;
            return (int)str[0];
        }
    }

    // ── CHAR ───────────────────────────────────────────────────────────
    /// <summary>
    /// CHAR(int) - returns the character for a given Unicode code point.
    /// </summary>
    private sealed class CharFunction : IScalarFunction
    {
        public int MinArgs => 1;
        public int MaxArgs => 1;

        public object? Execute(object?[] args)
        {
            if (HasNull(args)) return null;
            var codePoint = ToInt(args[0]!);
            if (codePoint < 0 || codePoint > 65535) return null;
            return ((char)codePoint).ToString();
        }
    }

    // ── QUOTENAME ──────────────────────────────────────────────────────
    /// <summary>
    /// QUOTENAME(string [, quote_char]) - wraps a string with delimiters.
    /// Default quote_char is '[' (brackets). Also supports single-quote and double-quote.
    /// </summary>
    private sealed class QuoteNameFunction : IScalarFunction
    {
        public int MinArgs => 1;
        public int MaxArgs => 2;

        public object? Execute(object?[] args)
        {
            if (args[0] is null) return null;
            var str = AsString(args[0])!;
            var quoteChar = args.Length >= 2 && args[1] is not null
                ? AsString(args[1])!
                : "[";

            if (str.Length > 128) return null; // T-SQL limit

            return quoteChar switch
            {
                "[" => "[" + str.Replace("]", "]]") + "]",
                "'" => "'" + str.Replace("'", "''") + "'",
                "\"" => "\"" + str.Replace("\"", "\"\"") + "\"",
                _ => null
            };
        }
    }

    // ── SOUNDEX ────────────────────────────────────────────────────────
    /// <summary>
    /// SOUNDEX(string) - returns a four-character soundex code.
    /// </summary>
    private sealed class SoundexFunction : IScalarFunction
    {
        public int MinArgs => 1;
        public int MaxArgs => 1;

        public object? Execute(object?[] args)
        {
            if (HasNull(args)) return null;
            var str = AsString(args[0])!;
            return ComputeSoundex(str);
        }
    }

    // ── DIFFERENCE ─────────────────────────────────────────────────────
    /// <summary>
    /// DIFFERENCE(string1, string2) - returns 0-4 indicating how similar
    /// the SOUNDEX values of two strings are (4 = most similar).
    /// </summary>
    private sealed class DifferenceFunction : IScalarFunction
    {
        public int MinArgs => 2;
        public int MaxArgs => 2;

        public object? Execute(object?[] args)
        {
            if (HasNull(args)) return null;
            var s1 = ComputeSoundex(AsString(args[0])!);
            var s2 = ComputeSoundex(AsString(args[1])!);
            int match = 0;
            for (int i = 0; i < 4; i++)
            {
                if (s1[i] == s2[i]) match++;
            }
            return match;
        }
    }

    // ── STRING_AGG (scalar fallback) ──────────────────────────────────
    /// <summary>
    /// STRING_AGG(expression, separator) - scalar fallback for non-aggregate context.
    /// In aggregate context this is handled by the aggregate pipeline.
    /// In scalar context, returns the expression value as-is.
    /// </summary>
    private sealed class StringAggFunction : IScalarFunction
    {
        public int MinArgs => 2;
        public int MaxArgs => 2;

        public object? Execute(object?[] args)
        {
            // In scalar context, just return the first argument as string
            if (args[0] is null) return null;
            return AsString(args[0]);
        }
    }

    /// <summary>
    /// Computes the Soundex code for a string per the American Soundex algorithm.
    /// </summary>
    internal static string ComputeSoundex(string input)
    {
        if (string.IsNullOrEmpty(input)) return "0000";

        var result = new char[4];
        result[0] = char.ToUpperInvariant(input[0]);
        int idx = 1;
        char lastCode = SoundexCode(result[0]);

        for (int i = 1; i < input.Length && idx < 4; i++)
        {
            char c = char.ToUpperInvariant(input[i]);
            char code = SoundexCode(c);
            if (code != '0' && code != lastCode)
            {
                result[idx++] = code;
            }
            lastCode = code;
        }

        while (idx < 4)
        {
            result[idx++] = '0';
        }

        return new string(result);
    }

    private static char SoundexCode(char c)
    {
        switch (char.ToUpperInvariant(c))
        {
            case 'B': case 'F': case 'P': case 'V': return '1';
            case 'C': case 'G': case 'J': case 'K': case 'Q': case 'S': case 'X': case 'Z': return '2';
            case 'D': case 'T': return '3';
            case 'L': return '4';
            case 'M': case 'N': return '5';
            case 'R': return '6';
            default: return '0';
        }
    }
}
