using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PPDS.Auth.Credentials;
using PPDS.Auth.Pooling;
using PPDS.Auth.Profiles;
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

    /// <summary>
    /// Initializes a new instance of the <see cref="McpToolContext"/> class.
    /// </summary>
    /// <param name="poolManager">The connection pool manager.</param>
    /// <param name="profileStore">The profile store for loading/saving auth profiles.</param>
    /// <param name="credentialStore">The secure credential store.</param>
    /// <param name="loggerFactory">Optional logger factory.</param>
    public McpToolContext(
        IMcpConnectionPoolManager poolManager,
        ProfileStore profileStore,
        ISecureCredentialStore credentialStore,
        ILoggerFactory? loggerFactory = null)
    {
        _poolManager = poolManager ?? throw new ArgumentNullException(nameof(poolManager));
        _profileStore = profileStore ?? throw new ArgumentNullException(nameof(profileStore));
        _credentialStore = credentialStore ?? throw new ArgumentNullException(nameof(credentialStore));
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
    }

    /// <summary>
    /// Gets the active authentication profile.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The active profile.</returns>
    /// <exception cref="InvalidOperationException">Thrown if no profile is active.</exception>
    public async Task<AuthProfile> GetActiveProfileAsync(CancellationToken cancellationToken = default)
    {
        var store = _profileStore;
        var collection = await store.LoadAsync(cancellationToken).ConfigureAwait(false);

        var profile = collection.ActiveProfile
            ?? throw new InvalidOperationException(
                "No active profile configured. Run 'ppds auth create' to create a profile.");

        return profile;
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

        if (profile.Environment == null)
        {
            throw new InvalidOperationException(
                "No environment selected. Run 'ppds env select <url>' to select an environment.");
        }

        var profileName = profile.Name ?? profile.DisplayIdentifier;
        return await _poolManager.GetOrCreatePoolAsync(
            new[] { profileName },
            profile.Environment.Url,
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
    /// Use this method when you need access to services like IMetadataService or ISqlQueryService.
    /// </remarks>
    public async Task<ServiceProvider> CreateServiceProviderAsync(CancellationToken cancellationToken = default)
    {
        var profile = await GetActiveProfileAsync(cancellationToken).ConfigureAwait(false);

        if (profile.Environment == null)
        {
            throw new InvalidOperationException(
                "No environment selected. Run 'ppds env select <url>' to select an environment.");
        }

        var sources = new List<IConnectionSource>();

        try
        {
            var source = new ProfileConnectionSource(
                profile,
                profile.Environment.Url,
                maxPoolSize: 52,
                deviceCodeCallback: null,
                environmentDisplayName: profile.Environment.DisplayName,
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

        // Register credential store (DI owns lifecycle, not this child provider).
        services.AddSingleton<ISecureCredentialStore>(credentialStore);

        var dataverseOptions = new DataverseOptions();
        services.AddSingleton<IOptions<DataverseOptions>>(new OptionsWrapper<DataverseOptions>(dataverseOptions));

        var poolOptions = new ConnectionPoolOptions
        {
            Enabled = true,
            DisableAffinityCookie = true
        };

        // Register shared services (IThrottleTracker, IBulkOperationExecutor, IMetadataService, etc.).
        services.RegisterDataverseServices();

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
