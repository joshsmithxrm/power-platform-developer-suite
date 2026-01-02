using FluentAssertions;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using PPDS.Dataverse.BulkOperations;
using Xunit;

namespace PPDS.Dataverse.IntegrationTests.BulkOperations;

/// <summary>
/// Tests for bulk operations with ElasticTable mode enabled.
/// Elastic tables (Cosmos DB-backed) use different APIs and support partial success.
/// </summary>
public class ElasticTableTests : BulkOperationExecutorTestsBase
{
    private const string EntityName = "account";

    #region DeleteMultiple - ElasticTable Mode

    [Fact]
    public async Task DeleteMultipleAsync_ElasticTable_WithSingleId_DeletesSuccessfully()
    {
        // Arrange
        var createResult = await Executor.CreateMultipleAsync(EntityName, CreateTestEntities(EntityName, 1));
        var idToDelete = createResult.CreatedIds![0];
        var options = new BulkOperationOptions { ElasticTable = true };

        // Act
        var result = await Executor.DeleteMultipleAsync(EntityName, new[] { idToDelete }, options);

        // Assert
        result.Should().NotBeNull();
        result.IsSuccess.Should().BeTrue();
        result.SuccessCount.Should().Be(1);
        result.FailureCount.Should().Be(0);
    }

    [Fact]
    public async Task DeleteMultipleAsync_ElasticTable_WithMultipleIds_DeletesAllSuccessfully()
    {
        // Arrange
        var createResult = await Executor.CreateMultipleAsync(EntityName, CreateTestEntities(EntityName, 10));
        var idsToDelete = createResult.CreatedIds!.ToList();
        var options = new BulkOperationOptions { ElasticTable = true };

        // Act
        var result = await Executor.DeleteMultipleAsync(EntityName, idsToDelete, options);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.SuccessCount.Should().Be(10);
        result.FailureCount.Should().Be(0);
    }

    [Fact]
    public async Task DeleteMultipleAsync_ElasticTable_RemovesRecordsFromDatastore()
    {
        // Arrange
        var createResult = await Executor.CreateMultipleAsync(EntityName, CreateTestEntities(EntityName, 5));
        var idsToDelete = createResult.CreatedIds!.ToList();
        var options = new BulkOperationOptions { ElasticTable = true };

        // Act
        await Executor.DeleteMultipleAsync(EntityName, idsToDelete, options);

        // Assert - Verify records no longer exist
        foreach (var id in idsToDelete)
        {
            var query = new QueryExpression(EntityName)
            {
                ColumnSet = new ColumnSet(true),
                Criteria = new FilterExpression
                {
                    Conditions =
                    {
                        new ConditionExpression(EntityName + "id", ConditionOperator.Equal, id)
                    }
                }
            };
            var results = Service.RetrieveMultiple(query);
            results.Entities.Should().BeEmpty();
        }
    }

    [Fact]
    public async Task DeleteMultipleAsync_ElasticTable_WithProgressReporter_ReportsProgress()
    {
        // Arrange
        var createResult = await Executor.CreateMultipleAsync(EntityName, CreateTestEntities(EntityName, 50));
        var idsToDelete = createResult.CreatedIds!.ToList();
        var options = new BulkOperationOptions { ElasticTable = true };
        var progress = CreateProgressReporter();

        // Act
        await Executor.DeleteMultipleAsync(EntityName, idsToDelete, options, progress);

        // Assert
        progress.Reports.Should().NotBeEmpty();
        progress.LastReport.Should().NotBeNull();
        progress.LastReport!.Processed.Should().Be(50);
    }

    [Fact]
    public async Task DeleteMultipleAsync_ElasticTable_WithCustomBatchSize_RespectsBatchSize()
    {
        // Arrange
        var createResult = await Executor.CreateMultipleAsync(EntityName, CreateTestEntities(EntityName, 25));
        var idsToDelete = createResult.CreatedIds!.ToList();
        var options = new BulkOperationOptions
        {
            ElasticTable = true,
            BatchSize = 10,
            MaxParallelBatches = 1
        };

        // Act
        var result = await Executor.DeleteMultipleAsync(EntityName, idsToDelete, options);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.SuccessCount.Should().Be(25);
    }

