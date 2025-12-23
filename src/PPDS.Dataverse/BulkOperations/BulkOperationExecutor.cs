using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.ServiceModel;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Newtonsoft.Json;
using PPDS.Dataverse.DependencyInjection;
using PPDS.Dataverse.Pooling;
using PPDS.Dataverse.Progress;
using PPDS.Dataverse.Resilience;

namespace PPDS.Dataverse.BulkOperations
{
    /// <summary>
    /// Executes bulk operations using modern Dataverse APIs.
    /// Uses CreateMultipleRequest, UpdateMultipleRequest, UpsertMultipleRequest for optimal performance.
    /// </summary>
    public sealed class BulkOperationExecutor : IBulkOperationExecutor
    {
        /// <summary>
        /// Maximum number of retries when connection pool is exhausted.
        /// </summary>
        private const int MaxPoolExhaustionRetries = 3;

        /// <summary>
        /// Maximum number of retries when service protection limits are hit.
        /// </summary>
        private const int MaxThrottleRetries = 3;

        /// <summary>
        /// Fallback Retry-After duration when not provided by the server.
        /// </summary>
        private static readonly TimeSpan FallbackRetryAfter = TimeSpan.FromSeconds(30);

        private readonly IDataverseConnectionPool _connectionPool;
        private readonly IThrottleTracker _throttleTracker;
        private readonly DataverseOptions _options;
        private readonly ILogger<BulkOperationExecutor> _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="BulkOperationExecutor"/> class.
        /// </summary>
        /// <param name="connectionPool">The connection pool.</param>
        /// <param name="throttleTracker">The throttle tracker for recording service protection events.</param>
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

