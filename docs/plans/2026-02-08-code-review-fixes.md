# Code Review Fixes Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Fix all critical and important issues found during the comprehensive code review of the `fix/tui-colors` branch before creating a PR.

**Architecture:** Fixes are organized into independent groups that can be dispatched to parallel agents. Each group touches a distinct area of the codebase with no file overlaps, enabling maximum parallelism.

**Tech Stack:** C# (.NET 8/9/10), Terminal.Gui 1.19+, xUnit

---

## Parallel Group A: Query Engine Safety (Tasks 1-2)

### Task 1: DML Safety Guard — Recursive Script Detection

The safety guard's `Check` method falls through to `IsBlocked = false` for `SqlIfStatement` and `SqlBlockStatement`, allowing DML inside scripts to bypass safety checks.

**Files:**
- Modify: `src/PPDS.Cli/Services/Query/DmlSafetyGuard.cs:21-36`
- Modify: `tests/PPDS.Cli.Tests/Services/Query/DmlSafetyGuardTests.cs`

**Step 1: Write failing tests**

Add to `DmlSafetyGuardTests.cs`:

```csharp
// ── Script DML detection ─────────────────────────────────────────

[Fact]
public void Check_IfStatement_ContainingDelete_RequiresConfirmation()
{
    var delete = DeleteWithWhere();
    var block = new SqlBlockStatement(new ISqlStatement[] { delete }, 0);
    var ifStmt = new SqlIfStatement(
        new SqlComparisonCondition(SqlColumnRef.Simple("x"), SqlComparisonOperator.Equal, new SqlLiteral("1", SqlLiteralType.Number)),
        block, elseBlock: null, sourcePosition: 0);

    var result = _guard.Check(ifStmt, new DmlSafetyOptions());

    Assert.True(result.RequiresConfirmation, "DML inside IF should require confirmation");
}

[Fact]
public void Check_IfStatement_ContainingDeleteWithoutWhere_IsBlocked()
{
    var delete = DeleteWithoutWhere();
    var block = new SqlBlockStatement(new ISqlStatement[] { delete }, 0);
    var ifStmt = new SqlIfStatement(
        new SqlComparisonCondition(SqlColumnRef.Simple("x"), SqlComparisonOperator.Equal, new SqlLiteral("1", SqlLiteralType.Number)),
        block, elseBlock: null, sourcePosition: 0);

    var result = _guard.Check(ifStmt, new DmlSafetyOptions());

    Assert.True(result.IsBlocked, "DELETE without WHERE inside IF should be blocked");
}

[Fact]
public void Check_BlockStatement_ContainingUpdate_RequiresConfirmation()
{
    var update = UpdateWithWhere();
    var block = new SqlBlockStatement(new ISqlStatement[] { update }, 0);

    var result = _guard.Check(block, new DmlSafetyOptions());

    Assert.True(result.RequiresConfirmation, "DML inside block should require confirmation");
}

[Fact]
public void Check_NestedIfElse_ContainingDml_IsDetected()
{
    var delete = DeleteWithoutWhere();
    var innerBlock = new SqlBlockStatement(new ISqlStatement[] { delete }, 0);
    var elseBlock = new SqlBlockStatement(new ISqlStatement[] { new SqlSelectStatement(
        new[] { new SqlColumnRef("*", null) },
        new SqlTableRef("account"),
        where: null, orderBy: null, top: null, distinct: false, groupBy: null, having: null, sourcePosition: 0) }, 0);
    var ifStmt = new SqlIfStatement(
        new SqlComparisonCondition(SqlColumnRef.Simple("x"), SqlComparisonOperator.Equal, new SqlLiteral("1", SqlLiteralType.Number)),
        innerBlock, elseBlock, sourcePosition: 0);

    var result = _guard.Check(ifStmt, new DmlSafetyOptions());

    Assert.True(result.IsBlocked, "DELETE without WHERE in nested IF should be blocked");
}

[Fact]
public void Check_BlockStatement_SelectOnly_NotBlocked()
{
    var select = new SqlSelectStatement(
        new[] { new SqlColumnRef("*", null) },
        new SqlTableRef("account"),
        where: null, orderBy: null, top: null, distinct: false, groupBy: null, having: null, sourcePosition: 0);
    var block = new SqlBlockStatement(new ISqlStatement[] { select }, 0);

    var result = _guard.Check(block, new DmlSafetyOptions());

    Assert.False(result.IsBlocked);
    Assert.False(result.RequiresConfirmation);
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PPDS.Cli.Tests --filter "FullyQualifiedName~DmlSafetyGuardTests.Check_IfStatement" --no-build -v q`
Expected: FAIL

