using System;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PPDS.Dataverse.DependencyInjection;

namespace PPDS.Dataverse.Resilience
{
    /// <summary>
    /// Pool-level adaptive rate controller implementing AIMD (Additive Increase, Multiplicative Decrease).
    /// Manages total parallelism across all connections based on throughput and throttle responses.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This controller operates at the pool level, tracking aggregate throughput across all connections.
    /// While Dataverse enforces limits per-user (per app registration), tracking at the pool level
    /// ensures all work is accounted for in rate decisions.
    /// </para>
    /// <para>
    /// The ceiling is scaled by connection count (e.g., 2 app registrations = 2× the ceiling),
    /// reflecting the multiplied API quota available with multiple users.
    /// </para>
    /// </remarks>
    public sealed class AdaptiveRateController : IAdaptiveRateController
    {
        private readonly AdaptiveRateOptions _options;
        private readonly ILogger<AdaptiveRateController> _logger;
        private readonly object _syncRoot = new();

        // Pool state
        private int _currentParallelism;
        private int _floorParallelism;
        private int _ceilingParallelism;
        private int _connectionCount;
        private int _lastKnownGoodParallelism;
        private DateTime _lastKnownGoodTime;
        private int _batchesSinceThrottle;
        private int _totalThrottleEvents;
        private DateTime? _lastThrottleTime;
        private DateTime? _lastIncreaseTime;
        private DateTime? _lastActivityTime;

        // Throttle ceiling (reduced after throttle, expires over time)
        private int? _throttleCeiling;
        private DateTime? _throttleCeilingExpiry;

        // Batch duration tracking for execution time and request rate ceilings
        private double? _batchDurationEmaMs;
        private int _batchDurationSampleCount;
        private int? _executionTimeCeiling;
        private int? _requestRateCeiling;

        private bool _initialized;

        /// <summary>
        /// Initializes a new instance of the <see cref="AdaptiveRateController"/> class.
        /// </summary>
        public AdaptiveRateController(
            IOptions<DataverseOptions> options,
            ILogger<AdaptiveRateController> logger)
        {
            _options = options?.Value?.AdaptiveRate ?? new AdaptiveRateOptions();
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            LogEffectiveConfiguration();
        }

        /// <inheritdoc />
        public bool IsEnabled => _options.Enabled;

        /// <inheritdoc />
        public int GetParallelism(int recommendedPerConnection, int connectionCount)
        {
            // Ensure connectionCount is at least 1
            connectionCount = Math.Max(1, connectionCount);

            if (!IsEnabled)
            {
                return Math.Min(
                    recommendedPerConnection * connectionCount,
                    _options.HardCeiling * connectionCount);
            }

            lock (_syncRoot)
            {
                // Initialize or reinitialize if connection count changes
                if (!_initialized || connectionCount != _connectionCount)
                {
                    Initialize(recommendedPerConnection, connectionCount);
                }

                // Check for idle reset
                if (_lastActivityTime.HasValue)
                {
                    var timeSinceActivity = DateTime.UtcNow - _lastActivityTime.Value;
                    if (timeSinceActivity > _options.IdleResetPeriod)
                    {
                        _logger.LogDebug("Pool idle for {IdleTime}, resetting rate controller", timeSinceActivity);
                        Initialize(recommendedPerConnection, connectionCount);
                    }
                }

                _lastActivityTime = DateTime.UtcNow;
                return _currentParallelism;
            }
        }

        /// <inheritdoc />
        public void RecordBatchCompletion(TimeSpan duration)
        {
            if (!IsEnabled || !_initialized)
            {
                return;
            }

            lock (_syncRoot)
            {
                _lastActivityTime = DateTime.UtcNow;
                _batchesSinceThrottle++;

                // Update batch duration EMA
                var durationMs = duration.TotalMilliseconds;
                if (_batchDurationEmaMs.HasValue)
                {
                    var alpha = _options.BatchDurationSmoothingFactor;
                    _batchDurationEmaMs = (alpha * durationMs) + ((1 - alpha) * _batchDurationEmaMs.Value);
                }
                else
                {
                    _batchDurationEmaMs = durationMs;
                }
                _batchDurationSampleCount++;

                // Calculate ceilings once we have enough samples
                if (_batchDurationSampleCount >= _options.MinBatchSamplesForCeiling)
                {
                    UpdateCeilings();
                }

                // Expire stale lastKnownGood
                var timeSinceLastKnownGood = DateTime.UtcNow - _lastKnownGoodTime;
                if (timeSinceLastKnownGood > _options.LastKnownGoodTTL)
                {
                    _lastKnownGoodParallelism = _currentParallelism;
                    _lastKnownGoodTime = DateTime.UtcNow;
                }

                // Check if we should increase parallelism
                TryIncreaseParallelism();
            }
        }

