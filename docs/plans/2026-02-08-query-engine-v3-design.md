# PPDS Query Engine v3 — Full Design

**Date:** 2026-02-08
**Status:** Draft — awaiting approval
**Goal:** Build a production-grade, standalone SQL query engine for Dataverse that replaces all external SQL tooling dependencies with a superior alternative.

---

## 1. Vision

PPDS Query Engine v3 is a **clean-room, ground-up evolution** of the PPDS SQL query system. It becomes:

1. A **reusable .NET library** (PPDS.Query) with ADO.NET provider — any .NET application can query Dataverse via SQL
2. The **single execution engine** for all PPDS interfaces (CLI, TUI, RPC, VS Code)
3. **Performance-optimized** with cost-based query planning, dynamic parallelism, and streaming execution
4. **Feature-complete** with full T-SQL language support (CTEs, window functions, JSON, temp tables, control flow)

---

## 2. Current State Assessment

### What We Have (Solid Foundation)

| Capability | Status | Files |
|------------|--------|-------|
| Custom SQL parser (lexer + recursive descent) | Complete — to be replaced | `Sql/Parsing/SqlLexer.cs`, `SqlParser.cs`, 25 AST classes |
| FetchXML transpiler | Complete | `Sql/Transpilation/SqlToFetchXmlTranspiler.cs` |
| Query planner with 13 node types | Complete | `Query/Planning/` |
| Streaming execution (IAsyncEnumerable) | Complete | `PlanExecutor.cs` |
| Prefetch optimization | Complete | `PrefetchScanNode.cs` |
| TDS Endpoint routing | Complete | `TdsScanNode.cs`, `TdsCompatibilityChecker.cs` |
| DML with bulk APIs + safety guards | Complete | `DmlExecuteNode.cs`, `DmlSafetyGuard.cs` |
| Virtual column expansion | Complete | `SqlQueryResultExpander.cs` |
| IntelliSense (completions + validation + highlighting) | Complete | `Intellisense/` |
| Metadata queries (6 virtual tables) | Complete | `MetadataScanNode.cs`, `MetadataTableDefinitions.cs` |
| Window functions (8 functions) | Complete (no frame support) | `ClientWindowNode.cs` |
| Expression evaluator (24 functions) | Complete | `ExpressionEvaluator.cs`, `StringFunctions.cs`, `DateFunctions.cs` |
| Parallel aggregation | Complete (fixed pool) | `ParallelPartitionNode.cs` |
| TUI SQL screen | Complete (914 lines) | `SqlQueryScreen.cs` |
| Query history | Complete | `QueryHistoryService.cs` |
| Export (CSV, JSON, Excel) | Complete | `ExportService.cs` |
| Elastic table bulk operations | Complete | `BulkOperationExecutor.cs` |
| 80+ test files | Complete | Various |

### What We Need

