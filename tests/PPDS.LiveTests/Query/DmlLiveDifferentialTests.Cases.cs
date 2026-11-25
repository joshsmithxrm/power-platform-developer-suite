using System.Globalization;
using PPDS.Auth.Profiles;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services.Query;

namespace PPDS.LiveTests.Query;

public sealed partial class DmlLiveDifferentialTests
{
    private static int? I(Row r, string c) => r.Get<int?>(c);
    private static decimal? M(Row r, string c) => r.Get<decimal?>(c);
    private static string? S(Row r, string c) => r.Get<string>(c);
    private static DateTime? T(Row r, string c) => r.Get<DateTime?>(c);
    private static bool? B(Row r, string c) => r.Get<bool?>(c);

    private const string All = "ppds_name IS NULL OR ppds_name IS NOT NULL";

    /// <summary>Text as Dataverse stores it: an empty string is stored as NULL.</summary>
    private static string? Stored(string? text) => string.IsNullOrEmpty(text) ? null : text;

    private static decimal? Away(decimal? v, int places) => v is null ? null : Math.Round(v.Value, places, MidpointRounding.AwayFromZero);

    /// <summary>A value as a 2-place column stores it.</summary>
    private static decimal? R2(decimal? v) => Away(v, 2);

    /// <summary>T-SQL money arithmetic keeps 4 places.</summary>
    private static decimal? Money4(decimal? v) => Away(v, 4);

    /// <summary>T-SQL float arithmetic (double), as a 2-place column stores it.</summary>
    private static decimal? Fl(decimal? a, decimal? b, Func<double, double, double> op)
        => a is null || b is null ? null : R2((decimal)op((double)a.Value, (double)b.Value));

    private static int? Trunc(decimal? v) => v is null ? null : (int)decimal.Truncate(v.Value);

    /// <summary>An UPDATE whose WHERE selects <paramref name="where"/>: those rows get <paramref name="change"/>.</summary>
    private Task Upd(string sql, Func<Row, bool> where, Func<Row, Row> change, bool observe = false, int? rowCap = null)
        => CaseAsync(sql, rows => rows.Select(r => where(r) ? change(r) : r).ToList(), where, observe, rowCap);

    /// <summary>An UPDATE of every row (WHERE <see cref="All"/>).</summary>
    private Task SetAll(string assignments, Func<Row, Row> change)
        => Upd($"UPDATE {_table} SET {assignments} WHERE {All}", _ => true, change);

    /// <summary>A DELETE of the rows <paramref name="where"/> selects (rows pointing at them lose the link).</summary>
    private Task Del(string sql, Func<Row, bool> where, bool observe = true)
        => CaseAsync(sql, rows => Delete(rows, where, _ids), where, observe);

    /// <summary>An INSERT that adds <paramref name="added"/> (computed from the rows before it).</summary>
    private Task Ins(string sql, Func<List<Row>, IEnumerable<Row>> added)
        => CaseAsync(sql, rows => rows.Concat(added(rows)).ToList());

    private async Task RunAllCasesAsync()
    {
        await WhereCasesAsync();
        await WriteCasesAsync();
        // Computed values: refused under the interim rule (see ComputedAsync).
        await ComputedAsync(ValueCasesAsync);
        await ComputedAsync(ArithmeticCasesAsync);
        await ComputedAsync(ChoiceFunctionCasesAsync);
        await ComputedAsync(ConversionCasesAsync);
        await InsertCasesAsync();
        await HintAndCapCasesAsync();
        await ErrorCasesAsync();
        await SelectChecksAsync();
        await RefusedCasesAsync();
        await ScriptCasesAsync();
        await RefusedScriptCaseAsync();
        await DeleteCasesAsync(); // last: deleting a seed row gives it a new id when it is recreated
    }

    // ── WHERE: UPDATE ... SET note = 'HIT' marks exactly the matched rows ────────────────────

