using System.Xml.Linq;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Moq;
using PPDS.Cli.Commands;
using PPDS.Cli.Commands.PluginTraces;
using PPDS.Cli.Commands.Serve.Handlers;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services.PluginTraces;
using PPDS.Cli.Tests.Services.Shared;
using PPDS.Dataverse.Client;
using PPDS.Dataverse.Configuration;
using PPDS.Dataverse.Pooling;
using Xunit;

namespace PPDS.Cli.Tests.Services.PluginTraces;

/// <summary>
/// Issue 1920: deleting plug-in traces by age. The age is parsed in one exact form and must be
/// positive; the cut-off is computed once, from the clock in UTC, and compared with <c>lt</c> (not the
/// date-worded <c>on-or-before</c>), so a clean-up cannot delete more than was asked. Every test reads
/// an injected clock, never the machine's.
/// </summary>
[Trait("Category", "Unit")]
public class PluginTraceAgeDeleteTests
{
    // 05:38 UTC: far enough from midnight that a date-only reading and an exact one differ for 30m.
    private static readonly DateTime Now = new(2026, 11, 25, 5, 38, 27, DateTimeKind.Utc);

    // ─── The one accepted form ──────────────────────────────────────────────────

    [Theory]
    [InlineData("7d", 7 * 24 * 60)]
    [InlineData("1d", 24 * 60)]
    [InlineData("24h", 24 * 60)]
    [InlineData("30m", 30)]
    [InlineData("1m", 1)]
    [InlineData("36500d", 36500 * 24 * 60)]
    public void Parse_AcceptsTheDocumentedForm(string text, int minutes)
    {
        PluginTraceAge.Parse(text).Should().Be(TimeSpan.FromMinutes(minutes));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("0d")]
    [InlineData("0m")]
    [InlineData("-7d")]
    [InlineData("-1")]
    [InlineData("-0d")]
    [InlineData("+7d")]
    [InlineData("7")]          // no unit: no longer read as days
    [InlineData("07d")]
    [InlineData("7D")]
    [InlineData("7 d")]
    [InlineData(" 7d")]
    [InlineData("7d ")]
    [InlineData("7d\n")]
    [InlineData("7dx")]
    [InlineData("7days")]
    [InlineData("1.5d")]
    [InlineData("1e3m")]
    [InlineData("7w")]
    [InlineData("7s")]
    [InlineData("d")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("36501d")]     // more than 100 years
    [InlineData("876001h")]
    [InlineData("99999999m")]
    [InlineData("٧d")]         // a digit that is not ASCII
    public void Parse_RefusesAnythingElse(string? text)
    {
        var act = () => PluginTraceAge.Parse(text);

        act.Should().Throw<PpdsException>().Which.ErrorCode.Should().Be(ErrorCodes.Validation.InvalidValue);
    }

    [Fact]
    public void Parse_ErrorDoesNotRepeatTheInput()
    {
        var ex = Assert.Throws<PpdsException>(() => PluginTraceAge.Parse("7dSECRET"));

        ex.UserMessage.Should().NotContain("SECRET");
        ex.UserMessage.Should().Contain("7d, 24h or 30m");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(36500)]
    public void FromDays_AcceptsOneToTheMaximum(int days)
    {
        PluginTraceAge.FromDays(days).Should().Be(TimeSpan.FromDays(days));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(36501)]
    [InlineData(int.MaxValue)] // too large for a TimeSpan: refused, not a raw exception
    [InlineData(int.MinValue)]
    public void FromDays_RefusesOutOfRangeWithACode(int days)
    {
        var act = () => PluginTraceAge.FromDays(days);

        act.Should().Throw<PpdsException>().Which.ErrorCode.Should().Be(ErrorCodes.Validation.InvalidValue);
    }

    [Fact]
    public void CutoffUtc_IsNowLessTheAge()
    {
        var cutoff = PluginTraceAge.CutoffUtc(TimeSpan.FromMinutes(30), Now);

        cutoff.Should().Be(new DateTime(2026, 11, 25, 5, 8, 27, DateTimeKind.Utc));
        cutoff.Kind.Should().Be(DateTimeKind.Utc);
        PluginTraceAge.Format(cutoff).Should().Be("2026-11-25T05:08:27Z");
    }

