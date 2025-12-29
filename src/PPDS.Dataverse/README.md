# PPDS.Dataverse

High-performance Dataverse connectivity with connection pooling, throttle-aware routing, and bulk operations.

## Installation

```bash
dotnet add package PPDS.Dataverse
```

## Quick Start

```csharp
// 1. Register services with typed configuration
services.AddDataverseConnectionPool(options =>
{
    options.Connections.Add(new DataverseConnection("Primary")
    {
        Url = "https://org.crm.dynamics.com",
        ClientId = "your-client-id",
        ClientSecret = Environment.GetEnvironmentVariable("DATAVERSE_SECRET"),
        TenantId = "your-tenant-id"
    });
});

// 2. Inject and use
public class AccountService
{
    private readonly IDataverseConnectionPool _pool;

    public AccountService(IDataverseConnectionPool pool) => _pool = pool;

    public async Task<Entity> GetAccountAsync(Guid id)
    {
        await using var client = await _pool.GetClientAsync();
        return await client.RetrieveAsync("account", id, new ColumnSet(true));
    }
}
```

## Features

### Connection Pooling

Reuse connections efficiently with automatic lifecycle management:

```csharp
options.Pool.MaxIdleTime = TimeSpan.FromMinutes(5);
options.Pool.MaxLifetime = TimeSpan.FromMinutes(30);
// Pool size is automatically determined by DOP (server-recommended parallelism)
```

### Multi-Connection Load Distribution

Distribute load across multiple Application Users to multiply your API quota:

```csharp
options.Connections.Add(new DataverseConnection("AppUser1")
{
    Url = "https://org.crm.dynamics.com",
    ClientId = "app-user-1-client-id",
    ClientSecret = Environment.GetEnvironmentVariable("DATAVERSE_SECRET_1")
});
options.Connections.Add(new DataverseConnection("AppUser2")
{
    Url = "https://org.crm.dynamics.com",
    ClientId = "app-user-2-client-id",
    ClientSecret = Environment.GetEnvironmentVariable("DATAVERSE_SECRET_2")
});
// 2 users = 2x the quota!

options.Pool.SelectionStrategy = ConnectionSelectionStrategy.ThrottleAware;
```

### Throttle-Aware Routing

Automatically routes requests away from throttled connections:

```csharp
options.Pool.SelectionStrategy = ConnectionSelectionStrategy.ThrottleAware;
options.Resilience.EnableThrottleTracking = true;
```

### Bulk Operations

High-throughput data operations using modern Dataverse APIs:

```csharp
var executor = serviceProvider.GetRequiredService<IBulkOperationExecutor>();

var result = await executor.UpsertMultipleAsync("account", entities,
    new BulkOperationOptions
    {
        BatchSize = 1000,
        ContinueOnError = true,
        BypassCustomPluginExecution = true
    });

Console.WriteLine($"Success: {result.SuccessCount}, Failed: {result.FailureCount}");
```

### DOP-Based Parallelism

The pool uses the server's `RecommendedDegreesOfParallelism` (from the `x-ms-dop-hint` header) to determine optimal parallelism. This provides:

- **Automatic tuning**: Parallelism matches what the server recommends
- **Environment-aware**: Trial environments get lower DOP (~4), production gets higher (~50)
- **Safe by default**: No risk of guessing wrong parallelism values

To scale throughput, add more Application Users - each multiplies your API quota:

```
1 Application User  @ DOP=4  →  4 parallel requests
2 Application Users @ DOP=4  →  8 parallel requests
4 Application Users @ DOP=4  → 16 parallel requests
```

See [ADR-0005](docs/adr/0005_DOP_BASED_PARALLELISM.md) for details.

### Affinity Cookie Disabled by Default

The SDK's affinity cookie routes all requests to a single backend node. Disabling it provides 10x+ throughput improvement:

```csharp
options.Pool.DisableAffinityCookie = true; // Default
```

## Configuration

### Typed Configuration (Recommended)

Use typed properties with environment variable secret resolution:

