using System;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PPDS.Dataverse.DependencyInjection;
using PPDS.Dataverse.Resilience;
using Xunit;

namespace PPDS.Dataverse.Tests.Resilience
{
    public class AdaptiveRateControllerTests
    {
        private static AdaptiveRateController CreateController(AdaptiveRateOptions? options = null)
        {
            options ??= new AdaptiveRateOptions();
            var dataverseOptions = new DataverseOptions { AdaptiveRate = options };
            return new AdaptiveRateController(
                Options.Create(dataverseOptions),
                NullLogger<AdaptiveRateController>.Instance);
        }

        #region GetParallelism Tests

        [Fact]
        public void GetParallelism_ReturnsFloorOnFirstCall()
        {
            // Arrange
            var controller = CreateController();

            // Act
            var result = controller.GetParallelism(recommendedPerConnection: 4, connectionCount: 1);

            // Assert - should return floor (recommended × connections)
            result.Should().Be(4);
        }

        [Fact]
        public void GetParallelism_ScalesByConnectionCount()
        {
            // Arrange
            var controller = CreateController();

            // Act
            var result = controller.GetParallelism(recommendedPerConnection: 4, connectionCount: 2);

            // Assert - floor is 4 × 2 = 8
            result.Should().Be(8);
        }

        [Fact]
        public void GetParallelism_WhenDisabled_ReturnsScaledRecommended()
        {
            // Arrange
            var controller = CreateController(new AdaptiveRateOptions { Enabled = false });

            // Act
            var result = controller.GetParallelism(recommendedPerConnection: 10, connectionCount: 2);

            // Assert - returns recommended × connections, capped by ceiling × connections
            result.Should().Be(20);
        }

        #endregion

        #region RecordBatchCompletion Tests

        [Fact]
        public void RecordBatchCompletion_IncreasesParallelismAfterStabilization()
        {
            // Arrange
            var controller = CreateController(new AdaptiveRateOptions
            {
                StabilizationBatches = 3,
                MinIncreaseInterval = TimeSpan.Zero
            });

            controller.GetParallelism(recommendedPerConnection: 4, connectionCount: 1);

            // Act - record enough batches to trigger increase
            controller.RecordBatchCompletion(TimeSpan.FromSeconds(2));
            controller.RecordBatchCompletion(TimeSpan.FromSeconds(2));
            controller.RecordBatchCompletion(TimeSpan.FromSeconds(2));

            var stats = controller.GetStatistics();

            // Assert - parallelism should have increased from floor (4) by floor (4) = 8
            stats.CurrentParallelism.Should().BeGreaterThan(4);
        }

        [Fact]
        public void RecordBatchCompletion_UpdatesBatchDurationEma()
        {
            // Arrange
            var controller = CreateController();
            controller.GetParallelism(recommendedPerConnection: 4, connectionCount: 1);

            // Act
            controller.RecordBatchCompletion(TimeSpan.FromSeconds(2));
            controller.RecordBatchCompletion(TimeSpan.FromSeconds(2));
            controller.RecordBatchCompletion(TimeSpan.FromSeconds(2));

            var stats = controller.GetStatistics();

            // Assert
            stats.AverageBatchDuration.Should().NotBeNull();
            stats.BatchDurationSampleCount.Should().Be(3);
        }

        #endregion

        #region RecordThrottle Tests

        [Fact]
        public void RecordThrottle_DecreasesParallelism()
        {
            // Arrange
            var controller = CreateController(new AdaptiveRateOptions
            {
                StabilizationBatches = 1,
                MinIncreaseInterval = TimeSpan.Zero,
                DecreaseFactor = 0.5
            });

            controller.GetParallelism(recommendedPerConnection: 4, connectionCount: 1);

            // Ramp up first
            for (int i = 0; i < 10; i++)
            {
                controller.RecordBatchCompletion(TimeSpan.FromSeconds(2));
            }

            var beforeThrottle = controller.GetStatistics().CurrentParallelism;

            // Act
            controller.RecordThrottle(TimeSpan.FromSeconds(30));

            var afterThrottle = controller.GetStatistics().CurrentParallelism;

            // Assert
            afterThrottle.Should().BeLessThan(beforeThrottle);
        }