    [Fact]
    public void CutoffUtc_RefusesALocalTime()
    {
        var local = DateTime.SpecifyKind(Now, DateTimeKind.Local);

        var act = () => PluginTraceAge.CutoffUtc(TimeSpan.FromDays(1), local);

        act.Should().Throw<ArgumentException>();
    }

    // ─── The service refuses before anything is read ────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-7 * 24 * 60)]
    public async Task DeleteOlderThanAsync_RefusesZeroAndNegativeAges_BeforeReadingAnything(int minutes)
    {
        var store = new TraceStore(TenTracesFromTheLastHours());
        var service = store.CreateService(new TestClock(Now));

        var act = () => service.DeleteOlderThanAsync(TimeSpan.FromMinutes(minutes));

        (await act.Should().ThrowAsync<PpdsException>()).Which.ErrorCode.Should().Be(ErrorCodes.Validation.InvalidValue);
        store.ClientsTaken.Should().Be(0);
        store.Deleted.Should().BeEmpty();
        store.Remaining.Should().Be(10);
    }

    [Theory]
    [InlineData(0L)]                                     // now
    [InlineData(1L)]                                     // one tick before now
    [InlineData(TimeSpan.TicksPerMinute - 1)]            // just under the shortest age
    [InlineData(-7 * TimeSpan.TicksPerDay)]              // seven days in the future
    public async Task DeleteCreatedBeforeAsync_RefusesACutoffLessThanOneMinuteInThePast(long ticksBeforeNow)
    {
        var store = new TraceStore(TenTracesFromTheLastHours());
        var service = store.CreateService(new TestClock(Now));

        var act = () => service.DeleteCreatedBeforeAsync(Now - TimeSpan.FromTicks(ticksBeforeNow));

        (await act.Should().ThrowAsync<PpdsException>()).Which.ErrorCode.Should().Be(ErrorCodes.Validation.InvalidValue);
        store.ClientsTaken.Should().Be(0);
        store.Remaining.Should().Be(10);
    }

    [Fact]
    public async Task DeleteCreatedBeforeAsync_AcceptsACutoffExactlyOneMinuteInThePast()
    {
        var store = new TraceStore(TenTracesFromTheLastHours());
        var service = store.CreateService(new TestClock(Now));

        var deleted = await service.DeleteCreatedBeforeAsync(Now - PluginTraceAge.MinAge);

        deleted.Should().Be(9); // the trace created exactly at Now is not older
        store.Remaining.Should().Be(1);
    }

    [Fact]
    public async Task DeleteCreatedBeforeAsync_RefusesACutoffThatIsNotUtc()
    {
        var store = new TraceStore(TenTracesFromTheLastHours());
        var service = store.CreateService(new TestClock(Now));
        var unspecified = DateTime.SpecifyKind(Now.AddDays(-1), DateTimeKind.Unspecified);

        var act = () => service.DeleteCreatedBeforeAsync(unspecified);

        (await act.Should().ThrowAsync<PpdsException>()).Which.ErrorCode.Should().Be(ErrorCodes.Validation.InvalidValue);
        store.ClientsTaken.Should().Be(0);
    }

    // ─── The condition sent ─────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteCreatedBeforeAsync_SendsLessThanOnTheExactCutoff()
    {
        var store = new TraceStore(TenTracesFromTheLastHours());
        var service = store.CreateService(new TestClock(Now));
        var cutoff = Now.AddHours(-2);

        await service.DeleteCreatedBeforeAsync(cutoff);

        store.CreatedOnConditions.Should().NotBeEmpty();
        store.CreatedOnConditions.Should().OnlyContain(c => c.Operator == ConditionOperator.LessThan);
        store.CreatedOnConditions.Should().OnlyContain(c => (DateTime)c.Values[0] == cutoff);
    }

