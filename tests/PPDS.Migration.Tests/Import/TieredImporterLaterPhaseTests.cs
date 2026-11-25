using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Moq;
using PPDS.Dataverse.BulkOperations;
using PPDS.Dataverse.Client;
using PPDS.Dataverse.Pooling;
using PPDS.Dataverse.Progress;
using PPDS.Migration.Analysis;
using PPDS.Migration.Formats;
using PPDS.Migration.Import;
using PPDS.Migration.Import.Handlers;
using PPDS.Migration.Models;
using PPDS.Migration.Progress;
using Xunit;

namespace PPDS.Migration.Tests.Import;

/// <summary>
/// Only records the entity phase proved written receive the later phases' writes (deferred
/// fields, state, many-to-many, file data). A record that failed (in Create mode, one that already
/// exists in the target), one a record filter left out, and every record of a table with
/// <c>importMode="skip"</c> get nothing more (#1914 review MF1, MF2, MF4). Every request sent, in
/// every phase, is recorded.
/// </summary>
[Trait("Category", "Unit")]
public class TieredImporterLaterPhaseTests
{
    private readonly Mock<IDataverseConnectionPool> _pool = new();
    private readonly Mock<IPooledClient> _client = new();
    private readonly Mock<IBulkOperationExecutor> _bulk = new();
    private readonly Mock<ISchemaValidator> _validator = new();

    /// <summary>Every write: (what, the ids it touched).</summary>
    private readonly ConcurrentQueue<(string What, Guid[] Ids)> _writes = new();

    /// <summary>Ids that already exist in the target: a create of one fails.</summary>
    private readonly HashSet<Guid> _existing = new();

    /// <summary>Ids created during this run: a second create fails (a duplicate in the file).</summary>
    private readonly ConcurrentDictionary<Guid, byte> _created = new();

    /// <summary>When set, a failed create is reported without its record id.</summary>
    private bool _failuresWithoutRecordId;

    /// <summary>
    /// When set, a bulk upsert reports half its records as succeeded and none as failed, with no
    /// errors: what the executor returns for a batch whose request throws a task-cancelled exception
    /// while the caller's token is not cancelled, or for an upsert response without results.
    /// </summary>
    private bool _shortUpsertResult;

