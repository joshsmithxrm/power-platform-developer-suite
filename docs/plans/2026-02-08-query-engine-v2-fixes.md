# Query Engine v2: Phase 4-7 Review Fixes

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.
> **Context:** Code review findings from Phases 4-7 of `docs/specs/query-engine-v2.md`.
> **Branch:** `fix/tui-colors` in worktree `C:\VS\ppdsw\ppds\.worktrees\tui-polish`

**Goal:** Fix all critical, important, and minor issues identified in the Phase 4-7 code review. 16 tasks, grouped by dependency so parallelizable tasks can be dispatched together.

**Test command:** `dotnet test --filter "Category=TuiUnit|Category=PlanUnit"`

---

## Task 1: Wire IBulkOperationExecutor + IMetadataQueryExecutor into SqlQueryService [CRITICAL]

**Why:** Both DML and metadata queries fail at runtime with `InvalidOperationException` because `SqlQueryService` constructs `QueryPlanContext` without these dependencies. This is the #1 blocker — two entire feature areas are non-functional end-to-end.

**Files:**
- Modify: `src/PPDS.Cli/Services/Query/ISqlQueryService.cs`
- Modify: `src/PPDS.Cli/Services/Query/SqlQueryService.cs`
- Modify: `src/PPDS.Dataverse/Query/Planning/QueryPlanContext.cs`
- Modify: `src/PPDS.Dataverse/Query/Planning/Nodes/MetadataScanNode.cs` (remove null! suppression)
- Modify: `src/PPDS.Dataverse/Query/Planning/QueryPlanner.cs` (pass executor from context)

**Steps:**

1. Add optional constructor parameters to `SqlQueryService`:
   ```csharp
   public SqlQueryService(
       IQueryExecutor queryExecutor,
       IBulkOperationExecutor? bulkOperationExecutor = null,
       IMetadataQueryExecutor? metadataQueryExecutor = null)
   ```
   Store as private fields.

2. Update `QueryPlanContext` to accept and expose these:
   ```csharp
   public IBulkOperationExecutor? BulkOperationExecutor { get; }
   public IMetadataQueryExecutor? MetadataQueryExecutor { get; }
   ```
   Add them to the constructor. If the context already has these properties, ensure they're being set.

3. In `SqlQueryService.ExecuteAsync`, pass both to `QueryPlanContext`:
   ```csharp
   var context = new QueryPlanContext(
       _queryExecutor,
       _expressionEvaluator,
       cancellationToken,
       bulkOperationExecutor: _bulkOperationExecutor,
       metadataQueryExecutor: _metadataQueryExecutor);
   ```

4. In `SqlQueryService.ExecuteStreamingAsync`, do the same.

5. In `QueryPlanner.PlanMetadataQuery`, get the executor from context instead of passing `null!`:
   - Remove the `null!` suppression
   - `MetadataScanNode` should accept `IMetadataQueryExecutor?` and resolve from context at execution time, OR the planner should require it from `QueryPlanOptions`

6. In `MetadataScanNode`, make `MetadataExecutor` nullable and resolve from context in `ExecuteAsync`:
   ```csharp
   var executor = MetadataExecutor ?? context.MetadataQueryExecutor
       ?? throw new InvalidOperationException("MetadataQueryExecutor is required for metadata queries.");
   ```

7. Verify existing tests still pass. Add a test that constructs `SqlQueryService` with both optional dependencies and verifies they reach the context.

**Commit:**
```
fix(query): wire IBulkOperationExecutor and IMetadataQueryExecutor into SqlQueryService

Both DML and metadata queries were non-functional at runtime because
SqlQueryService did not pass these dependencies to QueryPlanContext.
```

---

## Task 2: Fix metadata.metadata. double prefix [CRITICAL]

**Why:** `QueryPlanner` passes `"metadata.entity"` (schema-qualified) to `MetadataScanNode`, which prepends `metadata.` again in its `Description` property, producing `"MetadataScan: metadata.metadata.entity"` in EXPLAIN output and logs.

**Files:**
- Modify: `src/PPDS.Dataverse/Query/Planning/QueryPlanner.cs` (PlanMetadataQuery method)
- Modify: `src/PPDS.Dataverse/Query/Planning/Nodes/MetadataScanNode.cs` (Description property)
- Modify: `tests/PPDS.Dataverse.Tests/Query/Planning/Nodes/MetadataScanNodeTests.cs` (verify description)

