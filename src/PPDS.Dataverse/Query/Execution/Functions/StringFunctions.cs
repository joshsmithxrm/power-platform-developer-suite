using System;
using System.Globalization;
using System.Text;

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
}
