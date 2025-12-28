using System;

namespace PPDS.Dataverse.Resilience
{
    /// <summary>
    /// Controls adaptive parallelism for bulk operations at the pool level.
    /// Determines how many concurrent batches can run across all connections.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This controller operates at the pool level, not per-connection. While Dataverse
    /// enforces limits per-user (per app registration), the controller tracks aggregate
    /// throughput and adjusts total parallelism accordingly.
    /// </para>
    /// <para>
    /// Per-connection quota tracking is handled separately by the pool's selection
    /// strategy, which routes work away from throttled connections.
    /// </para>
    /// </remarks>
    public interface IAdaptiveRateController
    {
        /// <summary>
        /// Gets whether adaptive rate control is enabled.
        /// </summary>
        bool IsEnabled { get; }

        /// <summary>
        /// Gets the current total parallelism for the pool.
        /// </summary>
        /// <param name="recommendedPerConnection">Server's recommended parallelism per connection (x-ms-dop-hint).</param>
        /// <param name="connectionCount">Number of configured connections (app registrations).</param>
        /// <returns>Total parallelism to use across all connections.</returns>
        /// <remarks>
        /// The returned value represents the total number of concurrent batches that should
        /// be in-flight across all connections. The ceiling is scaled by connectionCount
        /// (e.g., 2 connections = 2× the single-user ceiling).
        /// </remarks>
        int GetParallelism(int recommendedPerConnection, int connectionCount);

        /// <summary>
        /// Records a batch completion with its duration.
        /// Used for throughput calculation and execution time ceiling.
        /// </summary>
        /// <param name="duration">The wall-clock duration of the batch execution.</param>
        void RecordBatchCompletion(TimeSpan duration);

        /// <summary>
        /// Records a throttle event from any connection.
        /// Triggers parallelism reduction for the entire pool.
        /// </summary>
        /// <param name="retryAfter">The Retry-After duration from server.</param>
        /// <remarks>
        /// A throttle on any connection indicates the pool is pushing too hard overall.
        /// This triggers AIMD-style reduction of total parallelism.
        /// </remarks>
        void RecordThrottle(TimeSpan retryAfter);

        /// <summary>
        /// Resets the controller to initial state.
        /// </summary>
        void Reset();

        /// <summary>
        /// Gets current statistics for the rate controller.
        /// </summary>
        /// <returns>Current statistics.</returns>
        AdaptiveRateStatistics GetStatistics();
    }
}
