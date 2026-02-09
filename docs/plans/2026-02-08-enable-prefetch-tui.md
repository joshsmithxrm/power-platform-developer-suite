# Enable Prefetch for TUI Query Execution

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.
> **Context:** PrefetchScanNode exists and is tested but disabled by default. The TUI would benefit from page-ahead buffering for smooth scrolling on large result sets.
> **Branch:** `fix/tui-colors` in worktree `C:\VS\ppdsw\ppds\.worktrees\tui-polish`

**Goal:** Enable prefetch for TUI query execution so users get zero-wait scrolling on large result sets.

**Architecture:** Add `EnablePrefetch` to `SqlQueryRequest`, thread it through `SqlQueryService` to `QueryPlanOptions`, and set it in `SqlQueryScreen`. The planner already wraps `FetchXmlScanNode` with `PrefetchScanNode` when enabled — we just need to flip the switch.

**Test command:** `dotnet test --filter "Category=PlanUnit|Category=TuiUnit" --no-build`

---

## Task 1: Add EnablePrefetch to SqlQueryRequest

**Why:** `SqlQueryRequest` is how callers (TUI, CLI commands) communicate query options to `SqlQueryService`. Currently there's no way to request prefetch.

**Files:**
- Modify: `src/PPDS.Cli/Services/Query/SqlQueryRequest.cs`

**Step 1: Read the file**

Read `src/PPDS.Cli/Services/Query/SqlQueryRequest.cs` to see existing properties.

**Step 2: Add the property**

Add to `SqlQueryRequest`:
```csharp
/// <summary>Whether to enable page-ahead buffering for large result sets.</summary>
public bool EnablePrefetch { get; init; }
```

Default is `false` (backward compatible — existing callers unchanged).

**Step 3: Build**

Run: `dotnet build src/PPDS.Cli/PPDS.Cli.csproj --no-restore`

**Step 4: Commit**

```
feat(query): add EnablePrefetch to SqlQueryRequest
```

---

## Task 2: Thread EnablePrefetch through SqlQueryService

**Why:** `SqlQueryService` builds `QueryPlanOptions` internally from `SqlQueryRequest` properties. It needs to pass `EnablePrefetch` through so the planner can wrap scan nodes with `PrefetchScanNode`.

**Files:**
- Modify: `src/PPDS.Cli/Services/Query/SqlQueryService.cs`

**Step 1: Read the file**

Find where `QueryPlanOptions` is constructed in both `ExecuteAsync` (around line 126) and `ExecuteStreamingAsync` (around line 233). Both build a `new QueryPlanOptions { ... }`.

**Step 2: Add EnablePrefetch to both QueryPlanOptions constructions**

In `ExecuteAsync` (around line 126), add to the QueryPlanOptions initializer:
```csharp
EnablePrefetch = request.EnablePrefetch,
```

In `ExecuteStreamingAsync` (around line 233), add the same.

**Step 3: Build and test**

Run: `dotnet build src/PPDS.Cli/PPDS.Cli.csproj --no-restore`
Run: `dotnet test tests/PPDS.Cli.Tests --filter "Category=PlanUnit" --no-restore`

**Step 4: Commit**

```
feat(query): thread EnablePrefetch from request to QueryPlanOptions
```

---

## Task 3: Enable prefetch in SqlQueryScreen

**Why:** The TUI is the primary beneficiary of prefetch — users scroll through large result sets interactively.

**Files:**
- Modify: `src/PPDS.Cli/Tui/Screens/SqlQueryScreen.cs`

**Step 1: Read the file**

Find where `SqlQueryRequest` is constructed (search for `new SqlQueryRequest`). There will likely be at least two places: one for the initial streaming execution and one for "load more" pagination.

**Step 2: Set EnablePrefetch = true**

In the request construction for the **streaming** execution path, add:
```csharp
EnablePrefetch = true,
```

For the **pagination** path (`ExecuteAsync` for "load more"), also set `EnablePrefetch = true` since it also benefits from page-ahead buffering on multi-page results.

**Step 3: Build**

Run: `dotnet build src/PPDS.Cli/PPDS.Cli.csproj --no-restore`

**Step 4: Commit**

```
feat(query): enable prefetch in TUI SqlQueryScreen

Large result sets now use page-ahead buffering for zero-wait
scrolling. PrefetchScanNode reads the next page in the background
while the TUI displays current results.
```

---

## Task 4: Add a test verifying TUI request enables prefetch

**Why:** Guard against regression — if someone changes the TUI's request construction, this test catches the missing prefetch flag.

**Files:**
- Modify or create test in: `tests/PPDS.Cli.Tests/Services/Query/SqlQueryServiceTests.cs`

**Step 1: Write the test**

Add a test that verifies when `EnablePrefetch = true` is set on a request, the resulting plan includes a `PrefetchScanNode`. This tests the full wiring from request → options → planner.

```csharp
[Fact]
[Trait("Category", "PlanUnit")]
public async Task ExecuteAsync_EnablePrefetch_PlanIncludesPrefetchNode()
{
    // Arrange: request with EnablePrefetch = true
    var request = new SqlQueryRequest
    {
        Sql = "SELECT accountid, name FROM account",
        EnablePrefetch = true
    };

    // Act: call ExplainAsync (which runs the planner but not the executor)
    var plan = await service.ExplainAsync(request.Sql, CancellationToken.None);

    // Assert: plan description should contain "Prefetch"
    var formatted = PlanFormatter.Format(plan);
    Assert.Contains("Prefetch", formatted);
}
```

Adjust based on the actual test infrastructure — you may need to use `SqlQueryService` with mock executors, or test at the `QueryPlanner` level directly.

**Step 2: Run tests**

Run: `dotnet test --filter "Category=PlanUnit" --no-restore`

**Step 3: Commit**

```
test(query): verify EnablePrefetch produces PrefetchScanNode in plan
```

---

## Task Dependency Graph

```
[Task 1: Add property]     → [Task 2: Thread through service] → [Task 3: Enable in TUI]
                                                                → [Task 4: Add test]
```

Linear dependency — each task builds on the previous.

**Execution order:** 1 → 2 → 3 + 4 (3 and 4 are independent of each other)
