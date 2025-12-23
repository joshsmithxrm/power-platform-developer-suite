# Bulk Operations Benchmarks

Performance testing for bulk operations against Dataverse.

## Test Environment

- **Entity:** `ppds_zipcode` (simple entity with alternate key)
- **Record count:** 42,366
- **Environment:** Developer environment (single tenant)
- **Parallel workers:** 4

## Microsoft's Recommendation

From [Microsoft Docs](https://learn.microsoft.com/en-us/power-apps/developer/data-platform/bulk-operations):

> "Generally, we expect that **100 - 1,000 records per request** is a reasonable place to start if the size of the record data is small and there are no plug-ins."

## Results: Creates (UpsertMultiple)

| Approach | Batch Size | Time (s) | Throughput (rec/s) | Notes |
|----------|------------|----------|-------------------|-------|
| Single ServiceClient | 100 | 933 | 45.4 | Baseline |
| Connection Pool | 100 | 888 | **47.7** | 5% faster than baseline |
| Connection Pool | 1000 | 919 | 46.1 | 3% slower than batch 100 |

### Key Findings

1. **Connection Pool is faster than Single ServiceClient** (+5%)
   - True parallelism with independent connections
   - No internal locking/serialization overhead
   - Affinity cookie disabled improves server-side distribution

2. **Batch size 100 is optimal** (+3% vs batch 1000)
   - Aligns with Microsoft's recommendation
   - More granular parallelism
   - Less memory pressure per request

3. **Optimal configuration:** Connection Pool + Batch Size 100 = **47.7 records/sec**

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
      "MinPoolSize": 0,
      "DisableAffinityCookie": true
    }
  }
}
```

```csharp
var options = new BulkOperationOptions
{
    BatchSize = 100,
    MaxParallelBatches = 4
};
```
