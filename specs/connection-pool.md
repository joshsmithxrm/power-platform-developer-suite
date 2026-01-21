# Connection Pool

**Status:** Implemented
**Version:** 1.0
**Last Updated:** 2026-01-21
**Code:** [src/PPDS.Dataverse/Pooling/](../src/PPDS.Dataverse/Pooling/)

---

## Overview

The Connection Pool manages Dataverse connections with intelligent throttle-aware selection, multi-user quota multiplication, and automatic lifecycle management. It enables high-throughput bulk operations while respecting Microsoft's service protection limits.

### Goals

- **Quota Multiplication**: Support multiple Application Users to multiply API quota (N users = N x 6,000 requests/5min)
- **Throttle Resilience**: Route requests away from throttled connections automatically
- **Performance**: Disable affinity cookie for 10x+ throughput via backend load distribution
- **Fair Concurrency**: Semaphore-based queueing for multiple concurrent consumers

### Non-Goals

- Adaptive rate control (removed; DOP-based approach is simpler and prevents throttles)
- Per-operation retry logic (handled by consumers; pool provides transparent throttle waiting)
- Authentication (deferred to [authentication.md](./authentication.md) via `IConnectionSource`)

---

## Architecture

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                           IDataverseConnectionPool                          │
├─────────────────────────────────────────────────────────────────────────────┤
│ GetClientAsync() ─────▶ WaitForNonThrottled ─▶ Semaphore ─▶ SelectConnection│
│                         (no semaphore held)    (slot)       (strategy)      │
└───────────────────────────────────────────────────────────────────────────┬─┘
                                                                            │
        ┌───────────────────────────────────────────────────────────────────┘
        │
        ▼
┌───────────────────┐    ┌───────────────────┐    ┌───────────────────┐
│ IConnectionSource │    │ IConnectionSource │    │ IConnectionSource │
│   "AppUser1"      │    │   "AppUser2"      │    │   "AppUser3"      │
│   DOP: 52         │    │   DOP: 52         │    │   DOP: 52         │
└─────────┬─────────┘    └─────────┬─────────┘    └─────────┬─────────┘
          │                        │                        │
          ▼                        ▼                        ▼
┌─────────────────┐      ┌─────────────────┐      ┌─────────────────┐
│  Seed Client    │      │  Seed Client    │      │  Seed Client    │
│  (ServiceClient)│      │  (ServiceClient)│      │  (ServiceClient)│
└────────┬────────┘      └────────┬────────┘      └────────┬────────┘
         │                        │                        │
         ▼                        ▼                        ▼
┌─────────────────┐      ┌─────────────────┐      ┌─────────────────┐
│ Pool Queue      │      │ Pool Queue      │      │ Pool Queue      │
│ [Clone][Clone]  │      │ [Clone][Clone]  │      │ [Clone][Clone]  │
└─────────────────┘      └─────────────────┘      └─────────────────┘
         │                        │                        │
         └────────────────────────┼────────────────────────┘
                                  │
                                  ▼
                    ┌─────────────────────────┐
                    │     IThrottleTracker    │
                    │  Records 429 responses  │
                    │  Tracks Retry-After     │
                    └─────────────────────────┘
