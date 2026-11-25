using System.Globalization;
using PPDS.Cli.Services.Query;
using PPDS.LiveTests.Query.SelectFidelity;
using static PPDS.LiveTests.Query.SelectFidelity.TSql;

namespace PPDS.LiveTests.Query;

/// <summary>
/// The case catalogue. Every case states its SQL (tokens {P} {C} {B} {PId} {Red} {Blue} {Green} {Me} {G5} {G5U} are
/// replaced with this run's names and values) and computes its expected result from the in-memory read-back using
/// <see cref="TSql"/> semantics only. The surface is taken from the planner and compiler (ExecutionPlanBuilder*,
/// FetchXmlGenerator, ExpressionCompiler, FunctionRegistry, ScriptExecutionNode, ClientWindowNode), not from
/// specs/query.md alone (which lists far less than the planner accepts).
/// </summary>
public sealed partial class SelectFidelityLiveTests
{
    private static object?[] R(params object?[] values) => values;

    private static Expected E(string columns, IEnumerable<object?[]> rows, bool ordered = false, bool collation = false)
        => new() { Columns = columns.Split('|'), Rows = rows.ToList(), Ordered = ordered, CollationStrings = collation };

    private static Expected Strict(Expected e) => new()
    {
        Columns = e.Columns, Rows = e.Rows, Ordered = e.Ordered, CollationStrings = e.CollationStrings, StrictShape = true
    };

    private string X(string template) => template
        .Replace("ppds_", Prefix + "_")
        .Replace("{PId}", PId).Replace("{P}", P).Replace("{C}", C).Replace("{B}", B)
        .Replace("{Red}", RedV.ToString(CultureInfo.InvariantCulture))
        .Replace("{Blue}", BlueV.ToString(CultureInfo.InvariantCulture))
        .Replace("{Green}", GreenV.ToString(CultureInfo.InvariantCulture))
        .Replace("{Me}", MyUserId.ToString("D"))
        .Replace("{G5}", PRows.Single(r => r.Key == 5).Id.ToString("D"))
        .Replace("{G5U}", PRows.Single(r => r.Key == 5).Id.ToString("B").ToUpperInvariant());

    private Case Q(string id, string area, string sql, Func<Expected> expect, int? top = null, string? dml = null)
        => new() { Id = id, Area = area, Sql = X(sql), Expect = expect, Top = top, DmlWhere = dml is null ? null : X(dml) };

    private Expected Keys(Func<PRow, bool?> predicate) => E("ppds_key", PRows.Where(r => predicate(r) == true).Select(r => R(r.Key)));

    /// <summary>SELECT ppds_key FROM P WHERE &lt;where&gt;; the DELETE dry-run records whether the same filter reaches DML.</summary>
    private Case W(string id, string where, Func<PRow, bool?> predicate, bool dml = false)
        => Q(id, "WHERE", "SELECT ppds_key FROM {P} WHERE " + where, () => Keys(predicate), dml: dml ? where : null);

    /// <summary>SELECT ppds_key, &lt;expr&gt; AS v FROM P [WHERE ...].</summary>
    private Case F(string id, string expr, Func<PRow, object?> value, string? where = null, Func<PRow, bool>? filter = null, string area = "function")
        => Q(id, area, $"SELECT ppds_key, {expr} AS v FROM {{P}}" + (where is null ? "" : " WHERE " + where),
            () => E("ppds_key|v", PRows.Where(filter ?? (_ => true)).Select(r => R(r.Key, value(r)))));

    private PRow? ParentOf(CRow c) => c.ParentId is { } id ? PRows.SingleOrDefault(p => p.Id == id) : null;
    private IEnumerable<(CRow C, PRow P)> InnerJoin() => CRows.Select(c => (c, ParentOf(c))).Where(x => x.Item2 is not null).Select(x => (x.c, x.Item2!));

    private static object? SumInt(IEnumerable<int?> v) { var l = v.Where(x => x.HasValue).Select(x => (long)x!.Value).ToList(); return l.Count == 0 ? null : l.Sum(); }
    private static object? AvgInt(IEnumerable<int?> v) { var l = v.Where(x => x.HasValue).Select(x => (long)x!.Value).ToList(); return l.Count == 0 ? null : l.Sum() / l.Count; }
    private static object? SumDec(IEnumerable<decimal?> v) { var l = v.Where(x => x.HasValue).Select(x => x!.Value).ToList(); return l.Count == 0 ? null : l.Sum(); }
    private static object? AvgDec(IEnumerable<decimal?> v) { var l = v.Where(x => x.HasValue).Select(x => x!.Value).ToList(); return l.Count == 0 ? null : l.Sum() / l.Count; }
    private static object? SumDbl(IEnumerable<double?> v) { var l = v.Where(x => x.HasValue).Select(x => x!.Value).ToList(); return l.Count == 0 ? null : l.Sum(); }
    private static object? AvgDbl(IEnumerable<double?> v) { var l = v.Where(x => x.HasValue).Select(x => x!.Value).ToList(); return l.Count == 0 ? null : l.Average(); }
    private static object? Min(IEnumerable<object?> v) { var l = v.Where(x => x is not null).ToList(); return l.Count == 0 ? null : l.Aggregate((a, b) => Compare(a!, b!) <= 0 ? a : b); }
    private static object? Max(IEnumerable<object?> v) { var l = v.Where(x => x is not null).ToList(); return l.Count == 0 ? null : l.Aggregate((a, b) => Compare(a!, b!) >= 0 ? a : b); }
    private static int CountDistinct(IEnumerable<object?> v) => v.Where(x => x is not null).Distinct(new GroupComparer()).Count();

    private static readonly string[] Months = CultureInfo.InvariantCulture.DateTimeFormat.MonthNames;