        /// <summary>
        /// Resolves the parallelism to use for batch processing.
        /// Uses the explicit value if provided, otherwise queries the ServiceClient's RecommendedDegreesOfParallelism.
        /// </summary>
        private async Task<int> ResolveParallelismAsync(int? maxParallelBatches, CancellationToken cancellationToken)
        {
            int parallelism;

            if (maxParallelBatches.HasValue)
            {
                parallelism = maxParallelBatches.Value;
            }
            else
            {
                // Get RecommendedDegreesOfParallelism from a connection
                await using var client = await _connectionPool.GetClientAsync(cancellationToken: cancellationToken);
                var recommended = client.RecommendedDegreesOfParallelism;

                if (recommended > 0)
                {
                    _logger.LogDebug("Using RecommendedDegreesOfParallelism: {Parallelism}", recommended);
                    parallelism = recommended;
                }
                else
                {
                    _logger.LogWarning("RecommendedDegreesOfParallelism unavailable or zero, using sequential processing");
                    return 1;
                }
            }

            // Warn if parallelism exceeds pool size
            var maxPoolSize = _options.Pool.MaxPoolSize;
            if (parallelism > maxPoolSize)
            {
                _logger.LogWarning(
                    "MaxParallelBatches ({Parallelism}) exceeds MaxPoolSize ({MaxPoolSize}). " +
                    "Effective parallelism will be limited to {MaxPoolSize}. " +
                    "Consider increasing MaxPoolSize for optimal throughput.",
                    parallelism, maxPoolSize, maxPoolSize);
            }

            return parallelism;
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
            var parallelism = await ResolveParallelismAsync(options.MaxParallelBatches, cancellationToken);

            _logger.LogInformation(
                "CreateMultiple starting. Entity: {Entity}, Count: {Count}, ElasticTable: {ElasticTable}, Parallel: {Parallel}",
                entityLogicalName, entityList.Count, options.ElasticTable, parallelism);

            var stopwatch = Stopwatch.StartNew();
            var batches = Batch(entityList, options.BatchSize).ToList();
            var tracker = new ProgressTracker(entityList.Count);

            BulkOperationResult result;
            if (parallelism > 1 && batches.Count > 1)
            {
                result = await ExecuteBatchesParallelAsync(
                    batches,
                    (batch, ct) => ExecuteCreateMultipleBatchAsync(entityLogicalName, batch, options, ct),
                    parallelism,
                    tracker,
                    progress,
                    cancellationToken);
            }
            else
            {
                var allCreatedIds = new List<Guid>();
                var allErrors = new List<BulkOperationError>();
                var successCount = 0;

                foreach (var batch in batches)
                {
                    var batchResult = await ExecuteCreateMultipleBatchAsync(
                        entityLogicalName, batch, options, cancellationToken);

                    successCount += batchResult.SuccessCount;
                    allErrors.AddRange(batchResult.Errors);
                    if (batchResult.CreatedIds != null)
                    {
                        allCreatedIds.AddRange(batchResult.CreatedIds);
                    }

                    tracker.RecordProgress(batchResult.SuccessCount, batchResult.FailureCount);
                    progress?.Report(tracker.GetSnapshot());
                }

                result = new BulkOperationResult
                {
                    SuccessCount = successCount,
                    FailureCount = allErrors.Count,
                    Errors = allErrors,
                    Duration = stopwatch.Elapsed,
                    CreatedIds = allCreatedIds.Count > 0 ? allCreatedIds : null
                };
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
            var parallelism = await ResolveParallelismAsync(options.MaxParallelBatches, cancellationToken);

            _logger.LogInformation(
                "UpdateMultiple starting. Entity: {Entity}, Count: {Count}, ElasticTable: {ElasticTable}, Parallel: {Parallel}",
                entityLogicalName, entityList.Count, options.ElasticTable, parallelism);

            var stopwatch = Stopwatch.StartNew();
            var batches = Batch(entityList, options.BatchSize).ToList();
            var tracker = new ProgressTracker(entityList.Count);

            BulkOperationResult result;
            if (parallelism > 1 && batches.Count > 1)
            {
                result = await ExecuteBatchesParallelAsync(
                    batches,
                    (batch, ct) => ExecuteUpdateMultipleBatchAsync(entityLogicalName, batch, options, ct),
                    parallelism,
                    tracker,
                    progress,
                    cancellationToken);
            }
            else
            {
                var allErrors = new List<BulkOperationError>();
                var successCount = 0;

                foreach (var batch in batches)
                {
                    var batchResult = await ExecuteUpdateMultipleBatchAsync(
                        entityLogicalName, batch, options, cancellationToken);

                    successCount += batchResult.SuccessCount;
                    allErrors.AddRange(batchResult.Errors);

                    tracker.RecordProgress(batchResult.SuccessCount, batchResult.FailureCount);
                    progress?.Report(tracker.GetSnapshot());
                }

                result = new BulkOperationResult
                {
                    SuccessCount = successCount,
                    FailureCount = allErrors.Count,
                    Errors = allErrors,
                    Duration = stopwatch.Elapsed
                };
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
            var parallelism = await ResolveParallelismAsync(options.MaxParallelBatches, cancellationToken);

            _logger.LogInformation(
                "UpsertMultiple starting. Entity: {Entity}, Count: {Count}, ElasticTable: {ElasticTable}, Parallel: {Parallel}",
                entityLogicalName, entityList.Count, options.ElasticTable, parallelism);

            var stopwatch = Stopwatch.StartNew();
            var batches = Batch(entityList, options.BatchSize).ToList();
            var tracker = new ProgressTracker(entityList.Count);

            BulkOperationResult result;
            if (parallelism > 1 && batches.Count > 1)
            {
                result = await ExecuteBatchesParallelAsync(
                    batches,
                    (batch, ct) => ExecuteUpsertMultipleBatchAsync(entityLogicalName, batch, options, ct),
                    parallelism,
                    tracker,
                    progress,
                    cancellationToken);
            }
            else
            {
                var allErrors = new List<BulkOperationError>();
                var successCount = 0;

                foreach (var batch in batches)
                {
                    var batchResult = await ExecuteUpsertMultipleBatchAsync(
                        entityLogicalName, batch, options, cancellationToken);

                    successCount += batchResult.SuccessCount;
                    allErrors.AddRange(batchResult.Errors);

                    tracker.RecordProgress(batchResult.SuccessCount, batchResult.FailureCount);
                    progress?.Report(tracker.GetSnapshot());
                }

                result = new BulkOperationResult
                {
                    SuccessCount = successCount,
                    FailureCount = allErrors.Count,
                    Errors = allErrors,
                    Duration = stopwatch.Elapsed
                };
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
            var parallelism = await ResolveParallelismAsync(options.MaxParallelBatches, cancellationToken);

            _logger.LogInformation(
                "DeleteMultiple starting. Entity: {Entity}, Count: {Count}, ElasticTable: {ElasticTable}, Parallel: {Parallel}",
                entityLogicalName, idList.Count, options.ElasticTable, parallelism);

            var stopwatch = Stopwatch.StartNew();
            var batches = Batch(idList, options.BatchSize).ToList();
            var tracker = new ProgressTracker(idList.Count);

            // Select the appropriate batch execution function based on table type
            Func<List<Guid>, CancellationToken, Task<BulkOperationResult>> executeBatch = options.ElasticTable
                ? (batch, ct) => ExecuteElasticDeleteBatchAsync(entityLogicalName, batch, options, ct)
                : (batch, ct) => ExecuteStandardDeleteBatchAsync(entityLogicalName, batch, options, ct);

            BulkOperationResult result;
            if (parallelism > 1 && batches.Count > 1)
            {
                result = await ExecuteBatchesParallelAsync(batches, executeBatch, parallelism, tracker, progress, cancellationToken);
            }
            else
            {
                var allErrors = new List<BulkOperationError>();
                var successCount = 0;

                foreach (var batch in batches)
                {
                    var batchResult = await executeBatch(batch, cancellationToken);
                    successCount += batchResult.SuccessCount;
                    allErrors.AddRange(batchResult.Errors);

                    tracker.RecordProgress(batchResult.SuccessCount, batchResult.FailureCount);
                    progress?.Report(tracker.GetSnapshot());
                }

                result = new BulkOperationResult
                {
                    SuccessCount = successCount,
                    FailureCount = allErrors.Count,
                    Errors = allErrors,
                    Duration = stopwatch.Elapsed
                };
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
        /// Checks if there are any connections available (not currently throttled).
        /// </summary>
        /// <param name="totalConnections">The total number of configured connections.</param>
        /// <returns>True if at least one connection is available.</returns>
        private bool HasAvailableConnections(int totalConnections)
        {
            var throttledCount = _throttleTracker.ThrottledConnectionCount;
            return throttledCount < totalConnections;
        }

        /// <summary>
        /// Handles a throttle error by recording it and determining if retry is possible.
        /// </summary>
        /// <param name="connectionName">The name of the connection that was throttled.</param>
        /// <param name="retryAfter">The Retry-After duration.</param>
        /// <param name="errorCode">The service protection error code.</param>
        /// <param name="attempt">The current attempt number.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>True if the operation should be retried.</returns>
        private async Task<bool> HandleThrottleAsync(
            string connectionName,
            TimeSpan retryAfter,
            int errorCode,
            int attempt,
            CancellationToken cancellationToken)
        {
            // Record the throttle event
            _throttleTracker.RecordThrottle(connectionName, retryAfter);

            _logger.LogWarning(
                "Service protection limit hit. Connection: {Connection}, ErrorCode: {ErrorCode}, " +
                "RetryAfter: {RetryAfter}, Attempt: {Attempt}/{MaxRetries}",
                connectionName, errorCode, retryAfter, attempt, MaxThrottleRetries);

            // Check if we should retry
            if (attempt >= MaxThrottleRetries)
            {
                _logger.LogError(
                    "Max throttle retries exceeded. Connection: {Connection}, Attempts: {Attempts}",
                    connectionName, attempt);
                return false;
            }

            // Get total connection count from pool statistics
            var stats = _connectionPool.Statistics;
            var totalConnections = stats.TotalConnections;

            // Check if other connections are available
            if (HasAvailableConnections(totalConnections))
            {
                _logger.LogDebug(
                    "Other connections available. Retrying immediately on different connection.");
                return true; // Retry immediately with different connection
            }

            // All connections throttled - wait for shortest expiry
            var shortestExpiry = _throttleTracker.GetShortestExpiry();
            if (shortestExpiry > TimeSpan.Zero)
            {
                // Add a small buffer to account for timing
                var waitTime = shortestExpiry + TimeSpan.FromSeconds(1);
                _logger.LogInformation(
                    "All connections throttled. Waiting {WaitTime} before retry.",
                    waitTime);
                await Task.Delay(waitTime, cancellationToken);
            }

            return true;
        }

        /// <summary>
        /// Executes a batch operation with throttle detection and intelligent retry.
        /// On throttle, records the event and either retries immediately on a different connection
        /// or waits for the shortest throttle expiry if all connections are throttled.
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
            Exception? lastException = null;

            while (attempt < MaxThrottleRetries)
            {
                attempt++;
                IPooledClient? client = null;
                string connectionName = "unknown";

                try
                {
                    client = await GetClientWithRetryAsync(cancellationToken);
                    connectionName = client.ConnectionName;

                    return await executeBatch(client, batch, cancellationToken);
                }
                catch (Exception ex) when (TryGetThrottleInfo(ex, out var retryAfter, out var errorCode))
                {
                    lastException = ex;

                    // Handle throttle and determine if we should retry
                    var shouldRetry = await HandleThrottleAsync(
                        connectionName, retryAfter, errorCode, attempt, cancellationToken);

                    if (!shouldRetry)
                    {
                        // Max retries exceeded - throw as ServiceProtectionException
                        throw new ServiceProtectionException(connectionName, retryAfter, errorCode, ex);
                    }

                    // Continue to next iteration to retry
                }
                finally
                {
                    if (client != null)
                    {
                        await client.DisposeAsync();
                    }
                }
            }

            // This shouldn't be reached, but handle it gracefully
            throw new ServiceProtectionException(
                "unknown",
                FallbackRetryAfter,
                0,
                lastException ?? new InvalidOperationException($"Max throttle retries ({MaxThrottleRetries}) exceeded for {operationName}"));
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
                entityLogicalName, batch.Count, client.ConnectionName);

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
                // Elastic tables support partial success
                return new BulkOperationResult
                {
                    SuccessCount = successCount,
                    FailureCount = errors.Count,
                    Errors = errors,
                    Duration = TimeSpan.Zero
                };
            }
            catch (Exception ex) when (!TryGetThrottleInfo(ex, out _, out _))
            {
                // Non-throttle error on standard tables: entire batch fails
                _logger.LogError(ex, "CreateMultiple batch failed. Entity: {Entity}, BatchSize: {BatchSize}",
                    entityLogicalName, batch.Count);

                var errors = batch.Select((e, i) => new BulkOperationError
                {
                    Index = i,
                    RecordId = e.Id != Guid.Empty ? e.Id : null,
                    ErrorCode = -1,
                    Message = ex.Message
                }).ToList();

                return new BulkOperationResult
                {
                    SuccessCount = 0,
                    FailureCount = batch.Count,
                    Errors = errors,
                    Duration = TimeSpan.Zero
                };
            }
            // Throttle exceptions are not caught here - they propagate to the wrapper for retry
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
                entityLogicalName, batch.Count, client.ConnectionName);

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
                return new BulkOperationResult
                {
                    SuccessCount = successCount,
                    FailureCount = errors.Count,
                    Errors = errors,
                    Duration = TimeSpan.Zero
                };
            }
            catch (Exception ex) when (!TryGetThrottleInfo(ex, out _, out _))
            {
                _logger.LogError(ex, "UpdateMultiple batch failed. Entity: {Entity}, BatchSize: {BatchSize}",
                    entityLogicalName, batch.Count);

                var errors = batch.Select((e, i) => new BulkOperationError
                {
                    Index = i,
                    RecordId = e.Id,
                    ErrorCode = -1,
                    Message = ex.Message
                }).ToList();

                return new BulkOperationResult
                {
                    SuccessCount = 0,
                    FailureCount = batch.Count,
                    Errors = errors,
                    Duration = TimeSpan.Zero
                };
            }
            // Throttle exceptions propagate to wrapper for retry
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
                entityLogicalName, batch.Count, client.ConnectionName);

