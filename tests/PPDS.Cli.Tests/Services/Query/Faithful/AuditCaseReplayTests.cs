using Xunit;
using Xunit.Abstractions;

namespace PPDS.Cli.Tests.Services.Query.Faithful;

/// <summary>
/// Every case of the November 2026 live SELECT audit, replayed offline: SQL → the real engine → <see cref="FakeDataverse"/>
/// holding the audit's own dataset (<see cref="AuditDataset"/>), compared with what the audit recorded live
/// (<see cref="AuditCaseReplay"/> says what is compared). The audit's outcome is the engine's result against Dataverse, so
/// offline must equal live: a case the audit marks correct is correct offline, and a case it marks WRONG RESULT is wrong
/// offline in the same way.
/// <para>
/// 436 cases: 425 replay and match (127 of them wrong results, as recorded) and 11 cannot be replayed (<see cref="Expected"/> lists each with the
/// reason). A change to the engine or the fake that moves any case out of its class fails here. The cases that match are
/// <see cref="Case_ReplaysAndMatchesTheAuditRecord"/>; the 127 wrong results are <see cref="WrongCase_ReproducesItsRecordedDefectUntilTheIssueIsFixed"/> (or, once fixed, <see cref="FixedCases_NoLongerReproduceTheRecordedWrongOutcome"/>); the 11 that cannot be replayed are <see cref="Case_DoesNotMatch_ForItsRecordedReason"/>.
/// Each case writes how much the record let it compare (<see cref="ReplayResult.Coverage"/>), and
/// <see cref="Coverage_Totals"/> pins the totals.
/// </para>
/// </summary>
[Trait("Category", "Unit")]
public class AuditCaseReplayTests(ITestOutputHelper output)
{
    /// <summary>The cases that do not replay and match, with why. Every other case must match.</summary>
    private static readonly Dictionary<string, (ReplayClass Class, string Reason)> Expected = new()
    {
        // Not run in-process by the audit either: planning recurses without bound and the stack overflow ends the process.
        ["S05"] = (ReplayClass.CannotReplay, "crash:"),
        ["S16"] = (ReplayClass.CannotReplay, "crash:"),
        ["S18"] = (ReplayClass.CannotReplay, "crash:"),
        ["S19"] = (ReplayClass.CannotReplay, "crash:"),
        ["S20"] = (ReplayClass.CannotReplay, "crash:"),
        ["S21"] = (ReplayClass.CannotReplay, "crash:"),

        // The report redacted the caller's identity (systemuser, owner names).
        ["V06"] = (ReplayClass.CannotReplay, "redacted:"),
        ["V07"] = (ReplayClass.CannotReplay, "redacted:"),
        ["J14"] = (ReplayClass.CannotReplay, "redacted:"),

        // SELECT *: the report cut the list of (system) columns, so the shape cannot be compared.
        ["PRJ02"] = (ReplayClass.CannotReplay, "the report cut the recorded column list"),
        ["PRJ03"] = (ReplayClass.CannotReplay, "the report cut the recorded column list"),
    };

    /// <summary>A WRONG RESULT case's state and the issues that track its defect (none: no issue names it yet).</summary>
    private sealed record WrongCase(bool FixedSinceTheAudit, int[] Issues)
    {
        public string IssueText => Issues.Length == 0 ? "no issue yet" : string.Join(", ", Issues.Select(n => "#" + n));
        public string Marker(bool fixedSinceTheAudit) => $"{(fixedSinceTheAudit ? "Fixed" : "Wrong")}({string.Join(", ", Issues)})";
    }

    /// <summary>Still wrong as the audit recorded it.</summary>
    private static WrongCase Wrong(params int[] issues) => new(false, issues);

    /// <summary>Fixed since the audit: the replay must no longer reproduce the recorded wrong outcome.</summary>
    private static WrongCase Fixed(params int[] issues) => new(true, issues);

