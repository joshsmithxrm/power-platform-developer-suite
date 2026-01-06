using FluentAssertions;
using PPDS.Dataverse.Configuration;
using PPDS.Dataverse.Pooling;
using Xunit;

namespace PPDS.Dataverse.Tests.Pooling;

/// <summary>
/// Tests for DOP-based pool sizing (MaxPoolSize and MicrosoftHardLimitPerUser).
/// </summary>
public class PoolSizingTests
{
    #region Default Values Tests

    [Fact]
    public void ConnectionPoolOptions_DefaultMaxPoolSize_IsZero()
    {
        // Arrange & Act
        var options = new ConnectionPoolOptions();

        // Assert - 0 means use DOP-based sizing from server
        options.MaxPoolSize.Should().Be(0);
    }

    [Fact]
    public void ConnectionPoolOptions_DefaultEnabled_IsTrue()
    {
        // Arrange & Act
        var options = new ConnectionPoolOptions();

        // Assert
        options.Enabled.Should().BeTrue();
    }

    [Fact]
    public void ConnectionPoolOptions_DefaultAcquireTimeout_Is120Seconds()
    {
        // Arrange & Act
        var options = new ConnectionPoolOptions();

        // Assert - 120s allows for large imports with many batches queuing (ADR-0019)
        options.AcquireTimeout.Should().Be(TimeSpan.FromSeconds(120));
    }

    [Fact]
    public void ConnectionPoolOptions_DefaultMaxIdleTime_Is5Minutes()
    {
        // Arrange & Act
        var options = new ConnectionPoolOptions();

        // Assert
        options.MaxIdleTime.Should().Be(TimeSpan.FromMinutes(5));
    }

    [Fact]
    public void ConnectionPoolOptions_DefaultMaxLifetime_Is60Minutes()
    {
        // Arrange & Act
        var options = new ConnectionPoolOptions();

        // Assert
        options.MaxLifetime.Should().Be(TimeSpan.FromMinutes(60));
    }

    [Fact]
    public void ConnectionPoolOptions_DefaultDisableAffinityCookie_IsTrue()
    {
        // Arrange & Act
        var options = new ConnectionPoolOptions();

        // Assert - disabled by default for better load distribution
        options.DisableAffinityCookie.Should().BeTrue();
    }

    [Fact]
    public void ConnectionPoolOptions_DefaultSelectionStrategy_IsThrottleAware()
    {
        // Arrange & Act
        var options = new ConnectionPoolOptions();

        // Assert
        options.SelectionStrategy.Should().Be(ConnectionSelectionStrategy.ThrottleAware);
    }

    [Fact]
    public void ConnectionPoolOptions_DefaultValidationInterval_Is1Minute()
    {
        // Arrange & Act
        var options = new ConnectionPoolOptions();

        // Assert
        options.ValidationInterval.Should().Be(TimeSpan.FromMinutes(1));
    }

    [Fact]
    public void ConnectionPoolOptions_DefaultEnableValidation_IsTrue()
    {
        // Arrange & Act
        var options = new ConnectionPoolOptions();

        // Assert
        options.EnableValidation.Should().BeTrue();
    }

    [Fact]
    public void ConnectionPoolOptions_DefaultValidateOnCheckout_IsTrue()
    {
        // Arrange & Act
        var options = new ConnectionPoolOptions();

        // Assert
        options.ValidateOnCheckout.Should().BeTrue();
    }

    [Fact]
    public void ConnectionPoolOptions_DefaultMaxConnectionRetries_Is2()
    {
        // Arrange & Act
        var options = new ConnectionPoolOptions();

        // Assert
        options.MaxConnectionRetries.Should().Be(2);
    }

    [Fact]
    public void ConnectionPoolOptions_DefaultMaxRetryAfterTolerance_IsNull()
    {
        // Arrange & Act
        var options = new ConnectionPoolOptions();

        // Assert - null means wait indefinitely for throttle to clear
        options.MaxRetryAfterTolerance.Should().BeNull();
    }

    #endregion

    #region MaxPoolSize Override Tests

    [Theory]
    [InlineData(50)]
    [InlineData(100)]
    [InlineData(25)]
    public void PoolCapacity_MaxPoolSize_CanBeCustomized(int maxPoolSize)
    {
        // Arrange & Act
        var options = new ConnectionPoolOptions
        {
            MaxPoolSize = maxPoolSize
        };

        // Assert
        options.MaxPoolSize.Should().Be(maxPoolSize);
    }

    [Fact]
    public void PoolCapacity_ZeroMaxPoolSize_MeansUseDopBasedSizing()
    {
        // Arrange & Act
        var options = new ConnectionPoolOptions
        {
            MaxPoolSize = 0
        };

        // Assert - 0 is a sentinel meaning "use DOP from server"
        options.MaxPoolSize.Should().Be(0);
    }

    #endregion

