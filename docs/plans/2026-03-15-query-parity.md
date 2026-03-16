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
| Verify | `src/PPDS.Cli/Services/ServiceRegistration.cs` | Confirm `ISqlQueryService` is registered in daemon's service provider (add if missing) |
| Verify | `src/PPDS.Cli/Infrastructure/DaemonConnectionPoolManager.cs` | Confirm service provider includes `ISqlQueryService` |
| Modify | `tests/PPDS.Cli.Tests/Commands/Serve/Handlers/RpcMethodHandlerTests.cs` | Add tests for shared service usage, error mapping, virtual column expansion, cross-env |

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
| Modify | `src/PPDS.Extension/src/panels/webview/query-panel.ts` | Apply `data-env-color` attribute on toolbar |
| Modify | `src/PPDS.Extension/src/panels/styles/shared.css` | Add environment color CSS custom properties and left border rules |

### Phase 4: Cross-Environment Query Attribution

| Action | File | Responsibility |
|--------|------|----------------|
| Create | `src/PPDS.Cli/Services/Query/QueryDataSource.cs` | New record type |
| Modify | `src/PPDS.Cli/Services/Query/SqlQueryResult.cs` | Add `DataSources` and `AppliedHints` properties |
| Modify | `src/PPDS.Cli/Services/Query/SqlQueryService.cs` | Collect data sources from plan tree; populate applied hints list |
| Modify | `src/PPDS.Cli/Commands/Serve/Handlers/RpcMethodHandler.cs` | Add `dataSources` and `appliedHints` to `QueryResultResponse` DTO |
| Modify | `src/PPDS.Extension/src/panels/webview/query-panel.ts` | Render cross-env banner |
| Modify | `src/PPDS.Extension/src/panels/styles/query-panel.css` | Banner CSS |
| Modify | `tests/PPDS.Cli.Tests/Services/Query/SqlQueryServiceTests.cs` | Data source collection tests |

---

## Chunk 1: Phase 1 — Daemon Uses SqlQueryService

### Task 1: Verify ISqlQueryService registration in daemon service provider

**Files:**
- Verify: `src/PPDS.Cli/Services/ServiceRegistration.cs`
- Verify: `src/PPDS.Cli/Infrastructure/DaemonConnectionPoolManager.cs:337-374`

- [ ] **Step 1: Check if ISqlQueryService is registered in the daemon's service provider**

The daemon creates service providers via `DaemonConnectionPoolManager.CreateProviderFromSources()`. Check whether this calls `services.AddCliApplicationServices()` (which registers `ISqlQueryService` in `ServiceRegistration.cs:45-58`). If not, `sp.GetRequiredService<ISqlQueryService>()` will throw at runtime.

If missing, add the registration to `CreateProviderFromSources()`:

```csharp
services.AddCliApplicationServices(queryExecutor, tdsExecutor, bulkExecutor, metadataExecutor, poolCapacity);
```

Or register `ISqlQueryService` directly if `AddCliApplicationServices` pulls in too many dependencies.

- [ ] **Step 2: Run typecheck to verify**

Run: `dotnet build PPDS.sln -v q`

- [ ] **Step 3: Commit (if changes were needed)**

```
git add src/PPDS.Cli/Infrastructure/DaemonConnectionPoolManager.cs
git commit -m "fix(daemon): register ISqlQueryService in daemon service provider"
```

---

### Task 2: Write Phase 1 tests FIRST (TDD)

**Files:**
- Modify: `tests/PPDS.Cli.Tests/Commands/Serve/Handlers/RpcMethodHandlerTests.cs`
- Reference: `tests/PPDS.Cli.Tests/Mocks/FakeSqlQueryService.cs`
- Reference: `tests/PPDS.Cli.Tests/Commands/Serve/Handlers/RpcMethodHandlerPoolTests.cs` (for setup pattern)

- [ ] **Step 1: Write test for error mapping (AC-25)**

```csharp
[Fact]
[Trait("Category", "Unit")]
public async Task QuerySql_ParseError_MapsToRpcException()
{
    // Arrange: configure FakeSqlQueryService to throw QueryParseException
    var fakeSqlService = new FakeSqlQueryService();
    fakeSqlService.ExceptionToThrow = new QueryParseException("Unexpected token 'SELEC'", 1, 1);

    // Setup handler with mock pool manager that returns provider with fake service
    var mockPoolManager = new Mock<IDaemonConnectionPoolManager>();
    // ... wire service provider with fakeSqlService registered as ISqlQueryService

    // Act
    var handler = new RpcMethodHandler(mockPoolManager.Object, CreateAuthServices());
    var act = () => handler.QuerySqlAsync(new QuerySqlRequest { Sql = "SELEC name FROM account" });

    // Assert
    var ex = await Assert.ThrowsAsync<RpcException>(act);
    ex.StructuredErrorCode.Should().Be(ErrorCodes.Query.ParseError);
}
```

Note: Follow the setup pattern from `RpcMethodHandlerPoolTests.cs`. The mock pool manager must return a service provider that includes the `FakeSqlQueryService`. Check `CreateAuthServices()` helper in that file.

- [ ] **Step 2: Write test for DML blocked mapping (AC-25)**

```csharp
[Fact]
[Trait("Category", "Unit")]
public async Task QuerySql_DmlBlocked_MapsToRpcExceptionWithSafetyData()
{
    var fakeSqlService = new FakeSqlQueryService();
    fakeSqlService.ExceptionToThrow = new PpdsException(
        ErrorCodes.Query.DmlBlocked,
        "DELETE without WHERE clause is blocked");

    // ... setup handler

    var act = () => handler.QuerySqlAsync(new QuerySqlRequest
    {
        Sql = "DELETE FROM account",
        DmlSafety = new DmlSafetyRpcOptions(),
    });

    var ex = await Assert.ThrowsAsync<RpcException>(act);
    ex.StructuredErrorCode.Should().Be(ErrorCodes.Query.DmlBlocked);
    var errorData = ex.ErrorData.Should().BeOfType<DmlSafetyErrorData>().Subject;
    errorData.DmlBlocked.Should().BeTrue();
}
```

- [ ] **Step 3: Write test for virtual column expansion (AC-02)**

