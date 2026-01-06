using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.ServiceModel;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using PPDS.Dataverse.Client;
using PPDS.Dataverse.Configuration;
using PPDS.Dataverse.DependencyInjection;
using PPDS.Dataverse.Pooling.Strategies;
using PPDS.Dataverse.BulkOperations;
using PPDS.Dataverse.Resilience;
using PPDS.Dataverse.Security;

namespace PPDS.Dataverse.Pooling
{
    /// <summary>
    /// High-performance connection pool for Dataverse with multi-connection support.
    /// </summary>
    public sealed class DataverseConnectionPool : IDataverseConnectionPool
    {
        private readonly ILogger<DataverseConnectionPool> _logger;
        private readonly IReadOnlyList<IConnectionSource> _sources;
        private readonly ConnectionPoolOptions _poolOptions;
        private readonly IThrottleTracker _throttleTracker;
        private readonly IConnectionSelectionStrategy _selectionStrategy;
        private readonly ConcurrentDictionary<string, ServiceClient> _seedClients = new();
        private readonly ConcurrentDictionary<string, int> _sourceDop = new();
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _seedCreationLocks = new();

        private readonly ConcurrentDictionary<string, ConcurrentQueue<PooledClient>> _pools;
        private readonly ConcurrentDictionary<string, int> _activeConnections;
        private readonly ConcurrentDictionary<string, long> _requestCounts;
        private readonly SemaphoreSlim _connectionSemaphore;
        private readonly int _totalPoolCapacity;
        private readonly object _poolLock = new();

        private readonly CancellationTokenSource _validationCts;
        private readonly Task _validationTask;

        private long _totalRequestsServed;
        private long _invalidConnectionCount;
        private long _authFailureCount;
        private long _connectionFailureCount;
        private int _disposed;
        private static bool _performanceSettingsApplied;
        private static readonly object _performanceSettingsLock = new();
        private BatchParallelismCoordinator? _batchCoordinator;
        private readonly object _batchCoordinatorLock = new();

        /// <summary>
        /// Initializes a new connection pool from connection sources.
        /// </summary>
        /// <param name="sources">
        /// One or more connection sources providing seed clients.
        /// Each source's seed will be cloned to create pool members.
        /// </param>
        /// <param name="throttleTracker">Throttle tracking service.</param>
        /// <param name="poolOptions">Pool configuration options.</param>
        /// <param name="logger">Logger instance.</param>
        public DataverseConnectionPool(
            IEnumerable<IConnectionSource> sources,
            IThrottleTracker throttleTracker,
            ConnectionPoolOptions poolOptions,
            ILogger<DataverseConnectionPool> logger)
        {
            ArgumentNullException.ThrowIfNull(sources);
            ArgumentNullException.ThrowIfNull(throttleTracker);
            ArgumentNullException.ThrowIfNull(poolOptions);
            ArgumentNullException.ThrowIfNull(logger);

            _sources = sources.ToList().AsReadOnly();
            _throttleTracker = throttleTracker;
            _poolOptions = poolOptions;
            _logger = logger;

            if (_sources.Count == 0)
            {
                throw new ArgumentException("At least one connection source is required.", nameof(sources));
            }

            _pools = new ConcurrentDictionary<string, ConcurrentQueue<PooledClient>>();
            _activeConnections = new ConcurrentDictionary<string, int>();
            _requestCounts = new ConcurrentDictionary<string, long>();

            // Initialize pools for each source
            foreach (var source in _sources)
            {
                _pools[source.Name] = new ConcurrentQueue<PooledClient>();
                _activeConnections[source.Name] = 0;
                _requestCounts[source.Name] = 0;
            }

            // Apply performance settings before creating connections
            ApplyPerformanceSettings();

            // Create seeds first to discover DOP from server before sizing the semaphore
            InitializeSeedsAndDiscoverDop();

            // Now size the semaphore based on actual DOP (not the 52 hard limit)
            _totalPoolCapacity = CalculateTotalPoolCapacity();
            _connectionSemaphore = new SemaphoreSlim(_totalPoolCapacity, _totalPoolCapacity);

            _selectionStrategy = CreateSelectionStrategy();

            // Start background validation if enabled
            _validationCts = new CancellationTokenSource();
            if (_poolOptions.EnableValidation)
            {
                _validationTask = StartValidationLoopAsync(_validationCts.Token);
            }
            else
            {
                _validationTask = Task.CompletedTask;
            }

            // Warm up pool with 1 connection per source
            WarmUpConnections();

            _logger.LogInformation(
                "DataverseConnectionPool initialized. Sources: {SourceCount}, TotalDOP: {TotalDOP}, Strategy: {Strategy}",
                _sources.Count,
                _totalPoolCapacity,
                _poolOptions.SelectionStrategy);
        }

