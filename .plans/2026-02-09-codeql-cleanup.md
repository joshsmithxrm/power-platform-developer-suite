# CodeQL Alert Cleanup Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Resolve all 290 CodeQL alerts flagged on PR #524 to pass the CodeQL gate.

**Architecture:** Fix alerts in batches grouped by rule type. Each batch is a single commit targeting one rule across all affected files. The fixes are mechanical — no behavioral changes.

**Tech Stack:** C# (.NET 8/9/10), xUnit, Terminal.Gui

---

## Summary of all 290 alerts by rule

| Rule | Count | Severity | Fix pattern |
|------|-------|----------|-------------|
| cs/dereferenced-value-may-be-null | 185 | warning | Add `!` (null-forgiving) after `as` cast that is followed by `.Should().NotBeNull()` |
| cs/dispose-not-called-on-throw | 20 | warning | Wrap disposable creation + test body in `using` |
| cs/useless-upcast | 12 | warning | Remove `(object?)` cast on `null` — use `null` directly |
| cs/useless-cast-to-self | 12 | warning | Remove `(SqlSelectStatement)` cast — `SqlParser.Parse` already returns that type |
| cs/useless-assignment-to-local | 12 | warning | Remove unused variable or use discard `_` |
| cs/inefficient-containskey | 10 | note | Replace `ContainsKey` + indexer with `TryGetValue` |
| cs/local-not-disposed | 7 | warning | Wrap `CancellationTokenSource`/`ContextMenu` in `using` |
| cs/reference-equality-with-object | 6 | warning | Replace `==` with `.Equals()` or use typed comparison |
| cs/missed-using-statement | 5 | note | Convert manual `try/finally` dispose to `using` |
| cs/equality-on-floats | 4 | warning | Use tolerance-based comparison or cast to `decimal` first |
| cs/constant-condition | 3 | warning | Remove dead branch or fix logic |
| cs/unused-collection | 3 | error | Remove unused list/collection or add assertion |
| cs/static-field-written-by-instance | 3 | note | Make field instance-level or use proper pattern |
| cs/empty-catch-block | 2 | note | Add comment explaining why empty |
| cs/virtual-call-in-constructor | 2 | warning | Suppress with comment — Terminal.Gui pattern requires this |
| cs/loss-of-precision | 1 | error | Cast operand to `double` before multiplication |
| cs/complex-condition | 1 | note | Extract to named boolean variables |
| cs/call-to-obsolete-method | 1 | note | Replace with non-obsolete alternative |
| cs/xmldoc/missing-summary | 1 | note | Add `<summary>` tag |

## File inventory

### Production code (src/) — 23 alerts, 14 files

