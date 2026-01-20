# PPDS.Dataverse: Throttle Management

## Overview

The Throttle Management subsystem tracks, detects, and handles Dataverse service protection (429) errors. It provides a centralized throttle state tracker, detection wrappers for operations, and specialized exceptions for both throttle and authentication errors. The subsystem enables the connection pool to intelligently route requests away from throttled connections.

## Public API

### Interfaces

| Interface | Purpose |
|-----------|---------|
| `IThrottleTracker` | Tracks throttle state across connections |

### Classes

| Class | Purpose |
|-------|---------|
| `ThrottleTracker` | Thread-safe throttle state tracking implementation |
| `ThrottleDetector` | Wraps operations to detect throttle and auth errors |
| `AuthenticationErrorDetector` | Static utilities for detecting auth failures |
| `ServiceProtectionException` | Exception for service protection limit errors |
| `DataverseAuthenticationException` | Exception for auth/permission failures |

### DTOs/Models

| Type | Purpose |
|------|---------|
| `ThrottleState` | State of a throttled connection (timing info) |
| `ResilienceOptions` | Configuration for retry and throttle behavior |

## Behaviors

### Throttle State Tracking

The `ThrottleTracker` maintains per-connection throttle state:

1. **Recording**: When a 429 error occurs, `RecordThrottle(connectionName, retryAfter)` stores:
   - Connection name
   - Throttle timestamp
   - Expiry timestamp (now + retryAfter)
   - RetryAfter duration
2. **Querying**: `IsThrottled(connectionName)` returns true if throttle is active
3. **Expiry**: Throttle states automatically expire; queries clean up expired entries
4. **Statistics**: Tracks total throttle events and cumulative backoff time

### Throttle Detection

The `ThrottleDetector` wraps operations to intercept throttle and auth errors:

```
try {
    operation()
} catch (throttle) {
    invoke callback(connectionName, retryAfter)
    rethrow
} catch (auth failure) {
    wrap in DataverseAuthenticationException
    throw
}
```

### Service Protection Error Codes

| Error Code | Name | Description |
|------------|------|-------------|
| `-2147015902` (0x80072322) | RequestsExceeded | More than 6,000 requests in 5 minutes |
| `-2147015903` (0x80072321) | ExecutionTimeExceeded | More than 20 minutes combined execution time |
| `-2147015898` (0x80072326) | ConcurrentRequestsExceeded | More than 52 concurrent requests |

### Authentication Error Detection

The `AuthenticationErrorDetector` distinguishes between:

| Type | Detection Criteria | Requires Re-auth |
|------|-------------------|------------------|
| Token Failure | `MessageSecurityException`, HTTP 401, "token expired" in message, AADSTS errors | Yes |
| Permission Failure | Error codes -2147180286, -2147204720, -2147180285 | No |

### Lifecycle

- **Initialization**: `ThrottleTracker` starts empty; registered via DI
- **Operation**: States accumulate as throttles occur; expired states cleaned on access
- **Cleanup**: No persistent resources; states exist only in memory

## Edge Cases

| Scenario | Behavior | Notes |
|----------|----------|-------|
| No Retry-After header | Uses fallback (30 seconds) | Logged as warning |
| Multiple throttles same connection | Latest state overwrites | Extends throttle period |
| Query expired throttle | Returns false, cleans up | Lazy expiration cleanup |
| Empty connection name | `IsThrottled` returns false | Defensive null handling |
| Concurrent throttle updates | Thread-safe via `ConcurrentDictionary` | No lock contention |

## Error Handling

| Exception | Condition | Recovery |
|-----------|-----------|----------|
| `ServiceProtectionException` | API request hit service limit | Wait for `RetryAfter`, retry |
| `DataverseAuthenticationException` (RequiresReauthentication=true) | Token expired/invalid | Re-authenticate, retry |
| `DataverseAuthenticationException` (RequiresReauthentication=false) | User lacks privilege | Check security roles |

## Dependencies

- **Internal**: None (leaf component)
- **External**:
  - `Microsoft.Xrm.Sdk` (OrganizationServiceFault)
  - `Microsoft.Extensions.Logging.Abstractions`

## Configuration

