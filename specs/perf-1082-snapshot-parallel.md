# Parallel Environment Snapshot Loading

**Status:** Draft
**Last Updated:** 2026-05-16
**Code:** [src/PPDS.Cli/Services/Schema/Snapshots/](../src/PPDS.Cli/Services/Schema/Snapshots/)
**Surfaces:** CLI

---

## Overview

`EnvironmentSnapshotLoader` fetches full entity metadata (attributes + relationships) for every entity selected in a `schema compare` run. Each entity requires a separate SDK round-trip. With 200+ entities in a full schema, sequential fetching takes several minutes. This spec parallelizes those per-entity calls to match the pool's recommended degree of parallelism (DOP), cutting wall-clock time proportionally.

### Goals

- **Throughput**: Execute per-entity `GetEntityAsync` calls concurrently up to the pool's recommended DOP.
- **Pool correctness**: Each parallel task acquires its own pool client; no shared client is held across concurrent tasks.
- **Result stability**: Entity order and data content are identical to the sequential implementation.

### Non-Goals

- Parallelizing the initial `GetEntitiesAsync` (entity-list) call — that is a single request, not a bottleneck.
- Changing the `IMetadataQueryService` interface or `DataverseMetadataQueryService` implementation.
- Caching or prefetching entity metadata beyond the current snapshot scope.

---

## Architecture

```
CompareCommand
    │  pool.GetTotalRecommendedParallelism() → parallelism
    │
    ▼
EnvironmentSnapshotLoader(metadata, descriptor, progress, parallelism)
    │
    │  Parallel.ForEachAsync (DOP = parallelism)
    │  ┌─────────────────────────────────────────────┐
    │  │  task[i]: IMetadataQueryService.GetEntityAsync
    │  │           → DataverseMetadataQueryService
    │  │             → _pool.GetClientAsync()  [own client]
    │  │             → ExecuteAsync(RetrieveEntityRequest)
    │  │             → dispose client
    │  └─────────────────────────────────────────────┘
    │  results[i] = EntitySnapshot (written by index)
    ▼
SchemaSnapshot { Entities = results in original order }
```

### Components

| Component | Responsibility |
|-----------|----------------|
| `EnvironmentSnapshotLoader` | Fetches per-entity metadata in parallel; builds `SchemaSnapshot` |
| `IMetadataQueryService` | Acquires own pool client per `GetEntityAsync` call (thread-safe) |
| `IDataverseConnectionPool` | Supplies recommended DOP via `GetTotalRecommendedParallelism()` |
| `CompareCommand` | Reads DOP from pool at construction time; passes to loader |

### Dependencies

- Depends on: [connection-pooling.md](./connection-pooling.md)

---

## Specification

### Core Requirements

1. `EnvironmentSnapshotLoader` accepts an optional `int parallelism` parameter (default `1`). When `1`, behavior is functionally identical to the previous sequential implementation.
2. Parallel execution uses `Parallel.ForEachAsync` with `MaxDegreeOfParallelism = parallelism`. Results are written into a pre-allocated array by index so entity order matches the `selected` list regardless of completion order.
3. No pool client is held across parallel tasks. Each invocation of `IMetadataQueryService.GetEntityAsync` independently acquires and releases a pool client inside `DataverseMetadataQueryService`. The loader never touches the pool directly.
4. `CompareCommand` resolves `IDataverseConnectionPool` from the service provider and passes `pool.GetTotalRecommendedParallelism()` as the `parallelism` argument for both package-vs-env and env-vs-env modes.
5. Progress messages continue to fire once per entity with the entity's list position and logical name. Emission order is non-deterministic when `parallelism > 1`; content is always correct.
6. `CancellationToken` is threaded through every `GetEntityAsync` call inside the parallel body and through the `ParallelOptions`.
7. Exceptions from parallel tasks propagate as `AggregateException` unwrapped through `Parallel.ForEachAsync`. The existing `PpdsException` catch block in `LoadAsync` continues to wrap unexpected exceptions.

### Primary Flows

**Parallel entity fetch:**

1. **Enumerate**: `GetEntitiesAsync` returns `allEntities`; filter to `selected` list (unchanged).
2. **Allocate**: Pre-allocate `EntitySnapshot?[] results = new EntitySnapshot?[selected.Count]`.
3. **Dispatch**: `Parallel.ForEachAsync(selected.Select((e, i) => (e, i)), new ParallelOptions { MaxDegreeOfParallelism = _parallelism, CancellationToken = cancellationToken }, async (item, ct) => { ... results[item.Index] = snapshot; })`.
4. **Progress**: Within the parallel body, `_progress?.Invoke(...)` before the `GetEntityAsync` call.
5. **Collect**: After `Parallel.ForEachAsync` completes, build `List<EntitySnapshot>` from `results` (all slots are non-null at this point).
6. **Return**: Construct `SchemaSnapshot` as before.