```csharp
[Fact]
[Trait("Category", "Unit")]
public async Task QuerySql_ExpandsVirtualColumns()
{
    // Arrange: FakeSqlQueryService returns result with owneridname virtual column
    var fakeSqlService = new FakeSqlQueryService();
    fakeSqlService.NextResult = CreateResultWithVirtualColumn("owneridname", "John Doe");

    // ... setup handler

    var result = await handler.QuerySqlAsync(new QuerySqlRequest
    {
        Sql = "SELECT owneridname FROM account",
    });

    // Assert: the response includes the virtual column
    result.Columns.Should().Contain(c => c.LogicalName == "owneridname");
}
```

- [ ] **Step 4: Write test for cross-environment label resolution (AC-03)**

```csharp
[Fact]
[Trait("Category", "Unit")]
public async Task QuerySql_CrossEnvironment_ResolvesLabel()
{
    // AC-03: [LABEL].entity syntax works when label is configured
    var fakeSqlService = new FakeSqlQueryService();
    // Configure RemoteExecutorFactory to be called
    // The service should attempt to resolve label "QA" via ProfileResolutionService

    // ... setup handler with environment configs containing label "QA"

    var result = await handler.QuerySqlAsync(new QuerySqlRequest
    {
        Sql = "SELECT name FROM [QA].account",
    });

    // Assert: RemoteExecutorFactory was invoked with label "QA"
    // (or assert the query succeeds with cross-env data)
}
```

- [ ] **Step 5: Run tests to verify they fail**

Run: `dotnet test PPDS.sln --filter "QuerySql_ParseError|QuerySql_DmlBlocked|QuerySql_ExpandsVirtualColumns|QuerySql_CrossEnvironment" -v q`
Expected: Tests fail because `QuerySqlAsync` still uses the bespoke path.

- [ ] **Step 6: Commit failing tests**

```
git add tests/PPDS.Cli.Tests/Commands/Serve/Handlers/RpcMethodHandlerTests.cs
git commit -m "test(daemon): add failing tests for Phase 1 query parity (AC-02, AC-03, AC-25)"
```

---

### Task 3: Wire SqlQueryService into QuerySqlAsync

**Files:**
- Modify: `src/PPDS.Cli/Commands/Serve/Handlers/RpcMethodHandler.cs:942-1073`
- Reference: `src/PPDS.Cli/Tui/InteractiveSession.cs:321-355`

- [ ] **Step 1: Add helper method to configure SqlQueryService**

Add a private method in `RpcMethodHandler` that mirrors `InteractiveSession.GetSqlQueryServiceAsync()`:

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
    var envConfigStore = _authServices.GetRequiredService<EnvironmentConfigStore>();
    var envConfigs = await envConfigStore.GetAllConfigsAsync(cancellationToken)
        .ConfigureAwait(false);
    var resolver = new ProfileResolutionService(envConfigs);
    concrete.RemoteExecutorFactory = label =>
    {
        var config = resolver.ResolveByLabel(label);
        if (config?.Url == null) return null;
#pragma warning disable PPDS012 // Planner is synchronous; provider cache makes this effectively instant
        var remoteProvider = _poolManager.GetOrCreateServiceProviderAsync(config.Url)
            .GetAwaiter().GetResult();
#pragma warning restore PPDS012
        return remoteProvider.GetRequiredService<IQueryExecutor>();
    };
    concrete.ProfileResolver = resolver;

    // Wire environment-specific safety settings
    if (environmentUrl != null)
    {
        var envConfig = await envConfigStore.GetConfigAsync(environmentUrl, cancellationToken)
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

Key differences from the original plan:
- Uses `_authServices.GetRequiredService<EnvironmentConfigStore>()` (not `_envConfigStore`)
- Uses `_poolManager.GetOrCreateServiceProviderAsync()` (not `GetServiceProviderForEnvironmentAsync`)
- Includes `#pragma warning disable PPDS012` matching `InteractiveSession` pattern

- [ ] **Step 2: Replace QuerySqlAsync body**

Replace the entire body of `QuerySqlAsync` (lines 942-1073):

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

        try
        {
            var result = await service.ExecuteAsync(sqlRequest, ct);
            var mapped = MapToResponse(result.Result, result.TranspiledFetchXml);
            mapped.QueryMode = result.Result.IsAggregate ? "aggregate" : "dataverse";
            return mapped;
        }
        catch (QueryParseException ex)
        {
            throw new RpcException(ErrorCodes.Query.ParseError, ex.Message, ex);
        }
        catch (PpdsException ex) when (ex.ErrorCode == ErrorCodes.Query.DmlBlocked)
        {
            // SqlQueryService uses ErrorCodes.Query.DmlBlocked for both blocked and
            // confirmation-required cases. Distinguish by checking the message or
            // DmlSafetyResult property on the exception if available.
            var isConfirmation = ex.Message.Contains("requires confirmation",
                StringComparison.OrdinalIgnoreCase);
            throw new RpcException(
                isConfirmation
                    ? ErrorCodes.Query.DmlConfirmationRequired
                    : ErrorCodes.Query.DmlBlocked,
                ex.Message,
                new DmlSafetyErrorData
                {
                    Code = isConfirmation
                        ? ErrorCodes.Query.DmlConfirmationRequired
                        : ErrorCodes.Query.DmlBlocked,
                    Message = ex.Message,
                    DmlBlocked = !isConfirmation,
                    DmlConfirmationRequired = isConfirmation,
                });
        }
        catch (PpdsException ex)
        {
            throw new RpcException(ErrorCodes.Query.ExecutionFailed, ex.Message, ex);
        }
    }, cancellationToken);

    FireAndForgetHistorySave(request.Sql, response);
    return response;
}
```

Key differences from the original plan:
- `PpdsException.ErrorCode` is a `string` — comparison uses `ErrorCodes.Query.DmlBlocked` (string constant)
- Both DML blocked AND confirmation throw with the same error code from `SqlQueryService` — distinguished by message content
- No `PpdsErrorCode` enum (doesn't exist)

- [ ] **Step 3: Run tests from Task 2**

Run: `dotnet test PPDS.sln --filter "QuerySql_ParseError|QuerySql_DmlBlocked|QuerySql_ExpandsVirtualColumns" -v q`
Expected: Tests pass now.

- [ ] **Step 4: Commit**

```
git add src/PPDS.Cli/Commands/Serve/Handlers/RpcMethodHandler.cs
git commit -m "feat(daemon): replace QuerySqlAsync inline pipeline with SqlQueryService

