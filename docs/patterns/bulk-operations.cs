// Pattern: Bulk Operations with Parallelism
// Demonstrates: BulkOperationExecutor usage, pool DOP, parallel batching
// Related: ADR-0002, ADR-0005, CLAUDE.md "Use bulk APIs"
// Source: src/PPDS.Dataverse/BulkOperations/BulkOperationExecutor.cs
// NOTE: This is an illustrative pattern showing key concepts. For exact API
// signatures and method names, refer to the source files. The patterns here
// demonstrate the correct approach (get client inside loop, use pool DOP,
// handle partial failures) rather than exact copy-paste code.

// KEY PRINCIPLES:
// 1. Use pool.GetTotalRecommendedParallelism() - never guess parallelism
// 2. Get client INSIDE parallel loop - don't hold slots during entire batch
// 3. Handle throttling, auth, connection, and deadlock errors differently
// 4. Use thread-safe aggregation (Interlocked, ConcurrentBag)

using PPDS.Dataverse.Pooling;

public class BulkOperationExample
{
    private readonly IDataverseConnectionPool _connectionPool;

    public BulkOperationExample(IDataverseConnectionPool connectionPool)
    {
        _connectionPool = connectionPool;
    }

    public async Task<BulkOperationResult> CreateRecordsAsync(
        string entityLogicalName,
        IReadOnlyList<Entity> entities,
        IProgressReporter? progress = null,
        CancellationToken cancellationToken = default)
    {
        // PATTERN: Get parallelism from pool, not hardcoded
        var parallelism = _connectionPool.GetTotalRecommendedParallelism();

        // Create batches (1000 records max per CreateMultiple call)
        var batches = entities.Chunk(1000).ToList();

        // Track results thread-safely
        var successCount = 0;
        var failedRecords = new ConcurrentBag<FailedRecord>();

        if (batches.Count <= 1 || parallelism <= 1)
        {
            // Sequential execution for single batch
            foreach (var batch in batches)
            {
                var result = await ExecuteBatchAsync(entityLogicalName, batch, cancellationToken);
                successCount += result.SuccessCount;

                // PATTERN: Aggregate failed records from sequential execution too
                foreach (var failed in result.FailedRecords)
                {
                    failedRecords.Add(failed);
                }
            }
        }
        else
        {
            // PATTERN: Parallel execution with pool-managed concurrency
            await Parallel.ForEachAsync(
                batches,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = parallelism,
                    CancellationToken = cancellationToken
                },
                async (batch, ct) =>
                {
                    // CRITICAL: Get client INSIDE the parallel loop
                    // This allows the pool to manage concurrency properly
                    var result = await ExecuteBatchAsync(entityLogicalName, batch, ct);

                    // Thread-safe result aggregation
                    Interlocked.Add(ref successCount, result.SuccessCount);

                    foreach (var failed in result.FailedRecords)
                    {
                        failedRecords.Add(failed);
                    }
                });
        }

        progress?.Report(new ProgressUpdate($"Created {successCount} records"));

        return new BulkOperationResult
        {
            SuccessCount = successCount,
            FailedRecords = failedRecords.ToList()
        };
    }

    private async Task<BatchResult> ExecuteBatchAsync(
        string entityLogicalName,
        IEnumerable<Entity> batch,
        CancellationToken cancellationToken)
    {
        // PATTERN: Acquire client from pool for this batch only
        await using var client = await _connectionPool.GetClientAsync(cancellationToken);

        try
        {
            var batchList = batch.ToList();
            var request = new CreateMultipleRequest
            {
                Targets = new EntityCollection(batchList)
            };

            // PATTERN: Cast response to specific type to access Results
            var response = (CreateMultipleResponse)await client.ExecuteAsync(request, cancellationToken);

            // PATTERN: Handle partial failures - CreateMultiple can succeed partially
            // Check each response item for faults
            var failedInBatch = new List<FailedRecord>();
            var successInBatch = 0;

            if (response.Responses != null)
            {
                foreach (var responseItem in response.Responses)
                {
                    if (responseItem.Fault != null)
                    {
                        // Map failed item back using RequestIndex
                        var originalEntity = batchList[responseItem.RequestIndex];
                        failedInBatch.Add(new FailedRecord(originalEntity, responseItem.Fault));
                    }
                    else
                    {
                        successInBatch++;
                    }
                }
            }
            else
            {
                // All succeeded (older API versions may not return Responses)
                successInBatch = batchList.Count;
            }

            return new BatchResult { SuccessCount = successInBatch, FailedRecords = failedInBatch };
        }
        catch (FaultException<OrganizationServiceFault> ex)
        {
            // PATTERN: Different retry strategies for different errors
            if (IsThrottleError(ex))
            {
                // Throttle: wait and retry (infinite retries)
                await Task.Delay(GetRetryAfter(ex), cancellationToken);
                return await ExecuteBatchAsync(entityLogicalName, batch, cancellationToken);
            }

            if (IsAuthError(ex))
            {
                // Auth: limited retries, may need re-auth
                throw new PpdsAuthException(
                    ErrorCodes.Auth.TokenExpired,
                    "Authentication failed during bulk operation",
                    ex);
            }

            if (IsDeadlockError(ex))
            {
                // Deadlock: retry with backoff
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                return await ExecuteBatchAsync(entityLogicalName, batch, cancellationToken);
            }

            throw;
        }
    }

    private static bool IsThrottleError(FaultException<OrganizationServiceFault> ex)
        => ex.Detail?.ErrorCode == -2147015902; // 0x80072322

    private static bool IsAuthError(FaultException<OrganizationServiceFault> ex)
        => ex.Detail?.ErrorCode == -2147180286; // 0x80040222

    private static bool IsDeadlockError(FaultException<OrganizationServiceFault> ex)
        => ex.Message.Contains("deadlock", StringComparison.OrdinalIgnoreCase);

    private static TimeSpan GetRetryAfter(FaultException<OrganizationServiceFault> ex)
        => TimeSpan.FromSeconds(
            int.TryParse(ex.Detail?.InnerFault?.Message, out var seconds)
                ? seconds
                : 30);
}

// ANTI-PATTERNS TO AVOID:
//
// BAD: Hardcoded parallelism
// var parallelism = Environment.ProcessorCount;  // WRONG - use pool DOP
//
// BAD: Client outside parallel loop
// var client = await pool.GetClientAsync();  // WRONG - holds slot entire time
// await Parallel.ForEachAsync(..., async (batch, ct) => {
//     await client.ExecuteAsync(...);  // Uses same client for all batches
// });
//
// BAD: Non-thread-safe aggregation
// var count = 0;
// await Parallel.ForEachAsync(..., async (batch, ct) => {
//     count += result.Count;  // WRONG - race condition
// });
