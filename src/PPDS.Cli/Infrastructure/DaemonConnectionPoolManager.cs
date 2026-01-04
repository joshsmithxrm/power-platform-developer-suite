using System.Collections.Concurrent;
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

namespace PPDS.Cli.Infrastructure;

/// <summary>
/// Manages cached connection pools for the daemon, keyed by profile+environment combination.
/// Pools are long-lived and reused across RPC calls.
/// </summary>
public sealed class DaemonConnectionPoolManager : IDaemonConnectionPoolManager
{
    private readonly ConcurrentDictionary<string, Lazy<Task<CachedPoolEntry>>> _pools = new();
    private readonly ILoggerFactory _loggerFactory;
    private int _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="DaemonConnectionPoolManager"/> class.
    /// </summary>
    /// <param name="loggerFactory">Optional logger factory. If null, uses NullLoggerFactory.</param>
    public DaemonConnectionPoolManager(ILoggerFactory? loggerFactory = null)
    {
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
    }

    /// <inheritdoc/>
    public async Task<IDataverseConnectionPool> GetOrCreatePoolAsync(
        IReadOnlyList<string> profileNames,
        string environmentUrl,
        Action<DeviceCodeInfo>? deviceCodeCallback = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);

        if (profileNames == null || profileNames.Count == 0)
        {
            throw new ArgumentException("At least one profile name is required.", nameof(profileNames));
        }

        if (string.IsNullOrWhiteSpace(environmentUrl))
        {
            throw new ArgumentException("Environment URL is required.", nameof(environmentUrl));
        }

        var cacheKey = GenerateCacheKey(profileNames, environmentUrl);

        // Use Lazy<Task<T>> pattern to prevent duplicate creation races
        var lazyEntry = _pools.GetOrAdd(cacheKey, _ => new Lazy<Task<CachedPoolEntry>>(
            () => CreatePoolEntryAsync(profileNames, environmentUrl, deviceCodeCallback, cancellationToken),
            LazyThreadSafetyMode.ExecutionAndPublication));

