# ADR-0005: Pool Sizing Per Connection

**Status:** Approved for Implementation
**Applies to:** PPDS.Dataverse
**Date:** 2025-12-23

## Context

Microsoft's service protection limits are **per Application User** (per connection), not per environment:

- Each Application User can handle 52 concurrent requests (`RecommendedDegreesOfParallelism`)
- Multiple Application Users have **independent quotas**

Current configuration uses a shared pool size:

```csharp
public class PoolOptions
{
    public int MaxPoolSize { get; set; } = 50; // Shared across all connections
}
```

With 2 connections configured, this results in ~25 connections per user, leaving ~50% of available capacity unused.

## Decision

Change the default from **shared pool size** to **per-connection pool size**:

```csharp
public class PoolOptions
{
    /// <summary>
    /// Maximum concurrent connections per Application User (connection configuration).
    /// Default: 52 (matches Microsoft's RecommendedDegreesOfParallelism).
    /// Total pool capacity = this × number of configured connections.
    /// </summary>
    public int MaxConnectionsPerUser { get; set; } = 52;

    /// <summary>
    /// Legacy: Maximum total pool size across all connections.
    /// If set to non-zero, overrides MaxConnectionsPerUser calculation.
    /// Default: 0 (use per-connection sizing).
    /// </summary>
    [Obsolete("Use MaxConnectionsPerUser for optimal throughput")]
    public int MaxPoolSize { get; set; } = 0;
}
```

### Behavior

| Scenario | Calculation | Result |
|----------|-------------|--------|
| 1 connection, default | 1 × 52 | 52 total capacity |
| 2 connections, default | 2 × 52 | 104 total capacity |
| 4 connections, default | 4 × 52 | 208 total capacity |
| Legacy MaxPoolSize = 50 | 50 (ignores per-connection) | 50 total capacity |

### Implementation

```csharp
private int CalculateTotalPoolCapacity()
{
    // Legacy override takes precedence
    #pragma warning disable CS0618
    if (_options.Pool.MaxPoolSize > 0)
    {
        return _options.Pool.MaxPoolSize;
    }
    #pragma warning restore CS0618

    // Per-connection sizing (recommended)
    return _options.Connections.Count * _options.Pool.MaxConnectionsPerUser;
}

// Semaphore initialization
var totalCapacity = CalculateTotalPoolCapacity();
_connectionSemaphore = new SemaphoreSlim(totalCapacity);
```

## Consequences

### Positive

- **Optimal by default** - Utilizes full available quota without manual tuning
- **Scales naturally** - Add connections, get proportional capacity
- **Aligns with Microsoft** - Per-user limits match per-user pool sizing
- **Simple mental model** - "Each user can do 52 concurrent"

### Negative

- **Higher resource usage** - More connections = more memory
- **Breaking change for some** - Users expecting shared sizing may be surprised
- **Need migration path** - Document the change, keep legacy option

### Migration

Users who explicitly set `MaxPoolSize` keep their behavior. Users on defaults get improved throughput automatically.

## References

- [Service Protection API Limits](https://learn.microsoft.com/en-us/power-apps/developer/data-platform/api-limits) - Per-user limits
- [Send Parallel Requests](https://learn.microsoft.com/en-us/power-apps/developer/data-platform/send-parallel-requests) - RecommendedDegreesOfParallelism
