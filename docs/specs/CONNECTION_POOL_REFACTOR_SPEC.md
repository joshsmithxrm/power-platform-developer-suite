# Connection Pool Refactor Specification

**Status:** Ready for Implementation
**Author:** Josh Smith
**Date:** 2025-12-27
**Branch:** `feature/connection-source-abstraction`

---

## Problem Statement

The current `DataverseConnectionPool` is tightly coupled to connection string-based authentication. This forces the CLI to use a separate `DeviceCodeConnectionPool` implementation that:

1. **Doesn't actually pool** - clones on every request instead of reusing connections
2. **Misses all pool features** - no throttle tracking, no adaptive rate control, no connection validation
3. **Causes failures under load** - Clone() during throttle periods fails and isn't retried properly

The root cause is that the pool conflates two concerns:
- **Authentication** - how to get an initial ServiceClient
- **Pooling** - how to manage clones of that client

These concerns must be separated so any authentication method can use the same pool.

---

## Current State

### DataverseConnectionPool (PPDS.Dataverse)

Location: `src/PPDS.Dataverse/Pooling/DataverseConnectionPool.cs`

```csharp
public DataverseConnectionPool(
    IOptions<DataverseOptions> options,      // Requires full config
    IThrottleTracker throttleTracker,
    IAdaptiveRateController adaptiveRateController,
    ILogger<DataverseConnectionPool> logger)
```

- Creates connections from `DataverseOptions.Connections` (connection string config)
- `CreateNewConnection()` builds connection string, creates ServiceClient
- Has all pool features: throttle tracking, adaptive rate, validation, lifecycle

### DeviceCodeConnectionPool (PPDS.Migration.Cli)

Location: `src/PPDS.Migration.Cli/Infrastructure/DeviceCodeConnectionPool.cs`

```csharp
public DeviceCodeConnectionPool(Uri url, Func<string, Task<string>> tokenProvider)
```

- Takes pre-authenticated ServiceClient
- **Clones on every GetClientAsync() call** - no actual pooling
- Missing: throttle tracking, adaptive rate control, connection validation
- Clone() failures during throttle are not handled

---

## Target State

### Architecture

```
┌─────────────────────────────────────────────────────────────┐
│                    Authentication Layer                      │
│         (External - produces ServiceClient however)          │
├─────────────────────────────────────────────────────────────┤
│  DeviceCode    ConnectionString    ManagedIdentity   Cert   │
│      │              │                    │            │     │
│      ▼              ▼                    ▼            ▼     │
│  ServiceClient  ServiceClient      ServiceClient  ServiceClient
└────────┬────────────┬──────────────────┬────────────┬───────┘
         │            │                  │            │
         ▼            ▼                  ▼            ▼
┌─────────────────────────────────────────────────────────────┐
│                    IConnectionSource                         │
│  ┌──────────────────┐           ┌──────────────────────┐    │
│  │ServiceClientSource│           │ConnectionStringSource│    │
│  │(pre-authenticated)│           │(lazy from config)    │    │
│  └──────────────────┘           └──────────────────────┘    │
└────────────────────────────┬────────────────────────────────┘
                             │
                             ▼
┌─────────────────────────────────────────────────────────────┐
│                  DataverseConnectionPool                     │
│  • Gets seed client from IConnectionSource (once, cached)    │
│  • Clones seed to fill pool                                  │
│  • Manages lifecycle, throttling, adaptive rate              │
│  • Auth-agnostic - works with any ServiceClient source       │
└─────────────────────────────────────────────────────────────┘
```

### Key Principle

**The pool never creates ServiceClients directly.** It receives `IConnectionSource` implementations that provide seed clients. The pool's only job is to clone and manage those seeds.

---

## Interface Design

### IConnectionSource

Location: `src/PPDS.Dataverse/Pooling/IConnectionSource.cs` (new file)