    /// <summary>
    /// Every replayed case the audit marks WRONG RESULT, with the open issues that track its defect. The audit's numbered
    /// defect n is issue 1611 + n, defect 21 is #1632, and a case named in an issue body counts too. When a defect is fixed,
    /// the one edit is to change its cases here from <c>Wrong(...)</c> to <c>Fixed(...)</c>; the failure message says so.
    /// </summary>
    private static readonly Dictionary<string, WrongCase> WrongCases = new()
    {
        ["PRJ01"] = Wrong(1625),
        ["PRJ05"] = Wrong(1621),
        ["PRJ12"] = Wrong(1625),
        ["PRJ13"] = Wrong(1625),
        ["PRJ22"] = Wrong(1625),
        ["PRJ15"] = Wrong(1625),
        ["PRJ20"] = Wrong(1621),
        ["PRJ21"] = Wrong(1621),
        ["FN10"] = Wrong(1632),
        ["FN11"] = Wrong(1632),
        ["FN12"] = Wrong(1632),
        ["FN17"] = Wrong(1632),
        ["FN52"] = Wrong(1622),
        ["FN61"] = Wrong(1621),
        ["FN62"] = Wrong(1621),
        ["FN63"] = Wrong(1621),
        ["FN64"] = Wrong(1621),
        ["FN65"] = Wrong(1621),
        ["FN66"] = Wrong(1621, 1622),
        ["FN67"] = Wrong(1621),
        ["FN68"] = Wrong(1622),
        ["FN69"] = Wrong(1622),
        ["FN70"] = Wrong(1621),
        ["FN71"] = Wrong(1621),
        ["FN86"] = Wrong(1621),
        ["FN87"] = Wrong(1621),
        ["FN91"] = Wrong(1629),
        ["W02"] = Wrong(1616),
        ["W03"] = Wrong(1616),
        ["W09"] = Wrong(1615),
        ["W10"] = Wrong(1615),
        ["W11"] = Wrong(1615),
        ["W12"] = Wrong(1615),
        ["W19"] = Wrong(1616),
        ["W25"] = Wrong(1616),
        ["W29"] = Wrong(1615),
        ["W31"] = Wrong(1615),
        ["W44"] = Wrong(1615),
        ["W45"] = Wrong(1615),
        ["W46"] = Wrong(1615),
        ["W47"] = Wrong(1615),
        ["W48"] = Wrong(1615),
        ["W50"] = Wrong(1616),
        ["W52"] = Wrong(1616),
        ["W55"] = Wrong(1616),
        ["W60"] = Wrong(1622),
        ["W68"] = Wrong(1616),
        ["W74"] = Wrong(1622),
        ["W75"] = Wrong(1616),
        ["W76"] = Wrong(1612),
        ["W77"] = Wrong(1612),
        ["W86"] = Wrong(1625),
        ["W87"] = Wrong(1625),
        ["W88"] = Wrong(1625),
        ["W89"] = Wrong(1617),
        ["W90"] = Wrong(1622),
        ["W91"] = Wrong(1625),
        ["W92"] = Wrong(1622),
        ["W93"] = Wrong(1625),
        ["W94"] = Wrong(1625),
        ["W95"] = Wrong(1625),
        ["W96"] = Wrong(1625),
        ["W97"] = Wrong(1612),
        ["W98"] = Wrong(1612),
        ["W99"] = Wrong(1612),
        ["W100"] = Wrong(1622),
        ["W101"] = Wrong(1620),
        ["W103"] = Wrong(1620),
        ["W109"] = Wrong(1625),
        ["W110"] = Wrong(1622),
        ["W120"] = Wrong(1616),
        ["W134"] = Wrong(1631),
        ["W135"] = Wrong(1631),
        ["W136"] = Wrong(1612),
        ["W137"] = Wrong(1612),
        ["W138"] = Wrong(1617),
        ["X01"] = Wrong(1618),
        ["X08"] = Wrong(1631),
        ["X11"] = Wrong(1612),
        ["T02"] = Wrong(1619),
        ["T05"] = Wrong(1619),
        ["T07"] = Wrong(1619, 1625),
        ["T08"] = Wrong(1628),
        ["T11"] = Wrong(1625),
        ["D08"] = Wrong(1629),
        ["O04"] = Wrong(1628),
        ["O05"] = Wrong(1628),
        ["O07"] = Wrong(1628),
        ["A10"] = Wrong(1629),
        ["A30"] = Wrong(1612),
        ["J04"] = Wrong(1614),
        ["J08"] = Wrong(1627),
        ["J09"] = Wrong(1627),
        ["J10"] = Wrong(1627),
        ["J11"] = Wrong(1627),
        ["J13"] = Wrong(1627),
        ["J16"] = Wrong(1614),
        ["J17"] = Wrong(1621),
        ["J19"] = Wrong(),
        ["J23"] = Wrong(1614),
        ["J24"] = Wrong(1627),
        ["V03"] = Wrong(1626),
        ["V04"] = Wrong(1626),
        ["V15"] = Wrong(1625),
        ["V16"] = Wrong(1626),
        ["S03"] = Wrong(1613),
        ["S13"] = Wrong(),
        ["S14"] = Wrong(1613),
        ["S15"] = Wrong(1613),
        ["C03"] = Wrong(1630),
        ["C04"] = Wrong(1630),
        ["U01"] = Wrong(1623),
        ["U02"] = Wrong(1623),
        ["U03"] = Wrong(1623),
        ["U04"] = Wrong(1623),
        ["U05"] = Wrong(1623),
        ["U06"] = Wrong(1623),
        ["U07"] = Wrong(1623),
        ["U08"] = Wrong(1623),
        ["U09"] = Wrong(1623),
        ["WF02"] = Wrong(1621),
        ["WF03"] = Wrong(1621),
        ["WF07"] = Wrong(1621),
        ["WF11"] = Wrong(1621),
        ["WF13"] = Wrong(1621),
        ["PG11"] = Wrong(1619),
        ["E13"] = Wrong(1631),
    };