    private async Task WhereCasesAsync()
    {
        var t = _table;
        int C(int i) => _choices[i];
        var id0 = _ids[0];
        var id1 = _ids[1];
        var id4 = _ids[4];
        var utc = (int y, int mo, int d, int h, int mi) => Utc(y, mo, d, h, mi);
        var thousand = string.Join(", ", new[] { -7, 2 }.Concat(Enumerable.Range(1000, 998)));

        var where = new (string Sql, Func<Row, bool> Match)[]
        {
            // whole numbers
            ("ppds_num = 2", r => I(r, Num) == 2),
            ("ppds_num <> 2", r => I(r, Num) is { } n && n != 2),
            ("ppds_num != 2", r => I(r, Num) is { } n && n != 2),
            ("ppds_num < 2", r => I(r, Num) < 2),
            ("ppds_num > 2", r => I(r, Num) > 2),
            ("ppds_num <= 2", r => I(r, Num) <= 2),
            ("ppds_num >= 2", r => I(r, Num) >= 2),
            ("ppds_num = -1", _ => false),
            ("ppds_num = -7", r => I(r, Num) == -7),
            ("ppds_num IN (1, 2)", r => I(r, Num) is 1 or 2),
            ("ppds_num IN (2)", r => I(r, Num) == 2),
            ("ppds_num IN (-7, 0)", r => I(r, Num) is -7 or 0),
            ($"ppds_num IN ({thousand})", r => I(r, Num) is -7 or 2),
            ("ppds_num NOT IN (1, 2)", r => I(r, Num) is { } n && n is not (1 or 2)),
            ("ppds_num BETWEEN 1 AND 2", r => I(r, Num) >= 1 && I(r, Num) <= 2),
            ("ppds_num NOT BETWEEN 1 AND 2", r => I(r, Num) is { } n && !(n >= 1 && n <= 2)),
            ("ppds_num IS NULL", r => I(r, Num) is null),
            ("ppds_num IS NOT NULL", r => I(r, Num) is not null),
            // decimal
            ("ppds_amount = 10.5", r => M(r, Amount) == 10.5m),
            ("ppds_amount = -1.5", r => M(r, Amount) == -1.5m),
            ("ppds_amount = 0", r => M(r, Amount) == 0m),
            ("ppds_amount <> 30", r => M(r, Amount) is { } a && a != 30m),
            ("ppds_amount != 30", r => M(r, Amount) is { } a && a != 30m),
            ("ppds_amount < 10.5", r => M(r, Amount) < 10.5m),
            ("ppds_amount > 20", r => M(r, Amount) > 20m),
            ("ppds_amount <= 10.5", r => M(r, Amount) <= 10.5m),
            ("ppds_amount >= 30", r => M(r, Amount) >= 30m),
            ("ppds_amount < 0", r => M(r, Amount) < 0m),
            ("ppds_amount BETWEEN 0 AND 30", r => M(r, Amount) >= 0m && M(r, Amount) <= 30m),
            ("ppds_amount BETWEEN -10.5 AND 0", r => M(r, Amount) >= -10.5m && M(r, Amount) <= 0m),
            ("ppds_amount NOT BETWEEN 0 AND 30", r => M(r, Amount) is { } a && !(a >= 0m && a <= 30m)),
            ("ppds_amount IN (10.5, 30)", r => M(r, Amount) is 10.5m or 30m),
            ("ppds_amount IN (-1.5, 30)", r => M(r, Amount) is -1.5m or 30m),
            ("ppds_amount NOT IN (10.5)", r => M(r, Amount) is { } a && a != 10.5m),
            ("ppds_amount IS NULL", r => M(r, Amount) is null),
            ("ppds_amount IS NOT NULL", r => M(r, Amount) is not null),
            // money
            ("ppds_money = 10.5", r => M(r, MoneyCol) == 10.5m),
            ("ppds_money <> 0", r => M(r, MoneyCol) is { } m && m != 0m),
            ("ppds_money != 0", r => M(r, MoneyCol) is { } m && m != 0m),
            ("ppds_money < 10.5", r => M(r, MoneyCol) < 10.5m),
            ("ppds_money > 10.5", r => M(r, MoneyCol) > 10.5m),
            ("ppds_money <= 7.77", r => M(r, MoneyCol) <= 7.77m),
            ("ppds_money >= 30", r => M(r, MoneyCol) >= 30m),
            ("ppds_money BETWEEN 0 AND 10.5", r => M(r, MoneyCol) >= 0m && M(r, MoneyCol) <= 10.5m),
            ("ppds_money NOT BETWEEN 0 AND 10.5", r => M(r, MoneyCol) is { } m && !(m >= 0m && m <= 10.5m)),
            ("ppds_money IN (0, 30)", r => M(r, MoneyCol) is 0m or 30m),
            ("ppds_money IN (-1.5)", r => M(r, MoneyCol) == -1.5m),
            ("ppds_money NOT IN (0, 30)", r => M(r, MoneyCol) is { } m && m is not (0m or 30m)),
            ("ppds_money IS NULL", r => M(r, MoneyCol) is null),
            ("ppds_money IS NOT NULL", r => M(r, MoneyCol) is not null),
            // float
            ("ppds_float = 2.5", r => M(r, FloatCol) == 2.5m),
            ("ppds_float = 7.7", r => M(r, FloatCol) == 7.7m),
            ("ppds_float <> 0", r => M(r, FloatCol) is { } f && f != 0m),
            ("ppds_float != 0", r => M(r, FloatCol) is { } f && f != 0m),
            ("ppds_float < 1.25", r => M(r, FloatCol) < 1.25m),
            ("ppds_float > 2.5", r => M(r, FloatCol) > 2.5m),
            ("ppds_float <= 0", r => M(r, FloatCol) <= 0m),
            ("ppds_float >= 7.7", r => M(r, FloatCol) >= 7.7m),
            ("ppds_float BETWEEN -5 AND 2.5", r => M(r, FloatCol) >= -5m && M(r, FloatCol) <= 2.5m),
            ("ppds_float NOT BETWEEN -5 AND 2.5", r => M(r, FloatCol) is { } f && !(f >= -5m && f <= 2.5m)),
            ("ppds_float IS NULL", r => M(r, FloatCol) is null),
            ("ppds_float IS NOT NULL", r => M(r, FloatCol) is not null),
            // text, collation (case- and accent-insensitive, trailing spaces ignored by =)
            ("ppds_name = 'alpha'", r => TEq(S(r, N), "alpha")),
            ("ppds_name = N'alpha'", r => TEq(S(r, N), "alpha")),
            ("ppds_name = 'ALPHA'", r => TEq(S(r, N), "ALPHA")),
            ("ppds_name = 'cafe'", r => TEq(S(r, N), "cafe")),
            // With N: text without N that is not 7-bit ASCII is refused in a condition (review round 1, MF3).
            ("ppds_name = N'CAFÉ'", r => TEq(S(r, N), "CAFÉ")),
            ("ppds_name = 'trail'", r => TEq(S(r, N), "trail")),
            ("ppds_note = 'lead'", r => TEq(S(r, Note), "lead")),
            ("ppds_note = '  lead'", r => TEq(S(r, Note), "  lead")),
            ("ppds_note = 'naive'", r => TEq(S(r, Note), "naive")),
            ("ppds_name <> 'alpha'", r => S(r, N) is not null && !TEq(S(r, N), "alpha")),
            ("ppds_name <> 'cafe'", r => S(r, N) is not null && !TEq(S(r, N), "cafe")),
            ("ppds_name != 'trail'", r => S(r, N) is not null && !TEq(S(r, N), "trail")),
            ("ppds_note = 'trail'", r => TEq(S(r, Note), "trail")),
            ("ppds_note LIKE 'trail'", r => TLike(S(r, Note), "trail")),
            ("ppds_note LIKE '%pad%'", r => TLike(S(r, Note), "%pad%")),
            ("ppds_note LIKE 'trail %'", r => TLike(S(r, Note), "trail %")),
            ("ppds_note NOT LIKE 'trail %'", r => S(r, Note) is not null && !TLike(S(r, Note), "trail %")),
            ("ppds_note LIKE ''", r => TLike(S(r, Note), "")),
            ("ppds_name IN ('alpha', 'gamma')", r => TEq(S(r, N), "alpha") || TEq(S(r, N), "gamma")),
            ("ppds_name IN ('cafe', 'beta')", r => TEq(S(r, N), "cafe") || TEq(S(r, N), "beta")),
            ("ppds_name IN ('beta')", r => TEq(S(r, N), "beta")),
            ("ppds_name IN (N'cafe')", r => TEq(S(r, N), "cafe")),
            ("ppds_name NOT IN ('alpha', 'gamma')", r => S(r, N) is not null && !TEq(S(r, N), "alpha") && !TEq(S(r, N), "gamma")),
            ("ppds_name NOT IN ('cafe')", r => S(r, N) is not null && !TEq(S(r, N), "cafe")),
            ("ppds_name LIKE 'al%'", r => TLike(S(r, N), "al%")),
            ("ppds_name LIKE N'al%'", r => TLike(S(r, N), "al%")),
            ("ppds_name LIKE '%'", r => TLike(S(r, N), "%")),
            ("ppds_name LIKE '%mm%'", r => TLike(S(r, N), "%mm%")),
            ("ppds_name LIKE '%a'", r => TLike(S(r, N), "%a")),
            ("ppds_name LIKE 'a%a'", r => TLike(S(r, N), "a%a")),
            ("ppds_name LIKE 'alp_ha'", r => TLike(S(r, N), "alp_ha")),
            // wildcards inside a leading or trailing % pattern (sent whole as like, never as begins-with/ends-with)
            ("ppds_name LIKE 'alp_h%'", r => TLike(S(r, N), "alp_h%")),
            ("ppds_name LIKE '%l_ha'", r => TLike(S(r, N), "%l_ha")),
            ("ppds_name LIKE 'a%h%'", r => TLike(S(r, N), "a%h%")),
            ("ppds_name LIKE 'ca_e'", r => TLike(S(r, N), "ca_e")),
            ("ppds_name NOT LIKE '%a%a'", r => S(r, N) is not null && !TLike(S(r, N), "%a%a")),
            ("ppds_note NOT LIKE 'tr_il%'", r => S(r, Note) is not null && !TLike(S(r, Note), "tr_il%")),
            ("ppds_name LIKE 'caf%'", r => TLike(S(r, N), "caf%")),
            ("ppds_name LIKE 'trail'", r => TLike(S(r, N), "trail")),
            ("ppds_name LIKE 'trail%'", r => TLike(S(r, N), "trail%")),
            ("ppds_name NOT LIKE 'al%'", r => S(r, N) is not null && !TLike(S(r, N), "al%")),
            ("ppds_name NOT LIKE 'caf%'", r => S(r, N) is not null && !TLike(S(r, N), "caf%")),
            ("ppds_name NOT LIKE '%a'", r => S(r, N) is not null && !TLike(S(r, N), "%a")),
            ("ppds_name NOT LIKE '%mm%'", r => S(r, N) is not null && !TLike(S(r, N), "%mm%")),
            ("ppds_name NOT LIKE 'alp_ha'", r => S(r, N) is not null && !TLike(S(r, N), "alp_ha")),
            // NOT LIKE '%' matches no row; NOT LIKE '' every non-NULL text; NOT LIKE an exact text the others
            ("ppds_name NOT LIKE '%'", r => S(r, N) is not null && !TLike(S(r, N), "%")),
            ("ppds_name NOT LIKE ''", r => S(r, N) is not null && !TLike(S(r, N), "")),
            ("ppds_name NOT LIKE 'alpha'", r => S(r, N) is not null && !TLike(S(r, N), "alpha")),
            ("ppds_note IS NULL", r => S(r, Note) is null),
            ("ppds_note IS NOT NULL", r => S(r, Note) is not null),
            // multiline text
            ("ppds_memo = 'memo'", r => TEq(S(r, Memo), "memo")),
            ("ppds_memo = 'unicode memo'", r => TEq(S(r, Memo), "unicode memo")),
            ("ppds_memo LIKE 'memo%'", r => TLike(S(r, Memo), "memo%")),
            ("ppds_memo <> 'memo'", r => S(r, Memo) is not null && !TEq(S(r, Memo), "memo")),
            ("ppds_memo != 'memo'", r => S(r, Memo) is not null && !TEq(S(r, Memo), "memo")),
            ("ppds_memo NOT LIKE 'memo%'", r => S(r, Memo) is not null && !TLike(S(r, Memo), "memo%")),
            ("ppds_memo IS NULL", r => S(r, Memo) is null),
            ("ppds_memo IS NOT NULL", r => S(r, Memo) is not null),
            // yes/no (one row has no value)
            ("ppds_flag = 1", r => B(r, Flag) == true),
            ("ppds_flag = 0", r => B(r, Flag) == false),
            ("ppds_flag <> 1", r => B(r, Flag) == false),
            ("ppds_flag != 1", r => B(r, Flag) == false),
            ("ppds_flag <> 0", r => B(r, Flag) == true),
            // choice
            ($"ppds_choice = {C(0)}", r => I(r, Choice) == C(0)),
            ($"ppds_choice <> {C(0)}", r => I(r, Choice) is { } c && c != C(0)),
            ($"ppds_choice != {C(0)}", r => I(r, Choice) is { } c && c != C(0)),
            ($"ppds_choice IN ({C(0)}, {C(1)})", r => I(r, Choice) == C(0) || I(r, Choice) == C(1)),
            ($"ppds_choice NOT IN ({C(0)})", r => I(r, Choice) is { } c && c != C(0)),
            ("ppds_choice IS NULL", r => I(r, Choice) is null),
            ("ppds_choice IS NOT NULL", r => I(r, Choice) is not null),
            // date/time (user local): literals with Z or an offset
            ("ppds_when < '2024-01-01T00:00:00-01:00'", r => T(r, When) < utc(2024, 1, 1, 1, 0)),
            ("ppds_when BETWEEN '2024-01-01T00:00:00Z' AND '2024-06-15T12:00:00Z'", r => T(r, When) >= utc(2024, 1, 1, 0, 0) && T(r, When) <= utc(2024, 6, 15, 12, 0)),
            ("ppds_when >= '2024-01-01T00:00:00Z'", r => T(r, When) >= utc(2024, 1, 1, 0, 0)),
            ("ppds_when < '2024-01-15T00:00:00Z'", r => T(r, When) < utc(2024, 1, 15, 0, 0)),
            ("ppds_when > '2024-01-15T00:00:00+01:00'", r => T(r, When) > utc(2024, 1, 14, 23, 0)),
            ("ppds_when = '2024-06-15T12:00:00Z'", r => T(r, When) == utc(2024, 6, 15, 12, 0)),
            ("ppds_when = '2024-06-15T14:00:00+02:00'", r => T(r, When) == utc(2024, 6, 15, 12, 0)),
            ("ppds_when <> '2024-06-15T12:00:00Z'", r => T(r, When) is { } w && w != utc(2024, 6, 15, 12, 0)),
            ("ppds_when != '2024-06-15T12:00:00Z'", r => T(r, When) is { } w && w != utc(2024, 6, 15, 12, 0)),
            ("ppds_when <= '2024-01-15T00:30:00Z'", r => T(r, When) <= utc(2024, 1, 15, 0, 30)),
            ("ppds_when NOT BETWEEN '2024-01-01T00:00:00Z' AND '2024-06-15T12:00:00Z'", r => T(r, When) is { } w && !(w >= utc(2024, 1, 1, 0, 0) && w <= utc(2024, 6, 15, 12, 0))),
            ("ppds_when IS NULL", r => T(r, When) is null),
            ("ppds_when IS NOT NULL", r => T(r, When) is not null),
            // date only
            ("ppds_day = '2024-01-15'", r => T(r, Day) == D(2024, 1, 15)),
            ("ppds_day = '2024-02-29'", r => T(r, Day) == D(2024, 2, 29)),
            ("ppds_day <> '2024-01-15'", r => T(r, Day) is { } d && d != D(2024, 1, 15)),
            ("ppds_day != '2024-01-15'", r => T(r, Day) is { } d && d != D(2024, 1, 15)),
            ("ppds_day < '2024-01-15'", r => T(r, Day) < D(2024, 1, 15)),
            ("ppds_day > '2024-02-29'", r => T(r, Day) > D(2024, 2, 29)),
            ("ppds_day <= '2024-01-15'", r => T(r, Day) <= D(2024, 1, 15)),
            ("ppds_day >= '2024-02-29'", r => T(r, Day) >= D(2024, 2, 29)),
            ("ppds_day BETWEEN '2024-01-01' AND '2024-02-29'", r => T(r, Day) >= D(2024, 1, 1) && T(r, Day) <= D(2024, 2, 29)),
            ("ppds_day NOT BETWEEN '2024-01-01' AND '2024-02-29'", r => T(r, Day) is { } d && !(d >= D(2024, 1, 1) && d <= D(2024, 2, 29))),
            ("ppds_day IS NULL", r => T(r, Day) is null),
            ("ppds_day IS NOT NULL", r => T(r, Day) is not null),
            // primary key: GUID literal forms
            ($"{Pk} = '{id0}'", r => r.Id == id0),
            ($"{Pk} = '{id0.ToString().ToUpperInvariant()}'", r => r.Id == id0),
            ($"{Pk} = '{id0:B}'", r => r.Id == id0),
            ($"{Pk} <> '{id0}'", r => r.Id != id0),
            ($"{Pk} != '{id0}'", r => r.Id != id0),
            ($"{Pk} IN ('{id0}', '{id1}')", r => r.Id == id0 || r.Id == id1),
            ($"{Pk} IN ('{id0:B}', '{id1}')", r => r.Id == id0 || r.Id == id1),
            ($"{Pk} NOT IN ('{id0}')", r => r.Id != id0),
            // lookup by id
            ($"ppds_parent = '{id0}'", r => I(r, Parent) == 0),
            ($"ppds_parent = '{id0:B}'", r => I(r, Parent) == 0),
            ($"ppds_parent <> '{id0}'", r => I(r, Parent) is { } p && p != 0),
            ($"ppds_parent != '{id0}'", r => I(r, Parent) is { } p && p != 0),
            ($"ppds_parent IN ('{id0}', '{id4}')", r => I(r, Parent) is 0 or 4),
            ($"ppds_parent IN ('{id0:B}')", r => I(r, Parent) == 0),
            ($"ppds_parent NOT IN ('{id0}')", r => I(r, Parent) is { } p && p != 0),
            ("ppds_parent IS NULL", r => I(r, Parent) is null),
            ("ppds_parent IS NOT NULL", r => I(r, Parent) is not null),
            // owner by id (every row is the caller's: proven only at the extremes, see specs/query.md)
            ($"ownerid = '{_me}'", _ => true),
            ($"ownerid <> '{_me}'", _ => false),
            ($"ownerid != '{_me}'", _ => false),
            // status and status reason (rows on both sides)
            ("statecode = 0", r => I(r, State) == 0),
            ("statecode = 1", r => I(r, State) == 1),
            ("statecode <> 0", r => I(r, State) != 0),
            ("statecode != 0", r => I(r, State) != 0),
            ("statecode IN (0, 1)", _ => true),
            ("statecode IN (1)", r => I(r, State) == 1),
            ("statecode NOT IN (1)", r => I(r, State) != 1),
            ("statuscode = 1", r => I(r, Status) == 1),
            ("statuscode = 2", r => I(r, Status) == 2),
            ("statuscode <> 1", r => I(r, Status) != 1),
            ("statuscode != 1", r => I(r, Status) != 1),
            ("statuscode IN (1, 2)", _ => true),
            ("statuscode IN (2)", r => I(r, Status) == 2),
            ("statuscode NOT IN (2)", r => I(r, Status) != 2),
            // precedence and nesting
            ("ppds_num = 1 OR ppds_num = 2 AND ppds_amount > 100", r => I(r, Num) == 1 || (I(r, Num) == 2 && M(r, Amount) > 100m)),
            ("(ppds_num = 1 OR ppds_num = 2) AND ppds_amount IS NULL", r => (I(r, Num) == 1 || I(r, Num) == 2) && M(r, Amount) is null),
            ("(ppds_name = 'cafe' OR ppds_num > 4) AND statecode = 0", r => (TEq(S(r, N), "cafe") || I(r, Num) > 4) && I(r, State) == 0),
            ("((ppds_num = 1 OR (ppds_num = 2 AND (ppds_amount IS NULL OR ppds_amount > 5))) OR ppds_name = 'gamma')",
                r => I(r, Num) == 1 || (I(r, Num) == 2 && (M(r, Amount) is null || M(r, Amount) > 5m)) || TEq(S(r, N), "gamma")),
            // negated conditions inside OR and nested parentheses: the not-null pairing stays in its own branch
            ("ppds_num = 1 OR ppds_note <> 'x'", r => I(r, Num) == 1 || (S(r, Note) is not null && !TEq(S(r, Note), "x"))),
            ("ppds_num = 5 OR ppds_name NOT IN ('alpha')", r => I(r, Num) == 5 || (S(r, N) is not null && !TEq(S(r, N), "alpha"))),
            ("(ppds_num = 2 OR (ppds_flag = 1 AND ppds_memo NOT LIKE 'memo%'))", r => I(r, Num) == 2 || (B(r, Flag) == true && S(r, Memo) is not null && !TLike(S(r, Memo), "memo%"))),
            ("ppds_choice IS NULL OR ppds_amount <> 30", r => I(r, Choice) is null || (M(r, Amount) is { } a && a != 30m)),
        };
        foreach (var (sql, match) in where)
            await Upd($"UPDATE {t} SET ppds_note = 'HIT' WHERE {sql}", match, r => r.With(Note, "HIT"), observe: true);

        Func<Row, bool> Num1(int n) => r => I(r, Num) == n;
        await Upd($"UPDATE dbo.{t} SET ppds_note = 'HIT' WHERE ppds_num = 1", Num1(1), r => r.With(Note, "HIT"));
        await Upd($"UPDATE a SET ppds_note = 'ALIAS' FROM dbo.{t} a WHERE a.ppds_num = 2", Num1(2), r => r.With(Note, "ALIAS"));
        await Upd($"UPDATE a SET ppds_note = 'ALIAS' FROM {t} a WHERE a.ppds_num = 1", Num1(1), r => r.With(Note, "ALIAS"));
        // the table name as qualifier, FROM the table itself without an alias, and upper-case and bracketed names
        await Upd($"UPDATE {t} SET ppds_note = 'Q' WHERE {t}.ppds_num = 1", Num1(1), r => r.With(Note, "Q"));
        await Upd($"UPDATE {t} SET ppds_note = 'F' FROM {t} WHERE {t}.ppds_num = 2", Num1(2), r => r.With(Note, "F"));
        await Upd($"UPDATE dbo.{t} SET ppds_note = 'F' FROM {t} WHERE ppds_num = 4", Num1(4), r => r.With(Note, "F"));
        await Upd($"UPDATE [{t.ToUpperInvariant()}] SET [PPDS_NOTE] = 'UP' WHERE [PPDS_NUM] = 1", Num1(1), r => r.With(Note, "UP"));
        await Upd($"UPDATE {t.ToUpperInvariant()} SET PPDS_NOTE = PPDS_NAME WHERE PPDS_NUM = 2", Num1(2), r => r.With(Note, S(r, N)));

        // no WHERE, with the environment's no-WHERE prevention switched off
        await WithSettingsAsync(new QuerySafetySettings { PreventUpdateWithoutWhere = false },
            () => Upd($"UPDATE {t} SET ppds_note = 'ALL'", _ => true, r => r.With(Note, "ALL")));
    }

