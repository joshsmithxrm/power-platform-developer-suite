using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.ServiceModel;
using System.ServiceModel.Security;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using PPDS.Dataverse.DependencyInjection;
using PPDS.Dataverse.Pooling;
using PPDS.Dataverse.Progress;
using PPDS.Dataverse.Resilience;
using PPDS.Dataverse.Security;

namespace PPDS.Dataverse.BulkOperations
{
    /// <summary>
    /// Executes bulk operations using modern Dataverse APIs.
    /// Uses CreateMultipleRequest, UpdateMultipleRequest, UpsertMultipleRequest for optimal performance.
    /// </summary>
    public sealed class BulkOperationExecutor : IBulkOperationExecutor
    {
        /// <summary>
        /// Maximum number of retries when connection pool is exhausted in GetClientWithRetryAsync.
        /// This provides a safety net before propagating to the outer retry loop in
        /// ExecuteBatchWithThrottleHandlingAsync, which handles PoolExhaustedException with
        /// unlimited retries (pool exhaustion is always transient).
        /// </summary>
        private const int MaxPoolExhaustionRetries = 3;

        /// <summary>
        /// Maximum number of retries for bulk operation infrastructure race conditions.
        /// This covers TVP race conditions (SQL 3732/2766) and stored procedure creation race (SQL 2812).
        /// </summary>
        private const int MaxBulkInfrastructureRetries = 3;

        /// <summary>
        /// Maximum number of retries for SQL deadlock errors.
        /// </summary>
        private const int MaxDeadlockRetries = 3;

        /// <summary>
        /// Maximum number of pre-flight throttle check attempts before proceeding anyway.
        /// This prevents infinite loops when the pool keeps returning throttled connections.
        /// </summary>
        private const int MaxPreFlightAttempts = 10;

        /// <summary>
        /// CRM error code for generic SQL error wrapper that may contain TVP race condition.
        /// </summary>
        private const int SqlErrorCode = unchecked((int)0x80044150);

        /// <summary>
        /// Fallback Retry-After duration when not provided by the server.
        /// </summary>
        private static readonly TimeSpan FallbackRetryAfter = TimeSpan.FromSeconds(30);

