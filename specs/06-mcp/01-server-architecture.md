# PPDS.Mcp: Server Architecture

## Overview

The PPDS.Mcp server provides Model Context Protocol (MCP) integration for AI assistants (Claude, etc.) to interact with Dataverse environments. Built on the Microsoft `ModelContextProtocol.Server` SDK, it uses stdio transport for IDE integration. The architecture centers around `McpToolContext` which provides shared access to connection pools and services, and `McpConnectionPoolManager` which caches long-lived connection pools keyed by profile+environment combinations.

## Public API

### Interfaces

| Interface | Purpose |
|-----------|---------|
| `IMcpConnectionPoolManager` | Manages cached connection pools keyed by profile+environment |

### Classes

| Class | Purpose |
|-------|---------|
| `McpConnectionPoolManager` | Implementation of connection pool caching with invalidation |
| `McpToolContext` | Shared context for tools - profile access, pool management, service creation |
| `ProfileConnectionSourceAdapter` | Adapts `ProfileConnectionSource` to `IConnectionSource` interface |

## Behaviors

### Server Initialization

```csharp
// Program.cs
Console.SetOut(Console.Error);  // MCP protocol uses stdout

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();  // Auto-discover [McpServerToolType] classes

builder.Services.AddSingleton<IMcpConnectionPoolManager, McpConnectionPoolManager>();
builder.Services.AddSingleton<McpToolContext>();
builder.Services.RegisterDataverseServices();

await host.RunAsync();
```

### Stdio Transport Constraints

| Rule | Reason |
|------|--------|
| stdout reserved for MCP protocol | JSON-RPC messages only |
| All logging goes to stderr | Console output redirected |
| LogLevel.Warning minimum | Reduce noise in MCP channel |

### Connection Pool Lifecycle

```
MCP Tool Request
    │
    ├── McpToolContext.GetPoolAsync()
    │       │
    │       └── McpConnectionPoolManager.GetOrCreatePoolAsync()
    │               │
    │               ├── Cache hit: Return existing pool
    │               │
    │               └── Cache miss: Create new pool
    │                       │
    │                       ├── Load ProfileCollection
    │                       ├── Create ProfileConnectionSource(s)
    │                       ├── Build ServiceProvider with DI
    │                       └── Cache entry by profile+environment key
    │
    └── Use pool for Dataverse operations
```

### Cache Key Generation

```csharp
// Profile names sorted for consistent keying
// Format: "profile1,profile2|https://org.crm.dynamics.com"
internal static string GenerateCacheKey(IReadOnlyList<string> profileNames, string environmentUrl)
{
    var sortedProfiles = string.Join(",", profileNames.OrderBy(p => p, StringComparer.OrdinalIgnoreCase));
    var normalizedUrl = url.TrimEnd('/').ToLowerInvariant();
    return $"{sortedProfiles}|{normalizedUrl}";
}
```

### Lazy<Task<T>> Pattern

Prevents duplicate pool creation during concurrent requests:

```csharp
var lazyEntry = _pools.GetOrAdd(cacheKey, _ => new Lazy<Task<CachedPoolEntry>>(
    () => CreatePoolEntryAsync(...),
    LazyThreadSafetyMode.ExecutionAndPublication));

var entry = await lazyEntry.Value;
```

### Invalidation

| Method | When to Call |
|--------|--------------|
| `InvalidateProfile(name)` | Profile modified or deleted |
| `InvalidateEnvironment(url)` | Environment selection changed |

Invalidation disposes affected pools and removes from cache.

## Edge Cases

| Scenario | Behavior | Notes |
|----------|----------|-------|
| No active profile | Throws `InvalidOperationException` | Message: "Run 'ppds auth create'" |
| No environment selected | Throws `InvalidOperationException` | Message: "Run 'ppds env select'" |
| Pool creation timeout | Throws `TimeoutException` | Default: 5 minutes |
| Concurrent create requests | Only one creates, others await | Lazy<Task<T>> pattern |
| Pool creation failure | Entry removed from cache | Next caller can retry |
| Device code flow | Callback passed to first creator | Others await same pool |

## Error Handling

| Exception | Condition | Recovery |
|-----------|-----------|----------|
| `InvalidOperationException` | No profile or environment | User runs setup commands |
| `TimeoutException` | Pool creation exceeds 5 min | Retry, check network |
| `ObjectDisposedException` | Manager disposed | Server shutdown |