**Steps:**

1. In `QueryPlanner.PlanMetadataQuery`, strip the `metadata.` prefix before passing to `MetadataScanNode`:
   ```csharp
   var metadataTable = entityName;
   if (metadataTable.StartsWith("metadata.", StringComparison.OrdinalIgnoreCase))
       metadataTable = metadataTable["metadata.".Length..];
   ```

2. In `MetadataScanNode.Description`, the node stores and displays just the table name (e.g., `"entity"`). If the Description property prepends `metadata.`, it should only do so once:
   ```csharp
   public string Description => $"MetadataScan: metadata.{MetadataTable}";
   ```
   This works correctly when `MetadataTable` is `"entity"` (not `"metadata.entity"`).

3. Verify `MetadataQueryExecutor.QueryMetadataAsync` still works — its `GetTableName()` helper that strips prefixes may need adjustment since the input will now be just `"entity"` instead of `"metadata.entity"`.

4. Update any tests that assert on `MetadataTable` or `Description` values.

**Commit:**
```
fix(query): fix double metadata. prefix in MetadataScanNode description

QueryPlanner now strips the schema prefix before passing to
MetadataScanNode. EXPLAIN output now correctly shows
"MetadataScan: metadata.entity" instead of "metadata.metadata.entity".
```

---

## Task 3: Thread row cap from DmlSafetyGuard to DmlExecuteNode

**Why:** The 10K default row cap and `--no-limit` flag have no runtime effect. `DmlSafetyGuard` computes the cap but it never reaches the executor.

**Files:**
- Modify: `src/PPDS.Cli/Services/Query/SqlQueryRequest.cs` (add RowCap to DmlSafety)
- Modify: `src/PPDS.Cli/Services/Query/SqlQueryService.cs` (pass row cap to planner)
- Modify: `src/PPDS.Dataverse/Query/Planning/QueryPlanOptions.cs` (add RowCap)
- Modify: `src/PPDS.Dataverse/Query/Planning/QueryPlanner.cs` (pass RowCap to DmlExecuteNode)
- Modify: `tests/PPDS.Dataverse.Tests/Query/Planning/DmlPlannerTests.cs` (verify cap propagation)

**Steps:**

1. Add `RowCap` to `QueryPlanOptions`:
   ```csharp
   public int? DmlRowCap { get; init; }  // null = unlimited, default 10_000
   ```

2. In `SqlQueryService.ExecuteAsync`, after `DmlSafetyGuard.Check()`, pass the computed row cap to the plan options:
   ```csharp
   var planOptions = new QueryPlanOptions
   {
       // ... existing
       DmlRowCap = safetyResult.RowCap
   };
   ```

3. In `QueryPlanner.PlanInsert/PlanUpdate/PlanDelete`, pass `options.DmlRowCap` to `DmlExecuteNode` factory:
   ```csharp
   return DmlExecuteNode.Delete(sourceNode, entityName, rowCap: options.DmlRowCap ?? int.MaxValue);
   ```

4. Add test: create a plan with `DmlRowCap = 100`, verify `DmlExecuteNode.RowCap == 100`.

**Commit:**
```
fix(query): thread DML row cap from safety guard through to executor

The 10K default cap and --no-limit flag now have runtime effect.
DmlSafetyGuard computes the cap, it flows through QueryPlanOptions
to DmlExecuteNode where it limits actual rows processed.
```

---

## Task 4: Add virtual column expansion to streaming path

**Why:** `ExecuteStreamingAsync` skips `SqlQueryResultExpander.ExpandFormattedValueColumns`, so streaming results lack `*name` columns (owneridname, statuscodename, etc.) — a key PPDS feature regressed in the streaming path.

**Files:**
- Modify: `src/PPDS.Cli/Services/Query/SqlQueryService.cs` (ExecuteStreamingAsync)
- Modify: `tests/PPDS.Dataverse.Tests/Query/Execution/StreamingExecutorTests.cs` (add expansion test)

**Steps:**

1. In `ExecuteStreamingAsync`, after collecting each chunk of rows, apply the result expander before yielding. The transpile result (with virtual column info) is already available:
   ```csharp
   // After collecting chunk rows into a QueryResult-like structure:
   var expandedChunk = SqlQueryResultExpander.ExpandFormattedValueColumns(
       chunkResult, transpileResult.VirtualColumns);
   ```

