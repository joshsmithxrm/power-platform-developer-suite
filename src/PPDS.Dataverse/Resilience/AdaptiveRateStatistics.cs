using System;

namespace PPDS.Dataverse.Resilience
{
    /// <summary>
    /// Current statistics from the adaptive rate controller.
    /// </summary>
    public sealed class AdaptiveRateStatistics
    {
        /// <summary>
        /// Gets the connection name these statistics are for.
        /// </summary>
        public required string ConnectionName { get; init; }

        /// <summary>
        /// Gets the current allowed parallelism.
        /// </summary>
        public required int CurrentParallelism { get; init; }

        /// <summary>
        /// Gets the maximum parallelism ceiling.
        /// </summary>
        public required int MaxParallelism { get; init; }

        /// <summary>
        /// Gets the last known good parallelism level (before throttle).
        /// </summary>
        public required int LastKnownGoodParallelism { get; init; }

        /// <summary>
        /// Gets whether the last known good value is stale (older than TTL).
        /// </summary>
        public required bool IsLastKnownGoodStale { get; init; }

        /// <summary>
        /// Gets the number of successes since the last throttle.
        /// </summary>
        public required int SuccessesSinceThrottle { get; init; }

        /// <summary>
        /// Gets the total number of throttle events recorded.
        /// </summary>
        public required int TotalThrottleEvents { get; init; }

        /// <summary>
        /// Gets the time of the last throttle event, if any.
        /// </summary>
        public required DateTime? LastThrottleTime { get; init; }

        /// <summary>
        /// Gets the time of the last parallelism increase, if any.
        /// </summary>
        public required DateTime? LastIncreaseTime { get; init; }

        /// <summary>
        /// Gets the time of the last activity (any operation).
        /// </summary>
        public required DateTime LastActivityTime { get; init; }

        /// <summary>
        /// Gets whether the controller is in recovery phase (below last known good).
        /// </summary>
        public bool IsInRecoveryPhase => CurrentParallelism < LastKnownGoodParallelism && !IsLastKnownGoodStale;
    }
}