Wire RemoteExecutorFactory, ProfileResolver, and safety settings on the
service instance. Map SqlQueryService exceptions to existing RPC error
codes. showFetchXml mode uses service.TranspileSql()."
```

---

### Task 4: Align QueryExplainAsync to use SqlQueryService

**Files:**
- Modify: `src/PPDS.Cli/Commands/Serve/Handlers/RpcMethodHandler.cs:1247-1291`

- [ ] **Step 1: Replace QueryExplainAsync body**

The current handler returns a response with `Plan` (formatted tree + FetchXML), `Format` ("text" or "fetchxml"), and `FetchXml`. Preserve this format:

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

        try
        {
            var plan = await service.ExplainAsync(request.Sql, ct);
            var fetchXml = service.TranspileSql(request.Sql);
            var formatted = plan.Description;

            return new QueryExplainResponse
            {
                Plan = formatted + "\n\n--- FetchXML ---\n" + fetchXml,
                Format = "text",
                FetchXml = fetchXml,
            };
        }
        catch (QueryParseException ex)
        {
            throw new RpcException(ErrorCodes.Query.ParseError, ex.Message, ex);
        }
        catch (Exception)
        {
            // Fallback: return just the transpiled FetchXML
            var fetchXml = service.TranspileSql(request.Sql);
            return new QueryExplainResponse
            {
                Plan = fetchXml,
                Format = "fetchxml",
                FetchXml = fetchXml,
            };
        }
    }, cancellationToken);
}
```