2. This may require restructuring the streaming method slightly — the expander operates on `QueryResult`, so either:
   - a) Build a mini `QueryResult` per chunk and expand it, or
   - b) Extract the expansion logic into a row-level method that can be applied per-row

   Option (a) is simpler and preserves the existing expander contract.

3. Also fix column type inference in `InferColumnsFromRow` — instead of `QueryColumnType.Unknown`, detect type from `QueryValue`:
   ```csharp
   DataType = value.IsLookup ? QueryColumnType.Lookup
       : value.IsOptionSet ? QueryColumnType.OptionSet
       : value.IsBoolean ? QueryColumnType.Boolean
       : QueryColumnType.Unknown
   ```

4. Add test: streaming query with a lookup column verifies `owneridname` appears in output chunks.

**Commit:**
```
fix(query): add virtual column expansion to streaming path

Streaming results now include *name columns (owneridname,
statuscodename, etc.) matching the non-streaming path. Also
improves column type inference from QueryValue metadata.
```

---

## Task 5: Pass IProgressReporter to bulk executor DML calls

**Why:** CLAUDE.md ALWAYS rule: "Accept IProgressReporter for operations >1 second." DML operations via BulkOperationExecutor don't report progress.

**Files:**
- Modify: `src/PPDS.Dataverse/Query/Planning/Nodes/DmlExecuteNode.cs`
- Modify: `src/PPDS.Dataverse/Query/Planning/QueryPlanContext.cs` (ensure ProgressReporter accessible)

**Steps:**

1. In `DmlExecuteNode.ExecuteInsertValuesAsync`, `ExecuteInsertSelectAsync`, `ExecuteUpdateAsync`, `ExecuteDeleteAsync` — pass `context.ProgressReporter` to the bulk executor calls:
   ```csharp
   var result = await context.BulkOperationExecutor.CreateMultipleAsync(
       TargetEntity, entities,
       new BulkOperationOptions { BatchSize = 100 },
       progress: context.ProgressReporter,
       cancellationToken: context.CancellationToken);
   ```

2. If `QueryPlanContext.ProgressReporter` doesn't exist yet, add it:
   ```csharp
   public IProgress<ProgressSnapshot>? ProgressReporter { get; init; }
   ```

3. Verify by checking that existing tests still pass (progress parameter is optional/nullable in the bulk executor interface).

**Commit:**
```
fix(query): pass IProgressReporter to bulk executor in DML operations

DML operations now report progress through the existing
IProgressReporter infrastructure, enabling TUI and CLI feedback
during long-running INSERT/UPDATE/DELETE operations.
```

---

## Task 6: Dispose SemaphoreSlim in ParallelPartitionNode

**Why:** `SemaphoreSlim` implements `IDisposable`. The semaphore created per execution is never disposed — resource leak.

**Files:**
- Modify: `src/PPDS.Dataverse/Query/Planning/Nodes/ParallelPartitionNode.cs`

**Steps:**

1. Wrap the semaphore in a `using` block or add explicit disposal in a `finally`:
   ```csharp
   using var semaphore = new SemaphoreSlim(MaxParallelism);
   // ... existing Task.WhenAll logic
   ```

2. Verify existing `ParallelPartitionNodeTests` still pass.

**Commit:**
```
fix(query): dispose SemaphoreSlim in ParallelPartitionNode
```

---

## Task 7: Fix INSERT SELECT column mapping to ordinal

**Why:** `INSERT INTO account (name) SELECT fullname FROM contact` fails because `DmlExecuteNode` looks up source row by INSERT column name (`name`) instead of by ordinal position.

**Files:**
- Modify: `src/PPDS.Dataverse/Query/Planning/Nodes/DmlExecuteNode.cs` (ExecuteInsertSelectAsync)
- Modify: `tests/PPDS.Dataverse.Tests/Query/Planning/Nodes/DmlExecuteNodeTests.cs` (add ordinal mapping test)

**Steps:**

