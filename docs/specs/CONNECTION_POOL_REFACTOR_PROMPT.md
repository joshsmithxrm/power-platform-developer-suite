# Implementation Prompt: Connection Pool Refactor

Copy this entire prompt into a new Claude Code session.

---

## Context

You are implementing a refactor of the Dataverse connection pool to separate authentication from pooling. The full specification is at:

```
C:\VS\ppds\sdk\docs\specs\CONNECTION_POOL_REFACTOR_SPEC.md
```

**Read this spec file completely before starting any implementation.**

## Branch

Create and work on branch: `feature/connection-source-abstraction`

```bash
git checkout -b feature/connection-source-abstraction
```

## Problem Summary

The current `DataverseConnectionPool` requires `DataverseOptions` with connection string configuration. This forces CLI (device code auth) to use a separate `DeviceCodeConnectionPool` that:
- Doesn't actually pool (clones every request)
- Misses throttle tracking, adaptive rate control, connection validation
- Fails under load when Clone() hits throttle

The fix: separate authentication from pooling via `IConnectionSource` abstraction.

## Implementation Order

**YOU MUST FOLLOW THIS ORDER. Do not skip steps or combine phases.**

### Phase 1: Create Abstraction (3 files)

1. **Create `src/PPDS.Dataverse/Pooling/IConnectionSource.cs`**
   - Interface with: `Name`, `MaxPoolSize`, `GetSeedClient()`, `IDisposable`
   - Full XML documentation as shown in spec

2. **Create `src/PPDS.Dataverse/Pooling/ServiceClientSource.cs`**
   - Wraps pre-authenticated ServiceClient
   - Validates client is ready in constructor
   - Returns same client from GetSeedClient()
   - Disposes client on Dispose()

3. **Create `src/PPDS.Dataverse/Pooling/ConnectionStringSource.cs`**
   - Takes `DataverseConnection` config
   - Creates client lazily on first GetSeedClient() call (thread-safe with lock)
   - Caches and returns same instance on subsequent calls
   - Uses existing `SecretResolver` and `ConnectionStringBuilder`
   - Wraps exceptions in `DataverseConnectionException`

4. **Build to verify compilation:**
   ```bash
   dotnet build src/PPDS.Dataverse/PPDS.Dataverse.csproj
   ```

### Phase 2: Add Unit Tests for Sources

5. **Create `tests/PPDS.Dataverse.Tests/Pooling/ServiceClientSourceTests.cs`**
   - Test all constructor validations
   - Test GetSeedClient returns provided client
   - Test disposal
   - Test ObjectDisposedException after dispose
   - Use Moq to mock ServiceClient where needed

6. **Create `tests/PPDS.Dataverse.Tests/Pooling/ConnectionStringSourceTests.cs`**
   - Test constructor validation
   - Test lazy creation
   - Test same instance returned on multiple calls
   - Test thread safety (optional but good)
   - Test disposal states

7. **Run tests:**
   ```bash
   dotnet test tests/PPDS.Dataverse.Tests
   ```

### Phase 3: Refactor DataverseConnectionPool