        /// <summary>
        /// Initializes a new connection pool from DataverseOptions configuration.
        /// This constructor maintains backward compatibility with existing DI registration.
        /// </summary>
        [Obsolete("Use the IConnectionSource-based constructor for new code.")]
        public DataverseConnectionPool(
            IOptions<DataverseOptions> options,
            IThrottleTracker throttleTracker,
            ILogger<DataverseConnectionPool> logger)
            : this(
                CreateSourcesFromOptions(options?.Value ?? throw new ArgumentNullException(nameof(options))),
                throttleTracker,
                options.Value.Pool,
                logger)
        {
        }

        private static IEnumerable<IConnectionSource> CreateSourcesFromOptions(DataverseOptions options)
        {
            if (options.Connections == null || options.Connections.Count == 0)
            {
                var environmentName = options.Connections?.FirstOrDefault()?.SourceEnvironment;
                throw ConfigurationException.NoConnectionsConfigured(environmentName);
            }

            // Validate connections before creating sources
            foreach (var connection in options.Connections)
            {
                ValidateConnection(connection);
            }

            return options.Connections.Select(c => new ConnectionStringSource(c));
        }

        /// <inheritdoc />
        public bool IsEnabled => _poolOptions.Enabled;

        /// <inheritdoc />
        public int SourceCount => _sources.Count;

        /// <inheritdoc />
        public BatchParallelismCoordinator BatchCoordinator
        {
            get
            {
                if (_batchCoordinator != null) return _batchCoordinator;

                lock (_batchCoordinatorLock)
                {
                    return _batchCoordinator ??= new BatchParallelismCoordinator(this);
                }
            }
        }

        /// <inheritdoc />
        public PoolStatistics Statistics => GetStatistics();

        /// <inheritdoc />
        public int GetTotalRecommendedParallelism()
        {
            // Sum live DOP values from seed clients
            int total = 0;
            foreach (var source in _sources)
            {
                total += GetLiveSourceDop(source.Name);
            }
            return total;
        }

        /// <inheritdoc />
        public int GetLiveSourceDop(string sourceName)
        {
            // Read live value from seed client if available
            if (_seedClients.TryGetValue(sourceName, out var seed))
            {
                return Math.Clamp(seed.RecommendedDegreesOfParallelism, 1, ConnectionPoolOptions.MicrosoftHardLimitPerUser);
            }

            // Fall back to cached value if seed exists in cache
            if (_sourceDop.TryGetValue(sourceName, out var cached))
            {
                return cached;
            }

            // Conservative default
            return 4;
        }

        /// <inheritdoc />
        public int GetActiveConnectionCount(string sourceName)
        {
            return _activeConnections.GetValueOrDefault(sourceName, 0);
        }

        /// <inheritdoc />
        public async Task<IPooledClient?> TryGetClientWithCapacityAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();

            if (!IsEnabled)
            {
                return CreateDirectClient(null);
            }

            // Find a source that has DOP headroom and is not throttled
            foreach (var source in _sources)
            {
                var active = _activeConnections.GetValueOrDefault(source.Name, 0);
                var dop = GetLiveSourceDop(source.Name);

                if (active < dop && !_throttleTracker.IsThrottled(source.Name))
                {
                    // This source has capacity - try to get a client from it
                    try
                    {
                        return await GetClientFromSourceAsync(source.Name, null, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Failed to get client from {Source}, trying next", source.Name);
                        // Continue to next source
                    }
                }
            }

            // No source has capacity
            return null;
        }

        /// <summary>
        /// Gets a client specifically from the named source.
        /// </summary>
        private async Task<IPooledClient> GetClientFromSourceAsync(
            string sourceName,
            DataverseClientOptions? options,
            CancellationToken cancellationToken)
        {
            // Acquire semaphore
            var acquired = await _connectionSemaphore.WaitAsync(_poolOptions.AcquireTimeout, cancellationToken);
            if (!acquired)
            {
                throw new TimeoutException($"Timed out waiting for connection from {sourceName}");
            }

            try
            {
                var pool = _pools[sourceName];

                // Try to get from pool first
                while (pool.TryDequeue(out var existingClient))
                {
                    if (IsValidConnection(existingClient))
                    {
                        _activeConnections.AddOrUpdate(sourceName, 1, (_, v) => v + 1);
                        Interlocked.Increment(ref _totalRequestsServed);
                        _requestCounts.AddOrUpdate(sourceName, 1, (_, v) => v + 1);

                        existingClient.UpdateLastUsed();
                        if (options != null)
                        {
                            existingClient.ApplyOptions(options);
                        }

                        _logger.LogDebug(
                            "Retrieved connection from pool. ConnectionId: {ConnectionId}, Name: {ConnectionName}",
                            existingClient.ConnectionId, existingClient.ConnectionName);

                        return existingClient;
                    }

                    // Invalid - dispose and try next
                    existingClient.ForceDispose();
                }

                // Pool is empty, create new connection
                var newClient = CreateNewConnection(sourceName);
                _activeConnections.AddOrUpdate(sourceName, 1, (_, v) => v + 1);
                Interlocked.Increment(ref _totalRequestsServed);
                _requestCounts.AddOrUpdate(sourceName, 1, (_, v) => v + 1);

                if (options != null)
                {
                    newClient.ApplyOptions(options);
                }

                return newClient;
            }
            catch
            {
                _connectionSemaphore.Release();
                throw;
            }
        }

