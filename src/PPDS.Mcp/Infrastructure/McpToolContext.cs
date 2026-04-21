using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PPDS.Auth.Credentials;
using PPDS.Auth.Pooling;
using PPDS.Auth.Profiles;
using PPDS.Cli.Services;
using PPDS.Dataverse.Configuration;
using PPDS.Dataverse.DependencyInjection;
using PPDS.Dataverse.Pooling;
using PPDS.Dataverse.Resilience;

namespace PPDS.Mcp.Infrastructure;

/// <summary>
/// Shared context for all MCP tools providing access to connection pools and services.
/// </summary>
/// <remarks>
/// This class encapsulates the common operations needed by MCP tools:
/// - Loading and validating the active profile
/// - Getting or creating connection pools
/// - Creating service providers with full DI for service access
/// </remarks>
public sealed class McpToolContext
{
    private readonly IMcpConnectionPoolManager _poolManager;
    private readonly ProfileStore _profileStore;
    private readonly ISecureCredentialStore _credentialStore;
    private readonly ILoggerFactory _loggerFactory;
    private readonly McpSessionOptions _sessionOptions;
    private AuthProfile? _lockedProfile;
    private readonly SemaphoreSlim _lockGate = new(1, 1);

    /// <summary>
    /// Initializes a new instance of the <see cref="McpToolContext"/> class.
    /// </summary>
    /// <param name="poolManager">The connection pool manager.</param>
    /// <param name="profileStore">The profile store for loading/saving auth profiles.</param>
    /// <param name="credentialStore">The secure credential store.</param>
    /// <param name="sessionOptions">Session configuration options.</param>
    /// <param name="loggerFactory">Optional logger factory.</param>
    public McpToolContext(
        IMcpConnectionPoolManager poolManager,
        ProfileStore profileStore,
        ISecureCredentialStore credentialStore,
        McpSessionOptions sessionOptions,
        ILoggerFactory? loggerFactory = null)
    {
        _poolManager = poolManager ?? throw new ArgumentNullException(nameof(poolManager));
        _profileStore = profileStore ?? throw new ArgumentNullException(nameof(profileStore));
        _credentialStore = credentialStore ?? throw new ArgumentNullException(nameof(credentialStore));
        _sessionOptions = sessionOptions ?? throw new ArgumentNullException(nameof(sessionOptions));
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
    }

    /// <summary>Whether DML operations are disabled for this session.</summary>
    public bool IsReadOnly => _sessionOptions.ReadOnly;

    /// <summary>The overridden environment URL, if specified via --environment.</summary>
    public string? EnvironmentUrlOverride => _sessionOptions.Environment;

    /// <summary>
    /// Validates whether switching to the given environment URL is allowed.
    /// </summary>
    /// <exception cref="InvalidOperationException">If the environment is not in the allowlist.</exception>
    public void ValidateEnvironmentSwitch(string environmentUrl)
    {
        if (!_sessionOptions.IsEnvironmentAllowed(environmentUrl))
        {
            var msg = _sessionOptions.AllowedEnvironments.Count == 0
                ? "Environment switching is disabled for this MCP session. The session is locked to the initial environment."
                : $"Environment '{environmentUrl}' is not in the allowed list. Allowed: {string.Join(", ", _sessionOptions.AllowedEnvironments)}";
            throw new InvalidOperationException(msg);
        }
    }

    /// <summary>
    /// Gets the active authentication profile.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The active profile.</returns>
    /// <exception cref="InvalidOperationException">Thrown if no profile is active.</exception>
    public async Task<AuthProfile> GetActiveProfileAsync(CancellationToken cancellationToken = default)
    {
        // Session locking: resolve the profile once, then reuse for the session
        if (_lockedProfile != null)
            return _lockedProfile;

        await _lockGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_lockedProfile != null)
                return _lockedProfile;

            var collection = await _profileStore.LoadAsync(cancellationToken).ConfigureAwait(false);

            AuthProfile? profile;
            if (_sessionOptions.Profile != null)
            {
                profile = collection.GetByNameOrIndex(_sessionOptions.Profile)
                    ?? throw new InvalidOperationException(
                        $"Profile '{_sessionOptions.Profile}' not found. Available profiles: {string.Join(", ", collection.All.Select(p => p.Name ?? $"Profile {p.Index}"))}");
            }
            else
            {
                profile = collection.ActiveProfile
                    ?? throw new InvalidOperationException(
                        "No active profile configured. Run 'ppds auth create' to create a profile.");
            }

