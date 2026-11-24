# Differential tests against a real SQL Server

This project runs the same scalar expression on a real SQL Server and through PPDS's query engine, and compares three things: the value, the error and the result type (#1798, part of #1764). SQL Server is the reference. A value PPDS computes differently is a possible wrong value in a `SELECT` or in a value DML writes.

All tests carry `Category=SqlDifferential`. The tests that need a server skip themselves, with the reason, when `PPDS_REFSQL_HOST` is not set, so `dotnet test PPDS.sln --filter "Category!=Integration"` stays green on a machine without one. The harness's self-tests need no server and always run.

## Running it

| Variable | Meaning |
|---|---|
| `PPDS_REFSQL_HOST` | The server: a host name, `host,port`, or `(localdb)\MSSQLLocalDB`. Unset: the server tests skip. |
| `PPDS_REFSQL_PASSWORD` | Password of a SQL login. Unset: integrated security (LocalDB on Windows). |
| `PPDS_REFSQL_USER` | The SQL login; `sa` when unset. |
| `PPDS_SQLDIFF_SEEDS` | Seeds for a report-only run, comma-separated. Unset: the report test skips. The gated run always uses its fixed seeds. |
| `PPDS_SQLDIFF_COUNT` | Expressions per seed of a report-only run; default 300. |
| `PPDS_SQLDIFF_DEPTH` | Maximum nesting depth of a report-only run; default 3. |
| `PPDS_SQLDIFF_REPORT` | A directory: each report-only seed's report is written there as well. |
| `PPDS_SQLDIFF_WRITE_SNAPSHOT` | A file path: the gated test writes the default run's snapshot there and fails, instead of comparing. |

```bash
dotnet test tests/PPDS.Query.DifferentialTests -f net10.0 --filter "Category=SqlDifferential" --logger "console;verbosity=detailed"
```

**Developer machine, Windows:** LocalDB is enough. Set `PPDS_REFSQL_HOST=(localdb)\MSSQLLocalDB` and leave the password unset.

**Developer machine or CI, container:** the reference has been SQL Server 2022 (16.0.4325.4, CU28) with collation `SQL_Latin1_General_CP1_CI_AS`. Use the same major version, for example:

```bash
docker run -d --name sql-reference -e ACCEPT_EULA=Y -e MSSQL_SA_PASSWORD="<a strong password>" -p 1433:1433 mcr.microsoft.com/mssql/server:2022-latest
```

Then set `PPDS_REFSQL_HOST=localhost` and `PPDS_REFSQL_PASSWORD` to that password. In GitHub Actions the same image runs as a job service container on a Linux runner, with the password generated in the job, so no secret is needed. That wiring is not part of this project: where the comparison runs in CI, and whether it gates merges, is decided separately.

The server needs nothing set up beforehand. Each run creates its own database, named `ppds_sqldiff_<UTC time>_<random>` with collation `SQL_Latin1_General_CP1_CI_AS` (checked after it is created), uses only that database, and drops it at the end, by the name it created it with. It never reads or changes another database. A run that is killed before the end leaves its database behind; drop it by hand.

## What is compared

- **Value**, exactly: NULL against NULL, decimal digits with their scale (`2.50` is not `2.5`), money with its four places, floats in round-trip form, text with trailing spaces, line feeds and look-alike letters spelled out.
- **Error**: error against no error, and the class of error (divide by zero, overflow, conversion, truncation, type clash, refused). The number or exception name is recorded with the class. Two errors of the class "other" (an unmapped SQL Server error, a .NET exception) never compare equal. "Refused" is only the engine's own parser or compiler rejecting the expression; anything else it throws while compiling is "other".
- **Harness failures** stop the run and are never outcomes: a timeout, a connection error, an error of severity 20 or more, a syntax error in the harness's own SQL, a missing operand row, session settings or compatibility level other than the reference's (checked when the database is set up and after every batch), and a value of a type the harness cannot render exactly (binary, datetimeoffset, time, anything not listed in `Outcome.Render`).
- **Result type**: SQL Server's type is the type of the result column it sends with the value (the TDS column metadata, which SqlClient exposes through `GetColumnSchema`): by definition the type of the value returned, with a decimal's precision and scale and a character type's length. Money and smallmoney get their documented 19,4 and 10,4, which the metadata does not carry. `sp_describe_first_result_set` was tried first and rejected: it types the statement without running it, and for `CASE WHEN CAST(1.0000000000000002 AS float) > 1.0 THEN 'gt' ELSE 'not' END` it reports `varchar(2)` (the branch it folds to) where the executed result is `varchar(3)`. When SQL Server raises an error there is no type, and none is compared. Today's engine has no SQL types: it reports the .NET type of the value it computed, so the type is compared as a family (tinyint, smallint, int, bigint, decimal including money, float, text, bit), and not at all for a NULL. The whole-number types stay four families, because each overflows at a different point. Once the typed engine reports full SQL types, the comparison becomes exact.

