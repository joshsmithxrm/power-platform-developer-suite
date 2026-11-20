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

    /// <summary>Recorded "FetchXML" entries that are plan text, not FetchXML (client-side plans, set operations, scripts).</summary>
    private static readonly HashSet<string> RecordedPlanText =
        ["X01", "X08", "X11", "J09", "J10", "J11", "C03", "C04", "U01", "U02", "U03", "U04", "U05", "U06", "U07", "U08", "U09"];

    public static IEnumerable<object[]> MatchingCaseIds() => AuditCaseReplay.Cases.Where(c => !Expected.ContainsKey(c.Id)).Select(c => new object[] { c.Id });

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