**Step 3: Implement recursive DML detection**

Replace the `Check` method in `DmlSafetyGuard.cs` (lines 21-36):

```csharp
public DmlSafetyResult Check(ISqlStatement statement, DmlSafetyOptions options)
{
    return statement switch
    {
        SqlDeleteStatement delete => CheckDelete(delete, options),
        SqlUpdateStatement update => CheckUpdate(update, options),
        SqlInsertStatement => new DmlSafetyResult
        {
            IsBlocked = false,
            RequiresConfirmation = !options.IsConfirmed,
            EstimatedAffectedRows = -1
        },
        SqlSelectStatement => new DmlSafetyResult { IsBlocked = false },
        SqlBlockStatement block => CheckBlock(block, options),
        SqlIfStatement ifStmt => CheckIf(ifStmt, options),
        _ => new DmlSafetyResult { IsBlocked = false }
    };
}

private DmlSafetyResult CheckBlock(SqlBlockStatement block, DmlSafetyOptions options)
{
    // Return the most restrictive result from any contained statement
    DmlSafetyResult worst = new() { IsBlocked = false };
    foreach (var stmt in block.Statements)
    {
        var result = Check(stmt, options);
        if (result.IsBlocked) return result; // Blocked is the most restrictive
        if (result.RequiresConfirmation) worst = result;
    }
    return worst;
}

private DmlSafetyResult CheckIf(SqlIfStatement ifStmt, DmlSafetyOptions options)
{
    var thenResult = Check(ifStmt.ThenBlock, options);
    if (thenResult.IsBlocked) return thenResult;

    if (ifStmt.ElseBlock != null)
    {
        var elseResult = Check(ifStmt.ElseBlock, options);
        if (elseResult.IsBlocked) return elseResult;
        if (elseResult.RequiresConfirmation) return elseResult;
    }

    return thenResult;
}
```

**Step 4: Run tests to verify they pass**

Run: `dotnet test tests/PPDS.Cli.Tests --filter "FullyQualifiedName~DmlSafetyGuardTests" --no-build -v q`
Expected: ALL PASS

**Step 5: Commit**

```
feat(query): add recursive DML detection for script blocks
```

---

### Task 2: ParallelPartitionNode — Flatten AggregateException

Add `AggregateException.Flatten()` before checking for aggregate limit messages.

**Files:**
- Modify: `src/PPDS.Dataverse/Query/Planning/Nodes/ParallelPartitionNode.cs:121-142`

**Step 1: Update WrapIfAggregateLimitExceeded**

In `ParallelPartitionNode.cs`, modify `WrapIfAggregateLimitExceeded` (line 121):

```csharp
private static Exception WrapIfAggregateLimitExceeded(Exception ex)
{
    // Flatten AggregateException so we check all inner exceptions, not just the chain
    var toCheck = ex is AggregateException agg ? agg.Flatten() : ex;

    var current = toCheck;
    while (current != null)
    {
        if (current.Message.Contains("AggregateQueryRecordLimit", StringComparison.OrdinalIgnoreCase)
            || current.Message.Contains("aggregate operation exceeded", StringComparison.OrdinalIgnoreCase))
        {
            return new QueryExecutionException(
                QueryErrorCode.AggregateLimitExceeded,
                "Aggregate query exceeded the Dataverse 50,000 record limit. " +
                "Consider partitioning the query by date range or adding more restrictive filters.",
                ex);
        }
        current = current.InnerException;
    }

    // For flattened AggregateException, also check all inner exceptions
    if (toCheck is AggregateException flattened)
    {
        foreach (var inner in flattened.InnerExceptions)
        {
            var innerCurrent = inner;
            while (innerCurrent != null)
            {
                if (innerCurrent.Message.Contains("AggregateQueryRecordLimit", StringComparison.OrdinalIgnoreCase)
                    || innerCurrent.Message.Contains("aggregate operation exceeded", StringComparison.OrdinalIgnoreCase))
                {
                    return new QueryExecutionException(
                        QueryErrorCode.AggregateLimitExceeded,
                        "Aggregate query exceeded the Dataverse 50,000 record limit. " +
                        "Consider partitioning the query by date range or adding more restrictive filters.",
                        ex);
                }
                innerCurrent = innerCurrent.InnerException;
            }
        }
    }

    return ex;
}
```

