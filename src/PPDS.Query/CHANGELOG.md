# Changelog - PPDS.Query

All notable changes to PPDS.Query will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [1.0.0-beta.2] - 2026-04-17

### Added

- **FetchXML IntelliSense** — `FetchXmlCompletionEngine` provides cursor-aware completions (element names, attribute names, operators, filter types, boolean flags) for FetchXML documents. Wired through the CLI daemon, TUI, and VS Code extension.
- **Query hint execution** — Eight `ppds:` hints integrated into the execution and routing pipeline: `USE_TDS`, `NOLOCK`, `HASH_GROUP`, `MAX_ROWS`, `MAXDOP`, `BATCH_SIZE`, `BYPASS_PLUGINS`, `BYPASS_FLOWS`.

### Fixed

- **Paged TDS queries** — Maintain consistent results across pages.
- **`FetchXmlCompletionEngine` tag detection** — Correct XML context identification for nested elements.
- **Notebook interactive prompts** — Appear once before execution rather than per-cell during Run All.

## [1.0.0-beta.1] - 2026-03-02

### Added

#### SQL Language Support

- **Full T-SQL parsing** via Microsoft.SqlServer.TransactSql.ScriptDom
- **SELECT** with columns, aliases, TOP, DISTINCT
- **WHERE** with all comparison operators, LIKE, IN, BETWEEN, IS NULL, IS NOT DISTINCT FROM
- **JOIN support** — INNER, LEFT, RIGHT, FULL OUTER, CROSS, OUTER APPLY with FetchXML pushdown when possible, client-side hash/merge/nested-loop fallback
- **Multi-column key support** in client-side JOIN ON conditions
- **GROUP BY** with aggregates (COUNT, SUM, AVG, MIN, MAX, STDEV, STDEVP, VAR, VARP) including expression GROUP BY
- **HAVING clause** with aggregate predicate support
- **ORDER BY** with ASC/DESC
- **UNION and UNION ALL**
- **Subqueries** — IN (SELECT), NOT IN, EXISTS, NOT EXISTS, derived tables (FROM subquery), scalar subqueries
- **NOT IN subquery rewrite** to LEFT OUTER JOIN for FetchXML pushdown
- **Window functions** — ROW_NUMBER, RANK, DENSE_RANK, CUME_DIST, PERCENT_RANK with PARTITION BY and ORDER BY
- **Common Table Expressions** (WITH ... AS) including recursive CTEs
- **CASE/WHEN/THEN/ELSE and IIF** expressions
- **Computed column expressions** in SELECT
- **Expression evaluation** — ISNULL, COALESCE, NULLIF, CAST, string functions, date functions (DATEADD, DATEDIFF, DATEPART, YEAR, MONTH, DAY, GETDATE, GETUTCDATE, TIMEFROMPARTS)
- **OPENJSON** table-valued function
- **JSON_MODIFY** with array path support

#### DML Support

- **INSERT, UPDATE, DELETE** with safety guards and configurable row caps
- **INSERT ... SELECT** with ordinal mapping
- **SELECT INTO #temp** for script-scoped temporary tables

#### Scripting & Flow Control

- **DECLARE/SET** variable assignment (SELECT @var = expr)
- **IF/ELSE** flow control for multi-statement scripts
- **WHILE loops** with BREAK and CONTINUE
- **@@ERROR and ERROR_MESSAGE()** tracking in session context

#### Execution Engine

- **FetchXML transpilation** from SQL AST
- **Execution plan engine** with Volcano iterator model for streaming results
- **Prefetch scan node** for page-ahead buffering
- **TDS Endpoint routing** for compatible queries
- **Parallel partitioned aggregates** for accurate COUNT(*) beyond Dataverse 50K limit
- **Adaptive aggregate retry** with binary date-range splitting when partitions exceed limits
- **IndexSpoolNode** for correlated subquery caching
- **TableSpoolNode** for in-memory result materialization
- **EXPLAIN command** for query plan inspection with pool/parallelism metadata
- **OPTION() query hints** and comment-based hint parser

#### ADO.NET Provider

- **PpdsDbConnection** for standard .NET data access
- **PpdsDbCommand** with parameterized query support
- **PpdsDbDataReader** for streaming result consumption
- **PpdsDbProviderFactory** for ADO.NET provider discoverability

#### Cross-Environment Queries

- **Cross-environment query planning** with bracket syntax (`[environment].[entity]`)
- **Smart label detection** for 2-part cross-environment references
- **RemoteScanNode** for cross-environment FetchXML execution

#### Metadata

- **Metadata query system** for querying entity/attribute schema

#### Safety & Protection

- **DML safety guard** with configurable thresholds and environment protection levels
- **QueryExecutionException** with structured error codes

[Unreleased]: https://github.com/joshsmithxrm/power-platform-developer-suite/compare/Query-v1.0.0-beta.1...HEAD
[1.0.0-beta.1]: https://github.com/joshsmithxrm/power-platform-developer-suite/releases/tag/Query-v1.0.0-beta.1