1. In `ExecuteInsertSelectAsync`, change the column mapping from name-based to ordinal-based. The source rows come from a SELECT query — their column names may differ from the INSERT column names. Map by position:
   ```csharp
   // Get source column names in order from the first row (or from plan metadata)
   var sourceColumnNames = sourceRow.Values.Keys.ToList();

   for (int i = 0; i < InsertColumns.Count; i++)
   {
       var insertColumn = InsertColumns[i];
       var sourceColumn = sourceColumnNames[i];
       var value = sourceRow.Values[sourceColumn];
       entity[insertColumn] = ConvertToSdkValue(value);
   }
   ```

   However, `IReadOnlyDictionary.Keys` order is not guaranteed. Better approach: have the source plan node (ProjectNode or FetchXmlScanNode) emit column metadata alongside the rows, or use the AST's SELECT column list to determine ordering.

   Simplest correct fix: store the source SELECT's column aliases/names during planning and use them for the positional mapping:
   ```csharp
   // At plan time, capture source column order:
   public IReadOnlyList<string> SourceColumns { get; }  // from SELECT column list

   // At execution time:
   for (int i = 0; i < InsertColumns.Count; i++)
   {
       var sourceKey = SourceColumns[i];
       var value = sourceRow.Values.TryGetValue(sourceKey, out var v) ? v : QueryValue.Null;
       entity[InsertColumns[i]] = ConvertToSdkValue(value);
   }
   ```

2. Update `DmlExecuteNode.InsertSelect` factory to accept source column names.

3. Update `QueryPlanner.PlanInsert` to extract column names from the source SELECT AST and pass them.

4. Add test: `INSERT INTO target (col_a) SELECT col_b FROM source` — verify col_b value ends up in col_a.

**Commit:**
```
fix(query): fix INSERT SELECT to map columns by ordinal position

INSERT INTO t (a, b) SELECT x, y FROM s now correctly maps x→a
and y→b by position, regardless of column name differences.
```

---

## Task 8: Integrate PrefetchScanNode into QueryPlanner

**Why:** PrefetchScanNode exists and is tested but is never wired into the planner. Pages are fetched sequentially — no zero-wait scrolling.

**Files:**
- Modify: `src/PPDS.Dataverse/Query/Planning/QueryPlanner.cs`
- Modify: `src/PPDS.Dataverse/Query/Planning/QueryPlanOptions.cs` (add EnablePrefetch flag)
- Modify: `tests/PPDS.Dataverse.Tests/Query/Planning/QueryPlannerTests.cs` (verify prefetch wrapping)

**Steps:**

1. Add option to `QueryPlanOptions`:
   ```csharp
   public bool EnablePrefetch { get; init; } = true;  // default on
   public int PrefetchBufferSize { get; init; } = 5000;  // ~3 pages
   ```

2. In `QueryPlanner.Plan` for SELECT queries, after building the `FetchXmlScanNode`, wrap it with `PrefetchScanNode` when prefetch is enabled and the query isn't an aggregate (aggregates return few rows — prefetch adds overhead for no benefit):
   ```csharp
   IQueryPlanNode scanNode = new FetchXmlScanNode(fetchXml, entityName, autoPage: true);

   if (options.EnablePrefetch && !statement.HasAggregates())
   {
       scanNode = new PrefetchScanNode(scanNode, bufferSize: options.PrefetchBufferSize);
   }
   ```

3. Ensure the TUI's `SqlQueryScreen` benefits automatically — since it consumes results through the plan executor, wrapping the scan node is sufficient.

4. Add planner test: non-aggregate SELECT produces PrefetchScanNode wrapping FetchXmlScanNode. Aggregate SELECT does NOT get prefetch.

**Commit:**
```
feat(query): integrate PrefetchScanNode into query planner

Non-aggregate SELECT queries are now wrapped with PrefetchScanNode
for page-ahead buffering. Enables zero-wait scrolling in TUI.
Configurable via QueryPlanOptions.EnablePrefetch.
```

---

## Task 9: Add memory bounds to ClientWindowNode

**Why:** Spec § 11.4 requires configurable memory limits for materializing nodes. ClientWindowNode loads all rows into memory with no limit — risk of OOM on large result sets.

**Files:**
- Modify: `src/PPDS.Dataverse/Query/Planning/Nodes/ClientWindowNode.cs`
- Modify: `src/PPDS.Dataverse/Query/Planning/QueryPlanOptions.cs` (add MaxMaterializationRows)
- Modify: `src/PPDS.Cli/Infrastructure/Errors/ErrorCodes.cs` (add QUERY_MEMORY_LIMIT if not exists)
- Modify: `tests/PPDS.Dataverse.Tests/Query/Planning/Nodes/ClientWindowNodeTests.cs` (add limit test)

**Steps:**

1. Add option to `QueryPlanOptions`:
   ```csharp
   public int MaxMaterializationRows { get; init; } = 500_000;  // Safety limit
   ```