        /// <inheritdoc />
        public void RecordThrottle(TimeSpan retryAfter)
        {
            if (!IsEnabled || !_initialized)
            {
                return;
            }

            lock (_syncRoot)
            {
                var oldParallelism = _currentParallelism;
                _totalThrottleEvents++;
                _lastThrottleTime = DateTime.UtcNow;
                _lastActivityTime = DateTime.UtcNow;

                // Remember where we were (minus one step) as last known good
                _lastKnownGoodParallelism = Math.Max(
                    _currentParallelism - _options.IncreaseRate,
                    _floorParallelism);
                _lastKnownGoodTime = DateTime.UtcNow;

                // Calculate throttle ceiling based on how badly we overshot
                // overshootRatio: how much of the 5-min budget we consumed
                // reductionFactor: how much to reduce ceiling (more overshoot = more reduction)
                var overshootRatio = retryAfter.TotalMinutes / 5.0;
                var reductionFactor = 1.0 - (overshootRatio / 2.0);
                reductionFactor = Math.Max(0.5, Math.Min(1.0, reductionFactor)); // Clamp to [0.5, 1.0]

                // Use the higher of current parallelism or existing throttle ceiling as base
                var ceilingBase = _throttleCeiling.HasValue
                    ? Math.Max(oldParallelism, _throttleCeiling.Value)
                    : oldParallelism;

                var newThrottleCeiling = (int)(ceilingBase * reductionFactor);
                newThrottleCeiling = Math.Max(newThrottleCeiling, _floorParallelism);

                _throttleCeiling = newThrottleCeiling;
                _throttleCeilingExpiry = DateTime.UtcNow + retryAfter + TimeSpan.FromMinutes(5);

                // AIMD: Multiplicative decrease
                var calculatedNew = (int)(oldParallelism * _options.DecreaseFactor);
                _currentParallelism = Math.Max(calculatedNew, _floorParallelism);
                _batchesSinceThrottle = 0;

                var atFloor = _currentParallelism == _floorParallelism;
                _logger.LogInformation(
                    "Throttle (Retry-After: {RetryAfter}). {Old} -> {New} (throttle ceiling: {ThrottleCeiling}, expires: {Expiry:HH:mm:ss}){FloorNote}",
                    retryAfter, oldParallelism, _currentParallelism,
                    newThrottleCeiling, _throttleCeilingExpiry.Value,
                    atFloor ? " (at floor)" : "");
            }
        }

        /// <inheritdoc />
        public void Reset()
        {
            lock (_syncRoot)
            {
                var totalThrottles = _totalThrottleEvents;
                ResetState();
                _totalThrottleEvents = totalThrottles; // Preserve total count
                _initialized = false;

                _logger.LogDebug("Rate controller reset. Total throttle events preserved: {Total}", totalThrottles);
            }
        }

        /// <inheritdoc />
        public AdaptiveRateStatistics GetStatistics()
        {
            lock (_syncRoot)
            {
                var throttleCeilingActive = _throttleCeilingExpiry.HasValue &&
                    _throttleCeilingExpiry > DateTime.UtcNow;
                var isStale = (DateTime.UtcNow - _lastKnownGoodTime) > _options.LastKnownGoodTTL;

                return new AdaptiveRateStatistics
                {
                    CurrentParallelism = _currentParallelism,
                    FloorParallelism = _floorParallelism,
                    CeilingParallelism = _ceilingParallelism,
                    ConnectionCount = _connectionCount,
                    ThrottleCeiling = throttleCeilingActive ? _throttleCeiling : null,
                    ThrottleCeilingExpiry = throttleCeilingActive ? _throttleCeilingExpiry : null,
                    ExecutionTimeCeiling = _executionTimeCeiling,
                    RequestRateCeiling = _requestRateCeiling,
                    AverageBatchDuration = _batchDurationEmaMs.HasValue
                        ? TimeSpan.FromMilliseconds(_batchDurationEmaMs.Value)
                        : null,
                    BatchDurationSampleCount = _batchDurationSampleCount,
                    LastKnownGoodParallelism = _lastKnownGoodParallelism,
                    IsLastKnownGoodStale = isStale,
                    BatchesSinceThrottle = _batchesSinceThrottle,
                    TotalThrottleEvents = _totalThrottleEvents,
                    LastThrottleTime = _lastThrottleTime,
                    LastIncreaseTime = _lastIncreaseTime,
                    LastActivityTime = _lastActivityTime
                };
            }
        }

