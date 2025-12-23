using System;

namespace PPDS.Dataverse.Resilience
{
    /// <summary>
    /// Configuration options for AIMD-based adaptive rate control.
    /// Controls how parallelism is adjusted based on throttle responses.
    /// </summary>
    public class AdaptiveRateOptions
    {
        /// <summary>
        /// Gets or sets whether adaptive rate control is enabled.
        /// When disabled, uses fixed parallelism from RecommendedDegreesOfParallelism.
        /// Default: true
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Gets or sets the initial parallelism as a factor of max (0.1-1.0).
        /// Starts at this percentage of RecommendedDegreesOfParallelism.
        /// Default: 0.5 (50%)
        /// </summary>
        public double InitialParallelismFactor { get; set; } = 0.5;

        /// <summary>
        /// Gets or sets the minimum parallelism floor.
        /// Never goes below this regardless of throttling.
        /// Default: 1
        /// </summary>
        public int MinParallelism { get; set; } = 1;

        /// <summary>
        /// Gets or sets the parallelism increase amount per stabilization period.
        /// Default: 2
        /// </summary>
        public int IncreaseRate { get; set; } = 2;

        /// <summary>
        /// Gets or sets the multiplier applied on throttle (0.1-0.9).
        /// Halves parallelism on throttle by default.
        /// Default: 0.5
        /// </summary>
        public double DecreaseFactor { get; set; } = 0.5;

        /// <summary>
        /// Gets or sets the number of successful batches required before considering increase.
        /// Must also satisfy MinIncreaseInterval.
        /// Default: 3
        /// </summary>
        public int StabilizationBatches { get; set; } = 3;

        /// <summary>
        /// Gets or sets the minimum time between parallelism increases.
        /// Prevents rapid oscillation when batches complete quickly.
        /// Default: 5 seconds
        /// </summary>
        public TimeSpan MinIncreaseInterval { get; set; } = TimeSpan.FromSeconds(5);

        /// <summary>
        /// Gets or sets the multiplier for recovery phase (getting back to last-known-good).
        /// Increases faster during recovery, slower when probing new territory.
        /// Default: 2.0
        /// </summary>
        public double RecoveryMultiplier { get; set; } = 2.0;

        /// <summary>
        /// Gets or sets the TTL for lastKnownGood value.
        /// Matches Microsoft's rolling window. Stale values are discarded.
        /// Default: 5 minutes
        /// </summary>
        public TimeSpan LastKnownGoodTTL { get; set; } = TimeSpan.FromMinutes(5);

        /// <summary>
        /// Gets or sets the idle period after which state resets.
        /// Long-running integrations with gaps get fresh starts.
        /// Default: 5 minutes
        /// </summary>
        public TimeSpan IdleResetPeriod { get; set; } = TimeSpan.FromMinutes(5);
    }
}