    // ── writes: every column type, values and NULL ───────────────────────────────────────────

    private async Task WriteCasesAsync()
    {
        var t = _table;
        int C(int i) => _choices[i];
        var id0 = _ids[0];
        var id4 = _ids[4];

        Task Set(string assignment, Func<Row, Row> change, string where = All, Func<Row, bool>? match = null)
            => Upd($"UPDATE {t} SET {assignment} WHERE {where}", match ?? (_ => true), change);

        await Set("ppds_when = '2024-05-05T10:00:00Z'", r => r.With(When, Utc(2024, 5, 5, 10, 0)));
        await Set("ppds_when = '2024-05-05T10:00:00+02:00'", r => r.With(When, Utc(2024, 5, 5, 8, 0)));
        await Set("ppds_when = '2024-05-05T03:00:00-05:00'", r => r.With(When, Utc(2024, 5, 5, 8, 0)));
        await Set("ppds_when = NULL", r => r.With(When, null));
        await Set("ppds_day = '2024-05-05'", r => r.With(Day, D(2024, 5, 5)));
        await Set("ppds_day = NULL", r => r.With(Day, null));
        await Set($"ppds_choice = {C(2)}", r => r.With(Choice, C(2)));
        await Set("ppds_choice = NULL", r => r.With(Choice, null));
        await Set("ppds_flag = 0", r => r.With(Flag, false));
        await Set("ppds_flag = 1", r => r.With(Flag, true));
        await Set("ppds_flag = NULL", r => r.With(Flag, null));
        // C-M2: rows 1 and 2 already point at row 0; the selected-rows check sees a write to them, value unchanged or not
        await Set($"ppds_parent = '{id0}'", r => r.With(Parent, 0), "ppds_num = 4", r => I(r, Num) == 4);
        await Set($"ppds_parent = '{id0.ToString("B").ToUpperInvariant()}'", r => r.With(Parent, 0), "ppds_num = 4", r => I(r, Num) == 4);
        await Set($"ppds_parent = CAST('{id4}' AS uniqueidentifier)", r => r.With(Parent, 4), "ppds_num = 4", r => I(r, Num) == 4);
        await Set("ppds_parent = NULL", r => r.With(Parent, null));
        await Set("ppds_money = 12.34", r => r.With(MoneyCol, 12.34m));
        await ComputedAsync(async () =>
        {
            await Set("ppds_money = ppds_money * 2", r => r.With(MoneyCol, M(r, MoneyCol) * 2));
            await Set("ppds_money = ppds_money + 1", r => r.With(MoneyCol, M(r, MoneyCol) + 1));
            await Set("ppds_float = ppds_float + 1", r => r.With(FloatCol, M(r, FloatCol) + 1));
            await Set("ppds_float = ppds_float * 2", r => r.With(FloatCol, M(r, FloatCol) * 2));
            await Set("ppds_memo = CONCAT(ppds_memo, '!')", r => r.With(Memo, (S(r, Memo) ?? "") + "!"));
        });
        await Set("ppds_money = NULL", r => r.With(MoneyCol, null));
        await Set("ppds_float = 3.5", r => r.With(FloatCol, 3.5m));
        await Set("ppds_float = NULL", r => r.With(FloatCol, null));
        await Set("ppds_memo = 'new memo'", r => r.With(Memo, "new memo"));
        await Set("ppds_memo = NULL", r => r.With(Memo, null));
        await Set("ppds_num = NULL", r => r.With(Num, null));
        await Set("ppds_amount = NULL", r => r.With(Amount, null));
        await Set("ppds_amount = 10.555", r => r.With(Amount, 10.56m));
        await Set("ppds_amount = -2.5", r => r.With(Amount, -2.5m));
        await Set("ppds_note = NULL, ppds_num = 3", r => r.With(Note, null).With(Num, 3));
        await Set("ppds_note = N'naïve'", r => r.With(Note, "naïve"));
        await Set("statecode = 1, statuscode = 2", r => r.With(State, 1).With(Status, 2), "statecode = 0", r => I(r, State) == 0);
        await Set("statecode = 0, statuscode = 1", r => r.With(State, 0).With(Status, 1), "statecode = 1", r => I(r, State) == 1);

        // each number column from each number column, text between text and multiline text, whole and negative literals
        await SetAll("ppds_amount = ppds_num, ppds_money = ppds_amount, ppds_float = ppds_money, ppds_memo = ppds_name, ppds_name = ppds_memo",
            r => r.With(Amount, I(r, Num)).With(MoneyCol, M(r, Amount)).With(FloatCol, M(r, MoneyCol)).With(Memo, S(r, N)).With(N, S(r, Memo)));
        await SetAll("ppds_amount = ppds_money, ppds_money = ppds_num, ppds_float = ppds_amount",
            r => r.With(Amount, M(r, MoneyCol)).With(MoneyCol, I(r, Num)).With(FloatCol, M(r, Amount)));
        await SetAll("ppds_float = ppds_num, ppds_amount = 1, ppds_money = -1",
            r => r.With(FloatCol, I(r, Num)).With(Amount, 1m).With(MoneyCol, -1m));
        await SetAll("ppds_float = -1, ppds_memo = N'ň memo', ppds_note = '2024-01-01'",
            r => r.With(FloatCol, -1m).With(Memo, "ň memo").With(Note, "2024-01-01"));
        await SetAll("ppds_note = 'lit'", r => r.With(Note, "lit"));

        // implicit conversions of a decimal or floating-point value to a whole number truncate toward zero, as in SQL
        await SetAll("ppds_num = ppds_amount", r => r.With(Num, Trunc(M(r, Amount))));
        await SetAll("ppds_num = ppds_float", r => r.With(Num, Trunc(M(r, FloatCol))));
        await SetAll("ppds_num = 2.7", r => r.With(Num, 2));
        await SetAll("ppds_num = -2.7", r => r.With(Num, -2));

        // the GUID cast in each admitted spelling (braces, lower case)
        await Set($"ppds_parent = CAST('{{{id4.ToString().ToUpperInvariant()}}}' AS uniqueidentifier)", r => r.With(Parent, 4), "ppds_num = 4", r => I(r, Num) == 4);
        await Set($"ppds_parent = CAST('{id0.ToString().ToLowerInvariant()}' AS uniqueidentifier)", r => r.With(Parent, 0), "ppds_num = 4", r => I(r, Num) == 4);
    }

    // ── values the engine computes ───────────────────────────────────────────────────────────

