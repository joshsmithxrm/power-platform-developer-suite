using System;
using System.Threading;
using System.Threading.Tasks;
using PPDS.Dataverse.Client;

namespace PPDS.Dataverse.Pooling
{
    /// <summary>
    /// Manages a pool of Dataverse connections with intelligent selection and lifecycle management.
    /// Supports multiple connection sources for load distribution across Application Users.
    /// </summary>
    public interface IDataverseConnectionPool : IAsyncDisposable, IDisposable
    {
        /// <summary>
        /// Gets a client from the pool asynchronously.
        /// </summary>
        /// <param name="options">Optional per-request options (CallerId, etc.)</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A pooled client that returns to pool on dispose.</returns>
        /// <exception cref="TimeoutException">Thrown when no connection is available within the timeout period.</exception>
        /// <exception cref="InvalidOperationException">Thrown when the pool is not enabled or has been disposed.</exception>
        Task<IPooledClient> GetClientAsync(
            DataverseClientOptions? options = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets a client from the pool synchronously.
        /// </summary>
        /// <param name="options">Optional per-request options (CallerId, etc.)</param>
        /// <returns>A pooled client that returns to pool on dispose.</returns>
        /// <exception cref="TimeoutException">Thrown when no connection is available within the timeout period.</exception>
        /// <exception cref="InvalidOperationException">Thrown when the pool is not enabled or has been disposed.</exception>
        IPooledClient GetClient(DataverseClientOptions? options = null);

        /// <summary>
        /// Gets pool statistics and health information.
        /// </summary>
        PoolStatistics Statistics { get; }

        /// <summary>
        /// Gets a value indicating whether the pool is enabled.
        /// </summary>
        bool IsEnabled { get; }

        /// <summary>
        /// Records an authentication failure for statistics.
        /// </summary>
        void RecordAuthFailure();

        /// <summary>
        /// Records a connection failure for statistics.
        /// </summary>
        void RecordConnectionFailure();
    }
}