    [Fact]
    public async Task DeleteOlderThanAsync_UsesTheServiceClockInUtc()
    {
        var store = new TraceStore(TenTracesFromTheLastHours());
        var service = store.CreateService(new TestClock(Now, zone: PlusTen));

        await service.DeleteOlderThanAsync(TimeSpan.FromHours(2));

        store.CreatedOnConditions.Should().NotBeEmpty();
        store.CreatedOnConditions.Should().OnlyContain(c =>
            (DateTime)c.Values[0] == Now.AddHours(-2) && ((DateTime)c.Values[0]).Kind == DateTimeKind.Utc);
    }

    [Fact]
    public async Task CountAsync_SendsLtAndGeOnTheFullTimestamp()
    {
        var store = new TraceStore(TenTracesFromTheLastHours());
        var service = store.CreateService(new TestClock(Now));
        var before = new DateTime(2026, 11, 25, 5, 8, 27, DateTimeKind.Utc);
        var after = new DateTime(2026, 11, 20, 1, 2, 3, DateTimeKind.Utc);

        await service.CountAsync(new PluginTraceFilter { CreatedBefore = before, CreatedAfter = after });

        var conditions = store.FetchConditions.Where(c => c.Attribute == "createdon").ToList();
        conditions.Should().HaveCount(2);
        conditions.Should().ContainSingle(c => c.Operator == "lt" && c.Value == "2026-11-25T05:08:27.0000000Z");
        conditions.Should().ContainSingle(c => c.Operator == "ge" && c.Value == "2026-11-20T01:02:03.0000000Z");
    }

    [Fact]
    public async Task ListAsync_SendsGreaterEqualForCreatedAfter()
    {
        var store = new TraceStore(TenTracesFromTheLastHours());
        var service = store.CreateService(new TestClock(Now));

        await service.ListAsync(new PluginTraceFilter { CreatedAfter = Now.AddHours(-1) });

        store.CreatedOnConditions.Should().ContainSingle()
            .Which.Operator.Should().Be(ConditionOperator.GreaterEqual);
    }

    // ─── What gets deleted (the issue's two examples, and paging) ───────────────

    [Fact]
    public async Task DeleteOlderThanAsync_ThirtyMinutes_DeletesOnlyTheTracesOlderThanThirtyMinutes()
    {
        // Traces aged 10 minutes, 20 minutes, 3 days and 4 days. The fake compares `on-or-before`
        // by date only, as its documented wording ("on or before a specified date") allows; `lt`
        // compares the full value.
        var store = new TraceStore(new[]
        {
            Now.AddMinutes(-10), Now.AddMinutes(-20), Now.AddDays(-3), Now.AddDays(-4),
        });
        var service = store.CreateService(new TestClock(Now));

        var deleted = await service.DeleteOlderThanAsync(TimeSpan.FromMinutes(30));

        deleted.Should().Be(2);
        store.Deleted.Should().HaveCount(2);
        store.Remaining.Should().Be(2);
    }

    [Fact]
    public async Task DeleteOlderThanAsync_NegativeSevenDays_DeletesNothing()
    {
        var store = new TraceStore(TenTracesFromTheLastHours());
        var service = store.CreateService(new TestClock(Now));

        var act = () => service.DeleteOlderThanAsync(TimeSpan.FromDays(-7));

        await act.Should().ThrowAsync<PpdsException>();
        store.Deleted.Should().BeEmpty();
        store.Remaining.Should().Be(10);
    }

    [Fact]
    public async Task DeleteOlderThanAsync_MoreMatchesThanOnePage_DeletesThemAll()
    {
        // One page is 5000 rows: 5003 old traces need a second page; two recent ones stay.
        var old = Enumerable.Range(0, 5003).Select(i => Now.AddDays(-2).AddSeconds(-i));
        var store = new TraceStore(old.Concat(new[] { Now.AddMinutes(-5), Now.AddMinutes(-1) }));
        var service = store.CreateService(new TestClock(Now));

        var deleted = await service.DeleteOlderThanAsync(TimeSpan.FromDays(1));

        deleted.Should().Be(5003);
        store.Remaining.Should().Be(2);
        store.ListPages.Should().BeGreaterThan(1);
    }

    // ─── The command's flow (PluginTraceOlderThanDeletion) ──────────────────────

