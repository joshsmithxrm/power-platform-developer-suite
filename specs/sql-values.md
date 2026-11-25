# SQL Values

**Status:** Draft
**Last Updated:** 2026-11-25
**Code:** [src/PPDS.Query/Execution/](../src/PPDS.Query/Execution/), [src/PPDS.Query/Planning/](../src/PPDS.Query/Planning/), [src/PPDS.Dataverse/Query/Execution/Functions/](../src/PPDS.Dataverse/Query/Execution/Functions/), [tests/PPDS.Query.DifferentialTests/](../tests/PPDS.Query.DifferentialTests/)
**Surfaces:** CLI, TUI, Extension, MCP

---

## Overview

This spec describes what PPDS is to do; little of it is built. Today, on `main`, PPDS computes every value from SQL text (an `UPDATE ... SET` value, an `INSERT` value, a computed column, a client-side comparison) with an untyped evaluator and writes any value expression without a check. Under this spec, every value has a SQL type that is known before anything runs and is computed in that type the way SQL Server computes it; one type model decides both what is refused and how each value is computed; and the model is checked against a real SQL Server by the differential harness in `tests/PPDS.Query.DifferentialTests` (#1798).

There is one exception, for reading only. In a script that contains no statement that writes, an expression that uses a construct with no proven type rule is not refused: the whole expression is computed by the earlier untyped evaluator, as a labelled fallback, and every surface says so. In a script that contains any statement that writes, nothing gets the fallback, in any statement: this is [the whole-script rule](#the-whole-script-rule). What such a script may contain is closed: only the rows in force of one [admission table](#admission-in-statements-that-write), each backed by SQL Server evidence and by a live Dataverse case on the engine's own write path. Everything else is refused before the script's first statement runs. A script that writes therefore contains only `INSERT`, `UPDATE` and `DELETE` statements. Today no row is in force. The first live cases ran on 2026-11-24 on the demo environment, on the build of the DML safety work (#1698). They gave many rows their Dataverse evidence, but every row still owes something: SQL Server evidence, a live case or the change to this spec that brings it into force ([Evidence by row](#evidence-by-row)). Once the interim rule takes effect, every statement that writes is refused until its rows are in force, and the rows come into force one by one. When they are all in force, a 2.0 user can write, on standard tables and with conditions on ids, whole numbers, text, choices, dates and null tests:
- a `DELETE`;
- an `UPDATE` that sets text, whole-number, decimal, currency, floating-point, yes/no, choice, status, single-target lookup and date columns (user local and date only) to a literal in that kind's exact form, or sets any of them except yes/no and status to `NULL`;
- an `UPDATE` that copies a column of the same kind (for currency and floating point, of the same precision) to a text, whole-number, currency or floating-point column.

For `INSERT`, the rows give `INSERT ... VALUES` the same literals and `NULL`, except status values. They give `INSERT ... SELECT` only copies of text and whole-number columns within the same table, with `TOP` only beside `ORDER BY`. Nothing computed, until rows for it are added.

The maintainer's decisions are listed under [Design Decisions](#design-decisions). What is still pending (the live cases, the evidence each row owes, and the author's readings that are his to confirm) is listed under [Pending](#pending).

### Goals

- **One model**: the code that decides whether an expression is allowed is the code that evaluates it. No second model predicts the first.
- **SQL Server results**: a computed value equals the value SQL Server produces for the same expression over the same typed inputs. That includes errors: where SQL Server, under the reference server's session settings, raises an error (overflow and divide by zero are the usual cases; SET ANSI_WARNINGS and SET ARITHABORT, Remarks), PPDS raises an error; where SQL Server shortens or rounds a value without an error, PPDS returns the same shortened or rounded value.
- **Fail closed**: in a script that writes, anything that is not a row in force of the admission table refuses the whole script before any of it runs, with a message naming that part. In a script that does not write, it is computed by the labelled fallback and never passed off as a proven value.
- **Checked before the first write**: every value a statement would write is computed and checked against the target column's limits before the first row is written.
- **Proof that composes**: each rule is proven once; nesting is covered by generated expressions run on a real SQL Server by the differential harness, not by listing statement shapes.

### Non-Goals

- Supporting every T-SQL function. A function without a proven rule is refused in a script that writes, and computed by the labelled fallback in a script that does not.
- Reproducing SQL Server query plans or performance.
- Collation-exact behaviour for every language. Text rules state which collation they are proven for and do not apply outside it; core requirement 10 says what happens then.
- Deciding which statement shapes are allowed in a statement that does not write, and which rows a statement selects: [query.md](./query.md) governs those. For statements that write, the [admission table](#admission-in-statements-that-write) is the only authority on shapes, clauses and hints. The rule table in this spec types value expressions only.
- A runtime cross-check through the Dataverse SQL (TDS) endpoint (computing the same values with Dataverse's own SQL and refusing on a difference). Not in 2.0; see [Roadmap](#roadmap).

---

## Architecture

```
SQL text (one request: the whole script)
   |
   v
ScriptDom parse tree
   |
   v
Script check (does any statement write? then no fallback anywhere in the script)
   |
   v
Binder  <---- column types (Dataverse metadata through the column type map)
   |             each node gets a SQL type; otherwise the script is refused,
   |             or, in a script that does not write, the expression goes to the fallback
   v
Typed expression tree (every implicit conversion is an explicit node)
   |
   v
Evaluator (computes each node in its SQL type)
   |
   v
Assignment check (convert to the target column's type; check its limits; all rows)
   |
   v
Write to Dataverse
```

### Components

| Component | Responsibility |
|-----------|----------------|
| SQL type | Kind plus precision, scale and length. The kinds are the closed list in [SQL types](#sql-types) |
| Column type map | The SQL type of each Dataverse column kind, with where its precision, scale and length come from. One table, verified through the Dataverse SQL (TDS) endpoint when the map is built |
| Script check | Before any statement of a script runs, finds whether the script contains a statement that writes ([the whole-script rule](#the-whole-script-rule)). If it does, the binder gives no expression in the script the fallback |
| Binder | Assigns a SQL type to every node from the rules below; inserts conversion nodes. In a script that writes, it admits only what a row in force of the admission table admits; in a script that does not, a node with no rule sends the whole expression to the fallback |
| Evaluator | Computes a typed tree over a row. It never chooses an operation from the runtime type of a value |
| Assignment check | Converts each value as the admission row that admits it says, checks the column's limits, and refuses the whole statement on the first failure (core requirement 3) |
| Rule table | The list of constructs that have a type rule, by operand types, with the collation each text rule was proven under. The binder, the tests and the documentation read the same table |
| Fallback | The untyped evaluator that exists today, kept for expressions with no proven rule in scripts that do not write. Its values are labelled. No script that writes can reach it |
| Differential harness | `tests/PPDS.Query.DifferentialTests`: runs each expression on the reference SQL Server and through PPDS and compares value, error and result type. It is the proof mechanism of this spec ([Reference server](#reference-server)) |

Apart from the fallback and the differential harness, none of these components exists yet; this spec is the description they are built and reviewed against. Until they exist, values are computed by the untyped `ExpressionCompiler` in `src/PPDS.Query/Execution/` and the function library in `src/PPDS.Dataverse/Query/Execution/Functions/`.

The components live in the libraries (`PPDS.Query` and `PPDS.Dataverse`), not in `PPDS.Cli`, so that every caller of the libraries passes through them. Which of the two packages holds each type is fixed in #1800 and recorded in [Core Types](#core-types).

### Dependencies

- Uses: [query.md](./query.md) for parsing, planning and which rows a statement selects.
- Uses: Dataverse column metadata.
- Uses patterns from: [architecture.md](./architecture.md) for Application Services and structured errors.

---

## Specification

### Scope

"Every value PPDS computes from SQL text" means every expression that PPDS itself evaluates, wherever it appears.

In scope:

- The value of an `UPDATE ... SET column = value` assignment.
- Each item of an `INSERT ... VALUES` row and each projection of an `INSERT ... SELECT`.
- Computed columns of a `SELECT` (projections that are not a plain column).
- Comparisons and conditions PPDS evaluates on rows it has read: the conditions inside `CASE` and `IIF`, filters that are not sent to Dataverse, and `ORDER BY` expressions sorted in PPDS.
- Every other expression the engine evaluates in a script: variable assignments (`DECLARE`, `SET @v`, `SELECT @v =`), the conditions of `IF` and `WHILE`, the query of a cursor, the arguments of `PRINT`, `THROW` and `RAISERROR`, join conditions, grouping, `HAVING`, window functions and aggregates computed in PPDS, `OPENJSON` and `STRING_SPLIT` arguments, and filters on the metadata virtual tables.
- ADO.NET parameter values, where they are used in one of the above.

Out of scope:

- Conditions, sorts and aggregates that are sent to Dataverse as FetchXML. Dataverse evaluates them; [query.md](./query.md) governs how they are translated.
- Queries routed through the Dataverse SQL (TDS) endpoint. The endpoint computes their values.
- Which statements that do not write are allowed, and which rows any statement selects (see Non-Goals). For statements that write, the admission table decides.

#### The whole-script rule

Decision M: "One whole-script rule". A script that contains any statement that writes gets no fallback anywhere; an unproven construct in it is refused.

- **A script** is the whole text of one request, every statement and every batch in it, whichever caller sends it: the CLI, the TUI, the daemon, the MCP query tool or an ADO.NET command.
- **A statement that writes** is every statement that is not on this closed list of read-only statements: `SELECT` without `INTO`; `DECLARE` of a variable or of a cursor; `SET` of a variable; `SET NOCOUNT` (it changes only the row-count messages); `IF`, `WHILE`, `BEGIN ... END`, `BREAK`, `CONTINUE` and `BEGIN TRY ... END TRY BEGIN CATCH ... END CATCH` (the blocks themselves; the statements inside them are classified one by one); `PRINT`, `THROW`, `RAISERROR`; `OPEN`, `FETCH`, `CLOSE` and `DEALLOCATE` of a cursor. Everything else counts as a write, whether or not it changes data: `INSERT`, `UPDATE`, `DELETE` and `MERGE` whatever the target (a temporary table included), `SELECT ... INTO`, `CREATE TABLE` and `DROP TABLE`, `EXECUTE` of a Dataverse message, `EXECUTE AS` and `REVERT`, every `SET` of a session option other than `NOCOUNT` (`SET DATEFORMAT` and `SET LANGUAGE` change how text is read as a date, S11), and any statement kind the engine admits later until it is added to this list by a change to this spec.
- **Contains** means anywhere in the text: inside a `BEGIN ... END` block, a branch of an `IF` that may not run, a `WHILE` body, a `TRY` or a `CATCH` block. Whether the script writes is decided from the parse of the whole script before its first statement runs, never from which statements happen to run.
- **In a script that writes**, nothing gets the fallback, and the only things admitted, in any statement of the script, are the rows in force of the [admission table](#admission-in-statements-that-write). Anything else refuses the whole script before its first statement runs, with `Query.DmlUnsupportedConstruct`: no row is read, and nothing is written or printed. Every statement on the read-only list is refused in such a script, because no row admits it. So a script that writes may contain only `INSERT`, `UPDATE` and `DELETE` statements, each one a form of rows A01 to A04, as the DML safety work (#1698) builds it. Variables, parameters, `IF`, `WHILE`, a `SELECT`, and joins or filters evaluated in PPDS are refused in it. The maintainer accepted this consequence: "Yes, accept it" ([Decisions of 2026-11-24 and 2026-11-25](#decisions-of-2026-11-24-and-2026-11-25-follow-up)).
- **In a script that does not write**, an expression with a part that has no proven rule is computed, as a whole, by the fallback (Decision J), wherever it is, except on a surface that cannot carry the label ([Surface-Specific Behavior](#surface-specific-behavior)), where it is refused. The fallback's output is labelled:
  - a computed column whose expression used the fallback is marked as computed by the fallback;
  - the result as a whole is marked when a filter, join condition, grouping, `HAVING` condition, sort, `OPENJSON` or `STRING_SPLIT` argument, or a filter on a metadata virtual table, evaluated in PPDS, used the fallback, because it decides which rows are returned;
  - every result that a statement returns after an earlier statement of the same script used the fallback for anything other than its own returned columns (a variable assignment, a cursor's query, an `IF` or `WHILE` condition, a `PRINT`, `THROW` or `RAISERROR` argument) is marked as a whole, because a value or a decision computed by the fallback may have reached it. No mark travels with a single value; the script carries it from that statement on.
- **A statement whose text is not known before the script runs** (`EXEC` of a string, dynamic SQL) is not supported today, and is not on the read-only list.
- **State kept between requests.** Nothing a script stores outlives its request on `main`: variables, temporary tables, cursors and an impersonation set by `EXECUTE AS` live in the script's own execution, and no product caller keeps a `SessionContext` between requests. If state is ever kept between requests, state that a request filled or changed with the fallback is marked as tainted, and a later request that writes and reads tainted state is refused before its first statement runs with `Query.DmlUnsupportedConstruct`. Any such state that a later request can read counts as read by every statement of that request.
- **Refusals inside a script.** A script that writes contains no `TRY ... CATCH`: `BEGIN TRY` is on the read-only list, and no row admits it. So `CATCH` matters only in a script that does not write, and there it catches exactly one thing: an evaluation error that SQL Server would also raise (overflow, divide by zero, a failed conversion), as SQL Server's `CATCH` would. It never catches a refusal or limit of PPDS's own (every other row of [Error Types](#error-types)): each ends the script at the statement that raised it, no `CATCH` block runs for it, and no later statement runs. In a script that writes, a rejection by Dataverse of a write (a row or a batch failing after the first write) ends the script as well. (On `main` today, `TRY ... CATCH` catches every exception; the typed path changes that.)
- **What is checked for the whole script, and what per statement.** Everything that depends only on the script's text and the metadata is checked for the whole script before its first statement runs: whether each part matches a row in force of the admission table, and, in a script that does not write, a collation that cannot be established. The check of computed values before the first write (core requirement 3) runs per statement, because a later statement's values can depend on rows an earlier statement wrote, which are not known until it has run. So in a script with more than one statement that writes, a later statement refused at run time (a value outside a column's limits, a value Dataverse may store differently, the row cap, the in-memory limit, an evaluation error) leaves the rows every earlier statement wrote in place. The refusal message names the refused statement by its line and says how many rows each earlier statement wrote.

The rule replaces the table of places in earlier drafts of this spec. That table named each place a fallback value could reach a write, and two reviews found places it had missed (a cursor, `TRY ... CATCH`, a collation-dependent rule in `SELECT @v = ...`); the rule names none of them because it covers all of them. [Why one whole-script rule](#why-one-whole-script-rule-instead-of-a-table-of-places) gives the reasoning.

Consequence for #1811 ("remove the untyped compiler"): the untyped compiler is not removed. It stays as the fallback, reachable only from a script that does not write and only for an expression with no proven rule. The rest of #1811 (ADO.NET parameters bound as typed values) is unchanged.

### Type rules

Each rule is implemented from the SQL Server documentation page named here and from tests against a real SQL Server. Do not read, copy, port or adapt source code from SQL 4 CDS or any other project.

| Area | Source of the rule |
|------|--------------------|
| Type of a literal | "Constants (Transact-SQL)"; "int, bigint, smallint, and tinyint" ("Convert integer data"); "decimal and numeric" (`numeric` is a synonym of `decimal`); reference observations R01 to R03 |
| Which operand converts to which | "Data type precedence (Transact-SQL)", "Data type conversion (Database Engine)" |
| Precision, scale and length of a result | "Precision, scale, and length (Transact-SQL)" |
| Currency arithmetic | The Result Types section of each operator ("+ (Addition)", "- (Subtraction)", "* (Multiplication)", "/ (Division)", "% (Modulus)": "Returns the data type of the argument with the higher precedence"), "Data type precedence", and "money and smallmoney" for the four decimal places; the rounding of a currency division is documentation silent, reference observation R05 |
| Explicit conversion | "CAST and CONVERT (Transact-SQL)", its "Truncating and rounding results" table per pair of types; "TRY_CAST", "TRY_CONVERT"; reference observations R07 to R09, R13 to R18 |
| Conditional values | "CASE", "IIF", "COALESCE", "NULLIF", "ISNULL" |
| Each function | That function's own page (result type, NULL handling, errors) |
| Comparisons, three-valued logic and ordering (inside `CASE` and `IIF`; filters and `ORDER BY` evaluated in PPDS), including the collation a text comparison uses | The pages are named in this row when the rules are written: #1803 for comparisons inside conditional values, #1810 for filters and `ORDER BY` |
| The type of an ADO.NET parameter and of a T-SQL variable | The pages are named in this row when the rule is written (#1811). A variable assignment shortens text silently even with `ANSI_WARNINGS` on ("SET ANSI_WARNINGS"; reference observation R25), so a variable's declared type is part of its rule |

Where the documentation does not state a behaviour, or states it in two ways that disagree, the rule says "documentation silent; reference observation R<n>" and cites the case in the harness's `ReferenceObservations` that records SQL Server's answer (Decision F2). Where SQL Server's answer contradicts the documentation, the server wins and the rule says so.

#### What the reference server showed

The behaviours below were documentation silent, ambiguous or contradictory, or the documented rule was easy to misread. Each was run on the reference server (SQL Server 2022, under the settings in [Reference server](#reference-server)) and is pinned in `ReferenceObservations` by its id, except where the Source column says the case is owed to the harness. A rule for one of these behaviours states the answer given here. Elsewhere in this spec, "S8" means row S8 of this table.

| # | Behaviour | SQL Server's answer | Source |
|---|-----------|---------------------|--------|
| S1 | Precision and scale of a decimal literal | From its digits: leading zeros are dropped, trailing zeros kept. `748.58` is `decimal(5,2)`, `0.5` is `decimal(1,1)`, `00012.3400` is `decimal(6,4)` | Documentation silent ("Constants" gives no precision or scale); reference observation R01 |
| S2 | Type of a whole-number literal | `int` up to 2,147,483,647; above it `decimal(p,0)` with p its digits (`2147483648` is `decimal(10,0)`) | "int, bigint, smallint, and tinyint": "Integer constants greater than 2,147,483,647 are converted to the decimal data type"; reference observation R02 |
| S3 | Type of a negated whole-number literal | `int` when the negated value is from -2,147,483,648 to 2,147,483,647, also with white space or parentheses between the minus and the literal (`-2147483648`, `- 5`, `-(2147483648)`). The documentation implies `decimal` for `-2147483648`, a minus over the decimal `2147483648` ("- (Negative)": "Returns the data type of numeric_expression"); the server wins | Documentation silent on this; reference observation R02c. `- 5` and `-(2147483648)`: observed on the reference server in review, 2026-11-24; owed to the harness (R27b) |
| S4 | Type of a short text literal | `'abc'` is `varchar(3)`, `N'abc'` is `nvarchar(3)`, `''` is `varchar(1)` | Documentation ambiguous ("Constants" states the type only above 8,000 bytes); reference observation R03 |
| S5 | Type of the `NULL` constant | `int` where nothing else gives it a type (`NULL`, `ISNULL(NULL, NULL)`). `IIF` or `COALESCE` whose every value is the `NULL` constant is an error (8133, 4127) | Documentation ambiguous ("ISNULL" states `int` for one case; "COALESCE": "At least one of the null values must be a typed NULL"); reference observation R04 |
| S6 | A currency division | `money`, truncated toward zero at four places: `CAST(2 AS money) / 3` is `0.6666`, `CAST(-2 AS money) / CAST(3 AS money)` is `-0.6666` | Documentation silent; reference observation R05 |
| S7 | Currency with a decimal or a whole number | With a whole number or money: `money`, four places after each operation (`CAST(1 AS money) / 3 * 3` is `0.9999`). A multiplication rounds to the four places half away from zero (with both operands `money`, `0.0005 * 0.5` is `0.0003` and `-0.0003 * 0.5` is `-0.0002`); a division truncates (S6). With a decimal: `decimal`, at the scale "Precision, scale, and length" gives (`CAST(1 AS money) * 1.1` is `1.10000`, `decimal(22,5)`), because decimal ranks above money | "Data type precedence"; reference observation R06. The rounding of a multiplication is documentation silent; observed on the reference server in review, 2026-11-24, owed to the harness (R27h) |
| S8 | Conversion that drops decimal places, per pair of types | `decimal` and `float` to `int`: truncated toward zero (2.7 gives 2, -2.7 gives -2). `money` to `int`: rounded half away from zero (2.5 gives 3, -2.5 gives -3). `decimal` and `float` to `money`: rounded half away from zero to four places. `decimal` to a smaller scale: rounded half away from zero (`1.25` in `decimal(10,1)` is `1.3`). `money` to `decimal`: rounded (`1.2399` in `decimal(10,2)` is `1.24`; `CAST(... AS decimal)` with no precision is `decimal(18,0)`). An assignment to a column converts the same way as `CAST` | Truncate or round, per pair: "CAST and CONVERT", the "Truncating and rounding results" table (its general sentence "the value is rounded" is wrong for the truncate rows, and "money and smallmoney" says "truncated" where the server rounds; the server and the table agree). The table does not give the tie direction, and has no row for `float` to `money`: those are documentation silent; reference observations R07a to R07e (ties, and `float` to `money` in R07c), R08a to R08f (ties to a smaller scale, and the assignment), R09a to R09d |
| S9 | Casing | Depends on the collation, and differs from .NET's invariant casing: `UPPER(N'i' COLLATE Turkish_CI_AS)` is `İ`; `LOWER(N'İ')` is `i`; `UPPER(N'µ')` and `UPPER(N'ß')` are unchanged; `UPPER(N'ǆ')` is `Ǆ` | Documentation ambiguous ("Collation Precedence" lists `UPPER` and `LOWER` as collation-sensitive; their own pages are silent); reference observation R10 |
| S10 | `ROUND` beyond its type | An error (8115) for a positive or zero length too, and for money; the type is not widened: `ROUND(9.995, 2)`, `ROUND(CAST(99.99 AS decimal(4,2)), 1)`, `ROUND(CAST(9.5 AS decimal(2,1)), 0)`. The result keeps the input's type (`ROUND(1.005, 1)` is `1.000`, `decimal(4,3)`). Ties go half away from zero | Documentation silent on a positive length ("ROUND", Remarks, shows only `ROUND(748.58, -3)`); reference observation R11 |
| S11 | `yyyy-MM-dd` to `datetime` | Depends on the session: under `SET DATEFORMAT dmy` or `SET LANGUAGE French`, `'2026-11-05'` becomes 11 May 2026. To `date`, and in the form `yyyyMMdd`, it does not | Documentation silent on this form ("datetime" lists only `yyyy-MM-ddTHH:mm:ss[.mmm]` and `yyyyMMdd` as unaffected by the setting); reference observation R12 |
| S12 | A date and time string with `Z` or an offset to `datetime` | With `Z`: accepted, the clock time kept. With an offset: error 241. A `datetimeoffset` converted to `datetime` keeps the clock time and drops the offset (`10:00+05:00` gives `10:00`) | "datetime" (no offset range); "datetimeoffset": "the time zone is truncated"; reference observations R13, R14 |
| S13 | `TRY_CAST` and `TRY_CONVERT` | `NULL` when the conversion fails, including overflow and too little precision (`TRY_CAST(12345.6 AS decimal(4,1))`). An error in the argument stays an error (`TRY_CAST(1/0 AS int)`), and so does a conversion that is not permitted (to `xml`, 529) | "TRY_CAST"; overflow and argument errors documentation silent; reference observation R15 |
| S14 | Text to `uniqueidentifier` | The 36-character dashed form is accepted, with or without braces. Without dashes, in parentheses or with a leading space: error 8169. A longer string is cut to 36 characters and accepted (`'...FFjunk'` gives the GUID). A string with only a closing brace after the dashed form is accepted too (the 37th character is cut) | "uniqueidentifier" documents the dashed form and the truncation; braces and other spellings documentation silent; reference observations R16, R17; the closing-brace case observed on the reference server in review, 2026-11-24, owed to the harness (R27c) |
| S15 | Conversion to a shorter text type | Text is cut silently (`CAST(N'abcde' AS nvarchar(3))` is `'abc'`). A whole number too long for `nvarchar(n)` is an error (8115); for `varchar(n)` it gives `'*'`. An assignment to a column drops trailing spaces beyond its length and refuses any other over-long value (2628) | "CAST and CONVERT", "Truncating and rounding results"; "SET ANSI_WARNINGS"; reference observation R18 |
| S16 | `CONCAT` length | The result is cut to 4,000 characters when every argument is `nvarchar(n)` and to 8,000 when every argument is `varchar(n)` | Documentation ambiguous ("CONCAT": "truncates to 8,000 characters", without the `nvarchar` case); reference observation R19 |
| S17 | `TRIM`, `LTRIM`, `RTRIM` | Remove only the space `char(32)`. Tab, line feed, carriage return, no-break space and ideographic space stay | "TRIM", "LTRIM", "RTRIM": "Removes the space character char(32)"; reference observation R20 |
| S18 | `LEN` | Excludes trailing spaces only: a trailing tab or no-break space counts; `LEN(N'   ')` is 0. `int`, and `bigint` when the argument is `varchar(max)` or `nvarchar(max)` | "LEN"; reference observation R21 |
| S19 | `float` comparison | At full binary precision, against a decimal or a float: `CAST(0.1 AS float) + CAST(0.2 AS float) = 0.3` is false; `CAST(1.0000000000000002 AS float) > 1.0` is true | Documentation silent ("float and real" gives 15 digits as the display precision only); reference observation R22 |
| S20 | `ABS` of a decimal | Keeps the input's own precision and scale: `ABS(CAST(1.5 AS decimal(5,2)))` is `decimal(5,2)`, `ABS(1.5)` is `numeric(2,1)`. `ABS` of the smallest `int` overflows | Documentation says `decimal(38,s)` ("ABS", Return types); reference observation shows otherwise (observed on the reference server in review, 2026-11-24, with `sp_describe_first_result_set` and `SQL_VARIANT_PROPERTY`); the server wins. Owed to the harness (R27a) |
| S21 | Text without the `N` prefix holding characters the code page cannot hold | Stored with each such character replaced by `?`: a literal of six Cyrillic letters assigned to an `nvarchar` column is stored as `'??????'` | "nchar and nvarchar", Remarks ("converted to the default code page of the database that might not recognize certain characters"); observed on the reference server in review, 2026-11-24, owed to the harness (R27d) |
| S22 | A column assigned to a column of another type | `money` to text keeps two decimal places (a result such as `'1234.57'`); `float` to text gives six significant digits (such as `'123457'`); `datetime` to text gives the default style (such as `'Nov  5 2026 10:20AM'`); text `'5'` to `int` gives 5; `2.7` to `int` gives 2 | "CAST and CONVERT" (default styles); observed on the reference server in review, 2026-11-24, owed to the harness (R27e) |

Further documented behaviour the rules must follow:

- `CASE` evaluates its conditions in order and stops at the first that is true ("CASE", Remarks), so `CASE WHEN x = 0 THEN 0 ELSE 1 / x END` raises no error. The reference server likewise does not evaluate `ISNULL`'s second value when the first is not `NULL` (#1878).
- `ISNULL` converts its second value to the first value's type and shortens it to the first value's length ("ISNULL": "replacement_value can be truncated").
- `CONCAT` turns `NULL` into an empty string, and returns `''` when every argument is `NULL` ("CONCAT"); `SUBSTRING` with a start past the end returns `''` ("SUBSTRING"). Both can produce the empty string that core requirement 4 refuses.
- Conversion of `float` to text uses 6 significant digits by default, and `CAST` to `nvarchar` without a length is `nvarchar(30)` ("CAST and CONVERT"). The 2.0 floor lists neither; a rule that covers them states them.
- Text that is not a number fails conversion to a number ("CAST and CONVERT"); the engine's .NET parsing accepts thousands separators, currency symbols and parentheses today. The 2.0 floor does not convert text to a number.

#### Observations owed to the harness

The rows above marked "owed to the harness" were observed on the reference server during the review of this spec, not by the harness. Until each is pinned in `ReferenceObservations`, the rule that depends on it is not proven (item 1 of [What "a proven type rule" means](#what-a-proven-type-rule-means)). Adding them is a follow-up to #1798, not yet filed: R27a (S20, `ABS`), R27b (S3, `- 5` and `-(2147483648)`), R27c (S14, the closing brace), R27d (S21, text without `N`), R27e (S22, column to column of another type), R27g (text without `N` whose characters are all 7-bit ASCII, U+0000 to U+007F, is stored unchanged, admission row B06) and R27h (S7, a money multiplication tie).

The same-type assignments the admission rows rest on, which no observation pins yet. Each is one batch for `ReferenceObservations` (run on SQL Server only), with what it is expected to show; a row that cites one stays not in force until it is pinned:

| Id | Batch | Shows |
|----|-------|-------|
| R28a | `DECLARE @t TABLE (c nvarchar(20)); INSERT @t VALUES (N'abc'); UPDATE @t SET c = N'x y'; SELECT c, DATALENGTH(c) AS bytes FROM @t;` | `'x y'`, 6: an `nvarchar` literal is stored exactly in an `nvarchar(n)` column |
| R28b | The same with `nvarchar(max)` and a 5,000-character literal | Stored exactly, `LEN` 5000 |
| R28c | `DECLARE @t TABLE (a nvarchar(20), b nvarchar(max)); INSERT @t VALUES (N'abc', NULL); UPDATE @t SET b = a; UPDATE @t SET a = b; SELECT a, b FROM @t;` | Both `'abc'` |
| R28d | `DECLARE @t TABLE (i int); INSERT @t VALUES (42), (-5), (2147483647), (-2147483648); SELECT i FROM @t;` | The four values exactly |
| R28e | `DECLARE @t TABLE (a int, b int); INSERT @t VALUES (42, 0); UPDATE @t SET b = a; SELECT b FROM @t;` | 42 |
| R28f | `DECLARE @t TABLE (m money); INSERT @t VALUES (12), (-2147483648); SELECT m FROM @t;` | `12.0000`, `-2147483648.0000` |
| R28g | `DECLARE @t TABLE (a money, b money); INSERT @t VALUES (1.2346, 0); UPDATE @t SET b = a; SELECT b FROM @t;` | `1.2346` |
| R28h | `DECLARE @t TABLE (a float, b float); INSERT @t VALUES (47.123456789, 0); UPDATE @t SET b = a; SELECT b FROM @t;` | The same `float` |
| R28i | `DECLARE @t TABLE (b bit); INSERT @t VALUES (0), (1), (5); SELECT b FROM @t;` | 0, 1, 1 |
| R28j | `DECLARE @t TABLE (s nvarchar(20), m nvarchar(max), i int, d decimal(10,2), y money, f float, g uniqueidentifier, t datetime, o date); INSERT @t VALUES (N'a', N'a', 1, 1.5, 1, 1, NEWID(), '2024-01-15T09:30:00', '2024-01-15'); UPDATE @t SET s = NULL, m = NULL, i = NULL, d = NULL, y = NULL, f = NULL, g = NULL, t = NULL, o = NULL; SELECT * FROM @t;` | `NULL` in each |
| R28k | `SELECT SWITCHOFFSET(CAST('2024-01-15T09:30:00+05:00' AS datetimeoffset(0)), '+00:00') AS utc;` | `2024-01-15 04:30:00 +00:00`: the instant a date and time literal with an offset names, which rows B28 and C07 send as UTC. The conversion itself is the rule's own, not SQL Server's `datetime` conversion (core requirement 4); the observation pins only the instant |
| R28l | `SELECT 12345678901234567890123456789012345678 AS d38;` then, as a separate batch, the same with a 39th digit | The 38-digit literal is a value; the 39-digit one raises error 1007 (maximum precision 38), whatever its value. #1698's reference tests record this; the harness does not yet |
| R28m | `DECLARE @t TABLE (f float); INSERT @t VALUES (47.12345), (-0.0); SELECT f FROM @t;` | `47.12345` exactly as a `float`, and `0` for `-0.0`: a decimal literal whose digits a `float` holds is stored with those digits (row B27) |

### SQL types

The SQL type kinds are a closed list. A column whose type is not on it, a conversion to a type not on it, and a literal whose type is not on it have no type rule: no admission row admits them, so they refuse a script that writes (`Query.DmlUnsupportedConstruct`, before its first statement), and they are computed by the labelled fallback in a script that does not. The list is extended only by a change to this spec.

| Kind | Needed for |
|------|------------|
| `int` | whole-number columns, and whole-number literals from -2,147,483,648 to 2,147,483,647 ([What the reference server showed](#what-the-reference-server-showed), S2 and S3) |
| `bigint` | big-integer columns; `LEN` of a `max` type (S18) |
| `decimal(p,s)` (SQL Server's `numeric` is the same type: "decimal and numeric are synonyms") | decimal columns, decimal literals, whole-number literals too large for `int` |
| `money` | currency columns |
| `float` | floating-point columns |
| `bit` | yes/no columns |
| `nvarchar(n)` and `nvarchar(max)` | text and multi-line text columns, literals written with the `N` prefix |
| `varchar(n)` | text literals written without the `N` prefix (#1804); `''` is `varchar(1)` (S4) |
| `datetime` | date and time columns. A date literal is a text literal ("Constants", "Date-time constants") that is converted to the target's type. SQL Server's own conversion of text to `datetime` depends on the session language and refuses an offset (S11 and S12), so the conversion for each date column behaviour is defined by its rule (#1806, core requirement 4), not taken from a cast on a SQL Server session |
| `uniqueidentifier` | id and lookup columns |
| the type of `NULL` | the `NULL` constant. SQL Server types it `int` where nothing else gives it a type (S5) |

The "Needed for" column says why a kind is on the list. It is not the column type map: which kind a Dataverse column has is established by the probe (#1799) and recorded in the map (#1800). If the probe of the endpoint reports a type that is not on this list (for example another date and time type), that column kind is refused until this list is extended.

### Core Requirements

1. A script that contains a statement that writes ([the whole-script rule](#the-whole-script-rule)) is refused, before its first statement runs, when anything in it is not a row in force of the [admission table](#admission-in-statements-that-write). No row is read and nothing is written; what is read before the refusal is the column metadata and, for a collation-dependent rule, the organisation's collation. The code is `Query.DmlUnsupportedConstruct`. The message names the part no row admits by its kind and, for a function, by its name (for example "the function DATEADD", "a CONVERT style", "a money literal"), says whether a row for it exists but is not in force yet, and says where it is in the script (line and column). It does not repeat literal values from the statement.
2. A value is computed in the SQL type the binder gave its node. Overflow, divide by zero and a conversion that fails are errors for the whole statement wherever SQL Server raises an error for them under the reference server's session settings (see [Reference server](#reference-server)), unless the construct's own rule says otherwise (`TRY_CAST` and `TRY_CONVERT`, S13). In a statement that writes, such an error means nothing is written. In a `SELECT`, the error is raised when it is reached: rows already returned stay returned, and every surface marks the result incomplete.
3. Before the first write, every value for every row the statement would write is converted as the admission row that admits it says, and then checked against the column's limits (defined in [Limits the assignment check covers](#limits-the-assignment-check-covers)). A failure is refused with `Query.DmlColumnLimitExceeded` in this pass, before the statement's first write. Decimal places are handled by the row: the row says how they are rounded, or that a value with more places than the column keeps is refused. For now, no row rounds (rows B11 and B13): such a value is refused with `Query.DmlValueStoredDifferently`. The reason is that no live case has yet shown what Dataverse stores for a rounded value. A text value is never shortened: a text value longer than the column's maximum length is refused, not cut to fit (SQL Server's assignment would drop trailing spaces beyond the length, S15; PPDS refuses those as well). The computed values are held until every row has passed, and the held values are what is written; nothing is computed again for the write. The DML row cap ([query.md](./query.md)) bounds this pass: a statement over the cap is refused with `Query.DmlRowCapExceeded` before the pass starts. When the cap is lifted (`--no-limit`), the pass is still bounded by the engine's existing in-memory limit, `MaxMaterializationRows` of the query plan context (500,000 rows by default), which `--no-limit` does not lift: a statement whose pass would hold more rows than that fails with `Query.MemoryLimitExceeded` before its first write (Decision L).

   For now, the DML safety work (#1698) does not build this check:
   - It does not check a value's length or a number's minimum and maximum before the first write. The live cases of 2026-11-24 on the demo environment showed both reaching Dataverse. Dataverse refused a 21-code-unit value in a column of maximum length 20 (L04), and -5 in a column whose minimum is 0 (L08), after the dry run had foreseen neither.
   - Of the limits #1698 does check (a choice that is not one of the column's options, a number outside the range of its kind), each is refused with `Query.DmlUnsupportedConstruct`, because `Query.DmlColumnLimitExceeded` is not defined on that branch.

   Both are owed before a row that relies on this check is in force.
4. Where Dataverse would store something other than the value sent, the statement is refused with a message that says what to write instead. The rule is to refuse exactly what Dataverse does not store unchanged (Decision I), and each case is established by a live write and read-back. The known case is the empty string, which Dataverse stores as `NULL`: a write whose value is an empty string is refused with `Query.DmlValueStoredDifferently`, and the message says to write `NULL`. A literal `''` is refused before the script's first statement; a column copied from a row where it is empty is refused in the statement's pass, before its first write, and refuses the whole statement. A further case is added only by a change to this spec, with its live case.

   Text with White_Space at either end (Decision I). A text value made only of White_Space, or beginning or ending with a character of the Unicode White_Space property (U+0009 to U+000D, U+0020, U+0085, U+00A0, U+1680, U+2000 to U+200A, U+2028, U+2029, U+202F, U+205F, U+3000), is written only through rows B25 (String of format Text) and B26 (Memo) of the admission table. Until the row for the target is in force, such a value is refused with `Query.DmlValueStoredDifferently`: before the script's first statement for a literal, and in the statement's pass for a column value. The message names the column and says that such values are not written until Dataverse has been shown to store them unchanged. For every other text format no row exists, so such a value is always refused there.

   Live case L27 ran on 2026-11-24 on the demo environment, on the executor's path: the SDK messages `UpdateMultiple` and `CreateMultiple`, sent through `DmlExecuteNode`. Five values were written to a String and a Memo column by `UPDATE` and by `INSERT`: three spaces, `abc` followed by two spaces, `abc` after two spaces, `abc` followed by a tab, and `abc` followed by a line feed. Each was read back exactly (20 of 20). So rows B25 and B26 have their Dataverse evidence for those five values. Before either row is in force, these are still owed:
   - its SQL Server observation (R28a, R28b);
   - Dataverse evidence for the other White_Space characters at either end (carriage return, no-break space, U+3000 and the rest), or a narrowing of the row to space, tab and line feed;
   - the change to this spec that brings the row into force.

   An earlier observation was not on the executor's path: through the Dataverse Web API, on a test environment, on 2026-11-24, three spaces in `account.accountnumber` were returned unchanged. The empty-string rule above is unchanged; the empty string itself was not part of L27. SQL Server ignores trailing spaces in `LEN` (`LEN(N'   ')` is 0, reference observation R21e) and in `=`, so it does not tell such a value from `''`; that says nothing about what Dataverse stores.

   For a date and time column, "the value sent" is the value after the conversion Dataverse documents for the column's behaviour (user local, date only, time zone independent): a date write is stored unchanged when the value read back equals that converted value. The rule for each behaviour names the documentation page that states its conversion and has its own live case (#1806); a behaviour without both is refused. The literal must leave no room for interpretation. For a user-local column it carries `Z` or an explicit offset, and a literal without one is refused, because the stored value would otherwise depend on the calling user's time zone; the offset is applied to give the UTC time. SQL Server's conversion to `datetime` refuses an offset or drops it (S12), so this conversion is the rule's own, not SQL Server's. A date-only literal `yyyy-MM-dd` is read as year, month and day whatever the session language, which SQL Server's `datetime` conversion does not guarantee (S11). This binds #1806 and every later rule: whatever further literal forms are admitted, a user-local literal without an offset is not one of them. A date literal whose form has no rule for the column's behaviour, the user-local literal without `Z` or an offset among them, is refused before the script's first statement runs with `Query.DmlUnsupportedConstruct`: the refusal depends only on the literal's text and the metadata.

   For now, the forms are exactly those the DML safety work (#1698) builds, as rows B28 to B30 and C07 of the admission table:
   - a user-local date and time column takes exactly `yyyy-MM-ddTHH:mm:ss` followed by `Z` or `±hh:mm` (an offset of at most 14:00), a real date and time from 1753 to 9999. It is sent as its UTC instant, whether written or compared in the `WHERE` of a write: `'2024-01-15T09:30:00+05:00'` is sent as `2024-01-15T04:30:00Z`;
   - a date-only column takes exactly `yyyy-MM-dd`, a real date from 1753.

   No fraction of a second is admitted for now, because the digits Dataverse keeps are not established. The letters `T` and `Z` are upper case. Time-zone-independent columns, and user-local columns shown as date only, have no row. #1806 adds further forms and behaviours, each with its live case. Until a row's live case has run, every date literal written or compared in a statement that writes is refused, because no date row is in force.
5. In a script that does not write, computed values use the typed path wherever every part of the expression has a proven type rule. An expression with a part that has no proven rule is computed, as a whole, by the untyped fallback (Decision J), and labelled as [the whole-script rule](#the-whole-script-rule) says: per computed column, for the result as a whole when a filter, join condition, grouping, `HAVING` condition or sort evaluated in PPDS used it, and for every later result of the script when an earlier statement used it for anything other than its own returned columns. Every surface shows that label. The fallback is never used in a script that writes. No query that returned the right result before returns a different result; each change is a correction with proof or a new clear error.
6. The rule table is the only list. The admission tests, the differential generator and the documented list of supported constructs are generated from it. The documented list is a reference page for users, generated from the rule table by the documentation generators under `scripts/docs-gen/` ([docs-generation.md](./docs-generation.md)); its path is recorded here when the generator for it exists. Today the harness keeps its own `RuleTable` of what it generates; it reads the product's rule table once that exists.
7. No other code decides whether a value expression is allowed or what type it has. No construct is admitted by one component and typed differently by another.
8. At the 2.0 release the rule table covers, for DML, every construct in [Constructs required for 2.0](#constructs-required-for-20), for the operand type combinations and the column kinds listed there.
9. A column, a conversion target or a literal whose SQL type is not on the closed list in [SQL types](#sql-types) is treated as a construct outside the rule table: no admission row admits it, so it refuses a script that writes (`Query.DmlUnsupportedConstruct`, before the first statement), and it is computed by the labelled fallback in a script that does not.
10. The rule table records, for each text rule whose result depends on collation, the collation it was proven under (casing does depend on it: S9). Such a rule applies only if the organisation's collation has been established and is one the rule was proven under. The collation is established once per script, before its first statement runs, as part of binding (a read of metadata, not of rows), and a script that writes and needs a rule whose collation is not established is refused at that point, with `Query.DmlUnsupportedConstruct`, as any other unproven construct is. When the collation cannot be established, or is another one, the rule counts as not proven: a script that writes is refused, and in a script that does not write the expression goes to the labelled fallback (Decision K). How the collation is established is identified by #1799 and #1804 and recorded here; until a means is recorded, it cannot be established. `UPPER` and `LOWER` are in the 2.0 floor (core requirement 8), so a means of establishing the collation is itself required for 2.0: without one, those two constructs could not be admitted in a script that writes.

What the check before the first write does not cover: Dataverse can still reject a row or a later batch for a reason that is not a value's type or limits (a plug-in, a required column, a privilege, a duplicate rule). Earlier batches are then already written. This spec removes the failures that can be known from the statement and the column metadata; it does not make a multi-batch write atomic.

#### Admission in statements that write

**The one rule.** In a statement that writes, and in every statement of a script that contains one, anything that is not a row of the admission table below whose status is "in force" is refused before the script's first statement runs, with `Query.DmlUnsupportedConstruct`. No other sentence of this spec admits anything into a statement that writes; where another section describes how a value is converted or checked, it describes what an admitted row does.

This is how the interim rule ("Literals, columns, NULL", the recorded decision; Decision H1 for negative numbers and the GUID cast) is carried out, and how the typed path enters writes later: a construct of the rule table enters a statement that writes only as a row of this table. The DML safety work (#1698) lands on `main` cut down to this table (Decision H2); every other construct its branch admits in a value returns to DML one row at a time, as the construct's rule is proven and the row's live case passes. The table is decided and not yet in force on `main`: today `main` computes any value expression without a check.

**What is in force today: nothing.** A row is in force only when it has both kinds of evidence: SQL Server evidence (a documentation page, or a reference observation of the differential harness) and Dataverse evidence (a dated live case run through the engine's own write path, the SDK messages `CreateMultiple`, `UpdateMultiple` and `DeleteMultiple` sent by `DmlExecuteNode` through the bulk operation executor). The first live cases ran on that path on 2026-11-24, on the demo environment, on the build of #1698. Thirty of the 35 cases then listed were run, and five were not, because each needs a test table. [Evidence by row](#evidence-by-row) gives what each row has and what it still owes. The change that records that run brings no row into force; the next change does that, row by row, from that table. Most rows still owe a SQL Server observation or a case. Rows A01 to A03 owe only that change, and they are of use only beside rows of B and C in force. So, once the interim rule takes effect, every statement that writes is refused until rows are in force, and they come into force one by one. Each row that comes into force is a change to this spec that records its case's date and result.

For now, #1698 admits forms whose rows are not in force here: it is built to this table's forms, ahead of their evidence. This spec does not count what #1698 admits as evidence for any row.

**How a row is read.** A row admits a form, not a value. A value that matches a row's form is then checked against the column's limits (core requirement 3) and against what Dataverse stores (core requirement 4). A literal's value is known before the script runs, so a literal is checked then; a column's value is checked in the statement's pass before its first write. A value outside the column's limits is refused with `Query.DmlColumnLimitExceeded`, and a value Dataverse stores or may store differently with `Query.DmlValueStoredDifferently`, each at that time. What a row requires of the form (a literal's spelling, an operand that is not empty, a column's precision source known from the metadata, a table's kind) is part of the form: a statement that does not meet it matches no row.

A literal matches a row only in the one exact form its target column's kind admits, as #1698 builds it: one grammar per kind, anchored at both ends, with no general-purpose parser deciding admission. It matches only when the value that will be sent, computed as the executor computes it, loses nothing: written back as text it has the same digits, or it is the same instant. For now, a literal of an admitted form whose value would be sent with fewer decimal places than it has is refused with `Query.DmlValueStoredDifferently`. That covers a decimal with more places than a Decimal or Money column keeps, and digits a floating-point value cannot hold. It stays so until a row that rounds (B11, B13) is in force.

**Scope of a row.** A row is in force per operation: a value row (B) for `UPDATE` once its `UPDATE` case has passed, and for `INSERT` once its `INSERT` case has passed; a condition row (C) for `UPDATE`, `DELETE` and `INSERT ... SELECT` each once a case of that operation has passed. The statement forms (A) apply to standard tables only: a table whose metadata `TableType` is `Standard` and `IsActivity` is false. An elastic, virtual or activity table matches no form, because each is written by a different path (an elastic table's partitioned bulk messages, a virtual table's data provider, an activity's party lists) that no case has run; a row for one needs its own case.

**Order of refusals before the first statement.** When more than one would fire, the first in this order is the one raised: (1) the script cannot be parsed; (2) the DML safety guard, `Query.DmlBlocked` (for example `DELETE FROM account` without `WHERE`); (3) DML without metadata, `Query.DmlMetadataUnavailable`, since without metadata no row can be matched; (4) a literal's value refused by a row it matches (`Query.DmlColumnLimitExceeded` or `Query.DmlValueStoredDifferently`, see "How a row is read"); (5) not admitted by the table, `Query.DmlUnsupportedConstruct`. Only then does the safety guard ask for confirmation (`Query.DmlConfirmationRequired`), so a statement that would be refused is never put to the user.

Terms used in the rows:

- A *text literal* is a ScriptDom `StringLiteral` with the `N` prefix. It may also be written without `N` when every character is 7-bit ASCII (U+0000 to U+007F), as a written value (row B06) or as an operand (rows C01 and C03). Text without `N` holding any other character matches no row, as a written value or as an operand in the `WHERE` of a write. The maintainer decided "Refuse it" for a written value and "Yes, refuse it" for an operand in the `WHERE` of an `UPDATE` or `DELETE`. The message names the `N'...'` form. SQL Server converts such a literal to the database's code page (S21), and PPDS cannot establish the collation. Drawing the line at any character outside 7-bit ASCII is the coordinating session's conservative reading of "outside the code page", not the maintainer's words: it also refuses an accented Latin letter that a Latin code page would keep. #1698 applies the same refusal to the `WHERE` of an `INSERT ... SELECT`.
- A *number literal* is written with at most 38 digits; leading zeros and zeros at the end of the fraction count. One with more digits matches no row, written or compared: SQL Server refuses it whatever its value (error 1007, maximum precision 38; owed: R28l). For now it must also be held exactly by the executor's .NET decimal (at most 28 significant digits), because the executor carries numbers in that type.
- A *whole-number literal* is an `IntegerLiteral` whose value is from -2,147,483,648 to 2,147,483,647, or a `UnaryExpression` of type `Negative` directly over one (`-5`, `- 5`); its type is `int` (S2, S3). A minus over a parenthesised literal (`-(5)`) or over another minus (`-(-5)`) is not one, as #1698 builds it. An `IntegerLiteral` outside that range is typed `decimal(p,0)` (S2): it is a decimal literal for these rows, not a whole-number literal.
- A *decimal literal* is a `NumericLiteral` (digits with a decimal point) or an `IntegerLiteral` outside the `int` range, or one minus directly over either. A `RealLiteral` (`1E0`) and a `MoneyLiteral` (`$1`) are not decimal literals for these rows. A decimal literal matches no row whose target is an Integer column (`SET numberofemployees = 2.7`: its fraction would be lost).
- A *GUID literal* is a text literal, with or without the `N` prefix, whose text is the 36-character dashed form, with or without both braces (S14). Its characters are all from U+0020 to U+007E, so the prefix makes no difference. No other spelling is one.
- A *column* is a column of the target table, in the environment the statement writes to, whose column metadata `SourceType` is simple. In an `INSERT ... SELECT` the source table is the target table itself (row A04).
- A String column's *format* is its metadata `FormatName`, not `Format`. On the demo environment on 2026-11-24, `account.telephone1` had `Format` Text and `FormatName` Phone, so a binder that reads `Format` would treat a phone column as row B01's target. A virtual label column, a calculated or rollup column, a date and time column and a column of another environment are not columns for this table.
- A *target column* must be valid for the operation by its metadata (`IsValidForUpdate` for `UPDATE`, `IsValidForCreate` for `INSERT`); a column that is not matches no row. A column named twice in one `SET` list or one `INSERT` column list matches no form (SQL Server raises error 264 for it).
- *L01* and so on are the live cases, listed in [Live cases the table needs](#live-cases-the-table-needs). *R28a* and so on are reference observations the harness does not hold yet, listed in [Observations owed to the harness](#observations-owed-to-the-harness); a row that cites one stays not in force until it exists.

| ID | Part | Form, and target | What is written, or how it is evaluated | SQL Server evidence | Dataverse evidence | Criterion | Status |
|----|------|------------------|------------------------------------------|---------------------|--------------------|-----------|--------|
| A01 | Statement | `UPDATE t SET c = v [, ...] WHERE ...` on a standard table: no `FROM`, `TOP`, `OUTPUT`, hint or `ORDER BY`; each `c = v` a row of B for `UPDATE`, the condition made of rows of C | `UpdateMultiple` of the rows the condition selects | "UPDATE (Transact-SQL)" | L01 | AC-13 | Not in force until L01 |
| A02 | Statement | `DELETE FROM t WHERE ...` on a standard table: no `FROM` join, `TOP`, `OUTPUT` or hint; the condition made of rows of C for `DELETE` | `DeleteMultiple` of the rows the condition selects | "DELETE (Transact-SQL)" | L02 | AC-13 | Not in force until L02 |
| A03 | Statement | `INSERT INTO t (c, ...) VALUES (...) [, (...)]` on a standard table: no `OUTPUT` or hint; each value a row of B for `INSERT` | `CreateMultiple` of the rows | "INSERT (Transact-SQL)" | L03 | AC-13 | Not in force until L03 |
| A04 | Statement | `INSERT INTO t (c, ...) SELECT c, ... FROM t [WHERE ...]` on a standard table: a copy within the same table only (the source is the target table, optionally aliased); no join, grouping, `DISTINCT`, `UNION`, `OUTPUT` or hint; each projection a column of a row of B for `INSERT`; the condition made of rows of C for `INSERT ... SELECT`; `TOP` and `ORDER BY` only together, as A05. From another table it matches no form (the maintainer, 2026-11-25: "Refuse both, fix the spec") | `CreateMultiple` of one row per source row | "INSERT (Transact-SQL)"; "SELECT (Transact-SQL)" | L14 | AC-07, AC-13 | Not in force until L14 |
| A05 | Clause | `TOP n` or `TOP (n)` in the `SELECT` of A04, n a whole-number literal from 0 to 5,000, without `PERCENT` or `WITH TIES`, and only with `ORDER BY` on one or more columns of the source, ascending or descending. The columns must be of the whole-number, text, user-local date and time or date-only kind, and none may be the name of another `SELECT` item's alias. `ORDER BY` without `TOP`, and `TOP` without `ORDER BY`, match no form (the maintainer, 2026-11-25: "Refuse both, fix the spec") | The first n source rows in that order (`NULL` first when ascending; text in the collation's order); among rows equal in the order, no stated choice | "TOP (Transact-SQL)"; "SELECT - ORDER BY Clause" | L15 | AC-35 | Not in force until L15. Whether the sort is made by Dataverse (a FetchXML `order`) or in PPDS is not stated here and is owed before the row is in force. A `TOP` of exactly 5,000 out of more rows is not proven |
| B01 | Value | Text literal, to a String column of format Text | The text unchanged; its length counted in UTF-16 code units | Owed: R28a (an `nvarchar` value assigned to an `nvarchar` column is stored exactly) | UPDATE: L01, L04. INSERT: L03 | AC-05, AC-10, AC-13 | Not in force until R28a; for UPDATE until L01 and L04, for INSERT until L03 |
| B02 | Value | Text literal, to a String column of format Email | The text unchanged | Owed: R28a | UPDATE: L16. INSERT: L28 | AC-13 | Not in force until R28a; for UPDATE until L16, for INSERT until L28 |
| B03 | Value | Text literal, to a String column of format URL | The text unchanged | Owed: R28a | UPDATE: L16. INSERT: L28 | AC-13 | As B02 |
| B04 | Value | Text literal, to a String column whose `FormatName` is Phone (its `Format` is Text) | The text unchanged | Owed: R28a | UPDATE: L16. INSERT: L28 | AC-13, AC-42 | As B02 |
| B05 | Value | Text literal, to a Memo column | The text unchanged | Owed: R28b (the same for `nvarchar(max)`) | UPDATE: L05. INSERT: L28 | AC-05, AC-13 | Not in force until R28b; for UPDATE until L05, for INSERT until L28 |
| B06 | Value | Text literal without the `N` prefix whose characters are all 7-bit ASCII (U+0000 to U+007F), to the targets of B01 and B05 | The same text | "nchar and nvarchar", Remarks; S21; owed: R27g | UPDATE: L06. INSERT: L28 | AC-23 | Not in force until R27g; for UPDATE until L06, for INSERT until L28. A literal without `N` holding any other character matches no row (the maintainer: "Refuse it"; the 7-bit line is the coordinating session's conservative reading, see the terms above) |
| B07 | Value | A String or Memo column, to a String column of format Text or a Memo column | The text unchanged, after the length check | Owed: R28c (an `nvarchar` column assigned to an `nvarchar(max)` column and back) | UPDATE: L07 (String to Memo and Memo to String). INSERT: L32 | AC-05, AC-13 | Not in force until R28c; for UPDATE until L07, for INSERT until L32 |
| B08 | Value | Whole-number literal, to an Integer column of format None | The number unchanged | Owed: R28d (an `int` literal assigned to an `int` column) | UPDATE: L08. INSERT: L29 | AC-05, AC-13 | Not in force until R28d; for UPDATE until L08 has run on a column that admits negative numbers, for INSERT until L29 |
| B09 | Value | An Integer column, to an Integer column of format None | The number unchanged | Owed: R28e (an `int` column assigned to an `int` column) | UPDATE: L09. INSERT: L32 | AC-13 | Not in force until R28e; for UPDATE until L09, for INSERT until L32 |
| B10 | Value | Whole-number literal, to a Decimal column | The number exactly | "decimal and numeric": conversion from `int` "can cause overflow" only, which the range check refuses | UPDATE: L10. INSERT: L33 | AC-05, AC-27 | For UPDATE not in force until L10, for INSERT until L33 |
| B11 | Value | Decimal literal, to a Decimal column of precision p (10 when the metadata gives none) | For now: a literal with at most p places is sent exactly, and one with more places is refused with `Query.DmlValueStoredDifferently` before the script runs, as #1698 builds it. The row's rule once L10 has shown what Dataverse stores: rounded to p places, a tie away from zero, as SQL Server converts (the decision of 2026-11-21, "Convert as SQL Server does") | S8 (R08d to R08f) | UPDATE: L10. INSERT: L33 | AC-27 | As B10; the rounding stays refused until L10 has passed |
| B12 | Value | Whole-number literal, to a Money column whose precision source and precision are known from the metadata | The number exactly | "money and smallmoney" (accurate to a ten-thousandth); owed: R28f (an `int` literal assigned to a `money` column) | UPDATE: L11. INSERT: L29 | AC-05, AC-27 | Not in force until R28f; for UPDATE until L11, for INSERT until L29 |
| B13 | Value | Decimal literal, to a Money column whose precision source and precision are known from the metadata | The maintainer, 2026-11-25: "Round like SQL Server, then to the column". First, as SQL Server converts a decimal to `money`: rounded to four places, a tie away from zero (S8, R07a, R07b). Then the four-place value is rounded to the column's precision, a tie away from zero, before it is sent, so that the value sent is the value Dataverse stores (core requirement 4). So `1.23495` is `1.2350` and then, at precision 2, `1.24`; `-1.23495` is `-1.2350` and then `-1.24`. The value must lie within ±922,337,203,685,477.5807. Until this row is in force, a literal with more places than the column keeps (four at most) is refused with `Query.DmlValueStoredDifferently` before the script runs, as #1698 builds it; one with no more places is sent exactly | First step: S8, R07a, R07b. Second step: not SQL Server's; its Dataverse evidence is owed from L11 (what Dataverse stores for a four-place value at precision 2) | UPDATE: L11. INSERT: L29 (its Money part) | AC-27 | For UPDATE not in force until L11, for INSERT until L29's Money part; until then the rounding is refused |
| B14 | Value | A Money column of the same row (`UPDATE`), to a Money column with the same precision source and precision | The value unchanged | Owed: R28g (a `money` column assigned to a `money` column) | UPDATE: L12. INSERT: no row (the currency of a new row is not known from the source) | AC-13 | Not in force until R28g and L12; for INSERT, no row |
| B15 | Value | Whole-number literal, to a Double column | The number exactly (every `int` is exact in `float`, whose 53-bit significand holds more than 31 bits) | "float and real" (15 digits, more than a 10-digit `int` needs) | UPDATE: L13. INSERT: L29 | AC-05, AC-27 | For UPDATE not in force until L13, for INSERT until L29 |
| B16 | Value | A Double column of the same row (`UPDATE`), to a Double column of the same precision | The value unchanged | Owed: R28h (a `float` column assigned to a `float` column) | UPDATE: L13. INSERT: no row | AC-13 | Not in force until R28h and L13; for INSERT, no row |
| B17 | Value | Whole-number literal, to a Boolean column | 0 or 1. Any other value is refused with `Query.DmlColumnLimitExceeded` before the script runs, although SQL Server would store 1 for any nonzero value (owed: R28i). The maintainer, 2026-11-25: "Refuse it". `-0` is not 0 for this row. #1698 refuses such a value with `Query.DmlUnsupportedConstruct` for now (core requirement 3) | Owed: R28i (`int` to `bit`: 0, 1, and 5 giving 1) | UPDATE: L17. INSERT: L29 | AC-30 | Not in force until R28i; for UPDATE until L17, for INSERT until L29 |
| B18 | Value | Whole-number literal, to a Picklist column | The value, which must be one of the column's options (otherwise a limit failure) | Owed: R28d | UPDATE: L18. INSERT: L29 | AC-31 | Not in force until R28d; for UPDATE until L18, for INSERT until L29 |
| B19 | Value | Whole-number literals to a State and a Status column, both set in an `UPDATE` | The pair, which must be valid by the metadata (otherwise a limit failure) | Owed: R28d | UPDATE: L19. INSERT: no row (a created row takes the default state) | AC-32 | Not in force until R28d and L19; for INSERT, no row |
| B20 | Value | `CAST` of a GUID literal to `uniqueidentifier`, to a Lookup column with one target table | The GUID | S14 (R16a) | UPDATE: L20. INSERT: L30 | AC-33 | For UPDATE not in force until L20, for INSERT until L30 |
| B21 | Value | `NULL`, to a String or Memo column | The value cleared | Owed: R28j (`NULL` assigned to a column of each type) | UPDATE: L21. INSERT: L31 | AC-13 | Not in force until R28j; for UPDATE until L21, for INSERT until L31 |
| B22 | Value | `NULL`, to an Integer, Decimal, Money or Double column | The value cleared | Owed: R28j | UPDATE: L21 (Decimal: L10). INSERT: L31 (Decimal: L33) | AC-13 | As B21 |
| B23 | Value | `NULL`, to a Picklist column | The value cleared | Owed: R28j | UPDATE: L21. INSERT: L31 | AC-13 | As B21 |
| B24 | Value | `NULL`, to a Lookup column | The value cleared | Owed: R28j | UPDATE: L20. INSERT: L31 | AC-33 | Not in force until R28j; for UPDATE until L20, for INSERT until L31 |
| B25 | Value | A text literal made only of White_Space, or beginning or ending with a White_Space character (core requirement 4), to a String column of format Text. A column is not a source for this row | The text unchanged | Owed: R28a | UPDATE: L27. INSERT: L27 | AC-10 | Not in force until R28a and L27. Until then such a value is refused (core requirement 4) |
| B26 | Value | The same text literal, to a Memo column. A column is not a source for this row | The text unchanged | Owed: R28b | UPDATE: L27. INSERT: L27 | AC-10 | Not in force until R28b and L27; refused until then |
| B27 | Value | Decimal literal, to a Double column | The number, when the `double` sent has exactly the literal's digits; otherwise refused with `Query.DmlValueStoredDifferently` before the script runs. A negative zero is sent as 0, as SQL Server stores it. Whether Dataverse rounds a value to the column's precision is what L37 shows | Owed: R28m | UPDATE: L37. INSERT: L37 | AC-27 | Not in force until R28m and L37 |
| B28 | Value | A text literal of exactly `yyyy-MM-ddTHH:mm:ss` followed by `Z` or `±hh:mm` (at most 14:00), upper-case `T` and `Z`, a real date and time from 1753 to 9999, to a DateTime column of behaviour user local and format date and time | The UTC instant the literal names, sent as `yyyy-MM-ddTHH:mm:ssZ`. No fraction of a second for now: the digits Dataverse keeps are not established (core requirement 4) | Not SQL Server's conversion (core requirement 4); owed: R28k for the instant | UPDATE: L34. INSERT: L34 | AC-26 | Not in force until R28k and L34 |
| B29 | Value | A text literal of exactly `yyyy-MM-dd`, a real date from 1753, to a DateTime column of behaviour date only | The date as written, whatever the session language | S11 (R12) | UPDATE: L35. INSERT: L35 | AC-26 | Not in force until L35 |
| B30 | Value | `NULL`, to a DateTime column of a behaviour B28 or B29 targets | The value cleared | Owed: R28j | UPDATE: L34, L35. INSERT: L34, L35 | AC-13 | Not in force until R28j, L34 and L35 |
| C01 | Condition | `c = g` or `c IN (g, ...)`, where c is the table's primary id column or a Lookup column and each g a GUID literal or `CAST` of one, sent to Dataverse as FetchXML | Dataverse selects the row or rows with that id | S14 | UPDATE: L01, L22. DELETE: L02, L41. INSERT ... SELECT: L14, L42 | AC-35 | For each operation, not in force until its cases |
| C02 | Condition | `c op n` with op one of `=`, `<>`, `<`, `<=`, `>`, `>=`, or `c IN (n, ...)`, c an Integer column, n whole-number literals, sent as FetchXML | Dataverse's comparison | "Comparison Operators (Transact-SQL)" | UPDATE: L22. DELETE: L41. INSERT ... SELECT: L42 | AC-35 | For each operation, not in force until its case |
| C03 | Condition | `c = s`, `c <> s` or `c IN (s, ...)`, c a String column, s text literals (with `N`, or without `N` when 7-bit ASCII; see the terms) that are not empty and have no White_Space character at either end, sent as FetchXML | Dataverse's comparison. Equality ignores letter case and trailing spaces in the stored value: on the demo environment on 2026-11-24 (L23), `name = N'<m>'` selected the record named `<m>`, the one named in upper case and the one whose name ends in two spaces. That agrees with the reference server's case-insensitive collation, which ignores case and trailing spaces in `=` (Edge Case 21). Not observed: leading spaces, accents, and `<>` or `IN` on such values | "Comparison Operators"; how SQL Server treats case and trailing spaces depends on the collation (Edge Case 21) | UPDATE: L23. DELETE: L41. INSERT ... SELECT: L42 | AC-35 | For each operation, not in force until its case |
| C04 | Condition | `c = n`, `c <> n` or `c IN (n, ...)`, c a Picklist, State or Status column, n whole-number literals, sent as FetchXML | Dataverse's comparison of option values | "Comparison Operators" | UPDATE: L24. DELETE: L41. INSERT ... SELECT: L42 | AC-35 | For each operation, not in force until its case |
| C05 | Condition | `c IS NULL` or `c IS NOT NULL`, c a column of a kind some B row targets, sent as FetchXML | Dataverse's null test | "IS NULL (Transact-SQL)" | UPDATE: L25. DELETE: L41. INSERT ... SELECT: L42 | AC-35 | For each operation, not in force until its case |
| C06 | Condition | Rows of C01 to C05 and C07 joined by `AND` and `OR`, with parentheses; not `NOT` | Dataverse's filter of `and` and `or` groups | "AND", "OR" | UPDATE: L26. DELETE: L41. INSERT ... SELECT: L42 | AC-35 | For each operation, not in force until its case |
| C07 | Condition | `c op d` with op one of `=`, `<>`, `<`, `<=`, `>`, `>=`, c a DateTime column of a behaviour B28 or B29 targets, d a literal of that row's form, sent as FetchXML | For a user-local column, the operand is sent as its UTC instant (`'2024-01-15T09:30:00+05:00'` as `2024-01-15T04:30:00Z`); for a date-only column, as written. How Dataverse reads a compared date sent this way has not been observed | "Comparison Operators"; owed: R28k for the instant | UPDATE: L36. DELETE: L36. INSERT ... SELECT: L36 | AC-26, AC-35 | For each operation, not in force until L36 |

Rows: 42. In force: 0, for any operation. Not in force: 42.

What falls out of the one rule, because no row admits it (each refused with `Query.DmlUnsupportedConstruct` before the script's first statement): any computed expression, variable or ADO.NET parameter as a value or an operand; `EXECUTE` of a Dataverse message; `MERGE`; `ORDER BY` in a write other than beside `TOP` in row A05; `INSERT ... SELECT` from another table; `TOP` without `ORDER BY` in an `INSERT ... SELECT`; `OUTPUT`; every query hint (`OPTION (...)` and the `-- ppds:` comment hints, `MAX_ROWS`, `BATCH_SIZE`, `BYPASS_PLUGINS` and the rest); `TOP` in `UPDATE` or `DELETE`, `TOP ... PERCENT` and `WITH TIES`; a column compared with a column; a decimal or currency operand in a condition (`WHERE moneycol = 1.005`); an empty or White_Space-edged text operand (`WHERE name = ''`); text without `N` holding a character outside 7-bit ASCII, written or as an operand; a number literal of more than 38 digits; a decimal literal to an Integer column; a date and time literal in another form than B28 or B29 (a fraction of a second, no `Z` or offset for a user-local column, a lower-case `t` or `z`), and a date to a behaviour neither row targets; text to a number and a number to text; a bare GUID text literal written to a Lookup column; `NULL` to a Boolean, State or Status column; State and Status in an `INSERT`; a Money column to a Money column of another precision, and a Double column to one of another precision; a Money or Double column in `INSERT ... SELECT`; a column of a kind or source not listed; a target column not valid for the operation; a column named twice; a write to the id column; an elastic, virtual or activity table; every statement on the read-only list (a `SELECT`, a `DECLARE`, a `PRINT`, `BEGIN TRY`) in a script that writes, since no row admits it; and every construct of the rule table until a row admits it.

#### Evidence by row

For each row: what it has and what it owes. The next change to this spec works from this table, and brings a row into force for an operation only when all three columns are "Have" for it.

- **SQL Server evidence**: "Have" means a documentation page or a reference observation the harness holds; "Owed" names the observation still owed to the harness.
- **Dataverse evidence**: "Have" names the live case, run on 2026-11-24 on the demo environment through the engine's write path on #1698's build, with its value read back exactly; "Owed" names the case still to run.
- **Criterion**: "Have" names the acceptance criterion that states the row. No criterion has a test yet (each is 🔲), so the next change also owes each one's test.

| Row | SQL Server evidence | Dataverse evidence | Criterion |
|-----|---------------------|--------------------|-----------|
| A01 | Have: "UPDATE" | Have: L01 | Have: AC-13 |
| A02 | Have: "DELETE" | Have: L02 | Have: AC-13 |
| A03 | Have: "INSERT" | Have: L03 | Have: AC-13 |
| A04 | Have: "INSERT", "SELECT" | Owed: L14 (rewritten; the earlier copy from another table was refused by #1698 and gave nothing) | Have: AC-07, AC-13 |
| A05 | Have: "TOP", "ORDER BY Clause" | Owed: L15 (rewritten; the earlier form without `ORDER BY` was refused by #1698) | Have: AC-35 for the rows taken. Owed: whether the sort is Dataverse's |
| B01 | Owed: R28a | Have: L01 (`UPDATE`); L04 (`UPDATE`, the length unit: UTF-16 code units, a longer value refused by Dataverse and not shortened); L03 (`INSERT`) | Have: AC-05, AC-10, AC-13 |
| B02, B03 | Owed: R28a | Have: L16 (`UPDATE`), L28 (`INSERT`) | Have: AC-13 |
| B04 | Owed: R28a | Have: L16 (`UPDATE`), L28 (`INSERT`) | Have: AC-13, AC-42 |
| B05 | Owed: R28b | Have: L05 (`UPDATE`), L28 (`INSERT`) | Have: AC-13 |
| B06 | Have: "nchar and nvarchar", S21. Owed: R27g | Have: L06 (`UPDATE`), L28 (`INSERT`), ASCII text | Have: AC-23 |
| B07 | Owed: R28c | `UPDATE`: have, L07 (String to Memo and Memo to String). `INSERT`: owed, L32 (rewritten) | Have: AC-13 |
| B08 | Owed: R28d | `UPDATE`: partly. L08 stored 42; -5 was refused by Dataverse because the column's minimum is 0, so the negative half is owed on a column that admits negatives. `INSERT`: have, L29 (42) | Have: AC-05, AC-13 |
| B09 | Owed: R28e | Owed: L09 (`UPDATE`), L32 (`INSERT`) | Have: AC-13 |
| B10 | Have: "decimal and numeric" | Owed: L10 (`UPDATE`), L33 (`INSERT`) | Have: AC-05, AC-27 |
| B11 | Have: S8 (R08d to R08f) | Owed: L10, L33 | Have: AC-27 |
| B12 | Have: "money and smallmoney". Owed: R28f | Owed: L11 (`UPDATE`), L29's Money part (`INSERT`) | Have: AC-05, AC-27 |
| B13 | Have for the first step: S8, R07a, R07b. The second step is not SQL Server's | Owed: L11, L29's Money part | Have: AC-27 |
| B14 | Owed: R28g | Owed: L12. `INSERT`: no row | Have: AC-13 |
| B15 | Have: "float and real" | Have: L13 (`UPDATE`), L29 (`INSERT`) | Have: AC-05, AC-27 |
| B16 | Owed: R28h | Have: L13. `INSERT`: no row | Have: AC-13 |
| B17 | Owed: R28i | Have: L17 (`UPDATE`), L29 (`INSERT`) | Have: AC-30 |
| B18 | Owed: R28d | Have: L18 (`UPDATE`), L29 (`INSERT`) | Have: AC-31 |
| B19 | Owed: R28d | Have: L19. `INSERT`: no row | Have: AC-32 |
| B20 | Have: S14 (R16a) | Have: L20 (`UPDATE`), L30 (`INSERT`) | Have: AC-33 |
| B21 | Owed: R28j | Have: L21 (`UPDATE`), L31 (`INSERT`) | Have: AC-13 |
| B22 | Owed: R28j | Integer, Money, Double: have, L21 (`UPDATE`), L31 (`INSERT`). Decimal: owed, L10, L33 | Have: AC-13 |
| B23 | Owed: R28j | Have: L21 (`UPDATE`), L31 (`INSERT`) | Have: AC-13 |
| B24 | Owed: R28j | Have: L20 (`UPDATE`), L31 (`INSERT`) | Have: AC-33 |
| B25 | Owed: R28a | Have for the five values L27 names, by `UPDATE` and `INSERT`. Owed: the other White_Space characters, or a narrower row (core requirement 4) | Have: AC-10 |
| B26 | Owed: R28b | As B25 | Have: AC-10 |
| B27 | Owed: R28m | Owed: L37 | Have: AC-27 |
| B28 | Owed: R28k | Owed: L34 | Have: AC-26 |
| B29 | Have: S11 (R12) | Owed: L35 | Have: AC-26 |
| B30 | Owed: R28j | Owed: L34, L35 | Have: AC-13 |
| C01 | Have: S14 | `UPDATE`: have, L01, L22. `DELETE`: have, L02, L41. `INSERT ... SELECT`: owed, L14, L42 | Have: AC-35 |
| C02 | Have: "Comparison Operators" | `UPDATE`: have, L22. `DELETE`: have, L41. `INSERT ... SELECT`: owed, L42 | Have: AC-35 |
| C03 | Have: "Comparison Operators", Edge Case 21 | `UPDATE`: have, L23 (case and trailing spaces in the stored value ignored). `DELETE`: have, L41. `INSERT ... SELECT`: owed, L42 | Have: AC-35 |
| C04 | Have: "Comparison Operators" | `UPDATE`: have, L24. `DELETE`: have, L41. `INSERT ... SELECT`: owed, L42 | Have: AC-35 |
| C05 | Have: "IS NULL" | `UPDATE`: have, L25. `DELETE`: have, L41. `INSERT ... SELECT`: owed, L42 | Have: AC-35 |
| C06 | Have: "AND", "OR" | `UPDATE`: have, L26. `DELETE`: have, L41. `INSERT ... SELECT`: owed, L42 | Have: AC-35 |
| C07 | Have: "Comparison Operators". Owed: R28k | Owed: L36 | Have: AC-26, AC-35 |

Counts, by row (42 rows):

| Evidence | Have | Partly | Owed |
|----------|------|--------|------|
| SQL Server | 17 | 3 | 22 |
| Dataverse, every operation the row names | 18 | 11 | 13 |
| Criterion (written; no test yet) | 41 | 1 | 0 |

"Partly" means part of the evidence is held and part is owed:
- SQL Server: B06, B12 and C07, where a page is held and an observation owed.
- Dataverse: B07, B08 and B22 (one operation or one column kind owed); B25 and B26 (other White_Space characters owed); C01 to C06 (`INSERT ... SELECT` owed).
- Criterion: A05.

B02 and B03 are counted as two rows.

#### Live cases the table needs

Each case is a write and read-back through the engine's own write path (the CLI on the build under test, so `DmlExecuteNode` sends the SDK messages), on records the case creates and deletes after it, on a test environment. Each needs the maintainer's approval as a write. "Read back" means the value is read again and compared exactly with the value the row says is written, from the CLI's JSON output (not with `LEN`, which on `main` ignores every kind of trailing white space, #1632), and every other test record is checked unchanged. Every id is written as a GUID literal. The order is the order to run them in: the most useful rows first. A case marked "needs" cannot run until what it names exists.

Cases 1 to 35 were the list for the run of 2026-11-24 on the demo environment, and 30 of them ran. [Evidence by row](#evidence-by-row) gives what each gave. These are still owed, in the order to run them:
- L08, on a column that admits negative numbers;
- L11 and L29's Money part, then L12, L09, L10 and L33, which need a test table;
- L14, L32, L42 and L15, rewritten to the same-table form;
- the new cases L34 to L37.

| Order | Case | Statement | Column kind and value | Read back | Rows |
|-------|------|-----------|-----------------------|-----------|------|
| 1 | L01 | `UPDATE account SET name = N'<marker>' WHERE accountid = '<id>'` | String (Text) | `name` of that record; no other record changed | A01, B01, C01 (UPDATE) |
| 2 | L02 | `DELETE FROM account WHERE accountid = '<id>'` | Not applicable | The record is gone; no other record changed | A02, C01 (DELETE) |
| 3 | L03 | `INSERT INTO account (name) VALUES (N'<marker 1>'), (N'<marker 2>')` | String (Text) | Two new records with those names | A03, B01 (INSERT) |
| 4 | L08 | **Needs** an Integer column of format None whose minimum is below 0, on a standard table, named before the run. `account.numberofemployees` does not qualify: its minimum is 0, and on 2026-11-24 Dataverse refused -5 for it after storing 42. `UPDATE <table> SET <int> = 42 ...`, then `= -5` | Integer (None) | 42, then -5 | B08 (UPDATE) |
| 5 | L11 | **Needs** a Money column whose precision source is the column's own (`PrecisionSource` 0) at precision 2, on a test table, named before the run (`account.revenue` takes its precision from the currency, so it does not qualify unless the case records the currency's precision and the coordinator accepts that). `UPDATE <table> SET <money2> = 1.23495 ...`, then `= -1.23495`, then `= 12` | Money, precision 2 | 1.24, -1.24 (not 1.23 and -1.23, which one rounding would give), 12 | B12, B13 (UPDATE) |
| 6 | L18 | `UPDATE account SET industrycode = <one of its option values> ...` | Picklist | That value | B18 (UPDATE) |
| 7 | L20 | The case first creates a contact and records its id. `UPDATE account SET primarycontactid = CAST('<that id>' AS uniqueidentifier) ...`, then `= NULL`; then deletes the contact | Lookup (one target table) | That id, then `NULL` | B20, B24 (UPDATE) |
| 8 | L21 | `UPDATE account SET accountnumber = NULL, description = NULL, numberofemployees = NULL, revenue = NULL, address1_latitude = NULL, industrycode = NULL ...` | String, Memo, Integer, Money, Double, Picklist | `NULL` in each | B21, B22, B23 (UPDATE) |
| 9 | L17 | `UPDATE account SET donotemail = 1 ...`, then `= 0` | Boolean | 1 (true), then 0 (false) | B17 (UPDATE) |
| 10 | L19 | `UPDATE account SET statecode = 1, statuscode = 2 ...`, then `statecode = 0, statuscode = 1` | State and Status | Each pair | B19 (UPDATE) |
| 11 | L29 | `INSERT INTO account (name, numberofemployees, address1_latitude, donotemail, industrycode) VALUES (N'<m>', 42, 47, 1, <option value>)`; and, on L11's test table, `INSERT INTO <table> (<money2>) VALUES (1.23495), (12)` | Integer, Double, Boolean, Picklist, Money | Each value as for the UPDATE cases | B08, B12, B13, B15, B17, B18 (INSERT) |
| 12 | L30 | The case creates a contact; `INSERT INTO account (name, primarycontactid) VALUES (N'<m>', CAST('<that id>' AS uniqueidentifier))` | Lookup | That id | B20 (INSERT) |
| 13 | L31 | `INSERT INTO account (name, accountnumber, description, numberofemployees, revenue, address1_latitude, industrycode, primarycontactid) VALUES (N'<m>', NULL, NULL, NULL, NULL, NULL, NULL, NULL)` | Each kind, `NULL` | `NULL` in each | B21 to B24 (INSERT) |
| 14 | L05 | `UPDATE account SET description = N'<a text of several words>' ...` | Memo | The text | B05 (UPDATE) |
| 15 | L06 | `UPDATE account SET name = '<marker in ASCII>' ...` (no `N`) | String (Text) | The text | B06 (UPDATE) |
| 16 | L28 | `INSERT INTO account (name, description, accountnumber, emailaddress1, websiteurl, telephone1) VALUES (N'<m>', N'<text>', '<ascii>', N'a@example.com', N'https://example.com', N'555 0100')` | Memo, String without `N`, Email, URL, Phone | Each text | B02 to B06 (INSERT) |
| 17 | L04 | `UPDATE account SET accountnumber = N'<20 letters>' ...`, then `N'<19 letters and one character outside the Basic Multilingual Plane>'` (20 characters, 21 UTF-16 code units) | String (Text), maximum length 20 | Whether each is stored, refused by Dataverse, or shortened | B01 (the length unit) |
| 18 | L07 | `UPDATE account SET description = name ...`; then, with `description` set to a text of at most 20 characters, `UPDATE account SET accountnumber = description ...` | String to Memo, Memo to String | Each copy equals its source | B07 (UPDATE) |
| 19 | L14 | `INSERT INTO account (name) SELECT name FROM account WHERE accountid = '<id>'` (rewritten to the same table: the form from `account` into `contact` was refused by #1698 on 2026-11-24, as row A04 now refuses it) | String (Text) | One new account with that name; the source unchanged | A04, B07, C01 (INSERT ... SELECT) |
| 20 | L32 | `INSERT INTO account (name, description, numberofemployees) SELECT name, description, numberofemployees FROM account WHERE accountid = '<id>'`, the source having a description and a number of employees (rewritten to the same table, as L14) | String, Memo, Integer copies | Each copy equals its source | B07, B09 (INSERT) |
| 21 | L22 | On two records with `numberofemployees` 5 and 50: `UPDATE account SET description = N'x' WHERE numberofemployees > 10 AND accountid IN ('<id 1>', '<id 2>')` | Integer condition | Only the record with 50 changed | C01, C02 (UPDATE) |
| 22 | L23 | On records named `<m>`, `<M>` (another case) and `<m>` followed by two spaces. The last is created through the raw Web API, because no row admits writing it through PPDS; that write needs its own approval. `UPDATE account SET description = N'x' WHERE name = N'<m>' AND accountid IN ('<id 1>', '<id 2>', '<id 3>')`, the ids of the three records. The comparison under test is the name condition; the id list only bounds it, so no existing record whose name equals the marker can change | String condition | Which of the three changed | C03 (UPDATE) |
| 23 | L24 | `UPDATE account SET description = N'x' WHERE industrycode = <value> AND accountid IN (...)` | Picklist condition | Only the records with that value | C04 (UPDATE) |
| 24 | L25 | `UPDATE account SET description = N'x' WHERE accountnumber IS NULL AND accountid IN (...)` | Null test | Only the records with no account number | C05 (UPDATE) |
| 25 | L26 | `UPDATE account SET description = N'x' WHERE (numberofemployees = 5 OR numberofemployees = 50) AND accountid IN (...)` | `AND` and `OR` | Exactly the records the condition names | C06 (UPDATE) |
| 26 | L41 | On five records, one of which matches every part: `DELETE FROM account WHERE accountid IN (...) AND numberofemployees > 10 AND industrycode = <v> AND accountnumber IS NULL AND (name = N'<m>' OR name = N'<n>')` | Every condition row | Only the matching record deleted | C01 to C06 (DELETE) |
| 27 | L42 | The same condition as L41, as `INSERT INTO account (name) SELECT name FROM account WHERE ...` (rewritten to the same table, as L14) | Every condition row | Exactly one new account, with the matching record's name | C01 to C06 (INSERT ... SELECT) |
| 28 | L15 | On three source records named `<m>-1`, `<m>-2` and `<m>-3` (names that sort the same in any collation): `INSERT INTO account (name) SELECT TOP (1) name FROM account WHERE accountid IN (...) ORDER BY name`; then the same with `ORDER BY name DESC`; then the same without `ORDER BY`, which must be refused before anything runs (rewritten: #1698 refused the earlier form, without `ORDER BY`, on 2026-11-24, as row A05 now refuses it) | `TOP` with `ORDER BY` | One new account named `<m>-1`, then one named `<m>-3`; the third statement writes nothing. The plan shows whether the sort was sent to Dataverse | A05 |
| 29 | L16 | `UPDATE account SET emailaddress1 = N'a@example.com', websiteurl = N'https://example.com', telephone1 = N'555 0100' ...` | String of format Email, URL, Phone | Each text | B02, B03, B04 (UPDATE) |
| 30 | L13 | `UPDATE account SET address1_latitude = 47 ...`, then `address1_longitude = address1_latitude` (the two columns' precision recorded first) | Double | 47 in both | B15, B16 (UPDATE) |
| 31 | L12 | On L11's test table, with two Money columns of the same precision source and precision: `UPDATE <table> SET <money2b> = <money2> ...` | Money to Money | The same value | B14 (UPDATE) |
| 32 | L09 | **Needs** a standard table with two writable Integer columns of format None, named before the run. `UPDATE <table> SET <int2> = <int1> ...` | Integer to Integer | The same number | B09 (UPDATE) |
| 33 | L10 | **Needs** a test table with Decimal columns of precision 2 and 10 (a customization). `SET <dec2> = 1.005`, `= -1.005`, `= 1.004`, `= 7`, `= NULL`; `SET <dec10> = 1.00000000005` | Decimal | 1.01, -1.01, 1.00, 7.00, `NULL`; 1.0000000001 | B10, B11, B22 (UPDATE) |
| 34 | L33 | On L10's table: `INSERT INTO <table> (<dec2>, <dec10>) VALUES (1.005, 7), (NULL, NULL)` | Decimal | As L10 | B10, B11, B22 (INSERT) |
| 35 | L27 | Three spaces, `N'abc  '`, `N'  abc'`, a trailing tab and a trailing line feed, by `UPDATE` and by `INSERT`, to `accountnumber` and `description` | Text with White_Space at either end | Each value exactly | B25, B26 |
| 36 | L34 | **Needs** a DateTime column of behaviour user local and format date and time, named before the run. `UPDATE <table> SET <dt> = '2024-01-15T09:30:00Z' ...`, then `= '2024-01-15T09:30:00+05:00'`, then `= NULL`; and `INSERT INTO <table> (<name>, <dt>) VALUES (N'<m>', '2024-01-15T09:30:00-03:00'), (N'<n>', NULL)` | DateTime, user local | The UTC instants `2024-01-15T09:30:00Z`, `2024-01-15T04:30:00Z`, then `NULL`; `2024-01-15T12:30:00Z` and `NULL` | B28, B30 |
| 37 | L35 | **Needs** a DateTime column of behaviour date only, named before the run. `UPDATE <table> SET <d> = '2024-02-29' ...`, then `= NULL`; and `INSERT INTO <table> (<name>, <d>) VALUES (N'<m>', '1753-01-01')` | DateTime, date only | `2024-02-29`, then `NULL`; `1753-01-01` | B29, B30 |
| 38 | L36 | On L34's and L35's columns, on three records with known values: `UPDATE <table> SET <text> = N'x' WHERE <dt> > '2024-01-15T05:00:00+05:00' AND <id> IN (...)`, the same as a `DELETE`, and as `INSERT INTO <table> (<name>) SELECT <name> FROM <table> WHERE <d> = '2024-02-29' AND <id> IN (...)` | Date conditions | Exactly the records whose instant or date the condition names | C07 |
| 39 | L37 | `UPDATE account SET address1_latitude = 47.12345 ...` (the column's precision, recorded first), then `= 47.123456` (one place more), then `= -0.0`; and `INSERT INTO account (name, address1_latitude) VALUES (N'<m>', 47.12345)` | Double | Whether each is stored exactly, rounded to the column's precision, or refused; `-0.0` as 0 | B27 |

#### Not covered here, and where it is

Each case below is either refused in 2.0 or belongs to the issue named; this spec does not design it.

| Case | In 2.0 |
|------|--------|
| Variables and ADO.NET parameters as DML values | No row admits them. Typed parameters and variables: #1811 |
| A currency column whose scale comes from the currency or the organisation's pricing precision | Rows B12 and B13 require the precision source and precision from the metadata; otherwise no row matches. The column type map: #1799, #1800 |
| A column whose read value is not its stored value (virtual label, calculated, rollup, date and time) as a source | Not a column for the admission table, so no row matches. The typed path's column type map: #1800; dates: #1806 |
| A statement that writes to one environment and reads a column from another | A column of another environment is not a column for the admission table, so no row matches. The typed path's treatment: #1800 |
| A row changed by someone else between the check before the first write and the write | Not prevented: the held, checked values are written (AC-21), as any `UPDATE` would overwrite the change. Dataverse offers no snapshot across the read and the write |
| Temporary tables and cursor fetches when everything is bound before the first statement | No row admits a statement on a temporary table, a cursor or a variable, so a script that writes cannot contain one. Their typing on the typed path: #1800 for temporary table columns, #1811 for variables |
| `NULL` to a required column | Left to Dataverse ([Limits](#limits-the-assignment-check-covers)). No row admits `NULL` to a Boolean, State or Status column |
| `INSERT` with an explicit value for the table's id column | No row targets the id column |
| `TRUNCATE` and other DDL | Not on the read-only list, so they count as writes; the planner refuses them today |
| A script that cannot be parsed | Nothing runs |
| A value the fallback computed in a read-only script, copied by a user or an application into a later write | Outside what PPDS can see; the label is the protection |

### Limits the assignment check covers

The assignment check works from the target column's metadata. What it covers, by target column kind:

| Target column kind | Checked before the first write |
|--------------------|--------------------------------|
| Text (String, Memo) | The value's length against the column's maximum length. A longer value is refused; it is never shortened to fit, trailing spaces included. The length is counted in UTF-16 code units, the unit of `nvarchar(n)` ("nchar and nvarchar": "n defines the string size in byte-pairs"). Dataverse counts the same unit and refuses a longer value rather than shortening it. On the demo environment on 2026-11-24 (L04), it stored 20 letters in a column of maximum length 20 and refused 20 characters that take 21 code units, keeping the stored value |
| Whole number, decimal, currency, floating point | The value against the column's minimum and maximum, and against the range of the column's SQL type. A minimum is checked here, not left to Dataverse: on 2026-11-24 (L08) Dataverse refused -5 for a column whose minimum is 0, after the dry run had passed it. Decimal places are not a limit: the admission row says how they are rounded, or, for now, that a value with more places than the column keeps is refused (core requirement 3). A currency column's precision comes from its precision source (its own precision, the currency's, or the organisation's pricing precision); rows B12 and B13 admit a currency target only when both are known from the metadata, so a currency column whose precision cannot be established matches no row |
| Yes/no (Boolean) | The value is 0 or 1. No row admits `NULL` to a Boolean column |
| Choice, status, status reason (Picklist, State, Status) | The value is one of the column's option values. The DML safety work observed live that Dataverse stores a whole number that is not an option without an error, so this cannot be left to Dataverse. Status and status reason are not set to `NULL` |
| Lookup | Row B20 admits a lookup with exactly one target table only, so the id says which table it refers to; a lookup with more than one target table matches no row |
| Date and time | Rows B28 and B29 admit a user-local date and time from 1753 to 9999 (in UTC too) and a date only from 1753, in their exact forms (core requirement 4); other behaviours and forms wait for #1806 |

Each check in this table is made in the statement's pass and refuses the statement before its first write with `Query.DmlColumnLimitExceeded`; where a row says no admission row matches, the refusal is `Query.DmlUnsupportedConstruct` before the script's first statement. Left to Dataverse, and therefore able to fail after the first write: whether the row a lookup refers to exists; whether a status and status reason pair is valid beyond what the metadata states; required columns; plug-ins and business rules; privileges; duplicate detection.

### What "a proven type rule" means

A construct is in the rule table only when all of these hold:

1. The rule cites its SQL Server documentation page. Where the documentation does not state the behaviour, the rule says "documentation silent; reference observation R<n>" and cites the case in the harness's `ReferenceObservations` that establishes the behaviour.
2. A unit test covers the rule for each combination of operand types it admits.
3. The differential harness passes for the construct against the [reference server](#reference-server): generated expressions that nest it with the other admitted constructs to the configured depth, over boundary values, comparing the value, the error and the result type, with no difference left for the construct in the gated run's snapshot.
4. Every column kind the rule can read has its SQL type in the column type map. The type is established when the map is built, by the read-only probe of the Dataverse SQL (TDS) endpoint on the test environments (#1799). A column kind whose type could not be established is not in the map and is refused. This is a test-time property of the map, per column kind: nothing connects to the endpoint when a user's statement runs, and an environment whose endpoint is switched off behaves the same as any other.
5. For a value that is written: the construct enters a statement that writes only as a row of the [admission table](#admission-in-statements-that-write), which comes into force with its live Dataverse case on the engine's own write path.

### Reference server

The differential harness in `tests/PPDS.Query.DifferentialTests` (#1798) is the proof mechanism of this spec: a statement in this spec about what SQL Server does is backed by a documentation page or by a case in it. It runs each expression on a real SQL Server and through PPDS's `QueryParser` and `ExpressionCompiler` (the path a computed column, a client-side filter and a DML value take) and compares the value, the error and the result type. It has three parts:

The harness evaluates PPDS's side through the untyped `ExpressionCompiler` today, the path AC-16 keeps away from writes. So today the gated run proves nothing about the typed path: a rule of the rule table is proven by the gated run only once the harness evaluates through the typed evaluator, and AC-02 fails for a rule whose harness cases do not.

- **The gated run**: seeds 1, 2 and 3, 300 generated expressions each, nested to depth 3, over an operand table of typed columns (`int`, `bigint`, `decimal(23,10)`, `decimal(12,2)`, `money`, `float`, `nvarchar(100)`, `varchar(100)`) with seven rows of boundary values. It passes only when its differences equal `Snapshots/default-run-differences.tsv` exactly, so a new or changed difference fails it, and so does one that disappears. A construct is proven by this run when the snapshot holds no difference for it. The operand table holds no `bit`, `datetime`, `uniqueidentifier` or `nvarchar(max)` column today, so for a rule over one of those types (`LEN` of a Memo column among them) the snapshot proves nothing: such a rule is not proven by the gated run until the table holds that type. Adding those columns is a follow-up to #1798, not yet filed; the types the probe (#1799) finds for each column kind decide the rest.
- **The negative control** (`KnownDifferences`): the known wrong cases of the [Edge Cases](#edge-cases) table, with both outcomes pinned; a case that starts to agree with SQL Server is removed in the pull request that fixes it.
- **The reference observations** (`ReferenceObservations`, R01 to R22 and R24 to R26; there is no R23, which is the LocalDB run owed below): SQL Server's answer for each behaviour the documentation leaves silent or ambiguous ([What the reference server showed](#what-the-reference-server-showed)).

The reference, as #1798 fixed it:

| Setting | Value |
|---------|-------|
| Server | SQL Server 2022; the recorded runs used 16.0.4325.4 (CU28). On Linux and in CI, the container image `mcr.microsoft.com/mssql/server:2022-latest`; on Windows, LocalDB |
| Collation | `SQL_Latin1_General_CP1_CI_AS` for the database each run creates (checked after it is created). It is the container's default, and LocalDB's instance collation, which cannot be changed ("SQL Server Express LocalDB") |
| Compatibility level | 160 |
| Session settings | `ANSI_WARNINGS`, `ARITHABORT`, `CONCAT_NULL_YIELDS_NULL` and `ANSI_NULLS` on; `NUMERIC_ROUNDABORT` off; `LANGUAGE us_english`; `DATEFORMAT ymd`. Checked when the database is set up and after every batch (reference observation R24). `ARITHIGNORE` is left at its default (off); it matters only when `ANSI_WARNINGS` and `ARITHABORT` are both off |
| Depth and seeds | The gated run: depth 3, seeds 1 to 3, 300 expressions each. The nightly run's depth and seeds are recorded here when its job is added |

These settings matter to values, not only to errors: with `ANSI_WARNINGS` or `ARITHABORT` off, SQL Server returns `NULL` or a shortened value where it raises an error under the reference settings; with `NUMERIC_ROUNDABORT` on, a loss of precision is an error; the language and date format change how a date is read (S11). PPDS computes as SQL Server does under these settings.

Where it runs:

- It is a required check in CI (decision of 2026-11-19 on #1764). The CI job that starts the container and runs the harness is not on `main` yet; until it is, the harness's server tests skip in CI and run on a developer machine.
- It runs on a developer machine against a local SQL Server (LocalDB on Windows, or the same container image CI uses). Without a server (`PPDS_REFSQL_HOST` unset) its server tests skip themselves with the reason, and its self-tests still run.
- A deeper generated run (more nesting, more cases) runs nightly once its job exists.

Not settled yet: whether LocalDB and the container give the same answers. LocalDB's version and collation are owed by a run on Windows (`sqllocaldb versions`, `sqllocaldb info MSSQLLocalDB`, then `SELECT SERVERPROPERTY('Collation'), @@VERSION` on LocalDB and on the container). AC-22 waits on it.

### Constructs required for 2.0

Requirement 8 names this section. It is what the DML grammar on the branch of pull request #1698 admitted in a value before that branch was cut down to the interim rule (Decision H2), read from two files on that branch at commit `28d113d7`: `src/PPDS.Query/Planning/DmlStatementSupport.cs`, which admits constructs and their forms, and `src/PPDS.Cli/Services/Query/DmlColumnValidator.cs`, which narrows them by operand type and by column kind. The 2.0 floor is each construct below, with at least the forms given, for the operand type combinations and the column kinds given. Each must be in the rule table and proven, and returns to DML only as rows of the [admission table](#admission-in-statements-that-write) in force. This section admits nothing by itself; it is the list the admission table must reach by 2.0, and rows may be added beyond it within the core requirements (a user-local date literal without an offset stays refused, core requirement 4).

Forms:

| Construct | Forms |
|-----------|-------|
| Literals | Text literal, whole-number literal, decimal literal, `NULL` |
| Column reference | A column of the target table (`UPDATE ... SET`) or of the source table (`INSERT ... SELECT`) |
| Unary minus | `-x` |
| Arithmetic | `+`, `-`, `*`, `/`, `%`, with parentheses |
| `COALESCE` | Two or more values |
| `NULLIF` | Two values |
| `ISNULL` | Two values |
| `IIF` | A condition and two values |
| `CASE` | The searched form and the simple form |
| Conditions inside `IIF` and `CASE` | `=`, `<>`, `!=`, `<`, `>`, `<=`, `>=`, `IS NULL`, `IS NOT NULL`, joined by `AND` and `OR`, with parentheses |
| `CAST`, `CONVERT` without a style | To `nvarchar(n)` with n from 1 to 4000, to `int`, to `decimal(p,s)` with both given and p at most 28; `CAST` of a quoted GUID literal to `uniqueidentifier` |
| `TRY_CAST`, `TRY_CONVERT` without a style | To `int`, to `decimal(p,s)` with both given and p at most 28 |
| `UPPER`, `LOWER`, `LTRIM`, `RTRIM`, `TRIM`, `LEN` | One argument (`TRIM` without `LEADING`, `TRAILING`, `BOTH` or `FROM`; those forms need compatibility level 160, "TRIM") |
| `CONCAT` | Two or more arguments |
| `SUBSTRING` | Text, a literal start of 1 or more, a literal length of 0 or more |
| `ABS` | One argument |
| `ROUND` | A value and a literal number of places from 0 to 10 |
| `INSERT ... VALUES` items | Text, whole-number or decimal literal, `NULL`, a negative number literal, `CAST('<guid>' AS uniqueidentifier)` |

Operand types. The branch sorts a value into one of these: whole number (a whole-number literal, an Integer column, `LEN`, a conversion to `int`), decimal (a decimal literal, a Decimal column, a conversion to `decimal(p,s)`), currency (a Money column), floating point (a Double column), text (a text literal, a String or Memo column, a text function, `CONCAT`, a conversion to `nvarchar(n)`), and `NULL`. The branch's whole number is not one SQL type: `LEN` is `int`, and `bigint` when its argument is `nvarchar(max)` ("LEN"; S18), so `LEN` of a Memo column is `bigint` on the typed path and arithmetic on it overflows where `bigint` does. The branch admits these combinations:

| Construct | Operand types the branch admits |
|-----------|---------------------------------|
| `+`, `-`, `*`, `/` | Two numbers of the same type, or a whole number with a decimal, a currency or a floating-point value. Not text. Not decimal with currency, decimal with floating point, or currency with floating point |
| `%` | Whole numbers and decimals, in the combinations above. Not currency, not floating point |
| Unary minus | A number |
| `COALESCE`, `IIF`, `CASE` results | All text, or all numbers. Among numbers: the same type, or a whole number with a decimal, a currency or a floating-point value. `NULL` beside other results, but not `NULL` for every result (SQL Server refuses that too: S5) |
| Conditions inside `IIF` and `CASE`, and the comparison a simple `CASE` makes | Comparisons between numbers only, never with the `NULL` constant; `IS NULL` and `IS NOT NULL` of a value |
| `NULLIF` | Two numbers: the second of the first's type, or a whole number. Neither is the `NULL` constant |
| `ISNULL` | The first value is not the `NULL` constant. The second is `NULL`, of the first's type, or a whole number when the first is a decimal, a currency or a floating-point value |
| `CAST`, `CONVERT`, `TRY_CAST`, `TRY_CONVERT` to `int` or `decimal(p,s)` | A number, not text. Not currency to `int`. Not floating point to `decimal` |
| `CAST`, `CONVERT` to `nvarchar(n)` | Text (cut to n, as SQL Server does) or a whole number (an error when it needs more than n characters: S15) |
| `CAST` to `uniqueidentifier` | A quoted GUID literal in the 36-character dashed form, with or without braces (S14) |
| `UPPER`, `LOWER`, `LTRIM`, `RTRIM`, `TRIM`, `LEN`, `SUBSTRING` | Text |
| `CONCAT` | Each argument text or a whole number |
| `ABS`, `ROUND` | A number |
| Any function, any conversion | Not the `NULL` constant as an argument, except `ISNULL`'s second value |

Column kinds. A value may read columns of these Dataverse kinds: String, Memo, Integer, Decimal, Money, Double. It may be written to these:

| Target column kind | Values the branch admits |
|--------------------|--------------------------|
| String, Memo | A text value, or `NULL` |
| Integer, Decimal, Money, Double | A number, or `NULL`. Not a currency value to an Integer column. Not a floating-point value to a Decimal or Money column |
| Boolean | The literal 0 or 1, or `NULL` |
| Picklist | A whole-number literal that is one of the column's options, or `NULL` |
| State, Status | A whole-number literal that is one of the column's options. Not `NULL`. Both set in the same statement, or neither |
| Lookup with one target table | A quoted GUID literal, `CAST` of one to `uniqueidentifier`, or `NULL` |
| DateTime, date only | A text literal of the form `yyyy-MM-dd`, or `NULL` |
| DateTime, user local, date and time format | A text literal with `Z` or an offset, or `NULL` |

The branch refuses writes to every other kind, to the table's id column, to an Owner column, to a lookup with more than one target table and to any other date and time behaviour. Those are below the floor.

Where the branch admits a combination and gives a wrong value for it (the cases in the [Edge Cases](#edge-cases) table), the floor is the combination with the SQL Server value, not the branch's value.

### Primary Flows

The Architecture diagram is the flow for every script. There is no second flow.

### Surface-Specific Behavior

The CLI, the TUI, the daemon and the MCP query tool call the same script check and the same binder. A caller that uses the libraries directly, the ADO.NET provider included, gets the same refusals. DML is not planned without column metadata: this is to be built (#1800); today the planner uses no column metadata and the value coercer passes a value through unchanged when metadata is missing.

Every surface, and the ADO.NET provider, shows two marks that come with a `SELECT` result: the fallback label (core requirement 5) and the incomplete mark after an evaluation error (core requirement 2). The result carries both, in the fields [API/Contracts](#apicontracts) names; no surface works them out for itself. Any surface or output form that cannot carry the fallback label alongside the data, in the same output the data goes to, refuses the fallback: there, an expression with no proven rule is refused with `Query.FallbackUnavailable` before the script's first statement runs. A message (`PRINT`, `THROW`, `RAISERROR`) has no place for the label, so an argument with no proven rule is refused the same way on every surface. How each surface displays the marks is settled with #1809.

#### CLI Surface

Text output carries the fallback label in the data itself, on stdout, before the rows: a first line says which columns are marked and whether the result as a whole is marked, and each marked column's header ends with `*`. So output redirected to a file keeps the label, even when an error stops the rows part-way. The incomplete mark is the error, on stderr, and a non-zero exit code. JSON output carries the fields of [API/Contracts](#apicontracts) beside the rows of each result. CSV output has no place for the label, so with CSV output the fallback is refused with `Query.FallbackUnavailable`; the incomplete mark is the error on stderr and the non-zero exit code. Exit codes follow from the error codes (see [Error Handling](#error-handling)).

#### TUI Surface

Shows the fallback label and the incomplete mark. An export of a marked result to a format that cannot carry the mark is not offered (AC-38).

#### Extension Surface

Shows the fallback label and the incomplete mark, from the daemon's fields of [API/Contracts](#apicontracts). An export of a marked result to a format that cannot carry the mark is not offered (AC-38). How the extension shows a DML refusal and failed rows is in [data-explorer.md](./data-explorer.md). It recognises the four DML refusal codes of [Error Types](#error-types) and shows each as a refusal; an extension released before them still does, through the `dmlBlocked` flag (see the compatibility note there).

#### MCP Surface

Returns the fields of [API/Contracts](#apicontracts) in the tool result. Today the MCP SQL tool does not go through the shared query service or the planner; bringing it to the same script check and binder is part of the requirement above.

#### ADO.NET provider

A `DbDataReader` has no place a generic caller would read the marks from, so the provider refuses the fallback: through the ADO.NET provider, an expression with no proven rule is refused with `Query.FallbackUnavailable` in every script, and the incomplete mark is raised as the evaluation error itself.

### Constraints

- Implement from Microsoft's SQL Server documentation and from tests against a real SQL Server. Do not read, copy, port or adapt source code from SQL 4 CDS or any other project.
- Fail closed: in a script that writes, anything that is not a row in force of the admission table is refused before anything changes. The fallback serves scripts that do not write, only, and only on surfaces that carry its label.
- A statement about SQL Server in this spec, in a rule or in a test is backed by the documentation page it names or by a case in the differential harness. Memory is not a source.

### Validation Rules

N/A. Refusals are listed under [Error Handling](#error-handling).

---

## Acceptance Criteria

| ID | Criterion | Test | Status |
|----|-----------|------|--------|
| AC-01 | In a script that writes, anything anywhere in the script that is not a row in force of the admission table refuses the script with `Query.DmlUnsupportedConstruct` before any statement of it runs. The message names the part by kind (and, for a function, by name), says whether a row for it exists but is not in force, gives its line and column, and repeats no literal value from the statement | TBD | 🔲 |
| AC-02 | Every construct in the rule table has a differential case set in `tests/PPDS.Query.DifferentialTests` that passes against the reference server for value, error and result type, and the gated run's snapshot holds no difference for it. For a rule over a SQL type the gated run's operand table does not hold, the snapshot proves nothing until the table holds that type (see [Reference server](#reference-server)); the criterion fails for such a rule | TBD | 🔲 |
| AC-03 | The differential generator, the admission test and the documented list read the rule table; adding a rule without cases fails the build | TBD | 🔲 |
| AC-04 | Each known wrong-value case in the [Edge Cases](#edge-cases) table produces the SQL Server value or a refusal, with a test that fails if it regresses (its negative-control case in `KnownDifferences` is removed in the same change) | TBD | 🔲 |
| AC-05 | A statement in which, for any row it would write, a text value is longer than the target column's maximum length, or a number, after conversion to the target column's SQL type, is outside the column's range, writes nothing. A text value is never shortened to fit: an over-long text value, trailing spaces included, fails this criterion if it is written in any form | TBD | 🔲 |
| AC-06 | The column type map has an entry for every Dataverse column kind in [Column kinds](#column-kinds): its SQL type with the evidence for it, or "refused" with the reason | TBD | 🔲 |
| AC-07 | An `INSERT ... SELECT` that is not row A04 with rows of B and C in force is refused before any read or write with `Query.DmlUnsupportedConstruct`. That includes one from another table than its target, and one with `TOP` but no `ORDER BY`, on confirmed, unconfirmed and dry-run requests; once A04 is in force, each known wrong-value case of the Edge Cases table, written as an `INSERT ... SELECT` projection, is refused the same way, because no row admits a computed projection | TBD | 🔲 |
| AC-08 | Before `SELECT` moves to the typed path, the live SELECT suite's results are recorded as a baseline with each case marked right or wrong against the reference server. After the move, every case marked right returns the same result, whether it is computed on the typed path or by the fallback, and every case that differs is listed with its proof or its error | TBD | 🔲 |
| AC-09 | DML planned through the library without column metadata is refused with `Query.DmlMetadataUnavailable` | TBD | 🔲 |
| AC-10 | A statement in which the value for any row evaluates to an empty string writes nothing, and the message says to write `NULL`. A text value made only of spaces, or beginning or ending with a white-space character, from a literal or from a column, is refused with `Query.DmlValueStoredDifferently` for every text column whose row (B25 or B26) is not in force, and for every other text format (core requirement 4); the criterion fails if any such value is written | TBD | 🔲 |
| AC-11 | A DML statement in which the value for any row raises an evaluation error (overflow, divide by zero, failed conversion) writes nothing and raises `Query.EvaluationFailed` | TBD | 🔲 |
| AC-12 | Every rule in the rule table names its documentation page, or says "documentation silent; reference observation R<n>" and names the case that establishes the behaviour, and has a unit test for each combination of operand types it admits; a rule without these fails the build | TBD | 🔲 |
| AC-13 | Each row of the admission table has a live case for each operation it is in force for (`UPDATE`, `INSERT`, `DELETE`, `INSERT ... SELECT`), run through the engine's own write path (`CreateMultiple`, `UpdateMultiple`, `DeleteMultiple`), dated, with the value read back equal to what the row says is written, at the precision and format the row names. A row whose status is "in force" for an operation without a dated live case of that operation, its SQL Server source and every owed observation it cites fails this criterion, checked by a test that reads the table | TBD | 🔲 |
| AC-14 | The same unsupported script is refused with the same error code through the CLI, the TUI, the daemon, the MCP query tool and the ADO.NET provider | TBD | 🔲 |
| AC-15 | Every text rule whose result depends on collation records the collation it was proven under. When the organisation's collation cannot be established, or is not one the rule was proven under, a script that writes and uses that rule is refused, and in a script that does not write the expression goes to the labelled fallback | TBD | 🔲 |
| AC-16 | For every entry of the rule table, and for a generated set of constructs outside it, the binder's verdict and result type equal the table's. An architecture test fails if the product contains one of the earlier lists by name: the type categories of `DmlColumnValidator` or the value rules of `DmlStatementSupport` (`AllowedFunctions`, `CheckValue`), both from the DML safety work and removed by #1808. Once #1811 is done, the same test fails if any script that writes can reach the untyped `ExpressionCompiler` | TBD | 🔲 |
| AC-17 | Every construct in "Constructs required for 2.0" is in the rule table for each operand type combination and each column kind listed there; a missing one fails the build | TBD | 🔲 |
| AC-18 | The differential harness is a required CI check against a SQL Server container, and a nightly run executes it at a greater depth | TBD | 🔲 |
| AC-19 | Each error class in the Error Types table raises its own code at the time the table gives; the four DML refusal classes raise the four codes named there and the daemon's error data for each carries `dmlBlocked: true`; `Query.FallbackUnavailable` is raised only for a script that does not write; no message contains a GUID | TBD | 🔲 |
| AC-20 | A script that writes and uses, anywhere, a column, a conversion target or a literal whose SQL type is not on the closed list is refused with `Query.DmlUnsupportedConstruct` before any statement of it runs | TBD | 🔲 |
| AC-21 | The values a statement writes are the held values its check before the first write computed: when the source rows change between the check and the write, the statement still writes the checked values, and no value is computed a second time | TBD | 🔲 |
| AC-22 | The differential harness runs on a developer machine against a local SQL Server, on Windows and on Linux, and passes the same cases as the CI run | TBD | 🔲 |
| AC-23 | Before the first statement runs, a script that writes is refused with `Query.DmlUnsupportedConstruct` for each of these, none of which matches a row: a computed value; a variable or an ADO.NET parameter as a value or an operand; text to a number; money to text; a date literal; a `RealLiteral`; a `MoneyLiteral`; `-(-5)`; a GUID literal with only a closing brace, without dashes or in parentheses; a bare GUID text literal to a Lookup column; a text literal without `N` holding a character outside 7-bit ASCII, written or as an operand in the `WHERE` (the message names the `N'...'` form); a number literal of more than 38 digits; a decimal literal to an Integer column; a minus over a parenthesised literal; a source column that is a virtual label, calculated, rollup, date and time or other-environment column; a currency target whose precision source is not known from the metadata; `NULL` to a Boolean, State or Status column; `EXECUTE` of a Dataverse message; `MERGE`; a `SELECT`, `DECLARE`, `PRINT` or `BEGIN TRY` beside the write; `OUTPUT`; a query hint (`OPTION (...)`, `-- ppds:MAX_ROWS` and the other comment hints); a column named twice in a `SET` list or an `INSERT` column list; a target column not valid for the operation; State and Status in an `INSERT`; an elastic, virtual or activity table; `SET DATEFORMAT` or `SET LANGUAGE`. For each row in force, a case of its form is admitted and writes what the row says | TBD | 🔲 |
| AC-24 | In a script that does not write, an expression that has no proven type rule, in whole or in part, is computed as a whole by the fallback, and the result marks that column as computed by the fallback; when a filter, join condition, grouping, `HAVING` condition, sort, `OPENJSON` or `STRING_SPLIT` argument or filter on a metadata virtual table evaluated in PPDS used the fallback, the result as a whole is marked; when an earlier statement of the script used it for a variable, a cursor's query, a condition or a message, every later result is marked as a whole. The CLI's text and JSON output, the TUI, the extension, the daemon and the MCP query tool each carry the mark in the fields [API/Contracts](#apicontracts) names; the CLI's CSV output and the ADO.NET provider, which cannot carry it, refuse the fallback | TBD | 🔲 |
| AC-25 | No value computed by the fallback can reach a write. A script with a statement that writes and, anywhere in it, an expression with no proven type rule is refused before any statement of it runs, and the criterion fails if any statement of it runs. Cases, each with the unproven expression in a different place: an `INSERT ... VALUES` item; an `INSERT ... SELECT` projection; an `UPDATE ... SET` value; a filter of an `UPDATE` or of a `DELETE` that PPDS evaluates; a `MERGE` clause; `SET @v = ...`, `SELECT @v = ...` and `DECLARE @v = ...` before the write; `SELECT ... INTO #t`; the query of a cursor whose `FETCH ... INTO` feeds the write; an `IF` or `WHILE` condition, including `IF EXISTS (SELECT ...)`; a statement inside `BEGIN TRY` before the write; a plain `SELECT` and a `PRINT` beside the write; the write in a branch of `IF` that does not run; the write in a later `GO` batch than the unproven expression; a script whose only write is to a temporary table; a collation-dependent rule when the collation cannot be established | TBD | 🔲 |
| AC-26 | For each date column behaviour that has a rule, a live Dataverse case writes a literal and reads back the value after the documented conversion for that behaviour. A write to a behaviour without a rule and a live case is refused. A write of a literal without `Z` or an offset to a user-local column is refused; the criterion fails if such a literal is admitted by any rule. A user-local literal with an offset is stored at the UTC time the offset gives, and a date-only literal gives the same date under every session language. For now, a literal in another form than rows B28 and B29 give is refused before the script runs: a fraction of a second, a lower-case `t` or `z`, a space for `T`, another date order. A user-local date and time compared in the `WHERE` of a write (row C07) is put into the FetchXML condition as its UTC instant `yyyy-MM-ddTHH:mm:ssZ`, and live case L36 selects exactly the records that instant names | TBD | 🔲 |
| AC-27 | For rows B10 to B16 and B27, the value written is the one the row says. Exact, or unchanged. For a decimal literal to a Decimal column (B11), rounded to the column's precision with a tie away from zero. For a decimal literal to a Money column (B13), converted to `money` at four places first, as SQL Server does, then rounded to the column's precision, the step owed from live case L11. For a decimal literal to a Double column (B27), the literal's digits exactly. Until B11 and B13 are in force, a literal with more places than the column keeps is refused with `Query.DmlValueStoredDifferently` before the script runs and writes nothing, and so is a decimal literal whose digits a `double` cannot hold (B27). It equals SQL Server's conversion where the row cites one (S8, reference observations R07 to R09), and the row's live case reads back the value the row says is written | TBD | 🔲 |
| AC-28 | A `SELECT` in which an evaluation error occurs after rows have been returned raises the error when it is reached, keeps the rows already returned, and is marked incomplete on the CLI, the TUI, the extension, the MCP query tool and the ADO.NET provider | TBD | 🔲 |
| AC-29 | A DML statement that would write more rows than the DML row cap is refused before any value is computed. With the cap lifted (`--no-limit`), a statement whose pass before the first write would hold more rows than the engine's in-memory limit fails with `Query.MemoryLimitExceeded` and writes nothing | TBD | 🔲 |
| AC-30 | A statement that writes, for any row, a value other than 0 or 1 to a Boolean column through row B17 writes nothing and is refused with `Query.DmlColumnLimitExceeded` in its pass; `NULL` to a Boolean column matches no row and is refused with `Query.DmlUnsupportedConstruct` before the script runs. The criterion fails if a value other than 0 or 1 is written as 1, as SQL Server would store it (the maintainer: "Refuse it") | TBD | 🔲 |
| AC-31 | A statement that writes, for any row, a whole number that is not one of a Picklist column's options (row B18) writes nothing and is refused with `Query.DmlColumnLimitExceeded` in its pass; the criterion fails if Dataverse is left to accept it | TBD | 🔲 |
| AC-32 | A statement that sets a State and a Status column to a pair the metadata does not allow (row B19) is refused with `Query.DmlColumnLimitExceeded` in its pass; one that sets either to `NULL`, or one without the other, matches no row and is refused with `Query.DmlUnsupportedConstruct` before the script runs | TBD | 🔲 |
| AC-33 | A write to a lookup column with more than one target table matches no row and is refused with `Query.DmlUnsupportedConstruct` before the script runs; once B20 is in force, a write of a GUID to a lookup with one target table writes that GUID | TBD | 🔲 |
| AC-34 | In a script that does not write, `CATCH` catches an evaluation error SQL Server would raise and nothing else: a refusal or limit of PPDS's own raised inside `BEGIN TRY` (the in-memory limit, for example) ends the script, no `CATCH` block runs and no later statement runs. A script that writes and contains `BEGIN TRY` is refused with `Query.DmlUnsupportedConstruct` before it runs. In a script whose second statement that writes is refused at run time, the rows the first wrote stay written, and the refusal message names the refused statement's line and the number of rows each earlier statement wrote | TBD | 🔲 |
| AC-35 | In a statement that writes, a condition that is not made of rows C01 to C07 in force is refused with `Query.DmlUnsupportedConstruct` before the script runs. That covers: a date and time operand in another form than C07's; a decimal or currency operand; a variable or parameter operand; an empty or White_Space-edged text operand; text without `N` holding a character outside 7-bit ASCII; a column compared with a column; `NOT`; `LIKE`. `TOP` outside row A05 (in `UPDATE` or `DELETE`, with `PERCENT`, with `WITH TIES`, not a literal, above 5,000, or without `ORDER BY`) is refused the same way, and so is `ORDER BY` in a write other than beside `TOP` in A05. An `INSERT ... SELECT` with `TOP (n)` and `ORDER BY` (row A05) copies exactly the first n source rows in that order | TBD | 🔲 |
| AC-36 | A request that writes and reads state that an earlier request filled or changed with the fallback is refused before its first statement runs, once state is kept between requests; until then, a test shows that no product caller keeps a `SessionContext` between requests | TBD | 🔲 |
| AC-37 | A daemon `query/sql` request without `showsFallbackLabel: true` whose script does not write and has an expression with no proven rule is refused with `Query.FallbackUnavailable`; with it, the result carries the three fields | TBD | 🔲 |
| AC-38 | In the TUI and the extension, a result with any mark offers no export or copy to a format that cannot carry the mark | TBD | 🔲 |
| AC-39 | A rejection by Dataverse of part of a write ends the script: no later statement runs, and the message says what was written | TBD | 🔲 |
| AC-40 | The CLI's text output of a marked result carries the mark on stdout in a line before the rows and in the header of each marked column, so the mark survives redirection to a file; JSON output, the daemon and the MCP tool carry `fallbackColumns` as ordinals per result set, and a script returning two results with duplicate and unnamed columns marks the right ones; the CLI's CSV output refuses the fallback with `Query.FallbackUnavailable`; a `PRINT` argument with no proven rule is refused with that code | TBD | 🔲 |
| AC-41 | When several refusals would fire before a script's first statement, the one raised is the first in the order of [Admission in statements that write](#admission-in-statements-that-write): `DELETE FROM account` with no `WHERE` raises `Query.DmlBlocked`; a statement whose target's metadata is unavailable raises `Query.DmlMetadataUnavailable`, whatever else it contains; a literal `''` to an admitted text target raises `Query.DmlValueStoredDifferently`; and no refused statement is put to the user for confirmation | TBD | 🔲 |
| AC-42 | Each B row's target is bound from the column metadata as the row names it. A String column's format is read from `FormatName`, so a column with `Format` Text and `FormatName` Phone is row B04's target, not B01's. A test feeds such metadata and fails if the binder reads `Format` | TBD | 🔲 |

Status key: ✅ covered by passing test · ⚠️ test exists but failing · ❌ no test yet · 🔲 not yet implemented

Criteria that wait on something outside this spec: AC-13 and every row of the admission table wait on the live cases still owed ([Evidence by row](#evidence-by-row)) and on the observations owed to the harness; AC-02 waits on the harness evaluating through the typed evaluator and on the missing operand types; AC-18 waits on the CI job; AC-22 waits on the LocalDB run in [Reference server](#reference-server).

### Test Examples

The examples are in the differential harness. Each Edge Cases row below is a case of its negative control (`KnownDifferences`) or of the gated run's snapshot, and each row of [What the reference server showed](#what-the-reference-server-showed) is a reference observation. Three of them, as the harness records them (SQL Server's outcome, then the engine's on `main`):

| Expression | SQL Server | Engine on `main` |
|------------|------------|------------------|
| `CAST(2 AS money) / 3` | `0.6666 : money` | `0.6666666666666666666666666667` |
| `RTRIM('a' + CHAR(13) + CHAR(10))` | `'a{U+000D}{U+000A}' : varchar(3)` | `'a'` |
| `CAST(CAST(2.5 AS money) AS int)` | `3 : int` | `2` |

### Edge Cases

The known wrong-value cases, from #1764 and from the differential harness's first run (#1798). Each was observed on the reference server and through the engine on `main`, and is allowed by the engine today. For each, the expected outcome is the SQL Server value, or a refusal before anything changes.

| # | Case | What the engine on `main` does | What SQL Server does (source) | Issue |
|---|------|--------------------------------|-------------------------------|-------|
| 1 | `COALESCE`, `IIF` or `CASE` with values of different types | Returns the chosen value unconverted: a whole number mixed with a decimal, currency or float then divides as an integer; `COALESCE(2.5, 1.5E0)` is a decimal; an overflow from the conversion is not raised | Returns the highest-precedence type of the values ("CASE", "IIF", "COALESCE"; "Data type precedence") | #1764 |
| 2 | `ROUND` | Of a whole number, returns a floating-point value; with a negative length, throws; the result does not keep the input's scale | Returns the input's type (`int` for `int`; `ROUND(1.005, 1)` is `1.000`, `decimal(4,3)`); `ROUND(748.58, -1)` is `750.00` ("ROUND", return types; S10) | #1764, #1879 |
| 3 | Currency arithmetic | Keeps full precision, in a chain and in a single division | Money with money or a whole number: four places after each operation, a division truncated toward zero; money with a decimal is a decimal (S6 and S7) | #1764 |
| 4 | `ISNULL` | Does not convert its second value to the first value's type, precision, scale or length, so it neither shortens text nor raises the overflow; evaluates the second value when the first is not `NULL` | Converts the second value to the first's type and shortens it to the first's length (`ISNULL(CAST(NULL AS nvarchar(3)), N'a longer default')` is `'a l'`); does not evaluate it when the first is not `NULL` (`ISNULL(2.5, 2 % 0)` is `2.5`) ("ISNULL", Remarks; #1878) | #1764, #1878 |
| 5 | `ROUND` that overflows its type | Returns a value | Raises error 8115, for a negative, zero or positive length and for money (S10) | #1764 |
| 6 | Whole-number literals above the 32-bit range | Does not reproduce on `main`: such a literal is computed as a .NET decimal. Kept because the DML safety branch showed integer division for it; what remains is the decimal scale (Edge Case 13) | Types it `decimal(p,0)` (S2) | #1764 |
| 7 | Text without the `N` prefix, and `CAST` of `nvarchar` to `varchar` | Keeps characters the code page cannot hold (`CAST(N'āb' AS varchar(3))` is `'āb'`; a Cyrillic literal without `N` is written as typed) | Converts through the database's code page and loses them silently (`'ab'`; `'??????'`, S21) ("nchar and nvarchar", Remarks; "char and varchar": "data loss during code page translations isn't reported"). In a write, such a literal matches no admission row and is refused, written or as an operand in the `WHERE` (the maintainer: "Refuse it", "Yes, refuse it"; any character outside 7-bit ASCII, the coordinating session's conservative reading) | #1764 |
| 8 | A decimal or float written to a currency column | Skips the conversion to four places | Rounds half away from zero to four places (S8) | #1764 |
| 9 | `float` comparisons | Compares at 15 significant digits, through `decimal` (an overflow for the largest floats), against a decimal literal, a float or a whole number | Compares at full binary precision (S19) | #1764 |
| 10 | Values that exceed the target column's length, range or precision | Not checked before the first write, so a rejection by Dataverse part-way through leaves earlier batches committed | Not a SQL Server rule; Dataverse behaviour, shown by a live case | #1764 |
| 11 | `UPPER` and `LOWER` | Use .NET's invariant casing (`LOWER(N'İ')` is unchanged, `UPPER(N'µ')` is `Μ`) | Follow the collation (S9) | #1764 |
| 12 | The same expressions in a `SELECT` and in `INSERT ... SELECT` projections | Return the same wrong values | As the other rows | #1764 |
| 13 | Decimal arithmetic | Keeps .NET decimal's 28 digits; overflows where SQL Server fits a value in 38 digits | Gives each result the precision and scale of "Precision, scale, and length", reducing the scale at 38 digits (`2.50 / 2.00` is `1.250000`) | #1876 |
| 14 | A number plus text | Concatenates (`7 + N'5'` is `'75'`) | Converts the text to the number's type and adds (`12`), or raises a conversion error ("Data type precedence") | #1872 |
| 15 | Arithmetic overflow | Never raised: `int` arithmetic is computed in `bigint` (`2147483647 + 1` is `2147483648`), `bigint` wraps, money and float run out of range | Raises error 8115 ("+ (Addition)" and the other operators) | #1873 |
| 16 | `CAST`, `TRY_CAST` and `CONCAT` and the precision, scale and length of their result | Ignored: `CAST(2147483647 AS decimal(10,2))` is a value, `CAST(7 AS decimal(10,2))` is `7`, `TRY_CAST(12345.6 AS decimal(4,1))` is `12345.6`, `CAST(12345 AS nvarchar(3))` is `'123'`, `CONCAT` is not cut | Error 8115; `7.00`; `NULL`; error 8115; cut at 4,000 characters (S13, S15, S16) | #1874 |
| 17 | Conversions between types | .NET rules: money to integer truncates; float to text in round-trip form; GUID spellings SQL Server refuses are accepted, and a longer string is refused; `CAST(N'' AS int)` fails | Money to integer rounds (S8); float to text with 6 digits; GUID as S14; `CAST(N'' AS int)` is `0` ("CAST and CONVERT") | #1875 |
| 18 | A date and time string with an offset, to `datetime` | Applies the offset (`'...T10:00:00+05:00'` gives `05:00`, observed on a machine in UTC); a `datetimeoffset` cannot be converted | Refuses the string (241); drops the offset of a `datetimeoffset` (S12) | #1877 |
| 19 | `TRIM`, `LTRIM`, `RTRIM` and `LEN` | Treat every white-space character as a space (.NET `Trim()`): `RTRIM('a' + CHAR(13) + CHAR(10))` is `'a'`, `LEN('ab' + CHAR(9))` is 2 | Remove and ignore only `char(32)`: `'a'+CR+LF`, 3 (S17 and S18) | #1632 |
| 20 | `LEN` of `nvarchar(max)` | `int` | `bigint` (S18) | #1804 |
| 21 | `NOT` of a comparison with `NULL`, and text comparison | Two-valued `NOT` (true where SQL Server gives unknown); text compared ordinally, with case and trailing spaces significant | Three-valued logic; compares by the collation, ignoring trailing spaces and, in a case-insensitive collation, case (`IIF(N'Abc' = 'abc ', 1, 0)` is 1) | #1622 |

The engine on `main` also raises errors where SQL Server returns a value (`ROUND` with a negative length, `LEFT` and `RIGHT`, `ISNULL` evaluating its second value, `CAST(N'' AS int)`: #1879, #1880, #1878, #1875), and returns a value for some expressions SQL Server refuses as untypeable (`NULLIF(NULL, 1)`, `0.1 % 1E-1`, all-`NULL` `IIF` and `COALESCE`); the binder refuses the latter (#1800). Those fail closed or are refused by the typed path and are not wrong values written.

### Column kinds

The Dataverse column kinds AC-06 means, by attribute type: String, Memo, Integer, BigInt, Decimal, Double, Money, Boolean, DateTime (each behaviour: user local, date only, time zone independent), Picklist, State, Status, Lookup, Customer, Owner, Uniqueidentifier, PartyList, EntityName, ManagedProperty, CalendarRules, Virtual, and the kinds identified by attribute type name: multi-select choice, image, file.

---

## Core Types

Not defined yet. The public shape of the SQL type, the typed tree, the rule table and the column type map, and the package each lives in, are fixed in #1800 and written back to this section.

---

## Error Handling

### Error Types

Every refusal in this spec is one of these rows, with its code and its timing. A sentence elsewhere that says "refused" means the row its condition falls under.

| Class | Condition | When | Outcome | Code |
|-------|-----------|------|---------|------|
| Not admitted | In a script that writes: anything that is not a row in force of the [admission table](#admission-in-statements-that-write), in any statement, including a SQL type outside the closed list, a collation-dependent rule, a date literal, a column not of an admitted kind, and reading tainted state | Before the script's first statement runs | Nothing is read or written. The message names the part, says whether a row for it exists but is not in force yet, and gives its line and column | `Query.DmlUnsupportedConstruct` |
| Fallback not available | In a script that does not write: an expression with no proven rule on a surface or output form that cannot carry the label (CLI CSV output, the ADO.NET provider, a daemon client without `showsFallbackLabel`), or in a `PRINT`, `THROW` or `RAISERROR` argument | Before the script's first statement runs | Nothing runs. The message names the expression and says where the label could be shown | `Query.FallbackUnavailable` |
| Column limit | A value matched by an admitted row fails a check of [Limits the assignment check covers](#limits-the-assignment-check-covers) (length, range, 0 or 1, an option value, a State and Status pair) | For a literal, before the script's first statement; for a column value, in the statement's pass, before its first write | The statement writes nothing; earlier statements of the script keep what they wrote. The message names the column and the limit | `Query.DmlColumnLimitExceeded`. For now #1698 does not define it and raises the limits it checks with `Query.DmlUnsupportedConstruct` (core requirement 3) |
| Stored differently | A value Dataverse stores differently or may store differently: the empty string; text with White_Space at either end whose row (B25, B26) is not in force (core requirement 4); for now, a literal or column value with more decimal places than the target keeps, or digits a floating-point target cannot hold (rows B11, B13, B27) | As for a column limit | As for a column limit. The message says what to write instead, or that such values are not written yet | `Query.DmlValueStoredDifferently` |
| DML without metadata | A DML statement is planned and the target's column metadata is not available | At planning, before the script's first statement runs | Nothing runs | `Query.DmlMetadataUnavailable` |
| Row cap exceeded | A DML statement would write more rows than the DML row cap | Before the statement's pass starts | As for a column limit | `Query.DmlRowCapExceeded` (exists) |
| Pass over the in-memory limit | With the row cap lifted, the pass would hold more rows than the engine's in-memory limit | In the statement's pass, before its first write | As for a column limit | `Query.MemoryLimitExceeded` (exists) |
| Evaluation error | Overflow, divide by zero, or a conversion that fails, while a value is computed. With no row in force, no admitted row computes a value; this applies once rows that compute are added, and to `SELECT` | When reached | DML: the statement writes nothing. `SELECT`: rows already returned stay returned and every surface marks the result incomplete | `Query.EvaluationFailed` |
| Safety guard | The DML safety guard's own refusals (for example `DELETE` or `UPDATE` without `WHERE`, a cross-environment write set to read-only), unchanged by this spec | Before the script's first statement runs | As today | `Query.DmlBlocked` (exists) |

Of these, `CATCH` catches only the evaluation error, and only in a script that does not write; a script that writes cannot contain `TRY ... CATCH` (the whole-script rule, "Refusals inside a script"). When more than one refusal would fire before the first statement, the order in [Admission in statements that write](#admission-in-statements-that-write) decides which is raised.

Decision G: "A code per class". The four DML refusal classes of the decision (not admitted, column limit, stored differently, DML without metadata) each have a code of their own in `ErrorCodes.Query` (`src/PPDS.Cli/Infrastructure/Errors/ErrorCodes.cs`), named like the existing DML codes (`Query.DmlBlocked`, `Query.DmlRowCapExceeded`, `Query.DmlConfirmationRequired`), with the same string available to library callers as a `QueryErrorCode` constant, as existing query errors have (`src/PPDS.Dataverse/Query/Execution/QueryExecutionException.cs`). `Query.DmlBlocked` keeps its present meaning, the refusals of the DML safety guard (for example `DELETE` without `WHERE`, or a cross-environment write set to read-only), and is not used for these four. `Query.FallbackUnavailable` is a refusal of a read-only script, not a DML refusal, and is named the same way. Application Services surface each as a `PpdsException` with that code (Constitution D4). The message contains no GUID and no stack trace; if a row's id is reported, it is carried in the error's `Context`, not in the message. How a failing row is named to the user is fixed in #1800. How a code becomes a CLI exit code is in [CONTRIBUTING.md](../CONTRIBUTING.md), "Errors and exit codes": the four codes take the exit code `Query.DmlBlocked` has.

Compatibility. An extension released before these codes, talking to a newer daemon, receives codes it does not know. The daemon therefore sends each of the four with `dmlBlocked: true` in the error data, as it does for `Query.DmlBlocked`. An older extension recognises a refusal by that flag or by a code it knows (`src/PPDS.Extension/src/utils/dmlSafety.ts`), so it shows each new code as a refusal with the daemon's message; it cannot tell the four classes apart, and it never offers to confirm or retry them, which it does only for `Query.DmlConfirmationRequired`. Nothing has been written when any of the four is raised. A script or MCP client that tests for `Query.DmlBlocked` no longer matches these refusals, a breaking change for it that the CLI and MCP changelogs state when the codes ship.

### Edge Cases

N/A here. The cases are in the [Edge Cases](#edge-cases) table under Acceptance Criteria.

### Recovery Strategies

- **Refusals before the script's first statement** (every refusal of the unsupported-construct class): nothing has changed. The user corrects the script and runs it again.
- **A refusal or error at run time** (a column limit, a value stored differently, the row cap, the in-memory limit, an evaluation error): the refused statement wrote nothing, but every earlier statement of the script that wrote has written its rows, and they stay written. The message names the refused statement and the rows each earlier statement wrote, so the user can see what has changed before running anything again.
- **A rejection by Dataverse after the first write** (see "What the check before the first write does not cover"): rows already written stay written. The failed rows are reported as [query.md](./query.md) and [data-explorer.md](./data-explorer.md) describe.

---

## API/Contracts

This spec adds no endpoint. Its contract with every surface is the error codes above, the `dmlBlocked` flag the daemon sends with the four DML refusal codes, and three fields on each result set of a query (a script that returns several results carries them per result set, in the order the results are returned), carried by the shared query service's result, the daemon's `query/sql` response, the MCP query tool's result and the CLI's JSON output:

| Field (JSON name) | Type | Meaning |
|-------------------|------|---------|
| `fallbackColumns` | array of zero-based column ordinals in the result's column order, empty when none | The computed columns whose expression the fallback computed. Ordinals, not names, so duplicate and unnamed columns are told apart |
| `fallbackResult` | boolean | The result as a whole is marked: a filter, join, grouping, `HAVING`, sort or row source evaluated in PPDS used the fallback, or an earlier statement of the script used it for a variable, a cursor, a condition or a message |
| `incomplete` | boolean | An evaluation error was raised after rows had been returned (core requirement 2) |

A client that does not know these fields would ignore them and show fallback values without the label. So the daemon sends fallback values only to a client whose `query/sql` request says it shows the label (a boolean request field, `showsFallbackLabel`); for any other client, an older extension among them, it refuses the fallback with `Query.FallbackUnavailable`, as the ADO.NET provider does (AC-37). The MCP query tool returns the fields to the agent that called it; see [Pending](#pending) on whether MCP should refuse the fallback instead.

---

## Extension Points

### Adding a construct to the rule table

A construct is added by meeting every item of [What "a proven type rule" means](#what-a-proven-type-rule-means). There is no other way in: a function registered in the function library enters a script that writes only through a row of the admission table, after it has a rule; until then a script that does not write computes it by the labelled fallback.

---

## Configuration

No user setting. The reference server's settings and the run depths are test configuration; see [Reference server](#reference-server).

---

## Design Decisions

### Why one model instead of an engine and a checker?

**Context:** Three review rounds of the DML safety work (pull request #1698) kept finding statements that were allowed and wrote a value SQL Server would not. On that branch the checker reasoned about SQL types in categories; the engine computed with .NET runtime types; nothing compared them.

**Decision:** The binder that refuses is the binder that types the tree the evaluator runs.

**Alternatives considered:** limiting expressions to one level (wrong values occur with one operation or none); patching each reported case (the list does not converge).

### Why a real SQL Server as the reference?

**Context:** The live suite of that earlier work computed expected values with helper functions written by the same authors as the engine.

**Decision:** Expected values for expression semantics come from running the same expression on SQL Server, in the differential harness. The live Dataverse suite proves only what is specific to Dataverse: which rows are selected and what is stored. The harness's first run bore this out: it settled each behaviour the documentation left silent, ambiguous or self-contradictory ([What the reference server showed](#what-the-reference-server-showed)), and in one (`-2147483648`) the server contradicted what the documentation implies.

### Why one whole-script rule instead of a table of places?

**Context:** Earlier drafts kept fallback values away from writes with a table: each place a value could be computed (a variable, a temporary table, a condition, a filter) with its outcome. Each review found a place the table had missed: `SELECT @v = ...` and `DECLARE @v = ...`, then a cursor (`DECLARE ... CURSOR FOR SELECT`, `FETCH ... INTO`), `TRY ... CATCH`, and a collation-dependent rule in `SELECT @v = ...` that the collation requirement sent to the fallback. A list of places grows with the engine's statement kinds.

**Decision:** Decision M: a script that contains any statement that writes gets no fallback anywhere. Whether a script writes is a property of its text, decided before it runs, so no path inside the script needs to be named.

**Cost:** a script that writes can contain only rows in force of the admission table, even in statements that only read. Until live cases are run, no row is in force, and every statement that writes is refused.

### Decisions recorded on #1764

The maintainer's decisions of 2026-11-19 on #1764 that this spec carries:

| Decision | Where it is in this spec |
|----------|--------------------------|
| The admission rule is "a construct with a proven type rule" | Core requirement 1; "What a proven type rule means" |
| Until the typed path covers it, DML on `main` allows literals, columns and `NULL` | Admission in statements that write: the admission table |
| The reference SQL Server is a required CI check, runs locally, and has a deeper nightly run | Reference server; AC-18 |
| The Dataverse SQL endpoint is enabled on the test environments | Column type map; "What a proven type rule means", item 4 |
| A write that would evaluate to an empty string is refused, telling the user to write `NULL` | Core requirement 4; AC-10 |
| No runtime cross-check through the endpoint in 2.0 | Non-Goals; Roadmap |
| The correctness checks live in the library | Components; Surface-Specific Behavior; AC-09, AC-14 |
| Every construct today's DML grammar allows is proven and working in 2.0 DML | Core requirement 8; Constructs required for 2.0; AC-17 |

### Decisions of 2026-11-21

Made by the maintainer on 2026-11-21. The words in quotation marks are the option chosen.

| Question | Decision | Where it is in this spec |
|----------|----------|--------------------------|
| A `SELECT` that uses a construct with no proven type rule | "Keep a labelled untyped fallback for SELECT only" | Overview; core requirements 1, 5 and 9; Components (Fallback); the whole-script rule; AC-24, AC-25. Decision M of 2026-11-24 narrows it to scripts that do not write |
| Date and time columns under the stored-differently rule | "Define the value sent as the value after the documented conversion" | Core requirement 4; Limits; AC-26 |
| How the binder learns the organisation's collation at run time | "Record the collation each text rule was proven under; refuse when it cannot be established" | Core requirement 10; Rule table; AC-15 |
| A value with more decimal places than the target column | "Convert as SQL Server does, then check length and range" | Core requirement 3; Assignment check; Limits; AC-05, AC-27 |
| A `SELECT` whose evaluation fails after rows have been returned | "Raise the error when it is reached; mark the result incomplete on every surface" | Core requirement 2; Error Types; AC-28 |
| Expressions the engine evaluates that no sub-issue names | "The same answer as Decision A, written per place into the spec's Scope section" | Scope. Decision M of 2026-11-24 replaces the table of places with the whole-script rule |
| What a rule cites when the documentation is silent | "The rule says "documentation silent" and cites the differential case that establishes the behaviour" | Type rules; What the reference server showed; "What a proven type rule means", item 1; AC-12 |
| How the pass before the first write is bounded | "The DML row cap bounds it; a statement over the cap is refused before the pass starts" | Core requirement 3; Error Types; AC-29 |
| Whether the 2.0 requirement binds column kinds as well as constructs | "Yes; otherwise "working in 2.0 DML" could be met for whole-number columns only" | Core requirement 8; Constructs required for 2.0; AC-17 |

### Decisions of 2026-11-24

Made by the maintainer on 2026-11-24. The words in quotation marks are the option chosen.

| ID | Question | Decision | Where it is in this spec |
|----|----------|----------|--------------------------|
| M | How the spec keeps fallback values away from writes | "One whole-script rule": a script that contains any statement that writes gets no fallback anywhere; an unproven construct in it is refused | Overview; Scope, the whole-script rule; Admission in statements that write; core requirements 1, 5, 9 and 10; Components (Script check); Why one whole-script rule; AC-01, AC-20, AC-25 |
| G | Error codes for DML refusals | "A code per class" (unsupported construct; value outside the column's limits; value Dataverse would store differently; DML without metadata) | Error Types, the four codes and the compatibility note; Extension Surface; AC-09, AC-19 |
| H1 | Whether a negative number and a GUID cast count as literals in the interim rule | "Yes, include both" | Admission table: the terms "whole-number literal" and "decimal literal" (one minus over a literal) and row B20 (`CAST` of a GUID literal); AC-23 |
| H2 | Whether the DML safety work (#1698) is cut down to the interim rule before it lands | "Cut it down first" | Admission in statements that write; Constructs required for 2.0 |
| I | A write of text made only of spaces | "Run it live first": one write and read-back, then refuse exactly what Dataverse does not store unchanged | Core requirement 4: admitted is exactly what has been observed on the executor's path. Live case L27 (2026-11-24) observed five such values stored unchanged, which is rows B25 and B26's Dataverse evidence; until those rows are in force, text made only of spaces or with white space at either end is refused. The Web API observation of 2026-11-24 is recorded, not counted; AC-10, AC-13 |
| J | A `SELECT` expression that is only partly unproven | "The whole expression" goes to the labelled fallback | The whole-script rule; core requirement 5; AC-24 |
| K | A `SELECT` that needs a text rule when the collation cannot be established | "Labelled fallback" (a write is refused) | Core requirement 10; AC-15 |
| L | The pass before the first write when `--no-limit` lifts the row cap | "Yes, a ceiling", through the engine's existing memory limit, failing cleanly before the first write | Core requirement 3; Error Types; AC-29 |

### Decisions of 2026-11-24 and 2026-11-25 (follow-up)

Made by the maintainer after the spec was first merged. The words in quotation marks are his. Where the spec carries a decision further than his words, the row says whose reading that is.

| Question | Decision | Where it is in this spec |
|----------|----------|--------------------------|
| Text without the `N` prefix holding characters outside the code page, as a written value | "Refuse it": a clear error that says to write `N'...'` | Terms of the admission table; row B06; Edge Case 7; AC-23. Implemented as any character outside 7-bit ASCII: the coordinating session's conservative reading, not his words |
| The same text as an operand in the `WHERE` of an `UPDATE` or `DELETE` | "Yes, refuse it" (2026-11-25) | Terms; row C03; AC-23, AC-35. The 7-bit ASCII line is the same reading. #1698 refuses it in the `WHERE` of an `INSERT ... SELECT` too |
| The whole-script rule's consequence: a writing script may contain no expression PPDS evaluates while no admission row is in force (variables, parameters, `IF`, `WHILE`, `SELECT`, client-side joins and filters refused) | "Yes, accept it" | The whole-script rule: a script that writes contains only `INSERT`, `UPDATE` and `DELETE` statements |
| `INSERT ... SELECT` from another table, and `TOP` without `ORDER BY` in an `INSERT ... SELECT` (the spec admitted both; #1698 refuses both) | "Refuse both, fix the spec" (2026-11-25) | Rows A04 and A05; live cases L14, L15, L32 and L42 rewritten to the same table; AC-07, AC-35 |
| Row B13, a decimal literal with more places than a currency column keeps | "Round like SQL Server, then to the column" (2026-11-25): four places with ties away from zero, then the column's precision before sending. In force only after its live case; until then #1698's refusal stands | Row B13; AC-27 |
| Row B17, a yes/no literal other than 0 or 1 | "Refuse it" (2026-11-25) | Row B17; AC-30 |

### Pending

Not decided, or not yet backed by evidence; each is the maintainer's.

- **The live cases still owed**, listed under [Live cases the table needs](#live-cases-the-table-needs). Each is a write on a test environment and needs his approval. No admission row is in force until its evidence is complete ([Evidence by row](#evidence-by-row)).
- **Readings of the author that are his to confirm:**
  - temporary-table writes and `SELECT ... INTO` count as writes;
  - text with White_Space at either end is refused until rows B25 and B26 are in force, and whether those rows cover every White_Space character or only those L27 wrote (space, tab, line feed);
  - the check before the first write is per statement, so a later refusal leaves earlier writes in place;
  - PPDS's refusals and Dataverse's rejections are not caught by `TRY ... CATCH`;
  - `NULL` to a Boolean, State or Status column is not admitted;
  - the collation is established once per script;
  - the result fields, the request field `showsFallbackLabel`, the code `Query.FallbackUnavailable`, and the surfaces that refuse the fallback (CLI CSV output, the ADO.NET provider, daemon clients without the field, messages);
  - the CLI's text output carries the label on stdout;
  - whether the MCP query tool should refuse the fallback, since an agent can copy a value into a later write (written here as carrying the fields);
  - the four DML codes, `dmlBlocked: true` and the break for clients that match `Query.DmlBlocked`, as a public contract;
  - a user-local date literal needs `Z` or an offset, binding every later date rule; for now the date forms are exactly #1698's, with no fraction of a second;
  - a row is in force per operation; elastic, virtual and activity tables are refused; State and Status are not written by an `INSERT`;
  - for now no row rounds: a literal with more places than the column keeps is refused (rows B11, B13, B27), as #1698 builds it;
  - every query hint is refused in a write; `MAX_ROWS` does not lift the DML row cap (the cap comes from the safety guard's options only), but in a write it would cap the rows read, as an unordered `TOP`;
  - `SET DATEFORMAT`, `SET LANGUAGE` and every session option other than `NOCOUNT` count as writes, so a script with one admits nothing;
  - `EXECUTE` of a Dataverse message counts as a write here, so a script with only that is refused; #1698 does not count it as a write and refuses it only beside one;
  - a bare quoted GUID written to a Lookup column matches no row here; #1698 admits it.

### Prior art

SQL 4 CDS (MIT licence) takes a typed approach of this kind and is acknowledged as prior art for the idea. Its source code is not read, copied, ported or adapted.

---

## Related Specs

- [query.md](./query.md) - parsing, planning, DML safety, row selection
- [architecture.md](./architecture.md) - Application Services and structured errors
- [cli.md](./cli.md) - the CLI surface and its `ExceptionMapper`, which turns an error code into an exit code
- [data-explorer.md](./data-explorer.md) - how DML refusals and failed rows are shown in the extension
- [mcp.md](./mcp.md) - the MCP query tool, one of the callers of the binder
- [test-gates.md](./test-gates.md) - required CI checks, which the differential harness joins
- [docs-generation.md](./docs-generation.md) - the generators that publish the list of supported constructs

---

## Changelog

| Date | Change |
|------|--------|
| 2026-11-25 | Follow-up: the maintainer's decisions of 2026-11-24 and 2026-11-25 and the first live run (2026-11-24, the demo environment, #1698's build) written in. `INSERT ... SELECT` within the same table only, and `TOP` only with `ORDER BY` (rows A04, A05; "Refuse both, fix the spec"), with live cases L14, L15, L32 and L42 rewritten to the same table. B13 states the maintainer's rule ("Round like SQL Server, then to the column"), refused until L11. B17 records "Refuse it". Text without `N` outside 7-bit ASCII refused, written and in a write's `WHERE` ("Refuse it", "Yes, refuse it"; the 7-bit line marked as the coordinating session's reading). A script that writes contains only `INSERT`, `UPDATE` and `DELETE` ("Yes, accept it"). Value admission aligned with #1698 and marked "for now": one exact form per kind, a lossless conversion, no rounding, at most 38 digits, a decimal literal refused for an Integer column, date and time forms without a fraction of a second, a date operand sent as its UTC instant. New rows B27 to B30 and C07, observations R28k to R28m, live cases L34 to L37 and AC-42. Corrections from the run: L08 needs a column that admits negatives; B04 binds from `FormatName`; Dataverse counts UTF-16 code units and refuses a longer value; C03's text equality ignores case and trailing spaces; L27's five values were stored unchanged. [Evidence by row](#evidence-by-row) added. 42 rows, none in force |
| 2026-11-24 (review round 3) | Row-level fixes: one order for refusals before the first statement (AC-41); `CATCH` catches only an evaluation error in a script that does not write, and a script that writes contains no `TRY`; currency row B13 converts as SQL Server does (four places, S8, R07a) then to the column's precision, with live case L11's values changed to tell one rounding from two and a column whose precision source is known; every "No conversion" row given an observation owed to the harness (R28a to R28j); whole-number literals limited to the `int` range; B17's other values; rows in force per operation, with the `INSERT` cases owed (L28 to L33) and cases for `DELETE` and `INSERT ... SELECT` conditions (L41, L42); standard tables only; GUID literals with or without `N`; a repeated column refused; White_Space values as rows B25 and B26; hints, `OUTPUT`, column validity and session options; the overview states what `main` does today and what a 2.0 user can write. 37 rows, none in force |
| 2026-11-24 (review round 2) | The write side restructured as admission by table: one closed admission table (35 rows, each with SQL Server evidence, a live case, a criterion and a status; none in force, because no live case has run on the engine's write path) and one rule that refuses everything else with `Query.DmlUnsupportedConstruct`. Every other admitting sentence removed. The live cases the table needs, in the order to run them. Every refusal given one code and one timing; `Query.FallbackUnavailable` for read-only surfaces that cannot carry the label. White space is the Unicode White_Space property. The money multiplication tie added to S7. The label carried on stdout in CLI text output, by column ordinal and per result set. Criteria AC-37 to AC-40. A list of what is pending replaces "No decision is pending" |
| 2026-11-24 (review round 1) | Made fail closed where the review found gaps: writes defined by a closed read-only list; refusals inside a script (not catchable, per-statement limits, what stays written); the interim rule's literals by parse node, readable source columns and a table of admitted pairs; text without `N` outside U+0020 to U+007E refused; condition operands, `TOP` and `ORDER BY` in writes; Decision I narrowed to what the executor's path has shown (nothing yet); `ABS` corrected by the server's answer; observations owed to the harness; the result fields for the labels and the surfaces that refuse the fallback; acceptance criteria AC-30 to AC-36; the operand types the gated run lacks; the cases not covered and where they are |
| 2026-11-24 | The maintainer's decisions M, G, H1, H2, I, J, K and L written in: the whole-script rule replaces the table of places; a code per DML refusal class, with the compatibility note; the interim rule's literals; the memory ceiling on the pass before the first write. The SQL Server claims checked against the documentation and the differential harness's reference observations: LEN's type corrected; the silent and ambiguous behaviours replaced by the server's answers; the Edge Cases table checked and extended with the harness's findings; the reference server's settings recorded. Decision I's live observation written in |
| 2026-11-21 | The maintainer's decisions of 2026-11-21 written into the requirements; refusals added for values stored for a later statement and for conditions; pending decisions cut to G to L |
| 2026-11-20 | Initial spec |

---

## Roadmap

- A runtime cross-check through the Dataverse SQL (TDS) endpoint, revisited after the typed engine exists.
