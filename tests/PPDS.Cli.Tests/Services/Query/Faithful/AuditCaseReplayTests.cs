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
/// 436 cases. Since the DML safety branch (#1698): 293 replay and match; 87 wrong results still reproduce as recorded
/// (<see cref="WrongCase_ReproducesItsRecordedDefectUntilTheIssueIsFixed"/>); 18 are fixed and return the audit's own
/// expected value (<see cref="FixedCases_NowReturnTheAuditsExpectedValue"/>); 19 wrong results are now a clean error
/// (<see cref="RefusedCase_RaisesItsCleanError"/>); 2 changed but are still wrong, with the new result pinned
/// (<see cref="ChangedCase_ReturnsItsNewObservedResult"/>); 4 clean errors are another clean error for the same statement
/// (<see cref="ErrorChangedCase_RaisesItsNewCleanError"/>); 2 send a shape not yet observed live
/// (<see cref="LiveObservationOwedCase_IsRefusedByTheFakeForItsUnobservedShape"/>); and 11 cannot be replayed
/// (<see cref="Expected"/> lists each with the reason, <see cref="Case_DoesNotMatch_ForItsRecordedReason"/>). An error
/// the service reports as a PpdsException with an error code, keeping the live exception as its inner exception, is the
/// same error. Six cases the audit records as correct (W13, W24, W26, W51, X13, T06) are refused by the branch on purpose
/// and fail here until the maintainer decides. A change that moves any case out of its class fails here.
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

    /// <summary>What a WRONG RESULT case does now.</summary>
    private enum WrongState
    {
        /// <summary>Offline still reproduces the wrong outcome the audit recorded live.</summary>
        Wrong,

        /// <summary>Offline returns the audit's own expected value: the defect is fixed for this case.</summary>
        Fixed,

        /// <summary>
        /// The wrong result is replaced by a clean error (a <c>PpdsException</c> with an <c>ErrorCodes</c> code, message
        /// pinned): no wrong rows, but not the expected value either, so the issue stays open.
        /// </summary>
        Refused,

        /// <summary>The result changed but is still not the expected value: the new observed result is pinned, with a note.</summary>
        Changed,
    }

    /// <summary>
    /// A WRONG RESULT case's state and the issues that track its defect (none: no issue names it yet). <see cref="Pin"/> is
    /// the refusal message (<see cref="WrongState.Refused"/>) or the new observed result in the record's form
    /// (<see cref="WrongState.Changed"/>).
    /// </summary>
    private sealed record WrongCase(WrongState State, int[] Issues, string? Pin = null, string? Note = null)
    {
        public bool FixedSinceTheAudit => State == WrongState.Fixed;
        public string IssueText => Issues.Length == 0 ? "no issue yet" : string.Join(", ", Issues.Select(n => "#" + n));
        public string Marker(bool fixedSinceTheAudit) => $"{(fixedSinceTheAudit ? "Fixed" : "Wrong")}({string.Join(", ", Issues)})";
    }

    /// <summary>Whether a wrong case is marked fixed since the audit (used by the other fixtures' known-defect markers).</summary>
    internal static bool IsFixedSinceTheAudit(string id) => WrongCases.TryGetValue(id, out var w) && w.FixedSinceTheAudit;

    /// <summary>Whether a wrong case's outcome changed since the audit (fixed, refused or changed): its recorded FetchXML is the defect's.</summary>
    private static bool ChangedSinceTheAudit(string id) => WrongCases.TryGetValue(id, out var w) && w.State != WrongState.Wrong;

    [Fact]
    public void EveryKnownDefectMarkerNamesAWrongCaseAsItsWitness()
    {
        var markers = typeof(KnownDefect).GetFields().Where(f => f.IsLiteral).Select(f => (string)f.GetRawConstantValue()!).ToList();
        Assert.All(markers, m => Assert.True(KnownDefect.Witness.TryGetValue(m, out var id) && WrongCases.ContainsKey(id), $"{m}: no witness wrong case"));
    }

    /// <summary>Still wrong as the audit recorded it.</summary>
    private static WrongCase Wrong(params int[] issues) => new(WrongState.Wrong, issues);

    /// <summary>Fixed since the audit: the replay must return the audit's own expected value.</summary>
    private static WrongCase Fixed(params int[] issues) => new(WrongState.Fixed, issues);

    /// <summary>Refused since the audit with a clean error whose message contains <paramref name="message"/>.</summary>
    private static WrongCase Refused(string message, params int[] issues) => new(WrongState.Refused, issues, message);

    /// <summary>Changed since the audit but still wrong: the replay must return <paramref name="observed"/>.</summary>
    private static WrongCase Changed(string observed, string note, params int[] issues) => new(WrongState.Changed, issues, observed, note);

    private const string NotRefused = "NOT (...) is not supported in a WHERE clause sent to Dataverse.";
    private const string NullComparisonRefused = "Comparing with NULL (= NULL, <> NULL, IN (NULL)) is not supported.";
    private const string ColumnValueRefused = "The value ColumnReferenceExpression is not supported in a WHERE condition sent to Dataverse.";

    /// <summary>
    /// Every replayed case the audit marks WRONG RESULT, with the open issues that track its defect. The audit's numbered
    /// defect n is issue 1611 + n, defect 21 is #1632, and a case named in an issue body counts too. When a defect is fixed,
    /// the one edit is to change its cases here from <c>Wrong(...)</c> to <c>Fixed(...)</c>; the failure message says so.
    /// </summary>
    private static readonly Dictionary<string, WrongCase> WrongCases = new()
    {
        ["PRJ01"] = Wrong(1625),
        ["PRJ05"] = Fixed(1621),
        ["PRJ12"] = Wrong(1625),
        ["PRJ13"] = Wrong(1625),
        ["PRJ22"] = Wrong(1625),
        ["PRJ15"] = Wrong(1625),
        ["PRJ20"] = Fixed(1621),
        ["PRJ21"] = Fixed(1621),
        ["FN10"] = Wrong(1632),
        ["FN11"] = Wrong(1632),
        ["FN12"] = Wrong(1632),
        ["FN17"] = Wrong(1632),
        ["FN52"] = Wrong(1622),
        ["FN61"] = Fixed(1621),
        ["FN62"] = Fixed(1621),
        ["FN63"] = Fixed(1621),
        ["FN64"] = Fixed(1621),
        ["FN65"] = Fixed(1621),
        ["FN66"] = Changed("[ppds_key, v] 36 rows: (1,'other') (10,'other') (11,'other') (12,'other') (13,'other') (14,'small') (15,'small') (16,'other') (17,'small') (18,'small') (19,'other') (2,'small') (20,'small') (21,'small') (22,'other') (23,'small') (24,'small') (25,'small') (26,'other') (27,'small') (28,'small') (29,'other') (3,'small') (30,'small') (31,'small') (32,'small') (33,'small') (34,'small') (35,'small') (36,'other') (4,'small') (5,'small') (6,'small') (7,'other') (8,'small') (9,'other')",
            "The CASE now reads ppds_num (#1621, #1622), but NOT over an unknown comparison is taken as true where SQL takes it as unknown (ELSE): rows with ppds_num NULL (6, 14, 15, 20, 21, 34) give 'small' instead of 'other'.", 1621, 1622),
        ["FN67"] = Fixed(1621),
        ["FN68"] = Wrong(1622),
        ["FN69"] = Wrong(1622),
        ["FN70"] = Fixed(1621),
        ["FN71"] = Wrong(1621),
        ["FN86"] = Wrong(1621),
        ["FN87"] = Wrong(1621),
        ["FN91"] = Wrong(1629),
        ["W02"] = Fixed(1616),
        ["W03"] = Fixed(1616),
        ["W09"] = Refused(NotRefused, 1615),
        ["W10"] = Refused(NotRefused, 1615),
        ["W11"] = Refused(NotRefused, 1615),
        ["W12"] = Refused(NotRefused, 1615),
        ["W19"] = Fixed(1616),
        ["W25"] = Refused(NullComparisonRefused, 1616),
        ["W29"] = Refused(NotRefused, 1615),
        ["W31"] = Refused(NotRefused, 1615),
        ["W44"] = Refused(NotRefused, 1615),
        ["W45"] = Refused(NotRefused, 1615),
        ["W46"] = Refused(NotRefused, 1615),
        ["W47"] = Refused(NotRefused, 1615),
        ["W48"] = Refused(NotRefused, 1615),
        ["W50"] = Fixed(1616),
        ["W52"] = Refused(NullComparisonRefused, 1616),
        ["W55"] = Fixed(1616),
        ["W60"] = Wrong(1622),
        ["W68"] = Fixed(1616),
        ["W74"] = Wrong(1622),
        ["W75"] = Fixed(1616),
        ["W76"] = Wrong(1612),
        ["W77"] = Wrong(1612),
        ["W86"] = Wrong(1625),
        ["W87"] = Wrong(1625),
        ["W88"] = Wrong(1625),
        ["W89"] = Wrong(1617),
        ["W90"] = Refused(NotRefused, 1622),
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
        ["W136"] = Refused(ColumnValueRefused, 1612),
        ["W137"] = Refused(ColumnValueRefused, 1612),
        ["W138"] = Wrong(1617),
        ["X01"] = Refused("The variable @n cannot be used in a WHERE condition sent to Dataverse.", 1618),
        ["X08"] = Wrong(1631),
        ["X11"] = Wrong(1612),
        ["T02"] = Fixed(1619),
        ["T05"] = Refused("Only TOP n or TOP (n) with a whole number is supported", 1619),
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
        ["J08"] = Refused("'' is not a valid Dataverse table or column name.", 1627),
        ["J09"] = Wrong(1627),
        ["J10"] = Wrong(1627),
        ["J11"] = Wrong(1627),
        ["J13"] = Wrong(1627),
        ["J16"] = Wrong(1614),
        ["J17"] = Changed("[ck, t] 20 rows: (1,-16) (10,NULL) (11,17) (13,25) (14,22) (15,NULL) (16,NULL) (17,34) (19,42) (2,-12) (20,NULL) (21,43) (22,47) (23,51) (3,-8) (4,NULL) (5,NULL) (7,1) (8,NULL) (9,9)",
            "ck now has its value (#1621), but p.ppds_num in the computed item is read from the root table C, which also has a ppds_num, not from the joined P (the FetchXML requests ppds_num on C and nothing on the link): every t is c.ppds_num + c.ppds_qty, as on main. No issue tracks that part yet.", 1621),
        ["J19"] = Wrong(1840),
        ["J23"] = Wrong(1614),
        ["J24"] = Wrong(1627),
        ["V03"] = Wrong(1626),
        ["V04"] = Wrong(1626),
        ["V15"] = Wrong(1625),
        ["V16"] = Wrong(1626),
        ["S03"] = Wrong(1613),
        ["S13"] = Wrong(1841),
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

    /// <summary>
    /// Cases that raise a different clean error since the audit: the same statement is now refused before anything is
    /// sent, with the message pinned (still an error, as live). Each is a PpdsException with an ErrorCodes code.
    /// </summary>
    private static readonly Dictionary<string, (string Message, string Why)> ErrorChangedSinceTheAudit = new()
    {
        ["X02"] = ("The variable @i cannot be used in a WHERE condition sent to Dataverse.", "#1618: refused instead of being sent as the text '@i', which Dataverse failed to convert"),
        ["X03"] = ("The variable @i cannot be used in a WHERE condition sent to Dataverse.", "#1618: as X02"),
        ["X05"] = ("The variable @d cannot be used in a WHERE condition sent to Dataverse.", "#1618: refused instead of being sent as the text '@d', which Dataverse refused as a date"),
        ["X12"] = ("The variable @i cannot be used in a WHERE condition sent to Dataverse.", "#1618: as X02, inside IN"),
    };

    /// <summary>
    /// Cases whose FetchXML now uses a shape no live observation covers (the closed-world fake refuses it), so the result
    /// cannot be shown offline; each is on the live-observations list (LiveObservationsOwed.md).
    /// </summary>
    private static readonly Dictionary<string, string> LiveObservationOwed = new()
    {
        ["W117"] = "condition not-null Boolean", // ppds_flag <> 1 is now sent with not-null (#1616)
        ["W120"] = "condition not-null Choice",  // ppds_colorcode <> 374110001 is now sent with not-null (#1616)
    };

    /// <summary>
    /// Wrong cases that still reproduce their recorded wrong result but whose FetchXML changed: the column a computed item
    /// reads is now requested (#1621, #1622), or the WITHIN GROUP column (FN91). The replay shows the same rows as the record.
    /// </summary>
    private static readonly Dictionary<string, string> FetchXmlChangedSinceTheAudit = new()
    {
        ["FN68"] = "<fetch><entity name=\"P\"><attribute name=\"ppds_key\" /><attribute name=\"ppds_name\" /></entity></fetch>",
        ["FN69"] = "<fetch><entity name=\"P\"><attribute name=\"ppds_key\" /><attribute name=\"ppds_name\" /></entity></fetch>",
        ["FN71"] = "<fetch><entity name=\"P\"><attribute name=\"ppds_key\" /><attribute name=\"ppds_flag\" /></entity></fetch>",
        ["FN86"] = "<fetch><entity name=\"P\"><attribute name=\"ppds_key\" /><attribute name=\"ppds_name\" /></entity></fetch>",
        ["FN87"] = "<fetch><entity name=\"P\"><attribute name=\"ppds_key\" /><attribute name=\"ppds_name\" /></entity></fetch>",
        ["FN91"] = "<fetch><entity name=\"P\"><attribute name=\"ppds_itemtype\" /><attribute name=\"ppds_key\" /><filter><condition attribute=\"ppds_key\" operator=\"le\" value=\"5\" /></filter></entity></fetch>",
    };

    public static IEnumerable<object[]> MatchingCaseIds() => AuditCaseReplay.Cases
        .Where(c => !Expected.ContainsKey(c.Id) && !WrongCases.ContainsKey(c.Id) && !ErrorChangedSinceTheAudit.ContainsKey(c.Id) && !LiveObservationOwed.ContainsKey(c.Id))
        .Select(c => new object[] { c.Id });

    public static IEnumerable<object[]> WrongCaseIds() => WrongCases.Where(w => w.Value.State == WrongState.Wrong && !LiveObservationOwed.ContainsKey(w.Key)).Select(w => new object[] { w.Key });

    public static IEnumerable<object[]> FixedCaseIds() => WrongCases.Where(w => w.Value.FixedSinceTheAudit).Select(w => new object[] { w.Key });

    public static IEnumerable<object[]> RefusedCaseIds() => WrongCases.Where(w => w.Value.State == WrongState.Refused).Select(w => new object[] { w.Key });

    public static IEnumerable<object[]> ChangedCaseIds() => WrongCases.Where(w => w.Value.State == WrongState.Changed).Select(w => new object[] { w.Key });

    public static IEnumerable<object[]> ErrorChangedCaseIds() => ErrorChangedSinceTheAudit.Keys.Select(id => new object[] { id });

    public static IEnumerable<object[]> LiveObservationOwedIds() => LiveObservationOwed.Keys.Select(id => new object[] { id });

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
    /// The WRONG RESULT cases fixed since the audit: offline must return the audit's own expected value (not merely differ
    /// from the recorded wrong outcome). A fact over the list rather than a theory, so that an empty list is a pass.
    /// </summary>
    [Fact]
    public async Task FixedCases_NowReturnTheAuditsExpectedValue()
    {
        foreach (var id in FixedCaseIds().Select(row => (string)row[0]))
        {
            var c = AuditCaseReplay.Cases.Single(x => x.Id == id);
            var result = await AuditCaseReplay.ReplayAgainstExpectedAsync(c);
            var w = WrongCases[id];
            Assert.True(result.Class == ReplayClass.Matches,
                $"{id} is marked fixed ({w.IssueText}) but offline does not return the audit's expected value: {result.Class} {result.Reason}. " +
                $"If the defect is back, change [\"{id}\"] = {w.Marker(true)} to {w.Marker(false)}.\n  expected: {c.Expected}\n  offline:  {result.Offline}");
        }
    }

    /// <summary>A wrong result replaced by a clean error: a PpdsException with an ErrorCodes code and the pinned message, never rows.</summary>
    [Theory]
    [MemberData(nameof(RefusedCaseIds))]
    public async Task RefusedCase_RaisesItsCleanError(string id)
    {
        var c = AuditCaseReplay.Cases.Single(x => x.Id == id);
        var run = await AuditCaseReplay.ExecuteAsync(c);
        var w = WrongCases[id];

        var error = Assert.IsAssignableFrom<PPDS.Cli.Infrastructure.Errors.PpdsException>(run.Error);
        Assert.False(string.IsNullOrEmpty(error.ErrorCode), $"{id}: the refusal has no error code");
        Assert.True(run.Text.Contains(w.Pin!, StringComparison.Ordinal), $"{id} ({w.IssueText}): expected the refusal \"{w.Pin}\", got {run.Text}");
    }

    /// <summary>A wrong result that changed but is still not the expected value: the new observed result is pinned.</summary>
    [Theory]
    [MemberData(nameof(ChangedCaseIds))]
    public async Task ChangedCase_ReturnsItsNewObservedResult(string id)
    {
        var c = AuditCaseReplay.Cases.Single(x => x.Id == id);
        var w = WrongCases[id];

        var now = await AuditCaseReplay.ReplayAgainstAsync(c, w.Pin!);
        Assert.True(now.Class == ReplayClass.Matches, $"{id} ({w.IssueText}; {w.Note}) no longer returns its pinned result: {now.Reason}\n  offline: {now.Offline}");
        var expected = await AuditCaseReplay.ReplayAgainstExpectedAsync(c);
        Assert.True(expected.Class != ReplayClass.Matches, $"{id} now returns the audit's expected value: mark it Fixed({string.Join(", ", w.Issues)}).");
    }

    /// <summary>A clean error that is now another clean error for the same statement (refused before anything is sent).</summary>
    [Theory]
    [MemberData(nameof(ErrorChangedCaseIds))]
    public async Task ErrorChangedCase_RaisesItsNewCleanError(string id)
    {
        var c = AuditCaseReplay.Cases.Single(x => x.Id == id);
        Assert.False(c.Actual.StartsWith('['), $"{id}: only a recorded error may change into another error");
        var run = await AuditCaseReplay.ExecuteAsync(c);
        var (message, why) = ErrorChangedSinceTheAudit[id];

        var error = Assert.IsAssignableFrom<PPDS.Cli.Infrastructure.Errors.PpdsException>(run.Error);
        Assert.False(string.IsNullOrEmpty(error.ErrorCode), $"{id}: the error has no error code");
        Assert.True(run.Text.Contains(message, StringComparison.Ordinal), $"{id} ({why}): expected \"{message}\", got {run.Text}");
    }

    /// <summary>A case whose new FetchXML uses a shape the fake has no live observation for: it cannot be replayed until it is observed.</summary>
    [Theory]
    [MemberData(nameof(LiveObservationOwedIds))]
    public async Task LiveObservationOwedCase_IsRefusedByTheFakeForItsUnobservedShape(string id)
    {
        var (_, result) = await ReplayAsync(id);

        Assert.True(result.Class == ReplayClass.CannotReplay && result.Reason.Contains($"the shape '{LiveObservationOwed[id]}'", StringComparison.Ordinal),
            $"{id}: expected the fake to refuse the unobserved shape '{LiveObservationOwed[id]}', got {result.Class} {result.Reason}. " +
            "If it has been observed live, add the rule and move the case back.");
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
        if (ChangedSinceTheAudit(id) || LiveObservationOwed.ContainsKey(id))
            return; // the recorded FetchXML is the one the defect produced; the fix may change it
        if (RecordedPlanText.Contains(id))
        {
            Assert.Null(recorded);
            return;
        }

        var run = await AuditCaseReplay.ExecuteAsync(AuditCaseReplay.Cases.Single(x => x.Id == id));
        Assert.NotNull(recorded);
        if (FetchXmlChangedSinceTheAudit.TryGetValue(id, out var now))
        {
            Assert.NotEqual(recorded, now);
            Assert.Equal(now, AuditCaseReplay.CanonicalFetchXml(run.FetchXml));
            return;
        }
        Assert.Equal(recorded, AuditCaseReplay.CanonicalFetchXml(run.FetchXml));
    }
}