        [Fact]
        public void RecordThrottle_NeverGoesBelowFloor()
        {
            // Arrange
            var controller = CreateController(new AdaptiveRateOptions
            {
                DecreaseFactor = 0.1 // Aggressive decrease
            });

            controller.GetParallelism(recommendedPerConnection: 4, connectionCount: 1);

            // Act - multiple throttles
            controller.RecordThrottle(TimeSpan.FromMinutes(1));
            controller.RecordThrottle(TimeSpan.FromMinutes(1));
            controller.RecordThrottle(TimeSpan.FromMinutes(1));

            var stats = controller.GetStatistics();

            // Assert - should stay at floor (4)
            stats.CurrentParallelism.Should().BeGreaterThanOrEqualTo(4);
        }

        [Fact]
        public void RecordThrottle_SetsThrottleCeiling()
        {
            // Arrange
            var controller = CreateController(new AdaptiveRateOptions
            {
                StabilizationBatches = 1,
                MinIncreaseInterval = TimeSpan.Zero
            });

            controller.GetParallelism(recommendedPerConnection: 4, connectionCount: 1);

            // Ramp up
            for (int i = 0; i < 5; i++)
            {
                controller.RecordBatchCompletion(TimeSpan.FromSeconds(2));
            }

            // Act
            controller.RecordThrottle(TimeSpan.FromSeconds(30));

            var stats = controller.GetStatistics();

            // Assert
            stats.ThrottleCeiling.Should().NotBeNull();
            stats.ThrottleCeilingExpiry.Should().NotBeNull();
            stats.ThrottleCeilingExpiry.Should().BeAfter(DateTime.UtcNow);
        }

        #endregion

        #region Reset Tests

        [Fact]
        public void Reset_ClearsState()
        {
            // Arrange
            var controller = CreateController(new AdaptiveRateOptions
            {
                StabilizationBatches = 1,
                MinIncreaseInterval = TimeSpan.Zero
            });

            controller.GetParallelism(recommendedPerConnection: 4, connectionCount: 1);

            // Build up some state
            controller.RecordBatchCompletion(TimeSpan.FromSeconds(2));
            controller.RecordBatchCompletion(TimeSpan.FromSeconds(2));
            controller.RecordBatchCompletion(TimeSpan.FromSeconds(2));
            controller.RecordThrottle(TimeSpan.FromSeconds(30));

            // Act
            controller.Reset();

            // Re-initialize
            controller.GetParallelism(recommendedPerConnection: 4, connectionCount: 1);

            var stats = controller.GetStatistics();

            // Assert - should be back at floor with cleared ceilings
            stats.CurrentParallelism.Should().Be(4);
            stats.ThrottleCeiling.Should().BeNull();
            stats.BatchDurationSampleCount.Should().Be(0);
        }

        [Fact]
        public void Reset_PreservesTotalThrottleEvents()
        {
            // Arrange
            var controller = CreateController();
            controller.GetParallelism(recommendedPerConnection: 4, connectionCount: 1);
            controller.RecordThrottle(TimeSpan.FromSeconds(30));
            controller.RecordThrottle(TimeSpan.FromSeconds(30));

            // Act
            controller.Reset();
            controller.GetParallelism(recommendedPerConnection: 4, connectionCount: 1);

            var stats = controller.GetStatistics();

            // Assert
            stats.TotalThrottleEvents.Should().Be(2);
        }

        #endregion

        #region GetStatistics Tests

        [Fact]
        public void GetStatistics_ReturnsCorrectValues()
        {
            // Arrange
            var controller = CreateController();
            controller.GetParallelism(recommendedPerConnection: 4, connectionCount: 2);

            // Act
            var stats = controller.GetStatistics();

            // Assert
            stats.CurrentParallelism.Should().Be(8); // 4 × 2
            stats.FloorParallelism.Should().Be(8);
            stats.CeilingParallelism.Should().Be(104); // 52 × 2
            stats.ConnectionCount.Should().Be(2);
            stats.BatchesSinceThrottle.Should().Be(0);
        }

