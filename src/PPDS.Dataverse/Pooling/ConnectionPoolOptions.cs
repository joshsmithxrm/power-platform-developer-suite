using System;

namespace PPDS.Dataverse.Pooling
{
    /// <summary>
    /// Configuration options for the Dataverse connection pool.
    /// </summary>
    public class ConnectionPoolOptions
    {
        /// <summary>
        /// Microsoft's hard limit for concurrent requests per Application User.
        /// This is an enforced platform limit that cannot be exceeded.
        /// </summary>
        internal const int MicrosoftHardLimitPerUser = 52;

        /// <summary>
        /// Gets or sets a value indicating whether connection pooling is enabled.
        /// Default: true
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Gets or sets a fixed total pool size override.
        /// When 0 (default), uses 52 × connection count (Microsoft's per-user limit).
        /// Set to a positive value to enforce a specific total pool size.
        /// Default: 0 (use per-connection sizing at 52 per user).
        /// </summary>
        /// <remarks>
        /// The pool semaphore is sized at 52 × connections to respect Microsoft's hard limit.
        /// Actual parallelism is controlled by RecommendedDegreesOfParallelism from the server.
        /// </remarks>
        public int MaxPoolSize { get; set; } = 0;

        /// <summary>
        /// Gets or sets the maximum acceptable Retry-After duration before failing.
        /// Default: null (wait indefinitely for throttle to clear).
        /// If set, throws <see cref="Resilience.ServiceProtectionException"/> when all connections
        /// are throttled and the shortest wait exceeds this value.
        /// </summary>
        /// <remarks>
        /// Throttle waits are typically 30 seconds to 5 minutes. Most bulk operations should
        /// wait indefinitely (the default) since throttles are temporary and will clear.
        /// Only set a tolerance for interactive scenarios where responsiveness matters more
        /// than completion.
        /// </remarks>
        public TimeSpan? MaxRetryAfterTolerance { get; set; } = null;


        /// <summary>
        /// Gets or sets the maximum time to wait for a connection.
        /// Default: 30 seconds
        /// </summary>
        public TimeSpan AcquireTimeout { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Gets or sets the maximum connection idle time before eviction.
        /// Default: 5 minutes
        /// </summary>
        public TimeSpan MaxIdleTime { get; set; } = TimeSpan.FromMinutes(5);

        /// <summary>
        /// Gets or sets the maximum connection lifetime.
        /// Set within OAuth token validity window for stable long-running scenarios.
        /// Default: 60 minutes
        /// </summary>
        public TimeSpan MaxLifetime { get; set; } = TimeSpan.FromMinutes(60);

        /// <summary>
        /// Gets or sets a value indicating whether to disable the affinity cookie for load distribution.
        /// Default: true (disabled)
        /// </summary>
        /// <remarks>
        /// <para>
        /// CRITICAL: With EnableAffinityCookie = true (SDK default), all requests route to a single backend node.
        /// Disabling the affinity cookie can increase performance by at least one order of magnitude.
        /// </para>
        /// <para>
        /// Only set to false (enable affinity) for low-volume scenarios or when session affinity is required.
        /// </para>
        /// </remarks>
        public bool DisableAffinityCookie { get; set; } = true;

        /// <summary>
        /// Gets or sets the connection selection strategy.
        /// Default: ThrottleAware
        /// </summary>
        public ConnectionSelectionStrategy SelectionStrategy { get; set; } = ConnectionSelectionStrategy.ThrottleAware;

        /// <summary>
        /// Gets or sets the interval for background validation.
        /// Default: 1 minute
        /// </summary>
        public TimeSpan ValidationInterval { get; set; } = TimeSpan.FromMinutes(1);

        /// <summary>
        /// Gets or sets a value indicating whether background connection validation is enabled.
        /// Default: true
        /// </summary>
        public bool EnableValidation { get; set; } = true;

        /// <summary>
        /// Gets or sets a value indicating whether to validate connection health on checkout.
        /// When true, connections are checked for IsReady, age, and validity before being returned.
        /// Default: true
        /// </summary>
        public bool ValidateOnCheckout { get; set; } = true;

        /// <summary>
        /// Gets or sets the maximum number of retry attempts for auth/connection failures.
        /// When a connection fails due to auth or connectivity issues, operations will retry
        /// with a new connection up to this many times.
        /// Default: 2
        /// </summary>
        public int MaxConnectionRetries { get; set; } = 2;
    }

    /// <summary>
    /// Strategy for selecting which connection to use from the pool.
    /// </summary>
    public enum ConnectionSelectionStrategy
    {
        /// <summary>
        /// Simple rotation through connections.
        /// </summary>
        RoundRobin,

        /// <summary>
        /// Select connection with fewest active clients.
        /// </summary>
        LeastConnections,

        /// <summary>
        /// Avoid throttled connections, fallback to round-robin.
        /// </summary>
        ThrottleAware
    }
}