## Dependencies

- **Internal**:
  - `PPDS.Auth.Profiles.ProfileStore` - Profile loading and saving
  - `PPDS.Auth.Pooling.ProfileConnectionSource` - Connection source creation
  - `PPDS.Dataverse.Pooling.DataverseConnectionPool` - Connection pooling
  - `PPDS.Dataverse.DependencyInjection` - Service registration
- **External**:
  - `ModelContextProtocol.Server` - MCP SDK
  - `Microsoft.Extensions.Hosting` - Host builder
  - `Microsoft.Extensions.DependencyInjection` - DI container

## Configuration

### McpConnectionPoolManager Constructor

| Parameter | Type | Default | Description |
|-----------|------|---------|-------------|
| `loggerFactory` | `ILoggerFactory?` | `NullLoggerFactory` | Logger factory |
| `loadProfilesAsync` | `Func<CancellationToken, Task<ProfileCollection>>?` | ProfileStore | Profile loader for testing |
| `poolCreationTimeout` | `TimeSpan?` | 5 minutes | Timeout for pool creation |

### CachedPoolEntry Contents

| Property | Type | Description |
|----------|------|-------------|
| `ServiceProvider` | `ServiceProvider` | Full DI container for services |
| `Pool` | `IDataverseConnectionPool` | Connection pool |
| `ProfileNames` | `IReadOnlySet<string>` | Profiles used |
| `EnvironmentUrl` | `string` | Target environment |
| `CredentialStore` | `ISecureCredentialStore` | Token storage |
| `CreatedAt` | `DateTimeOffset` | Creation timestamp |

### ConnectionPoolOptions

| Option | Value | Description |
|--------|-------|-------------|
| `Enabled` | `true` | Pool enabled |
| `DisableAffinityCookie` | `true` | Better load distribution |
| `MaxPoolSize` | 52 | Connections per source |

## Thread Safety

### Concurrent Access

- `ConcurrentDictionary` for pool cache
- `Lazy<Task<T>>` prevents duplicate creation
- `Interlocked.Exchange` for disposed flag
- `ConcurrentBag` for disposal task tracking

### Cancellation Handling

```csharp
// User's token for overall timeout
// Internal CancellationToken.None for Lazy<Task<>> factory
// Linked token for operation timeout
using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
timeoutCts.CancelAfter(_poolCreationTimeout);
```

## McpToolContext Usage

Tools use `McpToolContext` for common operations:

```csharp
[McpServerToolType]
public class QuerySqlTool
{
    private readonly McpToolContext _context;

    public QuerySqlTool(McpToolContext context)
    {
        _context = context;
    }

    public async Task<string> ExecuteAsync(string sql, CancellationToken ct)
    {
        var pool = await _context.GetPoolAsync(ct);
        // Use pool for query execution
    }
}
```

### McpToolContext Methods

| Method | Purpose |
|--------|---------|
| `GetActiveProfileAsync()` | Get current auth profile |
| `GetProfileCollectionAsync()` | Get all profiles |
| `GetPoolAsync()` | Get/create connection pool |
| `CreateServiceProviderAsync()` | Full DI for services (IMetadataService, etc.) |
| `SaveProfileCollectionAsync()` | Save profile changes |
| `InvalidateEnvironment()` | Clear cached pool for environment |

## Related

- [PPDS.Mcp: Tools](02-tools.md) - Tool implementations
- [PPDS.Dataverse: Connection Pooling](../01-dataverse/01-connection-pooling.md) - Pool internals
- [PPDS.Auth: Profile Storage](../02-auth/01-profile-storage.md) - Profile management

## Source Files

| File | Purpose |
|------|---------|
| `src/PPDS.Mcp/Program.cs` | Server entry point, DI configuration |
| `src/PPDS.Mcp/Infrastructure/IMcpConnectionPoolManager.cs` | Pool manager interface |
| `src/PPDS.Mcp/Infrastructure/McpConnectionPoolManager.cs` | Pool caching implementation |
| `src/PPDS.Mcp/Infrastructure/McpToolContext.cs` | Shared tool context |
| `src/PPDS.Mcp/Infrastructure/ProfileConnectionSourceAdapter.cs` | Connection source adapter |
| `src/PPDS.Mcp/Tools/*.cs` | MCP tool implementations |
