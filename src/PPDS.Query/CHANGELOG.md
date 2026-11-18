# Changelog - PPDS.Query

All notable changes to PPDS.Query will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [1.1.0] - Unreleased

### Changed

- Aligned with PPDS.Dataverse 1.5.0 and its refreshed Microsoft.Extensions/JWT dependency graph in the coordinated release (#1501, #1502).
- Updated SourceLink build tooling to 10.0.403 (#1498). Notebook/RPC target-isolation fixes are in CLI/Extension.
- **Breaking:** the planner refuses INSERT, UPDATE and DELETE shapes that are not on the DML allowlist (`specs/query.md`, "DML allowlist") with `DmlSafetyRefusalException` (`QueryErrorCode.DmlBlocked`), so explain, scripts and the ADO.NET provider cannot plan them; MERGE, EXECUTE AS and REVERT are refused; a row limit, page number or paging cookie in `QueryPlanOptions` is refused for DML; SQL with more than one batch (`GO`) is a `QueryParseException`. DML target and column names are planned as lower-case logical names (#1698).
- INSERT ... SELECT is planned from the parsed statement, so its WHERE and ORDER BY are applied (they were dropped) (#1698).

### Fixed

- SELECT returns what its text says or fails with a clear error: `IN (subquery)`/`EXISTS` inside OR, parentheses or NOT no longer overflow the stack; `NOT (...)`, `= NULL`, `<> NULL` and variables in a pushed-down WHERE are errors (they selected wrong rows); `<>`, `NOT IN`, `NOT LIKE`, `NOT BEGINS/ENDS WITH` are paired with `not-null`, as SQL never matches NULL; `TOP (n)` is honoured, TOP PERCENT/WITH TIES/non-literal TOP are errors; an aliased column next to a computed item, and a computed item over a GROUP BY column, return their values; names that are not valid Dataverse names are refused instead of written into FetchXML; LIKE patterns with an inner wildcard are sent as `like` (#1698).
- Int (and bigint) arithmetic that overflows is an error instead of a wider value (#1698).

## [1.0.2] - 2026-10-30

### Security

- Consume PPDS.Dataverse 1.3.2, carrying `System.Security.Cryptography.Pkcs` 10.0.13 and `System.Security.Cryptography.Xml` 8.0.5 through the query dependency graph.

### Changed

- Refresh SourceLink build tooling to 10.0.401. Query behavior and public APIs are unchanged.

## [1.0.1] - 2026-09-12

### Fixed

- **DML dry-run plans nested statements without executing them** — script planning now delegates nested DML to its side-effect-free planning path instead of the execution path, while actual DML remains confirmation-gated and all hard safety blocks remain enforced ([#1401](https://github.com/joshsmithxrm/power-platform-developer-suite/pull/1401)).

### Changed

- **Dataverse dependency updated to 1.3.1** — carries the patched `System.Security.Cryptography.Xml` 8.0.4 dependency through the query package graph ([#1402](https://github.com/joshsmithxrm/power-platform-developer-suite/pull/1402)).

## [1.0.0] - 2026-04-18

First stable release. Consolidates features developed across `1.0.0-beta.1` and `1.0.0-beta.2`. Production-grade SQL query engine for Dataverse with FetchXML transpilation and an ADO.NET provider. Targets `net8.0`, `net9.0`, `net10.0`.

### Added

- **SQL language support** — Full T-SQL parsed via `Microsoft.SqlServer.TransactSql.ScriptDom`:
  - `SELECT` with columns, aliases, `TOP`, `DISTINCT`, computed expressions
  - `WHERE` with all comparison operators, `LIKE`, `IN`, `BETWEEN`, `IS NULL`, `IS NOT DISTINCT FROM`
  - `JOIN` — `INNER`, `LEFT`, `RIGHT`, `FULL OUTER`, `CROSS`, `OUTER APPLY` (FetchXML pushdown when possible; client-side hash/merge/nested-loop fallback)
  - Multi-column key support in client-side `JOIN ON` conditions
  - `GROUP BY` with aggregates (`COUNT`, `SUM`, `AVG`, `MIN`, `MAX`, `STDEV`, `STDEVP`, `VAR`, `VARP`) including expression `GROUP BY`
  - `HAVING` with aggregate predicate support
  - `ORDER BY` with `ASC`/`DESC`
  - `UNION` and `UNION ALL`
  - Subqueries — `IN (SELECT)`, `NOT IN`, `EXISTS`, `NOT EXISTS`, derived tables, scalar subqueries
  - `NOT IN` subquery rewrite to `LEFT OUTER JOIN` for FetchXML pushdown
  - Window functions — `ROW_NUMBER`, `RANK`, `DENSE_RANK`, `CUME_DIST`, `PERCENT_RANK` with `PARTITION BY` and `ORDER BY`
  - Common Table Expressions (`WITH ... AS`) including recursive CTEs
  - `CASE`/`WHEN`/`THEN`/`ELSE` and `IIF`
  - Expression evaluation — `ISNULL`, `COALESCE`, `NULLIF`, `CAST`, string and date functions (`DATEADD`, `DATEDIFF`, `DATEPART`, `YEAR`, `MONTH`, `DAY`, `GETDATE`, `GETUTCDATE`, `TIMEFROMPARTS`)
  - `OPENJSON` table-valued function and `JSON_MODIFY` with array path support
- **DML** — `INSERT` (with `VALUES` or `SELECT`), `UPDATE`, `DELETE` with safety guards and configurable row caps. `INSERT ... SELECT` with ordinal mapping. `SELECT INTO #temp` for script-scoped temporary tables.
- **Scripting and flow control** — `DECLARE`/`SET` variable assignment, `IF`/`ELSE`, `WHILE`/`BREAK`/`CONTINUE`, and `@@ERROR` / `ERROR_MESSAGE()` session tracking.
- **Execution engine** — Volcano iterator model for streaming results. Pipeline: `SQL Text → ScriptDom Parser → SQL AST → Query Planner → Execution Plan → FetchXML Transpiler → FetchXML (pushed down) → Volcano Iterators → Streaming rows`.
- **FetchXML transpilation** from SQL AST; filters, joins, and aggregates pushed to Dataverse when possible.
- **Prefetch scan node** — Page-ahead buffering for improved streaming throughput.
- **TDS endpoint routing** — Automatic routing of compatible queries to the SQL endpoint.
- **Parallel partitioned aggregates** — Accurate `COUNT(*)` beyond the Dataverse 50K limit via date-range partitioning, with adaptive retry (binary date-range splitting) when partitions exceed limits.
- **`IndexSpoolNode`** for correlated subquery caching; **`TableSpoolNode`** for in-memory result materialization.
- **`EXPLAIN`** — Query plan inspection with pool and parallelism metadata.
- **Query hints** — `OPTION()` hints and comment-based hint parser. Eight `ppds:` hints integrated into execution and routing: `USE_TDS`, `NOLOCK`, `HASH_GROUP`, `MAX_ROWS`, `MAXDOP`, `BATCH_SIZE`, `BYPASS_PLUGINS`, `BYPASS_FLOWS`.
- **ADO.NET provider** — `PpdsDbConnection`, `PpdsDbCommand` (parameterized), `PpdsDbDataReader` (streaming), and `PpdsDbProviderFactory` for idiomatic .NET consumption and discoverability.
- **Cross-environment queries** — `[environment].[entity]` bracket syntax; smart label detection for 2-part references; `RemoteScanNode` for remote FetchXML execution.
- **Metadata query system** — Queryable entity/attribute schema access.
- **FetchXML IntelliSense** — `FetchXmlCompletionEngine` provides cursor-aware completions (element names, attribute names, operators, filter types, boolean flags); wired through CLI daemon, TUI, and VS Code extension.
- **Safety and protection** — DML safety guard with configurable thresholds and environment protection levels; `QueryExecutionException` with structured error codes for programmatic handling.

[Unreleased]: https://github.com/joshsmithxrm/power-platform-developer-suite/compare/Query-v1.1.0...HEAD
[1.1.0]: https://github.com/joshsmithxrm/power-platform-developer-suite/compare/Query-v1.0.2...Query-v1.1.0
[1.0.2]: https://github.com/joshsmithxrm/power-platform-developer-suite/compare/Query-v1.0.1...Query-v1.0.2
[1.0.1]: https://github.com/joshsmithxrm/power-platform-developer-suite/compare/Query-v1.0.0...Query-v1.0.1
[1.0.0]: https://github.com/joshsmithxrm/power-platform-developer-suite/releases/tag/Query-v1.0.0