        private void Initialize(int recommendedPerConnection, int connectionCount)
        {
            _connectionCount = connectionCount;

            // Scale floor and ceiling by connection count
            // Floor: x-ms-dop-hint × connections (e.g., 4 × 2 = 8)
            // Ceiling: HardCeiling × connections (e.g., 52 × 2 = 104)
            _floorParallelism = Math.Max(recommendedPerConnection * connectionCount, _options.MinParallelism);
            _ceilingParallelism = _options.HardCeiling * connectionCount;

            // Start at floor
            _currentParallelism = _floorParallelism;
            _lastKnownGoodParallelism = _floorParallelism;
            _lastKnownGoodTime = DateTime.UtcNow;
            _batchesSinceThrottle = 0;
            _lastIncreaseTime = null;
            _lastActivityTime = DateTime.UtcNow;

            // Clear throttle ceiling (new operation)
            _throttleCeiling = null;
            _throttleCeilingExpiry = null;

            // Clear batch duration tracking (new operation, different entity characteristics)
            _batchDurationEmaMs = null;
            _batchDurationSampleCount = 0;
            _executionTimeCeiling = null;
            _requestRateCeiling = null;

            _initialized = true;

            _logger.LogInformation(
                "Adaptive rate initialized. Floor: {Floor}, Ceiling: {Ceiling}, Connections: {Connections}",
                _floorParallelism, _ceilingParallelism, connectionCount);
        }

        private void ResetState()
        {
            _currentParallelism = 0;
            _floorParallelism = 0;
            _ceilingParallelism = 0;
            _connectionCount = 0;
            _lastKnownGoodParallelism = 0;
            _lastKnownGoodTime = DateTime.MinValue;
            _batchesSinceThrottle = 0;
            _lastThrottleTime = null;
            _lastIncreaseTime = null;
            _lastActivityTime = null;
            _throttleCeiling = null;
            _throttleCeilingExpiry = null;
            _batchDurationEmaMs = null;
            _batchDurationSampleCount = 0;
            _executionTimeCeiling = null;
            _requestRateCeiling = null;
        }

        private void UpdateCeilings()
        {
            if (!_batchDurationEmaMs.HasValue || !_options.ExecutionTimeCeilingEnabled)
            {
                return;
            }

            var avgBatchSeconds = _batchDurationEmaMs.Value / 1000.0;

            // Execution time ceiling: Factor / batchDuration
            // Protects slow operations from exhausting the 20-minute execution time budget
            // Lower batch duration = higher ceiling (fast ops don't need this protection)
            var execTimeCeiling = (int)(_options.ExecutionTimeCeilingFactor / avgBatchSeconds);
            execTimeCeiling = Math.Max(_floorParallelism, Math.Min(execTimeCeiling, _ceilingParallelism));

            // Request rate ceiling: Factor × batchDuration
            // Protects fast operations from exhausting the 6,000 requests/5-min budget
            // Lower batch duration = lower ceiling (fast ops complete more requests/sec)
            var requestRateCeiling = (int)(_options.RequestRateCeilingFactor * avgBatchSeconds);
            requestRateCeiling = Math.Max(_floorParallelism, Math.Min(requestRateCeiling, _ceilingParallelism));

            // Log when either ceiling changes significantly
            var execTimeChanged = !_executionTimeCeiling.HasValue ||
                Math.Abs(execTimeCeiling - _executionTimeCeiling.Value) >= 2;
            var requestRateChanged = !_requestRateCeiling.HasValue ||
                Math.Abs(requestRateCeiling - _requestRateCeiling.Value) >= 2;

            if (execTimeChanged || requestRateChanged)
            {
                _logger.LogDebug(
                    "Ceilings updated (avg batch: {AvgBatch:F1}s, samples: {Samples}) - " +
                    "exec time: {ExecCeiling}, request rate: {RateCeiling}",
                    avgBatchSeconds, _batchDurationSampleCount,
                    execTimeCeiling, requestRateCeiling);
            }

            _executionTimeCeiling = execTimeCeiling;
            _requestRateCeiling = requestRateCeiling;
        }

