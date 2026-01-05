# Dataverse Connection Pool Patterns

**Read ADRs 0002 and 0005 before implementing any multi-record Dataverse operation.**

**Reference implementation:** `src/PPDS.Dataverse/BulkOperations/BulkOperationExecutor.cs`

---

## Pool Usage

### Wrong - Single client for multiple queries

```csharp
// WRONG: Holds one client for entire operation, defeats pool parallelism
await using var client = await pool.GetClientAsync(...);
foreach (var item in items)  // Sequential, same client
    await DoQuery(client, item);
```

### Correct - Client per parallel operation

```csharp
// CORRECT: Each parallel task gets its own client from the pool
var parallelism = pool.GetTotalRecommendedParallelism();
await Parallel.ForEachAsync(items,
    new ParallelOptions { MaxDegreeOfParallelism = parallelism },
    async (item, ct) =>
    {
        await using var client = await pool.GetClientAsync(cancellationToken: ct);
        await DoQuery(client, item);
    });
```

### Why This Matters

- Pool manages DOP limits, throttle tracking, and connection rotation
- Each Application User has independent API quota (6,000 requests/5 min)
- Getting client inside parallel loop enables true parallelism
- Pool waits for non-throttled connection automatically

---

## Service Protection Limits (Per User, Per 5-Minute Window)

| Limit | Value |
|-------|-------|
| Requests | 6,000 |
| Execution time | 20 minutes |
| Concurrent requests | 52 (check `x-ms-dop-hint` header) |

---

## Throughput Benchmarks (Microsoft Reference)

| Approach | Throughput |
|----------|------------|
| Single requests | ~50K records/hour |
| ExecuteMultiple | ~2M records/hour |
| CreateMultiple/UpdateMultiple | ~10M records/hour |
| Elastic tables | ~120M writes/hour |

---

## DOP-Based Parallelism

The pool uses `RecommendedDegreesOfParallelism` (from `x-ms-dop-hint` header):

- **DOP varies by environment**: Trial ~4, production up to 50
- **Hard cap of 52 per user**: Microsoft's enforced limit
- **Scale by adding connections**: 2 users at DOP=4 = 8 parallel requests

**Scaling Strategy:**
```
1 Application User  @ DOP=4  →  4 parallel requests
2 Application Users @ DOP=4  →  8 parallel requests
4 Application Users @ DOP=4  → 16 parallel requests
```

---

## Required ThreadPool Settings

The connection pool automatically applies these. If bypassing the pool, you MUST apply manually:

```csharp
ThreadPool.SetMinThreads(100, 100);           // Default is 4
ServicePointManager.DefaultConnectionLimit = 65000;  // Default is 2
ServicePointManager.Expect100Continue = false;
ServicePointManager.UseNagleAlgorithm = false;
```

---

## Key ADRs

| ADR | Key Pattern | Anti-Pattern |
|-----|-------------|--------------|
| **0002** | Get client INSIDE parallel loops | Hold single client for entire operation |
| **0005** | Use `pool.GetTotalRecommendedParallelism()` as DOP ceiling | Hardcode parallelism values |

---

## Microsoft Learn References

- [Optimize performance for bulk operations](https://learn.microsoft.com/en-us/power-apps/developer/data-platform/optimize-performance-create-update)
- [Send parallel requests](https://learn.microsoft.com/en-us/power-apps/developer/data-platform/send-parallel-requests)
- [Service protection API limits](https://learn.microsoft.com/en-us/power-apps/developer/data-platform/api-limits)
- [Use bulk operation messages](https://learn.microsoft.com/en-us/power-apps/developer/data-platform/bulk-operations)
