using FluentAssertions;
using PPDS.Dataverse.Progress;
using Xunit;

namespace PPDS.Dataverse.Tests.Progress;

/// <summary>
/// Tests for ProgressSnapshot.
/// </summary>
public class ProgressSnapshotTests
{
    #region Computed Property Tests

    [Fact]
    public void Processed_ReturnsSucceededPlusFailed()
    {
        // Arrange
        var snapshot = new ProgressSnapshot
        {
            Succeeded = 80,
            Failed = 20,
            Total = 100
        };

        // Assert
        snapshot.Processed.Should().Be(100);
    }

    [Fact]
    public void Remaining_ReturnsCorrectValue()
    {
        // Arrange
        var snapshot = new ProgressSnapshot
        {
            Succeeded = 30,
            Failed = 10,
            Total = 100
        };

        // Assert
        snapshot.Remaining.Should().Be(60);
    }

    [Fact]
    public void Remaining_WhenProcessedExceedsTotal_ReturnsZero()
    {
        // This can happen if TotalCount was estimated too low
        var snapshot = new ProgressSnapshot
        {
            Succeeded = 150,
            Failed = 0,
            Total = 100
        };

        // Assert
        snapshot.Remaining.Should().Be(0);
    }

    [Fact]
    public void PercentComplete_CalculatesCorrectly()
    {
        // Arrange
        var snapshot = new ProgressSnapshot
        {
            Succeeded = 50,
            Failed = 0,
            Total = 100
        };

        // Assert
        snapshot.PercentComplete.Should().Be(50.0);
    }

    [Fact]
    public void PercentComplete_WhenZeroTotal_ReturnsZero()
    {
        // Arrange
        var snapshot = new ProgressSnapshot
        {
            Succeeded = 0,
            Failed = 0,
            Total = 0
        };

        // Assert - avoid divide by zero
        snapshot.PercentComplete.Should().Be(0);
    }

    [Fact]
    public void PercentComplete_At100Percent_Returns100()
    {
        // Arrange
        var snapshot = new ProgressSnapshot
        {
            Succeeded = 90,
            Failed = 10,
            Total = 100
        };

        // Assert
        snapshot.PercentComplete.Should().Be(100.0);
    }

    #endregion

    #region Rate Property Tests

    [Fact]
    public void RatePerSecond_ReturnsOverallRate()
    {
        // Arrange
        var snapshot = new ProgressSnapshot
        {
            OverallRatePerSecond = 1000.0,
            InstantRatePerSecond = 500.0
        };

        // Assert - RatePerSecond should be the same as OverallRatePerSecond
        snapshot.RatePerSecond.Should().Be(snapshot.OverallRatePerSecond);
    }

    #endregion

    #region Estimated Completion Tests

    [Fact]
    public void EstimatedCompletionUtc_CalculatesCorrectly()
    {
        // Arrange
        var snapshot = new ProgressSnapshot
        {
            EstimatedRemaining = TimeSpan.FromMinutes(5)
        };

        var beforeCheck = DateTime.UtcNow;
        var completionTime = snapshot.EstimatedCompletionUtc;
        var afterCheck = DateTime.UtcNow;

        // Assert - completion time should be approximately 5 minutes from now
        completionTime.Should().BeAfter(beforeCheck.AddMinutes(4).AddSeconds(55));
        completionTime.Should().BeBefore(afterCheck.AddMinutes(5).AddSeconds(5));
    }

    [Fact]
    public void EstimatedCompletionUtc_WhenMaxValue_ReturnsMaxValue()
    {
        // Arrange
        var snapshot = new ProgressSnapshot
        {
            EstimatedRemaining = TimeSpan.MaxValue
        };

        // Assert
        snapshot.EstimatedCompletionUtc.Should().Be(DateTime.MaxValue);
    }

    #endregion

    #region Init Property Tests

    [Fact]
    public void AllInitProperties_CanBeSet()
    {
        // Arrange & Act
        var snapshot = new ProgressSnapshot
        {
            Succeeded = 100,
            Failed = 10,
            Total = 200,
            Elapsed = TimeSpan.FromMinutes(1),
            OverallRatePerSecond = 100.0,
            InstantRatePerSecond = 150.0,
            EstimatedRemaining = TimeSpan.FromMinutes(2)
        };

        // Assert
        snapshot.Succeeded.Should().Be(100);
        snapshot.Failed.Should().Be(10);
        snapshot.Total.Should().Be(200);
        snapshot.Elapsed.Should().Be(TimeSpan.FromMinutes(1));
        snapshot.OverallRatePerSecond.Should().Be(100.0);
        snapshot.InstantRatePerSecond.Should().Be(150.0);
        snapshot.EstimatedRemaining.Should().Be(TimeSpan.FromMinutes(2));
    }

    #endregion

    #region Immutability Tests

    [Fact]
    public void Snapshot_IsImmutable()
    {
        // Create a snapshot
        var snapshot = new ProgressSnapshot
        {
            Succeeded = 50,
            Failed = 5,
            Total = 100
        };

        // Properties should be read-only after construction
        // (This test documents the expected behavior - init properties can't be set after construction)
        snapshot.Processed.Should().Be(55);
        snapshot.Remaining.Should().Be(45);
        snapshot.PercentComplete.Should().BeApproximately(55.0, 0.001);
    }

    #endregion

    #region Edge Case Tests

    [Fact]
    public void Snapshot_WithLargeNumbers()
    {
        // Arrange - simulate large migration
        var snapshot = new ProgressSnapshot
        {
            Succeeded = 10_000_000,
            Failed = 1_000,
            Total = 20_000_000,
            Elapsed = TimeSpan.FromHours(2),
            OverallRatePerSecond = 10_000_001.0 / 7200.0,
            InstantRatePerSecond = 2000.0,
            EstimatedRemaining = TimeSpan.FromHours(2)
        };

        // Assert
        snapshot.Processed.Should().Be(10_001_000);
        snapshot.Remaining.Should().Be(9_999_000);
        snapshot.PercentComplete.Should().BeApproximately(50.005, 0.001);
    }

    [Fact]
    public void Snapshot_WithZeroElapsed()
    {
        // Arrange
        var snapshot = new ProgressSnapshot
        {
            Succeeded = 0,
            Failed = 0,
            Total = 100,
            Elapsed = TimeSpan.Zero,
            OverallRatePerSecond = 0,
            InstantRatePerSecond = 0,
            EstimatedRemaining = TimeSpan.MaxValue
        };

        // Assert
        snapshot.RatePerSecond.Should().Be(0);
        snapshot.EstimatedCompletionUtc.Should().Be(DateTime.MaxValue);
    }

    #endregion
}