```csharp
using Microsoft.PowerPlatform.Dataverse.Client;

namespace PPDS.Dataverse.Pooling;

/// <summary>
/// Provides a seed ServiceClient for the connection pool to clone.
/// Implementations handle specific authentication methods.
/// </summary>
/// <remarks>
/// <para>
/// The pool calls <see cref="GetSeedClient"/> once per source and caches the result.
/// All pool members for this source are clones of that seed client.
/// </para>
/// <para>
/// Implementations should be thread-safe. <see cref="GetSeedClient"/> may be called
/// from multiple threads during pool initialization or expansion.
/// </para>
/// </remarks>
public interface IConnectionSource : IDisposable
{
    /// <summary>
    /// Gets the unique name for this connection source.
    /// Used for logging, throttle tracking, and connection selection.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Gets the maximum number of pooled connections for this source.
    /// </summary>
    int MaxPoolSize { get; }

    /// <summary>
    /// Gets the seed ServiceClient for cloning.
    /// </summary>
    /// <returns>
    /// An authenticated, ready-to-use ServiceClient.
    /// The pool will clone this client to create pool members.
    /// </returns>
    /// <exception cref="DataverseConnectionException">
    /// Thrown if the client cannot be created or is not ready.
    /// </exception>
    /// <remarks>
    /// This method is called once per source. The result is cached by the pool.
    /// Implementations may create the client lazily on first call.
    /// </remarks>
    ServiceClient GetSeedClient();
}
```

### ServiceClientSource

Location: `src/PPDS.Dataverse/Pooling/ServiceClientSource.cs` (new file)

```csharp
using Microsoft.PowerPlatform.Dataverse.Client;

namespace PPDS.Dataverse.Pooling;

/// <summary>
/// Connection source for pre-authenticated ServiceClients.
/// Use this when you have an already-authenticated client (device code, managed identity, etc.)
/// </summary>
/// <remarks>
/// <para>
/// This source wraps an existing ServiceClient. The pool will clone this client
/// to create pool members. The original client is used as the seed and must remain
/// valid for the lifetime of the pool.
/// </para>
/// <para>
/// The caller is responsible for authenticating the ServiceClient before passing it here.
/// This class does not perform any authentication.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// // Device code authentication
/// var client = await DeviceCodeAuth.AuthenticateAsync(url);
/// var source = new ServiceClientSource(client, "Interactive", maxPoolSize: 10);
/// var pool = new DataverseConnectionPool(new[] { source }, ...);
///
/// // Managed identity
/// var client = new ServiceClient(url, tokenProviderFunc);
/// var source = new ServiceClientSource(client, "ManagedIdentity");
/// </code>
/// </example>
public sealed class ServiceClientSource : IConnectionSource
{
    private readonly ServiceClient _client;
    private bool _disposed;

    /// <summary>
    /// Creates a new connection source wrapping an existing ServiceClient.
    /// </summary>
    /// <param name="client">
    /// The authenticated ServiceClient to use as the seed for cloning.
    /// Must be ready (<see cref="ServiceClient.IsReady"/> == true).
    /// </param>
    /// <param name="name">
    /// Unique name for this connection source. Used for logging and tracking.
    /// </param>
    /// <param name="maxPoolSize">
    /// Maximum number of pooled connections from this source. Default is 10.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown if <paramref name="client"/> or <paramref name="name"/> is null.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown if <paramref name="client"/> is not ready or <paramref name="name"/> is empty.
    /// </exception>
    public ServiceClientSource(ServiceClient client, string name, int maxPoolSize = 10)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(name);

        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Name cannot be empty.", nameof(name));

        if (!client.IsReady)
            throw new ArgumentException("ServiceClient must be ready.", nameof(client));

        if (maxPoolSize < 1)
            throw new ArgumentOutOfRangeException(nameof(maxPoolSize), "MaxPoolSize must be at least 1.");

        _client = client;
        Name = name;
        MaxPoolSize = maxPoolSize;
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public int MaxPoolSize { get; }

    /// <inheritdoc />
    public ServiceClient GetSeedClient()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _client;
    }

    /// <summary>
    /// Disposes the underlying ServiceClient.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _client.Dispose();
    }
}
```

### ConnectionStringSource

Location: `src/PPDS.Dataverse/Pooling/ConnectionStringSource.cs` (new file)

