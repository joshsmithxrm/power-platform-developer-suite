using System;

namespace PPDS.Dataverse.Pooling
{
    /// <summary>
    /// Exception thrown when the connection pool is exhausted and no connection
    /// could be acquired within the configured timeout period.
    /// </summary>
    /// <remarks>
    /// This exception indicates a transient condition where all connections are currently
    /// in use. Callers can retry after a brief delay, as connections will become available
    /// when other operations complete.
    /// </remarks>
    public class PoolExhaustedException : TimeoutException
    {
        /// <summary>
        /// Gets the number of active connections at the time of the exception.
        /// </summary>
        public int ActiveConnections { get; }

        /// <summary>
        /// Gets the maximum pool size configured.
        /// </summary>
        public int MaxPoolSize { get; }

        /// <summary>
        /// Gets the acquire timeout that was exceeded.
        /// </summary>
        public TimeSpan AcquireTimeout { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="PoolExhaustedException"/> class.
        /// </summary>
        public PoolExhaustedException()
            : base("Connection pool exhausted.")
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="PoolExhaustedException"/> class.
        /// </summary>
        /// <param name="message">The error message.</param>
        public PoolExhaustedException(string message)
            : base(message)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="PoolExhaustedException"/> class.
        /// </summary>
        /// <param name="message">The error message.</param>
        /// <param name="innerException">The inner exception.</param>
        public PoolExhaustedException(string message, Exception innerException)
            : base(message, innerException)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="PoolExhaustedException"/> class.
        /// </summary>
        /// <param name="activeConnections">The number of active connections.</param>
        /// <param name="maxPoolSize">The maximum pool size.</param>
        /// <param name="acquireTimeout">The acquire timeout that was exceeded.</param>
        public PoolExhaustedException(int activeConnections, int maxPoolSize, TimeSpan acquireTimeout)
            : base($"Connection pool exhausted. Active: {activeConnections}, MaxPoolSize: {maxPoolSize}, Timeout: {acquireTimeout.TotalSeconds:F1}s. " +
                   "Consider increasing MaxPoolSize or reducing MaxParallelBatches.")
        {
            ActiveConnections = activeConnections;
            MaxPoolSize = maxPoolSize;
            AcquireTimeout = acquireTimeout;
        }
    }
}
