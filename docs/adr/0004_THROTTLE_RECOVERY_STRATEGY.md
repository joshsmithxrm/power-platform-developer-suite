# ADR-0004: Throttle Recovery Strategy

**Status:** Accepted
**Date:** 2025-12-22
**Applies to:** PPDS.Dataverse

## Context

When all connections are throttled, the pool must wait for the `Retry-After` period before resuming operations. Microsoft recommends a gradual ramp-up strategy after throttle recovery to minimize extended penalties:

> "If the application continues to send such demanding requests, the duration is extended to minimize the impact on shared resources. This causes the individual retry-after duration period to be longer."
>
> "When possible, we recommend trying to achieve a consistent rate by starting with a lower number of requests and gradually increasing until you start hitting the service protection API limits."

## Decision

### Current Implementation (v1)

The pool implements **transparent throttle waiting** with immediate full-parallelism recovery:

1. **Throttle detection**: PooledClient automatically records throttle via callback
2. **Wait phase**: `GetClientAsync` waits for throttle to clear **without holding semaphore slots**
3. **Recovery**: Resume at full configured parallelism immediately

```
Throttle detected → Wait for Retry-After → Resume at 100% parallelism
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

## Subsequent Enhancement

The limitation of immediate full-parallelism recovery was addressed in **ADR-0006: Execution Time Ceiling**.

The adaptive rate controller now implements AIMD (Additive Increase, Multiplicative Decrease) with execution time-aware ceilings:

- Tracks batch durations via exponential moving average
- Calculates dynamic parallelism ceiling based on batch time
- Halves parallelism on throttle, gradually increases on success
- Configurable via presets: Conservative, Balanced, Aggressive

**See:** [ADR-0006: Execution Time Ceiling](0006_EXECUTION_TIME_CEILING.md)

## Consequences

### Positive

- **No blocking**: Requests don't hold semaphore slots while waiting
- **Transparent**: Consumer doesn't need to handle service protection errors
- **Simple**: Easy to understand and debug

### Negative

- ~~**Suboptimal recovery**~~ - Addressed by ADR-0006 adaptive rate control
- ~~**Extended penalties**~~ - Addressed by ADR-0006 execution time ceiling
- ~~**Consumer workaround needed**~~ - No longer required; use presets

## References

- [Service protection API limits](https://learn.microsoft.com/en-us/power-apps/developer/data-platform/api-limits) - Retry-After behavior
- [Maximize API throughput](https://learn.microsoft.com/en-us/dynamics365/fin-ops-core/dev-itpro/data-entities/service-protection-maximizing-api-throughput) - Microsoft's ramp-up recommendation
- ADR-0003: Throttle-Aware Connection Selection - Related throttle handling decision
