# Query Parity Implementation Plan

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Align daemon query RPC handlers to use `SqlQueryService`, integrate query hints, surface environment colors, and add cross-environment attribution.

**Architecture:** Delete the daemon's bespoke query pipeline (inline transpilation, DML safety, direct executor calls). Replace with calls to the shared `SqlQueryService` that TUI/CLI already use. Then integrate `QueryHintParser` into the shared service so all interfaces get hints for free. Finally, add VS Code environment color rendering and cross-env data source banners.

**Tech Stack:** C# (.NET 8+), TypeScript (VS Code extension), xUnit + Moq (tests), Vitest (extension tests), CSS custom properties

**Spec:** [`specs/query-parity.md`](../../specs/query-parity.md)

---

## File Structure

### Phase 1: Daemon Uses SqlQueryService

| Action | File | Responsibility |
|--------|------|----------------|
| Modify | `src/PPDS.Cli/Commands/Serve/Handlers/RpcMethodHandler.cs` | Replace `QuerySqlAsync`, `QueryExplainAsync`, `QueryExportAsync` to use `SqlQueryService`; delete dead code |
| Modify | `src/PPDS.Cli/Services/ServiceRegistration.cs` | Ensure `ISqlQueryService` is registered in daemon's service provider |
| Modify | `tests/PPDS.Cli.Tests/Commands/Serve/Handlers/RpcMethodHandlerTests.cs` | Add tests for shared service usage and error mapping |

### Phase 2: Query Hints Integration

| Action | File | Responsibility |
|--------|------|----------------|
| Modify | `src/PPDS.Cli/Services/Query/SqlQueryService.cs` | Call `QueryHintParser.Parse()` in `PrepareExecutionAsync` and `ExplainAsync`, apply overrides |
| Modify | `src/PPDS.Dataverse/Query/Planning/QueryPlanOptions.cs` | Add `ForceClientAggregation` and `NoLock` properties |
| Create | `src/PPDS.Dataverse/Query/QueryExecutionOptions.cs` | New type: `BypassPlugins`, `BypassFlows` |
| Modify | `src/PPDS.Dataverse/Query/Planning/QueryPlanContext.cs` | Add `ExecutionOptions` constructor parameter |
| Modify | `src/PPDS.Dataverse/Query/IQueryExecutor.cs` | Add default-implementation overload accepting `QueryExecutionOptions?` |
| Modify | `src/PPDS.Dataverse/Query/QueryExecutor.cs` | Override new overload to apply bypass headers |
| Modify | `src/PPDS.Dataverse/Query/Planning/Nodes/FetchXmlScanNode.cs` | Inject `no-lock="true"` when NoLock is set; pass execution options to executor overload |
| Modify | `src/PPDS.Query/Planning/ExecutionPlanBuilder.cs` | Route to client-side aggregation when `ForceClientAggregation` is true |
| Modify | `tests/PPDS.Cli.Tests/Services/Query/SqlQueryServiceTests.cs` | Hint integration tests (AC-04 through AC-13, AC-21) |
| Modify | `tests/PPDS.Dataverse.Tests/Query/Planning/Nodes/FetchXmlScanNodeTests.cs` | NoLock injection tests |
| Modify | `tests/PPDS.Query.Tests/Planning/ExecutionPlanBuilderTests.cs` | HASH_GROUP test |

### Phase 3: VS Code Environment Colors

| Action | File | Responsibility |
|--------|------|----------------|
| Modify | `src/PPDS.Extension/src/panels/webview/shared/message-types.ts` | Add `envColor` to `updateEnvironment` messages |
| Modify | `src/PPDS.Extension/src/panels/QueryPanel.ts` | Fetch and send `envColor` in `updateEnvironment` |
| Modify | `src/PPDS.Extension/src/panels/SolutionsPanel.ts` | Fetch and send `envColor` in `updateEnvironment` |
| Modify | `src/PPDS.Extension/src/panels/webview/query-panel/query-panel.ts` | Apply `data-env-color` attribute on toolbar |
| Modify | `src/PPDS.Extension/src/panels/webview/shared/shared.css` | Add environment color CSS custom properties and left border rules |

### Phase 4: Cross-Environment Query Attribution

| Action | File | Responsibility |
|--------|------|----------------|
| Create | `src/PPDS.Cli/Services/Query/QueryDataSource.cs` | New record type |
| Modify | `src/PPDS.Cli/Services/Query/SqlQueryResult.cs` | Add `DataSources` property |
| Modify | `src/PPDS.Cli/Services/Query/SqlQueryService.cs` | Collect data sources from plan tree |
| Modify | `src/PPDS.Cli/Commands/Serve/Handlers/RpcMethodHandler.cs` | Add `dataSources` to `QueryResultResponse` DTO |
| Modify | `src/PPDS.Extension/src/panels/webview/query-panel/query-panel.ts` | Render cross-env banner |
| Modify | `src/PPDS.Extension/src/panels/styles/query-panel.css` | Banner CSS |
| Modify | `tests/PPDS.Cli.Tests/Services/Query/SqlQueryServiceTests.cs` | Data source collection tests |

---

## Chunk 1: Phase 1 — Daemon Uses SqlQueryService

### Task 1: Wire SqlQueryService into QuerySqlAsync

**Files:**
- Modify: `src/PPDS.Cli/Commands/Serve/Handlers/RpcMethodHandler.cs:942-1073`
- Reference: `src/PPDS.Cli/Tui/InteractiveSession.cs:321-355`

- [ ] **Step 1: Write a helper method to configure SqlQueryService**

Add a private method in `RpcMethodHandler` that mirrors `InteractiveSession.GetSqlQueryServiceAsync()`. This method gets the service from the provider, casts to concrete `SqlQueryService`, and wires `RemoteExecutorFactory`, `ProfileResolver`, `EnvironmentSafetySettings`, and `EnvironmentProtectionLevel`.

```csharp
private async Task<SqlQueryService> GetConfiguredSqlQueryServiceAsync(
    IServiceProvider sp,
    string? environmentUrl,
    CancellationToken cancellationToken)
{
    var service = sp.GetRequiredService<ISqlQueryService>();
    if (service is not SqlQueryService concrete)
        throw new InvalidOperationException("ISqlQueryService must be SqlQueryService");

    // Wire cross-environment support
    var envConfigs = await _envConfigStore.GetAllConfigsAsync(cancellationToken)
        .ConfigureAwait(false);
    var resolver = new ProfileResolutionService(envConfigs);
    concrete.RemoteExecutorFactory = label =>
    {
        var config = resolver.ResolveByLabel(label);
        if (config?.Url == null) return null;
        var remoteProvider = GetServiceProviderForEnvironmentAsync(config.Url)
            .GetAwaiter().GetResult();
        return remoteProvider.GetRequiredService<IQueryExecutor>();
    };
    concrete.ProfileResolver = resolver;

    // Wire environment-specific safety settings
    if (environmentUrl != null)
    {
        var envConfig = await _envConfigStore.GetConfigAsync(environmentUrl, cancellationToken)
            .ConfigureAwait(false);
        if (envConfig != null)
        {
            concrete.EnvironmentSafetySettings = envConfig.SafetySettings;
            var envType = envConfig.Type ?? EnvironmentType.Unknown;
            concrete.EnvironmentProtectionLevel = envConfig.Protection
                ?? DmlSafetyGuard.DetectProtectionLevel(envType);
        }
    }

    return concrete;
}
```

