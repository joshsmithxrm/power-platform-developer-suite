using System.Text.Json;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Moq;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Cli.Services.Metadata.Authoring;
using PPDS.Cli.Tests.Services.Shared;
using PPDS.Dataverse.Metadata;
using PPDS.Dataverse.Metadata.Authoring;
using PPDS.Dataverse.Pooling;
using Xunit;
using Xunit.Abstractions;

namespace PPDS.Cli.Tests.Services.Metadata.Authoring;

[Trait("Category", "Unit")]
public sealed partial class TableUpdateLeaseTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("AC", "AC-93")]
    public async Task SingleSlot_UpdateAndPublish_ReturnsMutationLeaseBeforeFollowup()
    {
        using var h = new Harness(publish: true);
        var operation = h.Invoke();
        try
        {
            await operation;
            Assert.Equal("Changed", h.StoredLabel);
            Assert.Equal(new[] { "read", "update", "confirm", "publish" }, h.SuccessfulSdk);
            Assert.Equal(new[]
            {
                "checkout:read", "acquired:1", "sdk:read:1", "success:read:1", "dispose:1",
                "checkout:update", "acquired:2", "sdk:update:2", "success:update:2", "dispose:2",
                "cache:list", "cache:entity", "checkout:confirm", "acquired:3", "sdk:confirm:3", "success:confirm:3", "dispose:3",
                "checkout:publish", "acquired:4", "sdk:publish:4", "success:publish:4", "dispose:4", "info"
            }, h.Events.Select(e => e.Action));
            h.AssertSequentialOwnership();
        }
        finally { output.WriteLine(h.Snapshot(operation)); }
    }

    [Fact]
    [Trait("AC", "AC-93")]
    public async Task UpdateWithoutPublish_ReturnsLeaseBeforeCacheAndSuccess()
    {
        using var h = new Harness(publish: false);
        var operation = h.Invoke();
        try
        {
            await operation;
            Assert.Equal(new[] { "read", "update", "confirm" }, h.SuccessfulSdk);
            Assert.Equal(new[]
            {
                "checkout:read", "acquired:1", "sdk:read:1", "success:read:1", "dispose:1",
                "checkout:update", "acquired:2", "sdk:update:2", "success:update:2", "dispose:2",
                "cache:list", "cache:entity", "checkout:confirm", "acquired:3", "sdk:confirm:3", "success:confirm:3", "dispose:3", "info"
            }, h.Events.Select(e => e.Action));
            h.AssertSequentialOwnership();
        }
        finally { output.WriteLine(h.Snapshot(operation)); }
    }

    [Fact]
    [Trait("AC", "AC-93")]
    [Trait("AC", "AC-96")]
    public async Task TwoSlotControl_CompletesSameUpdateAndPublication()
    {
        using var h = new Harness(publish: true, capacity: 2);
        var operation = h.Invoke();
        try
        {
            await operation;
            Assert.Equal("Changed", h.StoredLabel);
            Assert.Equal(new[] { "read", "update", "confirm", "publish" }, h.SuccessfulSdk);
            Assert.Equal(new[] { "list", "entity" }, h.Cache.Select(e => e.Name));
            Assert.Single(h.Infos);
            h.AssertReturned();
        }
        finally { output.WriteLine(h.Snapshot(operation)); }
    }

    public static IEnumerable<object[]> Failures()
    {
        foreach (var boundary in new[] { "update-checkout", "update-sdk", "publish-checkout", "publish-sdk" })
        {
            foreach (var kind in new[] { "ordinary", "coded", "cancel", "timeout" })
            {
                yield return [boundary, kind];
            }
        }
        yield return ["update-checkout", "pool"];
        yield return ["publish-checkout", "pool"];
    }

    [Theory]
    [MemberData(nameof(Failures))]
    [Trait("AC", "AC-94")]
    public async Task FailureAtEachBoundary_PreservesStageContractAndStopsLaterWork(string boundary, string kind)
    {
        // Two slots let the original code reach each injected boundary without incidental saturation.
        using var h = new Harness(publish: true, capacity: 2, failureBoundary: boundary, failureKind: kind);
        var operation = h.Invoke();
        try
        {
            var error = await Record.ExceptionAsync(() => operation);
            Assert.NotNull(error);
            if (boundary.EndsWith("checkout", StringComparison.Ordinal) && kind is not ("coded" or "cancel"))
                Assert.Same(h.Injected, Assert.IsAssignableFrom<PpdsException>(error).InnerException);
            else Assert.Same(h.Injected, error);
            Assert.Equal(kind == "cancel", operation.IsCanceled);
            Assert.Equal(kind != "cancel", operation.IsFaulted);
            if (kind == "cancel") Assert.Equal(h.Token, Assert.IsType<OperationCanceledException>(error).CancellationToken);
            var expectedCode = kind switch
            {
                "coded" => "Synthetic.Coded",
                "cancel" => "Operation.Cancelled",
                "timeout" or "pool" => "Connection.Timeout",
                _ => "Operation.Internal"
            };
            AssertCli(error, expectedCode, kind is "timeout" or "pool" ? 4 : 2);
            var afterUpdate = boundary.StartsWith("publish", StringComparison.Ordinal);
            Assert.Equal(afterUpdate ? "Changed" : "Existing", h.StoredLabel);
            Assert.Equal(afterUpdate ? new[] { "list", "entity" } : [], h.Cache.Select(e => e.Name));
            Assert.Equal(boundary switch
            {
                "update-checkout" => new[] { "read" },
                "publish-checkout" => ["read", "update", "confirm"],
                "publish-sdk" => ["read", "update", "confirm", "publish"],
                _ => ["read", "update"]
            }, h.AttemptedSdk);
            Assert.Equal(afterUpdate ? new[] { "read", "update", "confirm" } : ["read"], h.SuccessfulSdk);
            Assert.Empty(h.Infos);
            Assert.All(h.AttemptedSdk.GroupBy(s => s), group => Assert.Single(group));
            h.AssertReturned();
        }
        finally { output.WriteLine(h.Snapshot(operation)); }
    }

    /// <summary>
    /// The label read-back after a committed table update: a failed read (checkout or SDK) or labels that were not
    /// stored are WRITE_NOT_CONFIRMED naming what was written; publication is not attempted; the cause is kept.
    /// </summary>
    [Theory]
    [InlineData("confirm-checkout", "ordinary")]
    [InlineData("confirm-checkout", "timeout")]
    [InlineData("confirm-sdk", "ordinary")]
    [InlineData("confirm-sdk", "timeout")]
    [InlineData("confirm-sdk", "auth")]
    [InlineData("not-stored", null)]
    [Trait("AC", "AC-148")]
    public async Task LabelReadBackFailure_IsWriteNotConfirmed_AndSkipsPublication(string boundary, string? kind)
    {
        using var h = new Harness(publish: true, capacity: 2, failureBoundary: boundary == "not-stored" ? null : boundary, failureKind: kind);
        h.ConfirmStores = boundary != "not-stored";
        var operation = h.Invoke();
        try
        {
            var error = Assert.IsType<PpdsException>(await Record.ExceptionAsync(() => operation));
            Assert.Equal(MetadataErrorCodes.WriteNotConfirmed, error.ErrorCode);
            Assert.StartsWith($"Table '{Harness.Table}' was updated.", error.Message);
            Assert.Contains("Publication (--publish) was not attempted.", error.Message);
            if (boundary == "not-stored")
                Assert.Contains("did not store its display name: requested 'Changed' (LCID 1033), stored 'Existing'", error.Message);
            else
                Assert.Contains(h.Injected, Chain(error)); // the read failure is kept as the cause
            if (kind == "auth") Assert.Equal(true, error.Context?["requiresReauthentication"]);
            Assert.Equal("Changed", h.StoredLabel);
            Assert.DoesNotContain("publish", h.AttemptedSdk);
            Assert.Empty(h.Infos);
            h.AssertReturned();
        }
        finally { output.WriteLine(h.Snapshot(operation)); }
    }

    private static IEnumerable<Exception> Chain(Exception error)
    {
        for (var current = error.InnerException; current != null; current = current.InnerException) yield return current;
    }

    public static IEnumerable<object[]> ReadFailures()
    {
        foreach (var kind in new[] { "ordinary", "coded", "cancel", "timeout", "auth", "connection" })
            yield return ["read-sdk", kind];
        foreach (var kind in new[] { "ordinary", "coded", "cancel", "timeout", "pool", "auth" })
            yield return ["read-checkout", kind];
    }

    /// <summary>
    /// AC-147: the pre-write table read keeps the classification the update stage has: checkout failures follow the
    /// shared acquisition rule, SDK timeouts stay Connection.Timeout (exit 4), auth failures stay auth errors with the
    /// re-authentication signal, coded and cancellation failures propagate as themselves; nothing is written.
    /// </summary>
    [Theory]
    [MemberData(nameof(ReadFailures))]
    [Trait("AC", "AC-147")]
    public async Task LabelReadFailure_KeepsItsClassification_AndStopsBeforeTheUpdate(string boundary, string kind)
    {
        using var h = new Harness(publish: true, capacity: 2, failureBoundary: boundary, failureKind: kind);
        var operation = h.Invoke();
        try
        {
            var error = await Record.ExceptionAsync(() => operation);
            Assert.NotNull(error);
            if (kind is "coded" or "cancel") Assert.Same(h.Injected, error);
            else Assert.Same(h.Injected, Assert.IsAssignableFrom<PpdsException>(error).InnerException);
            Assert.Equal(kind == "cancel", operation.IsCanceled);
            var (expectedCode, expectedExit) = (boundary, kind) switch
            {
                (_, "coded") => ("Synthetic.Coded", 2),
                (_, "cancel") => ("Operation.Cancelled", 2),
                (_, "timeout" or "pool") => ("Connection.Timeout", 4),
                (_, "auth") => (ExceptionMapper.Map(h.Injected!).Code, 5),
                (_, "connection") => (ExceptionMapper.Map(h.Injected!).Code, 4),
                ("read-sdk", _) => (MetadataErrorCodes.SdkOperationFailed, 1),
                _ => ("Operation.Internal", 2)
            };
            if (boundary == "read-sdk" && kind == "ordinary")
            {
                Assert.Equal(MetadataErrorCodes.SdkOperationFailed, Assert.IsType<PpdsException>(error).ErrorCode);
                Assert.Equal(ExceptionMapper.ToExitCode(new PpdsException(MetadataErrorCodes.SdkOperationFailed, "x")), ExceptionMapper.ToExitCode(error));
            }
            else AssertCli(error, expectedCode, expectedExit);
            if (kind == "auth")
                Assert.True(Assert.IsType<PpdsAuthException>(error).RequiresReauthentication);
            Assert.Equal("Existing", h.StoredLabel);
            Assert.Equal(boundary == "read-sdk" ? new[] { "read" } : [], h.AttemptedSdk);
            Assert.Empty(h.SuccessfulSdk);
            Assert.Empty(h.Cache);
            Assert.Empty(h.Infos);
            h.AssertReturned();
        }
        finally { output.WriteLine(h.Snapshot(operation)); }
    }

    /// <summary>AC-147: a read that returns no table metadata stops the update with a coded error.</summary>
    [Fact]
    [Trait("AC", "AC-147")]
    public async Task LabelReadWithoutTableMetadata_StopsBeforeTheUpdate()
    {
        using var h = new Harness(publish: false, capacity: 2, emptyRead: true);
        var error = await Assert.ThrowsAsync<PpdsException>(() => h.Invoke());
        Assert.Equal(MetadataErrorCodes.SdkOperationFailed, error.ErrorCode);
        Assert.Equal(new[] { "read" }, h.AttemptedSdk);
        Assert.Equal("Existing", h.StoredLabel);
        Assert.Empty(h.Cache);
        h.AssertReturned();
    }

    [Theory]
    [InlineData("cache-list")]
    [InlineData("cache-entity")]
    [Trait("AC", "AC-94")]
    public async Task CacheFailure_AfterReturnedMutationLease_PreservesCauseAndStopsPublication(string boundary)
    {
        using var h = new Harness(publish: true, failureBoundary: boundary, failureKind: "ordinary");
        var operation = h.Invoke();
        try
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => operation);
            Assert.Same(h.Injected, error);
            Assert.Equal("Changed", h.StoredLabel);
            Assert.Equal(new[] { "read", "update" }, h.AttemptedSdk);
            Assert.Equal(boundary == "cache-list" ? new[] { "list" } : ["list", "entity"], h.Cache.Select(e => e.Name));
            Assert.Empty(h.Infos);
            Assert.All(h.Cache, e => Assert.Equal(0, e.Active));
            h.AssertReturned();
        }
        finally { output.WriteLine(h.Snapshot(operation)); }
    }

    [Theory]
    [InlineData("dry-run")]
    [InlineData("missing-solution")]
    [InlineData("missing-entity")]
    [InlineData("guard")]
    [InlineData("null")]
    [Trait("AC", "AC-95")]
    public async Task DryRunValidationAndGuard_DoNotAcquire(string scenario)
    {
        using var h = new Harness(publish: true, blocked: scenario == "guard");
        h.Request.DryRun = scenario == "dry-run";
        if (scenario == "missing-solution") h.Request.SolutionUniqueName = "";
        if (scenario == "missing-entity") h.Request.EntityLogicalName = "";
        var operation = scenario == "null" ? h.Service.UpdateTableAsync(null!, h.Reporter, h.Token) : h.Invoke();
        var error = await Record.ExceptionAsync(() => operation);
        if (scenario == "dry-run") Assert.Null(error);
        else if (scenario == "guard") Assert.Equal(ErrorCodes.Safety.ShakedownActive, Assert.IsType<PpdsException>(error).ErrorCode);
        else if (scenario == "null") Assert.IsType<ArgumentNullException>(error);
        else Assert.Equal(MetadataErrorCodes.MissingRequiredField, Assert.IsType<MetadataValidationException>(error).ErrorCode);
        Assert.Equal(0, h.Checkouts);
        Assert.Empty(h.Events);
        Assert.Empty(h.Cache);
        Assert.Empty(h.Infos);
        h.AssertReturned();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("AC", "AC-96")]
    public async Task PayloadAndSuccessMessages_RemainUnchanged(bool publish)
    {
        using var h = new Harness(publish, capacity: 2);
        await h.Invoke();
        var sdk = Assert.IsType<UpdateEntityRequest>(h.UpdateRequest);
        Assert.Equal(Harness.Solution, sdk.SolutionUniqueName);
        Assert.True(sdk.MergeLabels); // AC-147: 1033-only labels are merged with the other languages.
        Assert.NotNull(h.ReadRequest);
        var entity = sdk.Entity;
        Assert.Equal(Harness.Table, entity.LogicalName);
        Assert.Equal("Changed", Assert.Single(entity.DisplayName.LocalizedLabels).Label);
        Assert.Equal(1033, Assert.Single(entity.DisplayName.LocalizedLabels).LanguageCode);
        Assert.Equal("Changed Tables", Assert.Single(entity.DisplayCollectionName.LocalizedLabels).Label);
        Assert.Equal("", Assert.Single(entity.Description.LocalizedLabels).Label);
        Assert.False(entity.HasActivities);
        Assert.True(entity.HasNotes);
        Assert.False(entity.ChangeTrackingEnabled);
        Assert.False(entity.IsAuditEnabled.Value);
        Assert.False(entity.IsQuickCreateEnabled);
        Assert.False(entity.IsDuplicateDetectionEnabled.Value);
        Assert.False(entity.IsValidForQueue.Value);
        Assert.Equal("#123456", entity.EntityColor);
        Assert.Null(entity.IconSmallName);
        Assert.Equal("new_medium", entity.IconMediumName);
        Assert.Equal("", entity.IconVectorName);
        Assert.Null(entity.SchemaName);
        Assert.Equal(publish
            ? $"Table '{Harness.Table}' updated and published."
            : $"Table '{Harness.Table}' updated. Changes must be published to take effect.", Assert.Single(h.Infos));
        if (publish)
            Assert.Equal($"<importexportxml><entities><entity>{Harness.Table}</entity></entities></importexportxml>", Assert.IsType<PublishXmlRequest>(h.PublishRequest).ParameterXml);
        else Assert.Null(h.PublishRequest);
        h.AssertReturned();
    }

    private static void AssertCli(Exception error, string code, int exit)
    {
        var (mapped, actualExit) = ExceptionMapper.MapWithExitCode(error);
        Assert.Equal(code, mapped.Code);
        Assert.Equal(exit, actualExit);
        using var text = new StringWriter();
        new JsonOutputWriter(text).WriteError(mapped);
        using var json = JsonDocument.Parse(text.ToString());
        Assert.False(json.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(code, json.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    private sealed record Trace(string Action, int? Lease, int Active, string StoredLabel);
    private sealed record CacheEvent(string Name, int Active);

    private sealed class Harness : IDisposable
    {
        internal const string Table = "new_Table", Solution = "TestSolution";
        private readonly SemaphoreSlim _slots;
        private readonly CancellationTokenSource _cts = new();
        private readonly int _capacity;
        private readonly string? _failureBoundary, _failureKind;
        private readonly List<int> _acquired = [], _disposed = [];
        internal DataverseMetadataAuthoringService Service { get; }
        internal IMetadataAuthoringProgressReporter Reporter { get; }
        internal UpdateTableRequest Request { get; }
        internal CancellationToken Token => _cts.Token;
        internal string StoredLabel { get; private set; } = "Existing";
        internal int Checkouts { get; private set; }
        internal int PeakActive { get; private set; }
        internal Exception? Injected { get; private set; }
        internal Exception? ModeledTimeout { get; private set; }
        internal OrganizationRequest? ReadRequest { get; private set; }
        internal OrganizationRequest? UpdateRequest { get; private set; }
        internal OrganizationRequest? ConfirmRequest { get; private set; }

        /// <summary>False: the read-back returns the old labels, as if Dataverse acknowledged the update without storing it.</summary>
        internal bool ConfirmStores { get; set; } = true;
        internal OrganizationRequest? PublishRequest { get; private set; }
        internal List<Trace> Events { get; } = [];
        internal List<CacheEvent> Cache { get; } = [];
        internal List<string> Infos { get; } = [];
        internal List<string> AttemptedSdk { get; } = [];
        internal List<string> SuccessfulSdk { get; } = [];
        private int Active => _capacity - _slots.CurrentCount;

        private readonly bool _emptyRead;

        internal Harness(bool publish, int capacity = 1, string? failureBoundary = null, string? failureKind = null, bool blocked = false, bool emptyRead = false)
        {
            _emptyRead = emptyRead;
            _capacity = capacity;
            _slots = new SemaphoreSlim(capacity, capacity);
            _failureBoundary = failureBoundary; _failureKind = failureKind;
            var pool = new Mock<IDataverseConnectionPool>(MockBehavior.Strict);
            pool.Setup(p => p.GetClientAsync(null, null, Token)).Returns(Acquire);
            var cache = new Mock<ICachedMetadataProvider>(MockBehavior.Strict);
            cache.Setup(c => c.InvalidateEntityList()).Callback(() => CacheCall("list"));
            cache.Setup(c => c.InvalidateEntity(Table)).Callback(() => CacheCall("entity"));
            var reporter = new Mock<IMetadataAuthoringProgressReporter>(MockBehavior.Strict);
            reporter.Setup(r => r.ReportPhase(It.IsAny<string>(), It.IsAny<string?>()));
            reporter.Setup(r => r.ReportInfo(It.IsAny<string>())).Callback<string>(message =>
            {
                Infos.Add(message); Record("info");
            });
            Reporter = reporter.Object;
            Service = new DataverseMetadataAuthoringService(pool.Object, new SchemaValidator(),
                blocked ? new ActiveFakeShakedownGuard() : new InactiveFakeShakedownGuard(), cacheProvider: cache.Object, baseLanguage: new FixedOrganizationBaseLanguage(1033));
            Request = new UpdateTableRequest
            {
                SolutionUniqueName = Solution, EntityLogicalName = Table, DisplayName = "Changed",
                PluralDisplayName = "Changed Tables", Description = "", HasActivities = false, HasNotes = true,
                ChangeTrackingEnabled = false, IsAuditEnabled = false, IsQuickCreateEnabled = false,
                IsDuplicateDetectionEnabled = false, IsValidForQueue = false, EntityColor = "#123456",
                IconMediumName = "new_medium", IconVectorName = "", Publish = publish
            };
        }

        internal Task Invoke() => Service.UpdateTableAsync(Request, Reporter, Token);

        private void Record(string action, int? lease = null) => Events.Add(new Trace(action, lease, Active, StoredLabel));

        private void CacheCall(string name)
        {
            Cache.Add(new CacheEvent(name, Active)); Record("cache:" + name);
            MaybeFail("cache-" + name);
        }

        private void MaybeFail(string boundary)
        {
            if (_failureBoundary != boundary) return;
            Assert.Null(Injected); // Repeated submission is a test failure, never a retry fixture.
            Injected = _failureKind switch
            {
                "coded" => new PpdsException("Synthetic.Coded", "Coded fixture failure"),
                "timeout" => new TimeoutException("SDK/acquisition timeout fixture"),
                "pool" => new PoolExhaustedException(Active, _capacity, TimeSpan.FromMilliseconds(40)),
                "auth" => new PPDS.Dataverse.Resilience.DataverseAuthenticationException("Synthetic expired sign-in", requiresReauthentication: true),
                "connection" => new PPDS.Dataverse.Security.DataverseConnectionException("Synthetic dropped connection"),
                "cancel" => Cancel(),
                _ => new InvalidOperationException("Ordinary fixture failure")
            };
            Record("injected:" + boundary);
            throw Injected;
        }

        private OperationCanceledException Cancel()
        {
            _cts.Cancel();
            return new OperationCanceledException("Canceled fixture operation", Token);
        }

        private async Task<IPooledClient> Acquire()
        {
            var id = ++Checkouts;
            var stage = id switch { 1 => "read", 2 => "update", 3 => "confirm", 4 => "publish", _ => throw new InvalidOperationException("Unexpected retry checkout") };
            Record("checkout:" + stage);
            MaybeFail(stage + "-checkout");
            if (!await _slots.WaitAsync(TimeSpan.FromMilliseconds(40), Token))
            {
                ModeledTimeout = new PoolExhaustedException(Active, _capacity, TimeSpan.FromMilliseconds(40));
                Record("checkout-timeout:" + stage);
                throw ModeledTimeout;
            }
            _acquired.Add(id); PeakActive = Math.Max(PeakActive, Active); Record("acquired:" + id, id);
            var disposed = false;
            var client = new Mock<IPooledClient>(MockBehavior.Strict);
            client.Setup(c => c.DisposeAsync()).Returns(() =>
            {
                Assert.False(disposed); disposed = true; _disposed.Add(id); _slots.Release();
                Record("dispose:" + id, id); return ValueTask.CompletedTask;
            });
            client.Setup(c => c.ExecuteAsync(It.IsAny<OrganizationRequest>(), Token))
                .Returns<OrganizationRequest, CancellationToken>(async (request, token) =>
                {
                    Assert.False(disposed); Assert.Equal(Token, token);
                    var sdkStage = request switch
                    {
                        RetrieveEntityRequest { RetrieveAsIfPublished: true } => "confirm", // the label read-back
                        RetrieveEntityRequest => "read", UpdateEntityRequest => "update", PublishXmlRequest => "publish",
                        _ => throw new InvalidOperationException("Unexpected SDK operation")
                    };
                    Assert.Equal(stage, sdkStage);
                    AttemptedSdk.Add(sdkStage); Record("sdk:" + sdkStage + ":" + id, id);
                    await Task.Yield(); // Fault/cancel after an asynchronous boundary while the actual service awaits SDK work.
                    MaybeFail(sdkStage + "-sdk");
                    if (request is RetrieveEntityRequest { RetrieveAsIfPublished: true } confirm)
                    {
                        Assert.Equal(Table, confirm.LogicalName);
                        ConfirmRequest = confirm;
                    }
                    else if (request is RetrieveEntityRequest read)
                    {
                        // AC-147: the current (published) labels are read before a label update, like the column read.
                        Assert.Equal(Table, read.LogicalName);
                        Assert.False(read.RetrieveAsIfPublished);
                        ReadRequest = read;
                    }
                    else if (request is UpdateEntityRequest update)
                    {
                        UpdateRequest = update;
                        StoredLabel = Assert.Single(update.Entity.DisplayName.LocalizedLabels).Label;
                    }
                    else PublishRequest = request;
                    SuccessfulSdk.Add(sdkStage); Record("success:" + sdkStage + ":" + id, id);
                    return request switch
                    {
                        RetrieveEntityRequest { RetrieveAsIfPublished: true } when ConfirmStores is false => new RetrieveEntityResponse { Results = new ParameterCollection
                        {
                            ["EntityMetadata"] = new Microsoft.Xrm.Sdk.Metadata.EntityMetadata { LogicalName = Table, DisplayName = new Label("Existing", 1033) }
                        } },
                        RetrieveEntityRequest { RetrieveAsIfPublished: true } => new RetrieveEntityResponse { Results = new ParameterCollection
                        {
                            ["EntityMetadata"] = new Microsoft.Xrm.Sdk.Metadata.EntityMetadata
                            {
                                LogicalName = Table, DisplayName = ((UpdateEntityRequest)UpdateRequest!).Entity.DisplayName,
                                DisplayCollectionName = ((UpdateEntityRequest)UpdateRequest).Entity.DisplayCollectionName,
                                Description = ((UpdateEntityRequest)UpdateRequest).Entity.Description
                            }
                        } },
                        RetrieveEntityRequest when _emptyRead => new OrganizationResponse(),
                        RetrieveEntityRequest => new RetrieveEntityResponse { Results = new ParameterCollection
                        {
                            ["EntityMetadata"] = new Microsoft.Xrm.Sdk.Metadata.EntityMetadata { LogicalName = Table, DisplayName = new Label("Existing", 1033) }
                        } },
                        UpdateEntityRequest => new UpdateEntityResponse(),
                        _ => new PublishXmlResponse()
                    };
                });
            return client.Object;
        }

        internal void AssertReturned()
        {
            Assert.Equal(_capacity, _slots.CurrentCount);
            Assert.Equal(_acquired.Order(), _disposed.Order());
            Assert.Equal(_disposed.Count, _disposed.Distinct().Count());
        }

        internal void AssertSequentialOwnership()
        {
            Assert.Equal(1, PeakActive);
            Assert.All(Events.Where(e => e.Action.StartsWith("cache:", StringComparison.Ordinal) || e.Action == "info"), e => Assert.Equal(0, e.Active));
            Assert.All(Events.Where(e => e.Action.StartsWith("sdk:", StringComparison.Ordinal)).GroupBy(e => e.Lease), group => Assert.Single(group));
            AssertReturned();
        }

        internal string Snapshot(Task operation) => JsonSerializer.Serialize(new
        {
            scope = "Actual service with mocked SDK and bounded semaphore; synthetic state, not live persistence.",
            capacity = _capacity, waitMs = 40, taskStatus = operation.Status.ToString(), StoredLabel,
            Checkouts, PeakActive, finalPermits = _slots.CurrentCount, acquired = _acquired, disposed = _disposed,
            injectedType = Injected?.GetType().FullName, modeledTimeoutType = ModeledTimeout?.GetType().FullName,
            attemptedSdk = AttemptedSdk, successfulSdk = SuccessfulSdk, cache = Cache, infos = Infos, events = Events
        });

        public void Dispose() { _slots.Dispose(); _cts.Dispose(); }
    }
}