    /// <summary>
    /// Wrong cases whose fix the record cannot show, decided from the record alone (<see cref="AuditCaseReplay.FixVisibleInRecord"/>):
    /// the expected result has the same count and columns, and every row the record shows would stay. A fix would leave
    /// these green; they are on the live-observations list (LiveObservationsOwed.md).
    /// </summary>
    private static readonly HashSet<string> FixNotVisible = ["FN10", "FN11", "FN12", "FN68", "FN69", "FN71", "FN86", "FN87", "J16", "O07", "U02"];

    /// <summary>Recorded "FetchXML" entries that are plan text, not FetchXML (client-side plans, set operations, scripts).</summary>
    private static readonly HashSet<string> RecordedPlanText =
        ["X01", "X08", "X11", "J09", "J10", "J11", "C03", "C04", "U01", "U02", "U03", "U04", "U05", "U06", "U07", "U08", "U09"];

    public static IEnumerable<object[]> MatchingCaseIds() => AuditCaseReplay.Cases.Where(c => !Expected.ContainsKey(c.Id) && !WrongCases.ContainsKey(c.Id)).Select(c => new object[] { c.Id });

    public static IEnumerable<object[]> WrongCaseIds() => WrongCases.Where(w => !w.Value.FixedSinceTheAudit).Select(w => new object[] { w.Key });

    public static IEnumerable<object[]> FixedCaseIds() => WrongCases.Where(w => w.Value.FixedSinceTheAudit).Select(w => new object[] { w.Key });

    public static IEnumerable<object[]> NonMatchingCaseIds() => Expected.Keys.Select(id => new object[] { id });

    public static IEnumerable<object[]> FetchXmlIds() => AuditCaseReplay.RecordedFetchXml.Keys.Select(id => new object[] { id });

    [Fact]
    public void TheRecordsHoldEveryCase()
    {
        Assert.Equal(436, AuditCaseReplay.Cases.Count);
        Assert.Equal(129, AuditCaseReplay.RecordedFetchXml.Count);
        Assert.All(Expected.Keys, id => Assert.Contains(AuditCaseReplay.Cases, c => c.Id == id));
    }

    [Theory]
    [MemberData(nameof(MatchingCaseIds))]
    public async Task Case_ReplaysAndMatchesTheAuditRecord(string id)
    {
        var (c, result) = await ReplayAsync(id);

        Assert.True(result.Class == ReplayClass.Matches,
            $"{id} [{c.Verdict}] {c.Sql}\n  expected: Matches\n  got:      {result.Class} {result.Reason}\n  live:     {c.Actual}\n  offline:  {result.Offline}");
    }

    [Fact]
    public void TheWrongCasesAreExactlyTheAuditsReplayedWrongResults()
    {
        var recorded = AuditCaseReplay.Cases.Where(c => c.Verdict == "WRONG RESULT" && !Expected.ContainsKey(c.Id)).Select(c => c.Id).Order().ToList();
        Assert.Equal(recorded, WrongCases.Keys.Order().ToList());
        var computed = WrongCases.Keys.Where(id => !AuditCaseReplay.FixVisibleInRecord(AuditCaseReplay.Cases.Single(c => c.Id == id)).Visible).Order().ToList();
        foreach (var id in WrongCases.Keys.Order())
        {
            var (visible, why) = AuditCaseReplay.FixVisibleInRecord(AuditCaseReplay.Cases.Single(c => c.Id == id));
            output.WriteLine($"{id} {WrongCases[id].IssueText}: a fix is {(visible ? "visible" : "NOT visible")} in the record ({why})");
        }
        Assert.True(computed.SequenceEqual(FixNotVisible.Order()),
            $"FixNotVisible must be exactly the wrong cases whose fix the record cannot show: [{string.Join(", ", computed)}]");
    }

