# ADR-0019: Pool-Managed Concurrency

**Status:** Accepted
**Date:** 2026-01-05
**Applies to:** PPDS.Dataverse

## Context

When multiple consumers share a connection pool (e.g., multiple entities importing in parallel, or a VS Code extension and CLI sharing a daemon's pool), each consumer independently determines its parallelism.

### The Problem

Prior to this decision, `BulkOperationExecutor.ExecuteBatchesAdaptiveAsync()` read `pool.GetTotalRecommendedParallelism()` and spawned that many batch tasks. This worked for a single consumer but broke with multiple concurrent consumers:

```
TieredImporter processes 4 entities in parallel
│
├─► Entity A: pool.GetTotalRecommendedParallelism() = 16
│             Spawns 16 batch tasks
│
├─► Entity B: pool.GetTotalRecommendedParallelism() = 16 (SAME VALUE!)
│             Spawns 16 batch tasks
│
├─► Entity C: Spawns 16 batch tasks
│
└─► Entity D: Spawns 16 batch tasks
                    │
                    ▼
          64 TASKS competing for 16 semaphore slots
                    │
                    ▼
          Semaphore timeout → PoolExhaustedException storm
```

Each consumer assumed it could use the FULL pool capacity. With 4 consumers, this caused 4× oversubscription.

### Observed Behavior

During a large data migration:
- Tier 2 had 2 entities running in parallel
- Tier 3 started with a single entity (43,710 records = 437 batches)
- 125+ simultaneous "Connection pool exhausted" warnings
- All tasks retrying with exponential backoff at the same time (thundering herd)

### Future Considerations

This problem compounds in daemon scenarios where:
- VS Code extension, CLI commands, and background jobs share a pool
- Each consumer doesn't know about the others
- No central coordination of parallelism

## Decision

**Callers should not pre-calculate parallelism. Let the pool semaphore naturally limit concurrency.**

### Implementation

Replace the adaptive DOP-reading loop with simple `Parallel.ForEachAsync`:

```csharp
// BEFORE: Caller pre-calculates parallelism (broken with multiple consumers)
var maxParallelism = _connectionPool.GetTotalRecommendedParallelism();
while (pending.Count > 0 && inFlight.Count < maxParallelism)
{
    var task = SpawnBatchTask();
    inFlight.Add(task);
}

// AFTER: Pool semaphore naturally blocks when full
await Parallel.ForEachAsync(
    batches,
    new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount * 4 },
    async (batch, ct) =>
    {
        // This blocks when pool is at capacity - fair queuing!
        await using var client = await pool.GetClientAsync(ct);
        await ExecuteBatch(client, batch);
    });
```

The pool's semaphore (sized to total DOP per ADR-0005) handles:
- Queuing tasks when at capacity
- Fairness between concurrent consumers
- Releasing slots when connections return

### Relationship to ADR-0005

ADR-0005 (DOP-Based Parallelism) established:
- Pool semaphore capacity = sum of per-source DOPs
- Don't exceed DOP to avoid throttles
- Scale by adding connections (Application Users)

**That remains unchanged.** ADR-0019 changes WHO decides parallelism:

| Aspect | ADR-0005 | ADR-0019 |
|--------|----------|----------|
| Pool capacity sizing | DOP-based | (unchanged) |
| Who limits concurrency | Callers read DOP | Pool semaphore blocks |
| Multiple consumers | Each uses full DOP | Fair queueing |

## Consequences

### Positive

- **Fair sharing**: Multiple consumers queue on the pool semaphore
- **Simpler caller code**: No parallelism calculation needed
- **Daemon-ready**: VS Code, CLI, background jobs all share fairly
- **Self-regulating**: Pool adapts to any number of consumers
- **No coordination required**: Consumers don't need to know about each other

### Negative

- **More blocked Tasks**: Tasks block on `GetClientAsync()` rather than not being spawned
  - .NET handles this efficiently (tasks are lightweight when awaiting)
  - Trade-off is acceptable for correctness

## Addendum: Throttling Edge Case (2026-01-06)

The original guidance ("use `ProcessorCount * 4`") proved problematic on high-core machines during throttling:

```
Machine: 24 cores → ProcessorCount * 4 = 96 tasks
Pool capacity: 20 slots (4 profiles × 5 DOP)
AcquireTimeout: 120 seconds
```

When service protection throttling occurs:
1. Throttled connections hold semaphore slots during Retry-After (30s-5min)
2. Effective throughput drops from 20 to ~10-15 active connections
3. 96 tasks queue for reduced slots, exceeding AcquireTimeout

**Solution:** Cap parallelism at `Math.Min(ProcessorCount * 4, poolCapacity)`:

```csharp
var cpuBasedLimit = Environment.ProcessorCount * 4;
var poolCapacity = _connectionPool.GetTotalRecommendedParallelism();
var effectiveParallelism = Math.Min(cpuBasedLimit, Math.Max(poolCapacity, 1));
```

This differs from the regressed approach (which ONLY used pool capacity) by taking the SMALLER of CPU-based and pool-based limits, ensuring:
- Small pool → don't spawn excess tasks (prevents throttle timeout storms)
- Large pool + few cores → don't exceed CPU-based limit (original behavior)

### Measured Impact

| Scenario | Before | After |
|----------|--------|-------|
| Pool exhaustion warnings | 125+ simultaneous | 0 |
| 4 entities × 16 DOP | 64 tasks, 30s timeout storm | 64 tasks, fair queueing |
| Throughput | Degraded by retries | Pool-limited, stable |

## References

- [ADR-0005: DOP-Based Parallelism](0005_DOP_BASED_PARALLELISM.md) - Pool capacity sizing
- [ADR-0002: Multi-Connection Pooling](0002_MULTI_CONNECTION_POOLING.md) - Multiple users multiply quota