**Step 2: Run existing tests**

Run: `dotnet test tests/PPDS.Dataverse.Tests --filter "FullyQualifiedName~ParallelPartitionNodeTests" --no-build -v q`
Expected: ALL PASS

**Step 3: Commit**

```
fix(query): flatten AggregateException in ParallelPartitionNode error handling
```

---

## Parallel Group B: IntelliSense Fixes (Tasks 3-4)

### Task 3: SqlValidator — Fix Fire-and-Forget in ValidateConditionColumns

**Files:**
- Modify: `src/PPDS.Dataverse/Sql/Intellisense/SqlValidator.cs:396-426`
- Modify: `tests/PPDS.Cli.Tests/Tui/SqlValidatorTests.cs`

**Step 1: Write a test that validates WHERE column diagnostics are returned**

Add to `SqlValidatorTests.cs`:

```csharp
[Fact]
public async Task ValidateAsync_WhereWithUnknownColumn_ReturnsDiagnostic()
{
    var validator = new SqlValidator(new StubMetadataProvider());
    var sql = "SELECT name FROM account WHERE unknowncol = 'x'";

    var diags = await validator.ValidateAsync(sql);

    Assert.Contains(diags, d => d.Message.Contains("unknowncol", StringComparison.OrdinalIgnoreCase));
}

[Fact]
public async Task ValidateAsync_MultipleWhereConditions_AllValidated()
{
    var validator = new SqlValidator(new StubMetadataProvider());
    var sql = "SELECT name FROM account WHERE badcol1 = 'x' AND badcol2 = 'y'";

    var diags = await validator.ValidateAsync(sql);

    Assert.True(diags.Count >= 2, $"Expected at least 2 diagnostics for unknown columns, got {diags.Count}");
}
```

**Step 2: Run tests (may already pass or may be flaky due to race)**

Run: `dotnet test tests/PPDS.Cli.Tests --filter "FullyQualifiedName~SqlValidatorTests.ValidateAsync_WhereWithUnknownColumn" -v q`

**Step 3: Fix ValidateConditionColumns to be async and await all calls**

Replace `ValidateConditionColumns` method (lines 396-426):

```csharp
private async Task ValidateConditionColumnsAsync(
    ISqlCondition condition, Dictionary<string, string> tableMap,
    string defaultEntity, string sql, List<SqlDiagnostic> diagnostics,
    CancellationToken ct)
{
    switch (condition)
    {
        case SqlComparisonCondition comp:
            await ValidateColumnRefAsync(comp.Column, tableMap, defaultEntity, sql, diagnostics, ct);
            break;

        case SqlLikeCondition like:
            await ValidateColumnRefAsync(like.Column, tableMap, defaultEntity, sql, diagnostics, ct);
            break;

        case SqlNullCondition nullCond:
            await ValidateColumnRefAsync(nullCond.Column, tableMap, defaultEntity, sql, diagnostics, ct);
            break;

        case SqlInCondition inCond:
            await ValidateColumnRefAsync(inCond.Column, tableMap, defaultEntity, sql, diagnostics, ct);
            break;

        case SqlLogicalCondition logical:
            foreach (var child in logical.Conditions)
            {
                await ValidateConditionColumnsAsync(child, tableMap, defaultEntity, sql, diagnostics, ct);
            }
            break;
    }
}
```

Also update the call site in `ValidateSelectAsync` — find where `ValidateConditionColumns` is called and change to `await ValidateConditionColumnsAsync(...)`.

**Step 4: Seal SqlValidator**

Add `sealed` keyword to the class declaration (line 26):

```csharp
public sealed class SqlValidator
```

**Step 5: Run all validator tests**

Run: `dotnet test tests/PPDS.Cli.Tests --filter "FullyQualifiedName~SqlValidatorTests" --no-build -v q`
Expected: ALL PASS

**Step 6: Commit**

```
fix(intellisense): await condition column validation and seal SqlValidator
```

---

### Task 4: SqlValidator — Improve FindIdentifierPosition

