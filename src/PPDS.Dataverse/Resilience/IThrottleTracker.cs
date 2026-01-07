using System;
using System.Collections.Generic;

namespace PPDS.Dataverse.Resilience
{
    /// <summary>
    /// Tracks throttle state across connections.
    /// Used by the connection pool to route requests away from throttled connections.
    /// </summary>
    public interface IThrottleTracker
    {
        /// <summary>
        /// Records a throttle event for a connection.
        /// </summary>
        /// <param name="connectionName">The connection that was throttled.</param>
        /// <param name="retryAfter">How long to wait before retrying.</param>
        void RecordThrottle(string connectionName, TimeSpan retryAfter);

        /// <summary>
        /// Checks if a connection is currently throttled.
        /// </summary>
        /// <param name="connectionName">The connection to check.</param>
        /// <returns>True if the connection is throttled.</returns>
        bool IsThrottled(string connectionName);

        /// <summary>
        /// Gets when a connection's throttle expires.
        /// </summary>
        /// <param name="connectionName">The connection to check.</param>
        /// <returns>The expiry time, or null if not throttled.</returns>
        DateTime? GetThrottleExpiry(string connectionName);

        /// <summary>
        /// Clears throttle state for a connection.
        /// </summary>
        /// <param name="connectionName">The connection to clear.</param>
        void ClearThrottle(string connectionName);

        /// <summary>
        /// Gets the total number of throttle events recorded.
        /// </summary>
        long TotalThrottleEvents { get; }

        /// <summary>
        /// Gets the total backoff time accumulated from all throttle events.
        /// This represents the sum of all RetryAfter durations, not actual time spent waiting.
        /// </summary>
        TimeSpan TotalBackoffTime { get; }

        /// <summary>
        /// Gets the number of currently throttled connections.
        /// </summary>
        int ThrottledConnectionCount { get; }

        /// <summary>
        /// Gets the names of all currently throttled connections.
        /// </summary>
        IReadOnlyCollection<string> ThrottledConnections { get; }

        /// <summary>
        /// Gets the shortest time until any throttled connection expires.
        /// Returns <see cref="TimeSpan.Zero"/> if no connections are throttled.
        /// </summary>
        /// <returns>The shortest time until a throttle expires, or <see cref="TimeSpan.Zero"/> if none are throttled.</returns>
        TimeSpan GetShortestExpiry();
    }
}
