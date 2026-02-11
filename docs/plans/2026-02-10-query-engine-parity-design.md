# PPDS Query Engine — Feature Parity & Platform Hardening

**Date:** 2026-02-10
**Status:** Draft — awaiting approval
**Goal:** Bring the PPDS query engine to full T-SQL feature parity for Dataverse scenarios, add cross-environment querying, safety settings, and fix critical UX issues.

---

## 1. Context

The PPDS query engine (v3) has a solid foundation: ScriptDom parser, FetchXML transpilation, streaming execution, bulk DML, window functions, CTEs, and TDS endpoint routing. However, real-world usage exposes gaps that block common T-SQL patterns. Users who write SQL against Dataverse expect standard T-SQL to work — when `ISNULL`, `HAVING COUNT(*) > 1`, or `WHERE id IN (SELECT ...)` fails, trust is lost immediately.

This design closes those gaps and adds platform-differentiating features: cross-environment queries, environment-aware safety, and TDS as a user-facing read-replica option.

---

## 2. Feature Gaps — Complete Inventory

### 2.1 Expression Foundations (Wave 1)

These are low-effort, high-impact fixes in `ExpressionCompiler.CompileScalar`. The ScriptDom parser already produces the correct AST nodes — we just need to handle them.

#### 2.1.1 Aggregate Alias Resolution in HAVING / ORDER BY

**Problem:** When `COUNT(*)` (or any aggregate) appears in a HAVING clause or ORDER BY, `ExpressionCompiler` tries to invoke it as a scalar function through `FunctionRegistry`, which only contains scalar functions. The aggregate was already computed by FetchXML and exists as an alias column in the result set.

**Error:** `Function 'COUNT' is not supported.`

**Fix:** During expression compilation for HAVING and ORDER BY contexts, maintain a mapping from aggregate expressions to their plan-output alias columns. When `CompileFunctionCall` encounters a known aggregate function name (COUNT, SUM, AVG, MIN, MAX, COUNT_BIG, STDEV, STDEVP, VAR, VARP), check the aggregate alias map first. If found, compile to a column reference instead of a function invocation.

**Files:**
- `src/PPDS.Query/Execution/ExpressionCompiler.cs` — add aggregate alias map parameter to compilation context
- `src/PPDS.Query/Planning/ExecutionPlanBuilder.cs` — pass aggregate alias map when compiling HAVING/ORDER BY predicates

**Tests:**
- `SELECT col, COUNT(*) AS cnt FROM entity GROUP BY col HAVING COUNT(*) > 1`
- `SELECT col, SUM(amount) FROM entity GROUP BY col ORDER BY SUM(amount) DESC`
- `SELECT col, COUNT(*) AS cnt FROM entity WHERE col IS NOT NULL GROUP BY col HAVING COUNT(*) > 1 ORDER BY cnt DESC` (the original failing query)

#### 2.1.2 ISNULL / COALESCE / NULLIF

**Problem:** Three of the most common SQL functions have zero support.

**Fix:**
- `ISNULL(expr, replacement)` — register in `FunctionRegistry` as a two-arg function: `return arg0 ?? arg1`
- `COALESCE(expr1, expr2, ...)` — handle `CoalesceExpression` AST node in `CompileScalar`: evaluate each expression in order, return the first non-null
- `NULLIF(expr1, expr2)` — handle `NullIfExpression` AST node in `CompileScalar`: return null if equal, else return expr1

**Files:**
- `src/PPDS.Query/Execution/ExpressionCompiler.cs` — add cases for `CoalesceExpression`, `NullIfExpression`
- `src/PPDS.Dataverse/Query/Execution/Functions/NullFunctions.cs` — new file for ISNULL registration

**Tests:**
- `SELECT ISNULL(name, 'Unknown') FROM account`
- `SELECT COALESCE(phone1, phone2, phone3, 'N/A') FROM contact`
- `SELECT revenue / NULLIF(quantity, 0) FROM product` (division-by-zero guard)
- Nested: `SELECT COALESCE(NULLIF(name, ''), 'Unnamed') FROM account`

#### 2.1.3 Simple CASE Expression

**Problem:** Only `SearchedCaseExpression` (`CASE WHEN condition THEN ...`) works. `SimpleCaseExpression` (`CASE expr WHEN value THEN ...`) is not in the `CompileScalar` switch.

