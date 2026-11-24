# SQL Values

**Status:** Draft
**Last Updated:** 2026-11-24
**Code:** [src/PPDS.Query/Execution/](../src/PPDS.Query/Execution/), [src/PPDS.Query/Planning/](../src/PPDS.Query/Planning/), [src/PPDS.Dataverse/Query/Execution/Functions/](../src/PPDS.Dataverse/Query/Execution/Functions/), [tests/PPDS.Query.DifferentialTests/](../tests/PPDS.Query.DifferentialTests/)
**Surfaces:** CLI, TUI, Extension, MCP

---

## Overview

Every value PPDS computes from SQL text (an `UPDATE ... SET` value, an `INSERT` value, a computed column, a client-side comparison) has a SQL type that is known before anything runs, and is computed in that type the way SQL Server computes it. One type model decides both what is refused and how each value is computed. The model is checked against a real SQL Server by the differential harness in `tests/PPDS.Query.DifferentialTests` (#1798).

There is one exception, for reading only. In a script that contains no statement that writes, an expression that uses a construct with no proven type rule is not refused: the whole expression is computed by the earlier untyped evaluator, as a labelled fallback, and every surface says so. In a script that contains any statement that writes, nothing gets the fallback, in any statement: an unproven construct anywhere in the script refuses the script before its first statement runs. This is [the whole-script rule](#the-whole-script-rule).

Every choice this spec needed has been made by the maintainer; the decisions are listed under [Design Decisions](#design-decisions).

### Goals

- **One model**: the code that decides whether an expression is allowed is the code that evaluates it. No second model predicts the first.
- **SQL Server results**: a computed value equals the value SQL Server produces for the same expression over the same typed inputs. That includes errors: where SQL Server, under the reference server's session settings, raises an error (overflow and divide by zero are the usual cases; SET ANSI_WARNINGS and SET ARITHABORT, Remarks), PPDS raises an error; where SQL Server shortens or rounds a value without an error, PPDS returns the same shortened or rounded value.
- **Fail closed**: in a script that writes, an expression with a part that has no type rule refuses the whole script before any of it runs, with a message naming that part. In a script that does not write, it is computed by the labelled fallback and never passed off as a proven value.
- **Checked before the first write**: every value a statement would write is computed and checked against the target column's limits before the first row is written.
- **Proof that composes**: each rule is proven once; nesting is covered by generated expressions run on a real SQL Server by the differential harness, not by listing statement shapes.

### Non-Goals

- Supporting every T-SQL function. A function without a proven rule is refused in a script that writes, and computed by the labelled fallback in a script that does not.
- Reproducing SQL Server query plans or performance.
- Collation-exact behaviour for every language. Text rules state which collation they are proven for and do not apply outside it; core requirement 10 says what happens then.
- Deciding which statement shapes are allowed (joins, `TOP`, `OUTPUT`, `MERGE`, hints) and which rows a statement selects. The rule table in this spec admits value expressions only; statements are governed by [query.md](./query.md).
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
| Binder | Assigns a SQL type to every node from the rules below; inserts conversion nodes. A node with no rule refuses a script that writes, and sends the whole expression to the fallback in a script that does not |
| Evaluator | Computes a typed tree over a row. It never chooses an operation from the runtime type of a value |
| Assignment check | Converts each computed value to the target column's SQL type by SQL Server's rule for that pair of types (core requirement 3), checks the column's length and range, and refuses the whole statement on the first failure |
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
- Which statements are allowed and which rows they select (see Non-Goals).

#### The whole-script rule

Decision M: "One whole-script rule". A script that contains any statement that writes gets no fallback anywhere; an unproven construct in it is refused.

- **A script** is the whole text of one request, every statement and every batch in it, whichever caller sends it: the CLI, the TUI, the daemon, the MCP query tool or an ADO.NET command.
- **A statement that writes** is every statement that is not on this closed list of read-only statements: `SELECT` without `INTO`; `DECLARE` of a variable or of a cursor; `SET` of a variable; `IF`, `WHILE`, `BEGIN ... END`, `BREAK`, `CONTINUE` and `BEGIN TRY ... END TRY BEGIN CATCH ... END CATCH` (the blocks themselves; the statements inside them are classified one by one); `PRINT`, `THROW`, `RAISERROR`; `OPEN`, `FETCH`, `CLOSE` and `DEALLOCATE` of a cursor. Everything else counts as a write, whether or not it changes data: `INSERT`, `UPDATE`, `DELETE` and `MERGE` whatever the target (a temporary table included), `SELECT ... INTO`, `CREATE TABLE` and `DROP TABLE`, `EXECUTE` of a Dataverse message, `EXECUTE AS` and `REVERT`, and any statement kind the engine admits later until it is added to this list by a change to this spec.
- **Contains** means anywhere in the text: inside a `BEGIN ... END` block, a branch of an `IF` that may not run, a `WHILE` body, a `TRY` or a `CATCH` block. Whether the script writes is decided from the parse of the whole script before its first statement runs, never from which statements happen to run.
- **In a script that writes**, every expression in every statement is bound with no fallback. An expression anywhere in the script with a construct, a combination of operand types or a SQL type outside the rule table refuses the whole script before its first statement runs: no row is read, and nothing is written or printed. This includes the plain `SELECT`s of that script, its variable assignments, its conditions, its cursor queries and its messages.
- **In a script that does not write**, an expression with a part that has no proven rule is computed, as a whole, by the fallback (Decision J), wherever it is, except on a surface that cannot carry the label ([Surface-Specific Behavior](#surface-specific-behavior)), where it is refused. The fallback's output is labelled:
  - a computed column whose expression used the fallback is marked as computed by the fallback;
  - the result as a whole is marked when a filter, join condition, grouping, `HAVING` condition, sort, `OPENJSON` or `STRING_SPLIT` argument, or a filter on a metadata virtual table, evaluated in PPDS, used the fallback, because it decides which rows are returned;
  - every result that a statement returns after an earlier statement of the same script used the fallback for anything other than its own returned columns (a variable assignment, a cursor's query, an `IF` or `WHILE` condition, a `PRINT`, `THROW` or `RAISERROR` argument) is marked as a whole, because a value or a decision computed by the fallback may have reached it. No mark travels with a single value; the script carries it from that statement on.
- **A statement whose text is not known before the script runs** (`EXEC` of a string, dynamic SQL) is not supported today, and is not on the read-only list.
- **State kept between requests.** Nothing a script stores outlives its request on `main`: variables, temporary tables, cursors and an impersonation set by `EXECUTE AS` live in the script's own execution, and no product caller keeps a `SessionContext` between requests. If state is ever kept between requests, state that a request filled or changed with the fallback is marked as tainted, and a later request that writes and reads tainted state is refused before its first statement runs with `Query.DmlUnsupportedConstruct`. Any such state that a later request can read counts as read by every statement of that request.
- **Refusals inside a script.** The refusals and limits of [Error Types](#error-types) are PPDS's own; SQL Server would not raise them. `TRY ... CATCH` does not catch them: each ends the script at the statement that raised it, no `CATCH` block runs for it, and no later statement runs. An evaluation error SQL Server would also raise (overflow, divide by zero, a failed conversion) is caught by `CATCH` as SQL Server's would be. (On `main` today, `TRY ... CATCH` catches every exception; the typed path changes that.)
- **What is checked for the whole script, and what per statement.** Everything that depends only on the script's text and the metadata is checked for the whole script before its first statement runs: an unsupported construct, a SQL type outside the closed list, a source and target pair the interim rule does not admit, a collation that cannot be established, a date literal of a form that has no rule. The check of computed values before the first write (core requirement 3) runs per statement, because a later statement's values can depend on rows an earlier statement wrote, which are not known until it has run. So in a script with more than one statement that writes, a later statement refused at run time (a value outside a column's limits, a value Dataverse may store differently, the row cap, the in-memory limit, an evaluation error) leaves the rows every earlier statement wrote in place. The refusal message names the refused statement by its line and says how many rows each earlier statement wrote.

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
| S7 | Currency with a decimal or a whole number | With a whole number or money: `money`, four places after each operation (`CAST(1 AS money) / 3 * 3` is `0.9999`). With a decimal: `decimal`, at the scale "Precision, scale, and length" gives (`CAST(1 AS money) * 1.1` is `1.10000`, `decimal(22,5)`), because decimal ranks above money | "Data type precedence"; reference observation R06 |
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
- Conversion of `float` to text uses 6 significant digits by default, and `CAST` to `nvarchar` without a length is `nvarchar(30)` ("CAST and CONVERT"). The 2.0 floor admits neither; a rule that admits them states them.
- Text that is not a number fails conversion to a number ("CAST and CONVERT"); the engine's .NET parsing accepts thousands separators, currency symbols and parentheses today. The 2.0 floor does not convert text to a number.

#### Observations owed to the harness

The rows above marked "owed to the harness" were observed on the reference server during the review of this spec, not by the harness. Until each is pinned in `ReferenceObservations`, the rule that depends on it is not proven (item 1 of [What "a proven type rule" means](#what-a-proven-type-rule-means)). Adding them is a follow-up to #1798, not yet filed: R27a (S20, `ABS`), R27b (S3, `- 5` and `-(2147483648)`), R27c (S14, the closing brace), R27d (S21, text without `N`), R27e (S22, column to column of another type), and the ASCII case of the interim rule (text without `N` whose characters are all from U+0020 to U+007E is stored unchanged).

### SQL types

The SQL type kinds are a closed list. A column whose type is not on it, a conversion to a type not on it, and a literal whose type is not on it have no type rule: they refuse a script that writes, and are computed by the labelled fallback in a script that does not. The list is extended only by a change to this spec.

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

1. A script that contains a statement that writes ([the whole-script rule](#the-whole-script-rule)) is refused, before its first statement runs, when any expression anywhere in it contains a construct, or a combination of operand types, that is not in the rule table: a value, a filter PPDS evaluates, a condition, a variable assignment, a cursor's query, a message, or a computed column of a plain `SELECT`. No row is read and nothing is written; what is read before the refusal is the column metadata and, for a collation-dependent rule, the organisation's collation. The code is `Query.DmlUnsupportedConstruct`. The message names the unsupported part by its kind and, for a function, by its name (for example "the function DATEADD", "a CONVERT style", "arithmetic between a decimal and a floating-point value"), and says where it is in the script (line and column). It does not repeat literal values from the statement.
2. A value is computed in the SQL type the binder gave its node. Overflow, divide by zero and a conversion that fails are errors for the whole statement wherever SQL Server raises an error for them under the reference server's session settings (see [Reference server](#reference-server)), unless the construct's own rule says otherwise (`TRY_CAST` and `TRY_CONVERT`, S13). In a statement that writes, such an error means nothing is written. In a `SELECT`, the error is raised when it is reached: rows already returned stay returned, and every surface marks the result incomplete.
3. Before the first write, every value for every row the statement would write is converted to the target column's SQL type and then checked against the column's length and range (defined in [Limits the assignment check covers](#limits-the-assignment-check-covers)). The conversion is SQL Server's rule for that pair of types, from the "Truncating and rounding results" table of "CAST and CONVERT" as the reference server confirmed it ([What the reference server showed](#what-the-reference-server-showed), S8): a decimal or floating-point value written to an Integer column is truncated toward zero; a currency value written to an Integer column is rounded half away from zero; a value with more decimal places than a Decimal or Money column is rounded to the column's scale, a tie away from zero (reference observations R07 and R08). Decimal places alone are never a reason to refuse. Two conversions are not SQL Server's: a text value is never shortened (a text value longer than the column's maximum length is refused, not cut to fit; SQL Server's assignment would drop trailing spaces beyond the length, S15, and PPDS refuses those as well), and a date and time value is converted by its behaviour's rule (core requirement 4). Any failure of a check refuses the whole statement. The computed values are held until every row has passed, and the held values are what is written; nothing is computed again for the write. The DML row cap ([query.md](./query.md)) bounds this pass: a statement over the cap is refused before the pass starts. When the cap is lifted (`--no-limit`), the pass is still bounded by the engine's existing in-memory limit, `MaxMaterializationRows` of the query plan context (500,000 rows by default), which `--no-limit` does not lift: a statement whose pass would hold more rows than that fails with `Query.MemoryLimitExceeded` before its first write (Decision L).
4. Where Dataverse would store something other than the value sent, the statement is refused with a message that says what to write instead. The rule is to refuse exactly what Dataverse does not store unchanged (Decision I), and each case is established by a live write and read-back. The known case is the empty string, which Dataverse stores as `NULL`: a write whose value evaluates to an empty string is refused, and the message says to write `NULL`. This is checked on each row's computed value in the pass before the first write, so an expression that yields an empty string for any row (for example `LTRIM('  ')`, or `CONCAT` of two `NULL`s) refuses the whole statement. A further case is added only by a change to this spec, with its live case.

   Text with spaces at either end (Decision I). What is admitted is exactly what has been observed, live, on the path the DML executor writes by: the SDK messages `CreateMultiple` and `UpdateMultiple`, sent through the bulk operation executor (`DmlExecuteNode`). Nothing has been observed on that path yet. So, until it has, a text value that is made only of spaces, or that begins or ends with a space or another white-space character (tab, line feed, carriage return, no-break space), is refused for every text column, single-line or multi-line, of every format, with `Query.DmlValueStoredDifferently`; the message names the column and says that such values are not written until Dataverse has been shown to store them unchanged. This holds for a value from a literal and for one copied from a column. The one observation so far is evidence, not admission: on a test environment, on 2026-11-24, through the Dataverse Web API (not the executor's path), a single-line text column (`account.accountnumber`) written with a value of three spaces was returned unchanged, both in the update's response and in a separate read. A case is admitted, per column kind and format, once the same write and read-back has been made on the executor's path (AC-13); each admission is a change to this spec. The empty-string rule above is unchanged. SQL Server ignores trailing spaces in `LEN` (`LEN(N'   ')` is 0, reference observation R21e) and in `=`, so it does not tell such a value from `''`; that says nothing about what Dataverse stores.

   For a date and time column, "the value sent" is the value after the conversion Dataverse documents for the column's behaviour (user local, date only, time zone independent): a date write is stored unchanged when the value read back equals that converted value. The rule for each behaviour names the documentation page that states its conversion and has its own live case (#1806); a behaviour without both is refused. The literal must leave no room for interpretation. For a user-local column it carries `Z` or an explicit offset, and a literal without one is refused, because the stored value would otherwise depend on the calling user's time zone; the offset is applied to give the UTC time. SQL Server's conversion to `datetime` refuses an offset or drops it (S12), so this conversion is the rule's own, not SQL Server's. A date-only literal `yyyy-MM-dd` is read as year, month and day whatever the session language, which SQL Server's `datetime` conversion does not guarantee (S11). This binds #1806 and every later rule: whatever further literal forms are admitted, a user-local literal without an offset is not one of them. A date literal whose form has no rule for the column's behaviour, the user-local literal without `Z` or an offset among them, is refused before the script's first statement runs with `Query.DmlUnsupportedConstruct`: the refusal depends only on the literal's text and the metadata. Until #1806 records a rule, every date and time behaviour is without one, so every date literal written or compared in a statement that writes is refused.
5. In a script that does not write, computed values use the typed path wherever every part of the expression has a proven type rule. An expression with a part that has no proven rule is computed, as a whole, by the untyped fallback (Decision J), and labelled as [the whole-script rule](#the-whole-script-rule) says: per computed column, for the result as a whole when a filter, join condition, grouping, `HAVING` condition or sort evaluated in PPDS used it, and for every later result of the script when an earlier statement used it for anything other than its own returned columns. Every surface shows that label. The fallback is never used in a script that writes. No query that returned the right result before returns a different result; each change is a correction with proof or a new clear error.
6. The rule table is the only list. The admission tests, the differential generator and the documented list of supported constructs are generated from it. The documented list is a reference page for users, generated from the rule table by the documentation generators under `scripts/docs-gen/` ([docs-generation.md](./docs-generation.md)); its path is recorded here when the generator for it exists. Today the harness keeps its own `RuleTable` of what it generates; it reads the product's rule table once that exists.
7. No other code decides whether a value expression is allowed or what type it has. No construct is admitted by one component and typed differently by another.
8. At the 2.0 release the rule table covers, for DML, every construct in [Constructs required for 2.0](#constructs-required-for-20), for the operand type combinations and the column kinds listed there.
9. A column, a conversion target or a literal whose SQL type is not on the closed list in [SQL types](#sql-types) is treated as a construct outside the rule table: it refuses a script that writes, and is computed by the labelled fallback in a script that does not.
10. The rule table records, for each text rule whose result depends on collation, the collation it was proven under (casing does depend on it: S9). Such a rule applies only if the organisation's collation has been established and is one the rule was proven under. The collation is established once per script, before its first statement runs, as part of binding (a read of metadata, not of rows), and a script that writes and needs a rule whose collation is not established is refused at that point, with `Query.DmlUnsupportedConstruct`, as any other unproven construct is. When the collation cannot be established, or is another one, the rule counts as not proven: a script that writes is refused, and in a script that does not write the expression goes to the labelled fallback (Decision K). How the collation is established is identified by #1799 and #1804 and recorded here; until a means is recorded, it cannot be established. `UPPER` and `LOWER` are in the 2.0 floor (core requirement 8), so a means of establishing the collation is itself required for 2.0: without one, those two constructs could not be admitted in a script that writes.

What the check before the first write does not cover: Dataverse can still reject a row or a later batch for a reason that is not a value's type or limits (a plug-in, a required column, a privilege, a duplicate rule). Earlier batches are then already written. This spec removes the failures that can be known from the statement and the column metadata; it does not make a multi-batch write atomic.

#### Interim rule

Until the rule table covers a construct, DML on `main` allows only literals, columns and `NULL` as values. Those are the words of the recorded decision ("Literals, columns, NULL"). A literal or a column is written to a target column only for a pair in the table of admitted pairs below; every other pair is refused before the script's first statement runs, with `Query.DmlUnsupportedConstruct`.

- **A literal**, by its ScriptDom parse node:
  - a text literal: a `StringLiteral`. With the `N` prefix it is `nvarchar`. Without it, it is `varchar` and is admitted only when every character in it is from U+0020 to U+007E; a text literal without `N` that holds any other character is refused, and the message says to write it with the `N` prefix (`N'...'`), because SQL Server would store each character the code page cannot hold as `?` (S21) and the code page comes from a collation that cannot be established yet (core requirement 10);
  - a whole-number literal: an `IntegerLiteral`;
  - a decimal literal: a `NumericLiteral`, digits with a decimal point. A `RealLiteral` (`1E0`) and a `MoneyLiteral` (`$1`) are not decimal literals and are refused;
  - a negative number: a `UnaryExpression` of type `Negative` over a whole-number or decimal literal, with any white space or parentheses between them (`-5`, `- 5`, `-(5)`, `-2.5`) (Decision H1). It has the type of the number it spells: `int` from -2,147,483,648 to 2,147,483,647 (S3);
  - `CAST` of a quoted GUID literal to `uniqueidentifier` (Decision H1), and a quoted GUID literal written to a Lookup column. The GUID literal is the 36-character dashed form, with or without both braces; any other spelling is refused, because SQL Server refuses some (S14: without dashes, in parentheses, with a leading space) and silently cuts a longer string, or one with only a closing brace, to a valid GUID;
  - `NULL`.
- **A column** is a reference to a column of the target table (`UPDATE ... SET`) or of the source table (`INSERT ... SELECT`), in the environment the statement writes to, of one of the readable kinds: String, Memo, Integer, Decimal, Money, Double. A column of any other kind is refused as a source, and so are these, whatever their kind: a virtual label column (the name column Dataverse adds beside a lookup, choice or yes/no column), a calculated or rollup column (the column metadata's `SourceType` is not simple), a date and time column (its read value is a conversion of the stored value), and a column read from another environment (`[LABEL].table`).
- **Everything else** a script that writes evaluates in PPDS is refused under the whole-script rule while it has no proven rule, in any statement of that script, not only in the write's values: a computed column of a plain `SELECT` beside the write, a condition, a variable assignment, a variable or an ADO.NET parameter used as a value. The checks of the DML safety guard (for example `DELETE` or `UPDATE` without `WHERE`) stay as they are.
- **The operands of conditions sent to Dataverse.** A condition of a statement that writes that is sent to Dataverse as FetchXML is evaluated by Dataverse, but its operand is computed and formatted by PPDS (`FetchXmlGenerator.FormatLiteralValue` copies a literal's text and substitutes a variable's value), and it decides which rows are written or deleted. So it falls under the same rules as a written value: a literal operand is admitted only for the pairs marked "and as a condition operand" in the table below; a variable, a parameter or any other expression as an operand is refused; a date and time literal operand is refused (no date rule exists; when one does, a literal without `Z` or an offset stays refused, as for `SET`). `IN` lists and `BETWEEN` bounds are operands one by one. A `TOP` in a statement that writes takes a whole-number literal only, and an `ORDER BY` in a statement that writes (in the `SELECT` of an `INSERT ... SELECT`) takes column references only. Each refusal here has the code `Query.DmlUnsupportedConstruct`.

Admitted pairs, value to target column kind. A pair is added only with a cited documentation page or reference observation and an acceptance criterion; the first pairs are those that need no conversion, then the conversions SQL Server documents and the reference server confirms.

| Value | Target column kind | What is written | Source | Criterion |
|-------|--------------------|-----------------|--------|-----------|
| Text literal (either form above), String or Memo column; also as a condition operand on a String or Memo column | String, Memo | The text unchanged (no conversion for `nvarchar`; the U+0020 to U+007E characters of a `varchar` literal are the same in `nvarchar`), after the length check and core requirement 4 | No conversion; "nchar and nvarchar" (Remarks) and S21 for the characters a `varchar` literal may hold; the ASCII case is owed to the harness | AC-05, AC-10, AC-13 |
| Whole-number literal, Integer column; also as a condition operand on an Integer, Picklist, State or Status column | Integer | The value unchanged, after the range check | No conversion | AC-05, AC-13 |
| Whole-number literal, Integer column | Decimal, Money, Double | The value exactly; a value outside the column's range is a range failure | "decimal and numeric": conversion from `int` "can cause overflow" (the range check refuses it); "money and smallmoney" (four decimal places); "float and real" (15 digits, more than a 10-digit `int` needs) | AC-05, AC-27 |
| Decimal literal, Decimal column; also a decimal literal as a condition operand on a Decimal or Money column | Decimal | Rounded to the column's scale, a tie away from zero | S8; R08d to R08f | AC-27 |
| Decimal literal, Decimal column | Money | Rounded to four places, a tie away from zero | S8; R07a, R07b | AC-27 |
| Decimal literal, Decimal or Double column | Integer | Truncated toward zero | S8; R08a to R08c, R08f | AC-27 |
| Money column | Money | Unchanged | No conversion | AC-13 |
| Money column | Decimal | Rounded to the column's scale | S8; R09a to R09d | AC-27 |
| Double column | Double | Unchanged | No conversion | AC-13 |
| Whole-number literal 0 or 1; also as a condition operand on a Boolean column | Boolean | 0 or 1 | No conversion beyond `int` to `bit` for 0 and 1 | AC-30 |
| Whole-number literal | Picklist, State, Status | The value, when it is one of the column's options | No conversion | AC-31, AC-32 |
| Quoted GUID literal, `CAST` of one to `uniqueidentifier`; also as a condition operand on a Lookup or id column | Lookup with one target table | The GUID | S14; R16a | AC-33 |
| `NULL` | String, Memo, Integer, Decimal, Money, Double, Picklist, Lookup | `NULL` (whether the column is required is left to Dataverse) | No conversion | AC-13 |

Not admitted, among others: text to a number (`SET intcol = '5'` gives 5 in SQL Server, S22); a number, a currency value or a date to text (S22); any date and time value (no rule until #1806); a floating-point value to a Decimal or Money column, and a currency value to an Integer column (below the 2.0 floor); `NULL` to a Boolean column (what Dataverse stores is not observed) and to a State or Status column; a write to the table's id column.

The DML safety work (#1698) lands on `main` cut down to this rule (Decision H2). Every other construct its branch admits in a value (the rest of [Constructs required for 2.0](#constructs-required-for-20)) returns to DML one construct at a time, as each construct's rule is proven under [What "a proven type rule" means](#what-a-proven-type-rule-means). The rule is decided and not yet in force: today `main` computes any value expression without a check.

#### Not covered here, and where it is

Each case below is either refused in 2.0 or belongs to the issue named; this spec does not design it.

| Case | In 2.0 |
|------|--------|
| Variables and ADO.NET parameters as DML values | Refused under the interim rule. Typed parameters and variables: #1811 |
| A currency column whose scale comes from the currency or the organisation's pricing precision | The column type map records the scale's source (#1799, #1800); a currency target whose scale cannot be established is refused ([Limits](#limits-the-assignment-check-covers)) |
| A column whose read value is not its stored value (virtual label, calculated, rollup, date and time) as a source | Refused as a source under the interim rule. The typed path's column type map: #1800; dates: #1806 |
| A statement that writes to one environment and reads a column from another | The column from the other environment is refused as a source under the interim rule. Binding uses the target environment's metadata and collation. The typed path's treatment: #1800 |
| A row changed by someone else between the check before the first write and the write | Not prevented: the held, checked values are written (AC-21), as any `UPDATE` would overwrite the change. Dataverse offers no snapshot across the read and the write |
| Temporary tables and cursor fetches when everything is bound before the first statement | In a script that writes, a temporary table is written by a statement that writes and a `FETCH ... INTO` fills variables, which are refused as values under the interim rule. Their typing on the typed path: #1800 for temporary table columns, #1811 for variables |
| `NULL` to a required column | Left to Dataverse ([Limits](#limits-the-assignment-check-covers)). `NULL` to a Boolean column is refused until observed |
| `INSERT` with an explicit value for the table's id column | Refused: the id column is not a target |
| `TRUNCATE` and other DDL | Not on the read-only list, so they count as writes; the planner refuses them today |
| A script that cannot be parsed | Nothing runs |
| A value the fallback computed in a read-only script, copied by a user or an application into a later write | Outside what PPDS can see; the label is the protection |

### Limits the assignment check covers

The assignment check works from the target column's metadata. What it covers, by target column kind:

| Target column kind | Checked before the first write |
|--------------------|--------------------------------|
| Text (String, Memo) | The value's length against the column's maximum length. A longer value is refused; it is never shortened to fit, trailing spaces included. The length is counted in UTF-16 code units, the unit of `nvarchar(n)` ("nchar and nvarchar": "n defines the string size in byte-pairs"); that Dataverse counts the same unit is shown by the live case of AC-13 before a text rule is admitted |
| Whole number, decimal, currency, floating point | The value against the column's minimum and maximum, and against the range of the column's SQL type. Decimal places are not a check: the conversion to the column's SQL type sets them by SQL Server's rule for the pair (core requirement 3). A currency column's scale is the one the column type map records for it, from the column's precision source (its own precision, the currency's, or the organisation's pricing precision); a currency column whose scale cannot be established from the metadata is refused as a target |
| Yes/no (Boolean) | The value is 0 or 1. `NULL` is refused until a live case shows what Dataverse stores for it |
| Choice, status, status reason (Picklist, State, Status) | The value is one of the column's option values. The DML safety work observed live that Dataverse stores a whole number that is not an option without an error, so this cannot be left to Dataverse. Status and status reason are not set to `NULL` |
| Lookup | The value is a GUID, and the lookup has exactly one target table, so the id says which table it refers to. A GUID written as text is in the 36-character dashed form, with or without braces (interim rule) |
| Date and time | The literal has a form admitted for the column's behaviour, and the behaviour has a rule (core requirement 4). A user-local literal without `Z` or an offset is refused. Either refusal is made before the script's first statement runs, with `Query.DmlUnsupportedConstruct` |

Each check in this table refuses the statement before its first write with `Query.DmlColumnLimitExceeded`, except where the row says otherwise. Left to Dataverse, and therefore able to fail after the first write: whether the row a lookup refers to exists; whether a status and status reason pair is valid beyond what the metadata states; required columns; plug-ins and business rules; privileges; duplicate detection.

### What "a proven type rule" means

A construct is in the rule table only when all of these hold:

1. The rule cites its SQL Server documentation page. Where the documentation does not state the behaviour, the rule says "documentation silent; reference observation R<n>" and cites the case in the harness's `ReferenceObservations` that establishes the behaviour.
2. A unit test covers the rule for each combination of operand types it admits.
3. The differential harness passes for the construct against the [reference server](#reference-server): generated expressions that nest it with the other admitted constructs to the configured depth, over boundary values, comparing the value, the error and the result type, with no difference left for the construct in the gated run's snapshot.
4. Every column kind the rule can read has its SQL type in the column type map. The type is established when the map is built, by the read-only probe of the Dataverse SQL (TDS) endpoint on the test environments (#1799). A column kind whose type could not be established is not in the map and is refused. This is a test-time property of the map, per column kind: nothing connects to the endpoint when a user's statement runs, and an environment whose endpoint is switched off behaves the same as any other.
5. For a value that is written: a live Dataverse case shows that a value of that SQL type sent to each admitted target column kind is stored unchanged.

### Reference server

The differential harness in `tests/PPDS.Query.DifferentialTests` (#1798) is the proof mechanism of this spec: a statement in this spec about what SQL Server does is backed by a documentation page or by a case in it. It runs each expression on a real SQL Server and through PPDS's `QueryParser` and `ExpressionCompiler` (the path a computed column, a client-side filter and a DML value take) and compares the value, the error and the result type. It has three parts:

- **The gated run**: seeds 1, 2 and 3, 300 generated expressions each, nested to depth 3, over an operand table of typed columns (`int`, `bigint`, `decimal(23,10)`, `decimal(12,2)`, `money`, `float`, `nvarchar(100)`, `varchar(100)`) with seven rows of boundary values. It passes only when its differences equal `Snapshots/default-run-differences.tsv` exactly, so a new or changed difference fails it, and so does one that disappears. A construct is proven by this run when the snapshot holds no difference for it. The operand table holds no `bit`, `datetime`, `uniqueidentifier` or `nvarchar(max)` column today, so for a rule over one of those types (`LEN` of a Memo column among them) the snapshot proves nothing: such a rule is not proven by the gated run until the table holds that type. Adding those columns is a follow-up to #1798, not yet filed; the types the probe (#1799) finds for each column kind decide the rest.
- **The negative control** (`KnownDifferences`): the known wrong cases of the [Edge Cases](#edge-cases) table, with both outcomes pinned; a case that starts to agree with SQL Server is removed in the pull request that fixes it.
- **The reference observations** (`ReferenceObservations`, R01 to R26): SQL Server's answer for each behaviour the documentation leaves silent or ambiguous ([What the reference server showed](#what-the-reference-server-showed)).

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

Requirement 8 names this section. It is what the DML grammar on the branch of pull request #1698 admitted in a value before that branch was cut down to the interim rule (Decision H2), read from two files on that branch at commit `28d113d7`: `src/PPDS.Query/Planning/DmlStatementSupport.cs`, which admits constructs and their forms, and `src/PPDS.Cli/Services/Query/DmlColumnValidator.cs`, which narrows them by operand type and by column kind. The 2.0 floor is each construct below, with at least the forms given, for the operand type combinations and the column kinds given. Each must be in the rule table and proven before it returns to DML. The typed path may admit more, within the core requirements (a user-local date literal without an offset stays refused, core requirement 4); it may not admit less.

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

Every surface, and the ADO.NET provider, shows two marks that come with a `SELECT` result: the fallback label (core requirement 5) and the incomplete mark after an evaluation error (core requirement 2). The result carries both, in the fields [API/Contracts](#apicontracts) names; no surface works them out for itself. A surface that cannot carry the fallback label alongside the data refuses the fallback: there, an expression with no proven rule is refused with `Query.DmlUnsupportedConstruct` as in a script that writes. How each surface displays the marks is settled with #1809.

#### CLI Surface

Text output shows the fallback label and the incomplete mark (the data on stdout, the marks on stderr, as every status line). JSON output carries the fields of [API/Contracts](#apicontracts) beside the rows. CSV output has no place for the marks, so with CSV output the fallback is refused. Exit codes follow from the error codes (see [Error Handling](#error-handling)).

#### TUI Surface

Shows the fallback label and the incomplete mark. An export of a marked result to a format that cannot carry the mark is not offered.

#### Extension Surface

Shows the fallback label and the incomplete mark, from the daemon's fields of [API/Contracts](#apicontracts). An export of a marked result to a format that cannot carry the mark is not offered. How the extension shows a DML refusal and failed rows is in [data-explorer.md](./data-explorer.md). It recognises the four DML refusal codes of [Error Types](#error-types) and shows each as a refusal; an extension released before them still does, through the `dmlBlocked` flag (see the compatibility note there).

#### MCP Surface

Returns the fields of [API/Contracts](#apicontracts) in the tool result. Today the MCP SQL tool does not go through the shared query service or the planner; bringing it to the same script check and binder is part of the requirement above.

#### ADO.NET provider

A `DbDataReader` has no place a generic caller would read the marks from, so the provider refuses the fallback: through the ADO.NET provider, an expression with no proven rule is refused in every script, and the incomplete mark is raised as the evaluation error itself.

### Constraints

- Implement from Microsoft's SQL Server documentation and from tests against a real SQL Server. Do not read, copy, port or adapt source code from SQL 4 CDS or any other project.
- Fail closed: in a script that writes, what cannot be typed is refused before anything changes. The fallback serves scripts that do not write, only.
- A statement about SQL Server in this spec, in a rule or in a test is backed by the documentation page it names or by a case in the differential harness. Memory is not a source.

### Validation Rules

N/A. Refusals are listed under [Error Handling](#error-handling).

---

## Acceptance Criteria

| ID | Criterion | Test | Status |
|----|-----------|------|--------|
| AC-01 | In a script that writes, an expression anywhere in the script containing a construct outside the rule table refuses the script before any statement of it runs. The message names the unsupported part by kind (and, for a function, by name), gives its line and column, and repeats no literal value from the statement | TBD | 🔲 |
| AC-02 | Every construct in the rule table has a differential case set in `tests/PPDS.Query.DifferentialTests` that passes against the reference server for value, error and result type, and the gated run's snapshot holds no difference for it. For a rule over a SQL type the gated run's operand table does not hold, the snapshot proves nothing until the table holds that type (see [Reference server](#reference-server)); the criterion fails for such a rule | TBD | 🔲 |
| AC-03 | The differential generator, the admission test and the documented list read the rule table; adding a rule without cases fails the build | TBD | 🔲 |
| AC-04 | Each known wrong-value case in the [Edge Cases](#edge-cases) table produces the SQL Server value or a refusal, with a test that fails if it regresses (its negative-control case in `KnownDifferences` is removed in the same change) | TBD | 🔲 |
| AC-05 | A statement in which, for any row it would write, a text value is longer than the target column's maximum length, or a number, after conversion to the target column's SQL type, is outside the column's range, writes nothing. A text value is never shortened to fit: an over-long text value, trailing spaces included, fails this criterion if it is written in any form | TBD | 🔲 |
| AC-06 | The column type map has an entry for every Dataverse column kind in [Column kinds](#column-kinds): its SQL type with the evidence for it, or "refused" with the reason | TBD | 🔲 |
| AC-07 | An `INSERT ... SELECT` whose projection uses a construct outside the rule table is refused before any read or write, and each known wrong-value case of the Edge Cases table, written as an `INSERT ... SELECT` projection, stores the SQL Server value or is refused | TBD | 🔲 |
| AC-08 | Before `SELECT` moves to the typed path, the live SELECT suite's results are recorded as a baseline with each case marked right or wrong against the reference server. After the move, every case marked right returns the same result, whether it is computed on the typed path or by the fallback, and every case that differs is listed with its proof or its error | TBD | 🔲 |
| AC-09 | DML planned through the library without column metadata is refused with `Query.DmlMetadataUnavailable` | TBD | 🔲 |
| AC-10 | A statement in which the value for any row evaluates to an empty string writes nothing, and the message says to write `NULL`. A text value made only of spaces, or beginning or ending with a white-space character, from a literal or from a column, is refused with `Query.DmlValueStoredDifferently` for every text column kind and format not yet observed on the executor's path (core requirement 4); the criterion fails if any such value is written | TBD | 🔲 |
| AC-11 | A DML statement in which the value for any row raises an evaluation error (overflow, divide by zero, failed conversion) writes nothing | TBD | 🔲 |
| AC-12 | Every rule in the rule table names its documentation page, or says "documentation silent; reference observation R<n>" and names the case that establishes the behaviour, and has a unit test for each combination of operand types it admits; a rule without these fails the build | TBD | 🔲 |
| AC-13 | For each admitted pair of value and target column kind (the interim rule's table, then each rule's), a live Dataverse case through the DML executor's path (`CreateMultiple`, `UpdateMultiple`) writes a value and reads back the same value; for text, one of the values is at the column's maximum length counted in UTF-16 code units | TBD | 🔲 |
| AC-14 | The same unsupported script is refused with the same error code through the CLI, the TUI, the daemon, the MCP query tool and the ADO.NET provider | TBD | 🔲 |
| AC-15 | Every text rule whose result depends on collation records the collation it was proven under. When the organisation's collation cannot be established, or is not one the rule was proven under, a script that writes and uses that rule is refused, and in a script that does not write the expression goes to the labelled fallback | TBD | 🔲 |
| AC-16 | For every entry of the rule table, and for a generated set of constructs outside it, the binder's verdict and result type equal the table's. An architecture test fails if the product contains one of the earlier lists by name: the type categories of `DmlColumnValidator` or the value rules of `DmlStatementSupport` (`AllowedFunctions`, `CheckValue`), both from the DML safety work and removed by #1808. Once #1811 is done, the same test fails if any script that writes can reach the untyped `ExpressionCompiler` | TBD | 🔲 |
| AC-17 | Every construct in "Constructs required for 2.0" is in the rule table for each operand type combination and each column kind listed there; a missing one fails the build | TBD | 🔲 |
| AC-18 | The differential harness is a required CI check against a SQL Server container, and a nightly run executes it at a greater depth | TBD | 🔲 |
| AC-19 | Each error class in the Error Handling table raises its own error code; the four DML refusal classes raise the four codes named there, and the daemon's error data for each carries `dmlBlocked: true`; no message contains a GUID | TBD | 🔲 |
| AC-20 | A script that writes and uses, anywhere, a column, a conversion target or a literal whose SQL type is not on the closed list is refused before any statement of it runs | TBD | 🔲 |
| AC-21 | The values a statement writes are the held values its check before the first write computed: when the source rows change between the check and the write, the statement still writes the checked values, and no value is computed a second time | TBD | 🔲 |
| AC-22 | The differential harness runs on a developer machine against a local SQL Server, on Windows and on Linux, and passes the same cases as the CI run | TBD | 🔲 |
| AC-23 | While the interim rule is in force, a script that writes is refused with `Query.DmlUnsupportedConstruct` before any statement of it runs when it contains, anywhere, an expression PPDS evaluates that is not a literal, a column or `NULL`, or a value and target pair that is not in the table of admitted pairs. Cases: each admitted pair writes what the table says; a pair not in it (text to a number, money to text, a date literal, a `RealLiteral`, a `MoneyLiteral`) is refused; a negative literal in each listed form is admitted with its type; a GUID literal in the dashed form with or without both braces is admitted and one with only a closing brace, without dashes or in parentheses is refused; a text literal without `N` holding a character outside U+0020 to U+007E is refused with a message naming the `N` prefix; a column of a virtual label, calculated, rollup, date and time or other-environment kind as a source is refused; a variable or an ADO.NET parameter as a value is refused | TBD | 🔲 |
| AC-24 | In a script that does not write, an expression that has no proven type rule, in whole or in part, is computed as a whole by the fallback, and the result marks that column as computed by the fallback; when a filter, join condition, grouping, `HAVING` condition, sort, `OPENJSON` or `STRING_SPLIT` argument or filter on a metadata virtual table evaluated in PPDS used the fallback, the result as a whole is marked; when an earlier statement of the script used it for a variable, a cursor's query, a condition or a message, every later result is marked as a whole. The CLI's text and JSON output, the TUI, the extension, the daemon and the MCP query tool each carry the mark in the fields [API/Contracts](#apicontracts) names; the CLI's CSV output and the ADO.NET provider, which cannot carry it, refuse the fallback | TBD | 🔲 |
| AC-25 | No value computed by the fallback can reach a write. A script with a statement that writes and, anywhere in it, an expression with no proven type rule is refused before any statement of it runs, and the criterion fails if any statement of it runs. Cases, each with the unproven expression in a different place: an `INSERT ... VALUES` item; an `INSERT ... SELECT` projection; an `UPDATE ... SET` value; a filter of an `UPDATE` or of a `DELETE` that PPDS evaluates; a `MERGE` clause; `SET @v = ...`, `SELECT @v = ...` and `DECLARE @v = ...` before the write; `SELECT ... INTO #t`; the query of a cursor whose `FETCH ... INTO` feeds the write; an `IF` or `WHILE` condition, including `IF EXISTS (SELECT ...)`; a statement inside `BEGIN TRY` before the write; a plain `SELECT` and a `PRINT` beside the write; the write in a branch of `IF` that does not run; the write in a later `GO` batch than the unproven expression; a script whose only write is to a temporary table; a collation-dependent rule when the collation cannot be established | TBD | 🔲 |
| AC-26 | For each date column behaviour that has a rule, a live Dataverse case writes a literal and reads back the value after the documented conversion for that behaviour. A write to a behaviour without a rule and a live case is refused. A write of a literal without `Z` or an offset to a user-local column is refused; the criterion fails if such a literal is admitted by any rule. A user-local literal with an offset is stored at the UTC time the offset gives, and a date-only literal gives the same date under every session language | TBD | 🔲 |
| AC-27 | A value with more decimal places than the target column, and a decimal, floating-point or currency value written to an Integer column, is written as the value SQL Server's conversion for that pair of types gives (reference observations R07 to R09), and a live Dataverse case reads that value back unchanged | TBD | 🔲 |
| AC-28 | A `SELECT` in which an evaluation error occurs after rows have been returned raises the error when it is reached, keeps the rows already returned, and is marked incomplete on the CLI, the TUI, the extension, the MCP query tool and the ADO.NET provider | TBD | 🔲 |
| AC-29 | A DML statement that would write more rows than the DML row cap is refused before any value is computed. With the cap lifted (`--no-limit`), a statement whose pass before the first write would hold more rows than the engine's in-memory limit fails with `Query.MemoryLimitExceeded` and writes nothing | TBD | 🔲 |
| AC-30 | A statement that writes a value other than 0 or 1 to a Boolean column, or `NULL` to one, writes nothing and is refused with `Query.DmlColumnLimitExceeded` | TBD | 🔲 |
| AC-31 | A statement that writes, for any row, a whole number that is not one of a Picklist, State or Status column's options writes nothing and is refused with `Query.DmlColumnLimitExceeded`; the criterion fails if Dataverse is left to accept it | TBD | 🔲 |
| AC-32 | A statement that sets a State or Status column to `NULL`, or sets one of the two without the other, is refused before its first write | TBD | 🔲 |
| AC-33 | A write to a lookup column with more than one target table is refused before its first write; a write of a GUID to a lookup with one target table writes that GUID | TBD | 🔲 |
| AC-34 | A refusal or limit of PPDS's own (the codes of Error Types) raised inside `BEGIN TRY` is not caught: no `CATCH` block runs and no later statement runs. In a script whose second statement that writes is refused at run time, the rows the first wrote stay written, and the refusal message names the refused statement's line and the number of rows each earlier statement wrote | TBD | 🔲 |
| AC-35 | In a statement that writes, a condition sent to Dataverse as FetchXML with a date and time literal operand, a variable operand or a parameter operand is refused before any statement runs; a literal operand outside the admitted pairs is refused; a `TOP` that is not a whole-number literal and an `ORDER BY` expression that is not a column reference are refused; each with `Query.DmlUnsupportedConstruct` | TBD | 🔲 |
| AC-36 | A request that writes and reads state that an earlier request filled or changed with the fallback is refused before its first statement runs, once state is kept between requests; until then, a test shows that no product caller keeps a `SessionContext` between requests | TBD | 🔲 |

Status key: ✅ covered by passing test · ⚠️ test exists but failing · ❌ no test yet · 🔲 not yet implemented

Criteria that wait on something outside this spec: AC-13 waits on the live cases of the executor's path, and each text column kind and format stays refused under AC-10 until its case has run; AC-18 waits on the CI job; AC-22 waits on the LocalDB run in [Reference server](#reference-server).

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
| 7 | Text without the `N` prefix, and `CAST` of `nvarchar` to `varchar` | Keeps characters the code page cannot hold (`CAST(N'āb' AS varchar(3))` is `'āb'`; a Cyrillic literal without `N` is written as typed) | Converts through the database's code page and loses them silently (`'ab'`; `'??????'`, S21) ("nchar and nvarchar", Remarks; "char and varchar": "data loss during code page translations isn't reported"). In a write, the interim rule refuses such a literal and asks for `N'...'` | #1764 |
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

| Class | Condition | Outcome | Code |
|-------|-----------|---------|------|
| Unsupported construct | A script that writes contains, anywhere, a construct, a combination of operand types, or a SQL type, outside the rule table and the closed type list; a value and target pair, a source column or a condition operand the interim rule does not admit; a date literal of a form with no rule; a collation-dependent rule whose collation is not established; or reads tainted state (the whole-script rule). Also, on a surface that cannot carry the fallback label, any expression with no proven rule | The script is refused by the binder before any statement runs. The message names the unsupported part and where it is. In a script that does not write this is not an error: the expression is computed by the labelled fallback | `Query.DmlUnsupportedConstruct` |
| Assignment-limit failure | A computed value, after conversion to the target column's SQL type, fails a check in [Limits the assignment check covers](#limits-the-assignment-check-covers) | Nothing is written. The message names the column and the limit | `Query.DmlColumnLimitExceeded` |
| Stored-differently refusal | Dataverse stores something other than the value sent, or may, for a text value with white space at either end not yet observed on the executor's path (core requirement 4) | Nothing is written. The message says what to write instead | `Query.DmlValueStoredDifferently` |
| DML without metadata | A DML statement is planned and the target's column metadata is not available | Planning is refused | `Query.DmlMetadataUnavailable` |
| Evaluation error | Overflow, divide by zero, or a conversion that fails, while a value is computed | DML: nothing is written. `SELECT`: the error is raised when it is reached; rows already returned stay returned and every surface marks the result incomplete | Named in #1800 |
| Row cap exceeded | A DML statement would write more rows than the DML row cap | Refused before the pass that computes the values starts | `Query.DmlRowCapExceeded` (exists) |
| Pass over the in-memory limit | With the row cap lifted, the pass before the first write would hold more rows than the engine's in-memory limit | Fails before the first write | `Query.MemoryLimitExceeded` (exists) |

Decision G: "A code per class". The four DML refusal classes (the first four rows) each have a code of their own in `ErrorCodes.Query` (`src/PPDS.Cli/Infrastructure/Errors/ErrorCodes.cs`), named like the existing DML codes (`Query.DmlBlocked`, `Query.DmlRowCapExceeded`, `Query.DmlConfirmationRequired`), with the same string available to library callers as a `QueryErrorCode` constant, as existing query errors have (`src/PPDS.Dataverse/Query/Execution/QueryExecutionException.cs`). `Query.DmlBlocked` keeps its present meaning, the refusals of the DML safety guard (for example `DELETE` without `WHERE`, or a cross-environment write set to read-only), and is not used for these four. Application Services surface each as a `PpdsException` with that code (Constitution D4). The message contains no GUID and no stack trace; if a row's id is reported, it is carried in the error's `Context`, not in the message. How a failing row is named to the user is fixed in #1800. How a code becomes a CLI exit code is in [CONTRIBUTING.md](../CONTRIBUTING.md), "Errors and exit codes": the four codes take the exit code `Query.DmlBlocked` has.

Compatibility. An extension released before these codes, talking to a newer daemon, receives codes it does not know. The daemon therefore sends each of the four with `dmlBlocked: true` in the error data, as it does for `Query.DmlBlocked`. An older extension recognises a refusal by that flag or by a code it knows (`src/PPDS.Extension/src/utils/dmlSafety.ts`), so it shows each new code as a refusal with the daemon's message; it cannot tell the four classes apart, and it never offers to confirm or retry them, which it does only for `Query.DmlConfirmationRequired`. Nothing has been written when any of the four is raised. A script or MCP client that tests for `Query.DmlBlocked` no longer matches these refusals, a breaking change for it that the CLI and MCP changelogs state when the codes ship.

### Edge Cases

N/A here. The cases are in the [Edge Cases](#edge-cases) table under Acceptance Criteria.

### Recovery Strategies

- **Refusals before the script's first statement** (every refusal of the unsupported-construct class): nothing has changed. The user corrects the script and runs it again.
- **A refusal or error at run time** (a column limit, a value stored differently, the row cap, the in-memory limit, an evaluation error): the refused statement wrote nothing, but every earlier statement of the script that wrote has written its rows, and they stay written. The message names the refused statement and the rows each earlier statement wrote, so the user can see what has changed before running anything again.
- **A rejection by Dataverse after the first write** (see "What the check before the first write does not cover"): rows already written stay written. The failed rows are reported as [query.md](./query.md) and [data-explorer.md](./data-explorer.md) describe.

---

## API/Contracts

This spec adds no endpoint. Its contract with every surface is the error codes above, the `dmlBlocked` flag the daemon sends with the four DML refusal codes, and three fields on a query result, carried by the shared query service's result, the daemon's `query/sql` response, the MCP query tool's result and the CLI's JSON output:

| Field (JSON name) | Type | Meaning |
|-------------------|------|---------|
| `fallbackColumns` | array of column names, empty when none | The computed columns whose expression the fallback computed |
| `fallbackResult` | boolean | The result as a whole is marked: a filter, join, grouping, `HAVING`, sort or row source evaluated in PPDS used the fallback, or an earlier statement of the script used it for a variable, a cursor, a condition or a message |
| `incomplete` | boolean | An evaluation error was raised after rows had been returned (core requirement 2) |

A client that does not know these fields would ignore them and show fallback values without the label. So the daemon sends fallback values only to a client whose `query/sql` request says it shows the label (a boolean request field, `showsFallbackLabel`); for any other client, an older extension among them, it refuses the fallback as the ADO.NET provider does.

---

## Extension Points

### Adding a construct to the rule table

A construct is added by meeting every item of [What "a proven type rule" means](#what-a-proven-type-rule-means). There is no other way in: a function registered in the function library is not admitted in a script that writes until it has a rule, and until then a script that does not write computes it by the labelled fallback.

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

**Cost:** a script that writes can use only proven constructs, even in statements that only read. While the rule table is empty (the interim rule), a script that writes can compute nothing in PPDS.

### Decisions recorded on #1764

The maintainer's decisions of 2026-11-19 on #1764 that this spec carries:

| Decision | Where it is in this spec |
|----------|--------------------------|
| The admission rule is "a construct with a proven type rule" | Core requirement 1; "What a proven type rule means" |
| Until the typed path covers it, DML on `main` allows literals, columns and `NULL` | Interim rule |
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
| M | How the spec keeps fallback values away from writes | "One whole-script rule": a script that contains any statement that writes gets no fallback anywhere; an unproven construct in it is refused | Overview; Scope, the whole-script rule; core requirements 1, 5, 9 and 10; Components (Script check); Why one whole-script rule; AC-01, AC-20, AC-25 |
| G | Error codes for DML refusals | "A code per class" (unsupported construct; value outside the column's limits; value Dataverse would store differently; DML without metadata) | Error Types, the four codes and the compatibility note; Extension Surface; AC-09, AC-19 |
| H1 | Whether a negative number and a GUID cast count as literals in the interim rule | "Yes, include both" | Interim rule; AC-23 |
| H2 | Whether the DML safety work (#1698) is cut down to the interim rule before it lands | "Cut it down first" | Interim rule; Constructs required for 2.0 |
| I | A write of text made only of spaces | "Run it live first": one write and read-back, then refuse exactly what Dataverse does not store unchanged | Core requirement 4: admitted is exactly what has been observed on the executor's path, which is nothing yet, so text made only of spaces or with white space at either end is refused; the Web API observation of 2026-11-24 is recorded as evidence; AC-10, AC-13 |
| J | A `SELECT` expression that is only partly unproven | "The whole expression" goes to the labelled fallback | The whole-script rule; core requirement 5; AC-24 |
| K | A `SELECT` that needs a text rule when the collation cannot be established | "Labelled fallback" (a write is refused) | Core requirement 10; AC-15 |
| L | The pass before the first write when `--no-limit` lifts the row cap | "Yes, a ceiling", through the engine's existing memory limit, failing cleanly before the first write | Core requirement 3; Error Types; AC-29 |

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
| 2026-11-24 (review round 1) | Made fail closed where the review found gaps: writes defined by a closed read-only list; refusals inside a script (not catchable, per-statement limits, what stays written); the interim rule's literals by parse node, readable source columns and a table of admitted pairs; text without `N` outside U+0020 to U+007E refused; condition operands, `TOP` and `ORDER BY` in writes; Decision I narrowed to what the executor's path has shown (nothing yet); `ABS` corrected by the server's answer; observations owed to the harness; the result fields for the labels and the surfaces that refuse the fallback; acceptance criteria AC-30 to AC-36; the operand types the gated run lacks; the cases not covered and where they are |
| 2026-11-24 | The maintainer's decisions M, G, H1, H2, I, J, K and L written in: the whole-script rule replaces the table of places; a code per DML refusal class, with the compatibility note; the interim rule's literals; the memory ceiling on the pass before the first write. The SQL Server claims checked against the documentation and the differential harness's reference observations: LEN's type corrected; the silent and ambiguous behaviours replaced by the server's answers; the Edge Cases table checked and extended with the harness's findings; the reference server's settings recorded. No decision is pending. Decision I's live observation written in: a value made only of spaces is stored unchanged in a single-line text column, with the cases it does not cover refused until observed |
| 2026-11-21 | The maintainer's decisions of 2026-11-21 written into the requirements; refusals added for values stored for a later statement and for conditions; pending decisions cut to G to L |
| 2026-11-20 | Initial spec |

---

## Roadmap

- A runtime cross-check through the Dataverse SQL (TDS) endpoint, revisited after the typed engine exists.