Key differences from original plan:
- Preserves `Format = "text"` and the `formatted + "\n\n--- FetchXML ---\n" + fetchXml` pattern from the existing handler
- No `Success` field (doesn't exist on `QueryExplainResponse`)
- Includes fallback to FetchXML-only matching current behavior

- [ ] **Step 2: Run typecheck**

Run: `dotnet build PPDS.sln -v q`

- [ ] **Step 3: Commit**

```
git add src/PPDS.Cli/Commands/Serve/Handlers/RpcMethodHandler.cs
git commit -m "feat(daemon): replace QueryExplainAsync with SqlQueryService.ExplainAsync"
```

---

### Task 5: Align QueryExportAsync SQL path

**Files:**
- Modify: `src/PPDS.Cli/Commands/Serve/Handlers/RpcMethodHandler.cs:1162-1240`

- [ ] **Step 1: Replace SQL transpilation in QueryExportAsync**

The export handler has two input paths: SQL and FetchXML. Only the SQL path changes — replace the `TranspileSqlToFetchXml()` call with `service.TranspileSql()`. The FetchXML input path stays as-is.

```csharp
// In QueryExportAsync, replace the SQL transpilation:
// OLD: var fetchXml = TranspileSqlToFetchXml(request.Sql, request.Top);
// NEW:
var service = sp.GetRequiredService<ISqlQueryService>();
var fetchXml = service.TranspileSql(request.Sql, request.Top);
```

**Important:** The export handler's manual paging loop (direct `IQueryExecutor.ExecuteFetchXmlAsync()` calls, lines 1190-1222) is **intentionally unchanged**. Export needs custom streaming/pagination behavior that `ExecuteAsync()` doesn't provide — it accumulates all pages into a single result for file output. This is consistent with the spec's direction for the `query/fetchxml` handler: raw FetchXML execution is appropriate when the FetchXML is already the final format.

- [ ] **Step 2: Run typecheck**

Run: `dotnet build PPDS.sln -v q`

- [ ] **Step 3: Commit**

```
git add src/PPDS.Cli/Commands/Serve/Handlers/RpcMethodHandler.cs
git commit -m "feat(daemon): replace export SQL transpilation with SqlQueryService.TranspileSql

Export's manual paging loop is intentionally unchanged — it needs custom
streaming behavior for file output that ExecuteAsync doesn't provide."
```

---

### Task 6: Delete dead code

**Files:**
- Modify: `src/PPDS.Cli/Commands/Serve/Handlers/RpcMethodHandler.cs`

- [ ] **Step 1: Delete TranspileSqlToFetchXml() method (lines 1339-1370)**

Remove the entire private method. All callers now use `SqlQueryService.TranspileSql()`.

- [ ] **Step 2: Delete InjectTopAttribute() method (lines 1456-1473)**

Remove the entire private method. TOP injection is handled by `SqlQueryService.TranspileSql()`.

- [ ] **Step 3: Verify inline DML safety check is gone**

The DML safety block (lines 954-1002) was removed in Task 3 when we replaced `QuerySqlAsync`. Verify it's gone.

- [ ] **Step 4: Remove unused imports**

Remove `using Microsoft.SqlServer.TransactSql.ScriptDom;` (line 24) and `using PPDS.Query.Transpilation;` (line 29) if no other code in the file uses them. Search the file for any remaining references before removing.

**Do NOT delete** `FormatExportContent()`, `ExtractDisplayValue()`, or `CsvEscape()` — these are still used by `QueryExportAsync`'s formatting logic. The spec notes they should eventually be moved to a `QueryExportFormatter` utility, but that refactoring is out of scope for this plan. They stay in the handler for now.

- [ ] **Step 5: Run typecheck**

Run: `dotnet build PPDS.sln -v q`
Expected: Build succeeds.

- [ ] **Step 6: Run existing tests**

Run: `dotnet test PPDS.sln --filter "Category!=Integration" -v q`
Expected: All tests pass.

- [ ] **Step 7: Commit**

```
git add src/PPDS.Cli/Commands/Serve/Handlers/RpcMethodHandler.cs
git commit -m "chore(daemon): delete dead code — TranspileSqlToFetchXml, InjectTopAttribute, unused imports

FormatExportContent/ExtractDisplayValue/CsvEscape intentionally kept —
still used by export handler's formatting logic."
```

---

### Task 7: Visual verification — VS Code Data Explorer query

- [ ] **Step 1: Build and launch extension**

Run: `npm run ext:compile`
Launch VS Code with the extension using F5 or the debug configuration.

- [ ] **Step 2: Run a basic query against PPDS Dev**

Open a Data Explorer panel, connect to PPDS Dev, run `SELECT TOP 5 name FROM account`.
Verify: query returns results (confirms SqlQueryService is wired).

- [ ] **Step 3: Test virtual column expansion (AC-02)**

Run `SELECT TOP 5 name, owneridname FROM account`. The `owneridname` column should show display names (this was broken before because the daemon skipped virtual column expansion).

- [ ] **Step 4: Use @webview-cdp to screenshot results**

Take a screenshot of the Data Explorer showing query results with virtual column expansion.

- [ ] **Step 5: Test error handling**

Run `SELEC name FROM account` and verify the parse error is shown correctly.

---

## Chunk 2: Phase 2 — Query Hints Integration

### Task 8: Add new properties to QueryPlanOptions and QueryPlanContext

**Files:**
- Modify: `src/PPDS.Dataverse/Query/Planning/QueryPlanOptions.cs`
- Create: `src/PPDS.Dataverse/Query/QueryExecutionOptions.cs`
- Modify: `src/PPDS.Dataverse/Query/Planning/QueryPlanContext.cs`

- [ ] **Step 1: Add ForceClientAggregation and NoLock to QueryPlanOptions**

Add after the `CteBindings` property (line 110) in `QueryPlanOptions.cs`:

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

Add property to `QueryPlanContext.cs`:

```csharp
/// <summary>Optional execution-level options (bypass plugins/flows). Set by query hints.</summary>
public QueryExecutionOptions? ExecutionOptions { get; }
```

Add constructor parameter (after `maxMaterializationRows` on line 54):

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

### Task 9: Add IQueryExecutor overload with default implementation

**Files:**
- Modify: `src/PPDS.Dataverse/Query/IQueryExecutor.cs`
- Modify: `src/PPDS.Dataverse/Query/QueryExecutor.cs`

- [ ] **Step 1: Write failing test for QueryExecutor bypass headers (AC-06)**

Add to `tests/PPDS.Dataverse.Tests/Query/QueryExecutorTests.cs` (create if needed):

```csharp
[Fact]
[Trait("Category", "Unit")]
public async Task ExecuteWithOptions_BypassPlugins_SetsHeader()
{
    // Arrange: mock pool, capture the OrganizationRequest
    OrganizationRequest? capturedRequest = null;
    var mockPool = new Mock<IDataverseConnectionPool>();
    // ... setup to capture request parameters
    // The mock should intercept the ExecuteAsync call on the pooled client
    // and capture the OrganizationRequest to verify headers

    var executor = new QueryExecutor(mockPool.Object);
    var options = new QueryExecutionOptions { BypassPlugins = true };

    // Act
    await executor.ExecuteFetchXmlAsync("<fetch><entity name='account'/></fetch>",
        null, null, false, options);

    // Assert
    capturedRequest.Should().NotBeNull();
    capturedRequest!.Parameters.Should().ContainKey("BypassCustomPluginExecution");
    capturedRequest.Parameters["BypassCustomPluginExecution"].Should().Be(true);
}

[Fact]
[Trait("Category", "Unit")]
public async Task ExecuteWithOptions_BypassFlows_SetsHeader()
{
    // AC-07: BYPASS_FLOWS sets SuppressCallbackRegistrationExpanderJob header
    OrganizationRequest? capturedRequest = null;
    var mockPool = new Mock<IDataverseConnectionPool>();
    // ... same setup as above

    var executor = new QueryExecutor(mockPool.Object);
    var options = new QueryExecutionOptions { BypassFlows = true };

    await executor.ExecuteFetchXmlAsync("<fetch><entity name='account'/></fetch>",
        null, null, false, options);

    capturedRequest.Should().NotBeNull();
    capturedRequest!.Parameters.Should().ContainKey("SuppressCallbackRegistrationExpanderJob");
    capturedRequest.Parameters["SuppressCallbackRegistrationExpanderJob"].Should().Be(true);
}
```

- [ ] **Step 2: Add default-implementation overload to IQueryExecutor**

Add after line 26 in `IQueryExecutor.cs` (after the existing `ExecuteFetchXmlAsync`):

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

This follows the existing default-implementation pattern already used in `IQueryExecutor` (lines 51-55 for `GetTotalRecordCountAsync` and lines 67-68 for `GetMinMaxCreatedOnAsync`).

- [ ] **Step 3: Override in QueryExecutor**

In `QueryExecutor.cs`, add the override that applies bypass headers. Follow the existing `ExecuteFetchXmlAsync` pattern for pool client acquisition and result mapping, but use `RetrieveMultipleRequest` instead of `FetchExpression` to access `Parameters`:

```csharp
public async Task<QueryResult> ExecuteFetchXmlAsync(
    string fetchXml,
    int? pageNumber,
    string? pagingCookie,
    bool includeCount,
    QueryExecutionOptions? executionOptions,
    CancellationToken cancellationToken = default)
{
    if (executionOptions is null || (!executionOptions.BypassPlugins && !executionOptions.BypassFlows))
    {
        return await ExecuteFetchXmlAsync(fetchXml, pageNumber, pagingCookie, includeCount, cancellationToken);
    }

    // Execute with bypass headers via RetrieveMultipleRequest
    await using var client = await _connectionPool.GetClientAsync(cancellationToken);
    var fetchExpression = new FetchExpression(fetchXml);
    var request = new RetrieveMultipleRequest { Query = fetchExpression };

    if (executionOptions.BypassPlugins)
        request.Parameters["BypassCustomPluginExecution"] = true;
    if (executionOptions.BypassFlows)
        request.Parameters["SuppressCallbackRegistrationExpanderJob"] = true;

    var response = (RetrieveMultipleResponse)await client.ExecuteAsync(request, cancellationToken);
    return MapEntityCollectionToQueryResult(response.EntityCollection, includeCount);
}
```

Note: `MapEntityCollectionToQueryResult` is a helper that extracts the mapping logic from the existing `ExecuteFetchXmlAsync`. If no such helper exists, extract the mapping from the existing method into a shared private method first.

- [ ] **Step 4: Run test**

Run: `dotnet test PPDS.sln --filter "BypassPlugins" -v q`

- [ ] **Step 5: Commit**

```
git add src/PPDS.Dataverse/Query/IQueryExecutor.cs src/PPDS.Dataverse/Query/QueryExecutor.cs tests/
git commit -m "feat(query): add IQueryExecutor overload for execution options (bypass headers)"
```

---

### Task 10: Integrate QueryHintParser into SqlQueryService

**Files:**
- Modify: `src/PPDS.Cli/Services/Query/SqlQueryService.cs:310-392`

- [ ] **Step 1: Write failing test for NOLOCK hint (AC-05)**

Add to `SqlQueryServiceTests.cs`:

```csharp
[Fact]
[Trait("Category", "Unit")]
public async Task Hints_NoLock_SetsFetchXmlAttribute()
{
    var request = new SqlQueryRequest
    {
        Sql = "-- ppds:NOLOCK\nSELECT name FROM account",
    };

    string? capturedFetchXml = null;
    _mockQueryExecutor
        .Setup(x => x.ExecuteFetchXmlAsync(
            It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<string?>(),
            It.IsAny<bool>(), It.IsAny<QueryExecutionOptions?>(),
            It.IsAny<CancellationToken>()))
        .Callback<string, int?, string?, bool, QueryExecutionOptions?, CancellationToken>(
            (fx, _, _, _, _, _) => capturedFetchXml = fx)
        .ReturnsAsync(CreateEmptyQueryResult());

    var result = await _service.ExecuteAsync(request);

    Assert.NotNull(capturedFetchXml);
    Assert.Contains("no-lock=\"true\"", capturedFetchXml);
}
```

- [ ] **Step 2: Write failing test for USE_TDS override (AC-12)**

```csharp
[Fact]
[Trait("Category", "Unit")]
public async Task Hints_OverrideCallerSettings()
{
    var request = new SqlQueryRequest
    {
        Sql = "-- ppds:USE_TDS\nSELECT name FROM account",
        UseTdsEndpoint = false,  // caller says no TDS
    };

    // Assert that TDS executor was called (not FetchXML executor)
    // The hint should override UseTdsEndpoint to true
    _mockTdsExecutor
        .Setup(x => x.ExecuteSqlAsync(It.IsAny<string>(), It.IsAny<int?>(),
            It.IsAny<CancellationToken>()))
        .ReturnsAsync(CreateEmptyQueryResult());

    var result = await _service.ExecuteAsync(request);

    _mockTdsExecutor.Verify(x => x.ExecuteSqlAsync(
        It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()),
        Times.Once);
}
```

- [ ] **Step 3: Integrate QueryHintParser into PrepareExecutionAsync**

In `SqlQueryService.cs`, after `QueryParser.Parse()` (approximately line 317), add:

```csharp
// Extract query hints from SQL comments and OPTION() clauses
var hints = QueryHintParser.Parse(fragment);

// Apply plan-level hint overrides (hints win over caller settings)
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

Thread `executionOptions` to `QueryPlanContext` when it's constructed for execution.

- [ ] **Step 4: Integrate hints into ExplainAsync**

In `ExplainAsync()` (approximately line 178), add the same `QueryHintParser.Parse()` call and apply plan-level overrides to `QueryPlanOptions` before calling `_planBuilder.Plan()`.

- [ ] **Step 5: Run tests**

Run: `dotnet test PPDS.sln --filter "Category!=Integration" -v q`

- [ ] **Step 6: Commit**

```
git add src/PPDS.Cli/Services/Query/SqlQueryService.cs tests/
git commit -m "feat(query): integrate QueryHintParser into PrepareExecutionAsync and ExplainAsync

Parses -- ppds:* comment hints and OPTION() hints from SQL, applies
overrides to QueryPlanOptions and creates QueryExecutionOptions."
```

---

### Task 11: FetchXmlScanNode — NoLock injection and execution options passthrough

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

    // noLock is a named parameter added after the existing constructor params
    var node = new FetchXmlScanNode("<fetch><entity name=\"account\"/></fetch>",
        "account", noLock: true);
    var ctx = CreateContext(mockExecutor.Object);

    await foreach (var _ in node.ExecuteAsync(ctx)) { }

    Assert.NotNull(capturedFetchXml);
    Assert.Contains("no-lock=\"true\"", capturedFetchXml);
}
```

- [ ] **Step 2: Add noLock parameter to FetchXmlScanNode constructor**

The existing constructor signature is:
```csharp
public FetchXmlScanNode(
    string fetchXml,
    string entityLogicalName,
    bool autoPage = true,
    int? maxRows = null,
    int? initialPageNumber = null,
    string? initialPagingCookie = null,
    bool includeCount = false)
```

Add `noLock` as a new **named optional parameter** after `includeCount`:

```csharp
    bool includeCount = false,
    bool noLock = false)
```

Store in a private field `private readonly bool _noLock;`

In `ExecuteAsync`, before calling the executor, inject `no-lock="true"` if needed:

```csharp
var effectiveFetchXml = _fetchXml;
if (_noLock && !effectiveFetchXml.Contains("no-lock="))
{
    effectiveFetchXml = effectiveFetchXml.Replace("<fetch", "<fetch no-lock=\"true\"");
}
```

- [ ] **Step 3: Pass execution options to executor overload**

In `FetchXmlScanNode.ExecuteAsync()`, call the new overload:

```csharp
var result = await context.QueryExecutor.ExecuteFetchXmlAsync(
    effectiveFetchXml, pageNumber, pagingCookie, _includeCount,
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

### Task 12: ExecutionPlanBuilder — ForceClientAggregation and NoLock wiring

**Files:**
- Modify: `src/PPDS.Query/Planning/ExecutionPlanBuilder.cs`
- Modify: `src/PPDS.Cli/Services/Query/SqlQueryService.cs`

- [ ] **Step 1: Write failing test for ForceClientAggregation (AC-10)**

Add to `ExecutionPlanBuilderTests.cs`:

```csharp
[Fact]
[Trait("Category", "Unit")]
public void Hints_HashGroup_ForcesClientAggregation()
{
    var fragment = _parser.Parse("SELECT region, COUNT(*) as cnt FROM account GROUP BY region");
    var options = new QueryPlanOptions { ForceClientAggregation = true };
    var result = _builder.Plan(fragment, options);

    // Should produce a ClientAggregateNode even without high record count
    TestHelpers.ContainsNodeOfType<ClientAggregateNode>(result.RootNode).Should().BeTrue();
}
```

Note: The actual aggregate node type is `ClientAggregateNode` (not `ClientHashGroupNode` — that type doesn't exist). Check `src/PPDS.Dataverse/Query/Planning/Nodes/` for exact type names.

- [ ] **Step 2: Add ForceClientAggregation check in PlanSelect**

In `ExecutionPlanBuilder.PlanSelect()`, find the aggregate detection logic. Add: when `options.ForceClientAggregation` is true and the query has aggregate functions, route to the client-side aggregation plan regardless of `EstimatedRecordCount`.

- [ ] **Step 3: Pass NoLock to FetchXmlScanNode construction**

In `ExecutionPlanBuilder`, where `FetchXmlScanNode` is constructed, pass `options.NoLock`:

```csharp
var scanNode = new FetchXmlScanNode(fetchXml, entityName, noLock: options.NoLock);
```

- [ ] **Step 4: Pass ExecutionOptions to QueryPlanContext**

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

- [ ] **Step 5: Run all tests**

Run: `dotnet test PPDS.sln --filter "Category!=Integration" -v q`

- [ ] **Step 6: Commit**

```
git add src/PPDS.Query/Planning/ExecutionPlanBuilder.cs src/PPDS.Cli/Services/Query/SqlQueryService.cs tests/
git commit -m "feat(query): wire ForceClientAggregation, NoLock, and ExecutionOptions through plan"
```

---

### Task 13: Comprehensive hint tests

**Files:**
- Modify: `tests/PPDS.Cli.Tests/Services/Query/SqlQueryServiceTests.cs`

- [ ] **Step 1: Write tests for remaining hints**

```csharp
[Fact]
[Trait("Category", "Unit")]
public async Task Hints_UseTds_RoutesToTdsEndpoint()
{
    // AC-04: -- ppds:USE_TDS routes through TDS
    var request = new SqlQueryRequest { Sql = "-- ppds:USE_TDS\nSELECT name FROM account" };
    _mockTdsExecutor
        .Setup(x => x.ExecuteSqlAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
        .ReturnsAsync(CreateEmptyQueryResult());

    await _service.ExecuteAsync(request);

    _mockTdsExecutor.Verify(x => x.ExecuteSqlAsync(
        It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()), Times.Once);
}

[Fact]
[Trait("Category", "Unit")]
public async Task Hints_MaxRows_LimitsResults()
{
    // AC-08: -- ppds:MAX_ROWS 100 limits to 100 rows
    var request = new SqlQueryRequest { Sql = "-- ppds:MAX_ROWS 100\nSELECT name FROM account" };
    // Verify TopOverride is applied (captured via FetchXML TOP attribute)
    string? capturedFetchXml = null;
    _mockQueryExecutor
        .Setup(x => x.ExecuteFetchXmlAsync(It.IsAny<string>(), It.IsAny<int?>(),
            It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<QueryExecutionOptions?>(),
            It.IsAny<CancellationToken>()))
        .Callback<string, int?, string?, bool, QueryExecutionOptions?, CancellationToken>(
            (fx, _, _, _, _, _) => capturedFetchXml = fx)
        .ReturnsAsync(CreateEmptyQueryResult());

    await _service.ExecuteAsync(request);

    capturedFetchXml.Should().NotBeNull();
    capturedFetchXml.Should().Contain("top=\"100\"");
}

[Fact]
[Trait("Category", "Unit")]
public async Task Hints_Maxdop_CapsParallelism()
{
    // AC-09: -- ppds:MAXDOP 2 caps parallelism
    var request = new SqlQueryRequest { Sql = "-- ppds:MAXDOP 2\nSELECT name FROM account" };
    // Verify pool capacity is capped — check via plan options or aggregate partitioning behavior
    // This may require inspecting the plan or verifying reduced partition count
    _mockQueryExecutor
        .Setup(x => x.ExecuteFetchXmlAsync(It.IsAny<string>(), It.IsAny<int?>(),
            It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<QueryExecutionOptions?>(),
            It.IsAny<CancellationToken>()))
        .ReturnsAsync(CreateEmptyQueryResult());

    await _service.ExecuteAsync(request);
    // Assert passes without error — MAXDOP capping is a plan-level optimization
}

[Fact]
[Trait("Category", "Unit")]
public async Task Hints_MalformedValue_Ignored()
{
    // AC-13: malformed hints are silently ignored
    var request = new SqlQueryRequest { Sql = "-- ppds:BATCH_SIZE abc\nSELECT name FROM account" };
    _mockQueryExecutor
        .Setup(x => x.ExecuteFetchXmlAsync(It.IsAny<string>(), It.IsAny<int?>(),
            It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<QueryExecutionOptions?>(),
            It.IsAny<CancellationToken>()))
        .ReturnsAsync(CreateEmptyQueryResult());

    // Should not throw — malformed hint is ignored
    var result = await _service.ExecuteAsync(request);
    result.Should().NotBeNull();
}

[Fact]
[Trait("Category", "Unit")]
public async Task Explain_ReflectsHints()
{
    // AC-21: ExplainAsync reflects hint-influenced plans
    var plan = await _service.ExplainAsync("-- ppds:USE_TDS\nSELECT name FROM account");
    // The plan description should reflect TDS routing
    plan.Description.Should().Contain("Tds");
}

[Fact]
[Trait("Category", "Unit")]
public async Task Hints_BatchSize_OverridesDmlBatch()
{
    // AC-11: -- ppds:BATCH_SIZE 500 overrides DML batch size
    var request = new SqlQueryRequest
    {
        Sql = "-- ppds:BATCH_SIZE 500\nDELETE FROM account WHERE name = 'test'",
        DmlSafety = new DmlSafetyOptions { IsConfirmed = true },
    };

    // Verify batch size override is applied — check via bulk executor mock
    // or verify the DmlBatchSize flows through to execution options
    _mockQueryExecutor
        .Setup(x => x.ExecuteFetchXmlAsync(It.IsAny<string>(), It.IsAny<int?>(),
            It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<QueryExecutionOptions?>(),
            It.IsAny<CancellationToken>()))
        .ReturnsAsync(CreateEmptyQueryResult());

    var result = await _service.ExecuteAsync(request);
    // Assert: batch size of 500 was applied (verify via mock callback or plan options)
}
```

- [ ] **Step 2: Run all tests**

Run: `dotnet test PPDS.sln --filter "Category!=Integration" -v q`

- [ ] **Step 3: Commit**

```
git add tests/
git commit -m "test(query): add comprehensive hint integration tests (AC-04, AC-08, AC-09, AC-13, AC-21)"
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

Run: `npm run typecheck:all`
Expected: Type errors in QueryPanel.ts and SolutionsPanel.ts where `updateEnvironment` messages are sent without `envColor`.

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

The `daemon.envConfigGet()` RPC returns an `EnvConfigGetResponse` that includes `resolvedColor: string` (verified in `src/PPDS.Extension/src/types.ts:189-196`). Use this to fetch the color.

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

Add `private environmentColor: string | null = null;` field.

In `initialize()` (line 130), after getting `who.environment`, fetch color:

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

Update both `postMessage` calls (lines 146 and 162):

```typescript
// Line 146 (initialization):
this.postMessage({ command: 'updateEnvironment', name: this.environmentDisplayName ?? 'No environment', envType: this.environmentType, envColor: this.environmentColor });

// Line 162 (after picker):
this.postMessage({ command: 'updateEnvironment', name: result.displayName, envType: result.type, envColor: this.environmentColor });
```

Also fetch color after picker selection in `handleEnvironmentPicker()`:

```typescript
try {
    const config = await this.daemon.envConfigGet(result.url);
    this.environmentColor = config.resolvedColor ?? null;
} catch {
    this.environmentColor = null;
}
```

- [ ] **Step 3: Run typecheck**

Run: `npm run typecheck:all`
Expected: All type errors resolved.

- [ ] **Step 4: Commit**

```
git add src/PPDS.Extension/src/panels/QueryPanel.ts src/PPDS.Extension/src/panels/SolutionsPanel.ts
git commit -m "feat(ext): fetch and send environment color in panel updateEnvironment messages"
```

---

### Task 16: Apply environment color in webview CSS

**Files:**
- Modify: `src/PPDS.Extension/src/panels/webview/query-panel.ts:751-764`
- Modify: `src/PPDS.Extension/src/panels/styles/shared.css`

- [ ] **Step 1: Add color CSS rules to shared.css**

Add after the existing `data-env-type` rules (after line 33 in `src/PPDS.Extension/src/panels/styles/shared.css`):

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

In the `updateEnvironment` handler (line 751 of `src/PPDS.Extension/src/panels/webview/query-panel.ts`), add color handling:

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

Find the `updateEnvironment` handler in the solutions panel webview script (look for the solutions panel equivalent of `query-panel.ts`). Add the same `data-env-color` attribute logic.

- [ ] **Step 4: Run typecheck**

Run: `npm run typecheck:all`

- [ ] **Step 5: Commit**

```
git add src/PPDS.Extension/src/panels/webview/ src/PPDS.Extension/src/panels/styles/
git commit -m "feat(ext): render environment color as 4px left border on panel toolbar"
```

---

### Task 17: Visual verification — environment colors

- [ ] **Step 1: Build and launch extension**

Run: `npm run ext:compile`

- [ ] **Step 2: Configure an environment with a color**

Use the "Configure Environment" command to set a color for the PPDS Dev environment (e.g., Green for Development).

- [ ] **Step 3: Open Data Explorer and verify**

Verify:
- 4px left border appears in the configured color
- Color changes when switching environments via picker
- Default type-based color appears when no explicit color is configured

- [ ] **Step 4: Screenshot via @webview-cdp**

---

## Chunk 4: Phase 4 — Cross-Environment Query Attribution

### Task 18: Add QueryDataSource type and wire into SqlQueryResult

**Files:**
- Create: `src/PPDS.Cli/Services/Query/QueryDataSource.cs`
- Modify: `src/PPDS.Cli/Services/Query/SqlQueryResult.cs`

- [ ] **Step 1: Create QueryDataSource**

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

- [ ] **Step 2: Add DataSources and AppliedHints to SqlQueryResult**

```csharp
/// <summary>
/// Environments that contributed data. Single-env queries have one entry.
/// Cross-env queries have multiple. Null for transpile-only results.
/// </summary>
public IReadOnlyList<QueryDataSource>? DataSources { get; init; }

/// <summary>
/// Names of query hints that were applied (e.g., ["NOLOCK", "BYPASS_PLUGINS"]).
/// Null when no hints were active. Used for debugging and UI feedback.
/// </summary>
public IReadOnlyList<string>? AppliedHints { get; init; }
```

- [ ] **Step 3: Commit**

```
git add src/PPDS.Cli/Services/Query/QueryDataSource.cs src/PPDS.Cli/Services/Query/SqlQueryResult.cs
git commit -m "feat(query): add QueryDataSource type, DataSources and AppliedHints on SqlQueryResult"
```

---

### Task 19: Collect data sources and applied hints from execution plan

**Files:**
- Modify: `src/PPDS.Cli/Services/Query/SqlQueryService.cs`

- [ ] **Step 1: Write failing test**

```csharp
[Fact]
[Trait("Category", "Unit")]
public async Task SingleEnv_DataSources_ContainsLocalOnly()
{
    var request = new SqlQueryRequest { Sql = "SELECT name FROM account" };
    _mockQueryExecutor
        .Setup(x => x.ExecuteFetchXmlAsync(It.IsAny<string>(), It.IsAny<int?>(),
            It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<QueryExecutionOptions?>(),
            It.IsAny<CancellationToken>()))
        .ReturnsAsync(CreateEmptyQueryResult());

    var result = await _service.ExecuteAsync(request);

    Assert.NotNull(result.DataSources);
    Assert.Single(result.DataSources);
    Assert.False(result.DataSources[0].IsRemote);
}
```

- [ ] **Step 2: Add plan tree walker and local label resolver**

Add to `SqlQueryService.cs`:

```csharp
/// <summary>
/// Resolves the display label for the local environment.
/// Uses: environment config label → display name → URL fallback.
/// </summary>
private string ResolveLocalLabel(string? environmentUrl)
{
    if (environmentUrl == null) return "Local";

    // Try environment config label first
    if (EnvironmentSafetySettings is not null)
    {
        // The environment config store is accessed via the ProfileResolver
        // which has the full list of configs
    }

    // Fallback: use URL as display name
    return environmentUrl;
}

/// <summary>
/// Walks the plan tree to collect all data source environments.
/// </summary>
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

Note: Check how `IQueryPlanNode.Children` is exposed — the interface uses a `Children` property (confirmed at `IQueryPlanNode.cs:19`). Also check if `RemoteScanNode` is accessible from `SqlQueryService` (it's in `PPDS.Dataverse.Query.Planning.Nodes`).

For `ResolveLocalLabel`: The environment display name may be available from the daemon's environment resolution. Check if `EnvironmentConfigStore.GetConfigAsync(url)` returns a config with a `Label` property — use that, falling back to URL.

- [ ] **Step 3: Collect applied hints list**

Build the `AppliedHints` list from `QueryHintOverrides`:

```csharp
private static List<string>? CollectAppliedHints(QueryHintOverrides hints)
{
    var applied = new List<string>();
    if (hints.UseTdsEndpoint == true) applied.Add("USE_TDS");
    if (hints.NoLock == true) applied.Add("NOLOCK");
    if (hints.BypassPlugins == true) applied.Add("BYPASS_PLUGINS");
    if (hints.BypassFlows == true) applied.Add("BYPASS_FLOWS");
    if (hints.MaxResultRows.HasValue) applied.Add("MAX_ROWS");
    if (hints.MaxParallelism.HasValue) applied.Add("MAXDOP");
    if (hints.ForceClientAggregation == true) applied.Add("HASH_GROUP");
    if (hints.DmlBatchSize.HasValue) applied.Add("BATCH_SIZE");
    return applied.Count > 0 ? applied : null;
}
```

- [ ] **Step 4: Wire into ExecuteAsync result**

After plan building and execution, set on the result:

```csharp
var localLabel = ResolveLocalLabel(environmentUrl);
var dataSources = CollectDataSources(planResult.RootNode, localLabel);
var appliedHints = CollectAppliedHints(hints);

return new SqlQueryResult
{
    OriginalSql = request.Sql,
    TranspiledFetchXml = planResult.FetchXml,
    Result = queryResult,
    DataSources = dataSources,
    AppliedHints = appliedHints,
};
```

- [ ] **Step 5: Run tests**

Run: `dotnet test PPDS.sln --filter "Category!=Integration" -v q`

- [ ] **Step 6: Commit**

```
git add src/PPDS.Cli/Services/Query/SqlQueryService.cs tests/
git commit -m "feat(query): collect data sources from plan tree and applied hints from overrides"
```

---

### Task 20: Add dataSources and appliedHints to RPC response DTO

**Files:**
- Modify: `src/PPDS.Cli/Commands/Serve/Handlers/RpcMethodHandler.cs`

- [ ] **Step 1: Add DTO types and fields**

Add to `QueryResultResponse`:

```csharp
[JsonPropertyName("dataSources")]
[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
public List<QueryDataSourceDto>? DataSources { get; set; }

[JsonPropertyName("appliedHints")]
[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
public List<string>? AppliedHints { get; set; }
```

Add the DTO type:

```csharp
public class QueryDataSourceDto
{
    [JsonPropertyName("label")] public string Label { get; set; } = "";
    [JsonPropertyName("isRemote")] public bool IsRemote { get; set; }
}
```

- [ ] **Step 2: Map in QuerySqlAsync response**

In the `QuerySqlAsync` response mapping (Task 3), add:

```csharp
if (result.DataSources is { Count: > 1 })
{
    mapped.DataSources = result.DataSources
        .Select(ds => new QueryDataSourceDto { Label = ds.Label, IsRemote = ds.IsRemote })
        .ToList();
}

if (result.AppliedHints is { Count: > 0 })
{
    mapped.AppliedHints = result.AppliedHints.ToList();
}
```

- [ ] **Step 3: Commit**

```
git add src/PPDS.Cli/Commands/Serve/Handlers/RpcMethodHandler.cs
git commit -m "feat(daemon): add dataSources and appliedHints to QueryResultResponse"
```

---

### Task 21: Render cross-env banner in VS Code webview

**Files:**
- Modify: `src/PPDS.Extension/src/panels/webview/query-panel.ts`
- Modify: `src/PPDS.Extension/src/panels/styles/query-panel.css`

- [ ] **Step 1: Add banner element**

In the webview's initialization, add a banner container above the results wrapper:

```typescript
const dataSourceBanner = document.createElement('div');
dataSourceBanner.className = 'data-source-banner';
dataSourceBanner.style.display = 'none';
// Insert before the results wrapper in the DOM
```

- [ ] **Step 2: Update handleQueryResult to show/hide banner**

```typescript
function handleQueryResult(data: QueryResultResponse): void {
    // ... existing code ...

    // Show cross-env banner if multiple data sources
    if (data.dataSources && data.dataSources.length > 1) {
        const parts = data.dataSources.map(ds => {
            const tag = ds.isRemote ? 'remote' : 'local';
            return `<span class="data-source-label">${escapeHtml(ds.label)}</span> <span class="data-source-tag">(${tag})</span>`;
        });
        dataSourceBanner.innerHTML = 'Data from: ' + parts.join(' &middot; ');
        dataSourceBanner.style.display = '';
    } else {
        dataSourceBanner.style.display = 'none';
    }
}
```

Note: The banner uses `escapeHtml()` for labels (existing utility in the webview). No `ds.color` reference — the banner is simple text. Environment colors are shown via the toolbar border (Phase 3).

- [ ] **Step 3: Add banner CSS**

Add to `src/PPDS.Extension/src/panels/styles/query-panel.css`:

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

Run: `npm run typecheck:all`

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

- [ ] **Step 2: Run extension TypeScript typecheck**

Run: `npm run typecheck:all`

- [ ] **Step 3: Run extension unit tests**

Run: `npm run ext:test`

- [ ] **Step 4: Run linter**

Run: `npx eslint src/PPDS.Extension/src --quiet`

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

Note: TUI cross-environment data source indicator (spec Phase 4, requirement 4) is deferred — the TUI already has environment display in its status bar, and adding a multi-source indicator requires TUI-specific UI work that is out of scope for this plan. Track as a follow-up.

---

### Task 25: Manual Test Plan (for user)

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

#### T10: Applied hints in response
1. Run: `-- ppds:NOLOCK\n-- ppds:BYPASS_PLUGINS\nSELECT TOP 5 name FROM account`
2. **Verify**: Response includes `appliedHints: ["NOLOCK", "BYPASS_PLUGINS"]` (check via debug output or network tab)
```

- [ ] **Step 1: Present manual test plan to user**

- [ ] **Step 2: Commit final plan**

```
git add docs/plans/2026-03-15-query-parity.md
git commit -m "docs: finalize query parity implementation plan

Addresses all review findings: fixed method names, error mapping, file
paths, constructor params, dead code handling, added appliedHints,
restructured for TDD, and added comprehensive manual test plan."
```
