# TDS Endpoint Wiring Implementation Plan

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Wire up `ITdsQueryExecutor` in DI, report actual execution mode honestly, re-enable UI toggles, and fail cleanly when TDS can't be used.

**Architecture:** `SqlQueryService` is the single orchestration point (Constitution A2). It pre-checks TDS compatibility before planning, sets `ExecutionMode` on results after execution, and catches TDS connection failures. DI registration happens in two sites (daemon + CLI), each creating a `TdsQueryExecutor` per-environment with a token provider adapted from `IPowerPlatformTokenProvider`.

**Tech Stack:** C# (.NET 8+), TypeScript, xUnit, Vitest

**Spec:** `specs/query-parity.md` Phase 5

---

## File Structure

| File | Action | Responsibility |
|------|--------|----------------|
| `src/PPDS.Cli/Infrastructure/Errors/ErrorCodes.cs` | Modify | Add `TdsIncompatible` and `TdsConnectionFailed` error codes |
| `src/PPDS.Cli/Services/Query/QueryExecutionMode.cs` | Create | `QueryExecutionMode` enum (`Dataverse`, `Tds`) |
| `src/PPDS.Cli/Services/Query/SqlQueryResult.cs` | Modify | Add `ExecutionMode` property |
| `src/PPDS.Cli/Services/Query/SqlQueryStreamChunk.cs` | Modify | Add `ExecutionMode` property |
| `src/PPDS.Cli/Services/Query/SqlQueryService.cs` | Modify | TDS pre-check, ExecutionMode setting, connection failure catch |
| `src/PPDS.Dataverse/Query/TdsQueryExecutor.cs` | Modify | Wrap `InvalidOperationException` in `PpdsException` (D4) |
| `src/PPDS.Cli/Infrastructure/DaemonConnectionPoolManager.cs` | Modify | Register `ITdsQueryExecutor` in daemon DI |
| `src/PPDS.Cli/Services/ServiceRegistration.cs` | Modify | Register `ITdsQueryExecutor` in CLI DI |
| `src/PPDS.Cli/Commands/Serve/Handlers/RpcMethodHandler.cs` | Modify | Map `ExecutionMode` to `queryMode`, add TDS error catch clauses |
| `src/PPDS.Extension/src/panels/webview/query-panel.ts` | Modify | Un-comment TDS menu item |
| `src/PPDS.Cli/Tui/Screens/SqlQueryScreen.cs` | Modify | Show actual execution mode in status label |
| `tests/PPDS.Dataverse.Tests/Query/TdsQueryExecutorTests.cs` | Modify | Add D4 compliance test |
| `tests/PPDS.Cli.Tests/Services/Query/SqlQueryServiceTests.cs` | Modify | Add TDS pre-check and ExecutionMode tests |

---

## Chunk 1: Foundation Types and Error Codes

### Task 1: Add TDS Error Codes

**Files:**
- Modify: `src/PPDS.Cli/Infrastructure/Errors/ErrorCodes.cs:179` (after `DmlConfirmationRequired`)

- [ ] **Step 1: Add error codes**

```csharp
/// <summary>Query is incompatible with TDS Endpoint (DML, unsupported entity, unsupported feature).</summary>
public const string TdsIncompatible = "Query.TdsIncompatible";

/// <summary>TDS Endpoint connection failed (endpoint may be disabled on environment).</summary>
public const string TdsConnectionFailed = "Query.TdsConnectionFailed";
```

- [ ] **Step 2: Build to verify no errors**

Run: `dotnet build src/PPDS.Cli/PPDS.Cli.csproj -v q`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```
feat(query): add TDS error codes for incompatible queries and connection failures
```

### Task 2: Create QueryExecutionMode Enum

**Files:**
- Create: `src/PPDS.Cli/Services/Query/QueryExecutionMode.cs`

- [ ] **Step 1: Create the enum file**

```csharp
namespace PPDS.Cli.Services.Query;

/// <summary>
/// The actual execution path used for a query.
/// </summary>
public enum QueryExecutionMode
{
    /// <summary>Query executed via FetchXML against Dataverse Web API.</summary>
    Dataverse,

    /// <summary>Query executed via TDS Endpoint (SQL Server wire protocol).</summary>
    Tds
}
```

- [ ] **Step 2: Add ExecutionMode to SqlQueryResult**

In `src/PPDS.Cli/Services/Query/SqlQueryResult.cs`, add after the `AppliedHints` property:

```csharp
/// <summary>
/// The actual execution path used. Null for transpile-only or dry-run results.
/// </summary>
public QueryExecutionMode? ExecutionMode { get; init; }
```

- [ ] **Step 3: Add ExecutionMode to SqlQueryStreamChunk**

In `src/PPDS.Cli/Services/Query/SqlQueryStreamChunk.cs`, add after the `AppliedHints` property:

