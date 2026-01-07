using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using PPDS.Dataverse.Resilience;
using Xunit;

namespace PPDS.Dataverse.Tests.Resilience;

public class ThrottleTrackerTests
{
    private readonly Mock<ILogger<ThrottleTracker>> _loggerMock;
    private readonly ThrottleTracker _tracker;

    public ThrottleTrackerTests()
    {
        _loggerMock = new Mock<ILogger<ThrottleTracker>>();
        _tracker = new ThrottleTracker(_loggerMock.Object);
    }

    #region RecordThrottle Tests

    [Fact]
    public void RecordThrottle_MarksConnectionAsThrottled()
    {
        // Arrange
        const string connectionName = "Primary";
        var retryAfter = TimeSpan.FromMinutes(5);

        // Act
        _tracker.RecordThrottle(connectionName, retryAfter);

        // Assert
        _tracker.IsThrottled(connectionName).Should().BeTrue();
    }

    [Fact]
    public void RecordThrottle_IncrementsTotalThrottleEvents()
    {
        // Arrange
        const string connectionName = "Primary";
        var retryAfter = TimeSpan.FromMinutes(5);
        var initialCount = _tracker.TotalThrottleEvents;

        // Act
        _tracker.RecordThrottle(connectionName, retryAfter);

        // Assert
        _tracker.TotalThrottleEvents.Should().Be(initialCount + 1);
    }

    [Fact]
    public void RecordThrottle_UpdatesExistingThrottle()
    {
        // Arrange
        const string connectionName = "Primary";
        var firstRetryAfter = TimeSpan.FromMinutes(5);
        var secondRetryAfter = TimeSpan.FromMinutes(10);

        // Act
        _tracker.RecordThrottle(connectionName, firstRetryAfter);
        var firstExpiry = _tracker.GetThrottleExpiry(connectionName);

        _tracker.RecordThrottle(connectionName, secondRetryAfter);
        var secondExpiry = _tracker.GetThrottleExpiry(connectionName);

        // Assert
        secondExpiry.Should().BeAfter(firstExpiry!.Value);
    }

    [Fact]
    public void RecordThrottle_ThrowsOnNullConnectionName()
    {
        // Arrange
        var retryAfter = TimeSpan.FromMinutes(5);

        // Act & Assert
        var act = () => _tracker.RecordThrottle(null!, retryAfter);
        act.Should().Throw<ArgumentNullException>();
    }

    #endregion

    #region IsThrottled Tests

    [Fact]
    public void IsThrottled_ReturnsFalseForUnknownConnection()
    {
        // Arrange
        const string connectionName = "Unknown";

        // Act & Assert
        _tracker.IsThrottled(connectionName).Should().BeFalse();
    }

    [Fact]
    public void IsThrottled_ReturnsFalseAfterExpiry()
    {
        // Arrange
        const string connectionName = "Primary";
        var retryAfter = TimeSpan.FromMilliseconds(1);

        // Act
        _tracker.RecordThrottle(connectionName, retryAfter);
        Thread.Sleep(50); // Wait for expiry

        // Assert
        _tracker.IsThrottled(connectionName).Should().BeFalse();
    }

    [Fact]
    public void IsThrottled_ReturnsTrueWithinExpiryWindow()
    {
        // Arrange
        const string connectionName = "Primary";
        var retryAfter = TimeSpan.FromMinutes(5);

        // Act
        _tracker.RecordThrottle(connectionName, retryAfter);

        // Assert
        _tracker.IsThrottled(connectionName).Should().BeTrue();
    }

    [Fact]
    public void IsThrottled_ReturnsFalseForNullOrEmpty()
    {
        // Act & Assert
        _tracker.IsThrottled(null!).Should().BeFalse();
        _tracker.IsThrottled(string.Empty).Should().BeFalse();
    }

    #endregion

    #region GetThrottleExpiry Tests

    [Fact]
    public void GetThrottleExpiry_ReturnsNullForUnknownConnection()
    {
        // Arrange
        const string connectionName = "Unknown";

        // Act & Assert
        _tracker.GetThrottleExpiry(connectionName).Should().BeNull();
    }

    [Fact]
    public void GetThrottleExpiry_ReturnsExpiryTime()
    {
        // Arrange
        const string connectionName = "Primary";
        var retryAfter = TimeSpan.FromMinutes(5);
        var before = DateTime.UtcNow;

        // Act
        _tracker.RecordThrottle(connectionName, retryAfter);
        var expiry = _tracker.GetThrottleExpiry(connectionName);

        // Assert
        expiry.Should().NotBeNull();
        expiry!.Value.Should().BeAfter(before);
        expiry.Value.Should().BeCloseTo(before + retryAfter, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void GetThrottleExpiry_ReturnsNullAfterExpiry()
    {
        // Arrange
        const string connectionName = "Primary";
        var retryAfter = TimeSpan.FromMilliseconds(1);

        // Act
        _tracker.RecordThrottle(connectionName, retryAfter);
        Thread.Sleep(50); // Wait for expiry

        // Assert
        _tracker.GetThrottleExpiry(connectionName).Should().BeNull();
    }

    #endregion

    #region ClearThrottle Tests

    [Fact]
    public void ClearThrottle_RemovesThrottleState()
    {
        // Arrange
        const string connectionName = "Primary";
        _tracker.RecordThrottle(connectionName, TimeSpan.FromMinutes(5));

        // Act
        _tracker.ClearThrottle(connectionName);

        // Assert
        _tracker.IsThrottled(connectionName).Should().BeFalse();
    }

    [Fact]
    public void ClearThrottle_DoesNothingForUnknownConnection()
    {
        // Arrange
        const string connectionName = "Unknown";

        // Act & Assert (should not throw)
        var act = () => _tracker.ClearThrottle(connectionName);
        act.Should().NotThrow();
    }

    #endregion

    #region TotalBackoffTime Tests

    [Fact]
    public void TotalBackoffTime_StartsAtZero()
    {
        // Assert
        _tracker.TotalBackoffTime.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void TotalBackoffTime_AccumulatesBackoffDurations()
    {
        // Arrange
        var firstBackoff = TimeSpan.FromMinutes(1);
        var secondBackoff = TimeSpan.FromMinutes(2);

        // Act
        _tracker.RecordThrottle("Primary", firstBackoff);
        _tracker.RecordThrottle("Secondary", secondBackoff);

        // Assert
        _tracker.TotalBackoffTime.Should().Be(firstBackoff + secondBackoff);
    }

    [Fact]
    public void TotalBackoffTime_AccumulatesForSameConnection()
    {
        // Arrange
        var firstBackoff = TimeSpan.FromMinutes(1);
        var secondBackoff = TimeSpan.FromMinutes(2);

        // Act
        _tracker.RecordThrottle("Primary", firstBackoff);
        _tracker.RecordThrottle("Primary", secondBackoff);

        // Assert
        _tracker.TotalBackoffTime.Should().Be(firstBackoff + secondBackoff);
    }

    #endregion
}
