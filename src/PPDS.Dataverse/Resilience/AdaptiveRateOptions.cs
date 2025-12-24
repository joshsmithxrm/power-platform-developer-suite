using System;
using System.Collections.Generic;

namespace PPDS.Dataverse.Resilience
{
    /// <summary>
    /// Configuration options for adaptive rate control.
    /// Use <see cref="Preset"/> for quick configuration, or set individual properties for fine-tuning.
    /// </summary>
    public class AdaptiveRateOptions
    {
        #region Preset Support

        /// <summary>
        /// Gets or sets the rate control preset.
        /// Presets provide sensible defaults for common scenarios.
        /// Individual property settings override preset values.
        /// Default: <see cref="RateControlPreset.Balanced"/>
        /// </summary>
        public RateControlPreset Preset { get; set; } = RateControlPreset.Balanced;

        /// <summary>
        /// Gets the default values for a given preset.
        /// </summary>
        internal static PresetDefaults GetPresetDefaults(RateControlPreset preset) => preset switch
        {
            RateControlPreset.Conservative => new PresetDefaults(
                ExecutionTimeCeilingFactor: 140,
                SlowBatchThresholdMs: 6_000,
                DecreaseFactor: 0.4,
                StabilizationBatches: 5,
                MinIncreaseIntervalSeconds: 8),

            RateControlPreset.Balanced => new PresetDefaults(
                ExecutionTimeCeilingFactor: 200,
                SlowBatchThresholdMs: 8_000,
                DecreaseFactor: 0.5,
                StabilizationBatches: 3,
                MinIncreaseIntervalSeconds: 5),

            RateControlPreset.Aggressive => new PresetDefaults(
                ExecutionTimeCeilingFactor: 320,
                SlowBatchThresholdMs: 11_000,
                DecreaseFactor: 0.6,
                StabilizationBatches: 2,
                MinIncreaseIntervalSeconds: 3),

            _ => GetPresetDefaults(RateControlPreset.Balanced)
        };

        internal readonly record struct PresetDefaults(
            int ExecutionTimeCeilingFactor,
            int SlowBatchThresholdMs,
            double DecreaseFactor,
            int StabilizationBatches,
            int MinIncreaseIntervalSeconds);

        #endregion

        #region Public Options

        /// <summary>
        /// Gets or sets whether adaptive rate control is enabled.
        /// Default: true
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Gets or sets whether execution time-based ceiling is enabled.
        /// When enabled, parallelism is capped based on observed batch durations
        /// to avoid exhausting the server's execution time budget.
        /// Default: true
        /// </summary>
        public bool ExecutionTimeCeilingEnabled { get; set; } = true;

        /// <summary>
        /// Gets or sets the maximum acceptable Retry-After duration before failing the operation.
        /// If null (default), waits indefinitely for throttle recovery.
        /// If set, throws <see cref="ServiceProtectionException"/> when Retry-After exceeds this value.
        /// </summary>
        /// <example>
        /// // Fail fast for user-facing operations
        /// options.MaxRetryAfterTolerance = TimeSpan.FromSeconds(30);
        ///
        /// // Wait indefinitely for background jobs
        /// options.MaxRetryAfterTolerance = null;
        /// </example>
        public TimeSpan? MaxRetryAfterTolerance { get; set; } = null;

        #endregion

        #region Preset-Affected Options (with nullable backing fields)

        private int? _executionTimeCeilingFactor;

        /// <summary>
        /// Gets or sets the execution time ceiling factor.
        /// The ceiling is calculated as: Factor / AverageBatchTimeSeconds.
        /// Higher values allow more aggressive parallelism.
        /// If not set, uses the value from <see cref="Preset"/>.
        /// </summary>
        /// <remarks>
        /// Preset defaults: Conservative=140, Balanced=200, Aggressive=320
        /// </remarks>
        public int ExecutionTimeCeilingFactor
        {
            get => _executionTimeCeilingFactor ?? GetPresetDefaults(Preset).ExecutionTimeCeilingFactor;
            set => _executionTimeCeilingFactor = value;
        }

        private int? _slowBatchThresholdMs;

        /// <summary>
        /// Gets or sets the slow batch threshold in milliseconds.
        /// Execution time ceiling is only applied when average batch duration exceeds this threshold.
        /// This allows fast operations (like creates) to run at full parallelism while
        /// protecting slow operations (like updates/deletes) from execution time exhaustion.
        /// If not set, uses the value from <see cref="Preset"/>.
        /// </summary>
        /// <remarks>
        /// Preset defaults: Conservative=6000, Balanced=8000, Aggressive=11000
        /// </remarks>
        public int SlowBatchThresholdMs
        {
            get => _slowBatchThresholdMs ?? GetPresetDefaults(Preset).SlowBatchThresholdMs;
            set => _slowBatchThresholdMs = value;
        }

        private double? _decreaseFactor;

        /// <summary>
        /// Gets or sets the multiplier applied on throttle (0.4-0.7).
        /// Lower values mean more aggressive backoff on throttle.
        /// If not set, uses the value from <see cref="Preset"/>.
        /// </summary>
        /// <remarks>
        /// Preset defaults: Conservative=0.4, Balanced=0.5, Aggressive=0.6
        /// </remarks>
        public double DecreaseFactor
        {
            get => _decreaseFactor ?? GetPresetDefaults(Preset).DecreaseFactor;
            set => _decreaseFactor = value;
        }