```csharp
/// <summary>
/// The actual execution path used. Non-null only on the final chunk.
/// </summary>
public QueryExecutionMode? ExecutionMode { get; init; }
```

- [ ] **Step 4: Build to verify no errors**

Run: `dotnet build src/PPDS.Cli/PPDS.Cli.csproj -v q`
Expected: Build succeeded

- [ ] **Step 5: Commit**

```
feat(query): add QueryExecutionMode enum and wire to SqlQueryResult/SqlQueryStreamChunk
```

### Task 3: Fix TdsQueryExecutor D4 Compliance

**Architecture note:** `TdsQueryExecutor` is in `PPDS.Dataverse`, which cannot reference `PPDS.Cli` (circular dependency). The codebase solves this with `QueryExecutionException` + `QueryErrorCode` in `PPDS.Dataverse.Query.Execution` — the established Dataverse-layer exception pattern. The `ExceptionMapper` in `PPDS.Cli` then maps these to `PpdsException` at the CLI boundary.

**Files:**
- Modify: `src/PPDS.Dataverse/Query/Execution/QueryExecutionException.cs` — add `TdsIncompatible` constant to `QueryErrorCode`
- Modify: `src/PPDS.Dataverse/Query/TdsQueryExecutor.cs:58-64`
- Test: `tests/PPDS.Dataverse.Tests/Query/TdsQueryExecutorTests.cs`

- [ ] **Step 1: Add TdsIncompatible to QueryErrorCode**

In `src/PPDS.Dataverse/Query/Execution/QueryExecutionException.cs`, add to the `QueryErrorCode` static class (after the last constant `SubqueryMultipleRows`):

```csharp
/// <summary>Query is incompatible with TDS Endpoint (DML, unsupported entity, unsupported feature).</summary>
public const string TdsIncompatible = "Query.TdsIncompatible";

/// <summary>TDS Endpoint connection failed (endpoint may be disabled on environment).</summary>
public const string TdsConnectionFailed = "Query.TdsConnectionFailed";
```

- [ ] **Step 2: Write failing test**

In `tests/PPDS.Dataverse.Tests/Query/TdsQueryExecutorTests.cs`, add to the `ExecuteSqlAsync Validation` region:

```csharp
[Fact]
public async Task ExecuteSqlAsync_DmlStatement_ThrowsQueryExecutionException()
{
    var executor = new TdsQueryExecutor(
        "https://org.crm.dynamics.com",
        _ => Task.FromResult("token"));

    var ex = await Assert.ThrowsAsync<QueryExecutionException>(
        () => executor.ExecuteSqlAsync("DELETE FROM account"));
    Assert.Equal(QueryErrorCode.TdsIncompatible, ex.ErrorCode);
}
```

Add required using: `using PPDS.Dataverse.Query.Execution;`

- [ ] **Step 3: Run test to verify it fails**

Run: `dotnet test tests/PPDS.Dataverse.Tests/PPDS.Dataverse.Tests.csproj --filter "ExecuteSqlAsync_DmlStatement_ThrowsQueryExecutionException" -v q`
Expected: FAIL — currently throws `InvalidOperationException`, not `QueryExecutionException`

- [ ] **Step 4: Fix TdsQueryExecutor to throw QueryExecutionException**

In `src/PPDS.Dataverse/Query/TdsQueryExecutor.cs`, replace lines 58-64:

```csharp
var compatibility = TdsCompatibilityChecker.CheckCompatibility(sql);
if (compatibility != TdsCompatibility.Compatible)
{
    throw new InvalidOperationException(
        $"Query is not compatible with TDS Endpoint: {compatibility}. " +
        "Use FetchXML execution path instead.");
}
```

With:

```csharp
var compatibility = TdsCompatibilityChecker.CheckCompatibility(sql);
if (compatibility != TdsCompatibility.Compatible)
{
    throw new QueryExecutionException(
        QueryErrorCode.TdsIncompatible,
        $"Query is not compatible with TDS Endpoint: {compatibility}. " +
        "Use FetchXML execution path instead.");
}
```

