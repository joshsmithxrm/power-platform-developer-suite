using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using PPDS.Cli.Services.Query;
using PPDS.Cli.Tests.Services.Shared;
using PPDS.Dataverse.Query;

namespace PPDS.Cli.Tests.Services.Query.Faithful;

/// <summary>One row of the audit's per-case table, as recorded (see AuditRecords/select-fidelity-audit-2026-11.txt).</summary>
internal sealed record AuditCase(string Id, string Area, string Sql, string SqlSuffix, string Expected, string Actual, string Verdict, string Detail)
{
    public override string ToString() => Id;
}

internal enum ReplayClass
{
    /// <summary>Offline equals what the audit recorded live.</summary>
    Matches,
    /// <summary>Offline differs from what the audit recorded live.</summary>
    Differs,
    /// <summary>The case cannot be replayed; <see cref="ReplayResult.Reason"/> says why.</summary>
    CannotReplay
}

/// <summary>
/// The outcome of one replayed case, and how much of it the record let the replay compare: <see cref="RowsCompared"/> of
/// <see cref="RowsTotal"/> rows, whether the order of the shown rows was compared, and how many shown ids were compared
/// (live ids the dataset holds) or masked (ids the dataset generated).
/// </summary>
internal sealed record ReplayResult(ReplayClass Class, string Reason, string Offline)
{
    public int RowsCompared { get; init; }
    public int RowsTotal { get; init; }
    public bool OrderCompared { get; init; }
    public int IdsCompared { get; init; }
    public int IdsMasked { get; init; }
    public int MessageSegments { get; init; }

    /// <summary>Every row the case returned was compared (or it returned none, or it is an error).</summary>
    public bool Full => RowsCompared == RowsTotal;

    public string Coverage => RowsTotal == 0 && MessageSegments > 0
        ? $"error: {MessageSegments} recorded message segment(s) compared in order"
        : $"{(Full ? "full" : "partial")}: {RowsCompared} of {RowsTotal} rows compared; order {(OrderCompared ? "compared" : "not compared (no top-level ORDER BY or DISTINCT, or one row shown)")}; ids {IdsCompared} compared, {IdsMasked} masked (generated offline)";
}

/// <summary>
/// Replays the audit's recorded cases: SQL → the real <see cref="SqlQueryService"/> → <see cref="FakeDataverse"/> holding
/// <see cref="AuditDataset"/>, rendered exactly as the audit rendered the engine's result (<c>RunCaseAsync</c>,
/// <c>ResultComparer.Describe</c>/<c>Canon</c> in the live suite), and compared with the recorded outcome.
/// <para>
/// What the record allows to be compared, and nothing looser:
/// <list type="bullet">
/// <item>An error: the exception type, and every segment of the recorded message between the points the report cut or
/// redacted (<c>…</c>, <c>[REDACTED]</c>, <c>&lt;user id&gt;</c>-style placeholders), in order; the first segment is a prefix
/// unless the record begins with a cut. Text from where live-organisation detail begins (<c>orgIndex</c>, the metadata
/// cache's version) is not compared.</item>
/// <item>Rows: the column names exactly, the row count exactly, and every row the report shows. With a top-level ORDER BY,
/// or a <c>SELECT DISTINCT</c> without one (Dataverse sorts it by the returned columns, R14), the shown rows are the first
/// rows in order; without one Dataverse's order is its own (it follows the live run's random
/// record ids), so each shown row must occur in the offline result, and when the report shows all rows the two must be the
/// same multiset. A row the report cut short is not compared. An id the report shows is compared when the dataset holds it
/// (<see cref="AuditDataset.RecordedIds"/>); an id the dataset generated is masked on both sides. Each result says how
/// much was compared (<see cref="ReplayResult.Coverage"/>).</item>
/// </list>
/// </para>
/// </summary>
internal static class AuditCaseReplay
{
    private static readonly Lazy<(IReadOnlyList<AuditCase> Cases, IReadOnlyDictionary<string, string> FetchXml)> Records = new(Load);

    public static IReadOnlyList<AuditCase> Cases => Records.Value.Cases;

