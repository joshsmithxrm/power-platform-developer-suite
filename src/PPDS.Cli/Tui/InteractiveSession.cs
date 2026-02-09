using System.Collections.Concurrent;
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
using PPDS.Dataverse.Metadata;
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

    private readonly ConcurrentDictionary<string, ServiceProvider> _providers = new(StringComparer.OrdinalIgnoreCase);
    private string? _activeEnvironmentUrl;
    private string? _activeEnvironmentDisplayName;
    private bool _disposed;
    private readonly EnvironmentConfigStore _envConfigStore;
    private readonly EnvironmentConfigService _envConfigService;
    private readonly Lazy<ITuiErrorService> _errorService;
    private readonly Lazy<IHotkeyRegistry> _hotkeyRegistry;
    private readonly Lazy<IProfileService> _profileService;
    private readonly Lazy<IEnvironmentService> _environmentService;
    private readonly Lazy<ITuiThemeService> _themeService;
    private readonly Lazy<IQueryHistoryService> _queryHistoryService;
    private readonly Lazy<IExportService> _exportService;

    /// <summary>
    /// Event raised when the environment changes (either via initialization or explicit switch).
    /// </summary>
    public event Action<string?, string?>? EnvironmentChanged;

    /// <summary>
    /// Event raised when the active profile changes.
    /// </summary>
    public event Action<string?>? ProfileChanged;

    /// <summary>
    /// Event raised when environment configuration (label, type, color) is saved.
    /// </summary>
    public event Action? ConfigChanged;

    /// <summary>
    /// Gets the current environment URL, or null if no connection has been established.
    /// </summary>
    public string? CurrentEnvironmentUrl => _activeEnvironmentUrl;

    /// <summary>
    /// Gets the current environment display name, or null if not set.
    /// </summary>
    public string? CurrentEnvironmentDisplayName => _activeEnvironmentDisplayName;

    /// <summary>
    /// Gets the current profile name, or null if using the active profile.
    /// </summary>
    public string? CurrentProfileName => string.IsNullOrEmpty(_profileName) ? null : _profileName;

    /// <summary>
    /// Gets the identity for the current profile (username or app ID), or null if unavailable.
    /// </summary>
    public string? CurrentProfileIdentity { get; private set; }

    /// <summary>
    /// Gets the environment configuration service for label, type, and color resolution.
    /// </summary>
    public IEnvironmentConfigService EnvironmentConfigService => _envConfigService;

    /// <summary>
    /// Creates a new interactive session for the specified profile.
    /// </summary>
    /// <param name="profileName">The profile name (null for active profile).</param>
    /// <param name="profileStore">Shared profile store instance.</param>
    /// <param name="envConfigStore">Shared environment config store instance.</param>
    /// <param name="serviceProviderFactory">Factory for creating service providers (null for default).</param>
    /// <param name="deviceCodeCallback">Callback for device code display.</param>
    /// <param name="beforeInteractiveAuth">Callback invoked before browser opens for interactive auth.
    /// Returns the user's choice (OpenBrowser, UseDeviceCode, or Cancel).</param>
    public InteractiveSession(
        string? profileName,
        ProfileStore profileStore,
        EnvironmentConfigStore envConfigStore,
        IServiceProviderFactory? serviceProviderFactory = null,
        Action<DeviceCodeInfo>? deviceCodeCallback = null,
        Func<Action<DeviceCodeInfo>?, PreAuthDialogResult>? beforeInteractiveAuth = null)
    {
        _profileName = profileName ?? string.Empty;
        _profileStore = profileStore ?? throw new ArgumentNullException(nameof(profileStore));
        _envConfigStore = envConfigStore ?? throw new ArgumentNullException(nameof(envConfigStore));
        _serviceProviderFactory = serviceProviderFactory ?? new ProfileBasedServiceProviderFactory();
        _deviceCodeCallback = deviceCodeCallback;
        _beforeInteractiveAuth = beforeInteractiveAuth;
        _envConfigService = new EnvironmentConfigService(_envConfigStore);

        // Initialize lazy service instances (thread-safe by default)
        _profileService = new Lazy<IProfileService>(() => new ProfileService(_profileStore, NullLogger<ProfileService>.Instance));
        _environmentService = new Lazy<IEnvironmentService>(() => new EnvironmentService(_profileStore, NullLogger<EnvironmentService>.Instance));
        _themeService = new Lazy<ITuiThemeService>(() => new TuiThemeService(_envConfigService));
        _errorService = new Lazy<ITuiErrorService>(() => new TuiErrorService());
        _hotkeyRegistry = new Lazy<IHotkeyRegistry>(() => new HotkeyRegistry());
        _queryHistoryService = new Lazy<IQueryHistoryService>(() => new QueryHistoryService(NullLogger<QueryHistoryService>.Instance));
        _exportService = new Lazy<IExportService>(() => new ExportService(NullLogger<ExportService>.Instance));
    }

    /// <summary>
    /// Initializes the session by loading the active profile and warming the connection pool.
    /// Call this early (e.g., during TUI startup) so the connection is ready when needed.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        TuiDebugLog.Log($"Initializing session with profile filter: '{_profileName}'");

        // Pre-load environment config so sync-over-async calls in UI thread are cache hits
        await _envConfigStore.LoadAsync(cancellationToken).ConfigureAwait(false);

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
            _activeEnvironmentUrl = profile.Environment.Url;
            _activeEnvironmentDisplayName = profile.Environment.DisplayName;
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
    /// Updates the displayed environment without persisting to profile or pre-warming providers.
    /// Use this when switching tabs to sync the status bar with the active tab's environment.
    /// </summary>
    /// <param name="environmentUrl">The environment URL to display.</param>
    /// <param name="displayName">The display name for the environment.</param>
    public void UpdateDisplayedEnvironment(string? environmentUrl, string? displayName)
    {
        if (_activeEnvironmentUrl == environmentUrl && _activeEnvironmentDisplayName == displayName)
            return;

        _activeEnvironmentUrl = environmentUrl;
        _activeEnvironmentDisplayName = displayName;
        EnvironmentChanged?.Invoke(environmentUrl, displayName);
    }

    /// <summary>
    /// Notifies listeners that environment configuration has changed.
    /// </summary>
    public void NotifyConfigChanged() => ConfigChanged?.Invoke();

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

        TuiDebugLog.Log($"Switching active environment to {displayName ?? environmentUrl}");

        var profileService = GetProfileService();
        var profileName = string.IsNullOrEmpty(_profileName) ? null : _profileName;
        await profileService.SetEnvironmentAsync(profileName, environmentUrl, displayName, cancellationToken)
            .ConfigureAwait(false);

        // Update active environment (for status bar display)
        // Do NOT invalidate — other tabs may still be using old environment's provider
        _activeEnvironmentUrl = environmentUrl;
        _activeEnvironmentDisplayName = displayName;

        // Pre-warm the new environment's provider
        GetErrorService().FireAndForget(
            GetServiceProviderAsync(environmentUrl, cancellationToken),
            "WarmNewEnvironment");

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

        // Fast path: already cached
        if (_providers.TryGetValue(environmentUrl, out var existing))
        {
            TuiDebugLog.Log($"Reusing existing provider for {environmentUrl}");
            return existing;
        }

        // Slow path: create new provider (serialized to prevent duplicate creation)
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Double-check after lock
            if (_providers.TryGetValue(environmentUrl, out existing))
            {
                return existing;
            }

            TuiDebugLog.Log($"Creating new provider for {environmentUrl}, profile={_profileName}");

            var provider = await _serviceProviderFactory.CreateAsync(
                string.IsNullOrEmpty(_profileName) ? null : _profileName,
                environmentUrl,
                _deviceCodeCallback,
                _beforeInteractiveAuth,
                cancellationToken).ConfigureAwait(false);

            _providers[environmentUrl] = provider;
            TuiDebugLog.Log($"Provider created successfully for {environmentUrl}");

            // Fire-and-forget metadata preload so IntelliSense has entity names ready
            var cachedMetadata = provider.GetService<ICachedMetadataProvider>();
            if (cachedMetadata != null)
            {
                TuiDebugLog.Log($"Starting metadata preload for {environmentUrl}");
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await cachedMetadata.PreloadAsync(cancellationToken).ConfigureAwait(false);
                        TuiDebugLog.Log($"Metadata preload completed for {environmentUrl}");
                    }
                    catch (OperationCanceledException)
                    {
                        TuiDebugLog.Log($"Metadata preload cancelled for {environmentUrl}");
                    }
                    catch (Exception ex)
                    {
                        TuiDebugLog.Log($"Metadata preload failed for {environmentUrl}: {ex.Message}");
                    }
                }, cancellationToken);
            }

            return provider;
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
    /// Gets the cached metadata provider for the specified environment.
    /// The provider caches entity, attribute, and relationship metadata for IntelliSense.
    /// </summary>
    /// <param name="environmentUrl">The environment URL to connect to.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The cached metadata provider.</returns>
    public async Task<ICachedMetadataProvider> GetCachedMetadataProviderAsync(
        string environmentUrl,
        CancellationToken cancellationToken = default)
    {
        var provider = await GetServiceProviderAsync(environmentUrl, cancellationToken).ConfigureAwait(false);
        return provider.GetRequiredService<ICachedMetadataProvider>();
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
    /// Invalidates cached providers. If <paramref name="environmentUrl"/> is specified,
    /// only that provider is disposed. Otherwise all providers are disposed.
    /// </summary>
    /// <param name="environmentUrl">Optional URL to invalidate a specific provider.</param>
    /// <param name="caller">Automatically populated with caller method name.</param>
    /// <param name="filePath">Automatically populated with caller file path.</param>
    /// <param name="lineNumber">Automatically populated with caller line number.</param>
    public async Task InvalidateAsync(
        string? environmentUrl = null,
        [CallerMemberName] string? caller = null,
        [CallerFilePath] string? filePath = null,
        [CallerLineNumber] int lineNumber = 0)
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            var fileName = filePath != null ? Path.GetFileName(filePath) : "unknown";

            if (environmentUrl != null)
            {
                // Invalidate specific environment
                if (_providers.TryRemove(environmentUrl, out var provider))
                {
                    TuiDebugLog.Log($"Invalidating provider for {environmentUrl} (from {caller} at {fileName}:{lineNumber})");
                    await provider.DisposeAsync().ConfigureAwait(false);
                    TuiDebugLog.Log($"Provider for {environmentUrl} invalidated");
                }
            }
            else
            {
                // Invalidate all
                TuiDebugLog.Log($"Invalidating all {_providers.Count} providers (from {caller} at {fileName}:{lineNumber})");
                foreach (var kvp in _providers)
                {
                    try
                    {
                        await kvp.Value.DisposeAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        TuiDebugLog.Log($"Error disposing provider for {kvp.Key}: {ex.Message}");
                    }
                }
                _providers.Clear();
                TuiDebugLog.Log("All providers invalidated");
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
        if (string.IsNullOrEmpty(_activeEnvironmentUrl))
        {
            throw new InvalidOperationException("Cannot re-authenticate: no environment is currently configured.");
        }

        var environmentUrl = _activeEnvironmentUrl;

        TuiDebugLog.Log($"Re-authenticating session for {environmentUrl}...");

        // Invalidate the specific environment's provider
        await InvalidateAsync(environmentUrl).ConfigureAwait(false);

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

        // Profile change invalidates ALL cached providers since credentials differ
        await InvalidateAsync().ConfigureAwait(false);

        // Update display name if provided
        if (environmentDisplayName != null)
        {
            _activeEnvironmentDisplayName = environmentDisplayName;
        }

        // Re-warm with new profile credentials if environment is known
        if (!string.IsNullOrWhiteSpace(environmentUrl))
        {
            // Set URL immediately so it's available to consumers
            _activeEnvironmentUrl = environmentUrl;

            TuiDebugLog.Log($"Re-warming connection for {environmentDisplayName ?? environmentUrl}");

            // Notify listeners synchronously (like SetEnvironmentAsync does)
            EnvironmentChanged?.Invoke(environmentUrl, environmentDisplayName);

            // Then warm pool asynchronously
            GetErrorService().FireAndForget(
                GetServiceProviderAsync(environmentUrl, cancellationToken),
                "WarmAfterProfileSwitch");
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
        return _profileService.Value;
    }

    /// <summary>
    /// Gets the environment service for environment discovery and selection.
    /// This service uses local file storage and does not require a Dataverse connection.
    /// </summary>
    /// <returns>The environment service.</returns>
    public IEnvironmentService GetEnvironmentService()
    {
        return _environmentService.Value;
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
        return _themeService.Value;
    }

    /// <summary>
    /// Gets the error service for centralized error handling.
    /// The service is lazily created and shared across the session lifetime.
    /// </summary>
    /// <returns>The error service.</returns>
    public ITuiErrorService GetErrorService()
    {
        return _errorService.Value;
    }

    /// <summary>
    /// Gets the hotkey registry for centralized keyboard shortcut management.
    /// The registry is lazily created and shared across the session lifetime.
    /// </summary>
    /// <returns>The hotkey registry.</returns>
    public IHotkeyRegistry GetHotkeyRegistry()
    {
        return _hotkeyRegistry.Value;
    }

    /// <summary>
    /// Gets the query history service for local history operations.
    /// This service uses local file storage and does not require a Dataverse connection.
    /// </summary>
    /// <returns>The query history service.</returns>
    public IQueryHistoryService GetQueryHistoryService()
    {
        return _queryHistoryService.Value;
    }

    /// <summary>
    /// Gets the export service for local export operations.
    /// This service uses local file storage and does not require a Dataverse connection.
    /// </summary>
    /// <returns>The export service.</returns>
    public IExportService GetExportService()
    {
        return _exportService.Value;
    }

    #endregion

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;

        TuiDebugLog.Log("Disposing InteractiveSession...");
        _disposed = true;

        if (_providers.Count > 0)
        {
            TuiDebugLog.Log($"Disposing {_providers.Count} ServiceProviders...");
            foreach (var kvp in _providers)
            {
                try
                {
                    using var cts = new CancellationTokenSource(DisposeTimeout);
                    var disposeTask = kvp.Value.DisposeAsync().AsTask();
                    var completed = await Task.WhenAny(disposeTask, Task.Delay(Timeout.Infinite, cts.Token))
                        .ConfigureAwait(false);

                    if (completed == disposeTask)
                    {
                        await disposeTask.ConfigureAwait(false);
                        TuiDebugLog.Log($"Provider for {kvp.Key} disposed");
                    }
                    else
                    {
                        TuiDebugLog.Log($"Provider for {kvp.Key} disposal timed out - abandoning");
                    }
                }
                catch (OperationCanceledException)
                {
                    TuiDebugLog.Log($"Provider for {kvp.Key} disposal timed out - abandoning");
                }
                catch (Exception ex)
                {
                    TuiDebugLog.Log($"Provider for {kvp.Key} disposal error: {ex.Message}");
                }
            }
            _providers.Clear();
        }

        _envConfigStore.Dispose();
        _lock.Dispose();
        TuiDebugLog.Log("InteractiveSession disposed");
    }
}
