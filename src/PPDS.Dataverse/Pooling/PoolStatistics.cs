using System;
using System.Collections.Generic;

namespace PPDS.Dataverse.Pooling
{
    /// <summary>
    /// Statistics and health information for the connection pool.
    /// </summary>
    public class PoolStatistics
    {
        /// <summary>
        /// Gets the total number of connections (active + idle).
        /// </summary>
        public int TotalConnections { get; init; }

        /// <summary>
        /// Gets the number of connections currently in use.
        /// </summary>
        public int ActiveConnections { get; init; }

        /// <summary>
        /// Gets the number of idle connections in the pool.
        /// </summary>
        public int IdleConnections { get; init; }

        /// <summary>
        /// Gets the number of connections currently throttled.
        /// </summary>
        public int ThrottledConnections { get; init; }

        /// <summary>
        /// Gets the total number of requests served by the pool.
        /// </summary>
        public long RequestsServed { get; init; }

        /// <summary>
        /// Gets the total number of throttle events recorded.
        /// </summary>
        public long ThrottleEvents { get; init; }

        /// <summary>
        /// Gets the total backoff time accumulated from all throttle events.
        /// </summary>
        public TimeSpan TotalBackoffTime { get; init; }

        /// <summary>
        /// Gets the total number of retry attempts made after throttle events.
        /// </summary>
        public long RetriesAttempted { get; init; }

        /// <summary>
        /// Gets the number of retry attempts that succeeded.
        /// </summary>
        public long RetriesSucceeded { get; init; }

        /// <summary>
        /// Gets the number of connections that were invalidated due to failures.
        /// </summary>
        public long InvalidConnections { get; init; }

        /// <summary>
        /// Gets the number of authentication failures detected.
        /// </summary>
        public long AuthFailures { get; init; }

        /// <summary>
        /// Gets the number of connection failures detected.
        /// </summary>
        public long ConnectionFailures { get; init; }

        /// <summary>
        /// Gets per-connection statistics.
        /// </summary>
        public IReadOnlyDictionary<string, ConnectionStatistics> ConnectionStats { get; init; }
            = new Dictionary<string, ConnectionStatistics>();
    }

    /// <summary>
    /// Statistics for a specific connection configuration.
    /// </summary>
    public class ConnectionStatistics
    {
        /// <summary>
        /// Gets the connection name.
        /// </summary>
        public string Name { get; init; } = string.Empty;

        /// <summary>
        /// Gets the number of active connections.
        /// </summary>
        public int ActiveConnections { get; init; }

        /// <summary>
        /// Gets the number of idle connections.
        /// </summary>
        public int IdleConnections { get; init; }

        /// <summary>
        /// Gets a value indicating whether this connection is currently throttled.
        /// </summary>
        public bool IsThrottled { get; init; }

        /// <summary>
        /// Gets the requests served by this connection.
        /// </summary>
        public long RequestsServed { get; init; }
    }
}