```csharp
using Microsoft.PowerPlatform.Dataverse.Client;
using PPDS.Dataverse.Configuration;
using PPDS.Dataverse.Security;

namespace PPDS.Dataverse.Pooling;

/// <summary>
/// Connection source that creates a ServiceClient from connection string configuration.
/// Used for backward compatibility with existing DataverseOptions configuration.
/// </summary>
/// <remarks>
/// The ServiceClient is created lazily on the first call to <see cref="GetSeedClient"/>.
/// This allows the pool to be constructed without immediately authenticating.
/// </remarks>
public sealed class ConnectionStringSource : IConnectionSource
{
    private readonly DataverseConnection _config;
    private readonly object _lock = new();
    private ServiceClient? _client;
    private bool _disposed;

    /// <summary>
    /// Creates a new connection source from connection configuration.
    /// </summary>
    /// <param name="config">The connection configuration.</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown if <paramref name="config"/> is null.
    /// </exception>
    public ConnectionStringSource(DataverseConnection config)
    {
        ArgumentNullException.ThrowIfNull(config);
        _config = config;
    }

    /// <inheritdoc />
    public string Name => _config.Name;

    /// <inheritdoc />
    public int MaxPoolSize => _config.MaxPoolSize;

    /// <inheritdoc />
    public ServiceClient GetSeedClient()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_client != null)
            return _client;

        lock (_lock)
        {
            if (_client != null)
                return _client;

            ServiceClient client;
            try
            {
                var secret = SecretResolver.ResolveSync(
                    _config.ClientSecretKeyVaultUri,
                    _config.ClientSecret);

                var connectionString = ConnectionStringBuilder.Build(_config, secret);
                client = new ServiceClient(connectionString);
            }
            catch (Exception ex)
            {
                throw DataverseConnectionException.CreateConnectionFailed(Name, ex);
            }

            if (!client.IsReady)
            {
                var error = client.LastError ?? "Unknown error";
                var exception = client.LastException;
                client.Dispose();

                if (exception != null)
                    throw DataverseConnectionException.CreateConnectionFailed(Name, exception);

                throw new DataverseConnectionException(
                    Name,
                    $"Connection '{Name}' failed to initialize: {error}",
                    new InvalidOperationException(error));
            }

            _client = client;
            return _client;
        }
    }

    /// <summary>
    /// Disposes the underlying ServiceClient if it was created.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _client?.Dispose();
    }
}
```

---

## DataverseConnectionPool Changes

### New Constructor (Primary)

```csharp
/// <summary>
/// Initializes a new connection pool from connection sources.
/// </summary>
/// <param name="sources">
/// One or more connection sources providing seed clients.
/// Each source's seed will be cloned to create pool members.
/// </param>
/// <param name="throttleTracker">Throttle tracking service.</param>
/// <param name="adaptiveRateController">Adaptive rate control service.</param>
/// <param name="poolOptions">Pool configuration options.</param>
/// <param name="logger">Logger instance.</param>
public DataverseConnectionPool(
    IEnumerable<IConnectionSource> sources,
    IThrottleTracker throttleTracker,
    IAdaptiveRateController adaptiveRateController,
    DataversePoolOptions poolOptions,
    ILogger<DataverseConnectionPool> logger)
```

### Legacy Constructor (Backward Compatible)

```csharp
/// <summary>
/// Initializes a new connection pool from DataverseOptions configuration.
/// This constructor maintains backward compatibility with existing DI registration.
/// </summary>
[Obsolete("Use the IConnectionSource-based constructor for new code.")]
public DataverseConnectionPool(
    IOptions<DataverseOptions> options,
    IThrottleTracker throttleTracker,
    IAdaptiveRateController adaptiveRateController,
    ILogger<DataverseConnectionPool> logger)
    : this(
        options.Value.Connections.Select(c => new ConnectionStringSource(c)),
        throttleTracker,
        adaptiveRateController,
        options.Value.Pool,
        logger)
{ }
```

### Internal Changes

1. **Add `_sources` field:**
   ```csharp
   private readonly IReadOnlyList<IConnectionSource> _sources;
   ```

2. **Add `_seedClients` cache:**
   ```csharp
   private readonly ConcurrentDictionary<string, ServiceClient> _seedClients = new();
   ```

3. **Add `GetSeedClient` method:**
   ```csharp
   private ServiceClient GetSeedClient(string connectionName)
   {
       return _seedClients.GetOrAdd(connectionName, name =>
       {
           var source = _sources.First(s => s.Name == name);
           return source.GetSeedClient();
       });
   }
   ```

4. **Modify `CreateNewConnection`:**
   ```csharp
   private PooledClient CreateNewConnection(string connectionName)
   {
       _logger.LogDebug("Creating new connection for {ConnectionName}", connectionName);

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
               $"Cloned connection not ready: {error}", null);
       }

       // Disable affinity cookie for better load distribution
       if (_poolOptions.DisableAffinityCookie)
       {
           serviceClient.EnableAffinityCookie = false;
       }

       // Disable SDK internal retry - we handle throttling ourselves
       serviceClient.MaxRetryCount = 0;

       var client = new DataverseClient(serviceClient);
       var pooledClient = new PooledClient(client, connectionName, ReturnConnection, OnThrottleDetected);

       _logger.LogDebug(
           "Created new connection. ConnectionId: {ConnectionId}, Name: {ConnectionName}",
           pooledClient.ConnectionId, connectionName);

       return pooledClient;
   }
   ```

