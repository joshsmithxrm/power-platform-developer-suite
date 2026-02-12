using FluentAssertions;
using PPDS.Dataverse.BulkOperations;
using Xunit;

namespace PPDS.Dataverse.Tests.BulkOperations;

[Trait("Category", "Unit")]
public class AdaptiveThrottleManagerTests
{
    #region Constructor Tests

    [Fact]
    public void Constructor_DefaultParameters_SetsActiveDopToMax()
    {
        var manager = new AdaptiveThrottleManager(maxDop: 8);

        manager.ActiveDop.Should().Be(8);
        manager.MaxDop.Should().Be(8);
    }

    [Fact]
    public void Constructor_CustomInitialDop_SetsActiveDop()
    {
        var manager = new AdaptiveThrottleManager(maxDop: 8, initialDop: 4);

        manager.ActiveDop.Should().Be(4);
        manager.MaxDop.Should().Be(8);
    }

    [Fact]
    public void Constructor_InitialDopExceedsMax_ClampsToMax()
    {
        var manager = new AdaptiveThrottleManager(maxDop: 4, initialDop: 10);

        manager.ActiveDop.Should().Be(4);
    }

    [Fact]
    public void Constructor_ZeroInitialDop_DefaultsToMax()
    {
        var manager = new AdaptiveThrottleManager(maxDop: 6, initialDop: 0);

        manager.ActiveDop.Should().Be(6);
    }

    [Fact]
    public void Constructor_NegativeInitialDop_DefaultsToMax()
    {
        var manager = new AdaptiveThrottleManager(maxDop: 6, initialDop: -1);

        manager.ActiveDop.Should().Be(6);
    }

    [Fact]
    public void Constructor_MaxDopLessThanOne_ThrowsArgumentOutOfRangeException()
    {
        Action act = () => new AdaptiveThrottleManager(maxDop: 0);

        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("maxDop");
    }

    [Fact]
    public void Constructor_DefaultSuccessThreshold_IsFive()
    {
        var manager = new AdaptiveThrottleManager(maxDop: 4);

        manager.SuccessThreshold.Should().Be(5);
    }

