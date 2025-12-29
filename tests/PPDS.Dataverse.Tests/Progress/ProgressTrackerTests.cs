using FluentAssertions;
using PPDS.Dataverse.Progress;
using Xunit;

namespace PPDS.Dataverse.Tests.Progress;

/// <summary>
/// Tests for ProgressTracker.
/// </summary>
public class ProgressTrackerTests
{
    #region Constructor Tests

    [Fact]
    public void Constructor_WithValidTotalCount_InitializesCorrectly()
    {
        // Act
        var tracker = new ProgressTracker(1000);

        // Assert
        tracker.TotalCount.Should().Be(1000);
        tracker.Succeeded.Should().Be(0);
        tracker.Failed.Should().Be(0);
        tracker.Processed.Should().Be(0);
    }

    [Fact]
    public void Constructor_WithZeroTotalCount_InitializesCorrectly()
    {
        // Act
        var tracker = new ProgressTracker(0);

        // Assert
        tracker.TotalCount.Should().Be(0);
    }

    [Fact]
    public void Constructor_WithNegativeTotalCount_ThrowsArgumentOutOfRangeException()
    {
        // Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProgressTracker(-1));
    }

    [Fact]
    public void Constructor_WithCustomRollingWindow_Succeeds()
    {
        // Act
        var tracker = new ProgressTracker(1000, rollingWindowSeconds: 60);

        // Assert
        tracker.TotalCount.Should().Be(1000);
    }

    [Fact]
    public void Constructor_WithRollingWindowLessThanOne_ThrowsArgumentOutOfRangeException()
    {
        // Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProgressTracker(1000, rollingWindowSeconds: 0));
    }

    #endregion

    #region RecordProgress Tests

    [Fact]
    public void RecordProgress_WithSuccessCount_IncrementsSucceeded()
    {
        // Arrange
        var tracker = new ProgressTracker(100);

        // Act
        tracker.RecordProgress(10);

        // Assert
        tracker.Succeeded.Should().Be(10);
        tracker.Failed.Should().Be(0);
        tracker.Processed.Should().Be(10);
    }

    [Fact]
    public void RecordProgress_WithSuccessAndFailure_IncrementsBoth()
    {
        // Arrange
        var tracker = new ProgressTracker(100);

        // Act
        tracker.RecordProgress(successCount: 8, failureCount: 2);

        // Assert
        tracker.Succeeded.Should().Be(8);
        tracker.Failed.Should().Be(2);
        tracker.Processed.Should().Be(10);
    }

    [Fact]
    public void RecordProgress_MultipleCalls_Accumulates()
    {
        // Arrange
        var tracker = new ProgressTracker(100);

        // Act
        tracker.RecordProgress(10);
        tracker.RecordProgress(20);
        tracker.RecordProgress(5, 3);

        // Assert
        tracker.Succeeded.Should().Be(35);
        tracker.Failed.Should().Be(3);
        tracker.Processed.Should().Be(38);
    }

    [Fact]
    public void RecordProgress_WithZeroCounts_DoesNotChange()
    {
        // Arrange
        var tracker = new ProgressTracker(100);
        tracker.RecordProgress(10);

        // Act
        tracker.RecordProgress(0, 0);

        // Assert
        tracker.Succeeded.Should().Be(10);
        tracker.Failed.Should().Be(0);
    }

    [Fact]
    public void RecordProgress_WithNegativeSuccessCount_ThrowsArgumentOutOfRangeException()
    {
        // Arrange
        var tracker = new ProgressTracker(100);

        // Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(() => tracker.RecordProgress(-1));
    }

    [Fact]
    public void RecordProgress_WithNegativeFailureCount_ThrowsArgumentOutOfRangeException()
    {
        // Arrange
        var tracker = new ProgressTracker(100);

        // Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(() => tracker.RecordProgress(10, -1));
    }

    #endregion

    #region GetSnapshot Tests

    [Fact]
    public void GetSnapshot_InitialState_ReturnsZeroProgress()
    {
        // Arrange
        var tracker = new ProgressTracker(100);

        // Act
        var snapshot = tracker.GetSnapshot();

        // Assert
        snapshot.Succeeded.Should().Be(0);
        snapshot.Failed.Should().Be(0);
        snapshot.Total.Should().Be(100);
        snapshot.Processed.Should().Be(0);
        snapshot.Remaining.Should().Be(100);
        snapshot.PercentComplete.Should().Be(0);
    }

    [Fact]
    public void GetSnapshot_AfterProgress_ReturnsCorrectValues()
    {
        // Arrange
        var tracker = new ProgressTracker(100);
        tracker.RecordProgress(50);

        // Act
        var snapshot = tracker.GetSnapshot();

        // Assert
        snapshot.Succeeded.Should().Be(50);
        snapshot.Total.Should().Be(100);
        snapshot.Processed.Should().Be(50);
        snapshot.Remaining.Should().Be(50);
        snapshot.PercentComplete.Should().Be(50.0);
    }

