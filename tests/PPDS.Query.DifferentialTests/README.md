# Differential tests against a real SQL Server

This project runs the same scalar expression on a real SQL Server and through PPDS's query engine, and compares three things: the value, the error and the result type (#1798, part of #1764). SQL Server is the reference. A value PPDS computes differently is a possible wrong value in a `SELECT` or in a value DML writes.

All tests carry `Category=SqlDifferential`. The tests that need a server skip themselves, with the reason, when `PPDS_REFSQL_HOST` is not set, so `dotnet test PPDS.sln --filter "Category!=Integration"` stays green on a machine without one. The harness's self-tests need no server and always run.

## Running it

| Variable | Meaning |
|---|---|
| `PPDS_REFSQL_HOST` | The server: a host name, `host,port`, or `(localdb)\MSSQLLocalDB`. Unset: the server tests skip. |
| `PPDS_REFSQL_PASSWORD` | Password of a SQL login. Unset: integrated security (LocalDB on Windows). |
| `PPDS_REFSQL_USER` | The SQL login; `sa` when unset. |
| `PPDS_SQLDIFF_SEEDS` | Seeds for the generated run, comma-separated; default `1,2,3`. |
| `PPDS_SQLDIFF_COUNT` | Expressions per seed; default 300. |
| `PPDS_SQLDIFF_DEPTH` | Maximum nesting depth; default 3. |
| `PPDS_SQLDIFF_REPORT` | A directory: each seed's report is written there as well. |

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
- **Error**: error against no error, and the class of error (divide by zero, overflow, conversion, truncation, type clash, refused). Messages and numbers are reported, not compared, with one exception: two errors of the class "other" (an unmapped SQL Server error, a .NET exception) never compare equal. A timeout, a connection error, an error of severity 20 or more, or a missing operand row is a harness failure that stops the run, never an outcome.
- **Result type**: SQL Server's type is the type of the result column it sends with the value (the TDS column metadata, which SqlClient exposes through `GetColumnSchema`): by definition the type of the value returned, with a decimal's precision and scale and a character type's length. Money and smallmoney get their documented 19,4 and 10,4, which the metadata does not carry. `sp_describe_first_result_set` was tried first and rejected: it types the statement without running it, and for `CASE WHEN CAST(1.0000000000000002 AS float) > 1.0 THEN 'gt' ELSE 'not' END` it reports `varchar(2)` (the branch it folds to) where the executed result is `varchar(3)`. When SQL Server raises an error there is no type, and none is compared. Today's engine has no SQL types: it reports the .NET type of the value it computed, so the type is compared as a family (tinyint, smallint, int, bigint, decimal including money, float, text, bit), and not at all for a NULL. The whole-number types stay four families, because each overflows at a different point. Once the typed engine reports full SQL types, the comparison becomes exact.

The expressions run under stated session settings (`ANSI_WARNINGS`, `ARITHABORT` and `CONCAT_NULL_YIELDS_NULL` on, `NUMERIC_ROUNDABORT` off; see `ReferenceSettings`), over an operand table of typed columns (`int`, `bigint`, `decimal(23,10)`, `decimal(12,2)`, `money`, `float`, `nvarchar(100)`, `varchar(100)`) with seven rows of boundary values (ordinary, zero, negative, upper and lower bounds, all NULL, line breaks). PPDS gets the stored rows as SqlClient reads them, the way it gets a Dataverse row: a currency value is a plain decimal, a whole number an int.

PPDS evaluates each expression with the product's `QueryParser` and `ExpressionCompiler`, the same path a `SELECT` without `FROM`, a computed column, a client-side filter and a DML value take.

## The generator

`RuleTable` lists the leaves (the columns, and literals at and around the type boundaries) and the rules (operators, functions, conversions, conditionals, comparisons). `ExpressionGenerator` builds expressions from it, nested to the chosen depth, from a seed. The random source is written out in the code, so a seed gives the same expressions on every .NET version and machine. A failing run prints its seed and the index of each expression, which reproduces it.

When a whole expression differs, the runner evaluates every node of it on both sides and finds the smallest nodes that differ while all their operands agree: the causes. Each cause is either a known difference or new. An expression counts as an expected difference only when every cause in it is known, so a known difference cannot hide a new one beside it.

**Limit.** A node above a cause has operands that already differ, so its own result is not checked in that evaluation; a defect in that node shows only where the generator puts the same construct over operands that agree, which it does at every depth. Every report prints this line.

**Baseline.** `NewDifferenceBaseline` lists the new-difference groups found when it was recorded (seeds 1 to 3, 1,000 expressions each). It accepts nothing: the run fails while any new group exists. The report names every group that is not in the baseline, so one more cannot hide among the known ones. A fix, or an entry citing a filed issue, removes its group.

## Known differences (the negative control)

`KnownDifferences` records the wrong cases already on #1764 and #1851. Each has:

- hand-written cases with both outcomes pinned. `NegativeControlTests` runs them and fails if SQL Server's result is not the recorded one, if the case stops differing (fixed: remove the case), or if the engine's wrong result changes to another wrong result;
- a narrow matcher that recognises the same defect as a cause in a generated expression. It recomputes the defect from the operands (the value the defect gives and the value SQL Server gives), so another defect on the same construct stays new;
- an expression tree for each case: the negative control runs the runner on it and checks that the cause it finds is explained by this entry and no other. `KnownDifferenceMatcherTests` checks the same without a server, plus near misses (a wrong branch, a NULL, a value a thousand times off, a dropped space, an inverted comparison) that must stay new, and pins the list of entries and their case counts.

A fix that makes a case agree with SQL Server has to remove that case in the same pull request. Nothing may be added without an open issue that records the difference.

## Reference observations

`ReferenceObservations` pins SQL Server's result for behaviours its documentation leaves silent or ambiguous (literal typing, currency division, money and decimal conversions, casing by collation, ROUND overflow, DATEFORMAT, offsets, TRY_CAST, GUID spellings, short text, CONCAT, TRIM and LEN). A case fails when the server's result is not the pinned one; the engine's result for a scalar expression is reported beside it, not asserted.
