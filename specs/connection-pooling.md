# Connection Pooling

**Status:** Implemented
**Version:** 1.0
**Last Updated:** 2026-01-23
**Code:** [src/PPDS.Dataverse/Pooling/](../src/PPDS.Dataverse/Pooling/), [src/PPDS.Dataverse/Resilience/](../src/PPDS.Dataverse/Resilience/)

---

## Overview

The connection pooling system manages Dataverse client lifecycle with intelligent load distribution and throttle awareness. It maintains a pool of pre-authenticated connections across multiple Application Users, automatically routing requests away from throttled connections and handling service protection errors transparently.

### Goals

- **Performance**: Reuse authenticated connections instead of creating new ones (42,000x faster)
- **Resilience**: Automatic throttle detection, tracking, and intelligent routing
- **Scalability**: Support multiple Application Users for parallel operations beyond single-user limits

### Non-Goals

- Implementing retry logic for business errors (only service protection errors)
- Connection-level transaction management (Dataverse is stateless)
- Cross-environment pooling (one pool per environment)

---

## Architecture

```
┌──────────────────────────────────────────────────────────────────────────┐
│                         Application Layer                                 │
│                  (CLI, TUI, RPC, MCP, Migration)                         │
└────────────────────────────────┬─────────────────────────────────────────┘
                                 │
                                 ▼
┌──────────────────────────────────────────────────────────────────────────┐
│                    IDataverseConnectionPool                               │
│  ┌─────────────┐  ┌─────────────────────┐  ┌────────────────────────┐   │
│  │ GetClient   │  │ ExecuteAsync        │  │ TryGetClientWithCapacity│   │
│  │ Async/Sync  │  │ (auto throttle retry│  │ (DOP-aware)            │   │
│  └──────┬──────┘  └──────────┬──────────┘  └───────────┬────────────┘   │
│         │                    │                         │                 │
│         └────────────────────┼─────────────────────────┘                 │
│                              ▼                                           │
│  ┌────────────────────────────────────────────────────────────────────┐ │
│  │                     Connection Selection                            │ │
│  │  ┌───────────────────┐ ┌──────────────────┐ ┌──────────────────┐  │ │
│  │  │ ThrottleAware     │ │ LeastConnections │ │ RoundRobin       │  │ │
│  │  │ (default)         │ │                  │ │                  │  │ │
│  │  └─────────┬─────────┘ └────────┬─────────┘ └────────┬─────────┘  │ │
│  │            └────────────────────┴────────────────────┘            │ │
│  └────────────────────────────────────────────────────────────────────┘ │
│                              │                                           │
│  ┌───────────────────────────┴───────────────────────────────────────┐  │
│  │              Per-Source Connection Queues                          │  │
│  │   Source A: [PooledClient, PooledClient, ...]  (max 52 per user)  │  │
│  │   Source B: [PooledClient, PooledClient, ...]                      │  │
│  │   Source C: [PooledClient, PooledClient, ...]                      │  │
│  └───────────────────────────┬───────────────────────────────────────┘  │
│                              │                                           │
│  ┌───────────────────────────┴───────────────────────────────────────┐  │
│  │              Seed Client Cache (1 per source)                      │  │
│  │   Clone() creates pool members with inherited authentication       │  │
│  └────────────────────────────────────────────────────────────────────┘  │
└──────────────────────────────────────────────────────────────────────────┘
                                 │
                                 ▼
┌──────────────────────────────────────────────────────────────────────────┐
│                        IThrottleTracker                                   │
│  Tracks throttle state per connection, provides expiry information       │
│  Used by ThrottleAwareStrategy to route away from throttled connections  │
└──────────────────────────────────────────────────────────────────────────┘
```

### Components

| Component | Responsibility |
|-----------|----------------|
| `DataverseConnectionPool` | Manages connection lifecycle, semaphore, selection, validation |
| `IConnectionSource` | Provides seed ServiceClient for cloning |
| `PooledClient` | Wraps client, detects throttle, returns to pool on dispose |
| `IThrottleTracker` | Tracks throttle state per connection |
| `ThrottleDetector` | Wraps operations to detect service protection errors |
| `IConnectionSelectionStrategy` | Selects which connection to use |
| `BatchParallelismCoordinator` | Coordinates parallel bulk operations within pool capacity |

