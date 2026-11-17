using System.Globalization;

namespace PPDS.LiveTests.Query.SelectFidelity;

/// <summary>
/// Independent reference semantics for the SELECT fidelity live test: T-SQL three-valued logic, the string collation
/// observed on the organisation (case-insensitive; accent sensitivity is probed at run time and set once), trailing-space
/// padding for '=' / '&lt;' comparisons, LIKE with %, _, [...], [^...] and ESCAPE, and NULL-first ordering. Nothing here
/// calls PPDS query code.
/// </summary>
internal static class TSql
{
    private static readonly CompareInfo Invariant = CultureInfo.InvariantCulture.CompareInfo;

    /// <summary>Case-insensitive always; accent-insensitive when the organisation's collation is (probed live).</summary>
    public static bool AccentInsensitive { get; set; } = true;

    private static CompareOptions Options => AccentInsensitive
        ? CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace | CompareOptions.IgnoreKanaType | CompareOptions.IgnoreWidth
        : CompareOptions.IgnoreCase | CompareOptions.IgnoreKanaType | CompareOptions.IgnoreWidth;

    // ───── comparison ─────

    /// <summary>Collation comparison of two strings with T-SQL trailing-space padding.</summary>
    public static int CompareStrings(string a, string b) => Invariant.Compare(a.TrimEnd(' '), b.TrimEnd(' '), Options);

    /// <summary>Total order on non-null values of the same family; numbers compare numerically.</summary>
    public static int Compare(object a, object b)
    {
        if (a is string sa && b is string sb) return CompareStrings(sa, sb);
        if (IsNumber(a) && IsNumber(b)) return ToDecimal(a).CompareTo(ToDecimal(b));
        if (a is DateTime da && b is DateTime db) return da.CompareTo(db);
        if (a is Guid ga && b is Guid gb) return CompareGuid(ga, gb);
        throw new InvalidOperationException($"Reference model cannot compare {a.GetType().Name} with {b.GetType().Name}.");
    }

    /// <summary>SQL Server uniqueidentifier ordering (bytes 10-15, then 8-9, 6-7, 4-5, 0-3).</summary>
    public static int CompareGuid(Guid a, Guid b)
    {
        var x = a.ToByteArray();
        var y = b.ToByteArray();
        int[] order = [10, 11, 12, 13, 14, 15, 8, 9, 6, 7, 4, 5, 0, 1, 2, 3];
        foreach (var i in order)
        {
            var c = x[i].CompareTo(y[i]);
            if (c != 0) return c;
        }
        return 0;
    }

    public static bool IsNumber(object o) => o is int or long or short or byte or decimal or double or float or bool;

    public static decimal ToDecimal(object o) => o switch
    {
        bool b => b ? 1m : 0m,
        double d => (decimal)d,
        float f => (decimal)f,
        _ => Convert.ToDecimal(o, CultureInfo.InvariantCulture)
    };

    // ───── three-valued predicates ─────

    public static bool? Eq(object? a, object? b) => a is null || b is null ? null : Compare(a, b) == 0;
    public static bool? Ne(object? a, object? b) => a is null || b is null ? null : Compare(a, b) != 0;
    public static bool? Lt(object? a, object? b) => a is null || b is null ? null : Compare(a, b) < 0;
    public static bool? Le(object? a, object? b) => a is null || b is null ? null : Compare(a, b) <= 0;
    public static bool? Gt(object? a, object? b) => a is null || b is null ? null : Compare(a, b) > 0;
    public static bool? Ge(object? a, object? b) => a is null || b is null ? null : Compare(a, b) >= 0;
    public static bool? Not(bool? a) => a is null ? null : !a.Value;

    public static bool? And(bool? a, bool? b)
    {
        if (a == false || b == false) return false;
        if (a is null || b is null) return null;
        return true;
    }

    public static bool? Or(bool? a, bool? b)
    {
        if (a == true || b == true) return true;
        if (a is null || b is null) return null;
        return false;
    }

    public static bool? Between(object? v, object? lo, object? hi) => And(Ge(v, lo), Le(v, hi));

    public static bool? In(object? v, params object?[] list)
    {
        bool? result = false;
        foreach (var item in list)
            result = Or(result, Eq(v, item));
        return result;
    }

    public static bool? IsNull(object? v) => v is null;

    // ───── LIKE ─────

    public static bool? Like(object? value, string? pattern, char? escape = null)
    {
        if (value is null || pattern is null) return null;
        var s = Convert.ToString(value, CultureInfo.InvariantCulture)!;
        return LikeMatch(s, 0, pattern, 0, escape);
    }

    private static bool CharEquals(char a, char b) => Invariant.Compare(a.ToString(), b.ToString(), Options) == 0;