```csharp
services.AddDataverseConnectionPool(options =>
{
    options.Connections.Add(new DataverseConnection("Primary")
    {
        Url = "https://org.crm.dynamics.com",
        ClientId = "your-client-id",
        ClientSecret = Environment.GetEnvironmentVariable("DATAVERSE_SECRET"),
        TenantId = "your-tenant-id",
        AuthType = DataverseAuthType.ClientSecret
    });
    options.Pool.MaxConnectionsPerUser = 52;
    options.Pool.DisableAffinityCookie = true;
    options.Pool.SelectionStrategy = ConnectionSelectionStrategy.ThrottleAware;
});
```

### Secret Sources

- **`ClientSecret`** - Set directly (read from env var, config, etc. at your discretion)
- **`ClientSecretKeyVaultUri`** - Library fetches from Azure Key Vault automatically

### Authentication Types

```csharp
// Client Secret (most common for server-to-server)
new DataverseConnection("Primary")
{
    AuthType = DataverseAuthType.ClientSecret,
    Url = "https://org.crm.dynamics.com",
    ClientId = "your-client-id",
    ClientSecret = Environment.GetEnvironmentVariable("DATAVERSE_SECRET")
}

// Certificate Authentication
new DataverseConnection("Primary")
{
    AuthType = DataverseAuthType.Certificate,
    Url = "https://org.crm.dynamics.com",
    ClientId = "your-client-id",
    CertificateThumbprint = "ABC123...",
    CertificateStoreLocation = "CurrentUser"
}

// OAuth (Interactive)
new DataverseConnection("Primary")
{
    AuthType = DataverseAuthType.OAuth,
    Url = "https://org.crm.dynamics.com",
    ClientId = "your-client-id",
    RedirectUri = "http://localhost:8080",
    LoginPrompt = OAuthLoginPrompt.Auto
}
```

## Multi-Environment Scenarios

When working with multiple environments (Dev, QA, Prod), **do not put them in the same connection pool**. The pool is designed for load-balancing within a single organization.

### Correct: Separate Providers per Environment

```csharp
// Create separate providers per environment
await using var devProvider = CreateProvider("https://dev.crm.dynamics.com");
await using var qaProvider = CreateProvider("https://qa.crm.dynamics.com");

// Export from Dev
var devExporter = devProvider.GetRequiredService<IExporter>();
await devExporter.ExportAsync(schema, "data.zip", options);

// Import to QA
var qaImporter = qaProvider.GetRequiredService<IImporter>();
await qaImporter.ImportAsync("data.zip", importOptions);

ServiceProvider CreateProvider(string url)
{
    var services = new ServiceCollection();
    services.AddDataverseConnectionPool(options =>
    {
        options.Connections.Add(new DataverseConnection("Primary")
        {
            Url = url,
            ClientId = Environment.GetEnvironmentVariable("DATAVERSE_CLIENT_ID"),
            ClientSecret = Environment.GetEnvironmentVariable("DATAVERSE_SECRET")
        });
    });
    return services.BuildServiceProvider();
}
```

### When to Use Multiple Connections in One Pool

Multiple connections are appropriate when using **same organization, multiple Application Users** to multiply your API quota.

## Impersonation

Execute operations on behalf of another user:

```csharp
var options = new DataverseClientOptions { CallerId = userId };
await using var client = await pool.GetClientAsync(options);
await client.CreateAsync(entity);  // Created as userId
```

## Pool Statistics

Monitor pool health:

```csharp
var stats = pool.Statistics;
Console.WriteLine($"Active: {stats.ActiveConnections}");
Console.WriteLine($"Idle: {stats.IdleConnections}");
Console.WriteLine($"Throttled: {stats.ThrottledConnections}");
Console.WriteLine($"Requests: {stats.RequestsServed}");
```

## Security

### Connection String Handling

Connection strings contain sensitive credentials. This library provides built-in protection:

**Automatic Redaction:** Connection strings are automatically redacted in logs and error messages.

**Safe ToString:** `DataverseConnection.ToString()` excludes credentials:

```csharp
var connection = new DataverseConnection("Primary") { ... };
Console.WriteLine(connection); // "DataverseConnection { Name = Primary, Url = https://..., AuthType = ClientSecret }"
```

### Best Practices

1. **Use Environment Variables** - Read from env vars: `ClientSecret = Environment.GetEnvironmentVariable("...")`
2. **Use Azure Key Vault** - For production, use `ClientSecretKeyVaultUri` to fetch automatically
3. **Never log connection details directly**

## Target Frameworks

- `net8.0`
- `net10.0`

## License

MIT License
