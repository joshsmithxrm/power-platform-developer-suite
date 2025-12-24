using System;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using PPDS.Dataverse.DependencyInjection;
using PPDS.Dataverse.Resilience;
using Xunit;

namespace PPDS.Dataverse.Tests.Resilience;

/// <summary>
/// Tests for AIMD-based adaptive rate controller.
/// </summary>
public class AdaptiveRateControllerTests
{
    private readonly Mock<ILogger<AdaptiveRateController>> _loggerMock;

    public AdaptiveRateControllerTests()
    {
        _loggerMock = new Mock<ILogger<AdaptiveRateController>>();
    }

    private AdaptiveRateController CreateController(AdaptiveRateOptions? rateOptions = null)
    {
        var options = new DataverseOptions
        {
            AdaptiveRate = rateOptions ?? new AdaptiveRateOptions()
        };

        return new AdaptiveRateController(
            Options.Create(options),
            _loggerMock.Object);
    }

    #region Initialization Tests

    [Fact]
    public void GetParallelism_InitialValue_StartsAtFloor()
    {
        // Arrange
        var controller = CreateController();

        // Act - recommendedParallelism=10, connectionCount=1
        var parallelism = controller.GetParallelism("Primary", recommendedParallelism: 10, connectionCount: 1);

        // Assert - starts at floor (recommended * connections = 10 * 1 = 10)
        parallelism.Should().Be(10);
    }

    [Fact]
    public void GetParallelism_WithMultipleConnections_ScalesFloor()
    {
        // Arrange
        var controller = CreateController();

        // Act - recommendedParallelism=5, connectionCount=2
        var parallelism = controller.GetParallelism("Primary", recommendedParallelism: 5, connectionCount: 2);

        // Assert - floor = 5 * 2 = 10
        parallelism.Should().Be(10);
    }

    [Fact]
    public void GetParallelism_WhenDisabled_ReturnsScaledRecommended()
    {
        // Arrange
        var controller = CreateController(new AdaptiveRateOptions
        {
            Enabled = false
        });

        // Act
        var parallelism = controller.GetParallelism("Primary", recommendedParallelism: 10, connectionCount: 2);

        // Assert - when disabled, returns min(recommended*connections, ceiling*connections)
        // HardCeiling is fixed at 52
        parallelism.Should().Be(20); // min(10*2, 52*2) = min(20, 104) = 20
    }

    [Fact]
    public void IsEnabled_ReflectsOptionsValue()
    {
        // Arrange
        var enabled = CreateController(new AdaptiveRateOptions { Enabled = true });
        var disabled = CreateController(new AdaptiveRateOptions { Enabled = false });

        // Assert
        enabled.IsEnabled.Should().BeTrue();
        disabled.IsEnabled.Should().BeFalse();
    }

    #endregion

    #region Throttle Tests

    [Fact]
    public void RecordThrottle_ReducesParallelism()
    {
        // Arrange - floor=10, ceiling=52, probe up then throttle
        var controller = CreateController(new AdaptiveRateOptions
        {
            DecreaseFactor = 0.5,
            StabilizationBatches = 1,
            MinIncreaseInterval = TimeSpan.Zero
        });

        controller.GetParallelism("Primary", recommendedParallelism: 10, connectionCount: 1); // Init at 10
        controller.RecordSuccess("Primary"); // Increase to 20 (10 + 10)
        controller.RecordSuccess("Primary"); // Increase to 30 (20 + 10)
        controller.RecordSuccess("Primary"); // Increase to 40 (30 + 10)

        var before = controller.GetStatistics("Primary")!.CurrentParallelism;
        before.Should().Be(40);

        // Act
        controller.RecordThrottle("Primary", TimeSpan.FromSeconds(30));

        // Assert - 40 * 0.5 = 20, above floor of 10
        var after = controller.GetStatistics("Primary")!.CurrentParallelism;
        after.Should().Be(20);
    }

    [Fact]
    public void RecordThrottle_RespectsFloor()
    {
        // Arrange
        var controller = CreateController(new AdaptiveRateOptions
        {
            DecreaseFactor = 0.5
        });

        controller.GetParallelism("Primary", recommendedParallelism: 10, connectionCount: 1); // Init at 10

        // Act - throttle should reduce by 50%, but floor is 10
        controller.RecordThrottle("Primary", TimeSpan.FromSeconds(30));

        // Assert - 10 * 0.5 = 5, but floor is 10, so stays at 10
        var parallelism = controller.GetStatistics("Primary")!.CurrentParallelism;
        parallelism.Should().Be(10);
    }

