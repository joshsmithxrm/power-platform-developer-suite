using System;

namespace PPDS.Dataverse.Resilience
{
    /// <summary>
    /// Exception thrown when a service protection limit is hit.
    /// </summary>
    public class ServiceProtectionException : Exception
    {
        /// <summary>
        /// Error code for "Number of requests exceeded".
        /// </summary>
        public const int ErrorCodeRequestsExceeded = -2147015902;

        /// <summary>
        /// Error code for "Combined execution time exceeded".
        /// </summary>
        public const int ErrorCodeExecutionTimeExceeded = -2147015903;

        /// <summary>
        /// Error code for "Concurrent requests exceeded".
        /// </summary>
        public const int ErrorCodeConcurrentRequestsExceeded = -2147015898;

        /// <summary>
        /// Gets the name of the connection that was throttled.
        /// </summary>
        public string ConnectionName { get; }

        /// <summary>
        /// Gets the time to wait before retrying.
        /// </summary>
        public TimeSpan RetryAfter { get; }

        /// <summary>
        /// Gets the error code from the service.
        /// </summary>
        public int ErrorCode { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="ServiceProtectionException"/> class
        /// with a custom message (used when all connections are throttled and tolerance is exceeded).
        /// </summary>
        /// <param name="message">The exception message.</param>
        public ServiceProtectionException(string message)
            : base(message)
        {
            ConnectionName = string.Empty;
            RetryAfter = TimeSpan.Zero;
            ErrorCode = 0;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="ServiceProtectionException"/> class.
        /// </summary>
        /// <param name="connectionName">The connection that was throttled.</param>
        /// <param name="retryAfter">Time to wait before retrying.</param>
        /// <param name="errorCode">The error code from the service.</param>
        public ServiceProtectionException(string connectionName, TimeSpan retryAfter, int errorCode)
            : base($"Service protection limit hit for connection '{connectionName}'. Retry after {retryAfter}.")
        {
            ConnectionName = connectionName;
            RetryAfter = retryAfter;
            ErrorCode = errorCode;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="ServiceProtectionException"/> class.
        /// </summary>
        /// <param name="connectionName">The connection that was throttled.</param>
        /// <param name="retryAfter">Time to wait before retrying.</param>
        /// <param name="errorCode">The error code from the service.</param>
        /// <param name="innerException">The inner exception.</param>
        public ServiceProtectionException(string connectionName, TimeSpan retryAfter, int errorCode, Exception innerException)
            : base($"Service protection limit hit for connection '{connectionName}'. Retry after {retryAfter}.", innerException)
        {
            ConnectionName = connectionName;
            RetryAfter = retryAfter;
            ErrorCode = errorCode;
        }

        /// <summary>
        /// Determines if an error code is a service protection error.
        /// </summary>
        /// <param name="errorCode">The error code to check.</param>
        /// <returns>True if the error code indicates a service protection limit.</returns>
        public static bool IsServiceProtectionError(int errorCode)
        {
            return errorCode == ErrorCodeRequestsExceeded
                || errorCode == ErrorCodeExecutionTimeExceeded
                || errorCode == ErrorCodeConcurrentRequestsExceeded;
        }
    }
}
