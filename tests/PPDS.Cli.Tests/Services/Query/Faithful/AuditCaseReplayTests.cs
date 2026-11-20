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
/// 436 cases: 424 replay and match, 1 replays and differs, 11 cannot be replayed (<see cref="Expected"/> lists each with the
/// reason). A change to the engine or the fake that moves any case out of its class fails here. The cases that match are
/// <see cref="Case_ReplaysAndMatchesTheAuditRecord"/>; the 12 that do not are <see cref="Case_DoesNotMatch_ForItsRecordedReason"/>.
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
        // The engine ignores ORDER BY 2, 1 (#1628), so the rows come in Dataverse's id order. The live id of key 27 never
        // appears in the report, so its place in that order cannot be reproduced; every other row is where live has it.
        ["O05"] = (ReplayClass.Differs, "ordered rows differ"),

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

    /// <summary>
    /// The 126 replayed cases the audit marks WRONG RESULT, each with the open issues that track its defect (the audit's
    /// numbered defect n is issue 1611 + n; a case named in an issue body counts too). Empty: no issue names the case yet.
    /// When a case stops reproducing the recorded wrong outcome, the failure names the issue: remove its entry here.
    /// </summary>
    private static readonly Dictionary<string, int[]> WrongCases = new()
    {
        ["PRJ01"] = [1625],
        ["PRJ05"] = [1621],
        ["PRJ12"] = [1625],
        ["PRJ13"] = [1625],
        ["PRJ22"] = [1625],
        ["PRJ15"] = [1625],
        ["PRJ20"] = [1621],
        ["PRJ21"] = [1621],
        ["FN10"] = [],
        ["FN11"] = [],
        ["FN12"] = [],
        ["FN17"] = [],
        ["FN52"] = [1622],
        ["FN61"] = [1621],
        ["FN62"] = [1621],
        ["FN63"] = [1621],
        ["FN64"] = [1621],
        ["FN65"] = [],
        ["FN66"] = [1621, 1622],
        ["FN67"] = [1621],
        ["FN68"] = [1622],
        ["FN69"] = [1622],
        ["FN70"] = [1621],
        ["FN71"] = [1621],
        ["FN86"] = [1621],
        ["FN87"] = [1621],
        ["FN91"] = [1629],
        ["W02"] = [1616],
        ["W03"] = [1616],
        ["W09"] = [1615],
        ["W10"] = [1615],
        ["W11"] = [1615],
        ["W12"] = [1615],
        ["W19"] = [1616],
        ["W25"] = [1616],
        ["W29"] = [1615],
        ["W31"] = [1615],
        ["W44"] = [1615],
        ["W45"] = [1615],
        ["W46"] = [],
        ["W47"] = [],
        ["W48"] = [1615],
        ["W50"] = [1616],
        ["W52"] = [1616],
        ["W55"] = [1616],
        ["W60"] = [1622],
        ["W68"] = [1616],
        ["W74"] = [1622],
        ["W75"] = [1616],
        ["W76"] = [1612],
        ["W77"] = [1612],
        ["W86"] = [1625],
        ["W87"] = [],
        ["W88"] = [],
        ["W89"] = [1617],
        ["W90"] = [1622],
        ["W91"] = [],
        ["W92"] = [1622],
        ["W93"] = [],
        ["W94"] = [],
        ["W95"] = [],
        ["W96"] = [1625],
        ["W97"] = [1612],
        ["W98"] = [1612],
        ["W99"] = [1612],
        ["W100"] = [1622],
        ["W101"] = [1620],
        ["W103"] = [1620],
        ["W109"] = [1625],
        ["W110"] = [1622],
        ["W120"] = [1616],
        ["W134"] = [1631],
        ["W135"] = [1631],
        ["W136"] = [1612],
        ["W137"] = [1612],
        ["W138"] = [1617],
        ["X01"] = [1618],
        ["X08"] = [1631],
        ["X11"] = [1612],
        ["T02"] = [1619],
        ["T05"] = [1619],
        ["T07"] = [1619, 1625],
        ["T08"] = [1628],
        ["T11"] = [1625],
        ["D08"] = [1629],
        ["O04"] = [1628],
        ["O07"] = [1628],
        ["A10"] = [1629],
        ["A30"] = [1612],
        ["J04"] = [1614],
        ["J08"] = [1627],
        ["J09"] = [1627],
        ["J10"] = [1627],
        ["J11"] = [1627],
        ["J13"] = [1627],
        ["J16"] = [1614],
        ["J17"] = [1621],
        ["J19"] = [],
        ["J23"] = [1614],
        ["J24"] = [1627],
        ["V03"] = [1626],
        ["V04"] = [1626],
        ["V15"] = [1625],
        ["V16"] = [1626],
        ["S03"] = [1613],
        ["S13"] = [],
        ["S14"] = [1613],
        ["S15"] = [1613],
        ["C03"] = [1630],
        ["C04"] = [1630],
        ["U01"] = [1623],
        ["U02"] = [1623],
        ["U03"] = [1623],
        ["U04"] = [1623],
        ["U05"] = [1623],
        ["U06"] = [1623],
        ["U07"] = [1623],
        ["U08"] = [1623],
        ["U09"] = [1623],
        ["WF02"] = [1621],
        ["WF03"] = [1621],
        ["WF07"] = [1621],
        ["WF11"] = [1621],
        ["WF13"] = [1621],
        ["PG11"] = [1619],
        ["E13"] = [1631],
    };

    /// <summary>
    /// Wrong cases whose fix the record cannot show: with the columns the audit's verdict ignored dropped, the wrong
    /// offline result also matches what the audit expected (the shown rows and the count are the same either way). A fix
    /// would leave these green; they are on the live-observations list (LiveObservationsOwed.md).
    /// </summary>
    private static readonly HashSet<string> FixNotVisible = ["FN10", "FN11", "FN12", "FN17", "FN69", "FN86", "FN87", "J16", "O07", "U02"];

    /// <summary>Recorded "FetchXML" entries that are plan text, not FetchXML (client-side plans, set operations, scripts).</summary>
    private static readonly HashSet<string> RecordedPlanText =
        ["X01", "X08", "X11", "J09", "J10", "J11", "C03", "C04", "U01", "U02", "U03", "U04", "U05", "U06", "U07", "U08", "U09"];

    public static IEnumerable<object[]> MatchingCaseIds() => AuditCaseReplay.Cases.Where(c => !Expected.ContainsKey(c.Id) && !WrongCases.ContainsKey(c.Id)).Select(c => new object[] { c.Id });

    public static IEnumerable<object[]> WrongCaseIds() => WrongCases.Keys.Select(id => new object[] { id });

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
        Assert.Equal(126, recorded.Count);
        Assert.All(FixNotVisible, id => Assert.Contains(id, WrongCases.Keys));
    }

    /// <summary>
    /// A WRONG RESULT case: offline reproduces the wrong outcome the audit recorded live. When it no longer does, the
    /// failure names the issue that tracks the defect and says to remove the marker. Also pins whether a fix would be
    /// visible in what the record shows.
    /// </summary>
    [Theory]
    [MemberData(nameof(WrongCaseIds))]
    public async Task WrongCase_ReproducesItsRecordedDefectUntilTheIssueIsFixed(string id)
    {
        var (c, result) = await ReplayAsync(id);
        var issues = WrongCases[id].Length == 0 ? "no issue yet" : string.Join(", ", WrongCases[id].Select(n => "#" + n));

        Assert.True(result.Class == ReplayClass.Matches,
            $"{id} no longer reproduces the wrong result the audit recorded live (defect tracked by {issues}). If that defect is " +
            $"fixed, remove the known-defect marker: the entry for {id} in WrongCases.\n  got:     {result.Class} {result.Reason}\n" +
            $"  live:    {c.Actual}\n  offline: {result.Offline}");

        var expected = await AuditCaseReplay.CompareWithExpectedAsync(c);
        var visible = expected.Class != ReplayClass.Matches;
        output.WriteLine($"{id}: {issues}; a fix is {(visible ? "visible" : "NOT visible")} in the record ({expected.Class} {expected.Reason})");
        Assert.True(visible == !FixNotVisible.Contains(id),
            $"{id}: a fix would {(visible ? "" : "not ")}be visible in the record, but FixNotVisible says otherwise ({expected.Class} {expected.Reason})");
    }

    /// <summary>The 12 cases that do not replay and match: each keeps its class and reason, or this fails.</summary>
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
    /// How much the record let the replay compare, over every matching case. A partial case is one whose report shows only
    /// the first rows; the rest of its rows are compared by count alone.
    /// </summary>
    [Fact]
    public async Task Coverage_Totals()
    {
        var results = new List<ReplayResult>();
        foreach (var c in AuditCaseReplay.Cases.Where(c => !Expected.ContainsKey(c.Id)))
            results.Add((await ReplayAsync(c.Id)).Result);
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
        "cases 424: errors 69, row results 355 (full 113, partial 242); rows compared 1099 of 38917; order compared in 36; ids compared 563, masked 19";

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
