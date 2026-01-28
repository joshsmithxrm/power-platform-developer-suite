# Bulk Operations

**Status:** Implemented
**Version:** 1.0
**Last Updated:** 2026-01-23
**Code:** [src/PPDS.Dataverse/BulkOperations/](../src/PPDS.Dataverse/BulkOperations/)

---

## Overview

The bulk operations system provides high-throughput data manipulation using Dataverse's native bulk APIs (`CreateMultiple`, `UpdateMultiple`, `UpsertMultiple`, `DeleteMultiple`). It delivers up to 5x better performance than `ExecuteMultiple` by leveraging modern bulk endpoints with automatic batching, parallel execution, throttle handling, and diagnostic analysis for batch failures.

### Goals

- **Performance**: 5x throughput over ExecuteMultiple via native bulk APIs
- **Resilience**: Automatic retry for throttling, auth failures, deadlocks, and infrastructure races
- **Observability**: Real-time progress with rate calculations and ETA

### Non-Goals

- Implementing `ExecuteMultiple` fallback (deprecated approach)
- Cross-entity transaction support (Dataverse is per-entity)
- Relationship cascade operations (handled by Dataverse platform)

---

## Architecture

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                           Application Layer                                  │
│           (CLI Commands, TUI, Migration Engine, MCP Tools)                  │
└─────────────────────────────────┬───────────────────────────────────────────┘
                                  │
                                  ▼
┌─────────────────────────────────────────────────────────────────────────────┐
│                        IBulkOperationExecutor                                │
│  ┌──────────────┐ ┌──────────────┐ ┌──────────────┐ ┌──────────────────┐   │
│  │CreateMultiple│ │UpdateMultiple│ │UpsertMultiple│ │  DeleteMultiple  │   │
│  │    Async     │ │    Async     │ │    Async     │ │      Async       │   │
│  └──────┬───────┘ └──────┬───────┘ └──────┬───────┘ └────────┬─────────┘   │
│         └────────────────┼────────────────┼──────────────────┘             │
│                          ▼                                                  │
│  ┌──────────────────────────────────────────────────────────────────────┐  │
│  │                         Batching Engine                               │  │
│  │  • Split records into BatchSize chunks (default 100)                 │  │
│  │  • Coordinate parallel execution via BatchParallelismCoordinator      │  │
│  │  • Aggregate results across batches                                   │  │
│  └──────────────────────────────────────────────────────────────────────┘  │
│                          │                                                  │
│  ┌──────────────────────────────────────────────────────────────────────┐  │
│  │                    Per-Batch Execution Loop                           │  │
│  │  1. Acquire slot from BatchCoordinator (respects pool DOP)           │  │
│  │  2. Get pooled client with pre-flight throttle check                 │  │
│  │  3. Execute bulk request (CreateMultiple/UpdateMultiple/etc.)        │  │
│  │  4. Handle errors: throttle, auth, deadlock, infrastructure race     │  │
│  │  5. Record progress and return to pool                               │  │
│  └──────────────────────────────────────────────────────────────────────┘  │
└─────────────────────────────────┬───────────────────────────────────────────┘
                                  │
                                  ▼