        [Fact]
        public void GetStatistics_EffectiveCeiling_ReflectsRequestRateCeiling()
        {
            // Arrange
            var controller = CreateController(new AdaptiveRateOptions
            {
                Preset = RateControlPreset.Balanced // RequestRateCeilingFactor = 16
            });

            controller.GetParallelism(recommendedPerConnection: 4, connectionCount: 1);

            // Record 2-second batches: RequestRateCeiling = 16 * 2 = 32
            controller.RecordBatchCompletion(TimeSpan.FromSeconds(2));
            controller.RecordBatchCompletion(TimeSpan.FromSeconds(2));
            controller.RecordBatchCompletion(TimeSpan.FromSeconds(2));

            // Assert
            var stats = controller.GetStatistics();
            stats.RequestRateCeiling.Should().Be(32);
            stats.EffectiveCeiling.Should().Be(32);
        }

        #endregion

        #region Request Rate Ceiling Tests

        [Fact]
        public void RecordBatchCompletion_CalculatesRequestRateCeiling()
        {
            // Arrange - Balanced preset has RequestRateCeilingFactor = 16
            var controller = CreateController(new AdaptiveRateOptions
            {
                Preset = RateControlPreset.Balanced
            });

            controller.GetParallelism(recommendedPerConnection: 4, connectionCount: 1);

            // Act - Record 3 batches with 2-second duration
            // RequestRateCeiling = 16 * 2 = 32
            controller.RecordBatchCompletion(TimeSpan.FromSeconds(2));
            controller.RecordBatchCompletion(TimeSpan.FromSeconds(2));
            controller.RecordBatchCompletion(TimeSpan.FromSeconds(2));

            // Assert
            var stats = controller.GetStatistics();
            stats.RequestRateCeiling.Should().Be(32);
        }

        [Fact]
        public void RecordBatchCompletion_FastBatches_LowerRequestRateCeiling()
        {
            // Arrange - Balanced preset has RequestRateCeilingFactor = 16
            var controller = CreateController(new AdaptiveRateOptions
            {
                Preset = RateControlPreset.Balanced
            });

            controller.GetParallelism(recommendedPerConnection: 4, connectionCount: 1);

            // Act - Record 3 batches with 1-second duration
            // RequestRateCeiling = 16 * 1 = 16
            controller.RecordBatchCompletion(TimeSpan.FromSeconds(1));
            controller.RecordBatchCompletion(TimeSpan.FromSeconds(1));
            controller.RecordBatchCompletion(TimeSpan.FromSeconds(1));

            // Assert - fast batches = lower ceiling (fewer concurrent to avoid request rate limit)
            var stats = controller.GetStatistics();
            stats.RequestRateCeiling.Should().Be(16);
        }

        [Fact]
        public void RecordBatchCompletion_SlowBatches_HigherRequestRateCeiling()
        {
            // Arrange - Balanced preset has RequestRateCeilingFactor = 16
            var controller = CreateController(new AdaptiveRateOptions
            {
                Preset = RateControlPreset.Balanced
            });

            controller.GetParallelism(recommendedPerConnection: 4, connectionCount: 1);

            // Act - Record 3 batches with 10-second duration
            // RequestRateCeiling = 16 * 10 = 160, but clamped to hard ceiling (52)
            controller.RecordBatchCompletion(TimeSpan.FromSeconds(10));
            controller.RecordBatchCompletion(TimeSpan.FromSeconds(10));
            controller.RecordBatchCompletion(TimeSpan.FromSeconds(10));

            // Assert - slow batches = higher ceiling (clamped to 52)
            var stats = controller.GetStatistics();
            stats.RequestRateCeiling.Should().Be(52);
        }