**Fix:** Add `SimpleCaseExpression` handler to `CompileScalar`. Compile the input expression once, then for each WHEN clause compile the comparison value and use equality check. Falls through to ELSE or NULL.

**Files:**
- `src/PPDS.Query/Execution/ExpressionCompiler.cs` — add `SimpleCaseExpression` case

**Tests:**
- `SELECT CASE statecode WHEN 0 THEN 'Active' WHEN 1 THEN 'Inactive' END FROM account`
- With ELSE: `SELECT CASE statecode WHEN 0 THEN 'Active' ELSE 'Other' END FROM account`
- Nested simple CASE inside searched CASE

#### 2.1.4 BETWEEN Client-Side

**Problem:** BETWEEN only works when pushed to FetchXML. Client-side evaluation (in HAVING, computed expressions, etc.) is not compiled.

**Fix:** Handle `BetweenExpression` in `CompilePredicate`: compile as `expr >= low AND expr <= high` (or `NOT (expr >= low AND expr <= high)` for NOT BETWEEN).

**Files:**
- `src/PPDS.Query/Execution/ExpressionCompiler.cs` — add `BetweenExpression` case

**Tests:**
- `SELECT name FROM account WHERE revenue BETWEEN 1000 AND 5000` (FetchXML path — existing)
- `SELECT name, revenue FROM account HAVING revenue BETWEEN 1000 AND 5000` (client-side path — new)

---

### 2.2 JOIN Completeness (Wave 2)

#### 2.2.1 Extend JoinType Enum

**Current state:** `JoinType` only has `Inner` and `Left`.

**Fix:** Add `Right`, `FullOuter`, `Cross` to the enum. Update all three join nodes (Hash, NestedLoop, Merge) to handle new types.

**File:** `src/PPDS.Query/Planning/JoinType.cs`

#### 2.2.2 RIGHT OUTER JOIN

**Fix:** Transform to LEFT JOIN with swapped operands. When the planner encounters a RIGHT JOIN, swap left and right child nodes and change the join type to LEFT. This is a planning-time transformation — no runtime changes needed.

**File:** `src/PPDS.Query/Planning/ExecutionPlanBuilder.cs`

#### 2.2.3 FULL OUTER JOIN

**Fix:** Implement as: LEFT JOIN + anti-semi-join of right side (unmatched right rows), UNION ALL the results. Each join node (Hash, NestedLoop, Merge) needs a `FullOuter` execution mode that:
1. Yields all left rows with matched right rows (like LEFT JOIN)
2. Tracks which right rows were matched
3. After exhausting the left side, yields unmatched right rows with NULLs for left columns

**Files:**
- `src/PPDS.Query/Planning/Nodes/HashJoinNode.cs`
- `src/PPDS.Query/Planning/Nodes/NestedLoopJoinNode.cs`
- `src/PPDS.Query/Planning/Nodes/MergeJoinNode.cs`

#### 2.2.4 CROSS JOIN

**Fix:** NestedLoopJoin with no predicate (cartesian product). When the planner encounters a CROSS JOIN, emit a NestedLoopJoinNode with a null/always-true predicate.

**File:** `src/PPDS.Query/Planning/ExecutionPlanBuilder.cs`

#### 2.2.5 CROSS APPLY / OUTER APPLY

**Fix:** NestedLoopJoin where the inner side is re-evaluated per outer row. The inner plan node receives the current outer row as context (for correlated references). OUTER APPLY emits NULLs when the inner side produces no rows.

This is required for patterns like `CROSS APPLY STRING_SPLIT(column, ',')` and `CROSS APPLY OPENJSON(json_column)`.

**Files:**
- `src/PPDS.Query/Planning/ExecutionPlanBuilder.cs` — detect APPLY in FROM clause
- `src/PPDS.Query/Planning/Nodes/NestedLoopJoinNode.cs` — add correlated re-evaluation mode

**Tests:**
- `SELECT a.name, s.value FROM account a CROSS APPLY STRING_SPLIT(a.tags, ',') s`
- `SELECT a.name, j.* FROM account a OUTER APPLY OPENJSON(a.metadata) j`
- `SELECT * FROM a CROSS JOIN b` (cartesian)
- `SELECT * FROM a FULL OUTER JOIN b ON a.id = b.id`

---

### 2.3 Subqueries (Wave 3)

#### 2.3.1 IN (Subquery)

