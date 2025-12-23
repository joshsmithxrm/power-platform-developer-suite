using System;

namespace PPDS.Dataverse.Resilience
{
    /// <summary>
    /// Controls adaptive parallelism using AIMD (Additive Increase, Multiplicative Decrease).
    /// Manages parallelism per connection based on throttle responses.
    /// </summary>
    public interface IAdaptiveRateController
    {
        /// <summary>
        /// Gets whether adaptive rate control is enabled.
        /// </summary>
        bool IsEnabled { get; }

        /// <summary>
        /// Gets the current recommended parallelism for a connection.
        /// Also updates last activity timestamp and checks for idle reset.
        /// </summary>
        /// <param name="connectionName">The connection to get parallelism for.</param>
        /// <param name="maxParallelism">The ceiling (from RecommendedDegreesOfParallelism).</param>
        /// <returns>Current parallelism to use.</returns>
        int GetParallelism(string connectionName, int maxParallelism);

        /// <summary>
        /// Records successful batch completion. May increase parallelism if stable.
        /// </summary>
        /// <param name="connectionName">The connection that succeeded.</param>
        void RecordSuccess(string connectionName);

        /// <summary>
        /// Records throttle event. Reduces parallelism.
        /// </summary>
        /// <param name="connectionName">The connection that was throttled.</param>
        /// <param name="retryAfter">The Retry-After duration from server.</param>
        void RecordThrottle(string connectionName, TimeSpan retryAfter);

        /// <summary>
        /// Manually resets state for a connection.
        /// </summary>
        /// <param name="connectionName">The connection to reset.</param>
        void Reset(string connectionName);

        /// <summary>
        /// Gets current statistics for monitoring/logging.
        /// </summary>
        /// <param name="connectionName">The connection to get stats for.</param>
        /// <returns>Current statistics, or null if no state exists.</returns>
        AdaptiveRateStatistics? GetStatistics(string connectionName);
    }
}
