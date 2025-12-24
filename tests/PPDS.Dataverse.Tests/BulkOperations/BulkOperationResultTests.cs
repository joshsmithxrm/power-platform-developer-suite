using FluentAssertions;
using PPDS.Dataverse.BulkOperations;
using Xunit;

namespace PPDS.Dataverse.Tests.BulkOperations;

public class BulkOperationResultTests
{
    #region Basic Properties Tests

    [Fact]
    public void BulkOperationResult_DefaultValues_AreCorrect()
    {
        // Arrange & Act
        var result = new BulkOperationResult();

        // Assert
        result.SuccessCount.Should().Be(0);
        result.FailureCount.Should().Be(0);
        result.Errors.Should().BeEmpty();
        result.Duration.Should().Be(TimeSpan.Zero);
        result.IsSuccess.Should().BeTrue();
        result.TotalCount.Should().Be(0);
        result.CreatedIds.Should().BeNull();
        result.CreatedCount.Should().BeNull();
        result.UpdatedCount.Should().BeNull();
    }

    [Fact]
    public void IsSuccess_ReturnsFalse_WhenFailureCountGreaterThanZero()
    {
        // Arrange & Act
        var result = new BulkOperationResult
        {
            SuccessCount = 10,
            FailureCount = 2
        };

        // Assert
        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public void TotalCount_ReturnsSumOfSuccessAndFailure()
    {
        // Arrange & Act
        var result = new BulkOperationResult
        {
            SuccessCount = 8,
            FailureCount = 2
        };

        // Assert
        result.TotalCount.Should().Be(10);
    }

    #endregion

    #region CreatedCount and UpdatedCount Tests

    [Fact]
    public void CreatedCount_CanBeSetAndRetrieved()
    {
        // Arrange & Act
        var result = new BulkOperationResult
        {
            SuccessCount = 100,
            CreatedCount = 60
        };

        // Assert
        result.CreatedCount.Should().Be(60);
    }

    [Fact]
    public void UpdatedCount_CanBeSetAndRetrieved()
    {
        // Arrange & Act
        var result = new BulkOperationResult
        {
            SuccessCount = 100,
            UpdatedCount = 40
        };

        // Assert
        result.UpdatedCount.Should().Be(40);
    }

    [Fact]
    public void CreatedCount_AndUpdatedCount_SumToSuccessCount()
    {
        // Arrange & Act
        var result = new BulkOperationResult
        {
            SuccessCount = 100,
            CreatedCount = 60,
            UpdatedCount = 40
        };

        // Assert
        result.CreatedCount.Should().Be(60);
        result.UpdatedCount.Should().Be(40);
        (result.CreatedCount + result.UpdatedCount).Should().Be(result.SuccessCount);
    }

    [Fact]
    public void CreatedCount_IsNull_ForNonUpsertOperations()
    {
        // Arrange - simulating a CreateMultiple result
        var result = new BulkOperationResult
        {
            SuccessCount = 50,
            FailureCount = 0,
            CreatedIds = new List<Guid> { Guid.NewGuid(), Guid.NewGuid() }
            // CreatedCount and UpdatedCount are not set for CreateMultiple
        };

        // Assert
        result.CreatedCount.Should().BeNull();
        result.UpdatedCount.Should().BeNull();
        result.CreatedIds.Should().NotBeNull();
    }

    [Fact]
    public void UpsertResult_HasCreatedAndUpdatedCounts_ButNoCreatedIds()
    {
        // Arrange - simulating an UpsertMultiple result
        var result = new BulkOperationResult
        {
            SuccessCount = 100,
            FailureCount = 0,
            CreatedCount = 25,
            UpdatedCount = 75
            // CreatedIds is not set for UpsertMultiple
        };

        // Assert
        result.CreatedCount.Should().Be(25);
        result.UpdatedCount.Should().Be(75);
        result.CreatedIds.Should().BeNull();
    }

    [Fact]
    public void ZeroCreatedCount_IsDifferentFromNull()
    {
        // Arrange & Act
        var resultWithZero = new BulkOperationResult
        {
            SuccessCount = 10,
            CreatedCount = 0,
            UpdatedCount = 10
        };

        var resultWithNull = new BulkOperationResult
        {
            SuccessCount = 10
        };

        // Assert
        resultWithZero.CreatedCount.Should().Be(0);
        resultWithZero.CreatedCount.Should().NotBeNull();

        resultWithNull.CreatedCount.Should().BeNull();
    }

    [Fact]
    public void AllUpdates_HasZeroCreatedCount()
    {
        // Arrange - all records were updated (none created)
        var result = new BulkOperationResult
        {
            SuccessCount = 50,
            FailureCount = 0,
            CreatedCount = 0,
            UpdatedCount = 50
        };

        // Assert
        result.CreatedCount.Should().Be(0);
        result.UpdatedCount.Should().Be(50);
    }

    [Fact]
    public void AllCreates_HasZeroUpdatedCount()
    {
        // Arrange - all records were created (none updated)
        var result = new BulkOperationResult
        {
            SuccessCount = 50,
            FailureCount = 0,
            CreatedCount = 50,
            UpdatedCount = 0
        };

        // Assert
        result.CreatedCount.Should().Be(50);
        result.UpdatedCount.Should().Be(0);
    }

    #endregion

    #region Record Immutability Tests

    [Fact]
    public void BulkOperationResult_IsRecord_SupportsWithExpression()
    {
        // Arrange
        var original = new BulkOperationResult
        {
            SuccessCount = 100,
            CreatedCount = 60,
            UpdatedCount = 40
        };

        // Act - use 'with' expression to create modified copy
        var modified = original with { Duration = TimeSpan.FromSeconds(5) };

        // Assert
        modified.SuccessCount.Should().Be(100);
        modified.CreatedCount.Should().Be(60);
        modified.UpdatedCount.Should().Be(40);
        modified.Duration.Should().Be(TimeSpan.FromSeconds(5));

        // Original is unchanged
        original.Duration.Should().Be(TimeSpan.Zero);
    }

    #endregion
}