            var targets = new EntityCollection(batch) { EntityName = entityLogicalName };
            var request = new UpsertMultipleRequest { Targets = targets };

            ApplyBypassOptions(request, options);

            try
            {
                var response = (UpsertMultipleResponse)await client.ExecuteAsync(request, cancellationToken);

                _logger.LogDebug("UpsertMultiple batch completed. Entity: {Entity}, Success: {Success}",
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
                return new BulkOperationResult
                {
                    SuccessCount = successCount,
                    FailureCount = errors.Count,
                    Errors = errors,
                    Duration = TimeSpan.Zero
                };
            }
            catch (Exception ex) when (!TryGetThrottleInfo(ex, out _, out _))
            {
                _logger.LogError(ex, "UpsertMultiple batch failed. Entity: {Entity}, BatchSize: {BatchSize}",
                    entityLogicalName, batch.Count);

                var errors = batch.Select((e, i) => new BulkOperationError
                {
                    Index = i,
                    RecordId = e.Id != Guid.Empty ? e.Id : null,
                    ErrorCode = -1,
                    Message = ex.Message
                }).ToList();

                return new BulkOperationResult
                {
                    SuccessCount = 0,
                    FailureCount = batch.Count,
                    Errors = errors,
                    Duration = TimeSpan.Zero
                };
            }
            // Throttle exceptions propagate to wrapper for retry
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
                entityLogicalName, batch.Count, client.ConnectionName);

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
                return new BulkOperationResult
                {
                    SuccessCount = successCount,
                    FailureCount = errors.Count,
                    Errors = errors,
                    Duration = TimeSpan.Zero
                };
            }
            catch (Exception ex) when (!TryGetThrottleInfo(ex, out _, out _))
            {
                _logger.LogError(ex, "DeleteMultiple (elastic) batch failed. Entity: {Entity}, BatchSize: {BatchSize}",
                    entityLogicalName, batch.Count);

                var errors = batch.Select((id, i) => new BulkOperationError
                {
                    Index = i,
                    RecordId = id,
                    ErrorCode = -1,
                    Message = ex.Message
                }).ToList();

                return new BulkOperationResult
                {
                    SuccessCount = 0,
                    FailureCount = batch.Count,
                    Errors = errors,
                    Duration = TimeSpan.Zero
                };
            }
            // Throttle exceptions propagate to wrapper for retry
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
                entityLogicalName, batch.Count, client.ConnectionName);

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
            // Preferred: BypassBusinessLogicExecution (newer, more control)
            if (!string.IsNullOrEmpty(options.BypassBusinessLogicExecution))
            {
                request.Parameters["BypassBusinessLogicExecution"] = options.BypassBusinessLogicExecution;
            }
            // Fallback: BypassCustomPluginExecution (legacy)
            else if (options.BypassCustomPluginExecution)
            {
                request.Parameters["BypassCustomPluginExecution"] = true;
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
                    var details = JsonConvert.DeserializeObject<List<BulkApiErrorDetail>>(errorDetails.ToString()!);
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
                    var details = JsonConvert.DeserializeObject<List<BulkApiErrorDetail>>(errorDetails.ToString()!);
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
        /// Executes batches in parallel with bounded concurrency.
        /// </summary>
        private static async Task<BulkOperationResult> ExecuteBatchesParallelAsync<T>(
            List<List<T>> batches,
            Func<List<T>, CancellationToken, Task<BulkOperationResult>> executeBatch,
            int maxParallelism,
            ProgressTracker tracker,
            IProgress<ProgressSnapshot>? progress,
            CancellationToken cancellationToken)
        {
            var allErrors = new ConcurrentBag<BulkOperationError>();
            var allCreatedIds = new ConcurrentBag<Guid>();
            var successCount = 0;
            var failureCount = 0;

            await Parallel.ForEachAsync(
                batches,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = maxParallelism,
                    CancellationToken = cancellationToken
                },
                async (batch, ct) =>
                {
                    // Use the combined cancellation token (ct) which includes Parallel.ForEachAsync's internal cancellation
                    var batchResult = await executeBatch(batch, ct).ConfigureAwait(false);

                    Interlocked.Add(ref successCount, batchResult.SuccessCount);
                    Interlocked.Add(ref failureCount, batchResult.FailureCount);

                    foreach (var error in batchResult.Errors)
                    {
                        allErrors.Add(error);
                    }

                    if (batchResult.CreatedIds != null)
                    {
                        foreach (var id in batchResult.CreatedIds)
                        {
                            allCreatedIds.Add(id);
                        }
                    }

                    // Report progress after each batch
                    tracker.RecordProgress(batchResult.SuccessCount, batchResult.FailureCount);
                    progress?.Report(tracker.GetSnapshot());
                }).ConfigureAwait(false);

            return new BulkOperationResult
            {
                SuccessCount = successCount,
                FailureCount = failureCount,
                Errors = allErrors.ToList(),
                Duration = TimeSpan.Zero,
                CreatedIds = allCreatedIds.Count > 0 ? allCreatedIds.ToList() : null
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
    }
}