    private async Task ValueCasesAsync()
    {
        string? Prefix(string? s, int n) => s is null ? null : s.Length <= n ? s : s[..n];

        var notes = new (string Expr, Func<Row, string?> Value)[]
        {
            ("COALESCE(ppds_note, ppds_name)", r => S(r, Note) ?? S(r, N)),
            ("ISNULL(ppds_note, 'none')", r => S(r, Note) ?? "none"),
            ("IIF(ppds_num > 2, 'big', 'small')", r => I(r, Num) > 2 ? "big" : "small"),
            ("IIF(ppds_note IS NULL, 'none', 'some')", r => S(r, Note) is null ? "none" : "some"),
            ("IIF(ppds_amount > ppds_num, 'amount', 'num')", r => M(r, Amount) > I(r, Num) ? "amount" : "num"),
            ("CASE WHEN ppds_amount > 20 THEN 'hi' ELSE 'lo' END", r => M(r, Amount) > 20m ? "hi" : "lo"),
            ("CASE WHEN ppds_note IS NULL THEN 'none' ELSE ppds_note END", r => S(r, Note) ?? "none"),
            ("CASE WHEN ppds_num > 2 THEN CASE WHEN ppds_amount > 20 THEN 'a' ELSE 'b' END ELSE 'c' END",
                r => I(r, Num) > 2 ? (M(r, Amount) > 20m ? "a" : "b") : "c"),
            ("IIF(ppds_num > 1 AND ppds_amount > 20, 'both', 'no')", r => I(r, Num) > 1 && M(r, Amount) > 20m ? "both" : "no"),
            ("CASE WHEN ppds_num > 4 OR ppds_amount > 30 THEN 'y' ELSE 'n' END", r => I(r, Num) > 4 || M(r, Amount) > 30m ? "y" : "n"),
            ("CASE WHEN ppds_num > 1 AND ppds_num < 5 THEN 'mid' END", r => I(r, Num) > 1 && I(r, Num) < 5 ? "mid" : null),
            ("CASE WHEN ppds_money IS NULL OR ppds_float IS NULL THEN 'gap' ELSE 'full' END", r => M(r, MoneyCol) is null || M(r, FloatCol) is null ? "gap" : "full"),
            ("UPPER(ppds_name)", r => S(r, N)?.ToUpperInvariant()),
            ("LOWER(ppds_name)", r => S(r, N)?.ToLowerInvariant()),
            ("LTRIM(ppds_note)", r => Stored(S(r, Note)?.TrimStart(' '))),
            ("RTRIM(ppds_note)", r => Stored(S(r, Note)?.TrimEnd(' '))),
            ("TRIM(ppds_note)", r => Stored(S(r, Note)?.Trim(' '))),
            ("UPPER(ppds_memo)", r => S(r, Memo)?.ToUpperInvariant()),
            ("CONCAT(ppds_name, '-', ppds_note)", r => (S(r, N) ?? "") + "-" + (S(r, Note) ?? "")),
            ("CONCAT(ppds_memo, '|', ppds_name)", r => (S(r, Memo) ?? "") + "|" + (S(r, N) ?? "")),
            ("SUBSTRING(ppds_name, 2, 3)", r => S(r, N) is not { } s ? null : Stored(s.Length < 2 ? "" : s.Substring(1, Math.Min(3, s.Length - 1)))),
            ("SUBSTRING(ppds_name, 3, 100)", r => S(r, N) is not { } s ? null : Stored(s.Length < 3 ? "" : s[2..])),
            ("SUBSTRING(ppds_name, 1, 0)", _ => null),
            ("SUBSTRING(ppds_name, 1, 3)", r => Prefix(S(r, N), 3)),
            ("CAST(ppds_num AS nvarchar(10))", r => I(r, Num)?.ToString(CultureInfo.InvariantCulture)),
            ("CONVERT(nvarchar(10), ppds_num)", r => I(r, Num)?.ToString(CultureInfo.InvariantCulture)),
            ("CAST(ppds_name AS nvarchar(3))", r => Prefix(S(r, N), 3)),
            ("CONVERT(nvarchar(4), ppds_memo)", r => Prefix(S(r, Memo), 4)),
            ("CAST(ppds_name AS nvarchar(1))", r => Prefix(S(r, N), 1)),
            ("CONVERT(nvarchar(1), ppds_memo)", r => Prefix(S(r, Memo), 1)),
            ("CAST(ppds_memo AS nvarchar(4000))", r => S(r, Memo)),
            ("CONVERT(nvarchar(4000), ppds_name)", r => S(r, N)),
            ("CAST('lit' AS nvarchar(2))", _ => "li"),
            ("CONCAT(ppds_name, ppds_num)", r => (S(r, N) ?? "") + (I(r, Num)?.ToString(CultureInfo.InvariantCulture) ?? "")),
            ("CONCAT(ppds_name, '#', CONVERT(nvarchar(10), ppds_num * 2))", r => (S(r, N) ?? "") + "#" + ((I(r, Num) * 2)?.ToString(CultureInfo.InvariantCulture) ?? "")),
            ("CONCAT(ppds_name, CAST(ppds_amount AS int))", r => (S(r, N) ?? "") + (Trunc(M(r, Amount))?.ToString(CultureInfo.InvariantCulture) ?? "")),
            // D-2: the rewrite the currency refusals offer (decimal(19, 4), then int, truncates)
            ("CAST(CAST(CAST(ppds_money AS decimal(19, 4)) AS int) AS nvarchar(20))", r => Trunc(M(r, MoneyCol))?.ToString(CultureInfo.InvariantCulture)),
        };
        foreach (var (expr, value) in notes)
            await SetAll($"ppds_note = {expr}", r => r.With(Note, Stored(value(r))));

        var nums = new (string Expr, Func<Row, int?> Value)[]
        {
            ("LEN(ppds_note)", r => S(r, Note)?.TrimEnd(' ').Length),
            ("LEN(ppds_memo)", r => S(r, Memo)?.TrimEnd(' ').Length),
            ("ppds_num * 2 + 1", r => I(r, Num) * 2 + 1),
            ("ABS(ppds_num - 3)", r => I(r, Num) is { } n ? Math.Abs(n - 3) : null),
            ("-ppds_num", r => -I(r, Num)),
            ("-(ppds_num + 1)", r => -(I(r, Num) + 1)),
            ("ppds_num % 2", r => I(r, Num) % 2),
            ("ppds_num / 2", r => I(r, Num) / 2),
            ("NULLIF(ppds_num, 2)", r => I(r, Num) == 2 ? null : I(r, Num)),
            ("CASE ppds_num WHEN 1 THEN 10 WHEN 2 THEN 20 ELSE 0 END", r => I(r, Num) switch { 1 => 10, 2 => 20, _ => 0 }),
            ("ISNULL(ppds_num, 0)", r => I(r, Num) ?? 0),
            ("COALESCE(ppds_num, -1)", r => I(r, Num) ?? -1),
            ("IIF(ppds_num > 2, 1, 0)", r => I(r, Num) > 2 ? 1 : 0),
            ("CAST(ppds_amount AS int)", r => Trunc(M(r, Amount))),
            ("CONVERT(int, ppds_amount)", r => Trunc(M(r, Amount))),
            ("TRY_CAST(ppds_amount AS int)", r => Trunc(M(r, Amount))),
            ("TRY_CONVERT(int, ppds_amount)", r => Trunc(M(r, Amount))),
            // D-2: currency to a whole number only through decimal(19, 4), which truncates as SQL Server truncates a decimal
            ("CAST(CAST(ppds_money AS decimal(19, 4)) AS int)", r => Trunc(M(r, MoneyCol))),
            ("TRY_CAST(CAST(ppds_money AS decimal(19, 4)) AS int)", r => Trunc(M(r, MoneyCol))),
        };
        foreach (var (expr, value) in nums)
            await SetAll($"ppds_num = {expr}", r => r.With(Num, value(r)));

        // decimal(p, s): rounded to the scale (half away from zero); TRY_ gives NULL where it does not fit
        decimal? Fits(decimal? v, int precision, int scale) => Away(v, scale) is { } x && Math.Abs(x) < (decimal)Math.Pow(10, precision - scale) ? x : null;
        var amounts = new (string Expr, Func<Row, decimal?> Value)[]
        {
            ("ROUND(ppds_amount, 0)", r => Away(M(r, Amount), 0)),
            ("ROUND(ppds_amount, 1)", r => Away(M(r, Amount), 1)),
            // C-M2: ROUND of a whole number written to the decimal column (a value that changes on every row)
            ("ROUND(ppds_num, 1)", r => I(r, Num)),
            ("ABS(ppds_amount)", r => M(r, Amount) is { } a ? Math.Abs(a) : null),
            ("ppds_amount % 3", r => M(r, Amount) % 3),
            ("ppds_amount / 4", r => R2(M(r, Amount) / 4)),
            ("ppds_num / 4", r => I(r, Num) is { } n ? n / 4 : null),
            ("ppds_num / 4.0", r => R2(I(r, Num) / 4.0m)),
            ("CAST(ppds_num AS decimal(10, 2))", r => I(r, Num)),
            ("CONVERT(decimal(10, 1), ppds_amount)", r => Away(M(r, Amount), 1)),
            ("TRY_CAST(ppds_amount AS decimal(3, 1))", r => Fits(M(r, Amount), 3, 1)),
            ("TRY_CAST(ppds_amount AS decimal(2, 1))", r => Fits(M(r, Amount), 2, 1)),
            ("TRY_CONVERT(decimal(2, 1), ppds_amount)", r => Fits(M(r, Amount), 2, 1)),
            ("ppds_amount * ppds_num", r => R2(M(r, Amount) * I(r, Num))),
            ("IIF(ppds_amount > ppds_num, ppds_amount, ppds_num)", r => M(r, Amount) > I(r, Num) ? M(r, Amount) : I(r, Num)),
        };
        foreach (var (expr, value) in amounts)
            await SetAll($"ppds_amount = {expr}", r => r.With(Amount, value(r)));

        await SetAll("ppds_money = ROUND(ppds_money, 1)", r => r.With(MoneyCol, Away(M(r, MoneyCol), 1)));
        await SetAll("ppds_money = ppds_money / 5", r => r.With(MoneyCol, R2(Money4(M(r, MoneyCol) / 5))));
        await SetAll("ppds_float = ROUND(ppds_float, 1)", r => r.With(FloatCol, Away(M(r, FloatCol), 1)));
        await SetAll("ppds_float = ABS(ppds_float)", r => r.With(FloatCol, M(r, FloatCol) is { } f ? Math.Abs(f) : null));
        await SetAll("ppds_float = ppds_float / 5", r => r.With(FloatCol, Fl(M(r, FloatCol), 5, (a, b) => a / b)));

        // functions on currency and the ROUND / SUBSTRING boundaries (10 places, 2 places)
        await SetAll("ppds_money = ABS(ppds_money), ppds_amount = ROUND(ppds_amount / 3, 2), ppds_float = ROUND(ppds_float, 10)",
            r => r.With(MoneyCol, M(r, MoneyCol) is { } m ? Math.Abs(m) : null).With(Amount, Away(M(r, Amount) / 3, 2)).With(FloatCol, M(r, FloatCol)));
        await SetAll("ppds_money = ROUND(ppds_money, 10), ppds_float = ROUND(ppds_float, 2), ppds_amount = ROUND(ppds_amount, 10)",
            r => r.With(MoneyCol, M(r, MoneyCol)).With(FloatCol, Away(M(r, FloatCol), 2)).With(Amount, M(r, Amount)));
    }

    // ── arithmetic between every pair of number types that is admitted ───────────────────────

