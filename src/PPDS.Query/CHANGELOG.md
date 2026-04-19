# Changelog - PPDS.Query

All notable changes to PPDS.Query will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

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

### Fixed

- **Paged TDS queries** — Consistent results maintained across pages.
- **`FetchXmlCompletionEngine` tag detection** — Correct XML context identification for nested elements.
- **Notebook interactive prompts** — Appear once before execution rather than per-cell during Run All.

[Unreleased]: https://github.com/joshsmithxrm/power-platform-developer-suite/compare/Query-v1.0.0...HEAD
[1.0.0]: https://github.com/joshsmithxrm/power-platform-developer-suite/releases/tag/Query-v1.0.0
