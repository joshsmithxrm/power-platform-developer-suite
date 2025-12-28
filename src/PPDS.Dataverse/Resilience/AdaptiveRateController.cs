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

            LogEffectiveConfiguration();
        }

        private void LogEffectiveConfiguration()
        {
            if (!_options.Enabled)
            {
                _logger.LogInformation("Adaptive rate control: Disabled");
                return;
            }

            // Log effective configuration with override indicators
            // This helps operators verify their config is applied correctly
            _logger.LogInformation(
                "Adaptive rate control: Preset={Preset}, ExecTimeFactor={ExecTimeFactor}, RequestRateFactor={RequestRateFactor}, " +
                "SlowThreshold={Threshold}ms, DecreaseFactor={DecreaseFactor}, Stabilization={Stabilization}, Interval={Interval}s",
                _options.Preset,
                AdaptiveRateOptions.FormatValue(_options.ExecutionTimeCeilingFactor, _options.IsExecutionTimeCeilingFactorOverridden),
                AdaptiveRateOptions.FormatValue(_options.RequestRateCeilingFactor, _options.IsRequestRateCeilingFactorOverridden),
                AdaptiveRateOptions.FormatValue(_options.SlowBatchThresholdMs, _options.IsSlowBatchThresholdMsOverridden),
                AdaptiveRateOptions.FormatValue(_options.DecreaseFactor, _options.IsDecreaseFactorOverridden),
                AdaptiveRateOptions.FormatValue(_options.StabilizationBatches, _options.IsStabilizationBatchesOverridden),
                AdaptiveRateOptions.FormatValue(_options.MinIncreaseInterval.TotalSeconds, _options.IsMinIncreaseIntervalOverridden));
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

                // Calculate effective ceiling (minimum of all applicable ceilings)
                var effectiveCeiling = state.CeilingParallelism;
                var throttleCeilingActive = false;
                var execTimeCeilingActive = false;
                var requestRateCeilingActive = false;

                if (state.ThrottleCeilingExpiry.HasValue && state.ThrottleCeilingExpiry > DateTime.UtcNow && state.ThrottleCeiling.HasValue)
                {
                    effectiveCeiling = Math.Min(effectiveCeiling, state.ThrottleCeiling.Value);
                    throttleCeilingActive = true;
                }

                // Request rate ceiling: Always applied when available
                // Protects fast operations from hitting the 6,000 requests/5-min limit
                if (state.RequestRateCeiling.HasValue)
                {
                    effectiveCeiling = Math.Min(effectiveCeiling, state.RequestRateCeiling.Value);
                    requestRateCeilingActive = state.RequestRateCeiling.Value < state.CeilingParallelism;
                }

                // Execution time ceiling: Only apply for slow batches
                // Protects slow operations from exhausting the 20-min execution time budget
                if (state.ExecutionTimeCeiling.HasValue &&
                    state.BatchDurationEmaMs.HasValue &&
                    state.BatchDurationEmaMs.Value >= _options.SlowBatchThresholdMs)
                {
                    effectiveCeiling = Math.Min(effectiveCeiling, state.ExecutionTimeCeiling.Value);
                    execTimeCeilingActive = state.ExecutionTimeCeiling.Value < state.CeilingParallelism;
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

                    // Build ceiling note for logging
                    var ceilingNotes = new System.Collections.Generic.List<string>();
                    if (throttleCeilingActive)
                        ceilingNotes.Add($"throttle ceiling until {state.ThrottleCeilingExpiry:HH:mm:ss}");
                    if (requestRateCeilingActive)
                        ceilingNotes.Add($"request rate ceiling {state.RequestRateCeiling}");
                    if (execTimeCeilingActive)
                        ceilingNotes.Add($"exec time ceiling {state.ExecutionTimeCeiling}");

                    _logger.LogDebug(
                        "Connection {Connection}: {Old} -> {New} (floor: {Floor}, ceiling: {Ceiling}{CeilingNote})",
                        connectionName, oldParallelism, state.CurrentParallelism,
                        state.FloorParallelism, effectiveCeiling,
                        ceilingNotes.Count > 0 ? $", {string.Join(", ", ceilingNotes)}" : "");

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

                // Use the higher of current parallelism or existing throttle ceiling as the base.
                // This prevents rapid throttle cascades from dropping the ceiling too aggressively -
                // if we already have a ceiling of 29 from the first throttle, subsequent rapid
                // throttles shouldn't keep lowering it just because parallelism has dropped.
                var ceilingBase = state.ThrottleCeiling.HasValue
                    ? Math.Max(oldParallelism, state.ThrottleCeiling.Value)
                    : oldParallelism;

                var throttleCeiling = (int)(ceilingBase * reductionFactor);
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
        public void RecordBatchDuration(string connectionName, TimeSpan duration)
        {
            if (!IsEnabled || !_options.ExecutionTimeCeilingEnabled)
            {
                return;
            }

            if (!_states.TryGetValue(connectionName, out var state))
            {
                return;
            }

            lock (state.SyncRoot)
            {
                var durationMs = duration.TotalMilliseconds;

                // Update EMA of batch duration
                if (state.BatchDurationEmaMs.HasValue)
                {
                    // EMA: new = alpha * current + (1 - alpha) * previous
                    var alpha = _options.BatchDurationSmoothingFactor;
                    state.BatchDurationEmaMs = alpha * durationMs + (1 - alpha) * state.BatchDurationEmaMs.Value;
                }
                else
                {
                    // First sample - use it directly
                    state.BatchDurationEmaMs = durationMs;
                }

                state.BatchDurationSampleCount++;

                // Calculate ceilings once we have enough samples
                if (state.BatchDurationSampleCount >= _options.MinBatchSamplesForCeiling)
                {
                    var avgBatchSeconds = state.BatchDurationEmaMs.Value / 1000.0;

                    // Execution time ceiling: Factor / batchDuration
                    // Protects slow operations from exhausting the 20-minute execution time budget
                    // Lower batch duration = higher ceiling (fast ops don't need this protection)
                    var execTimeCeiling = (int)(_options.ExecutionTimeCeilingFactor / avgBatchSeconds);
                    execTimeCeiling = Math.Max(state.FloorParallelism, Math.Min(execTimeCeiling, state.CeilingParallelism));

                    // Request rate ceiling: Factor × batchDuration
                    // Protects fast operations from exhausting the 6,000 requests/5-min budget
                    // Lower batch duration = lower ceiling (fast ops complete more requests/sec)
                    var requestRateCeiling = (int)(_options.RequestRateCeilingFactor * avgBatchSeconds);
                    requestRateCeiling = Math.Max(state.FloorParallelism, Math.Min(requestRateCeiling, state.CeilingParallelism));

                    // Log when either ceiling changes significantly
                    var execTimeChanged = !state.ExecutionTimeCeiling.HasValue ||
                        Math.Abs(execTimeCeiling - state.ExecutionTimeCeiling.Value) >= 2;
                    var requestRateChanged = !state.RequestRateCeiling.HasValue ||
                        Math.Abs(requestRateCeiling - state.RequestRateCeiling.Value) >= 2;

                    if (execTimeChanged || requestRateChanged)
                    {
                        _logger.LogDebug(
                            "Connection {Connection}: Ceilings updated (avg batch: {AvgBatch:F1}s, samples: {Samples}) - " +
                            "exec time: {ExecCeiling}, request rate: {RateCeiling}",
                            connectionName, avgBatchSeconds, state.BatchDurationSampleCount,
                            execTimeCeiling, requestRateCeiling);
                    }

                    state.ExecutionTimeCeiling = execTimeCeiling;
                    state.RequestRateCeiling = requestRateCeiling;
                }
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
                    // Full reset also clears batch duration tracking and derived ceilings
                    state.BatchDurationEmaMs = null;
                    state.BatchDurationSampleCount = 0;
                    state.ExecutionTimeCeiling = null;
                    state.RequestRateCeiling = null;
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
                    ExecutionTimeCeiling = state.ExecutionTimeCeiling,
                    RequestRateCeiling = state.RequestRateCeiling,
                    AverageBatchDuration = state.BatchDurationEmaMs.HasValue
                        ? TimeSpan.FromMilliseconds(state.BatchDurationEmaMs.Value)
                        : null,
                    BatchDurationSampleCount = state.BatchDurationSampleCount,
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
            // Note: We intentionally do NOT reset batch duration tracking on idle reset.
            // The execution time ceiling should persist across idle periods as it reflects
            // the operation's inherent characteristics, not transient throttle state.
            // Only a full Reset() call should clear these.
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

            /// <summary>
            /// Exponential moving average of batch durations in milliseconds.
            /// Used to calculate execution time ceiling.
            /// </summary>
            public double? BatchDurationEmaMs { get; set; }

            /// <summary>
            /// Number of batch duration samples collected.
            /// </summary>
            public int BatchDurationSampleCount { get; set; }

            /// <summary>
            /// Execution time-based ceiling calculated from batch durations.
            /// Protects slow operations from exhausting execution time budget.
            /// </summary>
            public int? ExecutionTimeCeiling { get; set; }

            /// <summary>
            /// Request rate-based ceiling calculated from batch durations.
            /// Protects fast operations from exhausting request count budget.
            /// </summary>
            public int? RequestRateCeiling { get; set; }
        }
    }
}
