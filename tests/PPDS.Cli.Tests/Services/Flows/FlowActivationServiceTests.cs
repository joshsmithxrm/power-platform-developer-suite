using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Moq;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services.ConnectionReferences;
using PPDS.Cli.Services.Environment;
using PPDS.Cli.Services.Flows;
using PPDS.Dataverse.Models;
using PPDS.Dataverse.Pooling;
using Xunit;

namespace PPDS.Cli.Tests.Services.Flows;

public sealed class FlowActivationServiceTests
{
    private static readonly Guid FirstId = new("00000000-0000-0000-0000-000000000001");
    private static readonly Guid SecondId = new("00000000-0000-0000-0000-000000000002");
    private static readonly Guid ThirdId = new("00000000-0000-0000-0000-000000000003");
    private const string Target = "https://test.crm.dynamics.com";
    private static FlowInfo Flow(Guid id, FlowState state = FlowState.Draft) => new()
    {
        Id = id, UniqueName = $"test_{id:N}", State = state, Category = FlowCategory.ModernFlow,
        DefinitionType = 1, ConnectionReferencesVerified = true, ConnectionReferenceLogicalNames = []
    };
    private static FlowActivationRequest Batch(FlowState target = FlowState.Activated) => new()
    {
        All = true, Solution = "test_solution", TargetState = target, ConfirmEnvironment = Target
    };

