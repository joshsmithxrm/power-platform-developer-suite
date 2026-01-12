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
    /// <summary>
    /// Default timeout for pool creation (5 minutes to allow for device code flow).
    /// </summary>
    private static readonly TimeSpan DefaultPoolCreationTimeout = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, Lazy<Task<CachedPoolEntry>>> _pools = new();
    private readonly ConcurrentBag<Task> _disposalTasks = new();
    private readonly ILoggerFactory _loggerFactory;
    private readonly Func<CancellationToken, Task<ProfileCollection>> _loadProfilesAsync;
    private readonly TimeSpan _poolCreationTimeout;
    private int _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="DaemonConnectionPoolManager"/> class.
    /// </summary>
    /// <param name="loggerFactory">Optional logger factory. If null, uses NullLoggerFactory.</param>
    /// <param name="loadProfilesAsync">Optional profile loader for testability. If null, uses ProfileStore.</param>
    /// <param name="poolCreationTimeout">Optional timeout for pool creation. If null, uses 5 minutes.</param>
    public DaemonConnectionPoolManager(
        ILoggerFactory? loggerFactory = null,
        Func<CancellationToken, Task<ProfileCollection>>? loadProfilesAsync = null,
        TimeSpan? poolCreationTimeout = null)
    {
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _loadProfilesAsync = loadProfilesAsync ?? DefaultLoadProfilesAsync;
        _poolCreationTimeout = poolCreationTimeout ?? DefaultPoolCreationTimeout;
    }

    /// <summary>
    /// Default profile loader using ProfileStore.
    /// </summary>
    private static async Task<ProfileCollection> DefaultLoadProfilesAsync(CancellationToken cancellationToken)
    {
        using var store = new ProfileStore();
        return await store.LoadAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The <paramref name="deviceCodeCallback"/> is captured by the first caller for a given cache key.
    /// Subsequent callers awaiting the same pool creation will use the first caller's callback.
    /// This is acceptable because all callers for the same profile+environment combination should
    /// receive identical device code notifications.
    /// </remarks>
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
        // Note: CancellationToken.None is passed to CreatePoolEntryAsync because the Lazy<Task<>>
        // caches the result - we don't want the first caller's token to affect subsequent callers.
        var lazyEntry = _pools.GetOrAdd(cacheKey, _ => new Lazy<Task<CachedPoolEntry>>(
            () => CreatePoolEntryAsync(profileNames, environmentUrl, deviceCodeCallback, CancellationToken.None),
            LazyThreadSafetyMode.ExecutionAndPublication));

        // Add timeout wrapper to prevent indefinite blocking (e.g., device code flow)
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_poolCreationTimeout);

        try
        {
            var entry = await lazyEntry.Value.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
            return entry.Pool;
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // Our timeout fired, not caller's cancellation
            // Remove failed entry so next caller can retry
            _pools.TryRemove(cacheKey, out _);
            throw new TimeoutException($"Pool creation timed out after {_poolCreationTimeout.TotalSeconds} seconds for key: {cacheKey}");
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Pool creation failed - remove the failed entry so next caller can retry
            _pools.TryRemove(cacheKey, out _);
            throw;
        }
    }

    /// <inheritdoc/>
    public void InvalidateProfile(string profileName)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);

        if (string.IsNullOrWhiteSpace(profileName))
        {
            return;
        }

        // Snapshot keys to avoid enumeration during concurrent modification.
        // Race window is small: entries added after snapshot but before removal
        // will use old pool briefly until next invalidation. This is acceptable
        // since profile invalidation typically follows profile modification.
        var keysToRemove = _pools.Keys
            .Where(key => KeyContainsProfile(key, profileName))
            .ToList();

        foreach (var key in keysToRemove)
        {
            if (_pools.TryRemove(key, out var lazyEntry))
            {
                // Track disposal task for awaiting on shutdown
                _disposalTasks.Add(DisposeEntryAsync(lazyEntry).AsTask());
            }
        }
    }

    /// <inheritdoc/>
    public void InvalidateEnvironment(string environmentUrl)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);

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
                // Track disposal task for awaiting on shutdown
                _disposalTasks.Add(DisposeEntryAsync(lazyEntry).AsTask());
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

        // Dispose all active pools
        var entries = _pools.Values.ToList();
        _pools.Clear();

        foreach (var lazyEntry in entries)
        {
            await DisposeEntryAsync(lazyEntry).ConfigureAwait(false);
        }

        // Await any pending background disposals from invalidation calls
        var pendingDisposals = _disposalTasks.ToArray();
        if (pendingDisposals.Length > 0)
        {
            try
            {
                await Task.WhenAll(pendingDisposals).ConfigureAwait(false);
            }
            catch
            {
                // Ignore disposal errors during shutdown
            }
        }
    }

    /// <summary>
    /// Generates a cache key from profile names and environment URL.
    /// Profile names are sorted for consistent keying regardless of order.
    /// </summary>
    /// <param name="profileNames">The profile names to include in the key.</param>
    /// <param name="environmentUrl">The environment URL to include in the key.</param>
    /// <returns>A normalized cache key.</returns>
    internal static string GenerateCacheKey(IReadOnlyList<string> profileNames, string environmentUrl)
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
        var collection = await _loadProfilesAsync(cancellationToken).ConfigureAwait(false);
        var credentialStore = new NativeCredentialStore();

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