```

### Components

| Component | Responsibility |
|-----------|----------------|
| `IDataverseConnectionPool` | Main interface for acquiring and returning connections |
| `DataverseConnectionPool` | Core implementation with semaphore, selection, lifecycle |
| `IConnectionSource` | Abstraction for authentication methods (connection string, device code) |
| `IPooledClient` | Wrapper around ServiceClient with automatic pool return |
| `IThrottleTracker` | Records and queries throttle state per connection |
| `ThrottleAwareStrategy` | Default selection strategy routing away from throttled connections |
| `BatchParallelismCoordinator` | Coordinates concurrent bulk operations to prevent over-subscription |

### Dependencies

- Uses patterns from: [architecture.md](./architecture.md)
- Authentication via: [authentication.md](./authentication.md)

---

## Specification

### Core Requirements

1. **Semaphore-limited concurrency**: Pool semaphore sized at `52 x connectionCount` (Microsoft's hard limit per user)
2. **Two-phase throttle wait**: Wait for non-throttled connection WITHOUT holding semaphore, then acquire slot
3. **Automatic seed cloning**: Create pool members by cloning authenticated seed clients
4. **Connection validation**: Check idle time, max lifetime, IsReady state on checkout
5. **Transparent return**: `await using` pattern returns connection to pool on dispose

### Primary Flows

**Connection Acquisition:**

1. **Wait for non-throttled** ([`DataverseConnectionPool.cs:475-522`](../src/PPDS.Dataverse/Pooling/DataverseConnectionPool.cs#L475-L522)): Loop checking if any source is available; if all throttled, wait for shortest expiry (semaphore NOT held)
2. **Acquire semaphore** ([`DataverseConnectionPool.cs:397-404`](../src/PPDS.Dataverse/Pooling/DataverseConnectionPool.cs#L397-L404)): Wait with `AcquireTimeout` (default 120s)
3. **Select connection** ([`DataverseConnectionPool.cs:408-417`](../src/PPDS.Dataverse/Pooling/DataverseConnectionPool.cs#L408-L417)): Use selection strategy to pick source
4. **Get from pool or clone** ([`DataverseConnectionPool.cs:530-583`](../src/PPDS.Dataverse/Pooling/DataverseConnectionPool.cs#L530-L583)): Dequeue existing or create new from seed
5. **Apply options**: Set per-request CallerId, CallerAADObjectId

**Connection Return:**

1. **Decrement active count**: Track per-source active connections
2. **Check validity**: If marked invalid, dispose instead of pooling
3. **Reset state**: Restore original CallerId, MaxRetryCount, RetryPauseTime
4. **Enqueue if pool not full**: Return to source's queue
5. **Release semaphore**: Always release in finally block

**Throttle Detection:**

1. **Wrap operations** ([`ThrottleDetector.cs:46`](../src/PPDS.Dataverse/Resilience/ThrottleDetector.cs#L46)): All IOrganizationService methods wrapped
2. **Check for 429**: Detect FaultException with service protection error
3. **Extract Retry-After**: Parse from ErrorDetails (fallback 30s)
4. **Record state** ([`ThrottleTracker.cs:39-65`](../src/PPDS.Dataverse/Resilience/ThrottleTracker.cs#L39-L65)): Store connection name + expiry time
5. **Auto-clear**: Expired entries cleaned on next query

### Constraints

- Maximum 52 concurrent requests per Application User (Microsoft hard limit)
- Connection lifetime capped at 60 minutes (OAuth token validity window)
- Semaphore slot must be released even on exception (finally block pattern)
- Never hold semaphore during throttle wait (prevents pool exhaustion)

### Service Protection Limits

| Limit | Value | Per |
|-------|-------|-----|
| Concurrent requests | 52 | Application User |
| Requests | 6,000 | 5 minutes per user |
| Execution time | 20 minutes | 5 minutes per user |

---

## Core Types

### IDataverseConnectionPool

Main interface for connection pool operations ([`IDataverseConnectionPool.cs:15-182`](../src/PPDS.Dataverse/Pooling/IDataverseConnectionPool.cs#L15-L182)).

```csharp
public interface IDataverseConnectionPool : IAsyncDisposable, IDisposable
{
    Task<IPooledClient> GetClientAsync(DataverseClientOptions? options = null,
        string? excludeConnectionName = null, CancellationToken cancellationToken = default);
    Task<OrganizationResponse> ExecuteAsync(OrganizationRequest request, CancellationToken ct = default);
    int GetTotalRecommendedParallelism();
    PoolStatistics Statistics { get; }
}
```

### IPooledClient

Connection wrapper with automatic pool return ([`IPooledClient.cs:22-68`](../src/PPDS.Dataverse/Pooling/IPooledClient.cs#L22-L68)).

```csharp
public interface IPooledClient : IDataverseClient, IAsyncDisposable, IDisposable
{
    string ConnectionName { get; }
    bool IsInvalid { get; }
    void MarkInvalid(string reason);
}
```

### IConnectionSource

Authentication abstraction for pool seed creation ([`IConnectionSource.cs:20-63`](../src/PPDS.Dataverse/Pooling/IConnectionSource.cs#L20-L63)).

```csharp
public interface IConnectionSource : IDisposable
{
    string Name { get; }
    ServiceClient GetSeedClient();
    void InvalidateSeed();
}
```

### Usage Pattern

```csharp
// Get connection - returns to pool when disposed
await using var client = await _pool.GetClientAsync();
var result = await client.RetrieveAsync("account", id, new ColumnSet(true));