    private static bool LikeMatch(string s, int si, string p, int pi, char? escape)
    {
        while (pi < p.Length)
        {
            var pc = p[pi];
            if (escape.HasValue && pc == escape.Value && pi + 1 < p.Length)
            {
                if (si >= s.Length || !CharEquals(s[si], p[pi + 1])) return false;
                si++;
                pi += 2;
                continue;
            }
            if (pc == '%')
            {
                // Collapse consecutive %.
                while (pi < p.Length && p[pi] == '%') pi++;
                if (pi == p.Length) return true;
                for (var k = si; k <= s.Length; k++)
                    if (LikeMatch(s, k, p, pi, escape)) return true;
                return false;
            }
            if (si >= s.Length) return false;
            if (pc == '_')
            {
                si++;
                pi++;
                continue;
            }
            if (pc == '[')
            {
                var close = p.IndexOf(']', pi + 2 <= p.Length ? pi + 1 : p.Length);
                // T-SQL: "[]" is not a set; "[" with no closing "]" matches a literal "[" (treated as literal here).
                if (close > pi + 1)
                {
                    var set = p.Substring(pi + 1, close - pi - 1);
                    var negate = set.Length > 1 && set[0] == '^';
                    if (negate) set = set[1..];
                    var match = SetContains(set, s[si]);
                    if (match == negate) return false;
                    si++;
                    pi = close + 1;
                    continue;
                }
            }
            if (!CharEquals(s[si], pc)) return false;
            si++;
            pi++;
        }
        return si == s.Length;
    }

    private static bool SetContains(string set, char c)
    {
        for (var i = 0; i < set.Length; i++)
        {
            if (i + 2 < set.Length && set[i + 1] == '-')
            {
                var lo = set[i];
                var hi = set[i + 2];
                if (Invariant.Compare(c.ToString(), lo.ToString(), Options) >= 0 && Invariant.Compare(c.ToString(), hi.ToString(), Options) <= 0)
                    return true;
                i += 2;
                continue;
            }
            if (CharEquals(set[i], c)) return true;
        }
        return false;
    }

    // ───── ordering ─────

    /// <summary>T-SQL ordering: NULLs sort first ascending (last descending).</summary>
    public static int OrderCompare(object? a, object? b)
    {
        if (a is null && b is null) return 0;
        if (a is null) return -1;
        if (b is null) return 1;
        return Compare(a, b);
    }

    /// <summary>Sorts by a sequence of keys (each with its direction).</summary>
    public static IEnumerable<T> OrderBy<T>(IEnumerable<T> rows, params (Func<T, object?> Key, bool Desc)[] keys)
    {
        var list = rows.ToList();
        list.Sort((x, y) =>
        {
            foreach (var (key, desc) in keys)
            {
                var c = OrderCompare(key(x), key(y));
                if (c != 0) return desc ? -c : c;
            }
            return 0;
        });
        return list;
    }

    /// <summary>Equality under the collation for grouping / DISTINCT (NULLs are one group).</summary>
    public static bool GroupEquals(object? a, object? b) => a is null ? b is null : b is not null && Compare(a, b) == 0;

    public static IEnumerable<IGrouping<object?, T>> GroupBy<T>(IEnumerable<T> rows, Func<T, object?> key)
        => rows.GroupBy(key, new GroupComparer());

    public sealed class GroupComparer : IEqualityComparer<object?>
    {
        public new bool Equals(object? x, object? y) => GroupEquals(x, y);

        public int GetHashCode(object? obj) => obj switch
        {
            null => 0,
            string s => Invariant.GetSortKey(s.TrimEnd(' '), Options).GetHashCode(),
            _ when IsNumber(obj) => ToDecimal(obj).GetHashCode(),
            _ => obj.GetHashCode()
        };
    }

    // ───── scalar helpers (T-SQL semantics) ─────

    public static object? Upper(string? s) => s?.ToUpperInvariant();
    public static object? Lower(string? s) => s?.ToLowerInvariant();
    public static object? Len(string? s) => s?.TrimEnd(' ').Length;

    /// <summary>REPLACE under a case-insensitive collation: matches case-insensitively.</summary>
    public static string? Replace(string? s, string find, string with)
    {
        if (s is null) return null;
        var result = new System.Text.StringBuilder();
        var i = 0;
        while (i < s.Length)
        {
            if (i + find.Length <= s.Length && Invariant.Compare(s.Substring(i, find.Length), find, Options) == 0)
            {
                result.Append(with);
                i += find.Length;
            }
            else
            {
                result.Append(s[i]);
                i++;
            }
        }
        return result.ToString();
    }

    /// <summary>CHARINDEX under the collation (1-based, 0 when absent).</summary>
    public static int? CharIndex(string find, string? s)
    {
        if (s is null) return null;
        for (var i = 0; i + find.Length <= s.Length; i++)
            if (Invariant.Compare(s.Substring(i, find.Length), find, Options) == 0) return i + 1;
        return 0;
    }

    public static string? Substring(string? s, int start, int length)
    {
        if (s is null) return null;
        // T-SQL: start is 1-based; characters before position 1 count against length.
        var begin = start - 1;
        var end = begin + length;
        begin = Math.Max(begin, 0);
        end = Math.Min(end, s.Length);
        return end <= begin ? "" : s[begin..end];
    }
}