    [Fact]
    public void Constructor_DefaultCooldownPeriod_Is30Seconds()
    {
        var manager = new AdaptiveThrottleManager(maxDop: 4);

        manager.CooldownPeriod.Should().Be(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void Constructor_CustomThresholdAndCooldown_AppliesValues()
    {
        var manager = new AdaptiveThrottleManager(
            maxDop: 4,
            successThreshold: 10,
            cooldownPeriod: TimeSpan.FromSeconds(60));

        manager.SuccessThreshold.Should().Be(10);
        manager.CooldownPeriod.Should().Be(TimeSpan.FromSeconds(60));
    }

    [Fact]
    public void Constructor_LastThrottleTime_IsMinValue()
    {
        var manager = new AdaptiveThrottleManager(maxDop: 4);

        manager.LastThrottleTime.Should().Be(DateTime.MinValue);
    }

    #endregion

    #region RecordThrottle Tests

    [Fact]
    public void ThrottleResponse_ReducesConcurrency()
    {
        var manager = new AdaptiveThrottleManager(maxDop: 4, initialDop: 4);

        manager.RecordThrottle();

        manager.ActiveDop.Should().Be(2, "throttle should halve DOP from 4 to 2");
    }

    [Fact]
    public void ThrottleResponse_MinimumIsOne()
    {
        var manager = new AdaptiveThrottleManager(maxDop: 1, initialDop: 1);

        manager.RecordThrottle();

        manager.ActiveDop.Should().Be(1, "DOP should never go below 1");
    }

    [Fact]
    public void ThrottleResponse_FromTwo_HalvesToOne()
    {
        var manager = new AdaptiveThrottleManager(maxDop: 4, initialDop: 2);

        manager.RecordThrottle();

        manager.ActiveDop.Should().Be(1, "throttle should halve DOP from 2 to 1");
    }

    [Fact]
    public void ThrottleResponse_FromThree_HalvesToOne()
    {
        // Integer division: 3 / 2 = 1
        var manager = new AdaptiveThrottleManager(maxDop: 4, initialDop: 3);

        manager.RecordThrottle();

        manager.ActiveDop.Should().Be(1, "throttle should halve DOP from 3 to 1 (integer division)");
    }

    [Fact]
    public void ThrottleResponse_RepeatedThrottles_ContinuesToHalve()
    {
        var manager = new AdaptiveThrottleManager(maxDop: 16, initialDop: 16);

        manager.RecordThrottle(); // 16 -> 8
        manager.ActiveDop.Should().Be(8);

        manager.RecordThrottle(); // 8 -> 4
        manager.ActiveDop.Should().Be(4);

        manager.RecordThrottle(); // 4 -> 2
        manager.ActiveDop.Should().Be(2);

        manager.RecordThrottle(); // 2 -> 1
        manager.ActiveDop.Should().Be(1);

        manager.RecordThrottle(); // 1 -> 1 (minimum)
        manager.ActiveDop.Should().Be(1);
    }

    [Fact]
    public void ThrottleResponse_ResetsConsecutiveSuccesses()
    {
        var now = DateTime.UtcNow;
        var manager = new AdaptiveThrottleManager(maxDop: 8, clock: () => now);

        // Accumulate some successes
        manager.RecordSuccess();
        manager.RecordSuccess();
        manager.ConsecutiveSuccesses.Should().Be(2);

        // Throttle should reset the counter
        manager.RecordThrottle();

        manager.ConsecutiveSuccesses.Should().Be(0);
    }

    [Fact]
    public void ThrottleResponse_RecordsTimestamp()
    {
        var now = new DateTime(2025, 6, 15, 12, 0, 0, DateTimeKind.Utc);
        var manager = new AdaptiveThrottleManager(maxDop: 4, clock: () => now);

        manager.RecordThrottle();

        manager.LastThrottleTime.Should().Be(now);
    }

    #endregion

    #region RecordSuccess Tests

    [Fact]
    public void SuccessAfterThrottle_GraduallyRestoresConcurrency()
    {
        var now = new DateTime(2025, 6, 15, 12, 0, 0, DateTimeKind.Utc);
        var currentTime = now;
        var manager = new AdaptiveThrottleManager(
            maxDop: 4,
            initialDop: 4,
            clock: () => currentTime);

        // Throttle: 4 -> 2
        manager.RecordThrottle();
        manager.ActiveDop.Should().Be(2);

        // Advance time past cooldown
        currentTime = now + TimeSpan.FromSeconds(31);

        // 5 consecutive successes -> DOP should increase to 3
        for (int i = 0; i < 5; i++)
        {
            manager.RecordSuccess();
        }

        manager.ActiveDop.Should().Be(3,
            "after 5 consecutive successes past cooldown, DOP should increase by 1");
    }

    [Fact]
    public void SuccessBeforeCooldown_DoesNotIncrease()
    {
        var now = new DateTime(2025, 6, 15, 12, 0, 0, DateTimeKind.Utc);
        var currentTime = now;
        var manager = new AdaptiveThrottleManager(
            maxDop: 4,
            initialDop: 4,
            clock: () => currentTime);

        // Throttle: 4 -> 2
        manager.RecordThrottle();
        manager.ActiveDop.Should().Be(2);

        // Only advance 10 seconds (less than 30s cooldown)
        currentTime = now + TimeSpan.FromSeconds(10);

        // 5 consecutive successes - but cooldown not met
        for (int i = 0; i < 5; i++)
        {
            manager.RecordSuccess();
        }

        manager.ActiveDop.Should().Be(2,
            "DOP should NOT increase when cooldown period hasn't elapsed");
    }

    [Fact]
    public void Success_BelowThreshold_DoesNotIncrease()
    {
        var now = new DateTime(2025, 6, 15, 12, 0, 0, DateTimeKind.Utc);
        var currentTime = now;
        var manager = new AdaptiveThrottleManager(
            maxDop: 4,
            initialDop: 4,
            clock: () => currentTime);

        // Throttle: 4 -> 2
        manager.RecordThrottle();
        currentTime = now + TimeSpan.FromSeconds(31);

        // Only 4 successes (threshold is 5)
        for (int i = 0; i < 4; i++)
        {
            manager.RecordSuccess();
        }

        manager.ActiveDop.Should().Be(2,
            "DOP should NOT increase with fewer than 5 consecutive successes");
    }

    [Fact]
    public void Success_AtMaxDop_DoesNotIncreaseFurther()
    {
        var now = new DateTime(2025, 6, 15, 12, 0, 0, DateTimeKind.Utc);
        var currentTime = now;
        var manager = new AdaptiveThrottleManager(
            maxDop: 4,
            initialDop: 4,
            clock: () => currentTime);

        // Already at max DOP, no throttle has occurred
        currentTime = now + TimeSpan.FromSeconds(31);

        for (int i = 0; i < 10; i++)
        {
            manager.RecordSuccess();
        }

        manager.ActiveDop.Should().Be(4,
            "DOP should never exceed maxDop");
    }

    [Fact]
    public void Success_FullRecoveryRequiresMultipleRampSteps()
    {
        var now = new DateTime(2025, 6, 15, 12, 0, 0, DateTimeKind.Utc);
        var currentTime = now;
        var manager = new AdaptiveThrottleManager(
            maxDop: 4,
            initialDop: 4,
            clock: () => currentTime);

        // Throttle twice: 4 -> 2 -> 1
        manager.RecordThrottle();
        currentTime += TimeSpan.FromMilliseconds(1);
        manager.RecordThrottle();
        manager.ActiveDop.Should().Be(1);

        // Ramp step 1: advance past cooldown, 5 successes -> DOP 1 -> 2
        currentTime = now + TimeSpan.FromSeconds(31);
        for (int i = 0; i < 5; i++)
        {
            manager.RecordSuccess();
        }
        manager.ActiveDop.Should().Be(2);

        // Ramp step 2: 5 more successes -> DOP 2 -> 3
        // Cooldown is from last throttle, which was ~31s ago, so still past cooldown
        for (int i = 0; i < 5; i++)
        {
            manager.RecordSuccess();
        }
        manager.ActiveDop.Should().Be(3);

        // Ramp step 3: 5 more successes -> DOP 3 -> 4 (back to max)
        for (int i = 0; i < 5; i++)
        {
            manager.RecordSuccess();
        }
        manager.ActiveDop.Should().Be(4);

        // Ramp step 4: 5 more successes -> should stay at 4 (max)
        for (int i = 0; i < 5; i++)
        {
            manager.RecordSuccess();
        }
        manager.ActiveDop.Should().Be(4, "should not exceed maxDop");
    }

    [Fact]
    public void Success_ThrottleDuringRecovery_ResetsProgress()
    {
        var now = new DateTime(2025, 6, 15, 12, 0, 0, DateTimeKind.Utc);
        var currentTime = now;
        var manager = new AdaptiveThrottleManager(
            maxDop: 8,
            initialDop: 8,
            clock: () => currentTime);

        // Throttle: 8 -> 4
        manager.RecordThrottle();
        manager.ActiveDop.Should().Be(4);

        // Advance past cooldown, accumulate 3 successes (not yet at threshold)
        currentTime = now + TimeSpan.FromSeconds(31);
        manager.RecordSuccess();
        manager.RecordSuccess();
        manager.RecordSuccess();

        // Another throttle during recovery: 4 -> 2
        currentTime += TimeSpan.FromMilliseconds(1);
        manager.RecordThrottle();
        manager.ActiveDop.Should().Be(2);
        manager.ConsecutiveSuccesses.Should().Be(0, "throttle should reset success counter");
    }

    [Fact]
    public void Success_ConsecutiveSuccessCounter_IncrementsCorrectly()
    {
        var manager = new AdaptiveThrottleManager(maxDop: 4);

        manager.ConsecutiveSuccesses.Should().Be(0);

        manager.RecordSuccess();
        manager.ConsecutiveSuccesses.Should().Be(1);

        manager.RecordSuccess();
        manager.ConsecutiveSuccesses.Should().Be(2);

        manager.RecordSuccess();
        manager.ConsecutiveSuccesses.Should().Be(3);
    }

    [Fact]
    public void Success_ResetsCounterAfterDopIncrease()
    {
        var now = new DateTime(2025, 6, 15, 12, 0, 0, DateTimeKind.Utc);
        var currentTime = now;
        var manager = new AdaptiveThrottleManager(
            maxDop: 4,
            initialDop: 4,
            clock: () => currentTime);

        // Throttle: 4 -> 2
        manager.RecordThrottle();
        currentTime = now + TimeSpan.FromSeconds(31);

        // 5 successes -> DOP increases to 3, counter resets
        for (int i = 0; i < 5; i++)
        {
            manager.RecordSuccess();
        }
        manager.ActiveDop.Should().Be(3);
        manager.ConsecutiveSuccesses.Should().Be(0,
            "consecutive success counter should reset after DOP increase");
    }

    #endregion

    #region Custom Threshold and Cooldown Tests

    [Fact]
    public void CustomSuccessThreshold_RequiresMoreSuccesses()
    {
        var now = new DateTime(2025, 6, 15, 12, 0, 0, DateTimeKind.Utc);
        var currentTime = now;
        var manager = new AdaptiveThrottleManager(
            maxDop: 4,
            initialDop: 4,
            successThreshold: 10,
            clock: () => currentTime);

        manager.RecordThrottle(); // 4 -> 2
        currentTime = now + TimeSpan.FromSeconds(31);

        // 9 successes - not enough with threshold of 10
        for (int i = 0; i < 9; i++)
        {
            manager.RecordSuccess();
        }
        manager.ActiveDop.Should().Be(2);

        // 10th success triggers increase
        manager.RecordSuccess();
        manager.ActiveDop.Should().Be(3);
    }

    [Fact]
    public void CustomCooldownPeriod_RespectsLongerCooldown()
    {
        var now = new DateTime(2025, 6, 15, 12, 0, 0, DateTimeKind.Utc);
        var currentTime = now;
        var manager = new AdaptiveThrottleManager(
            maxDop: 4,
            initialDop: 4,
            cooldownPeriod: TimeSpan.FromMinutes(2),
            clock: () => currentTime);

        manager.RecordThrottle(); // 4 -> 2

        // 60 seconds later - past default 30s but not past custom 2 minutes
        currentTime = now + TimeSpan.FromSeconds(60);
        for (int i = 0; i < 5; i++)
        {
            manager.RecordSuccess();
        }
        manager.ActiveDop.Should().Be(2, "2-minute cooldown not yet elapsed");

        // 121 seconds later - past custom 2-minute cooldown
        currentTime = now + TimeSpan.FromSeconds(121);
        // Counter is still 5 from above, so we need 5 fresh ones.
        // But the counter wasn't reset because DOP didn't increase. Let's check:
        // After the first 5 successes, the counter hits 5 but cooldown fails, so
        // counter stays at 5. Next success makes it 6, still >= threshold, re-checks cooldown.
        manager.RecordSuccess();
        manager.ActiveDop.Should().Be(3, "now past 2-minute cooldown, DOP should increase");
    }

    #endregion

    #region Edge Cases

    [Fact]
    public void MaxDopOfOne_ThrottleAndRecovery()
    {
        var now = new DateTime(2025, 6, 15, 12, 0, 0, DateTimeKind.Utc);
        var currentTime = now;
        var manager = new AdaptiveThrottleManager(
            maxDop: 1,
            clock: () => currentTime);

        // Throttle should keep DOP at 1
        manager.RecordThrottle();
        manager.ActiveDop.Should().Be(1);

        // Recovery should also stay at 1 (already at max)
        currentTime = now + TimeSpan.FromSeconds(31);
        for (int i = 0; i < 5; i++)
        {
            manager.RecordSuccess();
        }
        manager.ActiveDop.Should().Be(1);
    }

    [Fact]
    public void NoThrottleEver_SuccessesDoNotChangeDop()
    {
        var now = new DateTime(2025, 6, 15, 12, 0, 0, DateTimeKind.Utc);
        var currentTime = now;
        var manager = new AdaptiveThrottleManager(
            maxDop: 4,
            initialDop: 4,
            clock: () => currentTime);

        // Without any throttle, already at max DOP
        currentTime = now + TimeSpan.FromMinutes(5);
        for (int i = 0; i < 100; i++)
        {
            manager.RecordSuccess();
        }

        manager.ActiveDop.Should().Be(4, "already at max, no change needed");
    }

    [Fact]
    public void LargeDop_ThrottleHalvesCorrectly()
    {
        var manager = new AdaptiveThrottleManager(maxDop: 100, initialDop: 100);

        manager.RecordThrottle();
        manager.ActiveDop.Should().Be(50);

        manager.RecordThrottle();
        manager.ActiveDop.Should().Be(25);

        manager.RecordThrottle();
        manager.ActiveDop.Should().Be(12);

        manager.RecordThrottle();
        manager.ActiveDop.Should().Be(6);

        manager.RecordThrottle();
        manager.ActiveDop.Should().Be(3);

        manager.RecordThrottle();
        manager.ActiveDop.Should().Be(1);
    }

    #endregion
}