        var entry = await lazyEntry.Value.ConfigureAwait(false);
        return entry.Pool;
    }

    /// <inheritdoc/>
    public void InvalidateProfile(string profileName)
    {
        if (string.IsNullOrWhiteSpace(profileName))
        {
            return;
        }

        var keysToRemove = _pools.Keys
            .Where(key => KeyContainsProfile(key, profileName))
            .ToList();

        foreach (var key in keysToRemove)
        {
            if (_pools.TryRemove(key, out var lazyEntry))
            {
                // Dispose asynchronously in background to not block
                _ = DisposeEntryAsync(lazyEntry);
            }
        }
    }

    /// <inheritdoc/>
    public void InvalidateEnvironment(string environmentUrl)
    {
        if (string.IsNullOrWhiteSpace(environmentUrl))
        {
            return;
        }

        var normalizedUrl = NormalizeUrl(environmentUrl);
        var keysToRemove = _pools.Keys
            .Where(key => key.EndsWith($"|{normalizedUrl}", StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var key in keysToRemove)
        {
            if (_pools.TryRemove(key, out var lazyEntry))
            {
                _ = DisposeEntryAsync(lazyEntry);
            }
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        var entries = _pools.Values.ToList();
        _pools.Clear();

        foreach (var lazyEntry in entries)
        {
            await DisposeEntryAsync(lazyEntry).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Generates a cache key from profile names and environment URL.
    /// Profile names are sorted for consistent keying regardless of order.
    /// </summary>
    private static string GenerateCacheKey(IReadOnlyList<string> profileNames, string environmentUrl)
    {
        var sortedProfiles = string.Join(",", profileNames.OrderBy(p => p, StringComparer.OrdinalIgnoreCase));
        var normalizedUrl = NormalizeUrl(environmentUrl);
        return $"{sortedProfiles}|{normalizedUrl}";
    }

    /// <summary>
    /// Normalizes a URL for consistent cache key generation.
    /// </summary>
    private static string NormalizeUrl(string url)
    {
        return url.TrimEnd('/').ToLowerInvariant();
    }

    /// <summary>
    /// Checks if a cache key contains a specific profile name.
    /// </summary>
    private static bool KeyContainsProfile(string key, string profileName)
    {
        var pipeIndex = key.IndexOf('|');
        if (pipeIndex < 0)
        {
            return false;
        }

        var profilesPart = key[..pipeIndex];
        var profiles = profilesPart.Split(',');
        return profiles.Any(p => p.Equals(profileName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Creates a new pool entry for the given profiles and environment.
    /// </summary>
    private async Task<CachedPoolEntry> CreatePoolEntryAsync(
        IReadOnlyList<string> profileNames,
        string environmentUrl,
        Action<DeviceCodeInfo>? deviceCodeCallback,
        CancellationToken cancellationToken)
    {
        var store = new ProfileStore();
        var collection = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
        var credentialStore = new SecureCredentialStore();

        // Create connection sources for each profile
        var sources = new List<IConnectionSource>();
        try
        {
            foreach (var profileName in profileNames)
            {
                var profile = collection.GetByName(profileName)
                    ?? throw new InvalidOperationException($"Profile '{profileName}' not found.");

                var source = new ProfileConnectionSource(
                    profile,
                    environmentUrl,
                    maxPoolSize: 52,
                    deviceCodeCallback: deviceCodeCallback,
                    environmentDisplayName: null,
                    credentialStore: credentialStore);

                var adapter = new ProfileConnectionSourceAdapter(source);
                sources.Add(adapter);
            }

            // Build service provider with pool
            var serviceProvider = CreateProviderFromSources(
                sources.ToArray(),
                credentialStore);

            var pool = serviceProvider.GetRequiredService<IDataverseConnectionPool>();

            return new CachedPoolEntry
            {
                ServiceProvider = serviceProvider,
                Pool = pool,
                ProfileNames = profileNames.ToHashSet(StringComparer.OrdinalIgnoreCase),
                EnvironmentUrl = environmentUrl,
                CredentialStore = credentialStore
            };
        }
        catch
        {
            // Cleanup on failure
            foreach (var source in sources)
            {
                source.Dispose();
            }
            credentialStore.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Creates a service provider from connection sources.
    /// This is similar to ProfileServiceFactory.CreateProviderFromSources but simplified for daemon use.
    /// </summary>
    private ServiceProvider CreateProviderFromSources(
        IConnectionSource[] sources,
        ISecureCredentialStore credentialStore)
    {
        var services = new ServiceCollection();

        // Configure minimal logging for daemon
        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Warning);
            builder.AddProvider(new LoggerFactoryProvider(_loggerFactory));
        });

        // Register credential store for disposal with service provider
        services.AddSingleton<ISecureCredentialStore>(credentialStore);

        var dataverseOptions = new DataverseOptions();
        services.AddSingleton<IOptions<DataverseOptions>>(new OptionsWrapper<DataverseOptions>(dataverseOptions));

        var poolOptions = new ConnectionPoolOptions
        {
            Enabled = true,
            DisableAffinityCookie = true
        };

        // Register shared services (IThrottleTracker, IBulkOperationExecutor, IMetadataService)
        services.RegisterDataverseServices();

        // Connection pool with factory delegate
        services.AddSingleton<IDataverseConnectionPool>(sp =>
            new DataverseConnectionPool(
                sources,
                sp.GetRequiredService<IThrottleTracker>(),
                poolOptions,
                sp.GetRequiredService<ILogger<DataverseConnectionPool>>()));

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Disposes a lazy pool entry if it has been created.
    /// </summary>
    private static async ValueTask DisposeEntryAsync(Lazy<Task<CachedPoolEntry>> lazyEntry)
    {
        if (!lazyEntry.IsValueCreated)
        {
            return;
        }

        try
        {
            var entry = await lazyEntry.Value.ConfigureAwait(false);
            await entry.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // Ignore disposal errors
        }
    }

    /// <summary>
    /// Holds a cached pool entry with its associated service provider.
    /// </summary>
    private sealed class CachedPoolEntry : IAsyncDisposable
    {
        public required ServiceProvider ServiceProvider { get; init; }
        public required IDataverseConnectionPool Pool { get; init; }
        public required IReadOnlySet<string> ProfileNames { get; init; }
        public required string EnvironmentUrl { get; init; }
        public required ISecureCredentialStore CredentialStore { get; init; }
        public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

        public async ValueTask DisposeAsync()
        {
            await ServiceProvider.DisposeAsync().ConfigureAwait(false);
        }
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
            // Don't dispose the factory - it's owned externally
        }
    }
}
