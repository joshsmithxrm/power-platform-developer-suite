using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PPDS.Auth.Credentials;
using PPDS.Auth.Profiles;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Services;
using PPDS.Cli.Services.Environment;
using PPDS.Cli.Services.Export;
using PPDS.Cli.Services.History;
using PPDS.Cli.Services.Profile;
using PPDS.Cli.Services.Query;
using PPDS.Cli.Services.Settings;
using PPDS.Cli.Tui.Infrastructure;
using PPDS.Dataverse.Query;
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
    private string? _displayedProfileName;
    private string? _displayedProfileIdentity;
    private bool _disposed;
    private readonly EnvironmentConfigStore _envConfigStore;
    private readonly EnvironmentConfigService _envConfigService;
    private readonly TuiStateStore _tuiStateStore;
    private ProfileResolutionService? _profileResolutionService;
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
    /// Gets the currently displayed profile name (reflects the active tab's profile).
    /// Falls back to the session default profile if no tab-specific override is set.
    /// </summary>
    public string? CurrentProfileName =>
        !string.IsNullOrEmpty(_displayedProfileName) ? _displayedProfileName :
        !string.IsNullOrEmpty(_profileName) ? _profileName : null;

    /// <summary>
    /// Gets the session default profile name, used for creating new tabs.
    /// </summary>
    public string? DefaultProfileName => string.IsNullOrEmpty(_profileName) ? null : _profileName;

    /// <summary>
    /// Gets the identity for the currently displayed profile (username or app ID), or null if unavailable.
    /// </summary>
    public string? CurrentProfileIdentity => _displayedProfileIdentity;

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
    /// <param name="tuiStateStore">Shared TUI state store for persisting screen filter state.</param>
    /// <param name="serviceProviderFactory">Factory for creating service providers (null for default).</param>
    /// <param name="deviceCodeCallback">Callback for device code display.</param>
    /// <param name="beforeInteractiveAuth">Callback invoked before browser opens for interactive auth.
    /// Returns the user's choice (OpenBrowser, UseDeviceCode, or Cancel).</param>
    public InteractiveSession(
        string? profileName,
        ProfileStore profileStore,
        EnvironmentConfigStore envConfigStore,
        TuiStateStore tuiStateStore,
        IServiceProviderFactory? serviceProviderFactory = null,
        Action<DeviceCodeInfo>? deviceCodeCallback = null,
        Func<Action<DeviceCodeInfo>?, PreAuthDialogResult>? beforeInteractiveAuth = null)
    {
        _profileName = profileName ?? string.Empty;
        _profileStore = profileStore ?? throw new ArgumentNullException(nameof(profileStore));
        _envConfigStore = envConfigStore ?? throw new ArgumentNullException(nameof(envConfigStore));
        _tuiStateStore = tuiStateStore ?? throw new ArgumentNullException(nameof(tuiStateStore));
        _serviceProviderFactory = serviceProviderFactory ?? new ProfileBasedServiceProviderFactory();
        _deviceCodeCallback = deviceCodeCallback;
        _beforeInteractiveAuth = beforeInteractiveAuth;
        _envConfigService = new EnvironmentConfigService(_envConfigStore);

        // Initialize lazy service instances (thread-safe by default)
        _profileService = new Lazy<IProfileService>(() => new ProfileService(_profileStore, NullLogger<ProfileService>.Instance, envConfigStore: _envConfigStore));
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
        var envConfigs = await _envConfigStore.LoadAsync(cancellationToken).ConfigureAwait(false);

        var collection = await _profileStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var profile = string.IsNullOrEmpty(_profileName)
            ? collection.ActiveProfile
            : collection.GetByNameOrIndex(_profileName);

        TuiDebugLog.Log($"Loaded profile: {profile?.DisplayIdentifier ?? "(none)"}, AuthMethod: {profile?.AuthMethod}");

        if (profile == null)
        {
            TuiDebugLog.Log("No active profile — skipping initialization. User will select a profile.");
            return;
        }

        // Build label resolver for cross-environment queries ([LABEL].entity syntax)
        // Only needed when we have a profile that can actually execute queries
        _profileResolutionService = new ProfileResolutionService(envConfigs.Environments);

        // Set the identity for status bar display
        _displayedProfileIdentity = profile.IdentityDisplay;

        // If using active profile (no explicit name specified), update _profileName
        // so CurrentProfileName returns the actual profile name instead of null
        if (string.IsNullOrEmpty(_profileName))
        {
            _profileName = profile.Name ?? collection.ActiveProfileName ?? $"[{profile.Index}]";
            TuiDebugLog.Log($"Using active profile: {_profileName}");
        }

        if (profile.Environment?.Url != null)
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
    /// Updates the displayed profile without changing the session default.
    /// Use this when switching tabs to sync the status bar with the active tab's profile.
    /// </summary>
    /// <param name="profileName">The profile name to display.</param>
    /// <param name="profileIdentity">The profile identity (username or app ID) to display.</param>
    public void UpdateDisplayedProfile(string? profileName, string? profileIdentity)
    {
        if (_displayedProfileName == profileName && _displayedProfileIdentity == profileIdentity)
            return;

        _displayedProfileName = profileName;
        _displayedProfileIdentity = profileIdentity;
        ProfileChanged?.Invoke(profileName);
    }

    /// <summary>
    /// Notifies listeners that environment configuration has changed.
    /// </summary>
    public void NotifyConfigChanged() => ConfigChanged?.Invoke();

    private static string ProviderKey(string profileName, string environmentUrl) =>
        $"{profileName}\0{environmentUrl}";

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
    /// Gets or creates a service provider for the specified environment using the session's default profile.
    /// </summary>
    /// <param name="environmentUrl">The environment URL to connect to.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The service provider with connection pool.</returns>
    public Task<ServiceProvider> GetServiceProviderAsync(
        string environmentUrl,
        CancellationToken cancellationToken = default)
    {
        return GetServiceProviderAsync(environmentUrl, null, cancellationToken);
    }

    /// <summary>
    /// Gets or creates a service provider for the specified profile and environment.
    /// Providers are cached by (profileName, environmentUrl) — different profiles get independent providers.
    /// </summary>
    /// <param name="environmentUrl">The environment URL to connect to.</param>
    /// <param name="profileName">Profile name (null to use the session's default profile).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The service provider with connection pool.</returns>
    public async Task<ServiceProvider> GetServiceProviderAsync(
        string environmentUrl,
        string? profileName,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var resolvedProfile = !string.IsNullOrEmpty(profileName) ? profileName : _profileName;
        var cacheKey = ProviderKey(resolvedProfile, environmentUrl);

        // Fast path: already cached
        if (_providers.TryGetValue(cacheKey, out var existing))
        {
            TuiDebugLog.Log($"Reusing existing provider for {environmentUrl} (profile={resolvedProfile})");
            return existing;
        }

        // Slow path: create new provider (serialized to prevent duplicate creation)
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Double-check after lock
            if (_providers.TryGetValue(cacheKey, out existing))
            {
                return existing;
            }

            TuiDebugLog.Log($"Creating new provider for {environmentUrl}, profile={resolvedProfile}");

            var provider = await _serviceProviderFactory.CreateAsync(
                string.IsNullOrEmpty(resolvedProfile) ? null : resolvedProfile,
                environmentUrl,
                _deviceCodeCallback,
                _beforeInteractiveAuth,
                cancellationToken).ConfigureAwait(false);

            _providers[cacheKey] = provider;
            TuiDebugLog.Log($"Provider created successfully for {environmentUrl} (profile={resolvedProfile})");

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
    /// Gets the SQL query service for the specified environment using the session's default profile.
    /// </summary>
    public Task<ISqlQueryService> GetSqlQueryServiceAsync(
        string environmentUrl,
        CancellationToken cancellationToken = default)
    {
        return GetSqlQueryServiceAsync(environmentUrl, null, cancellationToken);
    }

    /// <summary>
    /// Gets the SQL query service for the specified profile and environment.
    /// The underlying connection pool is reused across calls.
    /// </summary>
    /// <param name="environmentUrl">The environment URL to connect to.</param>
    /// <param name="profileName">Profile name (null to use the session's default profile).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The SQL query service.</returns>
    public async Task<ISqlQueryService> GetSqlQueryServiceAsync(
        string environmentUrl,
        string? profileName,
        CancellationToken cancellationToken)
    {
        var provider = await GetServiceProviderAsync(environmentUrl, profileName, cancellationToken).ConfigureAwait(false);
        var service = provider.GetRequiredService<ISqlQueryService>();

        // Wire cross-environment support: [LABEL].entity resolves via profile labels
        if (service is SqlQueryService concrete && _profileResolutionService != null)
        {
            var capturedProfile = profileName;
            concrete.RemoteExecutorFactory = label =>
            {
                var config = _profileResolutionService.ResolveByLabel(label);
                if (config?.Url == null) return null;
#pragma warning disable PPDS012 // Planner is synchronous; provider cache makes this effectively instant
                var remoteProvider = GetServiceProviderAsync(config.Url, capturedProfile, CancellationToken.None).GetAwaiter().GetResult();
#pragma warning restore PPDS012
                return remoteProvider.GetRequiredService<IQueryExecutor>();
            };

            // Wire environment-specific DML safety settings
            concrete.ProfileResolver = _profileResolutionService;
            var envConfig = await _envConfigStore.GetConfigAsync(environmentUrl, cancellationToken)
                .ConfigureAwait(false);
            if (envConfig != null)
            {
                concrete.EnvironmentSafetySettings = envConfig.SafetySettings;
                var envType = envConfig.Type ?? EnvironmentType.Unknown;
                concrete.EnvironmentProtectionLevel = envConfig.Protection
                    ?? DmlSafetyGuard.DetectProtectionLevel(envType);
            }
        }

        return service;
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
    /// Invalidates cached providers. Supports filtering by environment URL, profile name, or both.
    /// If neither is specified, all providers are disposed.
    /// </summary>
    /// <param name="environmentUrl">Optional URL to filter by environment.</param>
    /// <param name="profileName">Optional profile name to filter by profile.</param>
    /// <param name="caller">Automatically populated with caller method name.</param>
    /// <param name="filePath">Automatically populated with caller file path.</param>
    /// <param name="lineNumber">Automatically populated with caller line number.</param>
    public async Task InvalidateAsync(
        string? environmentUrl = null,
        string? profileName = null,
        [CallerMemberName] string? caller = null,
        [CallerFilePath] string? filePath = null,
        [CallerLineNumber] int lineNumber = 0)
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            var fileName = filePath != null ? Path.GetFileName(filePath) : "unknown";

            if (environmentUrl != null || profileName != null)
            {
                // Invalidate matching providers
                var keysToRemove = _providers.Keys.Where(key =>
                {
                    var sepIndex = key.IndexOf('\0');
                    var keyProfile = sepIndex >= 0 ? key[..sepIndex] : string.Empty;
                    var keyEnv = sepIndex >= 0 ? key[(sepIndex + 1)..] : key;

                    var profileMatch = profileName == null ||
                        string.Equals(keyProfile, profileName, StringComparison.OrdinalIgnoreCase);
                    var envMatch = environmentUrl == null ||
                        string.Equals(keyEnv, environmentUrl, StringComparison.OrdinalIgnoreCase);
                    return profileMatch && envMatch;
                }).ToList();

                foreach (var key in keysToRemove)
                {
                    if (_providers.TryRemove(key, out var provider))
                    {
                        TuiDebugLog.Log($"Invalidating provider {key} (from {caller} at {fileName}:{lineNumber})");
                        await provider.DisposeAsync().ConfigureAwait(false);
                    }
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
    /// <param name="profileName">Profile name to re-authenticate (null for session default).</param>
    /// <param name="environmentUrl">Environment URL to re-authenticate (null for displayed environment).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="InvalidOperationException">Thrown if no environment is currently configured.</exception>
    public async Task InvalidateAndReauthenticateAsync(
        string? profileName = null,
        string? environmentUrl = null,
        CancellationToken cancellationToken = default)
    {
        var resolvedEnv = environmentUrl ?? _activeEnvironmentUrl;
        if (string.IsNullOrEmpty(resolvedEnv))
        {
            throw new InvalidOperationException("Cannot re-authenticate: no environment is currently configured.");
        }

        var resolvedProfile = !string.IsNullOrEmpty(profileName) ? profileName : _profileName;

        TuiDebugLog.Log($"Re-authenticating for {resolvedEnv} (profile={resolvedProfile})...");

        // Invalidate the specific (profile, environment) provider
        await InvalidateAsync(resolvedEnv, resolvedProfile).ConfigureAwait(false);

        // Create a new service provider - this will trigger authentication
        await GetServiceProviderAsync(resolvedEnv, resolvedProfile, cancellationToken).ConfigureAwait(false);

        TuiDebugLog.Log("Re-authentication complete");
    }

    /// <summary>
    /// Switches to a different profile. Updates the session default and the displayed profile.
    /// Providers are cached per (profile, environment) so no invalidation is needed.
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

        // Update the session default profile for future tabs
        _profileName = profileName;

        // Load profile to get identity for status bar display
        var collection = await _profileStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var profile = collection.GetByNameOrIndex(profileName);
        var identity = profile?.IdentityDisplay;

        // Update displayed profile (for status bar)
        _displayedProfileName = profileName;
        _displayedProfileIdentity = identity;

        // Persist environment selection to profile if provided
        if (!string.IsNullOrWhiteSpace(environmentUrl))
        {
            var profileService = GetProfileService();
            await profileService.SetEnvironmentAsync(profileName, environmentUrl, environmentDisplayName, cancellationToken)
                .ConfigureAwait(false);
        }

        // Notify listeners of profile change
        ProfileChanged?.Invoke(_profileName);

        // Update display name if provided
        if (environmentDisplayName != null)
        {
            _activeEnvironmentDisplayName = environmentDisplayName;
        }

        // Pre-warm with new profile credentials if environment is known
        if (!string.IsNullOrWhiteSpace(environmentUrl))
        {
            _activeEnvironmentUrl = environmentUrl;

            TuiDebugLog.Log($"Pre-warming connection for {environmentDisplayName ?? environmentUrl} (profile={profileName})");

            EnvironmentChanged?.Invoke(environmentUrl, environmentDisplayName);

            GetErrorService().FireAndForget(
                GetServiceProviderAsync(environmentUrl, profileName, cancellationToken),
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

    /// <summary>
    /// Gets the TUI state store for persisting screen filter selections across sessions.
    /// </summary>
    /// <returns>The shared TUI state store.</returns>
    public TuiStateStore GetTuiStateStore()
    {
        return _tuiStateStore;
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
        _tuiStateStore.Dispose();
        _lock.Dispose();
        TuiDebugLog.Log("InteractiveSession disposed");
    }
}
