# Specification: Throttle Detection and Intelligent Routing

**Status:** Draft
**Author:** Claude Code
**Date:** 2025-12-22
**Priority:** High

---

## Problem Statement

The PPDS.Dataverse SDK has infrastructure for throttle-aware connection routing (`ThrottleTracker`, `ThrottleAwareStrategy`, `ServiceProtectionException`) but this infrastructure is not wired up. The `RecordThrottle()` method is never called, making `ThrottleAwareStrategy` non-functional.

When service protection limits are hit:
- The SDK doesn't detect the throttle event
- The SDK doesn't record which connection was throttled
- The SDK doesn't route future requests away from throttled connections
- Operators have no visibility into throttling behavior

This defeats the purpose of multi-user connection pooling, where the goal is to route away from throttled Application Users to maximize throughput.

---

## Microsoft's Guidance

### Service Protection Limits (Per User, Per 5-Minute Sliding Window)

| Limit | Threshold | Error Code |
|-------|-----------|------------|
| Requests | 6,000 | `-2147015902` (`0x80072322`) |
| Execution time | 20 minutes (1,200,000 ms) | `-2147015903` (`0x80072321`) |
| Concurrent requests | 52 | `-2147015898` (`0x80072326`) |

### How Throttle Errors Are Returned

**SDK (.NET):**
```csharp
// Thrown as FaultException<OrganizationServiceFault>
// Retry-After is in ErrorDetails collection
TimeSpan retryAfter = (TimeSpan)fault.ErrorDetails["Retry-After"];
int errorCode = fault.ErrorCode;
```

### Microsoft's Recommended Approach

