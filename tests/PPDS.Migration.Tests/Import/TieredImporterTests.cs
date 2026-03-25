using FluentAssertions;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Moq;
using PPDS.Dataverse.BulkOperations;
using PPDS.Dataverse.Pooling;
using PPDS.Dataverse.Progress;
using PPDS.Migration.Analysis;
using PPDS.Migration.Formats;
using PPDS.Migration.Import;
using PPDS.Migration.Models;
using PPDS.Migration.Progress;
using Xunit;

namespace PPDS.Migration.Tests.Import;

[Trait("Category", "Unit")]
public class TieredImporterTests
{
    private readonly Mock<IDataverseConnectionPool> _connectionPool;
    private readonly Mock<IBulkOperationExecutor> _bulkExecutor;
    private readonly Mock<ICmtDataReader> _dataReader;
    private readonly Mock<IDependencyGraphBuilder> _graphBuilder;
    private readonly Mock<IExecutionPlanBuilder> _planBuilder;
    private readonly Mock<ISchemaValidator> _schemaValidator;
    private readonly Mock<IPluginStepManager> _pluginStepManager;
    private readonly DeferredFieldProcessor _deferredFieldProcessor;
    private readonly RelationshipProcessor _relationshipProcessor;
    private readonly BulkOperationProber _prober;
    private readonly TieredImporter _sut;

    public TieredImporterTests()
    {
        _connectionPool = new Mock<IDataverseConnectionPool>();
        _bulkExecutor = new Mock<IBulkOperationExecutor>();
        _dataReader = new Mock<ICmtDataReader>();
        _graphBuilder = new Mock<IDependencyGraphBuilder>();
        _planBuilder = new Mock<IExecutionPlanBuilder>();
        _schemaValidator = new Mock<ISchemaValidator>();
        _pluginStepManager = new Mock<IPluginStepManager>();

        // Set up pool statistics to avoid null reference
        _connectionPool.Setup(p => p.Statistics).Returns(new PoolStatistics());

        _prober = new BulkOperationProber(_bulkExecutor.Object);
        _deferredFieldProcessor = new DeferredFieldProcessor(_connectionPool.Object, _prober);
        _relationshipProcessor = new RelationshipProcessor(_connectionPool.Object);

        _sut = new TieredImporter(
            _connectionPool.Object,
            _bulkExecutor.Object,
            _dataReader.Object,
            _graphBuilder.Object,
            _planBuilder.Object,
            _schemaValidator.Object,
            _deferredFieldProcessor,
            _relationshipProcessor,
            _prober,
            migrationOptions: null,
            pluginStepManager: _pluginStepManager.Object,
            logger: null);
    }

    #region Helpers

    /// <summary>
    /// Creates a MigrationData with a single entity and its records.
    /// </summary>
    private static MigrationData CreateMigrationData(string entityName, List<Entity> records, EntitySchema? entitySchema = null)
    {
        var schema = entitySchema ?? new EntitySchema
        {
            LogicalName = entityName,
            PrimaryIdField = $"{entityName}id",
            DisplayName = entityName
        };

        return new MigrationData
        {
            Schema = new MigrationSchema
            {
                Entities = new List<EntitySchema> { schema }
            },
            EntityData = new Dictionary<string, IReadOnlyList<Entity>>
            {
                [entityName] = records
            }
        };
    }

    /// <summary>
    /// Creates a MigrationData with multiple entities.
    /// </summary>
    private static MigrationData CreateMultiEntityMigrationData(
        Dictionary<string, List<Entity>> entityRecords,
        List<EntitySchema>? schemas = null)
    {
        schemas ??= entityRecords.Keys.Select(name => new EntitySchema
        {
            LogicalName = name,
            PrimaryIdField = $"{name}id",
            DisplayName = name
        }).ToList();

        return new MigrationData
        {
            Schema = new MigrationSchema
            {
                Entities = schemas
            },
            EntityData = entityRecords.ToDictionary(
                kvp => kvp.Key,
                kvp => (IReadOnlyList<Entity>)kvp.Value)
        };
    }

    /// <summary>
    /// Creates a single-tier execution plan for the given entities.
    /// </summary>
    private static ExecutionPlan CreateSingleTierPlan(params string[] entityNames)
    {
        return new ExecutionPlan
        {
            Tiers = new List<ImportTier>
            {
                new ImportTier
                {
                    TierNumber = 0,
                    Entities = entityNames.ToList()
                }
            },
            DeferredFields = new Dictionary<string, IReadOnlyList<string>>()
        };
    }

    /// <summary>
    /// Creates a multi-tier execution plan.
    /// </summary>
    private static ExecutionPlan CreateMultiTierPlan(params string[][] tiers)
    {
        return new ExecutionPlan
        {
            Tiers = tiers.Select((entities, index) => new ImportTier
            {
                TierNumber = index,
                Entities = entities.ToList()
            }).ToList(),
            DeferredFields = new Dictionary<string, IReadOnlyList<string>>()
        };
    }

    /// <summary>
    /// Creates an empty FieldMetadataCollection.
    /// </summary>
    private static FieldMetadataCollection CreateEmptyFieldMetadata()
    {
        return new FieldMetadataCollection(
            new Dictionary<string, Dictionary<string, FieldValidity>>());
    }

