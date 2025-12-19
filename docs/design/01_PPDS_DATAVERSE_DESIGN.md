# PPDS.Dataverse - Detailed Design

**Status:** Design
**Created:** December 19, 2025
**Purpose:** High-performance Dataverse connectivity with connection pooling, bulk operations, and resilience

---

## Overview

`PPDS.Dataverse` is a foundational library providing optimized Dataverse connectivity for .NET applications. It addresses common pain points when building integrations:

- **Connection management** - Pool and reuse connections efficiently
- **Throttling** - Handle service protection limits gracefully
- **Bulk operations** - Leverage modern APIs for 5x throughput
- **Multi-tenant** - Support multiple Application Users for load distribution

---

## Key Design Decisions

### 1. Multi-Connection Architecture

**Problem:** Single connection string = single Application User = all requests share same quota. Under load, you hit 6,000 requests/5min limit quickly.

**Solution:** Support multiple connection configurations with intelligent selection.

```csharp
options.Connections = new[]
{
    new DataverseConnection("AppUser1", connectionString1),
    new DataverseConnection("AppUser2", connectionString2),
    new DataverseConnection("AppUser3", connectionString3),
};
```

Each connection can be a different Application User, distributing load across multiple quotas.

### 2. Disable Affinity Cookie by Default

**Problem:** With `EnableAffinityCookie = true` (SDK default), all requests route to a single backend node, creating a bottleneck.

**Solution:** Default to `EnableAffinityCookie = false` for high-throughput scenarios.