### Surface-Specific Behavior

#### CLI Surface

Progress messages on stderr continue to fire once per entity. When `parallelism > 1`, the messages may arrive out of list order (e.g., entity 7 might finish before entity 3). The message format does not change: `"  Loading entity {1-based-index}/{total}: {logicalName}"`.

### Constraints

- **NEVER rule (CLAUDE.md D2)**: No single pooled client is held across multiple parallel calls. Each parallel task calls `IMetadataQueryService.GetEntityAsync` independently; the service acquires/releases its own client internally.
- `parallelism` is clamped to `Math.Max(1, parallelism)` in the constructor to guard against misconfigured pools returning 0.
- Result array index access is safe: each task writes to a unique index; no locking needed.

---

## Acceptance Criteria

| ID | Criterion | Test | Status |
|----|-----------|------|--------|
| AC-01 | `LoadAsync` with default `parallelism=1` produces the same snapshot as the previous sequential implementation (same entities, same order, same attribute/relationship data) | `EnvironmentSnapshotLoaderTests.LoadAsync_BuildsSnapshotFromMetadataService` (existing) | ✅ |
| AC-02 | With `parallelism=4` and 6 entities, entities appear in the original list order in `snapshot.Entities` regardless of completion order | `EnvironmentSnapshotLoaderTests.LoadAsync_ResultsPreserveOrder_WhenParallel` | ✅ |
| AC-03 | With `parallelism=4` and 8 entities, at most 4 concurrent `GetEntityAsync` calls are in-flight simultaneously (semaphore-based concurrency probe) | `EnvironmentSnapshotLoaderTests.LoadAsync_BoundsParallelism_ByDegree` | ✅ |
| AC-04 | With `parallelism=4` and 8 entities, peak concurrency measured in the mock exceeds 1 (actual parallelism achieved) | `EnvironmentSnapshotLoaderTests.LoadAsync_AchievesConcurrency_WhenParallelismGreaterThanOne` | ✅ |
| AC-05 | Progress callback fires exactly once per entity (no double-firing, no skipped entities) | `EnvironmentSnapshotLoaderTests.LoadAsync_InvokesProgressCallback_PerEntity` (existing) | ✅ |
| AC-06 | `OperationCanceledException` from a cancelled `CancellationToken` propagates out of `LoadAsync` (not swallowed) | `EnvironmentSnapshotLoaderTests.LoadAsync_PropagatesCancellation_WhenTokenCancelled` | ✅ |
| AC-07 | `CompareCommand` (package-vs-env mode) constructs `EnvironmentSnapshotLoader` with `pool.GetTotalRecommendedParallelism()` as the parallelism degree | `CompareCommandTests.ExecuteAsync_PackageVsEnv_PassesPoolParallelismToLoader` | ❌ (deferred — no harness for CompareCommand integration tests) |

### Edge Cases

| Scenario | Input | Expected Output |
|----------|-------|-----------------|
| No entities after filter | empty `selected` | `SchemaSnapshot` with empty `Entities` list |
| `parallelism = 0` from misconfigured pool | `parallelism ≤ 0` | clamped to `1`; sequential execution |
| Single entity | 1 entity, `parallelism = 4` | snapshot with 1 entity; no error |
| Entity fetch throws `PpdsException` | mock throws on one entity | exception propagates from `LoadAsync` |
| Token cancelled mid-parallel | `CancellationToken` cancelled after 2 of 10 tasks start | `OperationCanceledException` |

### Test Examples