2. Pass limit through `QueryPlanContext` (or read from a property on the node).

3. In `ClientWindowNode.ExecuteAsync`, during the materialization loop, check row count:
   ```csharp
   var allRows = new List<QueryRow>();
   await foreach (var row in Input.ExecuteAsync(context, cancellationToken))
   {
       cancellationToken.ThrowIfCancellationRequested();
       allRows.Add(row);
       if (allRows.Count > context.Options.MaxMaterializationRows)
       {
           throw new PpdsException(
               ErrorCode.QueryMemoryLimit,
               $"Window function materialized {allRows.Count:N0} rows, exceeding the " +
               $"{context.Options.MaxMaterializationRows:N0} row limit. Add a WHERE or TOP clause " +
               "to reduce the result set.");
       }
   }
   ```

4. Add test: mock source yields 100 rows, set limit to 50, verify PpdsException thrown with correct error code.

5. Add cancellation test while at it (reviewer noted missing): cancel token mid-materialization, verify `OperationCanceledException`.

**Commit:**
```
fix(query): add memory bounds to ClientWindowNode

Window function queries now enforce a configurable row limit
(default 500K) during materialization. Throws PpdsException
with QUERY_MEMORY_LIMIT error code when exceeded, suggesting
the user add WHERE or TOP to reduce results.
```

---

## Task 10: Create ExplainCommandTests.cs

**Why:** The plan explicitly listed this as a deliverable. EXPLAIN command CLI integration (argument parsing, profile resolution, error handling) has no test coverage.

**Files:**
- Create: `tests/PPDS.Cli.Tests/Commands/Query/ExplainCommandTests.cs`

**Steps:**

1. Create test class with `[Trait("Category", "PlanUnit")]`.

2. Test cases:
   - `ExplainCommand_ProducesFormattedPlanOutput` — execute with a simple SELECT, verify output contains plan tree markers (├──, └──, FetchXmlScan, Project)
   - `ExplainCommand_AggregateQuery_ShowsAggregateNode` — explain an aggregate query, verify the plan shows aggregate=true
   - `ExplainCommand_InvalidSql_ReturnsParseError` — malformed SQL returns structured error
   - `ExplainCommand_WithJoin_ShowsLinkEntity` — JOIN query plan includes link-entity in FetchXML
   - `ExplainCommand_DmlQuery_ShowsDmlNode` — explain an INSERT/UPDATE/DELETE shows DmlExecuteNode

3. Use `FakeSqlQueryService` (already exists at `tests/PPDS.Cli.Tests/Mocks/FakeSqlQueryService.cs`) or mock `ISqlQueryService.ExplainAsync` for the tests.

4. Verify all tests pass.

**Commit:**
```
test(query): add ExplainCommand CLI integration tests

Covers plan output formatting, aggregate queries, parse errors,
JOIN queries, and DML plan inspection. Fills planned deliverable
gap from Phase 4 review.
```

---

## Task 11: Fix AVG weighted merge in parallel aggregate partitioning [CRITICAL]

**Why:** `BuildMergeAggregateColumns` generates companion COUNT aliases (e.g., `avg_rev_count`) for weighted AVG merging, but `PlanAggregateWithPartitioning` never injects additional COUNT attributes into the partitioned FetchXML. The weighted-average code path in `MergeAggregateNode` is unreachable — AVG always falls back to incorrect average-of-averages.

**Files:**
- Modify: `src/PPDS.Dataverse/Query/Planning/QueryPlanner.cs` (PlanAggregateWithPartitioning)
- Modify: `tests/PPDS.Dataverse.Tests/Query/Planning/QueryPlannerTests.cs` (add AVG partition test)

**Steps:**

1. In `PlanAggregateWithPartitioning`, after transpiling the base FetchXML, detect AVG columns in the SELECT list.

2. For each AVG column, inject a companion COUNT aggregate into the FetchXML so each partition returns both `avg_rev` and `avg_rev_count`:
   ```xml
   <!-- Original AVG attribute -->
   <attribute name="revenue" alias="avg_rev" aggregate="avg" />
   <!-- Injected companion COUNT -->
   <attribute name="revenue" alias="avg_rev_count" aggregate="countcolumn" />
   ```

3. The injection should happen in the `InjectDateRangeFilter` method or a new `InjectAvgCompanionCounts` method that modifies the FetchXML before splitting into partitions.

