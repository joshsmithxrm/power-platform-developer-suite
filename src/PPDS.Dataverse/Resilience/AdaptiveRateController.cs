using System;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PPDS.Dataverse.DependencyInjection;

namespace PPDS.Dataverse.Resilience
{
    /// <summary>
    /// Adaptive rate controller for throttle recovery.
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
        public int GetParallelism(string connectionName, int recommendedParallelism, int connectionCount)
        {
            // Ensure connectionCount is at least 1
            connectionCount = Math.Max(1, connectionCount);

            if (!IsEnabled)
            {
                return Math.Min(recommendedParallelism * connectionCount, _options.HardCeiling * connectionCount);
            }

            // Scale floor and ceiling by connection count
            // Floor: x-ms-dop-hint × connections (e.g., 5 × 2 = 10)
            // Ceiling: HardCeiling × connections (e.g., 52 × 2 = 104)
            var floor = Math.Max(recommendedParallelism * connectionCount, _options.MinParallelism);
            var ceiling = _options.HardCeiling * connectionCount;

            var state = GetOrCreateState(connectionName, floor, ceiling);

            lock (state.SyncRoot)
            {
                // Check for idle reset
                var timeSinceActivity = DateTime.UtcNow - state.LastActivityTime;
                if (timeSinceActivity > _options.IdleResetPeriod)
                {
                    _logger.LogDebug(
                        "Connection {Connection} idle for {IdleTime}, resetting",
                        connectionName, timeSinceActivity);
                    ResetStateInternal(state, floor, ceiling);
                }

                // Floor can change dynamically - update it
                state.FloorParallelism = floor;

                // If server raised recommendation above our current, follow it
                if (state.CurrentParallelism < floor)
                {
                    _logger.LogDebug(
                        "Connection {Connection}: Floor raised to {Floor}, adjusting from {Current}",
                        connectionName, floor, state.CurrentParallelism);
                    state.CurrentParallelism = floor;
                    state.LastKnownGoodParallelism = floor;
                    state.LastKnownGoodTimestamp = DateTime.UtcNow;
                }

                state.LastActivityTime = DateTime.UtcNow;
                return state.CurrentParallelism;
            }
        }

        /// <inheritdoc />
        public void RecordSuccess(string connectionName)
        {
            if (!IsEnabled || !_states.TryGetValue(connectionName, out var state))
            {
                return;
            }

            lock (state.SyncRoot)
            {
                state.LastActivityTime = DateTime.UtcNow;
                state.SuccessesSinceThrottle++;

                // Expire stale lastKnownGood
                var timeSinceLastKnownGood = DateTime.UtcNow - state.LastKnownGoodTimestamp;
                if (timeSinceLastKnownGood > _options.LastKnownGoodTTL)
                {
                    state.LastKnownGoodParallelism = state.CurrentParallelism;
                    state.LastKnownGoodTimestamp = DateTime.UtcNow;
                }

                // Calculate effective ceiling (respect throttle ceiling if not expired)
                var effectiveCeiling = state.CeilingParallelism;
                var throttleCeilingActive = false;
                if (state.ThrottleCeilingExpiry.HasValue && state.ThrottleCeilingExpiry > DateTime.UtcNow && state.ThrottleCeiling.HasValue)
                {
                    effectiveCeiling = Math.Min(effectiveCeiling, state.ThrottleCeiling.Value);
                    throttleCeilingActive = true;
                }

                var canIncrease = state.SuccessesSinceThrottle >= _options.StabilizationBatches
                    && (DateTime.UtcNow - state.LastIncreaseTime) >= _options.MinIncreaseInterval;

                if (canIncrease && state.CurrentParallelism < effectiveCeiling)
                {
                    var oldParallelism = state.CurrentParallelism;

                    // Increment by floor (server's recommendation) for faster ramp
                    // Recovery phase uses multiplier to get back to known-good faster
                    var baseIncrease = Math.Max(state.FloorParallelism, _options.IncreaseRate);
                    var increase = state.CurrentParallelism < state.LastKnownGoodParallelism
                        ? (int)(baseIncrease * _options.RecoveryMultiplier)
                        : baseIncrease;

                    state.CurrentParallelism = Math.Min(
                        state.CurrentParallelism + increase,
                        effectiveCeiling);

                    _logger.LogDebug(
                        "Connection {Connection}: {Old} -> {New} (floor: {Floor}, ceiling: {Ceiling}{ThrottleCeilingNote})",
                        connectionName, oldParallelism, state.CurrentParallelism,
                        state.FloorParallelism, effectiveCeiling,
                        throttleCeilingActive ? $", throttle ceiling active until {state.ThrottleCeilingExpiry:HH:mm:ss}" : "");

                    state.SuccessesSinceThrottle = 0;
                    state.LastIncreaseTime = DateTime.UtcNow;
                }
            }
        }