Note: Check how `_envConfigStore` and `GetServiceProviderForEnvironmentAsync` are available in `RpcMethodHandler`. The daemon's `WithProfileAndEnvironmentAsync` provides a service provider — the helper may need to accept it as a parameter. Match the exact patterns from `InteractiveSession`.

- [ ] **Step 2: Replace QuerySqlAsync execute path**

Replace the entire body of `QuerySqlAsync` (lines 942-1073) with the new flow:

```csharp
[JsonRpcMethod("query/sql")]
public async Task<QueryResultResponse> QuerySqlAsync(
    QuerySqlRequest request,
    CancellationToken cancellationToken = default)
{
    if (string.IsNullOrWhiteSpace(request.Sql))
    {
        throw new RpcException(
            ErrorCodes.Validation.RequiredField,
            "The 'sql' parameter is required");
    }

    // showFetchXml mode: transpile only, no execution
    if (request.ShowFetchXml)
    {
        return await WithProfileAndEnvironmentAsync(request.EnvironmentUrl, async (sp, ct) =>
        {
            var service = sp.GetRequiredService<ISqlQueryService>();
            var fetchXml = service.TranspileSql(request.Sql, request.Top);
            return new QueryResultResponse
            {
                Success = true,
                ExecutedFetchXml = fetchXml,
                QueryMode = "transpile",
            };
        }, cancellationToken);
    }

    // Execute mode: delegate to SqlQueryService
    var response = await WithProfileAndEnvironmentAsync(request.EnvironmentUrl, async (sp, ct) =>
    {
        var service = await GetConfiguredSqlQueryServiceAsync(sp, request.EnvironmentUrl, ct);

        var sqlRequest = new SqlQueryRequest
        {
            Sql = request.Sql,
            TopOverride = request.Top,
            PageNumber = request.Page,
            PagingCookie = request.PagingCookie,
            IncludeCount = request.Count,
            UseTdsEndpoint = request.UseTds,
            DmlSafety = request.DmlSafety != null
                ? new DmlSafetyOptions
                {
                    IsConfirmed = request.DmlSafety.IsConfirmed,
                    IsDryRun = request.DmlSafety.IsDryRun,
                    NoLimit = request.DmlSafety.NoLimit,
                    RowCap = request.DmlSafety.RowCap,
                }
                : null,
        };

        var result = await service.ExecuteAsync(sqlRequest, ct);
        var mapped = MapToResponse(result.Result, result.TranspiledFetchXml);
        mapped.QueryMode = result.Result.IsAggregate ? "aggregate" : "dataverse";
        return mapped;
    }, cancellationToken);

    FireAndForgetHistorySave(request.Sql, response);
    return response;
}
```

- [ ] **Step 3: Add error mapping try/catch**

Wrap the `service.ExecuteAsync()` call in a try/catch that maps service exceptions to RPC error codes:

```csharp
try
{
    var result = await service.ExecuteAsync(sqlRequest, ct);
    // ... map response
}
catch (QueryParseException ex)
{
    throw new RpcException(ErrorCodes.Query.ParseError, ex.Message, ex);
}
catch (PpdsException ex) when (ex.ErrorCode == PpdsErrorCode.DmlBlocked)
{
    throw new RpcException(
        ErrorCodes.Query.DmlBlocked,
        ex.Message,
        new DmlSafetyErrorData
        {
            Code = ErrorCodes.Query.DmlBlocked,
            Message = ex.Message,
            DmlBlocked = true,
        });
}
catch (PpdsException ex) when (ex.ErrorCode == PpdsErrorCode.DmlConfirmationRequired)
{
    throw new RpcException(
        ErrorCodes.Query.DmlConfirmationRequired,
        ex.Message,
        new DmlSafetyErrorData
        {
            Code = ErrorCodes.Query.DmlConfirmationRequired,
            Message = ex.Message,
            DmlConfirmationRequired = true,
        });
}
catch (PpdsException ex)
{
    throw new RpcException(ErrorCodes.Query.ExecutionFailed, ex.Message, ex);
}
```

Note: Verify the exact `PpdsErrorCode` enum values — check `src/PPDS.Cli/Infrastructure/Errors/PpdsException.cs` for the DML-related error codes. The `when` clauses filter on specific error codes.

- [ ] **Step 4: Run typecheck to verify compilation**

Run: `dotnet build PPDS.sln -v q`
Expected: Build succeeds with no errors. Warnings are OK.

- [ ] **Step 5: Commit**

```
git add src/PPDS.Cli/Commands/Serve/Handlers/RpcMethodHandler.cs
git commit -m "feat(daemon): replace QuerySqlAsync inline pipeline with SqlQueryService

Wire RemoteExecutorFactory, ProfileResolver, and safety settings on the
service instance. Map SqlQueryService exceptions to existing RPC error
codes. showFetchXml mode uses service.TranspileSql()."
```

---

### Task 2: Align QueryExplainAsync to use SqlQueryService

**Files:**
- Modify: `src/PPDS.Cli/Commands/Serve/Handlers/RpcMethodHandler.cs:1247-1291`

- [ ] **Step 1: Replace QueryExplainAsync body**

The current handler instantiates `QueryParser`, `ExecutionPlanBuilder`, and `FetchXmlGeneratorService` inline. Replace with `SqlQueryService.ExplainAsync()`:

```csharp
[JsonRpcMethod("query/explain")]
public async Task<QueryExplainResponse> QueryExplainAsync(
    QueryExplainRequest request,
    CancellationToken cancellationToken = default)
{
    if (string.IsNullOrWhiteSpace(request.Sql))
    {
        throw new RpcException(
            ErrorCodes.Validation.RequiredField,
            "The 'sql' parameter is required");
    }

    return await WithProfileAndEnvironmentAsync(request.EnvironmentUrl, async (sp, ct) =>
    {
        var service = await GetConfiguredSqlQueryServiceAsync(sp, request.EnvironmentUrl, ct);
        var plan = await service.ExplainAsync(request.Sql, ct);
        var fetchXml = service.TranspileSql(request.Sql);

        return new QueryExplainResponse
        {
            Success = true,
            Plan = plan.Description,
            FetchXml = fetchXml,
        };
    }, cancellationToken);
}
```

Note: Verify `QueryExplainResponse` DTO structure — check the existing response fields match what `ExplainAsync` returns.

- [ ] **Step 2: Run typecheck**

Run: `dotnet build PPDS.sln -v q`

- [ ] **Step 3: Commit**

```
git add src/PPDS.Cli/Commands/Serve/Handlers/RpcMethodHandler.cs
git commit -m "feat(daemon): replace QueryExplainAsync with SqlQueryService.ExplainAsync"
```

---

### Task 3: Align QueryExportAsync SQL path

