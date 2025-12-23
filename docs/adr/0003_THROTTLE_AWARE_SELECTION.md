# ADR-0003: Throttle-Aware Connection Selection

**Status:** Accepted
**Applies to:** PPDS.Dataverse

## Context

When Dataverse returns a 429 (Too Many Requests) with a `Retry-After` header, the SDK's built-in retry logic waits and retries on the same connection. This wastes time when other connections have available quota.

## Decision

Implement throttle-aware connection selection as the default strategy:

```csharp
options.Pool.SelectionStrategy = ConnectionSelectionStrategy.ThrottleAware;
```

When a connection receives a throttle response:
1. Record the throttle state with expiry time
2. Route subsequent requests to non-throttled connections
3. Automatically clear throttle state when the cooldown expires

## Available Strategies

| Strategy | Behavior | Use Case |
|----------|----------|----------|
| `RoundRobin` | Simple rotation | Even distribution, no throttle awareness |
| `LeastConnections` | Fewest active clients | Balance concurrent load |
| `ThrottleAware` | Avoid throttled + round-robin | **Default.** High-throughput with multiple connections |

## Consequences

### Positive

- **Maximizes throughput** - No wasted time on throttled connections
- **Automatic recovery** - Connections return to rotation after cooldown
- **Transparent** - Application code doesn't need to handle throttling

### Negative

- Requires tracking state per connection (memory overhead, though minimal)
- If all connections are throttled, falls back to first connection (must still wait)

### How It Works

```
Request 1 → AppUser1 (available) ✓
Request 2 → AppUser2 (available) ✓
Request 3 → AppUser3 (available) ✓
Request 4 → AppUser1 → 429 Throttled (5 min cooldown)
            ThrottleTracker.Record("AppUser1", 5 min)
Request 5 → AppUser2 (available, skip AppUser1) ✓
Request 6 → AppUser3 (available, skip AppUser1) ✓
...
[5 minutes later]
Request N → AppUser1 (cooldown expired, available again) ✓
```

## References

- [Retry-After header](https://learn.microsoft.com/en-us/power-apps/developer/data-platform/api-limits#retry-operations) - How to handle throttle responses
- [Service protection limits](https://learn.microsoft.com/en-us/power-apps/developer/data-platform/api-limits) - Throttling thresholds and error codes
- [Optimize performance for bulk operations](https://learn.microsoft.com/en-us/power-apps/developer/data-platform/optimize-performance-create-update) - Throttle handling best practices
- [Maximize API throughput](https://learn.microsoft.com/en-us/dynamics365/fin-ops-core/dev-itpro/data-entities/service-protection-maximizing-api-throughput) - Strategies for high-throughput scenarios
