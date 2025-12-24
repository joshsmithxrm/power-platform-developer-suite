using System;

namespace PPDS.Dataverse.Resilience
{
    /// <summary>
    /// Controls adaptive parallelism based on throttle responses.
    /// </summary>
    public interface IAdaptiveRateController
    {
        /// <summary>
        /// Gets whether adaptive rate control is enabled.
        /// </summary>
        bool IsEnabled { get; }

        /// <summary>
        /// Gets the current parallelism for a connection.
        /// </summary>
        /// <param name="connectionName">The connection identifier.</param>
        /// <param name="recommendedParallelism">Server's recommended parallelism (x-ms-dop-hint).</param>
        /// <param name="connectionCount">Number of configured connections (scales floor/ceiling).</param>
        /// <returns>Current parallelism to use.</returns>
        int GetParallelism(string connectionName, int recommendedParallelism, int connectionCount);

        /// <summary>
        /// Records successful batch completion.
        /// </summary>
        /// <param name="connectionName">The connection that succeeded.</param>
        void RecordSuccess(string connectionName);

        /// <summary>
        /// Records throttle event.
        /// </summary>
        /// <param name="connectionName">The connection that was throttled.</param>
        /// <param name="retryAfter">The Retry-After duration from server.</param>
        void RecordThrottle(string connectionName, TimeSpan retryAfter);

        /// <summary>
        /// Resets state for a connection.
        /// </summary>
        /// <param name="connectionName">The connection to reset.</param>
        void Reset(string connectionName);

        /// <summary>
        /// Gets current statistics for a connection.
        /// </summary>
        /// <param name="connectionName">The connection to get stats for.</param>
        /// <returns>Current statistics, or null if no state exists.</returns>
        AdaptiveRateStatistics? GetStatistics(string connectionName);
    }
}
