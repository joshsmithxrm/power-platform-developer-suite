using FluentAssertions;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using PPDS.Dataverse.BulkOperations;
using Xunit;

namespace PPDS.Dataverse.IntegrationTests.BulkOperations;

/// <summary>
/// Tests for DeleteMultipleAsync using FakeXrmEasy.
/// Note: Standard delete uses ExecuteMultiple which has limited support in FakeXrmEasy.
/// Some tests are skipped or simplified due to FakeXrmEasy limitations.
/// </summary>
public class DeleteMultipleTests : BulkOperationExecutorTestsBase
{
    private const string EntityName = "account";

    [Fact]
    public async Task DeleteMultipleAsync_WithEmptyCollection_ReturnsEmptyResult()
    {
        // Arrange
        var ids = new List<Guid>();

        // Act
        var result = await Executor.DeleteMultipleAsync(EntityName, ids);

        // Assert
        result.SuccessCount.Should().Be(0);
        result.FailureCount.Should().Be(0);
        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task DeleteMultipleAsync_WithCancellationToken_AcceptsToken()
    {
        // Arrange
        var createResult = await Executor.CreateMultipleAsync(EntityName, CreateTestEntities(EntityName, 3));
        var idsToDelete = createResult.CreatedIds!.ToList();
        var cts = new CancellationTokenSource();

        // Act - verify the method accepts a cancellation token and processes records
        var result = await Executor.DeleteMultipleAsync(EntityName, idsToDelete, cancellationToken: cts.Token);

        // Assert - With FakeXrmEasy ExecuteMultiple, results may vary
        result.Should().NotBeNull();
        result.TotalCount.Should().Be(3);
    }

    [Fact]
    public async Task DeleteMultipleAsync_Duration_IsNonZero()
    {
        // Arrange
        var createResult = await Executor.CreateMultipleAsync(EntityName, CreateTestEntities(EntityName, 3));
        var idsToDelete = createResult.CreatedIds!.ToList();

        // Act
        var result = await Executor.DeleteMultipleAsync(EntityName, idsToDelete);

        // Assert - Duration should always be tracked
        result.Duration.Should().BeGreaterThan(TimeSpan.Zero);
    }

    [Fact]
    public async Task DeleteMultipleAsync_WithProgress_ReportsProgress()
    {
        // Arrange
        var createResult = await Executor.CreateMultipleAsync(EntityName, CreateTestEntities(EntityName, 10));
        var idsToDelete = createResult.CreatedIds!.ToList();
        var progress = CreateProgressReporter();

        // Act
        await Executor.DeleteMultipleAsync(EntityName, idsToDelete, progress: progress);

        // Assert - Progress should be reported
        progress.Reports.Should().NotBeEmpty();
        progress.LastReport.Should().NotBeNull();
        progress.LastReport!.Total.Should().Be(10);
    }

    [Fact]
    public async Task DeleteMultipleAsync_WithOptions_AcceptsOptions()
    {
        // Arrange
        var createResult = await Executor.CreateMultipleAsync(EntityName, CreateTestEntities(EntityName, 5));
        var idsToDelete = createResult.CreatedIds!.ToList();
        var options = new BulkOperationOptions
        {
            BatchSize = 2,
            ContinueOnError = true,
            MaxParallelBatches = 1
        };

        // Act - verify the method accepts options
        var result = await Executor.DeleteMultipleAsync(EntityName, idsToDelete, options);

        // Assert
        result.Should().NotBeNull();
        result.TotalCount.Should().Be(5);
    }
}