4. Alternatively, modify the transpiler to emit both attributes when it detects an AVG aggregate and partitioning is enabled. This keeps FetchXML manipulation in one place.

5. Add test: partition an `AVG(revenue)` query into 2 partitions with known data, verify the merged result uses weighted average (not simple average-of-averages). E.g., partition 1: avg=10, count=100; partition 2: avg=20, count=300 → expected weighted avg = 17.5, not 15.0.

**Commit:**
```
fix(query): inject companion COUNT for weighted AVG in parallel aggregate partitioning

Partitioned aggregate queries with AVG now inject a companion
countcolumn aggregate into each partition's FetchXML so
MergeAggregateNode can compute correct weighted averages
instead of incorrect average-of-averages.
```

---

## Task 12: Add PpdsException error codes to plan nodes

**Why:** Spec § 11.1 and CLAUDE.md ALWAYS rule: "Include ErrorCode in PpdsException — enables programmatic handling." No plan node uses PpdsException. Error codes QUERY_AGGREGATE_LIMIT, QUERY_PLAN_TIMEOUT, QUERY_TYPE_MISMATCH are specified but never defined or used.

**Files:**
- Modify: `src/PPDS.Cli/Infrastructure/Errors/ErrorCodes.cs` (add new error codes)
- Modify: `src/PPDS.Dataverse/Query/Planning/Nodes/ParallelPartitionNode.cs` (wrap partition failures)
- Modify: `src/PPDS.Dataverse/Query/Execution/ExpressionEvaluator.cs` (wrap type errors)
- Create: `tests/PPDS.Dataverse.Tests/Query/Planning/Nodes/ErrorCodeTests.cs`

**Steps:**

1. Add error codes to `ErrorCodes.cs`:
   ```csharp
   public const string AggregateLimitExceeded = "Query.AggregateLimitExceeded";
   public const string PlanTimeout = "Query.PlanTimeout";
   public const string TypeMismatch = "Query.TypeMismatch";
   public const string MemoryLimit = "Query.MemoryLimit";  // if not added by Task 9
   ```

2. In `ParallelPartitionNode.ExecuteAsync`, catch the Dataverse 50K aggregate limit error in partition tasks and wrap it in `PpdsException` with `QUERY_AGGREGATE_LIMIT`:
   ```csharp
   catch (FaultException ex) when (ex.Message.Contains("AggregateQueryRecordLimit"))
   {
       throw new PpdsException(ErrorCodes.Query.AggregateLimitExceeded,
           "Aggregate query exceeded the 50,000 record limit. ...", ex);
   }
   ```

3. In `ExpressionEvaluator`, replace `InvalidOperationException` for type errors with `PpdsException(ErrorCodes.Query.TypeMismatch, ...)` — e.g., in `EvaluateArithmetic` when types are incompatible, and in `NegateValue` for non-numeric types.

4. Add tests verifying the correct error codes are thrown for each scenario.

**Commit:**
```
fix(query): add PpdsException error codes to plan nodes and expression evaluator

Defines QUERY_AGGREGATE_LIMIT, QUERY_PLAN_TIMEOUT, QUERY_TYPE_MISMATCH,
QUERY_MEMORY_LIMIT error codes. Plan nodes and expression evaluator now
throw PpdsException with appropriate codes per CLAUDE.md rules.
```

---

## Task 13: Add progress reporting to ParallelPartitionNode

**Why:** Spec § 11.3 explicitly requires ParallelPartitionNode to report partition completion progress. Task 5 covers DML progress only.

**Files:**
- Modify: `src/PPDS.Dataverse/Query/Planning/Nodes/ParallelPartitionNode.cs`

**Steps:**

1. After each partition task completes, report progress:
   ```csharp
   var completedCount = 0;
   // ... in the finally block of each partition task:
   finally
   {
       semaphore.Release();
       var completed = Interlocked.Increment(ref completedCount);
       context.ProgressReporter?.ReportProgress(
           completed, Partitions.Count,
           $"Partition {completed}/{Partitions.Count} complete");
   }
   ```

2. Report phase at the start:
   ```csharp
   context.ProgressReporter?.ReportPhase("Parallel Aggregation",
       $"Executing {Partitions.Count} partitions across {MaxParallelism} connections");
   ```

3. Verify existing tests still pass (progress calls are optional/no-op when reporter is null).

**Commit:**
```
fix(query): add progress reporting to ParallelPartitionNode

Reports partition completion progress through IQueryProgressReporter.
Enables TUI and CLI feedback during long-running parallel aggregations.
```