        /// <summary>
        /// JSON serializer options for parsing BulkApiErrorDetails.
        /// Case-insensitive to match Newtonsoft.Json default behavior.
        /// </summary>
        private static readonly JsonSerializerOptions BulkApiErrorDetailJsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        private readonly IDataverseConnectionPool _connectionPool;
        private readonly IThrottleTracker _throttleTracker;
        private readonly DataverseOptions _options;
        private readonly ILogger<BulkOperationExecutor> _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="BulkOperationExecutor"/> class.
        /// </summary>
        /// <param name="connectionPool">The connection pool.</param>
        /// <param name="throttleTracker">The throttle tracker for pre-flight throttle checks.</param>
        /// <param name="options">Configuration options.</param>
        /// <param name="logger">Logger instance.</param>
        public BulkOperationExecutor(
            IDataverseConnectionPool connectionPool,
            IThrottleTracker throttleTracker,
            IOptions<DataverseOptions> options,
            ILogger<BulkOperationExecutor> logger)
        {
            _connectionPool = connectionPool ?? throw new ArgumentNullException(nameof(connectionPool));
            _throttleTracker = throttleTracker ?? throw new ArgumentNullException(nameof(throttleTracker));
            _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <inheritdoc />
        public async Task<BulkOperationResult> CreateMultipleAsync(
            string entityLogicalName,
            IEnumerable<Entity> entities,
            BulkOperationOptions? options = null,
            IProgress<ProgressSnapshot>? progress = null,
            CancellationToken cancellationToken = default)
        {
            options ??= _options.BulkOperations;
            var entityList = entities.ToList();

            // Get connection info for logging only - release immediately to avoid holding pool slot
            int recommended;
            {
                await using var infoClient = await _connectionPool.GetClientAsync(cancellationToken: cancellationToken);
                recommended = infoClient.RecommendedDegreesOfParallelism;
            }

            var stopwatch = Stopwatch.StartNew();
            var batches = Batch(entityList, options.BatchSize).ToList();
            var tracker = new ProgressTracker(entityList.Count);

            // Determine parallelism: user override or pool's DOP-based recommendation
            var parallelism = options.MaxParallelBatches ?? _connectionPool.GetTotalRecommendedParallelism();

            _logger.LogInformation(
                "CreateMultiple starting. Entity: {Entity}, Count: {Count}, Batches: {Batches}, ElasticTable: {ElasticTable}, " +
                "Parallelism: {Parallelism}, Recommended: {Recommended}",
                entityLogicalName, entityList.Count, batches.Count, options.ElasticTable,
                options.MaxParallelBatches.HasValue ? $"Fixed({parallelism})" : $"DOP({parallelism})",
                recommended);

            BulkOperationResult result;

            if (batches.Count <= 1 || parallelism <= 1)
            {
                // Sequential execution for single batch or when parallelism unavailable
                result = await ExecuteBatchesSequentiallyAsync(
                    batches,
                    (batch, ct) => ExecuteCreateMultipleBatchAsync(entityLogicalName, batch, options, ct),
                    tracker,
                    progress,
                    cancellationToken);
            }
            else
            {
                // Pool-managed parallel execution - pool semaphore limits concurrency
                result = await ExecuteBatchesParallelAsync(
                    batches,
                    (batch, ct) => ExecuteCreateMultipleBatchAsync(entityLogicalName, batch, options, ct),
                    tracker,
                    progress,
                    cancellationToken);
            }

            stopwatch.Stop();
            result = result with { Duration = stopwatch.Elapsed };

            _logger.LogInformation(
                "CreateMultiple completed. Entity: {Entity}, Success: {Success}, Failed: {Failed}, Duration: {Duration}ms",
                entityLogicalName, result.SuccessCount, result.FailureCount, stopwatch.ElapsedMilliseconds);

            return result;
        }

        /// <inheritdoc />
        public async Task<BulkOperationResult> UpdateMultipleAsync(
            string entityLogicalName,
            IEnumerable<Entity> entities,
            BulkOperationOptions? options = null,
            IProgress<ProgressSnapshot>? progress = null,
            CancellationToken cancellationToken = default)
        {
            options ??= _options.BulkOperations;
            var entityList = entities.ToList();

            // Get connection info for logging only - release immediately to avoid holding pool slot
            int recommended;
            {
                await using var infoClient = await _connectionPool.GetClientAsync(cancellationToken: cancellationToken);
                recommended = infoClient.RecommendedDegreesOfParallelism;
            }

            var stopwatch = Stopwatch.StartNew();
            var batches = Batch(entityList, options.BatchSize).ToList();
            var tracker = new ProgressTracker(entityList.Count);

            // Determine parallelism: user override or pool's DOP-based recommendation
            var parallelism = options.MaxParallelBatches ?? _connectionPool.GetTotalRecommendedParallelism();

            _logger.LogInformation(
                "UpdateMultiple starting. Entity: {Entity}, Count: {Count}, Batches: {Batches}, ElasticTable: {ElasticTable}, " +
                "Parallelism: {Parallelism}, Recommended: {Recommended}",
                entityLogicalName, entityList.Count, batches.Count, options.ElasticTable,
                options.MaxParallelBatches.HasValue ? $"Fixed({parallelism})" : $"DOP({parallelism})",
                recommended);

            BulkOperationResult result;

            if (batches.Count <= 1 || parallelism <= 1)
            {
                // Sequential execution for single batch or when parallelism unavailable
                result = await ExecuteBatchesSequentiallyAsync(
                    batches,
                    (batch, ct) => ExecuteUpdateMultipleBatchAsync(entityLogicalName, batch, options, ct),
                    tracker,
                    progress,
                    cancellationToken);
            }
            else
            {
                // Pool-managed parallel execution - pool semaphore limits concurrency
                result = await ExecuteBatchesParallelAsync(
                    batches,
                    (batch, ct) => ExecuteUpdateMultipleBatchAsync(entityLogicalName, batch, options, ct),
                    tracker,
                    progress,
                    cancellationToken);
            }

            stopwatch.Stop();
            result = result with { Duration = stopwatch.Elapsed };

            _logger.LogInformation(
                "UpdateMultiple completed. Entity: {Entity}, Success: {Success}, Failed: {Failed}, Duration: {Duration}ms",
                entityLogicalName, result.SuccessCount, result.FailureCount, stopwatch.ElapsedMilliseconds);

            return result;
        }

        /// <inheritdoc />
        public async Task<BulkOperationResult> UpsertMultipleAsync(
            string entityLogicalName,
            IEnumerable<Entity> entities,
            BulkOperationOptions? options = null,
            IProgress<ProgressSnapshot>? progress = null,
            CancellationToken cancellationToken = default)
        {
            options ??= _options.BulkOperations;
            var entityList = entities.ToList();

            // Get connection info for logging only - release immediately to avoid holding pool slot
            int recommended;
            {
                await using var infoClient = await _connectionPool.GetClientAsync(cancellationToken: cancellationToken);
                recommended = infoClient.RecommendedDegreesOfParallelism;
            }

            var stopwatch = Stopwatch.StartNew();
            var batches = Batch(entityList, options.BatchSize).ToList();
            var tracker = new ProgressTracker(entityList.Count);

            // Determine parallelism: user override or pool's DOP-based recommendation
            var parallelism = options.MaxParallelBatches ?? _connectionPool.GetTotalRecommendedParallelism();

            _logger.LogInformation(
                "UpsertMultiple starting. Entity: {Entity}, Count: {Count}, Batches: {Batches}, ElasticTable: {ElasticTable}, " +
                "Parallelism: {Parallelism}, Recommended: {Recommended}",
                entityLogicalName, entityList.Count, batches.Count, options.ElasticTable,
                options.MaxParallelBatches.HasValue ? $"Fixed({parallelism})" : $"DOP({parallelism})",
                recommended);

            BulkOperationResult result;

            if (batches.Count <= 1 || parallelism <= 1)
            {
                // Sequential execution for single batch or when parallelism unavailable
                result = await ExecuteBatchesSequentiallyAsync(
                    batches,
                    (batch, ct) => ExecuteUpsertMultipleBatchAsync(entityLogicalName, batch, options, ct),
                    tracker,
                    progress,
                    cancellationToken);
            }
            else
            {
                // Pool-managed parallel execution - pool semaphore limits concurrency
                result = await ExecuteBatchesParallelAsync(
                    batches,
                    (batch, ct) => ExecuteUpsertMultipleBatchAsync(entityLogicalName, batch, options, ct),
                    tracker,
                    progress,
                    cancellationToken);
            }

            stopwatch.Stop();
            result = result with { Duration = stopwatch.Elapsed };

            _logger.LogInformation(
                "UpsertMultiple completed. Entity: {Entity}, Success: {Success}, Failed: {Failed}, Duration: {Duration}ms",
                entityLogicalName, result.SuccessCount, result.FailureCount, stopwatch.ElapsedMilliseconds);

            return result;
        }

        /// <inheritdoc />
        public async Task<BulkOperationResult> DeleteMultipleAsync(
            string entityLogicalName,
            IEnumerable<Guid> ids,
            BulkOperationOptions? options = null,
            IProgress<ProgressSnapshot>? progress = null,
            CancellationToken cancellationToken = default)
        {
            options ??= _options.BulkOperations;
            var idList = ids.ToList();

            // Get connection info for logging only - release immediately to avoid holding pool slot
            int recommended;
            {
                await using var infoClient = await _connectionPool.GetClientAsync(cancellationToken: cancellationToken);
                recommended = infoClient.RecommendedDegreesOfParallelism;
            }

            var stopwatch = Stopwatch.StartNew();
            var batches = Batch(idList, options.BatchSize).ToList();
            var tracker = new ProgressTracker(idList.Count);

            // Select the appropriate batch execution function based on table type
            Func<List<Guid>, CancellationToken, Task<BulkOperationResult>> executeBatch = options.ElasticTable
                ? (batch, ct) => ExecuteElasticDeleteBatchAsync(entityLogicalName, batch, options, ct)
                : (batch, ct) => ExecuteStandardDeleteBatchAsync(entityLogicalName, batch, options, ct);

            // Determine parallelism: user override or pool's DOP-based recommendation
            var parallelism = options.MaxParallelBatches ?? _connectionPool.GetTotalRecommendedParallelism();

            _logger.LogInformation(
                "DeleteMultiple starting. Entity: {Entity}, Count: {Count}, Batches: {Batches}, ElasticTable: {ElasticTable}, " +
                "Parallelism: {Parallelism}, Recommended: {Recommended}",
                entityLogicalName, idList.Count, batches.Count, options.ElasticTable,
                options.MaxParallelBatches.HasValue ? $"Fixed({parallelism})" : $"DOP({parallelism})",
                recommended);

            BulkOperationResult result;

            if (batches.Count <= 1 || parallelism <= 1)
            {
                // Sequential execution for single batch or when parallelism unavailable
                result = await ExecuteBatchesSequentiallyAsync(batches, executeBatch, tracker, progress, cancellationToken);
            }
            else
            {
                // Pool-managed parallel execution - pool semaphore limits concurrency
                result = await ExecuteBatchesParallelAsync(batches, executeBatch, tracker, progress, cancellationToken);
            }

            stopwatch.Stop();
            result = result with { Duration = stopwatch.Elapsed };

            _logger.LogInformation(
                "DeleteMultiple completed. Entity: {Entity}, Success: {Success}, Failed: {Failed}, Duration: {Duration}ms",
                entityLogicalName, result.SuccessCount, result.FailureCount, stopwatch.ElapsedMilliseconds);

            return result;
        }

        /// <summary>
        /// Gets a connection from the pool with retry logic for pool exhaustion.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A pooled client.</returns>
        /// <exception cref="PoolExhaustedException">Thrown when the pool remains exhausted after all retries.</exception>
        private async Task<IPooledClient> GetClientWithRetryAsync(CancellationToken cancellationToken)
        {
            // Attempts are 1-indexed for clearer logging
            for (int attempt = 1; attempt <= MaxPoolExhaustionRetries; attempt++)
            {
                try
                {
                    return await _connectionPool.GetClientAsync(cancellationToken: cancellationToken);
                }
                catch (PoolExhaustedException) when (attempt < MaxPoolExhaustionRetries)
                {
                    // Exponential backoff: 1s, 2s before attempts 2 and 3
                    var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt - 1));
                    _logger.LogWarning(
                        "Connection pool exhausted, waiting for connection (attempt {Attempt}/{MaxRetries}, delay: {Delay}s)",
                        attempt, MaxPoolExhaustionRetries, delay.TotalSeconds);
                    await Task.Delay(delay, cancellationToken);
                }
                // On final attempt, PoolExhaustedException propagates to caller
            }

            // Unreachable: loop either returns a client or throws on final attempt
            throw new InvalidOperationException("Unexpected code path in connection pool retry logic");
        }