### ResilienceOptions

| Setting | Type | Default | Description |
|---------|------|---------|-------------|
| `EnableThrottleTracking` | bool | true | Enable throttle state tracking |
| `DefaultThrottleCooldown` | TimeSpan | 5min | Default wait when no Retry-After |
| `MaxRetryCount` | int | 3 | Max retries for transient failures |
| `RetryDelay` | TimeSpan | 1s | Base delay between retries |
| `UseExponentialBackoff` | bool | true | Use exponential backoff for retries |
| `MaxRetryDelay` | TimeSpan | 30s | Maximum delay between retries |

### Fallback Values

| Value | Usage |
|-------|-------|
| 30 seconds | Default Retry-After when not provided by server |

## Thread Safety

- **ThrottleTracker**:
  - Uses `ConcurrentDictionary<string, ThrottleState>` for state storage
  - `Interlocked` operations for statistics counters
  - All public methods are thread-safe
- **ThrottleDetector**: Stateless after construction; thread-safe
- **AuthenticationErrorDetector**: Static class with pure functions; thread-safe
- **Guarantees**: Multiple concurrent operations can safely share a tracker

## Integration Points

### Connection Pool Integration

The pool uses `IThrottleTracker` to:
1. **Pre-flight check**: Before executing a batch, check `IsThrottled(connectionName)`
2. **Selection strategy**: `ThrottleAwareStrategy` queries tracker to skip throttled connections
3. **Wait calculation**: `GetShortestExpiry()` determines how long to wait when all throttled
4. **Recording**: `PooledClient` calls `RecordThrottle` via callback when error detected

### Bulk Operations Integration

`BulkOperationExecutor` uses throttle management for:
1. **Pre-flight guard**: Avoids "in-flight avalanche" on throttled connections
2. **Error handling**: Detects service protection errors, invokes infinite retry
3. **Connection switching**: On throttle, disposes client and gets new one

## Detection Patterns

### Retry-After Extraction

The `ThrottleDetector` extracts Retry-After from `OrganizationServiceFault.ErrorDetails`:

```csharp
if (fault.ErrorDetails.TryGetValue("Retry-After", out var obj))
{
    return obj switch {
        TimeSpan ts => ts,
        int seconds => TimeSpan.FromSeconds(seconds),
        double seconds => TimeSpan.FromSeconds(seconds),
        _ => FallbackRetryAfter  // 30 seconds
    };
}
```

### Authentication Failure Patterns

| Pattern | Detection Method |
|---------|------------------|
| Token not sent | `MessageSecurityException` |
| Token rejected | HTTP 401 in inner `HttpRequestException` |
| Token expired | "token" + "expired" in fault message |
| Credential invalid | "credential" + "invalid/expired" in message |
| Azure AD error | "aadsts" in fault message |
| No privilege | Error code -2147180286 |
| User disabled | Error code -2147204720 |
| Access denied | Error code -2147180285 |

## Related

- [ADR-0003: Throttle-Aware Connection Selection](../../docs/adr/0003_THROTTLE_AWARE_SELECTION.md)
- [Connection Pooling spec](./01-connection-pooling.md) - Uses tracker for selection
- [Bulk Operations spec](./02-bulk-operations.md) - Uses tracker for pre-flight checks

## Source Files

| File | Purpose |
|------|---------|
| `src/PPDS.Dataverse/Resilience/IThrottleTracker.cs` | Throttle tracker interface |
| `src/PPDS.Dataverse/Resilience/ThrottleTracker.cs` | Throttle tracker implementation |
| `src/PPDS.Dataverse/Resilience/ThrottleDetector.cs` | Operation wrapper for detection |
| `src/PPDS.Dataverse/Resilience/ThrottleState.cs` | Throttle state DTO |
| `src/PPDS.Dataverse/Resilience/ServiceProtectionException.cs` | Throttle exception |
| `src/PPDS.Dataverse/Resilience/AuthenticationErrorDetector.cs` | Auth failure detection |
| `src/PPDS.Dataverse/Resilience/DataverseAuthenticationException.cs` | Auth exception |
| `src/PPDS.Dataverse/Resilience/ResilienceOptions.cs` | Configuration options |