        /// <inheritdoc />
        public void RecordThrottle(string connectionName, TimeSpan retryAfter)
        {
            if (!IsEnabled || !_states.TryGetValue(connectionName, out var state))
            {
                return;
            }

            lock (state.SyncRoot)
            {
                state.LastActivityTime = DateTime.UtcNow;
                state.TotalThrottleEvents++;
                state.LastThrottleTime = DateTime.UtcNow;

                var oldParallelism = state.CurrentParallelism;

                // Calculate throttle ceiling based on how badly we overshot
                // overshootRatio: how much of the 5-min budget we consumed
                // reductionFactor: how much to reduce ceiling (more overshoot = more reduction)
                // 5 min Retry-After → 50% ceiling, 2.5 min → 75%, 30 sec → 95%
                var overshootRatio = retryAfter.TotalMinutes / 5.0;
                var reductionFactor = 1.0 - (overshootRatio / 2.0);
                reductionFactor = Math.Max(0.5, Math.Min(1.0, reductionFactor)); // Clamp to [0.5, 1.0]

                var throttleCeiling = (int)(oldParallelism * reductionFactor);
                throttleCeiling = Math.Max(throttleCeiling, state.FloorParallelism);

                state.ThrottleCeiling = throttleCeiling;
                // Clamp duration = RetryAfter + 5 minutes (one full budget window to stabilize)
                state.ThrottleCeilingExpiry = DateTime.UtcNow + retryAfter + TimeSpan.FromMinutes(5);

                // Remember where we were (minus one step) as last known good
                state.LastKnownGoodParallelism = Math.Max(
                    state.CurrentParallelism - _options.IncreaseRate,
                    state.FloorParallelism);
                state.LastKnownGoodTimestamp = DateTime.UtcNow;

                // Multiplicative decrease, but never below floor
                var calculatedNew = (int)(state.CurrentParallelism * _options.DecreaseFactor);
                state.CurrentParallelism = Math.Max(calculatedNew, state.FloorParallelism);
                state.SuccessesSinceThrottle = 0;

                var atFloor = state.CurrentParallelism == state.FloorParallelism;
                _logger.LogInformation(
                    "Connection {Connection}: Throttle (Retry-After: {RetryAfter}). {Old} -> {New} (throttle ceiling: {ThrottleCeiling}, expires: {Expiry:HH:mm:ss}){FloorNote}",
                    connectionName, retryAfter, oldParallelism, state.CurrentParallelism,
                    throttleCeiling, state.ThrottleCeilingExpiry.Value,
                    atFloor ? " (at floor)" : "");
            }
        }

        /// <inheritdoc />
        public void Reset(string connectionName)
        {
            if (_states.TryGetValue(connectionName, out var state))
            {
                lock (state.SyncRoot)
                {
                    ResetStateInternal(state, state.FloorParallelism, state.CeilingParallelism);
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

                // Only include throttle ceiling if it's still active
                var throttleCeilingActive = state.ThrottleCeilingExpiry.HasValue &&
                                            state.ThrottleCeilingExpiry > DateTime.UtcNow &&
                                            state.ThrottleCeiling.HasValue;

                return new AdaptiveRateStatistics
                {
                    ConnectionName = connectionName,
                    CurrentParallelism = state.CurrentParallelism,
                    FloorParallelism = state.FloorParallelism,
                    CeilingParallelism = state.CeilingParallelism,
                    ThrottleCeiling = throttleCeilingActive ? state.ThrottleCeiling : null,
                    ThrottleCeilingExpiry = throttleCeilingActive ? state.ThrottleCeilingExpiry : null,
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

        private ConnectionState GetOrCreateState(string connectionName, int floor, int ceiling)
        {
            return _states.GetOrAdd(connectionName, _ =>
            {
                _logger.LogInformation(
                    "Adaptive rate initialized for {Connection}. Floor: {Floor}, Ceiling: {Ceiling}",
                    connectionName, floor, ceiling);

                return new ConnectionState
                {
                    FloorParallelism = floor,
                    CeilingParallelism = ceiling,
                    CurrentParallelism = floor,
                    LastKnownGoodParallelism = floor,
                    LastKnownGoodTimestamp = DateTime.UtcNow,
                    SuccessesSinceThrottle = 0,
                    LastIncreaseTime = DateTime.UtcNow,
                    LastActivityTime = DateTime.UtcNow,
                    TotalThrottleEvents = 0,
                    LastThrottleTime = null
                };
            });
        }

        private void ResetStateInternal(ConnectionState state, int floor, int ceiling)
        {
            state.FloorParallelism = floor;
            state.CeilingParallelism = ceiling;
            state.CurrentParallelism = floor;
            state.LastKnownGoodParallelism = floor;
            state.LastKnownGoodTimestamp = DateTime.UtcNow;
            state.SuccessesSinceThrottle = 0;
            state.LastIncreaseTime = DateTime.UtcNow;
            state.LastActivityTime = DateTime.UtcNow;
            state.ThrottleCeiling = null;
            state.ThrottleCeilingExpiry = null;
        }

        private sealed class ConnectionState
        {
            public readonly object SyncRoot = new();
            public int FloorParallelism { get; set; }
            public int CeilingParallelism { get; set; }
            public int CurrentParallelism { get; set; }
            public int LastKnownGoodParallelism { get; set; }
            public DateTime LastKnownGoodTimestamp { get; set; }
            public int SuccessesSinceThrottle { get; set; }
            public DateTime LastIncreaseTime { get; set; }
            public DateTime LastActivityTime { get; set; }
            public int TotalThrottleEvents { get; set; }
            public DateTime? LastThrottleTime { get; set; }

            /// <summary>
            /// Throttle-derived ceiling calculated from Retry-After duration.
            /// Used to prevent probing above a level that caused throttling.
            /// </summary>
            public int? ThrottleCeiling { get; set; }

            /// <summary>
            /// When the throttle ceiling expires (RetryAfter + 5 minutes).
            /// After expiry, probing can resume up to the hard ceiling.
            /// </summary>
            public DateTime? ThrottleCeilingExpiry { get; set; }
        }
    }
}