    private async Task ArithmeticCasesAsync()
    {
        var t = _table;
        static decimal? Op(decimal? a, decimal? b, char op) => a is null || b is null ? null : op switch
        {
            '+' => a + b,
            '-' => a - b,
            '*' => a * b,
            _ => a / b
        };
        static int? IntOp(int? a, int? b, char op) => a is null || b is null ? null : op switch
        {
            '+' => a + b,
            '-' => a - b,
            '*' => a * b,
            _ => a / b
        };
        static Func<double, double, double> Dbl(char op) => op switch
        {
            '+' => (a, b) => a + b,
            '-' => (a, b) => a - b,
            '*' => (a, b) => a * b,
            _ => (a, b) => a / b
        };

        foreach (var op in new[] { '+', '-', '*', '/' })
        {
            // the same type on both sides (a whole-number and a decimal literal, two currency and two floating-point columns)
            var sameWhere = op == '/' ? "ppds_money <> 0 AND ppds_float <> 0" : All;
            Func<Row, bool> same = op == '/' ? r => M(r, MoneyCol) is { } m && m != 0 && M(r, FloatCol) is { } f && f != 0 : _ => true;
            await Upd($"UPDATE {t} SET ppds_num = ppds_num {op} 3, ppds_amount = ppds_amount {op} 3.0, ppds_money = ppds_money {op} ppds_money, " +
                      $"ppds_float = ppds_float {op} ppds_float WHERE {sameWhere}", same,
                r => r.With(Num, IntOp(I(r, Num), 3, op)).With(Amount, R2(Op(M(r, Amount), 3.0m, op)))
                    .With(MoneyCol, R2(Money4(Op(M(r, MoneyCol), M(r, MoneyCol), op)))).With(FloatCol, Fl(M(r, FloatCol), M(r, FloatCol), Dbl(op))));

            // decimal, currency and floating point with a whole number on the right
            var rightWhere = op == '/' ? "ppds_num <> 0" : All;
            Func<Row, bool> right = op == '/' ? r => I(r, Num) is { } n && n != 0 : _ => true;
            await Upd($"UPDATE {t} SET ppds_amount = ppds_amount {op} ppds_num, ppds_money = ppds_money {op} ppds_num, ppds_float = ppds_float {op} ppds_num WHERE {rightWhere}", right,
                r => r.With(Amount, R2(Op(M(r, Amount), I(r, Num), op))).With(MoneyCol, R2(Money4(Op(M(r, MoneyCol), I(r, Num), op))))
                    .With(FloatCol, Fl(M(r, FloatCol), I(r, Num), Dbl(op))));

            // ... and with a whole number on the left
            var leftWhere = op == '/' ? "ppds_amount <> 0 AND ppds_money <> 0 AND ppds_float <> 0" : All;
            Func<Row, bool> left = op == '/'
                ? r => M(r, Amount) is { } a && a != 0 && M(r, MoneyCol) is { } m && m != 0 && M(r, FloatCol) is { } f && f != 0
                : _ => true;
            await Upd($"UPDATE {t} SET ppds_amount = ppds_num {op} ppds_amount, ppds_money = ppds_num {op} ppds_money, ppds_float = ppds_num {op} ppds_float WHERE {leftWhere}", left,
                r => r.With(Amount, R2(Op(I(r, Num), M(r, Amount), op))).With(MoneyCol, R2(Money4(Op(I(r, Num), M(r, MoneyCol), op))))
                    .With(FloatCol, Fl(I(r, Num), M(r, FloatCol), Dbl(op))));
        }

        // % on whole numbers and decimals (a decimal % gives a decimal)
        await SetAll("ppds_num = ppds_num % 3, ppds_amount = ppds_amount % 3.0, ppds_float = ppds_num % 2.5",
            r => r.With(Num, I(r, Num) % 3).With(Amount, M(r, Amount) % 3.0m).With(FloatCol, I(r, Num) % 2.5m));

        // unary minus on decimal, currency and floating point
        await SetAll("ppds_amount = -ppds_amount, ppds_money = -ppds_money, ppds_float = -ppds_float",
            r => r.With(Amount, -M(r, Amount)).With(MoneyCol, -M(r, MoneyCol)).With(FloatCol, -M(r, FloatCol)));
    }

    // ── COALESCE, ISNULL, NULLIF, IIF and CASE over every admitted pair of value types ───────────

    private async Task ChoiceFunctionCasesAsync()
    {
        // (the SQL of the function over x and y, its T-SQL value from the row and the two values)
        var forms = new (string Name, Func<string, string, string> Sql, Func<Row, object?, object?, object?> Value)[]
        {
            ("COALESCE", (x, y) => $"COALESCE({x}, {y})", (_, x, y) => x ?? y),
            ("IIF", (x, y) => $"IIF(ppds_num > 2, {x}, {y})", (r, x, y) => I(r, Num) > 2 ? x : y),
            ("CASE", (x, y) => $"CASE WHEN ppds_num > 2 THEN {x} ELSE {y} END", (r, x, y) => I(r, Num) > 2 ? x : y),
            ("SIMPLECASE", (x, y) => $"CASE ppds_num WHEN 1 THEN {x} ELSE {y} END", (r, x, y) => I(r, Num) == 1 ? x : y),
            ("ISNULL", (x, y) => $"ISNULL({x}, {y})", (_, x, y) => x ?? y),
        };
        static decimal? Dec(object? v) => v switch { null => null, int i => i, decimal d => d, _ => throw new InvalidOperationException() };

        foreach (var (name, sql, value) in forms)
        {
            // the same type on both sides
            await SetAll($"ppds_num = {sql("ppds_num", "3")}, ppds_amount = {sql("ppds_amount", "2.5")}, ppds_money = {sql("ppds_money", "ppds_money * 2")}, " +
                         $"ppds_float = {sql("ppds_float", "ppds_float * 2")}, ppds_note = {sql("ppds_note", "ppds_name")}",
                r => r.With(Num, (int?)value(r, I(r, Num), 3)).With(Amount, Dec(value(r, M(r, Amount), 2.5m)))
                    .With(MoneyCol, Dec(value(r, M(r, MoneyCol), M(r, MoneyCol) * 2))).With(FloatCol, R2(Dec(value(r, M(r, FloatCol), M(r, FloatCol) * 2))))
                    .With(Note, (string?)value(r, S(r, Note), S(r, N))));
            // with NULL
            await SetAll($"ppds_num = {sql("ppds_num", "NULL")}, ppds_amount = {sql("ppds_amount", "NULL")}, ppds_money = {sql("ppds_money", "NULL")}, " +
                         $"ppds_float = {sql("ppds_float", "NULL")}, ppds_note = {sql("ppds_name", "NULL")}",
                r => r.With(Num, (int?)value(r, I(r, Num), null)).With(Amount, Dec(value(r, M(r, Amount), null)))
                    .With(MoneyCol, Dec(value(r, M(r, MoneyCol), null))).With(FloatCol, Dec(value(r, M(r, FloatCol), null)))
                    .With(Note, (string?)value(r, S(r, N), null)));
            // a whole number with each other number type (ISNULL: the whole number second, converted to the first's type)
            var (a, m, f) = name == "ISNULL"
                ? (sql("ppds_amount", "ppds_num"), sql("ppds_money", "ppds_num"), sql("ppds_float", "ppds_num"))
                : (sql("ppds_num", "ppds_amount"), sql("ppds_num", "ppds_money"), sql("ppds_num", "ppds_float"));
            await SetAll($"ppds_amount = {a}, ppds_money = {m}, ppds_float = {f}",
                r => name == "ISNULL"
                    ? r.With(Amount, Dec(value(r, M(r, Amount), I(r, Num)))).With(MoneyCol, Dec(value(r, M(r, MoneyCol), I(r, Num)))).With(FloatCol, Dec(value(r, M(r, FloatCol), I(r, Num))))
                    : r.With(Amount, Dec(value(r, I(r, Num), M(r, Amount)))).With(MoneyCol, Dec(value(r, I(r, Num), M(r, MoneyCol)))).With(FloatCol, Dec(value(r, I(r, Num), M(r, FloatCol)))));
        }

        // NULLIF: the same type, and a whole number second
        static decimal? NullIf(decimal? a, decimal? b) => a is null || b is null ? a : a == b ? null : a;
        await SetAll("ppds_num = NULLIF(ppds_num, 2), ppds_amount = NULLIF(ppds_amount, 30.0), ppds_money = NULLIF(ppds_money, ppds_money * 1), ppds_float = NULLIF(ppds_float, ppds_float * 1)",
            r => r.With(Num, I(r, Num) == 2 ? null : I(r, Num)).With(Amount, NullIf(M(r, Amount), 30m))
                .With(MoneyCol, NullIf(M(r, MoneyCol), M(r, MoneyCol))).With(FloatCol, NullIf(M(r, FloatCol), M(r, FloatCol))));
        await SetAll("ppds_amount = NULLIF(ppds_amount, 0), ppds_money = NULLIF(ppds_money, 0), ppds_float = NULLIF(ppds_float, 0)",
            r => r.With(Amount, NullIf(M(r, Amount), 0)).With(MoneyCol, NullIf(M(r, MoneyCol), 0)).With(FloatCol, NullIf(M(r, FloatCol), 0)));

        // comparisons inside IIF between number types, each operator, IS NOT NULL, OR and parentheses (unknown takes ELSE)
        static int If(bool? condition) => condition == true ? 1 : 0;
        static bool? Cmp(decimal? a, decimal? b, Func<decimal, decimal, bool> op) => a is null || b is null ? null : op(a.Value, b.Value);
        await SetAll("ppds_num = IIF(ppds_amount != ppds_float, 1, 0), ppds_amount = IIF(ppds_amount <= ppds_money, 1, 0), " +
                     "ppds_money = IIF(ppds_money >= ppds_money * 2, 1, 0), ppds_float = IIF((ppds_float > ppds_float * 2 OR ppds_num IS NOT NULL), 1, 0)",
            r => r.With(Num, If(Cmp(M(r, Amount), M(r, FloatCol), (a, b) => a != b)))
                .With(Amount, If(Cmp(M(r, Amount), M(r, MoneyCol), (a, b) => a <= b)))
                .With(MoneyCol, If(Cmp(M(r, MoneyCol), M(r, MoneyCol) * 2, (a, b) => a >= b)))
                .With(FloatCol, If(Cmp(M(r, FloatCol), M(r, FloatCol) * 2, (a, b) => a > b) == true || I(r, Num) is not null)));
        await SetAll("ppds_num = IIF(ppds_num > ppds_float, 1, 0), ppds_amount = IIF(ppds_float < ppds_money, 1, 0), " +
                     "ppds_money = IIF(ppds_num <> ppds_money, 1, 0), ppds_float = IIF(ppds_amount = ppds_amount, 1, 0)",
            r => r.With(Num, If(Cmp(I(r, Num), M(r, FloatCol), (a, b) => a > b)))
                .With(Amount, If(Cmp(M(r, FloatCol), M(r, MoneyCol), (a, b) => a < b)))
                .With(MoneyCol, If(Cmp(I(r, Num), M(r, MoneyCol), (a, b) => a != b)))
                .With(FloatCol, If(Cmp(M(r, Amount), M(r, Amount), (a, b) => a == b))));
    }

    // ── CAST, CONVERT, TRY_CAST, TRY_CONVERT: each admitted target and source, and the bounds ────────

    private async Task ConversionCasesAsync()
    {
        foreach (var kind in new[] { "CAST", "CONVERT", "TRY_CAST", "TRY_CONVERT" })
        {
            string K(string value, string type) => kind.EndsWith("CAST", StringComparison.Ordinal) ? $"{kind}({value} AS {type})" : $"{kind}({type}, {value})";

            // to int from int, decimal and floating point (truncated); currency to decimal(19, 4)
            await SetAll($"ppds_num = {K("ppds_num", "int")}, ppds_amount = {K("ppds_amount", "int")}, ppds_float = {K("ppds_float", "int")}, ppds_money = {K("ppds_money", "decimal(19, 4)")}",
                r => r.With(Amount, Trunc(M(r, Amount))).With(FloatCol, Trunc(M(r, FloatCol))));
            // to decimal(28, 0), decimal(28, 10) and decimal(1, 0)
            await SetAll($"ppds_amount = {K("ppds_num", "decimal(28, 0)")}, ppds_money = {K("ppds_money", "decimal(28, 10)")}, ppds_float = {K("ppds_num", "decimal(1, 0)")}",
                r => r.With(Amount, I(r, Num)).With(FloatCol, I(r, Num)));
            // to decimal(28, 28): every value below 1 (amount / 64 has no midpoint at the column's 2 places)
            await SetAll($"ppds_amount = {K("ppds_amount / 64", "decimal(28, 28)")}", r => r.With(Amount, R2(M(r, Amount) / 64)));
        }
    }

    // ── INSERT VALUES and INSERT ... SELECT ──────────────────────────────────────────────────