┌─────────────────────────────────────────────────────────────────────────────┐
│                      IDataverseConnectionPool                                │
│  • Provides pooled clients for parallel batch execution                     │
│  • Tracks throttle state per connection                                     │
│  • Coordinates total parallelism via BatchParallelismCoordinator            │
└─────────────────────────────────────────────────────────────────────────────┘
```

### Components

| Component | Responsibility |
|-----------|----------------|
| `BulkOperationExecutor` | Orchestrates batching, parallelism, retry logic |
| `BulkOperationOptions` | Configuration (batch size, bypass flags, parallelism) |
| `BulkOperationResult` | Aggregated results with success/failure counts |
| `BulkOperationError` | Per-record error details with diagnostics |
| `BatchParallelismCoordinator` | Semaphore for fair parallelism sharing |
| `BatchFailureDiagnostic` | Root cause analysis for batch failures |
| `ProgressTracker` | Thread-safe progress with rolling rate calculation |

### Dependencies

- Depends on: [connection-pooling.md](./connection-pooling.md) for client management
- Uses patterns from: [architecture.md](./architecture.md) for progress reporting

---

## Specification

### Core Requirements

1. **Automatic batching**: Records split into configurable chunks (default 100)
2. **Pool-managed parallelism**: Batches acquire slots from `BatchParallelismCoordinator`, never exceed pool DOP
3. **Indefinite retry for throttling**: Service protection errors always retry (data loss prevention)
4. **Finite retry for transient errors**: Auth, connection, deadlock, infrastructure races have limits
5. **Progress with ETA**: Report rate, percentage, estimated completion time

### Primary Flows

**CreateMultiple:**

1. **Validate**: Check entities collection not empty, entity name valid
2. **Batch**: Split into `BatchSize` chunks
3. **Execute parallel**: Each batch acquires coordinator slot, gets pooled client
4. **Per-batch**: Build `CreateMultipleRequest`, execute with throttle retry
5. **Collect IDs**: Extract created GUIDs from response
6. **Report**: Update progress after each batch
7. **Aggregate**: Combine all batch results into single `BulkOperationResult`

**UpsertMultiple:**

1. **Same as Create** but uses `UpsertMultipleRequest`
2. **Track counts**: Separate `CreatedCount` and `UpdatedCount` from response

**UpdateMultiple:**

1. **Validate**: All entities must have ID set
2. **Same execution** as Create but uses `UpdateMultipleRequest`

**DeleteMultiple:**

1. **Standard tables**: Use `ExecuteMultiple` with individual `DeleteRequest` (no native bulk delete)
2. **Elastic tables**: Use native `DeleteMultiple` API
3. **ContinueOnError**: For standard tables, controls `ExecuteMultipleSettings.ContinueOnError`

### Constraints

- Batch size 100-1000 recommended; 100 optimal for elastic tables
- Standard tables: Create/Update/Upsert are all-or-nothing per batch
- Elastic tables: Support partial success with per-record errors
- Delete on standard tables uses ExecuteMultiple (no bulk delete API)
- `prvBypassCustomBusinessLogic` privilege required for plugin bypass

### Validation Rules

| Field | Rule | Error |
|-------|------|-------|
| `entities` | Not null or empty | `ArgumentException` |
| `entityLogicalName` | Not null or whitespace | `ArgumentException` |
| `BatchSize` | Must be > 0 | Default 100 applied |
| Entity ID (Update) | Must be non-empty GUID | `ArgumentException` |

---

## Core Types

### IBulkOperationExecutor

Entry point for all bulk operations ([`IBulkOperationExecutor.cs`](../src/PPDS.Dataverse/BulkOperations/IBulkOperationExecutor.cs)).

```csharp
public interface IBulkOperationExecutor
{
    Task<BulkOperationResult> CreateMultipleAsync(string entityLogicalName,
        IEnumerable<Entity> entities, BulkOperationOptions? options = null,
        IProgress<ProgressSnapshot>? progress = null, CancellationToken ct = default);
    Task<BulkOperationResult> UpdateMultipleAsync(...);
    Task<BulkOperationResult> UpsertMultipleAsync(...);
    Task<BulkOperationResult> DeleteMultipleAsync(string entityLogicalName,
        IEnumerable<Guid> ids, ...);
}
```

### BulkOperationOptions

Configuration for bulk operation behavior ([`BulkOperationOptions.cs`](../src/PPDS.Dataverse/BulkOperations/BulkOperationOptions.cs)).

```csharp
public class BulkOperationOptions
{
    public int BatchSize { get; set; } = 100;
    public bool ElasticTable { get; set; } = false;
    public CustomLogicBypass BypassCustomLogic { get; set; } = CustomLogicBypass.None;
    public bool BypassPowerAutomateFlows { get; set; } = false;
    public int? MaxParallelBatches { get; set; } = null;  // Uses pool DOP if null
    public bool ContinueOnError { get; set; } = true;
    public bool SuppressDuplicateDetection { get; set; } = false;
    public string? Tag { get; set; }  // Passed to plugin execution context via SharedVariables
}
```

### BulkOperationResult

Aggregated outcome of a bulk operation ([`BulkOperationResult.cs`](../src/PPDS.Dataverse/BulkOperations/BulkOperationResult.cs)).

```csharp
public record BulkOperationResult
{
    public int SuccessCount { get; init; }
    public int FailureCount { get; init; }
    public IReadOnlyList<BulkOperationError> Errors { get; init; }
    public TimeSpan Duration { get; init; }
    public bool IsSuccess => FailureCount == 0;
    public IReadOnlyList<Guid>? CreatedIds { get; init; }  // Create only
    public int? CreatedCount { get; init; }  // Upsert only
    public int? UpdatedCount { get; init; }  // Upsert only
}
```

### ProgressSnapshot

Immutable progress state with rate and ETA calculations.

```csharp
public record ProgressSnapshot
{
    public int Succeeded { get; init; }
    public int Failed { get; init; }
    public int Total { get; init; }
    public double PercentComplete { get; init; }
    public double OverallRatePerSecond { get; init; }
    public double InstantRatePerSecond { get; init; }  // 30s rolling window
    public TimeSpan? EstimatedRemaining { get; init; }
}
```

### Usage Pattern

```csharp
// Basic usage
var result = await bulkExecutor.CreateMultipleAsync("account", entities);

