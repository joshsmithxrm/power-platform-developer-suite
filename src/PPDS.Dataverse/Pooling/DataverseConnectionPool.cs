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
using PPDS.Dataverse.DependencyInjection;
using PPDS.Dataverse.Pooling.Strategies;
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
        private readonly DataverseOptions _options;
        private readonly IThrottleTracker _throttleTracker;
        private readonly IConnectionSelectionStrategy _selectionStrategy;

        private readonly ConcurrentDictionary<string, ConcurrentQueue<PooledClient>> _pools;
        private readonly ConcurrentDictionary<string, int> _activeConnections;
        private readonly ConcurrentDictionary<string, long> _requestCounts;
        private readonly SemaphoreSlim _connectionSemaphore;
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

        /// <summary>
        /// Initializes a new instance of the <see cref="DataverseConnectionPool"/> class.
        /// </summary>
        /// <param name="options">Pool configuration options.</param>
        /// <param name="throttleTracker">Throttle tracking service.</param>
        /// <param name="logger">Logger instance.</param>
        public DataverseConnectionPool(
            IOptions<DataverseOptions> options,
            IThrottleTracker throttleTracker,
            ILogger<DataverseConnectionPool> logger)
        {
            _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
            _throttleTracker = throttleTracker ?? throw new ArgumentNullException(nameof(throttleTracker));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            ValidateOptions();

            _pools = new ConcurrentDictionary<string, ConcurrentQueue<PooledClient>>();
            _activeConnections = new ConcurrentDictionary<string, int>();
            _requestCounts = new ConcurrentDictionary<string, long>();
            _connectionSemaphore = new SemaphoreSlim(_options.Pool.MaxPoolSize, _options.Pool.MaxPoolSize);

            _selectionStrategy = CreateSelectionStrategy();

            // Initialize pools for each connection
            foreach (var connection in _options.Connections)
            {
                _pools[connection.Name] = new ConcurrentQueue<PooledClient>();
                _activeConnections[connection.Name] = 0;
                _requestCounts[connection.Name] = 0;
            }

            // Apply performance settings once
            ApplyPerformanceSettings();

            // Start background validation if enabled
            _validationCts = new CancellationTokenSource();
            if (_options.Pool.EnableValidation)
            {
                _validationTask = StartValidationLoopAsync(_validationCts.Token);
            }
            else
            {
                _validationTask = Task.CompletedTask;
            }

            // Initialize minimum connections
            InitializeMinimumConnections();

            _logger.LogInformation(
                "DataverseConnectionPool initialized. Connections: {ConnectionCount}, MaxPoolSize: {MaxPoolSize}, Strategy: {Strategy}",
                _options.Connections.Count,
                _options.Pool.MaxPoolSize,
                _options.Pool.SelectionStrategy);
        }

        /// <inheritdoc />
        public bool IsEnabled => _options.Pool.Enabled;

        /// <inheritdoc />
        public PoolStatistics Statistics => GetStatistics();

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
                var acquired = await _connectionSemaphore.WaitAsync(_options.Pool.AcquireTimeout, cancellationToken);
                if (!acquired)
                {
                    throw new PoolExhaustedException(
                        GetTotalActiveConnections(),
                        _options.Pool.MaxPoolSize,
                        _options.Pool.AcquireTimeout);
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

            var acquired = _connectionSemaphore.Wait(_options.Pool.AcquireTimeout);
            if (!acquired)
            {
                throw new PoolExhaustedException(
                    GetTotalActiveConnections(),
                    _options.Pool.MaxPoolSize,
                    _options.Pool.AcquireTimeout);
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
        private async Task WaitForNonThrottledConnectionAsync(
            string? excludeConnectionName,
            CancellationToken cancellationToken)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Check if any non-excluded connection is available (not throttled)
                var hasAvailable = _options.Connections
                    .Where(c => string.IsNullOrEmpty(excludeConnectionName) ||
                                !string.Equals(c.Name, excludeConnectionName, StringComparison.OrdinalIgnoreCase))
                    .Any(c => !_throttleTracker.IsThrottled(c.Name));

                if (hasAvailable)
                {
                    return; // At least one connection is available
                }

                // All connections are throttled - wait for shortest expiry
                var waitTime = _throttleTracker.GetShortestExpiry();
                if (waitTime <= TimeSpan.Zero)
                {
                    return; // Throttle already expired
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
            var connections = _options.Connections.AsReadOnly();

            // If an exclusion is requested and we have multiple connections, filter
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

        private PooledClient CreateNewConnection(string connectionName)
        {
            var connectionConfig = _options.Connections.First(c => c.Name == connectionName);

            _logger.LogDebug("Creating new connection for {ConnectionName}", connectionName);

            ServiceClient serviceClient;
            try
            {
                serviceClient = new ServiceClient(connectionConfig.ConnectionString);
            }
            catch (Exception ex)
            {
                // Wrap the exception to prevent connection string leakage in error messages
                throw DataverseConnectionException.CreateConnectionFailed(connectionName, ex);
            }

            if (!serviceClient.IsReady)
            {
                var error = serviceClient.LastError ?? "Unknown error";
                var exception = serviceClient.LastException;

                serviceClient.Dispose();

                if (exception != null)
                {
                    throw DataverseConnectionException.CreateConnectionFailed(connectionName, exception);
                }

                throw new DataverseConnectionException(
                    connectionName,
                    $"Connection '{connectionName}' failed to initialize: {ConnectionStringRedactor.RedactExceptionMessage(error)}",
                    new InvalidOperationException(error));
            }

            // Disable affinity cookie for better load distribution
            if (_options.Pool.DisableAffinityCookie)
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
                connectionName,
                pooledClient.IsReady);

            return pooledClient;
        }

        /// <summary>
        /// Called by PooledClient when a throttle is detected.
        /// </summary>
        private void OnThrottleDetected(string connectionName, TimeSpan retryAfter)
        {
            _throttleTracker.RecordThrottle(connectionName, retryAfter);
        }

        private PooledClient CreateDirectClient(DataverseClientOptions? options)
        {
            // When pooling is disabled, create a direct connection
            var connectionConfig = _options.Connections.FirstOrDefault()
                ?? throw new InvalidOperationException("No connections configured.");

            var client = CreateNewConnection(connectionConfig.Name);

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
                        client.ConnectionName,
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
                    if (pool.Count < _options.Connections.First(c => c.Name == client.ConnectionName).MaxPoolSize)
                    {
                        pool.Enqueue(client);
                        _logger.LogDebug(
                            "Returned connection to pool. ConnectionId: {ConnectionId}, Name: {ConnectionName}",
                            client.ConnectionId,
                            client.ConnectionName);
                    }
                    else
                    {
                        client.ForceDispose();
                        _logger.LogDebug(
                            "Pool full, disposed connection. ConnectionId: {ConnectionId}, Name: {ConnectionName}",
                            client.ConnectionId,
                            client.ConnectionName);
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
                if (DateTime.UtcNow - client.LastUsedAt > _options.Pool.MaxIdleTime)
                {
                    _logger.LogDebug("Connection idle too long. ConnectionId: {ConnectionId}", client.ConnectionId);
                    return false;
                }

                // Check max lifetime
                if (DateTime.UtcNow - client.CreatedAt > _options.Pool.MaxLifetime)
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
            return _options.Pool.SelectionStrategy switch
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

        private void InitializeMinimumConnections()
        {
            if (!IsEnabled || _options.Pool.MinPoolSize <= 0)
            {
                return;
            }

            _logger.LogDebug("Initializing minimum pool connections");

            foreach (var connection in _options.Connections)
            {
                var pool = _pools[connection.Name];
                var activeCount = _activeConnections.GetValueOrDefault(connection.Name, 0);
                var currentTotal = pool.Count + activeCount;
                var targetMin = Math.Min(_options.Pool.MinPoolSize, connection.MaxPoolSize);
                var toCreate = Math.Max(0, targetMin - currentTotal);

                if (toCreate > 0)
                {
                    _logger.LogDebug(
                        "Pool {ConnectionName}: Active={Active}, Idle={Idle}, Target={Target}, Creating={ToCreate}",
                        connection.Name, activeCount, pool.Count, targetMin, toCreate);
                }

                for (int i = 0; i < toCreate; i++)
                {
                    try
                    {
                        var client = CreateNewConnection(connection.Name);
                        pool.Enqueue(client);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to initialize connection for {ConnectionName}", connection.Name);
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
                    await Task.Delay(_options.Pool.ValidationInterval, cancellationToken);
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

            // Ensure minimum pool size
            InitializeMinimumConnections();
        }

        private void ValidateOptions()
        {
            if (_options.Connections == null || _options.Connections.Count == 0)
            {
                throw new InvalidOperationException("At least one connection must be configured.");
            }

            if (_options.Pool.MaxPoolSize < _options.Pool.MinPoolSize)
            {
                throw new InvalidOperationException("MaxPoolSize must be >= MinPoolSize.");
            }

            foreach (var connection in _options.Connections)
            {
                if (string.IsNullOrWhiteSpace(connection.Name))
                {
                    throw new InvalidOperationException("Connection name cannot be empty.");
                }

                if (string.IsNullOrWhiteSpace(connection.ConnectionString))
                {
                    throw new InvalidOperationException($"Connection string for '{connection.Name}' cannot be empty.");
                }
            }

            // Warn if multiple connections target different organizations
            WarnIfMultipleOrganizations();
        }

        private void WarnIfMultipleOrganizations()
        {
            if (_options.Connections.Count < 2)
            {
                return;
            }

            var orgUrls = new Dictionary<string, string>(); // connectionName -> orgUrl

            foreach (var connection in _options.Connections)
            {
                var orgUrl = ExtractOrgUrl(connection.ConnectionString);
                if (!string.IsNullOrEmpty(orgUrl))
                {
                    orgUrls[connection.Name] = orgUrl;
                }
            }

            var distinctOrgs = orgUrls.Values.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            if (distinctOrgs.Count > 1)
            {
                _logger.LogWarning(
                    "Connection pool contains connections to {OrgCount} different organizations: {Orgs}. " +
                    "Requests will be load-balanced across these organizations, which is likely unintended. " +
                    "For multi-environment scenarios (Dev/QA/Prod), create separate service providers per environment. " +
                    "See documentation for the recommended pattern.",
                    distinctOrgs.Count,
                    string.Join(", ", distinctOrgs));
            }
        }

        private static string? ExtractOrgUrl(string connectionString)
        {
            // Parse connection string to extract Url parameter
            // Format: "AuthType=...;Url=https://org.crm.dynamics.com;..."
            if (string.IsNullOrEmpty(connectionString))
            {
                return null;
            }

            var url = connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Split('=', 2))
                .Where(kv => kv.Length == 2 && kv[0].Trim().Equals("Url", StringComparison.OrdinalIgnoreCase))
                .Select(kv => kv[1].Trim())
                .FirstOrDefault();

            if (url == null)
            {
                return null;
            }

            // Extract just the host for comparison
            return Uri.TryCreate(url, UriKind.Absolute, out var uri)
                ? uri.Host.ToLowerInvariant()
                : url.ToLowerInvariant();
        }

        private PoolStatistics GetStatistics()
        {
            var connectionStats = new Dictionary<string, ConnectionStatistics>();

            foreach (var connection in _options.Connections)
            {
                var pool = _pools.GetValueOrDefault(connection.Name);
                connectionStats[connection.Name] = new ConnectionStatistics
                {
                    Name = connection.Name,
                    ActiveConnections = _activeConnections.GetValueOrDefault(connection.Name),
                    IdleConnections = pool?.Count ?? 0,
                    IsThrottled = _throttleTracker.IsThrottled(connection.Name),
                    RequestsServed = _requestCounts.GetValueOrDefault(connection.Name)
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

            _connectionSemaphore.Dispose();
            _validationCts.Dispose();
        }
    }
}