**Files:**
- Modify: `src/PPDS.Cli/Commands/Serve/Handlers/RpcMethodHandler.cs:1162-1240`

- [ ] **Step 1: Replace SQL transpilation path in QueryExportAsync**

The export handler has two paths: SQL input and FetchXML input. Only the SQL path changes — replace the `TranspileSqlToFetchXml()` call with `service.TranspileSql()`. The FetchXML path stays as-is.

```csharp
// In QueryExportAsync, replace the SQL transpilation:
// OLD: var fetchXml = TranspileSqlToFetchXml(request.Sql, request.Top);
// NEW:
var service = sp.GetRequiredService<ISqlQueryService>();
var fetchXml = service.TranspileSql(request.Sql, request.Top);
```

Note: The export handler's paging loop (manual `ExecuteFetchXmlAsync` calls) can stay for now — export needs custom streaming behavior that `ExecuteAsync` doesn't provide. The key fix is removing the `TranspileSqlToFetchXml()` dependency.

- [ ] **Step 2: Run typecheck**

Run: `dotnet build PPDS.sln -v q`

- [ ] **Step 3: Commit**

```
git add src/PPDS.Cli/Commands/Serve/Handlers/RpcMethodHandler.cs
git commit -m "feat(daemon): replace export SQL transpilation with SqlQueryService.TranspileSql"
```

---

### Task 4: Delete dead code

**Files:**
- Modify: `src/PPDS.Cli/Commands/Serve/Handlers/RpcMethodHandler.cs`

- [ ] **Step 1: Delete TranspileSqlToFetchXml() method (lines 1339-1370)**

Remove the entire private method. All callers now use `SqlQueryService.TranspileSql()`.

- [ ] **Step 2: Delete InjectTopAttribute() method (lines 1456-1473)**

Remove the entire private method. TOP injection is handled by `SqlQueryService.TranspileSql()`.

- [ ] **Step 3: Delete inline DML safety check block (lines 954-1002)**

This was already removed in Task 1 when we replaced `QuerySqlAsync`. Verify it's gone.

- [ ] **Step 4: Remove unused imports**

Remove `using Microsoft.SqlServer.TransactSql.ScriptDom;` (line 24) and `using PPDS.Query.Transpilation;` (line 29) if no other code in the file uses them. Search the file for any remaining references before removing.

- [ ] **Step 5: Run typecheck to verify nothing is broken**

Run: `dotnet build PPDS.sln -v q`
Expected: Build succeeds. If there are compilation errors, some caller still references a deleted method — find and fix.

- [ ] **Step 6: Run existing tests**

Run: `dotnet test PPDS.sln --filter "Category!=Integration" -v q`
Expected: All existing tests pass.

- [ ] **Step 7: Commit**

```
git add src/PPDS.Cli/Commands/Serve/Handlers/RpcMethodHandler.cs
git commit -m "chore(daemon): delete dead code — TranspileSqlToFetchXml, InjectTopAttribute, unused imports

These methods are replaced by SqlQueryService.TranspileSql() and are no
longer called by any RPC handler."
```

---

### Task 5: Write Phase 1 tests

**Files:**
- Modify: `tests/PPDS.Cli.Tests/Commands/Serve/Handlers/RpcMethodHandlerTests.cs`
- Reference: `tests/PPDS.Cli.Tests/Mocks/FakeSqlQueryService.cs`

- [ ] **Step 1: Write test for error mapping**

Test that when `SqlQueryService` throws `QueryParseException`, the daemon maps it to `ErrorCodes.Query.ParseError`:

```csharp
[Fact]
[Trait("Category", "Unit")]
public async Task QuerySql_ParseError_MapsToRpcException()
{
    // Arrange: configure FakeSqlQueryService to throw QueryParseException
    // Act: call QuerySqlAsync
    // Assert: RpcException with ErrorCodes.Query.ParseError
}
```

Note: Check how `RpcMethodHandler` is instantiated in existing pool tests (`RpcMethodHandlerPoolTests.cs`) — follow the same pattern with `Mock<IDaemonConnectionPoolManager>`. If the handler requires a real `WithProfileAndEnvironmentAsync` that resolves service providers, you may need to mock the pool manager to return a provider with `FakeSqlQueryService` registered.

- [ ] **Step 2: Write test for DML blocked error mapping**

```csharp
[Fact]
[Trait("Category", "Unit")]
public async Task QuerySql_DmlBlocked_MapsToRpcExceptionWithSafetyData()
{
    // Arrange: FakeSqlQueryService throws PpdsException with DmlBlocked ErrorCode
    // Act: call QuerySqlAsync with DML safety enabled
    // Assert: RpcException with DmlSafetyErrorData.DmlBlocked = true
}
```

- [ ] **Step 3: Run tests**

Run: `dotnet test PPDS.sln --filter "Category!=Integration" -v q`
Expected: New tests pass.

- [ ] **Step 4: Commit**

```
git add tests/PPDS.Cli.Tests/Commands/Serve/Handlers/RpcMethodHandlerTests.cs
git commit -m "test(daemon): add error mapping tests for Phase 1 query parity"
```

---

### Task 6: Visual verification — VS Code Data Explorer query

- [ ] **Step 1: Build and launch extension**

Run: `npm run ext:compile` to build the extension.
Launch VS Code with the extension using F5 or the debug configuration.

- [ ] **Step 2: Run a basic query via Data Explorer against PPDS Dev**

Open a Data Explorer panel, connect to PPDS Dev, run `SELECT TOP 5 name FROM account`.

Verify:
- Query returns results (confirms SqlQueryService is wired)
- Virtual columns work: run `SELECT TOP 5 name, owneridname FROM account` — `owneridname` should show display names (this was broken before because the daemon skipped expansion)
- Executed FetchXML is visible (if the UI shows it)

- [ ] **Step 3: Use @webview-cdp to screenshot results**

Take a screenshot of the Data Explorer showing query results with virtual column expansion.

- [ ] **Step 4: Test error handling**

Run an invalid query like `SELEC name FROM account` and verify the parse error is shown correctly.

---

## Chunk 2: Phase 2 — Query Hints Integration

### Task 7: Add new properties to QueryPlanOptions and QueryPlanContext

**Files:**
- Modify: `src/PPDS.Dataverse/Query/Planning/QueryPlanOptions.cs`
- Create: `src/PPDS.Dataverse/Query/QueryExecutionOptions.cs`
- Modify: `src/PPDS.Dataverse/Query/Planning/QueryPlanContext.cs`

- [ ] **Step 1: Add ForceClientAggregation and NoLock to QueryPlanOptions**

Add after line 110 in `QueryPlanOptions.cs`:

```csharp
/// <summary>
/// When true, forces aggregate queries to use client-side hash grouping
/// regardless of record count. Set by -- ppds:HASH_GROUP hint.
/// </summary>
public bool ForceClientAggregation { get; init; }

/// <summary>
/// When true, injects no-lock="true" on the FetchXML fetch element.
/// Set by -- ppds:NOLOCK hint.
/// </summary>
public bool NoLock { get; init; }
```

- [ ] **Step 2: Create QueryExecutionOptions**

