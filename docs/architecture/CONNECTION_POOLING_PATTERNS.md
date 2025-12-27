# Connection Pooling Pattern

## When to Use

Use connection pooling when your application:

- Makes multiple Dataverse requests per operation
- Handles concurrent users or background jobs
- Needs to maximize throughput
- Wants to avoid connection setup overhead

## When NOT to Use

Skip pooling for:

- Single-request CLI tools
- Low-frequency scheduled jobs (< 10 requests/minute)
- Plugins running inside Dataverse (use the provided `IOrganizationService`)

## Basic Pattern

```csharp
// Register once at startup
services.AddDataverseConnectionPool(options =>
{
    options.Connections.Add(new DataverseConnection("Primary", connectionString));
});

// Inject the pool
public class MyService
{
    private readonly IDataverseConnectionPool _pool;

    public MyService(IDataverseConnectionPool pool) => _pool = pool;

    public async Task DoWorkAsync()
    {
        // Get a client - returns to pool when disposed
        await using var client = await _pool.GetClientAsync();

        // Use it
        var result = await client.RetrieveAsync("account", id, new ColumnSet(true));
    }
}
```

## Key Points

### Always Dispose the Client

The pooled client returns to the pool on dispose. Use `await using` or `using`:

```csharp
// ✅ Correct - client returns to pool
await using var client = await _pool.GetClientAsync();

// ❌ Wrong - connection leak
var client = await _pool.GetClientAsync();
// forgot to dispose
```

### Don't Store the Client

Get a client, use it, dispose it. Don't store it in a field:

```csharp
// ❌ Wrong - holding a pooled connection
public class BadService
{
    private IPooledClient _client; // Don't do this

    public BadService(IDataverseConnectionPool pool)
    {
        _client = pool.GetClient(); // Blocks the pool
    }
}

// ✅ Correct - get per operation
public class GoodService
{
    private readonly IDataverseConnectionPool _pool;

    public GoodService(IDataverseConnectionPool pool) => _pool = pool;

    public async Task DoWorkAsync()
    {
        await using var client = await _pool.GetClientAsync();
        // use and release
    }
}
```

### Parallel Operations

For parallel work, get multiple clients:

```csharp
var tasks = accountIds.Select(async id =>
{
    await using var client = await _pool.GetClientAsync();
    return await client.RetrieveAsync("account", id, new ColumnSet("name"));
});

var results = await Task.WhenAll(tasks);
```

### Using Server-Recommended Parallelism

Microsoft provides a recommended degree of parallelism via the `x-ms-dop-hint` response header, exposed as `RecommendedDegreesOfParallelism` on the client. **Performance degrades if you exceed this value.**

```csharp
await using var client = await _pool.GetClientAsync();

// Get server-recommended parallelism (typically varies by environment)
int recommendedDop = client.RecommendedDegreesOfParallelism;

var parallelOptions = new ParallelOptions
{
    MaxDegreeOfParallelism = recommendedDop
};

await Parallel.ForEachAsync(records, parallelOptions, async (record, ct) =>
{
    await using var innerClient = await _pool.GetClientAsync();
    await innerClient.CreateAsync(record, ct);
});
```

> **Note:** The recommended value varies based on environment resources and current load. Always query it dynamically rather than hardcoding.

## Scaling Pattern

For high-throughput scenarios, use multiple Application Users:

```csharp
services.AddDataverseConnectionPool(options =>
{
    // 3 Application Users = 3x the API quota
    options.Connections.Add(new("AppUser1", config["Conn1"]));
    options.Connections.Add(new("AppUser2", config["Conn2"]));
    options.Connections.Add(new("AppUser3", config["Conn3"]));

    options.Pool.MaxPoolSize = 50;
    options.Pool.SelectionStrategy = ConnectionSelectionStrategy.ThrottleAware;
});
```

## Monitoring

Check pool health:

```csharp
var stats = _pool.Statistics;

if (stats.ThrottledConnections > 0)
{
    _logger.LogWarning("Connections throttled: {Count}", stats.ThrottledConnections);
}

_logger.LogInformation(
    "Pool: Active={Active}, Idle={Idle}, Served={Served}",
    stats.ActiveConnections,
    stats.IdleConnections,
    stats.RequestsServed);
```

## Configuration Reference

| Setting | Default | Description |
|---------|---------|-------------|
| `MaxConnectionsPerUser` | 52 | Connections per Application User |
| `MaxPoolSize` | 0 | Fixed total (0 = use per-user sizing) |
| `MinPoolSize` | 5 | Minimum idle connections |
| `AcquireTimeout` | 30s | Max wait for a connection |
| `MaxIdleTime` | 5m | Evict idle connections after |
| `MaxLifetime` | 60m | Recycle connections after |
| `DisableAffinityCookie` | true | Distribute across backend nodes |
| `SelectionStrategy` | ThrottleAware | How to pick connections |

**Note:** By default, pool capacity = `MaxConnectionsPerUser` × number of connections. See [ADR-0005](../adr/0005_POOL_SIZING_PER_CONNECTION.md).

## Performance Settings Applied Automatically

The connection pool automatically applies Microsoft's recommended performance settings:

```csharp
ThreadPool.SetMinThreads(100, 100);           // Default is 4
ServicePointManager.DefaultConnectionLimit = 65000;  // Default is 2
ServicePointManager.Expect100Continue = false;
ServicePointManager.UseNagleAlgorithm = false;
```

These are applied once when the first pool is created. If you bypass the pool and create `ServiceClient` instances directly, you must apply these settings manually.

## References

- [Send parallel requests](https://learn.microsoft.com/en-us/power-apps/developer/data-platform/send-parallel-requests) - Parallelism patterns and `x-ms-dop-hint`
- [Optimize performance for bulk operations](https://learn.microsoft.com/en-us/power-apps/developer/data-platform/optimize-performance-create-update) - Connection optimization settings
- [Service protection API limits](https://learn.microsoft.com/en-us/power-apps/developer/data-platform/api-limits) - Throttling thresholds
- [Scaling Dynamics 365 CRM Integrations in Azure](https://techcommunity.microsoft.com/blog/microsoftmissioncriticalblog/scaling-dynamics-365-crm-integrations-in-azure-the-right-way-to-use-the-sdk-s/4447143) - Clone pattern and anti-patterns
