using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PPDS.LiveTests.Query;

public sealed partial class DmlLiveDifferentialTests
{
    private const string N = "ppds_name";
    private const string Num = "ppds_num";
    private const string Amount = "ppds_amount";
    private const string MoneyCol = "ppds_money";
    private const string FloatCol = "ppds_float";
    private const string When = "ppds_when";
    private const string Day = "ppds_day";
    private const string Tzi = "ppds_tzi";
    private const string LegacyDay = "ppds_legacy";
    private const string Flag = "ppds_flag";
    private const string Choice = "ppds_choice";
    private const string Note = "ppds_note";
    private const string Memo = "ppds_memo";
    private const string Parent = "ppds_parent";
    private const string State = "statecode";
    private const string Status = "statuscode";

    private static readonly string[] Columns = [N, Num, Amount, MoneyCol, FloatCol, When, Day, Flag, Choice, Note, Memo, Parent, State, Status];

    /// <summary>
    /// A row as the test models it. Money and float are held as decimals rounded to 2 places, the lookup as the index of
    /// the seed row it points to, date-only values as a date at midnight, date/time values in UTC.
    /// </summary>
    private sealed record Row(Guid Id, Dictionary<string, object?> V)
    {
        public T? Get<T>(string column) => V.TryGetValue(column, out var value) && value is not null ? (T)value : default;

        public Row With(string column, object? value) => new(Id, new Dictionary<string, object?>(V) { [column] = value });

        public string Key => string.Join("|", Columns.Select(c => Format(V.GetValueOrDefault(c))));

        private static string Format(object? value) => value switch
        {
            null => "<null>",
            DateTime { TimeOfDay.Ticks: 0, Kind: not DateTimeKind.Utc } d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            DateTime d => d.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
            decimal m => m.ToString("0.00", CultureInfo.InvariantCulture),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString()!
        };
    }

    private Row Seed(string? name, int? num, decimal? amount, decimal? money, decimal? flt, DateTime? when, DateTime? day, bool flag,
        int? choice, string? note, string? memo, int? parent, int state)
        => new(Guid.Empty, new Dictionary<string, object?>
        {
            [N] = name, [Num] = num, [Amount] = amount, [MoneyCol] = money, [FloatCol] = flt, [When] = when, [Day] = day, [Flag] = flag,
            [Choice] = choice, [Note] = note, [Memo] = memo, [Parent] = parent, [State] = state, [Status] = state == 0 ? 1 : 2
        });

    /// <summary>
    /// NULLs in every column, accents, case variants, leading and trailing spaces, negative and zero numbers, dates near
    /// midnight UTC, active and inactive rows, and parent links (including to a row a case may delete).
    /// </summary>
    private Row[] SeedRows() =>
    [
        Seed("alpha", 1, 10.5m, 10.50m, 1.25m, Utc(2024, 1, 1, 0, 0), D(2024, 1, 15), true, _choices[0], "a", "line one", null, 0),
        Seed("Beta", 2, null, null, 2.5m, null, D(2024, 2, 29), false, _choices[1], null, null, 0, 0),
        Seed("gamma", null, 30m, 30m, null, Utc(2024, 6, 15, 12, 0), null, false, null, "  pad  ", "memo gamma", 0, 1),
        Seed("alp_ha", 4, 40.25m, 40.25m, -3.75m, Utc(2023, 12, 31, 23, 30), D(2023, 12, 31), true, _choices[2], "x", null, 1, 0),
        Seed(null, 5, 50m, 0m, 0m, Utc(2025, 1, 1, 0, 0), D(2025, 1, 1), false, _choices[0], "d5", "Ünïcode memo", null, 1),
        Seed("Café", 0, -1.5m, -1.50m, 100.5m, Utc(2024, 1, 15, 0, 30), D(2024, 1, 15), true, _choices[1], "trail  ", "x", 4, 0),
        Seed("cafe", -7, 0m, null, null, Utc(2024, 1, 14, 23, 59), null, false, null, "  lead", null, null, 0),
        Seed("trail  ", 7, 7.77m, 7.77m, 7.7m, null, D(2024, 3, 1), true, _choices[2], "Naïve", "memo", 6, 1),
    ];

    private static DateTime Utc(int y, int mo, int d, int h, int mi) => new(y, mo, d, h, mi, 0, DateTimeKind.Utc);

    private static DateTime D(int y, int mo, int d) => new(y, mo, d, 0, 0, 0, DateTimeKind.Unspecified);

    // ── T-SQL reference semantics (collation as observed on the organisation) ───────────────

    private const CompareOptions Collation = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;

    /// <summary>= under a case- and accent-insensitive collation: trailing spaces are ignored, leading ones are not.</summary>
    private static bool TEq(string? value, string literal)
        => value is not null && CultureInfo.InvariantCulture.CompareInfo.Compare(value.TrimEnd(' '), literal.TrimEnd(' '), Collation) == 0;

    /// <summary>LIKE on nvarchar under the same collation: % and _ wildcards; trailing spaces are significant for Unicode.</summary>
    private static bool TLike(string? value, string pattern)
    {
        if (value is null)
            return false;
        var regex = "^" + Regex.Escape(Fold(pattern)).Replace("%", ".*").Replace("_", ".") + "$";
        return Regex.IsMatch(Fold(value), regex, RegexOptions.Singleline);
    }

    private static string Fold(string text)
    {
        var builder = new StringBuilder();
        foreach (var ch in text.Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                builder.Append(ch);
        return builder.ToString().Normalize(NormalizationForm.FormC).ToLowerInvariant();
    }

    /// <summary>ORDER BY under the collation, NULLs first (as SQL Server sorts them).</summary>
    private static int TCompare(string? a, string? b) => (a, b) switch
    {
        (null, null) => 0,
        (null, _) => -1,
        (_, null) => 1,
        _ => CultureInfo.InvariantCulture.CompareInfo.Compare(a, b, Collation)
    };

    /// <summary>Deletes the matching rows; rows that pointed at a deleted row lose the link (the relationship removes it).</summary>
    private static List<Row> Delete(List<Row> rows, Func<Row, bool> match, Guid[] ids)
    {
        var deleted = rows.Where(match).Select(r => Array.IndexOf(ids, r.Id)).ToHashSet();
        return rows.Where(r => !match(r)).Select(r => r.Get<int?>(Parent) is { } p && deleted.Contains(p) ? r.With(Parent, null) : r).ToList();
    }

    /// <summary>A newly inserted row: the given values, every other column empty, active, flag false (the column default).</summary>
    private static Row Inserted(params (string Column, object? Value)[] values)
    {
        var v = Columns.ToDictionary(c => c, _ => (object?)null);
        v[Flag] = false;
        v[State] = 0;
        v[Status] = 1;
        foreach (var (column, value) in values)
            v[column] = value;
        return new Row(Guid.Empty, v);
    }
}