    [Fact]
    public async Task Plan_FiltersDefinitionsReportsExclusionsAndNeverWritesOrConfirms()
    {
        var f = new Fixture(Flow(FirstId), Flow(SecondId) with { Category = FlowCategory.DesktopFlow },
            Flow(ThirdId) with { DefinitionType = 3 });
        var result = await f.Service.PlanAsync(Batch());
        Assert.True(result.Success);
        Assert.True(result.DryRun);
        Assert.Equal(Target, result.EnvironmentUrl);
        Assert.Equal(3, result.TotalCount);
        Assert.Single(result.Flows);
        Assert.Equal(2, result.ExcludedCount);
        Assert.Equal(64, result.PlanHash.Length);
        Assert.Empty(f.Writes);
        f.Guard.Verify(g => g.EnsureConfirmedAsync(It.IsAny<string>(), It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(FlowState.Activated, 1, 2)]
    [InlineData(FlowState.Draft, 0, 1)]
    public async Task Execute_WritesStateStatusAndVerifiesReadback(FlowState target, int state, int status)
    {
        var f = new Fixture(Flow(FirstId, target == FlowState.Draft ? FlowState.Activated : FlowState.Draft));
        var result = await f.Service.ExecuteAsync(Batch(target));
        Assert.True(result.Success);
        var write = Assert.Single(f.Writes);
        Assert.Equal(FirstId, write.Id);
        Assert.Equal(state, write.GetAttributeValue<OptionSetValue>("statecode").Value);
        Assert.Equal(status, write.GetAttributeValue<OptionSetValue>("statuscode").Value);
        Assert.Equal("succeeded", result.Flows[0].Status);
        Assert.Equal(target, result.Flows[0].ObservedState);
        f.Guard.Verify(g => g.EnsureConfirmedAsync("flows.state-change", Target, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Execute_AlreadyMatchingIsUnchangedAndDoesNotWrite()
    {
        var f = new Fixture(Flow(FirstId, FlowState.Activated));
        var result = await f.Service.ExecuteAsync(Batch() with { ConfirmEnvironment = null });
        Assert.True(result.Success);
        Assert.Equal("unchanged", Assert.Single(result.Flows).Status);
        Assert.Empty(f.Writes);
    }

    [Fact]
    public async Task Execute_ReadOnlyRejectsActualEntryPointEvenForNoopButAllowsDryRun()
    {
        var f = new Fixture(Flow(FirstId, FlowState.Activated));
        f.Guard.SetupGet(g => g.Context).Returns(new EnvironmentConfirmationContext("s", "p", Target, true));
        Assert.True((await f.Service.PlanAsync(Batch())).Success);
        Assert.Equal("Flow.ReadOnly",
            (await Assert.ThrowsAsync<PpdsException>(() => f.Service.ExecuteAsync(Batch()))).ErrorCode);
        Assert.Empty(f.Writes);
    }

    [Fact]
    public async Task Execute_GuardFailureStopsBeforeWrite()
    {
        var f = new Fixture(Flow(FirstId));
        f.Guard.Setup(g => g.EnsureConfirmedAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new PpdsException(EnvironmentConfirmationErrorCodes.Required, "Confirm target"));
        Assert.Equal(EnvironmentConfirmationErrorCodes.Required,
            (await Assert.ThrowsAsync<PpdsException>(() => f.Service.ExecuteAsync(Batch()))).ErrorCode);
        Assert.Empty(f.Writes);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("inactive")]
    [InlineData("unbound")]
    [InlineData("state-unknown")]
    public async Task Plan_KnownReferenceBlockerPreventsEntireBatch(string expected)
    {
        var f = new Fixture(Flow(FirstId), Flow(SecondId) with { ConnectionReferenceLogicalNames = ["test_reference"] });
        if (expected != "missing")
            f.References["test_reference"] = new ConnectionReferenceInfo
            {
                Id = Guid.NewGuid(), LogicalName = "test_reference",
                IsActive = expected == "state-unknown" ? null : expected != "inactive",
                ConnectionId = expected == "unbound" ? null : "bound-id"
            };
        var result = await f.Service.ExecuteAsync(Batch());
        Assert.False(result.Success);
        Assert.Equal("Flow.PreflightBlocked", result.ErrorCode);
        Assert.Equal("not-attempted", result.Flows[0].Status);
        Assert.Equal("blocked", result.Flows[1].Status);
        Assert.Equal(expected, Assert.Single(result.Flows[1].References).Status);
        Assert.Empty(f.Writes);
    }

    [Fact]
    public async Task Plan_UnparseableDependenciesBlockActivationButNotDisable()
    {
        var f = new Fixture(Flow(FirstId) with { ConnectionReferencesVerified = false });
        Assert.False((await f.Service.PlanAsync(Batch())).Success);
        f.Flows[FirstId] = f.Flows[FirstId] with { State = FlowState.Activated };
        Assert.True((await f.Service.ExecuteAsync(Batch(FlowState.Draft))).Success);
        Assert.Single(f.Writes);
    }

    [Fact]
    public async Task Plan_UnreadableReferenceIsPerFlowBlockerAndRetainsEntirePlan()
    {
        var f = new Fixture(Flow(FirstId) with { ConnectionReferenceLogicalNames = ["test_unreadable"] },
            Flow(SecondId), Flow(ThirdId));
        f.OnReferenceRead = (_, _) => Task.FromException<ConnectionReferenceInfo?>(
            new InvalidOperationException("clientSecret=must-not-appear"));
        var result = await f.Service.ExecuteAsync(Batch());
        Assert.Equal(3, result.EligibleCount);
        Assert.Equal("Flow.DependenciesUnverified", result.Flows[0].ErrorCode);
        Assert.Equal("unreadable", Assert.Single(result.Flows[0].References).Status);
        Assert.Equal(new[] { "blocked", "not-attempted", "not-attempted" }, result.Flows.Select(x => x.Status));
        Assert.DoesNotContain("must-not-appear", System.Text.Json.JsonSerializer.Serialize(result));
        Assert.Empty(f.Writes);
    }

    [Fact]
    public async Task Plan_BatchLimitReportsFullCountWithoutTruncationOrWrites()
    {
        var f = new Fixture(Flow(FirstId), Flow(SecondId));
        var result = await f.Service.ExecuteAsync(Batch() with { MaxFlows = 1 });
        Assert.Equal("Flow.BatchLimitExceeded", result.ErrorCode);
        Assert.Equal(2, result.EligibleCount);
        Assert.All(result.Flows, x => Assert.Equal("not-attempted", x.Status));
        Assert.Empty(f.Writes);
    }

    [Fact]
    public async Task Execute_ExpectedPlanMismatchPreventsWrites()
    {
        var f = new Fixture(Flow(FirstId));
        var plan = await f.Service.PlanAsync(Batch());
        Assert.Equal(plan.PlanHash, (await f.Service.PlanAsync(Batch())).PlanHash);
        f.Flows[SecondId] = Flow(SecondId);
        var result = await f.Service.ExecuteAsync(Batch() with { ExpectedPlanHash = plan.PlanHash });
        Assert.Equal("Flow.PlanChanged", result.ErrorCode);
        Assert.Empty(f.Writes);
    }

    [Fact]
    public async Task Execute_RuntimeFailurePreservesEarlierSuccessAndStopsRemainingWrites()
    {
        var f = new Fixture(Flow(FirstId), Flow(SecondId), Flow(ThirdId));
        f.OnWrite = (entity, _) => entity.Id == SecondId
            ? Task.FromException(new IOException("transport lost")) : Task.CompletedTask;
        var result = await f.Service.ExecuteAsync(Batch());
        Assert.False(result.Success);
        Assert.Equal(new[] { "succeeded", "unknown", "not-attempted" }, result.Flows.Select(x => x.Status));
        Assert.Equal(new[] { FirstId, SecondId }, f.Writes.Select(x => x.Id));
    }

    [Fact]
    public async Task Execute_PreconditionChangeStopsBeforeSubmission()
    {
        var f = new Fixture(Flow(FirstId), Flow(SecondId));
        f.OnRead = (id, _, _) => Task.FromResult<FlowInfo?>(f.Flows[id] with { State = FlowState.Suspended });
        var result = await f.Service.ExecuteAsync(Batch());
        Assert.Equal("failed", result.Flows[0].Status);
        Assert.Equal("Flow.PreconditionChanged", result.Flows[0].ErrorCode);
        Assert.Equal("not-attempted", result.Flows[1].Status);
        Assert.Empty(f.Writes);
    }

    [Fact]
    public async Task Execute_CancellationAfterSubmissionIsUnknownAndDoesNotWriteNextFlow()
    {
        using var cts = new CancellationTokenSource();
        var f = new Fixture(Flow(FirstId), Flow(SecondId));
        f.OnWrite = (_, _) => { cts.Cancel(); return Task.FromCanceled(cts.Token); };
        var result = await f.Service.ExecuteAsync(Batch(), cancellationToken: cts.Token);
        Assert.Equal(new[] { "unknown", "not-attempted" }, result.Flows.Select(x => x.Status));
        Assert.Single(f.Writes);
    }

    [Fact]
    public async Task Execute_ServerRejectionIsFailedAndPreservesFaultReason()
    {
        var f = new Fixture(Flow(FirstId));
        f.OnWrite = (_, _) => Task.FromException(new System.ServiceModel.FaultException<OrganizationServiceFault>(
            new OrganizationServiceFault { ErrorCode = -1, Message = "Connection permission denied" },
            new System.ServiceModel.FaultReason("Connection permission denied")));
        var result = await f.Service.ExecuteAsync(Batch());
        Assert.Equal("failed", result.Flows[0].Status);
        Assert.Contains("Connection permission denied", result.Flows[0].Message);
        Assert.Single(f.Writes);
    }

    [Fact]
    public async Task Execute_AcceptedWriteFollowedByReadFaultIsUnknownNotRejected()
    {
        var f = new Fixture(Flow(FirstId), Flow(SecondId));
        f.OnRead = (id, count, _) => count == 2
            ? Task.FromException<FlowInfo?>(new System.ServiceModel.FaultException<OrganizationServiceFault>(
                new OrganizationServiceFault { ErrorCode = -1, Message = "Read unavailable" },
                new System.ServiceModel.FaultReason("Read unavailable")))
            : Task.FromResult<FlowInfo?>(f.Flows[id]);
        var result = await f.Service.ExecuteAsync(Batch());
        Assert.Equal(FlowState.Activated, f.Flows[FirstId].State);
        Assert.Equal("unknown", result.Flows[0].Status);
        Assert.Equal("verification", result.Flows[0].Phase);
        Assert.DoesNotContain("rejected", result.Flows[0].Message);
        Assert.Equal("not-attempted", result.Flows[1].Status);
        Assert.Single(f.Writes);
    }

    [Fact]
    public async Task Plan_SingleFlowMembershipAndSolutionAreRequired()
    {
        var f = new Fixture(Flow(FirstId)) { MembershipExists = false };
        var request = Batch() with { All = false, Id = FirstId };
        Assert.Equal("Flow.SolutionMembershipRequired",
            (await Assert.ThrowsAsync<PpdsException>(() => f.Service.PlanAsync(request))).ErrorCode);
        f.SolutionExists = false;
        await Assert.ThrowsAsync<PpdsNotFoundException>(() => f.Service.PlanAsync(Batch()));
        Assert.Empty(f.Writes);
    }

    [Fact]
    public async Task Plan_EmptyExistingSolutionSucceedsWithExplicitZeroCount()
    {
        var result = await new Fixture().Service.PlanAsync(Batch());
        Assert.True(result.Success);
        Assert.Equal(0, result.TotalCount);
    }

    [Fact]
    public async Task Execute_VerificationDeadlineCancelsBlockedReadAndReportsUnknown()
    {
        var time = new ManualTimeProvider();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var f = new Fixture(time, Flow(FirstId), Flow(SecondId));
        f.OnRead = async (id, count, ct) =>
        {
            if (count == 2)
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            return f.Flows[id];
        };
        var pending = f.Service.ExecuteAsync(Batch() with { VerificationTimeout = TimeSpan.FromSeconds(5) });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        time.Advance(TimeSpan.FromSeconds(5));
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("Flow.VerificationTimeout", result.Flows[0].ErrorCode);
        Assert.Equal(new[] { "unknown", "not-attempted" }, result.Flows.Select(x => x.Status));
        Assert.Single(f.Writes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Execute_LateReadCannotWinDeadlineOrCancellation(bool cancel)
    {
        var time = new ManualTimeProvider();
        using var cts = new CancellationTokenSource();
        var f = new Fixture(time, Flow(FirstId), Flow(SecondId));
        f.OnRead = (id, count, _) =>
        {
            if (count == 2)
            {
                if (cancel) cts.Cancel();
                else time.Advance(TimeSpan.FromSeconds(5));
            }
            // Deliberately model an SDK operation that ignores cancellation.
            return Task.FromResult<FlowInfo?>(f.Flows[id]);
        };
        var result = await f.Service.ExecuteAsync(Batch() with { VerificationTimeout = TimeSpan.FromSeconds(5) },
            cancellationToken: cts.Token);
        Assert.Equal("unknown", result.Flows[0].Status);
        Assert.Equal(cancel ? "Flow.Cancelled" : "Flow.VerificationTimeout", result.Flows[0].ErrorCode);
        Assert.Equal("not-attempted", result.Flows[1].Status);
        Assert.Single(f.Writes);
    }

    private sealed class Fixture
    {
        public Dictionary<Guid, FlowInfo> Flows { get; }
        public Dictionary<string, ConnectionReferenceInfo> References { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<Entity> Writes { get; } = [];
        public Mock<IEnvironmentConfirmationService> Guard { get; } = new();
        public FlowActivationService Service { get; }
        public bool SolutionExists { get; set; } = true;
        public bool MembershipExists { get; set; } = true;
        public Func<Entity, CancellationToken, Task>? OnWrite { get; set; }
        public Func<Guid, int, CancellationToken, Task<FlowInfo?>>? OnRead { get; set; }
        public Func<string, CancellationToken, Task<ConnectionReferenceInfo?>>? OnReferenceRead { get; set; }
        private int _reads;

        public Fixture(params FlowInfo[] flows) : this(TimeProvider.System, flows) { }

        public Fixture(TimeProvider timeProvider, params FlowInfo[] flows)
        {
            Flows = flows.ToDictionary(f => f.Id);
            var reader = new Mock<IFlowService>();
            reader.Setup(s => s.ListAsync(It.IsAny<string?>(), It.IsAny<FlowState?>(), true, It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new ListResult<FlowInfo> { Items = Flows.Values.OrderBy(f => f.Id).ToList(), TotalCount = Flows.Count });
            reader.Setup(s => s.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .Returns((Guid id, CancellationToken ct) => OnRead?.Invoke(id, ++_reads, ct)
                    ?? Task.FromResult(Flows.GetValueOrDefault(id)));
            reader.Setup(s => s.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string name, CancellationToken _) => Flows.Values.SingleOrDefault(f => f.UniqueName == name));
            var references = new Mock<IConnectionReferenceService>();
            references.Setup(s => s.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns((string name, CancellationToken ct) => OnReferenceRead?.Invoke(name, ct)
                    ?? Task.FromResult(References.GetValueOrDefault(name)));
            Guard.SetupGet(g => g.Context).Returns(new EnvironmentConfirmationContext("session", "identity", Target));
            Guard.Setup(g => g.EnsureConfirmedAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            var client = new Mock<IPooledClient>();
            client.Setup(c => c.DisposeAsync()).Returns(ValueTask.CompletedTask);
            client.Setup(c => c.RetrieveMultipleAsync(It.IsAny<QueryBase>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((QueryBase query, CancellationToken _) =>
                {
                    var name = ((QueryExpression)query).EntityName;
                    var exists = name == "solution" ? SolutionExists : MembershipExists;
                    return new EntityCollection(exists ? [new Entity(name, Guid.NewGuid())] : []);
                });
            client.Setup(c => c.UpdateAsync(It.IsAny<Entity>(), It.IsAny<CancellationToken>()))
                .Returns(async (Entity entity, CancellationToken ct) =>
                {
                    Writes.Add(entity);
                    if (OnWrite is not null) await OnWrite(entity, ct);
                    Flows[entity.Id] = Flows[entity.Id] with
                    { State = (FlowState)entity.GetAttributeValue<OptionSetValue>("statecode").Value };
                });
            var pool = new Mock<IDataverseConnectionPool>();
            pool.Setup(p => p.GetClientAsync(null, null, It.IsAny<CancellationToken>())).ReturnsAsync(client.Object);
            Service = new FlowActivationService(pool.Object, reader.Object, references.Object, Guard.Object,
                NullLogger<FlowActivationService>.Instance, timeProvider);
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _ticks;
        private readonly List<ManualTimer> _timers = [];
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(_ticks);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state, dueTime);
            _timers.Add(timer);
            return timer;
        }
        public void Advance(TimeSpan elapsed)
        {
            _ticks += elapsed.Ticks;
            foreach (var timer in _timers.ToArray()) timer.FireIfDue(_ticks);
        }
        private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state,
            TimeSpan dueTime) : ITimer
        {
            private long _due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : owner._ticks + dueTime.Ticks;
            public bool Change(TimeSpan due, TimeSpan period)
            {
                _due = due == Timeout.InfiniteTimeSpan ? long.MaxValue : owner._ticks + due.Ticks;
                return true;
            }
            public void Dispose() => _due = long.MaxValue;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
            public void FireIfDue(long ticks)
            {
                if (_due > ticks) return;
                Dispose();
                callback(state);
            }
        }
    }
}