Create `src/PPDS.Dataverse/Query/QueryExecutionOptions.cs`:

```csharp
namespace PPDS.Dataverse.Query;

/// <summary>
/// Execution-level options applied as OrganizationRequest headers.
/// Separate from QueryPlanOptions because these affect how the query
/// is sent to Dataverse, not how the plan is built.
/// </summary>
public sealed record QueryExecutionOptions
{
    /// <summary>Skip synchronous plugin execution (BypassCustomPluginExecution header).</summary>
    public bool BypassPlugins { get; init; }

    /// <summary>Skip Power Automate flow triggers (SuppressCallbackRegistrationExpanderJob header).</summary>
    public bool BypassFlows { get; init; }
}
```

- [ ] **Step 3: Add ExecutionOptions to QueryPlanContext**

Add constructor parameter and property to `QueryPlanContext.cs`:

```csharp
/// <summary>Optional execution-level options (bypass plugins/flows). Set by query hints.</summary>
public QueryExecutionOptions? ExecutionOptions { get; }
```

Add to constructor parameter list (after `maxMaterializationRows`):

```csharp
QueryExecutionOptions? executionOptions = null)
```

Add to constructor body:

```csharp
ExecutionOptions = executionOptions;
```

- [ ] **Step 4: Run typecheck**

Run: `dotnet build PPDS.sln -v q`
Expected: Build succeeds. Constructor callers use named/optional params, so existing code is unaffected.

- [ ] **Step 5: Commit**

```
git add src/PPDS.Dataverse/Query/Planning/QueryPlanOptions.cs src/PPDS.Dataverse/Query/QueryExecutionOptions.cs src/PPDS.Dataverse/Query/Planning/QueryPlanContext.cs
git commit -m "feat(query): add QueryExecutionOptions, ForceClientAggregation, NoLock to plan options"
```

---

### Task 8: Add IQueryExecutor overload with default implementation

**Files:**
- Modify: `src/PPDS.Dataverse/Query/IQueryExecutor.cs`
- Modify: `src/PPDS.Dataverse/Query/QueryExecutor.cs`

- [ ] **Step 1: Write failing test for QueryExecutor bypass headers**

Add to `tests/PPDS.Dataverse.Tests/Query/QueryExecutorTests.cs` (create if needed):

```csharp
[Fact]
[Trait("Category", "Unit")]
public async Task ExecuteWithOptions_BypassPlugins_SetsHeader()
{
    // This test verifies AC-06: BYPASS_PLUGINS hint sets header
    // Arrange: mock the underlying ServiceClient or pool
    // Act: call ExecuteFetchXmlAsync with options { BypassPlugins = true }
    // Assert: the OrganizationRequest had BypassCustomPluginExecution = true
}
```

Note: Check how `QueryExecutor` is tested in existing tests — it wraps `IDataverseConnectionPool`. You may need to mock the pool and capture the request.

- [ ] **Step 2: Add default-implementation overload to IQueryExecutor**

Add after line 26 in `IQueryExecutor.cs`:

```csharp
/// <summary>
/// Executes a FetchXML query with optional execution-level options (bypass plugins/flows).
/// Default implementation ignores options and delegates to the standard overload.
/// </summary>
Task<QueryResult> ExecuteFetchXmlAsync(
    string fetchXml,
    int? pageNumber,
    string? pagingCookie,
    bool includeCount,
    QueryExecutionOptions? executionOptions,
    CancellationToken cancellationToken = default)
{
    return ExecuteFetchXmlAsync(fetchXml, pageNumber, pagingCookie, includeCount, cancellationToken);
}
```

- [ ] **Step 3: Override in QueryExecutor**

In `QueryExecutor.cs`, add the override that applies bypass headers:

```csharp
public async Task<QueryResult> ExecuteFetchXmlAsync(
    string fetchXml,
    int? pageNumber,
    string? pagingCookie,
    bool includeCount,
    QueryExecutionOptions? executionOptions,
    CancellationToken cancellationToken = default)
{
    // If no execution options, delegate to standard path
    if (executionOptions is null || (!executionOptions.BypassPlugins && !executionOptions.BypassFlows))
    {
        return await ExecuteFetchXmlAsync(fetchXml, pageNumber, pagingCookie, includeCount, cancellationToken);
    }

    // Execute with bypass headers
    await using var client = await _connectionPool.GetClientAsync(cancellationToken);
    var fetchExpression = new FetchExpression(fetchXml);
    var request = new RetrieveMultipleRequest { Query = fetchExpression };

    if (executionOptions.BypassPlugins)
        request.Parameters["BypassCustomPluginExecution"] = true;
    if (executionOptions.BypassFlows)
        request.Parameters["SuppressCallbackRegistrationExpanderJob"] = true;

    var response = (RetrieveMultipleResponse)await client.ExecuteAsync(request, cancellationToken);
    // ... map response to QueryResult (follow existing ExecuteFetchXmlAsync pattern)
}
```

Note: Check the existing `ExecuteFetchXmlAsync` in `QueryExecutor.cs` to see how it maps `EntityCollection` to `QueryResult`. Reuse that mapping logic — either extract a helper or call through.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test PPDS.sln --filter "BypassPlugins" -v q`

- [ ] **Step 5: Commit**

```
git add src/PPDS.Dataverse/Query/IQueryExecutor.cs src/PPDS.Dataverse/Query/QueryExecutor.cs tests/
git commit -m "feat(query): add IQueryExecutor overload for execution options (bypass headers)"
```

---

### Task 9: Integrate QueryHintParser into SqlQueryService

**Files:**
- Modify: `src/PPDS.Cli/Services/Query/SqlQueryService.cs:310-392`

- [ ] **Step 1: Write failing test for NOLOCK hint**

Add to `SqlQueryServiceTests.cs`:

```csharp
[Fact]
[Trait("Category", "Unit")]
public async Task Hints_NoLock_SetsFetchXmlAttribute()
{
    // AC-05: -- ppds:NOLOCK produces <fetch no-lock="true">
    var request = new SqlQueryRequest
    {
        Sql = "-- ppds:NOLOCK\nSELECT name FROM account",
    };

    // Arrange: mock executor to capture the FetchXML passed to it
    string? capturedFetchXml = null;
    _mockQueryExecutor
        .Setup(x => x.ExecuteFetchXmlAsync(It.IsAny<string>(), It.IsAny<int?>(),
            It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<QueryExecutionOptions?>(),
            It.IsAny<CancellationToken>()))
        .Callback<string, int?, string?, bool, QueryExecutionOptions?, CancellationToken>(
            (fx, _, _, _, _, _) => capturedFetchXml = fx)
        .ReturnsAsync(CreateEmptyQueryResult());

    var result = await _service.ExecuteAsync(request);

    Assert.NotNull(capturedFetchXml);
    Assert.Contains("no-lock=\"true\"", capturedFetchXml);
}
```

- [ ] **Step 2: Write failing test for USE_TDS hint override**

```csharp
[Fact]
[Trait("Category", "Unit")]
public async Task Hints_OverrideCallerSettings()
{
    // AC-12: inline hint overrides caller-provided useTds=false
    var request = new SqlQueryRequest
    {
        Sql = "-- ppds:USE_TDS\nSELECT name FROM account",
        UseTdsEndpoint = false,  // caller says no TDS
    };

    // The hint should override to TDS=true
    // Assert that the query was routed through TDS executor
    // (verify TdsQueryExecutor was called, not FetchXML executor)
}
```

- [ ] **Step 3: Integrate QueryHintParser into PrepareExecutionAsync**

In `SqlQueryService.cs`, after `QueryParser.Parse()` (approximately line 317), add:

```csharp
// Extract query hints from SQL comments
var hints = QueryHintParser.Parse(fragment);