// With options and progress
var options = new BulkOperationOptions
{
    BatchSize = 100,
    BypassCustomLogic = CustomLogicBypass.Synchronous
};
var progress = new Progress<ProgressSnapshot>(p =>
    Console.WriteLine($"{p.PercentComplete:F1}% - {p.OverallRatePerSecond:F0} rec/s"));

var result = await bulkExecutor.UpsertMultipleAsync("contact", entities, options, progress);

if (!result.IsSuccess)
{
    foreach (var error in result.Errors)
        Console.WriteLine($"Record {error.Index}: {error.Message}");
}
```

---

## Error Handling

### Error Types

| Error | Condition | Recovery |
|-------|-----------|----------|
| Service Protection | Throttle limit hit | Indefinite retry with backoff |
| Pool Exhaustion | No connections available | Indefinite retry with exponential backoff (cap 32s) |
| Auth Failure | Token expired or invalid | Finite retry (3), invalidate seed |
| Connection Failure | Network/socket error | Finite retry (3), mark connection invalid |
| Infrastructure Race | TVP/stored proc creation race (SQL 3732/2766/2812) | Finite retry (3), exponential backoff |
| SQL Deadlock | Concurrent batch conflict (SQL 1205) | Finite retry (3), exponential backoff |

### Retry Strategy

The executor uses nested retry loops ([`BulkOperationExecutor.cs:700-900`](../src/PPDS.Dataverse/BulkOperations/BulkOperationExecutor.cs#L700-L900)):

```
Outer Loop (infinite for throttle/pool exhaustion):
├── Pre-flight guard: Check if connection is throttled
├── Execute batch
└── On error:
    ├── Service protection → Record throttle, retry outer loop
    ├── Pool exhaustion → Exponential backoff (1s→32s), retry outer loop
    └── Other → Inner retry or propagate

Inner Retry (finite):
├── Auth failure → Up to 3 retries, invalidate seed
├── Connection failure → Up to 3 retries, mark invalid
├── Infrastructure race → Up to 3 retries, backoff (500ms→2s)
└── Deadlock → Up to 3 retries, backoff (500ms→2s)
```

### Batch Failure Diagnostics

When a batch fails, the executor analyzes the error to identify root causes ([`BulkOperationExecutor.AnalyzeBatchFailure`](../src/PPDS.Dataverse/BulkOperations/BulkOperationExecutor.cs)):

```csharp
public static IReadOnlyList<BatchFailureDiagnostic> AnalyzeBatchFailure(
    IReadOnlyList<Entity> batch, Exception exception)