---

## Task 14: Fix --dry-run to show execution plan

**Why:** Spec § 8.1 says `--dry-run shows plan + estimated rows without executing`. Current implementation exits before the planner runs, so no plan is generated.

**Files:**
- Modify: `src/PPDS.Cli/Services/Query/SqlQueryService.cs` (ExecuteAsync, move dry-run after planning)

**Steps:**

1. In `ExecuteAsync`, move the dry-run check to AFTER `_planner.Plan(statement)` runs:
   ```csharp
   // Before (current): dry-run returns before planning
   // After: run planner, then return dry-run result WITH plan info

   var planResult = _planner.Plan(statement, planOptions);

   if (request.DmlSafety?.IsDryRun == true)
   {
       return new SqlQueryResult
       {
           // Include plan description so user sees what WOULD execute
           ExecutedFetchXml = planResult.FetchXml,
           // ... existing dry-run result fields
       };
   }
   ```

2. Ensure the plan is NOT executed — only built. The planner is side-effect-free so this is safe.

3. The `DmlSafetyResult.EstimatedAffectedRows` should also be populated if possible (e.g., via the estimated row count from the scan node).

4. Add test: `--dry-run` on a DELETE returns the FetchXML plan but does NOT call the query executor.

**Commit:**
```
fix(query): show execution plan in --dry-run output

--dry-run now runs the query planner before returning, so users
see the FetchXML and execution plan that WOULD execute. The plan
is built but never executed — no data is modified.
```

---

## Task 15: Add pool/parallelism metadata to EXPLAIN output

**Why:** Spec § 7.3 shows EXPLAIN output should include pool capacity, effective parallelism, and estimated execution time. These diagnostic lines are absent.

**Files:**
- Modify: `src/PPDS.Dataverse/Query/Planning/PlanFormatter.cs` (add footer rendering)
- Modify: `src/PPDS.Dataverse/Query/Planning/QueryPlanDescription.cs` (add metadata fields)
- Modify: `src/PPDS.Cli/Services/Query/SqlQueryService.cs` (populate metadata in ExplainAsync)
- Modify: `tests/PPDS.Dataverse.Tests/Query/Planning/PlanFormatterTests.cs` (verify footer)

**Steps:**

1. Add optional metadata properties to `QueryPlanDescription`:
   ```csharp
   public int? PoolCapacity { get; init; }
   public int? EffectiveParallelism { get; init; }
   ```

2. In `PlanFormatter.Format`, append footer lines when metadata is present:
   ```
   Pool capacity: 48 (from connection pool)
   Effective parallelism: 5 (partition count)
   ```

3. In `SqlQueryService.ExplainAsync`, populate these from `QueryPlanOptions.PoolCapacity` and from the plan tree (count ParallelPartitionNode children).

4. Add test: EXPLAIN with aggregate partitioning shows pool capacity and effective parallelism in output.

**Commit:**
```
feat(query): add pool and parallelism metadata to EXPLAIN output

EXPLAIN now shows pool capacity and effective parallelism for
parallel aggregate queries, matching the spec's diagnostic output.
```

---

## Task 16: Fix FetchXML top+page attribute conflict [CRITICAL]

**Why:** The default TUI query `SELECT TOP 100 accountid, name, createdon FROM account` crashes with "The top attribute can't be specified with paging attribute page". Per Microsoft docs, `top` and `page`/`count` are mutually exclusive FetchXML attributes. `FetchXmlScanNode` unconditionally passes `pageNumber=1` to the executor, which adds `page="1"` alongside the transpiler's `top="100"`. Same crash occurs via `ppds query fetch --top 50 --page 2` CLI path.

**Root cause:** The transpiler emits `top` on the `<fetch>` element, but `FetchXmlScanNode` always passes a non-null `pageNumber` to `QueryExecutor.ExecuteFetchXmlAsync`, which unconditionally adds `page` when `pageNumber` is non-null. These two layers don't coordinate on fetch-level attributes.

**How to avoids this:** One way to avoid this is to treat `top` (absolute row limit, no paging) and `count` (page size, with paging) as semantically distinct. When `top` is set, it never adds paging attributes. Row limiting is enforced client-side.