// Apply plan-level hint overrides
if (hints.UseTdsEndpoint == true)
    request = request with { UseTdsEndpoint = true };
if (hints.MaxResultRows.HasValue)
    request = request with { TopOverride = hints.MaxResultRows.Value };
```

When building `QueryPlanOptions` (approximately line 360), apply hint overrides:

```csharp
var planOptions = new QueryPlanOptions
{
    // ... existing fields ...
    UseTdsEndpoint = request.UseTdsEndpoint,  // may have been overridden by hint
    MaxRows = request.TopOverride,             // may have been overridden by hint
    ForceClientAggregation = hints.ForceClientAggregation == true,
    NoLock = hints.NoLock == true,
    RemoteExecutorFactory = RemoteExecutorFactory,
};

// Cap MAXDOP to pool capacity
if (hints.MaxParallelism.HasValue)
{
    planOptions = planOptions with
    {
        PoolCapacity = Math.Min(hints.MaxParallelism.Value, _poolCapacity),
    };
}
```

Create `QueryExecutionOptions` from execution-level hints:

```csharp
var executionOptions = (hints.BypassPlugins == true || hints.BypassFlows == true)
    ? new QueryExecutionOptions
    {
        BypassPlugins = hints.BypassPlugins == true,
        BypassFlows = hints.BypassFlows == true,
    }
    : null;
```

Thread `executionOptions` through to the `QueryPlanContext` when executing the plan.

- [ ] **Step 4: Integrate hints into ExplainAsync**

In `ExplainAsync()` (approximately line 178), add the same `QueryHintParser.Parse()` call and apply overrides to `QueryPlanOptions` before calling `_planBuilder.Plan()`.

- [ ] **Step 5: Run tests**

Run: `dotnet test PPDS.sln --filter "Category!=Integration" -v q`
Expected: New hint tests pass, all existing tests still pass.

- [ ] **Step 6: Commit**

```
git add src/PPDS.Cli/Services/Query/SqlQueryService.cs tests/
git commit -m "feat(query): integrate QueryHintParser into PrepareExecutionAsync and ExplainAsync