Add `using PPDS.Dataverse.Query.Execution;` at the top.

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/PPDS.Dataverse.Tests/PPDS.Dataverse.Tests.csproj --filter "ExecuteSqlAsync_DmlStatement_ThrowsQueryExecutionException" -v q`
Expected: PASS

- [ ] **Step 6: Update existing test that asserts InvalidOperationException**

The existing `ExecuteSqlAsync_DmlStatement_ThrowsInvalidOperation` test at line 409 now fails. `QueryExecutionException` extends `InvalidOperationException`, so the existing test may still pass (subclass is caught by base type assertion). Either way, update it to assert the specific type:

```csharp
[Fact]
public async Task ExecuteSqlAsync_DmlStatement_ThrowsQueryExecutionException_WithCode()
{
    var executor = new TdsQueryExecutor(
        "https://org.crm.dynamics.com",
        _ => Task.FromResult("token"));

    var ex = await Assert.ThrowsAsync<QueryExecutionException>(
        () => executor.ExecuteSqlAsync("DELETE FROM account"));
    Assert.Contains("IncompatibleDml", ex.Message);
}
```

Delete the old `ExecuteSqlAsync_DmlStatement_ThrowsInvalidOperation` test.

- [ ] **Step 7: Run all TDS executor tests**

Run: `dotnet test tests/PPDS.Dataverse.Tests/PPDS.Dataverse.Tests.csproj --filter "TdsQueryExecutor" -v q`
Expected: All pass

- [ ] **Step 8: Commit**

```
fix(query): wrap TdsQueryExecutor incompatibility error in QueryExecutionException (D4 compliance)
```

---

## Chunk 2: SqlQueryService TDS Pre-Check and ExecutionMode

### Task 4: Add TDS Compatibility Pre-Check in SqlQueryService

**Files:**
- Modify: `src/PPDS.Cli/Services/Query/SqlQueryService.cs` — `PrepareExecutionAsync()` method
- Test: `tests/PPDS.Cli.Tests/Services/Query/SqlQueryServiceTests.cs`

- [ ] **Step 1: Write failing test — TDS with DML throws**

```csharp
[Fact]
public async Task Execute_TdsWithDml_ThrowsTdsIncompatible()
{
    // Arrange: SqlQueryService with a mock ITdsQueryExecutor
    var service = CreateServiceWithTdsExecutor();

    var request = new SqlQueryRequest
    {
        Sql = "DELETE FROM account WHERE name = 'test'",
        UseTdsEndpoint = true
    };

    // Act & Assert
    var act = () => service.ExecuteAsync(request);
    var ex = await act.Should().ThrowAsync<PpdsException>();
    ex.Which.ErrorCode.Should().Be(ErrorCodes.Query.TdsIncompatible);
}
```

Note: You'll need to look at the existing test file to understand the test setup pattern (how `SqlQueryService` is instantiated with mocks). Adapt the helper method name accordingly.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/PPDS.Cli.Tests/PPDS.Cli.Tests.csproj --filter "Execute_TdsWithDml_ThrowsTdsIncompatible" -v q`
Expected: FAIL — currently no pre-check, DML goes through planning

- [ ] **Step 3: Add the pre-check in PrepareExecutionAsync**

In `src/PPDS.Cli/Services/Query/SqlQueryService.cs`, in `PrepareExecutionAsync()`, add after **both** hint override blocks (after line 372 — the `MaxResultRows` hint block closing brace) and before the DML safety check (line 374):

```csharp
// TDS compatibility pre-check — fail fast before planning.
// When the user explicitly requests TDS, they get TDS or an error,
// never a silent substitution to Dataverse.
if (request.UseTdsEndpoint)
{
    var querySpec = ExtractQuerySpecification(fragment);
    var entityName = querySpec != null ? ExtractEntityName(querySpec) : null;
    var tdsCompatibility = TdsCompatibilityChecker.CheckCompatibility(
        request.Sql, entityName);

    if (tdsCompatibility != TdsCompatibility.Compatible)
    {
        var reason = tdsCompatibility switch
        {
            TdsCompatibility.IncompatibleDml =>
                "Cannot execute via TDS: DML statements (DELETE, UPDATE, INSERT) are not supported by the TDS Endpoint. Disable TDS mode to execute this query against Dataverse.",
            TdsCompatibility.IncompatibleEntity =>
                $"Cannot execute via TDS: The target entity is not available via the TDS Endpoint (elastic/virtual table). Disable TDS mode to query via Dataverse.",
            _ =>
                "Cannot execute via TDS: This query uses features not supported by the TDS Endpoint. Disable TDS mode to execute via Dataverse."
        };

        throw new PpdsException(ErrorCodes.Query.TdsIncompatible, reason);
    }
}
```

Note: `ExtractQuerySpecification` returns `QuerySpecification?` (nullable). The null check avoids a CS8604 compiler error since `TreatWarningsAsErrors` is enabled. For DML statements, `querySpec` may be null — that's fine because `CheckCompatibility(sql, null)` short-circuits on the DML keyword check.