### Dependencies

- Uses patterns from: [architecture.md](./architecture.md)
- Consumed by: [bulk-operations.md](./bulk-operations.md), [dataverse-services.md](./dataverse-services.md), [mcp.md](./mcp.md)

---

## Specification

### Core Requirements

1. **Pool members are clones of authenticated seeds**: One ServiceClient per Application User serves as the seed; pool members are created via `Clone()` with inherited authentication
2. **Semaphore enforces capacity**: Total pool capacity respects Microsoft's 52-per-user limit
3. **Throttle-aware routing**: Requests route away from throttled connections when alternatives exist
4. **Two-phase acquisition**: Wait for non-throttled connection first, then acquire semaphore (prevents holding semaphore during throttle waits)
5. **Automatic return on dispose**: `PooledClient` returns to pool via `IDisposable`/`IAsyncDisposable`

### Primary Flows

**Client Acquisition (Async):**

1. **Wait for non-throttled**: `WaitForNonThrottledConnectionAsync()` blocks until at least one connection is not throttled
2. **Acquire semaphore**: Wait for pool capacity slot with `AcquireTimeout` (default 120s)
3. **Select connection**: Strategy chooses which source to use (ThrottleAware default)
4. **Get or create client**: Dequeue from pool or clone seed if pool empty
5. **Apply options**: Set `CallerId`, `CallerAADObjectId` if requested
6. **Return wrapped client**: `PooledClient` returns to pool on dispose

**Throttle Detection:**

1. **Wrap operations**: `ThrottleDetector` wraps all Dataverse operations
2. **Catch fault**: Detect `FaultException<OrganizationServiceFault>` with service protection error codes
3. **Extract Retry-After**: Parse from `ErrorDetails["Retry-After"]` or use 30s default
4. **Record throttle**: Call `IThrottleTracker.RecordThrottle(connectionName, retryAfter)`
5. **Re-throw**: Exception propagates to caller after recording

**Connection Validation:**

1. **Background loop**: Every `ValidationInterval` (1 minute default), drain and validate all pools
2. **Check validity**: `IsReady`, `MaxIdleTime` (5 min), `MaxLifetime` (60 min)
3. **Evict invalid**: Dispose connections that fail validation
4. **Maintain warmth**: Ensure at least 1 connection per source

### Constraints

- Pool capacity = sum of `RecommendedDegreesOfParallelism` per source (max 52 per user)
- `MaxPoolSize = 0` means use server-provided DOP; positive value overrides
- Affinity cookie disabled by default for load distribution (10x+ performance gain)
- Seed invalidation required when token expires (clones share token context)

### Validation Rules

| Field | Rule | Error |
|-------|------|-------|
| Connection sources | At least 1 required | `InvalidOperationException` |
| `AcquireTimeout` | Must be positive | Default 120s applied |
| `MaxIdleTime` | Must be positive | Default 5 min applied |
| `MaxLifetime` | Must be positive | Default 60 min applied |

---

## Core Types

### IDataverseConnectionPool

Central interface for connection management ([`IDataverseConnectionPool.cs`](../src/PPDS.Dataverse/Pooling/IDataverseConnectionPool.cs)).

```csharp
public interface IDataverseConnectionPool : IAsyncDisposable, IDisposable
{
    // Client acquisition
    Task<IPooledClient> GetClientAsync(DataverseClientOptions? options = null,
        string? excludeConnectionName = null, CancellationToken ct = default);
    IPooledClient GetClient(DataverseClientOptions? options = null);
    Task<IPooledClient?> TryGetClientWithCapacityAsync(CancellationToken ct = default);

    // Execution
    Task<OrganizationResponse> ExecuteAsync(OrganizationRequest request, CancellationToken ct);

    // Pool state
    PoolStatistics Statistics { get; }
    IReadOnlyList<SeedInitializationResult> InitializationResults { get; }
    bool IsEnabled { get; }
    int SourceCount { get; }
    BatchParallelismCoordinator BatchCoordinator { get; }

    // Capacity
    int GetTotalRecommendedParallelism();
    int GetLiveSourceDop(string sourceName);
    int GetActiveConnectionCount(string sourceName);

    // Lifecycle
    Task EnsureInitializedAsync(CancellationToken ct = default);
    void InvalidateSeed(string connectionName);
    void RecordAuthFailure();
    void RecordConnectionFailure();
}
```