```

Detects patterns:
- **SELF_REFERENCE**: Record references its own ID (not yet created)
- **SAME_BATCH_REFERENCE**: References another record in same batch
- **MISSING_REFERENCE**: References non-existent record

Each diagnostic includes a `Suggestion` field with resolution guidance.

### Edge Cases

| Scenario | Expected Behavior |
|----------|-------------------|
| Empty collection | Return success with 0 counts |
| Single record | Execute as single-item batch |
| All fail (elastic) | Return with all errors, FailureCount = Total |
| Batch fails (standard) | Entire batch counts as failure |
| Cancellation during batch | Return partial results |

---

## Design Decisions

### Why Native Bulk APIs Over ExecuteMultiple?

**Context:** ExecuteMultiple processes requests sequentially server-side, limiting throughput. Microsoft introduced CreateMultiple/UpdateMultiple with parallel server-side processing.

**Decision:** Use native bulk APIs exclusively; do not fall back to ExecuteMultiple.

**Test results:**
| Approach | Throughput |
|----------|------------|
| Single requests | ~50K records/hour |
| ExecuteMultiple | ~2M records/hour |
| CreateMultiple/UpdateMultiple | ~10M records/hour |

**Consequences:**
- Positive: 5x throughput over ExecuteMultiple
- Negative: All-or-nothing batch semantics on standard tables

### Why Batch Size 100?

**Context:** Microsoft recommends 100-1000 records per batch. Smaller batches enable finer parallelism; larger batches reduce request overhead.

**Decision:** Default to 100 records per batch.

**Rationale:**
- Matches Microsoft's elastic table recommendation
- More granular parallelism distribution
- Reduces timeout risk with plugins
- Provides good balance between overhead and parallelism

**Consequences:**
- Positive: Predictable execution time per batch
- Negative: Slightly more requests than 1000-record batches

### Why Pool-Managed Parallelism?

**Context:** Early implementations used fixed parallelism, ignoring pool capacity. This caused pool exhaustion when multiple operations ran concurrently.

**Decision:** Batches acquire slots from `BatchParallelismCoordinator`, which is shared across all bulk operations on the pool.

**Algorithm:**
1. Pool exposes `BatchCoordinator` semaphore with `TotalRecommendedParallelism` slots
2. Each batch calls `AcquireAsync()` before getting a client
3. Slot released when batch completes (success or failure)

**Consequences:**
- Positive: Fair sharing between concurrent bulk operations
- Positive: Cannot exceed pool capacity
- Negative: Single slow batch holds slot, may limit throughput

### Why Pre-Flight Throttle Guard?

**Context:** Without pre-flight checking, batches would acquire clients, execute, hit throttle, and retry—wasting capacity during throttle waits.

**Decision:** Check `IThrottleTracker.IsThrottled()` before executing each batch. If throttled, return client to pool and get a different one.

**Implementation:** ([`BulkOperationExecutor.cs:704-734`](../src/PPDS.Dataverse/BulkOperations/BulkOperationExecutor.cs#L704-L734))
```
Pre-flight loop (max 10 attempts):
1. Get client from pool
2. Check if client's connection is throttled
3. If throttled, return client, get another
4. If all throttled after 10 attempts, proceed anyway (safety valve)
```

**Consequences:**
- Positive: Prevents "in-flight avalanche" hitting throttled connections
- Negative: Extra tracker lookup per batch

### Why Indefinite Retry for Throttling?

**Context:** Throttling is always transient—server recovers in 30-120 seconds. Failing bulk operations on throttle risks data loss and forces callers to implement retry.

**Decision:** Retry indefinitely for service protection errors. The pool naturally handles this via `WaitForNonThrottledConnectionAsync()`.

**Consequences:**
- Positive: Operations eventually complete; callers don't implement retry
- Negative: Long-running operations during heavy throttling
- Mitigation: `CancellationToken` allows caller to abort

### Why Separate Delete Path for Standard vs Elastic?

**Context:** Dataverse has no native `DeleteMultiple` API for standard (SQL-backed) tables. Elastic (Cosmos-backed) tables support it.

**Decision:** Use `ExecuteMultiple` with `DeleteRequest` for standard tables; use native `DeleteMultiple` for elastic tables.

**Detection:** `BulkOperationOptions.ElasticTable` flag set by caller.

**Consequences:**
- Positive: Delete works on all table types
- Negative: Standard table delete is slower (sequential server-side)
- Negative: Caller must know table type

---

## Extension Points

### Adding Bulk Operation Probing

For entities that don't support bulk operations, use `BulkOperationProber` ([`src/PPDS.Migration/Import/BulkOperationProber.cs`](../src/PPDS.Migration/Import/BulkOperationProber.cs)):

```csharp
var prober = new BulkOperationProber(bulkExecutor);
var result = await prober.ExecuteWithProbeAsync(
    entityName, records, BulkOperationType.Create,
    options, fallbackExecutor, progress, cancellationToken);