**Strategy:** Two-path approach:
1. **FetchXML folding (preferred):** When the subquery is a simple `SELECT column FROM entity [WHERE ...]`, fold into a FetchXML semi-join using `link-entity` with `link-type="in"`. This pushes the work to the server.
2. **Client-side fallback:** Materialize the inner query results into a hash set, then probe the hash set for each outer row. Used when the subquery is too complex for FetchXML folding.

**Files:**
- `src/PPDS.Query/Planning/ExecutionPlanBuilder.cs` — detect IN subqueries in WHERE
- `src/PPDS.Query/Planning/Nodes/HashSemiJoinNode.cs` — new node for client-side IN/EXISTS

#### 2.3.2 EXISTS / NOT EXISTS

**Strategy:** Same two-path approach:
1. **FetchXML folding:** EXISTS → `link-entity` with `link-type="exists"`. NOT EXISTS → `link-entity` with `link-type="not exists"` or LEFT JOIN + NULL check.
2. **Client-side:** Materialize inner query, probe per outer row using HashSemiJoinNode.

#### 2.3.3 NOT IN / NOT EXISTS Optimization

When NOT IN or NOT EXISTS can be expressed as a LEFT OUTER JOIN with a NULL check on the join key, rewrite it that way. This is significantly more efficient than client-side anti-semi-join because it pushes work to FetchXML.

**Transform:**
```sql
-- Original
WHERE id NOT IN (SELECT id FROM other)
-- Rewritten to
LEFT JOIN other ON entity.id = other.id WHERE other.id IS NULL
```

#### 2.3.4 Scalar Subqueries

**Fix:** New `ScalarSubqueryNode` that executes the inner query and asserts exactly one row is returned (throw if 0 or >1 rows). Returns the single scalar value.

**Files:**
- `src/PPDS.Query/Planning/Nodes/ScalarSubqueryNode.cs` — new
- `src/PPDS.Query/Planning/Nodes/AssertNode.cs` — new, validates row count constraints

#### 2.3.5 Correlated Subqueries

**Fix:** The inner query references columns from the outer query. Implementation:
1. **IndexSpoolNode** — cache inner query results indexed by the correlated column values
2. Per outer row, probe the spool using the outer row's correlated column values
3. If the spool has no cached results for those values, execute the inner query with those values as parameters

**Files:**
- `src/PPDS.Query/Planning/Nodes/IndexSpoolNode.cs` — new
- `src/PPDS.Query/Planning/Nodes/TableSpoolNode.cs` — new, general-purpose result cache

#### 2.3.6 Derived Tables

**Fix:** `SELECT * FROM (SELECT ...) AS sub` — plan the inner SELECT, materialize into a TableSpoolNode, then use that spool as the scan source for the outer query.

**Tests:**
- `WHERE id IN (SELECT id FROM other WHERE status = 1)`
- `WHERE NOT EXISTS (SELECT 1 FROM other WHERE other.parentid = entity.id)`
- `SELECT (SELECT COUNT(*) FROM contact WHERE parentcustomerid = a.accountid) AS cnt FROM account a`
- `SELECT * FROM (SELECT name, revenue FROM account WHERE revenue > 1000) AS big_accounts`

---

### 2.4 Remaining Gaps (Wave 4)

#### 2.4.1 GROUP BY on Expressions

**Problem:** `ExtractGroupByColumnNames` only extracts `ColumnReferenceExpression`. Expressions like `GROUP BY YEAR(createdon)` are silently ignored.

**Fix:** When GROUP BY contains function calls or expressions:
1. Check if the expression maps to a native FetchXML date grouping (`YEAR`, `MONTH`, `DAY`, `WEEK`, `QUARTER` on date columns → FetchXML `dategrouping` attribute). If so, emit native FetchXML grouping.
2. Otherwise, fall back to client-side aggregation: retrieve all rows, evaluate the GROUP BY expression per row, group in memory using `ClientAggregateNode`.

#### 2.4.2 Recursive CTE Wiring

**Problem:** `RecursiveCteNode` exists with full anchor + recursive member execution logic and `MaxRecursion` (default 100), but the planner's `PlanWithCtes` does not detect recursive references or route to `RecursiveCteNode`.

**Fix:** In `PlanWithCtes`, detect when a CTE's body references its own name. When detected, separate the anchor member (non-recursive SELECT) from the recursive member (SELECT that references the CTE name), and wire them into `RecursiveCteNode`.

