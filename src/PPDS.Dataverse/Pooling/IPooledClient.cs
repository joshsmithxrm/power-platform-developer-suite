using System;
using PPDS.Dataverse.Client;

namespace PPDS.Dataverse.Pooling
{
    /// <summary>
    /// A client obtained from the connection pool.
    /// Implements <see cref="IAsyncDisposable"/> and <see cref="IDisposable"/> to return the connection to the pool.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Always dispose of the pooled client when done to return it to the pool.
    /// Using 'await using' or 'using' statements is recommended.
    /// </para>
    /// <example>
    /// <code>
    /// await using var client = await pool.GetClientAsync();
    /// var result = await client.RetrieveAsync("account", id, new ColumnSet(true));
    /// </code>
    /// </example>
    /// </remarks>
    public interface IPooledClient : IDataverseClient, IAsyncDisposable, IDisposable
    {
        /// <summary>
        /// Gets the unique identifier for this connection instance.
        /// </summary>
        Guid ConnectionId { get; }

        /// <summary>
        /// Gets the name of the connection configuration this client came from.
        /// Used as a stable key for throttle tracking and rate control.
        /// </summary>
        string ConnectionName { get; }

        /// <summary>
        /// Gets a formatted display name for logging, combining identity and org name.
        /// Format: "{ConnectionName}@{ConnectedOrgFriendlyName}"
        /// </summary>
        string DisplayName { get; }

        /// <summary>
        /// Gets when this connection was created.
        /// </summary>
        DateTime CreatedAt { get; }

        /// <summary>
        /// Gets when this connection was last used.
        /// </summary>
        DateTime LastUsedAt { get; }

        /// <summary>
        /// Gets whether this connection has been marked as invalid.
        /// Invalid connections will be disposed instead of returned to the pool.
        /// </summary>
        bool IsInvalid { get; }

        /// <summary>
        /// Gets the reason the connection was marked invalid, if any.
        /// </summary>
        string? InvalidReason { get; }

        /// <summary>
        /// Marks this connection as invalid. It will be disposed on return instead of being pooled.
        /// Call this when an unrecoverable error occurs (auth failure, connection failure, etc.).
        /// </summary>
        /// <param name="reason">The reason for invalidation (for logging/diagnostics).</param>
        void MarkInvalid(string reason);
    }
}
