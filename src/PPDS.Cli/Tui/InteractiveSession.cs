using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PPDS.Auth.Credentials;
using PPDS.Auth.Profiles;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Services.Environment;
using PPDS.Cli.Services.Export;
using PPDS.Cli.Services.History;
using PPDS.Cli.Services.Profile;
using PPDS.Cli.Services.Query;
using PPDS.Cli.Tui.Infrastructure;
using PPDS.Dataverse.Pooling;

namespace PPDS.Cli.Tui;

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
    /// <summary>
    /// Timeout for service provider disposal to prevent hanging on exit.
    /// </summary>
    private static readonly TimeSpan DisposeTimeout = TimeSpan.FromSeconds(2);

    private string _profileName;
    private readonly Action<DeviceCodeInfo>? _deviceCodeCallback;
    private readonly Func<Action<DeviceCodeInfo>?, PreAuthDialogResult>? _beforeInteractiveAuth;
    private readonly ProfileStore _profileStore;
    private readonly IServiceProviderFactory _serviceProviderFactory;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private ServiceProvider? _serviceProvider;
    private string? _currentEnvironmentUrl;
    private string? _currentEnvironmentDisplayName;
    private bool _disposed;
    private ITuiErrorService? _errorService;

    /// <summary>
    /// Event raised when the environment changes (either via initialization or explicit switch).
    /// </summary>
    public event Action<string?, string?>? EnvironmentChanged;

    /// <summary>
    /// Gets the current environment URL, or null if no connection has been established.
    /// </summary>
    public string? CurrentEnvironmentUrl => _currentEnvironmentUrl;

    /// <summary>
    /// Gets the current environment display name, or null if not set.
    /// </summary>
    public string? CurrentEnvironmentDisplayName => _currentEnvironmentDisplayName;

    /// <summary>
    /// Creates a new interactive session for the specified profile.
    /// </summary>
    /// <param name="profileName">The profile name (null for active profile).</param>
    /// <param name="profileStore">Shared profile store instance.</param>
    /// <param name="serviceProviderFactory">Factory for creating service providers (null for default).</param>
    /// <param name="deviceCodeCallback">Callback for device code display.</param>
    /// <param name="beforeInteractiveAuth">Callback invoked before browser opens for interactive auth.
    /// Returns the user's choice (OpenBrowser, UseDeviceCode, or Cancel).</param>
    public InteractiveSession(
        string? profileName,
        ProfileStore profileStore,
        IServiceProviderFactory? serviceProviderFactory = null,
        Action<DeviceCodeInfo>? deviceCodeCallback = null,
        Func<Action<DeviceCodeInfo>?, PreAuthDialogResult>? beforeInteractiveAuth = null)
    {
        _profileName = profileName ?? string.Empty;
        _profileStore = profileStore ?? throw new ArgumentNullException(nameof(profileStore));
        _serviceProviderFactory = serviceProviderFactory ?? new ProfileBasedServiceProviderFactory();
        _deviceCodeCallback = deviceCodeCallback;
        _beforeInteractiveAuth = beforeInteractiveAuth;
    }

    /// <summary>
    /// Initializes the session by loading the active profile and warming the connection pool.
    /// Call this early (e.g., during TUI startup) so the connection is ready when needed.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        TuiDebugLog.Log($"Initializing session with profile filter: '{_profileName}'");

        var collection = await _profileStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var profile = string.IsNullOrEmpty(_profileName)
            ? collection.ActiveProfile
            : collection.GetByName(_profileName);

        TuiDebugLog.Log($"Loaded profile: {profile?.DisplayIdentifier ?? "(none)"}, AuthMethod: {profile?.AuthMethod}");

        if (profile?.Environment?.Url != null)
        {
            _currentEnvironmentDisplayName = profile.Environment.DisplayName;
            TuiDebugLog.Log($"Warming connection to {profile.Environment.DisplayName} ({profile.Environment.Url})");

            // Fire-and-forget warming - don't block TUI startup
            // Errors are logged but don't prevent TUI from starting
#pragma warning disable PPDS013 // Fire-and-forget with explicit error handling
            _ = WarmPoolAsync(profile.Environment.Url, cancellationToken)
                .ContinueWith(t =>
                {
                    if (t.IsFaulted)
                    {
                        TuiDebugLog.Log($"Warm failed: {t.Exception?.InnerException?.Message}");
                    }
                    else
                    {
                        TuiDebugLog.Log("Connection pool warmed successfully");
                        EnvironmentChanged?.Invoke(profile.Environment.Url, profile.Environment.DisplayName);
                    }
                }, TaskScheduler.Default);
#pragma warning restore PPDS013
        }
        else
        {
            TuiDebugLog.Log("No environment configured - skipping connection warming");
        }
    }

    /// <summary>
    /// Actually warms the connection pool by initializing all seed connections.
    /// This triggers authentication (if needed) during TUI startup rather than first query.
    /// </summary>
    /// <param name="environmentUrl">The environment URL to warm.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private async Task WarmPoolAsync(string environmentUrl, CancellationToken cancellationToken)
    {
        TuiDebugLog.Log($"Warming pool (triggering auth if needed) for {environmentUrl}");

        var provider = await GetServiceProviderAsync(environmentUrl, cancellationToken)
            .ConfigureAwait(false);

        var pool = provider.GetRequiredService<IDataverseConnectionPool>();
        await pool.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        TuiDebugLog.Log("Pool warmed - seeds initialized");
    }

    /// <summary>
    /// Switches to a new environment, updating the profile and warming the new connection.
    /// </summary>
    /// <param name="environmentUrl">The new environment URL.</param>
    /// <param name="displayName">The display name for the environment.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task SetEnvironmentAsync(
        string environmentUrl,
        string? displayName = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(environmentUrl);

        TuiDebugLog.Log($"Switching environment to {displayName ?? environmentUrl}");

        // Update profile to persist the environment selection
        var profileService = GetProfileService();
        var profileName = string.IsNullOrEmpty(_profileName) ? null : _profileName;
        await profileService.SetEnvironmentAsync(profileName, environmentUrl, displayName, cancellationToken)
            .ConfigureAwait(false);

        // Invalidate old connection
        await InvalidateAsync().ConfigureAwait(false);

        // Update local state
        _currentEnvironmentDisplayName = displayName;

        // Warm new connection (fire-and-forget)