**Files:**
- Modify: `src/PPDS.Dataverse/Sql/Intellisense/SqlValidator.cs:432-437`

**Step 1: Improve FindIdentifierPosition to accept a search-start hint**

Replace `FindIdentifierPosition` and add an overload:

```csharp
private static int FindIdentifierPosition(string sql, string identifier, int searchFrom = 0)
{
    var idx = sql.IndexOf(identifier, searchFrom, StringComparison.OrdinalIgnoreCase);
    return idx >= 0 ? idx : 0;
}
```

Then update callers to pass a `searchFrom` hint where available. For entity names in FROM/JOIN, search from the position of the FROM/JOIN keyword. For column names, search from the position of the relevant clause keyword. Where the AST has a `SourcePosition`, use that directly.

**Step 2: Run all validator tests**

Run: `dotnet test tests/PPDS.Cli.Tests --filter "FullyQualifiedName~SqlValidatorTests" --no-build -v q`
Expected: ALL PASS

**Step 3: Commit**

```
fix(intellisense): improve error position detection with search-start hints
```

---

## Parallel Group C: TUI View Fixes (Tasks 5-7)

### Task 5: SplitterView — Reset Drag Baseline

**Files:**
- Modify: `src/PPDS.Cli/Tui/Views/SplitterView.cs:71-80`

**Step 1: Add baseline reset after drag event**

In `SplitterView.cs`, line 78, after `Dragged?.Invoke(delta);`, add:

```csharp
_dragStartScreenY = ev.Y;
```

The full block (lines 71-81) should become:

```csharp
if (_isDragging && ev.Flags.HasFlag(MouseFlags.ReportMousePosition))
{
    var delta = ev.Y - _dragStartScreenY;
    if (delta != 0)
    {
        Dragged?.Invoke(delta);
        _dragStartScreenY = ev.Y;
    }
    return true;
}
```

**Step 2: Run build to verify**

Run: `dotnet build src/PPDS.Cli --no-restore -v q`
Expected: Build succeeded

**Step 3: Commit**

```
fix(tui): reset drag baseline in SplitterView to prevent erratic resizing
```

---

### Task 6: SyntaxHighlightedTextView — Dispose CTS and Timer

**Files:**
- Modify: `src/PPDS.Cli/Tui/Views/SyntaxHighlightedTextView.cs`

**Step 1: Add Dispose override**

Find the class declaration and add a `Dispose` override. Add after the field declarations:

```csharp
/// <inheritdoc />
protected override void Dispose(bool disposing)
{
    if (disposing)
    {
        _completionCts?.Cancel();
        _completionCts?.Dispose();
        _completionCts = null;

        _validationCts?.Cancel();
        _validationCts?.Dispose();
        _validationCts = null;

        if (_validationTimerToken != null)
        {
            Application.MainLoop?.RemoveTimeout(_validationTimerToken);
            _validationTimerToken = null;
        }
    }
    base.Dispose(disposing);
}
```

**Step 2: Run build**

Run: `dotnet build src/PPDS.Cli --no-restore -v q`
Expected: Build succeeded

**Step 3: Commit**

```
fix(tui): dispose CTS and validation timer in SyntaxHighlightedTextView
```

---

### Task 7: DataTableView — Fix Missing `]` Escape in Filter

**Files:**
- Modify: `src/PPDS.Cli/Tui/Views/DataTableView.cs:513-521`

**Step 1: Add missing `]` escape**

Replace `EscapeFilterValue` in `DataTableView.cs` (lines 513-521):

```csharp
private static string EscapeFilterValue(string value)
{
    // Escape special characters for DataView.RowFilter
    return value
        .Replace("'", "''")
        .Replace("[", "[[]")
        .Replace("]", "[]]")
        .Replace("%", "[%]")
        .Replace("*", "[*]");
}
```

**Step 2: Run build**

Run: `dotnet build src/PPDS.Cli --no-restore -v q`
Expected: Build succeeded

**Step 3: Commit**

```
fix(tui): add missing ] escape in DataTableView filter
```

---

## Parallel Group D: TUI Infrastructure Fixes (Tasks 8-10)

### Task 8: TuiColorPalette — Cache Static ColorScheme Properties

**Files:**
- Modify: `src/PPDS.Cli/Tui/Infrastructure/TuiColorPalette.cs`