**Key methods:**
- `GetClientAsync`: Primary acquisition path with throttle-aware routing
- `ExecuteAsync`: Automatic throttle retry (never throws service protection errors)
- `TryGetClientWithCapacityAsync`: Returns null if all sources at capacity
- `InvalidateSeed`: Force re-authentication when token expires
- `BatchCoordinator`: Coordinates concurrent bulk operations across pool capacity

### IPooledClient

Wrapped client that returns to pool on dispose ([`IPooledClient.cs`](../src/PPDS.Dataverse/Pooling/IPooledClient.cs)).

```csharp
public interface IPooledClient : IDataverseClient, IAsyncDisposable, IDisposable
{
    Guid ConnectionId { get; }
    string ConnectionName { get; }    // Stable key for throttle tracking
    string DisplayName { get; }       // "{ConnectionName}@{OrgFriendlyName}"
    DateTime CreatedAt { get; }
    DateTime LastUsedAt { get; }
    bool IsInvalid { get; }
    string? InvalidReason { get; }
    void MarkInvalid(string reason);  // Dispose instead of return to pool
}
```

### IConnectionSource

Provides seed ServiceClient for cloning ([`IConnectionSource.cs`](../src/PPDS.Dataverse/Pooling/IConnectionSource.cs)).

```csharp
public interface IConnectionSource : IDisposable
{
    string Name { get; }
    int MaxPoolSize { get; }
    ServiceClient GetSeedClient();
    void InvalidateSeed();
}
```

Implementations:
- `ConnectionStringSource`: Creates from connection configuration (ClientSecret, Certificate)
- `ServiceClientSource`: Wraps pre-authenticated client (DeviceCode, ManagedIdentity)

### IThrottleTracker

Tracks throttle state per connection ([`IThrottleTracker.cs`](../src/PPDS.Dataverse/Resilience/IThrottleTracker.cs)).

```csharp
public interface IThrottleTracker
{
    void RecordThrottle(string connectionName, TimeSpan retryAfter);
    bool IsThrottled(string connectionName);
    DateTime? GetThrottleExpiry(string connectionName);
    void ClearThrottle(string connectionName);
    TimeSpan GetShortestExpiry();

    // Statistics
    long TotalThrottleEvents { get; }
    TimeSpan TotalBackoffTime { get; }
    int ThrottledConnectionCount { get; }
    IReadOnlyCollection<string> ThrottledConnections { get; }
}
```

### Usage Pattern

```csharp
// Basic usage - auto-returns to pool
await using var client = await pool.GetClientAsync();
var result = await client.RetrieveAsync("account", id, new ColumnSet(true));

// With caller context
var options = new DataverseClientOptions { CallerId = userId };
await using var client = await pool.GetClientAsync(options);

// Auto-retry on throttle (never throws ServiceProtectionException)
var response = await pool.ExecuteAsync(new CreateRequest { Target = entity });
```

---

## Error Handling

### Error Types

| Error | Condition | Recovery |
|-------|-----------|----------|
| `PoolExhaustedException` | No connection available within `AcquireTimeout` | Increase pool size or reduce parallelism |
| `ServiceProtectionException` | All connections throttled, wait exceeds `MaxRetryAfterTolerance` | Wait for throttle to clear or adjust tolerance |
| `DataverseAuthenticationException` | Token expired or invalid credentials | Call `InvalidateSeed()` for re-auth |

### Recovery Strategies

- **Throttle detected**: Recorded in tracker, connection remains valid, routing adjusted
- **Auth failure**: Mark connection invalid, invalidate seed, force re-authentication
- **Connection failure**: Mark connection invalid, pool creates new clone on next request

### Edge Cases