#pragma warning disable PPDS013 // Fire-and-forget with explicit error handling
        _ = GetServiceProviderAsync(environmentUrl, cancellationToken)
            .ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    TuiDebugLog.Log($"Warm failed after switch: {t.Exception?.InnerException?.Message}");
                }
                else
                {
                    TuiDebugLog.Log("New connection pool warmed successfully");
                }
            }, TaskScheduler.Default);
#pragma warning restore PPDS013

        // Notify listeners
        EnvironmentChanged?.Invoke(environmentUrl, displayName);
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
                TuiDebugLog.Log($"Reusing existing provider for {environmentUrl}");
                return _serviceProvider;
            }

            // Log why we're creating a new provider
            if (_serviceProvider == null)
            {
                TuiDebugLog.Log($"Creating new provider (no existing provider) for {environmentUrl}, profile={_profileName}");
            }
            else
            {
                TuiDebugLog.Log($"Creating new provider (URL mismatch: '{_currentEnvironmentUrl}' != '{environmentUrl}'), profile={_profileName}");
            }

            // Dispose existing provider if environment changed
            if (_serviceProvider != null)
            {
                await _serviceProvider.DisposeAsync().ConfigureAwait(false);
                _serviceProvider = null;
                _currentEnvironmentUrl = null;
            }

            // Create new provider using injected factory
            _serviceProvider = await _serviceProviderFactory.CreateAsync(
                string.IsNullOrEmpty(_profileName) ? null : _profileName,
                environmentUrl,
                _deviceCodeCallback,
                _beforeInteractiveAuth,
                cancellationToken).ConfigureAwait(false);

            _currentEnvironmentUrl = environmentUrl;
            TuiDebugLog.Log($"Provider created successfully for {environmentUrl}");
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
    /// Gets the query history service for the specified environment.
    /// </summary>
    /// <param name="environmentUrl">The environment URL to connect to.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The query history service.</returns>
    public async Task<IQueryHistoryService> GetQueryHistoryServiceAsync(
        string environmentUrl,
        CancellationToken cancellationToken = default)
    {
        var provider = await GetServiceProviderAsync(environmentUrl, cancellationToken).ConfigureAwait(false);
        return provider.GetRequiredService<IQueryHistoryService>();
    }

    /// <summary>
    /// Gets the export service for the specified environment.
    /// </summary>
    /// <param name="environmentUrl">The environment URL to connect to.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The export service.</returns>
    public async Task<IExportService> GetExportServiceAsync(
        string environmentUrl,
        CancellationToken cancellationToken = default)
    {
        var provider = await GetServiceProviderAsync(environmentUrl, cancellationToken).ConfigureAwait(false);
        return provider.GetRequiredService<IExportService>();
    }

    /// <summary>
    /// Invalidates the current session, disposing the connection pool.
    /// The next call to Get*Async will create a fresh provider.
    /// </summary>
    /// <param name="caller">Automatically populated with caller method name.</param>
    /// <param name="filePath">Automatically populated with caller file path.</param>
    /// <param name="lineNumber">Automatically populated with caller line number.</param>
    public async Task InvalidateAsync(
        [CallerMemberName] string? caller = null,
        [CallerFilePath] string? filePath = null,
        [CallerLineNumber] int lineNumber = 0)
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_serviceProvider != null)
            {
                var fileName = filePath != null ? Path.GetFileName(filePath) : "unknown";
                TuiDebugLog.Log($"Invalidating connection pool (from {caller} at {fileName}:{lineNumber})...");
                await _serviceProvider.DisposeAsync().ConfigureAwait(false);
                _serviceProvider = null;
                _currentEnvironmentUrl = null;
                TuiDebugLog.Log("Connection pool invalidated");
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Switches to a different profile, invalidating the connection pool and optionally re-warming.
    /// </summary>
    /// <param name="profileName">The new profile name.</param>
    /// <param name="environmentUrl">The environment URL for re-warming (optional).</param>
    /// <param name="environmentDisplayName">The environment display name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task SetActiveProfileAsync(
        string profileName,
        string? environmentUrl,
        string? environmentDisplayName = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileName);

        TuiDebugLog.Log($"Switching to profile: {profileName}");

        // Update the profile name for future service provider creation
        _profileName = profileName;

        // Invalidate old connection (uses old profile's credentials)
        await InvalidateAsync().ConfigureAwait(false);

        // Update display name if provided
        if (environmentDisplayName != null)
        {
            _currentEnvironmentDisplayName = environmentDisplayName;
        }

        // Re-warm with new profile credentials if environment is known
        if (!string.IsNullOrWhiteSpace(environmentUrl))
        {
            TuiDebugLog.Log($"Re-warming connection for {environmentDisplayName ?? environmentUrl}");

#pragma warning disable PPDS013 // Fire-and-forget with explicit error handling
            _ = GetServiceProviderAsync(environmentUrl, cancellationToken)
                .ContinueWith(t =>
                {
                    if (t.IsFaulted)
                    {
                        TuiDebugLog.Log($"Re-warm failed: {t.Exception?.InnerException?.Message}");
                    }
                    else
                    {
                        TuiDebugLog.Log("New profile connection warmed successfully");
                        EnvironmentChanged?.Invoke(environmentUrl, environmentDisplayName);
                    }
                }, TaskScheduler.Default);
#pragma warning restore PPDS013
        }
    }

    #region Local Services (no Dataverse connection required)

    /// <summary>
    /// Gets the profile service for profile management operations.
    /// This service uses local file storage and does not require a Dataverse connection.
    /// </summary>
    /// <returns>The profile service.</returns>
    public IProfileService GetProfileService()
    {
        return new ProfileService(_profileStore, NullLogger<ProfileService>.Instance);
    }

    /// <summary>
    /// Gets the environment service for environment discovery and selection.
    /// This service uses local file storage and does not require a Dataverse connection.
    /// </summary>
    /// <returns>The environment service.</returns>
    public IEnvironmentService GetEnvironmentService()
    {
        return new EnvironmentService(_profileStore, NullLogger<EnvironmentService>.Instance);
    }

    /// <summary>
    /// Gets the shared profile store for direct profile collection access.
    /// Prefer using <see cref="GetProfileService"/> for business operations.
    /// </summary>
    /// <returns>The shared profile store.</returns>
    public ProfileStore GetProfileStore()
    {
        return _profileStore;
    }

    /// <summary>
    /// Gets the theme service for color scheme and environment detection.
    /// </summary>
    /// <returns>The theme service.</returns>
    public ITuiThemeService GetThemeService()
    {
        return new TuiThemeService();
    }

    /// <summary>
    /// Gets the error service for centralized error handling.
    /// The service is lazily created and shared across the session lifetime.
    /// </summary>
    /// <returns>The error service.</returns>
    public ITuiErrorService GetErrorService()
    {
        return _errorService ??= new TuiErrorService();
    }

    #endregion

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        TuiDebugLog.Log("Disposing InteractiveSession...");
        _disposed = true;

        if (_serviceProvider != null)
        {
            TuiDebugLog.Log("Disposing ServiceProvider (connection pool)...");

            // Use timeout to prevent blocking on ServiceClient.Dispose()
            // Microsoft's ServiceClient can hang during disposal if there are pending HTTP requests
            try
            {
                using var cts = new CancellationTokenSource(DisposeTimeout);
                var disposeTask = _serviceProvider.DisposeAsync().AsTask();

                // Wait with timeout - if it doesn't complete, force-abandon
                var completedTask = await Task.WhenAny(disposeTask, Task.Delay(Timeout.Infinite, cts.Token))
                    .ConfigureAwait(false);

                if (completedTask == disposeTask)
                {
                    await disposeTask.ConfigureAwait(false); // Propagate any exception
                    TuiDebugLog.Log("ServiceProvider disposed");
                }
                else
                {
                    TuiDebugLog.Log("ServiceProvider disposal timed out - abandoning");
                }
            }
            catch (OperationCanceledException)
            {
                TuiDebugLog.Log("ServiceProvider disposal timed out - abandoning");
            }
            catch (Exception ex)
            {
                TuiDebugLog.Log($"ServiceProvider disposal error: {ex.Message}");
            }
            finally
            {
                _serviceProvider = null;
            }
        }

        _lock.Dispose();
        TuiDebugLog.Log("InteractiveSession disposed");
    }
}