**Step 1: Convert high-frequency properties from expression-bodied to cached fields**

Replace the expression-bodied properties with lazy-cached fields. For each property like:

```csharp
public static ColorScheme Default => new()
{
    Normal = MakeAttr(Color.White, Color.Black),
    Focus = MakeAttr(Color.White, Color.Black),
    HotNormal = MakeAttr(Color.White, Color.Black),
    HotFocus = MakeAttr(Color.White, Color.Black),
    Disabled = MakeAttr(Color.DarkGray, Color.Black),
};
```

Change to:

```csharp
private static ColorScheme? _default;
public static ColorScheme Default => _default ??= new()
{
    Normal = MakeAttr(Color.White, Color.Black),
    Focus = MakeAttr(Color.White, Color.Black),
    HotNormal = MakeAttr(Color.White, Color.Black),
    HotFocus = MakeAttr(Color.White, Color.Black),
    Disabled = MakeAttr(Color.DarkGray, Color.Black),
};
```

Apply this pattern to ALL `=> new()` ColorScheme properties in the file: `Default`, `Focused`, `TextInput`, `ReadOnlyText`, `FileDialog`, `StatusBar_Production`, `StatusBar_Sandbox`, `StatusBar_Development`, `StatusBar_Trial`, `StatusBar_Default`, `MenuBar`, `TabActive`, `TabInactive`, `TableHeader`, `Selected`, `Error`, `Success`.

**Step 2: Run existing TuiColorPalette tests**

Run: `dotnet test tests/PPDS.Cli.Tests --filter "FullyQualifiedName~TuiColorPaletteTests" --no-build -v q`
Expected: ALL PASS

**Step 3: Commit**

```
perf(tui): cache TuiColorPalette static ColorScheme properties
```

---

### Task 9: HotkeyRegistry — Allow Alt+Letter Through for Menu Accelerators

**Files:**
- Modify: `src/PPDS.Cli/Tui/Infrastructure/HotkeyRegistry.cs:148-169`

**Step 1: Fix the letter-blocking to only block plain letters, not Alt+letter**

Replace the menu-open letter blocking logic (lines 148-169):

```csharp
if (_isMenuOpen?.Invoke() == true)
{
    var key = keyEvent.Key;
    var baseKey = key & ~Key.AltMask & ~Key.CtrlMask & ~Key.ShiftMask;
    var keyValue = (int)baseKey;

    bool isLetter = (keyValue >= 'a' && keyValue <= 'z') ||
                    (keyValue >= 'A' && keyValue <= 'Z');

    bool hasAlt = (key & Key.AltMask) != 0;
    bool hasCtrl = (key & Key.CtrlMask) != 0;

    // Block plain letters only (not Alt+letter which are menu accelerators,
    // and not Ctrl+letter which are shortcuts)
    if (isLetter && !hasCtrl && !hasAlt)
    {
        TuiDebugLog.Log($"Blocking plain letter key '{(char)keyValue}' while menu is open");
        return true; // Consume the key - prevents first-letter navigation
    }
}
```

**Step 2: Run build**

Run: `dotnet build src/PPDS.Cli --no-restore -v q`
Expected: Build succeeded

**Step 3: Commit**

```
fix(tui): allow Alt+letter menu accelerators while menu is open
```

---

### Task 10: QueryResultsTableView — Broaden Exception Handling in LoadMoreAsync

**Files:**
- Modify: `src/PPDS.Cli/Tui/Views/QueryResultsTableView.cs:420-446`

**Step 1: Add general exception catch**

Replace `LoadMoreAsync` error handling (lines 420-446):

```csharp
private async Task LoadMoreAsync()
{
    if (_isLoadingMore || LoadMoreRequested == null)
        return;

    _isLoadingMore = true;
    _statusLabel.Text = $"Loading page {CurrentPageNumber + 1}...";
    Application.Refresh();

    try
    {
        await LoadMoreRequested.Invoke();
    }
    catch (OperationCanceledException)
    {
        // Normal cancellation, no message needed
    }
    catch (InvalidOperationException ex)
    {
        ShowTemporaryStatus($"Error loading: {ex.Message}");
    }
    catch (HttpRequestException ex)
    {
        ShowTemporaryStatus($"Network error: {ex.Message}");
    }
    catch (Exception ex)
    {
        ShowTemporaryStatus($"Error: {ex.Message}");
        TuiDebugLog.Log($"LoadMoreAsync error: {ex}");
    }
    finally
    {
        _isLoadingMore = false;
    }
}
```