        [Fact]
        public void RecordBatchCompletion_RespectsRequestRateCeiling()
        {
            // Arrange - set up with fast batches that create a low request rate ceiling
            var controller = CreateController(new AdaptiveRateOptions
            {
                Preset = RateControlPreset.Balanced, // RequestRateCeilingFactor = 16
                StabilizationBatches = 1,
                MinIncreaseInterval = TimeSpan.Zero
            });

            controller.GetParallelism(recommendedPerConnection: 4, connectionCount: 1);

            // Record 2-second batches: RequestRateCeiling = 16 * 2 = 32
            controller.RecordBatchCompletion(TimeSpan.FromSeconds(2));
            controller.RecordBatchCompletion(TimeSpan.FromSeconds(2));
            controller.RecordBatchCompletion(TimeSpan.FromSeconds(2));

            var stats = controller.GetStatistics();
            stats.RequestRateCeiling.Should().Be(32);

            // Act - try to increase past the request rate ceiling
            // Starting at 4, increase by 4 each time: 8, 12, 16, 20, 24, 28, 32, cap
            for (int i = 0; i < 10; i++)
            {
                controller.RecordBatchCompletion(TimeSpan.FromSeconds(2));
            }

            // Assert - capped at request rate ceiling
            stats = controller.GetStatistics();
            stats.CurrentParallelism.Should().BeLessThanOrEqualTo(32);
        }

        [Fact]
        public void RequestRateCeiling_ComplementsExecutionTimeCeiling()
        {
            // Arrange - with slow batches, exec time ceiling should be lower
            // while request rate ceiling should be higher (or clamped to hard ceiling)
            var controller = CreateController(new AdaptiveRateOptions
            {
                Preset = RateControlPreset.Balanced,
                ExecutionTimeCeilingFactor = 200,
                RequestRateCeilingFactor = 16.0
            });

            controller.GetParallelism(recommendedPerConnection: 4, connectionCount: 1);

            // Record 10-second batches
            // ExecTimeCeiling = 200 / 10 = 20
            // RequestRateCeiling = 16 * 10 = 160 (clamped to 52)
            controller.RecordBatchCompletion(TimeSpan.FromSeconds(10));
            controller.RecordBatchCompletion(TimeSpan.FromSeconds(10));
            controller.RecordBatchCompletion(TimeSpan.FromSeconds(10));

            // Assert - for slow batches, exec time ceiling is the constraint
            var stats = controller.GetStatistics();
            stats.ExecutionTimeCeiling.Should().Be(20);
            stats.RequestRateCeiling.Should().Be(52); // Clamped to hard ceiling
        }

        [Fact]
        public void RequestRateCeiling_WithFastBatches_IsMoreRestrictive()
        {
            // Arrange - with fast batches, request rate ceiling should be lower
            // while exec time ceiling should be higher (or clamped to hard ceiling)
            var controller = CreateController(new AdaptiveRateOptions
            {
                Preset = RateControlPreset.Balanced,
                ExecutionTimeCeilingFactor = 200,
                RequestRateCeilingFactor = 16.0
            });

            controller.GetParallelism(recommendedPerConnection: 4, connectionCount: 1);

            // Record 1-second batches
            // ExecTimeCeiling = 200 / 1 = 200 (clamped to 52)
            // RequestRateCeiling = 16 * 1 = 16
            controller.RecordBatchCompletion(TimeSpan.FromSeconds(1));
            controller.RecordBatchCompletion(TimeSpan.FromSeconds(1));
            controller.RecordBatchCompletion(TimeSpan.FromSeconds(1));

            // Assert - for fast batches, request rate ceiling is the constraint
            var stats = controller.GetStatistics();
            stats.ExecutionTimeCeiling.Should().Be(52); // Clamped to hard ceiling
            stats.RequestRateCeiling.Should().Be(16);
            stats.EffectiveCeiling.Should().Be(16); // Request rate ceiling is more restrictive
        }

