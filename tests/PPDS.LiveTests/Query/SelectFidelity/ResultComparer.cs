using System.Globalization;
using System.Text;
using PPDS.Dataverse.Query;

namespace PPDS.LiveTests.Query.SelectFidelity;

/// <summary>The result T-SQL semantics require, computed by the test from its in-memory copy of the data.</summary>
internal sealed class Expected
{
    public required IReadOnlyList<string> Columns { get; init; }
    public required IReadOnlyList<object?[]> Rows { get; init; }

    /// <summary>Row order is part of the contract (ORDER BY with a total order).</summary>
    public bool Ordered { get; init; }

    /// <summary>T-SQL raises an error for this statement; any clear engine error is correct.</summary>
    public string? Error { get; init; }

    /// <summary>Compare strings by the collation (DISTINCT / GROUP BY / UNION over case or accent variants).</summary>
    public bool CollationStrings { get; init; }

    /// <summary>SELECT *: the expected columns must be present (by name); other columns are allowed.</summary>
    public bool ContainsColumns { get; init; }

    /// <summary>T-SQL returns several result sets for this script (described here); returning one is a wrong result.</summary>
    public string? MultipleResultSets { get; init; }

    /// <summary>Do not ignore an unrequested primary-key column (used by the cases that check the result shape itself).</summary>
    public bool StrictShape { get; init; }

    public static Expected Fails(string why) => new() { Columns = [], Rows = [], Error = why };
}

/// <summary>What the engine returned, flattened to column names (Alias ?? LogicalName, as the CLI prints) and raw values.</summary>
internal sealed class ActualTable
{
    public required List<string> Columns { get; init; }
    public required List<object?[]> Rows { get; init; }
    public List<string> AutoExpanded { get; } = [];
    public List<string> ExtraKeys { get; } = [];

    public static readonly object Missing = new();

    public static ActualTable From(QueryResult result)
    {
        var columns = result.Columns.Select(c => c.Alias ?? c.LogicalName).ToList();
        var rows = result.Records
            .Select(record => columns.Select(k => record.TryGetValue(k, out var qv) ? qv?.Value : Missing).ToArray())
            .ToList();
        return new ActualTable { Columns = columns, Rows = rows };
    }
}

internal enum Verdict
{
    Correct,
    WrongResult,
    CleanError,
    Crash,
    HarnessError
}

internal static class ResultComparer
{
    /// <summary>
    /// Compares shape (column names and order) and rows (multiset, or sequence when ordered). Returns null when equal,
    /// otherwise a description of the difference. A column named X+"name" directly after a lookup/choice/boolean column X
    /// that the statement did not ask for is the documented *name auto-expansion and is ignored (recorded on
    /// <see cref="ActualTable.AutoExpanded"/>).
    /// </summary>
    public static string? Compare(Expected expected, ActualTable actual, IReadOnlyCollection<string> primaryKeys)
    {
        if (!expected.StrictShape)
        {
            DropAutoExpansion(expected, actual);
            DropColumns(actual, i => primaryKeys.Contains(actual.Columns[i], StringComparer.OrdinalIgnoreCase)
                                     && !expected.Columns.Any(e => NamedMatch(e, actual.Columns[i])), actual.ExtraKeys);
        }
        var problems = new List<string>();

        // A zero-row result reports no columns (documented in specs/query.md "Query with 0 results").
        if (actual.Rows.Count == 0 && actual.Columns.Count == 0)
            return expected.Rows.Count == 0 ? null : RowDiff(expected.Rows.Select(r => RowKey(r, expected.CollationStrings)).ToList(), []);

        // Map expected columns to actual positions.
        int[] map;
        if (expected.ContainsColumns)
        {
            map = expected.Columns.Select(e => actual.Columns.FindIndex(a => NameMatches(e, a))).ToArray();
            var missing = expected.Columns.Where((e, i) => map[i] < 0).ToList();
            if (missing.Count > 0)
                problems.Add($"missing columns [{string.Join(", ", missing)}] (got [{string.Join(", ", actual.Columns)}])");
        }
        else
        {
            var shapeOk = actual.Columns.Count == expected.Columns.Count
                && expected.Columns.Select((e, i) => NameMatches(e, actual.Columns[i])).All(x => x);
            if (!shapeOk)
                problems.Add($"columns [{string.Join(", ", actual.Columns)}], expected [{string.Join(", ", expected.Columns)}]");
            map = shapeOk || actual.Columns.Count == expected.Columns.Count
                ? Enumerable.Range(0, expected.Columns.Count).ToArray()
                : expected.Columns.Select(e => actual.Columns.FindIndex(a => NameMatches(e, a))).ToArray();
        }

        var expRows = expected.Rows.Select(r => RowKey(r, expected.CollationStrings)).ToList();
        var actRows = actual.Rows
            .Select(r => RowKey(map.Select(i => i >= 0 && i < r.Length ? r[i] : ActualTable.Missing).ToArray(), expected.CollationStrings))
            .ToList();

        if (expected.Ordered)
        {
            if (!expRows.SequenceEqual(actRows))
            {
                var sameSet = expRows.OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(actRows.OrderBy(x => x, StringComparer.Ordinal));
                problems.Add(sameSet
                    ? $"same rows, wrong order: got {Preview(actRows)}"
                    : RowDiff(expRows, actRows));
            }
        }
        else
        {
            var e = expRows.OrderBy(x => x, StringComparer.Ordinal).ToList();
            var a = actRows.OrderBy(x => x, StringComparer.Ordinal).ToList();
            if (!e.SequenceEqual(a))
                problems.Add(RowDiff(expRows, actRows));
        }

        return problems.Count == 0 ? null : string.Join("; ", problems);
    }