**Step 2: Run build**

Run: `dotnet build src/PPDS.Cli --no-restore -v q`
Expected: Build succeeded

**Step 3: Commit**

```
fix(tui): broaden exception handling in QueryResultsTableView.LoadMoreAsync
```

---

## Parallel Group E: Auth & Config Fixes (Tasks 11-13)

### Task 11: EnvironmentConfigStore — Add Disposed Guard and Fix Null-Clear

**Files:**
- Modify: `src/PPDS.Auth/Profiles/EnvironmentConfigStore.cs`
- Modify: `tests/PPDS.Cli.Tests/Services/Environment/EnvironmentConfigStoreTests.cs`

**Step 1: Write test for null-clear behavior**

Add to `EnvironmentConfigStoreTests.cs`:

```csharp
[Fact]
public async Task SaveConfigAsync_EmptyString_ClearsField()
{
    // Set a label first
    await _store.SaveConfigAsync("https://org.crm.dynamics.com", label: "MyLabel");

    // Clear it with empty string
    var result = await _store.SaveConfigAsync("https://org.crm.dynamics.com", label: "");

    Assert.Null(result.Label); // Empty string should clear to null
}
```

**Step 2: Add ThrowIfDisposed and fix merge logic**

In `EnvironmentConfigStore.cs`, add helper method:

```csharp
private void ThrowIfDisposed()
{
    ObjectDisposedException.ThrowIf(_disposed, this);
}
```

Add `ThrowIfDisposed()` as the first line of: `LoadAsync`, `SaveAsync`, `GetConfigAsync`, `SaveConfigAsync`, `RemoveConfigAsync`, `ClearCache`.

Fix the merge logic in `SaveConfigAsync` (lines 110-114) to handle empty-string-means-clear:

```csharp
if (existing != null)
{
    if (label != null) existing.Label = label == "" ? null : label;
    if (type != null) existing.Type = type == "" ? null : type;
    if (color != null) existing.Color = color;
}
```

**Step 3: Run tests**

Run: `dotnet test tests/PPDS.Cli.Tests --filter "FullyQualifiedName~EnvironmentConfigStoreTests" --no-build -v q`
Expected: ALL PASS

**Step 4: Commit**

```
fix(auth): add disposed guard and empty-string-clears-field in EnvironmentConfigStore
```

---

### Task 12: Fix Bare Catch Blocks in Dialogs

**Files:**
- Modify: `src/PPDS.Cli/Tui/Dialogs/EnvironmentConfigDialog.cs:170`
- Modify: `src/PPDS.Cli/Tui/Dialogs/EnvironmentSelectorDialog.cs:499,529`

**Step 1: Replace bare catches with typed catches**

In `EnvironmentConfigDialog.cs` line 170, replace:
```csharp
catch
{
    // Non-critical: if load fails, dialog starts with empty fields
}
```
with:
```csharp
catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
{
    // Non-critical: if load fails, dialog starts with empty fields
    TuiDebugLog.Log($"EnvironmentConfigDialog load failed: {ex.Message}");
}
```

In `EnvironmentSelectorDialog.cs` line 499, replace:
```csharp
catch
{
    _previewType.Text = $"Type: {env.Type ?? "(unknown)"}";
    _previewColor.Text = $"Region: {env.Region ?? "(unknown)"}";
    _previewStatus.Text = string.Empty;
}
```
with:
```csharp
catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
{
    _previewType.Text = $"Type: {env.Type ?? "(unknown)"}";
    _previewColor.Text = $"Region: {env.Region ?? "(unknown)"}";
    _previewStatus.Text = string.Empty;
    TuiDebugLog.Log($"EnvironmentSelector preview failed: {ex.Message}");
}
```

In `EnvironmentSelectorDialog.cs` line 529, replace:
```csharp
catch
{
    return env.Type;
}
```
with:
```csharp
catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
{
    TuiDebugLog.Log($"ResolveDisplayType failed: {ex.Message}");
    return env.Type;
}
```

**Step 2: Run build**

Run: `dotnet build src/PPDS.Cli --no-restore -v q`
Expected: Build succeeded