            _lockedProfile = profile;
            return profile;
        }
        finally
        {
            _lockGate.Release();
        }
    }

    /// <summary>
    /// Gets the profile collection.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The profile collection.</returns>
    public async Task<ProfileCollection> GetProfileCollectionAsync(CancellationToken cancellationToken = default)
    {
        var store = _profileStore;
        return await store.LoadAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets or creates a connection pool for the active profile's environment.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A connection pool for the active profile's environment.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown if no profile is active or no environment is selected.
    /// </exception>
    public async Task<IDataverseConnectionPool> GetPoolAsync(CancellationToken cancellationToken = default)
    {
        var profile = await GetActiveProfileAsync(cancellationToken).ConfigureAwait(false);

        var environmentUrl = _sessionOptions.Environment ?? profile.Environment?.Url
            ?? throw new InvalidOperationException(
                "No environment selected. Run 'ppds env select <url>' to select an environment.");

        var profileName = profile.Name ?? profile.DisplayIdentifier;
        return await _poolManager.GetOrCreatePoolAsync(
            new[] { profileName },
            environmentUrl,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Creates a service provider with full DI for the active profile.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A service provider configured for the active profile's environment.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown if no profile is active or no environment is selected.
    /// </exception>
    /// <remarks>
    /// The returned service provider should be disposed after use.
    /// For most operations, prefer <see cref="GetPoolAsync"/> which uses cached pools.
    /// Use this method when you need access to services like IMetadataQueryService or ISqlQueryService.
    /// </remarks>
    public async Task<ServiceProvider> CreateServiceProviderAsync(CancellationToken cancellationToken = default)
    {
        var profile = await GetActiveProfileAsync(cancellationToken).ConfigureAwait(false);

        var environmentUrl = _sessionOptions.Environment ?? profile.Environment?.Url
            ?? throw new InvalidOperationException(
                "No environment selected. Run 'ppds env select <url>' to select an environment.");

        var environmentDisplayName = _sessionOptions.Environment != null
            ? _sessionOptions.Environment
            : profile.Environment?.DisplayName;

        var sources = new List<IConnectionSource>();

        try
        {
            var source = new ProfileConnectionSource(
                profile,
                environmentUrl,
                maxPoolSize: 52,
                deviceCodeCallback: null,
                environmentDisplayName: environmentDisplayName,
                credentialStore: _credentialStore);

            var adapter = new ProfileConnectionSourceAdapter(source);
            sources.Add(adapter);

            return CreateProviderFromSources(sources.ToArray(), _credentialStore);
        }
        catch
        {
            foreach (var source in sources)
            {
                source.Dispose();
            }
            throw;
        }
    }

    /// <summary>
    /// Saves the profile collection after modifications (e.g., environment selection).
    /// </summary>
    /// <param name="collection">The modified profile collection.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task SaveProfileCollectionAsync(ProfileCollection collection, CancellationToken cancellationToken = default)
    {
        var store = _profileStore;
        await store.SaveAsync(collection, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Invalidates the cached pool for the current environment after environment change.
    /// </summary>
    /// <param name="environmentUrl">The environment URL that was changed.</param>
    public void InvalidateEnvironment(string environmentUrl)
    {
        _poolManager.InvalidateEnvironment(environmentUrl);
    }

    /// <summary>
    /// Creates a service provider from connection sources.
    /// </summary>
    private ServiceProvider CreateProviderFromSources(
        IConnectionSource[] sources,
        ISecureCredentialStore credentialStore)
    {
        var services = new ServiceCollection();

        // Configure minimal logging.
        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Warning);
            builder.AddProvider(new LoggerFactoryProvider(_loggerFactory));
        });

        // Register credential store FIRST (DI owns lifecycle, not this child provider).
        // AddCliApplicationServices → AddAuthServices uses TryAddSingleton, so this
        // per-tool-context instance wins over the default NativeCredentialStore.
        services.AddSingleton<ISecureCredentialStore>(credentialStore);

        var dataverseOptions = new DataverseOptions();
        services.AddSingleton<IOptions<DataverseOptions>>(new OptionsWrapper<DataverseOptions>(dataverseOptions));

        var poolOptions = new ConnectionPoolOptions
        {
            Enabled = true,
            DisableAffinityCookie = true
        };

        // Register shared services (IThrottleTracker, IBulkOperationExecutor, IMetadataQueryService, etc.).
        services.RegisterDataverseServices();

        // Register CLI application services — the 12 domain services (IPluginTraceService,
        // ISolutionService, IWebResourceService, …) relocated out of PPDS.Dataverse now live
        // here, plus IShakedownGuard. MCP tools resolve these from this child provider, so
        // they must be registered here too (not only in PPDS.Mcp.Program.cs).
        services.AddCliApplicationServices();

        // Connection pool with factory delegate.
        services.AddSingleton<IDataverseConnectionPool>(sp =>
            new DataverseConnectionPool(
                sources,
                sp.GetRequiredService<IThrottleTracker>(),
                poolOptions,
                sp.GetRequiredService<ILogger<DataverseConnectionPool>>()));

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Simple logger provider that wraps an existing ILoggerFactory.
    /// </summary>
    private sealed class LoggerFactoryProvider : ILoggerProvider
    {
        private readonly ILoggerFactory _factory;

        public LoggerFactoryProvider(ILoggerFactory factory)
        {
            _factory = factory;
        }

        public ILogger CreateLogger(string categoryName) => _factory.CreateLogger(categoryName);

        public void Dispose()
        {
            // Don't dispose the factory - it's owned externally.
        }
    }
}