Add `using PPDS.Dataverse.Query;` if not already present (for `TdsCompatibilityChecker` and `TdsCompatibility`).

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/PPDS.Cli.Tests/PPDS.Cli.Tests.csproj --filter "Execute_TdsWithDml_ThrowsTdsIncompatible" -v q`
Expected: PASS

- [ ] **Step 5: Write test — TDS with incompatible entity throws**

```csharp
[Fact]
public async Task Execute_TdsWithIncompatibleEntity_ThrowsTdsIncompatible()
{
    var service = CreateServiceWithTdsExecutor();

    var request = new SqlQueryRequest
    {
        Sql = "SELECT * FROM activityparty",
        UseTdsEndpoint = true
    };

    var act = () => service.ExecuteAsync(request);
    var ex = await act.Should().ThrowAsync<PpdsException>();
    ex.Which.ErrorCode.Should().Be(ErrorCodes.Query.TdsIncompatible);
}
```

Note: `TdsCompatibilityChecker.CheckCompatibility(sql, entityName)` checks entity compatibility only when `entityName` is provided. The `ExtractEntityName(ExtractQuerySpecification(fragment))` call extracts it from the parsed AST. Verify this works for `activityparty` — the entity name should be extracted from the FROM clause.

- [ ] **Step 6: Run to verify it passes**

Run: `dotnet test tests/PPDS.Cli.Tests/PPDS.Cli.Tests.csproj --filter "Execute_TdsWithIncompatibleEntity_ThrowsTdsIncompatible" -v q`
Expected: PASS (pre-check already handles this)

- [ ] **Step 7: Write test — TDS not requested, DML proceeds normally**

```csharp
[Fact]
public async Task Execute_NoTds_DmlProceeds()
{
    var service = CreateServiceWithTdsExecutor();

    var request = new SqlQueryRequest
    {
        Sql = "DELETE FROM account WHERE name = 'test'",
        UseTdsEndpoint = false,
        DmlSafety = new DmlSafetyOptions { IsConfirmed = true }
    };

    // Should NOT throw TdsIncompatible — DML safety may still block it,
    // but not the TDS pre-check
    var act = () => service.ExecuteAsync(request);
    await act.Should().NotThrowAsync<PpdsException>(
        because: "TDS pre-check only applies when UseTdsEndpoint is true");
}
```

Adapt this test based on what the existing test infrastructure supports — it may throw for other reasons (no real Dataverse connection).

- [ ] **Step 8: Commit**

```
feat(query): add TDS compatibility pre-check in SqlQueryService — fail, don't fall back
```

### Task 5: Set ExecutionMode on SqlQueryResult

**Files:**
- Modify: `src/PPDS.Cli/Services/Query/SqlQueryService.cs` — `ExecuteAsync()` and `ExecuteStreamingAsync()`
- Test: `tests/PPDS.Cli.Tests/Services/Query/SqlQueryServiceTests.cs`

- [ ] **Step 1: Write failing test — ExecutionMode is Tds when TdsScanNode**

```csharp
[Fact]
public async Task Execute_TdsPlan_SetsExecutionModeTds()
{
    // Arrange: service with ITdsQueryExecutor mock that returns results
    var service = CreateServiceWithWorkingTdsExecutor();

    var request = new SqlQueryRequest
    {
        Sql = "SELECT TOP 5 name FROM account",
        UseTdsEndpoint = true
    };

    // Act
    var result = await service.ExecuteAsync(request);

    // Assert
    result.ExecutionMode.Should().Be(QueryExecutionMode.Tds);
}
```

- [ ] **Step 2: Run test to verify it fails**

Expected: FAIL — `ExecutionMode` is null (not yet set)

- [ ] **Step 3: Add ExecutionMode detection helper**

Add a private helper method in `SqlQueryService`:

```csharp
/// <summary>
/// Detects the execution mode by walking the plan tree for TdsScanNode.
/// </summary>
private static QueryExecutionMode DetectExecutionMode(IQueryPlanNode rootNode)
{
    if (ContainsTdsScanNode(rootNode))
        return QueryExecutionMode.Tds;
    return QueryExecutionMode.Dataverse;
}