        /// <inheritdoc />
        public async Task<IPooledClient> GetClientAsync(
            DataverseClientOptions? options = null,
            string? excludeConnectionName = null,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();

            if (!IsEnabled)
            {
                return CreateDirectClient(options);
            }

            // Loop until we get a connection
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Phase 1: Wait for non-throttled connection BEFORE acquiring semaphore
                // This prevents holding semaphore slots while waiting for throttle to clear
                await WaitForNonThrottledConnectionAsync(excludeConnectionName, cancellationToken);

                // Phase 2: Acquire semaphore
                var acquired = await _connectionSemaphore.WaitAsync(_poolOptions.AcquireTimeout, cancellationToken);
                if (!acquired)
                {
                    throw new PoolExhaustedException(
                        GetTotalActiveConnections(),
                        _totalPoolCapacity,
                        _poolOptions.AcquireTimeout);
                }

                try
                {
                    // Phase 3: Select connection and check throttle (quick, no waiting)
                    var connectionName = SelectConnection(excludeConnectionName);

                    // Race check: throttle status could have changed while waiting for semaphore
                    if (_throttleTracker.IsThrottled(connectionName))
                    {
                        // Connection became throttled - release semaphore and retry
                        _connectionSemaphore.Release();
                        continue;
                    }

                    // Phase 4: Get the actual connection from pool
                    return GetConnectionFromPoolCore(connectionName, options);
                }
                catch (DataverseConnectionException ex) when (ex.Message.Contains("throttled"))
                {
                    // CreateNewConnection blocked due to throttle (race condition).
                    // Release semaphore and retry - WaitForNonThrottledConnectionAsync will wait.
                    _connectionSemaphore.Release();
                    _logger.LogDebug("Clone blocked by throttle, retrying after wait. {Message}", ex.Message);
                    continue;
                }
                catch
                {
                    _connectionSemaphore.Release();
                    throw;
                }
            }
        }

        /// <inheritdoc />
        public IPooledClient GetClient(DataverseClientOptions? options = null)
        {
            ThrowIfDisposed();

            if (!IsEnabled)
            {
                return CreateDirectClient(options);
            }

            var acquired = _connectionSemaphore.Wait(_poolOptions.AcquireTimeout);
            if (!acquired)
            {
                throw new PoolExhaustedException(
                    GetTotalActiveConnections(),
                    _totalPoolCapacity,
                    _poolOptions.AcquireTimeout);
            }

            try
            {
                return GetConnectionFromPool(options);
            }
            catch
            {
                _connectionSemaphore.Release();
                throw;
            }
        }

        /// <summary>
        /// Waits until at least one connection is not throttled.
        /// This method does NOT hold the semaphore, allowing other requests to also wait.
        /// </summary>
        /// <exception cref="ServiceProtectionException">
        /// Thrown when all connections are throttled and the wait time exceeds MaxRetryAfterTolerance.
        /// </exception>
        private async Task WaitForNonThrottledConnectionAsync(
            string? excludeConnectionName,
            CancellationToken cancellationToken)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Check if any non-excluded source is available (not throttled)
                var hasAvailable = _sources
                    .Where(s => string.IsNullOrEmpty(excludeConnectionName) ||
                                !string.Equals(s.Name, excludeConnectionName, StringComparison.OrdinalIgnoreCase))
                    .Any(s => !_throttleTracker.IsThrottled(s.Name));

                if (hasAvailable)
                {
                    return; // At least one connection is available
                }

                // All connections are throttled - check tolerance before waiting
                var waitTime = _throttleTracker.GetShortestExpiry();
                if (waitTime <= TimeSpan.Zero)
                {
                    return; // Throttle already expired
                }

                // Check if wait exceeds tolerance
                if (_poolOptions.MaxRetryAfterTolerance.HasValue &&
                    waitTime > _poolOptions.MaxRetryAfterTolerance.Value)
                {
                    throw new ServiceProtectionException(
                        $"All connections throttled. Wait time ({waitTime:g}) exceeds tolerance ({_poolOptions.MaxRetryAfterTolerance.Value:g}).");
                }