        /// <summary>
        /// Checks if an exception is a service protection throttle error and extracts the Retry-After duration.
        /// </summary>
        /// <param name="exception">The exception to check.</param>
        /// <param name="retryAfter">The Retry-After duration if this is a throttle error.</param>
        /// <param name="errorCode">The service protection error code if this is a throttle error.</param>
        /// <returns>True if this is a service protection error.</returns>
        private bool TryGetThrottleInfo(Exception exception, out TimeSpan retryAfter, out int errorCode)
        {
            retryAfter = TimeSpan.Zero;
            errorCode = 0;

            // Check if this is a FaultException with OrganizationServiceFault
            if (exception is not FaultException<OrganizationServiceFault> faultEx)
            {
                return false;
            }

            var fault = faultEx.Detail;
            errorCode = fault.ErrorCode;

            // Check if this is a service protection error
            if (!ServiceProtectionException.IsServiceProtectionError(errorCode))
            {
                return false;
            }

            // Extract Retry-After from ErrorDetails
            if (fault.ErrorDetails != null &&
                fault.ErrorDetails.TryGetValue("Retry-After", out var retryAfterObj))
            {
                if (retryAfterObj is TimeSpan retryAfterSpan)
                {
                    retryAfter = retryAfterSpan;
                }
                else if (retryAfterObj is int retryAfterSeconds)
                {
                    retryAfter = TimeSpan.FromSeconds(retryAfterSeconds);
                }
                else if (retryAfterObj is double retryAfterDouble)
                {
                    retryAfter = TimeSpan.FromSeconds(retryAfterDouble);
                }
                else
                {
                    _logger.LogWarning(
                        "Unexpected Retry-After type: {Type}. Using fallback.",
                        retryAfterObj?.GetType().Name ?? "null");
                    retryAfter = FallbackRetryAfter;
                }
            }
            else
            {
                _logger.LogWarning(
                    "Service protection error without Retry-After. ErrorCode: {ErrorCode}. Using fallback: {Fallback}s",
                    errorCode, FallbackRetryAfter.TotalSeconds);
                retryAfter = FallbackRetryAfter;
            }

            return true;
        }