    [Fact]
    public void GetSnapshot_WhenComplete_ReturnsCorrectValues()
    {
        // Arrange
        var tracker = new ProgressTracker(100);
        tracker.RecordProgress(95, 5);

        // Act
        var snapshot = tracker.GetSnapshot();

        // Assert
        snapshot.Succeeded.Should().Be(95);
        snapshot.Failed.Should().Be(5);
        snapshot.Processed.Should().Be(100);
        snapshot.Remaining.Should().Be(0);
        snapshot.PercentComplete.Should().Be(100.0);
    }

    [Fact]
    public void GetSnapshot_HasElapsedTime()
    {
        // Arrange
        var tracker = new ProgressTracker(100);

        // Wait a small amount of time
        Thread.Sleep(50);

        // Act
        var snapshot = tracker.GetSnapshot();

        // Assert
        snapshot.Elapsed.Should().BeGreaterThan(TimeSpan.Zero);
    }

    [Fact]
    public void GetSnapshot_CalculatesRates()
    {
        // Arrange
        var tracker = new ProgressTracker(1000);

        // Simulate some progress over time
        tracker.RecordProgress(100);
        Thread.Sleep(100);
        tracker.RecordProgress(100);

        // Act
        var snapshot = tracker.GetSnapshot();

        // Assert
        snapshot.OverallRatePerSecond.Should().BeGreaterThan(0);
        snapshot.InstantRatePerSecond.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public void GetSnapshot_CalculatesEstimatedRemaining()
    {
        // Arrange
        var tracker = new ProgressTracker(1000);

        // Simulate progress
        for (int i = 0; i < 10; i++)
        {
            tracker.RecordProgress(10);
            Thread.Sleep(10);
        }

        // Act
        var snapshot = tracker.GetSnapshot();

        // Assert - ETA should be a positive value (there's remaining work)
        snapshot.EstimatedRemaining.Should().BeGreaterThan(TimeSpan.Zero);
    }

    #endregion

    #region Reset Tests

    [Fact]
    public void Reset_ClearsProgress()
    {
        // Arrange
        var tracker = new ProgressTracker(100);
        tracker.RecordProgress(50, 10);

        // Act
        tracker.Reset();

        // Assert
        tracker.Succeeded.Should().Be(0);
        tracker.Failed.Should().Be(0);
        tracker.Processed.Should().Be(0);
    }

    [Fact]
    public void Reset_TotalCountUnchanged()
    {
        // Arrange
        var tracker = new ProgressTracker(100);
        tracker.RecordProgress(50);

        // Act
        tracker.Reset();

        // Assert
        tracker.TotalCount.Should().Be(100);
    }

    [Fact]
    public void Reset_RestartsElapsedTimer()
    {
        // Arrange
        var tracker = new ProgressTracker(100);
        Thread.Sleep(100);
        var beforeReset = tracker.GetSnapshot().Elapsed;

        // Act
        tracker.Reset();
        var afterReset = tracker.GetSnapshot().Elapsed;

        // Assert
        afterReset.Should().BeLessThan(beforeReset);
    }

    #endregion

    #region Thread Safety Tests

    [Fact]
    public void RecordProgress_ThreadSafe()
    {
        // Arrange
        var tracker = new ProgressTracker(10000);
        const int threadCount = 10;
        const int incrementsPerThread = 100;

        // Act - run multiple threads updating concurrently
        var tasks = new Task[threadCount];
        for (int i = 0; i < threadCount; i++)
        {
            tasks[i] = Task.Run(() =>
            {
                for (int j = 0; j < incrementsPerThread; j++)
                {
                    tracker.RecordProgress(1);
                }
            });
        }
        Task.WaitAll(tasks);

        // Assert
        tracker.Succeeded.Should().Be(threadCount * incrementsPerThread);
    }

    [Fact]
    public void GetSnapshot_ThreadSafe()
    {
        // Arrange
        var tracker = new ProgressTracker(10000);
        const int iterations = 100;
        var snapshots = new List<ProgressSnapshot>();
        var lockObj = new object();

        // Act - run GetSnapshot and RecordProgress concurrently
        var recordTask = Task.Run(() =>
        {
            for (int i = 0; i < iterations; i++)
            {
                tracker.RecordProgress(10);
                Thread.Sleep(1);
            }
        });

        var snapshotTask = Task.Run(() =>
        {
            for (int i = 0; i < iterations; i++)
            {
                var snapshot = tracker.GetSnapshot();
                lock (lockObj)
                {
                    snapshots.Add(snapshot);
                }
                Thread.Sleep(1);
            }
        });

        Task.WaitAll(recordTask, snapshotTask);

        // Assert - all snapshots should be valid (no exceptions thrown)
        snapshots.Count.Should().Be(iterations);
        snapshots.All(s => s.Total == 10000).Should().BeTrue();
    }

    #endregion
}