From [Service protection API limits](https://learn.microsoft.com/en-us/power-apps/developer/data-platform/api-limits):

> "Backing off requests using the Retry-After delay is the fastest way to recover from throttling."

> "If you aren't getting some service protection limit errors, you haven't maximized your application's capability."

**Key insight:** Throttling is expected at maximum throughput. The goal is not to avoid throttling but to handle it correctly.

### References

- [Service protection API limits](https://learn.microsoft.com/en-us/power-apps/developer/data-platform/api-limits)
- [Retry operations](https://learn.microsoft.com/en-us/power-apps/developer/data-platform/api-limits#retry-operations)
- [Optimize performance for bulk operations](https://learn.microsoft.com/en-us/power-apps/developer/data-platform/optimize-performance-create-update)

---

## Current State

### Existing Components

| Component | Location | Status |
|-----------|----------|--------|
| `ServiceProtectionException` | `Resilience/ServiceProtectionException.cs` | Complete - has error codes, `IsServiceProtectionError()` |
| `ThrottleTracker` | `Resilience/ThrottleTracker.cs` | Complete - can record throttles, check if throttled |
| `IThrottleTracker` | `Resilience/IThrottleTracker.cs` | Complete - interface defined |
| `ThrottleState` | `Resilience/ThrottleState.cs` | Complete - tracks expiry |
| `ThrottleAwareStrategy` | `Pooling/Strategies/ThrottleAwareStrategy.cs` | Complete - routes away from throttled |
| `PoolStatistics` | `Pooling/PoolStatistics.cs` | Partial - has `ThrottledConnections` but not populated |

### What's Missing

1. **Detection:** `BulkOperationExecutor` catches `OrganizationServiceFault` for `Plugin.BulkApiErrorDetails` but doesn't check for service protection errors.

2. **Recording:** `RecordThrottle()` is never called anywhere in the codebase.

3. **Retry Logic:** No retry-after-throttle logic in `BulkOperationExecutor`.

4. **Statistics:** `PoolStatistics.ThrottledConnections` is not populated.

---

## Proposed Solution

### Architecture

```
┌─────────────────────────────────────────────────────────────────────┐
│                    BulkOperationExecutor                             │
├─────────────────────────────────────────────────────────────────────┤
│  ExecuteBatchAsync()                                                 │
│  ┌─────────────────────────────────────────────────────────────┐    │
│  │ 1. Get connection from pool                                  │    │
│  │ 2. Execute request                                           │    │
│  │ 3. On success → return result                                │    │
│  │ 4. On FaultException:                                        │    │
│  │    a. Check IsServiceProtectionError(errorCode)              │    │
│  │    b. If YES:                                                 │    │
│  │       - Extract Retry-After from ErrorDetails                │    │
│  │       - Call throttleTracker.RecordThrottle()                │    │
│  │       - If other connections available → retry immediately   │    │
│  │       - If all throttled → wait shortest expiry → retry      │    │
│  │    c. If NO:                                                  │    │
│  │       - Handle as regular error                              │    │
│  └─────────────────────────────────────────────────────────────┘    │
└─────────────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────────────────┐
│                    IThrottleTracker                                  │
├─────────────────────────────────────────────────────────────────────┤
│  RecordThrottle(connectionName, retryAfter)                         │
│  IsThrottled(connectionName) → bool                                  │
│  GetThrottleExpiry(connectionName) → DateTime?                       │
│  TotalThrottleEvents → long                                          │
└─────────────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────────────────┐
│                    ThrottleAwareStrategy                             │
├─────────────────────────────────────────────────────────────────────┤
│  SelectConnection():                                                 │
│  - Filter out connections where IsThrottled() == true                │
│  - If none available, return one with shortest expiry                │
│  - Return least-recently-used among available                        │
└─────────────────────────────────────────────────────────────────────┘
```

### Retry Strategy

**Priority Order:**
1. **Try a different connection immediately** - If AppUser1 is throttled but AppUser2 is available, switch immediately without waiting.
2. **Wait only when all connections are throttled** - Find the shortest `Retry-After` across all connections and wait that duration.
3. **Respect maximum retries** - Don't retry indefinitely. After N attempts, throw `ServiceProtectionException` to let the consumer decide.

**Why this order:**
- Maximizes throughput by using available quota immediately
- Only waits when absolutely necessary
- Prevents infinite retry loops

---

## Implementation Details

### 1. Add IThrottleTracker to BulkOperationExecutor

**File:** `BulkOperations/BulkOperationExecutor.cs`

```csharp
public sealed class BulkOperationExecutor : IBulkOperationExecutor
{
    private readonly IDataverseConnectionPool _connectionPool;
    private readonly IThrottleTracker _throttleTracker;  // ADD
    private readonly DataverseOptions _options;
    private readonly ILogger<BulkOperationExecutor> _logger;

    public BulkOperationExecutor(
        IDataverseConnectionPool connectionPool,
        IThrottleTracker throttleTracker,  // ADD
        IOptions<DataverseOptions> options,
        ILogger<BulkOperationExecutor> logger)
    {
        _connectionPool = connectionPool ?? throw new ArgumentNullException(nameof(connectionPool));
        _throttleTracker = throttleTracker ?? throw new ArgumentNullException(nameof(throttleTracker));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }
}
```

### 2. Add Throttle Detection Helper

**File:** `BulkOperations/BulkOperationExecutor.cs`

```csharp
/// <summary>
/// Checks if an exception is a service protection throttle and extracts Retry-After.
/// </summary>
/// <param name="exception">The exception to check.</param>
/// <param name="retryAfter">The Retry-After duration if throttled.</param>
/// <param name="errorCode">The error code if throttled.</param>
/// <returns>True if this is a service protection error.</returns>
private static bool TryGetThrottleInfo(
    Exception exception,
    out TimeSpan retryAfter,
    out int errorCode)
{
    retryAfter = TimeSpan.Zero;
    errorCode = 0;

    if (exception is not FaultException<OrganizationServiceFault> faultEx)
    {
        return false;
    }

    var fault = faultEx.Detail;
    errorCode = fault.ErrorCode;

    if (!ServiceProtectionException.IsServiceProtectionError(errorCode))
    {
        return false;
    }

    // Extract Retry-After from ErrorDetails
    if (fault.ErrorDetails.TryGetValue("Retry-After", out var retryAfterObj)
        && retryAfterObj is TimeSpan retryAfterSpan)
    {
        retryAfter = retryAfterSpan;
    }
    else
    {
        // Fallback if Retry-After not provided (shouldn't happen, but be safe)
        retryAfter = TimeSpan.FromSeconds(30);
        _logger.LogWarning(
            "Service protection error without Retry-After. Using fallback: {Fallback}s",
            retryAfter.TotalSeconds);
    }

    return true;
}
```

### 3. Add Throttle-Aware Execution Wrapper

**File:** `BulkOperations/BulkOperationExecutor.cs`

```csharp
/// <summary>
/// Executes an operation with throttle detection, recording, and intelligent retry.
/// </summary>
private async Task<T> ExecuteWithThrottleHandlingAsync<T>(
    Func<IDataverseClient, CancellationToken, Task<T>> operation,
    int maxRetries,
    CancellationToken cancellationToken)
{
    var attempts = 0;
    Exception? lastException = null;

    while (attempts < maxRetries)
    {
        attempts++;
        IDataverseClient? client = null;
        string? connectionName = null;

        try
        {
            client = await _connectionPool.GetClientAsync(cancellationToken: cancellationToken);
            connectionName = client.ConnectionName;

            return await operation(client, cancellationToken);
        }
        catch (Exception ex) when (TryGetThrottleInfo(ex, out var retryAfter, out var errorCode))
        {
            lastException = ex;

            // Record the throttle event
            if (connectionName != null)
            {
                _throttleTracker.RecordThrottle(connectionName, retryAfter);
            }

            _logger.LogWarning(
                "Service protection limit hit. Connection: {Connection}, ErrorCode: {ErrorCode}, " +
                "RetryAfter: {RetryAfter}, Attempt: {Attempt}/{MaxRetries}",
                connectionName, errorCode, retryAfter, attempts, maxRetries);

            // Check if other connections are available
            if (HasAvailableConnections())
            {
                _logger.LogDebug("Other connections available. Retrying immediately on different connection.");
                continue; // Retry immediately with different connection
            }

            // All connections throttled - wait for shortest expiry
            var waitTime = GetShortestThrottleExpiry();
            if (waitTime > TimeSpan.Zero && attempts < maxRetries)
            {
                _logger.LogInformation(
                    "All connections throttled. Waiting {WaitTime} before retry.",
                    waitTime);
                await Task.Delay(waitTime, cancellationToken);
            }
        }
        finally
        {
            if (client is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync();
            }
            else if (client is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }

    // Max retries exceeded
    throw new ServiceProtectionException(
        "unknown",
        TimeSpan.Zero,
        0,
        lastException ?? new InvalidOperationException("Max retries exceeded"));
}

/// <summary>
/// Checks if any connections are available (not throttled).
/// </summary>
private bool HasAvailableConnections()
{
    // This requires access to connection names from the pool
    // Implementation depends on pool exposing this information
    var stats = _connectionPool.Statistics;
    return stats.ThrottledConnections < stats.TotalConnections;
}

/// <summary>
/// Gets the shortest time until a throttled connection becomes available.
/// </summary>
private TimeSpan GetShortestThrottleExpiry()
{
    // This requires IThrottleTracker to expose GetShortestExpiry()
    // Or iterate through connections and check each
    // For now, return a reasonable default
    return TimeSpan.FromSeconds(30);
}
```

### 4. Update IThrottleTracker Interface

**File:** `Resilience/IThrottleTracker.cs`

Add methods to support the retry logic:

```csharp
/// <summary>
/// Gets the number of currently throttled connections.
/// </summary>
int ThrottledConnectionCount { get; }

/// <summary>
/// Gets all currently throttled connection names.
/// </summary>
IReadOnlyCollection<string> ThrottledConnections { get; }

/// <summary>
/// Gets the shortest time until any throttled connection expires.
/// Returns TimeSpan.Zero if no connections are throttled.
/// </summary>
TimeSpan GetShortestExpiry();
```

### 5. Update ThrottleTracker Implementation

**File:** `Resilience/ThrottleTracker.cs`

```csharp
public int ThrottledConnectionCount
{
    get
    {
        CleanupExpired();
        return _throttleStates.Count;
    }
}

public IReadOnlyCollection<string> ThrottledConnections
{
    get
    {
        CleanupExpired();
        return _throttleStates.Keys.ToList().AsReadOnly();
    }
}

public TimeSpan GetShortestExpiry()
{
    CleanupExpired();

    if (_throttleStates.IsEmpty)
    {
        return TimeSpan.Zero;
    }

    var now = DateTime.UtcNow;
    var shortest = _throttleStates.Values
        .Select(s => s.ExpiresAt - now)
        .Where(t => t > TimeSpan.Zero)
        .DefaultIfEmpty(TimeSpan.Zero)
        .Min();

    return shortest;
}

private void CleanupExpired()
{
    var now = DateTime.UtcNow;
    var expired = _throttleStates
        .Where(kvp => kvp.Value.ExpiresAt <= now)
        .Select(kvp => kvp.Key)
        .ToList();

    foreach (var key in expired)
    {
        _throttleStates.TryRemove(key, out _);
    }
}
```

### 6. Update PoolStatistics

**File:** `Pooling/PoolStatistics.cs`

Ensure `ThrottledConnections` is populated from `IThrottleTracker`:

```csharp
public class PoolStatistics
{
    // ... existing properties ...

    /// <summary>
    /// Gets the number of currently throttled connections.
    /// </summary>
    public int ThrottledConnections { get; init; }

    /// <summary>
    /// Gets the total number of throttle events since pool creation.
    /// </summary>
    public long TotalThrottleEvents { get; init; }
}
```

### 7. Update DI Registration

**File:** `DependencyInjection/ServiceCollectionExtensions.cs`

Ensure `IThrottleTracker` is registered and injected:

```csharp
services.AddSingleton<IThrottleTracker, ThrottleTracker>();
// Ensure BulkOperationExecutor receives IThrottleTracker
```

### 8. Add Configuration Options

**File:** `Resilience/ResilienceOptions.cs`

```csharp
public class ResilienceOptions
{
    /// <summary>
    /// Maximum number of retry attempts on service protection errors.
    /// Default: 3
    /// </summary>
    public int MaxThrottleRetries { get; set; } = 3;

    /// <summary>
    /// Whether to enable throttle tracking for connection routing.
    /// Default: true
    /// </summary>
    public bool EnableThrottleTracking { get; set; } = true;

    /// <summary>
    /// Fallback Retry-After duration when not provided by server.
    /// Default: 30 seconds
    /// </summary>
    public TimeSpan FallbackRetryAfter { get; set; } = TimeSpan.FromSeconds(30);
}
```

---

## Integration Points

### Where to Add Throttle Handling

| Method | File | Change Required |
|--------|------|-----------------|
| `ExecuteCreateMultipleBatchAsync` | `BulkOperationExecutor.cs` | Wrap with throttle handling |
| `ExecuteUpdateMultipleBatchAsync` | `BulkOperationExecutor.cs` | Wrap with throttle handling |
| `ExecuteUpsertMultipleBatchAsync` | `BulkOperationExecutor.cs` | Wrap with throttle handling |
| `ExecuteElasticDeleteBatchAsync` | `BulkOperationExecutor.cs` | Wrap with throttle handling |
| `ExecuteStandardDeleteBatchAsync` | `BulkOperationExecutor.cs` | Wrap with throttle handling |

### Connection Name Exposure

The `IDataverseClient` interface needs to expose the connection name so we can record throttles against the correct connection:

```csharp
public interface IDataverseClient
{
    // ... existing members ...

    /// <summary>
    /// Gets the name of the connection this client belongs to.
    /// </summary>
    string ConnectionName { get; }
}
```

---

## Testing Requirements

### Unit Tests

1. **Throttle Detection**
   - Verify `TryGetThrottleInfo` correctly identifies service protection errors
   - Verify `Retry-After` extraction from `ErrorDetails`
   - Verify fallback when `Retry-After` is missing

2. **Throttle Recording**
   - Verify `RecordThrottle` is called when throttle detected
   - Verify correct connection name and `Retry-After` are recorded

3. **Retry Logic**
   - Verify immediate retry when other connections available
   - Verify wait-and-retry when all connections throttled
   - Verify max retries is respected

4. **Statistics**
   - Verify `ThrottledConnections` count is accurate
   - Verify `TotalThrottleEvents` increments correctly

### Integration Tests

1. **Simulated Throttling**
   - Mock `OrganizationServiceFault` with service protection error codes
   - Verify routing switches to different connection
   - Verify timing of retries matches `Retry-After`

2. **Multi-Connection Routing**
   - Configure 3 Application Users
   - Throttle User1
   - Verify requests route to User2 and User3
   - Un-throttle User1
   - Verify User1 receives requests again

### Load Testing

1. **Push to Throttle**
   - Run bulk operations until throttling occurs
   - Verify throttle events are logged
   - Verify statistics show throttle counts
   - Verify throughput is maximized (routes away from throttled)

---

## Acceptance Criteria

1. [ ] Service protection errors (all 3 codes) are detected
2. [ ] `Retry-After` is extracted from `ErrorDetails`
3. [ ] `RecordThrottle()` is called for each throttle event
4. [ ] `ThrottleAwareStrategy` routes away from throttled connections
5. [ ] Immediate retry occurs when other connections available
6. [ ] Wait-and-retry occurs when all connections throttled
7. [ ] Max retries prevents infinite loops
8. [ ] `PoolStatistics.ThrottledConnections` shows current count
9. [ ] `PoolStatistics.TotalThrottleEvents` shows total count
10. [ ] Logging includes connection name, error code, and `Retry-After`
11. [ ] Throttle handling is configurable via `ResilienceOptions`
12. [ ] All unit tests pass
13. [ ] Integration tests verify multi-connection routing
14. [ ] Load test verifies throttle handling under pressure

---

## Risks and Mitigations

| Risk | Mitigation |
|------|------------|
| Double-retry (ServiceClient + ours) | ServiceClient's internal retry is for transient failures; service protection errors bubble up |
| Infinite retry loops | `MaxThrottleRetries` configuration with sensible default |
| Clock skew with `Retry-After` | Add small buffer (1-2 seconds) to wait time |
| Memory growth in `ThrottleTracker` | `CleanupExpired()` removes old entries |
| Race conditions in throttle state | Use `ConcurrentDictionary` (already implemented) |

---

## Future Enhancements

1. **Proactive Throttle Detection** - Track request rates and slow down before hitting limits
2. **Throttle Prediction** - Use historical data to predict when throttling will occur
3. **Adaptive Parallelism** - Automatically reduce `MaxDegreeOfParallelism` when throttling detected
4. **Circuit Breaker** - If a connection is repeatedly throttled, exclude it temporarily