        private void TryIncreaseParallelism()
        {
            // Check stabilization requirement
            if (_batchesSinceThrottle < _options.StabilizationBatches)
            {
                return;
            }

            // Check minimum interval since last increase
            if (_lastIncreaseTime.HasValue &&
                (DateTime.UtcNow - _lastIncreaseTime.Value) < _options.MinIncreaseInterval)
            {
                return;
            }

            // Calculate effective ceiling (minimum of all applicable ceilings)
            var effectiveCeiling = _ceilingParallelism;
            var throttleCeilingActive = false;
            var execTimeCeilingActive = false;
            var requestRateCeilingActive = false;

            if (_throttleCeilingExpiry.HasValue && _throttleCeilingExpiry > DateTime.UtcNow && _throttleCeiling.HasValue)
            {
                effectiveCeiling = Math.Min(effectiveCeiling, _throttleCeiling.Value);
                throttleCeilingActive = true;
            }

            // Request rate ceiling: Always applied when available
            if (_requestRateCeiling.HasValue)
            {
                effectiveCeiling = Math.Min(effectiveCeiling, _requestRateCeiling.Value);
                requestRateCeilingActive = _requestRateCeiling.Value < _ceilingParallelism;
            }

            // Execution time ceiling: Only apply for slow batches
            if (_executionTimeCeiling.HasValue &&
                _batchDurationEmaMs.HasValue &&
                _batchDurationEmaMs.Value >= _options.SlowBatchThresholdMs)
            {
                effectiveCeiling = Math.Min(effectiveCeiling, _executionTimeCeiling.Value);
                execTimeCeilingActive = _executionTimeCeiling.Value < _ceilingParallelism;
            }

            // Already at ceiling?
            if (_currentParallelism >= effectiveCeiling)
            {
                return;
            }

            // AIMD: Additive increase
            var oldParallelism = _currentParallelism;

            // Increment by floor (server's recommendation) for faster ramp
            // Recovery phase uses multiplier to get back to known-good faster
            var baseIncrease = Math.Max(_floorParallelism, _options.IncreaseRate);
            var increase = _currentParallelism < _lastKnownGoodParallelism
                ? (int)(baseIncrease * _options.RecoveryMultiplier)
                : baseIncrease;

            var newParallelism = Math.Min(oldParallelism + increase, effectiveCeiling);

            if (newParallelism > oldParallelism)
            {
                _currentParallelism = newParallelism;
                _lastIncreaseTime = DateTime.UtcNow;
                _batchesSinceThrottle = 0;

                // Build ceiling note for logging
                var ceilingNotes = new System.Collections.Generic.List<string>();
                if (throttleCeilingActive)
                    ceilingNotes.Add($"throttle ceiling until {_throttleCeilingExpiry:HH:mm:ss}");
                if (requestRateCeilingActive)
                    ceilingNotes.Add($"request rate ceiling {_requestRateCeiling}");
                if (execTimeCeilingActive)
                    ceilingNotes.Add($"exec time ceiling {_executionTimeCeiling}");

                var ceilingNote = ceilingNotes.Count > 0
                    ? $", {string.Join(", ", ceilingNotes)}"
                    : "";

                _logger.LogDebug(
                    "{Old} -> {New} (floor: {Floor}, ceiling: {Ceiling}{CeilingNote})",
                    oldParallelism, newParallelism, _floorParallelism, effectiveCeiling, ceilingNote);
            }
        }

        private void LogEffectiveConfiguration()
        {
            if (!_options.Enabled)
            {
                _logger.LogInformation("Adaptive rate control: Disabled");
                return;
            }

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
    }
}
