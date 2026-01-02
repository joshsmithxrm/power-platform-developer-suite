using System;
using Microsoft.PowerPlatform.Dataverse.Client;
using PPDS.Dataverse.Configuration;
using PPDS.Dataverse.Security;

namespace PPDS.Dataverse.Pooling;

/// <summary>
/// Connection source that creates a ServiceClient from connection string configuration.
/// Used for backward compatibility with existing DataverseOptions configuration.
/// </summary>
/// <remarks>
/// The ServiceClient is created lazily on the first call to <see cref="GetSeedClient"/>.
/// This allows the pool to be constructed without immediately authenticating.
/// </remarks>
public sealed class ConnectionStringSource : IConnectionSource
{
    private readonly DataverseConnection _config;
    private readonly object _lock = new();
    private volatile ServiceClient? _client;
    private bool _disposed;

    /// <summary>
    /// Creates a new connection source from connection configuration.
    /// </summary>
    /// <param name="config">The connection configuration.</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown if <paramref name="config"/> is null.
    /// </exception>
    public ConnectionStringSource(DataverseConnection config)
    {
        ArgumentNullException.ThrowIfNull(config);
        _config = config;
    }

    /// <inheritdoc />
    public string Name => _config.Name;

    /// <inheritdoc />
    public int MaxPoolSize => _config.MaxPoolSize;

    /// <inheritdoc />
    public ServiceClient GetSeedClient()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_client != null)
            return _client;

        lock (_lock)
        {
            if (_client != null)
                return _client;

            ServiceClient client;
            try
            {
                var secret = SecretResolver.ResolveSync(
                    _config.ClientSecretKeyVaultUri,
                    _config.ClientSecret);

                var connectionString = ConnectionStringBuilder.Build(_config, secret);
                client = new ServiceClient(connectionString);
            }
            catch (Exception ex)
            {
                throw DataverseConnectionException.CreateConnectionFailed(Name, ex);
            }

            if (!client.IsReady)
            {
                var error = client.LastError ?? "Unknown error";
                var exception = client.LastException;
                client.Dispose();

                if (exception != null)
                    throw DataverseConnectionException.CreateConnectionFailed(Name, exception);

                throw new DataverseConnectionException(
                    Name,
                    $"Connection '{Name}' failed to initialize: {error}",
                    new InvalidOperationException(error));
            }

            _client = client;
            return _client;
        }
    }

    /// <inheritdoc />
    public void InvalidateSeed()
    {
        lock (_lock)
        {
            if (_client == null) return;

            _client.Dispose();
            _client = null;
        }
    }

    /// <summary>
    /// Disposes the underlying ServiceClient if it was created.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _client?.Dispose();
    }
}