```csharp
[Fact]
public async Task LoadAsync_ResultsPreserveOrder_WhenParallel()
{
    // Arrange: 6 entities with artificial delay reversed (last entity is fastest)
    var names = new[] { "a", "b", "c", "d", "e", "f" };
    var mock = new Mock<IMetadataQueryService>();
    mock.Setup(m => m.GetEntitiesAsync(...)).ReturnsAsync(names.Select(Summary).ToArray());

    // Each entity introduces a delay inversely proportional to its index
    // (so "f" resolves first, "a" resolves last)
    var call = 0;
    mock.Setup(m => m.GetEntityAsync(It.IsAny<string>(), ...))
        .Returns((string name, ...) =>
        {
            var delay = (names.Length - Array.IndexOf(names, name)) * 10;
            return Task.Delay(delay).ContinueWith(_ => BuildEntity(name));
        });

    var loader = new EnvironmentSnapshotLoader(mock.Object, "env:test", parallelism: 4);
    var snapshot = await loader.LoadAsync();

    snapshot.Entities.Select(e => e.LogicalName).Should().Equal(names);
}

[Fact]
public async Task LoadAsync_BoundsParallelism_ByDegree()
{
    const int parallelism = 4;
    const int entityCount = 8;
    var peak = 0;
    var current = 0;

    var mock = new Mock<IMetadataQueryService>();
    mock.Setup(m => m.GetEntitiesAsync(...))
        .ReturnsAsync(Enumerable.Range(1, entityCount).Select(i => Summary($"e{i}")).ToArray());

    mock.Setup(m => m.GetEntityAsync(It.IsAny<string>(), ...))
        .Returns(async (string name, ...) =>
        {
            var c = Interlocked.Increment(ref current);
            Interlocked.Exchange(ref peak, Math.Max(peak, c));
            await Task.Delay(50);
            Interlocked.Decrement(ref current);
            return BuildEntity(name);
        });

    var loader = new EnvironmentSnapshotLoader(mock.Object, "env:test", parallelism: parallelism);
    await loader.LoadAsync();

    peak.Should().BeLessThanOrEqualTo(parallelism);
    peak.Should().BeGreaterThan(1); // actual concurrency achieved
}
```

---

## Core Types

### `EnvironmentSnapshotLoader` (updated constructor)

```csharp
public EnvironmentSnapshotLoader(
    IMetadataQueryService metadata,
    string sourceDescriptor,
    Action<string>? progress = null,
    int parallelism = 1)
```

The `parallelism` parameter maps directly to `Parallel.ForEachAsync`'s `MaxDegreeOfParallelism`. Clamped to `Math.Max(1, parallelism)`.

### Usage Pattern

```csharp
// In CompareCommand — get DOP from pool at construction time
var pool = provider.GetRequiredService<IDataverseConnectionPool>();
var loader = new EnvironmentSnapshotLoader(
    provider.GetRequiredService<IMetadataQueryService>(),
    $"env:{connInfo.EnvironmentUrl}",
    progress,
    pool.GetTotalRecommendedParallelism());
```

---

## Design Decisions

### Why `int parallelism` parameter rather than injecting `IDataverseConnectionPool`?

**Context:** The loader needs a parallelism bound. Two options: accept `IDataverseConnectionPool` and call `GetTotalRecommendedParallelism()` internally, or accept a pre-resolved integer.

**Decision:** Accept `int parallelism` (mirrors `SqlQueryService._poolCapacity`).

**Alternatives considered:**
- Inject `IDataverseConnectionPool`: Would require the loader to depend on the pool directly, adding infrastructure coupling to what is conceptually a "compute" service. The pool DOP doesn't change during a single `LoadAsync` call, so reading it once at construction time (or call-site) is sufficient. `SqlQueryService` uses the same pattern.

**Consequences:**
- Positive: Simpler constructor; testable without a mock pool.
- Negative: DOP is captured once at construction; a pool whose DOP changes mid-call won't be reflected. Acceptable because metadata fetches are fast and the session is short-lived.

### Why `Parallel.ForEachAsync` with pre-allocated array?

**Context:** Must preserve entity order while running tasks concurrently.

**Decision:** Pre-allocate `EntitySnapshot?[]` by list length; each task writes to `results[index]`. `Parallel.ForEachAsync` handles concurrency bounding, back-pressure, and cancellation propagation natively.

**Alternatives considered:**
- `SemaphoreSlim` + `Task.WhenAll`: More boilerplate; `Parallel.ForEachAsync` is idiomatic for .NET 6+.
- `ConcurrentBag` + post-sort: Requires a sort key on `EntitySnapshot`; pre-allocated array is simpler and allocation-free.

**Consequences:**
- Positive: No locking required (each task writes to a unique index); clean cancellation.
- Negative: None material.

---

## Related Specs

- [connection-pooling.md](./connection-pooling.md) — Pool DOP semantics and `GetTotalRecommendedParallelism()`

---

## Changelog

| Date | Change |
|------|--------|
| 2026-05-16 | Initial spec (issue #1082) |