    [Fact]
    public async Task DeleteMultipleAsync_ElasticTable_WithLargeBatch_ProcessesSuccessfully()
    {
        // Arrange
        var createResult = await Executor.CreateMultipleAsync(EntityName, CreateTestEntities(EntityName, 200));
        var idsToDelete = createResult.CreatedIds!.ToList();
        var options = new BulkOperationOptions
        {
            ElasticTable = true,
            BatchSize = 50,
            MaxParallelBatches = 1
        };

        // Act
        var result = await Executor.DeleteMultipleAsync(EntityName, idsToDelete, options);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.SuccessCount.Should().Be(200);
    }

    [Fact]
    public async Task DeleteMultipleAsync_ElasticTable_OnlyDeletesSpecifiedRecords()
    {
        // Arrange - Create 5 entities, delete only 3
        var createResult = await Executor.CreateMultipleAsync(EntityName, CreateTestEntities(EntityName, 5));
        var allIds = createResult.CreatedIds!.ToList();
        var idsToDelete = allIds.Take(3).ToList();
        var idsToKeep = allIds.Skip(3).ToList();
        var options = new BulkOperationOptions { ElasticTable = true };

        // Act
        await Executor.DeleteMultipleAsync(EntityName, idsToDelete, options);

        // Assert - Deleted records should be gone
        foreach (var id in idsToDelete)
        {
            var query = new QueryExpression(EntityName)
            {
                Criteria = { Conditions = { new ConditionExpression(EntityName + "id", ConditionOperator.Equal, id) } }
            };
            Service.RetrieveMultiple(query).Entities.Should().BeEmpty();
        }

        // Assert - Kept records should still exist
        foreach (var id in idsToKeep)
        {
            var retrieved = Service.Retrieve(EntityName, id, new ColumnSet(true));
            retrieved.Should().NotBeNull();
        }
    }

    #endregion

    #region CreateMultiple - ElasticTable Mode

    [Fact]
    public async Task CreateMultipleAsync_ElasticTable_CreatesSuccessfully()
    {
        // Arrange
        var entities = CreateTestEntities(EntityName, 10);
        var options = new BulkOperationOptions { ElasticTable = true };

        // Act
        var result = await Executor.CreateMultipleAsync(EntityName, entities, options);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.SuccessCount.Should().Be(10);
        result.CreatedIds.Should().HaveCount(10);
    }

    [Fact]
    public async Task CreateMultipleAsync_ElasticTable_WithBatching_ProcessesAllBatches()
    {
        // Arrange
        var entities = CreateTestEntities(EntityName, 35);
        var options = new BulkOperationOptions
        {
            ElasticTable = true,
            BatchSize = 10,
            MaxParallelBatches = 1
        };

        // Act
        var result = await Executor.CreateMultipleAsync(EntityName, entities, options);

        // Assert - 35 entities with batch size 10 = 4 batches
        result.IsSuccess.Should().BeTrue();
        result.SuccessCount.Should().Be(35);
    }

    [Fact]
    public async Task CreateMultipleAsync_ElasticTable_WithProgress_ReportsCorrectly()
    {
        // Arrange
        var entities = CreateTestEntities(EntityName, 30);
        var options = new BulkOperationOptions
        {
            ElasticTable = true,
            BatchSize = 10,
            MaxParallelBatches = 1
        };
        var progress = CreateProgressReporter();

        // Act
        await Executor.CreateMultipleAsync(EntityName, entities, options, progress);

        // Assert
        progress.Reports.Should().HaveCount(3); // 3 batches of 10
        progress.LastReport!.Processed.Should().Be(30);
        progress.LastReport.Total.Should().Be(30);
    }

    #endregion

    #region UpdateMultiple - ElasticTable Mode