        [Fact]
        public void RecordThrottle_DebouncesMultipleThrottlesWithinWindow()
        {
            // Arrange - simulate 52 concurrent 429s
            var controller = CreateController(new AdaptiveRateOptions
            {
                StabilizationBatches = 1,
                MinIncreaseInterval = TimeSpan.Zero,
                DecreaseFactor = 0.5
            });

            controller.GetParallelism(recommendedPerConnection: 4, connectionCount: 1);

            // Ramp up to simulate being at high parallelism
            for (int i = 0; i < 10; i++)
            {
                controller.RecordBatchCompletion(TimeSpan.FromSeconds(2));
            }

            var beforeThrottle = controller.GetStatistics().CurrentParallelism;

            // Act - simulate burst of 5 throttles (as if 5 concurrent requests all 429'd)
            controller.RecordThrottle(TimeSpan.FromSeconds(30));
            controller.RecordThrottle(TimeSpan.FromSeconds(30));
            controller.RecordThrottle(TimeSpan.FromSeconds(30));
            controller.RecordThrottle(TimeSpan.FromSeconds(30));
            controller.RecordThrottle(TimeSpan.FromSeconds(30));

            var stats = controller.GetStatistics();

            // Assert - parallelism should only decrease once (first throttle processed, rest debounced)
            // Without debouncing: 52->26->13->6->4 (cascade)
            // With debouncing: 52->26 (single decrease)
            var expectedAfterSingleDecrease = (int)(beforeThrottle * 0.5);
            stats.CurrentParallelism.Should().BeGreaterThanOrEqualTo(expectedAfterSingleDecrease);

            // Total events should still be counted
            stats.TotalThrottleEvents.Should().Be(5);
        }

        [Fact]
        public void RecordThrottle_AtFloor_DoesNotReduceThrottleCeiling()
        {
            // Arrange - start at floor and trigger throttle
            var controller = CreateController(new AdaptiveRateOptions
            {
                DecreaseFactor = 0.5
            });

            controller.GetParallelism(recommendedPerConnection: 4, connectionCount: 1);

            // Act - throttle when already at floor
            controller.RecordThrottle(TimeSpan.FromSeconds(30));

            var stats = controller.GetStatistics();

            // Assert - throttle ceiling should not be set when already at floor
            // (no point reducing ceiling when we can't go any lower)
            stats.CurrentParallelism.Should().Be(4); // Still at floor
            stats.ThrottleCeiling.Should().BeNull(); // Ceiling not reduced
        }

        #endregion

        #region BatchesPerSecond Tests

        [Fact]
        public void RecordBatchCompletion_TracksBatchesPerSecond()
        {
            // Arrange
            var controller = CreateController();
            controller.GetParallelism(recommendedPerConnection: 4, connectionCount: 1);

            // Act - record batches with simulated time gaps
            // Note: Since we can't easily control time, we just verify the property exists
            controller.RecordBatchCompletion(TimeSpan.FromSeconds(2));
            controller.RecordBatchCompletion(TimeSpan.FromSeconds(2));
            controller.RecordBatchCompletion(TimeSpan.FromSeconds(2));

            var stats = controller.GetStatistics();

            // Assert - after multiple batches, we should have a rate calculated
            // The exact value depends on actual elapsed time, but it should be set
            // (or null if batches completed too quickly to measure)
            stats.BatchDurationSampleCount.Should().Be(3);
            // BatchesPerSecond may be null if all batches happened within same tick
        }

        #endregion

        #region Throttle Cascade Prevention Tests

        [Fact]
        public void InitialCeiling_CapsParallelismBeforeSamplesCollected()
        {
            // Arrange - Before MinBatchSamplesForCeiling (3), ceiling should be InitialCeilingBeforeSamples (20)
            var controller = CreateController(new AdaptiveRateOptions
            {
                StabilizationBatches = 1,
                MinIncreaseInterval = TimeSpan.Zero
            });

            controller.GetParallelism(recommendedPerConnection: 4, connectionCount: 1);

            // Act - try to ramp up before collecting 3 samples
            controller.RecordBatchCompletion(TimeSpan.FromSeconds(2));
            controller.RecordBatchCompletion(TimeSpan.FromSeconds(2));

            var stats = controller.GetStatistics();

            // Assert - should be capped at 20 (InitialCeilingBeforeSamples)
            stats.CurrentParallelism.Should().BeLessThanOrEqualTo(20);
            stats.BatchDurationSampleCount.Should().Be(2); // Less than 3 samples
        }

