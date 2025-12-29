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

        // Throttle debouncing: when 52 requests all 429 simultaneously,
        // we only want to process the first throttle, not cascade 52 times
        private DateTime? _lastThrottleProcessed;
        private static readonly TimeSpan ThrottleDebounceWindow = TimeSpan.FromSeconds(2);

        // Request rate tracking: batches per second (EMA-based)
        private double? _batchRateEma;
        private DateTime? _lastBatchTime;

        // Batch duration tracking for execution time and request rate ceilings
        private double? _batchDurationEmaMs;
        private double? _minimumBatchDurationMs; // Fastest observed batch (for request rate ceiling)
        private int _batchDurationSampleCount;
        private int? _executionTimeCeiling;
        private int? _requestRateCeiling;

        // Ramp-up protection: track total successful batches and first throttle
        private int _totalSuccessfulBatches;
        private bool _hasHadFirstThrottle;

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
                var now = DateTime.UtcNow;
                _lastActivityTime = now;
                _batchesSinceThrottle++;

                // Update batch rate EMA (batches per second)
                if (_lastBatchTime.HasValue)
                {
                    var timeSinceLastBatch = (now - _lastBatchTime.Value).TotalSeconds;
                    if (timeSinceLastBatch > 0)
                    {
                        var instantRate = 1.0 / timeSinceLastBatch;
                        if (_batchRateEma.HasValue)
                        {
                            // EMA with same smoothing factor as batch duration
                            var alpha = _options.BatchDurationSmoothingFactor;
                            _batchRateEma = (alpha * instantRate) + ((1 - alpha) * _batchRateEma.Value);
                        }
                        else
                        {
                            _batchRateEma = instantRate;
                        }
                    }
                }
                _lastBatchTime = now;

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

                // Track minimum batch duration (fastest observed) for request rate ceiling
                // Using minimum prevents feedback loop: slow batches → higher ceiling → more load → slower batches
                if (!_minimumBatchDurationMs.HasValue || durationMs < _minimumBatchDurationMs.Value)
                {
                    _minimumBatchDurationMs = durationMs;
                }

                _batchDurationSampleCount++;
                _totalSuccessfulBatches++;

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
                var now = DateTime.UtcNow;

                // Always count throttle events for statistics
                _totalThrottleEvents++;
                _lastThrottleTime = now;
                _lastActivityTime = now;

                // Throttle debouncing: when 52 requests all 429 simultaneously,
                // only process the first one to avoid cascade (52->26->13->6->4 in <1s)
                if (_lastThrottleProcessed.HasValue &&
                    (now - _lastThrottleProcessed.Value) < ThrottleDebounceWindow)
                {
                    _logger.LogDebug(
                        "Throttle debounced ({TimeSinceLast:F1}s < {Window:F1}s window). " +
                        "Total throttles in burst: {Total}",
                        (now - _lastThrottleProcessed.Value).TotalSeconds,
                        ThrottleDebounceWindow.TotalSeconds,
                        _totalThrottleEvents);
                    return;
                }

                _lastThrottleProcessed = now;
                _hasHadFirstThrottle = true;
                var oldParallelism = _currentParallelism;
                var atFloor = oldParallelism == _floorParallelism;

                // Remember where we were (minus one step) as last known good
                _lastKnownGoodParallelism = Math.Max(
                    _currentParallelism - _options.IncreaseRate,
                    _floorParallelism);
                _lastKnownGoodTime = now;

                // Floor protection: don't reduce throttle ceiling when already at floor.
                // The throttle ceiling dropping to 4 when parallelism is already 4 serves no purpose.
                if (!atFloor)
                {
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
                    _throttleCeilingExpiry = now + retryAfter + TimeSpan.FromMinutes(5);
                }

                // AIMD: Multiplicative decrease
                var calculatedNew = (int)(oldParallelism * _options.DecreaseFactor);
                _currentParallelism = Math.Max(calculatedNew, _floorParallelism);
                _batchesSinceThrottle = 0;

                // Build rate info for logging
                var rateInfo = _batchRateEma.HasValue
                    ? $", rate: {_batchRateEma.Value:F1} batch/s"
                    : "";

                _logger.LogInformation(
                    "Throttle (Retry-After: {RetryAfter}). {Old} -> {New} (throttle ceiling: {ThrottleCeiling}, expires: {Expiry:HH:mm:ss}{RateInfo}){FloorNote}",
                    retryAfter, oldParallelism, _currentParallelism,
                    _throttleCeiling ?? _floorParallelism,
                    _throttleCeilingExpiry ?? now.AddMinutes(5),
                    rateInfo,
                    atFloor ? " (at floor, ceiling unchanged)" : "");
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
                    MinimumBatchDuration = _minimumBatchDurationMs.HasValue
                        ? TimeSpan.FromMilliseconds(_minimumBatchDurationMs.Value)
                        : null,
                    BatchDurationSampleCount = _batchDurationSampleCount,
                    TotalSuccessfulBatches = _totalSuccessfulBatches,
                    HasHadFirstThrottle = _hasHadFirstThrottle,
                    BatchesPerSecond = _batchRateEma,
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
            _minimumBatchDurationMs = null;
            _batchDurationSampleCount = 0;
            _executionTimeCeiling = null;
            _requestRateCeiling = null;

            // Clear ramp-up protection state
            _totalSuccessfulBatches = 0;
            _hasHadFirstThrottle = false;

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
            _lastThrottleProcessed = null;
            _batchDurationEmaMs = null;
            _minimumBatchDurationMs = null;
            _batchDurationSampleCount = 0;
            _executionTimeCeiling = null;
            _requestRateCeiling = null;
            _batchRateEma = null;
            _lastBatchTime = null;
            _totalSuccessfulBatches = 0;
            _hasHadFirstThrottle = false;
        }

        private void UpdateCeilings()
        {
            if (!_batchDurationEmaMs.HasValue || !_options.ExecutionTimeCeilingEnabled)
            {
                return;
            }

            var avgBatchSeconds = _batchDurationEmaMs.Value / 1000.0;

            // Execution time ceiling: Factor / batchDuration (uses EMA - slow batches need this)
            // Protects slow operations from exhausting the 20-minute execution time budget
            // Lower batch duration = higher ceiling (fast ops don't need this protection)
            var execTimeCeiling = (int)(_options.ExecutionTimeCeilingFactor / avgBatchSeconds);
            execTimeCeiling = Math.Max(_floorParallelism, Math.Min(execTimeCeiling, _ceilingParallelism));

            // Request rate ceiling: Factor × batchDuration (uses MINIMUM - fastest observed)
            // Using minimum prevents feedback loop: slow batches → higher ceiling → more load → slower batches
            // The fastest batch represents the server's true capability without contention
            var minBatchSeconds = (_minimumBatchDurationMs ?? _batchDurationEmaMs.Value) / 1000.0;
            var requestRateCeiling = (int)(_options.RequestRateCeilingFactor * minBatchSeconds);
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

            // Hard request rate cap: 6000 requests per 5 min = 20/sec
            // Stop increasing if we're already at 90% of this limit (18 batch/sec)
            // This is a safety valve regardless of other ceiling calculations
            // Only apply when rate is in realistic range (0.1 to 100 batch/sec)
            // Higher rates indicate measurement artifacts (e.g., tests completing batches instantly)
            if (_batchRateEma.HasValue &&
                _batchRateEma.Value >= _options.HardRequestRateCapBatchesPerSecond &&
                _batchRateEma.Value < 100.0) // Skip if rate is unrealistically high
            {
                return;
            }

            // Calculate effective ceiling (minimum of all applicable ceilings)
            var effectiveCeiling = _ceilingParallelism;
            var throttleCeilingActive = false;
            var execTimeCeilingActive = false;
            var requestRateCeilingActive = false;
            var initialCeilingActive = false;

            // Conservative initial ceiling: Before we have enough samples, cap at 20
            // Prevents aggressive ramp-up when we don't yet know server behavior
            if (_batchDurationSampleCount < _options.MinBatchSamplesForCeiling)
            {
                effectiveCeiling = Math.Min(effectiveCeiling, _options.InitialCeilingBeforeSamples * _connectionCount);
                initialCeilingActive = true;
            }

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

            // Slower initial ramp-up: Only use floor as base increase after we've
            // had first throttle OR after 30+ successful batches (proven stable)
            // Before that, use small fixed IncreaseRate to avoid aggressive ramp
            var useAggressiveRamp = _hasHadFirstThrottle ||
                _totalSuccessfulBatches >= _options.MinBatchesForAggressiveRamp;

            var baseIncrease = useAggressiveRamp
                ? Math.Max(_floorParallelism, _options.IncreaseRate)
                : _options.IncreaseRate;

            // Recovery phase uses multiplier to get back to known-good faster
            // But only if we're using aggressive ramp (i.e., after first throttle or stable)
            var increase = useAggressiveRamp && _currentParallelism < _lastKnownGoodParallelism
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
                if (initialCeilingActive)
                    ceilingNotes.Add($"initial ceiling {_options.InitialCeilingBeforeSamples * _connectionCount}");
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