**Files:**
- Modify: `src/PPDS.Dataverse/Query/Planning/Nodes/FetchXmlScanNode.cs`
- Modify: `src/PPDS.Dataverse/Query/QueryExecutor.cs`
- Modify: `tests/PPDS.Dataverse.Tests/Query/Planning/Nodes/FetchXmlScanNodeTests.cs`

**Steps:**

1. In `FetchXmlScanNode`, add a private `_effectiveFetchXml` field computed in the constructor via a static `PrepareFetchXmlForExecution(string fetchXml)` method:
   - Parse FetchXML, check for `top` attribute on `<fetch>`
   - If present: remove `top`, set `count = min(topValue, 5000)` (Dataverse max page size)
   - If absent: return unchanged
   - Row limiting is already handled client-side by existing `MaxRows` logic (line 99-101)
   - Keep `FetchXml` property unchanged for EXPLAIN/logging

2. In `ExecuteAsync`, use `_effectiveFetchXml` instead of `FetchXml` when calling the executor:
   ```csharp
   var result = await context.QueryExecutor.ExecuteFetchXmlAsync(
       _effectiveFetchXml,  // was: FetchXml
       pageNumber, pagingCookie, ...);
   ```

3. In `QueryExecutor.ExecuteFetchXmlAsync` (lines 64-73), add a defensive guard before adding paging attributes:
   ```csharp
   // Resolve top/page conflict: Dataverse rejects top + page together.
   var topAttr = fetchElement.Attribute("top");
   if (topAttr != null && (pageNumber.HasValue || !string.IsNullOrEmpty(pagingCookie)))
   {
       if (int.TryParse(topAttr.Value, out var topInt))
       {
           topAttr.Remove();
           fetchElement.SetAttributeValue("count", Math.Min(topInt, 5000).ToString());
       }
   }
   ```
   This protects direct callers (FetchCommand, MCP, RPC) that bypass the plan-based path.

4. Add tests to `FetchXmlScanNodeTests.cs`:
   - `TopN_ConvertsToPaging` — `top="100"` → executor receives FetchXML with `count="100"`, no `top`, `page="1"`
   - `TopN_CapsCountAt5000` — `top="7000"` → `count="5000"`, auto-pages 2 pages, maxRows stops at 7000
   - `NoTop_Unchanged` — queries without `top` pass FetchXML unchanged

**Edge cases:**
| Scenario | Behavior |
|---|---|
| `SELECT TOP 100 ...` | `count="100"`, maxRows=100 — 1 page, ≤100 rows |
| `SELECT TOP 7000 ...` | `count="5000"`, auto-pages 2 pages, maxRows caps at 7000 |
| `SELECT ...` (no TOP) | Unchanged — default Dataverse behavior |
| `ppds query fetch --top 50 --page 2` | Guard converts `top`→`count="50"`, `page="2"` works |

**Commit:**
```
fix(query): resolve FetchXML top+page attribute conflict

Dataverse rejects FetchXML with both `top` and `page` attributes.
FetchXmlScanNode now converts `top` to `count` (page size) before
execution, relying on MaxRows for client-side row limiting.
QueryExecutor adds a defensive guard for direct FetchXML callers.
```

---

## Task Dependency Graph

```
[Task 1: DI wiring] ──────┐
[Task 2: metadata prefix] ─┤── Independent, do first (critical)
[Task 11: AVG merge fix] ──┤
[Task 16: top+page fix] ───┤
                            │
[Task 3: row cap] ─────────┤
[Task 4: streaming expand] ┤── Depend on Task 1 (SqlQueryService changes)
[Task 5: progress reporter] ┤
[Task 14: dry-run fix] ────┤
                            │
[Task 6: semaphore dispose] ┤
[Task 7: INSERT ordinal] ──┤
[Task 8: prefetch integrate]┤── Independent of each other, can parallel
[Task 9: memory bounds] ───┤
[Task 10: explain tests] ──┤
[Task 12: error codes] ────┤
[Task 13: partition progress]┤
[Task 15: explain metadata] ┘
```

**Execution order:**
1. Tasks 1 + 2 + 11 + 16 first (critical fixes, all independent)
2. Tasks 3 + 4 + 5 + 14 next (depend on Task 1's SqlQueryService changes)
3. Tasks 6 + 7 + 8 + 9 + 10 + 12 + 13 + 15 in parallel (all independent)

**Review checkpoint:** After Tasks 1-5 + 11 + 14, run full test suite and verify metadata + DML + AVG partitioning work end-to-end. Then proceed with the remaining tasks.