    [Fact]
    public async Task Flow_CountGetsNowLessTheAge_InUtc_FromTheClock()
    {
        var traces = new FlowFake(count: 4);
        var clock = new TestClock(Now, zone: PlusTen); // local time is ten hours ahead of UTC

        await PluginTraceOlderThanDeletion.RunAsync(
            traces.Object, clock, Request(TimeSpan.FromDays(7), force: true), (_, _) => true);

        traces.CountFilters.Should().ContainSingle();
        var before = traces.CountFilters[0].CreatedBefore!.Value;
        before.Should().Be(Now.AddDays(-7));
        before.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Fact]
    public async Task Flow_ForcedRun_DeletesOnceWithTheSameInstantTheCountUsed()
    {
        var traces = new FlowFake(count: 4);
        var clock = new TestClock(Now, step: TimeSpan.FromSeconds(1)); // each read is a second later

        var result = await PluginTraceOlderThanDeletion.RunAsync(
            traces.Object, clock, Request(TimeSpan.FromDays(7), force: true), (_, _) => throw new InvalidOperationException("no prompt when forced"));

        traces.DeleteCutoffs.Should().ContainSingle().Which.Should().Be(Now.AddDays(-7));
        traces.DeleteCutoffs[0].Should().Be(traces.CountFilters.Single().CreatedBefore!.Value);
        result.Status.Should().Be(OlderThanDeletionStatus.Deleted);
        result.CutoffUtc.Should().Be(Now.AddDays(-7));
        traces.Mock.Verify(t => t.DeleteOlderThanAsync(It.IsAny<TimeSpan>(), It.IsAny<IProgress<int>?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Flow_NotForced_AsksWithTheCutoffAndCount_AndDeclineDeletesNothing()
    {
        var traces = new FlowFake(count: 4);
        var asked = new List<(DateTime Cutoff, int Count)>();

        var result = await PluginTraceOlderThanDeletion.RunAsync(
            traces.Object, new TestClock(Now), Request(TimeSpan.FromHours(24)),
            (cutoff, count) => { asked.Add((cutoff, count)); return false; });

        asked.Should().ContainSingle().Which.Should().Be((Now.AddHours(-24), 4));
        traces.DeleteCutoffs.Should().BeEmpty();
        result.Status.Should().Be(OlderThanDeletionStatus.Declined);
    }

    [Fact]
    public async Task Flow_NotForced_AcceptedPromptDeletesOnce()
    {
        var traces = new FlowFake(count: 4);
        var asked = 0;

        await PluginTraceOlderThanDeletion.RunAsync(
            traces.Object, new TestClock(Now), Request(TimeSpan.FromHours(24)), (_, _) => { asked++; return true; });

        asked.Should().Be(1);
        traces.DeleteCutoffs.Should().ContainSingle().Which.Should().Be(Now.AddHours(-24));
    }

    [Fact]
    public async Task Flow_ShowsTheCutoffBeforeDeleting_EvenWhenForced()
    {
        var traces = new FlowFake(count: 4);
        var events = new List<string>();
        traces.OnDelete = () => events.Add("delete");

        await PluginTraceOlderThanDeletion.RunAsync(
            traces.Object, new TestClock(Now), Request(TimeSpan.FromDays(7), force: true), (_, _) => true,
            deleting: (cutoff, count) => events.Add($"show {PluginTraceAge.Format(cutoff)} {count}"));

        events.Should().Equal("show 2026-11-18T05:38:27Z 4", "delete");
    }

    [Fact]
    public async Task Flow_DryRunAndNothingToDelete_NeitherAskNorDelete()
    {
        var dry = new FlowFake(count: 4);
        var none = new FlowFake(count: 0);

        var preview = await PluginTraceOlderThanDeletion.RunAsync(
            dry.Object, new TestClock(Now), Request(TimeSpan.FromDays(7), dryRun: true, json: true), (_, _) => throw new InvalidOperationException());
        var empty = await PluginTraceOlderThanDeletion.RunAsync(
            none.Object, new TestClock(Now), Request(TimeSpan.FromDays(7)), (_, _) => throw new InvalidOperationException());

        preview.Should().Be(new OlderThanDeletionResult(OlderThanDeletionStatus.Previewed, Now.AddDays(-7), 4, 0));
        empty.Status.Should().Be(OlderThanDeletionStatus.NothingToDelete);
        dry.DeleteCutoffs.Should().BeEmpty();
        none.DeleteCutoffs.Should().BeEmpty();
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Flow_WithoutAWayToConfirm_IsRefusedBeforeCounting(bool json, bool redirected)
    {
        var traces = new FlowFake(count: 4);

        var act = () => PluginTraceOlderThanDeletion.RunAsync(
            traces.Object, new TestClock(Now), Request(TimeSpan.FromDays(7), json: json, redirected: redirected), (_, _) => true);

        (await act.Should().ThrowAsync<PpdsException>()).Which.ErrorCode.Should().Be(ErrorCodes.Validation.InvalidValue);
        traces.CountFilters.Should().BeEmpty();
        traces.DeleteCutoffs.Should().BeEmpty();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-7 * 24 * 60)]
    public async Task Flow_ZeroOrNegativeAge_IsRefusedBeforeCounting(int minutes)
    {
        var traces = new FlowFake(count: 4);

        var act = () => PluginTraceOlderThanDeletion.RunAsync(
            traces.Object, new TestClock(Now), Request(TimeSpan.FromMinutes(minutes), force: true), (_, _) => true);

        (await act.Should().ThrowAsync<PpdsException>()).Which.ErrorCode.Should().Be(ErrorCodes.Validation.InvalidValue);
        traces.CountFilters.Should().BeEmpty();
        traces.DeleteCutoffs.Should().BeEmpty();
    }

    [Theory]
    [InlineData(true, false, true, true)]     // --force
    [InlineData(false, true, true, true)]     // --dry-run deletes nothing
    [InlineData(false, false, false, false)]  // a terminal: the prompt shows the cut-off and count
    public void ConfirmationProblem_WithForceDryRunOrATerminal_IsNone(bool force, bool dryRun, bool json, bool redirected)
    {
        PluginTraceOlderThanDeletion.ConfirmationProblem(force, dryRun, json, redirected).Should().BeNull();
    }

    // ─── The command's execute path (no connection is made) ─────────────────────

    [Theory]
    [InlineData(true, false)]   // JSON output, no --force
    [InlineData(false, true)]   // redirected input, no --force
    public async Task Command_WithoutAWayToConfirm_IsRefusedBeforeConnecting(bool json, bool redirected)
    {
        var connects = 0;

        var exit = await DeleteCommand.ExecuteAsync(
            traceId: null, ids: null, olderThan: "7d", all: false, dryRun: false, force: false,
            profile: null, environment: null, Options(json), CancellationToken.None,
            connect: _ => { connects++; throw new InvalidOperationException("must not connect"); },
            clock: new TestClock(Now), inputRedirected: redirected);

        exit.Should().Be(ExitCodes.InvalidArguments);
        connects.Should().Be(0);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-7d")]
    [InlineData("7")]
    public async Task Command_InvalidAge_IsRefusedBeforeConnecting(string age)
    {
        var connects = 0;

        var exit = await DeleteCommand.ExecuteAsync(
            traceId: null, ids: null, olderThan: age, all: false, dryRun: false, force: true,
            profile: null, environment: null, Options(json: true), CancellationToken.None,
            connect: _ => { connects++; throw new InvalidOperationException("must not connect"); },
            clock: new TestClock(Now), inputRedirected: true);

        exit.Should().Be(ExitCodes.InvalidArguments);
        connects.Should().Be(0);
    }

    [Fact]
    public async Task Command_ForcedRun_DeletesWithTheInjectedClocksCutoff()
    {
        var traces = new FlowFake(count: 3);
        var connects = 0;

        var exit = await DeleteCommand.ExecuteAsync(
            traceId: null, ids: null, olderThan: "7d", all: false, dryRun: false, force: true,
            profile: null, environment: null, Options(json: true), CancellationToken.None,
            connect: _ =>
            {
                connects++;
                return Task.FromResult(new ServiceCollection().AddSingleton(traces.Object).BuildServiceProvider());
            },
            clock: new TestClock(Now, step: TimeSpan.FromSeconds(1), zone: PlusTen), inputRedirected: true);

        exit.Should().Be(ExitCodes.Success);
        connects.Should().Be(1);
        traces.CountFilters.Should().ContainSingle().Which.CreatedBefore.Should().Be(Now.AddDays(-7));
        traces.DeleteCutoffs.Should().ContainSingle().Which.Should().Be(Now.AddDays(-7));
    }

    // ─── The daemon's days value ────────────────────────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(36501)]
    [InlineData(int.MaxValue)]
    public async Task Daemon_OlderThanDaysOutOfRange_IsRefusedWithACode_BeforeConnecting(int days)
    {
        var poolManager = new Mock<IDaemonConnectionPoolManager>(MockBehavior.Strict);
        using var authServices = new ServiceCollection().BuildServiceProvider();
        var handler = new RpcMethodHandler(poolManager.Object, authServices);

        var act = () => handler.PluginTracesDeleteAsync(olderThanDays: days);

        (await act.Should().ThrowAsync<RpcException>()).Which.StructuredErrorCode
            .Should().Be(ErrorCodes.Validation.InvalidValue);
        poolManager.Invocations.Should().BeEmpty();
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    private static readonly TimeZoneInfo PlusTen =
        TimeZoneInfo.CreateCustomTimeZone("test+10", TimeSpan.FromHours(10), "test+10", "test+10");

    private static OlderThanDeletionRequest Request(
        TimeSpan age, bool force = false, bool dryRun = false, bool json = false, bool redirected = false)
        => new(age, dryRun, force, json, redirected);

    private static GlobalOptionValues Options(bool json)
        => new() { OutputFormat = json ? OutputFormat.Json : OutputFormat.Text };

    private static DateTime[] TenTracesFromTheLastHours()
        => Enumerable.Range(0, 10).Select(i => Now.AddMinutes(-30.0 * i)).ToArray();

    /// <summary>A clock fixed at a start time, optionally moving on by a step at every read.</summary>
    private sealed class TestClock : TimeProvider
    {
        private readonly TimeSpan _step;
        private readonly TimeZoneInfo _zone;
        private DateTimeOffset _now;

        public TestClock(DateTime startUtc, TimeSpan? step = null, TimeZoneInfo? zone = null)
        {
            _now = new DateTimeOffset(startUtc, TimeSpan.Zero);
            _step = step ?? TimeSpan.Zero;
            _zone = zone ?? TimeZoneInfo.Utc;
        }

        public override TimeZoneInfo LocalTimeZone => _zone;

        public override DateTimeOffset GetUtcNow()
        {
            var now = _now;
            _now += _step;
            return now;
        }
    }

    /// <summary>A strict trace service that records the count filters and delete cut-offs.</summary>
    private sealed class FlowFake
    {
        public FlowFake(int count)
        {
            Mock = new Mock<IPluginTraceService>(MockBehavior.Strict);
            Mock.Setup(t => t.CountAsync(It.IsAny<PluginTraceFilter?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((PluginTraceFilter? f, CancellationToken _) => { CountFilters.Add(f!); return count; });
            Mock.Setup(t => t.DeleteCreatedBeforeAsync(It.IsAny<DateTime>(), It.IsAny<IProgress<int>?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((DateTime cutoff, IProgress<int>? _, CancellationToken _) =>
                {
                    OnDelete?.Invoke();
                    DeleteCutoffs.Add(cutoff);
                    return count;
                });
        }

        public Mock<IPluginTraceService> Mock { get; }
        public IPluginTraceService Object => Mock.Object;
        public List<PluginTraceFilter> CountFilters { get; } = new();
        public List<DateTime> DeleteCutoffs { get; } = new();
        public Action? OnDelete { get; set; }
    }

    private sealed record FetchCondition(string Attribute, string Operator, string Value);

    /// <summary>A recording fake of the trace table behind a pooled client.</summary>
    private sealed class TraceStore
    {
        private readonly object _gate = new();
        private readonly Dictionary<Guid, DateTime> _rows;

        public TraceStore(IEnumerable<DateTime> createdOn)
        {
            _rows = createdOn.ToDictionary(_ => Guid.NewGuid(), c => c);
        }

        public int ClientsTaken;
        public int ListPages;
        public List<Guid> Deleted { get; } = new();
        public List<ConditionExpression> CreatedOnConditions { get; } = new();
        public List<FetchCondition> FetchConditions { get; } = new();

        public int Remaining
        {
            get { lock (_gate) return _rows.Count; }
        }

        public PluginTraceService CreateService(TimeProvider clock)
        {
            var client = new Mock<IPooledClient>(MockBehavior.Loose);
            client
                .Setup(c => c.RetrieveMultipleAsync(It.IsAny<QueryBase>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((QueryBase q, CancellationToken _) => Retrieve(q));
            client
                .Setup(c => c.DeleteAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .Returns((string _, Guid id, CancellationToken _) =>
                {
                    lock (_gate)
                    {
                        if (_rows.Remove(id)) Deleted.Add(id);
                    }
                    return Task.CompletedTask;
                });

            var pool = new Mock<IDataverseConnectionPool>(MockBehavior.Loose);
            pool
                .Setup(p => p.GetClientAsync(It.IsAny<DataverseClientOptions?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() =>
                {
                    Interlocked.Increment(ref ClientsTaken);
                    return client.Object;
                });
            pool.Setup(p => p.GetTotalRecommendedParallelism()).Returns(8);

            return new PluginTraceService(pool.Object, new InactiveFakeShakedownGuard(), NullLogger<PluginTraceService>.Instance, clock);
        }

        private EntityCollection Retrieve(QueryBase query)
        {
            lock (_gate)
            {
                if (query is FetchExpression fetch)
                {
                    var conditions = XDocument.Parse(fetch.Query).Descendants("condition")
                        .Select(c => new FetchCondition(
                            (string?)c.Attribute("attribute") ?? "",
                            (string?)c.Attribute("operator") ?? "",
                            (string?)c.Attribute("value") ?? ""))
                        .ToList();
                    FetchConditions.AddRange(conditions);
                    var count = _rows.Values.Count(v => conditions
                        .Where(c => c.Attribute == "createdon")
                        .All(c => Matches(v, c.Operator, DateTime.Parse(c.Value, null,
                            System.Globalization.DateTimeStyles.RoundtripKind))));
                    var row = new Entity("plugintracelog");
                    row["count"] = new AliasedValue("plugintracelog", "plugintracelogid", count);
                    return new EntityCollection(new List<Entity> { row });
                }

                ListPages++;
                var expression = (QueryExpression)query;
                var dateConditions = expression.Criteria.Conditions
                    .Where(c => c.AttributeName == "createdon").ToList();
                CreatedOnConditions.AddRange(dateConditions);
                var top = expression.TopCount ?? int.MaxValue;
                var matches = _rows
                    .Where(r => dateConditions.All(c => Matches(r.Value, c.Operator, (DateTime)c.Values[0])))
                    .Take(top)
                    .Select(r => new Entity("plugintracelog", r.Key) { ["createdon"] = r.Value })
                    .ToList();
                return new EntityCollection(matches);
            }
        }

        // `on-or-before` / `on-or-after` are compared by date only (the reading their documented
        // wording allows); `lt` / `ge` compare the full value.
        private static bool Matches(DateTime value, ConditionOperator op, DateTime compared) => op switch
        {
            ConditionOperator.LessThan => value < compared,
            ConditionOperator.GreaterEqual => value >= compared,
            ConditionOperator.OnOrBefore => value.Date <= compared.Date,
            ConditionOperator.OnOrAfter => value.Date >= compared.Date,
            _ => throw new NotSupportedException(op.ToString()),
        };

        private static bool Matches(DateTime value, string op, DateTime compared) => op switch
        {
            "lt" => Matches(value, ConditionOperator.LessThan, compared),
            "ge" => Matches(value, ConditionOperator.GreaterEqual, compared),
            "on-or-before" => Matches(value, ConditionOperator.OnOrBefore, compared),
            "on-or-after" => Matches(value, ConditionOperator.OnOrAfter, compared),
            _ => throw new NotSupportedException(op),
        };
    }
}