    private IEnumerable<Case> BuildCases()
    {
        var all = PRows;
        DateTime Jan15 = U(2024, 1, 15);
        var cases = new List<Case>();
        void Add(Case c) => cases.Add(c);

        // ═════════ Projection ═════════
        Add(Q("PRJ01", "projection", "SELECT ppds_key, ppds_name FROM {P}", () => Strict(E("ppds_key|ppds_name", all.Select(r => R(r.Key, r.Name))))));
        Add(Q("PRJ02", "projection", "SELECT * FROM {P}", () => new Expected
        {
            Columns = ["ppds_key", "ppds_name", "ppds_memo", "ppds_num", "ppds_num2", "ppds_dec", "ppds_money", "ppds_flt", "ppds_flag", "ppds_dt", "ppds_donly", "ppds_colorcode", "ppds_itemtype", "ppds_itemtypename", PId],
            Rows = all.Select(r => R(r.Key, r.Name, r.Memo, r.Num, r.Num2, r.Dec, r.Money, r.Flt, r.Flag, r.Dt, r.DOnly, r.Color, r.ItemType, r.ItemTypeName, r.Id)).ToList(),
            ContainsColumns = true
        }));
        Add(Q("PRJ03", "projection", "SELECT * FROM {P} WHERE ppds_key = 4", () => new Expected
        {
            Columns = ["ppds_key", "ppds_name", "ppds_money", "ppds_flt", "ppds_dt", "ppds_donly", "ppds_colorcode", "ppds_itemtypename"],
            Rows = all.Where(r => r.Key == 4).Select(r => R(r.Key, r.Name, r.Money, r.Flt, r.Dt, r.DOnly, r.Color, r.ItemTypeName)).ToList(),
            ContainsColumns = true
        }));
        Add(Q("PRJ04", "projection", "SELECT ppds_name AS n, ppds_key AS k FROM {P}", () => E("n|k", all.Select(r => R(r.Name, r.Key)))));
        Add(Q("PRJ05", "projection", "SELECT ppds_key, ppds_name AS n, UPPER(ppds_memo) AS d FROM {P}", () => E("ppds_key|n|d", all.Select(r => R(r.Key, r.Name, Upper(r.Memo))))));
        Add(Q("PRJ06", "projection", "SELECT ppds_key, ppds_num + ppds_num2 AS s FROM {P}", () => E("ppds_key|s", all.Select(r => R(r.Key, r.Num + r.Num2)))));
        Add(Q("PRJ07", "projection", "SELECT ppds_key, ppds_num * 2 AS d, ppds_num - 1 AS m, ppds_num / 3 AS q, ppds_num % 3 AS r FROM {P} WHERE ppds_num IS NOT NULL",
            () => E("ppds_key|d|m|q|r", all.Where(r => r.Num is not null).Select(r => R(r.Key, r.Num * 2, r.Num - 1, r.Num / 3, r.Num % 3)))));
        Add(Q("PRJ08", "projection", "SELECT ppds_key, ppds_dec * 2 AS d2, ppds_money + 1 AS m1, ppds_flt * 2 AS f2 FROM {P}",
            () => E("ppds_key|d2|m1|f2", all.Select(r => R(r.Key, r.Dec * 2, r.Money + 1, r.Flt * 2)))));
        Add(Q("PRJ09", "projection", "SELECT ppds_key, ppds_name + '!' AS bang FROM {P}", () => E("ppds_key|bang", all.Select(r => R(r.Key, r.Name is null ? null : r.Name + "!")))));
        Add(Q("PRJ10", "projection", "SELECT ppds_key, -ppds_num AS neg FROM {P}", () => E("ppds_key|neg", all.Select(r => R(r.Key, -r.Num)))));
        Add(Q("PRJ11", "projection", "SELECT ppds_key, 'lit' AS s, 42 AS i, NULL AS n FROM {P} WHERE ppds_key <= 3",
            () => E("ppds_key|s|i|n", all.Where(r => r.Key <= 3).Select(r => R(r.Key, "lit", 42, null)))));
        Add(Q("PRJ12", "projection", "SELECT ppds_key, ppds_key FROM {P} WHERE ppds_key <= 2", () => E("ppds_key|ppds_key", all.Where(r => r.Key <= 2).Select(r => R(r.Key, r.Key)))));
        Add(Q("PRJ13", "projection", "SELECT ppds_name FROM {P}", () => Strict(E("ppds_name", all.Select(r => R(r.Name))))));
        Add(Q("PRJ22", "projection", "SELECT c.ppds_key FROM {C} c", () => Strict(E("ppds_key", CRows.Select(r => R(r.Key))))));
        Add(Q("PRJ14", "projection", "SELECT ppds_key, UPPER(ppds_name) FROM {P}", () => E("ppds_key|", all.Select(r => R(r.Key, Upper(r.Name))))));
        Add(Q("PRJ15", "projection", "SELECT UPPER(ppds_name), LOWER(ppds_name) FROM {P} WHERE ppds_key <= 3", () => E("|", all.Where(r => r.Key <= 3).Select(r => R(Upper(r.Name), Lower(r.Name))))));
        Add(Q("PRJ16", "projection", "SELECT p.ppds_key, p.ppds_name FROM {P} p", () => E("ppds_key|ppds_name", all.Select(r => R(r.Key, r.Name)))));
        Add(Q("PRJ17", "projection", "SELECT {P}.ppds_key FROM {P}", () => E("ppds_key", all.Select(r => R(r.Key)))));
        Add(Q("PRJ18", "projection", "SELECT [ppds_key], [ppds_name] AS [my name] FROM [{P}]", () => E("ppds_key|my name", all.Select(r => R(r.Key, r.Name)))));
        Add(Q("PRJ19", "projection", "SELECT ppds_key AS ppds_num, ppds_num AS ppds_key FROM {P}", () => E("ppds_num|ppds_key", all.Select(r => R(r.Key, r.Num)))));
        Add(Q("PRJ20", "projection", "SELECT ppds_key, ppds_num AS n, ppds_num + 1 AS m FROM {P}", () => E("ppds_key|n|m", all.Select(r => R(r.Key, r.Num, r.Num + 1)))));
        Add(Q("PRJ21", "projection", "SELECT ppds_key, ppds_name AS n, ppds_key * 10 AS k10 FROM {P}", () => E("ppds_key|n|k10", all.Select(r => R(r.Key, r.Name, r.Key * 10)))));

        // ═════════ Scalar functions (string) ═════════
        Add(F("FN01", "UPPER(ppds_name)", r => Upper(r.Name)));
        Add(F("FN02", "LOWER(ppds_name)", r => Lower(r.Name)));
        Add(F("FN03", "LEN(ppds_name)", r => Len(r.Name)));
        Add(F("FN04", "LEFT(ppds_name, 3)", r => r.Name is null ? null : r.Name.Length <= 3 ? r.Name : r.Name[..3]));
        Add(F("FN05", "RIGHT(ppds_name, 2)", r => r.Name is null ? null : r.Name.Length <= 2 ? r.Name : r.Name[^2..]));
        Add(F("FN06", "SUBSTRING(ppds_name, 2, 3)", r => Substring(r.Name, 2, 3)));
        Add(F("FN07", "LTRIM(ppds_name)", r => r.Name?.TrimStart(' ')));
        Add(F("FN08", "RTRIM(ppds_name)", r => r.Name?.TrimEnd(' ')));
        Add(F("FN09", "TRIM(ppds_name)", r => r.Name?.Trim(' ')));
        Add(F("FN10", "REPLACE(ppds_name, 'a', 'o')", r => Replace(r.Name, "a", "o")));
        Add(F("FN11", "CHARINDEX('a', ppds_name)", r => CharIndex("a", r.Name)));
        Add(F("FN12", "PATINDEX('%a%', ppds_name)", r => CharIndex("a", r.Name)));
        Add(F("FN13", "CONCAT(ppds_name, '-', ppds_num)", r => (r.Name ?? "") + "-" + (r.Num?.ToString(CultureInfo.InvariantCulture) ?? "")));
        Add(F("FN14", "CONCAT_WS('-', ppds_name, ppds_memo)", r => string.Join("-", new[] { r.Name, r.Memo }.Where(s => s is not null))));
        Add(F("FN15", "REVERSE(ppds_name)", r => r.Name is null ? null : new string(r.Name.Reverse().ToArray())));
        Add(F("FN16", "REPLICATE(ppds_name, 2)", r => r.Name is null ? null : r.Name + r.Name));
        Add(F("FN17", "STUFF(ppds_name, 2, 1, 'X')", r => r.Name is null || r.Name.Length < 2 ? null : r.Name[..1] + "X" + r.Name[2..]));
        Add(F("FN18", "SPACE(2) + ppds_name", r => r.Name is null ? null : "  " + r.Name));
        Add(F("FN19", "QUOTENAME(ppds_name)", r => r.Name is null ? null : "[" + r.Name.Replace("]", "]]") + "]"));
        Add(F("FN20", "UNICODE(ppds_name)", r => string.IsNullOrEmpty(r.Name) ? null : (int)r.Name[0]));
        Add(F("FN21", "CHAR(65)", _ => "A", "ppds_key <= 2", r => r.Key <= 2));
        Add(F("FN22", "SOUNDEX('Robert')", _ => "R163", "ppds_key = 1", r => r.Key == 1));
        Add(F("FN23", "DIFFERENCE('Smith', 'Smyth')", _ => 4, "ppds_key = 1", r => r.Key == 1));
        Add(F("FN24", "FORMAT(ppds_num, 'D5')", r => r.Num?.ToString("D5", CultureInfo.InvariantCulture)));
        Add(F("FN25", "JSON_VALUE('{\"a\":5}', '$.a')", _ => "5", "ppds_key = 1", r => r.Key == 1));
        Add(F("FN26", "ISJSON('{\"a\":1}')", _ => 1, "ppds_key = 1", r => r.Key == 1));
        // math
        Add(F("FN30", "ABS(ppds_num)", r => r.Num is null ? null : Math.Abs(r.Num.Value)));
        Add(F("FN31", "CEILING(ppds_dec)", r => r.Dec is null ? null : Math.Ceiling(r.Dec.Value)));
        Add(F("FN32", "FLOOR(ppds_dec)", r => r.Dec is null ? null : Math.Floor(r.Dec.Value)));
        Add(F("FN33", "ROUND(ppds_dec, 1)", r => r.Dec is null ? null : Math.Round(r.Dec.Value, 1, MidpointRounding.AwayFromZero)));
        Add(F("FN34", "ROUND(ppds_flt, 0)", r => r.Flt is null ? null : Math.Round(r.Flt.Value, 0, MidpointRounding.AwayFromZero)));
        Add(F("FN35", "SIGN(ppds_num)", r => r.Num is null ? null : Math.Sign(r.Num.Value)));
        Add(F("FN36", "SQRT(ABS(ppds_num))", r => r.Num is null ? null : Math.Sqrt(Math.Abs(r.Num.Value))));
        Add(F("FN37", "SQUARE(ppds_num)", r => r.Num is null ? null : (double)r.Num.Value * r.Num.Value));
        Add(F("FN38", "POWER(ppds_num, 2)", r => r.Num is null ? null : r.Num.Value * r.Num.Value));
        Add(Q("FN39", "function", "SELECT EXP(0) AS e, LOG10(100) AS l, PI() AS p, SIN(0) AS s, COS(0) AS c, DEGREES(PI()) AS d, RADIANS(CAST(180 AS float)) AS r, ATN2(1, 1) AS a FROM {P} WHERE ppds_key = 1",
            () => E("e|l|p|s|c|d|r|a", [R(1.0, 2.0, Math.PI, 0.0, 1.0, 180.0, Math.PI, Math.Atan2(1, 1))])));
        // dates
        Add(F("FN40", "YEAR(ppds_dt)", r => r.Dt?.Year));
        Add(F("FN41", "MONTH(ppds_dt)", r => r.Dt?.Month));
        Add(F("FN42", "DAY(ppds_dt)", r => r.Dt?.Day));
        Add(F("FN43", "DATEPART(year, ppds_dt)", r => r.Dt?.Year));
        Add(F("FN44", "DATEPART(hour, ppds_dt)", r => r.Dt?.Hour));
        Add(F("FN45", "DATEADD(day, 1, ppds_dt)", r => r.Dt?.AddDays(1)));
        Add(F("FN46", "DATEADD(month, 1, ppds_donly)", r => r.DOnly?.AddMonths(1)));
        Add(F("FN47", "DATEDIFF(day, ppds_dt, '2024-01-01')", r => r.Dt is null ? null : (int)(new DateTime(2024, 1, 1) - r.Dt.Value.Date).TotalDays));
        Add(F("FN48", "DATENAME(month, ppds_dt)", r => r.Dt is null ? null : Months[r.Dt.Value.Month - 1]));
        Add(F("FN49", "EOMONTH(ppds_donly)", r => r.DOnly is null ? null : new DateTime(r.DOnly.Value.Year, r.DOnly.Value.Month, DateTime.DaysInMonth(r.DOnly.Value.Year, r.DOnly.Value.Month))));
        Add(F("FN50", "DATEFROMPARTS(2024, 1, 15)", _ => new DateTime(2024, 1, 15), "ppds_key = 1", r => r.Key == 1));
        Add(F("FN51", "DATETRUNC(month, ppds_dt)", r => r.Dt is null ? null : new DateTime(r.Dt.Value.Year, r.Dt.Value.Month, 1)));
        Add(F("FN52", "CASE WHEN GETUTCDATE() > '2020-01-01' THEN 1 ELSE 0 END", _ => 1, "ppds_key = 1", r => r.Key == 1));
        Add(F("FN53", "CASE WHEN YEAR(GETDATE()) >= 2026 THEN 1 ELSE 0 END", _ => 1, "ppds_key = 1", r => r.Key == 1));
        Add(F("FN54", "CAST(ppds_dt AS date)", r => r.Dt?.Date));
        // null functions and CASE
        Add(F("FN60", "ISNULL(ppds_num, -1)", r => r.Num ?? -1));
        Add(F("FN61", "COALESCE(ppds_num, ppds_num2, 0)", r => r.Num ?? r.Num2 ?? 0));
        Add(F("FN62", "NULLIF(ppds_num, 5)", r => r.Num == 5 ? null : r.Num));
        Add(F("FN63", "IIF(ppds_num > 5, 'big', 'small')", r => Gt(r.Num, 5) == true ? "big" : "small"));
        Add(F("FN64", "CASE WHEN ppds_num > 5 THEN 'big' WHEN ppds_num < 0 THEN 'neg' ELSE 'other' END", r => Gt(r.Num, 5) == true ? "big" : Lt(r.Num, 0) == true ? "neg" : "other"));
        Add(F("FN65", "CASE WHEN ppds_num > 5 THEN 'big' END", r => Gt(r.Num, 5) == true ? "big" : null));
        Add(F("FN66", "CASE WHEN NOT (ppds_num > 5) THEN 'small' ELSE 'other' END", r => Not(Gt(r.Num, 5)) == true ? "small" : "other"));
        Add(F("FN67", "CASE ppds_colorcode WHEN {Red} THEN 'r' WHEN {Blue} THEN 'b' ELSE 'other' END", r => r.Color == RedV ? "r" : r.Color == BlueV ? "b" : "other"));
        Add(F("FN68", "CASE WHEN ppds_name = 'cafe' THEN 1 ELSE 0 END", r => Eq(r.Name, "cafe") == true ? 1 : 0));
        Add(F("FN69", "CASE WHEN ppds_name = 'trail' THEN 1 ELSE 0 END", r => Eq(r.Name, "trail") == true ? 1 : 0));
        Add(F("FN70", "COALESCE(ppds_memo, ppds_name)", r => r.Memo ?? r.Name));
        Add(F("FN71", "IIF(ppds_flag = 1, 'yes', 'no')", r => r.Flag == true ? "yes" : "no"));
        // CAST / CONVERT / TRY_
        Add(F("FN80", "CAST(ppds_num AS nvarchar(10))", r => r.Num?.ToString(CultureInfo.InvariantCulture)));
        Add(F("FN81", "CAST(ppds_dec AS int)", r => r.Dec is null ? null : (int)decimal.Truncate(r.Dec.Value)));
        Add(F("FN82", "CAST('12' AS int)", _ => 12, "ppds_key = 1", r => r.Key == 1));
        Add(F("FN83", "CAST(ppds_num AS decimal(10,2))", r => (decimal?)r.Num));
        Add(F("FN84", "CONVERT(nvarchar(10), ppds_dt, 23)", r => r.Dt?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
        Add(F("FN85", "CAST(ppds_flag AS int)", r => r.Flag is null ? null : r.Flag.Value ? 1 : 0));
        Add(F("FN86", "TRY_CAST(ppds_name AS int)", r => TryInt(r.Name)));
        Add(F("FN87", "TRY_CONVERT(int, ppds_name)", r => TryInt(r.Name)));
        Add(F("FN88", "CAST(ppds_name AS int)", r => TryInt(r.Name), "ppds_key IN (26, 27)", r => r.Key is 26 or 27));
        Add(F("FN89", "CONVERT(int, ppds_dec)", r => r.Dec is null ? null : (int)decimal.Truncate(r.Dec.Value)));
        Add(F("FN90", "CAST(ppds_money AS decimal(12,1))", r => r.Money is null ? null : Math.Round(r.Money.Value, 1, MidpointRounding.AwayFromZero)));
        Add(Q("FN91", "function", "SELECT STRING_AGG(ppds_itemtype, ',') WITHIN GROUP (ORDER BY ppds_key) AS v FROM {P} WHERE ppds_key <= 5",
            () => E("v", [R(string.Join(",", all.Where(r => r.Key <= 5 && r.ItemType is not null).OrderBy(r => r.Key).Select(r => r.ItemType)))])));

        // ═════════ WHERE ═════════
        Add(W("W01", "ppds_num = 5", r => Eq(r.Num, 5)));
        Add(W("W02", "ppds_num <> 5", r => Ne(r.Num, 5), dml: true));
        Add(W("W03", "ppds_num != 5", r => Ne(r.Num, 5)));
        Add(W("W04", "ppds_num < 5", r => Lt(r.Num, 5)));
        Add(W("W05", "ppds_num > 5", r => Gt(r.Num, 5)));
        Add(W("W06", "ppds_num <= 5", r => Le(r.Num, 5)));
        Add(W("W07", "ppds_num >= 5", r => Ge(r.Num, 5)));
        Add(W("W08", "5 < ppds_num", r => Gt(r.Num, 5)));
        Add(W("W09", "NOT (ppds_num = 5)", r => Not(Eq(r.Num, 5)), dml: true));
        Add(W("W10", "NOT ppds_num = 5", r => Not(Eq(r.Num, 5))));
        Add(W("W11", "NOT (ppds_num > 5)", r => Not(Gt(r.Num, 5))));
        Add(W("W12", "NOT (ppds_num <> 5)", r => Not(Ne(r.Num, 5))));
        Add(W("W13", "NOT NOT ppds_num = 5", r => Eq(r.Num, 5)));
        Add(W("W14", "ppds_num = -1", r => Eq(r.Num, -1)));
        Add(W("W15", "ppds_num = 5.0", r => Eq(r.Num, 5m)));
        Add(W("W16", "ppds_name = 'alpha'", r => Eq(r.Name, "alpha")));
        Add(W("W17", "ppds_name = 'cafe'", r => Eq(r.Name, "cafe")));
        Add(W("W18", "ppds_name = 'trail'", r => Eq(r.Name, "trail")));
        Add(W("W19", "ppds_name <> 'Alpha'", r => Ne(r.Name, "Alpha")));
        Add(W("W20", "ppds_name = 'O''Brien'", r => Eq(r.Name, "O'Brien")));
        Add(W("W21", "ppds_name > 'x'", r => Gt(r.Name, "x")));
        Add(W("W22", "ppds_name = N'Café'", r => Eq(r.Name, "Café")));
        Add(W("W23", "ppds_name = ''", r => Eq(r.Name, "")));
        Add(W("W24", "ppds_name = NULL", _ => null, dml: true));
        Add(W("W25", "ppds_name <> NULL", _ => null));
        Add(W("W26", "ppds_num = NULL", _ => null));
        Add(W("W27", "ppds_name IS NULL", r => IsNull(r.Name)));
        Add(W("W28", "ppds_name IS NOT NULL", r => !IsNull(r.Name)));
        Add(W("W29", "NOT (ppds_name IS NULL)", r => Not(IsNull(r.Name)), dml: true));
        Add(W("W30", "NOT ppds_name IS NULL", r => Not(IsNull(r.Name))));
        Add(W("W31", "NOT (ppds_name IS NOT NULL)", r => IsNull(r.Name)));
        Add(W("W32", "ppds_memo IS NULL", r => IsNull(r.Memo)));
        Add(W("W33", "ppds_dec IS NULL", r => IsNull(r.Dec)));
        Add(W("W34", "ppds_money IS NULL", r => IsNull(r.Money)));
        Add(W("W35", "ppds_flt IS NULL", r => IsNull(r.Flt)));
        Add(W("W36", "ppds_dt IS NULL", r => IsNull(r.Dt)));
        Add(W("W37", "ppds_donly IS NULL", r => IsNull(r.DOnly)));
        Add(W("W38", "ppds_colorcode IS NULL", r => IsNull(r.Color)));
        Add(W("W39", "ppds_multi IS NULL", r => IsNull(r.Multi)));
        Add(W("W40", "ppds_flag IS NULL", r => IsNull(r.Flag)));
        Add(W("W41", "ppds_num = 5 OR ppds_num = 10 AND ppds_name = 'Alpha'", r => Or(Eq(r.Num, 5), And(Eq(r.Num, 10), Eq(r.Name, "Alpha")))));
        Add(W("W42", "(ppds_num = 5 OR ppds_num = 10) AND ppds_name = 'Alpha'", r => And(Or(Eq(r.Num, 5), Eq(r.Num, 10)), Eq(r.Name, "Alpha"))));
        Add(W("W43", "((ppds_num > 0 OR ppds_num2 > 0) AND (ppds_flag = 1 OR ppds_colorcode = {Red})) OR ppds_key = 36",
            r => Or(And(Or(Gt(r.Num, 0), Gt(r.Num2, 0)), Or(Eq(r.Flag, true), Eq(r.Color, RedV))), Eq(r.Key, 36))));
        Add(W("W44", "NOT (ppds_num = 5 AND ppds_name = 'dup')", r => Not(And(Eq(r.Num, 5), Eq(r.Name, "dup"))), dml: true));
        Add(W("W45", "NOT (ppds_num = 5 OR ppds_num = 10)", r => Not(Or(Eq(r.Num, 5), Eq(r.Num, 10)))));
        Add(W("W46", "NOT (ppds_num IN (5, 10))", r => Not(In(r.Num, 5, 10))));
        Add(W("W47", "ppds_num = 5 AND NOT (ppds_name = 'dup')", r => And(Eq(r.Num, 5), Not(Eq(r.Name, "dup")))));
        Add(W("W48", "NOT (ppds_num > 0 AND ppds_num2 > 0)", r => Not(And(Gt(r.Num, 0), Gt(r.Num2, 0)))));
        Add(W("W49", "ppds_num IN (5, 10)", r => In(r.Num, 5, 10)));
        Add(W("W50", "ppds_num NOT IN (5, 10)", r => Not(In(r.Num, 5, 10)), dml: true));
        Add(W("W51", "ppds_num IN (5, NULL)", r => In(r.Num, 5, null)));
        Add(W("W52", "ppds_num NOT IN (5, NULL)", r => Not(In(r.Num, 5, null)), dml: true));
        Add(W("W53", "ppds_name IN ('alpha', 'Beta')", r => In(r.Name, "alpha", "Beta")));
        Add(W("W54", "ppds_colorcode IN ({Red}, {Green})", r => In(r.Color, RedV, GreenV)));
        Add(W("W55", "ppds_name NOT IN ('alpha')", r => Not(In(r.Name, "alpha"))));
        Add(W("W56", "ppds_name LIKE 'A%'", r => Like(r.Name, "A%")));
        Add(W("W57", "ppds_name LIKE '%a'", r => Like(r.Name, "%a")));
        Add(W("W58", "ppds_name LIKE '%ph%'", r => Like(r.Name, "%ph%")));
        Add(W("W59", "ppds_name LIKE 'a_c'", r => Like(r.Name, "a_c")));
        Add(W("W60", "ppds_name LIKE 'a\\_c' ESCAPE '\\'", r => Like(r.Name, "a\\_c", '\\'), dml: true));
        Add(W("W61", "ppds_name LIKE '%[%]%'", r => Like(r.Name, "%[%]%")));
        Add(W("W62", "ppds_name LIKE '[ab]%'", r => Like(r.Name, "[ab]%"), dml: true));
        Add(W("W63", "ppds_name LIKE '[^a]%'", r => Like(r.Name, "[^a]%")));
        Add(W("W64", "ppds_name LIKE '%[_]%'", r => Like(r.Name, "%[_]%")));
        Add(W("W65", "ppds_name LIKE '50[%]%'", r => Like(r.Name, "50[%]%")));
        Add(W("W66", "ppds_name LIKE '_'", r => Like(r.Name, "_")));
        Add(W("W67", "ppds_name LIKE '%'", r => Like(r.Name, "%")));
        Add(W("W68", "ppds_name NOT LIKE 'A%'", r => Not(Like(r.Name, "A%")), dml: true));
        Add(W("W69", "ppds_name LIKE 'alpha'", r => Like(r.Name, "alpha")));
        Add(W("W70", "ppds_name LIKE 'Caf_'", r => Like(r.Name, "Caf_")));
        Add(W("W71", "ppds_name LIKE '%é%'", r => Like(r.Name, "%é%")));
        Add(W("W72", "ppds_name LIKE 'a[[]b]c'", r => Like(r.Name, "a[[]b]c")));
        Add(W("W73", "ppds_name LIKE 'under_score%'", r => Like(r.Name, "under_score%")));
        Add(W("W74", "ppds_name LIKE '%\\%%' ESCAPE '\\'", r => Like(r.Name, "%\\%%", '\\')));
        Add(W("W75", "ppds_name NOT LIKE '%a%'", r => Not(Like(r.Name, "%a%"))));
        Add(W("W76", "ppds_name LIKE ppds_itemtype + '%'", r => r.ItemType is null ? null : Like(r.Name, r.ItemType + "%"), dml: true));
        Add(W("W77", "UPPER(ppds_name) LIKE 'A%'", r => Like((string?)Upper(r.Name), "A%"), dml: true));
        Add(W("W78", "ppds_name LIKE 'A%' OR ppds_num = 5", r => Or(Like(r.Name, "A%"), Eq(r.Num, 5))));
        Add(W("W79", "ppds_name LIKE '[a-c]%'", r => Like(r.Name, "[a-c]%")));
        Add(W("W80", "ppds_num BETWEEN 0 AND 10", r => Between(r.Num, 0, 10)));
        Add(W("W81", "ppds_num NOT BETWEEN 0 AND 10", r => Not(Between(r.Num, 0, 10)), dml: true));
        Add(W("W82", "ppds_num BETWEEN 10 AND 0", r => Between(r.Num, 10, 0)));
        Add(W("W83", "ppds_name BETWEEN 'a' AND 'b'", r => Between(r.Name, "a", "b")));
        Add(W("W84", "ppds_dec BETWEEN -1 AND 1.25", r => Between(r.Dec, -1m, 1.25m)));
        Add(W("W85", "ppds_dt BETWEEN '2024-01-01' AND '2024-12-31'", r => Between(r.Dt, U(2024, 1, 1), U(2024, 12, 31))));
        Add(W("W86", "ppds_num = ppds_num2", r => Eq(r.Num, r.Num2)));
        Add(W("W87", "ppds_num <> ppds_num2", r => Ne(r.Num, r.Num2)));
        Add(W("W88", "ppds_num > ppds_num2", r => Gt(r.Num, r.Num2)));
        Add(W("W89", "ppds_num = ppds_num2 OR ppds_name = 'Beta'", r => Or(Eq(r.Num, r.Num2), Eq(r.Name, "Beta")), dml: true));
        Add(W("W90", "NOT (ppds_num = ppds_num2)", r => Not(Eq(r.Num, r.Num2)), dml: true));
        Add(W("W91", "ppds_num = ppds_num2 AND ppds_flag = 1", r => And(Eq(r.Num, r.Num2), Eq(r.Flag, true))));
        Add(W("W92", "UPPER(ppds_name) = 'ALPHA'", r => Eq(Upper(r.Name), "ALPHA")));
        Add(W("W93", "LEN(ppds_name) = 3", r => Eq(Len(r.Name), 3)));
        Add(W("W94", "ISNULL(ppds_num, 0) = 0", r => Eq(r.Num ?? 0, 0)));
        Add(W("W95", "ppds_num + 1 = 6", r => Eq(r.Num + 1, 6)));
        Add(W("W96", "YEAR(ppds_dt) = 2024", r => Eq(r.Dt?.Year, 2024)));
        Add(W("W97", "ISNULL(ppds_name, '') IN ('', 'x')", r => In(r.Name ?? "", "", "x"), dml: true));
        Add(W("W98", "LEN(ppds_name) BETWEEN 2 AND 3", r => Between(Len(r.Name), 2, 3), dml: true));
        Add(W("W99", "COALESCE(ppds_num, ppds_num2) IS NULL", r => IsNull(r.Num ?? r.Num2), dml: true));
        Add(W("W100", "UPPER(ppds_name) = 'CAFE'", r => Eq(Upper(r.Name), "CAFE")));
        Add(W("W101", "ppds_dt >= '2024-01-15'", r => Ge(r.Dt, Jan15)));
        Add(W("W102", "ppds_dt < '2024-01-15T12:00:00'", r => Lt(r.Dt, U(2024, 1, 15, 12))));
        Add(W("W103", "ppds_dt = '2024-01-15 00:30:00'", r => Eq(r.Dt, U(2024, 1, 15, 0, 30))));
        Add(W("W104", "ppds_dt > '20240115'", r => Gt(r.Dt, Jan15)));
        Add(W("W105", "ppds_dt >= '2024-01-15T00:00:00Z'", r => Ge(r.Dt, Jan15)));
        Add(W("W106", "ppds_dt >= '2024-01-15T00:00:00+00:00'", r => Ge(r.Dt, Jan15)));
        Add(W("W107", "ppds_donly = '2024-01-15'", r => Eq(r.DOnly, D(2024, 1, 15))));
        Add(W("W108", "ppds_donly >= '2024-03-10'", r => Ge(r.DOnly, D(2024, 3, 10))));
        Add(W("W109", "ppds_dt >= CAST('2024-01-15' AS datetime)", r => Ge(r.Dt, Jan15)));
        Add(W("W110", "CAST(ppds_dt AS date) = '2024-01-15'", r => Eq(r.Dt?.Date, Jan15)));
        Add(W("W111", "ppds_dt < '1901-01-01'", r => Lt(r.Dt, U(1901, 1, 1))));
        Add(W("W112", "ppds_donly BETWEEN '2024-01-14' AND '2024-01-16'", r => Between(r.DOnly, D(2024, 1, 14), D(2024, 1, 16))));
        Add(W("W113", "{PId} = '{G5}'", r => r.Key == 5));
        Add(W("W114", "{PId} = '{G5U}'", r => r.Key == 5));
        Add(W("W115", "ppds_flag = 1", r => Eq(r.Flag, true)));
        Add(W("W116", "ppds_flag = 0", r => Eq(r.Flag, false)));
        Add(W("W117", "ppds_flag <> 1", r => Ne(r.Flag, true)));
        Add(W("W118", "ppds_flag = 'true'", r => Eq(r.Flag, true)));
        Add(W("W119", "ppds_colorcode = {Blue}", r => Eq(r.Color, BlueV)));
        Add(W("W120", "ppds_colorcode <> {Blue}", r => Ne(r.Color, BlueV)));
        Add(W("W121", "ppds_dec = 1.25", r => Eq(r.Dec, 1.25m)));
        Add(W("W122", "ppds_money >= 50", r => Ge(r.Money, 50m)));
        Add(W("W123", "ppds_flt < 0", r => Lt(r.Flt, 0)));
        Add(W("W124", "ppds_money = -0.01", r => Eq(r.Money, -0.01m)));
        Add(W("W125", "ppds_memo = 'first memo'", r => Eq(r.Memo, "first memo")));
        Add(W("W126", "ppds_memo LIKE '%memo%'", r => Like(r.Memo, "%memo%")));
        Add(W("W127", "ppds_num IS DISTINCT FROM ppds_num2", r => !GroupEquals(r.Num, r.Num2)));
        Add(W("W128", "ppds_num IS NOT DISTINCT FROM ppds_num2", r => GroupEquals(r.Num, r.Num2)));
        Add(W("W129", "ppds_itemtypename = 'ItemA'", r => Eq(r.ItemTypeName, "ItemA")));
        Add(W("W130", "p.ppds_key = 1", r => r.Key == 1) with { Sql = X("SELECT ppds_key FROM {P} p WHERE p.ppds_key = 1") });
        Add(W("W131", "{P}.ppds_key = 2", r => r.Key == 2));
        Add(W("W132", "(ppds_key = 1)", r => r.Key == 1));
        Add(W("W133", "ppds_key IN (1, 2) AND (ppds_name IS NULL OR ppds_name = 'Alpha')", r => And(In(r.Key, 1, 2), Or(IsNull(r.Name), Eq(r.Name, "Alpha")))));
        Add(W("W134", "ppds_name COLLATE Latin1_General_CS_AS = 'alpha'", r => r.Name == "alpha"));
        Add(W("W135", "ppds_name = 'alpha' COLLATE Latin1_General_CS_AS", r => r.Name == "alpha"));
        Add(W("W136", "ppds_num BETWEEN ppds_num2 AND 10", r => Between(r.Num, r.Num2, 10), dml: true));
        Add(W("W137", "ppds_num IN (ppds_num2, 100)", r => In(r.Num, r.Num2, 100), dml: true));
        Add(W("W138", "ppds_num2 IS NULL OR ppds_num = ppds_num2", r => Or(IsNull(r.Num2), Eq(r.Num, r.Num2))));

        // ═════════ Variables and scripts ═════════
        Add(Q("X01", "script", "DECLARE @n nvarchar(20) = 'Alpha'; SELECT ppds_key FROM {P} WHERE ppds_name = @n", () => Keys(r => Eq(r.Name, "Alpha"))));
        Add(Q("X02", "script", "DECLARE @i int = 5; SELECT ppds_key FROM {P} WHERE ppds_num = @i", () => Keys(r => Eq(r.Num, 5))));
        Add(Q("X03", "script", "DECLARE @i int; SET @i = 7; SELECT ppds_key FROM {P} WHERE ppds_num > @i", () => Keys(r => Gt(r.Num, 7))));
        Add(Q("X04", "script", "DECLARE @i int = 5; SELECT ppds_key FROM {P} WHERE ppds_num = @i + 0", () => Keys(r => Eq(r.Num, 5))));
        Add(Q("X05", "script", "DECLARE @d datetime = '2024-01-15'; SELECT ppds_key FROM {P} WHERE ppds_dt >= @d", () => Keys(r => Ge(r.Dt, Jan15))));
        Add(Q("X06", "script", "IF (SELECT COUNT(*) FROM {P}) > 10 SELECT 'many' AS r ELSE SELECT 'few' AS r", () => E("r", [R(all.Count > 10 ? "many" : "few")])));
        Add(Q("X07", "script", "DECLARE @i int = 0; WHILE @i < 3 SET @i = @i + 1; SELECT ppds_key FROM {P} WHERE ppds_key = @i", () => Keys(r => r.Key == 3)));
        Add(Q("X08", "script", "SELECT ppds_key FROM {P} WHERE ppds_key = 1; SELECT ppds_key FROM {P} WHERE ppds_key = 2",
            () => new Expected { Columns = ["ppds_key"], Rows = [R(1)], MultipleResultSets = "two result sets: (1) then (2)" }));
        Add(Q("X09", "script", "DECLARE @v int; SELECT @v = MAX(ppds_num) FROM {P}; SELECT ppds_key FROM {P} WHERE ppds_num = @v", () => Keys(r => Eq(r.Num, Max(all.Select(x => (object?)x.Num))))));
        Add(Q("X10", "script", "SELECT ppds_key INTO #t FROM {P} WHERE ppds_key < 4; SELECT ppds_key FROM #t", () => Keys(r => r.Key < 4)));
        Add(Q("X11", "script", "DECLARE @pat nvarchar(10) = 'A%'; SELECT ppds_key FROM {P} WHERE ppds_name LIKE @pat", () => Keys(r => Like(r.Name, "A%"))));
        Add(Q("X12", "script", "DECLARE @i int = 5; SELECT ppds_key FROM {P} WHERE ppds_num IN (@i, 10)", () => Keys(r => In(r.Num, 5, 10))));
        Add(Q("X13", "script", "DECLARE @n nvarchar(20) = NULL; SELECT ppds_key FROM {P} WHERE ppds_name = @n", () => Keys(_ => null)));
        Add(Q("X14", "script", "DECLARE @i int = 5; SELECT ppds_key, @i AS v FROM {P} WHERE ppds_key <= 2", () => E("ppds_key|v", all.Where(r => r.Key <= 2).Select(r => R(r.Key, 5)))));
        Add(Q("X15", "script", "SELECT 1 + 1 AS x", () => E("x", [R(2)])));

        // ═════════ TOP / OFFSET / DISTINCT / ORDER BY ═════════
        Add(Q("T01", "top", "SELECT TOP 5 ppds_key FROM {P} ORDER BY ppds_key", () => E("ppds_key", all.OrderBy(r => r.Key).Take(5).Select(r => R(r.Key)), ordered: true)));
        Add(Q("T02", "top", "SELECT TOP (5) ppds_key FROM {P} ORDER BY ppds_key", () => E("ppds_key", all.OrderBy(r => r.Key).Take(5).Select(r => R(r.Key)), ordered: true)));
        Add(Q("T03", "top", "SELECT TOP 5 ppds_key FROM {P} ORDER BY ppds_num DESC, ppds_key",
            () => E("ppds_key", OrderBy(all, (r => r.Num, true), (r => r.Key, false)).Take(5).Select(r => R(r.Key)), ordered: true)));
        Add(Q("T04", "top", "SELECT TOP 0 ppds_key FROM {P}", () => E("ppds_key", [])));
        Add(Q("T05", "top", "SELECT TOP 50 PERCENT ppds_key FROM {P} ORDER BY ppds_key",
            () => E("ppds_key", all.OrderBy(r => r.Key).Take((int)Math.Ceiling(all.Count * 0.5)).Select(r => R(r.Key)), ordered: true)));
        Add(Q("T06", "top", "SELECT TOP 3 WITH TIES ppds_key, ppds_num FROM {P} WHERE ppds_num IS NOT NULL ORDER BY ppds_num", () =>
        {
            var sorted = all.Where(r => r.Num is not null).OrderBy(r => r.Num).ToList();
            var cut = sorted[2].Num;
            return E("ppds_key|ppds_num", sorted.Where(r => r.Num <= cut).Select(r => R(r.Key, r.Num)));
        }));
        Add(Q("T07", "top", "SELECT TOP 3 ppds_key FROM {P} WHERE ppds_num = ppds_num2 ORDER BY ppds_key",
            () => E("ppds_key", all.Where(r => Eq(r.Num, r.Num2) == true).OrderBy(r => r.Key).Take(3).Select(r => R(r.Key)), ordered: true)));
        Add(Q("T08", "top", "SELECT TOP 2 ppds_key FROM {P} WHERE ppds_name IS NOT NULL ORDER BY LEN(ppds_name), ppds_key",
            () => E("ppds_key", OrderBy(all.Where(r => r.Name is not null), (r => Len(r.Name), false), (r => r.Key, false)).Take(2).Select(r => R(r.Key)), ordered: true)));
        Add(Q("T09", "offset", "SELECT ppds_key FROM {P} ORDER BY ppds_key OFFSET 5 ROWS FETCH NEXT 3 ROWS ONLY", () => E("ppds_key", all.OrderBy(r => r.Key).Skip(5).Take(3).Select(r => R(r.Key)), ordered: true)));
        Add(Q("T10", "offset", "SELECT ppds_key FROM {P} ORDER BY ppds_key OFFSET 30 ROWS", () => E("ppds_key", all.OrderBy(r => r.Key).Skip(30).Select(r => R(r.Key)), ordered: true)));
        Add(Q("T11", "offset", "SELECT ppds_key FROM {P} WHERE ppds_num = ppds_num2 ORDER BY ppds_key OFFSET 1 ROWS FETCH NEXT 2 ROWS ONLY",
            () => E("ppds_key", all.Where(r => Eq(r.Num, r.Num2) == true).OrderBy(r => r.Key).Skip(1).Take(2).Select(r => R(r.Key)), ordered: true)));
        Add(Q("T12", "offset", "SELECT ppds_key FROM {P} ORDER BY ppds_num DESC, ppds_key OFFSET 2 ROWS FETCH NEXT 3 ROWS ONLY",
            () => E("ppds_key", OrderBy(all, (r => r.Num, true), (r => r.Key, false)).Skip(2).Take(3).Select(r => R(r.Key)), ordered: true)));
        Add(Q("T13", "top", "SELECT TOP 3 ppds_key FROM {P} WHERE UPPER(ppds_name) LIKE 'A%' ORDER BY ppds_key",
            () => E("ppds_key", all.Where(r => Like((string?)Upper(r.Name), "A%") == true).OrderBy(r => r.Key).Take(3).Select(r => R(r.Key)), ordered: true)));
        Add(Q("D01", "distinct", "SELECT DISTINCT ppds_num FROM {P}", () => E("ppds_num", all.Select(r => (object?)r.Num).Distinct(new GroupComparer()).Select(v => R(v)))));
        Add(Q("D02", "distinct", "SELECT DISTINCT ppds_num, ppds_num2 FROM {P}", () => E("ppds_num|ppds_num2", all.Select(r => (r.Num, r.Num2)).Distinct().Select(t => R(t.Num, t.Num2)))));
        Add(Q("D03", "distinct", "SELECT DISTINCT ppds_colorcode FROM {P}", () => E("ppds_colorcode", all.Select(r => r.Color).Distinct().Select(v => R(v)))));
        Add(Q("D04", "distinct", "SELECT DISTINCT UPPER(ppds_itemtype) AS u FROM {P}", () => E("u", all.Select(r => Upper(r.ItemType)).Distinct(new GroupComparer()).Select(v => R(v)), collation: true)));
        Add(Q("D05", "distinct", "SELECT DISTINCT ppds_name FROM {P}", () => E("ppds_name", all.Select(r => (object?)r.Name).Distinct(new GroupComparer()).Select(v => R(v)), collation: true)));
        Add(Q("D06", "distinct", "SELECT DISTINCT ppds_itemtype FROM {P}", () => E("ppds_itemtype", all.Select(r => (object?)r.ItemType).Distinct(new GroupComparer()).Select(v => R(v)), collation: true)));
        Add(Q("D07", "distinct", "SELECT DISTINCT TOP 3 ppds_num FROM {P} ORDER BY ppds_num",
            () => E("ppds_num", all.Select(r => (object?)r.Num).Distinct(new GroupComparer()).OrderBy(v => v, Comparer<object?>.Create(OrderCompare)).Take(3).Select(v => R(v)), ordered: true)));
        Add(Q("D08", "distinct", "SELECT DISTINCT ppds_num % 2 AS m FROM {P}", () => E("m", all.Select(r => (object?)(r.Num % 2)).Distinct(new GroupComparer()).Select(v => R(v)))));
        Add(Q("O01", "order", "SELECT ppds_key, ppds_num FROM {P} ORDER BY ppds_num, ppds_key", () => E("ppds_key|ppds_num", OrderBy(all, (r => r.Num, false), (r => r.Key, false)).Select(r => R(r.Key, r.Num)), ordered: true)));
        Add(Q("O02", "order", "SELECT ppds_key, ppds_num FROM {P} ORDER BY ppds_num DESC, ppds_key DESC", () => E("ppds_key|ppds_num", OrderBy(all, (r => r.Num, true), (r => r.Key, true)).Select(r => R(r.Key, r.Num)), ordered: true)));
        Add(Q("O03", "order", "SELECT ppds_key, ppds_num AS n FROM {P} ORDER BY n, ppds_key", () => E("ppds_key|n", OrderBy(all, (r => r.Num, false), (r => r.Key, false)).Select(r => R(r.Key, r.Num)), ordered: true)));
        Add(Q("O04", "order", "SELECT ppds_key, ppds_num FROM {P} ORDER BY ppds_num * -1, ppds_key", () => E("ppds_key|ppds_num", OrderBy(all, (r => -r.Num, false), (r => r.Key, false)).Select(r => R(r.Key, r.Num)), ordered: true)));
        Add(Q("O05", "order", "SELECT ppds_key, ppds_num FROM {P} ORDER BY 2, 1", () => E("ppds_key|ppds_num", OrderBy(all, (r => r.Num, false), (r => r.Key, false)).Select(r => R(r.Key, r.Num)), ordered: true)));
        Add(Q("O06", "order", "SELECT ppds_key FROM {P} ORDER BY ppds_num2, ppds_key", () => E("ppds_key", OrderBy(all, (r => r.Num2, false), (r => r.Key, false)).Select(r => R(r.Key)), ordered: true)));
        Add(Q("O07", "order", "SELECT ppds_key, ppds_colorcode FROM {P} ORDER BY ppds_colorcode, ppds_key", () => E("ppds_key|ppds_colorcode", OrderBy(all, (r => r.Color, false), (r => r.Key, false)).Select(r => R(r.Key, r.Color)), ordered: true)));
        Add(Q("O08", "order", "SELECT ppds_key, ppds_dt FROM {P} ORDER BY ppds_dt DESC, ppds_key", () => E("ppds_key|ppds_dt", OrderBy(all, (r => r.Dt, true), (r => r.Key, false)).Select(r => R(r.Key, r.Dt)), ordered: true)));
        Add(Q("O09", "order", "SELECT ppds_key, ppds_flag FROM {P} ORDER BY ppds_flag, ppds_key", () => E("ppds_key|ppds_flag", OrderBy(all, (r => r.Flag, false), (r => r.Key, false)).Select(r => R(r.Key, r.Flag)), ordered: true)));
        int[] alphaKeys = [1, 2, 3, 5, 6, 14, 15, 16, 17, 21, 22, 23, 28, 29, 33, 34];
        Add(Q("O10", "order", "SELECT ppds_key, ppds_name FROM {P} WHERE ppds_key IN (1,2,3,5,6,14,15,16,17,21,22,23,28,29,33,34) ORDER BY ppds_name, ppds_key",
            () => E("ppds_key|ppds_name", OrderBy(all.Where(r => alphaKeys.Contains(r.Key)), (r => r.Name, false), (r => r.Key, false)).Select(r => R(r.Key, r.Name)), ordered: true)));
        Add(Q("O11", "order", "SELECT ppds_key, ppds_dec FROM {P} ORDER BY ppds_dec, ppds_key", () => E("ppds_key|ppds_dec", OrderBy(all, (r => r.Dec, false), (r => r.Key, false)).Select(r => R(r.Key, r.Dec)), ordered: true)));
        Add(Q("O12", "order", "SELECT ppds_key, ppds_name FROM {P} WHERE ppds_key IN (1,2,3,5,6,14,15,16,17,21,22,23,28,29,33,34) ORDER BY ppds_name DESC, ppds_key",
            () => E("ppds_key|ppds_name", OrderBy(all.Where(r => alphaKeys.Contains(r.Key)), (r => r.Name, true), (r => r.Key, false)).Select(r => R(r.Key, r.Name)), ordered: true)));
        Add(Q("O13", "order", "SELECT ppds_key, ppds_money FROM {P} ORDER BY ppds_money ASC, ppds_key ASC", () => E("ppds_key|ppds_money", OrderBy(all, (r => r.Money, false), (r => r.Key, false)).Select(r => R(r.Key, r.Money)), ordered: true)));
        Add(Q("O14", "order", "SELECT p.ppds_key FROM {P} p ORDER BY p.ppds_num2 DESC, p.ppds_key", () => E("ppds_key", OrderBy(all, (r => r.Num2, true), (r => r.Key, false)).Select(r => R(r.Key)), ordered: true)));

        // ═════════ Aggregates / GROUP BY / HAVING ═════════
        Add(Q("A01", "aggregate", "SELECT COUNT(*) AS c FROM {P}", () => E("c", [R(all.Count)])));
        Add(Q("A02", "aggregate", "SELECT COUNT(ppds_num) AS c, COUNT(DISTINCT ppds_num) AS d, SUM(ppds_num) AS s, AVG(ppds_num) AS a, MIN(ppds_num) AS mn, MAX(ppds_num) AS mx FROM {P}",
            () => E("c|d|s|a|mn|mx", [R(all.Count(r => r.Num is not null), CountDistinct(all.Select(r => (object?)r.Num)), SumInt(all.Select(r => r.Num)), AvgInt(all.Select(r => r.Num)), Min(all.Select(r => (object?)r.Num)), Max(all.Select(r => (object?)r.Num)))])));
        Add(Q("A03", "aggregate", "SELECT COUNT(ppds_dec) AS c, SUM(ppds_dec) AS s, AVG(ppds_dec) AS a, MIN(ppds_dec) AS mn, MAX(ppds_dec) AS mx FROM {P}",
            () => E("c|s|a|mn|mx", [R(all.Count(r => r.Dec is not null), SumDec(all.Select(r => r.Dec)), AvgDec(all.Select(r => r.Dec)), Min(all.Select(r => (object?)r.Dec)), Max(all.Select(r => (object?)r.Dec)))])));
        Add(Q("A04", "aggregate", "SELECT COUNT(ppds_money) AS c, SUM(ppds_money) AS s, AVG(ppds_money) AS a, MIN(ppds_money) AS mn, MAX(ppds_money) AS mx FROM {P}",
            () => E("c|s|a|mn|mx", [R(all.Count(r => r.Money is not null), SumDec(all.Select(r => r.Money)), AvgDec(all.Select(r => r.Money)), Min(all.Select(r => (object?)r.Money)), Max(all.Select(r => (object?)r.Money)))])));
        Add(Q("A05", "aggregate", "SELECT COUNT(ppds_flt) AS c, SUM(ppds_flt) AS s, AVG(ppds_flt) AS a, MIN(ppds_flt) AS mn, MAX(ppds_flt) AS mx FROM {P}",
            () => E("c|s|a|mn|mx", [R(all.Count(r => r.Flt is not null), SumDbl(all.Select(r => r.Flt)), AvgDbl(all.Select(r => r.Flt)), Min(all.Select(r => (object?)r.Flt)), Max(all.Select(r => (object?)r.Flt)))])));
        Add(Q("A06", "aggregate", "SELECT MIN(ppds_name) AS mn, MAX(ppds_name) AS mx FROM {P}", () => E("mn|mx", [R(Min(all.Select(r => (object?)r.Name)), Max(all.Select(r => (object?)r.Name)))], collation: true)));
        Add(Q("A07", "aggregate", "SELECT MIN(ppds_dt) AS mn, MAX(ppds_dt) AS mx FROM {P}", () => E("mn|mx", [R(Min(all.Select(r => (object?)r.Dt)), Max(all.Select(r => (object?)r.Dt)))])));
        Add(Q("A08", "aggregate", "SELECT ppds_colorcode, COUNT(*) AS c FROM {P} GROUP BY ppds_colorcode", () => E("ppds_colorcode|c", GroupBy(all, r => r.Color).Select(g => R(g.Key, g.Count())))));
        Add(Q("A09", "aggregate", "SELECT ppds_itemtype, SUM(ppds_num) AS s, COUNT(ppds_num) AS n FROM {P} GROUP BY ppds_itemtype",
            () => E("ppds_itemtype|s|n", GroupBy(all, r => r.ItemType).Select(g => R(g.Key, SumInt(g.Select(r => r.Num)), g.Count(r => r.Num is not null))), collation: true)));
        Add(Q("A10", "aggregate", "SELECT COUNT(*) AS c, SUM(ppds_num) AS s, MAX(ppds_num) AS m FROM {P} WHERE ppds_key < 0", () => E("c|s|m", [R(0, null, null)])));
        Add(Q("A11", "aggregate", "SELECT ppds_colorcode, COUNT(*) AS c FROM {P} WHERE ppds_key < 0 GROUP BY ppds_colorcode", () => E("ppds_colorcode|c", [])));
        Add(Q("A12", "aggregate", "SELECT ppds_colorcode, COUNT(*) AS c FROM {P} GROUP BY ppds_colorcode HAVING COUNT(*) > 8",
            () => E("ppds_colorcode|c", GroupBy(all, r => r.Color).Where(g => g.Count() > 8).Select(g => R(g.Key, g.Count())))));
        Add(Q("A13", "aggregate", "SELECT ppds_colorcode, SUM(ppds_num) AS s FROM {P} GROUP BY ppds_colorcode HAVING SUM(ppds_num) > 10",
            () => E("ppds_colorcode|s", GroupBy(all, r => r.Color).Where(g => Gt(SumInt(g.Select(r => r.Num)), 10) == true).Select(g => R(g.Key, SumInt(g.Select(r => r.Num)))))));
        Add(Q("A14", "aggregate", "SELECT ppds_colorcode, ppds_flag, COUNT(*) AS c FROM {P} GROUP BY ppds_colorcode, ppds_flag",
            () => E("ppds_colorcode|ppds_flag|c", all.GroupBy(r => (r.Color, r.Flag)).Select(g => R(g.Key.Color, g.Key.Flag, g.Count())))));
        Add(Q("A15", "aggregate", "SELECT YEAR(ppds_dt) AS y, COUNT(*) AS c FROM {P} GROUP BY YEAR(ppds_dt)", () => E("y|c", all.GroupBy(r => r.Dt?.Year).Select(g => R(g.Key, g.Count())))));
        Add(Q("A16", "aggregate", "SELECT COUNT(*) FROM {P}", () => E("", [R(all.Count)])));
        Add(Q("A17", "aggregate", "SELECT SUM(ppds_num * 2) AS s FROM {P}", () => E("s", [R(SumInt(all.Select(r => r.Num * 2)))])));
        Add(Q("A18", "aggregate", "SELECT ppds_flag, COUNT(*) AS c FROM {P} GROUP BY ppds_flag", () => E("ppds_flag|c", all.GroupBy(r => r.Flag).Select(g => R(g.Key, g.Count())))));
        Add(Q("A19", "aggregate", "SELECT ppds_colorcode, COUNT(*) AS c FROM {P} GROUP BY ppds_colorcode ORDER BY c DESC, ppds_colorcode",
            () => E("ppds_colorcode|c", OrderBy(GroupBy(all, r => r.Color).Select(g => (g.Key, Count: g.Count())), (t => t.Count, true), (t => t.Key, false)).Select(t => R(t.Key, t.Count)), ordered: true)));
        Add(Q("A20", "aggregate", "SELECT COUNT(*) AS c, SUM(ppds_n) AS s FROM {B}", () => E("c|s", [R(BulkCount, (long)BulkCount * (BulkCount + 1) / 2)])));
        Add(Q("A21", "aggregate", "SELECT COUNT(*) AS c FROM {P} WHERE ppds_num = ppds_num2", () => E("c", [R(all.Count(r => Eq(r.Num, r.Num2) == true))])));
        Add(Q("A22", "aggregate", "SELECT COUNT(DISTINCT ppds_name) AS c FROM {P}", () => E("c", [R(CountDistinct(all.Select(r => (object?)r.Name)))])));
        Add(Q("A23", "aggregate", "SELECT ppds_name, COUNT(*) AS c FROM {P} GROUP BY ppds_name", () => E("ppds_name|c", GroupBy(all, r => r.Name).Select(g => R(g.Key, g.Count())), collation: true)));
        Add(Q("A24", "aggregate", "SELECT COUNT(DISTINCT ppds_itemtype) AS c FROM {P}", () => E("c", [R(CountDistinct(all.Select(r => (object?)r.ItemType)))])));
        Add(Q("A25", "aggregate", "SELECT MAX(ppds_colorcode) AS m FROM {P}", () => E("m", [R(Max(all.Select(r => (object?)r.Color)))])));
        Add(Q("A26", "aggregate", "SELECT COUNT(ppds_dt) AS c, COUNT(ppds_memo) AS m FROM {P}", () => E("c|m", [R(all.Count(r => r.Dt is not null), all.Count(r => r.Memo is not null))])));
        Add(Q("A27", "aggregate", "-- ppds:HASH_GROUP\nSELECT ppds_colorcode, COUNT(*) AS c, SUM(ppds_num) AS s FROM {P} GROUP BY ppds_colorcode",
            () => E("ppds_colorcode|c|s", GroupBy(all, r => r.Color).Select(g => R(g.Key, g.Count(), SumInt(g.Select(r => r.Num)))))));
        Add(Q("A28", "aggregate", "SELECT COUNT(*) AS c FROM {P} WHERE ppds_name LIKE 'a%'", () => E("c", [R(all.Count(r => Like(r.Name, "a%") == true))])));
        Add(Q("A29", "aggregate", "SELECT ppds_name, COUNT(*) AS c FROM {P} GROUP BY ppds_itemtype", () => Expected.Fails("column not in GROUP BY")));
        Add(Q("A30", "aggregate", "SELECT COUNT(*) AS c FROM {P} WHERE UPPER(ppds_name) LIKE 'A%'", () => E("c", [R(all.Count(r => Like((string?)Upper(r.Name), "A%") == true))])));
        Add(Q("A31", "aggregate", "SELECT ppds_colorcode, AVG(ppds_num) AS a FROM {P} GROUP BY ppds_colorcode", () => E("ppds_colorcode|a", GroupBy(all, r => r.Color).Select(g => R(g.Key, AvgInt(g.Select(r => r.Num)))))));
        Add(Q("A32", "aggregate", "SELECT COUNT(*) AS c FROM {P} HAVING COUNT(*) > 1000", () => E("c", [])));

        // ═════════ Joins ═════════
        Add(Q("J01", "join", "SELECT c.ppds_key AS ck, p.ppds_key AS pk FROM {C} c INNER JOIN {P} p ON c.ppds_parentid = p.{PId}", () => E("ck|pk", InnerJoin().Select(x => R(x.C.Key, x.P.Key)))));
        Add(Q("J02", "join", "SELECT p.ppds_key AS pk, c.ppds_key AS ck FROM {P} p INNER JOIN {C} c ON c.ppds_parentid = p.{PId}", () => E("pk|ck", InnerJoin().Select(x => R(x.P.Key, x.C.Key)))));
        IEnumerable<object?[]> LeftFromParent(Func<CRow, bool> on) => all.SelectMany(p =>
        {
            var kids = CRows.Where(c => c.ParentId == p.Id && on(c)).ToList();
            return kids.Count == 0 ? new[] { R(p.Key, null) } : kids.Select(c => R(p.Key, (object?)c.Key)).ToArray();
        });
        Add(Q("J03", "join", "SELECT p.ppds_key AS pk, c.ppds_key AS ck FROM {P} p LEFT JOIN {C} c ON c.ppds_parentid = p.{PId}", () => E("pk|ck", LeftFromParent(_ => true))));
        Add(Q("J04", "join", "SELECT p.ppds_key AS pk FROM {P} p LEFT JOIN {C} c ON c.ppds_parentid = p.{PId} WHERE c.ppds_key IS NULL",
            () => E("pk", all.Where(p => !CRows.Any(c => c.ParentId == p.Id)).Select(p => R(p.Key)))));
        Add(Q("J05", "join", "SELECT c.ppds_key AS ck, p.ppds_key AS pk FROM {C} c INNER JOIN {P} p ON c.ppds_parentid = p.{PId} WHERE c.ppds_qty > 0",
            () => E("ck|pk", InnerJoin().Where(x => Gt(x.C.Qty, 0) == true).Select(x => R(x.C.Key, x.P.Key)))));
        Add(Q("J06", "join", "SELECT c.ppds_key AS ck, p.ppds_key AS pk FROM {C} c INNER JOIN {P} p ON c.ppds_parentid = p.{PId} WHERE c.ppds_name = 'Alpha'",
            () => E("ck|pk", InnerJoin().Where(x => Eq(x.C.Name, "Alpha") == true).Select(x => R(x.C.Key, x.P.Key)))));
        Add(Q("J07", "join", "SELECT p.ppds_key AS pk, c.ppds_key AS ck FROM {P} p INNER JOIN {C} c ON c.ppds_parentid = p.{PId} WHERE p.ppds_num > 5",
            () => E("pk|ck", InnerJoin().Where(x => Gt(x.P.Num, 5) == true).Select(x => R(x.P.Key, x.C.Key)))));
        Add(Q("J08", "join", "SELECT p.ppds_key AS pk, c.ppds_key AS ck FROM {P} p LEFT JOIN {C} c ON c.ppds_parentid = p.{PId} AND c.ppds_qty > 0",
            () => E("pk|ck", LeftFromParent(c => Gt(c.Qty, 0) == true))));
        Add(Q("J09", "join", "SELECT p.ppds_key AS pk, c.ppds_key AS ck FROM {P} p RIGHT JOIN {C} c ON c.ppds_parentid = p.{PId}",
            () => E("pk|ck", CRows.Select(c => R(ParentOf(c)?.Key, c.Key)))));
        Add(Q("J10", "join", "SELECT p.ppds_key AS pk, c.ppds_key AS ck FROM {P} p FULL OUTER JOIN {C} c ON c.ppds_parentid = p.{PId}",
            () => E("pk|ck", LeftFromParent(_ => true).Concat(CRows.Where(c => ParentOf(c) is null).Select(c => R(null, c.Key))))));
        Add(Q("J11", "join", "SELECT p.ppds_key AS pk, c.ppds_key AS ck FROM {P} p CROSS JOIN {C} c WHERE p.ppds_key <= 3 AND c.ppds_key <= 2",
            () => E("pk|ck", all.Where(p => p.Key <= 3).SelectMany(p => CRows.Where(c => c.Key <= 2).Select(c => R(p.Key, c.Key))))));
        Add(Q("J12", "join", "SELECT a.ppds_key AS ak, b.ppds_key AS bk FROM {P} a INNER JOIN {P} b ON a.ppds_num = b.ppds_num2 WHERE a.ppds_key <= 10",
            () => E("ak|bk", all.Where(x => x.Key <= 10).SelectMany(x => all.Where(y => Eq(x.Num, y.Num2) == true).Select(y => R(x.Key, y.Key))))));
        Add(Q("J13", "join", "SELECT p.ppds_name, c.ppds_name FROM {C} c INNER JOIN {P} p ON c.ppds_parentid = p.{PId} WHERE c.ppds_key <= 5",
            () => E("ppds_name|ppds_name", InnerJoin().Where(x => x.C.Key <= 5).Select(x => R(x.P.Name, x.C.Name)))));
        Add(Q("J14", "join", "SELECT c.ppds_key AS ck, p.ppds_key AS pk, u.fullname FROM {C} c INNER JOIN {P} p ON c.ppds_parentid = p.{PId} INNER JOIN systemuser u ON u.systemuserid = c.owninguser",
            () => E("ck|pk|fullname", InnerJoin().Select(x => R(x.C.Key, x.P.Key, x.C.OwningUser == MyUserId ? MyFullName : "?")))));
        Add(Q("J15", "join", "SELECT c.ppds_key AS ck, c.ppds_qty AS q FROM {C} c INNER JOIN {P} p ON c.ppds_parentid = p.{PId} ORDER BY c.ppds_qty, c.ppds_key",
            () => E("ck|q", OrderBy(InnerJoin(), (x => x.C.Qty, false), (x => x.C.Key, false)).Select(x => R(x.C.Key, x.C.Qty)), ordered: true)));
        Add(Q("J16", "join", "SELECT p.ppds_key AS pk, COUNT(c.ppds_key) AS n FROM {P} p LEFT JOIN {C} c ON c.ppds_parentid = p.{PId} GROUP BY p.ppds_key",
            () => E("pk|n", all.Select(p => R(p.Key, CRows.Count(c => c.ParentId == p.Id))))));
        Add(Q("J17", "join", "SELECT c.ppds_key AS ck, p.ppds_num + c.ppds_qty AS t FROM {C} c INNER JOIN {P} p ON c.ppds_parentid = p.{PId}",
            () => E("ck|t", InnerJoin().Select(x => R(x.C.Key, x.P.Num + x.C.Qty)))));
        Add(Q("J18", "join", "SELECT c.ppds_key AS ck, p.ppds_key AS pk FROM {C} c, {P} p WHERE c.ppds_parentid = p.{PId}", () => E("ck|pk", InnerJoin().Select(x => R(x.C.Key, x.P.Key)))));
        Add(Q("J19", "join", "SELECT c.ppds_key AS ck, p.ppds_colorcodename FROM {C} c INNER JOIN {P} p ON c.ppds_parentid = p.{PId}", () => E("ck|ppds_colorcodename", InnerJoin().Select(x => R(x.C.Key, x.P.ColorLabel)))));
        Add(Q("J20", "join", "SELECT c.ppds_key AS ck, p.ppds_key AS pk FROM {C} c INNER JOIN {P} p ON p.{PId} = c.ppds_parentid", () => E("ck|pk", InnerJoin().Select(x => R(x.C.Key, x.P.Key)))));
        Add(Q("J21", "join", "SELECT p.ppds_key AS pk, c.ppds_key AS ck FROM {P} p LEFT JOIN {C} c ON c.ppds_parentid = p.{PId} WHERE c.ppds_qty > 0",
            () => E("pk|ck", InnerJoin().Where(x => Gt(x.C.Qty, 0) == true).Select(x => R(x.P.Key, x.C.Key)))));
        Add(Q("J22", "join", "SELECT c.ppds_key, p.ppds_key FROM {C} c INNER JOIN {P} p ON c.ppds_parentid = p.{PId}", () => E("ppds_key|ppds_key", InnerJoin().Select(x => R(x.C.Key, x.P.Key)))));
        Add(Q("J23", "join", "SELECT c.ppds_key AS ck, p.ppds_name AS pn FROM {C} c INNER JOIN {P} p ON c.ppds_parentid = p.{PId} WHERE p.ppds_name LIKE 'a%'",
            () => E("ck|pn", InnerJoin().Where(x => Like(x.P.Name, "a%") == true).Select(x => R(x.C.Key, x.P.Name)))));
        Add(Q("J24", "join", "SELECT c.ppds_key AS ck FROM {C} c INNER JOIN {P} p ON c.ppds_parentid = p.{PId} WHERE c.ppds_num = p.ppds_num",
            () => E("ck", InnerJoin().Where(x => Eq(x.C.Num, x.P.Num) == true).Select(x => R(x.C.Key)))));

        // ═════════ Virtual *name columns and formatted values ═════════
        Add(Q("V01", "virtual", "SELECT ppds_key, ppds_colorcodename FROM {P}", () => E("ppds_key|ppds_colorcodename", all.Select(r => R(r.Key, r.ColorLabel)))));
        Add(Q("V02", "virtual", "SELECT ppds_key, ppds_colorcode, ppds_colorcodename FROM {P}", () => E("ppds_key|ppds_colorcode|ppds_colorcodename", all.Select(r => R(r.Key, r.Color, r.ColorLabel)))));
        Add(Q("V03", "virtual", "SELECT ppds_key, ppds_itemtypename FROM {P}", () => E("ppds_key|ppds_itemtypename", all.Select(r => R(r.Key, r.ItemTypeName)))));
        Add(Q("V04", "virtual", "SELECT ppds_key, ppds_flagname FROM {P}", () => E("ppds_key|ppds_flagname", all.Select(r => R(r.Key, r.Flag is null ? null : r.Flag.Value ? "Yes" : "No")))));
        Add(Q("V05", "virtual", "SELECT ppds_key, statecodename, statuscodename FROM {P}", () => E("ppds_key|statecodename|statuscodename", all.Select(r => R(r.Key, "Active", "Active")))));
        Add(Q("V06", "virtual", "SELECT ppds_key, owneridname FROM {P}", () => E("ppds_key|owneridname", all.Select(r => R(r.Key, r.OwnerName)))));
        Add(Q("V07", "virtual", "SELECT domainname FROM systemuser WHERE systemuserid = '{Me}'", () => E("domainname", [R(MyDomainName)])));
        Add(Q("V08", "virtual", "SELECT ppds_key FROM {P} WHERE ppds_colorcodename = 'Blue'", () => Keys(r => Eq(r.ColorLabel, "Blue"))));
        Add(Q("V09", "virtual", "SELECT ppds_key, ppds_colorcodename FROM {P} ORDER BY ppds_colorcodename, ppds_key",
            () => E("ppds_key|ppds_colorcodename", OrderBy(all, (r => r.ColorLabel, false), (r => r.Key, false)).Select(r => R(r.Key, r.ColorLabel)), ordered: true)));
        Add(Q("V10", "virtual", "SELECT ppds_key, ppds_colorcode FROM {P}", () => E("ppds_key|ppds_colorcode", all.Select(r => R(r.Key, r.Color)))));
        Add(Q("V11", "virtual", "SELECT ppds_key, ppds_parentidname FROM {C}", () => E("ppds_key|ppds_parentidname", CRows.Select(c => R(c.Key, ParentOf(c)?.Name)))));
        Add(Q("V12", "virtual", "SELECT ppds_key, ppds_parentid, ppds_parentidname FROM {C}", () => E("ppds_key|ppds_parentid|ppds_parentidname", CRows.Select(c => R(c.Key, c.ParentId, ParentOf(c)?.Name)))));
        Add(Q("V13", "virtual", "SELECT ppds_key, ppds_flag FROM {P}", () => E("ppds_key|ppds_flag", all.Select(r => R(r.Key, r.Flag)))));
        Add(Q("V14", "virtual", "SELECT ppds_key, ppds_multi FROM {P}", () => E("ppds_key|ppds_multi", all.Select(r => R(r.Key, r.Multi)))));
        Add(Q("V15", "virtual", "SELECT ppds_key, ppds_parentidname AS pn FROM {C}", () => E("ppds_key|pn", CRows.Select(c => R(c.Key, ParentOf(c)?.Name)))));
        Add(Q("V16", "virtual", "SELECT ppds_key, ppds_itemtype, ppds_itemtypename FROM {P}", () => E("ppds_key|ppds_itemtype|ppds_itemtypename", all.Select(r => R(r.Key, r.ItemType, r.ItemTypeName)))));

        // ═════════ Subqueries ═════════
        Add(Q("S01", "subquery", "SELECT ppds_key FROM {P} WHERE ppds_key IN (SELECT ppds_key FROM {C})", () => Keys(r => CRows.Any(c => c.Key == r.Key))));
        Add(Q("S02", "subquery", "SELECT ppds_key FROM {P} WHERE {PId} IN (SELECT ppds_parentid FROM {C})", () => Keys(r => CRows.Any(c => c.ParentId == r.Id))));
        Add(Q("S03", "subquery", "SELECT ppds_key FROM {P} WHERE {PId} NOT IN (SELECT ppds_parentid FROM {C})",
            () => Keys(r => Not(In(r.Id, CRows.Select(c => (object?)c.ParentId).ToArray()))), dml: "{PId} NOT IN (SELECT ppds_parentid FROM {C})"));
        Add(Q("S04", "subquery", "SELECT ppds_key FROM {P} WHERE {PId} NOT IN (SELECT ppds_parentid FROM {C} WHERE ppds_parentid IS NOT NULL)",
            () => Keys(r => !CRows.Any(c => c.ParentId == r.Id)), dml: "{PId} NOT IN (SELECT ppds_parentid FROM {C} WHERE ppds_parentid IS NOT NULL)"));
        Add(Q("S05", "subquery", "SELECT ppds_key FROM {P} WHERE ppds_num = 5 OR ppds_key IN (SELECT ppds_key FROM {C} WHERE ppds_qty > 10)",
            () => Keys(r => Or(Eq(r.Num, 5), CRows.Any(c => c.Key == r.Key && Gt(c.Qty, 10) == true)))) with { CrashesProcess = StackOverflow });
        Add(Q("S06", "subquery", "SELECT p.ppds_key FROM {P} p WHERE EXISTS (SELECT 1 FROM {C} c WHERE c.ppds_parentid = p.{PId})", () => Keys(r => CRows.Any(c => c.ParentId == r.Id))));
        Add(Q("S07", "subquery", "SELECT p.ppds_key FROM {P} p WHERE NOT EXISTS (SELECT 1 FROM {C} c WHERE c.ppds_parentid = p.{PId})", () => Keys(r => !CRows.Any(c => c.ParentId == r.Id))));
        Add(Q("S08", "subquery", "SELECT p.ppds_key FROM {P} p WHERE EXISTS (SELECT 1 FROM {C} c WHERE c.ppds_parentid = p.{PId} AND c.ppds_qty > 0)",
            () => Keys(r => CRows.Any(c => c.ParentId == r.Id && Gt(c.Qty, 0) == true))));
        Add(Q("S09", "subquery", "SELECT p.ppds_key, (SELECT COUNT(*) FROM {C} c WHERE c.ppds_parentid = p.{PId}) AS n FROM {P} p",
            () => E("ppds_key|n", all.Select(r => R(r.Key, CRows.Count(c => c.ParentId == r.Id))))));
        Add(Q("S10", "subquery", "SELECT ppds_key FROM {P} WHERE ppds_num = (SELECT MAX(ppds_num) FROM {P})", () => Keys(r => Eq(r.Num, Max(all.Select(x => (object?)x.Num))))));
        Add(Q("S11", "subquery", "SELECT d.k FROM (SELECT ppds_key AS k, ppds_num FROM {P} WHERE ppds_num > 5) d WHERE d.k < 20",
            () => E("k", all.Where(r => Gt(r.Num, 5) == true && r.Key < 20).Select(r => R(r.Key)))));
        Add(Q("S12", "subquery", "SELECT d.t, d.c FROM (SELECT ppds_colorcode AS t, COUNT(*) AS c FROM {P} GROUP BY ppds_colorcode) d",
            () => E("t|c", GroupBy(all, r => r.Color).Select(g => R(g.Key, g.Count())))));
        Add(Q("S13", "subquery", "SELECT ppds_key FROM {P} WHERE ppds_num IN (SELECT ppds_num FROM {C})", () => Keys(r => In(r.Num, CRows.Select(c => (object?)c.Num).ToArray()))));
        Add(Q("S14", "subquery", "SELECT ppds_key FROM {P} WHERE ppds_num NOT IN (SELECT ppds_num FROM {C})", () => Keys(r => Not(In(r.Num, CRows.Select(c => (object?)c.Num).ToArray())))));
        Add(Q("S15", "subquery", "SELECT ppds_key FROM {P} WHERE ppds_key NOT IN (SELECT ppds_key FROM {C} WHERE ppds_qty > 0)",
            () => Keys(r => !CRows.Any(c => c.Key == r.Key && Gt(c.Qty, 0) == true)), dml: "ppds_key NOT IN (SELECT ppds_key FROM {C} WHERE ppds_qty > 0)"));
        Add(Q("S16", "subquery", "SELECT ppds_key FROM {P} WHERE ppds_key < 3 OR ppds_key NOT IN (SELECT ppds_key FROM {C})",
            () => Keys(r => r.Key < 3 || !CRows.Any(c => c.Key == r.Key))) with { CrashesProcess = StackOverflow });
        Add(Q("S18", "subquery", "SELECT ppds_key FROM {P} WHERE (ppds_key IN (SELECT ppds_key FROM {C}))", () => Keys(r => CRows.Any(c => c.Key == r.Key))) with { CrashesProcess = StackOverflow });
        Add(Q("S19", "subquery", "SELECT ppds_key FROM {P} WHERE NOT (ppds_key IN (SELECT ppds_key FROM {C}))", () => Keys(r => !CRows.Any(c => c.Key == r.Key))) with { CrashesProcess = StackOverflow });
        Add(Q("S20", "subquery", "SELECT p.ppds_key FROM {P} p WHERE p.ppds_key = 1 OR EXISTS (SELECT 1 FROM {C} c WHERE c.ppds_parentid = p.{PId})",
            () => Keys(r => r.Key == 1 || CRows.Any(c => c.ParentId == r.Id))) with { CrashesProcess = StackOverflow });
        Add(Q("S21", "subquery", "SELECT p.ppds_key FROM {P} p WHERE (EXISTS (SELECT 1 FROM {C} c WHERE c.ppds_parentid = p.{PId}))",
            () => Keys(r => CRows.Any(c => c.ParentId == r.Id))) with { CrashesProcess = StackOverflow });
        Add(Q("S17", "subquery", "SELECT ppds_key, (SELECT MAX(ppds_qty) FROM {C}) AS mq FROM {P} WHERE ppds_key <= 2",
            () => E("ppds_key|mq", all.Where(r => r.Key <= 2).Select(r => R(r.Key, Max(CRows.Select(c => (object?)c.Qty)))))));

        // ═════════ CTEs ═════════
        Add(Q("C01", "cte", "WITH x AS (SELECT ppds_key, ppds_num FROM {P} WHERE ppds_num > 5) SELECT ppds_key FROM x", () => Keys(r => Gt(r.Num, 5))));
        Add(Q("C02", "cte", "WITH x(k) AS (SELECT ppds_key FROM {P}) SELECT k FROM x WHERE k < 5", () => E("k", all.Where(r => r.Key < 5).Select(r => R(r.Key)))));
        Add(Q("C03", "cte", "WITH n AS (SELECT 1 AS i UNION ALL SELECT i + 1 FROM n WHERE i < 5) SELECT i FROM n", () => E("i", Enumerable.Range(1, 5).Select(i => R(i)))));
        Add(Q("C04", "cte", "WITH {P} AS (SELECT * FROM {P} WHERE ppds_num > 5) SELECT ppds_key FROM {P}", () => Expected.Fails("a CTE that references itself must be recursive (UNION ALL)")));
        Add(Q("C05", "cte", "WITH x AS (SELECT ppds_key AS k FROM {P}) SELECT a.k FROM x a INNER JOIN x b ON a.k = b.k WHERE a.k < 3", () => E("k", all.Where(r => r.Key < 3).Select(r => R(r.Key)))));

        // ═════════ Set operations ═════════
        Add(Q("U01", "set", "SELECT ppds_num FROM {P} UNION SELECT ppds_qty FROM {C}",
            () => E("ppds_num", all.Select(r => (object?)r.Num).Concat(CRows.Select(c => (object?)c.Qty)).Distinct(new GroupComparer()).Select(v => R(v)))));
        Add(Q("U02", "set", "SELECT ppds_num FROM {P} UNION ALL SELECT ppds_qty FROM {C}", () => E("ppds_num", all.Select(r => R(r.Num)).Concat(CRows.Select(c => R(c.Qty))))));
        Add(Q("U03", "set", "SELECT ppds_num FROM {P} INTERSECT SELECT ppds_num FROM {C}",
            () => E("ppds_num", all.Select(r => (object?)r.Num).Distinct(new GroupComparer()).Where(v => CRows.Any(c => GroupEquals(c.Num, v))).Select(v => R(v)))));
        Add(Q("U04", "set", "SELECT ppds_num FROM {P} EXCEPT SELECT ppds_num FROM {C}",
            () => E("ppds_num", all.Select(r => (object?)r.Num).Distinct(new GroupComparer()).Where(v => !CRows.Any(c => GroupEquals(c.Num, v))).Select(v => R(v)))));
        Add(Q("U05", "set", "SELECT ppds_key FROM {P} WHERE ppds_key <= 2 UNION SELECT ppds_key FROM {P} WHERE ppds_key <= 2 UNION ALL SELECT ppds_key FROM {P} WHERE ppds_key = 1",
            () => E("ppds_key", [R(1), R(2), R(1)])));
        Add(Q("U06", "set", "SELECT ppds_key AS k FROM {P} WHERE ppds_key <= 3 UNION ALL SELECT ppds_key FROM {C} WHERE ppds_key <= 3 ORDER BY k DESC",
            () => E("k", [R(3), R(3), R(2), R(2), R(1), R(1)], ordered: true)));
        Add(Q("U07", "set", "SELECT ppds_name AS a FROM {P} WHERE ppds_key <= 2 UNION ALL SELECT ppds_name AS b FROM {C} WHERE ppds_key <= 2",
            () => E("a", all.Where(r => r.Key <= 2).Select(r => R(r.Name)).Concat(CRows.Where(c => c.Key <= 2).Select(c => R(c.Name))))));
        Add(Q("U08", "set", "SELECT ppds_name FROM {P} WHERE ppds_key IN (1, 2) UNION SELECT ppds_name FROM {P} WHERE ppds_key = 3",
            () => E("ppds_name", all.Where(r => r.Key <= 3).Select(r => (object?)r.Name).Distinct(new GroupComparer()).Select(v => R(v)), collation: true)));
        Add(Q("U09", "set", "SELECT ppds_name FROM {C} INTERSECT SELECT ppds_name FROM {P}",
            () => E("ppds_name", CRows.Select(c => (object?)c.Name).Distinct(new GroupComparer()).Where(v => all.Any(p => GroupEquals(p.Name, v))).Select(v => R(v)), collation: true)));
        Add(Q("U10", "set", "SELECT ppds_key, ppds_name FROM {P} WHERE ppds_key = 1 UNION ALL SELECT ppds_key, ppds_name FROM {P} WHERE ppds_key = 2",
            () => E("ppds_key|ppds_name", all.Where(r => r.Key <= 2).Select(r => R(r.Key, r.Name)))));

        // ═════════ Window functions ═════════
        int RankAsc(PRow r, Func<PRow, object?> k) => 1 + all.Count(o => OrderCompare(k(o), k(r)) < 0);
        int RankDesc(PRow r, Func<PRow, object?> k) => 1 + all.Count(o => OrderCompare(k(o), k(r)) > 0);
        int Dense(PRow r, Func<PRow, object?> k) => 1 + all.Select(k).Distinct(new GroupComparer()).Count(v => OrderCompare(v, k(r)) < 0);
        Add(Q("WF01", "window", "SELECT ppds_key, ROW_NUMBER() OVER (ORDER BY ppds_key) AS rn FROM {P}", () => E("ppds_key|rn", all.Select(r => R(r.Key, all.Count(o => o.Key <= r.Key))))));
        Add(Q("WF02", "window", "SELECT ppds_key, RANK() OVER (ORDER BY ppds_num) AS r, DENSE_RANK() OVER (ORDER BY ppds_num) AS d FROM {P}",
            () => E("ppds_key|r|d", all.Select(r => R(r.Key, RankAsc(r, x => x.Num), Dense(r, x => x.Num))))));
        Add(Q("WF03", "window", "SELECT ppds_key, ROW_NUMBER() OVER (PARTITION BY ppds_colorcode ORDER BY ppds_key) AS rn FROM {P}",
            () => E("ppds_key|rn", all.Select(r => R(r.Key, all.Count(o => GroupEquals(o.Color, r.Color) && o.Key <= r.Key))))));
        Add(Q("WF04", "window", "SELECT ppds_key, SUM(ppds_num) OVER (PARTITION BY ppds_colorcode) AS s FROM {P}",
            () => E("ppds_key|s", all.Select(r => R(r.Key, SumInt(all.Where(o => GroupEquals(o.Color, r.Color)).Select(o => o.Num)))))));
        Add(Q("WF05", "window", "SELECT ppds_key, COUNT(*) OVER () AS total FROM {P}", () => E("ppds_key|total", all.Select(r => R(r.Key, all.Count)))));
        Add(Q("WF06", "window", "SELECT ppds_key, SUM(ppds_num) OVER (ORDER BY ppds_key) AS rs FROM {P}",
            () => E("ppds_key|rs", all.Select(r => R(r.Key, SumInt(all.Where(o => o.Key <= r.Key).Select(o => o.Num)))))));
        Add(Q("WF07", "window", "SELECT ppds_key, ROW_NUMBER() OVER (PARTITION BY ppds_itemtype ORDER BY ppds_key) AS rn FROM {P}",
            () => E("ppds_key|rn", all.Select(r => R(r.Key, all.Count(o => GroupEquals(o.ItemType, r.ItemType) && o.Key <= r.Key))))));
        Add(Q("WF08", "window", "SELECT ppds_key, ROW_NUMBER() OVER (ORDER BY ppds_key) AS rn FROM {P} WHERE ppds_num > 5", () =>
        {
            var kept = all.Where(r => Gt(r.Num, 5) == true).ToList();
            return E("ppds_key|rn", kept.Select(r => R(r.Key, kept.Count(o => o.Key <= r.Key))));
        }));
        Add(Q("WF09", "window", "SELECT ppds_key, AVG(ppds_dec) OVER (PARTITION BY ppds_colorcode) AS a FROM {P}",
            () => E("ppds_key|a", all.Select(r => R(r.Key, AvgDec(all.Where(o => GroupEquals(o.Color, r.Color)).Select(o => o.Dec)))))));
        Add(Q("WF10", "window", "SELECT ppds_key, MIN(ppds_num) OVER (PARTITION BY ppds_colorcode) AS mn, MAX(ppds_num) OVER (PARTITION BY ppds_colorcode) AS mx FROM {P}",
            () => E("ppds_key|mn|mx", all.Select(r => R(r.Key, Min(all.Where(o => GroupEquals(o.Color, r.Color)).Select(o => (object?)o.Num)), Max(all.Where(o => GroupEquals(o.Color, r.Color)).Select(o => (object?)o.Num)))))));
        Add(Q("WF11", "window", "SELECT ppds_key, RANK() OVER (ORDER BY ppds_num DESC) AS r FROM {P}", () => E("ppds_key|r", all.Select(r => R(r.Key, RankDesc(r, x => x.Num))))));
        Add(Q("WF12", "window", "SELECT ppds_key, LAG(ppds_num) OVER (ORDER BY ppds_key) AS prev FROM {P}",
            () => E("ppds_key|prev", all.Select(r => R(r.Key, all.Where(o => o.Key < r.Key).OrderByDescending(o => o.Key).Select(o => o.Num).FirstOrDefault())))));
        Add(Q("WF13", "window", "SELECT ppds_key, ROW_NUMBER() OVER (ORDER BY ppds_name, ppds_key) AS rn FROM {P} WHERE ppds_key IN (1,2,3,5,6,14,15,16,17,21,22,23,28,29,33,34)", () =>
        {
            var ordered = OrderBy(all.Where(r => alphaKeys.Contains(r.Key)), (r => r.Name, false), (r => r.Key, false)).ToList();
            return E("ppds_key|rn", ordered.Select((r, i) => R(r.Key, i + 1)));
        }));
        Add(Q("WF14", "window", "SELECT ppds_key, COUNT(ppds_num) OVER (PARTITION BY ppds_flag) AS n FROM {P}",
            () => E("ppds_key|n", all.Select(r => R(r.Key, all.Count(o => GroupEquals(o.Flag, r.Flag) && o.Num is not null))))));

        // ═════════ Paging, --top, MAX_ROWS ═════════
        Add(new Case
        {
            Id = "PG01", Area = "paging", Sql = X("SELECT ppds_n FROM {B}") + "  (--page 1, 2, ... with --paging-cookie)",
            Expect = () => E("ppds_n", Enumerable.Range(1, BulkCount).Select(i => R(i))),
            Run = () => PageAllAsync(X("SELECT ppds_n FROM {B}"), useCookie: true)
        });
        Add(new Case
        {
            Id = "PG02", Area = "paging", Sql = X("SELECT ppds_n FROM {B} ORDER BY ppds_n") + "  (--page 1, 2, ... without cookie)",
            Expect = () => E("ppds_n", Enumerable.Range(1, BulkCount).Select(i => R(i)), ordered: true),
            Run = () => PageAllAsync(X("SELECT ppds_n FROM {B} ORDER BY ppds_n"), useCookie: false)
        });
        Add(Q("PG03", "paging", "SELECT ppds_n FROM {B}", () => E("ppds_n", Enumerable.Range(1, BulkCount).Select(i => R(i)))));
        Add(Q("PG04", "paging", "SELECT ppds_n FROM {B} ORDER BY ppds_n DESC", () => E("ppds_n", Enumerable.Range(1, BulkCount).Reverse().Select(i => R(i)), ordered: true)));
        Add(Q("PG05", "paging", "SELECT TOP 5010 ppds_n FROM {B} ORDER BY ppds_n", () => E("ppds_n", Enumerable.Range(1, Math.Min(5010, BulkCount)).Select(i => R(i)), ordered: true)));
        Add(Q("PG06", "paging", "SELECT ppds_n FROM {B} WHERE ppds_n % 2 = 0", () => E("ppds_n", Enumerable.Range(1, BulkCount).Where(i => i % 2 == 0).Select(i => R(i)))));
        Add(Q("PG07", "paging", "SELECT COUNT(*) AS c FROM {B} WHERE ppds_n > 100", () => E("c", [R(BulkCount - 100)])));
        Add(Q("PG08", "paging", "SELECT DISTINCT ppds_name FROM {B}", () => E("ppds_name", Enumerable.Range(1, BulkCount).Select(i => R($"b{i:D5}")))));
        Add(Q("PG09", "top-flag", "SELECT ppds_key FROM {P} ORDER BY ppds_key", () => E("ppds_key", all.OrderBy(r => r.Key).Take(10).Select(r => R(r.Key)), ordered: true), top: 10));
        Add(Q("PG10", "top-flag", "SELECT TOP 3 ppds_key FROM {P} ORDER BY ppds_key", () => E("ppds_key", all.OrderBy(r => r.Key).Take(3).Select(r => R(r.Key)), ordered: true), top: 10));
        Add(Q("PG11", "top-flag", "SELECT ppds_key FROM {P} WHERE ppds_num = ppds_num2 ORDER BY ppds_key",
            () => E("ppds_key", all.Where(r => Eq(r.Num, r.Num2) == true).OrderBy(r => r.Key).Take(5).Select(r => R(r.Key)), ordered: true), top: 5));
        Add(Q("PG12", "top-flag", "-- ppds:MAX_ROWS 4\nSELECT ppds_key FROM {P} ORDER BY ppds_key", () => E("ppds_key", all.OrderBy(r => r.Key).Take(4).Select(r => R(r.Key)), ordered: true)));
        Add(Q("PG13", "top-flag", "SELECT ppds_key FROM {P} ORDER BY ppds_num2 DESC, ppds_key", () => E("ppds_key", OrderBy(all, (r => r.Num2, true), (r => r.Key, false)).Take(6).Select(r => R(r.Key)), ordered: true), top: 6));
        Add(Q("PG14", "top-flag", "SELECT ppds_colorcode, COUNT(*) AS c FROM {P} GROUP BY ppds_colorcode ORDER BY ppds_colorcode",
            () => E("ppds_colorcode|c", OrderBy(GroupBy(all, r => r.Color).Select(g => (g.Key, Count: g.Count())), (t => t.Key, false)).Take(2).Select(t => R(t.Key, t.Count)), ordered: true), top: 2));

        // ═════════ Errors, hints, odds and ends ═════════
        Add(Q("E01", "error", "SELECT ppds_key FROM {P} WHERE ppds_nosuchcol = 1", () => Expected.Fails("invalid column")));
        Add(Q("E02", "error", "SELECT ppds_nosuch FROM {P}", () => Expected.Fails("invalid column")));
        Add(Q("E03", "error", "SELECT ppds_key, ppds_num / 0 AS x FROM {P}", () => Expected.Fails("divide by zero")));
        Add(Q("E04", "error", "SELECT ppds_key, CAST(ppds_name AS int) AS x FROM {P}", () => Expected.Fails("conversion failed")));
        Add(Q("E05", "error", "SELECT 1 AS x", () => E("x", [R(1)])));
        Add(Q("E06", "error", "SELECT ppds_key FROM {P} WHERE ppds_num = 'abc'", () => Expected.Fails("conversion failed")));
        Add(Q("E07", "error", "SELECT ppds_key FROM ppds_nosuchtable", () => Expected.Fails("invalid object name")));
        Add(Q("E08", "hint", "SELECT ppds_key FROM {P} WITH (NOLOCK) WHERE ppds_key = 1", () => Keys(r => r.Key == 1)));
        Add(Q("E09", "hint", "-- ppds:NOLOCK\nSELECT ppds_key FROM {P} WHERE ppds_key = 1", () => Keys(r => r.Key == 1)));
        Add(Q("E10", "hint", "SELECT ppds_key FROM {P} WHERE ppds_key = 1 OPTION (MAXDOP 1)", () => Keys(r => r.Key == 1)));
        Add(Q("E11", "error", "SELECT ppds_key FROM {P} WHERE CONTAINS(ppds_name, 'x')", () => Expected.Fails("full-text not available")));
        Add(Q("E12", "error", "SELECT ppds_key, ppds_name COLLATE Latin1_General_CS_AS AS n FROM {P} WHERE ppds_key <= 2", () => E("ppds_key|n", all.Where(r => r.Key <= 2).Select(r => R(r.Key, r.Name)))));
        Add(Q("E13", "error", "SELECT ppds_key FROM {P} WHERE ppds_key = 1 FOR JSON PATH", () => E("", [R("[{\"ppds_key\":1}]".Replace("ppds_", Prefix + "_"))])));
        Add(Q("E14", "error", "SELECT ppds_key FROM {P} GROUP BY ppds_key HAVING ppds_key < 3", () => Keys(r => r.Key < 3)));
        Add(Q("E15", "error", "SELECT TOP 2 ppds_key FROM {P} ORDER BY ppds_key DESC", () => E("ppds_key", all.OrderBy(r => -r.Key).Take(2).Select(r => R(r.Key)), ordered: true)));

        return cases;
    }

    private const string StackOverflow = "planning recurses PlanSelect -> PlanInSubquery/PlanExistsSubquery without bound; the stack overflow (exit 0xC00000FD) ends the process. Reproduced offline in a child process (planner only, no Dataverse call)";

    private static int? TryInt(string? s)
        => s is not null && int.TryParse(s.Trim(' '), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var v) ? v : null;

    /// <summary>Caller-controlled paging as the CLI does it (--page N, optionally --paging-cookie from the previous page).</summary>
    private async Task<ActualTable> PageAllAsync(string sql, bool useCookie)
    {
        var rows = new List<object?[]>();
        List<string>? columns = null;
        string? cookie = null;
        var pages = new List<string>();
        for (var page = 1; page <= 20; page++)
        {
            var result = await _service.ExecuteAsync(new SqlQueryRequest
            {
                Sql = sql, PageNumber = page, PagingCookie = useCookie ? cookie : null, DmlSafety = new DmlSafetyOptions()
            });
            var table = ActualTable.From(result.Result);
            columns ??= table.Columns;
            rows.AddRange(table.Rows);
            pages.Add($"page {page}: {table.Rows.Count} rows, more={result.Result.MoreRecords}");
            if (!result.Result.MoreRecords) break;
            cookie = result.Result.PagingCookie;
        }
        _output.WriteLine($"    paging {(useCookie ? "with" : "without")} cookie: {string.Join("; ", pages)}");
        return new ActualTable { Columns = columns ?? [], Rows = rows };
    }
}