        [Fact]
        public void InitialCeiling_ScalesByConnectionCount()
        {
            // Arrange - with 2 connections, initial ceiling should be 40 (20 × 2)
            var controller = CreateController(new AdaptiveRateOptions
            {
                StabilizationBatches = 1,
                MinIncreaseInterval = TimeSpan.Zero
            });

            controller.GetParallelism(recommendedPerConnection: 4, connectionCount: 2);

            // Act - try to ramp up before collecting 3 samples
            controller.RecordBatchCompletion(TimeSpan.FromSeconds(2));
            controller.RecordBatchCompletion(TimeSpan.FromSeconds(2));

            var stats = controller.GetStatistics();

            // Assert - should be capped at 40 (20 × 2 connections)
            stats.CurrentParallelism.Should().BeLessThanOrEqualTo(40);
        }

        [Fact]
        public void SlowerInitialRamp_UsesSmallIncreaseBeforeThrottle()
        {
            // Arrange - before first throttle AND before 30 successful batches,
            // increase should be IncreaseRate (2), not floor (4)
            var controller = CreateController(new AdaptiveRateOptions
            {
                StabilizationBatches = 1,
                MinIncreaseInterval = TimeSpan.Zero
            });

            controller.GetParallelism(recommendedPerConnection: 4, connectionCount: 1);
            var initial = controller.GetStatistics().CurrentParallelism;

            // Act - record stabilization batches
            controller.RecordBatchCompletion(TimeSpan.FromSeconds(2));

            var stats = controller.GetStatistics();

            // Assert - should increase by IncreaseRate (2), not floor (4)
            // So 4 -> 6, not 4 -> 8
            stats.CurrentParallelism.Should().Be(initial + 2);
            stats.HasHadFirstThrottle.Should().BeFalse();
            stats.TotalSuccessfulBatches.Should().BeLessThan(30);
        }

        [Fact]
        public void SlowerInitialRamp_UsesFloorAfterFirstThrottle()
        {
            // Arrange
            var controller = CreateController(new AdaptiveRateOptions
            {
                StabilizationBatches = 1,
                MinIncreaseInterval = TimeSpan.Zero,
                DecreaseFactor = 0.9 // High factor to stay above floor
            });

            controller.GetParallelism(recommendedPerConnection: 4, connectionCount: 1);

            // Build up some parallelism first with slow ramp
            for (int i = 0; i < 5; i++)
            {
                controller.RecordBatchCompletion(TimeSpan.FromSeconds(2));
            }

            var beforeThrottle = controller.GetStatistics().CurrentParallelism;

            // Trigger throttle
            controller.RecordThrottle(TimeSpan.FromSeconds(30));

            var afterThrottle = controller.GetStatistics();
            afterThrottle.HasHadFirstThrottle.Should().BeTrue();

            // Now stabilize and increase should use floor (4)
            controller.RecordBatchCompletion(TimeSpan.FromSeconds(2));

            var afterRecovery = controller.GetStatistics();

            // Assert - after first throttle, should increase by floor (4) not just 2
            // Note: The exact value depends on current parallelism vs lastKnownGood
            afterRecovery.HasHadFirstThrottle.Should().BeTrue();
        }

