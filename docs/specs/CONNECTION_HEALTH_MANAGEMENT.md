# Specification: Connection Health Management and Failure Recovery

**Status:** Draft
**Author:** Claude Code
**Date:** 2025-12-22
**Priority:** High

---

## Problem Statement

The PPDS.Dataverse SDK needs to support "always-on" integration scenarios where connection pools run indefinitely. The current implementation has gaps:

1. **Fixed `MaxLifetime = 30min`** forces connection recycling even when connections are healthy, causing unnecessary churn.

2. **No failure detection during operations** - if a connection fails mid-operation (auth failure, network issue), the error bubbles up without recovery.

3. **No connection validation on checkout** - invalid connections can be returned from the pool.

4. **No graceful recovery** - when a connection fails, the operation fails. For enterprise migrations, we need retry-with-new-connection.

For data migrations handling millions of records, connection failures must not cause data loss. The system should detect failures, recover gracefully, and continue processing.

---

## Microsoft's Guidance

### Token Lifecycle

From [OAuth authentication](https://learn.microsoft.com/en-us/power-apps/developer/data-platform/authenticate-oauth):

- Access tokens expire in **~60-75 minutes**
- MSAL caches tokens and refreshes them automatically
- ServiceClient checks token expiry and refreshes **1 minute before expiration**

### ServiceClient Token Refresh

From [ServiceClient source](https://github.com/microsoft/PowerPlatform-DataverseServiceClient):

```csharp
// Internal logic (paraphrased)
if (token.ExpiresOn < DateTime.UtcNow.AddMinutes(1))
{
    RefreshToken();
}
```

This means ServiceClient handles token refresh internally for the happy path. The risk is when refresh **fails**.

### Authentication Patterns for Long-Running Apps

**Pattern 1: Connection String (Current)**
```csharp
var client = new ServiceClient(connectionString);
// ServiceClient manages tokens internally via MSAL
```

**Pattern 2: External Token Provider**
```csharp
var client = new ServiceClient(
    new ConnectionOptions
    {
        ServiceUri = new Uri("https://org.crm.dynamics.com"),
        AuthenticationType = AuthenticationType.ExternalTokenManagement,
        AccessTokenProviderFunctionAsync = async (uri) => await GetTokenAsync(uri)
    });
```

**Note:** Pattern 2 has known issues - [GitHub #377](https://github.com/microsoft/PowerPlatform-DataverseServiceClient/issues/377) reports excessive token acquisition calls.

### References

- [OAuth authentication](https://learn.microsoft.com/en-us/power-apps/developer/data-platform/authenticate-oauth)
- [ServiceClient Class](https://learn.microsoft.com/en-us/dotnet/api/microsoft.powerplatform.dataverse.client.serviceclient)
- [ConnectionOptions](https://learn.microsoft.com/en-us/dotnet/api/microsoft.powerplatform.dataverse.client.model.connectionoptions)
- [ServiceClient Source](https://github.com/microsoft/PowerPlatform-DataverseServiceClient)

---

## Current State

### Connection Pool Configuration

**File:** `Pooling/ConnectionPoolOptions.cs`

| Setting | Current Default | Purpose |
|---------|-----------------|---------|
| `MaxPoolSize` | 50 | Maximum connections |
| `MinPoolSize` | 5 | Minimum idle connections |
| `MaxIdleTime` | 5 minutes | Evict idle connections |
| `MaxLifetime` | 30 minutes | Force recycle all connections |
| `AcquireTimeout` | 30 seconds | Timeout waiting for connection |

### Current Lifecycle

```
Connection Created
       │
       ▼
   [In Pool]
       │
       ├──── Age > MaxLifetime (30min) ──────► Disposed
       │
       ├──── Idle > MaxIdleTime (5min) ──────► Disposed
       │
       └──── Checked Out ──────► Used ──────► Returned to Pool
```

### What's Missing

1. **Validation on checkout** - Pool returns connections without checking `IsReady`
2. **Failure recovery during operations** - Exceptions bubble up without retry
3. **Auth failure handling** - No special handling for authentication errors
4. **Health monitoring** - No proactive health checks on idle connections
5. **Connection invalidation** - No way to mark a connection as bad

---

## Proposed Solution

### Architecture

```
┌─────────────────────────────────────────────────────────────────────┐
│                    Connection Pool                                   │
├─────────────────────────────────────────────────────────────────────┤
│                                                                      │
│  GetClientAsync()                                                    │
│  ┌─────────────────────────────────────────────────────────────┐    │
│  │ 1. Get connection from internal pool                         │    │
│  │ 2. Validate connection:                                      │    │
│  │    - IsReady == true?                                        │    │
│  │    - Age < MaxLifetime?                                      │    │
│  │    - Not marked invalid?                                     │    │
│  │ 3. If invalid:                                               │    │
│  │    - Dispose connection                                      │    │
│  │    - Get/create another                                      │    │
│  │    - Repeat validation                                       │    │
│  │ 4. Return healthy connection                                 │    │
│  └─────────────────────────────────────────────────────────────┘    │
│                                                                      │
│  ReturnToPool(connection)                                            │
│  ┌─────────────────────────────────────────────────────────────┐    │
│  │ 1. Check if marked invalid                                   │    │
│  │ 2. If invalid → Dispose, don't return                        │    │
│  │ 3. If valid → Return to pool                                 │    │
│  └─────────────────────────────────────────────────────────────┘    │
│                                                                      │
│  MarkInvalid(connection)                                             │
│  ┌─────────────────────────────────────────────────────────────┐    │
│  │ 1. Mark connection as invalid                                │    │
│  │ 2. Log the reason                                            │    │
│  │ 3. Increment failure counter                                 │    │
│  │ 4. On next return/checkout → Will be disposed                │    │
│  └─────────────────────────────────────────────────────────────┘    │
│                                                                      │
└─────────────────────────────────────────────────────────────────────┘
```

### Failure Recovery Flow

```
┌─────────────────────────────────────────────────────────────────────┐
│                    BulkOperationExecutor                             │
├─────────────────────────────────────────────────────────────────────┤
│  ExecuteWithFailureRecoveryAsync()                                   │
│  ┌─────────────────────────────────────────────────────────────┐    │
│  │ 1. Get connection from pool                                  │    │
│  │ 2. Try execute operation                                     │    │
│  │ 3. On success → return result                                │    │
│  │ 4. On auth failure:                                          │    │
│  │    a. Mark connection as invalid                             │    │
│  │    b. Dispose connection (don't return to pool)              │    │
│  │    c. Get new connection                                     │    │
│  │    d. Retry operation                                        │    │
│  │ 5. On connection failure:                                    │    │
│  │    a. Same as auth failure                                   │    │
│  │ 6. On other failure:                                         │    │
│  │    a. Return connection to pool normally                     │    │
│  │    b. Throw exception to caller                              │    │
│  └─────────────────────────────────────────────────────────────┘    │
└─────────────────────────────────────────────────────────────────────┘
```

---

## Implementation Details

### 1. Add Connection Validation to Pool

**File:** `Pooling/DataverseConnectionPool.cs`

```csharp
/// <summary>
/// Validates that a connection is healthy and usable.
/// </summary>
/// <param name="client">The client to validate.</param>
/// <returns>True if the connection is healthy.</returns>
private bool IsConnectionHealthy(PooledDataverseClient client)
{
    // Check if ServiceClient reports ready
    if (!client.IsReady)
    {
        _logger.LogDebug(
            "Connection not ready. ConnectionId: {ConnectionId}",
            client.ConnectionId);
        return false;
    }

    // Check age against MaxLifetime
    var age = DateTime.UtcNow - client.CreatedAt;
    if (age > _options.Pool.MaxLifetime)
    {
        _logger.LogDebug(
            "Connection exceeded max lifetime. ConnectionId: {ConnectionId}, Age: {Age}",
            client.ConnectionId, age);
        return false;
    }

    // Check if marked as invalid
    if (client.IsInvalid)
    {
        _logger.LogDebug(
            "Connection marked invalid. ConnectionId: {ConnectionId}",
            client.ConnectionId);
        return false;
    }

    return true;
}

public async ValueTask<IDataverseClient> GetClientAsync(
    DataverseClientOptions? options = null,
    CancellationToken cancellationToken = default)
{
    var maxAttempts = 3; // Prevent infinite loops
    var attempts = 0;

    while (attempts < maxAttempts)
    {
        attempts++;
        var client = await GetClientFromPoolAsync(options, cancellationToken);

        if (IsConnectionHealthy(client))
        {
            return WrapClient(client, options);
        }

        // Unhealthy - dispose and try again
        _logger.LogInformation(
            "Connection failed health check. Disposing and getting another. " +
            "ConnectionId: {ConnectionId}, Attempt: {Attempt}",
            client.ConnectionId, attempts);

        await DisposeConnectionAsync(client);
    }

    throw new PoolExhaustedException(
        "Failed to get healthy connection after multiple attempts");
}
```

### 2. Add Connection Invalidation Support

**File:** `Pooling/PooledDataverseClient.cs`

```csharp
public class PooledDataverseClient : IDataverseClient
{
    // ... existing members ...

    /// <summary>
    /// Gets or sets whether this connection has been marked as invalid.
    /// Invalid connections will be disposed instead of returned to the pool.
    /// </summary>
    public bool IsInvalid { get; private set; }

    /// <summary>
    /// Gets the reason the connection was marked invalid, if any.
    /// </summary>
    public string? InvalidReason { get; private set; }

    /// <summary>
    /// Marks this connection as invalid. It will be disposed on return to pool.
    /// </summary>
    /// <param name="reason">The reason for invalidation.</param>
    public void MarkInvalid(string reason)
    {
        IsInvalid = true;
        InvalidReason = reason;
    }
}
```

### 3. Add IDataverseClient.MarkInvalid()

**File:** `Client/IDataverseClient.cs`

```csharp
public interface IDataverseClient : IAsyncDisposable, IDisposable
{
    // ... existing members ...

    /// <summary>
    /// Gets whether this connection has been marked as invalid.
    /// </summary>
    bool IsInvalid { get; }

    /// <summary>
    /// Marks this connection as invalid. It will not be returned to the pool.
    /// Call this when an unrecoverable error occurs (auth failure, etc.).
    /// </summary>
    /// <param name="reason">The reason for invalidation (for logging).</param>
    void MarkInvalid(string reason);
}
```

### 4. Update Return-to-Pool Logic

**File:** `Pooling/DataverseConnectionPool.cs`

```csharp
internal async Task ReturnConnectionAsync(PooledDataverseClient client)
{
    if (client.IsInvalid)
    {
        _logger.LogInformation(
            "Connection marked invalid, disposing instead of returning. " +
            "ConnectionId: {ConnectionId}, Reason: {Reason}",
            client.ConnectionId, client.InvalidReason);

        await DisposeConnectionAsync(client);
        Interlocked.Increment(ref _invalidConnectionCount);
        return;
    }

    // ... existing return-to-pool logic ...
}
```

### 5. Add Auth Failure Detection

**File:** `BulkOperations/BulkOperationExecutor.cs`

```csharp
/// <summary>
/// Checks if an exception indicates an authentication/authorization failure.
/// </summary>
private static bool IsAuthFailure(Exception exception)
{
    // Check for common auth failure patterns
    if (exception is FaultException<OrganizationServiceFault> faultEx)
    {
        var fault = faultEx.Detail;

        // Common auth error codes
        // -2147180286: Caller does not have privilege
        // -2147204720: User is disabled
        // -2147180285: AccessDenied
        var authErrorCodes = new[]
        {
            -2147180286, // No privilege
            -2147204720, // User disabled
            -2147180285, // Access denied
        };

        if (authErrorCodes.Contains(fault.ErrorCode))
        {
            return true;
        }

        // Check message for auth-related keywords
        var message = fault.Message?.ToLowerInvariant() ?? "";
        if (message.Contains("authentication") ||
            message.Contains("authorization") ||
            message.Contains("token") ||
            message.Contains("expired") ||
            message.Contains("credential"))
        {
            return true;
        }
    }

    // Check for HTTP 401/403 in inner exceptions
    if (exception.InnerException is HttpRequestException httpEx)
    {
        var message = httpEx.Message?.ToLowerInvariant() ?? "";
        if (message.Contains("401") || message.Contains("403") ||
            message.Contains("unauthorized") || message.Contains("forbidden"))
        {
            return true;
        }
    }

    return false;
}

/// <summary>
/// Checks if an exception indicates a connection failure.
/// </summary>
private static bool IsConnectionFailure(Exception exception)
{
    return exception is HttpRequestException ||
           exception is TaskCanceledException ||
           exception is OperationCanceledException ||
           exception is SocketException ||
           exception.InnerException is SocketException;
}
```

### 6. Add Failure Recovery Wrapper

**File:** `BulkOperations/BulkOperationExecutor.cs`

```csharp
/// <summary>
/// Executes an operation with automatic failure recovery.
/// On auth or connection failures, marks the connection invalid and retries with a new one.
/// </summary>
private async Task<T> ExecuteWithFailureRecoveryAsync<T>(
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

        try
        {
            client = await _connectionPool.GetClientAsync(cancellationToken: cancellationToken);
            return await operation(client, cancellationToken);
        }
        catch (Exception ex) when (IsAuthFailure(ex))
        {
            lastException = ex;

            _logger.LogWarning(
                "Authentication failure on connection {Connection}. " +
                "Marking invalid and retrying. Attempt: {Attempt}/{MaxRetries}. Error: {Error}",
                client?.ConnectionName, attempts, maxRetries, ex.Message);

            // Mark connection as invalid - it won't be returned to pool
            client?.MarkInvalid($"Auth failure: {ex.Message}");

            // Don't wait - immediately try with new connection
            continue;
        }
        catch (Exception ex) when (IsConnectionFailure(ex))
        {
            lastException = ex;

            _logger.LogWarning(
                "Connection failure on {Connection}. " +
                "Marking invalid and retrying. Attempt: {Attempt}/{MaxRetries}. Error: {Error}",
                client?.ConnectionName, attempts, maxRetries, ex.Message);

            client?.MarkInvalid($"Connection failure: {ex.Message}");
            continue;
        }
        finally
        {
            // Dispose will check IsInvalid and handle appropriately
            if (client is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync();
            }
        }
    }

    throw new DataverseConnectionException(
        "Operation failed after multiple attempts",
        lastException);
}
```

### 7. Add Health Check Configuration

**File:** `Pooling/ConnectionPoolOptions.cs`

```csharp
public class ConnectionPoolOptions
{
    // ... existing properties ...

    /// <summary>
    /// Maximum lifetime for a connection before it's recycled.
    /// Set higher for stable long-running scenarios.
    /// Default: 60 minutes (within OAuth token validity window)
    /// </summary>
    public TimeSpan MaxLifetime { get; set; } = TimeSpan.FromMinutes(60);

    /// <summary>
    /// Whether to validate connection health on checkout.
    /// When true, connections are checked for IsReady, age, and validity before being returned.
    /// Default: true
    /// </summary>
    public bool ValidateOnCheckout { get; set; } = true;

    /// <summary>
    /// Maximum number of retry attempts for auth/connection failures.
    /// Default: 2
    /// </summary>
    public int MaxConnectionRetries { get; set; } = 2;

    /// <summary>
    /// Whether to enable proactive health monitoring of idle connections.
    /// When true, a background task periodically checks idle connections.
    /// Default: true
    /// </summary>
    public bool EnableHealthMonitoring { get; set; } = true;

    /// <summary>
    /// Interval for proactive health monitoring of idle connections.
    /// Default: 5 minutes
    /// </summary>
    public TimeSpan HealthCheckInterval { get; set; } = TimeSpan.FromMinutes(5);
}
```

### 8. Add Background Health Monitor (Optional Enhancement)

**File:** `Pooling/ConnectionHealthMonitor.cs`

```csharp
/// <summary>
/// Background service that monitors connection health and removes unhealthy connections.
/// </summary>
public sealed class ConnectionHealthMonitor : BackgroundService
{
    private readonly IDataverseConnectionPool _pool;
    private readonly ConnectionPoolOptions _options;
    private readonly ILogger<ConnectionHealthMonitor> _logger;

    public ConnectionHealthMonitor(
        IDataverseConnectionPool pool,
        IOptions<ConnectionPoolOptions> options,
        ILogger<ConnectionHealthMonitor> logger)
    {
        _pool = pool;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.EnableHealthMonitoring)
        {
            _logger.LogInformation("Connection health monitoring is disabled");
            return;
        }

        _logger.LogInformation(
            "Connection health monitoring started. Interval: {Interval}",
            _options.HealthCheckInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_options.HealthCheckInterval, stoppingToken);

                // Trigger health check on pool
                // Pool will remove unhealthy idle connections
                var stats = _pool.Statistics;

                _logger.LogDebug(
                    "Health check complete. Active: {Active}, Idle: {Idle}, Invalid: {Invalid}",
                    stats.ActiveConnections,
                    stats.IdleConnections,
                    stats.InvalidConnections);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Normal shutdown
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during connection health check");
            }
        }

        _logger.LogInformation("Connection health monitoring stopped");
    }
}
```

### 9. Update PoolStatistics

**File:** `Pooling/PoolStatistics.cs`

```csharp
public class PoolStatistics
{
    // ... existing properties ...

    /// <summary>
    /// Number of connections that were invalidated due to failures.
    /// </summary>
    public long InvalidConnections { get; init; }

    /// <summary>
    /// Number of auth failures detected.
    /// </summary>
    public long AuthFailures { get; init; }

    /// <summary>
    /// Number of connection failures detected.
    /// </summary>
    public long ConnectionFailures { get; init; }

    /// <summary>
    /// Number of successful connection health checks.
    /// </summary>
    public long HealthChecksSuccess { get; init; }

    /// <summary>
    /// Number of failed connection health checks.
    /// </summary>
    public long HealthChecksFailed { get; init; }
}
```

---

## Configuration Recommendations

### For Data Migrations (Batch Jobs)

```csharp
services.AddDataverseConnectionPool(options =>
{
    options.Pool.MaxPoolSize = 50;
    options.Pool.MinPoolSize = 5;
    options.Pool.MaxLifetime = TimeSpan.FromMinutes(60);
    options.Pool.MaxIdleTime = TimeSpan.FromMinutes(10);
    options.Pool.ValidateOnCheckout = true;
    options.Pool.MaxConnectionRetries = 2;
    options.Pool.EnableHealthMonitoring = false; // Not needed for batch
});
```

### For Always-On Integrations

```csharp
services.AddDataverseConnectionPool(options =>
{
    options.Pool.MaxPoolSize = 20;
    options.Pool.MinPoolSize = 5;
    options.Pool.MaxLifetime = TimeSpan.FromMinutes(120); // Longer-lived
    options.Pool.MaxIdleTime = TimeSpan.FromMinutes(30);  // Keep connections warm
    options.Pool.ValidateOnCheckout = true;
    options.Pool.MaxConnectionRetries = 3;
    options.Pool.EnableHealthMonitoring = true;
    options.Pool.HealthCheckInterval = TimeSpan.FromMinutes(5);
});
```

---

## Error Handling Matrix

| Error Type | Detection | Action | Retry? |
|------------|-----------|--------|--------|
| Service Protection (429) | `IsServiceProtectionError()` | Record throttle, route away | Yes - different connection |
| Auth Failure | `IsAuthFailure()` | Mark invalid, dispose | Yes - new connection |
| Connection Failure | `IsConnectionFailure()` | Mark invalid, dispose | Yes - new connection |
| Business Logic Error | Fault without above patterns | Return to pool normally | No - throw to caller |
| Data Validation Error | Fault with specific codes | Return to pool normally | No - throw to caller |

---

## Testing Requirements

### Unit Tests

1. **Connection Validation**
   - Verify `IsConnectionHealthy` returns false for `IsReady == false`
   - Verify `IsConnectionHealthy` returns false for aged connections
   - Verify `IsConnectionHealthy` returns false for invalid connections

2. **Connection Invalidation**
   - Verify `MarkInvalid` sets `IsInvalid = true`
   - Verify invalid connections are disposed, not returned to pool
   - Verify `InvalidReason` is captured

3. **Auth Failure Detection**
   - Verify `IsAuthFailure` correctly identifies auth error codes
   - Verify `IsAuthFailure` detects auth-related messages

4. **Failure Recovery**
   - Verify auth failures trigger connection invalidation
   - Verify retry uses new connection
   - Verify max retries is respected

### Integration Tests

1. **Simulated Auth Failure**
   - Create mock that fails auth on first attempt
   - Verify connection is invalidated
   - Verify second attempt uses new connection
   - Verify operation succeeds on retry

2. **Connection Validation**
   - Create connection that reports `IsReady = false`
   - Verify pool gets different connection
   - Verify unhealthy connection is disposed

3. **Pool Statistics**
   - Trigger various failure types
   - Verify statistics accurately reflect failures

### Long-Running Tests

1. **Overnight Stability**
   - Run pool for 8+ hours
   - Verify connections are recycled properly
   - Verify no memory leaks
   - Verify token refresh works correctly

---

## Acceptance Criteria

1. [ ] Connections are validated on checkout (IsReady, age, validity)
2. [ ] Invalid connections are disposed, not returned to pool
3. [ ] `MarkInvalid()` method exists on `IDataverseClient`
4. [ ] Auth failures are detected and trigger connection invalidation
5. [ ] Connection failures are detected and trigger connection invalidation
6. [ ] Failed operations are retried with new connections
7. [ ] Max retries prevents infinite loops
8. [ ] `MaxLifetime` default increased to 60 minutes
9. [ ] `ValidateOnCheckout` is configurable (default true)
10. [ ] `MaxConnectionRetries` is configurable (default 2)
11. [ ] Statistics include failure counts
12. [ ] Logging includes connection ID and failure reason
13. [ ] All unit tests pass
14. [ ] Integration tests verify recovery behavior
15. [ ] Long-running test confirms stability

---

## Risks and Mitigations

| Risk | Mitigation |
|------|------------|
| False positive auth detection | Conservative error code list, logged for debugging |
| Connection validation overhead | Only validate on checkout, not continuously |
| Pool exhaustion during recovery | `MaxConnectionRetries` limits retry attempts |
| Memory leaks from invalid connections | Invalid connections are disposed immediately |
| Health monitor resource usage | Configurable interval, can be disabled |

---

## Future Enhancements

1. **Proactive Token Refresh** - Monitor token expiry and refresh before operations
2. **Circuit Breaker per Connection** - Temporarily exclude connections with repeated failures
3. **Connection Warmup** - Pre-create connections to `MinPoolSize` on startup
4. **Metrics Export** - Export pool health metrics to monitoring systems (Prometheus, App Insights)
5. **Graceful Shutdown** - Wait for in-flight operations before disposing connections