private static bool ContainsTdsScanNode(IQueryPlanNode node)
{
    if (node is TdsScanNode)
        return true;
    foreach (var child in node.Children)
    {
        if (ContainsTdsScanNode(child))
            return true;
    }
    return false;
}
```

Add `using PPDS.Dataverse.Query.Planning.Nodes;` if not already present.

- [ ] **Step 4: Set ExecutionMode in ExecuteAsync**

In `SqlQueryService.ExecuteAsync()`, update the return statement (around line 178) to include:

```csharp
return new SqlQueryResult
{
    OriginalSql = request.Sql,
    TranspiledFetchXml = planResult.FetchXml,
    Result = expandedResult,
    DataSources = dataSources,
    AppliedHints = appliedHints,
    ExecutionMode = DetectExecutionMode(planResult.RootNode)
};
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/PPDS.Cli.Tests/PPDS.Cli.Tests.csproj --filter "Execute_TdsPlan_SetsExecutionModeTds" -v q`
Expected: PASS

- [ ] **Step 6: Write test — ExecutionMode is Dataverse for standard query**

```csharp
[Fact]
public async Task Execute_FetchXmlPlan_SetsExecutionModeDataverse()
{
    var service = CreateService(); // no TDS executor

    var request = new SqlQueryRequest
    {
        Sql = "SELECT TOP 5 name FROM account",
        UseTdsEndpoint = false
    };

    var result = await service.ExecuteAsync(request);
    result.ExecutionMode.Should().Be(QueryExecutionMode.Dataverse);
}
```

- [ ] **Step 7: Run test to verify it passes**

Expected: PASS

- [ ] **Step 8: Set ExecutionMode on streaming final chunk**

In `SqlQueryService.ExecuteStreamingAsync()`, update the final chunk yield (around line 324):

```csharp
yield return new SqlQueryStreamChunk
{
    Rows = finalExpanded.rows,
    Columns = isFirstChunk ? finalExpanded.columns : null,
    EntityLogicalName = isFirstChunk ? planResult.EntityLogicalName : null,
    TotalRowsSoFar = totalRows,
    IsComplete = true,
    TranspiledFetchXml = isFirstChunk ? planResult.FetchXml : null,
    DataSources = streamDataSources,
    AppliedHints = streamAppliedHints,
    ExecutionMode = DetectExecutionMode(planResult.RootNode)
};
```

- [ ] **Step 9: Build and run all query service tests**

Run: `dotnet test tests/PPDS.Cli.Tests/PPDS.Cli.Tests.csproj --filter "SqlQueryService" -v q`
Expected: All pass

- [ ] **Step 10: Commit**

```
feat(query): set ExecutionMode on SqlQueryResult and SqlQueryStreamChunk from plan tree
```

### Task 6: Catch TDS Connection Failures in SqlQueryService

**Files:**
- Modify: `src/PPDS.Cli/Services/Query/SqlQueryService.cs` — `ExecuteAsync()` method
- Test: `tests/PPDS.Cli.Tests/Services/Query/SqlQueryServiceTests.cs`

- [ ] **Step 1: Write failing test**

```csharp
[Fact]
public async Task Execute_TdsConnectionFails_ThrowsTdsConnectionFailed()
{
    // Arrange: mock ITdsQueryExecutor that throws SqlException on execute
    var service = CreateServiceWithFailingTdsExecutor(
        new InvalidOperationException("Connection refused"));

    var request = new SqlQueryRequest
    {
        Sql = "SELECT TOP 5 name FROM account",
        UseTdsEndpoint = true
    };

    var act = () => service.ExecuteAsync(request);
    var ex = await act.Should().ThrowAsync<PpdsException>();
    ex.Which.ErrorCode.Should().Be(ErrorCodes.Query.TdsConnectionFailed);
}
```

Note: `SqlException` cannot be directly instantiated. Use the exception type that `TdsQueryExecutor` actually throws. Since `SqlConnection.OpenAsync` throws `SqlException`, and this is hard to mock, the test may need to use a wrapper approach. Alternatively, test with `InvalidOperationException` since that's what the mock can throw. Check what `_planExecutor.ExecuteAsync` propagates. Adapt accordingly.

- [ ] **Step 2: Run test to verify it fails**

Expected: FAIL — no catch around plan execution

- [ ] **Step 3: Add TDS connection failure catch in ExecuteAsync**

In `SqlQueryService.ExecuteAsync()`, wrap the plan execution (line 162) in a try/catch:

```csharp
QueryResult result;
try
{
    result = await _planExecutor.ExecuteAsync(planResult, context, cancellationToken);
}
catch (Exception ex) when (
    request.UseTdsEndpoint
    && ContainsTdsScanNode(planResult.RootNode)
    && ex is not OperationCanceledException
    && ex is not PpdsException)
{
    throw new PpdsException(
        ErrorCodes.Query.TdsConnectionFailed,
        $"TDS Endpoint connection failed: {ex.Message}. The TDS Endpoint may be disabled " +
        "on this environment. Disable TDS mode to query via Dataverse, or ask your Power " +
        "Platform admin to enable the TDS Endpoint.",
        ex);
}
```

The `when` filter ensures:
- Only catches when TDS was actually attempted (plan has TdsScanNode)
- Doesn't swallow cancellation
- Doesn't double-wrap PpdsException

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/PPDS.Cli.Tests/PPDS.Cli.Tests.csproj --filter "Execute_TdsConnectionFails_ThrowsTdsConnectionFailed" -v q`
Expected: PASS

- [ ] **Step 5: Add same catch in ExecuteStreamingAsync**

Apply the same pattern around the streaming execution path. The streaming path uses `_planExecutor.ExecuteStreamingAsync()` — wrap the `await foreach` in a try/catch with the same filter.

- [ ] **Step 6: Run all query service tests**

Run: `dotnet test tests/PPDS.Cli.Tests/PPDS.Cli.Tests.csproj --filter "SqlQueryService" -v q`
Expected: All pass

- [ ] **Step 7: Commit**

```
feat(query): catch TDS connection failures and wrap in PpdsException(TdsConnectionFailed)
```

---

