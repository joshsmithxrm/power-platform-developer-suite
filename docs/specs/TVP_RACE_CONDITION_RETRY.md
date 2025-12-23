# TVP Race Condition Retry Specification

**Status:** Draft
**Created:** 2025-12-22
**Author:** Claude Code

---

## Problem Statement

When executing parallel bulk operations (`CreateMultiple`, `UpdateMultiple`, `UpsertMultiple`) against a **newly created Dataverse table**, a transient SQL error occurs due to an internal race condition in Dataverse's lazy initialization of bulk operation infrastructure.

### Error Details

```
ErrorCode: 0x80044150
CRM ErrorCode: -2147204784
SQL ErrorCode: -2146232060
SQL Number: 3732

Message: Cannot drop type 'ppds_ZipCodeBase_tvp' because it is being
referenced by object 'p_ppds_ZipCodeBase_UpdateMultiple'. There may be
other objects that reference this type.
```

### Root Cause

1. Dataverse **lazily creates** internal SQL objects for bulk operations:
   - Table-valued parameter types (TVPs): `{entity}_tvp`
   - Stored procedures: `p_{entity}_CreateMultiple`, `p_{entity}_UpdateMultiple`

2. When multiple parallel requests hit a table **before** these objects exist:
   - Thread A creates the TVP
   - Thread B creates the stored procedure referencing the TVP
   - Thread A detects a schema mismatch, attempts to drop and recreate the TVP
   - SQL Server rejects the drop because Thread B's stored procedure references it

3. This is a **transient error** - subsequent requests succeed because the objects are now created.

### Impact

- First batch of parallel bulk operations fails (100 records marked as failed)
- Remaining batches succeed normally
- Error is **self-healing** but causes unnecessary failures and reduced throughput
- Particularly affects:
  - Fresh table deployments
  - CI/CD pipelines that recreate tables
  - Development environments with frequent schema changes

---

## Proposed Solution

Add retry logic specifically for SQL error 3732 (TVP dependency conflict) to `BulkOperationExecutor`, following the existing pattern used for pool exhaustion retries.

### Approach: Targeted Retry with Backoff

```
┌─────────────────────────────────────────────────────────────────┐
│                    Batch Execution Flow                         │
└─────────────────────────────────────────────────────────────────┘

  ExecuteBatch()
       │
       ▼
  ┌─────────┐
  │ Execute │──success──▶ Return Result
  │ Request │
  └────┬────┘
       │
     error
       │
       ▼
  ┌─────────────────┐
  │ Is SQL 3732?    │──no──▶ Propagate Error (existing behavior)
  │ (TVP conflict)  │
  └────────┬────────┘
           │
          yes
           │
           ▼
  ┌─────────────────┐
  │ Retry < Max?    │──no──▶ Propagate Error
  └────────┬────────┘
           │
          yes
           │
           ▼
  ┌─────────────────┐
  │ Wait (backoff)  │
  │ 500ms, 1s, 2s   │
  └────────┬────────┘
           │
           ▼
       Retry Execute
```

### Detection Logic

```csharp
private static bool IsTvpRaceConditionError(Exception ex)
{
    // Check for FaultException with specific error codes
    if (ex is FaultException<OrganizationServiceFault> fault)
    {
        // CRM ErrorCode: 0x80044150 (-2147204784) = Generic SQL error wrapper
        // SQL Number: 3732 = Cannot drop type because referenced
        var message = fault.Detail?.Message ?? ex.Message;

        return fault.Detail?.ErrorCode == unchecked((int)0x80044150)
            && (message.Contains("3732") || message.Contains("Cannot drop type"));
    }
    return false;
}
```

### Retry Parameters

| Parameter | Value | Rationale |
|-----------|-------|-----------|
| Max retries | 3 | TVP creation is fast; 3 attempts sufficient |
| Initial delay | 500ms | Allow other threads to complete TVP creation |
| Backoff multiplier | 2x | 500ms → 1s → 2s |
| Max delay | 2s | Don't wait too long for infrastructure issue |

### Implementation Location

Modify `BulkOperationExecutor.cs`:

1. Add new constant:
   ```csharp
   private const int MaxTvpRetries = 3;
   ```

2. Add detection method:
   ```csharp
   private static bool IsTvpRaceConditionError(Exception ex)
   ```

3. Modify batch execution methods to wrap with retry:
   - `ExecuteCreateMultipleBatchAsync`
   - `ExecuteUpdateMultipleBatchAsync`
   - `ExecuteUpsertMultipleBatchAsync`

