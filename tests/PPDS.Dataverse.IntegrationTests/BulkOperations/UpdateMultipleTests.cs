using FluentAssertions;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using PPDS.Dataverse.BulkOperations;
using Xunit;

namespace PPDS.Dataverse.IntegrationTests.BulkOperations;

/// <summary>
/// Tests for UpdateMultipleAsync using FakeXrmEasy.
/// </summary>
public class UpdateMultipleTests : BulkOperationExecutorTestsBase
{
    private const string EntityName = "account";

    [Fact]
    public async Task UpdateMultipleAsync_WithSingleEntity_UpdatesSuccessfully()
    {
        // Arrange - Create entities first
        var createResult = await Executor.CreateMultipleAsync(EntityName, CreateTestEntities(EntityName, 1));
        var updateEntities = CreateTestEntitiesWithIds(EntityName, createResult.CreatedIds!);

        // Act
        var result = await Executor.UpdateMultipleAsync(EntityName, updateEntities);

        // Assert
        result.Should().NotBeNull();
        result.IsSuccess.Should().BeTrue();
        result.SuccessCount.Should().Be(1);
        result.FailureCount.Should().Be(0);
    }

    [Fact]
    public async Task UpdateMultipleAsync_WithMultipleEntities_UpdatesAllSuccessfully()
    {
        // Arrange - Create entities first
        var createResult = await Executor.CreateMultipleAsync(EntityName, CreateTestEntities(EntityName, 10));
        var updateEntities = CreateTestEntitiesWithIds(EntityName, createResult.CreatedIds!);

        // Act
        var result = await Executor.UpdateMultipleAsync(EntityName, updateEntities);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.SuccessCount.Should().Be(10);
        result.FailureCount.Should().Be(0);
    }

    [Fact]
    public async Task UpdateMultipleAsync_PersistsChanges()
    {
        // Arrange
        var createResult = await Executor.CreateMultipleAsync(EntityName, CreateTestEntities(EntityName, 3));
        var updateEntities = createResult.CreatedIds!.Select((id, i) => new Entity(EntityName, id)
        {
            ["name"] = $"Updated Name {i}",
            ["description"] = $"Updated Description {i}"
        }).ToList();

        // Act
        await Executor.UpdateMultipleAsync(EntityName, updateEntities);

        // Assert - Verify changes were persisted
        for (int i = 0; i < createResult.CreatedIds!.Count; i++)
        {
            var retrieved = Service.Retrieve(EntityName, createResult.CreatedIds[i], new ColumnSet(true));
            retrieved.GetAttributeValue<string>("name").Should().Be($"Updated Name {i}");
            retrieved.GetAttributeValue<string>("description").Should().Be($"Updated Description {i}");
        }
    }

    [Fact]
    public async Task UpdateMultipleAsync_WithProgressReporter_ReportsProgress()
    {
        // Arrange
        var createResult = await Executor.CreateMultipleAsync(EntityName, CreateTestEntities(EntityName, 50));
        var updateEntities = CreateTestEntitiesWithIds(EntityName, createResult.CreatedIds!);
        var progress = CreateProgressReporter();

        // Act
        await Executor.UpdateMultipleAsync(EntityName, updateEntities, progress: progress);

        // Assert
        progress.Reports.Should().NotBeEmpty();
        progress.LastReport.Should().NotBeNull();
        progress.LastReport!.Processed.Should().Be(50);
    }

    [Fact]
    public async Task UpdateMultipleAsync_WithCustomBatchSize_RespectsBatchSize()
    {
        // Arrange
        var createResult = await Executor.CreateMultipleAsync(EntityName, CreateTestEntities(EntityName, 25));
        var updateEntities = CreateTestEntitiesWithIds(EntityName, createResult.CreatedIds!);
        var options = new BulkOperationOptions
        {
            BatchSize = 10,
            MaxParallelBatches = 1
        };

        // Act
        var result = await Executor.UpdateMultipleAsync(EntityName, updateEntities, options);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.SuccessCount.Should().Be(25);
    }

    [Fact]
    public async Task UpdateMultipleAsync_WithEmptyCollection_ReturnsEmptyResult()
    {
        // Arrange
        var entities = new List<Entity>();

        // Act
        var result = await Executor.UpdateMultipleAsync(EntityName, entities);

        // Assert
        result.SuccessCount.Should().Be(0);
        result.FailureCount.Should().Be(0);
        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task UpdateMultipleAsync_PreservesUnchangedAttributes()
    {
        // Arrange - Create with multiple attributes
        var createEntity = new Entity(EntityName)
        {
            ["name"] = "Original Name",
            ["description"] = "Original Description",
            ["revenue"] = new Money(1000m)
        };
        var createResult = await Executor.CreateMultipleAsync(EntityName, new[] { createEntity });

        // Update only name
        var updateEntity = new Entity(EntityName, createResult.CreatedIds![0])
        {
            ["name"] = "Updated Name"
        };

        // Act
        await Executor.UpdateMultipleAsync(EntityName, new[] { updateEntity });

        // Assert - Description and revenue should be unchanged
        var retrieved = Service.Retrieve(EntityName, createResult.CreatedIds![0], new ColumnSet(true));
        retrieved.GetAttributeValue<string>("name").Should().Be("Updated Name");
        retrieved.GetAttributeValue<string>("description").Should().Be("Original Description");
        retrieved.GetAttributeValue<Money>("revenue").Value.Should().Be(1000m);
    }

    [Fact]
    public async Task UpdateMultipleAsync_WithCancellationToken_AcceptsToken()
    {
        // Arrange
        var createResult = await Executor.CreateMultipleAsync(EntityName, CreateTestEntities(EntityName, 5));
        var updateEntities = CreateTestEntitiesWithIds(EntityName, createResult.CreatedIds!);
        var cts = new CancellationTokenSource();

        // Act - verify the method accepts a cancellation token
        var result = await Executor.UpdateMultipleAsync(EntityName, updateEntities, cancellationToken: cts.Token);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.SuccessCount.Should().Be(5);
    }

    [Fact]
    public async Task UpdateMultipleAsync_WithLargeBatch_ProcessesSuccessfully()
    {
        // Arrange
        var createResult = await Executor.CreateMultipleAsync(EntityName, CreateTestEntities(EntityName, 500));
        var updateEntities = CreateTestEntitiesWithIds(EntityName, createResult.CreatedIds!);
        var options = new BulkOperationOptions
        {
            BatchSize = 100,
            MaxParallelBatches = 1
        };

        // Act
        var result = await Executor.UpdateMultipleAsync(EntityName, updateEntities, options);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.SuccessCount.Should().Be(500);
    }

    [Fact]
    public async Task UpdateMultipleAsync_Duration_IsNonZero()
    {
        // Arrange
        var createResult = await Executor.CreateMultipleAsync(EntityName, CreateTestEntities(EntityName, 10));
        var updateEntities = CreateTestEntitiesWithIds(EntityName, createResult.CreatedIds!);

        // Act
        var result = await Executor.UpdateMultipleAsync(EntityName, updateEntities);

        // Assert
        result.Duration.Should().BeGreaterThan(TimeSpan.Zero);
    }
}