5. **Remove connection string building logic** - this moves to `ConnectionStringSource`

6. **Update validation** - validate sources instead of `DataverseOptions.Connections`

7. **Update disposal** - dispose sources when pool is disposed

---

## Files to Modify

### New Files

| File | Description |
|------|-------------|
| `src/PPDS.Dataverse/Pooling/IConnectionSource.cs` | Interface definition |
| `src/PPDS.Dataverse/Pooling/ServiceClientSource.cs` | Pre-authenticated client wrapper |
| `src/PPDS.Dataverse/Pooling/ConnectionStringSource.cs` | Config-based client factory |

### Modified Files

| File | Changes |
|------|---------|
| `src/PPDS.Dataverse/Pooling/DataverseConnectionPool.cs` | Add new constructor, refactor CreateNewConnection, add seed caching |
| `src/PPDS.Migration.Cli/Infrastructure/DeviceCodeConnectionPool.cs` | **DELETE** - no longer needed |
| `src/PPDS.Migration.Cli/Commands/ImportCommand.cs` | Use DataverseConnectionPool with ServiceClientSource |
| `src/PPDS.Migration.Cli/Commands/ExportCommand.cs` | Use DataverseConnectionPool with ServiceClientSource |
| `tests/PPDS.Dataverse.Tests/Pooling/DataverseConnectionPoolTests.cs` | Add tests for new constructor |
| `tests/PPDS.Dataverse.Tests/Pooling/ServiceClientSourceTests.cs` | New test file |
| `tests/PPDS.Dataverse.Tests/Pooling/ConnectionStringSourceTests.cs` | New test file |

---

## Implementation Steps

### Phase 1: Add Abstraction (No Breaking Changes)

1. Create `IConnectionSource.cs` with interface definition
2. Create `ServiceClientSource.cs` implementation
3. Create `ConnectionStringSource.cs` implementation
4. Add unit tests for both implementations

### Phase 2: Refactor Pool

1. Add `_sources` and `_seedClients` fields to `DataverseConnectionPool`
2. Add new constructor that takes `IEnumerable<IConnectionSource>`
3. Add `GetSeedClient()` method
4. Refactor `CreateNewConnection()` to use `GetSeedClient()` and clone
5. Move connection string building logic to `ConnectionStringSource`
6. Update legacy constructor to create `ConnectionStringSource` instances
7. Mark legacy constructor with `[Obsolete]` attribute
8. Update `Dispose`/`DisposeAsync` to dispose sources
9. Update validation logic for sources
10. Add/update unit tests

### Phase 3: Update CLI

1. Modify `ImportCommand` to use `DataverseConnectionPool` with `ServiceClientSource`
2. Modify `ExportCommand` similarly
3. Delete `DeviceCodeConnectionPool.cs`
4. Delete `DeviceCodePooledClient` (internal class in same file)
5. Run CLI integration tests

### Phase 4: Cleanup

1. Remove any dead code
2. Update XML documentation
3. Verify all tests pass
4. Build in Release mode

---

## Testing Requirements

### Unit Tests

**ServiceClientSourceTests.cs:**
- `Constructor_WithValidClient_SetsProperties`
- `Constructor_WithNullClient_ThrowsArgumentNullException`
- `Constructor_WithNullName_ThrowsArgumentNullException`
- `Constructor_WithEmptyName_ThrowsArgumentException`
- `Constructor_WithNotReadyClient_ThrowsArgumentException`
- `Constructor_WithInvalidMaxPoolSize_ThrowsArgumentOutOfRangeException`
- `GetSeedClient_ReturnsProvidedClient`
- `GetSeedClient_AfterDispose_ThrowsObjectDisposedException`
- `Dispose_DisposesUnderlyingClient`

**ConnectionStringSourceTests.cs:**
- `Constructor_WithValidConfig_SetsProperties`
- `Constructor_WithNullConfig_ThrowsArgumentNullException`
- `GetSeedClient_CreatesClientFromConfig`
- `GetSeedClient_CalledMultipleTimes_ReturnsSameInstance`
- `GetSeedClient_WithInvalidConfig_ThrowsDataverseConnectionException`
- `GetSeedClient_AfterDispose_ThrowsObjectDisposedException`
- `Dispose_DisposesCreatedClient`
- `Dispose_WhenClientNotCreated_DoesNotThrow`