        /// <summary>
        /// Checks if an exception indicates an authentication/authorization failure.
        /// This includes both token failures (expired/invalid token) and permission failures
        /// (user lacks privilege). Use <see cref="IsTokenFailure"/> to distinguish between them.
        /// </summary>
        /// <param name="exception">The exception to check.</param>
        /// <returns>True if this is an authentication or authorization failure.</returns>
        private static bool IsAuthFailure(Exception exception)
        {
            // MessageSecurityException indicates the token wasn't sent or was rejected.
            // This can occur when the OAuth token expires and refresh fails.
            if (exception is MessageSecurityException)
            {
                return true;
            }

            // Check for common auth failure patterns in FaultException
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
        /// Checks if an exception indicates a token/credential failure that requires seed invalidation.
        /// This is a subset of auth failures - specifically those where the authentication context
        /// itself is broken (token expired, credential invalid) rather than permission issues.
        /// </summary>
        /// <remarks>
        /// Token failures require invalidating the seed client so a fresh authentication can occur.
        /// Permission failures (user lacks privilege, user disabled) don't require seed invalidation
        /// because the authentication is valid - the user just doesn't have access.
        /// </remarks>
        /// <param name="exception">The exception to check.</param>
        /// <returns>True if this is a token failure requiring seed invalidation.</returns>
        private static bool IsTokenFailure(Exception exception)
        {
            // MessageSecurityException with "Anonymous" means the token wasn't sent at all.
            // This is the clearest indicator that the token expired and MSAL refresh failed.
            if (exception is MessageSecurityException)
            {
                return true;
            }

            // HTTP 401 Unauthorized means the token was rejected by the server.
            // This is different from 403 Forbidden which is a permission issue.
            if (exception.InnerException is HttpRequestException httpEx)
            {
                var message = httpEx.Message?.ToLowerInvariant() ?? "";
                if (message.Contains("401") || message.Contains("unauthorized"))
                {
                    return true;
                }
            }

            // Check for explicit token expiration in FaultException messages
            if (exception is FaultException<OrganizationServiceFault> faultEx)
            {
                var message = faultEx.Detail.Message?.ToLowerInvariant() ?? "";

                // Token expiration messages
                if (message.Contains("token") && message.Contains("expired"))
                {
                    return true;
                }

                // Credential issues
                if (message.Contains("credential") &&
                    (message.Contains("invalid") || message.Contains("expired")))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Checks if an exception indicates a connection/network failure.
        /// </summary>
        /// <param name="exception">The exception to check.</param>
        /// <returns>True if this is a connection failure.</returns>
        private static bool IsConnectionFailure(Exception exception)
        {
            return exception is HttpRequestException ||
                   exception is SocketException ||
                   exception is DataverseConnectionException ||
                   exception.InnerException is SocketException ||
                   exception.InnerException is HttpRequestException;
        }

        /// <summary>
        /// Extracts the connection name from an exception when the client is null.
        /// This handles the case where connection creation itself failed.
        /// </summary>
        /// <param name="exception">The exception to extract from.</param>
        /// <param name="fallback">The fallback value if connection name cannot be extracted.</param>
        /// <returns>The connection name or fallback.</returns>
        private static string GetConnectionNameFromException(Exception exception, string fallback)
        {
            if (exception is DataverseConnectionException dce && !string.IsNullOrEmpty(dce.ConnectionName))
            {
                return dce.ConnectionName;
            }

            return fallback;
        }

        /// <summary>
        /// Checks if an exception is a bulk operation infrastructure race condition error.
        /// This happens when parallel bulk operations hit a table before Dataverse has fully
        /// created the internal TVP types and stored procedures, or when schema changes occur.
        /// SQL Error 3732: Cannot drop type because it is being referenced by another object.
        /// SQL Error 2766: The definition for user-defined data type has changed.
        /// SQL Error 2812: Could not find stored procedure (bulk operation proc not yet created).
        /// </summary>
        /// <param name="exception">The exception to check.</param>
        /// <returns>True if this is a bulk operation infrastructure race condition error.</returns>
        private static bool IsBulkInfrastructureRaceConditionError(Exception exception)
        {
            if (exception is not FaultException<OrganizationServiceFault> faultEx)
            {
                return false;
            }

            var fault = faultEx.Detail;

            // Check for the generic SQL error wrapper code
            if (fault.ErrorCode != SqlErrorCode)
            {
                return false;
            }

            // Check the message for bulk operation infrastructure SQL errors:
            // - 3732: Cannot drop type (TVP in use by another operation)
            // - 2766: Type definition has changed (TVP modified during operation)
            // - 2812: Could not find stored procedure (bulk proc not yet created)
            var message = fault.Message ?? string.Empty;
            return message.Contains("3732") || message.Contains("Cannot drop type") ||
                   message.Contains("2766") || message.Contains("definition for user-defined data type") ||
                   message.Contains("2812") || message.Contains("Could not find stored procedure");
        }

        /// <summary>
        /// Checks if an exception is a SQL deadlock error.
        /// SQL Error 1205: Transaction was deadlocked on resources with another process and has been chosen as the deadlock victim.
        /// These are transient errors that occur under high concurrency and should be retried.
        /// </summary>
        /// <param name="exception">The exception to check.</param>
        /// <returns>True if this is a deadlock error.</returns>
        private static bool IsDeadlockError(Exception exception)
        {
            if (exception is not FaultException<OrganizationServiceFault> faultEx)
            {
                return false;
            }

            var fault = faultEx.Detail;

            // Check for the generic SQL error wrapper code (same as TVP)
            if (fault.ErrorCode != SqlErrorCode)
            {
                return false;
            }

            // Check the message for SQL error 1205 (deadlock)
            var message = fault.Message ?? string.Empty;
            return message.Contains("1205") || message.Contains("deadlock", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Logs a throttle error. The pool handles retry timing via GetClientAsync.
        /// PooledClient automatically records the throttle via callback.
        /// </summary>
        /// <param name="connectionName">The name of the connection that was throttled.</param>
        /// <param name="retryAfter">The Retry-After duration.</param>
        /// <param name="errorCode">The service protection error code.</param>
        private void LogThrottle(string connectionName, TimeSpan retryAfter, int errorCode)
        {
            // Note: PooledClient already recorded this throttle via callback.
            // We just log for visibility - pool handles waiting via GetClientAsync.
            _logger.LogWarning(
                "Service protection limit hit. Connection: {Connection}, ErrorCode: {ErrorCode}, " +
                "RetryAfter: {RetryAfter}. Pool will wait for non-throttled connection.",
                connectionName, errorCode, retryAfter);
        }

        /// <summary>
        /// Executes a batch operation with throttle detection, connection health management, and intelligent retry.
        /// Service protection errors retry indefinitely - the pool handles waiting for non-throttled connections.
        /// On auth/connection failure, marks the connection as invalid and retries with a new connection.
        /// </summary>
        private async Task<BulkOperationResult> ExecuteBatchWithThrottleHandlingAsync<T>(
            string operationName,
            string entityLogicalName,
            List<T> batch,
            BulkOperationOptions options,
            Func<IPooledClient, List<T>, CancellationToken, Task<BulkOperationResult>> executeBatch,
            CancellationToken cancellationToken)
        {
            var attempt = 0;
            var maxRetries = _options.Pool.MaxConnectionRetries;

            // Loop indefinitely for service protection errors - only CancellationToken stops us.
            // Other transient errors (auth, connection, TVP, deadlock) have finite retry limits.
            while (true)
            {
                attempt++;
                IPooledClient? client = null;
                string connectionName = "unknown";

                try
                {
                    client = await GetClientWithRetryAsync(cancellationToken);
                    connectionName = client.ConnectionName;

                    // PRE-FLIGHT GUARD: Don't execute if connection is known to be throttled.
                    // This prevents the "in-flight avalanche" where 80 requests hit a throttled
                    // connection simultaneously because they were all dispatched before the first
                    // throttle error returned. Each additional throttle error extends Retry-After.
                    var preFlightAttempts = 0;
                    while (_throttleTracker.IsThrottled(connectionName))
                    {
                        preFlightAttempts++;

                        if (preFlightAttempts > MaxPreFlightAttempts)
                        {
                            // Safety valve: if we've tried many times and still getting throttled
                            // connections, proceed anyway and let the throttle handler deal with it.
                            _logger.LogWarning(
                                "Pre-flight guard: Exceeded {MaxAttempts} attempts, proceeding with throttled connection {Connection}",
                                MaxPreFlightAttempts, connectionName);
                            break;
                        }

                        _logger.LogDebug(
                            "Pre-flight guard: Connection {Connection} is throttled (attempt {Attempt}/{Max}). Returning to pool for different connection.",
                            connectionName, preFlightAttempts, MaxPreFlightAttempts);

                        // Dispose this client and get a different one.
                        // The pool will prefer non-throttled connections, and will wait if all are throttled.
                        await client.DisposeAsync();
                        client = null;

                        client = await GetClientWithRetryAsync(cancellationToken);
                        connectionName = client.ConnectionName;
                    }

                    return await executeBatch(client, batch, cancellationToken);
                }
                catch (Exception ex) when (TryGetThrottleInfo(ex, out var retryAfter, out var errorCode))
                {
                    // Service protection is transient - always retry, never fail.
                    // PooledClient already recorded the throttle via callback.
                    // GetClientAsync will wait for a non-throttled connection.
                    LogThrottle(connectionName, retryAfter, errorCode);

                    // Continue to next iteration - pool handles the waiting
                }
                catch (Exception ex) when (IsAuthFailure(ex))
                {
                    // Extract connection name from exception if client is null
                    var failedConnection = client?.ConnectionName
                        ?? GetConnectionNameFromException(ex, connectionName);

                    _logger.LogWarning(
                        "Authentication failure on connection {Connection}. " +
                        "Marking invalid and retrying. Attempt: {Attempt}/{MaxRetries}. Error: {Error}",
                        failedConnection, attempt, maxRetries, ex.Message);

                    // Mark connection as invalid - it won't be returned to pool
                    client?.MarkInvalid($"Auth failure: {ex.Message}");

                    // Record the failure for statistics
                    _connectionPool.RecordAuthFailure();

                    // If this is a token failure (not just a permission issue), invalidate the seed.
                    // This ensures the next connection gets a fresh authentication context.
                    if (IsTokenFailure(ex))
                    {
                        _connectionPool.InvalidateSeed(failedConnection);
                    }

                    if (attempt >= maxRetries)
                    {
                        throw new DataverseConnectionException(
                            failedConnection,
                            $"Authentication failure after {attempt} attempts",
                            ex);
                    }

                    // Continue to next iteration to retry with new connection
                }
                catch (Exception ex) when (IsConnectionFailure(ex))
                {
                    // Extract connection name from exception if client is null (connection creation failed)
                    var failedConnection = client?.ConnectionName
                        ?? GetConnectionNameFromException(ex, connectionName);

                    _logger.LogWarning(
                        "Connection failure on {Connection}. " +
                        "Marking invalid and retrying. Attempt: {Attempt}/{MaxRetries}. Error: {Error}",
                        failedConnection, attempt, maxRetries, ex.Message);

                    // Mark connection as invalid (only if we have a client instance)
                    client?.MarkInvalid($"Connection failure: {ex.Message}");

                    // Record the failure for statistics
                    _connectionPool.RecordConnectionFailure();

                    if (attempt >= maxRetries)
                    {
                        throw new DataverseConnectionException(
                            failedConnection,
                            $"Connection failure after {attempt} attempts",
                            ex);
                    }

                    // Continue to next iteration to retry with new connection
                }
                catch (Exception ex) when (IsBulkInfrastructureRaceConditionError(ex))
                {
                    // Exponential backoff: 500ms, 1s, 2s
                    var delay = TimeSpan.FromMilliseconds(500 * Math.Pow(2, attempt - 1));

                    _logger.LogWarning(
                        "Bulk operation infrastructure race condition detected for {Entity}. " +
                        "This is transient on new tables. Retrying in {Delay}ms. Attempt: {Attempt}/{MaxRetries}",
                        entityLogicalName, delay.TotalMilliseconds, attempt, MaxBulkInfrastructureRetries);

                    if (attempt >= MaxBulkInfrastructureRetries)
                    {
                        _logger.LogError(
                            "Bulk operation infrastructure error persisted after {MaxRetries} retries for {Entity}. " +
                            "This may indicate a schema issue or concurrent schema modification.",
                            MaxBulkInfrastructureRetries, entityLogicalName);
                        throw;
                    }

                    await Task.Delay(delay, cancellationToken);

                    // Continue to next iteration to retry
                }
                catch (Exception ex) when (IsDeadlockError(ex))
                {
                    // Exponential backoff: 500ms, 1s, 2s
                    var delay = TimeSpan.FromMilliseconds(500 * Math.Pow(2, attempt - 1));

                    _logger.LogWarning(
                        "SQL deadlock detected for {Entity}. " +
                        "This is transient under high concurrency. Retrying in {Delay}ms. Attempt: {Attempt}/{MaxDeadlockRetries}",
                        entityLogicalName, delay.TotalMilliseconds, attempt, MaxDeadlockRetries);

                    if (attempt >= MaxDeadlockRetries)
                    {
                        _logger.LogError(
                            "SQL deadlock persisted after {MaxRetries} retries for {Entity}. " +
                            "Consider reducing parallelism or batch size.",
                            MaxDeadlockRetries, entityLogicalName);
                        throw;
                    }

                    await Task.Delay(delay, cancellationToken);

                    // Continue to next iteration to retry
                }
                catch (PoolExhaustedException ex)
                {
                    // Pool exhaustion is ALWAYS transient - connections will free up as batches complete.
                    // Retry indefinitely with exponential backoff (same pattern as throttling).
                    // Only cancellation token can stop this - pool exhaustion should never cause data loss.
                    var delay = TimeSpan.FromSeconds(Math.Min(Math.Pow(2, attempt - 1), 32)); // Cap at 32s

                    _logger.LogWarning(
                        "Pool exhausted for {Entity} (attempt {Attempt}). " +
                        "Waiting {Delay}s for connections to free. Active: {Active}/{MaxPool}",
                        entityLogicalName, attempt, delay.TotalSeconds,
                        ex.ActiveConnections, ex.MaxPoolSize);

                    await Task.Delay(delay, cancellationToken);

                    // Continue to next iteration of while(true) loop - pool will eventually have capacity
                }
                catch (Exception ex) when (ex is TaskCanceledException or OperationCanceledException)
                {
                    // Cancellation is expected when Parallel.ForEachAsync cancels remaining operations
                    // after one batch fails. Don't log as error - just return a canceled result.
                    _logger.LogDebug(
                        "{Operation} batch canceled. Entity: {Entity}, BatchSize: {BatchSize}",
                        operationName, entityLogicalName, batch.Count);

                    // Return empty result - the batch wasn't processed, not failed
                    return new BulkOperationResult
                    {
                        SuccessCount = 0,
                        FailureCount = 0,
                        Errors = Array.Empty<BulkOperationError>(),
                        Duration = TimeSpan.Zero
                    };
                }
                catch (Exception ex)
                {
                    // Non-retryable error - convert to failure result
                    _logger.LogError(ex, "{Operation} batch failed with non-retryable error. Entity: {Entity}, BatchSize: {BatchSize}",
                        operationName, entityLogicalName, batch.Count);

                    // Create appropriate failure result based on batch type
                    if (batch is List<Entity> entityBatch)
                    {
                        return CreateFailureResultForEntities(entityBatch, ex);
                    }
                    else if (batch is List<Guid> idBatch)
                    {
                        return CreateFailureResultForIds(idBatch, ex);
                    }
                    else
                    {
                        // Unknown batch type - rethrow
                        throw;
                    }
                }
                finally
                {
                    if (client != null)
                    {
                        await client.DisposeAsync();
                    }
                }
            }

            // Unreachable: loop only exits via return (success/failure result),
            // throw (non-retryable error), or cancellation (throws OperationCanceledException)
        }

        private Task<BulkOperationResult> ExecuteCreateMultipleBatchAsync(
            string entityLogicalName,
            List<Entity> batch,
            BulkOperationOptions options,
            CancellationToken cancellationToken)
        {
            return ExecuteBatchWithThrottleHandlingAsync(
                "CreateMultiple",
                entityLogicalName,
                batch,
                options,
                (client, b, ct) => ExecuteCreateMultipleCoreAsync(client, entityLogicalName, b, options, ct),
                cancellationToken);
        }

        private async Task<BulkOperationResult> ExecuteCreateMultipleCoreAsync(
            IPooledClient client,
            string entityLogicalName,
            List<Entity> batch,
            BulkOperationOptions options,
            CancellationToken cancellationToken)
        {
            _logger.LogDebug("Executing CreateMultiple batch. Entity: {Entity}, BatchSize: {BatchSize}, Connection: {Connection}",
                entityLogicalName, batch.Count, client.DisplayName);

            var targets = new EntityCollection(batch) { EntityName = entityLogicalName };
            var request = new CreateMultipleRequest { Targets = targets };

            ApplyBypassOptions(request, options);

            try
            {
                var response = (CreateMultipleResponse)await client.ExecuteAsync(request, cancellationToken);

                _logger.LogDebug("CreateMultiple batch completed. Entity: {Entity}, Created: {Created}",
                    entityLogicalName, response.Ids.Length);

                return new BulkOperationResult
                {
                    SuccessCount = response.Ids.Length,
                    FailureCount = 0,
                    Errors = Array.Empty<BulkOperationError>(),
                    Duration = TimeSpan.Zero,
                    CreatedIds = response.Ids
                };
            }
            catch (Exception ex) when (options.ElasticTable && TryExtractBulkApiErrors(ex, batch, out var errors, out var successCount))
            {
                // Elastic tables support partial success - this is expected behavior, not an error
                return new BulkOperationResult
                {
                    SuccessCount = successCount,
                    FailureCount = errors.Count,
                    Errors = errors,
                    Duration = TimeSpan.Zero
                };
            }
            // All other errors propagate to wrapper for retry or failure handling
        }

        private Task<BulkOperationResult> ExecuteUpdateMultipleBatchAsync(
            string entityLogicalName,
            List<Entity> batch,
            BulkOperationOptions options,
            CancellationToken cancellationToken)
        {
            return ExecuteBatchWithThrottleHandlingAsync(
                "UpdateMultiple",
                entityLogicalName,
                batch,
                options,
                (client, b, ct) => ExecuteUpdateMultipleCoreAsync(client, entityLogicalName, b, options, ct),
                cancellationToken);
        }

        private async Task<BulkOperationResult> ExecuteUpdateMultipleCoreAsync(
            IPooledClient client,
            string entityLogicalName,
            List<Entity> batch,
            BulkOperationOptions options,
            CancellationToken cancellationToken)
        {
            _logger.LogDebug("Executing UpdateMultiple batch. Entity: {Entity}, BatchSize: {BatchSize}, Connection: {Connection}",
                entityLogicalName, batch.Count, client.DisplayName);

            var targets = new EntityCollection(batch) { EntityName = entityLogicalName };
            var request = new UpdateMultipleRequest { Targets = targets };

            ApplyBypassOptions(request, options);

            try
            {
                await client.ExecuteAsync(request, cancellationToken);

                _logger.LogDebug("UpdateMultiple batch completed. Entity: {Entity}, Updated: {Updated}",
                    entityLogicalName, batch.Count);

                return new BulkOperationResult
                {
                    SuccessCount = batch.Count,
                    FailureCount = 0,
                    Errors = Array.Empty<BulkOperationError>(),
                    Duration = TimeSpan.Zero
                };
            }
            catch (Exception ex) when (options.ElasticTable && TryExtractBulkApiErrors(ex, batch, out var errors, out var successCount))
            {
                // Elastic tables support partial success - this is expected behavior, not an error
                return new BulkOperationResult
                {
                    SuccessCount = successCount,
                    FailureCount = errors.Count,
                    Errors = errors,
                    Duration = TimeSpan.Zero
                };
            }
            // All other errors propagate to wrapper for retry or failure handling
        }

        private Task<BulkOperationResult> ExecuteUpsertMultipleBatchAsync(
            string entityLogicalName,
            List<Entity> batch,
            BulkOperationOptions options,
            CancellationToken cancellationToken)
        {
            return ExecuteBatchWithThrottleHandlingAsync(
                "UpsertMultiple",
                entityLogicalName,
                batch,
                options,
                (client, b, ct) => ExecuteUpsertMultipleCoreAsync(client, entityLogicalName, b, options, ct),
                cancellationToken);
        }

        private async Task<BulkOperationResult> ExecuteUpsertMultipleCoreAsync(
            IPooledClient client,
            string entityLogicalName,
            List<Entity> batch,
            BulkOperationOptions options,
            CancellationToken cancellationToken)
        {
            _logger.LogDebug("Executing UpsertMultiple batch. Entity: {Entity}, BatchSize: {BatchSize}, Connection: {Connection}",
                entityLogicalName, batch.Count, client.DisplayName);

            var targets = new EntityCollection(batch) { EntityName = entityLogicalName };
            var request = new UpsertMultipleRequest { Targets = targets };

            ApplyBypassOptions(request, options);

            try
            {
                var response = (UpsertMultipleResponse)await client.ExecuteAsync(request, cancellationToken);

                // Count created vs updated from response results
                var createdCount = 0;
                var updatedCount = 0;

                if (response.Results != null)
                {
                    foreach (var upsertResponse in response.Results)
                    {
                        if (upsertResponse.RecordCreated)
                        {
                            createdCount++;
                        }
                        else
                        {
                            updatedCount++;
                        }
                    }
                }

                _logger.LogDebug("UpsertMultiple batch completed. Entity: {Entity}, Created: {Created}, Updated: {Updated}",
                    entityLogicalName, createdCount, updatedCount);

                return new BulkOperationResult
                {
                    SuccessCount = createdCount + updatedCount,
                    FailureCount = 0,
                    Errors = Array.Empty<BulkOperationError>(),
                    Duration = TimeSpan.Zero,
                    CreatedCount = createdCount,
                    UpdatedCount = updatedCount
                };
            }
            catch (Exception ex) when (options.ElasticTable && TryExtractBulkApiErrors(ex, batch, out var errors, out var successCount))
            {
                // Elastic tables support partial success - this is expected behavior, not an error
                // Note: Cannot determine created/updated split for partial failures
                return new BulkOperationResult
                {
                    SuccessCount = successCount,
                    FailureCount = errors.Count,
                    Errors = errors,
                    Duration = TimeSpan.Zero
                };
            }
            // All other errors propagate to wrapper for retry or failure handling
        }

        private Task<BulkOperationResult> ExecuteElasticDeleteBatchAsync(
            string entityLogicalName,
            List<Guid> batch,
            BulkOperationOptions options,
            CancellationToken cancellationToken)
        {
            return ExecuteBatchWithThrottleHandlingAsync(
                "DeleteMultiple (elastic)",
                entityLogicalName,
                batch,
                options,
                (client, b, ct) => ExecuteElasticDeleteCoreAsync(client, entityLogicalName, b, options, ct),
                cancellationToken);
        }

        private async Task<BulkOperationResult> ExecuteElasticDeleteCoreAsync(
            IPooledClient client,
            string entityLogicalName,
            List<Guid> batch,
            BulkOperationOptions options,
            CancellationToken cancellationToken)
        {
            _logger.LogDebug("Executing DeleteMultiple (elastic) batch. Entity: {Entity}, BatchSize: {BatchSize}, Connection: {Connection}",
                entityLogicalName, batch.Count, client.DisplayName);

            var entityReferences = batch
                .Select(id => new EntityReference(entityLogicalName, id))
                .ToList();

            var request = new OrganizationRequest("DeleteMultiple")
            {
                Parameters = { { "Targets", new EntityReferenceCollection(entityReferences) } }
            };

            ApplyBypassOptions(request, options);

            try
            {
                await client.ExecuteAsync(request, cancellationToken);

                return new BulkOperationResult
                {
                    SuccessCount = batch.Count,
                    FailureCount = 0,
                    Errors = Array.Empty<BulkOperationError>(),
                    Duration = TimeSpan.Zero
                };
            }
            catch (Exception ex) when (TryExtractBulkApiErrorsForDelete(ex, batch, out var errors, out var successCount))
            {
                // DeleteMultiple supports partial success - this is expected behavior, not an error
                return new BulkOperationResult
                {
                    SuccessCount = successCount,
                    FailureCount = errors.Count,
                    Errors = errors,
                    Duration = TimeSpan.Zero
                };
            }
            // All other errors propagate to wrapper for retry or failure handling
        }

        private Task<BulkOperationResult> ExecuteStandardDeleteBatchAsync(
            string entityLogicalName,
            List<Guid> batch,
            BulkOperationOptions options,
            CancellationToken cancellationToken)
        {
            return ExecuteBatchWithThrottleHandlingAsync(
                "DeleteMultiple (standard)",
                entityLogicalName,
                batch,
                options,
                (client, b, ct) => ExecuteStandardDeleteCoreAsync(client, entityLogicalName, b, options, ct),
                cancellationToken);
        }

        private async Task<BulkOperationResult> ExecuteStandardDeleteCoreAsync(
            IPooledClient client,
            string entityLogicalName,
            List<Guid> batch,
            BulkOperationOptions options,
            CancellationToken cancellationToken)
        {
            _logger.LogDebug("Executing DeleteMultiple (standard) batch. Entity: {Entity}, BatchSize: {BatchSize}, Connection: {Connection}",
                entityLogicalName, batch.Count, client.DisplayName);

            var executeMultiple = new ExecuteMultipleRequest
            {
                Requests = new OrganizationRequestCollection(),
                Settings = new ExecuteMultipleSettings
                {
                    ContinueOnError = options.ContinueOnError,
                    ReturnResponses = true
                }
            };

            foreach (var id in batch)
            {
                var deleteRequest = new DeleteRequest
                {
                    Target = new EntityReference(entityLogicalName, id)
                };

                ApplyBypassOptions(deleteRequest, options);
                executeMultiple.Requests.Add(deleteRequest);
            }

            // Note: ExecuteMultiple can also throw service protection errors
            // These will propagate to the wrapper for retry
            var response = (ExecuteMultipleResponse)await client.ExecuteAsync(executeMultiple, cancellationToken);

            var errors = new List<BulkOperationError>();
            var successCount = 0;

            for (int i = 0; i < batch.Count; i++)
            {
                var itemResponse = response.Responses.FirstOrDefault(r => r.RequestIndex == i);

                if (itemResponse?.Fault != null)
                {
                    errors.Add(new BulkOperationError
                    {
                        Index = i,
                        RecordId = batch[i],
                        ErrorCode = itemResponse.Fault.ErrorCode,
                        Message = itemResponse.Fault.Message
                    });
                }
                else
                {
                    successCount++;
                }
            }

            return new BulkOperationResult
            {
                SuccessCount = successCount,
                FailureCount = errors.Count,
                Errors = errors,
                Duration = TimeSpan.Zero
            };
        }

        private static void ApplyBypassOptions(OrganizationRequest request, BulkOperationOptions options)
        {
            // Custom business logic bypass
            if (options.BypassCustomLogic != CustomLogicBypass.None)
            {
                var parts = new List<string>(2);
                if (options.BypassCustomLogic.HasFlag(CustomLogicBypass.Synchronous))
                    parts.Add("CustomSync");
                if (options.BypassCustomLogic.HasFlag(CustomLogicBypass.Asynchronous))
                    parts.Add("CustomAsync");
                request.Parameters["BypassBusinessLogicExecution"] = string.Join(",", parts);
            }

            // Power Automate flows bypass
            if (options.BypassPowerAutomateFlows)
            {
                request.Parameters["SuppressCallbackRegistrationExpanderJob"] = true;
            }

            // Duplicate detection
            if (options.SuppressDuplicateDetection)
            {
                request.Parameters["SuppressDuplicateDetection"] = true;
            }

            // Tag for plugin context
            if (!string.IsNullOrEmpty(options.Tag))
            {
                request.Parameters["tag"] = options.Tag;
            }
        }

        private bool TryExtractBulkApiErrors(
            Exception ex,
            List<Entity> batch,
            out List<BulkOperationError> errors,
            out int successCount)
        {
            errors = new List<BulkOperationError>();
            successCount = 0;

            // Check for Plugin.BulkApiErrorDetails in FaultException
            if (ex is System.ServiceModel.FaultException<Microsoft.Xrm.Sdk.OrganizationServiceFault> faultEx)
            {
                return TryExtractFromFault(faultEx.Detail, batch.Count, out errors, out successCount);
            }

            return false;
        }

        private bool TryExtractBulkApiErrorsForDelete(
            Exception ex,
            List<Guid> batch,
            out List<BulkOperationError> errors,
            out int successCount)
        {
            errors = new List<BulkOperationError>();
            successCount = 0;

            if (ex is System.ServiceModel.FaultException<Microsoft.Xrm.Sdk.OrganizationServiceFault> faultEx)
            {
                return TryExtractFromFaultForDelete(faultEx.Detail, batch, out errors, out successCount);
            }

            return false;
        }

        private bool TryExtractFromFault(
            Microsoft.Xrm.Sdk.OrganizationServiceFault fault,
            int batchCount,
            out List<BulkOperationError> errors,
            out int successCount)
        {
            errors = new List<BulkOperationError>();
            successCount = 0;

            if (fault.ErrorDetails.TryGetValue("Plugin.BulkApiErrorDetails", out var errorDetails))
            {
                try
                {
                    var details = JsonSerializer.Deserialize<List<BulkApiErrorDetail>>(
                        errorDetails.ToString()!,
                        BulkApiErrorDetailJsonOptions);
                    if (details != null)
                    {
                        var failedIndexes = new HashSet<int>(details.Select(d => d.RequestIndex));
                        successCount = batchCount - failedIndexes.Count;

                        errors = details.Select(d => new BulkOperationError
                        {
                            Index = d.RequestIndex,
                            RecordId = !string.IsNullOrEmpty(d.Id) ? Guid.Parse(d.Id) : null,
                            ErrorCode = d.StatusCode,
                            Message = $"Bulk operation failed at index {d.RequestIndex}"
                        }).ToList();

                        return true;
                    }
                }
                catch (JsonException)
                {
                    _logger.LogWarning("Failed to parse BulkApiErrorDetails");
                }
            }

            return false;
        }

        private bool TryExtractFromFaultForDelete(
            Microsoft.Xrm.Sdk.OrganizationServiceFault fault,
            List<Guid> batch,
            out List<BulkOperationError> errors,
            out int successCount)
        {
            errors = new List<BulkOperationError>();
            successCount = 0;

            if (fault.ErrorDetails.TryGetValue("Plugin.BulkApiErrorDetails", out var errorDetails))
            {
                try
                {
                    var details = JsonSerializer.Deserialize<List<BulkApiErrorDetail>>(
                        errorDetails.ToString()!,
                        BulkApiErrorDetailJsonOptions);
                    if (details != null)
                    {
                        var failedIndexes = new HashSet<int>(details.Select(d => d.RequestIndex));
                        successCount = batch.Count - failedIndexes.Count;

                        errors = details.Select(d => new BulkOperationError
                        {
                            Index = d.RequestIndex,
                            RecordId = d.RequestIndex < batch.Count ? batch[d.RequestIndex] : null,
                            ErrorCode = d.StatusCode,
                            Message = $"Delete failed at index {d.RequestIndex}"
                        }).ToList();

                        return true;
                    }
                }
                catch (JsonException)
                {
                    _logger.LogWarning("Failed to parse BulkApiErrorDetails for delete");
                }
            }

            return false;
        }

        /// <summary>
        /// Executes batches in parallel with pool-managed concurrency.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Uses Parallel.ForEachAsync with a high local limit, relying on
        /// the connection pool's semaphore to naturally limit concurrency. Tasks block on
        /// <see cref="IDataverseConnectionPool.GetClientAsync"/> when the pool is at capacity.
        /// </para>
        /// <para>
        /// This approach enables fair sharing between multiple concurrent consumers (e.g.,
        /// multiple entities importing in parallel, or multiple CLI commands sharing a pool).
        /// Each consumer's tasks queue on the pool semaphore rather than each consumer
        /// assuming it can use the full pool capacity.
        /// </para>
        /// <para>
        /// See ADR-0019 for architectural rationale.
        /// </para>
        /// </remarks>
        private async Task<BulkOperationResult> ExecuteBatchesParallelAsync<T>(
            List<List<T>> batches,
            Func<List<T>, CancellationToken, Task<BulkOperationResult>> executeBatch,
            ProgressTracker tracker,
            IProgress<ProgressSnapshot>? progress,
            CancellationToken cancellationToken)
        {
            var allErrors = new ConcurrentBag<BulkOperationError>();
            var allCreatedIds = new ConcurrentBag<Guid>();
            var successCount = 0;
            var failureCount = 0;
            var createdCount = 0;
            var updatedCount = 0;
            var hasUpsertCounts = 0; // 0 = false, 1 = true (for thread-safe flag)

            // Cap parallelism at pool capacity to prevent over-subscription during throttling.
            // When throttling occurs, connections hold semaphore slots while waiting on Retry-After,
            // reducing effective throughput. Using ProcessorCount * 4 on a 24-core machine spawns
            // 96 tasks that queue for ~20 pool slots, exceeding AcquireTimeout (120s).
            // The Min(CPU, Pool) approach respects both constraints.
            var cpuBasedLimit = Environment.ProcessorCount * 4;
            var poolCapacity = _connectionPool.GetTotalRecommendedParallelism();
            var effectiveParallelism = Math.Min(cpuBasedLimit, Math.Max(poolCapacity, 1));

            await Parallel.ForEachAsync(
                batches,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = effectiveParallelism,
                    CancellationToken = cancellationToken
                },
                async (batch, ct) =>
                {
                    // Acquire global batch slot from coordinator.
                    // This ensures all concurrent bulk operations (e.g., multiple entities importing
                    // in parallel) don't exceed the pool's total capacity.
                    await using var slot = await _connectionPool.BatchCoordinator.AcquireAsync(ct);

                    var batchResult = await executeBatch(batch, ct);

                    // Aggregate results (thread-safe)
                    Interlocked.Add(ref successCount, batchResult.SuccessCount);
                    Interlocked.Add(ref failureCount, batchResult.FailureCount);

                    foreach (var error in batchResult.Errors)
                        allErrors.Add(error);

                    if (batchResult.CreatedIds != null)
                    {
                        foreach (var id in batchResult.CreatedIds)
                            allCreatedIds.Add(id);
                    }

                    if (batchResult.CreatedCount.HasValue)
                    {
                        Interlocked.Exchange(ref hasUpsertCounts, 1);
                        Interlocked.Add(ref createdCount, batchResult.CreatedCount.Value);
                    }
                    if (batchResult.UpdatedCount.HasValue)
                    {
                        Interlocked.Exchange(ref hasUpsertCounts, 1);
                        Interlocked.Add(ref updatedCount, batchResult.UpdatedCount.Value);
                    }

                    // Report progress
                    tracker.RecordProgress(batchResult.SuccessCount, batchResult.FailureCount);
                    progress?.Report(tracker.GetSnapshot());
                });

            return new BulkOperationResult
            {
                SuccessCount = successCount,
                FailureCount = failureCount,
                Errors = allErrors.ToList(),
                Duration = TimeSpan.Zero,
                CreatedIds = allCreatedIds.Count > 0 ? allCreatedIds.ToList() : null,
                CreatedCount = hasUpsertCounts == 1 ? createdCount : null,
                UpdatedCount = hasUpsertCounts == 1 ? updatedCount : null
            };
        }

        /// <summary>
        /// Executes batches sequentially (one at a time).
        /// Used for single batches or when parallelism is unavailable.
        /// </summary>
        private static async Task<BulkOperationResult> ExecuteBatchesSequentiallyAsync<T>(
            IReadOnlyList<List<T>> batches,
            Func<List<T>, CancellationToken, Task<BulkOperationResult>> executeBatch,
            ProgressTracker tracker,
            IProgress<ProgressSnapshot>? progress,
            CancellationToken cancellationToken)
        {
            var allErrors = new List<BulkOperationError>();
            var allCreatedIds = new List<Guid>();
            var successCount = 0;
            int? createdCount = null;
            int? updatedCount = null;

            foreach (var batch in batches)
            {
                var batchResult = await executeBatch(batch, cancellationToken).ConfigureAwait(false);

                successCount += batchResult.SuccessCount;
                allErrors.AddRange(batchResult.Errors);

                if (batchResult.CreatedIds != null)
                {
                    allCreatedIds.AddRange(batchResult.CreatedIds);
                }

                // Aggregate upsert created/updated counts
                if (batchResult.CreatedCount.HasValue)
                {
                    createdCount = (createdCount ?? 0) + batchResult.CreatedCount.Value;
                }
                if (batchResult.UpdatedCount.HasValue)
                {
                    updatedCount = (updatedCount ?? 0) + batchResult.UpdatedCount.Value;
                }

                tracker.RecordProgress(batchResult.SuccessCount, batchResult.FailureCount);
                progress?.Report(tracker.GetSnapshot());
            }

            return new BulkOperationResult
            {
                SuccessCount = successCount,
                FailureCount = allErrors.Count,
                Errors = allErrors,
                Duration = TimeSpan.Zero,
                CreatedIds = allCreatedIds.Count > 0 ? allCreatedIds : null,
                CreatedCount = createdCount,
                UpdatedCount = updatedCount
            };
        }

        /// <summary>
        /// Extracts the error code from an exception if it's a FaultException, otherwise returns -1.
        /// </summary>
        private static int ExtractErrorCode(Exception ex)
        {
            if (ex is FaultException<OrganizationServiceFault> faultEx)
            {
                return faultEx.Detail.ErrorCode;
            }
            return -1;
        }

        /// <summary>
        /// Extracts the most useful error message from an exception.
        /// For FaultException, uses Detail.Message which contains the actual Dataverse error.
        /// Falls back to ex.Message for other exception types.
        /// </summary>
        private static string GetExceptionMessage(Exception ex)
        {
            if (ex is FaultException<OrganizationServiceFault> faultEx
                && faultEx.Detail?.Message != null)
            {
                return faultEx.Detail.Message;
            }
            return ex.Message;
        }

        /// <summary>
        /// Attempts to extract a field/attribute name from a Dataverse error message.
        /// </summary>
        /// <remarks>
        /// Common patterns:
        /// - "attribute 'fieldname'" or "field 'fieldname'"
        /// - "Entity 'entityname' With Id = ..." (indicates a lookup field)
        /// - "'fieldname' contains invalid data"
        /// </remarks>
        private static string? TryExtractFieldName(string message)
        {
            if (string.IsNullOrEmpty(message))
                return null;

            // Pattern: attribute 'fieldname' or field 'fieldname'
            var match = Regex.Match(message, @"(?:attribute|field)\s*['""](\w+)['""]", RegexOptions.IgnoreCase);
            if (match.Success)
                return match.Groups[1].Value;

            // Pattern: 'fieldname' contains invalid data
            match = Regex.Match(message, @"['""](\w+)['""]\s+contains", RegexOptions.IgnoreCase);
            if (match.Success)
                return match.Groups[1].Value;

            return null;
        }

        /// <summary>
        /// Creates a safe description of a field value for logging (no PII).
        /// </summary>
        private static string? DescribeFieldValue(Entity entity, string? fieldName)
        {
            if (string.IsNullOrEmpty(fieldName) || !entity.Contains(fieldName))
                return null;

            var value = entity[fieldName];
            return value switch
            {
                EntityReference er => $"{er.LogicalName}:{er.Id.ToString("D")[..8]}...",
                OptionSetValue osv => $"OptionSet({osv.Value})",
                Money m => $"Money({m.Value})",
                null => "null",
                _ => value.GetType().Name
            };
        }

        /// <summary>
        /// Creates a failure result for a batch of entities that failed due to a non-retryable error.
        /// </summary>
        private static BulkOperationResult CreateFailureResultForEntities(List<Entity> batch, Exception ex)
        {
            var errorCode = ExtractErrorCode(ex);
            var message = GetExceptionMessage(ex);
            var fieldName = TryExtractFieldName(message);

            // Analyze the batch failure to identify which record(s) caused the issue
            var diagnostics = AnalyzeBatchFailure(batch, ex);

            var errors = batch.Select((e, i) => new BulkOperationError
            {
                Index = i,
                RecordId = e.Id != Guid.Empty ? e.Id : null,
                ErrorCode = errorCode,
                Message = message,
                FieldName = fieldName,
                FieldValueDescription = DescribeFieldValue(e, fieldName),
                Diagnostics = diagnostics.Count > 0 ? diagnostics : null
            }).ToList();

            return new BulkOperationResult
            {
                SuccessCount = 0,
                FailureCount = batch.Count,
                Errors = errors,
                Duration = TimeSpan.Zero
            };
        }

        /// <summary>
        /// Creates a failure result for a batch of IDs that failed due to a non-retryable error.
        /// </summary>
        private static BulkOperationResult CreateFailureResultForIds(List<Guid> batch, Exception ex)
        {
            var errorCode = ExtractErrorCode(ex);
            var message = GetExceptionMessage(ex);
            var fieldName = TryExtractFieldName(message);

            var errors = batch.Select((id, i) => new BulkOperationError
            {
                Index = i,
                RecordId = id,
                ErrorCode = errorCode,
                Message = message,
                FieldName = fieldName
                // FieldValueDescription not available when only IDs are provided
            }).ToList();

            return new BulkOperationResult
            {
                SuccessCount = 0,
                FailureCount = batch.Count,
                Errors = errors,
                Duration = TimeSpan.Zero
            };
        }

        private static IEnumerable<List<T>> Batch<T>(IEnumerable<T> source, int batchSize)
        {
            var batch = new List<T>(batchSize);

            foreach (var item in source)
            {
                batch.Add(item);

                if (batch.Count >= batchSize)
                {
                    yield return batch;
                    batch = new List<T>(batchSize);
                }
            }

            if (batch.Count > 0)
            {
                yield return batch;
            }
        }

        /// <summary>
        /// Error detail structure returned by elastic table bulk operations.
        /// </summary>
        private class BulkApiErrorDetail
        {
            public int RequestIndex { get; set; }
            public string? Id { get; set; }
            public int StatusCode { get; set; }
        }

        /// <summary>
        /// Regex pattern to extract GUIDs from "Does Not Exist" error messages.
        /// </summary>
        private static readonly Regex MissingIdPattern = new(
            @"(?:With )?Ids? = ([0-9a-fA-F-]{36})",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// Analyzes a batch failure to identify which record(s) caused the failure.
        /// </summary>
        /// <param name="batch">The batch of entities that failed.</param>
        /// <param name="exception">The exception that was thrown.</param>
        /// <returns>A list of diagnostics identifying problematic records.</returns>
        /// <remarks>
        /// <para>
        /// When a batch fails with a "Does Not Exist" error, this method:
        /// 1. Parses the error message to extract the missing reference ID(s)
        /// 2. Scans all records in the batch for EntityReference fields matching those IDs
        /// 3. Returns diagnostics identifying which record/field contains the problematic reference
        /// </para>
        /// <para>
        /// Special patterns detected:
        /// <list type="bullet">
        ///   <item><c>SELF_REFERENCE</c>: Record references itself (common in hierarchical entities)</item>
        ///   <item><c>SAME_BATCH_REFERENCE</c>: Record references another record in the same batch</item>
        ///   <item><c>MISSING_REFERENCE</c>: Record references an entity that doesn't exist in target</item>
        /// </list>
        /// </para>
        /// </remarks>
        public static IReadOnlyList<BatchFailureDiagnostic> AnalyzeBatchFailure(
            IReadOnlyList<Entity> batch,
            Exception exception)
        {
            ArgumentNullException.ThrowIfNull(batch);
            ArgumentNullException.ThrowIfNull(exception);

            var diagnostics = new List<BatchFailureDiagnostic>();
            var message = GetExceptionMessage(exception);

            // Extract all GUIDs mentioned in the error message
            var missingIds = new HashSet<Guid>();
            var matches = MissingIdPattern.Matches(message);
            foreach (Match match in matches)
            {
                if (Guid.TryParse(match.Groups[1].Value, out var id))
                {
                    missingIds.Add(id);
                }
            }

            if (missingIds.Count == 0)
            {
                // No GUIDs in error message - can't diagnose further
                return diagnostics;
            }

            // Build a set of IDs in this batch for detecting same-batch references
            var batchIds = new HashSet<Guid>(batch.Select(e => e.Id).Where(id => id != Guid.Empty));

            // Scan all records in the batch for references to missing IDs
            for (int i = 0; i < batch.Count; i++)
            {
                var entity = batch[i];

                foreach (var attr in entity.Attributes)
                {
                    if (attr.Value is EntityReference er && missingIds.Contains(er.Id))
                    {
                        // Determine the error pattern
                        string pattern;
                        string? suggestion = null;

                        if (er.Id == entity.Id)
                        {
                            pattern = "SELF_REFERENCE";
                            suggestion = "Record references itself. Consider two-pass import: create records first, then update self-references.";
                        }
                        else if (batchIds.Contains(er.Id))
                        {
                            pattern = "SAME_BATCH_REFERENCE";
                            suggestion = "Record references another record in the same batch that hasn't been created yet. Consider dependency-aware batching.";
                        }
                        else
                        {
                            pattern = "MISSING_REFERENCE";
                            suggestion = "Referenced record doesn't exist in target environment. Ensure the referenced entity is imported first.";
                        }

                        diagnostics.Add(new BatchFailureDiagnostic
                        {
                            RecordId = entity.Id,
                            RecordIndex = i,
                            FieldName = attr.Key,
                            ReferencedId = er.Id,
                            ReferencedEntityName = er.LogicalName,
                            Pattern = pattern,
                            Suggestion = suggestion
                        });
                    }
                }
            }

            return diagnostics;
        }
    }
}
