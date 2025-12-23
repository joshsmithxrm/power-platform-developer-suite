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
    public void GetParallelism_InitialValue_UsesInitialParallelismFactor()
    {
        // Arrange
        var controller = CreateController(new AdaptiveRateOptions
        {
            InitialParallelismFactor = 0.5
        });

        // Act
        var parallelism = controller.GetParallelism("Primary", maxParallelism: 52);

        // Assert
        parallelism.Should().Be(26); // 52 * 0.5 = 26
    }

    [Fact]
    public void GetParallelism_InitialValue_RespectsMinParallelism()
    {
        // Arrange
        var controller = CreateController(new AdaptiveRateOptions
        {
            InitialParallelismFactor = 0.1,
            MinParallelism = 5
        });

        // Act
        var parallelism = controller.GetParallelism("Primary", maxParallelism: 10);

        // Assert - 10 * 0.1 = 1, but min is 5
        parallelism.Should().Be(5);
    }

    [Fact]
    public void GetParallelism_WhenDisabled_ReturnsMaxParallelism()
    {
        // Arrange
        var controller = CreateController(new AdaptiveRateOptions
        {
            Enabled = false,
            InitialParallelismFactor = 0.5
        });

        // Act
        var parallelism = controller.GetParallelism("Primary", maxParallelism: 52);

        // Assert - should ignore initial factor when disabled
        parallelism.Should().Be(52);
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
        // Arrange
        var controller = CreateController(new AdaptiveRateOptions
        {
            InitialParallelismFactor = 1.0, // Start at max
            DecreaseFactor = 0.5,
            MinParallelism = 1
        });

        controller.GetParallelism("Primary", maxParallelism: 52);

        // Act
        controller.RecordThrottle("Primary", TimeSpan.FromSeconds(30));

        // Assert
        var parallelism = controller.GetParallelism("Primary", maxParallelism: 52);
        parallelism.Should().Be(26); // 52 * 0.5 = 26
    }

    [Fact]
    public void RecordThrottle_RespectsMinParallelism()
    {
        // Arrange
        var controller = CreateController(new AdaptiveRateOptions
        {
            InitialParallelismFactor = 0.2, // Start low
            DecreaseFactor = 0.5,
            MinParallelism = 5
        });

        controller.GetParallelism("Primary", maxParallelism: 20);

        // Act - multiple throttles
        controller.RecordThrottle("Primary", TimeSpan.FromSeconds(30));
        controller.RecordThrottle("Primary", TimeSpan.FromSeconds(30));
        controller.RecordThrottle("Primary", TimeSpan.FromSeconds(30));

        // Assert - should not go below min
        var parallelism = controller.GetParallelism("Primary", maxParallelism: 20);
        parallelism.Should().BeGreaterThanOrEqualTo(5);
    }

    [Fact]
    public void RecordThrottle_UpdatesStatistics()
    {
        // Arrange
        var controller = CreateController();
        controller.GetParallelism("Primary", maxParallelism: 52);

        // Act
        controller.RecordThrottle("Primary", TimeSpan.FromSeconds(30));

        // Assert
        var stats = controller.GetStatistics("Primary");
        stats.Should().NotBeNull();
        stats!.TotalThrottleEvents.Should().Be(1);
        stats.LastThrottleTime.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(1));
    }

    #endregion

    #region Recovery Tests

    [Fact]
    public void RecordSuccess_IncreasesParallelismAfterStabilization()
    {
        // Arrange
        var controller = CreateController(new AdaptiveRateOptions
        {
            InitialParallelismFactor = 0.5,
            StabilizationBatches = 3,
            IncreaseRate = 2,
            MinIncreaseInterval = TimeSpan.Zero // Disable time gating for test
        });

        controller.GetParallelism("Primary", maxParallelism: 52);
        var initialParallelism = controller.GetStatistics("Primary")!.CurrentParallelism;

        // Act - record enough successes
        controller.RecordSuccess("Primary");
        controller.RecordSuccess("Primary");
        controller.RecordSuccess("Primary"); // 3rd success should trigger increase

        // Assert
        var stats = controller.GetStatistics("Primary");
        stats!.CurrentParallelism.Should().Be(initialParallelism + 2);
    }

    [Fact]
    public void RecordSuccess_DoesNotExceedMaxParallelism()
    {
        // Arrange
        var controller = CreateController(new AdaptiveRateOptions
        {
            InitialParallelismFactor = 1.0, // Start at max
            StabilizationBatches = 1,
            IncreaseRate = 10,
            MinIncreaseInterval = TimeSpan.Zero
        });

        controller.GetParallelism("Primary", maxParallelism: 52);

        // Act - try to increase beyond max
        controller.RecordSuccess("Primary");

        // Assert
        var stats = controller.GetStatistics("Primary");
        stats!.CurrentParallelism.Should().Be(52); // Capped at max
    }

    [Fact]
    public void RecordSuccess_ResetsSuccessCounter()
    {
        // Arrange
        var controller = CreateController(new AdaptiveRateOptions
        {
            InitialParallelismFactor = 0.5,
            StabilizationBatches = 3,
            IncreaseRate = 2,
            MinIncreaseInterval = TimeSpan.Zero
        });

        controller.GetParallelism("Primary", maxParallelism: 52);

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
        // Arrange
        var controller = CreateController(new AdaptiveRateOptions
        {
            InitialParallelismFactor = 0.5
        });

        controller.GetParallelism("Primary", maxParallelism: 52);

        // Act
        var stats = controller.GetStatistics("Primary");

        // Assert
        stats.Should().NotBeNull();
        stats!.ConnectionName.Should().Be("Primary");
        stats.CurrentParallelism.Should().Be(26);
        stats.MaxParallelism.Should().Be(52);
        stats.SuccessesSinceThrottle.Should().Be(0);
        stats.TotalThrottleEvents.Should().Be(0);
    }

    [Fact]
    public void Statistics_IsInRecoveryPhase_IsCorrect()
    {
        // Arrange
        var controller = CreateController(new AdaptiveRateOptions
        {
            InitialParallelismFactor = 1.0,
            DecreaseFactor = 0.5
        });

        controller.GetParallelism("Primary", maxParallelism: 52);

        // Assert - not in recovery initially
        var statsBefore = controller.GetStatistics("Primary");
        statsBefore!.IsInRecoveryPhase.Should().BeFalse();

        // Act - trigger throttle
        controller.RecordThrottle("Primary", TimeSpan.FromSeconds(30));

        // Assert - now in recovery
        var statsAfter = controller.GetStatistics("Primary");
        statsAfter!.IsInRecoveryPhase.Should().BeTrue();
    }

    #endregion

    #region Reset Tests

    [Fact]
    public void Reset_RestoresInitialState()
    {
        // Arrange
        var controller = CreateController(new AdaptiveRateOptions
        {
            InitialParallelismFactor = 0.5
        });

        controller.GetParallelism("Primary", maxParallelism: 52);
        controller.RecordThrottle("Primary", TimeSpan.FromSeconds(30));
        var afterThrottle = controller.GetStatistics("Primary")!.CurrentParallelism;

        // Act
        controller.Reset("Primary");
        controller.GetParallelism("Primary", maxParallelism: 52);

        // Assert
        var afterReset = controller.GetStatistics("Primary")!.CurrentParallelism;
        afterReset.Should().Be(26); // Back to initial
        afterThrottle.Should().BeLessThan(26); // Was reduced by throttle
    }

    [Fact]
    public void Reset_PreservesTotalThrottleEvents()
    {
        // Arrange
        var controller = CreateController();
        controller.GetParallelism("Primary", maxParallelism: 52);
        controller.RecordThrottle("Primary", TimeSpan.FromSeconds(30));

        // Act
        controller.Reset("Primary");
        controller.GetParallelism("Primary", maxParallelism: 52);

        // Assert - throttle count preserved
        var stats = controller.GetStatistics("Primary");
        stats!.TotalThrottleEvents.Should().Be(1);
    }

    #endregion

    #region Per-Connection Tests

    [Fact]
    public void Controller_MaintainsSeparateStatePerConnection()
    {
        // Arrange
        var controller = CreateController(new AdaptiveRateOptions
        {
            InitialParallelismFactor = 1.0,
            DecreaseFactor = 0.5
        });

        controller.GetParallelism("Primary", maxParallelism: 52);
        controller.GetParallelism("Secondary", maxParallelism: 52);

        // Act - throttle only Primary
        controller.RecordThrottle("Primary", TimeSpan.FromSeconds(30));

        // Assert - only Primary affected
        var primaryStats = controller.GetStatistics("Primary");
        var secondaryStats = controller.GetStatistics("Secondary");

        primaryStats!.CurrentParallelism.Should().Be(26); // Reduced
        secondaryStats!.CurrentParallelism.Should().Be(52); // Unchanged
    }

    #endregion

    #region Options Tests

    [Fact]
    public void AdaptiveRateOptions_HasCorrectDefaults()
    {
        // Arrange & Act
        var options = new AdaptiveRateOptions();

        // Assert
        options.Enabled.Should().BeTrue();
        options.InitialParallelismFactor.Should().Be(0.5);
        options.MinParallelism.Should().Be(1);
        options.IncreaseRate.Should().Be(2);
        options.DecreaseFactor.Should().Be(0.5);
        options.StabilizationBatches.Should().Be(3);
        options.MinIncreaseInterval.Should().Be(TimeSpan.FromSeconds(5));
        options.RecoveryMultiplier.Should().Be(2.0);
        options.LastKnownGoodTTL.Should().Be(TimeSpan.FromMinutes(5));
        options.IdleResetPeriod.Should().Be(TimeSpan.FromMinutes(5));
    }

    #endregion
}
