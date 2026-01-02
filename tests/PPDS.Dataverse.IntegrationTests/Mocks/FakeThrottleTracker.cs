using PPDS.Dataverse.Resilience;

namespace PPDS.Dataverse.IntegrationTests.Mocks;

/// <summary>
/// Fake IThrottleTracker implementation for testing.
/// By default, never reports throttling. Can be configured to simulate throttling.
/// </summary>
public class FakeThrottleTracker : IThrottleTracker
{
    private readonly Dictionary<string, DateTime> _throttledConnections = new();
    private long _totalThrottleEvents;

    public long TotalThrottleEvents => _totalThrottleEvents;

    public int ThrottledConnectionCount => _throttledConnections.Count(kvp => kvp.Value > DateTime.UtcNow);

    public IReadOnlyCollection<string> ThrottledConnections =>
        _throttledConnections
            .Where(kvp => kvp.Value > DateTime.UtcNow)
            .Select(kvp => kvp.Key)
            .ToList();

    public void RecordThrottle(string connectionName, TimeSpan retryAfter)
    {
        _throttledConnections[connectionName] = DateTime.UtcNow.Add(retryAfter);
        Interlocked.Increment(ref _totalThrottleEvents);
    }

    public bool IsThrottled(string connectionName)
    {
        if (_throttledConnections.TryGetValue(connectionName, out var expiry))
        {
            return expiry > DateTime.UtcNow;
        }
        return false;
    }

    public DateTime? GetThrottleExpiry(string connectionName)
    {
        if (_throttledConnections.TryGetValue(connectionName, out var expiry) && expiry > DateTime.UtcNow)
        {
            return expiry;
        }
        return null;
    }

    public void ClearThrottle(string connectionName)
    {
        _throttledConnections.Remove(connectionName);
    }

    public TimeSpan GetShortestExpiry()
    {
        var now = DateTime.UtcNow;
        var activeThrottles = _throttledConnections
            .Where(kvp => kvp.Value > now)
            .Select(kvp => kvp.Value - now)
            .ToList();

        return activeThrottles.Count > 0 ? activeThrottles.Min() : TimeSpan.Zero;
    }

    /// <summary>
    /// Resets all throttle state. Useful for test cleanup.
    /// </summary>
    public void Reset()
    {
        _throttledConnections.Clear();
        _totalThrottleEvents = 0;
    }
}