## Chunk 3: DI Registration

### Task 7: Register ITdsQueryExecutor in Daemon Path

**Files:**
- Modify: `src/PPDS.Cli/Infrastructure/DaemonConnectionPoolManager.cs`

- [ ] **Step 1: Update CreateProviderFromSources signature**

Change the method signature to accept profile and environment URL:

```csharp
private ServiceProvider CreateProviderFromSources(
    IConnectionSource[] sources,
    ISecureCredentialStore credentialStore,
    AuthProfile profile,
    string environmentUrl)
```

- [ ] **Step 2: Add ITdsQueryExecutor registration**

In `CreateProviderFromSources`, after the `services.RegisterDataverseServices()` call (line 368) and before the connection pool registration (line 371), add:

```csharp
// TDS Endpoint executor — per-environment, uses same auth as connection pool
services.AddSingleton<ITdsQueryExecutor>(sp =>
{
    IPowerPlatformTokenProvider tokenProvider;
    if (profile.AuthMethod == AuthMethod.ClientSecret)
    {
        if (string.IsNullOrEmpty(profile.ApplicationId))
            throw new InvalidOperationException(
                $"Profile '{profile.DisplayIdentifier}' is configured for ClientSecret auth but has no ApplicationId.");

#pragma warning disable PPDS012
        var storedCredential = credentialStore.GetAsync(profile.ApplicationId).GetAwaiter().GetResult();
#pragma warning restore PPDS012
        if (storedCredential?.ClientSecret == null)
            throw new InvalidOperationException(
                $"Client secret not found for application '{profile.ApplicationId}'.");
        tokenProvider = PowerPlatformTokenProvider.FromProfileWithSecret(profile, storedCredential.ClientSecret);
    }
    else
    {
        tokenProvider = PowerPlatformTokenProvider.FromProfile(profile);
    }

    Func<CancellationToken, Task<string>> tdsTokenFunc = async ct =>
    {
        var token = await tokenProvider.GetTokenForResourceAsync(environmentUrl, ct)
            .ConfigureAwait(false);
        return token.AccessToken;
    };

    return new TdsQueryExecutor(
        environmentUrl,
        tdsTokenFunc,
        sp.GetService<ILogger<TdsQueryExecutor>>());
});
```

Add necessary usings:
```csharp
using PPDS.Auth.Credentials;
using PPDS.Dataverse.Query;
```

- [ ] **Step 3: Update the call site in CreatePoolEntryAsync**

In `CreatePoolEntryAsync` (around line 311), update the call to pass profile and environment URL:

```csharp
var serviceProvider = CreateProviderFromSources(
    sources.ToArray(),
    credentialStore,
    profile,
    environmentUrl);
```

Note: `profile` is available at line 295 (`collection.GetByNameOrIndex(profileName)`). Since there may be multiple profiles, use the first one — TDS authentication only needs one identity.

Move the profile resolution before the foreach loop, or capture the first profile:

```csharp
AuthProfile? firstProfile = null;
foreach (var profileName in profileNames)
{
    var profile = collection.GetByNameOrIndex(profileName)
        ?? throw new InvalidOperationException($"Profile '{profileName}' not found.");
    firstProfile ??= profile;
    // ... rest of loop (source creation, adapter wrapping)
}

// Guard: profileNames is always non-empty (validated by caller), but be explicit
if (firstProfile == null)
    throw new InvalidOperationException("No profiles provided for pool creation.");

var serviceProvider = CreateProviderFromSources(
    sources.ToArray(),
    credentialStore,
    firstProfile,
    environmentUrl);
```

- [ ] **Step 4: Build to verify no errors**

Run: `dotnet build src/PPDS.Cli/PPDS.Cli.csproj -v q`
Expected: Build succeeded

- [ ] **Step 5: Commit**

```
feat(query): register ITdsQueryExecutor in daemon DI path
```

**Token scope note:** `GetTokenForResourceAsync(environmentUrl)` uses scope `{environmentUrl}/.default`. The TDS Endpoint (port 5558) and Web API share the same Azure AD resource/app registration, so this token is valid for both. If the audience is wrong, `SqlConnection.OpenAsync` will fail — caught by the TDS connection failure handler. The integration test in Chunk 5 will verify this end-to-end.

### Task 8: Register ITdsQueryExecutor in CLI Path

**Files:**
- Modify: `src/PPDS.Cli/Services/ServiceRegistration.cs`

- [ ] **Step 1: Add ITdsQueryExecutor registration**

In `AddCliApplicationServices()`, after the `ISqlQueryService` registration block (line 58) and before the `IQueryHistoryService` line (line 59), add:

