using FluentAssertions;
using PPDS.Dataverse.BulkOperations;
using Xunit;

namespace PPDS.Dataverse.Tests.BulkOperations;

[Trait("Category", "Unit")]
public class AdaptiveBatchSizerTests
{
    #region Constructor Tests

    [Fact]
    public void Constructor_DefaultParameters_SetsBatchSizeTo100()
    {
        var sizer = new AdaptiveBatchSizer();

        sizer.CurrentBatchSize.Should().Be(100);
    }

    [Fact]
    public void Constructor_CustomInitialSize_SetsCurrentBatchSize()
    {
        var sizer = new AdaptiveBatchSizer(initialSize: 200);

        sizer.CurrentBatchSize.Should().Be(200);
    }

    [Fact]
    public void Constructor_InitialSizeBelowMin_ClampsToMin()
    {
        var sizer = new AdaptiveBatchSizer(initialSize: 0, minSize: 10);

        sizer.CurrentBatchSize.Should().Be(10);
    }

    [Fact]
    public void Constructor_InitialSizeAboveMax_ClampsToMax()
    {
        var sizer = new AdaptiveBatchSizer(initialSize: 5000, maxSize: 500);

        sizer.CurrentBatchSize.Should().Be(500);
    }

    [Fact]
    public void Constructor_NegativeTargetSeconds_DefaultsTo10()
    {
        // Should not throw; uses fallback of 10.0
        var sizer = new AdaptiveBatchSizer(initialSize: 100, targetSeconds: -5.0);

        sizer.CurrentBatchSize.Should().Be(100);

        // Verify it works correctly with the fallback target
        sizer.RecordBatchResult(100, TimeSpan.FromSeconds(10));
        // 100 records in 10s = 10 rec/s, target = 10 rec/s * 10s = 100
        // (100 + 100) / 2 = 100 — no change because we are at target
        sizer.CurrentBatchSize.Should().Be(100);
    }

    #endregion

    #region Batch Size Adjustment Tests

    [Fact]
    public void FastBatch_IncreasesBatchSize()
    {
        var sizer = new AdaptiveBatchSizer(initialSize: 100, targetSeconds: 10.0);

        // 100 records in 2 seconds = 50 rec/s → target = 500
        // New size = (100 + 500) / 2 = 300
        sizer.RecordBatchResult(100, TimeSpan.FromSeconds(2));

        sizer.CurrentBatchSize.Should().BeGreaterThan(100);
    }

    [Fact]
    public void SlowBatch_DecreasesBatchSize()
    {
        var sizer = new AdaptiveBatchSizer(initialSize: 100, targetSeconds: 10.0);

        // 100 records in 30 seconds = 3.33 rec/s → target = 33
        // New size = (100 + 33) / 2 = 66
        sizer.RecordBatchResult(100, TimeSpan.FromSeconds(30));

        sizer.CurrentBatchSize.Should().BeLessThan(100);
    }

    [Fact]
    public void BatchSize_NeverBelowMinimum()
    {
        var sizer = new AdaptiveBatchSizer(initialSize: 10, targetSeconds: 10.0, minSize: 5);

        // Extremely slow: 10 records in 600 seconds = 0.0167 rec/s → target = 0
        // New size = (10 + 0) / 2 = 5, clamped to min 5
        sizer.RecordBatchResult(10, TimeSpan.FromSeconds(600));

        sizer.CurrentBatchSize.Should().BeGreaterThanOrEqualTo(5);

        // Even slower to push further down
        sizer.RecordBatchResult(5, TimeSpan.FromSeconds(6000));

        sizer.CurrentBatchSize.Should().BeGreaterThanOrEqualTo(5);
    }

    [Fact]
    public void BatchSize_NeverAboveMaximum()
    {
        var sizer = new AdaptiveBatchSizer(initialSize: 500, targetSeconds: 10.0, maxSize: 1000);

        // Extremely fast: 500 records in 0.1 seconds = 5000 rec/s → target = 50000
        // New size = (500 + 50000) / 2 = 25250, clamped to max 1000
        sizer.RecordBatchResult(500, TimeSpan.FromSeconds(0.1));

        sizer.CurrentBatchSize.Should().BeLessThanOrEqualTo(1000);

        // Another very fast batch
        sizer.RecordBatchResult(1000, TimeSpan.FromSeconds(0.01));

        sizer.CurrentBatchSize.Should().BeLessThanOrEqualTo(1000);
    }