    [Fact]
    public async Task UpdateMultipleAsync_ElasticTable_UpdatesSuccessfully()
    {
        // Arrange
        var createResult = await Executor.CreateMultipleAsync(EntityName, CreateTestEntities(EntityName, 10));
        var updateEntities = CreateTestEntitiesWithIds(EntityName, createResult.CreatedIds!);
        var options = new BulkOperationOptions { ElasticTable = true };

        // Act
        var result = await Executor.UpdateMultipleAsync(EntityName, updateEntities, options);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.SuccessCount.Should().Be(10);
    }

    [Fact]
    public async Task UpdateMultipleAsync_ElasticTable_PersistsChanges()
    {
        // Arrange
        var createResult = await Executor.CreateMultipleAsync(EntityName, CreateTestEntities(EntityName, 3));
        var updateEntities = createResult.CreatedIds!.Select((id, i) => new Entity(EntityName, id)
        {
            ["name"] = $"ElasticTable Updated {i}",
            ["description"] = "Updated via elastic table mode"
        }).ToList();
        var options = new BulkOperationOptions { ElasticTable = true };

        // Act
        await Executor.UpdateMultipleAsync(EntityName, updateEntities, options);

        // Assert
        for (int i = 0; i < createResult.CreatedIds!.Count; i++)
        {
            var retrieved = Service.Retrieve(EntityName, createResult.CreatedIds[i], new ColumnSet(true));
            retrieved.GetAttributeValue<string>("name").Should().Be($"ElasticTable Updated {i}");
            retrieved.GetAttributeValue<string>("description").Should().Be("Updated via elastic table mode");
        }
    }

    [Fact]
    public async Task UpdateMultipleAsync_ElasticTable_WithBatching_ProcessesAllBatches()
    {
        // Arrange
        var createResult = await Executor.CreateMultipleAsync(EntityName, CreateTestEntities(EntityName, 45));
        var updateEntities = CreateTestEntitiesWithIds(EntityName, createResult.CreatedIds!);
        var options = new BulkOperationOptions
        {
            ElasticTable = true,
            BatchSize = 10,
            MaxParallelBatches = 1
        };

        // Act
        var result = await Executor.UpdateMultipleAsync(EntityName, updateEntities, options);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.SuccessCount.Should().Be(45);
    }

    #endregion

    #region UpsertMultiple - ElasticTable Mode

    [Fact]
    public async Task UpsertMultipleAsync_ElasticTable_UpdatesExistingRecords()
    {
        // Arrange
        var createResult = await Executor.CreateMultipleAsync(EntityName, CreateTestEntities(EntityName, 10));
        var upsertEntities = createResult.CreatedIds!.Select((id, i) => new Entity(EntityName, id)
        {
            ["name"] = $"ElasticTable Upserted {i}"
        }).ToList();
        var options = new BulkOperationOptions { ElasticTable = true };

        // Act
        var result = await Executor.UpsertMultipleAsync(EntityName, upsertEntities, options);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.SuccessCount.Should().Be(10);
        result.UpdatedCount.Should().Be(10);
    }

    [Fact]
    public async Task UpsertMultipleAsync_ElasticTable_PersistsChanges()
    {
        // Arrange
        var createResult = await Executor.CreateMultipleAsync(EntityName, CreateTestEntities(EntityName, 3));
        var upsertEntities = createResult.CreatedIds!.Select((id, i) => new Entity(EntityName, id)
        {
            ["name"] = $"ElasticTable Upsert Result {i}"
        }).ToList();
        var options = new BulkOperationOptions { ElasticTable = true };

        // Act
        await Executor.UpsertMultipleAsync(EntityName, upsertEntities, options);

        // Assert
        for (int i = 0; i < createResult.CreatedIds!.Count; i++)
        {
            var retrieved = Service.Retrieve(EntityName, createResult.CreatedIds[i], new ColumnSet(true));
            retrieved.GetAttributeValue<string>("name").Should().Be($"ElasticTable Upsert Result {i}");
        }
    }