// Or use ExecuteAsync for automatic throttle retry
var response = await _pool.ExecuteAsync(new RetrieveRequest { ... });
```

---

## Error Handling

### Error Types

| Error | Condition | Recovery |
|-------|-----------|----------|
| `PoolExhaustedException` | Semaphore wait timeout (120s default) | Reduce parallelism or add connection sources |
| `ServiceProtectionException` | All throttled + MaxRetryAfterTolerance exceeded | Wait longer or add connection sources |
| `DataverseConnectionException` | Seed creation or clone failure | Check credentials, network |
| `DataverseAuthenticationException` | 401/403 authentication errors | Call `InvalidateSeed()`, re-authenticate |

### Recovery Strategies

- **Throttle (429)**: Pool waits automatically; use `ExecuteAsync` for transparent retry
- **Auth failure**: Call `InvalidateSeed(connectionName)` to force fresh authentication
- **Pool exhausted**: Indicates over-parallelization; respect pool semaphore limits

### Edge Cases

| Scenario | Expected Behavior |
|----------|-------------------|
| All connections throttled | Wait for shortest Retry-After without holding semaphore |
| Token expired mid-operation | Mark connection invalid, pool drains all clones of that seed |
| Disposed during wait | CancellationToken propagates, semaphore released |
| High-core machine (24+ cores) | Cap parallel tasks at `Math.Min(ProcessorCount * 4, poolCapacity)` |

---

## Design Decisions

### Why Disable Affinity Cookie?

**Context:** ServiceClient's `EnableAffinityCookie` defaults to `true`, routing all requests to a single backend node regardless of connection count.

**Decision:** Disable affinity cookie by default (`DisableAffinityCookie = true`).

**Test results:**
| Scenario | Result |
|----------|--------|
| Affinity enabled (SDK default) | Single backend node, bottleneck |
| Affinity disabled | 10x+ throughput improvement |

**Alternatives considered:**
- Keep SDK default: Rejected - causes severe bottleneck

**Consequences:**
- Positive: 10x+ throughput for high-volume operations; better server capacity utilization
- Negative: Slightly higher latency per request (no connection reuse at backend)

---

### Why Multi-Connection Pooling?

**Context:** Dataverse enforces service protection limits per Application User: 6,000 requests/5min, 52 concurrent requests.

**Decision:** Support multiple connection sources (different Application Users) to multiply quota.

**Test results:**
| Users | DOP | Throughput | Throttles |
|-------|-----|------------|-----------|
| 1 | 5 | 202 rec/s | 0 |
| 2 | 10 | 395 rec/s | 0 |
| Scaling | 2 users | 1.95x | - |

**Alternatives considered:**
- Single connection: Rejected - quota limited to one user
- Load-balanced proxy: Rejected - unnecessary complexity

**Consequences:**
- Positive: Quota multiplied by N users; graceful degradation when one throttled
- Negative: Requires provisioning multiple Application Users in Entra ID

---

### Why DOP-Based Parallelism (Not Adaptive)?

**Context:** Need to determine optimal parallelism. Options: adaptive rate control (AIMD) vs server-recommended DOP ceiling.

**Decision:** Use `RecommendedDegreesOfParallelism` from server as ceiling, not floor. No adaptive ramping.

**Test results:**
| Scenario | Users | DOP | Throughput | Time | Throttles |
|----------|-------|-----|------------|------|-----------|
| DOP-based | 1 | 5 | 202 rec/s | 05:58 | 0 |
| DOP-based | 2 | 10 | 395 rec/s | 03:03 | 0 |
| Exceeded DOP | 1 | 10+ | ~100 rec/s | 12:15 | 155 |

**Alternatives considered:**
- Adaptive AIMD (start low, ramp up): Rejected - 155 throttles, 4x slower, ~500 lines removed

**Consequences:**
- Positive: Zero throttles when respecting DOP; simpler code; predictable performance
- Negative: Lower peak throughput on short operations; requires multiple users for high throughput

---

### Why Two-Phase Throttle Wait?

**Context:** When all connections are throttled, pool must wait. Question: hold semaphore during wait?

**Decision:** Wait for non-throttled connection WITHOUT holding semaphore (Phase 1), then acquire (Phase 2).

**Alternatives considered:**
- Hold semaphore during wait: Rejected - causes `PoolExhaustedException` when many requests wait simultaneously

**Consequences:**
- Positive: No semaphore exhaustion during throttle events; fair queueing
- Negative: All-or-nothing (either wait full Retry-After or fail immediately)

---

### Why IConnectionSource Abstraction?

**Context:** Pool was tightly coupled to connection string auth. CLI used separate `DeviceCodeConnectionPool` that missed all pool features.

**Decision:** Introduce `IConnectionSource` interface separating authentication from pooling.

**Implementations:**
- `ConnectionStringSource`: Traditional client credentials
- `ServiceClientSource`: Pre-authenticated clients (device code, managed identity)

**Alternatives considered:**
- Duplicate pools per auth method: Rejected - unmaintainable, missed features

**Consequences:**
- Positive: Any auth method uses same pool; CLI gets full features; extensible
- Negative: Breaking change for direct pool consumers

---

### Why Pool-Managed Concurrency?

**Context:** Multiple concurrent consumers (parallel entity imports) each called `GetTotalRecommendedParallelism()` and assumed full capacity.

**Decision:** Callers should NOT pre-calculate parallelism. Let pool semaphore naturally limit concurrency.

**Test results:**
| Scenario | Before | After |
|----------|--------|-------|
| Pool exhaustion warnings | 125+ simultaneous | 0 |
| 4 entities x 16 DOP | 64 tasks, timeout storm | 64 tasks, fair queueing |

**Alternatives considered:**
- Caller pre-calculates: Broken - 4 consumers x 16 DOP = 64 tasks for 16 slots

**Consequences:**
- Positive: Fair sharing; daemon-ready (VS Code, CLI, background jobs); self-regulating
- Negative: More blocked tasks (acceptable; .NET handles efficiently)

---

### Why Semaphore-Based Fair Queuing?

**Context:** Need fair access for multiple consumers without coordination overhead.

**Decision:** Single `SemaphoreSlim` sized at `52 x connectionCount`. All consumers queue on same semaphore.

**Alternatives considered:**
- Per-source semaphores: Rejected - doesn't help fairness
- Token bucket: Rejected - more complex, same effect

**Consequences:**
- Positive: Automatic fairness; simple implementation; .NET-native efficiency
- Negative: Coarse-grained (can't prioritize specific consumers)

---

## Configuration

| Setting | Type | Default | Description |
|---------|------|---------|-------------|
| `Enabled` | bool | true | Enable/disable pooling |
| `MaxPoolSize` | int | 0 | Override capacity (0 = 52 per user) |
| `MaxRetryAfterTolerance` | TimeSpan? | null | Fail if throttle exceeds this; null = wait indefinitely |
| `AcquireTimeout` | TimeSpan | 120s | Max wait for connection slot |
| `MaxIdleTime` | TimeSpan | 5min | Evict idle connections |
| `MaxLifetime` | TimeSpan | 60min | Max connection age (OAuth window) |
| `DisableAffinityCookie` | bool | true | Load distribution (10x+ perf) |
| `SelectionStrategy` | enum | ThrottleAware | RoundRobin, LeastConnections, ThrottleAware |
| `ValidationInterval` | TimeSpan | 1min | Background health check frequency |
| `EnableValidation` | bool | true | Enable background validation |
| `ValidateOnCheckout` | bool | true | Validate on acquire |
| `MaxConnectionRetries` | int | 2 | Auth/connection failure retries |

Configuration defined in [`ConnectionPoolOptions.cs:8-140`](../src/PPDS.Dataverse/Pooling/ConnectionPoolOptions.cs#L8-L140).

---

## Testing

### Acceptance Criteria

- [ ] Zero throttles when respecting DOP limits
- [ ] 10x+ throughput with affinity cookie disabled
- [ ] Fair queueing with multiple concurrent consumers
- [ ] Automatic recovery from token expiry via `InvalidateSeed`
- [ ] Semaphore released on all code paths (including exceptions)

### Edge Cases

| Scenario | Input | Expected Output |
|----------|-------|-----------------|
| All sources throttled | 3 sources, all 429 | Wait for shortest Retry-After |
| Token expired | Auth failure detected | Seed invalidated, pool drained, fresh auth |
| Pool disposed during wait | Dispose called | CancellationToken fires, clean exit |
| High parallelism request | 100 concurrent tasks | Semaphore queues, no exhaustion |

### Test Examples

```csharp
[Fact]
public async Task GetClientAsync_WhenAllThrottled_WaitsForShortestExpiry()
{
    // Arrange
    var tracker = new ThrottleTracker();
    tracker.RecordThrottle("Source1", TimeSpan.FromSeconds(30));
    tracker.RecordThrottle("Source2", TimeSpan.FromSeconds(10)); // shortest

    // Act - GetClientAsync waits for Source2's 10s expiry
    var client = await pool.GetClientAsync();

    // Assert
    Assert.Equal("Source2", client.ConnectionName);
}

[Fact]
public async Task ConcurrentConsumers_SharePoolFairly()
{
    // Arrange - pool with DOP=4
    var tasks = Enumerable.Range(0, 20)
        .Select(_ => pool.GetClientAsync());

    // Act - all 20 queue on semaphore, 4 proceed
    var clients = await Task.WhenAll(tasks);

    // Assert - all got connections without timeout
    Assert.Equal(20, clients.Length);
}
```

---

## Related Specs

- [architecture.md](./architecture.md) - Application Service pattern, multi-interface design
- [authentication.md](./authentication.md) - Profile model, credential providers, `IConnectionSource` implementations

---

## Roadmap

- Connection health metrics export (Prometheus/OpenTelemetry)
- Per-source priority weighting for heterogeneous quotas
- Warm-up optimization for cold-start scenarios