    #region Selection Strategy Tests

    [Theory]
    [InlineData(ConnectionSelectionStrategy.RoundRobin)]
    [InlineData(ConnectionSelectionStrategy.LeastConnections)]
    [InlineData(ConnectionSelectionStrategy.ThrottleAware)]
    public void SelectionStrategy_CanBeSet(ConnectionSelectionStrategy strategy)
    {
        // Arrange & Act
        var options = new ConnectionPoolOptions
        {
            SelectionStrategy = strategy
        };

        // Assert
        options.SelectionStrategy.Should().Be(strategy);
    }

    #endregion

    #region Timeout Configuration Tests

    [Fact]
    public void AcquireTimeout_CanBeCustomized()
    {
        // Arrange & Act
        var options = new ConnectionPoolOptions
        {
            AcquireTimeout = TimeSpan.FromSeconds(60)
        };

        // Assert
        options.AcquireTimeout.Should().Be(TimeSpan.FromSeconds(60));
    }

    [Fact]
    public void MaxIdleTime_CanBeCustomized()
    {
        // Arrange & Act
        var options = new ConnectionPoolOptions
        {
            MaxIdleTime = TimeSpan.FromMinutes(10)
        };

        // Assert
        options.MaxIdleTime.Should().Be(TimeSpan.FromMinutes(10));
    }

    [Fact]
    public void MaxLifetime_CanBeCustomized()
    {
        // Arrange & Act
        var options = new ConnectionPoolOptions
        {
            MaxLifetime = TimeSpan.FromMinutes(30)
        };

        // Assert
        options.MaxLifetime.Should().Be(TimeSpan.FromMinutes(30));
    }

    [Fact]
    public void MaxRetryAfterTolerance_CanBeCustomized()
    {
        // Arrange & Act
        var options = new ConnectionPoolOptions
        {
            MaxRetryAfterTolerance = TimeSpan.FromMinutes(2)
        };

        // Assert
        options.MaxRetryAfterTolerance.Should().Be(TimeSpan.FromMinutes(2));
    }

    #endregion

    #region Pool Behavior Configuration Tests

    [Fact]
    public void DisableAffinityCookie_CanBeSetToFalse()
    {
        // Arrange & Act
        var options = new ConnectionPoolOptions
        {
            DisableAffinityCookie = false
        };

        // Assert - can be set to false for session affinity scenarios
        options.DisableAffinityCookie.Should().BeFalse();
    }

    [Fact]
    public void EnableValidation_CanBeDisabled()
    {
        // Arrange & Act
        var options = new ConnectionPoolOptions
        {
            EnableValidation = false
        };

        // Assert
        options.EnableValidation.Should().BeFalse();
    }

    [Fact]
    public void ValidateOnCheckout_CanBeDisabled()
    {
        // Arrange & Act
        var options = new ConnectionPoolOptions
        {
            ValidateOnCheckout = false
        };

        // Assert
        options.ValidateOnCheckout.Should().BeFalse();
    }

    [Fact]
    public void MaxConnectionRetries_CanBeCustomized()
    {
        // Arrange & Act
        var options = new ConnectionPoolOptions
        {
            MaxConnectionRetries = 5
        };

        // Assert
        options.MaxConnectionRetries.Should().Be(5);
    }

    #endregion

    #region Documentation Tests

    [Fact]
    public void MicrosoftHardLimitPerUser_Is52()
    {
        // Microsoft's hard limit for concurrent requests per Application User is 52.
        // This is an enforced platform limit that cannot be exceeded.
        // See: https://learn.microsoft.com/en-us/power-apps/developer/data-platform/send-parallel-requests

        // The constant is internal, but we can verify the documented behavior through
        // the pool's DOP clamping logic (tested in DataverseConnectionPoolTests)

        // This test documents the expected value
        const int MicrosoftHardLimit = 52;
        MicrosoftHardLimit.Should().Be(52,
            "because Microsoft enforces a hard limit of 52 concurrent requests per Application User");
    }

    [Fact]
    public void DopBasedSizing_Concept_Documentation()
    {
        // DOP-based sizing uses the server's RecommendedDegreesOfParallelism (from x-ms-dop-hint header)
        // instead of a static configuration value.
        //
        // Benefits:
        // - Automatically adapts to environment type (trial=4, production=50)
        // - Respects server-side limits without manual configuration
        // - Scales with the number of connections: TotalDOP = sum(DOP per connection)
        //
        // When MaxPoolSize is 0 (default), the pool:
        // 1. Creates seed clients for each connection source
        // 2. Reads RecommendedDegreesOfParallelism from each seed
        // 3. Clamps values to [1, 52] (Microsoft's hard limit)
        // 4. Sums DOP across all sources for total capacity

        var options = new ConnectionPoolOptions();
        options.MaxPoolSize.Should().Be(0, "0 means use DOP-based sizing from server");
    }

    #endregion
}