```csharp
// TDS Endpoint executor — per-environment, uses same auth pattern as IConnectionService
services.AddTransient<ITdsQueryExecutor>(sp =>
{
    var connectionInfo = sp.GetRequiredService<ResolvedConnectionInfo>();
    var credentialStore = sp.GetRequiredService<ISecureCredentialStore>();
    var profile = connectionInfo.Profile;

    IPowerPlatformTokenProvider tokenProvider;
    if (profile.AuthMethod == AuthMethod.ClientSecret)
    {
        if (string.IsNullOrEmpty(profile.ApplicationId))
            throw new InvalidOperationException(
                $"Profile '{profile.DisplayIdentifier}' is configured for ClientSecret auth but has no ApplicationId.");

#pragma warning disable PPDS012
        var storedCredential = credentialStore.GetAsync(profile.ApplicationId).GetAwaiter().GetResult();
#pragma warning restore PPDS012
        if (storedCredential?.ClientSecret == null)
            throw new InvalidOperationException(
                $"Client secret not found for application '{profile.ApplicationId}'.");
        tokenProvider = PowerPlatformTokenProvider.FromProfileWithSecret(profile, storedCredential.ClientSecret);
    }
    else
    {
        tokenProvider = PowerPlatformTokenProvider.FromProfile(profile);
    }

    var environmentUrl = connectionInfo.EnvironmentUrl;

    Func<CancellationToken, Task<string>> tdsTokenFunc = async ct =>
    {
        var token = await tokenProvider.GetTokenForResourceAsync(environmentUrl, ct)
            .ConfigureAwait(false);
        return token.AccessToken;
    };

    return new TdsQueryExecutor(
        environmentUrl,
        tdsTokenFunc,
        sp.GetService<ILogger<TdsQueryExecutor>>());
});
```

Add `using PPDS.Dataverse.Query;` if not already present.

- [ ] **Step 2: Build to verify no errors**

Run: `dotnet build src/PPDS.Cli/PPDS.Cli.csproj -v q`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```
feat(query): register ITdsQueryExecutor in CLI DI path
```

---

## Chunk 4: RPC Handler and UI Updates

### Task 9: Map ExecutionMode in RpcMethodHandler

**Files:**
- Modify: `src/PPDS.Cli/Commands/Serve/Handlers/RpcMethodHandler.cs`

- [ ] **Step 1: Leave line 924 unchanged**

Line 924 is in the `query/fetch` handler (raw FetchXML) — it's always Dataverse, no TDS routing. Keep `mapped.QueryMode = "dataverse";` as-is.

- [ ] **Step 2: Replace hardcoded queryMode on line 1046 (query/sql handler)**

Replace:
```csharp
// Always "dataverse" — ITdsQueryExecutor is not registered in DI, so TDS never activates
mapped.QueryMode = "dataverse";
```

With:
```csharp
mapped.QueryMode = result.ExecutionMode switch
{
    QueryExecutionMode.Tds => "tds",
    _ => "dataverse"
};
```

Add `using PPDS.Cli.Services.Query;` if not already present (for `QueryExecutionMode`).

- [ ] **Step 3: Add TDS error catch clauses**

**IMPORTANT: Insertion order matters.** The catch chain has a general `catch (PpdsException ex)` at line 1090 that catches ALL PpdsExceptions. The new TDS-specific catches must go **between the `DmlBlocked` catch closing brace (line 1089) and the general catch (line 1090)**, not after the general catch — otherwise they'd be unreachable dead code.

Insert between lines 1089 and 1090:

```csharp
catch (PpdsException ex) when (ex.ErrorCode == ErrorCodes.Query.TdsIncompatible)
{
    throw new RpcException(ErrorCodes.Query.TdsIncompatible, ex.Message);
}
catch (PpdsException ex) when (ex.ErrorCode == ErrorCodes.Query.TdsConnectionFailed)
{
    throw new RpcException(ErrorCodes.Query.TdsConnectionFailed, ex.Message);
}
```

- [ ] **Step 4: Build to verify no errors**

Run: `dotnet build src/PPDS.Cli/PPDS.Cli.csproj -v q`
Expected: Build succeeded

- [ ] **Step 5: Commit**

```
feat(query): map ExecutionMode to queryMode in RPC handler, add TDS error clauses
```

### Task 10: Re-enable TDS Menu Item in Extension

**Files:**
- Modify: `src/PPDS.Extension/src/panels/webview/query-panel.ts`

- [ ] **Step 1: Un-comment TDS menu item**

In `query-panel.ts`, replace lines 510-512:

```typescript
// TODO: Re-enable when ITdsQueryExecutor is registered in DI (see ServiceCollectionExtensions.cs)
// { label: '', action: 'separator' },
// { label: 'TDS Read Replica', action: 'toggleTds', checked: useTds },
```

With:

```typescript
{ label: '', action: 'separator' },
{ label: 'TDS Read Replica', action: 'toggleTds', checked: useTds },
```

- [ ] **Step 2: Run typecheck**

Run: `npm run typecheck:all --prefix src/PPDS.Extension`
Expected: No errors

