using System;
using Microsoft.PowerPlatform.Dataverse.Client;

namespace PPDS.Dataverse.Pooling;

/// <summary>
/// Connection source for pre-authenticated ServiceClients.
/// Use this when you have an already-authenticated client (device code, managed identity, etc.)
/// </summary>
/// <remarks>
/// <para>
/// This source wraps an existing ServiceClient. The pool will clone this client
/// to create pool members. The original client is used as the seed and must remain
/// valid for the lifetime of the pool.
/// </para>
/// <para>
/// The caller is responsible for authenticating the ServiceClient before passing it here.
/// This class does not perform any authentication.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// // Device code authentication
/// var client = await DeviceCodeAuth.AuthenticateAsync(url);
/// var source = new ServiceClientSource(client, "Interactive", maxPoolSize: 10);
/// var pool = new DataverseConnectionPool(new[] { source }, ...);
///
/// // Managed identity
/// var client = new ServiceClient(url, tokenProviderFunc);
/// var source = new ServiceClientSource(client, "ManagedIdentity");
/// </code>
/// </example>
public sealed class ServiceClientSource : IConnectionSource
{
    private readonly ServiceClient _client;
    private bool _disposed;

    /// <summary>
    /// Creates a new connection source wrapping an existing ServiceClient.
    /// </summary>
    /// <param name="client">
    /// The authenticated ServiceClient to use as the seed for cloning.
    /// Must be ready (<see cref="ServiceClient.IsReady"/> == true).
    /// </param>
    /// <param name="name">
    /// Unique name for this connection source. Used for logging and tracking.
    /// </param>
    /// <param name="maxPoolSize">
    /// Maximum number of pooled connections from this source. Default is 10.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown if <paramref name="client"/> or <paramref name="name"/> is null.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown if <paramref name="client"/> is not ready or <paramref name="name"/> is empty.
    /// </exception>
    public ServiceClientSource(ServiceClient client, string name, int maxPoolSize = 10)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(name);

        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Name cannot be empty.", nameof(name));

        if (!client.IsReady)
            throw new ArgumentException("ServiceClient must be ready.", nameof(client));

        if (maxPoolSize < 1)
            throw new ArgumentOutOfRangeException(nameof(maxPoolSize), "MaxPoolSize must be at least 1.");

        _client = client;
        Name = name;
        MaxPoolSize = maxPoolSize;
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public int MaxPoolSize { get; }

    /// <inheritdoc />
    public ServiceClient GetSeedClient()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _client;
    }

    /// <inheritdoc />
    /// <remarks>
    /// For <see cref="ServiceClientSource"/>, this is a no-op because the client
    /// is provided externally and cannot be recreated. The caller must handle
    /// seed invalidation by creating a new source with a fresh client.
    /// </remarks>
    public void InvalidateSeed()
    {
        // ServiceClientSource wraps an externally-provided client.
        // We cannot recreate it - the caller must create a new source.
        // This is intentionally a no-op; the pool will log a warning.
    }

    /// <summary>
    /// Disposes the underlying ServiceClient.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _client.Dispose();
    }
}
