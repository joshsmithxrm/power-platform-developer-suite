using System;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PPDS.Dataverse.DependencyInjection;

namespace PPDS.Dataverse.Resilience
{
    /// <summary>
    /// AIMD-based adaptive rate controller for throttle recovery.
    /// Manages per-connection parallelism that adjusts based on throttle responses.
    /// </summary>
    public sealed class AdaptiveRateController : IAdaptiveRateController
    {
        private readonly AdaptiveRateOptions _options;
        private readonly ILogger<AdaptiveRateController> _logger;
        private readonly ConcurrentDictionary<string, ConnectionState> _states;

        /// <summary>
        /// Initializes a new instance of the <see cref="AdaptiveRateController"/> class.
        /// </summary>
        public AdaptiveRateController(
            IOptions<DataverseOptions> options,
            ILogger<AdaptiveRateController> logger)
        {
            _options = options?.Value?.AdaptiveRate ?? new AdaptiveRateOptions();
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _states = new ConcurrentDictionary<string, ConnectionState>(StringComparer.OrdinalIgnoreCase);
        }

        /// <inheritdoc />
        public bool IsEnabled => _options.Enabled;

        /// <inheritdoc />
        public int GetParallelism(string connectionName, int maxParallelism)
        {
            if (!IsEnabled)
            {
                return maxParallelism;
            }

            var state = GetOrCreateState(connectionName, maxParallelism);

            lock (state.SyncRoot)
            {
                // Check for idle reset
                var timeSinceActivity = DateTime.UtcNow - state.LastActivityTime;
                if (timeSinceActivity > _options.IdleResetPeriod)
                {
                    _logger.LogDebug(
                        "Connection {Connection} idle for {IdleTime}, resetting adaptive state",
                        connectionName, timeSinceActivity);
                    ResetStateInternal(state, maxParallelism);
                }

                state.LastActivityTime = DateTime.UtcNow;
                return state.CurrentParallelism;
            }
        }

        /// <inheritdoc />
        public void RecordSuccess(string connectionName)
        {
            if (!IsEnabled)
            {
                return;
            }

            if (!_states.TryGetValue(connectionName, out var state))
            {
                return;
            }

            lock (state.SyncRoot)
            {
                state.LastActivityTime = DateTime.UtcNow;
                state.SuccessesSinceThrottle++;

                // Check if lastKnownGood is stale
                var timeSinceLastKnownGood = DateTime.UtcNow - state.LastKnownGoodTimestamp;
                if (timeSinceLastKnownGood > _options.LastKnownGoodTTL)
                {
                    // Treat current as baseline
                    state.LastKnownGoodParallelism = state.CurrentParallelism;
                    state.LastKnownGoodTimestamp = DateTime.UtcNow;
                }

                // Check if we can increase (batch count AND time elapsed)
                var canIncrease = state.SuccessesSinceThrottle >= _options.StabilizationBatches
                    && (DateTime.UtcNow - state.LastIncreaseTime) >= _options.MinIncreaseInterval;

                if (canIncrease && state.CurrentParallelism < state.MaxParallelism)
                {
                    int increase;
                    if (state.CurrentParallelism < state.LastKnownGoodParallelism)
                    {
                        // Fast recovery phase - get back to known-good quickly
                        increase = (int)(_options.IncreaseRate * _options.RecoveryMultiplier);
                        _logger.LogDebug(
                            "Connection {Connection}: Recovery phase, increasing parallelism by {Increase} (current: {Current}, target: {Target})",
                            connectionName, increase, state.CurrentParallelism, state.LastKnownGoodParallelism);
                    }
                    else
                    {
                        // Probing phase - cautiously explore above known-good
                        increase = _options.IncreaseRate;
                        _logger.LogDebug(
                            "Connection {Connection}: Probing phase, increasing parallelism by {Increase} (current: {Current})",
                            connectionName, increase, state.CurrentParallelism);
                    }

                    state.CurrentParallelism = Math.Min(state.CurrentParallelism + increase, state.MaxParallelism);
                    state.SuccessesSinceThrottle = 0;
                    state.LastIncreaseTime = DateTime.UtcNow;
                }
            }
        }