**DataverseConnectionPoolTests.cs (additions):**
- `Constructor_WithSources_InitializesPool`
- `Constructor_WithEmptySources_ThrowsArgumentException`
- `Constructor_WithNullSources_ThrowsArgumentNullException`
- `GetClientAsync_WithServiceClientSource_ReturnsPooledClient`
- `GetClientAsync_ClonesFromSeedNotCreatesNew`
- `CreateNewConnection_UsesCachedSeed`
- `Dispose_DisposesSources`

### Integration Tests

- Run existing CLI import/export tests with the new pool
- Verify throttle handling works correctly
- Verify adaptive rate control works correctly

---

## Backward Compatibility

### DI Registration

Existing DI registration using `IOptions<DataverseOptions>` continues to work:

```csharp
services.AddDataverseClient(configuration);  // Still works
```

The legacy constructor creates `ConnectionStringSource` instances internally.

### Breaking Changes

**None for library consumers.** The new constructor is additive. The legacy constructor is deprecated but not removed.

### Migration Path

For consumers who want to use the new pattern:

```csharp
// Before (still works, but deprecated)
services.AddSingleton<IDataverseConnectionPool>(sp =>
    new DataverseConnectionPool(
        sp.GetRequiredService<IOptions<DataverseOptions>>(),
        sp.GetRequiredService<IThrottleTracker>(),
        sp.GetRequiredService<IAdaptiveRateController>(),
        sp.GetRequiredService<ILogger<DataverseConnectionPool>>()));

// After (recommended)
var sources = GetConnectionSources();  // However you want to create them
services.AddSingleton<IDataverseConnectionPool>(sp =>
    new DataverseConnectionPool(
        sources,
        sp.GetRequiredService<IThrottleTracker>(),
        sp.GetRequiredService<IAdaptiveRateController>(),
        poolOptions,
        sp.GetRequiredService<ILogger<DataverseConnectionPool>>()));
```

---

## Acceptance Criteria

1. **IConnectionSource interface exists** with Name, MaxPoolSize, GetSeedClient()
2. **ServiceClientSource works** with pre-authenticated clients
3. **ConnectionStringSource works** with existing config pattern
4. **DataverseConnectionPool accepts IConnectionSource[]** via new constructor
5. **Legacy constructor still works** (deprecated but functional)
6. **DeviceCodeConnectionPool is deleted**
7. **CLI uses DataverseConnectionPool** with ServiceClientSource
8. **All existing tests pass**
9. **New unit tests pass** for sources and pool
10. **CLI import/export works** with device code auth
11. **Throttle handling works** in CLI (previously broken)
12. **Adaptive rate control works** in CLI (previously missing)

---

## Usage Examples

### Device Code (CLI)

```csharp
// Authentication (external to pool)
var serviceClient = new ServiceClient(url, tokenProvider, useUniqueInstance: true);

// Create pool
var source = new ServiceClientSource(serviceClient, "Interactive", maxPoolSize: 10);
var pool = new DataverseConnectionPool(
    new[] { source },
    throttleTracker,
    adaptiveRateController,
    new DataversePoolOptions { MinPoolSize = 2 },
    logger);

// Use pool - all features work
await using var client = await pool.GetClientAsync();
await client.ExecuteAsync(request);
```

### Managed Identity (Future)

```csharp
var serviceClient = new ServiceClient(
    new Uri("https://org.crm.dynamics.com"),
    async uri => await credential.GetTokenAsync(new TokenRequestContext(new[] { $"{uri}/.default" })));

var source = new ServiceClientSource(serviceClient, "ManagedIdentity");
var pool = new DataverseConnectionPool(new[] { source }, ...);
```

### Multi-User Load Balancing (Library)

```csharp
var sources = config.Connections
    .Select(c => new ConnectionStringSource(c))
    .ToList();

var pool = new DataverseConnectionPool(sources, ...);
```

### Mixed Sources (Future)

```csharp
var sources = new List<IConnectionSource>
{
    new ServiceClientSource(managedIdentityClient, "Primary"),
    new ConnectionStringSource(fallbackConfig),
};

var pool = new DataverseConnectionPool(sources, ...);
```

---

## Notes

- The `ServiceClient.Clone()` method may fail during throttle. The pool's existing retry logic in `GetClientAsync` handles this by waiting for throttle to clear before acquiring connections.
- Pre-warming (`MinPoolSize > 0`) is recommended for CLI usage to perform clones at startup rather than under load.
- The pool disposes sources when it is disposed. Callers should not dispose sources separately.
