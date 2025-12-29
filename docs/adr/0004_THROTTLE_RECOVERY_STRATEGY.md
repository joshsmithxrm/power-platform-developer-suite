# ADR-0004: Throttle Recovery Strategy

**Status:** Accepted
**Date:** 2025-12-22
**Applies to:** PPDS.Dataverse

## Context

When all connections are throttled, the pool must wait for the `Retry-After` period before resuming operations. The question was how to handle this wait and subsequent recovery.

## Decision

### Implementation

The pool implements **transparent throttle waiting** with DOP-based parallelism:

1. **Throttle detection**: PooledClient automatically records throttle via callback
2. **Wait phase**: `GetClientAsync` waits for throttle to clear **without holding semaphore slots**
3. **Recovery**: Resume at DOP-based parallelism (see ADR-0005)

```
Throttle detected → Wait for Retry-After → Resume at DOP × connections
```

### Key Design: Semaphore Not Held During Wait

The pool separates "waiting for throttle" from "holding a connection slot":

```csharp
// Phase 1: Wait for non-throttled connection (NO semaphore held)
await WaitForNonThrottledConnectionAsync(cancellationToken);

// Phase 2: Acquire semaphore (only when ready to use connection)
await _connectionSemaphore.WaitAsync(timeout, cancellationToken);

// Phase 3: Get and use connection
return GetConnectionFromPoolCore(connectionName, options);
```

This prevents `PoolExhaustedException` when many requests are waiting for throttle recovery.

### Tolerance Option

The pool supports `MaxRetryAfterTolerance` for fail-fast scenarios:

```csharp
var poolOptions = new ConnectionPoolOptions
{
    MaxRetryAfterTolerance = TimeSpan.FromSeconds(30)  // Fail if wait exceeds this
};
```

When all connections are throttled and the shortest wait exceeds tolerance, `ServiceProtectionException` is thrown instead of waiting.

## Consequences

### Positive

- **No blocking**: Requests don't hold semaphore slots while waiting
- **Transparent**: Consumer doesn't need to handle service protection errors
- **Simple**: Easy to understand and debug
- **Configurable**: `MaxRetryAfterTolerance` allows fail-fast when needed

### Negative

- **All-or-nothing**: Either wait for full Retry-After or fail immediately

## References

- [Service protection API limits](https://learn.microsoft.com/en-us/power-apps/developer/data-platform/api-limits) - Retry-After behavior
- [ADR-0003: Throttle-Aware Connection Selection](0003_THROTTLE_AWARE_SELECTION.md) - Related throttle handling decision
- [ADR-0005: DOP-Based Parallelism](0005_DOP_BASED_PARALLELISM.md) - Parallelism model used after recovery