    /// <summary>The FetchXML the audit recorded for each non-correct case (appendix).</summary>
    public static IReadOnlyDictionary<string, string> RecordedFetchXml => Records.Value.FetchXml;

    private static (IReadOnlyList<AuditCase>, IReadOnlyDictionary<string, string>) Load()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("AuditRecords.select-fidelity-audit-2026-11.txt")
            ?? throw new InvalidOperationException("audit records resource missing");
        using var reader = new StreamReader(stream);
        var cases = new List<AuditCase>();
        var fetch = new Dictionary<string, string>(StringComparer.Ordinal);
        while (reader.ReadLine() is { } line)
        {
            if (line.StartsWith("| ", StringComparison.Ordinal))
            {
                var cells = line[2..^2].Split(" | ");
                if (cells.Length != 7)
                    throw new InvalidDataException($"audit row with {cells.Length} cells: {line[..40]}");
                var m = Regex.Match(cells[2], @"^`(?<sql>.*)`(?<suffix>.*)$", RegexOptions.Singleline);
                cases.Add(new AuditCase(cells[0], cells[1], m.Groups["sql"].Value, m.Groups["suffix"].Value.Trim(), cells[3], cells[4], cells[5].Trim('*'), cells[6]));
            }
            else if (Regex.Match(line, @"^- (?<id>[A-Z]+\d+): `(?<fx>.*)`$") is { Success: true } f)
            {
                fetch[f.Groups["id"].Value] = f.Groups["fx"].Value;
            }
        }
        return (cases, fetch);
    }

    // ───────────── names ─────────────

    private static readonly (string Report, string Offline)[] Names =
    [
        ("Pid", AuditDataset.P + "id"), ("Cid", AuditDataset.C + "id"), ("Bid", AuditDataset.B + "id"),
        ("P", AuditDataset.P), ("C", AuditDataset.C), ("B", AuditDataset.B)
    ];

    /// <summary>The report's P, C, B, Pid, Cid, Bid as table and column names, outside string literals.</summary>
    public static string ToOffline(string sql)
    {
        // The report prints a leading hint and its statement on one line; the statement followed a line break.
        sql = Regex.Replace(sql, @"^(-- ppds:\w+(?: \S+)?) (?=SELECT|WITH|DECLARE)", "$1\n");
        var parts = Regex.Split(sql, "('(?:[^']|'')*')");
        for (var i = 0; i < parts.Length; i += 2)
        {
            foreach (var (report, offline) in Names)
                parts[i] = Regex.Replace(parts[i], $@"(?<![\w@#]){report}(?!\w)", offline);
        }
        return string.Concat(parts);
    }

    /// <summary>Offline names back to the report's P, C, B, Pid, Cid, Bid.</summary>
    public static string ToReport(string text)
    {
        foreach (var (report, offline) in Names)
            text = text.Replace(offline, report, StringComparison.Ordinal);
        return text;
    }

    // ───────────── run and render as the audit did ─────────────

    public sealed record Run(string Text, string? FetchXml, Exception? Error, IReadOnlyList<string> Columns, IReadOnlyList<string> Rows);

    public static async Task<Run> ExecuteAsync(AuditCase c, IReadOnlySet<string>? dropColumns = null)
    {
        int? top = Regex.Match(c.SqlSuffix, @"^\(top (\d+)\)$") is { Success: true } t ? int.Parse(t.Groups[1].Value, CultureInfo.InvariantCulture) : null;
        var service = new SqlQueryService(AuditDataset.Dataverse.CreateQueryExecutor(), new InactiveFakeShakedownGuard(), poolCapacity: 4);
        // PG01, PG02: the audit's PageAllAsync asks for page 1, 2, ... (with or without the cookie) until MoreRecords is false.
        var paging = Regex.Match(c.Sql, @"^(?<sql>.*) \(--page 1, 2, \.\.\. (?<how>with|without) (--paging-)?cookie\)$");
        try
        {
            var sql = ToOffline(paging.Success ? paging.Groups["sql"].Value : c.Sql);
            var results = new List<QueryResult>();
            string? cookie = null, transpiled = null;
            for (var page = 1; page <= (paging.Success ? 20 : 1); page++)
            {
                var r = await service.ExecuteAsync(new SqlQueryRequest
                {
                    Sql = sql, TopOverride = top, DmlSafety = new DmlSafetyOptions(),
                    PageNumber = paging.Success ? page : null, PagingCookie = paging.Success && paging.Groups["how"].Value == "with" ? cookie : null
                });
                results.Add(r.Result);
                transpiled ??= r.TranspiledFetchXml;
                if (!paging.Success || !r.Result.MoreRecords)
                    break;
                cookie = r.Result.PagingCookie;
            }
            var first = results[0];
            var kept = first.Columns.Where(col => dropColumns is null || !dropColumns.Contains(ToReport(col.Alias ?? col.LogicalName))).ToList();
            var columns = kept.Select(col => ToReport(col.Alias ?? col.LogicalName)).ToList();
            var keys = kept.Select(col => col.Alias ?? col.LogicalName).ToList();
            var rows = results.SelectMany(res => res.Records)
                .Select(r => "(" + string.Join(",", keys.Select(k => Canon(r.TryGetValue(k, out var qv) ? qv?.Value : null))) + ")")
                .ToList();
            var text = $"[{string.Join(", ", columns)}] {rows.Count} row{(rows.Count == 1 ? "" : "s")}" + (rows.Count == 0 ? "" : ": " + Preview(rows));
            return new Run(text, transpiled, null, columns, rows);
        }
        catch (Exception ex)
        {
            return new Run(ToReport($"{ex.GetType().Name}: {FirstLine(Innermost(ex))}"), null, ex, [], []);
        }
    }

    private static string Preview(IReadOnlyList<string> rows, int max = 6)
        => string.Join(" ", rows.Take(max)) + (rows.Count > max ? $" …(+{rows.Count - max})" : "");

    private static string FirstLine(string s) => s.Split('\n')[0].Trim();

    private static string Innermost(Exception ex)
    {
        var inner = ex;
        while (inner.InnerException is not null) inner = inner.InnerException;
        return ReferenceEquals(inner, ex) || inner.Message == ex.Message ? ex.Message : $"{ex.Message} <- {inner.GetType().Name}: {inner.Message}";
    }

    /// <summary>The live suite's <c>ResultComparer.Canon</c> (non-collation form).</summary>
    public static string Canon(object? v)
    {
        switch (v)
        {
            case null: return "NULL";
            case string s: return "'" + s + "'";
            case bool b: return b ? "1" : "0";
            case DateTime dt: return (dt.Kind == DateTimeKind.Local ? dt.ToUniversalTime() : dt).ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
            case Guid g: return "{" + g.ToString("D") + "}";
            case int[] arr: return "[" + string.Join("|", arr.OrderBy(x => x)) + "]";
            case DateTimeOffset dto: return dto.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
            case byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal:
                decimal d;
                try { d = Convert.ToDecimal(v, CultureInfo.InvariantCulture); }
                catch (OverflowException) { return Convert.ToString(v, CultureInfo.InvariantCulture)!; }
                return Math.Round(d, 4, MidpointRounding.AwayFromZero).ToString("0.####", CultureInfo.InvariantCulture);
        }
        return "<" + v.GetType().Name + ":" + Convert.ToString(v, CultureInfo.InvariantCulture) + ">";
    }

    // ───────────── classify ─────────────

    private static readonly Regex GuidText = new(@"\{[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\}");

    /// <summary>Masks the ids the dataset generated; live ids (taken from the report) stay and are compared.</summary>
    private static string Mask(string s, ref int compared, ref int masked)
    {
        int c = 0, m = 0;
        var result = GuidText.Replace(s, g =>
        {
            if (AuditDataset.RecordedIds.Contains(Guid.Parse(g.Value))) { c++; return g.Value.ToLowerInvariant(); }
            m++;
            return "{id}";
        });
        compared += c;
        masked += m;
        return result;
    }

    private static string Mask(string s) { int c = 0, m = 0; return Mask(s, ref c, ref m); }

    private static readonly Regex Redaction = new(@"…|\[REDACTED\]|<[a-z]+(?: [a-z]+)*>");

    /// <summary>
    /// The recorded message's segments between cuts and redactions, without live-organisation detail; whether the record
    /// begins with a cut, and whether it ends with one (a cut or a redaction). Live-organisation detail (<c>orgIndex</c>, the
    /// metadata cache's version) is not a cut: the record is complete up to it, so the offline message must end where it begins.
    /// </summary>
    public static (IReadOnlyList<string> Segments, bool StartsCut, bool EndsCut) MessageSegments(string message)
    {
        var end = new[] { message.IndexOf("orgIndex", StringComparison.Ordinal), message.IndexOf("LazyDynamicMetadataCache", StringComparison.Ordinal) }
            .Where(i => i >= 0).DefaultIfEmpty(message.Length).Min();
        message = message[..end].TrimEnd();
        var startsCut = Redaction.Match(message) is { Success: true, Index: 0 };
        var endsCut = Redaction.Matches(message).Any(m => m.Index + m.Length == message.Length);
        return (Redaction.Split(message).Select(s => s.Trim()).Where(s => s.Length > 0).ToList(), startsCut, endsCut);
    }

    /// <summary>
    /// Null when the offline message holds every recorded segment in order, starting where the record starts and, where the
    /// record has no cut at its end, ending where the record ends; otherwise what is missing or extra.
    /// </summary>
    public static string? MissingSegment(string recorded, string offline)
    {
        var (segments, startsCut, endsCut) = MessageSegments(recorded);
        var at = 0;
        for (var i = 0; i < segments.Count; i++)
        {
            var found = offline.IndexOf(segments[i], at, StringComparison.Ordinal);
            if (found < 0 || (i == 0 && !startsCut && found != 0))
                return $"the recorded segment \"{segments[i]}\"";
            at = found + segments[i].Length;
        }
        if (!endsCut && offline[at..].Trim().Length > 0)
            return $"the record's end (offline continues: \"{offline[at..].Trim()}\")";
        return null;
    }

    public static async Task<ReplayResult> ReplayAsync(AuditCase c)
    {
        if (c.Actual.StartsWith("not run in-process", StringComparison.Ordinal))
            return new(ReplayClass.CannotReplay, "crash: the audit did not run it in-process (stack overflow ends the process)", "");
        if (Regex.IsMatch(c.Sql, "<[a-z][^>]*>"))
            return new(ReplayClass.CannotReplay, "redacted: the report replaced a value in the SQL", "");
        if (Regex.IsMatch(c.Actual, "<[a-z][^>]*>") && c.Actual.StartsWith('['))
            return new(ReplayClass.CannotReplay, "redacted: the report replaced values in the recorded rows", "");
        if (c.SqlSuffix.Length > 0 && !Regex.IsMatch(c.SqlSuffix, @"^\(top \d+\)$"))
            return new(ReplayClass.CannotReplay, $"runner option '{c.SqlSuffix}' not ported", "");

        var run = await ExecuteAsync(c);
        if (FakeRefusal(run.Error) is { } refusal)
            return new(ReplayClass.CannotReplay, "fake refuses: " + refusal, run.Text);
        return Compare(c, c.Actual, run);
    }

    /// <summary>A recorded row result: its columns, its row count, the rows the report shows in full, and whether it shows all.</summary>
    public sealed record RecordedRows(IReadOnlyList<string> Columns, int Count, IReadOnlyList<string> Shown, bool Complete);

    /// <summary>Null when the text is not a row result, or the report cut its column list.</summary>
    public static RecordedRows? ParseRows(string recorded)
    {
        var m = Regex.Match(recorded, @"^\[(?<cols>[^\]]*)\] (?<n>\d+) rows?(: (?<rows>.*))?$", RegexOptions.Singleline);
        if (!m.Success)
            return null;
        var columns = m.Groups["cols"].Value.Length == 0 ? [] : m.Groups["cols"].Value.Split(", ");
        var shownText = m.Groups["rows"].Value;
        var complete = !shownText.EndsWith('…') && !shownText.Contains("…(+", StringComparison.Ordinal);
        shownText = Regex.Replace(shownText, @" …\(\+\d+\)$", "");
        var shown = shownText.Length == 0 ? new List<string>() : Regex.Split(shownText, @"(?<=\)) (?=\()").ToList();
        if (shown.Count > 0 && (!shown[^1].EndsWith(')') || shownText.EndsWith('…')))
            shown.RemoveAt(shown.Count - 1); // cut short by the report
        return new(columns, int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture), shown, complete);
    }

    /// <summary>Whether the replay compares the order of the shown rows: a top-level ORDER BY, or a DISTINCT without one (R14).</summary>
    public static bool OrderIsCompared(AuditCase c) => HasTopLevelOrderBy(c.Sql) || IsDistinctWithoutOrderBy(c.Sql);

    /// <summary>
    /// How much of a case the record lets the replay compare, from the record alone (so it does not change when a defect is
    /// fixed): rows compared of the total, whether order is compared, ids compared and masked, or the error's segments.
    /// </summary>
    public static ReplayResult RecordCoverage(AuditCase c)
    {
        if (!c.Actual.StartsWith('['))
        {
            var colon = c.Actual.IndexOf(": ", StringComparison.Ordinal);
            return new(ReplayClass.Matches, "", "") { MessageSegments = MessageSegments(colon < 0 ? "" : c.Actual[(colon + 2)..]).Segments.Count };
        }
        var rows = ParseRows(c.Actual);
        if (rows is null)
            return new(ReplayClass.CannotReplay, "the report cut the recorded column list", "");
        int compared = 0, masked = 0;
        foreach (var row in rows.Shown)
            Mask(row, ref compared, ref masked);
        return new(ReplayClass.Matches, "", "")
        {
            RowsCompared = rows.Complete ? rows.Count : rows.Shown.Count, RowsTotal = rows.Count,
            OrderCompared = OrderIsCompared(c) && rows.Shown.Count > 1, IdsCompared = compared, IdsMasked = masked
        };
    }

    /// <summary>
    /// For a WRONG RESULT case, whether a fix of its defect would show in what the record holds, decided from the record
    /// alone. A fixed engine returns what the audit expected, so the fix is visible when the expected result differs from
    /// the recorded one in something the replay compares: an error instead of rows, the row count, the columns (once the
    /// columns the audit's verdict ignored are dropped), the order of the shown rows where order is compared, or a shown row
    /// that the audit's Detail lists as unexpected (or that the complete expected result lacks).
    /// </summary>
    public static (bool Visible, string Why) FixVisibleInRecord(AuditCase c)
    {
        if (c.Actual.StartsWith('[') != c.Expected.StartsWith('['))
            return (true, "an error on one side, rows on the other");
        if (!c.Actual.StartsWith('['))
            return (c.Actual != c.Expected, c.Actual != c.Expected ? "a different error" : "the same error text");
        var actual = ParseRows(c.Actual);
        var expected = ParseRows(c.Expected);
        if (actual is null || expected is null)
            return (true, "a column list the report cut");
        if (actual.Count != expected.Count)
            return (true, $"the row count ({actual.Count} recorded, {expected.Count} expected)");
        var ignored = IgnoredColumns(c.Detail);
        var kept = actual.Columns.Select((name, i) => (name, i)).Where(x => !ignored.Contains(x.name)).ToList();
        if (!kept.Select(x => x.name).SequenceEqual(expected.Columns))
            return (true, "the columns");
        var projected = new List<string>();
        foreach (var row in actual.Shown)
        {
            var values = SplitRow(row);
            if (values is null || values.Count != actual.Columns.Count)
                return (true, $"undecided: the row {row} does not split into its columns, so it is counted as visible");
            projected.Add(Mask("(" + string.Join(",", kept.Select(x => values[x.i])) + ")"));
        }
        var expectedShown = expected.Shown.Select(Mask).ToList();
        if (OrderIsCompared(c))
        {
            var n = Math.Min(projected.Count, expectedShown.Count);
            for (var i = 0; i < n; i++)
            {
                if (projected[i] != expectedShown[i])
                    return (true, $"the shown row {i + 1} ({actual.Shown[i]} recorded, {expected.Shown[i]} expected)");
            }
        }
        var unexpected = DetailRows(c.Detail, "unexpected").Select(Mask).ToList();
        foreach (var row in projected)
        {
            if (unexpected.Remove(row))
                return (true, $"the shown row {row}, which the audit lists as unexpected");
            if (expected.Complete && !expectedShown.Contains(row))
                return (true, $"the shown row {row}, which the complete expected result lacks");
        }
        return (false, "every row the record shows, the count and the columns are the same in the expected result");
    }

    /// <summary>The columns the audit's verdict ignored, named in its Detail.</summary>
    public static HashSet<string> IgnoredColumns(string detail)
    {
        var ignored = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(detail, @"(?:auto-added \*name columns|unrequested primary-key column) ignored: (?<names>[^;|]+)"))
            foreach (var name in m.Groups["names"].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                ignored.Add(name);
        return ignored;
    }

    private static List<string> DetailRows(string detail, string label)
    {
        var m = Regex.Match(detail, label + @" \d+: (?<rows>.*?)(?:;|$)");
        return m.Success ? Regex.Matches(m.Groups["rows"].Value, @"\((?:[^()']|'[^']*')*\)").Select(x => x.Value).ToList() : [];
    }

    /// <summary>A rendered row's values, or null when its text does not split unambiguously.</summary>
    private static List<string>? SplitRow(string row)
    {
        if (!row.StartsWith('(') || !row.EndsWith(')'))
            return null;
        var body = row[1..^1];
        var values = new List<string>();
        var i = 0;
        while (i <= body.Length)
        {
            if (i < body.Length && body[i] == '\'')
            {
                // A string runs to the next quote that is followed by a comma or the end.
                var close = i + 1;
                while (close < body.Length && !(body[close] == '\'' && (close + 1 == body.Length || body[close + 1] == ',')))
                    close++;
                if (close >= body.Length)
                    return null;
                values.Add(body[i..(close + 1)]);
                i = close + 2;
            }
            else
            {
                var comma = body.IndexOf(',', i);
                var end = comma < 0 ? body.Length : comma;
                values.Add(body[i..end]);
                i = end + 1;
            }
        }
        return values;
    }

    private static ReplayResult Compare(AuditCase c, string recorded, Run run)
    {
        if (!recorded.StartsWith('['))
        {
            // An error. Compare the type and the message up to where the report cut or redacted it.
            var colon = recorded.IndexOf(": ", StringComparison.Ordinal);
            var type = colon < 0 ? recorded : recorded[..colon];
            var message = colon < 0 ? "" : recorded[(colon + 2)..];
            var segments = MessageSegments(message).Segments.Count;
            if (run.Error is null)
                return new(ReplayClass.Differs, $"live raised {type}; offline returned rows", run.Text);
            var offlineType = run.Text[..run.Text.IndexOf(": ", StringComparison.Ordinal)];
            var offlineMessage = run.Text[(offlineType.Length + 2)..];
            if (offlineType != type)
                return new(ReplayClass.Differs, $"live raised {type}; offline raised {offlineType}", run.Text);
            if (MissingSegment(message, offlineMessage) is { } missing)
                return new(ReplayClass.Differs, $"same exception type; the offline message does not match {missing}", run.Text) { MessageSegments = segments };
            return new(ReplayClass.Matches, "", run.Text) { MessageSegments = segments };
        }

        if (run.Error is not null)
            return new(ReplayClass.Differs, "live returned rows; offline raised an error", run.Text);

        var parsed = ParseRows(recorded);
        if (parsed is null)
            return new(ReplayClass.CannotReplay, "the report cut the recorded column list", run.Text);
        var (columns, count, _, complete) = parsed;
        if (!columns.SequenceEqual(run.Columns))
            return new(ReplayClass.Differs, $"columns: live [{string.Join(", ", columns)}], offline [{string.Join(", ", run.Columns)}]", run.Text);
        if (count != run.Rows.Count)
            return new(ReplayClass.Differs, $"rows: live {count}, offline {run.Rows.Count}", run.Text);

        var shown = parsed.Shown.Select(Mask).ToList();
        var offline = run.Rows.Select(Mask).ToList();
        // R14 (D01-D06, PG08): a DISTINCT without ORDER BY comes back sorted by the returned columns, so its order is compared.
        var ordered = OrderIsCompared(c);
        var coverage = (ReferenceEquals(recorded, c.Actual) ? RecordCoverage(c) : new ReplayResult(ReplayClass.Matches, "", "")) with { Offline = run.Text };

        if (ordered)
        {
            if (!shown.SequenceEqual(offline.Take(shown.Count)))
                return coverage with { Class = ReplayClass.Differs, Reason = "ordered rows differ from the first rows shown" };
        }
        else
        {
            var pool = offline.GroupBy(r => r).ToDictionary(g => g.Key, g => g.Count());
            foreach (var row in shown)
            {
                if (!pool.TryGetValue(row, out var left) || left == 0)
                    return coverage with { Class = ReplayClass.Differs, Reason = $"live row {row} not in the offline result" };
                pool[row] = left - 1;
            }
            if (complete && pool.Values.Any(v => v > 0))
                return coverage with { Class = ReplayClass.Differs, Reason = "offline has rows live did not return" };
        }
        return coverage;
    }

    private static string? FakeRefusal(Exception? error)
    {
        for (var e = error; e is not null; e = e.InnerException)
        {
            if (e is NotSupportedException && (e.Message.StartsWith("Fake Dataverse", StringComparison.Ordinal) || e.Message.StartsWith("Fake table", StringComparison.Ordinal)))
                return ToReport(e.Message);
        }
        return null;
    }

    /// <summary>A top-level <c>SELECT DISTINCT</c> with no top-level ORDER BY (a hint line may precede it).</summary>
    public static bool IsDistinctWithoutOrderBy(string sql)
        => Regex.IsMatch(sql, @"^(-- ppds:\S+(?: \S+)? )?SELECT DISTINCT\b", RegexOptions.IgnoreCase) && !HasTopLevelOrderBy(sql);

    /// <summary>An ORDER BY at parenthesis depth 0 (not inside OVER (...) or a subquery).</summary>
    public static bool HasTopLevelOrderBy(string sql)
    {
        var depth = 0;
        var stripped = Regex.Replace(sql, "'(?:[^']|'')*'", "''");
        for (var i = 0; i < stripped.Length; i++)
        {
            var ch = stripped[i];
            if (ch == '(') depth++;
            else if (ch == ')') depth--;
            else if (depth == 0 && string.Compare(stripped, i, "ORDER BY", 0, 8, StringComparison.OrdinalIgnoreCase) == 0)
                return true;
        }
        return false;
    }

    // ───────────── recorded FetchXML ─────────────

    /// <summary>FetchXML with names in report form, attributes sorted, whitespace dropped; null when it is not FetchXML.</summary>
    public static string? CanonicalFetchXml(string? fetchXml)
    {
        if (fetchXml is null || !fetchXml.TrimStart().StartsWith("<fetch", StringComparison.Ordinal))
            return null;
        XElement root;
        // The engine names an anti-join link "__antijoin_" + 8 random hex digits, new on every run.
        try { root = XElement.Parse(Regex.Replace(ToReport(fetchXml), "__antijoin_[0-9a-f]{8}", "__antijoin_*")); }
        catch (System.Xml.XmlException) { return null; }
        static XElement Sort(XElement e) => new(e.Name, e.Attributes().OrderBy(a => a.Name.ToString(), StringComparer.Ordinal), e.Elements().Select(Sort),
            e.HasElements ? null : (string.IsNullOrWhiteSpace(e.Value) ? null : e.Value));
        return Sort(root).ToString(SaveOptions.DisableFormatting);
    }
}
