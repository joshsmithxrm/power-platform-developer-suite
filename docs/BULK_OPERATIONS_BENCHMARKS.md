# Bulk Operations Benchmarks

Performance testing for bulk operations against Dataverse.

## Test Environment

- **Entities:** `ppds_state`, `ppds_city`, `ppds_zipcode` (hierarchical with alternate keys)
- **Record count:** 72,493 (51 states, 30,076 cities, 42,366 ZIP codes)
- **Environment:** Developer environment (DOP=5 per user)
- **Application Users:** 1 and 2 (to demonstrate quota scaling)
- **Strategy:** DOP-based parallelism (server-recommended limits)

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

## Results: DOP-Based Scaling

### Primary Key Operations (GUID-based upsert)

Using primary key (GUID) for record matching - optimal performance path:

| Users | DOP | Records | Duration | Throughput | Scaling |
|-------|-----|---------|----------|------------|---------|
| 1 | 5 | 72,493 | 05:58 | **202.1 rec/s** | baseline |
| 2 | 10 | 72,493 | 03:03 | **394.5 rec/s** | **1.95x** |

**Key result:** Near-linear scaling with additional Application Users. Zero throttles when respecting DOP limits.

### Alternate Key Operations

Using alternate key (string field) for record matching - additional lookup overhead:

| Users | DOP | Records | Duration | Throughput | vs GUID |
|-------|-----|---------|----------|------------|---------|
| 2 | 10 | 42,366 | 04:22 | **161.4 rec/s** | 2.4x slower |

### Scaling Strategy

The DOP-based approach uses server-recommended parallelism as the ceiling, not a floor:

| Users | Per-User DOP | Total DOP | Expected Throughput |
|-------|--------------|-----------|---------------------|
| 1 | 5 | 5 | ~200 rec/s |
| 2 | 5 | 10 | ~400 rec/s |
| 4 | 5 | 20 | ~800 rec/s (projected) |

**Scaling is achieved by adding Application Users, not by exceeding DOP.**

### Key Findings

1. **DOP-based parallelism prevents throttling**
   - 1-user tests that exceeded DOP saw 98-155 throttle events with 30-second waits
   - Tests respecting DOP saw 0-2 throttle events
   - Throttle recovery adds significant latency (~30s per event)

2. **Near-linear scaling with multiple users**
   - 2 users = 1.95x throughput (theoretical max = 2.0x)
   - Each Application User has independent API quota
   - No contention between users

3. **Alternate keys add ~2.4x overhead**
   - Non-clustered index lookup + key lookup vs direct clustered index seek
   - Expected SQL behavior, not a bug
   - Use primary keys when GUIDs are available

4. **Batch size 100 remains optimal**
   - Aligns with Microsoft's recommendation
   - More granular parallelism distribution
   - Reduces timeout risk with plugins

### Recommended Configuration

```csharp
// Pool sizes automatically based on DOP - no manual tuning needed
services.AddDataverseConnectionPool(options =>
{
    // Add Application Users for scaling
    options.Connections.Add(new("User1", connectionString1));
    options.Connections.Add(new("User2", connectionString2));  // 2x throughput

    options.Pool.SelectionStrategy = ConnectionSelectionStrategy.ThrottleAware;
});

// Operations automatically use sum(DOP) as parallelism ceiling
var result = await bulkExecutor.UpsertMultipleAsync("account", records);
```

## Alternate Key Performance Deep Dive

### Why Alternate Keys Are Slower

When using UpsertMultiple with alternate keys vs primary keys (GUIDs):

| Key Type | Lookup Path | I/O Operations |
|----------|-------------|----------------|
| Primary Key (GUID) | Clustered index seek | 1 |
| Alternate Key | Non-clustered index seek → Key lookup | 2+ |

The ~2.4x overhead is inherent to SQL index mechanics:
1. **Non-clustered index** stores the alternate key values in a separate B-tree
2. **Key lookup** fetches the actual row data from the clustered index
3. **Uniqueness check** must verify no duplicates exist

### Microsoft's Guidance

From [Use Upsert to Create or Update a record](https://learn.microsoft.com/en-us/power-apps/developer/data-platform/use-upsert-insert-update-record):

> "There's a performance penalty in using Upsert versus using Create. If you're sure the record doesn't exist, use Create."

### When to Use Each Approach

| Scenario | Recommended Approach |
|----------|---------------------|
| Migrating between environments | Primary key (GUID) - fastest |
| Syncing from external system | Alternate key - required, accept overhead |
| Initial data load (no existing records) | CreateMultiple - skip upsert logic |
| Incremental sync | Alternate key upsert - correctness over speed |

## Analysis: Our Results vs Microsoft Benchmarks

Microsoft's reference shows ~10M records/hour for CreateMultiple/UpdateMultiple. Our DOP-based approach in a developer environment achieved:

| Configuration | Records/Hour | vs Microsoft |
|---------------|--------------|--------------|
| 1 user, DOP=5 | ~727K | 7.3% |
| 2 users, DOP=10 | ~1.42M | 14.2% |
| Projected: 10 users, DOP=50 | ~7.1M | 71% |

The gap is expected due to:

1. **Developer environment** - Lower resource allocation than production
2. **Conservative DOP** - Production environments report DOP=50 vs dev DOP=5
3. **Respecting limits** - We don't exceed DOP to avoid throttle recovery latency

### Scaling Projection

| Users | DOP | Throughput (projected) |
|-------|-----|------------------------|
| 1 | 5 | 200 rec/s (727K/hr) |
| 2 | 10 | 400 rec/s (1.44M/hr) |
| 5 | 25 | 1,000 rec/s (3.6M/hr) |
| 10 | 50 | 2,000 rec/s (7.2M/hr) |
| 20 | 100 | 4,000 rec/s (14.4M/hr) |

**Note:** Production environments with DOP=50 per user would reach Microsoft's benchmarks with fewer Application Users.

## References

- [Optimize performance for bulk operations](https://learn.microsoft.com/en-us/power-apps/developer/data-platform/optimize-performance-create-update)
- [Use bulk operation messages](https://learn.microsoft.com/en-us/power-apps/developer/data-platform/bulk-operations)
- [Send parallel requests](https://learn.microsoft.com/en-us/power-apps/developer/data-platform/send-parallel-requests)
- [Service protection API limits](https://learn.microsoft.com/en-us/power-apps/developer/data-platform/api-limits)