8. **Modify `src/PPDS.Dataverse/Pooling/DataverseConnectionPool.cs`**

   Add new fields:
   ```csharp
   private readonly IReadOnlyList<IConnectionSource> _sources;
   private readonly DataversePoolOptions _poolOptions;
   private readonly ConcurrentDictionary<string, ServiceClient> _seedClients = new();
   ```

   Add new primary constructor:
   ```csharp
   public DataverseConnectionPool(
       IEnumerable<IConnectionSource> sources,
       IThrottleTracker throttleTracker,
       IAdaptiveRateController adaptiveRateController,
       DataversePoolOptions poolOptions,
       ILogger<DataverseConnectionPool> logger)
   ```

   This constructor should:
   - Validate sources is not null/empty
   - Store sources as `_sources = sources.ToList().AsReadOnly()`
   - Store poolOptions as `_poolOptions`
   - Initialize pools dictionary keyed by source.Name
   - Initialize activeConnections and requestCounts for each source
   - Calculate total capacity from sum of source.MaxPoolSize
   - Apply performance settings
   - Start validation loop if enabled
   - Initialize minimum connections

   Modify legacy constructor to delegate:
   ```csharp
   public DataverseConnectionPool(
       IOptions<DataverseOptions> options,
       IThrottleTracker throttleTracker,
       IAdaptiveRateController adaptiveRateController,
       ILogger<DataverseConnectionPool> logger)
       : this(
           options.Value.Connections.Select(c => new ConnectionStringSource(c)),
           throttleTracker,
           adaptiveRateController,
           options.Value.Pool,
           logger)
   { }
   ```

   Add GetSeedClient method:
   ```csharp
   private ServiceClient GetSeedClient(string connectionName)
   {
       return _seedClients.GetOrAdd(connectionName, name =>
       {
           var source = _sources.First(s => s.Name == name);
           return source.GetSeedClient();
       });
   }
   ```

   Modify CreateNewConnection to use GetSeedClient and Clone:
   - Remove connection string building logic (now in ConnectionStringSource)
   - Call `var seed = GetSeedClient(connectionName)`
   - Clone: `var serviceClient = seed.Clone()`
   - Wrap Clone in try/catch, throw DataverseConnectionException on failure
   - Keep existing: disable affinity cookie, set MaxRetryCount = 0
   - Keep existing: wrap in DataverseClient then PooledClient

   Update validation:
   - Remove ValidateConnection (config validation) - sources validate themselves
   - Add validation that sources is not empty
   - Keep WarnIfMultipleOrganizations but get URLs from sources

   Update disposal:
   - In Dispose() and DisposeAsync(), dispose all sources
   - Dispose seed clients from _seedClients

   Remove `_options` field usage where it was used for connections - use `_sources` instead.
   Keep `_poolOptions` for pool configuration (MinPoolSize, MaxIdleTime, etc.)

9. **Build to verify:**
   ```bash
   dotnet build src/PPDS.Dataverse/PPDS.Dataverse.csproj
   ```

### Phase 4: Update Pool Tests

10. **Update `tests/PPDS.Dataverse.Tests/Pooling/DataverseConnectionPoolTests.cs`**
    - Add tests for new constructor with IConnectionSource[]
    - Verify existing tests still pass (they use legacy constructor)
    - Add test that CreateNewConnection uses cached seed
    - Add test that Dispose disposes sources

11. **Run all tests:**
    ```bash
    dotnet test tests/PPDS.Dataverse.Tests
    ```

### Phase 5: Update CLI

12. **Modify `src/PPDS.Migration.Cli/Commands/ImportCommand.cs`**

    Find where DeviceCodeConnectionPool is created and replace with:
    ```csharp
    var source = new ServiceClientSource(serviceClient, "Interactive", maxPoolSize: 10);
    var pool = new DataverseConnectionPool(
        new[] { source },
        throttleTracker,
        adaptiveRateController,
        new DataversePoolOptions
        {
            MinPoolSize = 2,
            Enabled = true,
            DisableAffinityCookie = true
        },
        logger);
    ```

    You'll need to create/inject throttleTracker, adaptiveRateController, and logger.
    Check how these are created elsewhere in the CLI or create simple instances.

13. **Modify `src/PPDS.Migration.Cli/Commands/ExportCommand.cs`**
    - Same pattern as ImportCommand

14. **Delete `src/PPDS.Migration.Cli/Infrastructure/DeviceCodeConnectionPool.cs`**
    - This file contains DeviceCodeConnectionPool and DeviceCodePooledClient
    - Both are no longer needed

15. **Build CLI:**
    ```bash
    dotnet build src/PPDS.Migration.Cli/PPDS.Migration.Cli.csproj
    ```

16. **Fix any compilation errors** from the deletion

### Phase 6: Final Verification

17. **Run all tests:**
    ```bash
    dotnet test
    ```

18. **Build in Release mode:**
    ```bash
    dotnet build -c Release
    ```

19. **Verify CLI runs:**
    ```bash
    dotnet run --project src/PPDS.Migration.Cli -- --help
    ```

## Critical Implementation Details

### IConnectionSource.GetSeedClient()

- Called once per source, result cached in pool's `_seedClients`
- Must be thread-safe (pool may call from multiple threads during init)
- Must throw `DataverseConnectionException` on failure, not raw exceptions