| Scenario | Expected Behavior |
|----------|-------------------|
| All connections throttled | Wait for shortest expiry, then retry |
| Single connection throttled | Route to other connections |
| Token expired mid-operation | Client marked invalid, seed invalidated on return |
| Pool disposed during wait | `ObjectDisposedException` thrown |
| Zero connections configured | `InvalidOperationException` on construction |

---

## Design Decisions

### Why Connection Pooling?

**Context:** Creating a new `ServiceClient` instance requires authentication, which involves network round-trips to Azure AD and Dataverse. Each creation takes 500ms-4s.

**Decision:** Pool authenticated connections and reuse across requests via `Clone()`.

**Test results:**
| Scenario | Result |
|----------|--------|
| New client per request | 4.2s per operation |
| Pooled (cloned) client | 0.1ms per checkout |
| Performance gain | 42,000x faster |

**Alternatives considered:**
- Singleton client: Rejected - not thread-safe, single-user limit (52 concurrent)
- Client per thread: Rejected - excessive memory, no load distribution

**Consequences:**
- Positive: Massive performance improvement, enables high-throughput operations
- Negative: Must manage pool lifecycle, handle seed invalidation

### Why Multi-Source Pooling?

**Context:** Microsoft enforces a hard limit of 52 concurrent requests per Application User. Bulk operations require higher parallelism.

**Decision:** Support multiple Application Users (connection sources) with load distribution across them.

**Consequences:**
- Positive: Linear scalability (N users × 52 = total capacity)
- Negative: Requires multiple app registrations, more complex configuration
- Negative: Selection strategy needed to distribute load

### Why Two-Phase Semaphore Acquisition?

**Context:** Original implementation acquired semaphore, then waited for throttle. This held capacity slots while waiting, starving other operations.

**Decision:** Wait for non-throttled connection *before* acquiring semaphore.

```
Phase 1: WaitForNonThrottledConnectionAsync() - no semaphore held
Phase 2: Acquire semaphore only when ready to use connection
```

**Consequences:**
- Positive: No capacity waste during throttle waits
- Positive: Prevents thundering herd on throttle recovery
- Negative: More complex acquisition logic

### Why ThrottleAware as Default Strategy?

**Context:** Round-robin distributes evenly but ignores throttle state. Throttled connections receive requests that immediately fail.

**Decision:** Default to `ThrottleAwareStrategy` which filters throttled connections before selection.

**Algorithm:**
1. Filter out throttled connections
2. If all throttled, return one with shortest expiry (for intelligent waiting)
3. Round-robin among non-throttled connections

**Alternatives considered:**
- LeastConnections: Good for load balance, ignores throttle
- RoundRobin: Simple but hits throttled connections

**Consequences:**
- Positive: Maximizes throughput by avoiding throttled connections
- Negative: Slightly more CPU per selection (throttle check per source)

### Why Disable Affinity Cookie by Default?

**Context:** SDK default enables affinity cookie, routing all requests to a single backend node. This creates a bottleneck.

**Decision:** `DisableAffinityCookie = true` by default.

**Test results:**
| Setting | Throughput |
|---------|------------|
| Affinity enabled (SDK default) | ~10 req/sec |
| Affinity disabled (PPDS default) | ~100+ req/sec |

**Consequences:**
- Positive: 10x+ throughput improvement
- Negative: No session affinity (not needed for stateless Dataverse operations)

### Why Seed Cloning Architecture?

**Context:** Need many connections sharing same authentication context without re-authenticating each.

**Decision:** One "seed" `ServiceClient` per source, all pool members created via `Clone()`.

**Key insight:** `Clone()` copies authentication context (token) without network call. Token refresh happens on seed, propagates to clones.