- [ ] **Step 3: Run extension tests**

Run: `npm run ext:test --prefix src/PPDS.Extension`
Expected: All pass

- [ ] **Step 4: Commit**

```
feat(ext): re-enable TDS Read Replica menu item in query panel
```

### Task 11: Fix TUI Status Label to Show Actual Execution Mode

**Files:**
- Modify: `src/PPDS.Cli/Tui/Screens/SqlQueryScreen.cs`

- [ ] **Step 1: Update status label after streaming completes**

In the streaming completion callback (around line 610), update `_statusText` to include execution mode. Find the line:

```csharp
_statusText = $"Returned {totalRows:N0} rows in {elapsedMs}ms";
```

Replace with a version that reads execution mode from the final chunk. You'll need to capture the execution mode during streaming. Add a variable before the `await foreach`:

```csharp
QueryExecutionMode? executionMode = null;
```

In the streaming loop, when `chunk.IsComplete` and `chunk.ExecutionMode` is set:

```csharp
if (chunkCapture.IsComplete && chunkCapture.ExecutionMode.HasValue)
{
    executionMode = chunkCapture.ExecutionMode;
}
```

Then in the finalize callback:

```csharp
var modeText = executionMode == QueryExecutionMode.Tds ? " via TDS" : " via Dataverse";
_statusText = $"Returned {totalRows:N0} rows in {elapsedMs}ms{modeText}";
```

Add `using PPDS.Cli.Services.Query;` if not already present.

- [ ] **Step 2: Build to verify no errors**

Run: `dotnet build src/PPDS.Cli/PPDS.Cli.csproj -v q`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```
feat(tui): show actual TDS/Dataverse execution mode in query status label
```

---

## Chunk 5: Verification

### Task 12: Run All Gates

- [ ] **Step 1: Run /gates**

Run the full quality gate suite: typecheck, lint, .NET build, extension tests, .NET unit tests.

- [ ] **Step 2: Fix any failures**

Address any compilation errors, test failures, or lint issues.

- [ ] **Step 3: Commit fixes if needed**

### Task 13: Visual Verification with CDP

- [ ] **Step 1: Open Query Panel, execute `SELECT TOP 5 name FROM account` with TDS OFF**

Verify: status shows "via Dataverse", results returned

- [ ] **Step 2: Toggle TDS ON via gear menu, execute same query**

Verify: either "via TDS" with results, or error message if TDS endpoint is disabled (this is correct behavior)

- [ ] **Step 3: Toggle TDS ON, execute DML query**

Verify: error message "Cannot execute via TDS: DML statements are not supported..." — query does NOT execute

- [ ] **Step 4: Check webview console for zero errors**

- [ ] **Step 5: Check PPDS output channel for TDS-related log messages**

### Task 14: TUI Verification

- [ ] **Step 1: Open TUI, run a query, verify status label says "via Dataverse"**

- [ ] **Step 2: Toggle Ctrl+Shift+T, run query, verify status reflects actual mode**

### Task 15: Remaining AC Test Coverage

These ACs need unit/integration tests that weren't covered in earlier tasks. Write them now that all code is in place.

- [ ] **Step 1: AC-38 — RPC maps ExecutionMode.Tds to queryMode "tds"**

In `tests/PPDS.Cli.Tests/Commands/Serve/Handlers/RpcMethodHandlerTests.cs`, add a test that verifies the `query/sql` handler returns `queryMode = "tds"` when `SqlQueryResult.ExecutionMode` is `Tds`. Follow the existing test patterns in this file.

- [ ] **Step 2: AC-44 — Streaming final chunk has ExecutionMode**

In `tests/PPDS.Cli.Tests/Services/Query/SqlQueryServiceTests.cs`, add a test that calls `ExecuteStreamingAsync` and verifies the final chunk (where `IsComplete = true`) has `ExecutionMode` set.

- [ ] **Step 3: AC-30 / AC-31 — DI resolution tests**

These verify that `ITdsQueryExecutor` resolves to non-null in daemon and CLI paths. These are integration-level tests that require significant DI setup. If the test infrastructure supports it, add them. Otherwise, mark as verified by the visual/manual verification steps (Tasks 13-14 implicitly verify DI resolution — if TDS works in the UI, it resolved correctly).

- [ ] **Step 4: AC-45 — Per-environment isolation**

This verifies that switching environments creates a fresh TDS executor. If testable with existing infrastructure, add. Otherwise, verified by Task 13 Step 2 (environment switch behavior).

- [ ] **Step 5: Run all tests**

Run: `dotnet test PPDS.sln --filter "Category!=Integration" -v q`
Expected: All pass

- [ ] **Step 6: Commit**

```
test(query): add remaining TDS acceptance criteria tests
```

### Task 16: Run /qa

- [ ] **Step 1: Dispatch blind QA verification on the extension**
