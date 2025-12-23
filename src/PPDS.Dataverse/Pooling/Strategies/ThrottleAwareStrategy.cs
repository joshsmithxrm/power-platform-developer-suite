using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using PPDS.Dataverse.Resilience;

namespace PPDS.Dataverse.Pooling.Strategies
{
    /// <summary>
    /// Avoids throttled connections and falls back to round-robin among available connections.
    /// If all connections are throttled, returns the connection with shortest remaining throttle time.
    /// </summary>
    public sealed class ThrottleAwareStrategy : IConnectionSelectionStrategy
    {
        private int _counter;

        /// <inheritdoc />
        public string SelectConnection(
            IReadOnlyList<DataverseConnection> connections,
            IThrottleTracker throttleTracker,
            IReadOnlyDictionary<string, int> activeConnections)
        {
            if (connections.Count == 0)
            {
                throw new InvalidOperationException("No connections available.");
            }

            if (connections.Count == 1)
            {
                return connections[0].Name;
            }

            // Strictly filter out ALL throttled connections
            var availableConnections = connections
                .Where(c => !throttleTracker.IsThrottled(c.Name))
                .ToList();

            if (availableConnections.Count == 0)
            {
                // All connections are throttled - find the one with shortest remaining throttle time
                // so the caller can wait for it to become available
                return FindConnectionWithShortestThrottleExpiry(connections, throttleTracker);
            }

            if (availableConnections.Count == 1)
            {
                return availableConnections[0].Name;
            }

            // Round-robin among available (non-throttled) connections only
            var index = Interlocked.Increment(ref _counter) % availableConnections.Count;
            return availableConnections[index].Name;
        }

        /// <summary>
        /// Finds the connection with the shortest remaining throttle expiry time.
        /// </summary>
        private static string FindConnectionWithShortestThrottleExpiry(
            IReadOnlyList<DataverseConnection> connections,
            IThrottleTracker throttleTracker)
        {
            string? shortestConnection = null;
            DateTime? shortestExpiry = null;

            foreach (var connection in connections)
            {
                var expiry = throttleTracker.GetThrottleExpiry(connection.Name);

                if (expiry == null)
                {
                    // This connection is no longer throttled (expired between checks) - use it
                    return connection.Name;
                }

                if (shortestExpiry == null || expiry < shortestExpiry)
                {
                    shortestExpiry = expiry;
                    shortestConnection = connection.Name;
                }
            }

            // Return the connection with shortest expiry, or fall back to first if none found
            return shortestConnection ?? connections[0].Name;
        }
    }
}
