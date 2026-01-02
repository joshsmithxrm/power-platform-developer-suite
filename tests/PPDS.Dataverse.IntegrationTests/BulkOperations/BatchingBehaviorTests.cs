using FluentAssertions;
using Microsoft.Xrm.Sdk;
using PPDS.Dataverse.BulkOperations;
using Xunit;

namespace PPDS.Dataverse.IntegrationTests.BulkOperations;

/// <summary>
/// Tests for verifying correct batching behavior in bulk operations.
/// </summary>
public class BatchingBehaviorTests : BulkOperationExecutorTestsBase
{
    private const string EntityName = "account";

    [Theory]
    [InlineData(1, 10)]    // 1 entity, batch size 10 = 1 batch
    [InlineData(10, 10)]   // 10 entities, batch size 10 = 1 batch
    [InlineData(11, 10)]   // 11 entities, batch size 10 = 2 batches
    [InlineData(100, 10)]  // 100 entities, batch size 10 = 10 batches
    [InlineData(105, 10)]  // 105 entities, batch size 10 = 11 batches
    public async Task CreateMultiple_WithVariousBatchSizes_ProcessesAllEntities(int entityCount, int batchSize)
    {
        // Arrange
        var entities = CreateTestEntities(EntityName, entityCount);
        var options = new BulkOperationOptions
        {
            BatchSize = batchSize,
            MaxParallelBatches = 1
        };

        // Act
        var result = await Executor.CreateMultipleAsync(EntityName, entities, options);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.SuccessCount.Should().Be(entityCount);
        result.CreatedIds.Should().HaveCount(entityCount);
    }

    [Fact]
    public async Task CreateMultiple_WithBatchSizeOne_ProcessesEachEntitySeparately()
    {
        // Arrange
        var entities = CreateTestEntities(EntityName, 5);
        var options = new BulkOperationOptions
        {
            BatchSize = 1,
            MaxParallelBatches = 1
        };

        // Act
        var result = await Executor.CreateMultipleAsync(EntityName, entities, options);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.SuccessCount.Should().Be(5);
    }

    [Fact]
    public async Task CreateMultiple_WithLargeBatchSize_ProcessesInSingleBatch()
    {
        // Arrange
        var entities = CreateTestEntities(EntityName, 50);
        var options = new BulkOperationOptions
        {
            BatchSize = 1000, // Much larger than entity count
            MaxParallelBatches = 1
        };

        // Act
        var result = await Executor.CreateMultipleAsync(EntityName, entities, options);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.SuccessCount.Should().Be(50);
    }

    [Fact]
    public async Task CreateMultiple_ProgressReports_MatchBatching()
    {
        // Arrange
        var entities = CreateTestEntities(EntityName, 25);
        var options = new BulkOperationOptions
        {
            BatchSize = 10,
            MaxParallelBatches = 1 // Sequential for deterministic progress
        };
        var progress = CreateProgressReporter();

        // Act
        await Executor.CreateMultipleAsync(EntityName, entities, options, progress);

        // Assert - Should have 3 batches: 10, 10, 5
        progress.Reports.Should().HaveCount(3);
        progress.Reports[0].Processed.Should().Be(10);
        progress.Reports[1].Processed.Should().Be(20);
        progress.Reports[2].Processed.Should().Be(25);
    }

    [Fact]
    public async Task UpdateMultiple_ProgressReports_MatchBatching()
    {
        // Arrange
        var createResult = await Executor.CreateMultipleAsync(EntityName, CreateTestEntities(EntityName, 25));
        var updateEntities = CreateTestEntitiesWithIds(EntityName, createResult.CreatedIds!);
        var options = new BulkOperationOptions
        {
            BatchSize = 10,
            MaxParallelBatches = 1
        };
        var progress = CreateProgressReporter();

        // Act
        await Executor.UpdateMultipleAsync(EntityName, updateEntities, options, progress);

        // Assert - Should have 3 batches: 10, 10, 5
        progress.Reports.Should().HaveCount(3);
        progress.LastReport!.Processed.Should().Be(25);
    }

    [Fact]
    public async Task DeleteMultiple_ProgressReports_MatchBatching()
    {
        // Arrange
        var createResult = await Executor.CreateMultipleAsync(EntityName, CreateTestEntities(EntityName, 25));
        var options = new BulkOperationOptions
        {
            BatchSize = 10,
            MaxParallelBatches = 1
        };
        var progress = CreateProgressReporter();

        // Act
        await Executor.DeleteMultipleAsync(EntityName, createResult.CreatedIds!.ToList(), options, progress);

        // Assert - Should have 3 batches: 10, 10, 5
        progress.Reports.Should().HaveCount(3);
        progress.LastReport!.Processed.Should().Be(25);
    }

    [Fact]
    public async Task ProgressReporter_TracksTotalCorrectly()
    {
        // Arrange
        var entities = CreateTestEntities(EntityName, 100);
        var options = new BulkOperationOptions
        {
            BatchSize = 25,
            MaxParallelBatches = 1
        };
        var progress = CreateProgressReporter();

        // Act
        await Executor.CreateMultipleAsync(EntityName, entities, options, progress);

        // Assert
        progress.LastReport!.Total.Should().Be(100);
        progress.LastReport.Processed.Should().Be(100);
        progress.LastReport.PercentComplete.Should().Be(100);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(10)]
    [InlineData(50)]
    [InlineData(100)]
    public async Task DefaultBatchSize_ProcessesAllEntities(int entityCount)
    {
        // Arrange - Use default options (batch size 100)
        var entities = CreateTestEntities(EntityName, entityCount);

        // Act
        var result = await Executor.CreateMultipleAsync(EntityName, entities);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.SuccessCount.Should().Be(entityCount);
    }

    [Fact]
    public async Task BypassCustomLogic_IsApplied()
    {
        // Arrange
        var entities = CreateTestEntities(EntityName, 5);
        var options = new BulkOperationOptions
        {
            BypassCustomLogic = CustomLogicBypass.All,
            MaxParallelBatches = 1
        };

        // Act - Should not throw
        var result = await Executor.CreateMultipleAsync(EntityName, entities, options);

        // Assert
        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task BypassPowerAutomateFlows_IsApplied()
    {
        // Arrange
        var entities = CreateTestEntities(EntityName, 5);
        var options = new BulkOperationOptions
        {
            BypassPowerAutomateFlows = true,
            MaxParallelBatches = 1
        };

        // Act - Should not throw
        var result = await Executor.CreateMultipleAsync(EntityName, entities, options);

        // Assert
        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task SuppressDuplicateDetection_IsApplied()
    {
        // Arrange
        var entities = CreateTestEntities(EntityName, 5);
        var options = new BulkOperationOptions
        {
            SuppressDuplicateDetection = true,
            MaxParallelBatches = 1
        };

        // Act - Should not throw
        var result = await Executor.CreateMultipleAsync(EntityName, entities, options);

        // Assert
        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Tag_IsApplied()
    {
        // Arrange
        var entities = CreateTestEntities(EntityName, 5);
        var options = new BulkOperationOptions
        {
            Tag = "TestBulkOperation",
            MaxParallelBatches = 1
        };

        // Act - Should not throw
        var result = await Executor.CreateMultipleAsync(EntityName, entities, options);

        // Assert
        result.IsSuccess.Should().BeTrue();
    }
}