> "Removing the affinity cookie could increase performance by at least one order of magnitude."
> — [Microsoft DataverseServiceClient Discussion #312](https://github.com/microsoft/PowerPlatform-DataverseServiceClient/discussions/312)

### 3. Throttle-Aware Connection Selection

**Problem:** When one connection hits throttling limits, continuing to use it wastes time on retries.

**Solution:** Track throttle state per-connection, route requests away from throttled connections.

### 4. Bulk API Wrappers

**Problem:** `ExecuteMultiple` provides ~2M records/hour. Modern bulk APIs provide ~10M records/hour.

**Solution:** Provide easy-to-use wrappers for `CreateMultiple`, `UpdateMultiple`, `UpsertMultiple`.

---

## Project Structure

```
PPDS.Dataverse/
├── PPDS.Dataverse.csproj
├── PPDS.Dataverse.snk
│
├── Client/                              # ServiceClient abstraction
│   ├── IDataverseClient.cs              # Main interface
│   ├── DataverseClient.cs               # Implementation wrapping ServiceClient
│   └── DataverseClientOptions.cs        # Per-request options (CallerId, etc.)
│
├── Pooling/                             # Connection pool
│   ├── IDataverseConnectionPool.cs      # Pool interface
│   ├── DataverseConnectionPool.cs       # Pool implementation
│   ├── DataverseConnection.cs           # Connection configuration
│   ├── ConnectionPoolOptions.cs         # Pool settings
│   ├── PooledClient.cs                  # Wrapper for pooled connections
│   │
│   └── Strategies/                      # Connection selection
│       ├── IConnectionSelectionStrategy.cs
│       ├── RoundRobinStrategy.cs        # Simple rotation
│       ├── LeastConnectionsStrategy.cs  # Least active connections
│       └── ThrottleAwareStrategy.cs     # Avoid throttled connections
│
├── BulkOperations/                      # Modern bulk API wrappers
│   ├── IBulkOperationExecutor.cs        # Executor interface
│   ├── BulkOperationExecutor.cs         # Implementation
│   ├── BulkOperationOptions.cs          # Batch size, parallelism
│   └── BulkOperationResult.cs           # Results with error details
│
├── Resilience/                          # Throttling and retry
│   ├── IThrottleTracker.cs              # Track throttle state
│   ├── ThrottleTracker.cs               # Implementation
│   ├── ThrottleState.cs                 # Per-connection throttle info
│   ├── RetryOptions.cs                  # Retry configuration
│   └── ServiceProtectionException.cs    # Typed exception for 429s
│
├── Diagnostics/                         # Observability
│   ├── IPoolMetrics.cs                  # Metrics interface
│   ├── PoolMetrics.cs                   # Implementation
│   └── DataverseActivitySource.cs       # OpenTelemetry support
│
└── DependencyInjection/                 # DI extensions
    ├── ServiceCollectionExtensions.cs   # AddDataverseConnectionPool()
    └── DataverseOptions.cs              # Root options object
```

---

## Core Interfaces

### IDataverseConnectionPool

```csharp
namespace PPDS.Dataverse.Pooling;

/// <summary>
/// Manages a pool of Dataverse connections with intelligent selection and lifecycle management.
/// </summary>
public interface IDataverseConnectionPool : IAsyncDisposable, IDisposable
{
    /// <summary>
    /// Gets a client from the pool asynchronously.
    /// </summary>
    /// <param name="options">Optional per-request options (CallerId, etc.)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>A pooled client that returns to pool on dispose</returns>
    Task<IPooledClient> GetClientAsync(
        DataverseClientOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a client from the pool synchronously.
    /// </summary>
    IPooledClient GetClient(DataverseClientOptions? options = null);

    /// <summary>
    /// Gets pool statistics and health information.
    /// </summary>
    PoolStatistics Statistics { get; }

    /// <summary>
    /// Gets whether the pool is enabled.
    /// </summary>
    bool IsEnabled { get; }
}
```

### IPooledClient

```csharp
namespace PPDS.Dataverse.Pooling;

/// <summary>
/// A client obtained from the connection pool. Dispose to return to pool.
/// Implements IAsyncDisposable for async-friendly patterns.
/// </summary>
public interface IPooledClient : IDataverseClient, IAsyncDisposable, IDisposable
{
    /// <summary>
    /// Unique identifier for this connection instance.
    /// </summary>
    Guid ConnectionId { get; }

    /// <summary>
    /// Name of the connection configuration this client came from.
    /// </summary>
    string ConnectionName { get; }

    /// <summary>
    /// When this connection was created.
    /// </summary>
    DateTime CreatedAt { get; }

    /// <summary>
    /// When this connection was last used.
    /// </summary>
    DateTime LastUsedAt { get; }
}
```

### IDataverseClient

```csharp
namespace PPDS.Dataverse.Client;

/// <summary>
/// Abstraction over ServiceClient providing core Dataverse operations.
/// </summary>
public interface IDataverseClient : IOrganizationServiceAsync2
{
    /// <summary>
    /// Whether the connection is ready for operations.
    /// </summary>
    bool IsReady { get; }

    /// <summary>
    /// Server-recommended degree of parallelism.
    /// </summary>
    int RecommendedDegreesOfParallelism { get; }

    /// <summary>
    /// Connected organization ID.
    /// </summary>
    Guid? ConnectedOrgId { get; }

    /// <summary>
    /// Connected organization friendly name.
    /// </summary>
    string ConnectedOrgFriendlyName { get; }

    /// <summary>
    /// Last error message from the service.
    /// </summary>
    string? LastError { get; }

    /// <summary>
    /// Last exception from the service.
    /// </summary>
    Exception? LastException { get; }

    /// <summary>
    /// Creates a clone of this client (shares underlying connection).
    /// </summary>
    IDataverseClient Clone();
}
```

### IBulkOperationExecutor

```csharp
namespace PPDS.Dataverse.BulkOperations;

/// <summary>
/// Executes bulk operations using modern Dataverse APIs.
/// </summary>
public interface IBulkOperationExecutor
{
    /// <summary>
    /// Creates multiple records using CreateMultiple API.
    /// </summary>
    Task<BulkOperationResult> CreateMultipleAsync(
        string entityLogicalName,
        IEnumerable<Entity> entities,
        BulkOperationOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates multiple records using UpdateMultiple API.
    /// </summary>
    Task<BulkOperationResult> UpdateMultipleAsync(
        string entityLogicalName,
        IEnumerable<Entity> entities,
        BulkOperationOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Upserts multiple records using UpsertMultiple API.
    /// </summary>
    Task<BulkOperationResult> UpsertMultipleAsync(
        string entityLogicalName,
        IEnumerable<Entity> entities,
        BulkOperationOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes multiple records using DeleteMultiple API.
    /// </summary>
    Task<BulkOperationResult> DeleteMultipleAsync(
        string entityLogicalName,
        IEnumerable<Guid> ids,
        BulkOperationOptions? options = null,
        CancellationToken cancellationToken = default);
}
```

### IThrottleTracker

```csharp
namespace PPDS.Dataverse.Resilience;

/// <summary>
/// Tracks throttle state across connections.
/// </summary>
public interface IThrottleTracker
{
    /// <summary>
    /// Records a throttle event for a connection.
    /// </summary>
    void RecordThrottle(string connectionName, TimeSpan retryAfter);

    /// <summary>
    /// Checks if a connection is currently throttled.
    /// </summary>
    bool IsThrottled(string connectionName);

    /// <summary>
    /// Gets when a connection's throttle expires.
    /// </summary>
    DateTime? GetThrottleExpiry(string connectionName);

    /// <summary>
    /// Gets all connections that are not currently throttled.
    /// </summary>
    IEnumerable<string> GetAvailableConnections();

    /// <summary>
    /// Clears throttle state for a connection.
    /// </summary>
    void ClearThrottle(string connectionName);
}
```

---

## Configuration

### DataverseOptions (Root)

```csharp
namespace PPDS.Dataverse.DependencyInjection;

public class DataverseOptions
{
    /// <summary>
    /// Connection configurations. At least one required.
    /// </summary>
    public List<DataverseConnection> Connections { get; set; } = new();

    /// <summary>
    /// Connection pool settings.
    /// </summary>
    public ConnectionPoolOptions Pool { get; set; } = new();

    /// <summary>
    /// Resilience and retry settings.
    /// </summary>
    public ResilienceOptions Resilience { get; set; } = new();

    /// <summary>
    /// Bulk operation settings.
    /// </summary>
    public BulkOperationOptions BulkOperations { get; set; } = new();
}
```

### DataverseConnection

```csharp
namespace PPDS.Dataverse.Pooling;

public class DataverseConnection
{
    /// <summary>
    /// Unique name for this connection (for logging/metrics).
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Dataverse connection string.
    /// </summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// Weight for load balancing (higher = more traffic). Default: 1
    /// </summary>
    public int Weight { get; set; } = 1;

    /// <summary>
    /// Maximum connections to create for this configuration.
    /// </summary>
    public int MaxPoolSize { get; set; } = 10;

    public DataverseConnection() { }

    public DataverseConnection(string name, string connectionString)
    {
        Name = name;
        ConnectionString = connectionString;
    }
}
```

### ConnectionPoolOptions

```csharp
namespace PPDS.Dataverse.Pooling;

public class ConnectionPoolOptions
{
    /// <summary>
    /// Enable connection pooling. Default: true
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Total maximum connections across all configurations.
    /// </summary>
    public int MaxPoolSize { get; set; } = 50;

    /// <summary>
    /// Minimum idle connections to maintain.
    /// </summary>
    public int MinPoolSize { get; set; } = 5;

    /// <summary>
    /// Maximum time to wait for a connection. Default: 30 seconds
    /// </summary>
    public TimeSpan AcquireTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Maximum connection idle time before eviction. Default: 5 minutes
    /// </summary>
    public TimeSpan MaxIdleTime { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Maximum connection lifetime. Default: 30 minutes
    /// </summary>
    public TimeSpan MaxLifetime { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Disable affinity cookie for load distribution. Default: true (disabled)
    /// CRITICAL: Set to false (enable affinity) only for low-volume scenarios.
    /// </summary>
    public bool DisableAffinityCookie { get; set; } = true;

    /// <summary>
    /// Connection selection strategy. Default: ThrottleAware
    /// </summary>
    public ConnectionSelectionStrategy SelectionStrategy { get; set; }
        = ConnectionSelectionStrategy.ThrottleAware;

    /// <summary>
    /// Interval for background validation. Default: 1 minute
    /// </summary>
    public TimeSpan ValidationInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Enable background connection validation. Default: true
    /// </summary>
    public bool EnableValidation { get; set; } = true;
}

public enum ConnectionSelectionStrategy
{
    /// <summary>
    /// Simple round-robin across connections.
    /// </summary>
    RoundRobin,

    /// <summary>
    /// Select connection with fewest active clients.
    /// </summary>
    LeastConnections,

    /// <summary>
    /// Avoid throttled connections, fallback to round-robin.
    /// </summary>
    ThrottleAware
}
```

### ResilienceOptions

```csharp
namespace PPDS.Dataverse.Resilience;

public class ResilienceOptions
{
    /// <summary>
    /// Enable throttle tracking across connections. Default: true
    /// </summary>
    public bool EnableThrottleTracking { get; set; } = true;

    /// <summary>
    /// Default cooldown period when throttled (if not specified by server).
    /// </summary>
    public TimeSpan DefaultThrottleCooldown { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Maximum retry attempts for transient failures. Default: 3
    /// </summary>
    public int MaxRetryCount { get; set; } = 3;

    /// <summary>
    /// Base delay between retries. Default: 1 second
    /// </summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Use exponential backoff for retries. Default: true
    /// </summary>
    public bool UseExponentialBackoff { get; set; } = true;

    /// <summary>
    /// Maximum delay between retries. Default: 30 seconds
    /// </summary>
    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromSeconds(30);
}
```

### BulkOperationOptions

```csharp
namespace PPDS.Dataverse.BulkOperations;

public class BulkOperationOptions
{
    /// <summary>
    /// Records per batch. Default: 1000 (Dataverse limit)
    /// </summary>
    public int BatchSize { get; set; } = 1000;

    /// <summary>
    /// Continue on individual record failures. Default: true
    /// </summary>
    public bool ContinueOnError { get; set; } = true;

    /// <summary>
    /// Bypass custom plugin execution. Default: false
    /// </summary>
    public bool BypassCustomPluginExecution { get; set; } = false;

    /// <summary>
    /// Bypass Power Automate flows. Default: false
    /// </summary>
    public bool BypassPowerAutomateFlows { get; set; } = false;

    /// <summary>
    /// Suppress duplicate detection. Default: false
    /// </summary>
    public bool SuppressDuplicateDetection { get; set; } = false;
}
```

---

## DI Registration

```csharp
namespace PPDS.Dataverse.DependencyInjection;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds Dataverse connection pooling services.
    /// </summary>
    public static IServiceCollection AddDataverseConnectionPool(
        this IServiceCollection services,
        Action<DataverseOptions> configure)
    {
        services.Configure(configure);

        services.AddSingleton<IThrottleTracker, ThrottleTracker>();
        services.AddSingleton<IDataverseConnectionPool, DataverseConnectionPool>();
        services.AddTransient<IBulkOperationExecutor, BulkOperationExecutor>();

        return services;
    }

    /// <summary>
    /// Adds Dataverse connection pooling services from configuration.
    /// </summary>
    public static IServiceCollection AddDataverseConnectionPool(
        this IServiceCollection services,
        IConfiguration configuration,
        string sectionName = "Dataverse")
    {
        services.Configure<DataverseOptions>(configuration.GetSection(sectionName));

        services.AddSingleton<IThrottleTracker, ThrottleTracker>();
        services.AddSingleton<IDataverseConnectionPool, DataverseConnectionPool>();
        services.AddTransient<IBulkOperationExecutor, BulkOperationExecutor>();

        return services;
    }
}
```

---

## appsettings.json Configuration

```json
{
  "Dataverse": {
    "Connections": [
      {
        "Name": "Primary",
        "ConnectionString": "AuthType=ClientSecret;Url=https://org.crm.dynamics.com;ClientId=xxx;ClientSecret=xxx",
        "Weight": 2,
        "MaxPoolSize": 20
      },
      {
        "Name": "Secondary",
        "ConnectionString": "AuthType=ClientSecret;Url=https://org.crm.dynamics.com;ClientId=yyy;ClientSecret=yyy",
        "Weight": 1,
        "MaxPoolSize": 10
      }
    ],
    "Pool": {
      "Enabled": true,
      "MaxPoolSize": 50,
      "MinPoolSize": 5,
      "AcquireTimeout": "00:00:30",
      "MaxIdleTime": "00:05:00",
      "MaxLifetime": "00:30:00",
      "DisableAffinityCookie": true,
      "SelectionStrategy": "ThrottleAware",
      "EnableValidation": true,
      "ValidationInterval": "00:01:00"
    },
    "Resilience": {
      "EnableThrottleTracking": true,
      "DefaultThrottleCooldown": "00:05:00",
      "MaxRetryCount": 3,
      "RetryDelay": "00:00:01",
      "UseExponentialBackoff": true,
      "MaxRetryDelay": "00:00:30"
    },
    "BulkOperations": {
      "BatchSize": 1000,
      "ContinueOnError": true,
      "BypassCustomPluginExecution": false
    }
  }
}
```

---

## Usage Examples

### Basic Usage

```csharp
// Startup
services.AddDataverseConnectionPool(options =>
{
    options.Connections.Add(new DataverseConnection("Default", connectionString));
});

// Usage
public class AccountService
{
    private readonly IDataverseConnectionPool _pool;

    public AccountService(IDataverseConnectionPool pool) => _pool = pool;

    public async Task<Entity> GetAccountAsync(Guid accountId)
    {
        await using var client = await _pool.GetClientAsync();

        return await client.RetrieveAsync(
            "account",
            accountId,
            new ColumnSet("name", "telephone1"));
    }
}
```

### With CallerId Impersonation

```csharp
public async Task CreateAsUserAsync(Entity entity, Guid userId)
{
    var options = new DataverseClientOptions { CallerId = userId };

    await using var client = await _pool.GetClientAsync(options);
    await client.CreateAsync(entity);
}
```

### Bulk Operations

```csharp
public class DataImportService
{
    private readonly IBulkOperationExecutor _bulk;

    public DataImportService(IBulkOperationExecutor bulk) => _bulk = bulk;

    public async Task ImportAccountsAsync(IEnumerable<Entity> accounts)
    {
        var result = await _bulk.UpsertMultipleAsync(
            "account",
            accounts,
            new BulkOperationOptions
            {
                BatchSize = 1000,
                BypassCustomPluginExecution = true,
                ContinueOnError = true
            });

        Console.WriteLine($"Success: {result.SuccessCount}, Failed: {result.FailureCount}");

        foreach (var error in result.Errors)
        {
            Console.WriteLine($"Record {error.Index}: {error.Message}");
        }
    }
}
```

### Multi-Connection Load Distribution

```csharp
services.AddDataverseConnectionPool(options =>
{
    // Three different Application Users for 3x quota
    options.Connections = new List<DataverseConnection>
    {
        new("AppUser1", config["Dataverse:Connection1"]) { Weight = 1 },
        new("AppUser2", config["Dataverse:Connection2"]) { Weight = 1 },
        new("AppUser3", config["Dataverse:Connection3"]) { Weight = 1 },
    };

    options.Pool.SelectionStrategy = ConnectionSelectionStrategy.ThrottleAware;
    options.Resilience.EnableThrottleTracking = true;
});
```

---

## Thread Safety

All public types are thread-safe:

- `DataverseConnectionPool` - Thread-safe via `ConcurrentQueue` and `SemaphoreSlim`
- `ThrottleTracker` - Thread-safe via `ConcurrentDictionary`
- `BulkOperationExecutor` - Stateless, thread-safe
- `PooledClient` - Single-threaded use after acquisition (standard ServiceClient behavior)

---

## Performance Optimizations

### 1. Affinity Cookie Disabled by Default

```csharp
// Applied when creating ServiceClient
serviceClient.EnableAffinityCookie = false;
```

### 2. Thread Pool Configuration

The pool applies recommended .NET settings on initialization:

```csharp
// Applied once at startup
ThreadPool.SetMinThreads(100, 100);
ServicePointManager.DefaultConnectionLimit = 65000;
ServicePointManager.Expect100Continue = false;
ServicePointManager.UseNagleAlgorithm = false;
```

### 3. Connection Cloning

New connections are cloned from healthy existing connections when possible:

```csharp
// Cloning is ~10x faster than creating new connection
var newClient = existingClient.Clone();
```

### 4. Bulk API Usage

Bulk operations use modern APIs automatically:

| Operation | API Used | Throughput |
|-----------|----------|------------|
| CreateMultiple | `CreateMultipleRequest` | ~10M records/hour |
| UpdateMultiple | `UpdateMultipleRequest` | ~10M records/hour |
| UpsertMultiple | `UpsertMultipleRequest` | ~10M records/hour |
| DeleteMultiple | `DeleteMultipleRequest` | ~10M records/hour |

---

## Error Handling

### Throttle Detection

```csharp
try
{
    await client.CreateAsync(entity);
}
catch (FaultException<OrganizationServiceFault> ex)
    when (ex.Detail.ErrorCode == -2147015902 || // Number of requests exceeded
          ex.Detail.ErrorCode == -2147015903 || // Combined execution time exceeded
          ex.Detail.ErrorCode == -2147015898)   // Concurrent requests exceeded
{
    var retryAfter = ex.Detail.ErrorDetails.ContainsKey("Retry-After")
        ? (TimeSpan)ex.Detail.ErrorDetails["Retry-After"]
        : _options.Resilience.DefaultThrottleCooldown;

    _throttleTracker.RecordThrottle(connectionName, retryAfter);
    throw new ServiceProtectionException(connectionName, retryAfter, ex);
}
```

### Automatic Retry

Transient failures are automatically retried with exponential backoff:

```csharp
// Automatically retried
- 503 Service Unavailable
- 429 Too Many Requests (with Retry-After)
- Timeout exceptions
- Transient network errors
```

---

## Diagnostics

### Pool Statistics

```csharp
var stats = pool.Statistics;

Console.WriteLine($"Total Connections: {stats.TotalConnections}");
Console.WriteLine($"Active Connections: {stats.ActiveConnections}");
Console.WriteLine($"Idle Connections: {stats.IdleConnections}");
Console.WriteLine($"Throttled Connections: {stats.ThrottledConnections}");
Console.WriteLine($"Requests Served: {stats.RequestsServed}");
Console.WriteLine($"Throttle Events: {stats.ThrottleEvents}");
```

### OpenTelemetry Support

```csharp
// Activity source for tracing
services.AddOpenTelemetry()
    .WithTracing(builder => builder
        .AddSource("PPDS.Dataverse")
        .AddConsoleExporter());
```

---

## Comparison with Original Implementation

| Feature | Original | PPDS.Dataverse |
|---------|----------|----------------|
| Connection sources | Single connection string | Multiple connections |
| Selection strategy | N/A | Round-robin, least-connections, throttle-aware |
| Affinity cookie | Not configured | Disabled by default |
| Throttle handling | Internal retries only | Track per-connection, route away |
| Bulk operations | Not included | CreateMultiple, UpsertMultiple, etc. |
| Metrics | Basic logging | Pool statistics, OpenTelemetry |
| Lock contention | Unnecessary locks | Optimized concurrent collections |
| Recursion | Unbounded | Bounded iteration |
| Configuration | Code only | appsettings.json + fluent API |

---

## Related Documents

- [Package Strategy](00_PACKAGE_STRATEGY.md) - Overall SDK architecture
- [PPDS.Migration Design](02_PPDS_MIGRATION_DESIGN.md) - Migration engine (uses PPDS.Dataverse)
- [Implementation Prompts](03_IMPLEMENTATION_PROMPTS.md) - Prompts for building

---

## References

- [Service protection API limits](https://learn.microsoft.com/en-us/power-apps/developer/data-platform/api-limits)
- [ServiceClient best practices discussion](https://github.com/microsoft/PowerPlatform-DataverseServiceClient/discussions/312)
- [Bulk operation performance](https://learn.microsoft.com/en-us/power-apps/developer/data-platform/org-service/use-createmultiple-updatemultiple)
