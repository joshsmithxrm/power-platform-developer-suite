using FluentAssertions;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using PPDS.Dataverse.BulkOperations;
using Xunit;

namespace PPDS.Dataverse.IntegrationTests.BulkOperations;

/// <summary>
/// Tests for CreateMultipleAsync using FakeXrmEasy.
/// </summary>
public class CreateMultipleTests : BulkOperationExecutorTestsBase
{
    private const string EntityName = "account";

    [Fact]
    public async Task CreateMultipleAsync_WithSingleEntity_CreatesSuccessfully()
    {
        // Arrange
        var entities = CreateTestEntities(EntityName, 1);

        // Act
        var result = await Executor.CreateMultipleAsync(EntityName, entities);

        // Assert
        result.Should().NotBeNull();
        result.IsSuccess.Should().BeTrue();
        result.SuccessCount.Should().Be(1);
        result.FailureCount.Should().Be(0);
        result.CreatedIds.Should().NotBeNull();
        result.CreatedIds.Should().HaveCount(1);
    }

    [Fact]
    public async Task CreateMultipleAsync_WithMultipleEntities_CreatesAllSuccessfully()
    {
        // Arrange
        var entities = CreateTestEntities(EntityName, 10);

        // Act
        var result = await Executor.CreateMultipleAsync(EntityName, entities);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.SuccessCount.Should().Be(10);
        result.FailureCount.Should().Be(0);
        result.CreatedIds.Should().HaveCount(10);
        result.CreatedIds!.Should().OnlyContain(id => id != Guid.Empty);
    }

    [Fact]
    public async Task CreateMultipleAsync_ReturnsValidIds_ThatCanBeRetrieved()
    {
        // Arrange
        var entities = CreateTestEntities(EntityName, 5);

        // Act
        var result = await Executor.CreateMultipleAsync(EntityName, entities);

        // Assert
        result.CreatedIds.Should().NotBeNull();
        foreach (var id in result.CreatedIds!)
        {
            var retrieved = Service.Retrieve(EntityName, id, new ColumnSet(true));
            retrieved.Should().NotBeNull();
            retrieved.LogicalName.Should().Be(EntityName);
        }
    }

    [Fact]
    public async Task CreateMultipleAsync_WithProgressReporter_ReportsProgress()
    {
        // Arrange
        var entities = CreateTestEntities(EntityName, 50);
        var progress = CreateProgressReporter();

        // Act
        await Executor.CreateMultipleAsync(EntityName, entities, progress: progress);

        // Assert
        progress.Reports.Should().NotBeEmpty();
        progress.LastReport.Should().NotBeNull();
        progress.LastReport!.Processed.Should().Be(50);
    }

    [Fact]
    public async Task CreateMultipleAsync_WithCustomBatchSize_RespectsBatchSize()
    {
        // Arrange
        var entities = CreateTestEntities(EntityName, 25);
        var options = new BulkOperationOptions
        {
            BatchSize = 10,
            MaxParallelBatches = 1
        };

        // Act
        var result = await Executor.CreateMultipleAsync(EntityName, entities, options);

        // Assert - 25 entities with batch size 10 = 3 batches (10, 10, 5)
        result.IsSuccess.Should().BeTrue();
        result.SuccessCount.Should().Be(25);
    }

    [Fact]
    public async Task CreateMultipleAsync_WithEmptyCollection_ReturnsEmptyResult()
    {
        // Arrange
        var entities = new List<Entity>();

        // Act
        var result = await Executor.CreateMultipleAsync(EntityName, entities);

        // Assert
        result.SuccessCount.Should().Be(0);
        result.FailureCount.Should().Be(0);
        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task CreateMultipleAsync_PreservesEntityAttributes()
    {
        // Arrange
        var entity = new Entity(EntityName)
        {
            ["name"] = "Test Account",
            ["description"] = "Test Description",
            ["revenue"] = new Money(1000m)
        };

        // Act
        var result = await Executor.CreateMultipleAsync(EntityName, new[] { entity });

        // Assert
        result.CreatedIds.Should().NotBeNull().And.HaveCount(1);
        var retrieved = Service.Retrieve(EntityName, result.CreatedIds![0], new ColumnSet(true));
        retrieved.GetAttributeValue<string>("name").Should().Be("Test Account");
        retrieved.GetAttributeValue<string>("description").Should().Be("Test Description");
        retrieved.GetAttributeValue<Money>("revenue").Value.Should().Be(1000m);
    }

    [Fact]
    public async Task CreateMultipleAsync_WithCancellationToken_AcceptsToken()
    {
        // Arrange
        var entities = CreateTestEntities(EntityName, 5);
        var cts = new CancellationTokenSource();

        // Act - verify the method accepts a cancellation token
        var result = await Executor.CreateMultipleAsync(EntityName, entities, cancellationToken: cts.Token);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.SuccessCount.Should().Be(5);
    }

    [Fact]
    public async Task CreateMultipleAsync_WithLargeBatch_ProcessesSuccessfully()
    {
        // Arrange - 500 entities exceeds default batch size of 100, tests batching
        var entities = CreateTestEntities(EntityName, 500);
        var options = new BulkOperationOptions
        {
            BatchSize = 100,
            MaxParallelBatches = 1
        };

        // Act
        var result = await Executor.CreateMultipleAsync(EntityName, entities, options);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.SuccessCount.Should().Be(500);
        result.CreatedIds.Should().HaveCount(500);
    }

    [Fact]
    public async Task CreateMultipleAsync_Duration_IsNonZero()
    {
        // Arrange
        var entities = CreateTestEntities(EntityName, 10);

        // Act
        var result = await Executor.CreateMultipleAsync(EntityName, entities);

        // Assert
        result.Duration.Should().BeGreaterThan(TimeSpan.Zero);
    }
}