#### 2.4.3 Control Flow Completeness

| Feature | Fix |
|---------|-----|
| `BREAK` | In `ExecuteWhileAsync`, check for `BreakStatement` and exit loop |
| `CONTINUE` | In `ExecuteWhileAsync`, check for `ContinueStatement` and skip to next iteration |
| `PRINT` | In `ScriptExecutionNode`, handle `PrintStatement` — route message to `IProgressReporter` |
| `THROW` | In `ScriptExecutionNode`, handle `ThrowStatement` — throw `PpdsException` with user message and error number |
| `RAISERROR` | In `ScriptExecutionNode`, handle `RaiseErrorStatement` — similar to THROW with severity/state |
| `GOTO` / labels | Low priority — implement only if user demand exists |

#### 2.4.4 SELECT @var = expr

**Fix:** In `ScriptExecutionNode`, handle `SelectStatement` where the SELECT list contains variable assignments. Execute the query, take the last row's values, and assign to variables in `VariableScope`.

#### 2.4.5 SELECT INTO #temp

**Fix:** In `ExecutionPlanBuilder`, detect `SelectStatement` with an `IntoClause`. Plan the SELECT normally, then wrap with a node that creates the temp table (schema inferred from SELECT output columns) and inserts all rows.

#### 2.4.6 STDEVP / VARP Population Formulas

**Problem:** Currently mapped to the same implementation as STDEV/VAR (sample formula with `n-1` divisor).

**Fix:** Add separate `AggregateFunction.StdevP` and `AggregateFunction.VarP` variants in `ClientAggregateNode` that use population formula (divisor `n` instead of `n-1`). Update `MapToMergeFunctionFromName` in `ExecutionPlanBuilder` to map correctly.

#### 2.4.7 IS NOT DISTINCT FROM

**Fix:** Handle `BooleanIsNotDistinctFromExpression` in `ExpressionCompiler.CompilePredicate`. Semantics: `a IS NOT DISTINCT FROM b` → `(a = b) OR (a IS NULL AND b IS NULL)`.

---

## 3. Cross-Environment Queries

### 3.1 Overview

Users can reference other configured environments in SQL using bracket syntax:

```sql
SELECT * FROM [UAT].dbo.account
WHERE accountid NOT IN (
    SELECT accountid FROM [PROD].dbo.account
)
```

The bracket identifier resolves to a profile label. The `dbo.` schema qualifier is optional.

### 3.2 Profile Model

```
Profile: Empire
  url: https://etsfsuat.crm.dynamics.com
  label: UAT                     ← display name AND bracket syntax identifier
  protection: Test               ← Dev | Test | Production (see §4)
```

- **Label** is the short identifier used in bracket syntax and display
- **Label must be unique** across configured profiles
- If unable to determine environment type from Dataverse metadata, default to **Production** protection (fail closed)
- Auto-detect protection level from org metadata (`EnvironmentType` field), allow user override

### 3.3 Execution Model

Cross-environment queries cannot use FetchXML joins across orgs. Execution model:

1. **Parse:** Identify multi-part table names (`[Label].dbo.entity` or `[Label].entity`)
2. **Plan:** For each remote reference, create a `RemoteScanNode` that targets the remote environment's connection pool
3. **Execute:** Remote scan nodes authenticate via the target profile's connection pool and execute their portion of the query
4. **Materialize:** Remote results are spooled into a `TableSpoolNode` (in-memory cache)
5. **Join:** Local join nodes (Hash, NestedLoop, Merge) join local and remote results

This is the linked-server pattern — straightforward and proven.

### 3.4 New Plan Nodes

- `RemoteScanNode` — executes a FetchXML or TDS query against a remote environment's connection pool
- Reuses existing `TableSpoolNode` for materialization

### 3.5 Cross-Environment DML

Supported but heavily guarded (see §4 Safety). The flow:

1. Source data is read from the source environment
2. Data is materialized locally
3. DML is executed against the target environment's connection pool
4. **Always prompts** with source/target labels and record count, regardless of safety settings

### 3.6 Error Handling

- Profile not found: `No environment found matching 'XYZ'. Configure a profile with label 'XYZ' to use cross-environment queries.`
- Auth failure on remote: `Authentication failed for environment 'UAT'. Check profile credentials.`
- Label ambiguity: Not possible — labels must be unique (enforced at profile creation)