        /// <inheritdoc />
        public void RecordThrottle(string connectionName, TimeSpan retryAfter)
        {
            if (!IsEnabled)
            {
                return;
            }

            if (!_states.TryGetValue(connectionName, out var state))
            {
                return;
            }

            lock (state.SyncRoot)
            {
                state.LastActivityTime = DateTime.UtcNow;
                state.TotalThrottleEvents++;
                state.LastThrottleTime = DateTime.UtcNow;

                // Remember current level as "almost good" (we were one step too high)
                state.LastKnownGoodParallelism = Math.Max(
                    state.CurrentParallelism - _options.IncreaseRate,
                    _options.MinParallelism);
                state.LastKnownGoodTimestamp = DateTime.UtcNow;

                // Multiplicative decrease
                var newParallelism = (int)(state.CurrentParallelism * _options.DecreaseFactor);
                state.CurrentParallelism = Math.Max(newParallelism, _options.MinParallelism);
                state.SuccessesSinceThrottle = 0;

                _logger.LogInformation(
                    "Connection {Connection}: Throttle received (Retry-After: {RetryAfter}). " +
                    "Reduced parallelism from {Old} to {New}. Last known good: {LastKnownGood}",
                    connectionName,
                    retryAfter,
                    (int)(state.CurrentParallelism / _options.DecreaseFactor),
                    state.CurrentParallelism,
                    state.LastKnownGoodParallelism);
            }
        }

        /// <inheritdoc />
        public void Reset(string connectionName)
        {
            if (_states.TryGetValue(connectionName, out var state))
            {
                lock (state.SyncRoot)
                {
                    ResetStateInternal(state, state.MaxParallelism);
                }
            }
        }

        /// <inheritdoc />
        public AdaptiveRateStatistics? GetStatistics(string connectionName)
        {
            if (!_states.TryGetValue(connectionName, out var state))
            {
                return null;
            }

            lock (state.SyncRoot)
            {
                var isStale = (DateTime.UtcNow - state.LastKnownGoodTimestamp) > _options.LastKnownGoodTTL;

                return new AdaptiveRateStatistics
                {
                    ConnectionName = connectionName,
                    CurrentParallelism = state.CurrentParallelism,
                    MaxParallelism = state.MaxParallelism,
                    LastKnownGoodParallelism = state.LastKnownGoodParallelism,
                    IsLastKnownGoodStale = isStale,
                    SuccessesSinceThrottle = state.SuccessesSinceThrottle,
                    TotalThrottleEvents = state.TotalThrottleEvents,
                    LastThrottleTime = state.LastThrottleTime,
                    LastIncreaseTime = state.LastIncreaseTime,
                    LastActivityTime = state.LastActivityTime
                };
            }
        }

        private ConnectionState GetOrCreateState(string connectionName, int maxParallelism)
        {
            return _states.GetOrAdd(connectionName, _ =>
            {
                var initialParallelism = Math.Max(
                    (int)(maxParallelism * _options.InitialParallelismFactor),
                    _options.MinParallelism);

                _logger.LogDebug(
                    "Initializing adaptive rate for connection {Connection}. " +
                    "Max: {Max}, Initial: {Initial} ({Factor:P0})",
                    connectionName, maxParallelism, initialParallelism, _options.InitialParallelismFactor);

                return new ConnectionState
                {
                    MaxParallelism = maxParallelism,
                    CurrentParallelism = initialParallelism,
                    LastKnownGoodParallelism = initialParallelism,
                    LastKnownGoodTimestamp = DateTime.UtcNow,
                    SuccessesSinceThrottle = 0,
                    LastIncreaseTime = DateTime.UtcNow,
                    LastActivityTime = DateTime.UtcNow,
                    TotalThrottleEvents = 0,
                    LastThrottleTime = null
                };
            });
        }

        private void ResetStateInternal(ConnectionState state, int maxParallelism)
        {
            var initialParallelism = Math.Max(
                (int)(maxParallelism * _options.InitialParallelismFactor),
                _options.MinParallelism);

            state.MaxParallelism = maxParallelism;
            state.CurrentParallelism = initialParallelism;
            state.LastKnownGoodParallelism = initialParallelism;
            state.LastKnownGoodTimestamp = DateTime.UtcNow;
            state.SuccessesSinceThrottle = 0;
            state.LastIncreaseTime = DateTime.UtcNow;
            state.LastActivityTime = DateTime.UtcNow;
            // Note: TotalThrottleEvents is NOT reset - it's cumulative
        }

        /// <summary>
        /// Internal state for a single connection.
        /// </summary>
        private sealed class ConnectionState
        {
            public readonly object SyncRoot = new();
            public int MaxParallelism { get; set; }
            public int CurrentParallelism { get; set; }
            public int LastKnownGoodParallelism { get; set; }
            public DateTime LastKnownGoodTimestamp { get; set; }
            public int SuccessesSinceThrottle { get; set; }
            public DateTime LastIncreaseTime { get; set; }
            public DateTime LastActivityTime { get; set; }
            public int TotalThrottleEvents { get; set; }
            public DateTime? LastThrottleTime { get; set; }
        }
    }
}