    private async Task InsertCasesAsync()
    {
        var t = _table;
        int C(int i) => _choices[i];
        var id0 = _ids[0];

        await Ins($"INSERT INTO {t} (ppds_name, ppds_num, ppds_amount, ppds_flag, ppds_note) VALUES ('ins1', 7, 1.5, 1, NULL), ('ins2', NULL, NULL, 0, 'n')",
            _ => [Inserted((N, "ins1"), (Num, 7), (Amount, 1.5m), (Flag, true)), Inserted((N, "ins2"), (Note, "n"))]);
        await Ins(
            $"INSERT INTO {t} (ppds_name, ppds_when, ppds_day, ppds_choice, ppds_flag, ppds_parent, ppds_money, ppds_float, ppds_memo, ppds_num, ppds_amount, ppds_note) VALUES " +
            $"('ins3', '2024-05-05T10:00:00Z', '2024-05-05', {C(1)}, 1, '{id0}', 1.25, 2.5, 'm', NULL, NULL, NULL), " +
            "('ins4', NULL, NULL, NULL, 0, NULL, NULL, NULL, NULL, 3, 0.5, 'n')",
            _ => [
                Inserted((N, "ins3"), (When, Utc(2024, 5, 5, 10, 0)), (Day, D(2024, 5, 5)), (Choice, C(1)), (Flag, true), (Parent, 0), (MoneyCol, 1.25m), (FloatCol, 2.5m), (Memo, "m")),
                Inserted((N, "ins4"), (Num, 3), (Amount, 0.5m), (Note, "n"))]);
        await Ins($"INSERT INTO {t} (ppds_name, ppds_num, ppds_amount, ppds_money, ppds_float) VALUES ('neg', -5, -2.5, -1.25, -0.5)",
            _ => [Inserted((N, "neg"), (Num, -5), (Amount, -2.5m), (MoneyCol, -1.25m), (FloatCol, -0.5m))]);
        await Ins($"INSERT INTO {t.ToUpperInvariant()} (PPDS_NAME, [PPDS_NUM]) VALUES (N'UP', 9)", _ => [Inserted((N, "UP"), (Num, 9))]);
        // S5: CAST('<guid>' AS uniqueidentifier) in VALUES
        await Ins($"INSERT INTO {t} (ppds_name, ppds_parent) VALUES ('guid', CAST('{id0}' AS uniqueidentifier))", _ => [Inserted((N, "guid"), (Parent, 0))]);
        // S5: more rows than one bulk batch (the suite's batch size is 10), and the most VALUES rows one statement takes
        await Ins($"INSERT INTO {t} (ppds_name, ppds_num) VALUES {string.Join(", ", Enumerable.Range(1, 12).Select(i => $"('v{i}', {i})"))}",
            _ => Enumerable.Range(1, 12).Select(i => Inserted((N, $"v{i}"), (Num, i))));
        await Ins($"INSERT INTO {t} (ppds_name) VALUES {string.Join(", ", Enumerable.Range(1, 1000).Select(i => $"('k{i}')"))}",
            _ => Enumerable.Range(1, 1000).Select(i => Inserted((N, $"k{i}"))));

        Row Copy(Row r, params string[] columns) => Inserted(columns.Select(c => (c, r.V[c])).ToArray());
        await Ins($"INSERT INTO {t} (ppds_name, ppds_num) SELECT TOP 2 ppds_name, ppds_num FROM {t} WHERE ppds_num IS NOT NULL ORDER BY ppds_num",
            rows => rows.Where(r => I(r, Num) is not null).OrderBy(r => I(r, Num)).Take(2).Select(r => Copy(r, N, Num)));
        await Ins($"INSERT INTO {t} (ppds_name, ppds_num) SELECT TOP (2) ppds_name, ppds_num FROM {t} WHERE ppds_num IS NOT NULL ORDER BY ppds_num DESC",
            rows => rows.Where(r => I(r, Num) is not null).OrderByDescending(r => I(r, Num)).Take(2).Select(r => Copy(r, N, Num)));
        await Ins($"INSERT INTO {t} (ppds_name) SELECT TOP (0) ppds_name FROM {t} ORDER BY ppds_name", _ => []);
        // the largest TOP (every row of this table)
        await Ins($"INSERT INTO {t} (ppds_name, ppds_num) SELECT TOP 5000 ppds_name, ppds_num FROM {t} ORDER BY ppds_num", rows => rows.Select(r => Copy(r, N, Num)));
        await ComputedAsync(async () =>
        {
            await Ins($"INSERT INTO {t} (ppds_name, ppds_note) SELECT ppds_name AS n, UPPER(ppds_name) AS u FROM {t} WHERE ppds_num = 1",
                rows => rows.Where(r => I(r, Num) == 1).Select(r => Inserted((N, S(r, N)), (Note, S(r, N)?.ToUpperInvariant()))));
            await Ins($"INSERT INTO {t} (ppds_note, ppds_name) SELECT ppds_name, COALESCE(ppds_note, 'none') AS c FROM {t} WHERE ppds_num >= 4",
                rows => rows.Where(r => I(r, Num) >= 4).Select(r => Inserted((Note, S(r, N)), (N, S(r, Note) ?? "none"))));
            await Ins($"INSERT INTO {t} (ppds_name, ppds_num) SELECT CONCAT(ppds_name, '!') AS c, ppds_num * 2 AS d FROM {t} WHERE ppds_num BETWEEN 1 AND 2",
                rows => rows.Where(r => I(r, Num) is >= 1 and <= 2).Select(r => Inserted((N, (S(r, N) ?? "") + "!"), (Num, I(r, Num) * 2))));
        });
        await Ins($"INSERT INTO {t} (ppds_name, ppds_note) SELECT ppds_name AS n, ppds_name AS u FROM {t} WHERE ppds_num = 1",
            rows => rows.Where(r => I(r, Num) == 1).Select(r => Inserted((N, S(r, N)), (Note, S(r, N)))));
        await Ins($"INSERT INTO {t} (ppds_name, ppds_note, ppds_num) SELECT ppds_name, 'copy' AS c, -1 AS d FROM {t} WHERE ppds_num >= 4",
            rows => rows.Where(r => I(r, Num) >= 4).Select(r => Inserted((N, S(r, N)), (Note, "copy"), (Num, -1))));
        await Ins($"INSERT INTO {t} (ppds_name, ppds_num) SELECT ppds_name, ppds_num FROM {t} WHERE ppds_name LIKE 'ca%' OR ppds_num BETWEEN 1 AND 2",
            rows => rows.Where(r => TLike(S(r, N), "ca%") || I(r, Num) is >= 1 and <= 2).Select(r => Copy(r, N, Num)));
        await Ins($"INSERT INTO {t} (ppds_name, ppds_num) SELECT s.ppds_name, s.ppds_num FROM {t} s WHERE s.ppds_num = 1",
            rows => rows.Where(r => I(r, Num) == 1).Select(r => Copy(r, N, Num)));
        // every number and text column copied
        await Ins($"INSERT INTO {t} (ppds_name, ppds_amount, ppds_float, ppds_memo, ppds_money) SELECT TOP 3 ppds_name, ppds_amount, ppds_float, ppds_memo, ppds_money FROM {t} ORDER BY ppds_num",
            rows => rows.OrderBy(r => I(r, Num) ?? int.MinValue).Take(3).Select(r => Copy(r, N, Amount, FloatCol, Memo, MoneyCol)));

        await Ins($"INSERT INTO dbo.{t} (ppds_name, ppds_num) VALUES ('dbo1', 11)", _ => [Inserted((N, "dbo1"), (Num, 11))]);
        await Ins($"INSERT INTO {t} (ppds_name, ppds_num) SELECT ppds_name, ppds_num FROM dbo.{t} WHERE ppds_num = 1",
            rows => rows.Where(r => I(r, Num) == 1).Select(r => Copy(r, N, Num)));

        // ORDER BY on text (collation order), on date/time and on a date-only column, with TOP; NULLs sort first.
        var three = new[] { "alpha", "Beta", "gamma" };
        var collation = Comparer<string?>.Create(TCompare);
        await Ins($"INSERT INTO {t} (ppds_name) SELECT TOP 2 ppds_name FROM {t} WHERE ppds_name IN ('alpha', 'Beta', 'gamma') ORDER BY ppds_name",
            rows => rows.Where(r => three.Any(n => TEq(S(r, N), n))).OrderBy(r => S(r, N), collation).Take(2).Select(r => Copy(r, N)));
        await Ins($"INSERT INTO {t} (ppds_name) SELECT TOP 2 ppds_name FROM {t} WHERE ppds_name IN ('alpha', 'Beta', 'gamma') ORDER BY ppds_name DESC",
            rows => rows.Where(r => three.Any(n => TEq(S(r, N), n))).OrderByDescending(r => S(r, N), collation).Take(2).Select(r => Copy(r, N)));
        await Ins($"INSERT INTO {t} (ppds_name, ppds_note) SELECT TOP 3 ppds_name, ppds_note FROM {t} ORDER BY ppds_when",
            rows => rows.OrderBy(r => T(r, When) ?? DateTime.MinValue).Take(3).Select(r => Copy(r, N, Note)));
        await Ins($"INSERT INTO {t} (ppds_name, ppds_note) SELECT TOP 2 ppds_name, ppds_note FROM {t} ORDER BY ppds_when DESC",
            rows => rows.OrderByDescending(r => T(r, When) ?? DateTime.MinValue).Take(2).Select(r => Copy(r, N, Note)));
        await Ins($"INSERT INTO {t} (ppds_name, ppds_note) SELECT TOP 3 ppds_name, ppds_note FROM {t} ORDER BY ppds_day",
            rows => rows.OrderBy(r => T(r, Day) ?? DateTime.MinValue).Take(3).Select(r => Copy(r, N, Note)));
        await Ins($"INSERT INTO {t} (ppds_name, ppds_note) SELECT TOP 2 ppds_name, ppds_note FROM {t} ORDER BY ppds_day DESC",
            rows => rows.OrderByDescending(r => T(r, Day) ?? DateTime.MinValue).Take(2).Select(r => Copy(r, N, Note)));
        // several ORDER BY columns: the later ones break ties of the earlier ones
        await Ins($"INSERT INTO {t} (ppds_name) SELECT TOP 3 ppds_name FROM {t} ORDER BY ppds_day, ppds_name",
            rows => rows.OrderBy(r => T(r, Day) ?? DateTime.MinValue).ThenBy(r => S(r, N), collation).Take(3).Select(r => Copy(r, N)));
        await Ins($"INSERT INTO {t} (ppds_name) SELECT TOP 4 ppds_name FROM {t} ORDER BY ppds_day DESC, ppds_num",
            rows => rows.OrderByDescending(r => T(r, Day) ?? DateTime.MinValue).ThenBy(r => I(r, Num) ?? int.MinValue).Take(4).Select(r => Copy(r, N)));
    }

    // ── hints and the row cap ────────────────────────────────────────────────────────────────

    private async Task HintAndCapCasesAsync()
    {
        var t = _table;
        Func<Row, bool> num1 = r => I(r, Num) == 1;
        await Upd($"-- ppds:BATCH_SIZE 2\nUPDATE {t} SET ppds_note = 'B' WHERE {All}", _ => true, r => r.With(Note, "B"));
        await Upd($"-- ppds:BYPASS_PLUGINS\nUPDATE {t} SET ppds_note = 'P' WHERE ppds_num = 1", num1, r => r.With(Note, "P"));
        await Upd($"-- ppds:BYPASS_FLOWS\nUPDATE {t} SET ppds_note = 'F' WHERE ppds_num = 1", num1, r => r.With(Note, "F"));
        // S5: DELETE and INSERT over several bulk batches, and the bypass hints on them
        await Del($"-- ppds:BATCH_SIZE 3\nDELETE FROM {t} WHERE ppds_num IS NOT NULL", r => I(r, Num) is not null);
        await Ins($"-- ppds:BATCH_SIZE 2\nINSERT INTO {t} (ppds_name) SELECT TOP 5 ppds_name FROM {t} ORDER BY ppds_num",
            rows => rows.OrderBy(r => I(r, Num) ?? int.MinValue).Take(5).Select(r => Inserted((N, S(r, N)))));
        await Del($"-- ppds:BYPASS_PLUGINS\nDELETE FROM {t} WHERE ppds_num = 1", num1);
        await Del($"-- ppds:BYPASS_FLOWS\nDELETE FROM {t} WHERE ppds_num = 1", num1);
        await Ins($"-- ppds:BYPASS_PLUGINS\nINSERT INTO {t} (ppds_name) VALUES ('bp')", _ => [Inserted((N, "bp"))]);
        await Ins($"-- ppds:BYPASS_FLOWS\nINSERT INTO {t} (ppds_name) VALUES ('bf')", _ => [Inserted((N, "bf"))]);

        var notNull = _seed.Count(r => I(r, Num) is not null);
        await Upd($"UPDATE {t} SET ppds_note = 'CAP' WHERE ppds_num IS NOT NULL", r => I(r, Num) is not null, r => r.With(Note, "CAP"), rowCap: notNull);
        await RefusedAsync($"UPDATE {t} SET ppds_note = 'CAP' WHERE ppds_num IS NOT NULL", ErrorCodes.Query.DmlRowCapExceeded, rowCap: notNull - 1);
    }

