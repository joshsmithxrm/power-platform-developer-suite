# Bulk Operations Benchmarks

Performance testing for bulk operations against Dataverse.

## Test Environment

- **Entity:** `ppds_zipcode` (simple entity with alternate key)
- **Record count:** 42,366
- **Environment:** Developer environment (single tenant)
- **Parallel workers:** Server-recommended (`RecommendedDegreesOfParallelism`)

## Microsoft's Reference Benchmarks

From [Microsoft Learn - Optimize performance for bulk operations](https://learn.microsoft.com/en-us/power-apps/developer/data-platform/optimize-performance-create-update):

| Approach | Throughput | Notes |
|----------|------------|-------|
| Single requests | ~50K records/hour | Baseline |
| ExecuteMultiple | ~2M records/hour | 40x improvement |
| CreateMultiple/UpdateMultiple | ~10M records/hour | 5x over ExecuteMultiple |
| Elastic tables (Cosmos DB) | ~120M writes/hour | Azure Cosmos DB backend |

> "Bulk operation APIs like CreateMultiple, UpdateMultiple, and UpsertMultiple can provide throughput improvement of up to 5x, growing from 2 million records created per hour using ExecuteMultiple to the creation of 10 million records in less than an hour."

## Microsoft's Batch Size Recommendation

From [Microsoft Learn - Use bulk operation messages](https://learn.microsoft.com/en-us/power-apps/developer/data-platform/bulk-operations):

> "Generally, we expect that **100 - 1,000 records per request** is a reasonable place to start if the size of the record data is small and there are no plug-ins."

For elastic tables specifically:
> "The recommended number of record operations to send with CreateMultiple and UpdateMultiple for elastic tables is **100**."

## Results: Creates (UpsertMultiple)

| Approach | Batch Size | Parallelism | Time (s) | Throughput (rec/s) | Notes |
|----------|------------|-------------|----------|-------------------|-------|
| Single ServiceClient | 100 | 4 | 933 | 45.4 | Baseline |
| Connection Pool | 100 | 4 | 888 | 47.7 | 5% faster than baseline |
| Connection Pool | 1000 | 4 | 919 | 46.1 | 3% slower than batch 100 |
| Connection Pool | 100 | 5 (server) | 704 | **60.2** | +26% using server-recommended parallelism |

### Key Findings

1. **Server-recommended parallelism is optimal** (+26% vs hardcoded)
   - `RecommendedDegreesOfParallelism` returns server-tuned value
   - Automatically adapts to environment capacity
   - No guesswork required

2. **Connection Pool is faster than Single ServiceClient** (+5%)
   - True parallelism with independent connections
   - No internal locking/serialization overhead
   - Affinity cookie disabled improves server-side distribution

3. **Batch size 100 is optimal** (+3% vs batch 1000)
   - Aligns with Microsoft's recommendation
   - More granular parallelism
   - Less memory pressure per request

4. **Optimal configuration:** Connection Pool + Batch Size 100 + Server Parallelism = **60.2 records/sec** (~217K/hour)

## Results: Updates (UpsertMultiple)

| Approach | Batch Size | Time (s) | Throughput (rec/s) | Notes |
|----------|------------|----------|-------------------|-------|
| Connection Pool | 100 | 1153 | 36.7 | Alternate key lookup overhead |

### Observations

- Updates are ~23% slower than creates (36.7/s vs 47.7/s)
- Expected due to server-side alternate key lookup before modification
- Connection approach doesn't affect this - it's server-side overhead

## Configuration

```json
{
  "Dataverse": {
    "Pool": {
      "Enabled": true,
      "MaxPoolSize": 50,
      "MinPoolSize": 5,
      "DisableAffinityCookie": true
    }
  }
}
```

```csharp
var options = new BulkOperationOptions
{
    BatchSize = 100
    // MaxParallelBatches omitted - uses RecommendedDegreesOfParallelism from server
};
```

## Analysis: Our Results vs Microsoft Benchmarks

Our measured throughput of **60.2 records/sec** (~217K records/hour) is lower than Microsoft's reference of ~10M records/hour. This is expected due to:

1. **Developer environment** - Single-tenant dev environments have lower resource allocation than production
2. **Entity complexity** - Alternate key lookups add overhead
3. **Service protection limits** - Dev environments have stricter throttling

### Progression Summary

| Change | Improvement |
|--------|-------------|
| Single client → Connection pool | +5% |
| Batch 1000 → Batch 100 | +3% |
| Hardcoded parallelism → Server-recommended | +26% |
| **Total improvement** | **+33%** (45.4 → 60.2 rec/s) |

**Key finding:** Using `RecommendedDegreesOfParallelism` from the server provided the largest single improvement (+26%), validating Microsoft's guidance to query this value rather than hardcoding parallelism.

## References

- [Optimize performance for bulk operations](https://learn.microsoft.com/en-us/power-apps/developer/data-platform/optimize-performance-create-update)
- [Use bulk operation messages](https://learn.microsoft.com/en-us/power-apps/developer/data-platform/bulk-operations)
- [Send parallel requests](https://learn.microsoft.com/en-us/power-apps/developer/data-platform/send-parallel-requests)
- [Service protection API limits](https://learn.microsoft.com/en-us/power-apps/developer/data-platform/api-limits)
