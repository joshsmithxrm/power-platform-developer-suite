using System.Text.Json;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Moq;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Cli.Services.Metadata.Authoring;
using PPDS.Cli.Tests.Services.Shared;
using PPDS.Dataverse.Metadata;
using PPDS.Dataverse.Metadata.Authoring;
using PPDS.Dataverse.Pooling;
using Xunit;
using SdkUpdateOption = Microsoft.Xrm.Sdk.Messages.UpdateOptionValueRequest;
using SdkDeleteOption = Microsoft.Xrm.Sdk.Messages.DeleteOptionValueRequest;

namespace PPDS.Cli.Tests.Services.Metadata.Authoring;

[Trait("Category", "Unit")]
public sealed class MetadataLeaseLifetimeTests
{
    public static TheoryData<string, bool, bool> Workflows => new()
    {
        { "global-add", true, false }, { "generic-local-add", true, false },
        { "global-update", true, false }, { "status-update", true, false },
        { "status-update", false, true }, { "status-update", true, true },
        { "status-remove", false, true }, { "table-create", false, true },
        { "global-add", false, false }, { "global-update", false, false },
        { "status-update", false, false }, { "status-remove", false, false },
        { "table-create", false, false }, { "status-add", true, true },
        { "column-add", true, true }, { "column-update", true, true },
        { "column-remove", false, true }
    };

    [Theory]
    [MemberData(nameof(Workflows))]
    [Trait("AC", "AC-82")]
    [Trait("AC", "AC-83")]
    [Trait("AC", "AC-84")]
    [Trait("AC", "AC-89")]
    public async Task SingleSlot_WorkflowsReturnEachLeaseBeforeNextOperation(string route, bool color, bool publish)
    {
        using var h = new Harness(route, color, publish);
        var result = await h.Run();
        h.AssertFinished(h.ExpectedStages);
        h.AssertIndividualLeases();
        Assert.Equal(1, h.PeakActive);
        if (route.EndsWith("add", StringComparison.Ordinal)) Assert.Equal(Harness.Value, Assert.IsType<int>(result));
        if (route == "table-create")
        {
            var created = Assert.IsType<CreateTableResult>(result);
            Assert.Equal(h.EntityId, created.MetadataId);
            Assert.Equal(Harness.Table, created.LogicalName);
            Assert.False(created.WasDryRun);
        }
    }

    [Fact]
    [Trait("AC", "AC-91")]
    public async Task PrefixLookup_UsesSeparateLeasesAndCachesAcrossDryRuns()
    {
        using var h = new Harness("table-create", false, true);
        Assert.True((await h.Service.CreateTableAsync(h.TableRequest(dryRun: true), ct: h.Token)).WasDryRun);
        Assert.True((await h.Service.CreateTableAsync(h.TableRequest(dryRun: true), ct: h.Token)).WasDryRun);
        h.AssertFinished(["solution", "publisher"]);
        h.AssertIndividualLeases();
        Assert.Empty(h.CacheEvents);
        Assert.Equal(2, h.Acquired);
    }

    [Theory]
    [InlineData("global-add")]
    [InlineData("column-add")]
    [InlineData("status-update")]
    [Trait("AC", "AC-84")]
    public async Task ColorRead_MissingTarget_PreventsWriteAndPublication(string route)
    {
        using var h = new Harness(route, true, true, capacity: 8, missingColorTarget: true);
        var error = await Assert.ThrowsAsync<MetadataValidationException>(() => h.Run());
        Assert.Equal(MetadataErrorCodes.OptionNotFound, error.ErrorCode);
        var colorRead = Array.FindIndex(h.ExpectedStages, s => s.EndsWith("color-read", StringComparison.Ordinal));
        h.AssertFinished(h.ExpectedStages.Take(colorRead + 1));
        Assert.DoesNotContain(h.AttemptedStages, s => s.EndsWith("color-write", StringComparison.Ordinal) || s == "publish");
        h.AssertIndividualLeases();
    }