    [Fact]
    public async Task UpsertMultipleAsync_ElasticTable_WithBatching_ProcessesAllBatches()
    {
        // Arrange
        var createResult = await Executor.CreateMultipleAsync(EntityName, CreateTestEntities(EntityName, 55));
        var upsertEntities = CreateTestEntitiesWithIds(EntityName, createResult.CreatedIds!);
        var options = new BulkOperationOptions
        {
            ElasticTable = true,
            BatchSize = 10,
            MaxParallelBatches = 1
        };

        // Act
        var result = await Executor.UpsertMultipleAsync(EntityName, upsertEntities, options);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.SuccessCount.Should().Be(55);
    }

    #endregion

    #region ContinueOnError Behavior

    [Fact]
    public async Task CreateMultipleAsync_ElasticTable_WithContinueOnError_AcceptsOption()
    {
        // Arrange
        var entities = CreateTestEntities(EntityName, 10);
        var options = new BulkOperationOptions
        {
            ElasticTable = true,
            ContinueOnError = true
        };

        // Act
        var result = await Executor.CreateMultipleAsync(EntityName, entities, options);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.SuccessCount.Should().Be(10);
    }

    [Fact]
    public async Task UpdateMultipleAsync_ElasticTable_WithContinueOnError_AcceptsOption()
    {
        // Arrange
        var createResult = await Executor.CreateMultipleAsync(EntityName, CreateTestEntities(EntityName, 10));
        var updateEntities = CreateTestEntitiesWithIds(EntityName, createResult.CreatedIds!);
        var options = new BulkOperationOptions
        {
            ElasticTable = true,
            ContinueOnError = true
        };

        // Act
        var result = await Executor.UpdateMultipleAsync(EntityName, updateEntities, options);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.SuccessCount.Should().Be(10);
    }

    [Fact]
    public async Task DeleteMultipleAsync_ElasticTable_WithContinueOnError_AcceptsOption()
    {
        // Arrange
        var createResult = await Executor.CreateMultipleAsync(EntityName, CreateTestEntities(EntityName, 10));
        var idsToDelete = createResult.CreatedIds!.ToList();
        var options = new BulkOperationOptions
        {
            ElasticTable = true,
            ContinueOnError = true
        };

        // Act
        var result = await Executor.DeleteMultipleAsync(EntityName, idsToDelete, options);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.SuccessCount.Should().Be(10);
    }

    #endregion

    #region Bypass Options with ElasticTable

    [Fact]
    public async Task CreateMultipleAsync_ElasticTable_WithBypassCustomLogic_AcceptsOption()
    {
        // Arrange
        var entities = CreateTestEntities(EntityName, 5);
        var options = new BulkOperationOptions
        {
            ElasticTable = true,
            BypassCustomLogic = CustomLogicBypass.All
        };

        // Act
        var result = await Executor.CreateMultipleAsync(EntityName, entities, options);

        // Assert
        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task UpdateMultipleAsync_ElasticTable_WithBypassPowerAutomate_AcceptsOption()
    {
        // Arrange
        var createResult = await Executor.CreateMultipleAsync(EntityName, CreateTestEntities(EntityName, 5));
        var updateEntities = CreateTestEntitiesWithIds(EntityName, createResult.CreatedIds!);
        var options = new BulkOperationOptions
        {
            ElasticTable = true,
            BypassPowerAutomateFlows = true
        };

        // Act
        var result = await Executor.UpdateMultipleAsync(EntityName, updateEntities, options);

        // Assert
        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task DeleteMultipleAsync_ElasticTable_WithTag_AcceptsOption()
    {
        // Arrange
        var createResult = await Executor.CreateMultipleAsync(EntityName, CreateTestEntities(EntityName, 5));
        var idsToDelete = createResult.CreatedIds!.ToList();
        var options = new BulkOperationOptions
        {
            ElasticTable = true,
            Tag = "ElasticTableBulkDelete"
        };

        // Act
        var result = await Executor.DeleteMultipleAsync(EntityName, idsToDelete, options);

        // Assert
        result.IsSuccess.Should().BeTrue();
    }

    #endregion
}
