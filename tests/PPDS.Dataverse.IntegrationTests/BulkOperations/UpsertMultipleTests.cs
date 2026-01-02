using FluentAssertions;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using PPDS.Dataverse.BulkOperations;
using Xunit;

namespace PPDS.Dataverse.IntegrationTests.BulkOperations;

/// <summary>
/// Tests for UpsertMultipleAsync using FakeXrmEasy.
/// Note: FakeXrmEasy has limitations with upsert on new entities (requires entity metadata).
/// Tests focus on upsert updates of existing records.
/// </summary>
public class UpsertMultipleTests : BulkOperationExecutorTestsBase
{
    private const string EntityName = "account";

    [Fact]
    public async Task UpsertMultipleAsync_WithExistingEntities_UpdatesAll()
    {
        // Arrange - Create entities first then upsert them
        var createResult = await Executor.CreateMultipleAsync(EntityName, CreateTestEntities(EntityName, 5));
        var upsertEntities = createResult.CreatedIds!.Select((id, i) => new Entity(EntityName, id)
        {
            ["name"] = $"Upserted Entity {i}"
        }).ToList();

        // Act
        var result = await Executor.UpsertMultipleAsync(EntityName, upsertEntities);

        // Assert
        result.Should().NotBeNull();
        result.IsSuccess.Should().BeTrue();
        result.SuccessCount.Should().Be(5);
        result.FailureCount.Should().Be(0);
        result.UpdatedCount.Should().Be(5);
    }

    [Fact]
    public async Task UpsertMultipleAsync_WithProgressReporter_ReportsProgress()
    {
        // Arrange - Use existing entities
        var createResult = await Executor.CreateMultipleAsync(EntityName, CreateTestEntities(EntityName, 50));
        var upsertEntities = CreateTestEntitiesWithIds(EntityName, createResult.CreatedIds!);
        var progress = CreateProgressReporter();

        // Act
        await Executor.UpsertMultipleAsync(EntityName, upsertEntities, progress: progress);

        // Assert
        progress.Reports.Should().NotBeEmpty();
        progress.LastReport.Should().NotBeNull();
        progress.LastReport!.Processed.Should().Be(50);
    }

    [Fact]
    public async Task UpsertMultipleAsync_PersistsChanges()
    {
        // Arrange - Create existing entity
        var createResult = await Executor.CreateMultipleAsync(EntityName, CreateTestEntities(EntityName, 1));
        var existingId = createResult.CreatedIds![0];

        // Upsert with updated values
        var upsertEntity = new Entity(EntityName, existingId)
        {
            ["name"] = "Upserted Name",
            ["description"] = "Upserted Description"
        };

        // Act
        await Executor.UpsertMultipleAsync(EntityName, new[] { upsertEntity });

        // Assert
        var retrieved = Service.Retrieve(EntityName, existingId, new ColumnSet(true));
        retrieved.GetAttributeValue<string>("name").Should().Be("Upserted Name");
        retrieved.GetAttributeValue<string>("description").Should().Be("Upserted Description");
    }

    [Fact]
    public async Task UpsertMultipleAsync_WithCustomBatchSize_RespectsBatchSize()
    {
        // Arrange
        var createResult = await Executor.CreateMultipleAsync(EntityName, CreateTestEntities(EntityName, 25));
        var upsertEntities = CreateTestEntitiesWithIds(EntityName, createResult.CreatedIds!);
        var options = new BulkOperationOptions
        {
            BatchSize = 10,
            MaxParallelBatches = 1
        };

        // Act
        var result = await Executor.UpsertMultipleAsync(EntityName, upsertEntities, options);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.SuccessCount.Should().Be(25);
    }

    [Fact]
    public async Task UpsertMultipleAsync_WithEmptyCollection_ReturnsEmptyResult()
    {
        // Arrange
        var entities = new List<Entity>();

        // Act
        var result = await Executor.UpsertMultipleAsync(EntityName, entities);

        // Assert
        result.SuccessCount.Should().Be(0);
        result.FailureCount.Should().Be(0);
        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task UpsertMultipleAsync_WithCancellationToken_AcceptsToken()
    {
        // Arrange - Use existing entities
        var createResult = await Executor.CreateMultipleAsync(EntityName, CreateTestEntities(EntityName, 5));
        var upsertEntities = CreateTestEntitiesWithIds(EntityName, createResult.CreatedIds!);
        var cts = new CancellationTokenSource();

        // Act - verify the method accepts a cancellation token
        var result = await Executor.UpsertMultipleAsync(EntityName, upsertEntities, cancellationToken: cts.Token);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.SuccessCount.Should().Be(5);
    }

    [Fact]
    public async Task UpsertMultipleAsync_WithLargeBatch_ProcessesSuccessfully()
    {
        // Arrange
        var createResult = await Executor.CreateMultipleAsync(EntityName, CreateTestEntities(EntityName, 200));
        var upsertEntities = CreateTestEntitiesWithIds(EntityName, createResult.CreatedIds!);
        var options = new BulkOperationOptions
        {
            BatchSize = 50,
            MaxParallelBatches = 1
        };

        // Act
        var result = await Executor.UpsertMultipleAsync(EntityName, upsertEntities, options);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.SuccessCount.Should().Be(200);
    }

    [Fact]
    public async Task UpsertMultipleAsync_Duration_IsNonZero()
    {
        // Arrange
        var createResult = await Executor.CreateMultipleAsync(EntityName, CreateTestEntities(EntityName, 10));
        var upsertEntities = CreateTestEntitiesWithIds(EntityName, createResult.CreatedIds!);

        // Act
        var result = await Executor.UpsertMultipleAsync(EntityName, upsertEntities);

        // Assert
        result.Duration.Should().BeGreaterThan(TimeSpan.Zero);
    }

    [Fact]
    public async Task UpsertMultipleAsync_PreservesExistingAttributes()
    {
        // Arrange - Create entity with multiple attributes
        var originalEntity = new Entity(EntityName)
        {
            ["name"] = "Original Name",
            ["description"] = "Original Description",
            ["revenue"] = new Money(1000m)
        };
        var createResult = await Executor.CreateMultipleAsync(EntityName, new[] { originalEntity });
        var existingId = createResult.CreatedIds![0];

        // Upsert with only name change
        var upsertEntity = new Entity(EntityName, existingId)
        {
            ["name"] = "Updated Name"
        };

        // Act
        await Executor.UpsertMultipleAsync(EntityName, new[] { upsertEntity });

        // Assert - name changed, other attributes preserved
        var retrieved = Service.Retrieve(EntityName, existingId, new ColumnSet(true));
        retrieved.GetAttributeValue<string>("name").Should().Be("Updated Name");
        retrieved.GetAttributeValue<string>("description").Should().Be("Original Description");
        retrieved.GetAttributeValue<Money>("revenue").Value.Should().Be(1000m);
    }
}