---

## 4. Safety Settings

### 4.1 Architecture

Settings are stored in the PPDS profile configuration file, shared across all interfaces (CLI, TUI, MCP, VS Code). Per-query overrides via `OPTION()` hints take precedence.

```
Profile config (persisted) → Session setting (TUI toggle) → Query OPTION() hint (override)
```

### 4.2 Execution Settings

| Setting | Type | Default | Description |
|---------|------|---------|-------------|
| `dml_batch_size` | int | 100 | Records per DML batch (1-1000) |
| `max_parallelism` | int | 0 | Worker threads for DML (0 = auto, based on pool size) |
| `bypass_custom_plugins` | enum | None | None / Synchronous / Asynchronous / All |
| `bypass_power_automate_flows` | bool | false | Suppress flow triggers on DML |
| `use_bulk_delete` | bool | false | Route full-table DELETE to async BulkDeleteRequest |
| `use_tds_endpoint` | bool | false | Route compatible SELECT queries to TDS read replica |
| `datetime_mode` | enum | UTC | UTC / Local / EnvironmentTimezone |
| `show_fetchxml_in_explain` | bool | true | Include FetchXML in EXPLAIN output |

### 4.3 Limit Settings

| Setting | Type | Default | Description |
|---------|------|---------|-------------|
| `max_result_rows` | int | 0 | Maximum rows returned (0 = unlimited) |
| `max_page_retrievals` | int | 200 | Maximum FetchXML pages fetched (0 = unlimited). 200 × 5000 = 1M records |
| `query_timeout_seconds` | int | 300 | Cancel query after N seconds. Error message: "Query cancelled after N seconds. Note: in-flight server requests may complete on the Dataverse side." |

### 4.4 Safety Settings

| Setting | Type | Default | Description |
|---------|------|---------|-------------|
| `warn_insert_threshold` | int | 1 | Prompt when inserting more than N records (0 = always) |
| `warn_update_threshold` | int | 0 | Prompt when updating more than N records (0 = always) |
| `warn_delete_threshold` | int | 0 | Prompt when deleting more than N records (0 = always) |
| `prevent_update_without_where` | bool | true | Block UPDATE without WHERE clause |
| `prevent_delete_without_where` | bool | true | Block DELETE without WHERE clause |

### 4.5 Environment Protection

Per-profile protection level:

| Level | SELECT | DML | Cross-Env DML Target |
|-------|--------|-----|---------------------|
| **Development** | Unrestricted | Unrestricted | Prompt with record count |
| **Test** | Unrestricted | Warn per thresholds | Prompt with record count |
| **Production** | Unrestricted | Block by default, require explicit confirmation with preview | Always prompt, show source/target/count |

Auto-detection from Dataverse `EnvironmentType` metadata:
- Sandbox → Development
- Production → Production
- Developer → Development
- Trial → Test
- **Unknown / API error → Production** (fail closed)

User can override: `ppds profile set MyProfile --protection development`

### 4.6 Cross-Environment DML Policy

| Policy | Behavior | Default? |
|--------|----------|----------|
| **Read-only** | Cross-env queries are SELECT only | **Yes** |
| **Prompt** | Confirm each cross-env DML with source/target/count | |
| **Allow** | No additional confirmation beyond standard DML safety | |

**Hard rule (not overridable):** Cross-environment DML targeting a Production-level environment always prompts, regardless of policy setting.

### 4.7 Query Hint Overrides

Users can override settings per-query using `OPTION()` syntax:

```sql
DELETE FROM account WHERE statecode = 1
OPTION (BATCH_SIZE 50, MAXDOP 4, BYPASS_PLUGINS, BYPASS_FLOWS)
```

Supported hints:
- `BATCH_SIZE n` — override `dml_batch_size`
- `MAXDOP n` — override `max_parallelism`
- `BYPASS_PLUGINS` — enable bypass for this query
- `BYPASS_FLOWS` — enable flow bypass for this query
- `USE_TDS` — route to TDS endpoint
- `NOLOCK` — map to FetchXML `no-lock` attribute
- `HASH GROUP` — force client-side aggregation
- `MAX_ROWS n` — override `max_result_rows`

---

## 5. TDS Endpoint — User-Facing Read Replica

### 5.1 Rationale

TDS is not an optimization detail — it's a deliberate user choice:

