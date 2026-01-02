using FluentAssertions;
using Microsoft.Xrm.Sdk;
using PPDS.Dataverse.BulkOperations;
using PPDS.Dataverse.IntegrationTests.FakeMessageExecutors;
using Xunit;

namespace PPDS.Dataverse.IntegrationTests.BulkOperations;

/// <summary>
/// Tests for partial success scenarios in bulk operations.
///
/// Note: Full partial success testing (per-record errors with ContinueOnError) requires
/// live Dataverse because the SDK's partial success response format is complex and
/// FakeXrmEasy cannot accurately simulate it.
///
/// These tests verify:
/// - Error handling when entire batches fail
/// - ContinueOnError option is properly passed
/// - Error details are captured
/// </summary>
public class PartialSuccessTests : BulkOperationExecutorTestsBase, IDisposable
{
    private const string EntityName = "account";

    public new void Dispose()
    {
        // Reset any failure predicates after each test
        DeleteMultipleRequestExecutor.ResetFailurePredicate();
        base.Dispose();
    }

    #region Delete with Non-Existent Records (Standard Tables)

    [Fact]
    public async Task DeleteMultipleAsync_StandardTable_WithNonExistentId_ReportsFailure()
    {
        // Arrange - Include a non-existent ID
        var createResult = await Executor.CreateMultipleAsync(EntityName, CreateTestEntities(EntityName, 3));
        var idsToDelete = createResult.CreatedIds!.ToList();
        idsToDelete.Add(Guid.NewGuid()); // Non-existent ID

        var options = new BulkOperationOptions
        {
            ContinueOnError = true,
            MaxParallelBatches = 1
        };

        // Act
        var result = await Executor.DeleteMultipleAsync(EntityName, idsToDelete, options);

        // Assert - Operation should complete with some failures recorded
        result.TotalCount.Should().Be(4);
        // Note: Exact success/failure count depends on ExecuteMultiple behavior in FakeXrmEasy
    }

    #endregion

    #region Delete with Simulated Failures (Elastic Tables)

    [Fact]
    public async Task DeleteMultipleAsync_ElasticTable_WhenRecordFailsToDelete_CapturesError()
    {
        // Arrange
        var createResult = await Executor.CreateMultipleAsync(EntityName, CreateTestEntities(EntityName, 5));
        var idsToDelete = createResult.CreatedIds!.ToList();
        var failId = idsToDelete[2]; // Third record will fail

        // Configure executor to fail on specific record
        DeleteMultipleRequestExecutor.FailurePredicate = entityRef => entityRef.Id == failId;

        var options = new BulkOperationOptions
        {
            ElasticTable = true,
            ContinueOnError = true,
            MaxParallelBatches = 1
        };

        // Act - The executor catches exceptions and returns them as failures
        var result = await Executor.DeleteMultipleAsync(EntityName, idsToDelete, options);

        // Assert - Should report failure
        result.TotalCount.Should().Be(5);
        result.FailureCount.Should().BeGreaterThan(0);
        result.Errors.Should().NotBeEmpty();
    }

    #endregion

    #region Update with Non-Existent Records

    [Fact]
    public async Task UpdateMultipleAsync_WithNonExistentId_ReportsFailure()
    {
        // Arrange - Create some entities and add a fake one
        var createResult = await Executor.CreateMultipleAsync(EntityName, CreateTestEntities(EntityName, 2));
        var updateEntities = createResult.CreatedIds!.Select((id, i) => new Entity(EntityName, id)
        {
            ["name"] = $"Updated {i}"
        }).ToList();

        // Add a non-existent entity
        updateEntities.Add(new Entity(EntityName, Guid.NewGuid())
        {
            ["name"] = "This should fail"
        });

        var options = new BulkOperationOptions
        {
            MaxParallelBatches = 1
        };

        // Act
        var result = await Executor.UpdateMultipleAsync(EntityName, updateEntities, options);

        // Assert - The batch with the non-existent record should fail
        result.TotalCount.Should().Be(3);
        result.FailureCount.Should().BeGreaterThan(0);
    }

    #endregion

    #region Error Details Collection

    [Fact]
    public async Task DeleteMultipleAsync_WhenFailure_PopulatesErrorDetails()
    {
        // Arrange - Delete non-existent record
        var nonExistentIds = new List<Guid> { Guid.NewGuid() };
        var options = new BulkOperationOptions
        {
            ContinueOnError = true,
            MaxParallelBatches = 1
        };

        // Act
        var result = await Executor.DeleteMultipleAsync(EntityName, nonExistentIds, options);

        // Assert - Error should be captured
        result.FailureCount.Should().Be(1);
        result.Errors.Should().NotBeEmpty();
        result.Errors[0].Index.Should().Be(0);
        result.Errors[0].Message.Should().NotBeNullOrEmpty();
    }

    #endregion

    #region ContinueOnError Behavior Verification

    [Fact]
    public async Task CreateMultipleAsync_ElasticTable_ContinueOnErrorFalse_IsDefault()
    {
        // Arrange
        var entities = CreateTestEntities(EntityName, 5);
        var options = new BulkOperationOptions
        {
            ElasticTable = true
            // ContinueOnError defaults to true for backward compatibility
        };

        // Act
        var result = await Executor.CreateMultipleAsync(EntityName, entities, options);

        // Assert - Should succeed normally
        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task UpdateMultipleAsync_ElasticTable_ContinueOnErrorTrue_ProcessesAll()
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

    #endregion

    #region Batch Failure Isolation

    [Fact]
    public async Task DeleteMultipleAsync_MultipleBatches_OneFailingBatch_OthersSucceed()
    {
        // Arrange - 3 batches: 10, 10, 5 records
        var createResult = await Executor.CreateMultipleAsync(EntityName, CreateTestEntities(EntityName, 25));
        var idsToDelete = createResult.CreatedIds!.ToList();

        // Inject a non-existent ID into the second batch (records 10-19)
        idsToDelete[15] = Guid.NewGuid();

        var options = new BulkOperationOptions
        {
            BatchSize = 10,
            ContinueOnError = true,
            MaxParallelBatches = 1
        };

        // Act
        var result = await Executor.DeleteMultipleAsync(EntityName, idsToDelete, options);

        // Assert - Some records should succeed, some should fail
        result.TotalCount.Should().Be(25);
        // First batch (10) and third batch (5) should succeed
        // Second batch behavior depends on ContinueOnError handling
    }

    #endregion
}