```

The prober:
1. Attempts bulk operation with a single record
2. If unsupported, caches result and uses fallback
3. Subsequent calls skip probe for cached entities

---

## Configuration

| Setting | Type | Required | Default | Description |
|---------|------|----------|---------|-------------|
| `BatchSize` | int | No | 100 | Records per batch request |
| `ElasticTable` | bool | No | false | Enable partial success (Cosmos-backed tables) |
| `ContinueOnError` | bool | No | true | For delete on standard tables only |
| `BypassCustomLogic` | enum | No | None | Bypass sync/async plugins (requires privilege) |
| `BypassPowerAutomateFlows` | bool | No | false | Bypass Power Automate triggers |
| `SuppressDuplicateDetection` | bool | No | false | Skip duplicate detection rules |
| `Tag` | string | No | null | Passed to plugins via `context.SharedVariables["tag"]` |
| `MaxParallelBatches` | int? | No | null | Override pool's recommended parallelism |

### CustomLogicBypass Values

```csharp
[Flags]
public enum CustomLogicBypass
{
    None = 0,                           // Execute all custom logic (default)
    Synchronous = 1,                    // Bypass sync plugins and workflows
    Asynchronous = 2,                   // Bypass async plugins and workflows
    All = Synchronous | Asynchronous    // Bypass all custom logic
}
```

**Note:** Requires `prvBypassCustomBusinessLogic` privilege (typically System Administrator only).

---

## Testing

### Acceptance Criteria

- [ ] CreateMultiple creates records and returns IDs
- [ ] UpdateMultiple updates existing records
- [ ] UpsertMultiple handles mixed create/update
- [ ] DeleteMultiple removes records (standard and elastic)
- [ ] Batching respects BatchSize option
- [ ] Progress reports after each batch
- [ ] Throttle retry eventually succeeds
- [ ] Cancellation stops processing

### Edge Cases

| Scenario | Input | Expected Output |
|----------|-------|-----------------|
| Empty collection | 0 entities | Success, 0 counts |
| Single record | 1 entity | Success, 1 created |
| Exact batch boundary | 100 entities, BatchSize=100 | 1 batch executed |
| One over boundary | 101 entities, BatchSize=100 | 2 batches executed |
| All records fail | Invalid data | FailureCount = Total, Errors populated |
| Partial failure (elastic) | Some invalid | SuccessCount + FailureCount = Total |

### Test Examples

```csharp
[Fact]
public async Task CreateMultipleAsync_ReturnCreatedIds()
{
    var entities = Enumerable.Range(0, 10)
        .Select(_ => new Entity("account") { ["name"] = Guid.NewGuid().ToString() })
        .ToList();

    var result = await Executor.CreateMultipleAsync("account", entities);

    result.IsSuccess.Should().BeTrue();
    result.SuccessCount.Should().Be(10);
    result.CreatedIds.Should().HaveCount(10);
}

[Fact]
public async Task UpsertMultipleAsync_TracksCreatedAndUpdated()
{
    // Create some records first
    var existing = await CreateTestRecords(5);

    // Upsert mix of existing and new
    var entities = existing.Concat(CreateNewEntities(5)).ToList();
    var result = await Executor.UpsertMultipleAsync("account", entities);

    result.IsSuccess.Should().BeTrue();
    result.CreatedCount.Should().Be(5);
    result.UpdatedCount.Should().Be(5);
}

[Fact]
public async Task Progress_ReportsAfterEachBatch()
{
    var snapshots = new List<ProgressSnapshot>();
    var progress = new Progress<ProgressSnapshot>(snapshots.Add);
    var entities = CreateTestEntities(250);  // 3 batches at size 100

    await Executor.CreateMultipleAsync("account", entities,
        new BulkOperationOptions { BatchSize = 100 }, progress);

    snapshots.Should().HaveCountGreaterOrEqualTo(3);
    snapshots.Last().PercentComplete.Should().Be(100);
}
```

---

## Related Specs

- [connection-pooling.md](./connection-pooling.md) - Pool provides clients and coordinates parallelism
- [architecture.md](./architecture.md) - Progress reporting patterns
- [migration.md](./migration.md) - Uses bulk operations for data import

---

## Roadmap

- Automatic elastic table detection (currently caller must specify)
- Adaptive batch sizing based on record complexity
- Batch failure recovery with record-level retry