### Example Implementation

```csharp
private async Task<BulkOperationResult> ExecuteWithTvpRetryAsync(
    Func<Task<BulkOperationResult>> operation,
    string entityLogicalName,
    int batchSize,
    CancellationToken cancellationToken)
{
    for (int attempt = 1; attempt <= MaxTvpRetries; attempt++)
    {
        try
        {
            return await operation();
        }
        catch (Exception ex) when (IsTvpRaceConditionError(ex) && attempt < MaxTvpRetries)
        {
            var delay = TimeSpan.FromMilliseconds(500 * Math.Pow(2, attempt - 1));
            _logger.LogWarning(
                "TVP race condition detected for {Entity}, retrying in {Delay}ms (attempt {Attempt}/{Max})",
                entityLogicalName, delay.TotalMilliseconds, attempt, MaxTvpRetries);

            await Task.Delay(delay, cancellationToken);
        }
    }

    // Unreachable: final attempt either succeeds or throws
    throw new InvalidOperationException("Unexpected code path");
}
```

---

## Alternatives Considered

### Alternative A: Single-Batch Warmup

Execute a single small batch sequentially before parallel execution to trigger TVP creation.

```csharp
// Before parallel execution
if (batches.Count > 1)
{
    var warmupBatch = batches[0];
    await ExecuteBatchAsync(warmupBatch, cancellationToken);
    batches = batches.Skip(1).ToList();
}
// Then execute remaining in parallel
```

**Pros:**
- Prevents the error entirely
- Simpler logic (no retry handling)

**Cons:**
- Adds latency to every bulk operation (not just new tables)
- Penalizes the common case to handle the rare case
- Doesn't help if table was just recreated mid-operation

**Rejected:** Penalizes all operations for a rare edge case.

### Alternative B: Pre-Check Table Metadata

Query table metadata to detect if bulk operations have been used before.

**Pros:**
- Could skip warmup for established tables

**Cons:**
- No reliable API to detect TVP existence
- Adds complexity and an extra round-trip
- Metadata could be stale

**Rejected:** No reliable detection mechanism exists.

### Alternative C: Document and Accept

Document the behavior and let callers handle retries.

**Pros:**
- No code changes
- Users have full control

**Cons:**
- Poor developer experience
- Inconsistent with existing retry patterns (pool exhaustion)
- Every consumer must implement retry logic

**Rejected:** Violates principle of hiding infrastructure complexity.

---

## Testing Strategy

### Unit Tests

1. **Detection test:** Verify `IsTvpRaceConditionError` correctly identifies the error
2. **Retry test:** Mock the error, verify retry with backoff
3. **Max retry test:** Verify error propagates after max attempts
4. **Success after retry:** Verify operation completes when retry succeeds

### Integration Tests

1. **New table test:** Create table, immediately run parallel bulk operation
2. **Existing table test:** Verify no unnecessary retries on established tables
3. **Concurrent test:** Multiple parallel operations on new table

### Manual Validation

Use the existing demo application:
```powershell
# Recreate schema and immediately load data
dotnet run -- create-geo-schema --delete-first
dotnet run -- load-geo-data --verbose
```

Expected: No failures logged for TVP race condition.

---

## Logging

### New Log Messages

| Level | Event | Message |
|-------|-------|---------|
| Warning | TVP retry | `TVP race condition detected for {Entity}, retrying in {Delay}ms (attempt {Attempt}/{Max})` |
| Debug | TVP success | `TVP retry succeeded for {Entity} on attempt {Attempt}` |
| Error | TVP exhausted | `TVP race condition persisted after {Max} retries for {Entity}` |

---

## Rollout Plan

1. Implement in `feature/tvp-retry` branch
2. Add unit tests
3. Validate with demo application
4. Update CHANGELOG.md
5. PR to main
6. Include in next minor release

---

## Success Criteria

- [ ] Zero TVP race condition failures in demo application
- [ ] No performance regression for established tables
- [ ] All existing tests pass
- [ ] New unit tests cover retry logic
- [ ] Documentation updated

---

## References

- [Dataverse Bulk Operations](https://learn.microsoft.com/en-us/power-apps/developer/data-platform/bulk-operations)
- [SQL Server Error 3732](https://learn.microsoft.com/en-us/sql/relational-databases/errors-events/database-engine-events-and-errors) - "Cannot drop type because it is being referenced"
- Existing pattern: `BulkOperationExecutor.GetClientWithRetryAsync()` (lines 371-394)
