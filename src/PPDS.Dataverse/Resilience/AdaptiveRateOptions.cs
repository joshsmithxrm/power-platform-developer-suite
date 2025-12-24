using System;

namespace PPDS.Dataverse.Resilience
{
    /// <summary>
    /// Configuration options for adaptive rate control.
    /// </summary>
    public class AdaptiveRateOptions
    {
        /// <summary>
        /// Gets or sets whether adaptive rate control is enabled.
        /// Default: true
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Gets or sets the hard ceiling for parallelism (Microsoft's per-user limit).
        /// Default: 52
        /// </summary>
        public int HardCeiling { get; set; } = 52;

        /// <summary>
        /// Gets or sets the absolute minimum parallelism.
        /// Fallback if server recommends less than this.
        /// Default: 1
        /// </summary>
        public int MinParallelism { get; set; } = 1;

        /// <summary>
        /// Gets or sets the parallelism increase amount per stabilization period.
        /// Default: 2
        /// </summary>
        public int IncreaseRate { get; set; } = 2;

        /// <summary>
        /// Gets or sets the multiplier applied on throttle (0.5-0.9).
        /// Default: 0.5 (aggressive backoff, throttle ceiling handles future probing)
        /// </summary>
        public double DecreaseFactor { get; set; } = 0.5;

        /// <summary>
        /// Gets or sets the number of successful batches required before considering increase.
        /// Default: 3
        /// </summary>
        public int StabilizationBatches { get; set; } = 3;

        /// <summary>
        /// Gets or sets the minimum time between parallelism increases.
        /// Default: 5 seconds
        /// </summary>
        public TimeSpan MinIncreaseInterval { get; set; } = TimeSpan.FromSeconds(5);

        /// <summary>
        /// Gets or sets the multiplier for recovery phase increases.
        /// Default: 2.0
        /// </summary>
        public double RecoveryMultiplier { get; set; } = 2.0;

        /// <summary>
        /// Gets or sets the TTL for lastKnownGood value.
        /// Default: 5 minutes
        /// </summary>
        public TimeSpan LastKnownGoodTTL { get; set; } = TimeSpan.FromMinutes(5);

        /// <summary>
        /// Gets or sets the idle period after which state resets.
        /// Default: 5 minutes
        /// </summary>
        public TimeSpan IdleResetPeriod { get; set; } = TimeSpan.FromMinutes(5);
    }
}
