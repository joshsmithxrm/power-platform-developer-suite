# Bulk Operations Benchmarks

Performance testing for bulk operations against Dataverse.

## Test Environment

- **Entity:** `ppds_zipcode` (simple entity with alternate key)
- **Record count:** 42,366
- **Environment:** Developer environment (single tenant)
- **App registrations:** Single (one set of API limits)
- **Parallelism tested:** Server-recommended (5) and elevated (50)

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

### Standard Mode (Server-Recommended Parallelism)

| Approach | Batch Size | Parallelism | Time (s) | Throughput (rec/s) | Notes |
|----------|------------|-------------|----------|-------------------|-------|
| Single ServiceClient | 100 | 4 | 933 | 45.4 | Baseline |
| Connection Pool | 100 | 4 | 888 | 47.7 | 5% faster than baseline |
| Connection Pool | 1000 | 4 | 919 | 46.1 | 3% slower than batch 100 |
| Connection Pool | 100 | 5 (server) | 704 | **60.2** | +26% using server-recommended parallelism |

### High-Throughput Mode (Elevated Parallelism)

For bulk data loading scenarios where throughput is critical, parallelism can be increased beyond the server-recommended value:

| Approach | Batch Size | Parallelism | Time (s) | Throughput (rec/s) | Notes |
|----------|------------|-------------|----------|-------------------|-------|
| Connection Pool | 100 | 50 | 83 | **508.6** | 8.4x faster than server-recommended |

**Key result:** 42,366 records loaded in 83 seconds with zero failures.

### When to Use Each Mode

| Mode | Parallelism | Use Case |
|------|-------------|----------|
| **Standard** | Server-recommended (typically 5) | Interactive operations, mixed workloads, shared environments |
| **High-Throughput** | 50+ | Bulk data migrations, initial data loads, batch processing jobs |

**Considerations for high-throughput mode:**

- Requires sufficient pool connections (`MaxPoolSize` ≥ parallelism)
- Consumes more API quota - avoid during business hours on shared environments
- Single app registration was used; multiple app registrations could potentially increase throughput further (untested)
- Monitor for throttling in production; the SDK handles 429 responses automatically

### Key Findings

1. **Server-recommended parallelism is a safe default** (+26% vs hardcoded)
   - `RecommendedDegreesOfParallelism` returns server-tuned value
   - Automatically adapts to environment capacity
   - No guesswork required

2. **Elevated parallelism unlocks massive gains for bulk operations** (+744% over server-recommended)
   - 508.6 rec/s vs 60.2 rec/s
   - ~1.83M records/hour vs ~217K records/hour
   - Appropriate for dedicated data loading scenarios

3. **Connection Pool is faster than Single ServiceClient** (+5%)
   - True parallelism with independent connections
   - No internal locking/serialization overhead
   - Affinity cookie disabled improves server-side distribution

4. **Batch size 100 is optimal** (+3% vs batch 1000)
   - Aligns with Microsoft's recommendation
   - More granular parallelism
   - Less memory pressure per request

### Recommended Configurations

**Standard (default):** Connection Pool + Batch Size 100 + Server Parallelism = **60.2 records/sec** (~217K/hour)

**High-Throughput:** Connection Pool + Batch Size 100 + Parallelism 50 = **508.6 records/sec** (~1.83M/hour)

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

Microsoft's reference benchmark shows ~10M records/hour for CreateMultiple/UpdateMultiple. Our high-throughput mode achieved **~1.83M records/hour** in a developer environment.

The gap is expected due to:

1. **Developer environment** - Single-tenant dev environments have lower resource allocation than production
2. **Single app registration** - One client credential = one set of API limits
3. **Entity complexity** - Alternate key lookups add overhead
4. **Service protection limits** - Dev environments have stricter throttling

In production environments with multiple app registrations (each with independent API quotas), throughput could approach Microsoft's benchmarks.

### Progression Summary

| Change | Improvement | Throughput |
|--------|-------------|------------|
| Single client (baseline) | — | 45.4 rec/s |
| → Connection pool | +5% | 47.7 rec/s |
| → Batch 100 (vs 1000) | +3% | — |
| → Server-recommended parallelism | +26% | 60.2 rec/s |
| → Elevated parallelism (50) | +744% | **508.6 rec/s** |
| **Total improvement** | **+1,020%** | 45.4 → 508.6 rec/s |

### Key Insights

1. **Server-recommended parallelism is a good starting point** - Provides +26% improvement with automatic tuning
2. **Elevated parallelism is the largest lever** - +744% improvement for bulk operations
3. **Multi-app-registration pooling** - Untested but theoretically could multiply throughput further by distributing load across independent API quotas

## References

- [Optimize performance for bulk operations](https://learn.microsoft.com/en-us/power-apps/developer/data-platform/optimize-performance-create-update)
- [Use bulk operation messages](https://learn.microsoft.com/en-us/power-apps/developer/data-platform/bulk-operations)
- [Send parallel requests](https://learn.microsoft.com/en-us/power-apps/developer/data-platform/send-parallel-requests)
- [Service protection API limits](https://learn.microsoft.com/en-us/power-apps/developer/data-platform/api-limits)
