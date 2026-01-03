using FluentAssertions;
using PPDS.Dataverse.Pooling;
using PPDS.Dataverse.Resilience;
using PPDS.LiveTests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace PPDS.LiveTests.Resilience;

/// <summary>
/// Live integration tests for throttle detection and handling infrastructure.
/// </summary>
/// <remarks>
/// <para>
/// These tests verify the throttle tracking infrastructure works correctly with real connections.
/// </para>
/// <para>
/// Note: Intentionally triggering 429 responses is not practical in automated tests because:
/// <list type="bullet">
/// <item>Service protection limits are per 5-minute window (6000 requests, 20 min execution time, 52 concurrent)</item>
/// <item>Triggering them would impact other tests and slow down the test suite</item>
/// <item>The actual throttle response parsing is covered by unit tests</item>
/// </list>
/// </para>
/// <para>
/// For manual throttle testing, see MANUAL_TESTING.md in the Resilience folder.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public class ThrottleDetectionLiveTests : LiveTestBase, IDisposable
{
    private readonly ITestOutputHelper _output;

    public ThrottleDetectionLiveTests(ITestOutputHelper output)
    {
        _output = output;
    }

    #region Throttle Tracker Integration Tests

    [Fact]
    public void ThrottleTracker_RecordsAndClearsThrottle()
    {
        // Arrange
        var tracker = LiveTestHelpers.CreateThrottleTracker();
        var connectionName = "TestConnection";

        // Act - Record a throttle
        tracker.RecordThrottle(connectionName, TimeSpan.FromSeconds(10));

        // Assert - Throttle is recorded
        tracker.IsThrottled(connectionName).Should().BeTrue();
        tracker.TotalThrottleEvents.Should().Be(1);
        tracker.GetThrottleExpiry(connectionName).Should().NotBeNull();

        _output.WriteLine($"Throttle recorded. Expiry: {tracker.GetThrottleExpiry(connectionName)}");

        // Act - Clear the throttle
        tracker.ClearThrottle(connectionName);

        // Assert - Throttle is cleared
        tracker.IsThrottled(connectionName).Should().BeFalse();
        tracker.GetThrottleExpiry(connectionName).Should().BeNull();

        _output.WriteLine("Throttle cleared successfully");
    }

    [Fact]
    public void ThrottleTracker_ExpiresThrottleAfterDuration()
    {
        // Arrange
        var tracker = LiveTestHelpers.CreateThrottleTracker();
        var connectionName = "ExpiryTest";
        var shortDuration = TimeSpan.FromMilliseconds(100);

        // Act - Record a short throttle
        tracker.RecordThrottle(connectionName, shortDuration);
        tracker.IsThrottled(connectionName).Should().BeTrue();

        // Wait for expiry
        Thread.Sleep(200);

        // Assert - Throttle should be expired
        tracker.IsThrottled(connectionName).Should().BeFalse(
            "Throttle should expire after the RetryAfter duration");

        _output.WriteLine("Throttle expired as expected");
    }

    [Fact]
    public void ThrottleTracker_TracksMultipleConnections()
    {
        // Arrange
        var tracker = LiveTestHelpers.CreateThrottleTracker();

        // Act - Throttle multiple connections
        tracker.RecordThrottle("Connection1", TimeSpan.FromSeconds(30));
        tracker.RecordThrottle("Connection2", TimeSpan.FromSeconds(60));

        // Assert
        tracker.IsThrottled("Connection1").Should().BeTrue();
        tracker.IsThrottled("Connection2").Should().BeTrue();
        tracker.ThrottledConnectionCount.Should().Be(2);
        tracker.TotalThrottleEvents.Should().Be(2);

        _output.WriteLine($"Throttled connections: {string.Join(", ", tracker.ThrottledConnections)}");
    }

    [Fact]
    public void ThrottleTracker_GetShortestExpiryReturnsMinimum()
    {
        // Arrange
        var tracker = LiveTestHelpers.CreateThrottleTracker();

        // Act - Throttle with different durations
        tracker.RecordThrottle("Short", TimeSpan.FromSeconds(5));
        tracker.RecordThrottle("Long", TimeSpan.FromSeconds(60));

        var shortest = tracker.GetShortestExpiry();

        // Assert - Should return the shorter duration (approximately)
        shortest.Should().BeLessThan(TimeSpan.FromSeconds(10),
            "Should return the shorter throttle duration");

        _output.WriteLine($"Shortest expiry: {shortest.TotalSeconds:N1} seconds");
    }

    #endregion

    #region Pool Throttle-Aware Strategy Tests

    [SkipIfNoClientSecret]
    public async Task Pool_UsesThrottleAwareStrategy()
    {
        // Arrange
        using var source = await LiveTestHelpers.CreateConnectionSourceAsync(
            Configuration, "ThrottleAwareTest");

        var options = new ConnectionPoolOptions
        {
            Enabled = true,
            EnableValidation = false,
            SelectionStrategy = ConnectionSelectionStrategy.ThrottleAware
        };

        using var pool = LiveTestHelpers.CreateConnectionPool(new[] { source }, options);

        // Act - Get a client (should work since nothing is throttled)
        await using var client = await pool.GetClientAsync();

        // Assert
        client.Should().NotBeNull();
        client.IsReady.Should().BeTrue();

        var stats = pool.Statistics;
        stats.ThrottledConnections.Should().Be(0,
            "No connections should be throttled initially");

        _output.WriteLine($"Pool using ThrottleAware strategy. Throttled: {stats.ThrottledConnections}");
    }

    [SkipIfNoClientSecret]
    public async Task Pool_StatisticsShowThrottleEvents()
    {
        // Arrange
        using var source = await LiveTestHelpers.CreateConnectionSourceAsync(
            Configuration, "ThrottleStatsTest");

        using var pool = LiveTestHelpers.CreateConnectionPool(new[] { source });

        // Act
        var initialStats = pool.Statistics;

        // Execute some requests
        await using (var client = await pool.GetClientAsync())
        {
            client.Execute(new Microsoft.Crm.Sdk.Messages.WhoAmIRequest());
        }

        var afterStats = pool.Statistics;

        // Assert
        _output.WriteLine($"Initial throttle events: {initialStats.ThrottleEvents}");
        _output.WriteLine($"After operations throttle events: {afterStats.ThrottleEvents}");
        _output.WriteLine("Note: ThrottleEvents should remain 0 under normal load");

        // Under normal conditions, no throttle events should occur
        afterStats.ThrottleEvents.Should().Be(0,
            "Normal operations should not trigger throttle events");
    }

    #endregion

    #region Connection Selection Strategy Tests

    [SkipIfNoClientSecret]
    public async Task Pool_CanUseRoundRobinStrategy()
    {
        // Arrange
        using var source = await LiveTestHelpers.CreateConnectionSourceAsync(
            Configuration, "RoundRobinTest");

        var options = new ConnectionPoolOptions
        {
            Enabled = true,
            EnableValidation = false,
            SelectionStrategy = ConnectionSelectionStrategy.RoundRobin
        };

        // Act
        using var pool = LiveTestHelpers.CreateConnectionPool(new[] { source }, options);
        await using var client = await pool.GetClientAsync();

        // Assert
        client.Should().NotBeNull();
        client.IsReady.Should().BeTrue();

        _output.WriteLine($"Pool using RoundRobin strategy - Connection: {client.ConnectionName}");
    }

    [SkipIfNoClientSecret]
    public async Task Pool_CanUseLeastConnectionsStrategy()
    {
        // Arrange
        using var source = await LiveTestHelpers.CreateConnectionSourceAsync(
            Configuration, "LeastConnTest");

        var options = new ConnectionPoolOptions
        {
            Enabled = true,
            EnableValidation = false,
            SelectionStrategy = ConnectionSelectionStrategy.LeastConnections
        };

        // Act
        using var pool = LiveTestHelpers.CreateConnectionPool(new[] { source }, options);
        await using var client = await pool.GetClientAsync();

        // Assert
        client.Should().NotBeNull();
        client.IsReady.Should().BeTrue();

        _output.WriteLine($"Pool using LeastConnections strategy - Connection: {client.ConnectionName}");
    }

    #endregion

    #region Throttle Tolerance Configuration Tests

    [SkipIfNoClientSecret]
    public async Task Pool_RespectsMaxRetryAfterTolerance()
    {
        // Arrange
        using var source = await LiveTestHelpers.CreateConnectionSourceAsync(
            Configuration, "ToleranceTest");

        var options = new ConnectionPoolOptions
        {
            Enabled = true,
            EnableValidation = false,
            MaxRetryAfterTolerance = TimeSpan.FromSeconds(5) // Short tolerance for testing
        };

        // Act
        using var pool = LiveTestHelpers.CreateConnectionPool(new[] { source }, options);
        await using var client = await pool.GetClientAsync();

        // Assert - Should work when not throttled
        client.Should().NotBeNull();
        client.IsReady.Should().BeTrue();

        _output.WriteLine($"MaxRetryAfterTolerance configured: 5 seconds");
        _output.WriteLine("Note: If all connections were throttled > 5s, pool would throw ServiceProtectionException");
    }

    #endregion

    public void Dispose()
    {
        Configuration.Dispose();
    }
}