**src/PPDS.Dataverse/**
- `Query/Execution/Functions/DateFunctions.cs:186` — cs/loss-of-precision
- `Query/Execution/Functions/CastConverter.cs:231,232` — cs/equality-on-floats (x2)
- `Query/Execution/ExpressionEvaluator.cs:460,461` — cs/equality-on-floats (x2)
- `Query/Execution/VariableScope.cs:35` — cs/inefficient-containskey
- `Query/TdsQueryExecutor.cs:73` — cs/useless-assignment-to-local
- `Metadata/CachedMetadataProvider.cs:86` — cs/constant-condition
- `Pooling/DataverseConnectionPool.cs:958` — cs/static-field-written-by-instance

**src/PPDS.Cli/**
- `Tui/TuiShell.cs:658,676` — cs/useless-assignment-to-local (x2)
- `Tui/Views/SyntaxHighlightedTextView.cs:450,689` — cs/useless-assignment-to-local (x2)
- `Tui/Views/DataTableView.cs:139` — cs/virtual-call-in-constructor
- `Tui/Views/DataTableView.cs:459` — cs/local-not-disposed
- `Tui/Views/QueryResultsTableView.cs:569` — cs/reference-equality-with-object
- `Tui/Infrastructure/HotkeyRegistry.cs:119,185,194,307,315` — cs/reference-equality-with-object (x5)
- `Tui/Dialogs/EnvironmentDetailsDialog.cs:209` — cs/useless-assignment-to-local
- `Tui/Dialogs/EnvironmentSelectorDialog.cs:279` — cs/complex-condition
- `Tui/Dialogs/EnvironmentConfigDialog.cs:31` — cs/xmldoc/missing-summary
- `Tui/Dialogs/TuiDialog.cs:28` — cs/virtual-call-in-constructor
- `Tui/PpdsApplication.cs:24` — cs/missed-using-statement
- `Services/Environment/EnvironmentConfigService.cs:85` — cs/constant-condition (x2)
- `Services/Profile/ProfileService.cs:247` — cs/missed-using-statement
- `Commands/Auth/AuthCommandGroup.cs:217,339` — cs/missed-using-statement (x2)

**src/PPDS.Auth/**
- `Credentials/UsernamePasswordCredentialProvider.cs:112` — cs/call-to-obsolete-method
- `Pooling/ProfileConnectionSource.cs:37` — cs/missed-using-statement

### Test code (tests/) — 267 alerts, 27 files

**tests/PPDS.Dataverse.Tests/Sql/Parsing/** — 148 cs/dereferenced-value-may-be-null
- `CaseExpressionParserTests.cs` (27 alerts)
- `ExistsParserTests.cs` (18 alerts)
- `ExpressionConditionTests.cs` (18 alerts)
- `ExpressionParserTests.cs` (44 alerts)
- `HavingParserTests.cs` (4 alerts)
- `InSubqueryParserTests.cs` (16 alerts)
- `SqlParserTests.cs` (17 alerts)
- `WindowFunctionParserTests.cs` (24 alerts)

**tests/PPDS.Dataverse.Tests/Query/** — 62 alerts
- `Planning/Rewrites/ExistsRewriteTests.cs` (6 null-deref)
- `Planning/Rewrites/InSubqueryRewriteTests.cs` (5 null-deref)
- `Planning/QueryPlannerTests.cs` (12 useless-cast-to-self)
- `Planning/Nodes/ParallelPartitionNodeTests.cs` (8 useless-upcast, 1 useless-assignment, 1 local-not-disposed, 2 static-field)
- `Planning/Nodes/AdaptiveAggregateScanNodeTests.cs` (2 useless-assignment, 1 local-not-disposed)
- `Planning/Nodes/MetadataScanNodeTests.cs` (1 unused-collection, 1 useless-assignment, 1 local-not-disposed)
- `Planning/Nodes/FetchXmlScanNodeTests.cs` (1 useless-assignment, 1 local-not-disposed)
- `Planning/Nodes/PrefetchScanNodeTests.cs` (1 useless-assignment)
- `Planning/Nodes/ClientWindowNodeTests.cs` (1 unused-collection)
- `Planning/Nodes/DistinctNodeTests.cs` (2 useless-upcast)
- `Planning/Nodes/ProjectNodeTests.cs` (1 inefficient-containskey)
- `Planning/TdsRoutingTests.cs` (1 local-not-disposed)
- `Execution/ExpressionEvaluatorTests.cs` (2 useless-upcast)
- `Execution/CaseExpressionEvalTests.cs` (1 useless-upcast)
- `Execution/CastConvertTests.cs` (1 useless-upcast)
- `QueryExecutorTests.cs` (1 unused-collection)

**tests/PPDS.Cli.Tests/** — 26 alerts
- `Tui/SqlCursorContextTests.cs` (2 inefficient-containskey)
- `Tui/CachedMetadataProviderTests.cs` (1 dispose-not-called-on-throw)
- `Tui/Infrastructure/TabManagerTests.cs` (3 dispose-not-called-on-throw)
- `Services/Query/SqlQueryServiceTests.cs` (4 inefficient-containskey)
- `Services/Environment/EnvironmentConfigServiceTests.cs` (2 inefficient-containskey)
- `Services/Environment/EnvironmentServiceTests.cs` (1 empty-catch-block)
- `Services/Profile/ProfileServiceTests.cs` (1 empty-catch-block)

**tests/PPDS.Auth.Tests/** — 6 alerts
- `Credentials/CredentialProviderFactoryTests.cs` (6 dispose-not-called-on-throw)

**tests/PPDS.Dataverse.Tests/** — other
- `Metadata/MetadataQueryExecutorTests.cs` (1 local-not-disposed)
- `BulkOperations/BatchParallelismCoordinatorTests.cs` (2 dispose-not-called-on-throw)
- `Pooling/ConnectionStringSourceTests.cs` (6 dispose-not-called-on-throw)
- `Pooling/DataverseConnectionPoolTests.cs` (2 dispose-not-called-on-throw)

---

## Tasks

### Task 1: Fix null-dereference alerts in SQL parsing tests (185 alerts)

**Files:**
- Modify: `tests/PPDS.Dataverse.Tests/Sql/Parsing/ExpressionParserTests.cs`
- Modify: `tests/PPDS.Dataverse.Tests/Sql/Parsing/CaseExpressionParserTests.cs`
- Modify: `tests/PPDS.Dataverse.Tests/Sql/Parsing/ExistsParserTests.cs`
- Modify: `tests/PPDS.Dataverse.Tests/Sql/Parsing/ExpressionConditionTests.cs`
- Modify: `tests/PPDS.Dataverse.Tests/Sql/Parsing/HavingParserTests.cs`
- Modify: `tests/PPDS.Dataverse.Tests/Sql/Parsing/InSubqueryParserTests.cs`
- Modify: `tests/PPDS.Dataverse.Tests/Sql/Parsing/SqlParserTests.cs`
- Modify: `tests/PPDS.Dataverse.Tests/Sql/Parsing/WindowFunctionParserTests.cs`
- Modify: `tests/PPDS.Dataverse.Tests/Query/Planning/Rewrites/ExistsRewriteTests.cs`
- Modify: `tests/PPDS.Dataverse.Tests/Query/Planning/Rewrites/InSubqueryRewriteTests.cs`

**Pattern:** All alerts follow the same pattern:
```csharp
// BEFORE (CodeQL warns: variable may be null)
var computed = result.Columns[0] as SqlComputedColumn;
computed.Should().NotBeNull();
computed!.Alias.Should().Be("tax");  // line N: dereference after as-cast

// CodeQL doesn't understand that .Should().NotBeNull() is an assertion.
// Fix: use Assert.NotNull which returns typed non-null value, or
// just use the null-forgiving operator since the assertion guarantees non-null.
```

**Fix approach:** The `!` null-forgiving operator is already used on these lines (e.g., `computed!.Alias`). The issue is CodeQL flags the `as` cast result as potentially null _before_ the `!`. The cleanest fix is to add a `#pragma warning disable` at file level for CodeQL's null-dereference in test files, since FluentAssertions `.Should().NotBeNull()` acts as the null guard.

Actually, the better fix: change `as` + `Should().NotBeNull()` + `!` to a direct cast + assert pattern that CodeQL understands:
```csharp
// AFTER
var computed = Assert.IsType<SqlComputedColumn>(result.Columns[0]);
computed.Alias.Should().Be("tax");
```

But that mixes xUnit Assert with FluentAssertions. The pragmatic fix for 185 instances is a file-level suppression.

**Step 1:** Add `// CodeQL[cs/dereferenced-value-may-be-null]` suppression comments OR convert `as` casts to direct casts in all 10 files.

**Step 2:** Run tests: `dotnet test tests/PPDS.Dataverse.Tests --filter "Category!=Integration" --no-restore`

**Step 3:** Commit: `fix: resolve null-dereference CodeQL alerts in SQL parsing tests`

### Task 2: Fix useless-cast-to-self in QueryPlannerTests (12 alerts)

**Files:**
- Modify: `tests/PPDS.Dataverse.Tests/Query/Planning/QueryPlannerTests.cs`

**Pattern:**
```csharp
// BEFORE — SqlParser.Parse already returns SqlSelectStatement
var stmt = (SqlSelectStatement)SqlParser.Parse("SELECT ...");

// AFTER — remove redundant cast
var stmt = SqlParser.Parse("SELECT ...");
```

**Step 1:** Remove all 12 `(SqlSelectStatement)` casts from lines 505-752
**Step 2:** Run tests: `dotnet test tests/PPDS.Dataverse.Tests --filter "FullyQualifiedName~QueryPlannerTests" --no-restore`
**Step 3:** Commit: `fix: remove redundant casts in QueryPlannerTests`

### Task 3: Fix useless-upcast in test files (12 alerts)

**Files:**
- Modify: `tests/PPDS.Dataverse.Tests/Query/Planning/Nodes/ParallelPartitionNodeTests.cs` (6)
- Modify: `tests/PPDS.Dataverse.Tests/Query/Planning/Nodes/DistinctNodeTests.cs` (2)
- Modify: `tests/PPDS.Dataverse.Tests/Query/Execution/ExpressionEvaluatorTests.cs` (2)
- Modify: `tests/PPDS.Dataverse.Tests/Query/Execution/CaseExpressionEvalTests.cs` (1)
- Modify: `tests/PPDS.Dataverse.Tests/Query/Execution/CastConvertTests.cs` (1)

**Pattern:**
```csharp
// BEFORE — upcast from null to object? is implicit
MakeRow("entity", ("col", (object?)null))

// AFTER
MakeRow("entity", ("col", null))
```

**Step 1:** Remove `(object?)` casts on null values in all 5 files
**Step 2:** Run tests
**Step 3:** Commit: `fix: remove useless null-to-object upcasts in tests`

### Task 4: Fix useless-assignment-to-local (12 alerts)

**Files:**
- Modify: `src/PPDS.Cli/Tui/TuiShell.cs:658,676`
- Modify: `src/PPDS.Cli/Tui/Views/SyntaxHighlightedTextView.cs:450,689`
- Modify: `src/PPDS.Cli/Tui/Dialogs/EnvironmentDetailsDialog.cs:209`
- Modify: `src/PPDS.Dataverse/Query/TdsQueryExecutor.cs:73`
- Modify: `tests/PPDS.Dataverse.Tests/Query/Planning/Nodes/PrefetchScanNodeTests.cs:416`
- Modify: `tests/PPDS.Dataverse.Tests/Query/Planning/Nodes/ParallelPartitionNodeTests.cs:301`
- Modify: `tests/PPDS.Dataverse.Tests/Query/Planning/Nodes/AdaptiveAggregateScanNodeTests.cs:279,313`
- Modify: `tests/PPDS.Dataverse.Tests/Query/Planning/Nodes/FetchXmlScanNodeTests.cs:152`
- Modify: `tests/PPDS.Dataverse.Tests/Query/Planning/Nodes/MetadataScanNodeTests.cs:216`

**Pattern varies:**
- Fire-and-forget: `var fireAndForget = RunValidationAsync();` → `_ = RunValidationAsync();`
- Unused local: `var subFrame = sub.Frame;` → remove line
- Unused row: `var row = await ...` → `_ = await ...` or `await ...` (if discardable)
- Unused init: `var columns = new List<>()` → remove if reassigned later

**Step 1:** Fix all 12 assignments
**Step 2:** Run tests
**Step 3:** Commit: `fix: remove useless local variable assignments`

### Task 5: Fix dispose-not-called-on-throw (20 alerts)

**Files:**
- Modify: `tests/PPDS.Auth.Tests/Credentials/CredentialProviderFactoryTests.cs` (6)
- Modify: `tests/PPDS.Dataverse.Tests/Pooling/ConnectionStringSourceTests.cs` (6)
- Modify: `tests/PPDS.Dataverse.Tests/Pooling/DataverseConnectionPoolTests.cs` (2)
- Modify: `tests/PPDS.Dataverse.Tests/BulkOperations/BatchParallelismCoordinatorTests.cs` (2)
- Modify: `tests/PPDS.Cli.Tests/Tui/Infrastructure/TabManagerTests.cs` (3)
- Modify: `tests/PPDS.Cli.Tests/Tui/CachedMetadataProviderTests.cs` (1)

**Pattern:**
```csharp
// BEFORE
var sut = new DisposableType(...);
sut.DoSomething();
sut.Dispose();

// AFTER — using ensures dispose even if assertion throws
using var sut = new DisposableType(...);
sut.DoSomething();
```

**Step 1:** Convert to `using var` in all 6 files
**Step 2:** Run tests
**Step 3:** Commit: `fix: ensure disposables are cleaned up on test assertion failure`

### Task 6: Fix local-not-disposed (7 alerts)

**Files:**
- Modify: `src/PPDS.Cli/Tui/Views/DataTableView.cs:459`
- Modify: `tests/PPDS.Dataverse.Tests/Metadata/MetadataQueryExecutorTests.cs:511`
- Modify: `tests/PPDS.Dataverse.Tests/Query/Planning/Nodes/AdaptiveAggregateScanNodeTests.cs:308`
- Modify: `tests/PPDS.Dataverse.Tests/Query/Planning/Nodes/FetchXmlScanNodeTests.cs:138`
- Modify: `tests/PPDS.Dataverse.Tests/Query/Planning/Nodes/MetadataScanNodeTests.cs:209`
- Modify: `tests/PPDS.Dataverse.Tests/Query/Planning/Nodes/ParallelPartitionNodeTests.cs:283`
- Modify: `tests/PPDS.Dataverse.Tests/Query/Planning/TdsRoutingTests.cs:447`

**Pattern:** `CancellationTokenSource` or `ContextMenu` created without `using`.

```csharp
// BEFORE
var cts = new CancellationTokenSource();
// AFTER
using var cts = new CancellationTokenSource();
```

**Step 1:** Add `using` to all 7 sites
**Step 2:** Run tests
**Step 3:** Commit: `fix: dispose CancellationTokenSource and ContextMenu`

### Task 7: Fix inefficient-containskey (10 alerts)

**Files:**
- Modify: `src/PPDS.Dataverse/Query/Execution/VariableScope.cs:35`
- Modify: `tests/PPDS.Cli.Tests/Tui/SqlCursorContextTests.cs:258,259`
- Modify: `tests/PPDS.Cli.Tests/Services/Query/SqlQueryServiceTests.cs:460,495,530,621`
- Modify: `tests/PPDS.Cli.Tests/Services/Environment/EnvironmentConfigServiceTests.cs:98,99`
- Modify: `tests/PPDS.Dataverse.Tests/Query/Planning/Nodes/ProjectNodeTests.cs:109`

**Pattern:**
```csharp
// BEFORE
if (dict.ContainsKey(key))
    return dict[key];  // double lookup

// AFTER
if (dict.TryGetValue(key, out var value))
    return value;
```

**Step 1:** Replace all 10 ContainsKey+indexer pairs with TryGetValue
**Step 2:** Run tests
**Step 3:** Commit: `fix: use TryGetValue instead of ContainsKey + indexer`

### Task 8: Fix reference-equality-with-object (6 alerts)

**Files:**
- Modify: `src/PPDS.Cli/Tui/Infrastructure/HotkeyRegistry.cs:119,185,194,307,315`
- Modify: `src/PPDS.Cli/Tui/Views/QueryResultsTableView.cs:569`

**Pattern:** Using `==` to compare objects (compares by reference, not value). In HotkeyRegistry, the `Owner` field is compared with `==`. These are actually intentional reference equality checks (comparing view instances), so the fix is to use `ReferenceEquals()` to make intent explicit.

```csharp
// BEFORE
b.Owner == _activeDialog

// AFTER
ReferenceEquals(b.Owner, _activeDialog)
```

**Step 1:** Replace `==` with `ReferenceEquals()` in all 6 sites
**Step 2:** Run tests
**Step 3:** Commit: `fix: use ReferenceEquals for intentional reference comparisons`

### Task 9: Fix missed-using-statement (5 alerts)

**Files:**
- Modify: `src/PPDS.Cli/Tui/PpdsApplication.cs:24`
- Modify: `src/PPDS.Cli/Services/Profile/ProfileService.cs:247`
- Modify: `src/PPDS.Cli/Commands/Auth/AuthCommandGroup.cs:217,339`
- Modify: `src/PPDS.Auth/Pooling/ProfileConnectionSource.cs:37`

**Pattern:** Manual try/finally dispose → use `using` statement.

**Step 1:** Convert to `using` in all 5 sites
**Step 2:** Run tests
**Step 3:** Commit: `fix: use 'using' for disposable resource management`

### Task 10: Fix equality-on-floats (4 alerts)

**Files:**
- Modify: `src/PPDS.Dataverse/Query/Execution/ExpressionEvaluator.cs:460,461`
- Modify: `src/PPDS.Dataverse/Query/Execution/Functions/CastConverter.cs:231,232`

**Step 1:** Read both files to understand the comparison context
**Step 2:** Apply appropriate fix (likely these are checking for exact integer values stored as doubles — use `Math.Abs(x - y) < epsilon` or cast to appropriate type)
**Step 3:** Run tests
**Step 4:** Commit: `fix: avoid direct floating-point equality comparisons`

### Task 11: Fix remaining production code alerts (8 alerts)

**Files:**
- Modify: `src/PPDS.Dataverse/Query/Execution/Functions/DateFunctions.cs:186` — loss-of-precision: cast `number` to `(double)` before `* 7`
- Modify: `src/PPDS.Dataverse/Metadata/CachedMetadataProvider.cs:86` — constant-condition: fix or remove dead branch
- Modify: `src/PPDS.Cli/Services/Environment/EnvironmentConfigService.cs:85` — constant-condition (x2): fix or remove dead branch
- Modify: `src/PPDS.Cli/Tui/Dialogs/EnvironmentSelectorDialog.cs:279` — complex-condition: extract to named booleans
- Modify: `src/PPDS.Cli/Tui/Dialogs/EnvironmentConfigDialog.cs:31` — missing-summary: add `<summary>` tag
- Modify: `src/PPDS.Cli/Tui/Dialogs/TuiDialog.cs:28` — virtual-call-in-constructor: suppress with pragma (Terminal.Gui pattern)
- Modify: `src/PPDS.Cli/Tui/Views/DataTableView.cs:139` — virtual-call-in-constructor: suppress with pragma
- Modify: `src/PPDS.Dataverse/Pooling/DataverseConnectionPool.cs:958` — static-field-written-by-instance

**Step 1:** Read each file, apply fix
**Step 2:** Run tests
**Step 3:** Commit: `fix: resolve remaining CodeQL alerts in production code`

### Task 12: Fix remaining test alerts (9 alerts)

**Files:**
- Modify: `tests/PPDS.Dataverse.Tests/Query/Planning/Nodes/MetadataScanNodeTests.cs:184` — unused-collection
- Modify: `tests/PPDS.Dataverse.Tests/Query/Planning/Nodes/ClientWindowNodeTests.cs:606` — unused-collection
- Modify: `tests/PPDS.Dataverse.Tests/Query/QueryExecutorTests.cs:224` — unused-collection
- Modify: `tests/PPDS.Dataverse.Tests/Query/Planning/Nodes/ParallelPartitionNodeTests.cs:85,97` — static-field-written-by-instance (x2)
- Modify: `tests/PPDS.Cli.Tests/Services/Environment/EnvironmentServiceTests.cs:40` — empty-catch-block
- Modify: `tests/PPDS.Cli.Tests/Services/Profile/ProfileServiceTests.cs:35` — empty-catch-block
- Modify: `src/PPDS.Auth/Credentials/UsernamePasswordCredentialProvider.cs:112` — call-to-obsolete-method

**Step 1:** Read each file, apply fix
**Step 2:** Run tests
**Step 3:** Commit: `fix: resolve remaining CodeQL alerts in tests`

### Task 13: Run full test suite and verify

**Step 1:** Run: `dotnet test --filter "Category!=Integration" --no-restore`
**Step 2:** Verify 0 failures
**Step 3:** Push to trigger CI: `git push`