    [Fact]
    public void ZeroElapsed_NoChange()
    {
        var sizer = new AdaptiveBatchSizer(initialSize: 100);

        sizer.RecordBatchResult(100, TimeSpan.Zero);

        sizer.CurrentBatchSize.Should().Be(100);
    }

    [Fact]
    public void NegativeElapsed_NoChange()
    {
        var sizer = new AdaptiveBatchSizer(initialSize: 100);

        sizer.RecordBatchResult(100, TimeSpan.FromSeconds(-5));

        sizer.CurrentBatchSize.Should().Be(100);
    }

    [Fact]
    public void ZeroBatchSize_NoChange()
    {
        var sizer = new AdaptiveBatchSizer(initialSize: 100);

        sizer.RecordBatchResult(0, TimeSpan.FromSeconds(5));

        sizer.CurrentBatchSize.Should().Be(100);
    }

    [Fact]
    public void NegativeBatchSize_NoChange()
    {
        var sizer = new AdaptiveBatchSizer(initialSize: 100);

        sizer.RecordBatchResult(-10, TimeSpan.FromSeconds(5));

        sizer.CurrentBatchSize.Should().Be(100);
    }

    #endregion

    #region Convergence Tests

    [Fact]
    public void MultipleBatches_ConvergesToTarget()
    {
        var sizer = new AdaptiveBatchSizer(initialSize: 100, targetSeconds: 10.0, maxSize: 1000);

        // Simulate 5 batches where the entity processes at a consistent 50 rec/s
        // Target batch size at 50 rec/s for 10s = 500 records
        const double recordsPerSecond = 50.0;

        for (int i = 0; i < 5; i++)
        {
            var batchSize = sizer.CurrentBatchSize;
            var elapsed = TimeSpan.FromSeconds(batchSize / recordsPerSecond);
            sizer.RecordBatchResult(batchSize, elapsed);
        }

        // After 5 iterations of halving the distance to 500, should be close:
        // Iteration 0: size=100, elapsed=2s, rate=50, target=500, new=(100+500)/2=300
        // Iteration 1: size=300, elapsed=6s, rate=50, target=500, new=(300+500)/2=400
        // Iteration 2: size=400, elapsed=8s, rate=50, target=500, new=(400+500)/2=450
        // Iteration 3: size=450, elapsed=9s, rate=50, target=500, new=(450+500)/2=475
        // Iteration 4: size=475, elapsed=9.5s, rate=50, target=500, new=(475+500)/2=487
        sizer.CurrentBatchSize.Should().BeInRange(400, 500,
            "after 5 iterations the batch size should converge near the target of 500");
    }

    [Fact]
    public void MultipleBatches_FromAboveTarget_ConvergesDownward()
    {
        var sizer = new AdaptiveBatchSizer(initialSize: 900, targetSeconds: 10.0, maxSize: 1000);

        // Simulate entity that processes at 10 rec/s → target = 100
        const double recordsPerSecond = 10.0;

        for (int i = 0; i < 5; i++)
        {
            var batchSize = sizer.CurrentBatchSize;
            var elapsed = TimeSpan.FromSeconds(batchSize / recordsPerSecond);
            sizer.RecordBatchResult(batchSize, elapsed);
        }

        // Should converge toward 100 from above
        sizer.CurrentBatchSize.Should().BeLessThan(200,
            "after 5 iterations the batch size should converge near the target of 100");
    }

    #endregion

    #region Smoothing Behavior Tests

    [Fact]
    public void Smoothing_PreventsAbruptJumps()
    {
        var sizer = new AdaptiveBatchSizer(initialSize: 100, targetSeconds: 10.0, maxSize: 1000);

        // Very fast batch: 100 records in 1 second = 100 rec/s → target = 1000
        // Without smoothing, would jump to 1000
        // With smoothing: (100 + 1000) / 2 = 550
        sizer.RecordBatchResult(100, TimeSpan.FromSeconds(1));

        sizer.CurrentBatchSize.Should().Be(550,
            "smoothing should move halfway from 100 to 1000");
    }

    [Fact]
    public void AtTargetRate_BatchSizeStaysStable()
    {
        var sizer = new AdaptiveBatchSizer(initialSize: 100, targetSeconds: 10.0);

        // If we process exactly at the rate that makes current batch size perfect:
        // 100 records in 10 seconds = 10 rec/s → target = 100
        // (100 + 100) / 2 = 100 — no change
        sizer.RecordBatchResult(100, TimeSpan.FromSeconds(10));

        sizer.CurrentBatchSize.Should().Be(100);
    }

    #endregion
}