    public static IEnumerable<object[]> FailureCases()
    {
        foreach (var (route, color, publish) in new[]
        {
            ("global-add", true, false), ("status-update", true, true),
            ("status-remove", false, true), ("table-create", false, true),
            ("column-add", true, true)
        })
        foreach (var stage in Harness.Stages(route, color, publish))
        foreach (var fault in new[] { "sdk", "cancel", "coded", "checkout", "checkout-cancel" })
            yield return [route, color, publish, stage, fault];
    }

    [Theory]
    [MemberData(nameof(FailureCases))]
    [Trait("AC", "AC-83")]
    [Trait("AC", "AC-84")]
    [Trait("AC", "AC-87")]
    [Trait("AC", "AC-91")]
    public async Task FailureAtEachStep_ReturnsLeasesAndStopsLaterWork(string route, bool color, bool publish, string stage, string fault)
    {
        // Capacity permits old nesting so the controls test failure semantics, not incidental saturation.
        using var h = new Harness(route, color, publish, stage, fault, capacity: 8);
        var operation = h.Run();
        var error = await Record.ExceptionAsync(() => operation);
        Assert.NotNull(error);
        Assert.NotNull(h.Injected);
        var stageIndex = Array.IndexOf(h.ExpectedStages, stage);
        var attempted = h.ExpectedStages.Take(stageIndex + (fault.StartsWith("checkout", StringComparison.Ordinal) ? 0 : 1));
        h.AssertFinished(attempted);
        Assert.Equal(h.ExpectedStages.Take(stageIndex), h.SuccessfulStages);
        Assert.DoesNotContain(h.AttemptedStages.Skip(stageIndex + 1), _ => true);
        var expectedCode = Harness.ExpectedFailureCode(route, stage, fault);
        if (expectedCode != null)
        {
            Assert.Equal(expectedCode, Assert.IsType<PpdsException>(error).ErrorCode);
            Assert.Same(h.Injected, error.InnerException);
        }
        else Assert.Same(h.Injected, error);
        Assert.Equal(fault.Contains("cancel", StringComparison.Ordinal) && expectedCode == null, operation.IsCanceled);
        // No successful publication or later SDK operation after a failed stage; preceding writes are never replayed.
        Assert.All(h.AttemptedStages.GroupBy(s => s), group => Assert.Single(group));
    }

    [Theory]
    [InlineData("global-add", "global-color-read", "sdk")]
    [InlineData("global-add", "global-color-write", "sdk")]
    [InlineData("column-add", "local-color-read", "sdk")]
    [InlineData("column-add", "local-color-write", "sdk")]
    [InlineData("global-add", "global-color-read", "cancel")]
    [InlineData("global-add", "global-color-write", "cancel")]
    [InlineData("column-add", "local-color-read", "cancel")]
    [InlineData("column-add", "local-color-write", "cancel")]
    [Trait("AC", "AC-87")]
    public async Task ColorSdkFaults_PreserveBaselineWrappingAndCliEnvelope(string route, string stage, string fault)
    {
        using var h = new Harness(route, true, false, stage, fault, capacity: 8);
        var operation = h.Run();
        var error = await Record.ExceptionAsync(() => operation);
        // The existing combined source already preserves local color-read cancellation (#1538).
        if (stage == "local-color-read" && fault == "cancel")
        {
            var canceled = Assert.IsType<OperationCanceledException>(error);
            Assert.Same(h.Injected, canceled);
            Assert.Equal(h.Token, canceled.CancellationToken);
            Assert.True(operation.IsCanceled);
            AssertCliEnvelope(canceled, "Operation.Cancelled", 2);
        }
        else
        {
            var wrapped = Assert.IsType<PpdsException>(error);
            Assert.Same(h.Injected, wrapped.InnerException);
            Assert.True(operation.IsFaulted);
            Assert.Equal("SDK_OPERATION_FAILED", wrapped.ErrorCode);
            AssertCliEnvelope(wrapped, "SDK_OPERATION_FAILED", 2);
        }
        h.AssertReturned();
    }