### ConnectionStringSource Thread Safety

```csharp
public ServiceClient GetSeedClient()
{
    if (_client != null) return _client;

    lock (_lock)
    {
        if (_client != null) return _client;  // Double-check

        // Create client...
        _client = client;
        return _client;
    }
}
```

### Pool Initialization Order

In the new constructor:
1. Validate arguments (sources not null/empty)
2. Store sources, poolOptions, throttleTracker, adaptiveRateController, logger
3. Initialize dictionaries (_pools, _activeConnections, _requestCounts) for each source
4. Calculate _totalPoolCapacity from sources
5. Create _connectionSemaphore
6. Create _selectionStrategy
7. Apply performance settings
8. Start validation loop
9. Initialize minimum connections (calls CreateNewConnection which calls GetSeedClient)

### Source Disposal

In pool's Dispose/DisposeAsync:
```csharp
// Dispose seed clients
foreach (var seed in _seedClients.Values)
{
    seed.Dispose();
}
_seedClients.Clear();

// Dispose sources
foreach (var source in _sources)
{
    source.Dispose();
}
```

**Important:** Sources own the original ServiceClient. When disposed, they dispose it. The pool also disposes cloned seeds from _seedClients. Don't double-dispose.

Actually, re-reading the design:
- `ServiceClientSource` disposes its wrapped client
- `ConnectionStringSource` disposes its lazily-created client
- The pool's `_seedClients` contains the SAME instances returned by sources
- So the pool should NOT dispose from _seedClients - let sources handle it

Corrected disposal:
```csharp
// Clear seed cache (sources will dispose the actual clients)
_seedClients.Clear();

// Dispose sources (which dispose their clients)
foreach (var source in _sources)
{
    source.Dispose();
}
```

### Removing _options Dependency

The old code used `_options.Connections` for:
1. Iterating connections to initialize pools → use `_sources`
2. Getting connection config in CreateNewConnection → no longer needed, use GetSeedClient
3. Validation → sources validate themselves
4. Pool settings → use `_poolOptions`

The old code used `_options.Pool` for pool settings → use `_poolOptions`

## Do NOT

- Do not modify `IDataverseConnectionPool` interface
- Do not change the public API of `DataverseConnectionPool` beyond adding the new constructor
- Do not remove the legacy constructor (just add Obsolete attribute if you want)
- Do not change how `PooledClient` works
- Do not change throttle tracking or adaptive rate control logic
- Do not change connection selection strategies

## Commit Strategy

Make atomic commits after each phase:

1. After Phase 1: `feat(Pooling): add IConnectionSource abstraction`
2. After Phase 2: `test(Pooling): add unit tests for connection sources`
3. After Phase 3: `refactor(Pooling): DataverseConnectionPool uses IConnectionSource`
4. After Phase 4: `test(Pooling): update pool tests for new constructor`
5. After Phase 5: `refactor(CLI): use DataverseConnectionPool instead of DeviceCodeConnectionPool`
6. After Phase 6: `chore: cleanup and final verification`

Then push:
```bash
git push -u origin feature/connection-source-abstraction
```

## Verification Checklist

Before considering this complete, verify:

- [ ] `IConnectionSource` interface exists with correct members
- [ ] `ServiceClientSource` validates client readiness in constructor
- [ ] `ConnectionStringSource` creates client lazily and thread-safely
- [ ] `DataverseConnectionPool` has new constructor accepting `IEnumerable<IConnectionSource>`
- [ ] Legacy constructor still works (delegates to new constructor)
- [ ] `CreateNewConnection` clones from seed, doesn't create from config
- [ ] `DeviceCodeConnectionPool.cs` is deleted
- [ ] CLI commands use `DataverseConnectionPool` with `ServiceClientSource`
- [ ] All unit tests pass
- [ ] CLI builds and runs

## Questions?

If anything in the spec is unclear, read the spec file again. The spec has complete interface definitions with full XML documentation. Follow them exactly.

If you encounter a decision not covered by the spec:
1. Make the simplest choice that doesn't break anything
2. Add a comment noting the decision
3. Continue with implementation
