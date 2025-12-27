# ADR-0007: Connection Source Abstraction

**Status:** Accepted
**Date:** 2025-12-27
**Applies to:** PPDS.Dataverse

## Context

The `DataverseConnectionPool` was tightly coupled to connection string-based authentication via `DataverseOptions`. This forced the CLI to use a separate `DeviceCodeConnectionPool` implementation that:

1. **Didn't actually pool** - Cloned on every request instead of reusing connections
2. **Missed all pool features** - No throttle tracking, no adaptive rate control, no connection validation
3. **Caused failures under load** - `Clone()` during throttle periods failed and wasn't retried properly

The root cause was that the pool conflated two concerns:
- **Authentication** - How to get an initial ServiceClient
- **Pooling** - How to manage clones of that client

## Decision

Introduce `IConnectionSource` abstraction to separate authentication from pooling:

```csharp
public interface IConnectionSource : IDisposable
{
    string Name { get; }
    int MaxPoolSize { get; }
    ServiceClient GetSeedClient();
}
```

The pool now accepts `IConnectionSource[]` instead of `DataverseOptions`:

```csharp
public DataverseConnectionPool(
    IEnumerable<IConnectionSource> sources,
    IThrottleTracker throttleTracker,
    IAdaptiveRateController adaptiveRateController,
    ILogger<DataverseConnectionPool> logger)
```

### Built-in Implementations

| Source | Use Case |
|--------|----------|
| `ConnectionStringSource` | Traditional client credentials (appsettings.json) |
| `ServiceClientSource` | Pre-authenticated clients (device code, managed identity, custom) |

### Usage Examples

**Configuration-based (existing pattern):**
```csharp
// DI extension creates ConnectionStringSources from configuration
services.AddDataverseConnectionPool(configuration);
```

**Pre-authenticated client:**
```csharp
// CLI device code flow
var client = await DeviceCodeAuth.AuthenticateAsync(url);
var source = new ServiceClientSource(client, "Interactive", maxPoolSize: 32);
var pool = new DataverseConnectionPool(new[] { source }, throttleTracker, rateController, logger);
```

**Managed identity:**
```csharp
var client = new ServiceClient(url, tokenProviderFunc);
var source = new ServiceClientSource(client, "ManagedIdentity");
var pool = new DataverseConnectionPool(new[] { source }, ...);
```

## Consequences

### Positive

- **Any auth method can use the pool** - Device code, managed identity, certificate, custom token providers
- **CLI gets full pool features** - Throttle tracking, adaptive rate control, connection validation
- **No duplicate implementations** - Single pool handles all scenarios
- **Testability** - Easy to mock connection sources in tests
- **Extensibility** - Custom sources for specialized scenarios (e.g., rotating credentials)

### Negative

- **Breaking change for direct pool consumers** - Must provide `IConnectionSource[]` instead of `DataverseOptions`
- **Slightly more complex extension point** - Custom auth requires implementing `IConnectionSource`

### Migration

Existing code using `AddDataverseConnectionPool(configuration)` continues to work unchanged. The DI extension internally creates `ConnectionStringSource` instances from configuration.

## References

- [ADR-0002: Multi-Connection Pooling](0002_MULTI_CONNECTION_POOLING.md) - Original pooling design
- [ADR-0005: Pool Sizing Per Connection](0005_POOL_SIZING_PER_CONNECTION.md) - Per-source sizing