    [Theory]
    [InlineData("global-add", "global-color-read")]
    [InlineData("global-add", "global-color-write")]
    [InlineData("column-add", "local-color-read")]
    [InlineData("column-add", "local-color-write")]
    [InlineData("status-update", "publish")]
    [InlineData("table-create", "publisher")]
    [InlineData("table-create", "table-create")]
    [InlineData("table-create", "publish")]
    [Trait("AC", "AC-87")]
    public async Task CheckoutFailures_PreserveRawIdentityAndCliClassification(string route, string stage)
    {
        using var h = new Harness(route, route != "table-create", true, stage, "checkout", capacity: 8);
        var error = await Assert.ThrowsAsync<PoolExhaustedException>(() => h.Run());
        Assert.Same(h.Injected, error);
        AssertCliEnvelope(error, "Connection.Timeout", 4);
        h.AssertReturned();
    }

    [Theory]
    [InlineData("global-update")]
    [InlineData("column-update")]
    [InlineData("status-update")]
    [Trait("AC", "AC-88")]
    public async Task Payloads_ReadModesAndResultsRemainStable(string route)
    {
        using var h = new Harness(route, true, route != "global-update");
        await h.Run();
        Assert.Equal("Changed", h.StoredLabel);
        Assert.Equal(h.ExpectedStages, h.SuccessfulStages);
        h.AssertIndividualLeases();
        h.AssertReturned();
        Assert.All(h.CacheEvents, e => Assert.Equal(0, e.Active));
    }