    // ── values T-SQL refuses while running: computed values, so refused before anything runs ─────────

    /// <summary>
    /// Divide by zero, overflow and a conversion that does not fit come only from computed values, which the interim rule
    /// refuses at the gate: each statement must be refused with nothing written. (When the typed engine admits these
    /// constructs, they return to failing while running, and the one-row-per-batch forms show the batches before it.)
    /// </summary>
    private async Task ErrorCasesAsync()
    {
        var t = _table;
        await RefusedAsync($"UPDATE {t} SET ppds_num = ppds_num / 0 WHERE ppds_num IS NOT NULL");
        await RefusedAsync($"UPDATE {t} SET ppds_num = 10 / ppds_num WHERE {All}");
        await RefusedAsync($"UPDATE {t} SET ppds_amount = CAST(ppds_amount AS decimal(2, 1)) WHERE {All}");
        await RefusedAsync($"UPDATE {t} SET ppds_note = CAST(ppds_num AS nvarchar(1)) WHERE {All}");
        await RefusedAsync($"UPDATE {t} SET ppds_num = ppds_num * 1000000000 WHERE {All}");
        // B-F2: int / int is int, so the multiplication after it overflows int
        await RefusedAsync($"UPDATE {t} SET ppds_amount = ppds_num / 2 * 1000000000 WHERE {All}");
        await RefusedAsync($"-- ppds:BATCH_SIZE 1\nUPDATE {t} SET ppds_num = 10 / ppds_num WHERE {All}");
        await RefusedAsync($"-- ppds:BATCH_SIZE 1\nUPDATE {t} SET ppds_num = ppds_num * 1000000000 WHERE {All}");
        await RefusedAsync($"-- ppds:BATCH_SIZE 1\nUPDATE {t} SET ppds_amount = CAST(ppds_amount AS decimal(2, 1)) WHERE {All}");
        await RefusedAsync($"-- ppds:BATCH_SIZE 1\nDELETE FROM {t} WHERE ppds_num IS NOT NULL", ErrorCodes.Query.DmlRowCapExceeded, rowCap: 2);
    }

    // ── a script whose later statement is refused: none of it runs ──────────────────────────────

    private async Task RefusedScriptCaseAsync()
        => await RefusedAsync($"UPDATE {_table} SET ppds_note = 'S1' WHERE ppds_num = 1; UPDATE {_table} SET ppds_note = UPPER(ppds_name) WHERE ppds_num = 2");

    // ── SELECT checks (read only) ────────────────────────────────────────────────────────────

    /// <summary>A computed item over a GROUP BY column (B, "unverified"): it must return the grouped value, not fail or be NULL.</summary>
    private async Task SelectChecksAsync()
    {
        if (_catalogue != null)
            return;
        await ResetAsync();
        var sql = $"SELECT ppds_name AS n, COUNT(*) AS c, UPPER(ppds_name) AS u FROM {_table} GROUP BY ppds_name";
        try
        {
            var result = await _service.ExecuteAsync(new SqlQueryRequest { Sql = sql });
            var rows = result.Result.Records.Select(r => (N: r["n"].Value as string, C: Convert.ToInt32(r["c"].Value, CultureInfo.InvariantCulture), U: r["u"].Value as string)).ToList();
            _output.WriteLine($"OBSERVED {sql} -> {string.Join(", ", rows.Select(x => $"({x.N ?? "<null>"}, {x.C}, {x.U ?? "<null>"})"))}");
            if (rows.Sum(x => x.C) != _seed.Count || rows.Any(x => x.N?.ToUpperInvariant() != x.U))
                Fail(sql, "the computed item does not return UPPER of the grouped value, or the counts do not add up to the rows");
            else
            {
                _passed++;
                _output.WriteLine($"PASS {sql}");
            }
        }
        catch (Exception ex)
        {
            Fail(sql, $"threw {ex.GetType().Name}: {ex.Message}");
        }
    }

    // ── refused: nothing may change ──────────────────────────────────────────────────────────