    /// <summary>
    /// A WRONG RESULT case that is still wrong: offline reproduces the wrong outcome the audit recorded live. When it no
    /// longer does, the failure names the issue that tracks the defect and the one edit to make.
    /// </summary>
    [Theory]
    [MemberData(nameof(WrongCaseIds))]
    public async Task WrongCase_ReproducesItsRecordedDefectUntilTheIssueIsFixed(string id)
    {
        var (c, result) = await ReplayAsync(id);
        var w = WrongCases[id];

        Assert.True(result.Class == ReplayClass.Matches,
            $"{id} no longer reproduces the wrong result the audit recorded live (defect tracked by {w.IssueText}). If that defect " +
            $"is fixed, remove the known-defect marker: in WrongCases change [\"{id}\"] = {w.Marker(false)} to {w.Marker(true)}. " +
            $"That is the only edit needed.\n  got:     {result.Class} {result.Reason}\n  live:    {c.Actual}\n  offline: {result.Offline}");
    }

    /// <summary>
    /// The WRONG RESULT cases fixed since the audit (none yet): offline must no longer reproduce the recorded wrong outcome.
    /// A fact over the list rather than a theory, so that an empty list is a pass.
    /// </summary>
    [Fact]
    public async Task FixedCases_NoLongerReproduceTheRecordedWrongOutcome()
    {
        foreach (var id in FixedCaseIds().Select(row => (string)row[0]))
        {
            var (c, result) = await ReplayAsync(id);
            var w = WrongCases[id];
            Assert.True(result.Class != ReplayClass.Matches,
                $"{id} reproduces the wrong result the audit recorded again (defect tracked by {w.IssueText}): the fix regressed. " +
                $"If the defect is back on purpose, change [\"{id}\"] = {w.Marker(true)} to {w.Marker(false)}.\n  live:    {c.Actual}\n  offline: {result.Offline}");
        }
    }

    /// <summary>The 11 cases that cannot be replayed: each keeps its class and reason, or this fails.</summary>
    [Theory]
    [MemberData(nameof(NonMatchingCaseIds))]
    public async Task Case_DoesNotMatch_ForItsRecordedReason(string id)
    {
        var (c, result) = await ReplayAsync(id);
        var (expectedClass, reason) = Expected[id];

        Assert.True(result.Class == expectedClass && result.Reason.StartsWith(reason, StringComparison.Ordinal),
            $"{id} [{c.Verdict}] {c.Sql}\n  expected: {expectedClass} {reason}\n  got:      {result.Class} {result.Reason}\n  live:     {c.Actual}\n  offline:  {result.Offline}");
    }

    /// <summary>
    /// How much the record lets the replay compare, over every case that replays. It is computed from the record alone
    /// (<see cref="AuditCaseReplay.RecordCoverage"/>), so fixing a defect does not change it. A partial case is one whose
    /// report shows only the first rows; the rest of its rows are compared by count alone.
    /// </summary>
    [Fact]
    public void Coverage_Totals()
    {
        var results = AuditCaseReplay.Cases.Where(c => !Expected.ContainsKey(c.Id)).Select(AuditCaseReplay.RecordCoverage).ToList();
        var rows = results.Where(r => r.MessageSegments == 0).ToList();
        var errors = results.Count - rows.Count;
        var full = rows.Count(r => r.Full);
        var totals = $"cases {results.Count}: errors {errors}, row results {rows.Count} (full {full}, partial {rows.Count - full}); " +
                     $"rows compared {rows.Sum(r => r.RowsCompared)} of {rows.Sum(r => r.RowsTotal)}; " +
                     $"order compared in {rows.Count(r => r.OrderCompared)}; ids compared {rows.Sum(r => r.IdsCompared)}, masked {rows.Sum(r => r.IdsMasked)}";
        output.WriteLine(totals);
        Assert.Equal(CoverageTotals, totals);
    }

    private const string CoverageTotals =
        "cases 425: errors 69, row results 356 (full 113, partial 243); rows compared 1101 of 38953; order compared in 45; ids compared 584, masked 0";

    private async Task<(AuditCase Case, ReplayResult Result)> ReplayAsync(string id)
    {
        var c = AuditCaseReplay.Cases.Single(x => x.Id == id);
        var result = await AuditCaseReplay.ReplayAsync(c);
        output.WriteLine($"{id}: {result.Class} {result.Reason} | {result.Coverage}");
        return (c, result);
    }

    [Theory]
    [MemberData(nameof(FetchXmlIds))]
    public async Task RecordedFetchXml_IsWhatTheEngineSendsOffline(string id)
    {
        var recorded = AuditCaseReplay.CanonicalFetchXml(AuditCaseReplay.RecordedFetchXml[id]);
        if (RecordedPlanText.Contains(id))
        {
            Assert.Null(recorded);
            return;
        }

        var run = await AuditCaseReplay.ExecuteAsync(AuditCaseReplay.Cases.Single(x => x.Id == id));
        Assert.NotNull(recorded);
        Assert.Equal(recorded, AuditCaseReplay.CanonicalFetchXml(run.FetchXml));
    }
}
