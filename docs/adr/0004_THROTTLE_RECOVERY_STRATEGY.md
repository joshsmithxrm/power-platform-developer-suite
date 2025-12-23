# ADR-0004: Throttle Recovery Strategy

**Status:** Accepted (with known limitation)
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

## Known Limitation

**The current implementation does not implement adaptive scaling after throttle recovery.**

Resuming at full parallelism immediately after `Retry-After` can cause:
- Immediate re-throttling
- Progressively longer `Retry-After` durations
- Suboptimal total throughput

### Optimal Behavior (Future Enhancement)

Microsoft recommends TCP-like congestion control:

```
After throttle recovery:
1. Resume at reduced parallelism (e.g., 50%)
2. Gradually ramp up if successful
3. Back off immediately if throttled again
4. Find and maintain sustainable rate
```

## Planned Enhancement

Adaptive rate control using AIMD (Additive Increase, Multiplicative Decrease) algorithm is designed and ready for implementation.

**See:** [ADAPTIVE_RATE_CONTROL_SPEC.md](../architecture/ADAPTIVE_RATE_CONTROL_SPEC.md)

Key features:
- Start at 50% of `RecommendedDegreesOfParallelism`
- Increase gradually after sustained success (batch count + time interval)
- Halve parallelism on throttle
- Fast recovery to last-known-good, then cautious probing
- 5-minute TTL on historical state (matches Microsoft's rolling window)
- Idle reset for long-running integrations

## Consequences

### Positive

- **No blocking**: Requests don't hold semaphore slots while waiting
- **Transparent**: Consumer doesn't need to handle service protection errors
- **Simple**: Easy to understand and debug

### Negative

- **Suboptimal recovery**: Full parallelism after recovery may cause re-throttling
- **Extended penalties**: Aggressive resumption can extend `Retry-After` durations
- **Consumer workaround needed**: For optimal throughput, consumers should manage parallelism externally

### Consumer Workaround

Until adaptive scaling is implemented, consumers can manage parallelism manually:

```csharp
// Start conservative, let the pool handle throttle waiting
var options = new BulkOperationOptions
{
    MaxParallelBatches = 10 // Lower than RecommendedDegreesOfParallelism
};

await executor.UpsertMultipleAsync(entities, options);
```

## References

- [Service protection API limits](https://learn.microsoft.com/en-us/power-apps/developer/data-platform/api-limits) - Retry-After behavior
- [Maximize API throughput](https://learn.microsoft.com/en-us/dynamics365/fin-ops-core/dev-itpro/data-entities/service-protection-maximizing-api-throughput) - Microsoft's ramp-up recommendation
- ADR-0003: Throttle-Aware Connection Selection - Related throttle handling decision
