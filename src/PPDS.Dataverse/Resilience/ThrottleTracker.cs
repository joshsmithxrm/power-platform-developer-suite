using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace PPDS.Dataverse.Resilience
{
    /// <summary>
    /// Tracks throttle state for connections using a thread-safe concurrent dictionary.
    /// </summary>
    public sealed class ThrottleTracker : IThrottleTracker
    {
        private readonly ConcurrentDictionary<string, ThrottleState> _throttleStates;
        private readonly ILogger<ThrottleTracker> _logger;
        private long _totalThrottleEvents;
        private long _totalBackoffTicks;

        /// <summary>
        /// Initializes a new instance of the <see cref="ThrottleTracker"/> class.
        /// </summary>
        /// <param name="logger">Logger instance.</param>
        public ThrottleTracker(ILogger<ThrottleTracker> logger)
        {
            _throttleStates = new ConcurrentDictionary<string, ThrottleState>();
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <inheritdoc />
        public long TotalThrottleEvents => _totalThrottleEvents;

        /// <inheritdoc />
        public TimeSpan TotalBackoffTime => TimeSpan.FromTicks(Interlocked.Read(ref _totalBackoffTicks));

        /// <inheritdoc />
        public int ThrottledConnectionCount
        {
            get
            {
                CleanupExpired();
                return _throttleStates.Count;
            }
        }

        /// <inheritdoc />
        public IReadOnlyCollection<string> ThrottledConnections
        {
            get
            {
                CleanupExpired();
                return _throttleStates.Keys.ToList().AsReadOnly();
            }
        }

        /// <inheritdoc />
        public void RecordThrottle(string connectionName, TimeSpan retryAfter)
        {
            if (string.IsNullOrEmpty(connectionName))
            {
                throw new ArgumentNullException(nameof(connectionName));
            }

            var now = DateTime.UtcNow;
            var state = new ThrottleState
            {
                ConnectionName = connectionName,
                ThrottledAt = now,
                ExpiresAt = now + retryAfter,
                RetryAfter = retryAfter
            };

            _throttleStates.AddOrUpdate(connectionName, state, (_, __) => state);
            Interlocked.Increment(ref _totalThrottleEvents);
            Interlocked.Add(ref _totalBackoffTicks, retryAfter.Ticks);

            _logger.LogWarning(
                "Connection throttled. Name: {ConnectionName}, RetryAfter: {RetryAfter}, ExpiresAt: {ExpiresAt}",
                connectionName,
                retryAfter,
                state.ExpiresAt);
        }

        /// <inheritdoc />
        public bool IsThrottled(string connectionName)
        {
            if (string.IsNullOrEmpty(connectionName))
            {
                return false;
            }

            if (!_throttleStates.TryGetValue(connectionName, out var state))
            {
                return false;
            }

            if (state.IsExpired)
            {
                // Clean up expired throttle
                _throttleStates.TryRemove(connectionName, out _);
                return false;
            }

            return true;
        }

        /// <inheritdoc />
        public DateTime? GetThrottleExpiry(string connectionName)
        {
            if (string.IsNullOrEmpty(connectionName))
            {
                return null;
            }

            if (!_throttleStates.TryGetValue(connectionName, out var state))
            {
                return null;
            }

            if (state.IsExpired)
            {
                _throttleStates.TryRemove(connectionName, out _);
                return null;
            }

            return state.ExpiresAt;
        }

        /// <inheritdoc />
        public void ClearThrottle(string connectionName)
        {
            if (string.IsNullOrEmpty(connectionName))
            {
                return;
            }

            if (_throttleStates.TryRemove(connectionName, out _))
            {
                _logger.LogInformation("Cleared throttle for connection: {ConnectionName}", connectionName);
            }
        }

        /// <inheritdoc />
        public TimeSpan GetShortestExpiry()
        {
            CleanupExpired();

            if (_throttleStates.IsEmpty)
            {
                return TimeSpan.Zero;
            }

            var now = DateTime.UtcNow;
            var shortest = _throttleStates.Values
                .Select(s => s.ExpiresAt - now)
                .Where(t => t > TimeSpan.Zero)
                .DefaultIfEmpty(TimeSpan.Zero)
                .Min();

            return shortest;
        }

        /// <summary>
        /// Removes expired throttle states from the dictionary.
        /// </summary>
        private void CleanupExpired()
        {
            var now = DateTime.UtcNow;
            var expired = _throttleStates
                .Where(kvp => kvp.Value.ExpiresAt <= now)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in expired.Where(k => _throttleStates.TryRemove(k, out _)))
            {
                _logger.LogDebug("Throttle expired for connection: {ConnectionName}", key);
            }
        }
    }
}