**Consequences:**
- Positive: Fast pool member creation
- Positive: Single point of token management
- Negative: Token expiry affects all clones simultaneously
- Negative: Must invalidate seed on token failure (clones can't recover independently)

---

## Extension Points

### Adding a New Connection Source

1. **Implement** `IConnectionSource` in `src/PPDS.Dataverse/Pooling/`
2. **Implement** `GetSeedClient()` returning authenticated `ServiceClient`
3. **Implement** `InvalidateSeed()` to clear cached client
4. **Pass** to pool constructor in sources list

**Example skeleton:**

```csharp
public class MyConnectionSource : IConnectionSource
{
    public string Name => "my-source";
    public int MaxPoolSize => 52;

    public ServiceClient GetSeedClient()
    {
        // Create and return authenticated client
    }

    public void InvalidateSeed()
    {
        // Clear cached client for re-auth
    }
}
```

### Adding a New Selection Strategy

1. **Implement** `IConnectionSelectionStrategy` in `src/PPDS.Dataverse/Pooling/Strategies/`
2. **Add enum value** to `ConnectionSelectionStrategy`
3. **Add case** to `CreateSelectionStrategy()` factory in `DataverseConnectionPool`

---

## Configuration

| Setting | Type | Required | Default | Description |
|---------|------|----------|---------|-------------|
| `Enabled` | bool | No | true | Enable/disable pooling |
| `MaxPoolSize` | int | No | 0 (use DOP) | Fixed pool size or 0 for server-recommended |
| `MaxRetryAfterTolerance` | TimeSpan? | No | null | Max throttle wait (null = wait forever) |
| `AcquireTimeout` | TimeSpan | No | 120s | Max wait for pool slot |
| `MaxIdleTime` | TimeSpan | No | 5 min | Evict connections idle longer |
| `MaxLifetime` | TimeSpan | No | 60 min | Evict connections older than |
| `DisableAffinityCookie` | bool | No | true | Disable for load distribution |
| `SelectionStrategy` | enum | No | ThrottleAware | Connection selection algorithm |
| `ValidationInterval` | TimeSpan | No | 1 min | Background validation frequency |
| `EnableValidation` | bool | No | true | Enable background validation |
| `ValidateOnCheckout` | bool | No | true | Check health before returning |
| `MaxConnectionRetries` | int | No | 2 | Retries for auth/connection failures |

---

## Testing

### Acceptance Criteria

- [ ] Pool reuses connections instead of creating new ones
- [ ] Throttled connections are tracked and avoided
- [ ] Multiple sources distribute load correctly
- [ ] Dispose returns connection to pool
- [ ] Invalid connections are evicted, not returned
- [ ] Seed invalidation triggers re-authentication

### Edge Cases

| Scenario | Input | Expected Output |
|----------|-------|-----------------|
| Single source throttled | 2 sources, 1 throttled | Route to non-throttled |
| All sources throttled | 2 sources, both throttled | Wait for shortest expiry |
| Pool exhausted | Max capacity reached | `PoolExhaustedException` after timeout |
| Dispose called twice | Double dispose | No-op (idempotent) |
| Token expired | Auth failure on operation | Mark invalid, invalidate seed |

### Test Examples

```csharp
[Fact]
public async Task GetClientAsync_ReturnsToPool_OnDispose()
{
    var source = new TestConnectionSource();
    await using var pool = new DataverseConnectionPool([source], options);

    var client1 = await pool.GetClientAsync();
    var id1 = client1.ConnectionId;
    await client1.DisposeAsync();

    var client2 = await pool.GetClientAsync();
    Assert.Equal(id1, client2.ConnectionId); // Same connection reused
}

[Fact]
public async Task ThrottleAwareStrategy_SkipsThrottledConnection()
{
    var tracker = new ThrottleTracker();
    tracker.RecordThrottle("source-a", TimeSpan.FromMinutes(5));

    var strategy = new ThrottleAwareStrategy();
    var selected = strategy.SelectConnection(
        [CreateConnection("source-a"), CreateConnection("source-b")],
        tracker,
        new Dictionary<string, int>());

    Assert.Equal("source-b", selected);
}
```

---

## Related Specs

- [architecture.md](./architecture.md) - Cross-cutting patterns (error handling, DI)
- [bulk-operations.md](./bulk-operations.md) - Uses pool for parallel bulk API calls
- [authentication.md](./authentication.md) - Credential providers create connection sources

---

## Roadmap

- Adaptive pool sizing based on server load signals
- Connection health metrics exposed via OpenTelemetry
- Warm-up hints for predictable traffic patterns
