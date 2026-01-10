// Pattern: Connection Pool Usage
// Demonstrates: Pool acquisition inside loops, DOP from server, semaphore gating
// Related: ADR-0002, ADR-0005, CLAUDE.md "Use connection pool"
// Source: src/PPDS.Dataverse/Pooling/DataverseConnectionPool.cs

// KEY PRINCIPLES:
// 1. Get client INSIDE parallel loop - don't hold slots during entire batch
// 2. Use pool.GetTotalRecommendedParallelism() - never guess parallelism
// 3. Pool manages concurrency via semaphore - respect it
// 4. Clone for read-heavy, pool for write-heavy operations

using PPDS.Dataverse.Pooling;

// CORRECT: Client acquired inside loop
public async Task<List<Entity>> FetchEntitiesParallelAsync(
    IDataverseConnectionPool pool,
    IReadOnlyList<string> entityLogicalNames,
    CancellationToken cancellationToken)
{
    var results = new ConcurrentBag<Entity>();

    // PATTERN: Get parallelism from pool DOP
    var parallelism = pool.GetTotalRecommendedParallelism();

    await Parallel.ForEachAsync(
        entityLogicalNames,
        new ParallelOptions
        {
            MaxDegreeOfParallelism = parallelism,
            CancellationToken = cancellationToken
        },
        async (entityName, ct) =>
        {
            // CRITICAL: Acquire client INSIDE the parallel loop
            // This allows pool to manage concurrency properly
            await using var client = await pool.GetClientAsync(ct);

            var query = new QueryExpression(entityName)
            {
                ColumnSet = new ColumnSet(true)
            };

            var entities = await client.RetrieveMultipleAsync(query, ct);

            foreach (var entity in entities.Entities)
            {
                results.Add(entity);
            }
        });

    return results.ToList();
}

// WRONG: Client acquired outside loop (holds slot entire time)
public async Task<List<Entity>> FetchEntitiesWrongAsync(
    IDataverseConnectionPool pool,
    IReadOnlyList<string> entityLogicalNames,
    CancellationToken cancellationToken)
{
    // BAD: This holds one pool slot for the entire parallel operation
    await using var client = await pool.GetClientAsync(cancellationToken);

    var results = new ConcurrentBag<Entity>();

    await Parallel.ForEachAsync(
        entityLogicalNames,
        async (entityName, ct) =>
        {
            // BAD: Using same client for all parallel operations
            // This serializes all requests through one connection
            var entities = await client.RetrieveMultipleAsync(
                new QueryExpression(entityName), ct);

            foreach (var entity in entities.Entities)
            {
                results.Add(entity);
            }
        });

    return results.ToList();
}

// POOL INTERFACE: What the pool provides
public interface IDataverseConnectionPool : IAsyncDisposable
{
    /// <summary>
    /// Get a client from the pool. Client must be disposed to return to pool.
    /// </summary>
    Task<IPooledClient> GetClientAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Get recommended parallelism from server DOP settings.
    /// Sum of all connection sources' RecommendedDegreesOfParallelism.
    /// </summary>
    int GetTotalRecommendedParallelism();

    /// <summary>
    /// Batch coordinator for managing parallel batch operations.
    /// </summary>
    IBatchCoordinator BatchCoordinator { get; }
}

// POOLED CLIENT: Wrapper that returns to pool on dispose
public interface IPooledClient : IAsyncDisposable
{
    Task<TResponse> ExecuteAsync<TResponse>(
        OrganizationRequest request,
        CancellationToken cancellationToken = default)
        where TResponse : OrganizationResponse;

    Task<EntityCollection> RetrieveMultipleAsync(
        QueryBase query,
        CancellationToken cancellationToken = default);

    // Clone for read-only operations (doesn't consume pool slot)
    ServiceClient Clone();
}

// WHEN TO USE POOL VS CLONE:
//
// USE POOL when:
// - Write operations (Create, Update, Delete)
// - Bulk operations (CreateMultiple, UpdateMultiple)
// - Mixed read/write workloads
// - When you need server DOP recommendations
//
// USE CLONE when:
// - Read-only operations
// - Many small reads in parallel
// - You already have a client and need more
//
// Example of clone usage:
public async Task<List<Entity>> FetchWithCloneAsync(
    ServiceClient baseClient,
    IReadOnlyList<Guid> ids,
    CancellationToken cancellationToken)
{
    var results = new ConcurrentBag<Entity>();

    await Parallel.ForEachAsync(
        ids,
        new ParallelOptions { MaxDegreeOfParallelism = 10 },
        async (id, ct) =>
        {
            // Clone is fast and doesn't consume pool slots
            using var clone = baseClient.Clone();
            var entity = await clone.RetrieveAsync("account", id, new ColumnSet(true), ct);
            results.Add(entity);
        });

    return results.ToList();
}

// BATCH COORDINATOR: For managing batches within bulk operations
public interface IBatchCoordinator
{
    /// <summary>
    /// Acquire a slot for batch execution. Limits concurrent batches pool-wide.
    /// </summary>
    Task<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken = default);
}

// Usage in BulkOperationExecutor:
await Parallel.ForEachAsync(
    batches,
    new ParallelOptions { MaxDegreeOfParallelism = parallelism },
    async (batch, ct) =>
    {
        // Acquire batch slot first (limits concurrent batches)
        await using var slot = await _pool.BatchCoordinator.AcquireAsync(ct);

        // Then acquire client (limits concurrent connections)
        await using var client = await _pool.GetClientAsync(ct);

        // Execute batch
        await ExecuteBatchAsync(client, batch, ct);
    });

// ANTI-PATTERNS TO AVOID:
//
// BAD: Hardcoded parallelism
// var parallelism = 4;  // WRONG
// var parallelism = Environment.ProcessorCount;  // WRONG
// var parallelism = pool.GetTotalRecommendedParallelism();  // CORRECT
//
// BAD: Client outside loop
// await using var client = await pool.GetClientAsync();
// await Parallel.ForEachAsync(..., async (item, ct) => {
//     await client.ExecuteAsync(...);  // WRONG - serializes through one connection
// });
//
// BAD: Never releasing clients
// var client = await pool.GetClientAsync();
// // ... use client
// // WRONG - never disposed, pool slot never returned
//
// BAD: Ignoring cancellation
// await Parallel.ForEachAsync(..., async (item, _) => {
//     await pool.GetClientAsync();  // WRONG - pass cancellation token
// });
