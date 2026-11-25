# Changelog - PPDS.Query

All notable changes to PPDS.Query will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html), under the rules in [docs/VERSIONING.md](https://github.com/joshsmithxrm/power-platform-developer-suite/blob/main/docs/VERSIONING.md).

## [Unreleased]

## [1.1.0] - Unreleased

### Changed

- **Breaking: SELECT forms that were right only by chance are refused.** `WHERE col = NULL` (and `<> NULL`, `!= NULL`), `WHERE col IN (5, NULL)` (and `NOT IN` with NULL), a script variable holding NULL compared in a WHERE (`DECLARE @n nvarchar(20) = NULL; ... WHERE name = @n`; any variable in a WHERE sent to Dataverse is refused), `WHERE NOT NOT col = 5` (any `NOT (...)` in a WHERE sent to Dataverse), and `SELECT TOP 3 WITH TIES ...` (and `TOP n PERCENT`) now fail with `QueryParseException` instead of being translated. In 1.4.x each of these could return the right rows only by chance: NULL was sent as an empty value (so `<> NULL` returned every row), a variable was sent as its own name, every NOT was dropped (so `NOT (a = 1)` selected the opposite rows) and WITH TIES and PERCENT were ignored. Maintainer decision of 2026-11-24: refuse them all. Each refusal says what to write instead: `IS NULL` or `IS NOT NULL`; in an IN list, leave NULL out and add `OR col IS NULL`; a literal value instead of the variable; the condition without NOT; `TOP (n)` without WITH TIES or PERCENT. They are `QueryParseException`s, which the CLI reports as `Query.ParseError` (a variable inside a script as `Query.ExecutionFailed`) (#1698, #1615, #1616, #1618, #1619).
- **Breaking: DML refuses text without the N prefix that is not 7-bit ASCII.** A text literal written by INSERT ... VALUES, UPDATE ... SET or the SELECT list of an INSERT ... SELECT, without N and with any character outside 7-bit ASCII (`UPDATE account SET name = 'Привет'`, and also one accented Latin letter such as `'naïve'`), is refused with `DmlUnsupportedConstruct` before anything is read or written, on confirmed, unconfirmed and dry-run requests; the message names the `N'...'` form. SQL Server converts such a literal to the database's code page and stores what the code page lacks as question marks, and the engine cannot tell the code page, so it refuses every such literal rather than write different text (maintainer decision, 2026-11-24). Before, the text was written as typed. `N'...'` and plain ASCII text are written as before. Such text in the condition of a write (`=`, `<>`, `IN`, `LIKE`, `BETWEEN`) is refused the same way, since SQL Server converts it before comparing and would select other rows (maintainer decision of 2026-11-25, "Yes, refuse it"). SELECT is unchanged.
- Aligned with PPDS.Dataverse 1.5.0 and its refreshed Microsoft.Extensions/JWT dependency graph in the coordinated release (#1501, #1502).
- Updated SourceLink build tooling to 10.0.403 (#1498). Notebook/RPC target-isolation fixes are in CLI/Extension.
- **Breaking:** the planner's own refusal messages repeat no literal from the script: a value, a condition or an expression is given by its kind and its line and column (`DmlStatementSupport.At`), and a name as written. `DmlStatementSupport.ToDmlLiteral` turns a ScriptDom literal into the `DmlLiteral` the admission functions of PPDS.Dataverse take (a whole number beyond Int32, which ScriptDom parses as a `NumericLiteral`, is a whole number) (#1698).
- **Breaking: the whole-script rule.** `ExecutionPlanBuilder.Plan` refuses a script that contains a write (INSERT, UPDATE, DELETE or MERGE, anywhere) unless every top-level statement is an INSERT, UPDATE or DELETE on the allowlist (`DmlStatementSupport.AdmittedScriptStatementKinds`, a closed list matched by exact type): IF, WHILE, DECLARE, SET, SELECT, SELECT ... INTO, TRY ... CATCH, PRINT, THROW, RETURN, BEGIN ... END, cursors, EXECUTE, temporary tables, table variables and every other kind are refused before anything is planned, with `QueryErrorCode.DmlUnsupportedConstruct` and a message naming the first such statement by line and column (`DmlStatementSupport.FindScriptRefusal`). A script with no write is not affected (#1698).
- **Breaking: a code per DML refusal class.** New `QueryErrorCode.DmlUnsupportedConstruct` (anything not on the allowlist), `DmlValueStoredDifferently` (a written value Dataverse stores differently: empty text, and text with white space at either end) and `DmlMetadataUnavailable` (no column metadata). `DmlBlocked` is no longer used by the allowlist; the planner keeps it for a row limit, page number or paging cookie passed for DML (#1698).
- **Breaking:** empty text written by INSERT or UPDATE is refused with `DmlValueStoredDifferently` (Dataverse stores it as NULL); text that is only white space or has white space at either end is refused the same way until rows B25 and B26 of the spec's admission table are in force (`DmlStatementSupport.WhiteSpaceEdgedTextIsWritten`). A number literal with more digits than System.Decimal holds is refused in DML with `DmlUnsupportedConstruct`, and is a `QueryExecutionException` (`QueryErrorCode.ExecutionFailed`) in a SELECT, instead of a raw `OverflowException`. `TOP ... WITH TIES` and `TOP ... PERCENT` inside a common table expression, and a comparison with NULL in a JOIN ... ON condition, are `QueryParseException`s naming the replacement (#1698).
- **Breaking:** the planner refuses INSERT, UPDATE and DELETE shapes that are not on the DML allowlist (`specs/query.md`, "DML allowlist") with `DmlSafetyRefusalException` (`QueryErrorCode.DmlUnsupportedConstruct`), so explain, scripts and the ADO.NET provider cannot plan them; MERGE, EXECUTE AS and REVERT are refused; a row limit, page number or paging cookie in `QueryPlanOptions` is refused for DML; SQL with more than one batch (`GO`) is a `QueryParseException`. DML target and column names are planned as lower-case logical names (#1698).
- **Breaking:** until the typed SQL value engine proves a construct, the planner admits as a value written by INSERT or UPDATE only a text, whole-number or decimal literal, NULL, a minus sign on a whole-number or decimal literal, `CAST('<guid>' AS uniqueidentifier)` with the GUID as 8-4-4-4-12 hexadecimal digits with dashes (optionally in braces, no spaces), or a column of the statement's table. Every other expression (arithmetic, functions, CASE, IIF, COALESCE, NULLIF, other conversions, variables, subqueries, parentheses) is refused with `DmlSafetyRefusalException` (`QueryErrorCode.DmlUnsupportedConstruct`) naming the construct; the engine computes them with .NET rules, which differ from SQL Server (#1764). Admission matches exact node types from `DmlStatementSupport.AdmittedValueNodeKinds`, so a node kind a later parser version adds is refused. Each construct comes back as the typed engine proves it (#1800 to #1806). A script is checked as a whole when it is planned (see the whole-script rule above), so for a direct caller such as the ADO.NET provider no statement of a script runs when any part of it is refused (#1698).
- INSERT ... SELECT is planned from the parsed statement, so its WHERE and ORDER BY are applied (they were dropped) (#1698).
- **Breaking:** the planner refuses, in INSERT, UPDATE and DELETE, a text literal with a lower-case n prefix (`n'x'`, which ScriptDom reads as `N'x'` and SQL Server does not read as text), and a WHERE nested more than 200 levels deep (AND, OR, NOT and parentheses) or with more than 1,000 conditions (`DmlStatementSupport.MaxConditionDepth`, `MaxConditions`; the checks of a condition recurse, and a chain of about 6,500 ORs exhausted the stack). The planner checks the size of every statement's condition, without recursion, before it walks the script, so a script whose later statement is too large is refused before its first statement runs. A table, column or alias name followed by a line feed is no longer taken for a logical name (#1698).

### Fixed

- SELECT returns what its text says or fails with a clear error: `IN (subquery)`/`EXISTS` inside OR, parentheses or NOT no longer overflow the stack; `NOT (...)`, `= NULL`, `<> NULL` and variables in a pushed-down WHERE are errors (they selected wrong rows); `<>`, `NOT IN`, `NOT LIKE`, `NOT BEGINS/ENDS WITH` are paired with `not-null`, as SQL never matches NULL; `TOP (n)` is honoured, TOP PERCENT/WITH TIES/non-literal TOP are errors; an aliased column next to a computed item, and a computed item over a GROUP BY column, return their values; names that are not valid Dataverse names are refused instead of written into FetchXML; LIKE patterns with an inner wildcard are sent as `like` (#1698).
- Int (and bigint) arithmetic that overflows is an error instead of a wider value: int / int and int % int are int, so a later step that leaves the int range is an error, and `-` of the smallest int or bigint is an overflow (#1698).

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