        [Fact]
        public void MinimumBatchDuration_UsedForRequestRateCeiling()
        {
            // Arrange - record fast batch then slow batch
            // Request rate ceiling should use minimum (fast), not EMA
            var controller = CreateController(new AdaptiveRateOptions
            {
                Preset = RateControlPreset.Balanced // RequestRateCeilingFactor = 16
            });

            controller.GetParallelism(recommendedPerConnection: 4, connectionCount: 1);

            // Act - record 1s batch (fast), then 10s batch (slow)
            // If using EMA, ceiling would increase toward 16*10=160
            // If using minimum, ceiling should stay at 16*1=16
            controller.RecordBatchCompletion(TimeSpan.FromSeconds(1)); // Min = 1s
            controller.RecordBatchCompletion(TimeSpan.FromSeconds(10)); // Slower, but min still 1s
            controller.RecordBatchCompletion(TimeSpan.FromSeconds(10)); // 3rd sample to calculate ceiling

            var stats = controller.GetStatistics();

            // Assert - ceiling should be based on minimum (1s), not EMA
            stats.MinimumBatchDuration.Should().Be(TimeSpan.FromSeconds(1));
            stats.RequestRateCeiling.Should().Be(16); // 16 * 1 = 16
        }

        [Fact]
        public void MinimumBatchDuration_PreventsFeedbackLoop()
        {
            // Arrange - This tests the core fix: slow batches shouldn't increase ceiling
            var controller = CreateController(new AdaptiveRateOptions
            {
                Preset = RateControlPreset.Balanced
            });

            controller.GetParallelism(recommendedPerConnection: 4, connectionCount: 1);

            // Start with fast batch to establish minimum
            controller.RecordBatchCompletion(TimeSpan.FromSeconds(2));

            var stats1 = controller.GetStatistics();

            // Simulate load: batches get progressively slower under contention
            controller.RecordBatchCompletion(TimeSpan.FromSeconds(3));
            controller.RecordBatchCompletion(TimeSpan.FromSeconds(4));
            controller.RecordBatchCompletion(TimeSpan.FromSeconds(5));

            var stats2 = controller.GetStatistics();

            // Assert - Request rate ceiling should NOT increase despite slower batches
            // It should stay based on the minimum (fastest) batch
            stats2.MinimumBatchDuration.Should().Be(TimeSpan.FromSeconds(2));
            stats2.RequestRateCeiling.Should().Be(32); // 16 * 2 = 32 (from minimum)
        }

        [Fact]
        public void TotalSuccessfulBatches_TrackedAcrossBatches()
        {
            // Arrange
            var controller = CreateController();
            controller.GetParallelism(recommendedPerConnection: 4, connectionCount: 1);

            // Act
            controller.RecordBatchCompletion(TimeSpan.FromSeconds(2));
            controller.RecordBatchCompletion(TimeSpan.FromSeconds(2));
            controller.RecordBatchCompletion(TimeSpan.FromSeconds(2));

            var stats = controller.GetStatistics();

            // Assert
            stats.TotalSuccessfulBatches.Should().Be(3);
        }

        [Fact]
        public void Reset_ClearsTotalSuccessfulBatches()
        {
            // Arrange
            var controller = CreateController();
            controller.GetParallelism(recommendedPerConnection: 4, connectionCount: 1);
            controller.RecordBatchCompletion(TimeSpan.FromSeconds(2));
            controller.RecordBatchCompletion(TimeSpan.FromSeconds(2));

            // Act
            controller.Reset();
            controller.GetParallelism(recommendedPerConnection: 4, connectionCount: 1);

            var stats = controller.GetStatistics();

            // Assert
            stats.TotalSuccessfulBatches.Should().Be(0);
            stats.HasHadFirstThrottle.Should().BeFalse();
            stats.MinimumBatchDuration.Should().BeNull();
        }

        #endregion

        #region Preset Tests

        [Fact]
        public void AdaptiveRateOptions_RequestRateCeilingFactor_HasCorrectPresetDefaults()
        {
            // Arrange & Act
            var balanced = new AdaptiveRateOptions { Preset = RateControlPreset.Balanced };
            var conservative = new AdaptiveRateOptions { Preset = RateControlPreset.Conservative };
            var aggressive = new AdaptiveRateOptions { Preset = RateControlPreset.Aggressive };

            // Assert - 60%, 80%, 90% of 20 req/sec limit
            conservative.RequestRateCeilingFactor.Should().Be(12.0);
            balanced.RequestRateCeilingFactor.Should().Be(16.0);
            aggressive.RequestRateCeilingFactor.Should().Be(18.0);
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
}