    private async Task RefusedCasesAsync()
    {
        var t = _table;
        foreach (var sql in new[]
        {
            $"UPDATE {t} SET ppds_note = 'x' WHERE NOT (ppds_num = 2)",
            $"UPDATE {t} SET ppds_note = 'x' WHERE UPPER(ppds_name) = 'ALPHA'",
            $"UPDATE {t} SET ppds_note = 'x' WHERE ppds_name = NULL",
            $"UPDATE {t} SET ppds_note = 'x' WHERE ppds_name <> NULL",
            $"UPDATE {t} SET ppds_note = 'x' WHERE ppds_num NOT IN (1, NULL)",
            $"UPDATE {t} SET ppds_note = 'x' WHERE ppds_name = ppds_note",
            $"UPDATE {t} SET ppds_note = 'x' WHERE ppds_num IN (ppds_num, 1)",
            $"UPDATE {t} SET ppds_note = 'x' WHERE ppds_num BETWEEN ppds_num AND 10",
            $"UPDATE {t} SET ppds_note = 'x' WHERE ppds_name LIKE ppds_note",
            $"UPDATE {t} SET ppds_note = 'x' WHERE ppds_name = 'ALPHA' COLLATE Latin1_General_CS_AS",
            $"UPDATE {t} SET ppds_note = 'x' WHERE ppds_name LIKE 'a[l]%'",
            $"UPDATE TOP (1) {t} SET ppds_note = 'x' WHERE ppds_num = 1",
            $"UPDATE {t} SET ppds_note = 'x' FROM {t} JOIN {t} b ON b.ppds_num = {t}.ppds_num WHERE {t}.ppds_num = 1",
            $"UPDATE {t} SET ppds_note = owneridname WHERE ppds_num = 1",
            $"UPDATE {t} SET ppds_note = ppds_name + '-x' WHERE ppds_num = 1",
            $"UPDATE {t} SET ppds_note = 'x' WHERE [ppds_num]] = 1 --] = 1",
            $"DELETE FROM {t} WHERE ppds_num IN (SELECT ppds_num FROM {t} WHERE ppds_num = 1)",
            $"DELETE FROM {t} WHERE ppds_num = 1 OR ppds_num IN (SELECT ppds_num FROM {t} WHERE ppds_num = 2)",
            $"INSERT INTO {t} (ppds_name) SELECT UPPER(ppds_name) FROM {t}",
            $"MERGE {t} AS x USING {t} AS y ON x.ppds_num = y.ppds_num WHEN NOT MATCHED THEN INSERT (ppds_name) VALUES ('m');",
            $"UPDATE {t} SET ppds_note = 'x' WHERE ppds_when < '2024-01-01'",
            $"UPDATE {t} SET ppds_when = '2024-01-01' WHERE ppds_num = 1",
            $"UPDATE {t} SET ppds_day = '2024-01-01T10:00:00Z' WHERE ppds_num = 1",
            $"UPDATE {t} SET ppds_note = 'x' WHERE ppds_name = 'a' + 'b'",
            $"UPDATE {t} SET ppds_note = 'x' WHERE ppds_name > 'b'",
            $"UPDATE {t} SET ppds_note = NULLIF(ppds_name, 'alpha') WHERE ppds_num = 1",
            $"UPDATE {t} SET ppds_note = CASE ppds_name WHEN 'cafe' THEN 'c' END WHERE ppds_num = 1",
            $"UPDATE {t} SET ppds_note = IIF(ppds_name = 'cafe', 'c', 'n') WHERE ppds_num = 1",
            $"UPDATE {t} SET ppds_note = REPLACE(ppds_name, 'a', 'o') WHERE ppds_num = 1",
            $"UPDATE {t} SET ppds_note = LEFT(ppds_name, 2) WHERE ppds_num = 1",
            $"UPDATE {t} SET ppds_note = RIGHT(ppds_name, 2) WHERE ppds_num = 1",
            $"UPDATE {t} SET ppds_num = CAST(ppds_note AS int) WHERE ppds_num = 1",
            $"UPDATE {t} SET ppds_num = TRY_CAST(ppds_note AS int) WHERE ppds_num = 1",
            $"UPDATE {t} SET ppds_when = CAST('2024-01-01T00:00:00Z' AS datetime) WHERE ppds_num = 1",
            $"UPDATE {t} SET ppds_when = GETDATE() WHERE ppds_num = 1",
            $"UPDATE {t} SET ppds_when = GETUTCDATE() WHERE ppds_num = 1",
            $"UPDATE {t} SET ppds_when = SYSUTCDATETIME() WHERE ppds_num = 1",
            $"INSERT INTO {t} ({Pk}, ppds_name) VALUES (NEWID(), 'x')",
            $"UPDATE {t} SET ownerid = '{_me}' WHERE ppds_num = 1",
            $"UPDATE {t} SET {Pk} = '{Guid.NewGuid()}' WHERE ppds_num = 1",
            $"UPDATE {t} SET ppds_parent = 'not-a-guid' WHERE ppds_num = 1",
            $"UPDATE {t} SET ppds_note = 'x' WHERE {Pk} = 'not-a-guid'",
            $"UPDATE {t} SET ppds_note = 'x' WHERE {Pk} = '{_ids[0]:N}'",
            $"UPDATE {t} SET statecode = 0, statuscode = 2 WHERE statecode = 1",
            $"UPDATE {t} SET statuscode = 2 WHERE statecode = 0",
            $"UPDATE {t} SET statecode = 1 WHERE statecode = 0",
            $"INSERT INTO {t} (ppds_name, statecode) VALUES ('x', 1)",
            $"UPDATE {t} SET ppds_choice = 999 WHERE ppds_num = 1",
            $"UPDATE {t} SET ppds_choice = 99999999999 WHERE ppds_num = 1",
            $"UPDATE {t} SET ppds_note = 'x' WHERE ppds_num = 99999999999",
            $"UPDATE {t} SET ppds_num = '5' WHERE ppds_num = 1",
            $"UPDATE {t} SET ppds_flag = 2 WHERE ppds_num = 1",
            $"UPDATE {t} SET ppds_note = 'x' WHERE ppds_num = 5.0",
            $"UPDATE {t} SET ppds_note = 'x' WHERE ppds_name = 5",
            $"UPDATE {t} SET ppds_note = 'x' WHERE ppds_float = 2.5E0",
            $"UPDATE {t} SET ppds_note = ppds_amount WHERE ppds_num = 1",
            $"UPDATE {t} SET ppds_note = 5 WHERE ppds_num = 1",
            $"UPDATE {t} SET ppds_note = CONCAT(ppds_name, ppds_amount) WHERE ppds_num = 1",
            $"UPDATE {t} SET ppds_note = CAST(ppds_money AS nvarchar(20)) WHERE ppds_num = 1",
            $"UPDATE {t} SET ppds_num = LEN(ppds_num) WHERE ppds_num = 1",
            $"UPDATE {t} SET ppds_note = CAST(ppds_name AS nvarchar) WHERE ppds_num = 1",
            $"UPDATE {t} SET ppds_note = CAST(ppds_name AS nvarchar(max)) WHERE ppds_num = 1",
            $"UPDATE {t} SET ppds_note = CAST(ppds_name AS nvarchar(4001)) WHERE ppds_num = 1",
            $"UPDATE {t} SET ppds_amount = CAST(ppds_amount AS decimal) WHERE ppds_num = 1",
            $"UPDATE {t} SET ppds_amount = CAST(ppds_amount AS numeric(10, 2)) WHERE ppds_num = 1",
            $"UPDATE {t} SET ppds_amount = TRY_CAST(ppds_amount AS decimal) WHERE ppds_num = 1",
            $"UPDATE {t} SET ppds_note = TRIM(TRAILING FROM ppds_note) WHERE ppds_num = 1",
            $"UPDATE {t} SET ppds_note = SUBSTRING(ppds_name, 0, 2) WHERE ppds_num = 1",
            $"UPDATE {t} SET ppds_note = SUBSTRING(ppds_name, 1, -1) WHERE ppds_num = 1",
            $"UPDATE {t} SET ppds_amount = ROUND(ppds_amount, -1) WHERE ppds_num = 1",
            $"UPDATE {t} SET ppds_note = 'x' WHERE ppds_num = 1 OPTION (MAXDOP 1)",
            $"-- ppds:NOLOCK\nUPDATE {t} SET ppds_note = 'x' WHERE ppds_num = 1",
            $"INSERT INTO {t} (ppds_name) VALUES (UPPER('a'))",
            $"INSERT INTO {t} (ppds_num) VALUES (1 + 2)",
            $"INSERT INTO {t} (ppds_name) SELECT TOP 2 ppds_name FROM {t}",
            $"INSERT INTO {t} (ppds_name) SELECT TOP 5001 ppds_name FROM {t} ORDER BY ppds_name",
            $"INSERT INTO {t} (ppds_name) SELECT fullname FROM contact",
            $"INSERT INTO {t} (ppds_name, ppds_note) SELECT ppds_note AS ppds_name, UPPER(ppds_name) AS u FROM {t} WHERE ppds_num = 1",
            $"INSERT INTO {t} (ppds_name) SELECT TOP 2 ppds_name FROM {t} ORDER BY ppds_choice",
            $"INSERT INTO {t} (ppds_name) SELECT TOP 2 ppds_name FROM {t} ORDER BY ppds_flag",
            $"INSERT INTO {t} (ppds_name) SELECT TOP 2 ppds_name FROM {t} ORDER BY ppds_parent",
            $"INSERT INTO {t} (ppds_name) SELECT TOP 2 ppds_name FROM {t} ORDER BY ppds_legacy",
            $"UPDATE {t} SET ppds_tzi = '2024-01-01T00:00:00Z' WHERE ppds_num = 1",
            $"UPDATE {t} SET ppds_note = 'x' WHERE ppds_tzi < '2024-01-01T00:00:00Z'",
            $"UPDATE {t} SET ppds_note = 'x' WHERE ppds_tzi IS NULL",
            $"UPDATE {t} SET ppds_legacy = '2024-01-01' WHERE ppds_num = 1",
            $"UPDATE {t} SET ppds_note = 'x' WHERE ppds_legacy = '2024-01-01'",
            $"UPDATE {t} SET ppds_note = 'x' WHERE ppds_legacy IS NOT NULL",
            $"INSERT INTO {t} (ppds_name, ppds_legacy) VALUES ('x', '2024-01-01T00:00:00Z')",
            // round 2: refused instead of computed differently from T-SQL, or admitted without proof
            $"INSERT INTO {t} (ppds_name) SELECT ppds_name FROM {t} WHERE ppds_num > 2 ORDER BY ppds_name",
            $"UPDATE {t} SET ppds_amount = TRY_CAST(ppds_amount AS decimal(38, 30)) WHERE ppds_num = 1",
            $"UPDATE {t} SET ppds_amount = CAST(ppds_amount AS decimal(29, 2)) WHERE ppds_num = 1",
            $"UPDATE {t} SET ppds_amount = ROUND(ppds_amount, 11) WHERE ppds_num = 1",
            $"UPDATE {t} SET ppds_num = ppds_money WHERE ppds_num = 1",
            $"UPDATE {t} SET ppds_num = CAST(ppds_money AS int) WHERE ppds_num = 1",
            $"UPDATE {t} SET ppds_num = TRY_CONVERT(int, ppds_money) WHERE ppds_num = 1",
            $"UPDATE {t} SET ppds_amount = ppds_float WHERE ppds_num = 1",
            $"UPDATE {t} SET ppds_money = CAST(ppds_float AS decimal(10, 2)) WHERE ppds_num = 1",
            $"UPDATE {t} SET ppds_float = ppds_float % 2 WHERE ppds_num = 1",
            $"UPDATE {t} SET ppds_money = ppds_money % 2 WHERE ppds_num = 1",
            $"UPDATE {t} SET ppds_amount = ISNULL(ppds_num, 2.5) WHERE ppds_num = 1",
            $"UPDATE {t} SET ppds_amount = ppds_amount + ppds_money WHERE ppds_num = 1",
            $"UPDATE {t} SET ppds_amount = COALESCE(ppds_amount, ppds_money) WHERE ppds_num = 1",
            $"UPDATE {t} SET ppds_float = ppds_float * ppds_amount WHERE ppds_num = 1",
            $"UPDATE {t} SET ppds_note = UPPER(NULL) WHERE ppds_num = 1",
            $"UPDATE {t} SET ppds_note = COALESCE(NULL, NULL) WHERE ppds_num = 1",
            $"UPDATE {t} SET ppds_num = NULLIF(ppds_num, ppds_amount) WHERE ppds_num = 1",
            // review round 1: the whole-script rule, and text without N that is not 7-bit ASCII in a condition
            $"IF 1 = 1 UPDATE {t} SET ppds_note = 'x' WHERE ppds_num = 1",
            $"UPDATE {t} SET ppds_note = 'x' WHERE ppds_num = 1; SELECT ppds_name FROM {t}",
            $"UPDATE {t} SET ppds_note = 'x' WHERE ppds_name = 'CAFÉ'",
        })
        {
            await RefusedAsync(sql);
        }

        // A write to another environment is the safety guard's own refusal, which keeps Query.DmlBlocked.
        await RefusedAsync($"DELETE FROM [QA].{t} WHERE ppds_num = 1", ErrorCodes.Query.DmlBlocked);

        // review round 1: values Dataverse stores differently
        await RefusedAsync($"UPDATE {t} SET ppds_note = '' WHERE ppds_num = 1", ErrorCodes.Query.DmlValueStoredDifferently);
        await RefusedAsync($"UPDATE {t} SET ppds_note = ' x' WHERE ppds_num = 1", ErrorCodes.Query.DmlValueStoredDifferently);

        await RefusedAsync($"UPDATE {t} SET ppds_note = 'x' WHERE ppds_num = 1\nGO\nUPDATE {t} SET ppds_note = 'y' WHERE ppds_num = 2", ErrorCodes.Validation.InvalidValue);
        await RefusedAsync($"-- ppds:USE_TDS\nUPDATE {t} SET ppds_note = 'x' WHERE ppds_num = 1", ErrorCodes.Query.TdsIncompatible);
    }

    // ── a script of several statements: each runs and is reported ──────────────────────────────

    private async Task ScriptCasesAsync()
    {
        var t = _table;
        var sql = $"UPDATE {t} SET ppds_note = 'S1' WHERE ppds_num = 1; DELETE FROM {t} WHERE ppds_num = 7; INSERT INTO {t} (ppds_name) VALUES ('s3')";
        if (_catalogue != null)
        {
            _catalogue.Add(new LiveCase(sql, Proves: true, ExpectedCode: null));
            return;
        }

        await ResetAsync();
        var before = await ReadAsync();
        SqlQueryResult result;
        try
        {
            result = await _service.ExecuteAsync(new SqlQueryRequest { Sql = sql, DmlSafety = new DmlSafetyOptions { IsConfirmed = true } });
        }
        catch (Exception ex)
        {
            Fail(sql, $"threw {ex.GetType().Name}: {ex.Message}");
            return;
        }

        var actual = await ReadAsync();
        // Row 7 (num 7) has no row pointing at it, so deleting it writes no other row.
        var want = before.Where(r => I(r, Num) != 7).Select(r => I(r, Num) == 1 ? r.With(Note, "S1") : r).Append(Inserted((N, "s3"))).ToList();
        var same = want.Select(r => r.Key).OrderBy(k => k, StringComparer.Ordinal).SequenceEqual(actual.Select(r => r.Key).OrderBy(k => k, StringComparer.Ordinal));
        var written = actual.Where(a => before.Any(b => b.Id == a.Id && b.Version != a.Version)).Select(a => a.Id).ToHashSet();
        var updated = before.Where(r => I(r, Num) == 1).Select(r => r.Id).ToHashSet();
        var statements = result.DmlOutcome?.Statements.Select(s => $"{s.Operation} {s.AffectedRows}/{s.FailedRows}").ToList() ?? [];
        var counts = string.Join(", ", statements);
        var wantCounts = $"Update {updated.Count}/0, Delete {before.Count(r => I(r, Num) == 7)}/0, Insert 1/0";
        if (!same)
            Fail(sql, "the table after the script is not what its three statements say");
        else if (!written.SetEquals(updated))
            Fail(sql, $"{written.Count} rows were written by the UPDATE, the text selects {updated.Count}");
        else if (counts != wantCounts)
            Fail(sql, $"reported [{counts}], the statements select [{wantCounts}]");
        else
        {
            _passed++;
            _output.WriteLine($"PASS {sql} (reported {counts})");
        }
    }

    // ── DELETE (last) ────────────────────────────────────────────────────────────────────────

    private async Task DeleteCasesAsync()
    {
        var t = _table;
        var id0 = _ids[0];
        await Del($"DELETE FROM {t} WHERE ppds_parent = '{id0}'", r => I(r, Parent) == 0);
        await Del($"DELETE FROM {t} WHERE {Pk} = '{id0}'", r => r.Id == id0);
        await Del($"DELETE a FROM {t} a WHERE a.ppds_num = 4", r => I(r, Num) == 4, observe: false);
        await Del($"DELETE {t} FROM {t} WHERE {t}.ppds_num = 4", r => I(r, Num) == 4, observe: false);
        await Del($"DELETE FROM dbo.{t} WHERE ppds_num = 7", r => I(r, Num) == 7);
        await Del($"DELETE FROM {t} WHERE ppds_num <> 2", r => I(r, Num) is { } n && n != 2);
        await Del($"DELETE FROM {t} WHERE ppds_num NOT IN (1, 2)", r => I(r, Num) is { } n && n is not (1 or 2));
        await Del($"DELETE FROM {t} WHERE ppds_num IN (1, 2) OR ppds_name LIKE 'ca%'", r => I(r, Num) is 1 or 2 || TLike(S(r, N), "ca%"));
        await Del($"DELETE FROM {t} WHERE ppds_amount BETWEEN 0 AND 10.5", r => M(r, Amount) >= 0m && M(r, Amount) <= 10.5m);
        await Del($"DELETE FROM {t} WHERE statecode = 1", r => I(r, State) == 1);
        await Del($"DELETE FROM {t} WHERE ppds_name = 'cafe'", r => TEq(S(r, N), "cafe"));

        // no WHERE, with the environment's no-WHERE prevention switched off
        await WithSettingsAsync(new QuerySafetySettings { PreventDeleteWithoutWhere = false },
            () => Del($"DELETE FROM {t}", _ => true));
    }
}
