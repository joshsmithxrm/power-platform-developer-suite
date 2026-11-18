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

    private async Task RunAllCasesAsync()
    {
        await WhereCasesAsync();
        await WriteCasesAsync();
        await ValueCasesAsync();
        await InsertCasesAsync();
        await HintAndCapCasesAsync();
        await SelectChecksAsync();
        await RefusedCasesAsync();
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
            ("ppds_name = 'CAFÉ'", r => TEq(S(r, N), "CAFÉ")),
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
            await CaseAsync($"UPDATE {t} SET ppds_note = 'HIT' WHERE {sql}", rows => rows.Select(r => match(r) ? r.With(Note, "HIT") : r).ToList(), observe: true);

        Func<List<Row>, List<Row>> Hit(int num, string note = "HIT") => rows => rows.Select(r => I(r, Num) == num ? r.With(Note, note) : r).ToList();
        await CaseAsync($"UPDATE dbo.{t} SET ppds_note = 'HIT' WHERE ppds_num = 1", Hit(1));
        await CaseAsync($"UPDATE a SET ppds_note = 'ALIAS' FROM dbo.{t} a WHERE a.ppds_num = 2", Hit(2, "ALIAS"));
        await CaseAsync($"UPDATE a SET ppds_note = 'ALIAS' FROM {t} a WHERE a.ppds_num = 1", Hit(1, "ALIAS"));
        // the table name as qualifier, FROM the table itself without an alias, and upper-case and bracketed names
        await CaseAsync($"UPDATE {t} SET ppds_note = 'Q' WHERE {t}.ppds_num = 1", Hit(1, "Q"));
        await CaseAsync($"UPDATE {t} SET ppds_note = 'F' FROM {t} WHERE {t}.ppds_num = 2", Hit(2, "F"));
        await CaseAsync($"UPDATE dbo.{t} SET ppds_note = 'F' FROM {t} WHERE ppds_num = 4", Hit(4, "F"));
        await CaseAsync($"UPDATE [{t.ToUpperInvariant()}] SET [PPDS_NOTE] = 'UP' WHERE [PPDS_NUM] = 1", Hit(1, "UP"));
        await CaseAsync($"UPDATE {t.ToUpperInvariant()} SET PPDS_NOTE = UPPER(PPDS_NAME) WHERE PPDS_NUM = 2",
            rows => rows.Select(r => I(r, Num) == 2 ? r.With(Note, S(r, N)?.ToUpperInvariant()) : r).ToList());

        // no WHERE, with the environment's no-WHERE prevention switched off
        _service.EnvironmentSafetySettings = new QuerySafetySettings { PreventUpdateWithoutWhere = false };
        try
        {
            await CaseAsync($"UPDATE {t} SET ppds_note = 'ALL'", rows => rows.Select(r => r.With(Note, "ALL")).ToList());
        }
        finally
        {
            _service.EnvironmentSafetySettings = null;
        }
    }

    // ── writes: every column type, values and NULL ───────────────────────────────────────────

    private async Task WriteCasesAsync()
    {
        var t = _table;
        int C(int i) => _choices[i];
        var id0 = _ids[0];
        var id4 = _ids[4];

        async Task Set(string assignment, Func<Row, Row> change, string where = All, Func<Row, bool>? match = null)
            => await CaseAsync($"UPDATE {t} SET {assignment} WHERE {where}", rows => rows.Select(r => match is null || match(r) ? change(r) : r).ToList());

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
        await Set($"ppds_parent = '{id0}'", r => r.With(Parent, 0), "ppds_num = 4", r => I(r, Num) == 4);
        await Set($"ppds_parent = '{id0.ToString("B").ToUpperInvariant()}'", r => r.With(Parent, 0), "ppds_num = 4", r => I(r, Num) == 4);
        await Set($"ppds_parent = CAST('{id4}' AS uniqueidentifier)", r => r.With(Parent, 4), "ppds_num = 4", r => I(r, Num) == 4);
        await Set("ppds_parent = NULL", r => r.With(Parent, null));
        await Set("ppds_money = 12.34", r => r.With(MoneyCol, 12.34m));
        await Set("ppds_money = ppds_money * 2", r => r.With(MoneyCol, M(r, MoneyCol) * 2));
        await Set("ppds_money = ppds_money + 1", r => r.With(MoneyCol, M(r, MoneyCol) + 1));
        await Set("ppds_money = NULL", r => r.With(MoneyCol, null));
        await Set("ppds_float = 3.5", r => r.With(FloatCol, 3.5m));
        await Set("ppds_float = ppds_float + 1", r => r.With(FloatCol, M(r, FloatCol) + 1));
        await Set("ppds_float = ppds_float * 2", r => r.With(FloatCol, M(r, FloatCol) * 2));
        await Set("ppds_float = NULL", r => r.With(FloatCol, null));
        await Set("ppds_memo = 'new memo'", r => r.With(Memo, "new memo"));
        await Set("ppds_memo = CONCAT(ppds_memo, '!')", r => r.With(Memo, (S(r, Memo) ?? "") + "!"));
        await Set("ppds_memo = NULL", r => r.With(Memo, null));
        await Set("ppds_num = NULL", r => r.With(Num, null));
        await Set("ppds_amount = NULL", r => r.With(Amount, null));
        await Set("ppds_amount = 10.555", r => r.With(Amount, 10.56m));
        await Set("ppds_amount = -2.5", r => r.With(Amount, -2.5m));
        await Set("ppds_note = NULL, ppds_num = 3", r => r.With(Note, null).With(Num, 3));
        await Set("ppds_note = N'naïve'", r => r.With(Note, "naïve"));
        await Set("statecode = 1, statuscode = 2", r => r.With(State, 1).With(Status, 2), "statecode = 0", r => I(r, State) == 0);
        await Set("statecode = 0, statuscode = 1", r => r.With(State, 0).With(Status, 1), "statecode = 1", r => I(r, State) == 1);
    }

    // ── values the engine computes ───────────────────────────────────────────────────────────

    private async Task ValueCasesAsync()
    {
        var t = _table;

        async Task Set(string assignment, Func<Row, Row> change)
            => await CaseAsync($"UPDATE {t} SET {assignment} WHERE {All}", rows => rows.Select(change).ToList());

        decimal? R2(decimal? v) => Away(v, 2);
        int? Trunc(decimal? v) => v is null ? null : (int)decimal.Truncate(v.Value);
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
            ("CONCAT(ppds_name, '-', ppds_note)", r => (S(r, N) ?? "") + "-" + (S(r, Note) ?? "")),
            ("CONCAT(ppds_memo, '|', ppds_name)", r => (S(r, Memo) ?? "") + "|" + (S(r, N) ?? "")),
            ("SUBSTRING(ppds_name, 2, 3)", r => S(r, N) is not { } s ? null : Stored(s.Length < 2 ? "" : s.Substring(1, Math.Min(3, s.Length - 1)))),
            ("SUBSTRING(ppds_name, 3, 100)", r => S(r, N) is not { } s ? null : Stored(s.Length < 3 ? "" : s[2..])),
            ("SUBSTRING(ppds_name, 1, 0)", _ => null),
            ("CAST(ppds_num AS nvarchar(10))", r => I(r, Num)?.ToString(CultureInfo.InvariantCulture)),
            ("CONVERT(nvarchar(10), ppds_num)", r => I(r, Num)?.ToString(CultureInfo.InvariantCulture)),
            ("CAST(ppds_name AS nvarchar(3))", r => Prefix(S(r, N), 3)),
            ("CONVERT(nvarchar(4), ppds_memo)", r => Prefix(S(r, Memo), 4)),
            ("'lit'", _ => "lit"),
            ("CONCAT(ppds_name, ppds_num)", r => (S(r, N) ?? "") + (I(r, Num)?.ToString(CultureInfo.InvariantCulture) ?? "")),
            ("CONCAT(ppds_name, '#', CONVERT(nvarchar(10), ppds_num * 2))", r => (S(r, N) ?? "") + "#" + ((I(r, Num) * 2)?.ToString(CultureInfo.InvariantCulture) ?? "")),
            ("CONCAT(ppds_name, CAST(ppds_amount AS int))", r => (S(r, N) ?? "") + (Trunc(M(r, Amount))?.ToString(CultureInfo.InvariantCulture) ?? "")),
        };
        foreach (var (expr, value) in notes)
            await Set($"ppds_note = {expr}", r => r.With(Note, Stored(value(r))));

        var nums = new (string Expr, Func<Row, int?> Value)[]
        {
            ("LEN(ppds_note)", r => S(r, Note)?.TrimEnd(' ').Length),
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
            ("ROUND(ppds_num, 0)", r => I(r, Num)),
            ("CAST(ppds_amount AS int)", r => Trunc(M(r, Amount))),
            ("CONVERT(int, ppds_amount)", r => Trunc(M(r, Amount))),
            ("TRY_CAST(ppds_amount AS int)", r => Trunc(M(r, Amount))),
            ("TRY_CONVERT(int, ppds_amount)", r => Trunc(M(r, Amount))),
            // implicit conversions to a whole number truncate toward zero, as in SQL
            ("ppds_amount", r => Trunc(M(r, Amount))),
            ("ppds_money", r => Trunc(M(r, MoneyCol))),
            ("ppds_float", r => Trunc(M(r, FloatCol))),
            ("2.7", _ => 2),
            ("-2.7", _ => -2),
        };
        foreach (var (expr, value) in nums)
            await Set($"ppds_num = {expr}", r => r.With(Num, value(r)));

        // decimal(p, s): rounded to the scale (half away from zero); TRY_ gives NULL where it does not fit
        decimal? Fits(decimal? v, int precision, int scale) => Away(v, scale) is { } x && Math.Abs(x) < (decimal)Math.Pow(10, precision - scale) ? x : null;
        var amounts = new (string Expr, Func<Row, decimal?> Value)[]
        {
            ("ROUND(ppds_amount, 0)", r => Away(M(r, Amount), 0)),
            ("ROUND(ppds_amount, 1)", r => Away(M(r, Amount), 1)),
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
            await Set($"ppds_amount = {expr}", r => r.With(Amount, value(r)));

        await Set("ppds_money = ROUND(ppds_money, 1)", r => r.With(MoneyCol, Away(M(r, MoneyCol), 1)));
        await Set("ppds_money = ppds_money / 5", r => r.With(MoneyCol, R2(M(r, MoneyCol) / 5)));
        await Set("ppds_float = ROUND(ppds_float, 1)", r => r.With(FloatCol, Away(M(r, FloatCol), 1)));
        await Set("ppds_float = ABS(ppds_float)", r => r.With(FloatCol, M(r, FloatCol) is { } f ? Math.Abs(f) : null));
        await Set("ppds_float = ppds_float / 5", r => r.With(FloatCol, R2(M(r, FloatCol) / 5)));

        // errors where T-SQL raises one: the statement fails and nothing is written
        await RefusedAsync($"UPDATE {t} SET ppds_num = ppds_num / 0 WHERE ppds_num IS NOT NULL");
        await RefusedAsync($"UPDATE {t} SET ppds_num = 10 / ppds_num WHERE {All}");
        await RefusedAsync($"UPDATE {t} SET ppds_amount = CAST(ppds_amount AS decimal(2, 1)) WHERE {All}");
        await RefusedAsync($"UPDATE {t} SET ppds_note = CAST(ppds_num AS nvarchar(1)) WHERE {All}");
        await RefusedAsync($"UPDATE {t} SET ppds_num = ppds_num * 1000000000 WHERE {All}");
    }

    // ── INSERT VALUES and INSERT ... SELECT ──────────────────────────────────────────────────

    private async Task InsertCasesAsync()
    {
        var t = _table;
        int C(int i) => _choices[i];
        var id0 = _ids[0];

        await CaseAsync($"INSERT INTO {t} (ppds_name, ppds_num, ppds_amount, ppds_flag, ppds_note) VALUES ('ins1', 7, 1.5, 1, NULL), ('ins2', NULL, NULL, 0, 'n')",
            rows => rows.Concat([Inserted((N, "ins1"), (Num, 7), (Amount, 1.5m), (Flag, true)), Inserted((N, "ins2"), (Note, "n"))]).ToList());
        await CaseAsync(
            $"INSERT INTO {t} (ppds_name, ppds_when, ppds_day, ppds_choice, ppds_flag, ppds_parent, ppds_money, ppds_float, ppds_memo, ppds_num, ppds_amount, ppds_note) VALUES " +
            $"('ins3', '2024-05-05T10:00:00Z', '2024-05-05', {C(1)}, 1, '{id0}', 1.25, 2.5, 'm', NULL, NULL, NULL), " +
            "('ins4', NULL, NULL, NULL, 0, NULL, NULL, NULL, NULL, 3, 0.5, 'n')",
            rows => rows.Concat([
                Inserted((N, "ins3"), (When, Utc(2024, 5, 5, 10, 0)), (Day, D(2024, 5, 5)), (Choice, C(1)), (Flag, true), (Parent, 0), (MoneyCol, 1.25m), (FloatCol, 2.5m), (Memo, "m")),
                Inserted((N, "ins4"), (Num, 3), (Amount, 0.5m), (Note, "n"))]).ToList());
        await CaseAsync($"INSERT INTO {t} (ppds_name, ppds_num, ppds_amount, ppds_money, ppds_float) VALUES ('neg', -5, -2.5, -1.25, -0.5)",
            rows => rows.Concat([Inserted((N, "neg"), (Num, -5), (Amount, -2.5m), (MoneyCol, -1.25m), (FloatCol, -0.5m))]).ToList());
        await CaseAsync($"INSERT INTO {t.ToUpperInvariant()} (PPDS_NAME, [PPDS_NUM]) VALUES (N'UP', 9)",
            rows => rows.Concat([Inserted((N, "UP"), (Num, 9))]).ToList());

        Row Copy(Row r, params string[] columns) => Inserted(columns.Select(c => (c, r.V[c])).ToArray());
        await CaseAsync($"INSERT INTO {t} (ppds_name, ppds_num) SELECT TOP 2 ppds_name, ppds_num FROM {t} WHERE ppds_num IS NOT NULL ORDER BY ppds_num",
            rows => rows.Concat(rows.Where(r => I(r, Num) is not null).OrderBy(r => I(r, Num)).Take(2).Select(r => Copy(r, N, Num))).ToList());
        await CaseAsync($"INSERT INTO {t} (ppds_name, ppds_num) SELECT TOP (2) ppds_name, ppds_num FROM {t} WHERE ppds_num IS NOT NULL ORDER BY ppds_num DESC",
            rows => rows.Concat(rows.Where(r => I(r, Num) is not null).OrderByDescending(r => I(r, Num)).Take(2).Select(r => Copy(r, N, Num))).ToList());
        await CaseAsync($"INSERT INTO {t} (ppds_name) SELECT TOP (0) ppds_name FROM {t} ORDER BY ppds_name", rows => rows);
        await CaseAsync($"INSERT INTO {t} (ppds_name, ppds_note) SELECT ppds_name AS n, UPPER(ppds_name) AS u FROM {t} WHERE ppds_num = 1",
            rows => rows.Concat(rows.Where(r => I(r, Num) == 1).Select(r => Inserted((N, S(r, N)), (Note, S(r, N)?.ToUpperInvariant())))).ToList());
        await CaseAsync($"INSERT INTO {t} (ppds_note, ppds_name) SELECT ppds_name, COALESCE(ppds_note, 'none') AS c FROM {t} WHERE ppds_num >= 4",
            rows => rows.Concat(rows.Where(r => I(r, Num) >= 4).Select(r => Inserted((Note, S(r, N)), (N, S(r, Note) ?? "none")))).ToList());
        await CaseAsync($"INSERT INTO {t} (ppds_name, ppds_num) SELECT CONCAT(ppds_name, '!') AS c, ppds_num * 2 AS d FROM {t} WHERE ppds_num BETWEEN 1 AND 2",
            rows => rows.Concat(rows.Where(r => I(r, Num) is >= 1 and <= 2).Select(r => Inserted((N, (S(r, N) ?? "") + "!"), (Num, I(r, Num) * 2)))).ToList());
        await CaseAsync($"INSERT INTO {t} (ppds_name, ppds_num) SELECT ppds_name, ppds_num FROM {t} WHERE ppds_name LIKE 'ca%' OR ppds_num BETWEEN 1 AND 2",
            rows => rows.Concat(rows.Where(r => TLike(S(r, N), "ca%") || I(r, Num) is >= 1 and <= 2).Select(r => Copy(r, N, Num))).ToList());
        await CaseAsync($"INSERT INTO {t} (ppds_name, ppds_num) SELECT s.ppds_name, s.ppds_num FROM {t} s WHERE s.ppds_num = 1",
            rows => rows.Concat(rows.Where(r => I(r, Num) == 1).Select(r => Copy(r, N, Num))).ToList());
        await CaseAsync($"INSERT INTO {t} (ppds_name) SELECT ppds_name FROM {t} WHERE ppds_num > 2 ORDER BY ppds_name",
            rows => rows.Concat(rows.Where(r => I(r, Num) > 2).Select(r => Copy(r, N))).ToList());

        await CaseAsync($"INSERT INTO dbo.{t} (ppds_name, ppds_num) VALUES ('dbo1', 11)", rows => rows.Concat([Inserted((N, "dbo1"), (Num, 11))]).ToList());
        await CaseAsync($"INSERT INTO {t} (ppds_name, ppds_num) SELECT ppds_name, ppds_num FROM dbo.{t} WHERE ppds_num = 1",
            rows => rows.Concat(rows.Where(r => I(r, Num) == 1).Select(r => Copy(r, N, Num))).ToList());

        // ORDER BY on text (collation order), on date/time and on a date-only column, with TOP; NULLs sort first.
        var three = new[] { "alpha", "Beta", "gamma" };
        var collation = Comparer<string?>.Create(TCompare);
        await CaseAsync($"INSERT INTO {t} (ppds_name) SELECT TOP 2 ppds_name FROM {t} WHERE ppds_name IN ('alpha', 'Beta', 'gamma') ORDER BY ppds_name",
            rows => rows.Concat(rows.Where(r => three.Any(n => TEq(S(r, N), n))).OrderBy(r => S(r, N), collation).Take(2).Select(r => Copy(r, N))).ToList());
        await CaseAsync($"INSERT INTO {t} (ppds_name) SELECT TOP 2 ppds_name FROM {t} WHERE ppds_name IN ('alpha', 'Beta', 'gamma') ORDER BY ppds_name DESC",
            rows => rows.Concat(rows.Where(r => three.Any(n => TEq(S(r, N), n))).OrderByDescending(r => S(r, N), collation).Take(2).Select(r => Copy(r, N))).ToList());
        await CaseAsync($"INSERT INTO {t} (ppds_name, ppds_note) SELECT TOP 3 ppds_name, ppds_note FROM {t} ORDER BY ppds_when",
            rows => rows.Concat(rows.OrderBy(r => T(r, When) ?? DateTime.MinValue).Take(3).Select(r => Copy(r, N, Note))).ToList());
        await CaseAsync($"INSERT INTO {t} (ppds_name, ppds_note) SELECT TOP 2 ppds_name, ppds_note FROM {t} ORDER BY ppds_when DESC",
            rows => rows.Concat(rows.OrderByDescending(r => T(r, When) ?? DateTime.MinValue).Take(2).Select(r => Copy(r, N, Note))).ToList());
        await CaseAsync($"INSERT INTO {t} (ppds_name, ppds_note) SELECT TOP 3 ppds_name, ppds_note FROM {t} ORDER BY ppds_day",
            rows => rows.Concat(rows.OrderBy(r => T(r, Day) ?? DateTime.MinValue).Take(3).Select(r => Copy(r, N, Note))).ToList());
        await CaseAsync($"INSERT INTO {t} (ppds_name, ppds_note) SELECT TOP 2 ppds_name, ppds_note FROM {t} ORDER BY ppds_day DESC",
            rows => rows.Concat(rows.OrderByDescending(r => T(r, Day) ?? DateTime.MinValue).Take(2).Select(r => Copy(r, N, Note))).ToList());
        // several ORDER BY columns: the later ones break ties of the earlier ones
        await CaseAsync($"INSERT INTO {t} (ppds_name) SELECT TOP 3 ppds_name FROM {t} ORDER BY ppds_day, ppds_name",
            rows => rows.Concat(rows.OrderBy(r => T(r, Day) ?? DateTime.MinValue).ThenBy(r => S(r, N), collation).Take(3).Select(r => Copy(r, N))).ToList());
        await CaseAsync($"INSERT INTO {t} (ppds_name) SELECT TOP 4 ppds_name FROM {t} ORDER BY ppds_day DESC, ppds_num",
            rows => rows.Concat(rows.OrderByDescending(r => T(r, Day) ?? DateTime.MinValue).ThenBy(r => I(r, Num) ?? int.MinValue).Take(4).Select(r => Copy(r, N))).ToList());
    }

    // ── hints and the row cap ────────────────────────────────────────────────────────────────

    private async Task HintAndCapCasesAsync()
    {
        var t = _table;
        await CaseAsync($"-- ppds:BATCH_SIZE 2\nUPDATE {t} SET ppds_note = 'B' WHERE {All}", rows => rows.Select(r => r.With(Note, "B")).ToList());
        await CaseAsync($"-- ppds:BYPASS_PLUGINS\nUPDATE {t} SET ppds_note = 'P' WHERE ppds_num = 1", rows => rows.Select(r => I(r, Num) == 1 ? r.With(Note, "P") : r).ToList());
        await CaseAsync($"-- ppds:BYPASS_FLOWS\nUPDATE {t} SET ppds_note = 'F' WHERE ppds_num = 1", rows => rows.Select(r => I(r, Num) == 1 ? r.With(Note, "F") : r).ToList());

        var notNull = _seed.Count(r => I(r, Num) is not null);
        await CaseAsync($"UPDATE {t} SET ppds_note = 'CAP' WHERE ppds_num IS NOT NULL",
            rows => rows.Select(r => I(r, Num) is not null ? r.With(Note, "CAP") : r).ToList(), rowCap: notNull);
        await RefusedAsync($"UPDATE {t} SET ppds_note = 'CAP' WHERE ppds_num IS NOT NULL", rowCap: notNull - 1);
    }

    // ── SELECT checks (read only) ────────────────────────────────────────────────────────────

    /// <summary>A computed item over a GROUP BY column (B, "unverified"): it must return the grouped value, not fail or be NULL.</summary>
    private async Task SelectChecksAsync()
    {
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
            $"UPDATE {t} SET ppds_note = 'x' WHERE ppds_num = 1\nGO\nUPDATE {t} SET ppds_note = 'y' WHERE ppds_num = 2",
            $"DELETE FROM [QA].{t} WHERE ppds_num = 1",
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
            $"UPDATE {t} SET ppds_amount = CAST(ppds_amount AS decimal) WHERE ppds_num = 1",
            $"UPDATE {t} SET ppds_amount = CAST(ppds_amount AS numeric(10, 2)) WHERE ppds_num = 1",
            $"UPDATE {t} SET ppds_amount = TRY_CAST(ppds_amount AS decimal) WHERE ppds_num = 1",
            $"UPDATE {t} SET ppds_note = TRIM(TRAILING FROM ppds_note) WHERE ppds_num = 1",
            $"UPDATE {t} SET ppds_note = SUBSTRING(ppds_name, 0, 2) WHERE ppds_num = 1",
            $"UPDATE {t} SET ppds_note = SUBSTRING(ppds_name, 1, -1) WHERE ppds_num = 1",
            $"UPDATE {t} SET ppds_amount = ROUND(ppds_amount, -1) WHERE ppds_num = 1",
            $"UPDATE {t} SET ppds_note = 'x' WHERE ppds_num = 1 OPTION (MAXDOP 1)",
            $"-- ppds:NOLOCK\nUPDATE {t} SET ppds_note = 'x' WHERE ppds_num = 1",
            $"-- ppds:USE_TDS\nUPDATE {t} SET ppds_note = 'x' WHERE ppds_num = 1",
            $"INSERT INTO {t} (ppds_name) VALUES (UPPER('a'))",
            $"INSERT INTO {t} (ppds_num) VALUES (1 + 2)",
            $"INSERT INTO {t} (ppds_name) SELECT TOP 2 ppds_name FROM {t}",
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
        })
        {
            await RefusedAsync(sql);
        }
    }

    // ── DELETE (last) ────────────────────────────────────────────────────────────────────────

    private async Task DeleteCasesAsync()
    {
        var t = _table;
        var id0 = _ids[0];
        await CaseAsync($"DELETE FROM {t} WHERE ppds_parent = '{id0}'", rows => Delete(rows, r => I(r, Parent) == 0, _ids), observe: true);
        await CaseAsync($"DELETE FROM {t} WHERE {Pk} = '{id0}'", rows => Delete(rows, r => r.Id == id0, _ids), observe: true);
        await CaseAsync($"DELETE a FROM {t} a WHERE a.ppds_num = 4", rows => Delete(rows, r => I(r, Num) == 4, _ids));
        await CaseAsync($"DELETE {t} FROM {t} WHERE {t}.ppds_num = 4", rows => Delete(rows, r => I(r, Num) == 4, _ids));
        await CaseAsync($"DELETE FROM dbo.{t} WHERE ppds_num = 7", rows => Delete(rows, r => I(r, Num) == 7, _ids), observe: true);
        await CaseAsync($"DELETE FROM {t} WHERE ppds_num <> 2", rows => Delete(rows, r => I(r, Num) is { } n && n != 2, _ids), observe: true);
        await CaseAsync($"DELETE FROM {t} WHERE ppds_num NOT IN (1, 2)", rows => Delete(rows, r => I(r, Num) is { } n && n is not (1 or 2), _ids), observe: true);
        await CaseAsync($"DELETE FROM {t} WHERE ppds_num IN (1, 2) OR ppds_name LIKE 'ca%'", rows => Delete(rows, r => I(r, Num) is 1 or 2 || TLike(S(r, N), "ca%"), _ids), observe: true);
        await CaseAsync($"DELETE FROM {t} WHERE ppds_amount BETWEEN 0 AND 10.5", rows => Delete(rows, r => M(r, Amount) >= 0m && M(r, Amount) <= 10.5m, _ids), observe: true);
        await CaseAsync($"DELETE FROM {t} WHERE statecode = 1", rows => Delete(rows, r => I(r, State) == 1, _ids), observe: true);
        await CaseAsync($"DELETE FROM {t} WHERE ppds_name = 'cafe'", rows => Delete(rows, r => TEq(S(r, N), "cafe"), _ids), observe: true);

        // no WHERE, with the environment's no-WHERE prevention switched off
        _service.EnvironmentSafetySettings = new QuerySafetySettings { PreventDeleteWithoutWhere = false };
        try
        {
            await CaseAsync($"DELETE FROM {t}", rows => Delete(rows, _ => true, _ids), observe: true);
        }
        finally
        {
            _service.EnvironmentSafetySettings = null;
        }
    }
}