**Step 3: Commit**

```
fix(tui): replace bare catches with typed exception filters in dialogs
```

---

### Task 13: EnvCommandGroup — Use Interface Instead of Concrete Type

**Files:**
- Modify: `src/PPDS.Cli/Commands/Env/EnvCommandGroup.cs`

**Step 1: Change helper method parameter types**

Replace `EnvironmentConfigService` with `IEnvironmentConfigService` in all four helper methods:

- Line 604: `ExecuteConfigSetAsync(IEnvironmentConfigService service, ...)`
- Line 618: `ExecuteConfigShowAsync(IEnvironmentConfigService service, ...)`
- Line 633: `ExecuteConfigRemoveAsync(IEnvironmentConfigService service, ...)`
- Line 650: `ExecuteConfigListAsync(IEnvironmentConfigService service, ...)`

Ensure the `using` directive for `IEnvironmentConfigService` is present (from `PPDS.Cli.Services.Environment`).

**Step 2: Run build**

Run: `dotnet build src/PPDS.Cli --no-restore -v q`
Expected: Build succeeded

**Step 3: Commit**

```
refactor(cli): use IEnvironmentConfigService interface in EnvCommandGroup
```

---

## Sequential Group F: Cross-Cutting Fix (Task 14)

### Task 14: Add Pre-Load of EnvironmentConfigStore in InteractiveSession

**Files:**
- Modify: `src/PPDS.Cli/Tui/InteractiveSession.cs`

**Step 1: Pre-load the config store during InitializeAsync**

In `InteractiveSession.InitializeAsync()`, add a pre-load call near the top of the method (after line 140, before the profile load):

```csharp
// Pre-load environment config so sync-over-async calls in UI thread are cache hits
await _envConfigStore.LoadAsync(cancellationToken).ConfigureAwait(false);
```

**Step 2: Run build**

Run: `dotnet build src/PPDS.Cli --no-restore -v q`
Expected: Build succeeded

**Step 3: Commit**

```
fix(tui): pre-load EnvironmentConfigStore to prevent UI thread blocking
```

---

## Final: Run Full Test Suite (Task 15)

### Task 15: Full Validation

**Step 1: Build entire solution**

Run: `dotnet build --nologo -v q`
Expected: Build succeeded

**Step 2: Run all non-integration tests**

Run: `dotnet test --filter "Category!=Integration" --nologo -v q`
Expected: ALL PASS, 0 failures

**Step 3: Final commit if any test adjustments needed**

---

## Parallelism Map

```
Time →

Group A (Tasks 1,2):     ████████████████████
Group B (Tasks 3,4):     ████████████████████
Group C (Tasks 5,6,7):   ████████████████████
Group D (Tasks 8,9,10):  ████████████████████
Group E (Tasks 11,12,13):████████████████████
                                              Group F (Task 14): ████████
                                                                  Task 15: ████████████
```

Groups A-E run in parallel (no file overlaps). Group F depends on Group E (same file). Task 15 runs last.

## Summary Table

| Task | Group | Issue | Severity |
|------|-------|-------|----------|
| 1 | A | DML safety guard bypass for scripts | Critical |
| 2 | A | ParallelPartitionNode AggregateException handling | Critical |
| 3 | B | SqlValidator fire-and-forget in ValidateConditionColumns | Critical |
| 4 | B | SqlValidator FindIdentifierPosition wrong occurrence | Important |
| 5 | C | SplitterView drag baseline not reset | Critical |
| 6 | C | SyntaxHighlightedTextView CTS/timer leak on dispose | Critical |
| 7 | C | DataTableView missing `]` escape in filter | Important |
| 8 | D | TuiColorPalette allocates new ColorScheme per access | Important |
| 9 | D | HotkeyRegistry blocks Alt+letter menu accelerators | Important |
| 10 | D | QueryResultsTableView LoadMoreAsync narrow exception handling | Important |
| 11 | E | EnvironmentConfigStore disposed guard + null-clear fix | Critical |
| 12 | E | Bare catch blocks in dialogs | Important |
| 13 | E | EnvCommandGroup uses concrete type instead of interface | Important |
| 14 | F | Pre-load EnvironmentConfigStore in InteractiveSession | Important |
| 15 | — | Full validation build + test suite | — |
