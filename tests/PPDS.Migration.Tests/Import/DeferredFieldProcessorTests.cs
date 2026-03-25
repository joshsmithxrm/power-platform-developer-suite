using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Xrm.Sdk;
using Moq;
using PPDS.Dataverse.BulkOperations;
using PPDS.Dataverse.Pooling;
using PPDS.Dataverse.Progress;
using PPDS.Migration.Import;
using PPDS.Migration.Models;
using PPDS.Migration.Progress;
using Xunit;

namespace PPDS.Migration.Tests.Import;

[Trait("Category", "Unit")]
public class DeferredFieldProcessorTests
{
    private readonly Mock<IDataverseConnectionPool> _pool;
    private readonly Mock<IPooledClient> _client;
    private readonly Mock<IBulkOperationExecutor> _bulkExecutor;
    private readonly BulkOperationProber _prober;
    private readonly DeferredFieldProcessor _sut;

    public DeferredFieldProcessorTests()
    {
        _pool = new Mock<IDataverseConnectionPool>();
        _client = new Mock<IPooledClient>();
        _bulkExecutor = new Mock<IBulkOperationExecutor>();
        _prober = new BulkOperationProber(_bulkExecutor.Object);

        _pool.Setup(p => p.GetClientAsync(It.IsAny<Dataverse.Client.DataverseClientOptions?>(),
                It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(_client.Object);

        _sut = new DeferredFieldProcessor(_pool.Object, _prober);
    }

    #region ProcessAsync — core deferred update flow

    [Fact]
    public async Task ProcessAsync_UpdatesDeferredFields_WithRemappedIds()
    {
        // Arrange
        var oldAccountId = Guid.NewGuid();
        var newAccountId = Guid.NewGuid();
        var oldParentId = Guid.NewGuid();
        var newParentId = Guid.NewGuid();

        var idMappings = new IdMappingCollection();
        idMappings.AddMapping("account", oldAccountId, newAccountId);
        idMappings.AddMapping("account", oldParentId, newParentId);

        // Source record has a self-referential parentaccountid lookup
        var sourceRecord = new Entity("account", oldAccountId);
        sourceRecord["parentaccountid"] = new EntityReference("account", oldParentId);

        var context = CreateContext(
            deferredFields: new Dictionary<string, IReadOnlyList<string>>
            {
                ["account"] = new List<string> { "parentaccountid" }
            },
            entityData: new Dictionary<string, IReadOnlyList<Entity>>
            {
                ["account"] = new List<Entity> { sourceRecord }
            },
            idMappings: idMappings);

        // Setup: bulk probe succeeds with the update
        _bulkExecutor.Setup(x => x.UpdateMultipleAsync(
                "account",
                It.IsAny<IEnumerable<Entity>>(),
                It.IsAny<BulkOperationOptions>(),
                It.IsAny<IProgress<ProgressSnapshot>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, IEnumerable<Entity> entities, BulkOperationOptions _, IProgress<ProgressSnapshot> _, CancellationToken _) =>
            {
                var list = entities.ToList();
                return new BulkOperationResult
                {
                    SuccessCount = list.Count,
                    FailureCount = 0,
                    Errors = Array.Empty<BulkOperationError>()
                };
            });

        // Act
        var result = await _sut.ProcessAsync(context, CancellationToken.None);

        // Assert
        result.Success.Should().BeTrue();
        result.SuccessCount.Should().BeGreaterThan(0);

        // Verify UpdateMultipleAsync was called (bulk path via prober)
        _bulkExecutor.Verify(x => x.UpdateMultipleAsync(
            "account",
            It.IsAny<IEnumerable<Entity>>(),
            It.IsAny<BulkOperationOptions>(),
            It.IsAny<IProgress<ProgressSnapshot>>(),
            It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    #endregion

    #region ProcessAsync — bulk path

    [Fact]
    public async Task ProcessAsync_UsesBulkUpdate_WhenSupported()
    {
        // Arrange
        var records = new List<Entity>();
        var idMappings = new IdMappingCollection();

        for (var i = 0; i < 5; i++)
        {
            var oldId = Guid.NewGuid();
            var newId = Guid.NewGuid();
            var oldRefId = Guid.NewGuid();
            var newRefId = Guid.NewGuid();

            idMappings.AddMapping("account", oldId, newId);
            idMappings.AddMapping("account", oldRefId, newRefId);

            var record = new Entity("account", oldId);
            record["parentaccountid"] = new EntityReference("account", oldRefId);
            records.Add(record);
        }

        var context = CreateContext(
            deferredFields: new Dictionary<string, IReadOnlyList<string>>
            {
                ["account"] = new List<string> { "parentaccountid" }
            },
            entityData: new Dictionary<string, IReadOnlyList<Entity>>
            {
                ["account"] = records
            },
            idMappings: idMappings);

        // Capture all entities passed to UpdateMultipleAsync regardless of batching strategy
        var allUpdatedEntities = new List<Entity>();
        _bulkExecutor.Setup(x => x.UpdateMultipleAsync(
                "account",
                It.IsAny<IEnumerable<Entity>>(),
                It.IsAny<BulkOperationOptions>(),
                It.IsAny<IProgress<ProgressSnapshot>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, IEnumerable<Entity> entities, BulkOperationOptions _, IProgress<ProgressSnapshot> _, CancellationToken _) =>
            {
                var batch = entities.ToList();
                allUpdatedEntities.AddRange(batch);
                return new BulkOperationResult
                {
                    SuccessCount = batch.Count,
                    FailureCount = 0,
                    Errors = Array.Empty<BulkOperationError>()
                };
            });

        // Act
        var result = await _sut.ProcessAsync(context, CancellationToken.None);

        // Assert — verify outcome (all 5 records updated) not call count
        result.Success.Should().BeTrue();
        result.SuccessCount.Should().Be(5);
        result.FailureCount.Should().Be(0);
        allUpdatedEntities.Should().HaveCount(5, "all 5 deferred field records should be updated via bulk path");

        // Verify bulk path was used (at least once — don't prescribe exact batching)
        _bulkExecutor.Verify(x => x.UpdateMultipleAsync(
            "account",
            It.IsAny<IEnumerable<Entity>>(),
            It.IsAny<BulkOperationOptions>(),
            It.IsAny<IProgress<ProgressSnapshot>>(),
            It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    #endregion

    #region ProcessAsync — fallback to individual

    [Fact]
    public async Task ProcessAsync_FallsBackToIndividual_WhenBulkFails()
    {
        // Arrange
        var oldId = Guid.NewGuid();
        var newId = Guid.NewGuid();
        var oldRefId = Guid.NewGuid();
        var newRefId = Guid.NewGuid();

        var idMappings = new IdMappingCollection();
        idMappings.AddMapping("team", oldId, newId);
        idMappings.AddMapping("team", oldRefId, newRefId);

        var record = new Entity("team", oldId);
        record["parentteamid"] = new EntityReference("team", oldRefId);

        var context = CreateContext(
            deferredFields: new Dictionary<string, IReadOnlyList<string>>
            {
                ["team"] = new List<string> { "parentteamid" }
            },
            entityData: new Dictionary<string, IReadOnlyList<Entity>>
            {
                ["team"] = new List<Entity> { record }
            },
            idMappings: idMappings);

        // Probe fails — bulk not supported for this entity
        _bulkExecutor.Setup(x => x.UpdateMultipleAsync(
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
                        Message = "UpdateMultiple is not enabled on the entity team"
                    }
                }
            });

        // Fallback: individual ExecuteAsync calls succeed
        _client.Setup(c => c.ExecuteAsync(It.IsAny<OrganizationRequest>()))
            .ReturnsAsync(new OrganizationResponse());

        // Act
        var result = await _sut.ProcessAsync(context, CancellationToken.None);

        // Assert
        result.Success.Should().BeTrue();
        result.SuccessCount.Should().Be(1);
        result.FailureCount.Should().Be(0);

        // Verify individual update was used after bulk failed
        _client.Verify(c => c.ExecuteAsync(It.IsAny<OrganizationRequest>()), Times.Once);
    }

    #endregion

    #region ProcessAsync — empty deferred fields

    [Fact]
    public async Task ProcessAsync_EmptyDeferredFields_NoOps()
    {
        // Arrange: plan has zero deferred fields
        var context = CreateContext(
            deferredFields: new Dictionary<string, IReadOnlyList<string>>());

        // Act
        var result = await _sut.ProcessAsync(context, CancellationToken.None);

        // Assert — returns skipped result, no Dataverse calls
        result.Success.Should().BeTrue();
        result.RecordsProcessed.Should().Be(0);

        _pool.Verify(p => p.GetClientAsync(
            It.IsAny<Dataverse.Client.DataverseClientOptions?>(),
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    #endregion

    #region Constructor

    [Fact]
    public void Constructor_ThrowsArgumentNullException_WhenConnectionPoolIsNull()
    {
        var act = () => new DeferredFieldProcessor(null!, _prober);

        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("connectionPool");
    }

    [Fact]
    public void Constructor_ThrowsArgumentNullException_WhenProberIsNull()
    {
        var act = () => new DeferredFieldProcessor(_pool.Object, null!);

        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("prober");
    }

    #endregion

    #region Helpers

    private ImportContext CreateContext(
        IReadOnlyDictionary<string, IReadOnlyList<string>>? deferredFields = null,
        IReadOnlyDictionary<string, IReadOnlyList<Entity>>? entityData = null,
        IdMappingCollection? idMappings = null,
        IProgressReporter? progress = null)
    {
        var data = new MigrationData
        {
            EntityData = entityData ?? new Dictionary<string, IReadOnlyList<Entity>>()
        };

        var plan = new ExecutionPlan
        {
            DeferredFields = deferredFields ?? new Dictionary<string, IReadOnlyList<string>>()
        };

        var options = new ImportOptions { ContinueOnError = true };
        var fieldMetadata = new FieldMetadataCollection(
            new Dictionary<string, Dictionary<string, FieldValidity>>());

        return new ImportContext(
            data,
            plan,
            options,
            idMappings ?? new IdMappingCollection(),
            fieldMetadata,
            progress);
    }

    #endregion
}
