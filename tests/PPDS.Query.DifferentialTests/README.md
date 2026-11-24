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
docker run -d --name ppds-refsql -e ACCEPT_EULA=Y -e MSSQL_SA_PASSWORD="<a strong password>" -p 1433:1433 mcr.microsoft.com/mssql/server:2022-latest
```

Then set `PPDS_REFSQL_HOST=localhost` and `PPDS_REFSQL_PASSWORD` to that password. In GitHub Actions the same image runs as a job service container on a Linux runner, with the password generated in the job, so no secret is needed. That wiring is not part of this project: where the comparison runs in CI, and whether it gates merges, is decided separately.

The server needs nothing set up beforehand. Each run creates its own database, named `ppds_sqldiff_<UTC time>_<random>`, uses only that database, and drops it at the end, by the name it created it with. It never reads or changes another database. A run that is killed before the end leaves its database behind; drop it by hand.

## What is compared

- **Value**, exactly: NULL against NULL, decimal digits with their scale (`2.50` is not `2.5`), money with its four places, floats in round-trip form, text with trailing spaces, line feeds and look-alike letters spelled out.
- **Error**: error against no error, and the class of error (divide by zero, overflow, conversion, truncation, type clash, refused). Messages and numbers are reported, not compared.
- **Result type**: SQL Server's type comes from `sp_describe_first_result_set`, which returns the type its binder gives the result column, with precision, scale and length (`numeric(16,6)`, `varchar(4)`), as its Transact-SQL reference page documents. It types the statement without running it, so an expression whose evaluation fails still has a type. The harness checks on every successful evaluation that it agrees with the type the server sends in the result set's metadata, and stops with a harness error if it does not. Today's engine has no SQL types: it reports the .NET type of the value it computed, so the type is compared as a family (int, bigint, decimal including money, float, text, bit), and not at all for a NULL. Once the typed engine reports full SQL types, the comparison becomes exact.

The expressions run under stated session settings (`ANSI_WARNINGS`, `ARITHABORT` and `CONCAT_NULL_YIELDS_NULL` on, `NUMERIC_ROUNDABORT` off; see `ReferenceSettings`), over an operand table of typed columns (`int`, `bigint`, `decimal(23,10)`, `decimal(12,2)`, `money`, `float`, `nvarchar(100)`, `varchar(100)`) with seven rows of boundary values (ordinary, zero, negative, upper and lower bounds, all NULL, line breaks). PPDS gets the stored rows as SqlClient reads them, the way it gets a Dataverse row: a currency value is a plain decimal, a whole number an int.

PPDS evaluates each expression with the product's `QueryParser` and `ExpressionCompiler`, the same path a `SELECT` without `FROM`, a computed column, a client-side filter and a DML value take.

## The generator

`RuleTable` lists the leaves (the columns, and literals at and around the type boundaries) and the rules (operators, functions, conversions, conditionals, comparisons). `ExpressionGenerator` builds expressions from it, nested to the chosen depth, from a seed. The random source is written out in the code, so a seed gives the same expressions on every .NET version and machine. A failing run prints its seed and the index of each expression, which reproduces it.

When a whole expression differs, the runner evaluates every node of it on both sides and finds the smallest nodes that differ while all their operands agree: the causes. Each cause is either a known difference or new. An expression counts as an expected difference only when every cause in it is known, so a known difference cannot hide a new one beside it.

## Known differences (the negative control)

`KnownDifferences` records the wrong cases already on #1764 and #1851. Each has:

- hand-written cases with both outcomes pinned. `NegativeControlTests` runs them and fails if SQL Server's result is not the recorded one, if the case stops differing (fixed: remove the case), or if the engine's wrong result changes to another wrong result;
- a narrow matcher that recognises the same defect as a cause in a generated expression.

A fix that makes a case agree with SQL Server has to remove that case in the same pull request. Nothing may be added without an open issue that records the difference.