        private int? _stabilizationBatches;

        /// <summary>
        /// Gets or sets the number of successful batches required before considering increase.
        /// Higher values are more cautious about ramping up.
        /// If not set, uses the value from <see cref="Preset"/>.
        /// </summary>
        /// <remarks>
        /// Preset defaults: Conservative=5, Balanced=3, Aggressive=2
        /// </remarks>
        public int StabilizationBatches
        {
            get => _stabilizationBatches ?? GetPresetDefaults(Preset).StabilizationBatches;
            set => _stabilizationBatches = value;
        }

        private TimeSpan? _minIncreaseInterval;

        /// <summary>
        /// Gets or sets the minimum time between parallelism increases.
        /// Longer intervals are more conservative.
        /// If not set, uses the value from <see cref="Preset"/>.
        /// </summary>
        /// <remarks>
        /// Preset defaults: Conservative=8s, Balanced=5s, Aggressive=3s
        /// </remarks>
        public TimeSpan MinIncreaseInterval
        {
            get => _minIncreaseInterval ?? TimeSpan.FromSeconds(GetPresetDefaults(Preset).MinIncreaseIntervalSeconds);
            set => _minIncreaseInterval = value;
        }

        #endregion

        #region Configuration Binding Fix

        /// <summary>
        /// Clears backing fields that were not explicitly configured.
        /// Call this after Bind() to fix the issue where ConfigurationBinder
        /// populates backing fields by reading getters and writing to setters.
        /// </summary>
        /// <param name="configuredKeys">
        /// The set of configuration keys that were explicitly present.
        /// Keys should be property names like "ExecutionTimeCeilingFactor".
        /// </param>
        internal void ClearNonConfiguredBackingFields(HashSet<string> configuredKeys)
        {
            if (!configuredKeys.Contains(nameof(ExecutionTimeCeilingFactor)))
            {
                _executionTimeCeilingFactor = null;
            }

            if (!configuredKeys.Contains(nameof(SlowBatchThresholdMs)))
            {
                _slowBatchThresholdMs = null;
            }

            if (!configuredKeys.Contains(nameof(DecreaseFactor)))
            {
                _decreaseFactor = null;
            }

            if (!configuredKeys.Contains(nameof(StabilizationBatches)))
            {
                _stabilizationBatches = null;
            }

            if (!configuredKeys.Contains(nameof(MinIncreaseInterval)))
            {
                _minIncreaseInterval = null;
            }
        }

        #endregion

        #region Override Detection (for logging)

        /// <summary>
        /// Returns true if ExecutionTimeCeilingFactor was explicitly set (not from preset).
        /// </summary>
        internal bool IsExecutionTimeCeilingFactorOverridden => _executionTimeCeilingFactor.HasValue;

        /// <summary>
        /// Returns true if SlowBatchThresholdMs was explicitly set (not from preset).
        /// </summary>
        internal bool IsSlowBatchThresholdMsOverridden => _slowBatchThresholdMs.HasValue;

        /// <summary>
        /// Returns true if DecreaseFactor was explicitly set (not from preset).
        /// </summary>
        internal bool IsDecreaseFactorOverridden => _decreaseFactor.HasValue;

        /// <summary>
        /// Returns true if StabilizationBatches was explicitly set (not from preset).
        /// </summary>
        internal bool IsStabilizationBatchesOverridden => _stabilizationBatches.HasValue;

        /// <summary>
        /// Returns true if MinIncreaseInterval was explicitly set (not from preset).
        /// </summary>
        internal bool IsMinIncreaseIntervalOverridden => _minIncreaseInterval.HasValue;

        /// <summary>
        /// Formats a value with an indicator of whether it's from preset or explicitly overridden.
        /// </summary>
        internal static string FormatValue<T>(T value, bool isOverridden) =>
            isOverridden ? $"{value} (override)" : $"{value}";

        #endregion

        #region Internal Options (implementation details)

        /// <summary>
        /// Hard ceiling for parallelism (Microsoft's per-user limit).
        /// This is not configurable - it's a platform limit.
        /// </summary>
        internal int HardCeiling => 52;

        /// <summary>
        /// Absolute minimum parallelism. Fallback if server recommends less than this.
        /// </summary>
        internal int MinParallelism => 1;

        /// <summary>
        /// Parallelism increase amount per stabilization period.
        /// Note: The actual increase uses Math.Max(floor, this value), so floor typically dominates.
        /// </summary>
        internal int IncreaseRate => 2;

        /// <summary>
        /// Multiplier for recovery phase increases.
        /// </summary>
        internal double RecoveryMultiplier => 2.0;

        /// <summary>
        /// TTL for lastKnownGood value.
        /// </summary>
        internal TimeSpan LastKnownGoodTTL => TimeSpan.FromMinutes(5);

        /// <summary>
        /// Idle period after which state resets.
        /// </summary>
        internal TimeSpan IdleResetPeriod => TimeSpan.FromMinutes(5);

        /// <summary>
        /// Minimum number of batch samples required before applying execution time ceiling.
        /// </summary>
        internal int MinBatchSamplesForCeiling => 3;

        /// <summary>
        /// Smoothing factor for exponential moving average of batch durations.
        /// </summary>
        internal double BatchDurationSmoothingFactor => 0.3;

        #endregion
    }
}