    private static void AssertCliEnvelope(Exception error, string code, int exit)
    {
        var (mapped, mappedExit) = ExceptionMapper.MapWithExitCode(error);
        Assert.Equal(code, mapped.Code);
        Assert.Equal(exit, mappedExit);
        using var output = new StringWriter();
        new JsonOutputWriter(output).WriteError(mapped);
        using var json = JsonDocument.Parse(output.ToString());
        Assert.False(json.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(code, json.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    private sealed class Harness : IDisposable
    {
        internal const string Table = "new_table", Column = "new_category", Global = "new_global", Color = "#123456", Solution = "TestSolution";
        internal const int Value = 77;
        private readonly SemaphoreSlim _slots;
        private readonly CancellationTokenSource _cts = new();
        private readonly string _route;
        private readonly bool _color, _publish, _missingColorTarget;
        private readonly string? _failStage, _fault;
        private readonly int _capacity;
        private int _nextId;
        private bool _mutated;
        private readonly Guid _publisherId = Guid.NewGuid();
        internal Guid EntityId { get; } = Guid.NewGuid();
        internal string StoredLabel { get; private set; } = "Existing";
        internal Exception? Injected { get; private set; }
        internal CancellationToken Token => _cts.Token;
        internal DataverseMetadataAuthoringService Service { get; }
        internal string[] ExpectedStages { get; }
        internal List<string> AttemptedStages { get; } = [];
        internal List<string> SuccessfulStages { get; } = [];
        internal List<(int Lease, string Stage)> Operations { get; } = [];
        internal List<(string Stage, int Active)> CacheEvents { get; } = [];
        private readonly List<int> _acquired = [], _disposed = [];
        internal int Acquired => _acquired.Count;
        internal int PeakActive { get; private set; }

        internal Harness(string route, bool color, bool publish, string? failStage = null, string? fault = null, int capacity = 1, bool missingColorTarget = false)
        {
            _route = route; _color = color; _publish = publish; _failStage = failStage; _fault = fault;
            _missingColorTarget = missingColorTarget;
            _capacity = capacity; _slots = new SemaphoreSlim(capacity, capacity);
            ExpectedStages = Stages(route, color, publish);
            var pool = new Mock<IDataverseConnectionPool>(MockBehavior.Strict);
            pool.Setup(p => p.GetClientAsync(null, null, Token)).Returns(Acquire);
            var cache = new Mock<ICachedMetadataProvider>(MockBehavior.Strict);
            cache.Setup(c => c.InvalidateEntityList()).Callback(() => CacheEvents.Add(("entity-list", capacity - _slots.CurrentCount)));
            cache.Setup(c => c.InvalidateEntity(It.IsAny<string>())).Callback<string>(name =>
            {
                Assert.Equal(Table, name);
                CacheEvents.Add(("entity", capacity - _slots.CurrentCount));
            });
            cache.Setup(c => c.InvalidateGlobalOptionSets()).Callback(() => CacheEvents.Add(("global", capacity - _slots.CurrentCount)));
            Service = new DataverseMetadataAuthoringService(pool.Object, new SchemaValidator(), new InactiveFakeShakedownGuard(), cacheProvider: cache.Object);
        }

        internal static string[] Stages(string route, bool color, bool publish)
        {
            var stages = route switch
            {
                "global-add" or "generic-local-add" => new List<string> { "insert" },
                "global-update" => ["global-target", "option-update"],
                "status-update" => ["status-target", "option-update"],
                "status-remove" => ["status-target", "option-remove"],
                "status-add" => ["status-target", "solution-option-prefix", "publisher-option-prefix", "status-insert"],
                "column-add" => ["local-target", "solution-option-prefix", "publisher-option-prefix", "insert"],
                "column-update" => ["local-target", "option-update"],
                "column-remove" => ["local-target", "option-remove"],
                "table-create" => ["solution", "publisher", "table-create"],
                _ => throw new ArgumentOutOfRangeException(nameof(route))
            };
            if (color)
            {
                var prefix = route is "global-add" or "global-update" ? "global" : "local";
                stages.Add(prefix + "-color-read"); stages.Add(prefix + "-color-write");
            }
            if (publish && route is not ("global-add" or "global-update" or "generic-local-add")) stages.Add("publish");
            return stages.ToArray();
        }

        internal static string? ExpectedFailureCode(string route, string stage, string fault)
        {
            if (route == "column-add" && stage == "insert")
                return fault == "coded" ? null : MetadataErrorCodes.SdkOperationFailed; // Existing try also owns checkout.
            if (fault.StartsWith("checkout", StringComparison.Ordinal)) return null;
            if (stage == "local-color-read" && fault == "cancel") return null; // Existing #1538 combined-source contract.
            if (stage is "status-target" or "local-target")
                return fault == "cancel" ? null : MetadataErrorCodes.EntityNotFound; // Existing list readers wrap coded errors too.
            if (fault == "coded") return null;
            return stage.Contains("color", StringComparison.Ordinal) || stage is "option-update" or "option-remove" or "status-insert"
                ? MetadataErrorCodes.SdkOperationFailed : null;
        }

        private Exception Failure(string kind)
        {
            if (kind.Contains("cancel", StringComparison.Ordinal))
            {
                _cts.Cancel();
                return Injected = new OperationCanceledException(Token);
            }
            return Injected = kind switch
            {
                "coded" => new PpdsException("Synthetic.Coded", "Already coded SDK fault"),
                "checkout" => new PoolExhaustedException(0, _capacity, TimeSpan.FromMilliseconds(40)),
                _ => new InvalidOperationException("Synthetic SDK fault")
            };
        }

        private async Task<IPooledClient> Acquire()
        {
            var nextStage = ExpectedStages.ElementAtOrDefault(AttemptedStages.Count);
            if (_failStage == nextStage && _fault?.StartsWith("checkout", StringComparison.Ordinal) == true) throw Failure(_fault);
            if (!await _slots.WaitAsync(TimeSpan.FromMilliseconds(40), Token))
                throw new PoolExhaustedException(_capacity - _slots.CurrentCount, _capacity, TimeSpan.FromMilliseconds(40));
            var id = ++_nextId;
            _acquired.Add(id);
            PeakActive = Math.Max(PeakActive, _capacity - _slots.CurrentCount);
            bool disposed = false;
            var lease = new Mock<IPooledClient>(MockBehavior.Strict);
            lease.Setup(c => c.DisposeAsync()).Returns(() =>
            {
                Assert.False(disposed); disposed = true; _disposed.Add(id); _slots.Release();
                return ValueTask.CompletedTask;
            });
            lease.Setup(c => c.ExecuteAsync(It.IsAny<OrganizationRequest>(), Token)).Returns<OrganizationRequest, CancellationToken>((request, _) =>
            {
                Assert.False(disposed);
                return Task.FromResult(Execute(id, request));
            });
            lease.Setup(c => c.RetrieveMultipleAsync(It.IsAny<QueryBase>(), Token)).Returns<QueryBase, CancellationToken>((query, _) =>
            {
                Assert.False(disposed);
                var q = Assert.IsType<QueryExpression>(query);
                var stage = _route is "status-add" or "column-add" ? q.EntityName + "-option-prefix" : q.EntityName;
                Begin(id, stage);
                var condition = Assert.Single(q.Criteria.Conditions);
                var row = new Entity(q.EntityName);
                if (q.EntityName == "solution")
                {
                    Assert.Equal("uniquename", condition.AttributeName);
                    Assert.Equal(Solution, Assert.Single(condition.Values));
                    Assert.Equal(new[] { "publisherid" }, q.ColumnSet.Columns);
                    row["publisherid"] = new EntityReference("publisher", _publisherId);
                }
                else
                {
                    Assert.Equal("publisher", q.EntityName);
                    Assert.Equal("publisherid", condition.AttributeName);
                    Assert.Equal(_publisherId, Assert.Single(condition.Values));
                    if (_route is "status-add" or "column-add")
                    {
                        Assert.Equal(new[] { "customizationoptionvalueprefix" }, q.ColumnSet.Columns);
                        row["customizationoptionvalueprefix"] = 10000;
                    }
                    else
                    {
                        Assert.Equal(new[] { "customizationprefix" }, q.ColumnSet.Columns);
                        row["customizationprefix"] = "new";
                    }
                }
                SuccessfulStages.Add(stage);
                return Task.FromResult(new EntityCollection([row]));
            });
            return lease.Object;
        }

        private void Begin(int lease, string stage)
        {
            Operations.Add((lease, stage)); AttemptedStages.Add(stage);
            if (stage == _failStage && _fault?.StartsWith("checkout", StringComparison.Ordinal) != true) throw Failure(_fault!);
        }

        private OrganizationResponse Execute(int lease, OrganizationRequest request)
        {
            var stage = request switch
            {
                RetrieveOptionSetRequest => _mutated ? "global-color-read" : "global-target",
                RetrieveAttributeRequest a => _mutated ? "local-color-read" : a.LogicalName == "statuscode" ? "status-target" : "local-target",
                InsertOptionValueRequest => "insert", InsertStatusValueRequest => "status-insert",
                SdkUpdateOption => "option-update", SdkDeleteOption => "option-remove",
                UpdateOptionSetRequest => "global-color-write", UpdateAttributeRequest => "local-color-write",
                CreateEntityRequest => "table-create", PublishXmlRequest => "publish",
                _ => throw new InvalidOperationException("Unexpected SDK request: " + request.RequestName)
            };
            Begin(lease, stage);
            OrganizationResponse response;
            switch (request)
            {
                case RetrieveOptionSetRequest r:
                    Assert.Equal(Global, r.Name);
                    Assert.Equal(stage == "global-target", r.RetrieveAsIfPublished);
                    response = new RetrieveOptionSetResponse { Results = new ParameterCollection { ["OptionSetMetadata"] = Options(false) } };
                    break;
                case RetrieveAttributeRequest r:
                    Assert.Equal(Table, r.EntityLogicalName);
                    Assert.Equal(_route.StartsWith("status", StringComparison.Ordinal) ? "statuscode" : Column, r.LogicalName);
                    var readsDrafts = stage == "local-color-read" || _route is "status-add" or "column-add";
                    Assert.Equal(readsDrafts, r.RetrieveAsIfPublished); // Existing #1538 draft allocation/color reads; update/remove targeting stays published.
                    EnumAttributeMetadata attribute = r.LogicalName == "statuscode" ? new StatusAttributeMetadata() : new PicklistAttributeMetadata();
                    attribute.LogicalName = r.LogicalName; attribute.SchemaName = r.LogicalName;
                    attribute.OptionSet = Options(r.LogicalName == "statuscode");
                    attribute.RequiredLevel = new AttributeRequiredLevelManagedProperty(AttributeRequiredLevel.SystemRequired);
                    attribute.Description = new Label("Do not resend", 1033);
                    response = new RetrieveAttributeResponse { Results = new ParameterCollection { ["AttributeMetadata"] = attribute } };
                    break;
                case InsertOptionValueRequest r:
                    Assert.Equal(Value, r.Value); Assert.Equal("Added", Assert.Single(r.Label.LocalizedLabels).Label);
                    Assert.Equal(Solution, r.SolutionUniqueName); Assert.False(r.Parameters.Contains("Color"));
                    StoredLabel = "Added";
                    response = new InsertOptionValueResponse { Results = new ParameterCollection { ["NewOptionValue"] = Value } };
                    break;
                case InsertStatusValueRequest r:
                    Assert.Equal(Value, r.Value); Assert.Equal(0, r.StateCode); Assert.Equal(Solution, r.SolutionUniqueName);
                    StoredLabel = "Added";
                    response = new InsertStatusValueResponse { Results = new ParameterCollection { ["NewOptionValue"] = Value } };
                    break;
                case SdkUpdateOption r:
                    Assert.Equal(Value, r.Value); Assert.Equal(Solution, r.SolutionUniqueName);
                    StoredLabel = Assert.Single(r.Label.LocalizedLabels).Label; Assert.Equal("Changed", StoredLabel);
                    response = new UpdateOptionValueResponse();
                    break;
                case SdkDeleteOption r:
                    Assert.Equal(Value, r.Value); Assert.Equal(Solution, r.SolutionUniqueName);
                    response = new DeleteOptionValueResponse();
                    break;
                case UpdateOptionSetRequest r:
                    Assert.Equal(Solution, r.SolutionUniqueName); VerifyOptions(Assert.IsType<OptionSetMetadata>(r.OptionSet));
                    response = new UpdateOptionSetResponse();
                    break;
                case UpdateAttributeRequest r:
                    Assert.Equal(Table, r.EntityName); Assert.Equal(Solution, r.SolutionUniqueName);
                    Assert.Null(r.Attribute.RequiredLevel); Assert.Null(r.Attribute.Description);
                    VerifyOptions(Assert.IsAssignableFrom<EnumAttributeMetadata>(r.Attribute).OptionSet);
                    response = new UpdateAttributeResponse();
                    break;
                case CreateEntityRequest r:
                    Assert.Equal("new_Table", r.Entity.SchemaName); Assert.Equal(Solution, r.SolutionUniqueName);
                    Assert.Equal("new_Name", r.PrimaryAttribute.SchemaName);
                    response = new CreateEntityResponse { Results = new ParameterCollection { ["EntityId"] = EntityId } };
                    break;
                case PublishXmlRequest r:
                    Assert.Contains("<entity>" + Table + "</entity>", r.ParameterXml);
                    response = new PublishXmlResponse();
                    break;
                default: throw new InvalidOperationException();
            }
            if (request is not (RetrieveAttributeRequest or RetrieveOptionSetRequest)) _mutated = true;
            SuccessfulStages.Add(stage);
            return response;
        }

        private OptionSetMetadata Options(bool status)
        {
            var options = new OptionSetMetadata { Name = Global };
            if ((_mutated || !_route.EndsWith("add", StringComparison.Ordinal)) && !(_mutated && _missingColorTarget))
            {
                OptionMetadata target = status ? new StatusOptionMetadata { State = 0 } : new OptionMetadata();
                target.Value = Value; target.Label = new Label(StoredLabel, 1033);
                target.Label.LocalizedLabels.Add(new LocalizedLabel("Existant", 1036));
                options.Options.Add(target);
            }
            options.Options.Add(new OptionMetadata(new Label("Other", 1033), 88));
            return options;
        }

        private void VerifyOptions(OptionSetMetadata options)
        {
            Assert.Equal(Global, options.Name);
            Assert.Equal(2, options.Options.Count);
            var target = Assert.Single(options.Options, o => o.Value == Value);
            Assert.Equal(Color, target.Color);
            Assert.Equal(StoredLabel, Assert.Single(target.Label.LocalizedLabels, l => l.LanguageCode == 1033).Label);
            Assert.Equal("Existant", Assert.Single(target.Label.LocalizedLabels, l => l.LanguageCode == 1036).Label);
            Assert.Equal("Other", Assert.Single(Assert.Single(options.Options, o => o.Value == 88).Label.LocalizedLabels).Label);
        }

        internal CreateTableRequest TableRequest(bool dryRun = false) => new()
        {
            SchemaName = "new_Table", DisplayName = "Table", PluralDisplayName = "Tables", SolutionUniqueName = Solution,
            Publish = _publish, DryRun = dryRun
        };

        internal async Task<object?> Run()
        {
            var color = _color ? Color : null;
            switch (_route)
            {
                case "global-add": case "generic-local-add":
                    return await Service.AddOptionValueAsync(new AddOptionValueRequest
                    {
                        OptionSetName = Global, Value = Value, Label = "Added", Color = color, SolutionUniqueName = Solution,
                        EntityLogicalName = _route == "generic-local-add" ? Table : null,
                        AttributeLogicalName = _route == "generic-local-add" ? Column : null
                    }, Token);
                case "global-update":
                    await Service.UpdateOptionValueAsync(new PPDS.Dataverse.Metadata.Authoring.UpdateOptionValueRequest
                    { OptionSetName = Global, Value = Value, NewLabel = "Changed", Color = color, SolutionUniqueName = Solution }, Token); break;
                case "status-update":
                    await Service.UpdateStatusReasonAsync(new UpdateStatusReasonRequest
                    { EntityLogicalName = Table, Value = Value, NewLabel = "Changed", Color = color, Publish = _publish, SolutionUniqueName = Solution }, Token); break;
                case "status-remove":
                    await Service.RemoveStatusReasonAsync(new RemoveStatusReasonRequest
                    { EntityLogicalName = Table, Value = Value, Publish = _publish, SolutionUniqueName = Solution }, Token); break;
                case "status-add":
                    return await Service.AddStatusReasonAsync(new AddStatusReasonRequest
                    { EntityLogicalName = Table, Value = Value, Label = "Added", StateCode = 0, Color = color, Publish = _publish, SolutionUniqueName = Solution }, ct: Token);
                case "column-add":
                    return await Service.AddColumnOptionAsync(new AddColumnOptionRequest
                    { EntityLogicalName = Table, ColumnLogicalName = Column, Value = Value, Label = "Added", Color = color, Publish = _publish, SolutionUniqueName = Solution }, Token);
                case "column-update":
                    await Service.UpdateColumnOptionAsync(new UpdateColumnOptionRequest
                    { EntityLogicalName = Table, ColumnLogicalName = Column, Value = Value, NewLabel = "Changed", Color = color, Publish = _publish, SolutionUniqueName = Solution }, Token); break;
                case "column-remove":
                    await Service.RemoveColumnOptionAsync(new RemoveColumnOptionRequest
                    { EntityLogicalName = Table, ColumnLogicalName = Column, Value = Value, Publish = _publish, SolutionUniqueName = Solution }, Token); break;
                case "table-create": return await Service.CreateTableAsync(TableRequest(), ct: Token);
                default: throw new ArgumentOutOfRangeException();
            }
            return null;
        }

        internal void AssertReturned()
        {
            Assert.Equal(_capacity, _slots.CurrentCount);
            Assert.Equal(_acquired.Order(), _disposed.Order());
            Assert.Equal(_disposed.Count, _disposed.Distinct().Count());
        }

        internal void AssertFinished(IEnumerable<string> attempted)
        {
            Assert.Equal(attempted, AttemptedStages);
            AssertReturned();
        }

        internal void AssertIndividualLeases()
        {
            Assert.All(Operations.GroupBy(o => o.Lease), group => Assert.Single(group));
            Assert.Equal(Operations.Count, Acquired);
            Assert.All(CacheEvents, e => Assert.Equal(0, e.Active));
        }

        public void Dispose() { _slots.Dispose(); _cts.Dispose(); }
    }
}
