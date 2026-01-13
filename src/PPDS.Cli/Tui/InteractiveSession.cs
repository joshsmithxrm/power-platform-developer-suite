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
    private IHotkeyRegistry? _hotkeyRegistry;

    /// <summary>
    /// Event raised when the environment changes (either via initialization or explicit switch).
    /// </summary>
    public event Action<string?, string?>? EnvironmentChanged;

    /// <summary>
    /// Event raised when the active profile changes.
    /// </summary>
    public event Action<string?>? ProfileChanged;

    /// <summary>
    /// Gets the current environment URL, or null if no connection has been established.
    /// </summary>
    public string? CurrentEnvironmentUrl => _currentEnvironmentUrl;

    /// <summary>
    /// Gets the current environment display name, or null if not set.
    /// </summary>
    public string? CurrentEnvironmentDisplayName => _currentEnvironmentDisplayName;

    /// <summary>
    /// Gets the current profile name, or null if using the active profile.
    /// </summary>
    public string? CurrentProfileName => string.IsNullOrEmpty(_profileName) ? null : _profileName;

    /// <summary>
    /// Gets the identity for the current profile (username or app ID), or null if unavailable.
    /// </summary>
    public string? CurrentProfileIdentity { get; private set; }

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
            : collection.GetByNameOrIndex(_profileName);

        TuiDebugLog.Log($"Loaded profile: {profile?.DisplayIdentifier ?? "(none)"}, AuthMethod: {profile?.AuthMethod}");

        // Set the identity for status bar display
        CurrentProfileIdentity = profile?.IdentityDisplay;

        // If using active profile (no explicit name specified), update _profileName
        // so CurrentProfileName returns the actual profile name instead of null
        if (string.IsNullOrEmpty(_profileName) && profile != null)
        {
            _profileName = profile.Name ?? collection.ActiveProfileName ?? $"[{profile.Index}]";
            TuiDebugLog.Log($"Using active profile: {_profileName}");
        }

        if (profile?.Environment?.Url != null)
        {
            _currentEnvironmentUrl = profile.Environment.Url;
            _currentEnvironmentDisplayName = profile.Environment.DisplayName;
            TuiDebugLog.Log($"Environment configured: {profile.Environment.DisplayName} ({profile.Environment.Url}) - will connect on first query");

            // Notify listeners of initial environment (but don't connect yet - lazy loading)
            // Connection/auth will happen when user runs their first query
            EnvironmentChanged?.Invoke(profile.Environment.Url, profile.Environment.DisplayName);
        }
        else
        {
            TuiDebugLog.Log("No environment configured - user will select environment manually");
        }
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
        _currentEnvironmentUrl = environmentUrl;
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
    /// Invalidates the current session and re-authenticates, creating a fresh connection.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Use this method when the current authentication token has expired (401 errors).
    /// This will:
    /// </para>
    /// <list type="number">
    /// <item>Dispose the current service provider (invalidating the connection pool)</item>
    /// <item>Create a new service provider with fresh authentication</item>
    /// </list>
    /// <para>
    /// The authentication flow (browser, device code) will be triggered as needed.
    /// </para>
    /// </remarks>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="InvalidOperationException">Thrown if no environment is currently configured.</exception>
    public async Task InvalidateAndReauthenticateAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(_currentEnvironmentUrl))
        {
            throw new InvalidOperationException("Cannot re-authenticate: no environment is currently configured.");
        }

        var environmentUrl = _currentEnvironmentUrl;

        TuiDebugLog.Log($"Re-authenticating session for {environmentUrl}...");

        // Invalidate the current session
        await InvalidateAsync().ConfigureAwait(false);

        // Create a new service provider - this will trigger authentication
        await GetServiceProviderAsync(environmentUrl, cancellationToken).ConfigureAwait(false);

        TuiDebugLog.Log("Re-authentication complete");
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

        // Load profile to get identity for status bar display
        var collection = await _profileStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var profile = collection.GetByNameOrIndex(profileName);
        CurrentProfileIdentity = profile?.IdentityDisplay;

        // Persist environment selection to profile if provided
        if (!string.IsNullOrWhiteSpace(environmentUrl))
        {
            var profileService = GetProfileService();
            await profileService.SetEnvironmentAsync(profileName, environmentUrl, environmentDisplayName, cancellationToken)
                .ConfigureAwait(false);
        }

        // Notify listeners of profile change
        ProfileChanged?.Invoke(_profileName);

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
            // Set URL immediately so it's available to consumers
            _currentEnvironmentUrl = environmentUrl;

            TuiDebugLog.Log($"Re-warming connection for {environmentDisplayName ?? environmentUrl}");

            // Notify listeners synchronously (like SetEnvironmentAsync does)
            EnvironmentChanged?.Invoke(environmentUrl, environmentDisplayName);

            // Then warm pool asynchronously
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

    /// <summary>
    /// Gets the hotkey registry for centralized keyboard shortcut management.
    /// The registry is lazily created and shared across the session lifetime.
    /// </summary>
    /// <returns>The hotkey registry.</returns>
    public IHotkeyRegistry GetHotkeyRegistry()
    {
        return _hotkeyRegistry ??= new HotkeyRegistry();
    }

    /// <summary>
    /// Gets the query history service for local history operations.
    /// This service uses local file storage and does not require a Dataverse connection.
    /// </summary>
    /// <returns>The query history service.</returns>
    public IQueryHistoryService GetQueryHistoryService()
    {
        return new QueryHistoryService(NullLogger<QueryHistoryService>.Instance);
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