Parses -- ppds:* comment hints and OPTION() hints from SQL, applies
overrides to QueryPlanOptions and creates QueryExecutionOptions. All 8
hints now flow through the shared service for all interfaces."
```

---

### Task 10: FetchXmlScanNode — NoLock injection and execution options passthrough

**Files:**
- Modify: `src/PPDS.Dataverse/Query/Planning/Nodes/FetchXmlScanNode.cs`

- [ ] **Step 1: Write failing test for NoLock FetchXML injection**

Add to `FetchXmlScanNodeTests.cs`:

```csharp
[Fact]
[Trait("Category", "PlanUnit")]
public async Task NoLock_InjectsAttributeIntoFetchXml()
{
    var mockExecutor = new Mock<IQueryExecutor>();
    string? capturedFetchXml = null;
    mockExecutor
        .Setup(x => x.ExecuteFetchXmlAsync(It.IsAny<string>(), It.IsAny<int?>(),
            It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
        .Callback<string, int?, string?, bool, CancellationToken>(
            (fx, _, _, _, _) => capturedFetchXml = fx)
        .ReturnsAsync(MakeResult("account", 1));

    var node = new FetchXmlScanNode("<fetch><entity name=\"account\"/></fetch>",
        "account", noLock: true);
    var ctx = CreateContext(mockExecutor.Object);

    await foreach (var _ in node.ExecuteAsync(ctx)) { }

    Assert.NotNull(capturedFetchXml);
    Assert.Contains("no-lock=\"true\"", capturedFetchXml);
}
```

- [ ] **Step 2: Add noLock constructor parameter to FetchXmlScanNode**

Add a `bool noLock = false` parameter to the constructor. When true, inject `no-lock="true"` into the FetchXML string before execution. The injection is simple string manipulation on the `<fetch` opening tag:

```csharp
if (_noLock && !fetchXml.Contains("no-lock="))
{
    fetchXml = fetchXml.Replace("<fetch", "<fetch no-lock=\"true\"");
}
```

- [ ] **Step 3: Pass execution options to executor overload**

In `FetchXmlScanNode.ExecuteAsync()`, when calling `context.QueryExecutor.ExecuteFetchXmlAsync()`, use the new overload if `context.ExecutionOptions` is set:

```csharp
var result = await context.QueryExecutor.ExecuteFetchXmlAsync(
    effectiveFetchXml, pageNumber, pagingCookie, includeCount,
    context.ExecutionOptions, cancellationToken);
```

- [ ] **Step 4: Run tests**

Run: `dotnet test PPDS.sln --filter "Category!=Integration" -v q`

- [ ] **Step 5: Commit**

```
git add src/PPDS.Dataverse/Query/Planning/Nodes/FetchXmlScanNode.cs tests/
git commit -m "feat(query): FetchXmlScanNode injects no-lock and passes execution options"
```

---

### Task 11: ExecutionPlanBuilder — ForceClientAggregation

**Files:**
- Modify: `src/PPDS.Query/Planning/ExecutionPlanBuilder.cs`

- [ ] **Step 1: Write failing test**

Add to `ExecutionPlanBuilderTests.cs`:

```csharp
[Fact]
[Trait("Category", "Unit")]
public void Hints_HashGroup_ForcesClientAggregation()
{
    // AC-10: HASH_GROUP forces client-side aggregation
    var fragment = _parser.Parse("SELECT region, COUNT(*) as cnt FROM account GROUP BY region");
    var options = new QueryPlanOptions { ForceClientAggregation = true };
    var result = _builder.Plan(fragment, options);

    // Should produce a ClientHashGroupNode even without high record count
    TestHelpers.ContainsNodeOfType<ClientHashGroupNode>(result.RootNode).Should().BeTrue();
}
```

Note: Find the exact class name for client-side hash group node — check `src/PPDS.Dataverse/Query/Planning/Nodes/` for the aggregation node types.

- [ ] **Step 2: Add ForceClientAggregation check in PlanSelect**

In `ExecutionPlanBuilder.PlanSelect()`, find the aggregate detection logic. Add a check: when `options.ForceClientAggregation` is true and the query has aggregate functions, route to the client-side plan regardless of `EstimatedRecordCount`.

- [ ] **Step 3: Run tests**

Run: `dotnet test PPDS.sln --filter "Category!=Integration" -v q`

- [ ] **Step 4: Commit**

```
git add src/PPDS.Query/Planning/ExecutionPlanBuilder.cs tests/
git commit -m "feat(query): ForceClientAggregation routes aggregates to client-side hash group"
```

---

### Task 12: Wire NoLock and ExecutionOptions through plan builder

**Files:**
- Modify: `src/PPDS.Query/Planning/ExecutionPlanBuilder.cs`
- Modify: `src/PPDS.Cli/Services/Query/SqlQueryService.cs`

- [ ] **Step 1: Pass NoLock to FetchXmlScanNode construction**

In `ExecutionPlanBuilder`, where `FetchXmlScanNode` is constructed, pass `options.NoLock`:

```csharp
var scanNode = new FetchXmlScanNode(fetchXml, entityName, noLock: options.NoLock);
```

- [ ] **Step 2: Pass ExecutionOptions to QueryPlanContext**

In `SqlQueryService`, where `QueryPlanContext` is constructed for plan execution, add the `executionOptions` parameter:

```csharp
var context = new QueryPlanContext(
    queryExecutor,
    cancellationToken,
    statistics: stats,
    tdsQueryExecutor: _tdsQueryExecutor,
    bulkOperationExecutor: _bulkExecutor,
    metadataQueryExecutor: _metadataExecutor,
    executionOptions: executionOptions);
```

- [ ] **Step 3: Run all tests**

Run: `dotnet test PPDS.sln --filter "Category!=Integration" -v q`

- [ ] **Step 4: Commit**

```
git add src/PPDS.Query/Planning/ExecutionPlanBuilder.cs src/PPDS.Cli/Services/Query/SqlQueryService.cs
git commit -m "feat(query): wire NoLock and ExecutionOptions through plan builder and context"
```

---

### Task 13: Remaining hint tests

**Files:**
- Modify: `tests/PPDS.Cli.Tests/Services/Query/SqlQueryServiceTests.cs`

- [ ] **Step 1: Write tests for remaining hints**

Add tests for AC-04 (USE_TDS), AC-08 (MAX_ROWS), AC-09 (MAXDOP), AC-11 (BATCH_SIZE), AC-13 (malformed hints), AC-21 (ExplainAsync reflects hints). Follow the same pattern as the NOLOCK test — mock the executor, capture arguments, verify the hint was applied.

- [ ] **Step 2: Run all tests**

Run: `dotnet test PPDS.sln --filter "Category!=Integration" -v q`

- [ ] **Step 3: Commit**

```
git add tests/
git commit -m "test(query): add comprehensive hint integration tests (AC-04 through AC-13, AC-21)"
```

---

## Chunk 3: Phase 3 — VS Code Environment Colors

### Task 14: Add envColor to message types

**Files:**
- Modify: `src/PPDS.Extension/src/panels/webview/shared/message-types.ts:37,91`

- [ ] **Step 1: Add envColor to QueryPanelHostToWebview**

Change line 37:
```typescript
// OLD:
| { command: 'updateEnvironment'; name: string; url: string | null; envType: string | null }
// NEW:
| { command: 'updateEnvironment'; name: string; url: string | null; envType: string | null; envColor: string | null }
```

- [ ] **Step 2: Add envColor to SolutionsPanelHostToWebview**

Change line 91:
```typescript
// OLD:
| { command: 'updateEnvironment'; name: string; envType: string | null }
// NEW:
| { command: 'updateEnvironment'; name: string; envType: string | null; envColor: string | null }
```

- [ ] **Step 3: Run typecheck**

Run: `npm run ext:typecheck`
Expected: Type errors in QueryPanel.ts and SolutionsPanel.ts where `updateEnvironment` messages are sent without `envColor`. This is expected — we fix those next.

- [ ] **Step 4: Commit**

```
git add src/PPDS.Extension/src/panels/webview/shared/message-types.ts
git commit -m "feat(ext): add envColor to updateEnvironment message types"
```

---

### Task 15: Send envColor from QueryPanel and SolutionsPanel

**Files:**
- Modify: `src/PPDS.Extension/src/panels/QueryPanel.ts:219,258`
- Modify: `src/PPDS.Extension/src/panels/SolutionsPanel.ts:146,162`

- [ ] **Step 1: Fetch and store envColor in QueryPanel**

Add a `private environmentColor: string | null = null;` field to `QueryPanel`.

In `initEnvironment()` (line 245), after getting `who.environment`, fetch the env config to get the color:

```typescript
if (this.environmentUrl) {
    try {
        const config = await this.daemon.envConfigGet(this.environmentUrl);
        this.environmentColor = config.resolvedColor ?? null;
    } catch {
        this.environmentColor = null;
    }
}
```

Update both `postMessage` calls to include `envColor`:

```typescript
// Line 219 (after picker selection):
this.postMessage({ command: 'updateEnvironment', name: env.displayName, url: env.url, envType: env.type, envColor: this.environmentColor });

// Line 258 (initialization):
this.postMessage({ command: 'updateEnvironment', name: this.environmentDisplayName ?? 'No environment', url: this.environmentUrl ?? null, envType: this.environmentType, envColor: this.environmentColor });
```

Also fetch color after picker selection:

```typescript
case 'requestEnvironmentList': {
    const env = await showEnvironmentPicker(this.daemon, this.environmentUrl);
    if (env) {
        this.environmentUrl = env.url;
        this.environmentDisplayName = env.displayName;
        this.environmentType = env.type;
        try {
            const config = await this.daemon.envConfigGet(env.url);
            this.environmentColor = config.resolvedColor ?? null;
        } catch {
            this.environmentColor = null;
        }
        this.postMessage({ command: 'updateEnvironment', name: env.displayName, url: env.url, envType: env.type, envColor: this.environmentColor });
        this.updateTitle();
    }
    break;
}
```

- [ ] **Step 2: Same for SolutionsPanel**

Add `environmentColor` field and fetch color similarly. Update both `postMessage` calls.

- [ ] **Step 3: Run typecheck**

Run: `npm run ext:typecheck`
Expected: All type errors resolved.

- [ ] **Step 4: Commit**

```
git add src/PPDS.Extension/src/panels/QueryPanel.ts src/PPDS.Extension/src/panels/SolutionsPanel.ts
git commit -m "feat(ext): fetch and send environment color in panel updateEnvironment messages"
```

---

### Task 16: Apply environment color in webview CSS

**Files:**
- Modify: `src/PPDS.Extension/src/panels/webview/query-panel/query-panel.ts:751-764`
- Modify: `src/PPDS.Extension/src/panels/webview/shared/shared.css`

- [ ] **Step 1: Add color CSS custom properties to shared.css**

Add after the existing `data-env-type` rules (after line 33):

```css
/* ── Environment color accent (from configured color) ─────────────────── */

.toolbar[data-env-color="red"]          { border-left: 4px solid #e74c3c; }
.toolbar[data-env-color="green"]        { border-left: 4px solid #2ecc71; }
.toolbar[data-env-color="yellow"]       { border-left: 4px solid #f1c40f; }
.toolbar[data-env-color="cyan"]         { border-left: 4px solid #00bcd4; }
.toolbar[data-env-color="blue"]         { border-left: 4px solid #3498db; }
.toolbar[data-env-color="gray"]         { border-left: 4px solid #95a5a6; }
.toolbar[data-env-color="brown"]        { border-left: 4px solid #8d6e63; }
.toolbar[data-env-color="white"]        { border-left: 4px solid #ecf0f1; }
.toolbar[data-env-color="brightred"]    { border-left: 4px solid #ff6b6b; }
.toolbar[data-env-color="brightgreen"]  { border-left: 4px solid #69db7c; }
.toolbar[data-env-color="brightyellow"] { border-left: 4px solid #ffe066; }
.toolbar[data-env-color="brightcyan"]   { border-left: 4px solid #66d9ef; }
.toolbar[data-env-color="brightblue"]   { border-left: 4px solid #74b9ff; }
```

- [ ] **Step 2: Apply data-env-color attribute in query-panel.ts**

In the `updateEnvironment` handler (line 751), add color handling:

```typescript
case 'updateEnvironment':
    updateEnvironmentDisplay(msg.name);
    currentEnvironmentUrl = msg.url || null;
    {
        const toolbar = document.querySelector('.toolbar');
        if (toolbar) {
            if (msg.envType) {
                toolbar.setAttribute('data-env-type', msg.envType.toLowerCase());
            } else {
                toolbar.removeAttribute('data-env-type');
            }
            // Apply environment color accent
            if (msg.envColor) {
                toolbar.setAttribute('data-env-color', msg.envColor.toLowerCase());
            } else {
                toolbar.removeAttribute('data-env-color');
            }
        }
    }
    break;
```

- [ ] **Step 3: Apply same in solutions panel webview**

Find the `updateEnvironment` handler in the solutions panel webview and add the same `data-env-color` attribute logic.

- [ ] **Step 4: Run typecheck**

Run: `npm run ext:typecheck`

- [ ] **Step 5: Commit**

```
git add src/PPDS.Extension/src/panels/webview/ src/PPDS.Extension/src/panels/styles/
git commit -m "feat(ext): render environment color as 4px left border on panel toolbar"
```

---

### Task 17: Visual verification — environment colors

- [ ] **Step 1: Build and launch extension**

Run: `npm run ext:compile`
Launch VS Code with the extension.

- [ ] **Step 2: Configure an environment with a color**

Use the "Configure Environment" command to set a color for the PPDS Dev environment (e.g., Green for Development).

- [ ] **Step 3: Open Data Explorer and verify**

Open a Data Explorer panel targeting the colored environment. Verify:
- 4px left border appears in the configured color
- Color changes when switching environments via picker
- Default type-based color appears when no explicit color is configured

- [ ] **Step 4: Screenshot via @webview-cdp**

Take screenshots showing the colored toolbar border.

---

## Chunk 4: Phase 4 — Cross-Environment Query Attribution

### Task 18: Add QueryDataSource type and wire into SqlQueryResult

**Files:**
- Create: `src/PPDS.Cli/Services/Query/QueryDataSource.cs`
- Modify: `src/PPDS.Cli/Services/Query/SqlQueryResult.cs`

- [ ] **Step 1: Create QueryDataSource**

Create `src/PPDS.Cli/Services/Query/QueryDataSource.cs`:

```csharp
namespace PPDS.Cli.Services.Query;

/// <summary>
/// Identifies an environment that contributed data to a query result.
/// </summary>
public sealed record QueryDataSource
{
    /// <summary>Display label for the environment.</summary>
    public required string Label { get; init; }

    /// <summary>Whether this is a remote environment (vs the local/primary one).</summary>
    public bool IsRemote { get; init; }
}
```

- [ ] **Step 2: Add DataSources to SqlQueryResult**

Add to `SqlQueryResult.cs`:

```csharp
/// <summary>
/// Environments that contributed data to this result. Single-environment
/// queries have one entry. Cross-environment queries have multiple.
/// Null when data source tracking is not applicable (e.g., transpile-only).
/// </summary>
public IReadOnlyList<QueryDataSource>? DataSources { get; init; }
```

- [ ] **Step 3: Commit**

```
git add src/PPDS.Cli/Services/Query/QueryDataSource.cs src/PPDS.Cli/Services/Query/SqlQueryResult.cs
git commit -m "feat(query): add QueryDataSource type and DataSources property on SqlQueryResult"
```

---

### Task 19: Collect data sources from execution plan

**Files:**
- Modify: `src/PPDS.Cli/Services/Query/SqlQueryService.cs`

- [ ] **Step 1: Write failing test**

Add to `SqlQueryServiceTests.cs`:

```csharp
[Fact]
[Trait("Category", "Unit")]
public async Task SingleEnv_DataSources_ContainsLocalOnly()
{
    // AC-18 (partial): single-env query has one data source
    var request = new SqlQueryRequest { Sql = "SELECT name FROM account" };
    // ... setup mock
    var result = await _service.ExecuteAsync(request);

    Assert.NotNull(result.DataSources);
    Assert.Single(result.DataSources);
    Assert.False(result.DataSources[0].IsRemote);
}
```

- [ ] **Step 2: Add plan tree walker to collect RemoteScanNode labels**

In `SqlQueryService`, add a helper method:

```csharp
private static List<QueryDataSource> CollectDataSources(
    IQueryPlanNode rootNode,
    string localLabel)
{
    var sources = new List<QueryDataSource>
    {
        new() { Label = localLabel, IsRemote = false }
    };

    CollectRemoteLabels(rootNode, sources);
    return sources;
}

private static void CollectRemoteLabels(IQueryPlanNode node, List<QueryDataSource> sources)
{
    if (node is RemoteScanNode remote)
    {
        if (!sources.Any(s => s.Label == remote.RemoteLabel))
        {
            sources.Add(new QueryDataSource { Label = remote.RemoteLabel, IsRemote = true });
        }
    }

    foreach (var child in node.Children)
    {
        CollectRemoteLabels(child, sources);
    }
}
```

Note: Check how `IQueryPlanNode.Children` is exposed — look for `GetChildren()` or a `Children` property on the node interface.

- [ ] **Step 3: Call from ExecuteAsync after plan building**

After `_planBuilder.Plan()` returns and before execution, collect data sources:

```csharp
var localLabel = ResolveLocalLabel(environmentUrl);
var dataSources = CollectDataSources(planResult.RootNode, localLabel);
```

Set on the result:

```csharp
return new SqlQueryResult
{
    OriginalSql = request.Sql,
    TranspiledFetchXml = planResult.FetchXml,
    Result = queryResult,
    DataSources = dataSources,
};
```

- [ ] **Step 4: Run tests**

Run: `dotnet test PPDS.sln --filter "Category!=Integration" -v q`

- [ ] **Step 5: Commit**

```
git add src/PPDS.Cli/Services/Query/SqlQueryService.cs tests/
git commit -m "feat(query): collect data sources from execution plan tree"
```

---

### Task 20: Add dataSources to RPC response DTO

**Files:**
- Modify: `src/PPDS.Cli/Commands/Serve/Handlers/RpcMethodHandler.cs`

- [ ] **Step 1: Add dataSources field to QueryResultResponse**

```csharp
[JsonPropertyName("dataSources")]
[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
public List<QueryDataSourceDto>? DataSources { get; set; }
```

Add the DTO type:

```csharp
public class QueryDataSourceDto
{
    [JsonPropertyName("label")] public string Label { get; set; } = "";
    [JsonPropertyName("isRemote")] public bool IsRemote { get; set; }
}
```

- [ ] **Step 2: Map DataSources in the response mapping**

In the `QuerySqlAsync` response mapping, add:

```csharp
if (result.DataSources is { Count: > 1 })
{
    mapped.DataSources = result.DataSources
        .Select(ds => new QueryDataSourceDto { Label = ds.Label, IsRemote = ds.IsRemote })
        .ToList();
}
```

- [ ] **Step 3: Commit**

```
git add src/PPDS.Cli/Commands/Serve/Handlers/RpcMethodHandler.cs
git commit -m "feat(daemon): add dataSources to QueryResultResponse for cross-env attribution"
```

---

### Task 21: Render cross-env banner in VS Code webview

**Files:**
- Modify: `src/PPDS.Extension/src/panels/webview/query-panel/query-panel.ts`
- Modify: `src/PPDS.Extension/src/panels/styles/query-panel.css`

- [ ] **Step 1: Add banner element to HTML**

In the webview's initial HTML setup, add a banner container above the results wrapper:

```typescript
const dataSourceBanner = document.createElement('div');
dataSourceBanner.className = 'data-source-banner';
dataSourceBanner.style.display = 'none';
// Insert before results wrapper
```

- [ ] **Step 2: Update handleQueryResult to show/hide banner**

```typescript
function handleQueryResult(data: QueryResultResponse): void {
    // ... existing code ...

    // Show cross-env banner if multiple data sources
    if (data.dataSources && data.dataSources.length > 1) {
        const parts = data.dataSources.map(ds => {
            const tag = ds.isRemote ? 'remote' : 'local';
            return `<span class="data-source-label" data-env-color="${(ds.color || 'gray').toLowerCase()}">${escapeHtml(ds.label)}</span> <span class="data-source-tag">(${tag})</span>`;
        });
        dataSourceBanner.innerHTML = 'Data from: ' + parts.join(' · ');
        dataSourceBanner.style.display = '';
    } else {
        dataSourceBanner.style.display = 'none';
    }
}
```

- [ ] **Step 3: Add banner CSS**

Add to `query-panel.css`:

```css
.data-source-banner {
    padding: 6px 12px;
    font-size: 12px;
    color: var(--vscode-descriptionForeground);
    border-bottom: 1px solid var(--vscode-panel-border);
    background: var(--vscode-editor-background);
}

.data-source-label {
    font-weight: 600;
}

.data-source-tag {
    font-style: italic;
    opacity: 0.7;
}
```

- [ ] **Step 4: Run typecheck**

Run: `npm run ext:typecheck`

- [ ] **Step 5: Commit**

```
git add src/PPDS.Extension/src/panels/webview/ src/PPDS.Extension/src/panels/styles/
git commit -m "feat(ext): render cross-environment data source banner in query results"
```

---

## Chunk 5: Final Verification and Manual Test Plan

### Task 22: Run all automated tests

- [ ] **Step 1: Run .NET unit tests**

Run: `dotnet test PPDS.sln --filter "Category!=Integration" -v q`
Expected: All tests pass.

- [ ] **Step 2: Run extension TypeScript typecheck**

Run: `npm run typecheck:all`
Expected: No type errors.

- [ ] **Step 3: Run extension unit tests**

Run: `npm run ext:test`
Expected: All tests pass.

- [ ] **Step 4: Run linter**

Run: `npx eslint src/PPDS.Extension/src --quiet`
Expected: No errors.

---

### Task 23: Visual verification — VS Code extension

Use `@webview-cdp` for all visual checks.

- [ ] **Step 1: Connect to PPDS Dev and run a basic query**

Open Data Explorer → `SELECT TOP 10 name, owneridname FROM account`
Verify: results show, virtual columns expanded, environment color border visible.

- [ ] **Step 2: Test NOLOCK hint**

Run: `-- ppds:NOLOCK\nSELECT TOP 5 name FROM account`
Verify: check the executed FetchXML contains `no-lock="true"`.

- [ ] **Step 3: Test error handling**

Run: `SELEC name FROM account`
Verify: parse error displayed correctly.

---

### Task 24: Visual verification — TUI

- [ ] **Step 1: Launch TUI and run a hint query**

Run `ppds tui`, navigate to SQL query screen, run:
```
-- ppds:NOLOCK
SELECT TOP 5 name FROM account
```

Verify: query executes, results display.

---

### Task 25: Manual Test Plan (for user)

This is the test plan for the user to verify against PPDS Dev:

```markdown
## Manual Test Plan — Query Parity

### Prerequisites
- PPDS Dev environment connected in both TUI and VS Code extension
- At least one environment label configured (e.g., "Dev" → PPDS Dev URL)

### Tests

#### T1: Basic query parity
1. TUI: `SELECT TOP 5 name, owneridname FROM account`
2. VS Code: Same query in Data Explorer
3. **Verify**: Both return identical results with `owneridname` expanded

#### T2: NOLOCK hint
1. Run in both: `-- ppds:NOLOCK\nSELECT TOP 5 name FROM account`
2. **Verify**: Executed FetchXML shows `no-lock="true"` in both interfaces

#### T3: USE_TDS hint
1. Run in both: `-- ppds:USE_TDS\nSELECT TOP 5 name FROM account`
2. **Verify**: Query routes through TDS endpoint (check query mode in response)

#### T4: BYPASS_PLUGINS hint
1. Run: `-- ppds:BYPASS_PLUGINS\nUPDATE account SET description = 'test' WHERE name = 'test-bypass'`
2. **Verify**: No plugin fires (check plugin trace log for absence of execution)

#### T5: MAX_ROWS hint
1. Run: `-- ppds:MAX_ROWS 3\nSELECT name FROM account`
2. **Verify**: Only 3 rows returned even though more exist

#### T6: Cross-environment query (if second env configured)
1. Configure a label: `ppds env config <url> --label QA`
2. Run: `SELECT * FROM account a JOIN [QA].account qa ON a.accountid = qa.accountid`
3. **Verify**: Data from both environments, banner shows "Data from: Dev (local) · QA (remote)"

#### T7: Environment color
1. VS Code: Configure Dev environment with Green color
2. Open Data Explorer
3. **Verify**: 4px green left border on toolbar

#### T8: Error handling
1. Run invalid SQL: `SELEC name FROM account`
2. **Verify**: Parse error shown correctly in both TUI and VS Code

#### T9: DML safety
1. Run: `DELETE FROM account` (no WHERE clause)
2. **Verify**: Blocked with safety message in both interfaces
```

- [ ] **Step 1: Save the manual test plan**

The test plan above is for the user to run. Present it to them when implementation is complete.

- [ ] **Step 2: Commit the plan document**

```
git add docs/plans/2026-03-15-query-parity.md
git commit -m "docs: add query parity implementation plan"
```
