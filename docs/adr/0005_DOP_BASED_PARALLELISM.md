# ADR-0005: DOP-Based Parallelism

**Status:** Accepted
**Date:** 2025-12-28
**Applies to:** PPDS.Dataverse

## Context

We needed to determine the optimal parallelism for bulk operations against Dataverse. Microsoft's service protection limits are:

| Limit | Value | Window |
|-------|-------|--------|
| Requests | 6,000 | 5 minutes |
| Execution time | 20 minutes | 5 minutes |
| Concurrent requests | 52 | Per user |

The `x-ms-dop-hint` response header (exposed via `ServiceClient.RecommendedDegreesOfParallelism`) provides Microsoft's recommended concurrent request limit per Application User. This value varies by environment (e.g., 4 for trial, up to 50 for production).

### Approaches Considered

**1. Adaptive Rate Control (AIMD)**
We initially implemented an Additive Increase, Multiplicative Decrease algorithm that:
- Started at a floor and ramped up parallelism
- Tracked batch durations via exponential moving average
- Calculated execution time ceilings to prevent throttles
- Reduced parallelism on throttle, then recovered

**2. DOP-Only**
Simply use `RecommendedDegreesOfParallelism × connectionCount` as a fixed ceiling.

### Test Results

We ran extensive tests with 72,493 records (developer environment, DOP=5 per user):

| Approach | Users | DOP | Throughput | Time | Throttles |
|----------|-------|-----|------------|------|-----------|
| DOP-based | 1 | 5 | 202 rec/s | 05:58 | 0 |
| DOP-based | 2 | 10 | 395 rec/s | 03:03 | 0 |
| Exceeded DOP | 1 | 10+ | ~100 rec/s | 12:15 | **155** |

The 1-user test that exceeded DOP hit 155 throttle events with 30-second Retry-After durations, resulting in 4x slower performance than respecting DOP limits.

### Key Finding

Microsoft's documentation states: "Performance worsens if you send more parallel requests than the response header recommends."

Our testing confirmed this. The adaptive approach that exceeded DOP achieved higher short-term throughput but:
- Caused throttles on longer operations
- Created throttle cascades (80-100 simultaneous 429 responses)
- Required complex ceiling calculations that were environment-dependent

## Decision

**Use DOP as the parallelism ceiling, not a floor to ramp from.**

### Implementation

1. **Pool semaphore sizing:** `52 × connectionCount`
   - 52 is Microsoft's hard limit per Application User
   - This is the maximum the pool will ever allow

2. **Parallelism for operations:** `sum(DOP per connection)`
   - Read `RecommendedDegreesOfParallelism` from each connection's seed client
   - Cap each at 52 (the hard limit)
   - Sum across all connections

3. **Scaling strategy:** Add connections, not parallelism
   - 1 connection at DOP=4 → 4 parallel requests
   - 2 connections at DOP=4 → 8 parallel requests
   - Each Application User has independent quota

4. **No adaptive ramping**
   - DOP is already the optimal sustainable value
   - Ramping from a lower floor just delays reaching optimal throughput
   - Ramping above DOP causes throttles

### Code Structure

```csharp
// Pool reads live DOP from each connection source's seed client
public int GetLiveSourceDop(string sourceName)
{
    if (_seedClients.TryGetValue(sourceName, out var seed))
    {
        var liveDop = seed.RecommendedDegreesOfParallelism;
        return Math.Min(liveDop, MicrosoftHardLimitPerUser); // Cap at 52
    }
    return DefaultDop; // Fallback
}

// Get total parallelism across all sources
public int GetTotalRecommendedParallelism()
{
    return _seedClients.Keys.Sum(name => GetLiveSourceDop(name));
}

// Bulk operations read DOP each iteration for adaptive execution
await Parallel.ForEachAsync(batches,
    new ParallelOptions { MaxDegreeOfParallelism = pool.GetTotalRecommendedParallelism() },
    async (batch, ct) => { ... });
```

**Note:** DOP is read live from `ServiceClient.RecommendedDegreesOfParallelism` rather than cached at initialization. This allows the pool to adapt if the server changes its recommendation mid-operation.

### Throttle Handling

Since we never exceed DOP, throttles should be rare. When they occur (due to external factors like shared quota):

1. Record throttle per connection (for routing away)
2. Wait for Retry-After duration
3. Resume at same DOP (no reduction needed)

The pool's `MaxRetryAfterTolerance` option allows failing fast if the wait exceeds tolerance.

## Consequences

### Positive

- **Zero throttles** when respecting DOP (vs 155 throttles when exceeding)
- **Simpler code** - Removed ~500 lines of adaptive rate control logic
- **Predictable performance** - No ramping delays, immediate optimal throughput
- **Near-linear scaling** - 2 users = 1.95x throughput (theoretical max 2.0x)
- **Environment-adaptive** - Automatically adjusts to dev (DOP=5) vs production (DOP=50)
- **Clear scaling model** - "Add Application Users for more throughput"

### Negative

- **Lower peak throughput** on short operations where exceeding DOP wouldn't exhaust budget
- **Requires multiple Application Users** for high throughput (by design)

### Measured Results

| Metric | 1 User | 2 Users |
|--------|--------|---------|
| DOP | 5 | 10 |
| Throughput | 202 rec/s | 395 rec/s |
| Scaling | baseline | 1.95x |
| Throttles | 0 | 0 |

### Removed Components

- `IAdaptiveRateController` / `AdaptiveRateController`
- `AdaptiveRateOptions` / `AdaptiveRateStatistics`
- `RateControlPreset` (Conservative/Balanced/Aggressive)
- AIMD ramping logic
- Execution time ceiling calculations
- Batch duration EMA tracking

## References

- [Optimize performance for bulk operations](https://learn.microsoft.com/en-us/power-apps/developer/data-platform/optimize-performance-create-update)
- [Send parallel requests](https://learn.microsoft.com/en-us/power-apps/developer/data-platform/send-parallel-requests)
- [Service protection API limits](https://learn.microsoft.com/en-us/power-apps/developer/data-platform/api-limits)
- [ADR-0002: Multi-Connection Pooling](0002_MULTI_CONNECTION_POOLING.md) - Multiple users multiply quota
