using Microsoft.Extensions.DependencyInjection;
using PPDS.Auth.Credentials;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Services.Query;
using PPDS.Dataverse.Pooling;

namespace PPDS.Cli.Interactive;

/// <summary>
/// Manages the interactive session state including connection pool lifecycle.
/// The pool is lazily created on first use and reused across all queries in the session.
/// </summary>
/// <remarks>
/// This class ensures that:
/// - Connection pool is created once and reused for all queries (faster subsequent queries)
/// - DOP detection happens once per session
/// - Throttle state is preserved across queries
/// - Environment changes trigger pool recreation
/// </remarks>
internal sealed class InteractiveSession : IAsyncDisposable
{
    private readonly string _profileName;
    private readonly Action<DeviceCodeInfo>? _deviceCodeCallback;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private ServiceProvider? _serviceProvider;
    private string? _currentEnvironmentUrl;
    private bool _disposed;

    /// <summary>
    /// Gets the current environment URL, or null if no connection has been established.
    /// </summary>
    public string? EnvironmentUrl => _currentEnvironmentUrl;

    /// <summary>
    /// Creates a new interactive session for the specified profile.
    /// </summary>
    /// <param name="profileName">The profile name (null for active profile).</param>
    /// <param name="deviceCodeCallback">Callback for device code display.</param>
    public InteractiveSession(string? profileName, Action<DeviceCodeInfo>? deviceCodeCallback = null)
    {
        _profileName = profileName ?? string.Empty;
        _deviceCodeCallback = deviceCodeCallback;
    }

    /// <summary>
    /// Gets or creates a service provider for the specified environment.
    /// If the environment changes, the existing provider is disposed and a new one is created.
    /// </summary>
    /// <param name="environmentUrl">The environment URL to connect to.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The service provider with connection pool.</returns>
    public async Task<ServiceProvider> GetServiceProviderAsync(
        string environmentUrl,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Check if we need to create or recreate the provider
            if (_serviceProvider != null && _currentEnvironmentUrl == environmentUrl)
            {
                return _serviceProvider;
            }

            // Dispose existing provider if environment changed
            if (_serviceProvider != null)
            {
                await _serviceProvider.DisposeAsync().ConfigureAwait(false);
                _serviceProvider = null;
                _currentEnvironmentUrl = null;
            }

            // Create new provider
            _serviceProvider = await ProfileServiceFactory.CreateFromProfileAsync(
                string.IsNullOrEmpty(_profileName) ? null : _profileName,
                environmentUrl,
                deviceCodeCallback: _deviceCodeCallback,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            _currentEnvironmentUrl = environmentUrl;
            return _serviceProvider;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Gets the SQL query service for the specified environment.
    /// The underlying connection pool is reused across calls.
    /// </summary>
    /// <param name="environmentUrl">The environment URL to connect to.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The SQL query service.</returns>
    public async Task<ISqlQueryService> GetSqlQueryServiceAsync(
        string environmentUrl,
        CancellationToken cancellationToken = default)
    {
        var provider = await GetServiceProviderAsync(environmentUrl, cancellationToken).ConfigureAwait(false);
        return provider.GetRequiredService<ISqlQueryService>();
    }

    /// <summary>
    /// Gets the connection pool for the specified environment.
    /// </summary>
    /// <param name="environmentUrl">The environment URL to connect to.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The connection pool.</returns>
    public async Task<IDataverseConnectionPool> GetConnectionPoolAsync(
        string environmentUrl,
        CancellationToken cancellationToken = default)
    {
        var provider = await GetServiceProviderAsync(environmentUrl, cancellationToken).ConfigureAwait(false);
        return provider.GetRequiredService<IDataverseConnectionPool>();
    }

    /// <summary>
    /// Invalidates the current session, disposing the connection pool.
    /// The next call to Get*Async will create a fresh provider.
    /// </summary>
    public async Task InvalidateAsync()
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_serviceProvider != null)
            {
                await _serviceProvider.DisposeAsync().ConfigureAwait(false);
                _serviceProvider = null;
                _currentEnvironmentUrl = null;
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_serviceProvider != null)
        {
            await _serviceProvider.DisposeAsync().ConfigureAwait(false);
            _serviceProvider = null;
        }

        _lock.Dispose();
    }
}