The expressions run under stated session settings (`ANSI_WARNINGS`, `ARITHABORT` and `CONCAT_NULL_YIELDS_NULL` on, `NUMERIC_ROUNDABORT` off; see `ReferenceSettings`), over an operand table of typed columns (`int`, `bigint`, `decimal(23,10)`, `decimal(12,2)`, `money`, `float`, `nvarchar(100)`, `varchar(100)`) with seven rows of boundary values (ordinary, zero, negative, upper and lower bounds, all NULL, line breaks). PPDS gets the stored rows as SqlClient reads them, the way it gets a Dataverse row: a currency value is a plain decimal, a whole number an int.

PPDS evaluates each expression with the product's `QueryParser` and `ExpressionCompiler`, the same path a `SELECT` without `FROM`, a computed column, a client-side filter and a DML value take.

## The generator

`RuleTable` lists the leaves (the columns, and literals at and around the type boundaries) and the rules (operators, functions, conversions, conditionals, comparisons). `ExpressionGenerator` builds expressions from it, nested to the chosen depth, from a seed. The random source is written out in the code, so a seed gives the same expressions on every .NET version and machine. A failing run prints its seed and the index of each expression, which reproduces it.

## The gated run and its snapshot

The gated test runs the fixed default run: seeds 1, 2 and 3, 300 expressions each, depth up to 3, every expression over every operand row (over the first row only when it reads no column). It records every evaluation that differs, exactly: the expression text and the operand row, SQL Server's outcome (value with its scale, type with precision, scale and length, or error class and number) and the engine's outcome (value, type family, or error class and exception name). It passes only when that set equals `Snapshots/default-run-differences.tsv` exactly. A new difference, a changed outcome on either side, or a recorded difference that no longer occurs fails the test and names the rows. It also checks that both engines evaluated every expression. No rule decides that a difference resembles a known one.

Each snapshot row also says where the expression was first generated (seed and index) and has a free-text `note` column (for example an issue number). Nothing reads the note.

### Updating the snapshot

When a change is meant to change the differences (an engine fix, a new rule in the rule table, a new operand row):

```bash
PPDS_SQLDIFF_WRITE_SNAPSHOT=$PWD/tests/PPDS.Query.DifferentialTests/Snapshots/default-run-differences.tsv dotnet test tests/PPDS.Query.DifferentialTests -f net10.0 --filter "FullyQualifiedName~The_default_run_differs"
```

The test writes the file and fails on purpose, so a run that writes the snapshot never passes. Then review the diff of the file line by line, cite in the pull request the issue for each removed or changed row (a fix) and the reason for each added row, and commit it. The same run writes the same bytes; the file was byte-identical across two runs and across net8.0, net9.0 and net10.0 when it was recorded. Notes of rows whose outcomes did not change are carried over.

## Report-only runs

Other seeds, more expressions or deeper nesting run in the report test (`PPDS_SQLDIFF_SEEDS`, `PPDS_SQLDIFF_COUNT`, `PPDS_SQLDIFF_DEPTH`). It never passes or fails on what it finds: it lists the differences grouped for reading and fails only when the harness itself fails. Each differing evaluation is listed under the smallest node that differs while its operands agree. That node may sit in a branch SQL Server did not take, and a node above it is not looked at, so a group names a place to start reading, not a cause.

## Known differences (the negative control)

`KnownDifferences` records the wrong cases already on #1764 and #1851 as hand-written expressions with both outcomes pinned exactly, each citing its issue. `NegativeControlTests` runs them and fails if SQL Server's result is not the recorded one, if the case stops differing (fixed: remove the case), or if the engine's wrong result changes to another wrong result. A self-test pins the list of entries and their case counts, so a dropped entry is a visible change.

A fix that makes a case agree with SQL Server has to remove that case in the same pull request. Nothing may be added without an open issue that records the difference.

## Reference observations

`ReferenceObservations` pins SQL Server's result for behaviours its documentation leaves silent or ambiguous (literal typing, currency division, money and decimal conversions, casing by collation, ROUND overflow, DATEFORMAT, offsets, TRY_CAST, GUID spellings, short text, CONCAT, TRIM and LEN). A case fails when the server's result is not the pinned one; the engine's result for a scalar expression is reported beside it, not asserted.
