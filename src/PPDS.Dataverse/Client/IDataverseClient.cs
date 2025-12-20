using System;
using Microsoft.PowerPlatform.Dataverse.Client;

namespace PPDS.Dataverse.Client
{
    /// <summary>
    /// Abstraction over ServiceClient providing core Dataverse operations.
    /// Extends <see cref="IOrganizationServiceAsync2"/> with additional Dataverse-specific properties.
    /// </summary>
    public interface IDataverseClient : IOrganizationServiceAsync2
    {
        /// <summary>
        /// Gets a value indicating whether the connection is ready for operations.
        /// </summary>
        bool IsReady { get; }

        /// <summary>
        /// Gets the server-recommended degree of parallelism for bulk operations.
        /// </summary>
        int RecommendedDegreesOfParallelism { get; }

        /// <summary>
        /// Gets the connected organization ID.
        /// </summary>
        Guid? ConnectedOrgId { get; }

        /// <summary>
        /// Gets the connected organization friendly name.
        /// </summary>
        string ConnectedOrgFriendlyName { get; }

        /// <summary>
        /// Gets the connected organization unique name.
        /// </summary>
        string ConnectedOrgUniqueName { get; }

        /// <summary>
        /// Gets the connected organization version.
        /// </summary>
        Version? ConnectedOrgVersion { get; }

        /// <summary>
        /// Gets the last error message from the service.
        /// </summary>
        string? LastError { get; }

        /// <summary>
        /// Gets the last exception from the service.
        /// </summary>
        Exception? LastException { get; }

        /// <summary>
        /// Gets or sets the caller ID for impersonation.
        /// </summary>
        Guid CallerId { get; set; }

        /// <summary>
        /// Gets or sets the caller AAD object ID for impersonation.
        /// </summary>
        Guid? CallerAADObjectId { get; set; }

        /// <summary>
        /// Gets or sets the maximum number of retry attempts for transient failures.
        /// </summary>
        int MaxRetryCount { get; set; }

        /// <summary>
        /// Gets or sets the pause time between retry attempts.
        /// </summary>
        TimeSpan RetryPauseTime { get; set; }

        /// <summary>
        /// Creates a clone of this client that shares the underlying connection.
        /// Cloning is significantly faster than creating a new connection.
        /// </summary>
        /// <returns>A cloned client instance.</returns>
        IDataverseClient Clone();
    }
}
