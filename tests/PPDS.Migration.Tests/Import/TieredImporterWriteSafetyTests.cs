using FluentAssertions;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
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
/// What the importer writes, counted on recording fakes: import modes and the result counts
/// (#1914), and plug-in steps disabled for an import (#1921).
/// </summary>
[Trait("Category", "Unit")]
public class TieredImporterWriteSafetyTests
{
    private readonly Mock<IDataverseConnectionPool> _pool = new();
    private readonly Mock<IPooledClient> _client = new();
    private readonly Mock<IBulkOperationExecutor> _bulk = new();
    private readonly Mock<ICmtDataReader> _reader = new();
    private readonly Mock<IDependencyGraphBuilder> _graph = new();
    private readonly Mock<IExecutionPlanBuilder> _planBuilder = new();
    private readonly Mock<ISchemaValidator> _validator = new();
    private readonly List<(string Operation, string Entity, int Count)> _bulkCalls = new();
    private readonly List<OrganizationRequest> _individualRequests = new();

    public TieredImporterWriteSafetyTests()
    {
        _pool.Setup(p => p.Statistics).Returns(new PoolStatistics());
        _pool.Setup(p => p.GetClientAsync(It.IsAny<DataverseClientOptions?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(_client.Object);

        _validator.Setup(v => v.LoadTargetFieldMetadataAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<IProgressReporter?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FieldMetadataCollection(new Dictionary<string, Dictionary<string, FieldValidity>>()));
        _validator.Setup(v => v.DetectMissingColumns(It.IsAny<MigrationData>(), It.IsAny<FieldMetadataCollection>()))
            .Returns(new SchemaMismatchResult(new Dictionary<string, List<string>>()));
        _validator.Setup(v => v.ShouldIncludeField(It.IsAny<string>(), It.IsAny<ImportMode>(), It.IsAny<IReadOnlyDictionary<string, FieldValidity>?>(), out It.Ref<string?>.IsAny))
            .Returns(true);

        RecordBulk(nameof(IBulkOperationExecutor.CreateMultipleAsync));
        RecordBulk(nameof(IBulkOperationExecutor.UpdateMultipleAsync));
        RecordBulk(nameof(IBulkOperationExecutor.UpsertMultipleAsync));
    }

    private void RecordBulk(string operation)
    {
        BulkOperationResult Record(string entity, IEnumerable<Entity> records)
        {
            var count = records.Count();
            lock (_bulkCalls) _bulkCalls.Add((operation, entity, count));
            return new BulkOperationResult { SuccessCount = count, Errors = Array.Empty<BulkOperationError>() };
        }

        switch (operation)
        {
            case nameof(IBulkOperationExecutor.CreateMultipleAsync):
                _bulk.Setup(x => x.CreateMultipleAsync(It.IsAny<string>(), It.IsAny<IEnumerable<Entity>>(), It.IsAny<BulkOperationOptions>(), It.IsAny<DataverseClientOptions>(), It.IsAny<IProgress<ProgressSnapshot>>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync((string e, IEnumerable<Entity> r, BulkOperationOptions _, DataverseClientOptions _, IProgress<ProgressSnapshot> _, CancellationToken _) => Record(e, r));
                break;
            case nameof(IBulkOperationExecutor.UpdateMultipleAsync):
                _bulk.Setup(x => x.UpdateMultipleAsync(It.IsAny<string>(), It.IsAny<IEnumerable<Entity>>(), It.IsAny<BulkOperationOptions>(), It.IsAny<DataverseClientOptions>(), It.IsAny<IProgress<ProgressSnapshot>>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync((string e, IEnumerable<Entity> r, BulkOperationOptions _, DataverseClientOptions _, IProgress<ProgressSnapshot> _, CancellationToken _) => Record(e, r));
                break;
            default:
                _bulk.Setup(x => x.UpsertMultipleAsync(It.IsAny<string>(), It.IsAny<IEnumerable<Entity>>(), It.IsAny<BulkOperationOptions>(), It.IsAny<DataverseClientOptions>(), It.IsAny<IProgress<ProgressSnapshot>>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync((string e, IEnumerable<Entity> r, BulkOperationOptions _, DataverseClientOptions _, IProgress<ProgressSnapshot> _, CancellationToken _) => Record(e, r));
                break;
        }
    }

    private TieredImporter CreateImporter(IPluginStepManager? pluginStepManager = null, IEnumerable<IRecordFilter>? filters = null)
    {
        var prober = new BulkOperationProber(_bulk.Object);
        return new TieredImporter(
            _pool.Object, _bulk.Object, _reader.Object, _graph.Object, _planBuilder.Object, _validator.Object,
            new DeferredFieldProcessor(_pool.Object, prober), new RelationshipProcessor(_pool.Object), prober,
            recordFilters: filters, pluginStepManager: pluginStepManager);
    }

    private void RecordIndividualRequests(Func<OrganizationRequest, OrganizationResponse>? respond = null)
    {
        _client.Setup(c => c.ExecuteAsync(It.IsAny<OrganizationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((OrganizationRequest request, CancellationToken _) =>
            {
                lock (_individualRequests) _individualRequests.Add(request);
                return respond?.Invoke(request) ?? request switch
                {
                    CreateRequest c => new CreateResponse { Results = { ["id"] = c.Target.Id } },
                    UpsertRequest u => new UpsertResponse { Results = { ["Target"] = u.Target.ToEntityReference(), ["RecordCreated"] = false } },
                    _ => new UpdateResponse()
                };
            });
    }

    private static List<Entity> Accounts(int count) =>
        Enumerable.Range(0, count).Select(i => new Entity("account") { Id = Guid.NewGuid(), ["name"] = $"Account {i}" }).ToList();

    private static MigrationData Data(params (EntitySchema Schema, List<Entity> Records)[] tables) => new()
    {
        Schema = new MigrationSchema { Entities = tables.Select(t => t.Schema).ToList() },
        EntityData = tables.ToDictionary(t => t.Schema.LogicalName, t => (IReadOnlyList<Entity>)t.Records)
    };

    private static EntitySchema Table(string name, ImportMode? mode = null, bool disablePlugins = false, int? sourceCode = null) => new()
    {
        LogicalName = name,
        PrimaryIdField = $"{name}id",
        DisplayName = name,
        ImportMode = mode,
        DisablePlugins = disablePlugins,
        ObjectTypeCode = sourceCode
    };

    private static ExecutionPlan Plan(params string[] entities) => new()
    {
        Tiers = new List<ImportTier> { new() { TierNumber = 0, Entities = entities.ToList() } },
        DeferredFields = new Dictionary<string, IReadOnlyList<string>>()
    };

    #region #1914: a global Skip is refused before anything is read or written

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GlobalSkip_IsRefused_AndNothingIsReadOrWritten(bool useBulkApis)
    {
        // The issue's example: 3 account records, global Skip, no per-table mode.
        var pluginSteps = new Mock<IPluginStepManager>(MockBehavior.Strict);
        var sut = CreateImporter(pluginSteps.Object);
        var data = Data((Table("account", disablePlugins: true), Accounts(3)));
        RecordIndividualRequests();

        var act = () => sut.ImportAsync(data, Plan("account"), new ImportOptions { Mode = ImportMode.Skip, UseBulkApis = useBulkApis });

        var ex = await act.Should().ThrowAsync<ImportRefusedException>();
        ex.Which.Reason.Should().Be(ImportRefusalReasons.SkipIsPerTable);
        ex.Which.Message.Should().Contain("importMode=\"skip\"").And.Contain("Nothing was read or written");
        _bulkCalls.Should().BeEmpty();
        _individualRequests.Should().BeEmpty();
        _validator.Verify(v => v.LoadTargetFieldMetadataAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<IProgressReporter?>(), It.IsAny<CancellationToken>()), Times.Never);
        _pool.Verify(p => p.GetClientAsync(It.IsAny<DataverseClientOptions?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GlobalSkip_FromAFile_IsRefusedBeforeTheFileIsRead()
    {
        var sut = CreateImporter();

        var act = () => sut.ImportAsync("data.zip", new ImportOptions { Mode = ImportMode.Skip });

        await act.Should().ThrowAsync<ImportRefusedException>();
        _reader.Verify(r => r.ReadAsync(It.IsAny<string>(), It.IsAny<IProgressReporter?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ModeOutsideTheEnum_IsRefused()
    {
        var sut = CreateImporter();
        var data = Data((Table("account"), Accounts(1)));

        var act = () => sut.ImportAsync(data, Plan("account"), new ImportOptions { Mode = (ImportMode)42 });

        (await act.Should().ThrowAsync<ImportRefusedException>()).Which.Reason
            .Should().Be(ImportRefusalReasons.ModeNotImplemented, "a mode outside the enum is not the Skip refusal");
        _bulkCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task SchemaWithImportIssues_IsRefused_BeforeAnythingIsReadOrWritten()
    {
        // #1934 r2 should-fix 4: refused where the import reads it.
        var sut = CreateImporter();
        var data = Data((Table("account"), Accounts(1)));
        data.Schema.ImportIssues = new[] { "Table 'account' in the schema carries the Configuration Migration Tool attribute skipupdate, which PPDS does not apply." };

        var act = () => sut.ImportAsync(data, Plan("account"), new ImportOptions());

        var ex = (await act.Should().ThrowAsync<ImportRefusedException>()).Which;
        ex.Reason.Should().Be(ImportRefusalReasons.SchemaNotApplicable);
        ex.Message.Should().Contain("skipupdate").And.Contain("nothing was imported");
        _bulkCalls.Should().BeEmpty();
        _validator.Verify(v => v.LoadTargetFieldMetadataAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<IProgressReporter?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(ImportMode.Create, BulkOperationType.Create)]
    [InlineData(ImportMode.Update, BulkOperationType.Update)]
    [InlineData(ImportMode.Upsert, BulkOperationType.Upsert)]
    public void ToBulkOperationType_MapsEachWritingMode(ImportMode mode, BulkOperationType expected)
    {
        TieredImporter.ToBulkOperationType(mode).Should().Be(expected);
    }

    [Fact]
    public void ToBulkOperationType_Skip_NeverFallsThroughToUpsert()
    {
        var act = () => TieredImporter.ToBulkOperationType(ImportMode.Skip);

        act.Should().Throw<InvalidOperationException>();
    }

    #endregion

    #region #1914: per-table Skip writes nothing and is reported; each mode reports what it did

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PerTableSkip_WritesNothingForThatTable_AndReportsItsRecordsAsSkipped(bool useBulkApis)
    {
        var sut = CreateImporter();
        var contacts = Enumerable.Range(0, 2).Select(_ => new Entity("contact") { Id = Guid.NewGuid() }).ToList();
        var data = Data((Table("account", ImportMode.Skip), Accounts(3)), (Table("contact"), contacts));
        RecordIndividualRequests();

        var result = await sut.ImportAsync(data, Plan("account", "contact"), new ImportOptions { Mode = ImportMode.Create, UseBulkApis = useBulkApis });

        result.Success.Should().BeTrue();
        _bulkCalls.Where(c => c.Entity == "account").Should().BeEmpty();
        _individualRequests.Should().NotContain(r => r.Parameters.Values.OfType<Entity>().Any(e => e.LogicalName == "account"));
        var account = result.EntityResults.Single(e => e.EntityLogicalName == "account");
        account.SkippedCount.Should().Be(3);
        account.SuccessCount.Should().Be(0);
        account.CreatedCount.Should().Be(0);
        account.UpdatedCount.Should().Be(0);
        result.SkippedCount.Should().Be(3);
        result.CreatedCount.Should().Be(2);
        result.UpdatedCount.Should().Be(0);
        result.FailureCount.Should().Be(0);
        result.RecordsImported.Should().Be(2);
    }

    [Theory]
    [InlineData(ImportMode.Create, nameof(IBulkOperationExecutor.CreateMultipleAsync), 3, 0)]
    [InlineData(ImportMode.Update, nameof(IBulkOperationExecutor.UpdateMultipleAsync), 0, 3)]
    public async Task CreateAndUpdate_SendTheirOwnOperation_AndReportCreatedAndUpdated(
        ImportMode mode, string operation, int created, int updated)
    {
        var sut = CreateImporter();
        var data = Data((Table("account"), Accounts(3)));

        var result = await sut.ImportAsync(data, Plan("account"), new ImportOptions { Mode = mode });

        _bulkCalls.Select(c => c.Operation).Distinct().Should().Equal(operation);
        _bulkCalls.Sum(c => c.Count).Should().Be(3);
        result.CreatedCount.Should().Be(created);
        result.UpdatedCount.Should().Be(updated);
        result.SkippedCount.Should().Be(0);
    }

    [Theory]
    [InlineData(ImportMode.Create, typeof(CreateRequest), 3, 0)]
    [InlineData(ImportMode.Update, typeof(UpdateRequest), 0, 3)]
    public async Task IndividualPath_SendsTheModesRequest_AndReportsCreatedAndUpdated(
        ImportMode mode, Type requestType, int created, int updated)
    {
        var sut = CreateImporter();
        var data = Data((Table("account"), Accounts(3)));
        RecordIndividualRequests();

        var result = await sut.ImportAsync(data, Plan("account"), new ImportOptions { Mode = mode, UseBulkApis = false });

        _individualRequests.Should().HaveCount(3).And.AllSatisfy(r => r.Should().BeOfType(requestType));
        result.CreatedCount.Should().Be(created);
        result.UpdatedCount.Should().Be(updated);
    }

    [Fact]
    public async Task IndividualUpsert_ReportsCreatedAndUpdatedFromTheResponses()
    {
        var sut = CreateImporter();
        var records = Accounts(3);
        var data = Data((Table("account"), records));
        RecordIndividualRequests(request =>
        {
            var target = ((UpsertRequest)request).Target;
            return new UpsertResponse { Results = { ["Target"] = target.ToEntityReference(), ["RecordCreated"] = target.Id == records[0].Id } };
        });

        var result = await sut.ImportAsync(data, Plan("account"), new ImportOptions { Mode = ImportMode.Upsert, UseBulkApis = false });

        result.CreatedCount.Should().Be(1);
        result.UpdatedCount.Should().Be(2);
    }

    private sealed class SkipFirstRecordFilter(Guid skip) : IRecordFilter
    {
        public bool CanHandle(string entityLogicalName) => entityLogicalName == "account";
        public bool ShouldSkip(Entity record, ImportContext context) => record.Id == skip;
    }

    [Fact]
    public async Task RecordsLeftOutByAFilter_AreReportedAsSkipped()
    {
        var records = Accounts(3);
        var sut = CreateImporter(filters: new[] { new SkipFirstRecordFilter(records[0].Id) });
        var data = Data((Table("account"), records));

        var result = await sut.ImportAsync(data, Plan("account"), new ImportOptions { Mode = ImportMode.Create });

        _bulkCalls.Sum(c => c.Count).Should().Be(2);
        result.SkippedCount.Should().Be(1);
        result.EntityResults.Single().SkippedCount.Should().Be(1);
        result.CreatedCount.Should().Be(2);
    }

    [Fact]
    public async Task TheCompletedProgressCarriesTheSkippedCount()
    {
        var sut = CreateImporter();
        var data = Data((Table("account", ImportMode.Skip), Accounts(3)));
        var progress = new Mock<IProgressReporter>();
        MigrationResult? completed = null;
        progress.Setup(p => p.Complete(It.IsAny<MigrationResult>())).Callback<MigrationResult>(r => completed = r);

        await sut.ImportAsync(data, Plan("account"), new ImportOptions(), progress.Object);

        completed!.SkippedCount.Should().Be(3);
        completed.CreatedCount.Should().Be(0);
        completed.UpdatedCount.Should().Be(0);
    }

    #endregion

    #region #1921: plug-in steps are selected in the TARGET by logical name

    private (TieredImporter Sut, FakePluginTarget Target, Guid WidgetStep, Guid GadgetStep) WidgetAndGadget()
    {
        // The issue's example: in the source new_widget had code 10050; in the target 10050 is
        // new_gadget and new_widget is 10051.
        var target = new FakePluginTarget();
        target.Codes["new_widget"] = 10051;
        target.Codes["new_gadget"] = 10050;
        var widgetStep = Guid.NewGuid();
        var gadgetStep = Guid.NewGuid();
        target.Steps.Add((widgetStep, "widget step", 10051));
        target.Steps.Add((gadgetStep, "gadget step", 10050));
        target.Attach(_client);
        return (CreateImporter(new PluginStepManager(_pool.Object)), target, widgetStep, gadgetStep);
    }

    private static MigrationData Widgets(int sourceCode = 10050) =>
        Data((Table("new_widget", disablePlugins: true, sourceCode: sourceCode),
              new List<Entity> { new("new_widget") { Id = Guid.NewGuid() } }));

    [Fact]
    public async Task DisablePlugins_DisablesTheTargetTablesSteps_NotTheTableWithTheSourceCode()
    {
        var (sut, target, widgetStep, gadgetStep) = WidgetAndGadget();
        var disabledDuringImport = new List<Guid>();
        _bulk.Setup(x => x.UpsertMultipleAsync(It.IsAny<string>(), It.IsAny<IEnumerable<Entity>>(), It.IsAny<BulkOperationOptions>(), It.IsAny<DataverseClientOptions>(), It.IsAny<IProgress<ProgressSnapshot>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                disabledDuringImport.AddRange(target.LeftDisabled);
                return new BulkOperationResult { SuccessCount = 1, Errors = Array.Empty<BulkOperationError>() };
            });

        var result = await sut.ImportAsync(Widgets(), Plan("new_widget"), new ImportOptions());

        result.Success.Should().BeTrue();
        target.MetadataRequests.Should().Equal("new_widget");
        target.FetchXml.Should().ContainSingle().Which.Should().Contain("value='10051'").And.NotContain("10050");
        disabledDuringImport.Should().Equal(widgetStep);
        target.StepUpdates.Should().NotContain(u => u.Id == gadgetStep, "another table's steps are never touched");
        target.StepUpdates.Should().Equal((widgetStep, 1), (widgetStep, 0));
        target.LeftDisabled.Should().BeEmpty();
    }

    [Fact]
    public async Task DisablePlugins_AnAlreadyDisabledStep_IsNotTouched_AndStaysDisabled()
    {
        var (sut, target, widgetStep, _) = WidgetAndGadget();
        var off = Guid.NewGuid();
        target.Steps.Add(new FakeStep { Id = off, Name = "switched off by an admin", Code = 10051, Enabled = false });

        await sut.ImportAsync(Widgets(), Plan("new_widget"), new ImportOptions());

        target.StepUpdates.Should().NotContain(u => u.Id == off);
        target.Steps.Single(st => st.Id == off).Enabled.Should().BeFalse("the import must not switch on a step that was off");
        target.Steps.Single(st => st.Id == widgetStep).Enabled.Should().BeTrue();
    }

    [Fact]
    public async Task DisablePlugins_TableWithoutASourceCode_IsStillResolved()
    {
        var (sut, target, widgetStep, _) = WidgetAndGadget();
        var data = Data((Table("new_widget", disablePlugins: true, sourceCode: null), new List<Entity> { new("new_widget") { Id = Guid.NewGuid() } }));

        await sut.ImportAsync(data, Plan("new_widget"), new ImportOptions());

        target.StepUpdates.Should().Equal((widgetStep, 1), (widgetStep, 0));
    }

    [Fact]
    public async Task DisablePlugins_TableNotInTheTarget_RefusesBeforeDisablingOrWritingAnything()
    {
        var (sut, target, _, _) = WidgetAndGadget();
        target.Codes.Remove("new_widget");

        var act = () => sut.ImportAsync(Widgets(), Plan("new_widget"), new ImportOptions());

        (await act.Should().ThrowAsync<ImportRefusedException>()).Which.Reason
            .Should().Be(ImportRefusalReasons.PluginTableNotResolved);
        target.StepUpdates.Should().BeEmpty();
        target.FetchXml.Should().BeEmpty();
        _bulkCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task DisablePlugins_SkippedTable_IsNotResolvedOrDisabled()
    {
        var (sut, target, _, _) = WidgetAndGadget();
        var data = Data((Table("new_widget", ImportMode.Skip, disablePlugins: true, sourceCode: 10050), new List<Entity> { new("new_widget") { Id = Guid.NewGuid() } }));

        await sut.ImportAsync(data, Plan("new_widget"), new ImportOptions());

        target.MetadataRequests.Should().BeEmpty();
        target.StepUpdates.Should().BeEmpty();
    }

    [Fact]
    public async Task DisablePlugins_CancelledWhileDisabling_ReenablesTheStepsAlreadyDisabled()
    {
        var (sut, target, _, _) = WidgetAndGadget();
        var second = Guid.NewGuid();
        target.Steps.Add((second, "widget step 2", 10051));
        var third = Guid.NewGuid();
        target.Steps.Add((third, "widget step 3", 10051));
        using var cts = new CancellationTokenSource();
        target.OnUpdate = (id, state) =>
        {
            if (state == 1 && id == second) cts.Cancel();
        };

        var act = () => sut.ImportAsync(Widgets(), Plan("new_widget"), new ImportOptions(), cancellationToken: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        target.StepUpdates.Where(u => u.State == 1).Should().HaveCount(2);
        target.LeftDisabled.Should().BeEmpty("every step that may have been disabled is re-enabled on cancel");
        target.StepUpdates.Should().NotContain(u => u.Id == third, "only the steps recorded as disabled are re-enabled, not every selected step");
        _bulkCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task DisablePlugins_StepThatCannotBeDisabled_RefusesTheImport_AndReenablesTheOthers()
    {
        var (sut, target, widgetStep, _) = WidgetAndGadget();
        var second = Guid.NewGuid();
        target.Steps.Add((second, "widget step 2", 10051));
        target.OnUpdate = (id, state) =>
        {
            if (state == 1 && id == second) throw new InvalidOperationException("no privilege");
        };

        var act = () => sut.ImportAsync(Widgets(), Plan("new_widget"), new ImportOptions());

        (await act.Should().ThrowAsync<ImportRefusedException>()).Which.Reason
            .Should().Be(ImportRefusalReasons.PluginStepNotDisabled);
        _bulkCalls.Should().BeEmpty("the import does not run with a step it was asked to suppress");
        target.StepUpdates.Where(u => u.State == 0).Select(u => u.Id).Should().BeEquivalentTo(new[] { widgetStep, second });
        target.LeftDisabled.Should().BeEmpty();
    }

    [Fact]
    public async Task DisablePlugins_StepThatCannotBeReenabled_IsNamedInTheResultAndTheProgress()
    {
        var (sut, target, widgetStep, _) = WidgetAndGadget();
        target.OnUpdate = (_, state) =>
        {
            if (state == 0) throw new InvalidOperationException("token expired");
        };
        var messages = new List<string>();
        var progress = new Mock<IProgressReporter>();
        progress.Setup(p => p.Report(It.IsAny<ProgressEventArgs>())).Callback<ProgressEventArgs>(a => messages.Add(a.Message ?? ""));

        var result = await sut.ImportAsync(Widgets(), Plan("new_widget"), new ImportOptions(), progress.Object);

        result.Warnings.Should().Contain(w => w.Code == ImportWarningCodes.PluginReenableFailed && w.Message.Contains("'widget step'") && w.Message.Contains(widgetStep.ToString()));
        messages.Should().Contain(m => m.Contains("'widget step'") && m.Contains("remains disabled"));
    }

    [Fact]
    public async Task DisablePlugins_CancelledImport_NamesAStepThatCannotBeReenabledInTheProgress()
    {
        var (sut, target, _, _) = WidgetAndGadget();
        using var cts = new CancellationTokenSource();
        target.OnUpdate = (_, state) =>
        {
            if (state == 1) cts.Cancel();
            if (state == 0) throw new InvalidOperationException("token expired");
        };
        var messages = new List<string>();
        var progress = new Mock<IProgressReporter>();
        progress.Setup(p => p.Report(It.IsAny<ProgressEventArgs>())).Callback<ProgressEventArgs>(a => messages.Add(a.Message ?? ""));

        var act = () => sut.ImportAsync(Widgets(), Plan("new_widget"), new ImportOptions(), progress.Object, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        messages.Should().Contain(m => m.Contains("'widget step'") && m.Contains("remains disabled"));
    }

    #region #1921 review MF3, MF6, should-fix 1 and 2: what is said when a step stays disabled

    [Fact]
    public async Task StepNotDisabled_AndAnotherNotReenabled_RefusalNamesTheStepLeftDisabled_AndDoesNotClaimItWasReenabled()
    {
        var (sut, target, widgetStep, _) = WidgetAndGadget();
        var second = Guid.NewGuid();
        target.Steps.Add((second, "widget step 2", 10051));
        target.OnUpdate = (id, state) =>
        {
            if (state == 1 && id == second) throw new InvalidOperationException("no privilege");
            if (state == 0 && id == widgetStep) throw new InvalidOperationException("token expired");
        };

        var act = () => sut.ImportAsync(Widgets(), Plan("new_widget"), new ImportOptions());

        var ex = (await act.Should().ThrowAsync<ImportRefusedException>()).Which;
        ex.Reason.Should().Be(ImportRefusalReasons.PluginStepNotDisabled);
        ex.Message.Should().Contain("Plug-in step 'widget step 2' could not be disabled")
            .And.Contain("remain disabled: 'widget step'")
            .And.NotContain("has been re-enabled")
            .And.NotContain(widgetStep.ToString(), "ids belong in the detail, not the one-line message");
        ex.PluginStepsLeftDisabled.Should().Equal(new PluginStepInfo(widgetStep, "widget step"));
        _bulkCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task StepNotDisabled_AllOthersReenabled_RefusalSaysSo()
    {
        var (sut, target, _, _) = WidgetAndGadget();
        var second = Guid.NewGuid();
        target.Steps.Add((second, "widget step 2", 10051));
        target.OnUpdate = (id, state) =>
        {
            if (state == 1 && id == second) throw new InvalidOperationException("no privilege");
        };

        var act = () => sut.ImportAsync(Widgets(), Plan("new_widget"), new ImportOptions());

        var ex = (await act.Should().ThrowAsync<ImportRefusedException>()).Which;
        ex.Message.Should().Contain("Every step this import disabled has been re-enabled.");
        ex.PluginStepsLeftDisabled.Should().BeEmpty();
    }

    private (TieredImporter Sut, Mock<IPluginStepManager> Steps, PluginStepInfo Step) ReenableAlwaysFails()
    {
        var step = new PluginStepInfo(Guid.NewGuid(), "audit step");
        var steps = new Mock<IPluginStepManager>();
        steps.Setup(p => p.GetActivePluginStepsAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { step });
        steps.Setup(p => p.DisablePluginStepsAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<ICollection<Guid>>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<Guid>, ICollection<Guid>, CancellationToken>((ids, disabled, _) => { foreach (var id in ids) disabled.Add(id); })
            .Returns(Task.CompletedTask);
        // The general branch: no per-step detail (a pool or sign-in failure).
        steps.Setup(p => p.EnablePluginStepsAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("the connection was closed"));
        return (CreateImporter(steps.Object), steps, step);
    }

    [Fact]
    public async Task ReenableFailsWithoutDetail_TheStepIsReportedLeftDisabled_AndTheImportIsNotASuccess()
    {
        var (sut, _, step) = ReenableAlwaysFails();
        var data = Data((Table("account", disablePlugins: true), Accounts(1)));
        var messages = new List<string>();
        MigrationResult? completed = null;
        var progress = new Mock<IProgressReporter>();
        progress.Setup(p => p.Report(It.IsAny<ProgressEventArgs>())).Callback<ProgressEventArgs>(a => messages.Add(a.Message ?? ""));
        progress.Setup(p => p.Complete(It.IsAny<MigrationResult>())).Callback<MigrationResult>(r => completed = r);

        var result = await sut.ImportAsync(data, Plan("account"), new ImportOptions(), progress.Object);

        result.Success.Should().BeFalse("a step left disabled is not a successful import");
        result.PluginStepsLeftDisabled.Should().Equal(step);
        result.Warnings.Should().Contain(w => w.Code == ImportWarningCodes.PluginReenableFailed && w.Message.Contains("'audit step'"));
        messages.Should().Contain(m => m.Contains("'audit step'") && m.Contains("remains disabled"));
        messages.Should().NotContain(m => m.StartsWith("Re-enabled"), "nothing was re-enabled");
        completed!.Success.Should().BeFalse();
        completed.PluginStepsLeftDisabled.Should().Be(1);
    }

    [Fact]
    public async Task ReenableFailsWithoutDetail_OnACancelledImport_TheCancellationCarriesTheStep()
    {
        var (sut, _, step) = ReenableAlwaysFails();
        var data = Data((Table("account", disablePlugins: true), Accounts(1)));
        using var cts = new CancellationTokenSource();
        _bulk.Setup(x => x.UpsertMultipleAsync(It.IsAny<string>(), It.IsAny<IEnumerable<Entity>>(), It.IsAny<BulkOperationOptions>(), It.IsAny<DataverseClientOptions>(), It.IsAny<IProgress<ProgressSnapshot>>(), It.IsAny<CancellationToken>()))
            .Returns(() => { cts.Cancel(); throw new OperationCanceledException(cts.Token); });
        var messages = new List<string>();
        var progress = new Mock<IProgressReporter>();
        progress.Setup(p => p.Report(It.IsAny<ProgressEventArgs>())).Callback<ProgressEventArgs>(a => messages.Add(a.Message ?? ""));

        var act = () => sut.ImportAsync(data, Plan("account"), new ImportOptions(), progress.Object, cts.Token);

        var ex = (await act.Should().ThrowAsync<ImportCancelledException>()).Which;
        ex.PluginStepsLeftDisabled.Should().Equal(step);
        messages.Should().Contain(m => m.Contains("'audit step'") && m.Contains("remains disabled"));
    }

    [Fact]
    public async Task ImportThatFails_ThenAFailedReenable_TheFailureResultKeepsTheStep()
    {
        // #1934 r2 MF6: the failure result (an exception during the import) must carry the steps left disabled.
        var (sut, _, step) = ReenableAlwaysFails();
        var data = Data((Table("account", disablePlugins: true), Accounts(1)));
        _bulk.Setup(x => x.UpsertMultipleAsync(It.IsAny<string>(), It.IsAny<IEnumerable<Entity>>(), It.IsAny<BulkOperationOptions>(), It.IsAny<DataverseClientOptions>(), It.IsAny<IProgress<ProgressSnapshot>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("the service went away"));

        var result = await sut.ImportAsync(data, Plan("account"), new ImportOptions());

        result.Success.Should().BeFalse();
        result.PluginStepsLeftDisabled.Should().Equal(step);
    }

    [Fact]
    public async Task StepNeitherDisabledNorReenabled_IsReportedAsStateUnknown_NotAsDisabled()
    {
        // #1934 r2 should-fix 1: no privilege on step registrations fails both updates.
        var (sut, target, widgetStep, _) = WidgetAndGadget();
        var second = Guid.NewGuid();
        target.Steps.Add((second, "widget step 2", 10051));
        target.OnUpdate = (id, state) =>
        {
            if (id == second) throw new InvalidOperationException("no privilege");
        };
        var messages = new List<string>();
        var progress = new Mock<IProgressReporter>();
        progress.Setup(p => p.Report(It.IsAny<ProgressEventArgs>())).Callback<ProgressEventArgs>(a => messages.Add(a.Message ?? ""));

        var act = () => sut.ImportAsync(Widgets(), Plan("new_widget"), new ImportOptions(), progress.Object);

        var ex = (await act.Should().ThrowAsync<ImportRefusedException>()).Which;
        ex.PluginStepInUnknownState.Should().Be(new PluginStepInfo(second, "widget step 2"));
        ex.PluginStepsLeftDisabled.Should().BeEmpty("the step that was disabled, widget step, was re-enabled");
        target.StepUpdates.Should().Contain((widgetStep, 0), "widget step was disabled and then re-enabled");
        ex.Message.Should().Contain("its state is unknown").And.NotContain("remain disabled");
        messages.Should().Contain(m => m.Contains("'widget step 2'") && m.Contains("could not be disabled and could not be re-enabled: its state is unknown"));
        messages.Should().NotContain(m => m.Contains("'widget step 2'") && m.Contains("remains disabled"));
    }

    [Fact]
    public async Task EveryStepReenabled_TheImportSucceeds_AndSaysReenabled()
    {
        var (sut, _, _, _) = WidgetAndGadget();
        var messages = new List<string>();
        var progress = new Mock<IProgressReporter>();
        progress.Setup(p => p.Report(It.IsAny<ProgressEventArgs>())).Callback<ProgressEventArgs>(a => messages.Add(a.Message ?? ""));

        var result = await sut.ImportAsync(Widgets(), Plan("new_widget"), new ImportOptions(), progress.Object);

        result.Success.Should().BeTrue();
        result.PluginStepsLeftDisabled.Should().BeEmpty();
        messages.Should().Contain("Re-enabled 1 plugin step(s).");
    }

    [Fact]
    public async Task TheStepsToDisable_AreNamedInTheProgress_BeforeTheFirstWrite()
    {
        var (sut, target, widgetStep, _) = WidgetAndGadget();
        var events = new List<string>();
        var progress = new Mock<IProgressReporter>();
        progress.Setup(p => p.Report(It.IsAny<ProgressEventArgs>())).Callback<ProgressEventArgs>(a => events.Add("progress: " + a.Message));
        target.OnUpdate = (id, state) => events.Add($"update {id} {state}");

        await sut.ImportAsync(Widgets(), Plan("new_widget"), new ImportOptions(), progress.Object);

        var named = events.FindIndex(e => e.StartsWith("progress: Disabling 1 plugin step(s) for the import: 'widget step' (" + widgetStep + ")"));
        var firstWrite = events.FindIndex(e => e.StartsWith("update "));
        named.Should().BeGreaterThanOrEqualTo(0);
        named.Should().BeLessThan(firstWrite);
        events[named].Should().Contain("re-enable them in the target");
    }

    #endregion
    #endregion
}
