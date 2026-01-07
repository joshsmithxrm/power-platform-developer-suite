using System;

namespace PPDS.Dataverse.Pooling
{
    /// <summary>
    /// Result of initializing a seed connection for a pool source.
    /// </summary>
    public class SeedInitializationResult
    {
        /// <summary>
        /// Gets the connection source name.
        /// </summary>
        public string ConnectionName { get; init; } = string.Empty;

        /// <summary>
        /// Gets a value indicating whether the seed was initialized successfully.
        /// </summary>
        public bool Success { get; init; }

        /// <summary>
        /// Gets the discovered DOP (degrees of parallelism) from the server.
        /// Only populated when <see cref="Success"/> is true.
        /// </summary>
        public int? DiscoveredDop { get; init; }

        /// <summary>
        /// Gets the error message if initialization failed.
        /// </summary>
        public string? ErrorMessage { get; init; }

        /// <summary>
        /// Gets the root cause category of the failure.
        /// </summary>
        public SeedFailureReason? FailureReason { get; init; }

        /// <summary>
        /// Gets the exception that caused the failure, if any.
        /// </summary>
        public Exception? Exception { get; init; }
    }

    /// <summary>
    /// Categorized reasons for seed initialization failure.
    /// </summary>
    public enum SeedFailureReason
    {
        /// <summary>
        /// Authentication failed (invalid credentials, expired token, etc.).
        /// </summary>
        AuthenticationFailed,

        /// <summary>
        /// Network connectivity issue (DNS, timeout, firewall, etc.).
        /// </summary>
        NetworkError,

        /// <summary>
        /// The Dataverse service returned an error.
        /// </summary>
        ServiceError,

        /// <summary>
        /// Connection was established but never became ready.
        /// </summary>
        ConnectionNotReady,

        /// <summary>
        /// Unknown or unclassified error.
        /// </summary>
        Unknown
    }
}