| Gap | Priority | Effort |
|-----|----------|--------|
| TSql160Parser integration (replaces custom parser) | P0 | Large |
| PPDS.Query library extraction | P0 | Large |
| ADO.NET provider (PpdsDbConnection) | P0 | Large |
| CTEs (non-recursive + recursive) | P1 | Medium |
| Temp tables (#temp) | P1 | Medium |
| Control flow (IF/ELSE, WHILE, TRY/CATCH) | P1 | Medium |
| Cost-based query optimizer | P1 | Large |
| Join strategy selection (Nested Loop / Merge / Hash) | P1 | Large |
| Dynamic parallelism (thread scaling) | P1 | Medium |
| Full string function coverage (~20 more functions) | P2 | Small |
| Full date function coverage (~10 more functions) | P2 | Small |
| Full math function coverage (~15 more functions) | P2 | Small |
| JSON functions (JSON_VALUE, OPENJSON, etc.) | P2 | Medium |
| STRING_SPLIT table-valued function | P2 | Small |
| STDEV, VAR, STRING_AGG aggregates | P2 | Small |
| INTERSECT / EXCEPT set operations | P2 | Small |
| MERGE statement | P2 | Medium |
| OFFSET/FETCH paging syntax | P2 | Small |
| Window frame support (ROWS BETWEEN) | P2 | Medium |
| Additional window functions (LAG, LEAD, NTILE, FIRST_VALUE, LAST_VALUE) | P2 | Medium |
| Query plan visualization (TUI) | P3 | Medium |
| Multi-tab query editor (TUI) | P3 | Medium |
| Result set comparison (TUI) | P3 | Medium |
| Cursor support | P3 | Medium |
| EXECUTE message (stored proc-like) | P3 | Medium |
| Impersonation (EXECUTE AS / REVERT) | P3 | Small |
| CREATEELASTICLOOKUP() | P3 | Small |
| Multi-org support | P3 | Large |

---

## 3. Architecture

### 3.1 Project Structure

```
src/
├── PPDS.Query/                          # NEW — Core query engine library (NuGet package)
│   ├── Parsing/
│   │   └── QueryParser.cs               # Wraps TSql160Parser, produces TSqlFragment AST
│   ├── Visitors/                         # AST transformation visitors
│   │   ├── NormalizeColumnNamesVisitor.cs
│   │   ├── BooleanRewriteVisitor.cs
│   │   ├── CteExpansionVisitor.cs
│   │   ├── WindowFunctionVisitor.cs
│   │   ├── TdsCompatibilityVisitor.cs
│   │   └── ...
│   ├── Planning/
│   │   ├── ExecutionPlanBuilder.cs       # AST → Plan node tree
│   │   ├── ExecutionPlanOptimizer.cs     # Cost-based optimization
│   │   ├── CostEstimator.cs             # Cardinality & cost estimation
│   │   └── Nodes/                        # All execution plan nodes
│   │       ├── IQueryPlanNode.cs
│   │       ├── FetchXmlScanNode.cs
│   │       ├── TdsScanNode.cs
│   │       ├── PrefetchScanNode.cs
│   │       ├── NestedLoopJoinNode.cs
│   │       ├── MergeJoinNode.cs
│   │       ├── HashJoinNode.cs
│   │       ├── HashMatchAggregateNode.cs
│   │       ├── StreamAggregateNode.cs
│   │       ├── PartitionedAggregateNode.cs
│   │       ├── WindowSpoolNode.cs
│   │       ├── FilterNode.cs
│   │       ├── SortNode.cs
│   │       ├── TopNode.cs
│   │       ├── OffsetFetchNode.cs
│   │       ├── DistinctNode.cs
│   │       ├── ProjectNode.cs
│   │       ├── ConcatenateNode.cs        # UNION/INTERSECT/EXCEPT
│   │       ├── DmlExecuteNode.cs
│   │       ├── MetadataScanNode.cs
│   │       ├── TempTableScanNode.cs
│   │       ├── ScriptExecutionNode.cs
│   │       ├── ConditionalNode.cs        # IF/ELSE
│   │       ├── WhileNode.cs
│   │       ├── TryCatchNode.cs
│   │       ├── DeclareVariablesNode.cs
│   │       ├── AssignVariablesNode.cs
│   │       ├── StringSplitNode.cs
│   │       ├── OpenJsonNode.cs
│   │       ├── CursorNodes.cs
│   │       └── ExecuteMessageNode.cs
│   ├── Execution/
│   │   ├── PlanExecutor.cs               # Plan consumer (streaming + collected)
│   │   ├── ExpressionCompiler.cs         # SQL expr → .NET delegate compilation
│   │   ├── DynamicParallel.cs            # Dynamic thread scaling
│   │   └── SessionContext.cs             # Variables, temp tables, cursor state
│   ├── Functions/
│   │   ├── FunctionRegistry.cs
│   │   ├── StringFunctions.cs            # ~30 functions
│   │   ├── DateFunctions.cs              # ~20 functions
│   │   ├── MathFunctions.cs              # ~15 functions
│   │   ├── JsonFunctions.cs              # JSON_VALUE, JSON_QUERY, etc.
│   │   └── CastConverter.cs
│   ├── Transpilation/
│   │   ├── FetchXmlGenerator.cs          # AST → FetchXML
│   │   └── FetchXmlConditionMapper.cs    # SQL operators → FetchXML operators
│   ├── Types/
│   │   ├── QueryResult.cs
│   │   ├── QueryRow.cs
│   │   ├── QueryColumn.cs
│   │   ├── QueryValue.cs
│   │   └── SqlTypeMapper.cs             # SQL types ↔ .NET types ↔ Dataverse types
│   ├── Provider/                         # ADO.NET Provider
│   │   ├── PpdsDbConnection.cs
│   │   ├── PpdsDbCommand.cs
│   │   ├── PpdsDataReader.cs
│   │   ├── PpdsDbParameter.cs
│   │   └── PpdsConnectionStringBuilder.cs
│   └── Safety/
│       └── DmlSafetyGuard.cs
│
├── PPDS.Query.Tests/                     # NEW — Engine tests
│
├── PPDS.Dataverse/                       # EXISTING — Dataverse I/O layer
│   ├── IQueryExecutor.cs                 # FetchXML execution interface
│   ├── ITdsQueryExecutor.cs              # TDS execution interface
│   └── IDataverseConnectionPool.cs       # Connection pooling
│
├── PPDS.Cli/                             # EXISTING — CLI + TUI + RPC
│   ├── Services/
│   │   ├── Query/
│   │   │   ├── ISqlQueryService.cs       # KEEP — Application Service interface
│   │   │   ├── SqlQueryService.cs        # UPDATED — delegates to PPDS.Query
│   │   │   ├── SqlQueryResultExpander.cs # KEEP — virtual column expansion
│   │   │   └── DmlSafetyGuard.cs        # MOVED to PPDS.Query
│   │   └── SqlLanguageService.cs         # UPDATED — uses ScriptDom tokens
│   ├── Tui/Screens/
│   │   ├── SqlQueryScreen.cs             # UPDATED — multi-tab, plan viz
│   │   └── QueryResultsTableView.cs
│   └── Intellisense/
│       ├── SqlSourceTokenizer.cs          # REWRITTEN — wraps ScriptDom
│       ├── SqlCompletionEngine.cs         # REWRITTEN — uses TSqlFragment
│       └── SqlCursorContext.cs            # REWRITTEN — uses TSqlFragment
```

### 3.2 Code to Delete (Dead Code Cleanup)

All of the following are replaced by TSql160Parser + PPDS.Query:

```
DELETE: src/PPDS.Dataverse/Sql/Parsing/SqlLexer.cs
DELETE: src/PPDS.Dataverse/Sql/Parsing/SqlParser.cs
DELETE: src/PPDS.Dataverse/Sql/Parsing/SqlToken.cs
DELETE: src/PPDS.Dataverse/Sql/Parsing/SqlComment.cs
DELETE: All 25 AST node classes in Sql/Parsing/ (SqlSelectStatement, SqlInsertStatement, etc.)
DELETE: src/PPDS.Dataverse/Sql/Transpilation/SqlToFetchXmlTranspiler.cs (replaced by FetchXmlGenerator)
DELETE: Old parser test files (replaced by new tests against TSql160Parser)
```

### 3.3 Dependency Graph

```
PPDS.Query
  ├── Microsoft.SqlServer.TransactSql.ScriptDom (TSql160Parser)
  ├── PPDS.Dataverse (IQueryExecutor, ITdsQueryExecutor, IDataverseConnectionPool)
  └── System.Data.Common (ADO.NET base classes)

PPDS.Cli
  ├── PPDS.Query
  └── PPDS.Dataverse

External Consumers
  └── PPDS.Query (NuGet package)
```

---

## 4. Key Design Decisions

### 4.1 Parser: TSql160Parser from Microsoft.SqlServer.TransactSql.ScriptDom

**Why:** Complete T-SQL grammar coverage with zero maintenance. Microsoft maintains it. Gives us CTEs, window functions, JSON, cursors, temp tables, and every future T-SQL feature for free.

**How it works:**
```csharp
public sealed class QueryParser
{
    private readonly TSql160Parser _parser = new(initialQuotedIdentifiers: true);

    public TSqlFragment Parse(string sql)
    {
        var result = _parser.Parse(new StringReader(sql), out var errors);
        if (errors.Any())
            throw new PpdsException(ErrorCodes.Query.ParseError, FormatErrors(errors));
        return result;
    }
}
```

**Impact on IntelliSense:**
- `SqlSourceTokenizer` rewrites to use ScriptDom's `TSqlTokenType` enum for syntax highlighting
- `SqlCompletionEngine` rewrites to walk `TSqlFragment` AST for context detection
- `SqlCursorContext` rewrites to use ScriptDom position tracking
- All three become simpler because the parser handles the hard parts

### 4.2 Execution Plan Builder: Visitor-Based AST → Plan Nodes

The ExecutionPlanBuilder walks the TSqlFragment AST via visitor pattern and constructs our execution plan node tree:

```
TSqlFragment (from parser)
    ↓ Visitor transforms (normalize, expand, validate)
    ↓ ExecutionPlanBuilder.Build()
    ↓
QueryPlanResult { RootNode: IQueryPlanNode }
    ↓ ExecutionPlanOptimizer.Optimize()
    ↓
Optimized QueryPlanResult
    ↓ PlanExecutor.ExecuteStreamingAsync() or ExecuteAsync()
    ↓
Results
```

### 4.3 Cost-Based Query Optimizer

```csharp
public sealed class ExecutionPlanOptimizer
{
    public QueryPlanResult Optimize(QueryPlanResult plan, IMetadataProvider metadata)
    {
        // 1. Predicate pushdown — move WHERE conditions into FetchXML where possible
        // 2. Join reordering — choose optimal join order based on cardinality
        // 3. Join strategy — select Nested Loop / Merge / Hash per join
        // 4. Aggregate strategy — select Hash / Stream / Partitioned per aggregate
        // 5. Sort elimination — remove sorts when data already ordered
        // 6. Constant folding — evaluate constant expressions at plan time
        return optimizedPlan;
    }
}
```

**Cardinality estimation** uses:
- Entity record count (from metadata or RetrieveTotalRecordCountRequest)
- Filter selectivity heuristics (equality = 10%, range = 33%, LIKE = 25%)
- Join cardinality = outer × inner × selectivity

### 4.4 Dynamic Parallelism

Replace fixed-pool `ParallelPartitionNode` with dynamic thread scaling:

```csharp
public sealed class DynamicParallel<T>
{
    private readonly ConcurrentQueue<T> _workQueue;
    private int _activeThreads;
    private readonly int _maxDop;

    // Monitor loop (every 1 second):
    // - If all threads busy + pending work → add thread (up to MaxDOP)
    // - If threads idle → remove thread (down to 1)
    // - Adapts to workload dynamically
}
```

Used by: PartitionedAggregateNode, DML batch execution, parallel partition scans.

### 4.5 ADO.NET Provider

```csharp
public sealed class PpdsDbConnection : DbConnection
{
    public int BatchSize { get; set; } = 100;
    public int MaxDegreeOfParallelism { get; set; } = 5;
    public bool UseTdsEndpoint { get; set; }
    public bool BlockUpdateWithoutWhere { get; set; } = true;
    public bool BlockDeleteWithoutWhere { get; set; } = true;

    // Events for DML confirmation, progress, etc.
    public event EventHandler<DmlConfirmationEventArgs> PreInsert;
    public event EventHandler<DmlConfirmationEventArgs> PreUpdate;
    public event EventHandler<DmlConfirmationEventArgs> PreDelete;
    public event EventHandler<ProgressEventArgs> Progress;
}
```

**Connection string format:**
```
Url=https://org.crm.dynamics.com;AuthType=OAuth;ClientId=...;
```

### 4.6 Session Context

Manages state that lives across statements in a batch:

```csharp
public sealed class SessionContext
{
    public Dictionary<string, object> Variables { get; }     // @var declarations
    public Dictionary<string, DataTable> TempTables { get; } // #temp tables
    public Dictionary<string, CursorState> Cursors { get; }  // DECLARE CURSOR
    public int FetchStatus { get; set; }                     // @@FETCH_STATUS
    public int ErrorNumber { get; set; }                     // @@ERROR
}
```

---

## 5. Feature Specifications

### 5.1 CTEs (Common Table Expressions)

```sql
-- Non-recursive
WITH active_accounts AS (
    SELECT accountid, name FROM account WHERE statecode = 0
)
SELECT * FROM active_accounts WHERE name LIKE 'C%'

-- Recursive
WITH hierarchy AS (
    SELECT accountid, name, parentaccountid, 0 as level
    FROM account WHERE parentaccountid IS NULL
    UNION ALL
    SELECT a.accountid, a.name, a.parentaccountid, h.level + 1
    FROM account a INNER JOIN hierarchy h ON a.parentaccountid = h.accountid
)
SELECT * FROM hierarchy
```

**Implementation:**
1. CteExpansionVisitor identifies WITH clause
2. Non-recursive: Replace CTE references with inline subqueries (AliasNode)
3. Recursive: Anchor query → loop (re-execute recursive part until no new rows) → combine

### 5.2 Temp Tables

```sql
CREATE TABLE #staging (id UNIQUEIDENTIFIER, name NVARCHAR(MAX))
INSERT INTO #staging SELECT accountid, name FROM account WHERE statecode = 0
SELECT * FROM #staging WHERE name LIKE 'C%'
DROP TABLE #staging
```

**Implementation:**
- CreateTableNode: Validates schema, creates in-memory DataTable in SessionContext
- TempTableScanNode: Reads from SessionContext.TempTables
- InsertNode: Writes to temp table via standard DML path
- DropTableNode: Removes from SessionContext
- Scope: Session-level (dropped when connection closes)

### 5.3 Control Flow

```sql
DECLARE @count INT
SELECT @count = COUNT(*) FROM account WHERE statecode = 0

IF @count > 1000
BEGIN
    SELECT 'Large org' as category, @count as total
END
ELSE
BEGIN
    SELECT 'Small org' as category, @count as total
END

-- WHILE loop
DECLARE @page INT = 1
WHILE @page <= 5
BEGIN
    SELECT * FROM account ORDER BY name OFFSET (@page - 1) * 100 ROWS FETCH NEXT 100 ROWS ONLY
    SET @page = @page + 1
END

-- TRY/CATCH
BEGIN TRY
    UPDATE account SET name = NULL WHERE accountid = @id
END TRY
BEGIN CATCH
    SELECT ERROR_MESSAGE() as error
END CATCH
```

### 5.4 JSON Functions

```sql
-- Extract scalar value from JSON column
SELECT JSON_VALUE(customfield, '$.address.city') FROM account

-- Shred JSON array into rows
SELECT j.* FROM account
CROSS APPLY OPENJSON(jsoncolumn) WITH (id INT, name NVARCHAR(100)) j

-- Check path exists
SELECT * FROM account WHERE JSON_PATH_EXISTS(customfield, '$.email') = 1
```

### 5.5 STRING_SPLIT

```sql
SELECT value FROM STRING_SPLIT('red,green,blue', ',')
-- Returns: red | green | blue (3 rows)

-- Common pattern: filter by list
SELECT * FROM account WHERE name IN (SELECT value FROM STRING_SPLIT(@nameList, ','))
```

### 5.6 Additional Functions to Add

**String (missing ~20):**
STUFF, REPLICATE, PATINDEX, CONCAT_WS, FORMAT, SPACE, UNICODE, CHAR, COLLATE, STRING_AGG, LEFT (if missing), RIGHT (if missing)

**Date (missing ~10):**
DATEFROMPARTS, DATETIMEFROMPARTS, EOMONTH, DATENAME, SYSDATETIME, TIMEFROMPARTS

**Math (missing ~15):**
POWER, LOG, LOG10, SQRT, EXP, SIN, COS, TAN, ASIN, ACOS, ATAN, ATAN2, DEGREES, RADIANS, RAND, PI, SQUARE, SIGN

**Aggregate (missing 3):**
STDEV, VAR, STRING_AGG

### 5.7 Join Strategy Selection

```
             ┌────────────────────────────────┐
             │  Choose Join Strategy           │
             │  (CostEstimator input)          │
             └────────────────┬───────────────┘
                              │
              ┌───────────────┼───────────────┐
              ↓               ↓               ↓
    ┌─────────────┐  ┌──────────────┐  ┌────────────┐
    │ Nested Loop  │  │ Merge Join   │  │ Hash Join  │
    │              │  │              │  │            │
    │ When:        │  │ When:        │  │ When:      │
    │ - Small      │  │ - Both       │  │ - Large    │
    │   inner set  │  │   inputs     │  │   unsorted │
    │ - Correlated │  │   sorted     │  │   inputs   │
    │   subquery   │  │ - Multi-col  │  │ - Mismatched│
    │ - Index      │  │   equijoin   │  │   cardinality│
    │   available  │  │              │  │            │
    └─────────────┘  └──────────────┘  └────────────┘
```

### 5.8 Window Frame Support

Extend `ClientWindowNode` (rename to `WindowSpoolNode`) to support:

```sql
-- Running total
SUM(revenue) OVER (ORDER BY createdon ROWS UNBOUNDED PRECEDING)

-- Moving average (3-row window)
AVG(revenue) OVER (ORDER BY createdon ROWS BETWEEN 1 PRECEDING AND 1 FOLLOWING)

-- Frame types
ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW     -- running
ROWS BETWEEN n PRECEDING AND n FOLLOWING              -- sliding
RANGE BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW    -- range-based
```

### 5.9 Additional Window Functions

| Function | Description |
|----------|-------------|
| LAG(col, offset, default) | Value from N rows before current |
| LEAD(col, offset, default) | Value from N rows after current |
| NTILE(n) | Distribute rows into N groups |
| FIRST_VALUE(col) | First value in window frame |
| LAST_VALUE(col) | Last value in window frame |
| CUME_DIST() | Cumulative distribution |
| PERCENT_RANK() | Percent rank |

---

## 6. TUI UX Enhancements

### 6.1 Query Plan Visualization

**Trigger:** Ctrl+Shift+P (or add to existing Ctrl+Shift+F dialog)

```
┌─ Execution Plan ────────────────────────────────────────────┐
│                                                              │
│  ProjectNode (3 cols)                    0.1ms    100 rows   │
│  └─ FilterNode (statecode = 0)          0.2ms    100 rows   │
│     └─ FetchXmlScanNode (account)       45ms     5432 rows  │
│        [FetchXML: <fetch>...]                                │
│        [Strategy: Cookie-based paging]                       │
│        [Pages: 2]                                            │
│                                                              │
│  Total: 45.3ms | Rows scanned: 5432 | Rows returned: 100   │
│                                                              │
└──────────────────────────────────────────────────────────────┘
```

Shows:
- Node tree with nesting
- Per-node execution time
- Per-node row counts (estimated vs actual)
- FetchXML at leaf nodes
- Total cost summary

### 6.2 Multi-Tab Query Editor

```
┌─ [Tab 1: accounts] [Tab 2: contacts] [+] ─────────────────┐
│ SELECT name FROM account WHERE statecode = 0               │
├────────────────────────────────────────────────────────────┤
│ Results for Tab 1                                          │
│ ┌──────────┬──────────────────┐                           │
│ │ name     │ statecode        │                           │
│ ├──────────┼──────────────────┤                           │
│ │ Contoso  │ Active           │                           │
│ └──────────┴──────────────────┘                           │
└────────────────────────────────────────────────────────────┘
```

- Ctrl+T: New tab
- Ctrl+W: Close current tab
- Ctrl+Tab / Ctrl+Shift+Tab: Switch tabs
- Each tab has independent: query text, results, history position, execution state
- Tab title auto-generated from first entity in FROM clause

### 6.3 Result Set Comparison

**Trigger:** Ctrl+Shift+C (compare mode)

```
┌─ Compare Results ──────────────────────────────────────────┐
│ Query A: SELECT name FROM account (Tab 1)                  │
│ Query B: SELECT name FROM account (Tab 2)                  │
│                                                             │
│ Summary: 150 in A only | 200 in B only | 5000 in both     │
│                                                             │
│ ┌─ Only in A ──────────────┬─ Only in B ─────────────────┐│
│ │ Contoso Corp             │ Adventure Works              ││
│ │ Fabrikam Inc             │ Northwind Traders            ││
│ │ ...                      │ ...                          ││
│ └──────────────────────────┴──────────────────────────────┘│
└─────────────────────────────────────────────────────────────┘
```

---

## 7. TDE/Elastic Table Support

**Current state:** Bulk operations fully support elastic tables. Query support routes through FetchXML (not TDS). No CREATEELASTICLOOKUP function.

**v3 additions (match external tooling parity):**
1. Add `CREATEELASTICLOOKUP(entity, logicalname, id, partitionid)` function
2. Ensure elastic table detection works in metadata queries
3. No special query routing changes needed (FetchXML works)

---

## 8. Execution Strategy

### 8.1 Orchestrator Pattern

One orchestrator session manages the entire effort:
- Sequences phases that have dependencies
- Parallelizes independent workstreams within phases
- Triggers code reviews at phase gates
- Ensures all tests + analyzers pass before proceeding

### 8.2 Phases & Parallelization

```
Phase 1: Foundation [SEQUENTIAL — everything depends on this]
  ├─ 1a. Create PPDS.Query project + move existing engine code
  ├─ 1b. Add Microsoft.SqlServer.TransactSql.ScriptDom dependency
  ├─ 1c. Build QueryParser wrapper around TSql160Parser
  ├─ 1d. Build ExecutionPlanBuilder (visitor → plan nodes)
  ├─ 1e. Rewrite FetchXmlGenerator against TSqlFragment AST
  ├─ 1f. Rewrite IntelliSense (tokenizer, completions, cursor context)
  ├─ 1g. Wire ISqlQueryService to PPDS.Query
  ├─ 1h. Delete old parser, AST classes, transpiler
  ├─ 1i. All existing tests rewritten + passing
  └─ GATE: Build succeeds, all tests pass, all analyzers clean

Phase 2-4: [PARALLEL STREAMS after Phase 1]

  Stream A: SQL Language Expansion (Phase 2)
    ├─ 2a. CTEs (non-recursive + recursive)
    ├─ 2b. Temp tables (#temp)
    ├─ 2c. Control flow (IF/ELSE, WHILE, DECLARE/SET)
    ├─ 2d. TRY/CATCH error handling
    ├─ 2e. OFFSET/FETCH paging
    ├─ 2f. INTERSECT / EXCEPT
    └─ 2g. MERGE statement

  Stream B: Functions & Expressions (Phase 3)
    ├─ 3a. String functions (~20 new)
    ├─ 3b. Date functions (~10 new)
    ├─ 3c. Math functions (~15 new)
    ├─ 3d. JSON functions (JSON_VALUE, OPENJSON, etc.)
    ├─ 3e. STRING_SPLIT table-valued function
    ├─ 3f. Aggregate additions (STDEV, VAR, STRING_AGG)
    └─ 3g. CAST/CONVERT with style codes, TRY_CONVERT

  Stream C: Query Optimization (Phase 4)
    ├─ 4a. CostEstimator (cardinality estimation)
    ├─ 4b. ExecutionPlanOptimizer (predicate pushdown, sort elimination)
    ├─ 4c. Join strategy selection (Nested Loop / Merge / Hash)
    ├─ 4d. Dynamic parallelism (DynamicParallel)
    ├─ 4e. Aggregate strategy selection (Hash / Stream / Partitioned)
    └─ 4f. Window frame support + additional window functions

  └─ GATE: All streams complete, all tests pass, all analyzers clean

Phase 5: ADO.NET Provider [SEQUENTIAL — depends on stable engine]
  ├─ 5a. PpdsDbConnection
  ├─ 5b. PpdsDbCommand + PpdsDbParameter
  ├─ 5c. PpdsDataReader (streaming)
  ├─ 5d. PpdsConnectionStringBuilder
  ├─ 5e. Integration tests
  └─ GATE: Provider tests pass, can use from LINQPad/external app

Phase 6: Advanced Features [PARALLEL STREAMS]
  ├─ 6a. Metadata query enhancements
  ├─ 6b. Cursor support (DECLARE/OPEN/FETCH/CLOSE/DEALLOCATE)
  ├─ 6c. EXECUTE message (Dataverse message execution)
  ├─ 6d. Impersonation (EXECUTE AS / REVERT)
  ├─ 6e. CREATEELASTICLOOKUP()
  └─ GATE: All tests pass, all analyzers clean

Phase 7: TUI UX Enhancements [PARALLEL STREAMS]
  ├─ 7a. Query plan visualization
  ├─ 7b. Multi-tab query editor
  └─ 7c. Result set comparison
  └─ GATE: TUI tests pass, manual verification
```

### 8.3 Quality Gates (Every Merge)

1. `dotnet build` — zero errors
2. `dotnet test --filter Category!=Integration` — all pass
3. `dotnet test --filter Category=TuiUnit` — all pass
4. Analyzers — zero new warnings
5. No dead code — old implementations deleted when replaced
6. No half implementations — feature is complete with tests or doesn't merge
7. Code review via `superpowers:requesting-code-review` at phase gates

### 8.4 Clean Room Rules

- No source code from external projects
- No mentions of external projects in source code, comments, or commit messages
- Design from public Microsoft documentation:
  - [T-SQL Reference](https://learn.microsoft.com/en-us/sql/t-sql/language-reference)
  - [FetchXML Reference](https://learn.microsoft.com/en-us/power-apps/developer/data-platform/use-fetchxml-construct-query)
  - [Dataverse SDK](https://learn.microsoft.com/en-us/power-apps/developer/data-platform/org-service/overview)
  - [Dataverse TDS Endpoint](https://learn.microsoft.com/en-us/power-apps/maker/data-platform/dataverse-sql-query)
- Microsoft.SqlServer.TransactSql.ScriptDom is a first-party Microsoft NuGet package

---

## 9. Testing Strategy

### 9.1 Test Categories

| Category | Scope | Trait | Run Command |
|----------|-------|-------|-------------|
| Parser | TSql160Parser wrapper, error formatting | `Category=Unit` | `dotnet test --filter Category=Unit` |
| Plan Builder | AST → Plan node construction | `Category=Unit` | Same |
| Plan Optimizer | Cost estimation, optimization passes | `Category=Unit` | Same |
| Plan Execution | Node execution, streaming, results | `Category=Unit` | Same |
| Functions | All 80+ scalar/aggregate functions | `Category=Unit` | Same |
| Transpilation | FetchXML generation from AST | `Category=Unit` | Same |
| ADO.NET | PpdsDbConnection, PpdsDbCommand, reader | `Category=Unit` | Same |
| IntelliSense | Tokenizer, completions, validation | `Category=Unit` | Same |
| TUI | SqlQueryScreen, multi-tab, plan viz | `Category=TuiUnit` | `dotnet test --filter Category=TuiUnit` |
| DML Safety | Safety guards, row caps, confirmations | `Category=Unit` | Same |
| Integration | Live Dataverse queries | `Category=Integration` | `dotnet test --filter Category=Integration` |

### 9.2 Test Infrastructure

- Continue using `TempProfileStore` + `MockServiceProviderFactory`
- Mock `IQueryExecutor` and `ITdsQueryExecutor` for unit tests
- Mock `IMetadataProvider` for optimizer tests
- Inline test doubles preferred
- Multi-target: net8.0, net9.0, net10.0

---

## 10. Performance Targets

| Scenario | Target | Baseline |
|----------|--------|----------|
| Parse + Plan (simple SELECT) | <5ms | Current: ~15ms (lex + parse + transpile) |
| Parse + Plan (complex CTE + JOIN) | <20ms | N/A (not supported yet) |
| FetchXML generation | <2ms | Current: ~10ms |
| Streaming first chunk (100 rows) | <200ms | Current: ~200ms |
| Aggregate with partitioning (1M records) | <30s | Current: ~45s |
| DML batch (10K inserts) | <60s | Current: ~60s |
| IntelliSense response | <100ms | Current: ~100ms |

---

## 11. Risk Assessment

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| TSql160Parser doesn't expose token positions needed for IntelliSense | Low | High | ScriptDom includes token position info; validate in Phase 1 spike |
| Performance regression from parser swap | Low | Medium | Benchmark before/after; TSql160Parser is highly optimized |
| Scope creep from "full T-SQL parity" | Medium | High | Strict phase gates; only implement what's in this doc |
| PPDS.Query extraction breaks existing wiring | Medium | Medium | Phase 1 is sequential; all tests must pass before proceeding |
| Dynamic parallelism introduces race conditions | Medium | Medium | Extensive concurrent tests; bounded channels; immutable data |

---

## 12. Open Questions

1. **NuGet package naming:** `PPDS.Query` or `PowerPlatformDeveloperSuite.Query`?
2. **Multi-org support:** Include in Phase 6 or defer to v4?
3. **FetchXML Builder integration:** Should the TUI link to external FetchXML tools for complex filter building?
4. **Telemetry:** Should the engine collect anonymized query pattern statistics for optimization tuning?