    /// <summary>
    /// Creates a FieldMetadataCollection with all fields valid for create and update.
    /// </summary>
    private static FieldMetadataCollection CreateFieldMetadata(
        string entityName,
        params string[] fieldNames)
    {
        var entityFields = new Dictionary<string, FieldValidity>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in fieldNames)
        {
            entityFields[field] = new FieldValidity(true, true);
        }

        return new FieldMetadataCollection(
            new Dictionary<string, Dictionary<string, FieldValidity>>
            {
                [entityName] = entityFields
            });
    }

    /// <summary>
    /// Sets up schema validator to return empty metadata and no mismatches.
    /// </summary>
    private void SetupSchemaValidatorNoMismatches(FieldMetadataCollection? metadata = null)
    {
        metadata ??= CreateEmptyFieldMetadata();

        _schemaValidator
            .Setup(v => v.LoadTargetFieldMetadataAsync(
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<IProgressReporter?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(metadata);

        _schemaValidator
            .Setup(v => v.DetectMissingColumns(
                It.IsAny<MigrationData>(),
                It.IsAny<FieldMetadataCollection>()))
            .Returns(new SchemaMismatchResult(new Dictionary<string, List<string>>()));

        _schemaValidator
            .Setup(v => v.ShouldIncludeField(
                It.IsAny<string>(),
                It.IsAny<ImportMode>(),
                It.IsAny<IReadOnlyDictionary<string, FieldValidity>?>(),
                out It.Ref<string?>.IsAny))
            .Returns(true);
    }

    /// <summary>
    /// Sets up bulk executor to return successful upsert results for a given entity.
    /// </summary>
    private void SetupBulkUpsertSuccess(string entityName, int recordCount)
    {
        // Probe call (1 record)
        var probeResult = new BulkOperationResult
        {
            SuccessCount = 1,
            FailureCount = 0,
            Errors = Array.Empty<BulkOperationError>()
        };

        // Remaining call
        var remainingResult = new BulkOperationResult
        {
            SuccessCount = Math.Max(0, recordCount - 1),
            FailureCount = 0,
            Errors = Array.Empty<BulkOperationError>()
        };

        _bulkExecutor.SetupSequence(x => x.UpsertMultipleAsync(
                entityName,
                It.IsAny<IEnumerable<Entity>>(),
                It.IsAny<BulkOperationOptions>(),
                It.IsAny<IProgress<ProgressSnapshot>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(probeResult)
            .ReturnsAsync(remainingResult);
    }

    #endregion

    #region ImportAsync_SingleTier_ImportsAllEntities

    [Fact]
    public async Task ImportAsync_SingleTier_ImportsAllEntities()
    {
        // Arrange
        var records = new List<Entity>
        {
            new Entity("account") { Id = Guid.NewGuid(), ["name"] = "Contoso" },
            new Entity("account") { Id = Guid.NewGuid(), ["name"] = "Fabrikam" },
            new Entity("account") { Id = Guid.NewGuid(), ["name"] = "Northwind" }
        };

        var data = CreateMigrationData("account", records);
        var plan = CreateSingleTierPlan("account");

        SetupSchemaValidatorNoMismatches();
        SetupBulkUpsertSuccess("account", 3);

        var options = new ImportOptions { RespectDisablePluginsSetting = false };

        // Act
        var result = await _sut.ImportAsync(data, plan, options);

        // Assert
        result.RecordsImported.Should().Be(3);
        result.TiersProcessed.Should().Be(1);
        result.SourceRecordCount.Should().Be(3);
        result.Success.Should().BeTrue();
    }

    #endregion

    #region ImportAsync_MultipleTiers_ProcessesInDependencyOrder

    [Fact]
    public async Task ImportAsync_MultipleTiers_ProcessesInDependencyOrder()
    {
        // Arrange
        var accountRecords = new List<Entity>
        {
            new Entity("account") { Id = Guid.NewGuid(), ["name"] = "Contoso" }
        };
        var contactRecords = new List<Entity>
        {
            new Entity("contact") { Id = Guid.NewGuid(), ["firstname"] = "John" }
        };

        var data = CreateMultiEntityMigrationData(new Dictionary<string, List<Entity>>
        {
            ["account"] = accountRecords,
            ["contact"] = contactRecords
        });

        // account in tier 0, contact in tier 1 - simulates dependency ordering
        var plan = CreateMultiTierPlan(
            new[] { "account" },
            new[] { "contact" });

        SetupSchemaValidatorNoMismatches();

        // Track the order in which entities are imported
        var importOrder = new List<string>();

        // account bulk setup
        _bulkExecutor.Setup(x => x.UpsertMultipleAsync(
                "account",
                It.IsAny<IEnumerable<Entity>>(),
                It.IsAny<BulkOperationOptions>(),
                It.IsAny<IProgress<ProgressSnapshot>>(),
                It.IsAny<CancellationToken>()))
            .Callback(() => importOrder.Add("account"))
            .ReturnsAsync(new BulkOperationResult
            {
                SuccessCount = 1,
                FailureCount = 0,
                Errors = Array.Empty<BulkOperationError>()
            });

        // contact bulk setup
        _bulkExecutor.Setup(x => x.UpsertMultipleAsync(
                "contact",
                It.IsAny<IEnumerable<Entity>>(),
                It.IsAny<BulkOperationOptions>(),
                It.IsAny<IProgress<ProgressSnapshot>>(),
                It.IsAny<CancellationToken>()))
            .Callback(() => importOrder.Add("contact"))
            .ReturnsAsync(new BulkOperationResult
            {
                SuccessCount = 1,
                FailureCount = 0,
                Errors = Array.Empty<BulkOperationError>()
            });

        var options = new ImportOptions { RespectDisablePluginsSetting = false };

        // Act
        var result = await _sut.ImportAsync(data, plan, options);

        // Assert
        result.RecordsImported.Should().Be(2);
        result.TiersProcessed.Should().Be(2);
        // Verify account was imported before contact (tier 0 before tier 1)
        importOrder.Should().ContainInOrder("account", "contact");
    }

    #endregion

    #region ImportAsync_ParallelEntityImport_RespectsParallelism

    [Fact]
    public async Task ImportAsync_ParallelEntityImport_RespectsParallelism()
    {
        // Arrange: 3 entities in the same tier, parallelism limited to 1
        var entity1Records = new List<Entity>
        {
            new Entity("entity1") { Id = Guid.NewGuid() }
        };
        var entity2Records = new List<Entity>
        {
            new Entity("entity2") { Id = Guid.NewGuid() }
        };
        var entity3Records = new List<Entity>
        {
            new Entity("entity3") { Id = Guid.NewGuid() }
        };

        var data = CreateMultiEntityMigrationData(new Dictionary<string, List<Entity>>
        {
            ["entity1"] = entity1Records,
            ["entity2"] = entity2Records,
            ["entity3"] = entity3Records
        });

        var plan = CreateSingleTierPlan("entity1", "entity2", "entity3");

        SetupSchemaValidatorNoMismatches();

        var concurrentCount = 0;
        var maxConcurrent = 0;
        var lockObj = new object();

        void TrackConcurrency()
        {
            lock (lockObj)
            {
                concurrentCount++;
                if (concurrentCount > maxConcurrent) maxConcurrent = concurrentCount;
            }
        }

        void ReleaseConcurrency()
        {
            lock (lockObj)
            {
                concurrentCount--;
            }
        }

        // Setup bulk executor for all 3 entities
        foreach (var entityName in new[] { "entity1", "entity2", "entity3" })
        {
            _bulkExecutor.Setup(x => x.UpsertMultipleAsync(
                    entityName,
                    It.IsAny<IEnumerable<Entity>>(),
                    It.IsAny<BulkOperationOptions>(),
                    It.IsAny<IProgress<ProgressSnapshot>>(),
                    It.IsAny<CancellationToken>()))
                .Returns(async () =>
                {
                    TrackConcurrency();
                    await Task.Delay(50); // Small delay to allow concurrent execution
                    ReleaseConcurrency();
                    return new BulkOperationResult
                    {
                        SuccessCount = 1,
                        FailureCount = 0,
                        Errors = Array.Empty<BulkOperationError>()
                    };
                });
        }

        var options = new ImportOptions
        {
            MaxParallelEntities = 1,
            RespectDisablePluginsSetting = false
        };

        // Act
        var result = await _sut.ImportAsync(data, plan, options);

        // Assert
        result.RecordsImported.Should().Be(3);
        // With MaxParallelEntities = 1, max concurrent should be exactly 1
        maxConcurrent.Should().Be(1);
    }

    #endregion

    #region ImportAsync_SchemaValidation_SkipsMissingColumns

    [Fact]
    public async Task ImportAsync_SchemaValidation_SkipsMissingColumns()
    {
        // Arrange
        var records = new List<Entity>
        {
            new Entity("account") { Id = Guid.NewGuid(), ["name"] = "Contoso", ["customfield"] = "value" }
        };

        var data = CreateMigrationData("account", records);
        var plan = CreateSingleTierPlan("account");

        var metadata = CreateEmptyFieldMetadata();

        _schemaValidator
            .Setup(v => v.LoadTargetFieldMetadataAsync(
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<IProgressReporter?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(metadata);

        // Schema mismatch detected: "customfield" is missing
        var missingColumns = new Dictionary<string, List<string>>
        {
            ["account"] = new List<string> { "customfield" }
        };

        _schemaValidator
            .Setup(v => v.DetectMissingColumns(
                It.IsAny<MigrationData>(),
                It.IsAny<FieldMetadataCollection>()))
            .Returns(new SchemaMismatchResult(missingColumns));

        _schemaValidator
            .Setup(v => v.ShouldIncludeField(
                It.IsAny<string>(),
                It.IsAny<ImportMode>(),
                It.IsAny<IReadOnlyDictionary<string, FieldValidity>?>(),
                out It.Ref<string?>.IsAny))
            .Returns(true);

        SetupBulkUpsertSuccess("account", 1);

        // SkipMissingColumns = true means import should proceed with a warning
        var options = new ImportOptions
        {
            SkipMissingColumns = true,
            RespectDisablePluginsSetting = false
        };

        // Act
        var result = await _sut.ImportAsync(data, plan, options);

        // Assert
        result.Success.Should().BeTrue();
        result.Warnings.Should().Contain(w => w.Code == ImportWarningCodes.ColumnSkipped);
    }

    [Fact]
    public async Task ImportAsync_SchemaValidation_ThrowsWhenSkipMissingColumnsIsFalse()
    {
        // Arrange
        var records = new List<Entity>
        {
            new Entity("account") { Id = Guid.NewGuid(), ["name"] = "Contoso" }
        };

        var data = CreateMigrationData("account", records);
        var plan = CreateSingleTierPlan("account");

        var metadata = CreateEmptyFieldMetadata();

        _schemaValidator
            .Setup(v => v.LoadTargetFieldMetadataAsync(
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<IProgressReporter?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(metadata);

        var missingColumns = new Dictionary<string, List<string>>
        {
            ["account"] = new List<string> { "customfield" }
        };

        _schemaValidator
            .Setup(v => v.DetectMissingColumns(
                It.IsAny<MigrationData>(),
                It.IsAny<FieldMetadataCollection>()))
            .Returns(new SchemaMismatchResult(missingColumns));

        // SkipMissingColumns = false (default) - should throw
        var options = new ImportOptions
        {
            SkipMissingColumns = false,
            RespectDisablePluginsSetting = false
        };

        // Act
        var act = async () => await _sut.ImportAsync(data, plan, options);

        // Assert
        await act.Should().ThrowAsync<SchemaMismatchException>();
    }

    #endregion

    #region ImportAsync_PluginDisable_DisablesBeforeImportEnablesAfter

    [Fact]
    public async Task ImportAsync_PluginDisable_DisablesBeforeImportEnablesAfter()
    {
        // Arrange
        var records = new List<Entity>
        {
            new Entity("account") { Id = Guid.NewGuid(), ["name"] = "Contoso" }
        };

        var entitySchema = new EntitySchema
        {
            LogicalName = "account",
            PrimaryIdField = "accountid",
            DisplayName = "Account",
            DisablePlugins = true,
            ObjectTypeCode = 1
        };

        var data = CreateMigrationData("account", records, entitySchema);
        var plan = CreateSingleTierPlan("account");

        SetupSchemaValidatorNoMismatches();
        SetupBulkUpsertSuccess("account", 1);

        var pluginStepIds = new List<Guid> { Guid.NewGuid(), Guid.NewGuid() };

        _pluginStepManager
            .Setup(p => p.GetActivePluginStepsAsync(
                It.IsAny<IEnumerable<int>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(pluginStepIds);

        _pluginStepManager
            .Setup(p => p.DisablePluginStepsAsync(
                It.IsAny<IEnumerable<Guid>>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _pluginStepManager
            .Setup(p => p.EnablePluginStepsAsync(
                It.IsAny<IEnumerable<Guid>>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var options = new ImportOptions { RespectDisablePluginsSetting = true };

        // Act
        var result = await _sut.ImportAsync(data, plan, options);

        // Assert
        result.Success.Should().BeTrue();

        // Verify plugins were disabled before import
        _pluginStepManager.Verify(p => p.DisablePluginStepsAsync(
            It.Is<IEnumerable<Guid>>(ids => ids.Count() == 2),
            It.IsAny<CancellationToken>()), Times.Once);

        // Verify plugins were re-enabled after import
        _pluginStepManager.Verify(p => p.EnablePluginStepsAsync(
            It.Is<IEnumerable<Guid>>(ids => ids.Count() == 2),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ImportAsync_PluginDisable_ReenablesEvenOnFailure()
    {
        // Arrange
        var records = new List<Entity>
        {
            new Entity("account") { Id = Guid.NewGuid(), ["name"] = "Contoso" }
        };

        var entitySchema = new EntitySchema
        {
            LogicalName = "account",
            PrimaryIdField = "accountid",
            DisplayName = "Account",
            DisablePlugins = true,
            ObjectTypeCode = 1
        };

        var data = CreateMigrationData("account", records, entitySchema);
        var plan = CreateSingleTierPlan("account");

        SetupSchemaValidatorNoMismatches();

        var pluginStepIds = new List<Guid> { Guid.NewGuid() };

        _pluginStepManager
            .Setup(p => p.GetActivePluginStepsAsync(
                It.IsAny<IEnumerable<int>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(pluginStepIds);

        _pluginStepManager
            .Setup(p => p.DisablePluginStepsAsync(
                It.IsAny<IEnumerable<Guid>>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _pluginStepManager
            .Setup(p => p.EnablePluginStepsAsync(
                It.IsAny<IEnumerable<Guid>>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Simulate a bulk operation failure
        _bulkExecutor.Setup(x => x.UpsertMultipleAsync(
                "account",
                It.IsAny<IEnumerable<Entity>>(),
                It.IsAny<BulkOperationOptions>(),
                It.IsAny<IProgress<ProgressSnapshot>>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Simulated failure"));

        var options = new ImportOptions { RespectDisablePluginsSetting = true };

        // Act
        var result = await _sut.ImportAsync(data, plan, options);

        // Assert
        result.Success.Should().BeFalse();

        // Plugins should still be re-enabled even though import failed
        _pluginStepManager.Verify(p => p.EnablePluginStepsAsync(
            It.IsAny<IEnumerable<Guid>>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    #endregion

    #region PrepareRecordForImport_RemapsEntityReferences

    [Fact]
    public async Task PrepareRecordForImport_RemapsEntityReferences()
    {
        // Arrange: contact references an account via a lookup. The account was already
        // imported so its ID mapping exists.
        var accountId = Guid.NewGuid();
        var contactId = Guid.NewGuid();

        var accountRecords = new List<Entity>
        {
            new Entity("account") { Id = accountId, ["name"] = "Contoso" }
        };
        var contactRecords = new List<Entity>
        {
            new Entity("contact")
            {
                Id = contactId,
                ["firstname"] = "John",
                ["parentcustomerid"] = new EntityReference("account", accountId)
            }
        };

        var data = CreateMultiEntityMigrationData(new Dictionary<string, List<Entity>>
        {
            ["account"] = accountRecords,
            ["contact"] = contactRecords
        });

        // account in tier 0, contact in tier 1
        var plan = CreateMultiTierPlan(
            new[] { "account" },
            new[] { "contact" });

        SetupSchemaValidatorNoMismatches();

        // Account bulk setup - single record, probe succeeds
        _bulkExecutor.Setup(x => x.UpsertMultipleAsync(
                "account",
                It.IsAny<IEnumerable<Entity>>(),
                It.IsAny<BulkOperationOptions>(),
                It.IsAny<IProgress<ProgressSnapshot>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BulkOperationResult
            {
                SuccessCount = 1,
                FailureCount = 0,
                Errors = Array.Empty<BulkOperationError>()
            });

        // Contact bulk setup - capture the prepared entity to verify remapping
        Entity? capturedContact = null;
        _bulkExecutor.Setup(x => x.UpsertMultipleAsync(
                "contact",
                It.IsAny<IEnumerable<Entity>>(),
                It.IsAny<BulkOperationOptions>(),
                It.IsAny<IProgress<ProgressSnapshot>>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, IEnumerable<Entity>, BulkOperationOptions, IProgress<ProgressSnapshot>?, CancellationToken>(
                (_, entities, _, _, _) => capturedContact = entities.FirstOrDefault())
            .ReturnsAsync(new BulkOperationResult
            {
                SuccessCount = 1,
                FailureCount = 0,
                Errors = Array.Empty<BulkOperationError>()
            });

        var options = new ImportOptions { RespectDisablePluginsSetting = false };

        // Act
        var result = await _sut.ImportAsync(data, plan, options);

        // Assert
        result.RecordsImported.Should().Be(2);
        // The contact's parentcustomerid should still reference the account
        // (in this case IDs are preserved since TieredImporter maps oldId -> oldId)
        capturedContact.Should().NotBeNull();
        capturedContact!.Contains("parentcustomerid").Should().BeTrue();
        var lookup = capturedContact["parentcustomerid"] as EntityReference
            ?? throw new InvalidOperationException("parentcustomerid should be an EntityReference");
        lookup.LogicalName.Should().Be("account");
        lookup.Id.Should().Be(accountId);
    }

    #endregion

    #region PrepareRecordForImport_StripsOwnerField_WhenConfigured

    [Fact]
    public async Task PrepareRecordForImport_StripsOwnerField_WhenConfigured()
    {
        // Arrange
        var ownerId = Guid.NewGuid();
        var records = new List<Entity>
        {
            new Entity("account")
            {
                Id = Guid.NewGuid(),
                ["name"] = "Contoso",
                ["ownerid"] = new EntityReference("systemuser", ownerId),
                ["createdby"] = new EntityReference("systemuser", ownerId),
                ["modifiedby"] = new EntityReference("systemuser", ownerId)
            }
        };

        var data = CreateMigrationData("account", records);
        var plan = CreateSingleTierPlan("account");

        SetupSchemaValidatorNoMismatches();

        // Capture what gets sent to bulk executor
        Entity? capturedEntity = null;
        _bulkExecutor.Setup(x => x.UpsertMultipleAsync(
                "account",
                It.IsAny<IEnumerable<Entity>>(),
                It.IsAny<BulkOperationOptions>(),
                It.IsAny<IProgress<ProgressSnapshot>>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, IEnumerable<Entity>, BulkOperationOptions, IProgress<ProgressSnapshot>?, CancellationToken>(
                (_, entities, _, _, _) => capturedEntity = entities.FirstOrDefault())
            .ReturnsAsync(new BulkOperationResult
            {
                SuccessCount = 1,
                FailureCount = 0,
                Errors = Array.Empty<BulkOperationError>()
            });

        var options = new ImportOptions
        {
            StripOwnerFields = true,
            RespectDisablePluginsSetting = false
        };

        // Act
        var result = await _sut.ImportAsync(data, plan, options);

        // Assert
        result.RecordsImported.Should().Be(1);
        capturedEntity.Should().NotBeNull();
        capturedEntity!.Contains("ownerid").Should().BeFalse("ownerid should be stripped");
        capturedEntity.Contains("createdby").Should().BeFalse("createdby should be stripped");
        capturedEntity.Contains("modifiedby").Should().BeFalse("modifiedby should be stripped");
        capturedEntity.Contains("name").Should().BeTrue("non-owner fields should be preserved");
    }

    #endregion

    #region RemapEntityReference_UsesUserMapping_WhenAvailable

    [Fact]
    public async Task RemapEntityReference_UsesUserMapping_WhenAvailable()
    {
        // Arrange
        var sourceUserId = Guid.NewGuid();
        var targetUserId = Guid.NewGuid();

        var records = new List<Entity>
        {
            new Entity("account")
            {
                Id = Guid.NewGuid(),
                ["name"] = "Contoso",
                ["ownerid"] = new EntityReference("systemuser", sourceUserId)
            }
        };

        var data = CreateMigrationData("account", records);
        var plan = CreateSingleTierPlan("account");

        SetupSchemaValidatorNoMismatches();

        Entity? capturedEntity = null;
        _bulkExecutor.Setup(x => x.UpsertMultipleAsync(
                "account",
                It.IsAny<IEnumerable<Entity>>(),
                It.IsAny<BulkOperationOptions>(),
                It.IsAny<IProgress<ProgressSnapshot>>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, IEnumerable<Entity>, BulkOperationOptions, IProgress<ProgressSnapshot>?, CancellationToken>(
                (_, entities, _, _, _) => capturedEntity = entities.FirstOrDefault())
            .ReturnsAsync(new BulkOperationResult
            {
                SuccessCount = 1,
                FailureCount = 0,
                Errors = Array.Empty<BulkOperationError>()
            });

        var userMappings = new UserMappingCollection
        {
            Mappings = new Dictionary<Guid, PPDS.Migration.Models.UserMapping>
            {
                [sourceUserId] = new PPDS.Migration.Models.UserMapping
                {
                    SourceUserId = sourceUserId,
                    TargetUserId = targetUserId
                }
            }
        };

        var options = new ImportOptions
        {
            UserMappings = userMappings,
            StripOwnerFields = false,
            RespectDisablePluginsSetting = false
        };

        // Act
        var result = await _sut.ImportAsync(data, plan, options);

        // Assert
        result.RecordsImported.Should().Be(1);
        capturedEntity.Should().NotBeNull();
        capturedEntity!.Contains("ownerid").Should().BeTrue();
        var ownerRef = capturedEntity["ownerid"] as EntityReference
            ?? throw new InvalidOperationException("ownerid should be an EntityReference");
        ownerRef.Id.Should().Be(targetUserId, "user mapping should remap ownerid to the target user");
    }

    [Fact]
    public async Task RemapEntityReference_UsesCurrentUserFallback_WhenNoExplicitMapping()
    {
        // Arrange
        var sourceUserId = Guid.NewGuid();
        var currentUserId = Guid.NewGuid();

        var records = new List<Entity>
        {
            new Entity("account")
            {
                Id = Guid.NewGuid(),
                ["name"] = "Contoso",
                ["ownerid"] = new EntityReference("systemuser", sourceUserId)
            }
        };

        var data = CreateMigrationData("account", records);
        var plan = CreateSingleTierPlan("account");

        SetupSchemaValidatorNoMismatches();

        Entity? capturedEntity = null;
        _bulkExecutor.Setup(x => x.UpsertMultipleAsync(
                "account",
                It.IsAny<IEnumerable<Entity>>(),
                It.IsAny<BulkOperationOptions>(),
                It.IsAny<IProgress<ProgressSnapshot>>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, IEnumerable<Entity>, BulkOperationOptions, IProgress<ProgressSnapshot>?, CancellationToken>(
                (_, entities, _, _, _) => capturedEntity = entities.FirstOrDefault())
            .ReturnsAsync(new BulkOperationResult
            {
                SuccessCount = 1,
                FailureCount = 0,
                Errors = Array.Empty<BulkOperationError>()
            });

        var userMappings = new UserMappingCollection
        {
            Mappings = new Dictionary<Guid, PPDS.Migration.Models.UserMapping>(), // No explicit mappings
            UseCurrentUserAsDefault = true
        };

        var options = new ImportOptions
        {
            UserMappings = userMappings,
            CurrentUserId = currentUserId,
            StripOwnerFields = false,
            RespectDisablePluginsSetting = false
        };

        // Act
        var result = await _sut.ImportAsync(data, plan, options);

        // Assert
        result.RecordsImported.Should().Be(1);
        capturedEntity.Should().NotBeNull();
        var ownerRef = capturedEntity!["ownerid"] as EntityReference
            ?? throw new InvalidOperationException("ownerid should be an EntityReference");
        ownerRef.Id.Should().Be(currentUserId, "should fall back to current user when no explicit mapping exists");
        ownerRef.LogicalName.Should().Be("systemuser");
    }

    #endregion

    #region ImportAsync_BulkOperation_FallsBackToIndividual

    [Fact]
    public async Task ImportAsync_BulkOperation_FallsBackToIndividual()
    {
        // Arrange
        var records = new List<Entity>
        {
            new Entity("team") { Id = Guid.NewGuid(), ["name"] = "Team A" },
            new Entity("team") { Id = Guid.NewGuid(), ["name"] = "Team B" }
        };

        var data = CreateMigrationData("team", records);
        var plan = CreateSingleTierPlan("team");

        SetupSchemaValidatorNoMismatches();

        // Probe fails with "not supported" error
        _bulkExecutor.Setup(x => x.UpsertMultipleAsync(
                "team",
                It.IsAny<IEnumerable<Entity>>(),
                It.IsAny<BulkOperationOptions>(),
                It.IsAny<IProgress<ProgressSnapshot>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BulkOperationResult
            {
                SuccessCount = 0,
                FailureCount = 1,
                Errors = new List<BulkOperationError>
                {
                    new BulkOperationError
                    {
                        Index = 0,
                        ErrorCode = -1,
                        Message = "UpsertMultiple is not enabled on the entity team"
                    }
                }
            });

        // Set up the pooled client for individual fallback operations
        var mockPooledClient = new Mock<IPooledClient>();
        mockPooledClient
            .Setup(c => c.ExecuteAsync(It.IsAny<OrganizationRequest>()))
            .ReturnsAsync(new UpsertResponse
            {
                Results = { ["Target"] = new EntityReference("team", Guid.NewGuid()) }
            });

        _connectionPool
            .Setup(p => p.GetClientAsync(
                It.IsAny<Dataverse.Client.DataverseClientOptions?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockPooledClient.Object);

        var options = new ImportOptions
        {
            UseBulkApis = true,
            RespectDisablePluginsSetting = false
        };

        // Act
        var result = await _sut.ImportAsync(data, plan, options);

        // Assert
        result.RecordsImported.Should().Be(2);
        result.Warnings.Should().Contain(w => w.Code == ImportWarningCodes.BulkNotSupported);
    }

    #endregion

    #region ImportAsync_CancellationToken_StopsProcessing

    [Fact]
    public async Task ImportAsync_CancellationToken_StopsProcessing()
    {
        // Arrange
        var records = new List<Entity>
        {
            new Entity("account") { Id = Guid.NewGuid(), ["name"] = "Contoso" }
        };

        var data = CreateMigrationData("account", records);
        var plan = CreateSingleTierPlan("account");

        SetupSchemaValidatorNoMismatches();

        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Cancel immediately

        var options = new ImportOptions { RespectDisablePluginsSetting = false };

        // Act
        var act = async () => await _sut.ImportAsync(data, plan, options, cancellationToken: cts.Token);

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    #endregion

    #region ImportAsync_ProgressReporter_ReportsPerEntity

    [Fact]
    public async Task ImportAsync_ProgressReporter_ReportsPerEntity()
    {
        // Arrange
        var records = new List<Entity>
        {
            new Entity("account") { Id = Guid.NewGuid(), ["name"] = "Contoso" },
            new Entity("account") { Id = Guid.NewGuid(), ["name"] = "Fabrikam" }
        };

        var data = CreateMigrationData("account", records);
        var plan = CreateSingleTierPlan("account");

        SetupSchemaValidatorNoMismatches();
        SetupBulkUpsertSuccess("account", 2);

        var progressReporter = new Mock<IProgressReporter>();
        var progressCalls = new List<ProgressEventArgs>();
        progressReporter
            .Setup(p => p.Report(It.IsAny<ProgressEventArgs>()))
            .Callback<ProgressEventArgs>(args => progressCalls.Add(args));

        var options = new ImportOptions { RespectDisablePluginsSetting = false };

        // Act
        var result = await _sut.ImportAsync(data, plan, options, progressReporter.Object);

        // Assert
        result.RecordsImported.Should().Be(2);
        progressReporter.Verify(p => p.Report(It.IsAny<ProgressEventArgs>()), Times.AtLeastOnce);

        // Verify that importing phase was reported (tier processing reports)
        progressCalls.Should().Contain(p => p.Phase == MigrationPhase.Importing);
        // Verify tier number is included in progress reports
        progressCalls.Should().Contain(p => p.TierNumber == 0);
        // Verify Complete was called with import result
        progressReporter.Verify(p => p.Complete(It.IsAny<MigrationResult>()), Times.Once);
    }

    #endregion

    #region ImportAsync_ErrorHandling_ContinuesOnEntityFailure

    [Fact]
    public async Task ImportAsync_ErrorHandling_ContinuesOnEntityFailure()
    {
        // Arrange
        var accountRecords = new List<Entity>
        {
            new Entity("account") { Id = Guid.NewGuid(), ["name"] = "Contoso" }
        };
        var contactRecords = new List<Entity>
        {
            new Entity("contact") { Id = Guid.NewGuid(), ["firstname"] = "John" }
        };

        var data = CreateMultiEntityMigrationData(new Dictionary<string, List<Entity>>
        {
            ["account"] = accountRecords,
            ["contact"] = contactRecords
        });

        // Both entities in tier 0 to test error handling within a tier
        var plan = CreateSingleTierPlan("account", "contact");

        SetupSchemaValidatorNoMismatches();

        // Account import succeeds
        _bulkExecutor.Setup(x => x.UpsertMultipleAsync(
                "account",
                It.IsAny<IEnumerable<Entity>>(),
                It.IsAny<BulkOperationOptions>(),
                It.IsAny<IProgress<ProgressSnapshot>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BulkOperationResult
            {
                SuccessCount = 1,
                FailureCount = 0,
                Errors = Array.Empty<BulkOperationError>()
            });

        // Contact import has a failure but continues
        _bulkExecutor.Setup(x => x.UpsertMultipleAsync(
                "contact",
                It.IsAny<IEnumerable<Entity>>(),
                It.IsAny<BulkOperationOptions>(),
                It.IsAny<IProgress<ProgressSnapshot>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BulkOperationResult
            {
                SuccessCount = 0,
                FailureCount = 1,
                Errors = new List<BulkOperationError>
                {
                    new BulkOperationError
                    {
                        Index = 0,
                        ErrorCode = -2147220891,
                        Message = "Duplicate record detected"
                    }
                }
            });

        var options = new ImportOptions
        {
            ContinueOnError = true,
            RespectDisablePluginsSetting = false
        };

        // Act
        var result = await _sut.ImportAsync(data, plan, options);

        // Assert
        // Account succeeded (1 record), contact failed (0 records)
        result.RecordsImported.Should().Be(1);
        result.Success.Should().BeFalse("there were errors");
        result.Errors.Should().HaveCountGreaterThan(0);
        result.Errors.Should().Contain(e => e.EntityLogicalName == "contact");
        result.EntityResults.Should().HaveCount(2, "both entities were processed");
    }

    [Fact]
    public async Task ImportAsync_ErrorHandling_CatchesExceptionAndReturnsFailureResult()
    {
        // Arrange
        var records = new List<Entity>
        {
            new Entity("account") { Id = Guid.NewGuid(), ["name"] = "Contoso" }
        };

        var data = CreateMigrationData("account", records);
        var plan = CreateSingleTierPlan("account");

        SetupSchemaValidatorNoMismatches();

        // Simulate an unrecoverable exception during import
        _bulkExecutor.Setup(x => x.UpsertMultipleAsync(
                "account",
                It.IsAny<IEnumerable<Entity>>(),
                It.IsAny<BulkOperationOptions>(),
                It.IsAny<IProgress<ProgressSnapshot>>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Connection pool exhausted"));

        var options = new ImportOptions { RespectDisablePluginsSetting = false };

        // Act
        var result = await _sut.ImportAsync(data, plan, options);

        // Assert
        result.Success.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Message.Contains("Connection pool exhausted"));
    }

    #endregion

    #region Additional Edge Cases

    [Fact]
    public async Task ImportAsync_NullData_ThrowsArgumentNullException()
    {
        // Act
        var act = async () => await _sut.ImportAsync(
            (MigrationData)null!,
            CreateSingleTierPlan("account"));

        // Assert
        await act.Should().ThrowAsync<ArgumentNullException>()
            .WithParameterName("data");
    }

    [Fact]
    public async Task ImportAsync_NullPlan_ThrowsArgumentNullException()
    {
        // Arrange
        var data = CreateMigrationData("account", new List<Entity>());

        // Act
        var act = async () => await _sut.ImportAsync(data, null!);

        // Assert
        await act.Should().ThrowAsync<ArgumentNullException>()
            .WithParameterName("plan");
    }

    [Fact]
    public async Task ImportAsync_EmptyEntityData_ReturnsSuccessWithZeroRecords()
    {
        // Arrange
        var data = CreateMigrationData("account", new List<Entity>());
        // Plan references "account" but there are no records
        var plan = CreateSingleTierPlan("account");

        SetupSchemaValidatorNoMismatches();

        var options = new ImportOptions { RespectDisablePluginsSetting = false };

        // Act
        var result = await _sut.ImportAsync(data, plan, options);

        // Assert
        result.Success.Should().BeTrue();
        result.RecordsImported.Should().Be(0);
    }

    [Fact]
    public async Task ImportAsync_TeamEntity_ForcesIsDefaultToFalse()
    {
        // Arrange
        var records = new List<Entity>
        {
            new Entity("team")
            {
                Id = Guid.NewGuid(),
                ["name"] = "Default Team",
                ["isdefault"] = true
            }
        };

        var data = CreateMigrationData("team", records);
        var plan = CreateSingleTierPlan("team");

        SetupSchemaValidatorNoMismatches();

        Entity? capturedEntity = null;
        _bulkExecutor.Setup(x => x.UpsertMultipleAsync(
                "team",
                It.IsAny<IEnumerable<Entity>>(),
                It.IsAny<BulkOperationOptions>(),
                It.IsAny<IProgress<ProgressSnapshot>>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, IEnumerable<Entity>, BulkOperationOptions, IProgress<ProgressSnapshot>?, CancellationToken>(
                (_, entities, _, _, _) => capturedEntity = entities.FirstOrDefault())
            .ReturnsAsync(new BulkOperationResult
            {
                SuccessCount = 1,
                FailureCount = 0,
                Errors = Array.Empty<BulkOperationError>()
            });

        var options = new ImportOptions { RespectDisablePluginsSetting = false };

        // Act
        var result = await _sut.ImportAsync(data, plan, options);

        // Assert
        result.RecordsImported.Should().Be(1);
        capturedEntity.Should().NotBeNull();
        capturedEntity!["isdefault"].Should().Be(false, "team.isdefault should be forced to false during import");
    }

    [Fact]
    public async Task ImportAsync_UseBulkApisFalse_UsesIndividualOperations()
    {
        // Arrange
        var records = new List<Entity>
        {
            new Entity("account") { Id = Guid.NewGuid(), ["name"] = "Contoso" }
        };

        var data = CreateMigrationData("account", records);
        var plan = CreateSingleTierPlan("account");

        SetupSchemaValidatorNoMismatches();

        // Set up pooled client for individual operations
        var mockPooledClient = new Mock<IPooledClient>();
        mockPooledClient
            .Setup(c => c.ExecuteAsync(It.IsAny<OrganizationRequest>()))
            .ReturnsAsync(new UpsertResponse
            {
                Results = { ["Target"] = new EntityReference("account", records[0].Id) }
            });

        _connectionPool
            .Setup(p => p.GetClientAsync(
                It.IsAny<Dataverse.Client.DataverseClientOptions?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockPooledClient.Object);

        var options = new ImportOptions
        {
            UseBulkApis = false,
            RespectDisablePluginsSetting = false
        };

        // Act
        var result = await _sut.ImportAsync(data, plan, options);

        // Assert
        result.RecordsImported.Should().Be(1);
        // Bulk executor should never be called
        _bulkExecutor.Verify(x => x.UpsertMultipleAsync(
            It.IsAny<string>(),
            It.IsAny<IEnumerable<Entity>>(),
            It.IsAny<BulkOperationOptions>(),
            It.IsAny<IProgress<ProgressSnapshot>>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    #endregion
}