    public TieredImporterLaterPhaseTests()
    {
        _pool.Setup(p => p.Statistics).Returns(new PoolStatistics());
        _pool.Setup(p => p.GetTotalRecommendedParallelism()).Returns(1);
        _pool.Setup(p => p.GetClientAsync(It.IsAny<DataverseClientOptions?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(_client.Object);

        _validator.Setup(v => v.LoadTargetFieldMetadataAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<IProgressReporter?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FieldMetadataCollection(new Dictionary<string, Dictionary<string, FieldValidity>>()));
        _validator.Setup(v => v.DetectMissingColumns(It.IsAny<MigrationData>(), It.IsAny<FieldMetadataCollection>()))
            .Returns(new SchemaMismatchResult(new Dictionary<string, List<string>>()));
        _validator.Setup(v => v.ShouldIncludeField(It.IsAny<string>(), It.IsAny<ImportMode>(), It.IsAny<IReadOnlyDictionary<string, FieldValidity>?>(), out It.Ref<string?>.IsAny))
            .Returns(true);

        _bulk.Setup(x => x.CreateMultipleAsync(It.IsAny<string>(), It.IsAny<IEnumerable<Entity>>(), It.IsAny<BulkOperationOptions>(), It.IsAny<DataverseClientOptions>(), It.IsAny<IProgress<ProgressSnapshot>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string e, IEnumerable<Entity> r, BulkOperationOptions _, DataverseClientOptions _, IProgress<ProgressSnapshot> _, CancellationToken _) => BulkCreate(e, r.ToList()));
        _bulk.Setup(x => x.UpdateMultipleAsync(It.IsAny<string>(), It.IsAny<IEnumerable<Entity>>(), It.IsAny<BulkOperationOptions>(), It.IsAny<DataverseClientOptions>(), It.IsAny<IProgress<ProgressSnapshot>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string e, IEnumerable<Entity> r, BulkOperationOptions _, DataverseClientOptions _, IProgress<ProgressSnapshot> _, CancellationToken _) => BulkSucceed("UpdateMultiple " + e, r.ToList()));
        _bulk.Setup(x => x.UpsertMultipleAsync(It.IsAny<string>(), It.IsAny<IEnumerable<Entity>>(), It.IsAny<BulkOperationOptions>(), It.IsAny<DataverseClientOptions>(), It.IsAny<IProgress<ProgressSnapshot>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string e, IEnumerable<Entity> r, BulkOperationOptions _, DataverseClientOptions _, IProgress<ProgressSnapshot> _, CancellationToken _) =>
            {
                var list = r.ToList();
                var result = BulkSucceed("UpsertMultiple " + e, list);
                return _shortUpsertResult
                    ? new BulkOperationResult { SuccessCount = list.Count / 2, FailureCount = 0, Errors = Array.Empty<BulkOperationError>() }
                    : result;
            });

        _client.Setup(c => c.ExecuteAsync(It.IsAny<OrganizationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((OrganizationRequest request, CancellationToken _) => Execute(request));
        _client.Setup(c => c.ExecuteAsync(It.IsAny<OrganizationRequest>()))
            .ReturnsAsync((OrganizationRequest request) => Execute(request));
    }

    private bool TryCreate(Guid id) => !_existing.Contains(id) && _created.TryAdd(id, 0);

    private BulkOperationResult BulkCreate(string entity, List<Entity> records)
    {
        _writes.Enqueue(("CreateMultiple " + entity, records.Select(r => r.Id).ToArray()));
        var errors = new List<BulkOperationError>();
        for (var i = 0; i < records.Count; i++)
        {
            if (!TryCreate(records[i].Id))
            {
                errors.Add(new BulkOperationError
                {
                    Index = i,
                    RecordId = _failuresWithoutRecordId ? null : records[i].Id,
                    ErrorCode = -2147220937,
                    Message = "A record with matching key values already exists."
                });
            }
        }

        return new BulkOperationResult { SuccessCount = records.Count - errors.Count, FailureCount = errors.Count, Errors = errors };
    }

    private BulkOperationResult BulkSucceed(string what, List<Entity> records)
    {
        _writes.Enqueue((what, records.Select(r => r.Id).ToArray()));
        return new BulkOperationResult { SuccessCount = records.Count, Errors = Array.Empty<BulkOperationError>() };
    }

    private OrganizationResponse Execute(OrganizationRequest request)
    {
        switch (request)
        {
            case RetrieveAllEntitiesRequest:
                return new RetrieveAllEntitiesResponse { Results = { ["EntityMetadata"] = Array.Empty<Microsoft.Xrm.Sdk.Metadata.EntityMetadata>() } };
            case RetrieveRequest retrieve:
                // The state phase reads the current state: every record is active.
                return new RetrieveResponse
                {
                    Results = { ["Entity"] = new Entity(retrieve.Target.LogicalName, retrieve.Target.Id) { ["statecode"] = new OptionSetValue(0), ["statuscode"] = new OptionSetValue(1) } }
                };
        }

        _writes.Enqueue((request.RequestName, IdsIn(request).ToArray()));

        switch (request)
        {
            case CreateRequest create:
                if (!TryCreate(create.Target.Id))
                {
                    throw new System.ServiceModel.FaultException<OrganizationServiceFault>(
                        new OrganizationServiceFault { Message = "A record with matching key values already exists." });
                }
                return new CreateResponse { Results = { ["id"] = create.Target.Id } };
            case UpsertRequest upsert:
                return new UpsertResponse { Results = { ["Target"] = upsert.Target.ToEntityReference(), ["RecordCreated"] = false } };
            default:
                return new OrganizationResponse();
        }
    }

    private static IEnumerable<Guid> IdsIn(OrganizationRequest request)
    {
        foreach (var value in request.Parameters.Values)
        {
            switch (value)
            {
                case Entity e: yield return e.Id; break;
                case EntityReference r: yield return r.Id; break;
                case EntityReferenceCollection c:
                    foreach (var r in c) yield return r.Id;
                    break;
            }
        }
    }

    private List<(string What, Guid[] Ids)> WritesTo(Guid id) => _writes.Where(w => w.Ids.Contains(id)).ToList();

    /// <summary>Runs after the state phase and keeps the import context, to read what was handed to it.</summary>
    private sealed class ContextProbe : IPostImportHandler
    {
        public ImportContext? Context { get; private set; }
        public bool CanHandle(string entityLogicalName) => true;
        public Task ExecuteAsync(string entityLogicalName, ImportContext context, CancellationToken cancellationToken)
        {
            Context = context;
            return Task.CompletedTask;
        }
    }

    private TieredImporter CreateImporter(IEnumerable<IRecordFilter>? filters = null, IPostImportHandler? probe = null)
    {
        var prober = new BulkOperationProber(_bulk.Object);
        return new TieredImporter(
            _pool.Object, _bulk.Object, Mock.Of<ICmtDataReader>(), Mock.Of<IDependencyGraphBuilder>(), Mock.Of<IExecutionPlanBuilder>(), _validator.Object,
            new DeferredFieldProcessor(_pool.Object, prober), new RelationshipProcessor(_pool.Object), prober,
            recordFilters: filters,
            postImportHandlers: probe != null ? new[] { probe } : null,
            stateTransitionProcessor: new StateTransitionProcessor(_pool.Object),
            fileColumnProcessor: new FileColumnProcessor(new FileColumnTransferHelper(_pool.Object)));
    }

    /// <summary>An inactive account with a deferred parent lookup to <paramref name="parent"/>.</summary>
    private static Entity InactiveAccount(Guid id, Guid parent) => new("account")
    {
        Id = id,
        ["name"] = "Account",
        ["parentaccountid"] = new EntityReference("account", parent),
        ["statecode"] = new OptionSetValue(1),
        ["statuscode"] = new OptionSetValue(2)
    };

    private static MigrationData AccountData(IReadOnlyList<Entity> accounts, ImportMode? tableMode = null) => new()
    {
        Schema = new MigrationSchema { Entities = new List<EntitySchema> { new() { LogicalName = "account", PrimaryIdField = "accountid", DisplayName = "Account", ImportMode = tableMode } } },
        EntityData = new Dictionary<string, IReadOnlyList<Entity>> { ["account"] = accounts }
    };

    private static ExecutionPlan Plan(Dictionary<string, IReadOnlyList<string>>? deferred = null, params string[] tables) => new()
    {
        Tiers = new List<ImportTier> { new() { TierNumber = 0, Entities = tables.ToList() } },
        DeferredFields = deferred ?? new Dictionary<string, IReadOnlyList<string>> { ["account"] = new List<string> { "parentaccountid" } }
    };

    #region P1: Create mode never changes a record that already exists

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CreateMode_ExistingRecord_GetsNothingButTheFailedCreate(bool useBulkApis)
    {
        var parent = Guid.NewGuid();
        var newAccount = Guid.NewGuid();
        var existing = Guid.NewGuid();
        _existing.Add(existing);
        var data = AccountData(new[]
        {
            new Entity("account") { Id = parent, ["name"] = "Parent" },
            InactiveAccount(newAccount, parent),
            InactiveAccount(existing, parent)
        });

        var result = await CreateImporter().ImportAsync(data, Plan(tables: "account"),
            new ImportOptions { Mode = ImportMode.Create, ContinueOnError = true, UseBulkApis = useBulkApis });

        var toExisting = WritesTo(existing);
        toExisting.Should().ContainSingle("only the create is sent to the record that already exists")
            .Which.What.Should().StartWith(useBulkApis ? "CreateMultiple" : "Create");
        WritesTo(newAccount).Select(w => w.What).Should().Contain(w => w.StartsWith("UpdateMultiple") || w == "Update")
            .And.Contain("SetState", "the new record still gets its deferred lookup and state");
        result.FailureCount.Should().Be(1);
        result.CreatedCount.Should().Be(2);
    }

    [Fact]
    public async Task CreateMode_FailedRecord_HasNoStateTransitionHandedToTheStatePhase()
    {
        // The state phase also refuses an unmapped record; this pins the entity phase on its own.
        var existing = Guid.NewGuid();
        var written = Guid.NewGuid();
        _existing.Add(existing);
        var parent = Guid.NewGuid();
        var data = AccountData(new[] { new Entity("account") { Id = parent, ["name"] = "Parent" }, InactiveAccount(written, parent), InactiveAccount(existing, parent) });
        var probe = new ContextProbe();

        await CreateImporter(probe: probe).ImportAsync(data, Plan(tables: "account"), new ImportOptions { Mode = ImportMode.Create, ContinueOnError = true });

        var transitions = probe.Context!.StateTransitions.GetTransitions("account").Select(t => t.RecordId).ToList();
        transitions.Should().Equal(written);
    }

    [Fact]
    public async Task CreateMode_DuplicateInTheFile_SecondCreateFails_AndTheIdGetsNoLaterWrites()
    {
        var parent = Guid.NewGuid();
        var duplicate = Guid.NewGuid();
        var data = AccountData(new[]
        {
            new Entity("account") { Id = parent, ["name"] = "Parent" },
            InactiveAccount(duplicate, parent),
            InactiveAccount(duplicate, parent)
        });

        var result = await CreateImporter().ImportAsync(data, Plan(tables: "account"),
            new ImportOptions { Mode = ImportMode.Create, ContinueOnError = true });

        WritesTo(duplicate).Should().OnlyContain(w => w.What.StartsWith("CreateMultiple"),
            "a failed id is not mapped, whichever occurrence failed");
        result.FailureCount.Should().Be(1);
    }

    [Fact]
    public async Task MixedBatch_OnlyTheFailedRecordIsLeftOut()
    {
        var parent = Guid.NewGuid();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();
        _existing.Add(b);
        var data = AccountData(new[] { new Entity("account") { Id = parent, ["name"] = "Parent" }, InactiveAccount(a, parent), InactiveAccount(b, parent), InactiveAccount(c, parent) });

        await CreateImporter().ImportAsync(data, Plan(tables: "account"), new ImportOptions { Mode = ImportMode.Create, ContinueOnError = true });

        WritesTo(b).Should().OnlyContain(w => w.What.StartsWith("CreateMultiple"));
        foreach (var written in new[] { a, c })
        {
            WritesTo(written).Select(w => w.What).Should().Contain(w => w.StartsWith("UpdateMultiple")).And.Contain("SetState");
        }
    }

    [Fact]
    public async Task ResultThatDoesNotNameTheFailedRecord_RunsNoLaterPhaseForTheTable_AndSaysSo()
    {
        _failuresWithoutRecordId = true;
        var parent = Guid.NewGuid();
        var a = Guid.NewGuid();
        var existing = Guid.NewGuid();
        _existing.Add(existing);
        var data = AccountData(new[] { new Entity("account") { Id = parent, ["name"] = "Parent" }, InactiveAccount(a, parent), InactiveAccount(existing, parent) });

        var result = await CreateImporter().ImportAsync(data, Plan(tables: "account"), new ImportOptions { Mode = ImportMode.Create, ContinueOnError = true });

        _writes.Should().OnlyContain(w => w.What.StartsWith("CreateMultiple"), "no later phase runs for a table whose written records are not known");
        result.Warnings.Should().Contain(w => w.Code == ImportWarningCodes.LaterPhasesSkipped && w.Entity == "account");
        result.EntityResults.Single().LaterPhasesSkipped.Should().BeTrue();
        result.EntityResults.Single().Success.Should().BeFalse();
    }

    [Theory]
    [InlineData(1, 1, 1, true)]   // counts add up and the error names a record sent
    [InlineData(0, 1, 1, false)]  // fewer results than records: the write stopped part-way
    [InlineData(1, 0, 1, false)]  // an error for a record counted as a success
    [InlineData(1, 1, 0, false)]  // a failure without an error
    public void ProvenWrittenRecords_ProvesOnlyWhatTheResultSays(int success, int failure, int errorCount, bool proven)
    {
        var ok = new Entity("account", Guid.NewGuid());
        var bad = new Entity("account", Guid.NewGuid());
        var result = new BulkOperationResult
        {
            SuccessCount = success,
            FailureCount = failure,
            Errors = Enumerable.Range(0, errorCount).Select(_ => new BulkOperationError { RecordId = bad.Id }).ToList()
        };

        var written = TieredImporter.ProvenWrittenRecords(new[] { ok, bad }, result, out var reason);

        if (proven)
        {
            written.Should().BeEquivalentTo(new[] { ok.Id });
        }
        else
        {
            written.Should().BeNull();
            reason.Should().NotBeNullOrEmpty();
        }
    }

    [Fact]
    public void ProvenWrittenRecords_ErrorListNamingTheSameRecordTwice_IsNotProof()
    {
        // #1934 r2 MF4: sent a, b, c; 1 succeeded, 2 failed, errors [a, a]: b and c cannot both be written.
        var a = new Entity("account", Guid.NewGuid());
        var b = new Entity("account", Guid.NewGuid());
        var c = new Entity("account", Guid.NewGuid());
        var result = new BulkOperationResult
        {
            SuccessCount = 1,
            FailureCount = 2,
            Errors = new[] { new BulkOperationError { RecordId = a.Id }, new BulkOperationError { RecordId = a.Id } }
        };

        TieredImporter.ProvenWrittenRecords(new[] { a, b, c }, result, out var reason).Should().BeNull();
        reason.Should().Contain("only 1 succeeded");
    }

    [Fact]
    public async Task UnaccountedRecords_MakeTheImportNotASuccess_AndSaySo()
    {
        // #1934 r2 MF1: 4 sent, 2 succeeded, 0 failed, no errors (the shape the real executor returns
        // for a batch whose request throws a task-cancelled exception).
        _shortUpsertResult = true;
        var parent = Guid.NewGuid();
        var data = AccountData(new[]
        {
            new Entity("account") { Id = parent, ["name"] = "Parent" },
            InactiveAccount(Guid.NewGuid(), parent), InactiveAccount(Guid.NewGuid(), parent), InactiveAccount(Guid.NewGuid(), parent)
        });

        var result = await CreateImporter().ImportAsync(data, Plan(tables: "account"), new ImportOptions { Mode = ImportMode.Upsert, UseBulkApis = true });

        result.Success.Should().BeFalse();
        result.Errors.Should().ContainSingle(e => e.EntityLogicalName == "account")
            .Which.Message.Should().Contain("3 of 4 account record(s) sent are unaccounted for", "the probe sends 1 (0 reported) and the rest 3 (1 reported)").And.Contain("were not sent");
        result.EntityResults.Single().LaterPhasesSkipped.Should().BeTrue();
        result.Warnings.Should().Contain(w => w.Code == ImportWarningCodes.LaterPhasesSkipped);
        _writes.Should().OnlyContain(w => w.What.StartsWith("UpsertMultiple"));
    }

    [Fact]
    public void ProvenWrittenRecords_ErrorNamingARecordNotSent_IsNotProof()
    {
        var sent = new Entity("account", Guid.NewGuid());
        var result = new BulkOperationResult
        {
            SuccessCount = 0,
            FailureCount = 1,
            Errors = new[] { new BulkOperationError { Index = 0, RecordId = Guid.NewGuid() } }
        };

        TieredImporter.ProvenWrittenRecords(new[] { sent }, result, out _).Should().BeNull();
    }

    #endregion

    #region P2: a record a filter left out receives no later write

    private sealed class LeaveOut(Guid id) : IRecordFilter
    {
        public bool CanHandle(string entityLogicalName) => true;
        public bool ShouldSkip(Entity record, ImportContext context) => record.Id == id;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FilteredRecord_ReceivesNoWriteInAnyPhase(bool useBulkApis)
    {
        var parent = Guid.NewGuid();
        var a = Guid.NewGuid();
        var filtered = Guid.NewGuid();
        var data = AccountData(new[] { new Entity("account") { Id = parent, ["name"] = "Parent" }, InactiveAccount(a, parent), InactiveAccount(filtered, parent) });

        var result = await CreateImporter(new[] { new LeaveOut(filtered) }).ImportAsync(data, Plan(tables: "account"),
            new ImportOptions { Mode = ImportMode.Upsert, UseBulkApis = useBulkApis });

        WritesTo(filtered).Should().BeEmpty();
        WritesTo(a).Should().NotBeEmpty();
        result.SkippedCount.Should().Be(1);
    }

    #endregion

    #region MF4: a table with importMode="skip" receives nothing in any phase

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task SkippedTable_WithDeferredStateManyToManyAndFiles_ReceivesNothingInAnyPhase(bool useBulkApis, bool continueOnError)
    {
        var parent = Guid.NewGuid();
        var skipped = Guid.NewGuid();
        var contact = Guid.NewGuid();
        var data = new MigrationData
        {
            Schema = new MigrationSchema
            {
                Entities = new List<EntitySchema>
                {
                    new() { LogicalName = "account", PrimaryIdField = "accountid", DisplayName = "Account", ImportMode = ImportMode.Skip },
                    new() { LogicalName = "contact", PrimaryIdField = "contactid", DisplayName = "Contact" }
                }
            },
            EntityData = new Dictionary<string, IReadOnlyList<Entity>>
            {
                ["account"] = new[] { new Entity("account") { Id = parent, ["name"] = "Parent" }, InactiveAccount(skipped, parent) },
                ["contact"] = new[] { new Entity("contact") { Id = contact, ["lastname"] = "Contact" } }
            },
            RelationshipData = new Dictionary<string, IReadOnlyList<ManyToManyRelationshipData>>
            {
                // Both ways: the skipped table as source, and as the target of another table's association.
                ["account"] = new[] { new ManyToManyRelationshipData { RelationshipName = "account_contact", SourceEntityName = "account", SourceId = skipped, TargetEntityName = "contact", TargetIds = new List<Guid> { contact } } },
                ["contact"] = new[] { new ManyToManyRelationshipData { RelationshipName = "contact_account", SourceEntityName = "contact", SourceId = contact, TargetEntityName = "account", TargetIds = new List<Guid> { skipped } } }
            },
            FileData = new Dictionary<string, IReadOnlyList<FileColumnData>>
            {
                ["account"] = new[] { new FileColumnData { RecordId = skipped, FieldName = "new_document", FileName = "a.txt", MimeType = "text/plain", Data = new byte[] { 1 } } }
            }
        };

        var result = await CreateImporter().ImportAsync(data, Plan(tables: new[] { "account", "contact" }),
            new ImportOptions { Mode = ImportMode.Create, UseBulkApis = useBulkApis, ContinueOnError = continueOnError });

        WritesTo(skipped).Should().BeEmpty();
        WritesTo(parent).Should().BeEmpty();
        WritesTo(contact).Should().NotBeEmpty("the other table is imported");
        result.Success.Should().BeTrue("the skipped table's file data is not a failure");
        result.SkippedCount.Should().Be(2);
    }

    #endregion
}