    [Fact]
    public void RecordThrottle_UpdatesStatistics()
    {
        // Arrange
        var controller = CreateController();
        controller.GetParallelism("Primary", recommendedParallelism: 10, connectionCount: 1);

        // Act
        controller.RecordThrottle("Primary", TimeSpan.FromSeconds(30));

        // Assert
        var stats = controller.GetStatistics("Primary");
        stats.Should().NotBeNull();
        stats!.TotalThrottleEvents.Should().Be(1);
        stats.LastThrottleTime.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void RecordThrottle_CalculatesThrottleCeiling()
    {
        // Arrange - start at 10, probe to 40, then throttle with 2.5 min Retry-After
        var controller = CreateController(new AdaptiveRateOptions
        {
            DecreaseFactor = 0.5,
            StabilizationBatches = 1,
            MinIncreaseInterval = TimeSpan.Zero
        });

        controller.GetParallelism("Primary", recommendedParallelism: 10, connectionCount: 1);
        controller.RecordSuccess("Primary"); // 20
        controller.RecordSuccess("Primary"); // 30
        controller.RecordSuccess("Primary"); // 40

        // Act - 2.5 min Retry-After = 75% reduction factor (overshootRatio = 0.5)
        controller.RecordThrottle("Primary", TimeSpan.FromMinutes(2.5));

        // Assert
        var stats = controller.GetStatistics("Primary");
        stats!.ThrottleCeiling.Should().NotBeNull();
        // throttleCeiling = 40 * 0.75 = 30
        stats.ThrottleCeiling.Should().Be(30);
        stats.ThrottleCeilingExpiry.Should().BeCloseTo(
            DateTime.UtcNow + TimeSpan.FromMinutes(2.5) + TimeSpan.FromMinutes(5),
            TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void RecordThrottle_SevereThrottle_ReducesCeilingMore()
    {
        // Arrange - start at 10, probe to 40, then throttle with 5 min Retry-After
        var controller = CreateController(new AdaptiveRateOptions
        {
            DecreaseFactor = 0.5,
            StabilizationBatches = 1,
            MinIncreaseInterval = TimeSpan.Zero
        });

        controller.GetParallelism("Primary", recommendedParallelism: 10, connectionCount: 1);
        controller.RecordSuccess("Primary"); // 20
        controller.RecordSuccess("Primary"); // 30
        controller.RecordSuccess("Primary"); // 40

        // Act - 5 min Retry-After = 50% reduction factor (overshootRatio = 1.0)
        controller.RecordThrottle("Primary", TimeSpan.FromMinutes(5));

        // Assert
        var stats = controller.GetStatistics("Primary");
        stats!.ThrottleCeiling.Should().NotBeNull();
        // throttleCeiling = 40 * 0.5 = 20
        stats.ThrottleCeiling.Should().Be(20);
    }

    #endregion

    #region Recovery Tests

    [Fact]
    public void RecordSuccess_IncreasesParallelismAfterStabilization()
    {
        // Arrange
        var controller = CreateController(new AdaptiveRateOptions
        {
            StabilizationBatches = 3,
            MinIncreaseInterval = TimeSpan.Zero
        });

        controller.GetParallelism("Primary", recommendedParallelism: 10, connectionCount: 1);
        var initialParallelism = controller.GetStatistics("Primary")!.CurrentParallelism;

        // Act - record enough successes
        controller.RecordSuccess("Primary");
        controller.RecordSuccess("Primary");
        controller.RecordSuccess("Primary"); // 3rd success should trigger increase

        // Assert - increment by floor (10)
        var stats = controller.GetStatistics("Primary");
        stats!.CurrentParallelism.Should().Be(initialParallelism + 10);
    }

    [Fact]
    public void RecordSuccess_RespectsThrottleCeiling()
    {
        // Arrange - start at 10, probe to 40, throttle (ceiling=30), then try to recover
        var controller = CreateController(new AdaptiveRateOptions
        {
            DecreaseFactor = 0.5,
            StabilizationBatches = 1,
            MinIncreaseInterval = TimeSpan.Zero
        });

        controller.GetParallelism("Primary", recommendedParallelism: 10, connectionCount: 1);
        controller.RecordSuccess("Primary"); // 20
        controller.RecordSuccess("Primary"); // 30
        controller.RecordSuccess("Primary"); // 40

        // Throttle with 2.5 min - creates ceiling of 30
        controller.RecordThrottle("Primary", TimeSpan.FromMinutes(2.5));
        // Current = 40 * 0.5 = 20
        controller.GetStatistics("Primary")!.CurrentParallelism.Should().Be(20);
        controller.GetStatistics("Primary")!.ThrottleCeiling.Should().Be(30);

        // Act - try to increase
        controller.RecordSuccess("Primary"); // Would be 30 (20 + 10), but clamped by throttle ceiling

        // Assert - clamped at throttle ceiling
        var stats = controller.GetStatistics("Primary");
        stats!.CurrentParallelism.Should().Be(30);

        // Further success should not increase (at throttle ceiling)
        controller.RecordSuccess("Primary");
        stats = controller.GetStatistics("Primary");
        stats!.CurrentParallelism.Should().Be(30); // Still at ceiling
    }

    [Fact]
    public void RecordSuccess_DoesNotExceedHardCeiling()
    {
        // Arrange - HardCeiling is fixed at 52, use connectionCount=1 so ceiling is 52
        var controller = CreateController(new AdaptiveRateOptions
        {
            StabilizationBatches = 1,
            MinIncreaseInterval = TimeSpan.Zero
        });

        controller.GetParallelism("Primary", recommendedParallelism: 10, connectionCount: 1);

        // Act - probe up to ceiling (10 -> 20 -> 30 -> 40 -> 50 -> should cap at 52)
        controller.RecordSuccess("Primary"); // 20
        controller.RecordSuccess("Primary"); // 30
        controller.RecordSuccess("Primary"); // 40
        controller.RecordSuccess("Primary"); // 50
        controller.RecordSuccess("Primary"); // Would be 60, but capped at 52

        // Assert
        var stats = controller.GetStatistics("Primary");
        stats!.CurrentParallelism.Should().Be(52); // Capped at hard ceiling
    }

    [Fact]
    public void RecordSuccess_ResetsSuccessCounter()
    {
        // Arrange
        var controller = CreateController(new AdaptiveRateOptions
        {
            StabilizationBatches = 3,
            MinIncreaseInterval = TimeSpan.Zero
        });

        controller.GetParallelism("Primary", recommendedParallelism: 10, connectionCount: 1);

        // Act - trigger increase
        controller.RecordSuccess("Primary");
        controller.RecordSuccess("Primary");
        controller.RecordSuccess("Primary");

        // Assert - counter should reset
        var stats = controller.GetStatistics("Primary");
        stats!.SuccessesSinceThrottle.Should().Be(0);
    }

    #endregion

    #region Statistics Tests

    [Fact]
    public void GetStatistics_ReturnsNull_ForUnknownConnection()
    {
        // Arrange
        var controller = CreateController();

        // Act
        var stats = controller.GetStatistics("Unknown");

        // Assert
        stats.Should().BeNull();
    }

    [Fact]
    public void GetStatistics_ReturnsValidStats_ForKnownConnection()
    {
        // Arrange - HardCeiling is fixed at 52
        var controller = CreateController();

        controller.GetParallelism("Primary", recommendedParallelism: 10, connectionCount: 1);

        // Act
        var stats = controller.GetStatistics("Primary");

        // Assert
        stats.Should().NotBeNull();
        stats!.ConnectionName.Should().Be("Primary");
        stats.CurrentParallelism.Should().Be(10); // Floor = recommended
        stats.FloorParallelism.Should().Be(10);
        stats.CeilingParallelism.Should().Be(52); // Hard ceiling
        stats.SuccessesSinceThrottle.Should().Be(0);
        stats.TotalThrottleEvents.Should().Be(0);
        stats.ThrottleCeiling.Should().BeNull(); // No throttle yet
        stats.ThrottleCeilingExpiry.Should().BeNull();
    }

    [Fact]
    public void Statistics_EffectiveCeiling_ReflectsActiveThrottleCeiling()
    {
        // Arrange - HardCeiling is fixed at 52
        var controller = CreateController(new AdaptiveRateOptions
        {
            DecreaseFactor = 0.5,
            StabilizationBatches = 1,
            MinIncreaseInterval = TimeSpan.Zero
        });

        controller.GetParallelism("Primary", recommendedParallelism: 10, connectionCount: 1);
        controller.RecordSuccess("Primary"); // 20
        controller.RecordSuccess("Primary"); // 30
        controller.RecordSuccess("Primary"); // 40

        // Act - throttle creates ceiling of 30
        controller.RecordThrottle("Primary", TimeSpan.FromMinutes(2.5));

        // Assert
        var stats = controller.GetStatistics("Primary");
        stats!.CeilingParallelism.Should().Be(52); // Hard ceiling unchanged
        stats.ThrottleCeiling.Should().Be(30);
        stats.EffectiveCeiling.Should().Be(30); // min(52, 30) = 30
    }

    [Fact]
    public void Statistics_IsInRecoveryPhase_IsCorrect()
    {
        // Arrange - floor = 10, probe to 40, then throttle
        var controller = CreateController(new AdaptiveRateOptions
        {
            DecreaseFactor = 0.5,
            StabilizationBatches = 1,
            MinIncreaseInterval = TimeSpan.Zero
        });

        controller.GetParallelism("Primary", recommendedParallelism: 10, connectionCount: 1);
        controller.RecordSuccess("Primary"); // 20
        controller.RecordSuccess("Primary"); // 30
        controller.RecordSuccess("Primary"); // 40

        // Assert - not in recovery initially (at probed level)
        var statsBefore = controller.GetStatistics("Primary");
        statsBefore!.IsInRecoveryPhase.Should().BeFalse();

        // Act - trigger throttle (40 * 0.5 = 20)
        controller.RecordThrottle("Primary", TimeSpan.FromSeconds(30));

        // Assert - now in recovery (current=20, lastKnownGood=38)
        var statsAfter = controller.GetStatistics("Primary");
        statsAfter!.IsInRecoveryPhase.Should().BeTrue();
        statsAfter.CurrentParallelism.Should().Be(20);
    }

    #endregion

    #region Reset Tests

    [Fact]
    public void Reset_RestoresInitialState()
    {
        // Arrange - floor = 10, probe to 30, throttle
        var controller = CreateController(new AdaptiveRateOptions
        {
            DecreaseFactor = 0.5,
            StabilizationBatches = 1,
            MinIncreaseInterval = TimeSpan.Zero
        });

        controller.GetParallelism("Primary", recommendedParallelism: 10, connectionCount: 1); // 10
        controller.RecordSuccess("Primary"); // 20
        controller.RecordSuccess("Primary"); // 30
        controller.RecordThrottle("Primary", TimeSpan.FromSeconds(30)); // 15
        var afterThrottle = controller.GetStatistics("Primary")!.CurrentParallelism;

        // Act
        controller.Reset("Primary");
        controller.GetParallelism("Primary", recommendedParallelism: 10, connectionCount: 1);

        // Assert
        var afterReset = controller.GetStatistics("Primary")!.CurrentParallelism;
        afterReset.Should().Be(10); // Back to floor
        afterThrottle.Should().Be(15); // Was reduced by throttle
    }

    [Fact]
    public void Reset_ClearsThrottleCeiling()
    {
        // Arrange
        var controller = CreateController(new AdaptiveRateOptions
        {
            DecreaseFactor = 0.5,
            StabilizationBatches = 1,
            MinIncreaseInterval = TimeSpan.Zero
        });

        controller.GetParallelism("Primary", recommendedParallelism: 10, connectionCount: 1);
        controller.RecordSuccess("Primary"); // 20
        controller.RecordThrottle("Primary", TimeSpan.FromMinutes(2.5)); // Creates throttle ceiling

        var beforeReset = controller.GetStatistics("Primary");
        beforeReset!.ThrottleCeiling.Should().NotBeNull();

        // Act
        controller.Reset("Primary");
        controller.GetParallelism("Primary", recommendedParallelism: 10, connectionCount: 1);

        // Assert
        var afterReset = controller.GetStatistics("Primary");
        afterReset!.ThrottleCeiling.Should().BeNull();
        afterReset.ThrottleCeilingExpiry.Should().BeNull();
    }

    [Fact]
    public void Reset_PreservesTotalThrottleEvents()
    {
        // Arrange
        var controller = CreateController();
        controller.GetParallelism("Primary", recommendedParallelism: 10, connectionCount: 1);
        controller.RecordThrottle("Primary", TimeSpan.FromSeconds(30));

        // Act
        controller.Reset("Primary");
        controller.GetParallelism("Primary", recommendedParallelism: 10, connectionCount: 1);

        // Assert - throttle count preserved
        var stats = controller.GetStatistics("Primary");
        stats!.TotalThrottleEvents.Should().Be(1);
    }

    #endregion

    #region Per-Connection Tests

    [Fact]
    public void Controller_MaintainsSeparateStatePerConnection()
    {
        // Arrange - floor = 10, probe both to 30, then throttle only Primary
        var controller = CreateController(new AdaptiveRateOptions
        {
            DecreaseFactor = 0.5,
            StabilizationBatches = 1,
            MinIncreaseInterval = TimeSpan.Zero
        });

        controller.GetParallelism("Primary", recommendedParallelism: 10, connectionCount: 1);
        controller.GetParallelism("Secondary", recommendedParallelism: 10, connectionCount: 1);
        controller.RecordSuccess("Primary"); // 20
        controller.RecordSuccess("Primary"); // 30
        controller.RecordSuccess("Secondary"); // 20
        controller.RecordSuccess("Secondary"); // 30

        // Act - throttle only Primary
        controller.RecordThrottle("Primary", TimeSpan.FromSeconds(30));

        // Assert - only Primary affected
        var primaryStats = controller.GetStatistics("Primary");
        var secondaryStats = controller.GetStatistics("Secondary");

        primaryStats!.CurrentParallelism.Should().Be(15); // Reduced (30 * 0.5)
        secondaryStats!.CurrentParallelism.Should().Be(30); // Unchanged
    }

    #endregion

    #region Connection Count Scaling Tests

    [Fact]
    public void GetParallelism_ScalesCeilingByConnectionCount()
    {
        // Arrange - HardCeiling is fixed at 52
        var controller = CreateController(new AdaptiveRateOptions
        {
            StabilizationBatches = 1,
            MinIncreaseInterval = TimeSpan.Zero
        });

        // Act - with 2 connections
        controller.GetParallelism("Primary", recommendedParallelism: 5, connectionCount: 2);

        // Assert - ceiling should be 52 * 2 = 104
        var stats = controller.GetStatistics("Primary");
        stats!.CeilingParallelism.Should().Be(104);
        stats.FloorParallelism.Should().Be(10); // 5 * 2
    }

    [Fact]
    public void GetParallelism_MinConnectionCountIsOne()
    {
        // Arrange
        var controller = CreateController();

        // Act - with 0 connections (edge case)
        var parallelism = controller.GetParallelism("Primary", recommendedParallelism: 10, connectionCount: 0);

        // Assert - should treat as 1 connection
        parallelism.Should().Be(10);
    }

    #endregion

    #region Options Tests

    [Fact]
    public void AdaptiveRateOptions_HasCorrectDefaults()
    {
        // Arrange & Act
        var options = new AdaptiveRateOptions();

        // Assert - public options with Balanced preset defaults
        options.Enabled.Should().BeTrue();
        options.ExecutionTimeCeilingEnabled.Should().BeTrue();
        options.MaxRetryAfterTolerance.Should().BeNull();
        options.Preset.Should().Be(RateControlPreset.Balanced);

        // Preset-affected options (Balanced defaults)
        options.ExecutionTimeCeilingFactor.Should().Be(200);
        options.SlowBatchThresholdMs.Should().Be(8_000);
        options.DecreaseFactor.Should().Be(0.5);
        options.StabilizationBatches.Should().Be(3);
        options.MinIncreaseInterval.Should().Be(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void AdaptiveRateOptions_ConservativePreset_AppliesCorrectDefaults()
    {
        // Arrange & Act
        var options = new AdaptiveRateOptions { Preset = RateControlPreset.Conservative };

        // Assert - Conservative uses lower factor (140) and threshold (6000) for headroom
        options.ExecutionTimeCeilingFactor.Should().Be(140);
        options.SlowBatchThresholdMs.Should().Be(6_000);
        options.DecreaseFactor.Should().Be(0.4);
        options.StabilizationBatches.Should().Be(5);
        options.MinIncreaseInterval.Should().Be(TimeSpan.FromSeconds(8));
    }

    [Fact]
    public void AdaptiveRateOptions_AggressivePreset_AppliesCorrectDefaults()
    {
        // Arrange & Act
        var options = new AdaptiveRateOptions { Preset = RateControlPreset.Aggressive };

        // Assert
        options.ExecutionTimeCeilingFactor.Should().Be(320);
        options.SlowBatchThresholdMs.Should().Be(11_000);
        options.DecreaseFactor.Should().Be(0.6);
        options.StabilizationBatches.Should().Be(2);
        options.MinIncreaseInterval.Should().Be(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public void AdaptiveRateOptions_ExplicitValue_OverridesPreset()
    {
        // Arrange & Act
        var options = new AdaptiveRateOptions
        {
            Preset = RateControlPreset.Conservative,
            ExecutionTimeCeilingFactor = 200 // Override preset's 140
        };

        // Assert - explicit value used, other preset values unchanged
        options.ExecutionTimeCeilingFactor.Should().Be(200); // Overridden
        options.SlowBatchThresholdMs.Should().Be(6_000); // From Conservative
        options.DecreaseFactor.Should().Be(0.4); // From Conservative
    }

    #endregion
}
