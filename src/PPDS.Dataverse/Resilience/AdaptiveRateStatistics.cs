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
        /// Gets the execution time-based ceiling (calculated from batch durations).
        /// Protects slow operations from exhausting the 20-minute execution time budget.
        /// Null if not enough samples have been collected yet.
        /// </summary>
        public int? ExecutionTimeCeiling { get; init; }

        /// <summary>
        /// Gets the request rate-based ceiling (calculated from batch durations).
        /// Protects fast operations from exhausting the 6,000 requests/5-min budget.
        /// Null if not enough samples have been collected yet.
        /// </summary>
        public int? RequestRateCeiling { get; init; }

        /// <summary>
        /// Gets the average batch duration used to calculate ceilings.
        /// Null if not enough samples have been collected yet.
        /// </summary>
        public TimeSpan? AverageBatchDuration { get; init; }

        /// <summary>
        /// Gets the number of batch duration samples collected.
        /// </summary>
        public int BatchDurationSampleCount { get; init; }

        /// <summary>
        /// Gets the effective ceiling (minimum of all applicable ceilings).
        /// </summary>
        public int EffectiveCeiling
        {
            get
            {
                var ceiling = CeilingParallelism;

                if (ThrottleCeilingExpiry.HasValue && ThrottleCeilingExpiry > DateTime.UtcNow && ThrottleCeiling.HasValue)
                {
                    ceiling = Math.Min(ceiling, ThrottleCeiling.Value);
                }

                if (RequestRateCeiling.HasValue)
                {
                    ceiling = Math.Min(ceiling, RequestRateCeiling.Value);
                }

                if (ExecutionTimeCeiling.HasValue)
                {
                    ceiling = Math.Min(ceiling, ExecutionTimeCeiling.Value);
                }

                return ceiling;
            }
        }

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