| | FetchXML (Dataverse) | TDS Endpoint |
|---|---|---|
| Writes | Full DML | Read-only |
| Data freshness | Real-time | Slight replication delay |
| Production impact | Hits live server | Hits read replica |
| Best for | DML, real-time data | Large reads, sizing up operations, safe exploration |

### 5.2 UX

- **TUI:** Toolbar toggle or status bar indicator showing `[Dataverse]` or `[TDS Read Replica]`. Clear visual distinction so users know they may see stale data.
- **CLI:** Flag `ppds query --tds "SELECT ..."`
- **Per-query:** `OPTION (USE_TDS)` hint
- **Profile setting:** `use_tds_endpoint: true` for users who prefer TDS as default

### 5.3 Current State

TDS routing exists (`TdsScanNode`, `TdsCompatibilityChecker`, `TdsQueryExecutor`) but is not exposed in any UI. Wiring needed:
- `QueryPlanOptions.UseTdsEndpoint` needs to be driven by profile setting / session toggle / query hint
- `SqlQueryScreen` needs a toggle control
- CLI needs `--tds` flag

---

## 6. Query Cancellation

### 6.1 Problem

Users cannot cancel a running query in the TUI. No cancel button, no hotkey, no Escape handling during execution. The only cancellation is screen-level (closing the tab).

### 6.2 Fix

1. Add a query-level `CancellationTokenSource` to `SqlQueryScreen`
2. Create/reset at the start of each `ExecuteQueryAsync`
3. Link to `ScreenCancellation` so tab close also cancels
4. Register **Escape** as the cancel trigger while `_isExecuting == true`
5. Show status: `Executing... (press Escape to cancel)`
6. On cancel: set status to `Cancelling...`, await graceful stop, then `Cancelled.`

### 6.3 Cancellation Behavior

- **Between pages:** Cancellation stops fetching the next FetchXML page. Results received so far are retained and displayed.
- **During page fetch:** The in-flight HTTP request cannot be aborted (ServiceClient limitation). The cancel takes effect when the current request completes.
- **During DML:** Cancel stops after the current batch completes. Records already written are not rolled back (Dataverse has no transactions).
- **Timeout:** `query_timeout_seconds` setting creates a `CancellationTokenSource` with timeout. Error message explicitly states: "Query cancelled after N seconds. Note: in-flight server requests may complete on the Dataverse side."

### 6.4 Files

- `src/PPDS.Cli/Tui/Screens/SqlQueryScreen.cs` — add `_queryCts`, Escape handler, status messaging

---

## 7. TUI Paste Bug Fix

### 7.1 Root Cause

Race condition between autocomplete popup and character-by-character paste processing.

When pasting via Ctrl+V, Windows Terminal injects clipboard text as individual `VK_PACKET` key events. Terminal.Gui processes each one through `ProcessKey`, which triggers autocomplete checks after `FROM ` and `JOIN ` keywords. If the language service returns completions before the next character arrives, the autocomplete popup activates mid-paste. Subsequent Enter/Tab characters in the paste stream then accept a completion and replace text using stale cursor positions, mangling the pasted content.

### 7.2 Fix

Override `Ctrl+V` in `SyntaxHighlightedTextView` to handle paste as a single bulk text insertion:

```
On Ctrl+V:
  1. Read clipboard via Clipboard.Contents
  2. Set _isPasting = true (suppresses autocomplete triggers)
  3. Insert full text as one operation
  4. Set _isPasting = false
  5. Trigger syntax highlighting on final state
  6. Optionally trigger autocomplete on final cursor position
```

In `CheckAutocompleteTrigger`: early-return if `_isPasting` is true.

### 7.3 Secondary Fix

`SqlQueryScreen.cs:201` maps Ctrl+Y to redo but actually triggers Terminal.Gui's paste command (Ctrl+Y = emacs yank). Fix: implement proper redo or remove the handler.

### 7.4 Files

- `src/PPDS.Cli/Tui/Views/SyntaxHighlightedTextView.cs` — Ctrl+V override, `_isPasting` flag
- `src/PPDS.Cli/Tui/Screens/SqlQueryScreen.cs` — fix Ctrl+Y handler

---

## 8. Execution Optimizations

### 8.1 NOT IN / NOT EXISTS → LEFT JOIN Rewrite