    private static void DropAutoExpansion(Expected expected, ActualTable actual)
        => DropColumns(actual, i => i >= 1
                                    && actual.Columns[i].Equals(actual.Columns[i - 1] + "name", StringComparison.OrdinalIgnoreCase)
                                    && !expected.Columns.Any(e => NamedMatch(e, actual.Columns[i])), actual.AutoExpanded);

    /// <summary>Like <see cref="NameMatches"/> but an unnamed expected column ("") matches nothing.</summary>
    private static bool NamedMatch(string expected, string actual) => expected.Length > 0 && NameMatches(expected, actual);

    private static void DropColumns(ActualTable actual, Func<int, bool> drop, List<string> record)
    {
        for (var i = actual.Columns.Count - 1; i >= 0; i--)
        {
            if (!drop(i)) continue;
            record.Add(actual.Columns[i]);
            actual.Columns.RemoveAt(i);
            for (var r = 0; r < actual.Rows.Count; r++)
            {
                var list = actual.Rows[r].ToList();
                if (i < list.Count) list.RemoveAt(i);
                actual.Rows[r] = list.ToArray();
            }
        }
    }

    /// <summary>Case-insensitive; "" (an unnamed T-SQL column) matches anything; "alias.col" matches "col".</summary>
    public static bool NameMatches(string expected, string actual)
        => expected.Length == 0
           || string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase)
           || actual.EndsWith("." + expected, StringComparison.OrdinalIgnoreCase);

    private static string RowDiff(List<string> expected, List<string> actual)
    {
        var remaining = actual.GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count());
        var missing = new List<string>();
        foreach (var e in expected)
        {
            if (remaining.TryGetValue(e, out var n) && n > 0) remaining[e] = n - 1;
            else missing.Add(e);
        }
        var extra = remaining.SelectMany(kv => Enumerable.Repeat(kv.Key, kv.Value)).ToList();
        var sb = new StringBuilder();
        sb.Append($"{actual.Count} rows (expected {expected.Count})");
        if (missing.Count > 0) sb.Append($"; missing {missing.Count}: {Preview(missing)}");
        if (extra.Count > 0) sb.Append($"; unexpected {extra.Count}: {Preview(extra)}");
        return sb.ToString();
    }

    public static string Preview(IReadOnlyList<string> rows, int max = 6)
        => string.Join(" ", rows.Take(max)) + (rows.Count > max ? $" …(+{rows.Count - max})" : "");

    public static string Describe(IReadOnlyList<object?[]> rows, int max = 6)
        => $"{rows.Count} row{(rows.Count == 1 ? "" : "s")}" + (rows.Count == 0 ? "" : ": " + Preview(rows.Select(r => RowKey(r, false)).ToList(), max));

    public static string RowKey(object?[] row, bool collation) => "(" + string.Join(",", row.Select(v => Canon(v, collation))) + ")";

    public static string Canon(object? v, bool collation)
    {
        // A record without the key prints as an empty cell (CLI) / omitted property (JSON): the value is NULL. A column the
        // result lacks altogether is reported by the shape check instead.
        if (ReferenceEquals(v, ActualTable.Missing)) return "NULL";
        switch (v)
        {
            case null:
                return "NULL";
            case string s:
                return collation ? "'" + Fold(s) + "'" : "'" + s + "'";
            case bool b:
                return b ? "1" : "0";
            case DateTime dt:
                var utc = dt.Kind == DateTimeKind.Local ? dt.ToUniversalTime() : dt;
                return utc.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
            case Guid g:
                return "{" + g.ToString("D") + "}";
            case int[] arr:
                return "[" + string.Join("|", arr.OrderBy(x => x)) + "]";
            case DateTimeOffset dto:
                return dto.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
        }
        if (TSql.IsNumber(v))
        {
            decimal d;
            try { d = TSql.ToDecimal(v); }
            catch (OverflowException) { return Convert.ToString(v, CultureInfo.InvariantCulture)!; }
            return Math.Round(d, 4, MidpointRounding.AwayFromZero).ToString("0.####", CultureInfo.InvariantCulture);
        }
        return "<" + v.GetType().Name + ":" + Convert.ToString(v, CultureInfo.InvariantCulture) + ">";
    }

    /// <summary>Collation key: case folded, trailing spaces trimmed, accents removed when the collation ignores them.</summary>
    public static string Fold(string s)
    {
        var t = s.TrimEnd(' ').ToUpperInvariant();
        if (!TSql.AccentInsensitive) return t;
        var decomposed = t.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var c in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
        return sb.ToString().Normalize(NormalizationForm.FormC).Replace("Å", "A");
    }
}