                // Add a small buffer for timing
                waitTime += TimeSpan.FromMilliseconds(100);

                _logger.LogInformation(
                    "All connections throttled. Waiting {WaitTime} for throttle to clear...",
                    waitTime);

                await Task.Delay(waitTime, cancellationToken);

                _logger.LogInformation("Throttle wait completed. Resuming operations.");

                // Loop back and check again
            }
        }

        private PooledClient GetConnectionFromPool(DataverseClientOptions? options)
        {
            var connectionName = SelectConnection(excludeConnectionName: null);
            return GetConnectionFromPoolCore(connectionName, options);
        }

        private PooledClient GetConnectionFromPoolCore(string connectionName, DataverseClientOptions? options)
        {
            var pool = _pools[connectionName];

            // Loop to find valid connection, draining any invalid ones
            while (true)
            {
                PooledClient? existingClient = null;
                lock (_poolLock)
                {
                    if (pool.IsEmpty || !pool.TryDequeue(out existingClient))
                    {
                        break; // Pool empty, exit to create new connection
                    }
                }

                if (IsValidConnection(existingClient))
                {
                    _activeConnections.AddOrUpdate(connectionName, 1, (_, v) => v + 1);
                    Interlocked.Increment(ref _totalRequestsServed);
                    _requestCounts.AddOrUpdate(connectionName, 1, (_, v) => v + 1);

                    existingClient.UpdateLastUsed();
                    if (options != null)
                    {
                        existingClient.ApplyOptions(options);
                    }

                    _logger.LogDebug(
                        "Retrieved connection from pool. ConnectionId: {ConnectionId}, Name: {ConnectionName}",
                        existingClient.ConnectionId,
                        connectionName);

                    return existingClient;
                }

                // Invalid connection, dispose and continue loop to try next
                existingClient.ForceDispose();
                _logger.LogDebug("Disposed invalid connection. ConnectionId: {ConnectionId}", existingClient.ConnectionId);
            }

            // Pool is empty (or drained of invalid connections), create new connection
            var newClient = CreateNewConnection(connectionName);
            _activeConnections.AddOrUpdate(connectionName, 1, (_, v) => v + 1);
            Interlocked.Increment(ref _totalRequestsServed);
            _requestCounts.AddOrUpdate(connectionName, 1, (_, v) => v + 1);

            if (options != null)
            {
                newClient.ApplyOptions(options);
            }

            return newClient;
        }

        private string SelectConnection(string? excludeConnectionName)
        {
            // Create DataverseConnection-like objects for the strategy
            // The strategy expects DataverseConnection but we can create a compatible list
            var connections = _sources.Select(s => new DataverseConnection(s.Name)
            {
                MaxPoolSize = s.MaxPoolSize
            }).ToList().AsReadOnly();

            // If an exclusion is requested and we have multiple sources, filter
            IReadOnlyList<DataverseConnection> filteredConnections;
            if (!string.IsNullOrEmpty(excludeConnectionName) && connections.Count > 1)
            {
                filteredConnections = connections
                    .Where(c => !string.Equals(c.Name, excludeConnectionName, StringComparison.OrdinalIgnoreCase))
                    .ToList()
                    .AsReadOnly();

                // If filtering would leave no connections, use all
                if (filteredConnections.Count == 0)
                {
                    filteredConnections = connections;
                }
            }
            else
            {
                filteredConnections = connections;
            }

            var activeDict = _activeConnections.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

            return _selectionStrategy.SelectConnection(filteredConnections, _throttleTracker, activeDict);
        }

        private ServiceClient GetSeedClient(string connectionName)
        {
            // Fast path - seed already exists and is ready
            if (_seedClients.TryGetValue(connectionName, out var existingSeed) && existingSeed.IsReady)
            {
                return existingSeed;
            }

            // Slow path - need to create/recreate seed, use lock to prevent races
            var seedLock = _seedCreationLocks.GetOrAdd(connectionName, _ => new SemaphoreSlim(1, 1));

            seedLock.Wait();
            try
            {
                // Double-check after acquiring lock
                if (_seedClients.TryGetValue(connectionName, out existingSeed) && existingSeed.IsReady)
                {
                    return existingSeed;
                }

                var source = _sources.First(s => s.Name == connectionName);
                ServiceClient? seed = null;
                Exception? lastException = null;

                // Retry loop for transient failures (e.g., token refresh)
                const int maxAttempts = 3;
                for (int attempt = 1; attempt <= maxAttempts; attempt++)
                {
                    try
                    {
                        seed = source.GetSeedClient();

                        // Wait briefly for connection to become ready if needed
                        if (!seed.IsReady)
                        {
                            _logger.LogDebug(
                                "Seed not ready for {ConnectionName}, waiting... (attempt {Attempt}/{MaxAttempts})",
                                connectionName, attempt, maxAttempts);

                            // Give it a moment - interactive auth may still be completing
                            Thread.Sleep(500);

                            if (!seed.IsReady)
                            {
                                throw new InvalidOperationException(
                                    $"Seed connection not ready for {connectionName} after wait. LastError: {seed.LastError}");
                            }
                        }

                        break; // Success
                    }
                    catch (Exception ex) when (attempt < maxAttempts)
                    {
                        lastException = ex;
                        _logger.LogWarning(ex,
                            "Seed creation attempt {Attempt}/{MaxAttempts} failed for {ConnectionName}, retrying after backoff",
                            attempt, maxAttempts, connectionName);

                        // Exponential backoff: 1s, 2s
                        Thread.Sleep(1000 * attempt);
                    }
                    catch (Exception ex)
                    {
                        lastException = ex;
                        _logger.LogError(ex,
                            "Seed creation failed after {MaxAttempts} attempts for {ConnectionName}",
                            maxAttempts, connectionName);
                    }
                }

                if (seed == null || !seed.IsReady)
                {
                    throw new DataverseConnectionException(
                        connectionName,
                        $"Failed to create seed after {maxAttempts} attempts",
                        lastException ?? new InvalidOperationException("Seed creation failed with no exception"));
                }

                // Initialize DOP for this source from the seed client
                var dop = seed.RecommendedDegreesOfParallelism;
                var cappedDop = Math.Clamp(dop, 1, ConnectionPoolOptions.MicrosoftHardLimitPerUser);
                _sourceDop[connectionName] = cappedDop;

                // Store the seed (overwrites any stale entry)
                _seedClients[connectionName] = seed;

                _logger.LogDebug(
                    "Initialized DOP for {ConnectionName}: {Dop} (capped at {Cap})",
                    connectionName, cappedDop, ConnectionPoolOptions.MicrosoftHardLimitPerUser);

                return seed;
            }
            finally
            {
                seedLock.Release();
            }
        }

        private PooledClient CreateNewConnection(string connectionName)
        {
            _logger.LogDebug("Creating new connection for {ConnectionName}", connectionName);

            // Don't attempt to clone when the connection is throttled.
            // Clone() internally calls RefreshInstanceDetails() which makes an API call.
            // If we're throttled (especially execution time limit), that call will fail,
            // causing the entire operation to fail instead of just waiting.
            if (_throttleTracker.IsThrottled(connectionName))
            {
                var expiry = _throttleTracker.GetThrottleExpiry(connectionName);
                throw new DataverseConnectionException(connectionName,
                    $"Cannot create new connection while throttled. Throttle expires at {expiry:HH:mm:ss}.",
                    new InvalidOperationException("Connection source is throttled"));
            }

            var seed = GetSeedClient(connectionName);

            ServiceClient serviceClient;
            try
            {
                serviceClient = seed.Clone();
            }
            catch (Exception ex)
            {
                throw DataverseConnectionException.CreateConnectionFailed(connectionName, ex);
            }

            if (!serviceClient.IsReady)
            {
                var error = serviceClient.LastError ?? "Unknown error";
                serviceClient.Dispose();
                throw new DataverseConnectionException(connectionName,
                    $"Cloned connection not ready: {error}", new InvalidOperationException(error));
            }

            // Disable affinity cookie for better load distribution
            if (_poolOptions.DisableAffinityCookie)
            {
                serviceClient.EnableAffinityCookie = false;
            }

            // Disable SDK internal retry - we handle throttling ourselves for visibility
            // Without this, ServiceClient silently waits on 429 and retries internally,
            // giving no visibility into throttle events
            serviceClient.MaxRetryCount = 0;

            var client = new DataverseClient(serviceClient);
            var pooledClient = new PooledClient(client, connectionName, ReturnConnection, OnThrottleDetected);

            _logger.LogDebug(
                "Created new connection. ConnectionId: {ConnectionId}, Name: {ConnectionName}, IsReady: {IsReady}",
                pooledClient.ConnectionId,
                pooledClient.DisplayName,
                pooledClient.IsReady);

            return pooledClient;
        }

        /// <summary>
        /// Called by PooledClient when a throttle is detected.
        /// </summary>
        private void OnThrottleDetected(string connectionName, TimeSpan retryAfter)
        {
            // Record throttle per-connection for routing decisions (avoid throttled connections)
            _throttleTracker.RecordThrottle(connectionName, retryAfter);
        }

        private PooledClient CreateDirectClient(DataverseClientOptions? options)
        {
            // When pooling is disabled, create a direct connection
            var source = _sources.FirstOrDefault()
                ?? throw new InvalidOperationException("No connections configured.");

            var client = CreateNewConnection(source.Name);

            if (options != null)
            {
                client.ApplyOptions(options);
            }

            return client;
        }

        private void ReturnConnection(PooledClient client)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                client.ForceDispose();
                return;
            }

            try
            {
                // Decrement active connections counter first
                _activeConnections.AddOrUpdate(client.ConnectionName, 0, (_, v) => Math.Max(0, v - 1));

                // Check if connection was marked as invalid - dispose instead of returning to pool
                if (client.IsInvalid)
                {
                    _logger.LogInformation(
                        "Connection marked invalid, disposing instead of returning. " +
                        "ConnectionId: {ConnectionId}, Name: {ConnectionName}, Reason: {Reason}",
                        client.ConnectionId,
                        client.DisplayName,
                        client.InvalidReason);

                    Interlocked.Increment(ref _invalidConnectionCount);
                    client.ForceDispose();
                    return;
                }

                var pool = _pools.GetValueOrDefault(client.ConnectionName);
                if (pool == null)
                {
                    client.ForceDispose();
                    return;
                }

                // Reset client to original state (this also resets the _returned flag for reuse)
                client.Reset();
                client.UpdateLastUsed();

                // Lock around pool enqueue to synchronize with GetConnectionFromPool
                lock (_poolLock)
                {
                    // Check if pool is full
                    var source = _sources.FirstOrDefault(s => s.Name == client.ConnectionName);
                    var maxPoolSize = source?.MaxPoolSize ?? 10;

                    if (pool.Count < maxPoolSize)
                    {
                        pool.Enqueue(client);
                        _logger.LogDebug(
                            "Returned connection to pool. ConnectionId: {ConnectionId}, Name: {ConnectionName}",
                            client.ConnectionId,
                            client.DisplayName);
                    }
                    else
                    {
                        client.ForceDispose();
                        _logger.LogDebug(
                            "Pool full, disposed connection. ConnectionId: {ConnectionId}, Name: {ConnectionName}",
                            client.ConnectionId,
                            client.DisplayName);
                    }
                }
            }
            finally
            {
                try
                {
                    _connectionSemaphore.Release();
                }
                catch (SemaphoreFullException)
                {
                    _logger.LogWarning("Semaphore full when releasing connection");
                }
            }
        }

        private bool IsValidConnection(PooledClient client)
        {
            try
            {
                // Check if marked as invalid
                if (client.IsInvalid)
                {
                    _logger.LogDebug("Connection marked invalid. ConnectionId: {ConnectionId}, Reason: {Reason}",
                        client.ConnectionId, client.InvalidReason);
                    return false;
                }

                // Check idle timeout
                if (DateTime.UtcNow - client.LastUsedAt > _poolOptions.MaxIdleTime)
                {
                    _logger.LogDebug("Connection idle too long. ConnectionId: {ConnectionId}", client.ConnectionId);
                    return false;
                }

                // Check max lifetime
                if (DateTime.UtcNow - client.CreatedAt > _poolOptions.MaxLifetime)
                {
                    _logger.LogDebug("Connection exceeded max lifetime. ConnectionId: {ConnectionId}", client.ConnectionId);
                    return false;
                }

                // Check if ready
                if (!client.IsReady)
                {
                    _logger.LogDebug("Connection not ready. ConnectionId: {ConnectionId}", client.ConnectionId);
                    return false;
                }

                return true;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
        }

        private IConnectionSelectionStrategy CreateSelectionStrategy()
        {
            return _poolOptions.SelectionStrategy switch
            {
                ConnectionSelectionStrategy.RoundRobin => new RoundRobinStrategy(),
                ConnectionSelectionStrategy.LeastConnections => new LeastConnectionsStrategy(),
                ConnectionSelectionStrategy.ThrottleAware => new ThrottleAwareStrategy(),
                _ => new ThrottleAwareStrategy()
            };
        }

        private void ApplyPerformanceSettings()
        {
            lock (_performanceSettingsLock)
            {
                if (_performanceSettingsApplied)
                {
                    return;
                }

                // Recommended settings for high-throughput Dataverse operations
                ThreadPool.SetMinThreads(100, 100);

                // These settings are still relevant for Dataverse SDK even though the APIs are deprecated
#pragma warning disable SYSLIB0014
                ServicePointManager.DefaultConnectionLimit = 65000;
                ServicePointManager.Expect100Continue = false;
                ServicePointManager.UseNagleAlgorithm = false;
#pragma warning restore SYSLIB0014

                _performanceSettingsApplied = true;
                _logger.LogDebug("Applied performance settings for high-throughput operations");
            }
        }

        /// <summary>
        /// Creates seed clients for all sources and discovers their DOP values.
        /// Must be called before CalculateTotalPoolCapacity() to enable DOP-based sizing.
        /// </summary>
        private void InitializeSeedsAndDiscoverDop()
        {
            if (!IsEnabled)
            {
                return;
            }

            foreach (var source in _sources)
            {
                try
                {
                    // GetSeedClient populates _sourceDop with the server's RecommendedDegreesOfParallelism
                    GetSeedClient(source.Name);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to initialize seed for {ConnectionName}, using default DOP=4", source.Name);
                    // Use conservative default if seed creation fails
                    _sourceDop[source.Name] = 4;
                }
            }
        }

        /// <summary>
        /// Warms up the pool by creating one connection per source.
        /// </summary>
        private void WarmUpConnections()
        {
            if (!IsEnabled)
            {
                return;
            }

            foreach (var source in _sources)
            {
                var pool = _pools[source.Name];

                // Only warm up if pool is empty
                if (pool.IsEmpty)
                {
                    try
                    {
                        var client = CreateNewConnection(source.Name);
                        pool.Enqueue(client);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to warm up connection for {ConnectionName}", source.Name);
                    }
                }
            }
        }

        private async Task StartValidationLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(_poolOptions.ValidationInterval, cancellationToken);
                    ValidateConnections();
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in validation loop");
                }
            }
        }

        private void ValidateConnections()
        {
            foreach (var (connectionName, pool) in _pools)
            {
                var count = pool.Count;
                var validated = new List<PooledClient>();

                for (int i = 0; i < count; i++)
                {
                    if (pool.TryDequeue(out var client))
                    {
                        if (IsValidConnection(client))
                        {
                            validated.Add(client);
                        }
                        else
                        {
                            client.ForceDispose();
                            _logger.LogDebug("Evicted invalid connection. ConnectionId: {ConnectionId}", client.ConnectionId);
                        }
                    }
                }

                foreach (var client in validated)
                {
                    pool.Enqueue(client);
                }
            }

            // Ensure at least 1 warm connection per source
            WarmUpConnections();
        }

        /// <summary>
        /// Calculates the total pool capacity based on discovered DOP values.
        /// </summary>
        /// <remarks>
        /// Pool capacity = sum of DOP for all sources. This is the server-recommended
        /// parallelism based on RecommendedDegreesOfParallelism from each connection.
        /// Seeds must be initialized before calling this method.
        /// </remarks>
        private int CalculateTotalPoolCapacity()
        {
            // Fixed pool size override
            if (_poolOptions.MaxPoolSize > 0)
            {
                return _poolOptions.MaxPoolSize;
            }

            // DOP-based sizing from discovered values
            if (!_sourceDop.IsEmpty)
            {
                return _sourceDop.Values.Sum();
            }

            // Fallback: conservative default if seeds not yet initialized (shouldn't happen)
            return _sources.Count * 4;
        }

        private static void ValidateConnection(DataverseConnection connection)
        {
            if (string.IsNullOrWhiteSpace(connection.Name))
            {
                throw ConfigurationException.MissingRequiredWithHints(
                    propertyName: "Name",
                    connectionName: $"[index {connection.SourceIndex}]",
                    connectionIndex: connection.SourceIndex,
                    environmentName: connection.SourceEnvironment);
            }

            if (string.IsNullOrWhiteSpace(connection.Url))
            {
                throw ConfigurationException.MissingRequiredWithHints(
                    propertyName: "Url",
                    connectionName: connection.Name,
                    connectionIndex: connection.SourceIndex,
                    environmentName: connection.SourceEnvironment);
            }

            if (string.IsNullOrWhiteSpace(connection.ClientId))
            {
                throw ConfigurationException.MissingRequiredWithHints(
                    propertyName: "ClientId",
                    connectionName: connection.Name,
                    connectionIndex: connection.SourceIndex,
                    environmentName: connection.SourceEnvironment);
            }
        }

        private PoolStatistics GetStatistics()
        {
            var connectionStats = new Dictionary<string, ConnectionStatistics>();

            foreach (var source in _sources)
            {
                var pool = _pools.GetValueOrDefault(source.Name);
                connectionStats[source.Name] = new ConnectionStatistics
                {
                    Name = source.Name,
                    ActiveConnections = _activeConnections.GetValueOrDefault(source.Name),
                    IdleConnections = pool?.Count ?? 0,
                    IsThrottled = _throttleTracker.IsThrottled(source.Name),
                    RequestsServed = _requestCounts.GetValueOrDefault(source.Name)
                };
            }

            return new PoolStatistics
            {
                TotalConnections = GetTotalConnections(),
                ActiveConnections = GetTotalActiveConnections(),
                IdleConnections = GetTotalIdleConnections(),
                ThrottledConnections = connectionStats.Values.Count(s => s.IsThrottled),
                RequestsServed = _totalRequestsServed,
                ThrottleEvents = _throttleTracker.TotalThrottleEvents,
                InvalidConnections = Interlocked.Read(ref _invalidConnectionCount),
                AuthFailures = Interlocked.Read(ref _authFailureCount),
                ConnectionFailures = Interlocked.Read(ref _connectionFailureCount),
                ConnectionStats = connectionStats
            };
        }

        private int GetTotalConnections() => GetTotalActiveConnections() + GetTotalIdleConnections();

        private int GetTotalActiveConnections() => _activeConnections.Values.Sum();

        private int GetTotalIdleConnections() => _pools.Values.Sum(p => p.Count);

        /// <inheritdoc />
        public void RecordAuthFailure()
        {
            Interlocked.Increment(ref _authFailureCount);
        }

        /// <inheritdoc />
        public void RecordConnectionFailure()
        {
            Interlocked.Increment(ref _connectionFailureCount);
        }

        /// <inheritdoc />
        public void InvalidateSeed(string connectionName)
        {
            if (string.IsNullOrEmpty(connectionName))
            {
                return;
            }

            // Remove from our seed cache
            if (_seedClients.TryRemove(connectionName, out _))
            {
                _logger.LogWarning(
                    "Invalidating seed client for connection {ConnectionName} due to token failure. " +
                    "Next connection request will create fresh authentication.",
                    connectionName);
            }

            // Invalidate the source's cached seed so GetSeedClient() creates a fresh one.
            // The source owns the seed client and is responsible for disposal:
            // - ConnectionStringSource: disposes and recreates on next GetSeedClient()
            // - ServiceClientSource: no-op (externally-managed client, caller owns lifecycle)
            var source = _sources.FirstOrDefault(s =>
                string.Equals(s.Name, connectionName, StringComparison.OrdinalIgnoreCase));

            if (source != null)
            {
                source.InvalidateSeed();
            }

            // Drain all pool members for this connection - they're clones of the broken seed
            if (_pools.TryGetValue(connectionName, out var pool))
            {
                var drained = 0;
                while (pool.TryDequeue(out var client))
                {
                    client.ForceDispose();
                    drained++;
                }

                if (drained > 0)
                {
                    _logger.LogInformation(
                        "Drained {Count} pooled connections for {ConnectionName} after seed invalidation",
                        drained, connectionName);
                }
            }
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                throw new ObjectDisposedException(nameof(DataverseConnectionPool));
            }
        }

        /// <inheritdoc />
        public async Task<OrganizationResponse> ExecuteAsync(OrganizationRequest request, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();

            // Retry forever on service protection errors - only CancellationToken stops us
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                await using var client = await GetClientAsync(cancellationToken: cancellationToken);

                try
                {
                    return await client.ExecuteAsync(request, cancellationToken);
                }
                catch (FaultException<OrganizationServiceFault> faultEx)
                    when (ServiceProtectionException.IsServiceProtectionError(faultEx.Detail.ErrorCode))
                {
                    // Throttle was already recorded by PooledClient via callback.
                    // Log and retry - GetClientAsync will wait for non-throttled connection.
                    _logger.LogDebug(
                        "Request throttled on connection {Connection}. Will retry with next available connection.",
                        client.ConnectionName);

                    // Loop continues - GetClientAsync will wait for a non-throttled connection
                }
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            // Use Interlocked.Exchange for atomic disposal check
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _validationCts.Cancel();

            foreach (var pool in _pools.Values)
            {
                while (pool.TryDequeue(out var client))
                {
                    client.ForceDispose();
                }
            }

            // Clear seed cache (sources will dispose the actual clients)
            _seedClients.Clear();

            // Dispose sources (which dispose their clients)
            foreach (var source in _sources)
            {
                source.Dispose();
            }

            _batchCoordinator?.Dispose();
            _connectionSemaphore.Dispose();
            _validationCts.Dispose();
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            // Use Interlocked.Exchange for atomic disposal check
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _validationCts.Cancel();

            try
            {
                await _validationTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected
            }

            foreach (var pool in _pools.Values)
            {
                while (pool.TryDequeue(out var client))
                {
                    client.ForceDispose();
                }
            }

            // Clear seed cache (sources will dispose the actual clients)
            _seedClients.Clear();

            // Dispose sources (which dispose their clients)
            foreach (var source in _sources)
            {
                source.Dispose();
            }

            _batchCoordinator?.Dispose();
            _connectionSemaphore.Dispose();
            _validationCts.Dispose();
        }
    }
}
