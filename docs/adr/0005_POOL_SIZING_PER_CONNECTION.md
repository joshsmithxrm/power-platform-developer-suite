# ADR-0005: Pool Sizing Per Connection

**Status:** Implemented (v1.0.0)
**Applies to:** PPDS.Dataverse
**Date:** 2025-12-23

## Context

Microsoft's service protection limits are **per Application User** (per connection), not per environment:

- Each Application User can handle 52 concurrent requests (`RecommendedDegreesOfParallelism`)
- Multiple Application Users have **independent quotas**

A shared pool size across connections underutilizes capacity. With 2 connections and a shared max of 50, each user only gets ~25 connections, leaving ~50% of available capacity unused.

## Decision

Use **per-connection pool sizing** as the default:

```csharp
public class ConnectionPoolOptions
{
    /// <summary>
    /// Maximum concurrent connections per Application User (connection configuration).
    /// Default: 52 (matches Microsoft's RecommendedDegreesOfParallelism).
    /// Total pool capacity = this × number of configured connections.
    /// </summary>
    public int MaxConnectionsPerUser { get; set; } = 52;

    /// <summary>
    /// Fixed total pool size override. If set to non-zero, overrides
    /// MaxConnectionsPerUser calculation.
    /// Default: 0 (use per-connection sizing).
    /// </summary>
    public int MaxPoolSize { get; set; } = 0;
}
```

### Behavior

| Scenario | Calculation | Result |
|----------|-------------|--------|
| 1 connection, default | 1 × 52 | 52 total capacity |
| 2 connections, default | 2 × 52 | 104 total capacity |
| 4 connections, default | 4 × 52 | 208 total capacity |
| MaxPoolSize = 50 | 50 (fixed override) | 50 total capacity |

### Implementation

```csharp
private int CalculateTotalPoolCapacity()
{
    if (_options.Pool.MaxPoolSize > 0)
    {
        return _options.Pool.MaxPoolSize;  // Fixed override
    }

    return _options.Connections.Count * _options.Pool.MaxConnectionsPerUser;
}
```

## Consequences

### Positive

- **Optimal by default** - Utilizes full available quota without manual tuning
- **Scales naturally** - Add connections, get proportional capacity
- **Aligns with Microsoft** - Per-user limits match per-user pool sizing
- **Simple mental model** - "Each user can do 52 concurrent"

### Negative

- **Higher resource usage** - More connections = more memory

## References

- [Service Protection API Limits](https://learn.microsoft.com/en-us/power-apps/developer/data-platform/api-limits) - Per-user limits
- [Send Parallel Requests](https://learn.microsoft.com/en-us/power-apps/developer/data-platform/send-parallel-requests) - RecommendedDegreesOfParallelism
