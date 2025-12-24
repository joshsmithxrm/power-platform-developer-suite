using System;

namespace PPDS.Dataverse.Resilience
{
    /// <summary>
    /// Current statistics from the adaptive rate controller.
    /// </summary>
    public sealed class AdaptiveRateStatistics
    {
        /// <summary>
        /// Gets the connection name.
        /// </summary>
        public required string ConnectionName { get; init; }

        /// <summary>
        /// Gets the current parallelism.
        /// </summary>
        public required int CurrentParallelism { get; init; }

        /// <summary>
        /// Gets the floor (from x-ms-dop-hint).
        /// </summary>
        public required int FloorParallelism { get; init; }

        /// <summary>
        /// Gets the ceiling (hard limit).
        /// </summary>
        public required int CeilingParallelism { get; init; }

        /// <summary>
        /// Gets the throttle-derived ceiling (calculated from Retry-After duration).
        /// Null if no throttle ceiling is active.
        /// </summary>
        public int? ThrottleCeiling { get; init; }

        /// <summary>
        /// Gets when the throttle ceiling expires.
        /// Null if no throttle ceiling is active.
        /// </summary>
        public DateTime? ThrottleCeilingExpiry { get; init; }

        /// <summary>
        /// Gets the effective ceiling (minimum of hard ceiling and throttle ceiling if active).
        /// </summary>
        public int EffectiveCeiling => ThrottleCeilingExpiry.HasValue && ThrottleCeilingExpiry > DateTime.UtcNow && ThrottleCeiling.HasValue
            ? Math.Min(CeilingParallelism, ThrottleCeiling.Value)
            : CeilingParallelism;

        /// <summary>
        /// Gets the last known good parallelism level.
        /// </summary>
        public required int LastKnownGoodParallelism { get; init; }

        /// <summary>
        /// Gets whether last known good is stale.
        /// </summary>
        public required bool IsLastKnownGoodStale { get; init; }

        /// <summary>
        /// Gets the number of successes since last throttle.
        /// </summary>
        public required int SuccessesSinceThrottle { get; init; }

        /// <summary>
        /// Gets total throttle events.
        /// </summary>
        public required int TotalThrottleEvents { get; init; }

        /// <summary>
        /// Gets time of last throttle.
        /// </summary>
        public required DateTime? LastThrottleTime { get; init; }

        /// <summary>
        /// Gets time of last parallelism increase.
        /// </summary>
        public required DateTime? LastIncreaseTime { get; init; }

        /// <summary>
        /// Gets time of last activity.
        /// </summary>
        public required DateTime LastActivityTime { get; init; }

        /// <summary>
        /// Gets whether in recovery phase (below last known good).
        /// </summary>
        public bool IsInRecoveryPhase => CurrentParallelism < LastKnownGoodParallelism && !IsLastKnownGoodStale;
    }
}
