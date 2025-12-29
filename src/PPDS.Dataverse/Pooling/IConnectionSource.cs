using System;
using Microsoft.PowerPlatform.Dataverse.Client;

namespace PPDS.Dataverse.Pooling;

/// <summary>
/// Provides a seed ServiceClient for the connection pool to clone.
/// Implementations handle specific authentication methods.
/// </summary>
/// <remarks>
/// <para>
/// The pool calls <see cref="GetSeedClient"/> once per source and caches the result.
/// All pool members for this source are clones of that seed client.
/// </para>
/// <para>
/// Implementations should be thread-safe. <see cref="GetSeedClient"/> may be called
/// from multiple threads during pool initialization or expansion.
/// </para>
/// </remarks>
public interface IConnectionSource : IDisposable
{
    /// <summary>
    /// Gets the unique name for this connection source.
    /// Used for logging, throttle tracking, and connection selection.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Gets the maximum number of pooled connections for this source.
    /// </summary>
    int MaxPoolSize { get; }

    /// <summary>
    /// Gets the seed ServiceClient for cloning.
    /// </summary>
    /// <returns>
    /// An authenticated, ready-to-use ServiceClient.
    /// The pool will clone this client to create pool members.
    /// </returns>
    /// <exception cref="Security.DataverseConnectionException">
    /// Thrown if the client cannot be created or is not ready.
    /// </exception>
    /// <remarks>
    /// This method is called once per source. The result is cached by the pool.
    /// Implementations may create the client lazily on first call.
    /// </remarks>
    ServiceClient GetSeedClient();

    /// <summary>
    /// Invalidates the cached seed client, forcing fresh authentication on next use.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Call this when a token failure is detected. The next call to <see cref="GetSeedClient"/>
    /// will create a new client with fresh authentication instead of returning the cached one.
    /// </para>
    /// <para>
    /// Implementations should dispose the old client if it exists and clear any internal cache.
    /// This method should be thread-safe and idempotent.
    /// </para>
    /// </remarks>
    void InvalidateSeed();
}