When `WHERE id NOT IN (SELECT id FROM other)` or `WHERE NOT EXISTS (SELECT 1 FROM other WHERE other.id = entity.id)` can be expressed as a LEFT JOIN + NULL check, rewrite during planning:

```sql
LEFT JOIN other ON entity.id = other.id WHERE other.id IS NULL
```

This pushes the anti-semi-join to FetchXML instead of materializing the subquery client-side.

### 8.2 Date Grouping Folding

When GROUP BY contains `YEAR(datecol)`, `MONTH(datecol)`, `DAY(datecol)`, `QUARTER(datecol)`, or `WEEK(datecol)`, convert to native FetchXML `dategrouping` attribute instead of pulling all records for client-side grouping.

### 8.3 Child Record Paging Fix

FetchXML pagination with linked entities can silently skip records when a parent has many children (the page boundary falls mid-parent). Detect this scenario and use custom paging logic that ensures all child records for a parent are retrieved before advancing.

### 8.4 Adaptive DML Batching

Auto-adjust `dml_batch_size` based on execution time per batch. Target ~10 seconds per batch. If batches complete faster, increase size (up to 1000). If slower, decrease. Prevents timeouts on slow/complex entities while maximizing throughput on fast ones.

### 8.5 Adaptive Thread Management

When DML parallelism encounters Dataverse service protection limit errors (HTTP 429), automatically reduce thread count and increase backoff. Prevents cascading throttling while maintaining throughput.

---

## 9. Implementation Waves

### Wave 1 — Expression Foundations + UX Fixes
**Scope:** Aggregate alias resolution, ISNULL/COALESCE/NULLIF, Simple CASE, BETWEEN client-side, query cancellation, paste bug fix

**Rationale:** Unblocks the most common real-world queries. Fixes the two worst UX issues (can't cancel, can't paste). Every subsequent wave benefits from these being in place.

### Wave 2 — JOIN Completeness + Safety Settings
**Scope:** RIGHT/FULL OUTER/CROSS JOIN, CROSS APPLY/OUTER APPLY, full safety settings architecture, environment protection levels, query hints

**Rationale:** JOINs are the next most common gap. Safety settings are prerequisite for Wave 3 (cross-environment) and for TDS exposure.

### Wave 3 — Subqueries + Cross-Environment + TDS
**Scope:** IN/EXISTS/NOT IN/NOT EXISTS (with FetchXML folding), scalar subqueries, correlated subqueries, derived tables, cross-environment queries, TDS as user-facing toggle

**Rationale:** Subqueries are the largest architectural addition. Cross-environment queries depend on safety settings (Wave 2). TDS exposure depends on safety settings.

### Wave 4 — Completeness + Optimizations
**Scope:** GROUP BY expressions, recursive CTE wiring, control flow (BREAK/CONTINUE/PRINT/THROW), SELECT @var, SELECT INTO #temp, STDEVP/VARP, IS NOT DISTINCT FROM, date grouping folding, child record paging fix, adaptive batching/threading, NOT IN/NOT EXISTS → LEFT JOIN rewrite

**Rationale:** Polish and performance. All features here are valuable but not blocking common queries.

---

## 10. Testing Strategy

Each wave includes tests at three levels:

1. **Unit tests** — Exercise individual nodes and compiler changes in isolation (e.g., `ExpressionCompiler` handles `CoalesceExpression` correctly)
2. **Plan tests** — Verify the execution plan builder produces the correct node tree for a given SQL input (e.g., `HAVING COUNT(*) > 1` produces a `ClientFilterNode` that references the aggregate alias, not a function invocation)
3. **Integration tests** — End-to-end query execution against a live Dataverse environment (existing `Category=Integration` test infrastructure)

TUI-specific tests for cancellation and paste fix use `Category=TuiUnit`.

---

## 11. Non-Goals

- **CREATE/ALTER/DROP persistent tables** — Dataverse schema changes are out of scope for the query engine
- **Stored procedure creation** — Not applicable to Dataverse
- **sys.* / INFORMATION_SCHEMA views** — Use existing metadata virtual tables instead
- **GOTO / labels** — Rarely used, add only on user demand
- **Cursor FETCH directions beyond NEXT** — PRIOR/FIRST/LAST/ABSOLUTE/RELATIVE are low-priority
- **TOP PERCENT / TOP WITH TIES** — Uncommon patterns
- **XML support** — Dataverse doesn't use XML data types
- **AI/Copilot features** — Separate initiative
