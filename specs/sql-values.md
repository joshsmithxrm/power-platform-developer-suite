# SQL Values

**Status:** Draft
**Last Updated:** 2026-11-24
**Code:** [src/PPDS.Query/Execution/](../src/PPDS.Query/Execution/), [src/PPDS.Query/Planning/](../src/PPDS.Query/Planning/), [src/PPDS.Dataverse/Query/Execution/Functions/](../src/PPDS.Dataverse/Query/Execution/Functions/), [tests/PPDS.Query.DifferentialTests/](../tests/PPDS.Query.DifferentialTests/)
**Surfaces:** CLI, TUI, Extension, MCP

---

## Overview

Every value PPDS computes from SQL text (an `UPDATE ... SET` value, an `INSERT` value, a computed column, a client-side comparison) has a SQL type that is known before anything runs, and is computed in that type the way SQL Server computes it. One type model decides both what is refused and how each value is computed. The model is checked against a real SQL Server by the differential harness in `tests/PPDS.Query.DifferentialTests` (#1798).

There is one exception, for reading only. In a script that contains no statement that writes, an expression that uses a construct with no proven type rule is not refused: the whole expression is computed by the earlier untyped evaluator, as a labelled fallback, and every surface says so. In a script that contains any statement that writes, nothing gets the fallback, in any statement: an unproven construct anywhere in the script refuses the script before its first statement runs. This is [the whole-script rule](#the-whole-script-rule).

Every choice this spec needed has been made by the maintainer; the decisions are listed under [Design Decisions](#design-decisions). One live result is still to be written in, for Decision I; it is marked **PLACEHOLDER** where it applies.

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
- **A statement that writes** is an `INSERT`, `UPDATE`, `DELETE` or `MERGE`, whatever its target (a temporary table included), and a `SELECT ... INTO`.
- **Contains** means anywhere in the text: inside a `BEGIN ... END` block, a branch of an `IF` that may not run, a `WHILE` body, a `TRY` or a `CATCH` block. Whether the script writes is decided from the parse of the whole script before its first statement runs, never from which statements happen to run.
- **In a script that writes**, every expression in every statement is bound with no fallback. An expression anywhere in the script with a construct, a combination of operand types or a SQL type outside the rule table refuses the whole script before its first statement runs: nothing is read, written or printed. This includes the plain `SELECT`s of that script, its variable assignments, its conditions, its cursor queries and its messages.
- **In a script that does not write**, an expression with a part that has no proven rule is computed, as a whole, by the fallback (Decision J), wherever it is. The fallback's output is labelled:
  - a computed column whose expression used the fallback is marked as computed by the fallback;
  - the result as a whole is marked when a filter, join condition, grouping, `HAVING` condition, sort, `OPENJSON` or `STRING_SPLIT` argument, or a filter on a metadata virtual table, evaluated in PPDS, used the fallback, because it decides which rows are returned;
  - every result that a statement returns after an earlier statement of the same script used the fallback for anything other than its own returned columns (a variable assignment, a cursor's query, an `IF` or `WHILE` condition, a `PRINT`, `THROW` or `RAISERROR` argument) is marked as a whole, because a value or a decision computed by the fallback may have reached it. No mark travels with a single value; the script carries it from that statement on.
- **A statement whose text is not known before the script runs** (`EXEC` of a string, dynamic SQL) is not supported today. If one is ever admitted, a script that contains one counts as a script that writes.
- **State kept between requests.** Nothing a script stores outlives its request on `main`: variables, temporary tables and cursors live in the script's own execution, and no product caller keeps a `SessionContext` between requests. If state is ever kept between requests (a temporary table a later request can read, for example), the request that fills it and every request that reads it count as one script for this rule.

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

The behaviours below were documentation silent, ambiguous or contradictory, or the documented rule was easy to misread. Each was run on the reference server (SQL Server 2022, under the settings in [Reference server](#reference-server)) and is pinned in `ReferenceObservations` by its id. A rule for one of these behaviours states the answer given here.

| # | Behaviour | SQL Server's answer | Source |
|---|-----------|---------------------|--------|
| 1 | Precision and scale of a decimal literal | From its digits: leading zeros are dropped, trailing zeros kept. `748.58` is `decimal(5,2)`, `0.5` is `decimal(1,1)`, `00012.3400` is `decimal(6,4)` | Documentation silent ("Constants" gives no precision or scale); reference observation R01 |
| 2 | Type of a whole-number literal | `int` up to 2,147,483,647; above it `decimal(p,0)` with p its digits (`2147483648` is `decimal(10,0)`) | "int, bigint, smallint, and tinyint": "Integer constants greater than 2,147,483,647 are converted to the decimal data type"; reference observation R02 |
| 3 | Type of `-2147483648` | `int`. The documentation implies `decimal`, a minus over the decimal `2147483648` ("- (Negative)": "Returns the data type of numeric_expression"); the server wins | Reference observation R02c |
| 4 | Type of a short text literal | `'abc'` is `varchar(3)`, `N'abc'` is `nvarchar(3)`, `''` is `varchar(1)` | Documentation ambiguous ("Constants" states the type only above 8,000 bytes); reference observation R03 |
| 5 | Type of the `NULL` constant | `int` where nothing else gives it a type (`NULL`, `ISNULL(NULL, NULL)`). `IIF` or `COALESCE` whose every value is the `NULL` constant is an error (8133, 4127) | Documentation ambiguous ("ISNULL" states `int` for one case; "COALESCE": "At least one of the null values must be a typed NULL"); reference observation R04 |
| 6 | A currency division | `money`, truncated toward zero at four places: `CAST(2 AS money) / 3` is `0.6666`, `CAST(-2 AS money) / CAST(3 AS money)` is `-0.6666` | Documentation silent; reference observation R05 |
| 7 | Currency with a decimal or a whole number | With a whole number or money: `money`, four places after each operation (`CAST(1 AS money) / 3 * 3` is `0.9999`). With a decimal: `decimal`, at the scale "Precision, scale, and length" gives (`CAST(1 AS money) * 1.1` is `1.10000`, `decimal(22,5)`), because decimal ranks above money | "Data type precedence"; reference observation R06 |
| 8 | Conversion that drops decimal places, per pair of types | `decimal` and `float` to `int`: truncated toward zero (2.7 gives 2, -2.7 gives -2). `money` to `int`: rounded half away from zero (2.5 gives 3, -2.5 gives -3). `decimal` and `float` to `money`: rounded half away from zero to four places. `decimal` to a smaller scale: rounded half away from zero (`1.25` in `decimal(10,1)` is `1.3`). `money` to `decimal`: rounded (`1.2399` in `decimal(10,2)` is `1.24`; `CAST(... AS decimal)` with no precision is `decimal(18,0)`). An assignment to a column converts the same way as `CAST` | "CAST and CONVERT", the "Truncating and rounding results" table. Its general sentence "the value is rounded" is wrong for the truncate rows, and "money and smallmoney" says "truncated" where the server rounds; the server and the table agree. Reference observations R07, R08, R09 |
| 9 | Casing | Depends on the collation, and differs from .NET's invariant casing: `UPPER(N'i' COLLATE Turkish_CI_AS)` is `İ`; `LOWER(N'İ')` is `i`; `UPPER(N'µ')` and `UPPER(N'ß')` are unchanged; `UPPER(N'ǆ')` is `Ǆ` | Documentation ambiguous ("Collation Precedence" lists `UPPER` and `LOWER` as collation-sensitive; their own pages are silent); reference observation R10 |
| 10 | `ROUND` beyond its type | An error (8115) for a positive or zero length too, and for money; the type is not widened: `ROUND(9.995, 2)`, `ROUND(CAST(99.99 AS decimal(4,2)), 1)`, `ROUND(CAST(9.5 AS decimal(2,1)), 0)`. The result keeps the input's type (`ROUND(1.005, 1)` is `1.000`, `decimal(4,3)`). Ties go half away from zero | Documentation silent on a positive length ("ROUND", Remarks, shows only `ROUND(748.58, -3)`); reference observation R11 |
| 11 | `yyyy-MM-dd` to `datetime` | Depends on the session: under `SET DATEFORMAT dmy` or `SET LANGUAGE French`, `'2026-11-05'` becomes 11 May 2026. To `date`, and in the form `yyyyMMdd`, it does not | Documentation silent on this form ("datetime" lists only `yyyy-MM-ddTHH:mm:ss[.mmm]` and `yyyyMMdd` as unaffected by the setting); reference observation R12 |
| 12 | A date and time string with `Z` or an offset to `datetime` | With `Z`: accepted, the clock time kept. With an offset: error 241. A `datetimeoffset` converted to `datetime` keeps the clock time and drops the offset (`10:00+05:00` gives `10:00`) | "datetime" (no offset range); "datetimeoffset": "the time zone is truncated"; reference observations R13, R14 |
| 13 | `TRY_CAST` and `TRY_CONVERT` | `NULL` when the conversion fails, including overflow and too little precision (`TRY_CAST(12345.6 AS decimal(4,1))`). An error in the argument stays an error (`TRY_CAST(1/0 AS int)`), and so does a conversion that is not permitted (to `xml`, 529) | "TRY_CAST"; overflow and argument errors documentation silent; reference observation R15 |
| 14 | Text to `uniqueidentifier` | The 36-character dashed form is accepted, with or without braces. Without dashes, in parentheses or with a leading space: error 8169. A longer string is cut to 36 characters and accepted (`'...FFjunk'` gives the GUID) | "uniqueidentifier" documents the dashed form and the truncation; braces and other spellings documentation silent; reference observations R16, R17 |
| 15 | Conversion to a shorter text type | Text is cut silently (`CAST(N'abcde' AS nvarchar(3))` is `'abc'`). A whole number too long for `nvarchar(n)` is an error (8115); for `varchar(n)` it gives `'*'`. An assignment to a column drops trailing spaces beyond its length and refuses any other over-long value (2628) | "CAST and CONVERT", "Truncating and rounding results"; "SET ANSI_WARNINGS"; reference observation R18 |
| 16 | `CONCAT` length | The result is cut to 4,000 characters when every argument is `nvarchar(n)` and to 8,000 when every argument is `varchar(n)` | Documentation ambiguous ("CONCAT": "truncates to 8,000 characters", without the `nvarchar` case); reference observation R19 |
| 17 | `TRIM`, `LTRIM`, `RTRIM` | Remove only the space `char(32)`. Tab, line feed, carriage return, no-break space and ideographic space stay | "TRIM", "LTRIM", "RTRIM": "Removes the space character char(32)"; reference observation R20 |
| 18 | `LEN` | Excludes trailing spaces only: a trailing tab or no-break space counts; `LEN(N'   ')` is 0. `int`, and `bigint` when the argument is `varchar(max)` or `nvarchar(max)` | "LEN"; reference observation R21 |
| 19 | `float` comparison | At full binary precision, against a decimal or a float: `CAST(0.1 AS float) + CAST(0.2 AS float) = 0.3` is false; `CAST(1.0000000000000002 AS float) > 1.0` is true | Documentation silent ("float and real" gives 15 digits as the display precision only); reference observation R22 |

Further documented behaviour the rules must follow:

- `CASE` evaluates its conditions in order and stops at the first that is true ("CASE", Remarks), so `CASE WHEN x = 0 THEN 0 ELSE 1 / x END` raises no error. The reference server likewise does not evaluate `ISNULL`'s second value when the first is not `NULL` (#1878).
- `ISNULL` converts its second value to the first value's type and shortens it to the first value's length ("ISNULL": "replacement_value can be truncated").
- `ABS` of `decimal(p,s)` is `decimal(38,s)`, and `ABS` of the smallest `int` overflows ("ABS").
- `CONCAT` turns `NULL` into an empty string, and returns `''` when every argument is `NULL` ("CONCAT"); `SUBSTRING` with a start past the end returns `''` ("SUBSTRING"). Both can produce the empty string that core requirement 4 refuses.
- Conversion of `float` to text uses 6 significant digits by default, and `CAST` to `nvarchar` without a length is `nvarchar(30)` ("CAST and CONVERT"). The 2.0 floor admits neither; a rule that admits them states them.
- Text that is not a number fails conversion to a number ("CAST and CONVERT"); the engine's .NET parsing accepts thousands separators, currency symbols and parentheses today. The 2.0 floor does not convert text to a number.

### SQL types

The SQL type kinds are a closed list. A column whose type is not on it, a conversion to a type not on it, and a literal whose type is not on it have no type rule: they refuse a script that writes, and are computed by the labelled fallback in a script that does not. The list is extended only by a change to this spec.

| Kind | Needed for |
|------|------------|
| `int` | whole-number columns, and whole-number literals from -2,147,483,648 to 2,147,483,647 ([What the reference server showed](#what-the-reference-server-showed), rows 2 and 3) |
| `bigint` | big-integer columns; `LEN` of a `max` type (row 18) |
| `decimal(p,s)` (SQL Server's `numeric` is the same type: "decimal and numeric are synonyms") | decimal columns, decimal literals, whole-number literals too large for `int` |
| `money` | currency columns |
| `float` | floating-point columns |
| `bit` | yes/no columns |
| `nvarchar(n)` and `nvarchar(max)` | text and multi-line text columns, literals written with the `N` prefix |
| `varchar(n)` | text literals written without the `N` prefix (#1804); `''` is `varchar(1)` (row 4) |
| `datetime` | date and time columns. A date literal is a text literal ("Constants", "Date-time constants") that is converted to the target's type. SQL Server's own conversion of text to `datetime` depends on the session language and refuses an offset (rows 11 and 12), so the conversion for each date column behaviour is defined by its rule (#1806, core requirement 4), not taken from a cast on a SQL Server session |
| `uniqueidentifier` | id and lookup columns |
| the type of `NULL` | the `NULL` constant. SQL Server types it `int` where nothing else gives it a type (row 5) |

The "Needed for" column says why a kind is on the list. It is not the column type map: which kind a Dataverse column has is established by the probe (#1799) and recorded in the map (#1800). If the probe of the endpoint reports a type that is not on this list (for example another date and time type), that column kind is refused until this list is extended.

### Core Requirements

1. A script that contains a statement that writes ([the whole-script rule](#the-whole-script-rule)) is refused, before its first statement runs, when any expression anywhere in it contains a construct, or a combination of operand types, that is not in the rule table: a value, a filter PPDS evaluates, a condition, a variable assignment, a cursor's query, a message, or a computed column of a plain `SELECT`. Nothing is read or written. The message names the unsupported part by its kind and, for a function, by its name (for example "the function DATEADD", "a CONVERT style", "arithmetic between a decimal and a floating-point value"), and says where it is in the script (line and column). It does not repeat literal values from the statement.
2. A value is computed in the SQL type the binder gave its node. Overflow, divide by zero and a conversion that fails are errors for the whole statement wherever SQL Server raises an error for them under the reference server's session settings (see [Reference server](#reference-server)), unless the construct's own rule says otherwise (`TRY_CAST` and `TRY_CONVERT`, row 13). In a statement that writes, such an error means nothing is written. In a `SELECT`, the error is raised when it is reached: rows already returned stay returned, and every surface marks the result incomplete.
3. Before the first write, every value for every row the statement would write is converted to the target column's SQL type and then checked against the column's length and range (defined in [Limits the assignment check covers](#limits-the-assignment-check-covers)). The conversion is SQL Server's rule for that pair of types, from the "Truncating and rounding results" table of "CAST and CONVERT" as the reference server confirmed it ([What the reference server showed](#what-the-reference-server-showed), row 8): a decimal or floating-point value written to an Integer column is truncated toward zero; a currency value written to an Integer column is rounded half away from zero; a value with more decimal places than a Decimal or Money column is rounded to the column's scale, a tie away from zero (reference observations R07 and R08). Decimal places alone are never a reason to refuse. Two conversions are not SQL Server's: a text value is never shortened (a text value longer than the column's maximum length is refused, not cut to fit; SQL Server's assignment would drop trailing spaces beyond the length, row 15, and PPDS refuses those as well), and a date and time value is converted by its behaviour's rule (core requirement 4). Any failure of a check refuses the whole statement. The computed values are held until every row has passed, and the held values are what is written; nothing is computed again for the write. The DML row cap ([query.md](./query.md)) bounds this pass: a statement over the cap is refused before the pass starts. When the cap is lifted (`--no-limit`), the pass is still bounded by the engine's existing in-memory limit, `MaxMaterializationRows` of the query plan context (500,000 rows by default), which `--no-limit` does not lift: a statement whose pass would hold more rows than that fails with `Query.MemoryLimitExceeded` before its first write (Decision L).
4. Where Dataverse would store something other than the value sent, the statement is refused with a message that says what to write instead. The rule is to refuse exactly what Dataverse does not store unchanged (Decision I), and each case is established by a live write and read-back. The known case is the empty string, which Dataverse stores as `NULL`: a write whose value evaluates to an empty string is refused, and the message says to write `NULL`. This is checked on each row's computed value in the pass before the first write, so an expression that yields an empty string for any row (for example `LTRIM('  ')`, or `CONCAT` of two `NULL`s) refuses the whole statement. A further case is added only by a change to this spec, with its live case.

   > **PLACEHOLDER (Decision I): to be replaced by the coordinator's live result.** One approved live write on a test environment sets a text column to a value made only of spaces and reads it back. This paragraph is replaced by what Dataverse stored (the value unchanged, `NULL`, or a shortened value) and the rule that follows from it: refused when what was read back differs from what was sent, admitted when it is the same. Until it is replaced, a value made only of spaces is refused as the empty string is. For comparison, SQL Server ignores trailing spaces in `LEN` (`LEN(N'   ')` is 0, reference observation R21e) and in `=`, so it does not tell such a value from `''` either; that is no evidence of what Dataverse stores.

   For a date and time column, "the value sent" is the value after the conversion Dataverse documents for the column's behaviour (user local, date only, time zone independent): a date write is stored unchanged when the value read back equals that converted value. The rule for each behaviour names the documentation page that states its conversion and has its own live case (#1806); a behaviour without both is refused. The literal must leave no room for interpretation. For a user-local column it carries `Z` or an explicit offset, and a literal without one is refused, because the stored value would otherwise depend on the calling user's time zone; the offset is applied to give the UTC time. SQL Server's conversion to `datetime` refuses an offset or drops it (row 12), so this conversion is the rule's own, not SQL Server's. A date-only literal `yyyy-MM-dd` is read as year, month and day whatever the session language, which SQL Server's `datetime` conversion does not guarantee (row 11). This binds #1806 and every later rule: whatever further literal forms are admitted, a user-local literal without an offset is not one of them.
5. In a script that does not write, computed values use the typed path wherever every part of the expression has a proven type rule. An expression with a part that has no proven rule is computed, as a whole, by the untyped fallback (Decision J), and labelled as [the whole-script rule](#the-whole-script-rule) says: per computed column, for the result as a whole when a filter, join condition, grouping, `HAVING` condition or sort evaluated in PPDS used it, and for every later result of the script when an earlier statement used it for anything other than its own returned columns. Every surface shows that label. The fallback is never used in a script that writes. No query that returned the right result before returns a different result; each change is a correction with proof or a new clear error.
6. The rule table is the only list. The admission tests, the differential generator and the documented list of supported constructs are generated from it. The documented list is a reference page for users, generated from the rule table by the documentation generators under `scripts/docs-gen/` ([docs-generation.md](./docs-generation.md)); its path is recorded here when the generator for it exists. Today the harness keeps its own `RuleTable` of what it generates; it reads the product's rule table once that exists.
7. No other code decides whether a value expression is allowed or what type it has. No construct is admitted by one component and typed differently by another.
8. At the 2.0 release the rule table covers, for DML, every construct in [Constructs required for 2.0](#constructs-required-for-20), for the operand type combinations and the column kinds listed there.
9. A column, a conversion target or a literal whose SQL type is not on the closed list in [SQL types](#sql-types) is treated as a construct outside the rule table is: it refuses a script that writes, and is computed by the labelled fallback in a script that does not.
10. The rule table records, for each text rule whose result depends on collation, the collation it was proven under (casing does depend on it: row 9). When a statement runs, such a rule applies only if the organisation's collation has been established and is one the rule was proven under. When the collation cannot be established, or is another one, the rule counts as not proven: a script that writes is refused, and in a script that does not write the expression goes to the labelled fallback (Decision K). How the collation is established when a statement runs is identified by #1799 and #1804 and recorded here; until a means is recorded, it cannot be established. `UPPER` and `LOWER` are in the 2.0 floor (core requirement 8), so a means of establishing the collation when a statement runs is itself required for 2.0: without one, those two constructs could not be admitted in a script that writes.

What the check before the first write does not cover: Dataverse can still reject a row or a later batch for a reason that is not a value's type or limits (a plug-in, a required column, a privilege, a duplicate rule). Earlier batches are then already written. This spec removes the failures that can be known from the statement and the column metadata; it does not make a multi-batch write atomic.

#### Interim rule

Until the rule table covers a construct, DML on `main` allows only literals, columns and `NULL` as values. Those are the words of the recorded decision ("Literals, columns, NULL").

- **A literal** is a text literal, a whole-number literal, a decimal literal, a negative number written as a minus sign directly before a whole-number or decimal literal (`-5`, `-2.5`), or `CAST` of a quoted GUID literal to `uniqueidentifier` (Decision H1). A negative literal has the type of the number it spells: `-2147483648` is `int` (row 3). The GUID literal is the 36-character dashed form, with or without braces; any other spelling is refused, because SQL Server refuses some (row 14: without dashes, in parentheses, with a leading space) and silently cuts a longer string to a valid GUID.
- **A column** is a reference to a column of the target table (`UPDATE ... SET`) or of the source table (`INSERT ... SELECT`).
- **Everything else** a script that writes evaluates in PPDS is refused under the whole-script rule while it has no proven rule, in any statement of that script, not only in the write's values: a computed column of a plain `SELECT` beside the write, a condition, a variable assignment. Conditions, sorts and aggregates sent to Dataverse as FetchXML are not evaluated by PPDS and are not affected. The checks of the DML safety guard (for example `DELETE` or `UPDATE` without `WHERE`) stay as they are.

The DML safety work (#1698) lands on `main` cut down to this rule (Decision H2). Every other construct its branch admits in a value (the rest of [Constructs required for 2.0](#constructs-required-for-20)) returns to DML one construct at a time, as each construct's rule is proven under [What "a proven type rule" means](#what-a-proven-type-rule-means). The rule is decided and not yet in force: today `main` computes any value expression without a check.

### Limits the assignment check covers

The assignment check works from the target column's metadata. What it covers, by target column kind:

| Target column kind | Checked before the first write |
|--------------------|--------------------------------|
| Text (String, Memo) | The value's length against the column's maximum length. A longer value is refused; it is never shortened to fit, trailing spaces included. The length is counted in UTF-16 code units, the unit of `nvarchar(n)` ("nchar and nvarchar": "n defines the string size in byte-pairs"); that Dataverse counts the same unit is shown by the live case of AC-13 before a text rule is admitted |
| Whole number, decimal, currency, floating point | The value against the column's minimum and maximum, and against the range of the column's SQL type. Decimal places are not a check: the conversion to the column's SQL type sets them by SQL Server's rule for the pair (core requirement 3) |
| Yes/no (Boolean) | The value is 0 or 1 |
| Choice, status, status reason (Picklist, State, Status) | The value is one of the column's option values. The DML safety work observed live that Dataverse stores a whole number that is not an option without an error, so this cannot be left to Dataverse. Status and status reason are not set to `NULL` |
| Lookup | The value is a GUID, and the lookup has exactly one target table, so the id says which table it refers to. A GUID written as text is in the 36-character dashed form, with or without braces (interim rule) |
| Date and time | The literal has a form admitted for the column's behaviour, and the behaviour has a rule (core requirement 4). A user-local literal without `Z` or an offset is refused |

Left to Dataverse, and therefore able to fail after the first write: whether the row a lookup refers to exists; whether a status and status reason pair is valid beyond what the metadata states; required columns; plug-ins and business rules; privileges; duplicate detection.

### What "a proven type rule" means

A construct is in the rule table only when all of these hold:

1. The rule cites its SQL Server documentation page. Where the documentation does not state the behaviour, the rule says "documentation silent; reference observation R<n>" and cites the case in the harness's `ReferenceObservations` that establishes the behaviour.
2. A unit test covers the rule for each combination of operand types it admits.
3. The differential harness passes for the construct against the [reference server](#reference-server): generated expressions that nest it with the other admitted constructs to the configured depth, over boundary values, comparing the value, the error and the result type, with no difference left for the construct in the gated run's snapshot.
4. Every column kind the rule can read has its SQL type in the column type map. The type is established when the map is built, by the read-only probe of the Dataverse SQL (TDS) endpoint on the test environments (#1799). A column kind whose type could not be established is not in the map and is refused. This is a test-time property of the map, per column kind: nothing connects to the endpoint when a user's statement runs, and an environment whose endpoint is switched off behaves the same as any other.
5. For a value that is written: a live Dataverse case shows that a value of that SQL type sent to each admitted target column kind is stored unchanged.

### Reference server

The differential harness in `tests/PPDS.Query.DifferentialTests` (#1798) is the proof mechanism of this spec: a statement in this spec about what SQL Server does is backed by a documentation page or by a case in it. It runs each expression on a real SQL Server and through PPDS's `QueryParser` and `ExpressionCompiler` (the path a computed column, a client-side filter and a DML value take) and compares the value, the error and the result type. It has three parts:

- **The gated run**: seeds 1, 2 and 3, 300 generated expressions each, nested to depth 3, over an operand table of typed columns (`int`, `bigint`, `decimal(23,10)`, `decimal(12,2)`, `money`, `float`, `nvarchar(100)`, `varchar(100)`) with seven rows of boundary values. It passes only when its differences equal `Snapshots/default-run-differences.tsv` exactly, so a new or changed difference fails it, and so does one that disappears. A construct is proven by this run when the snapshot holds no difference for it.
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

These settings matter to values, not only to errors: with `ANSI_WARNINGS` or `ARITHABORT` off, SQL Server returns `NULL` or a shortened value where it raises an error under the reference settings; with `NUMERIC_ROUNDABORT` on, a loss of precision is an error; the language and date format change how a date is read (row 11). PPDS computes as SQL Server does under these settings.

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

Operand types. The branch sorts a value into one of these: whole number (a whole-number literal, an Integer column, `LEN`, a conversion to `int`), decimal (a decimal literal, a Decimal column, a conversion to `decimal(p,s)`), currency (a Money column), floating point (a Double column), text (a text literal, a String or Memo column, a text function, `CONCAT`, a conversion to `nvarchar(n)`), and `NULL`. The branch's whole number is not one SQL type: `LEN` is `int`, and `bigint` when its argument is `nvarchar(max)` ("LEN"; row 18), so `LEN` of a Memo column is `bigint` on the typed path and arithmetic on it overflows where `bigint` does. The branch admits these combinations:

| Construct | Operand types the branch admits |
|-----------|---------------------------------|
| `+`, `-`, `*`, `/` | Two numbers of the same type, or a whole number with a decimal, a currency or a floating-point value. Not text. Not decimal with currency, decimal with floating point, or currency with floating point |
| `%` | Whole numbers and decimals, in the combinations above. Not currency, not floating point |
| Unary minus | A number |
| `COALESCE`, `IIF`, `CASE` results | All text, or all numbers. Among numbers: the same type, or a whole number with a decimal, a currency or a floating-point value. `NULL` beside other results, but not `NULL` for every result (SQL Server refuses that too: row 5) |
| Conditions inside `IIF` and `CASE`, and the comparison a simple `CASE` makes | Comparisons between numbers only, never with the `NULL` constant; `IS NULL` and `IS NOT NULL` of a value |
| `NULLIF` | Two numbers: the second of the first's type, or a whole number. Neither is the `NULL` constant |
| `ISNULL` | The first value is not the `NULL` constant. The second is `NULL`, of the first's type, or a whole number when the first is a decimal, a currency or a floating-point value |
| `CAST`, `CONVERT`, `TRY_CAST`, `TRY_CONVERT` to `int` or `decimal(p,s)` | A number, not text. Not currency to `int`. Not floating point to `decimal` |
| `CAST`, `CONVERT` to `nvarchar(n)` | Text (cut to n, as SQL Server does) or a whole number (an error when it needs more than n characters: row 15) |
| `CAST` to `uniqueidentifier` | A quoted GUID literal in the 36-character dashed form, with or without braces (row 14) |
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

Every surface, and the ADO.NET provider, shows two marks that come with a `SELECT` result: the fallback label (core requirement 5) and the incomplete mark after an evaluation error (core requirement 2). The result carries both; no surface works them out for itself. How each surface displays them is settled with #1809 and recorded in the subsections below.

#### CLI Surface

Shows the fallback label and the incomplete mark. Exit codes follow from the error codes (see [Error Handling](#error-handling)). Nothing else beyond the shared behaviour.

#### TUI Surface

Shows the fallback label and the incomplete mark. Nothing else beyond the shared behaviour.

#### Extension Surface

Shows the fallback label and the incomplete mark. How the extension shows a DML refusal and failed rows is in [data-explorer.md](./data-explorer.md). It recognises the four DML refusal codes of [Error Types](#error-types) and shows each as a refusal; an extension released before them still does, through the `dmlBlocked` flag (see the compatibility note there).

#### MCP Surface

Returns the fallback label and the incomplete mark in the tool result. Today the MCP SQL tool does not go through the shared query service or the planner; bringing it to the same script check and binder is part of the requirement above.

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
| AC-02 | Every construct in the rule table has a differential case set in `tests/PPDS.Query.DifferentialTests` that passes against the reference server for value, error and result type, and the gated run's snapshot holds no difference for it | TBD | 🔲 |
| AC-03 | The differential generator, the admission test and the documented list read the rule table; adding a rule without cases fails the build | TBD | 🔲 |
| AC-04 | Each known wrong-value case in the [Edge Cases](#edge-cases) table produces the SQL Server value or a refusal, with a test that fails if it regresses (its negative-control case in `KnownDifferences` is removed in the same change) | TBD | 🔲 |
| AC-05 | A statement in which, for any row it would write, a text value is longer than the target column's maximum length, or a number, after conversion to the target column's SQL type, is outside the column's range, writes nothing. A text value is never shortened to fit: an over-long text value, trailing spaces included, fails this criterion if it is written in any form | TBD | 🔲 |
| AC-06 | The column type map has an entry for every Dataverse column kind in [Column kinds](#column-kinds): its SQL type with the evidence for it, or "refused" with the reason | TBD | 🔲 |
| AC-07 | An `INSERT ... SELECT` whose projection uses a construct outside the rule table is refused before any read or write, and each known wrong-value case of the Edge Cases table, written as an `INSERT ... SELECT` projection, stores the SQL Server value or is refused | TBD | 🔲 |
| AC-08 | Before `SELECT` moves to the typed path, the live SELECT suite's results are recorded as a baseline with each case marked right or wrong against the reference server. After the move, every case marked right returns the same result, whether it is computed on the typed path or by the fallback, and every case that differs is listed with its proof or its error | TBD | 🔲 |
| AC-09 | DML planned through the library without column metadata is refused with `Query.DmlMetadataUnavailable` | TBD | 🔲 |
| AC-10 | A statement in which the value for any row evaluates to an empty string writes nothing, and the message says to write `NULL`. A value made only of spaces is treated as the live result of Decision I records (until it is written in: refused) | TBD | 🔲 |
| AC-11 | A DML statement in which the value for any row raises an evaluation error (overflow, divide by zero, failed conversion) writes nothing | TBD | 🔲 |
| AC-12 | Every rule in the rule table names its documentation page, or says "documentation silent; reference observation R<n>" and names the case that establishes the behaviour, and has a unit test for each combination of operand types it admits; a rule without these fails the build | TBD | 🔲 |
| AC-13 | For each admitted pair of SQL type and target column kind, a live Dataverse case writes a value and reads back the same value; for text, one of the values is at the column's maximum length counted in UTF-16 code units | TBD | 🔲 |
| AC-14 | The same unsupported script is refused with the same error code through the CLI, the TUI, the daemon, the MCP query tool and the ADO.NET provider | TBD | 🔲 |
| AC-15 | Every text rule whose result depends on collation records the collation it was proven under. When the organisation's collation cannot be established, or is not one the rule was proven under, a script that writes and uses that rule is refused, and in a script that does not write the expression goes to the labelled fallback | TBD | 🔲 |
| AC-16 | For every entry of the rule table, and for a generated set of constructs outside it, the binder's verdict and result type equal the table's. An architecture test fails if the product contains one of the earlier lists by name: the type categories of `DmlColumnValidator` or the value rules of `DmlStatementSupport` (`AllowedFunctions`, `CheckValue`), both from the DML safety work and removed by #1808. Once #1811 is done, the same test fails if any script that writes can reach the untyped `ExpressionCompiler` | TBD | 🔲 |
| AC-17 | Every construct in "Constructs required for 2.0" is in the rule table for each operand type combination and each column kind listed there; a missing one fails the build | TBD | 🔲 |
| AC-18 | The differential harness is a required CI check against a SQL Server container, and a nightly run executes it at a greater depth | TBD | 🔲 |
| AC-19 | Each error class in the Error Handling table raises its own error code; the four DML refusal classes raise the four codes named there, and the daemon's error data for each carries `dmlBlocked: true`; no message contains a GUID | TBD | 🔲 |
| AC-20 | A script that writes and uses, anywhere, a column, a conversion target or a literal whose SQL type is not on the closed list is refused before any statement of it runs | TBD | 🔲 |
| AC-21 | The values a statement writes are the held values its check before the first write computed: when the source rows change between the check and the write, the statement still writes the checked values, and no value is computed a second time | TBD | 🔲 |
| AC-22 | The differential harness runs on a developer machine against a local SQL Server, on Windows and on Linux, and passes the same cases as the CI run | TBD | 🔲 |
| AC-23 | While the interim rule is in force, a script that writes is refused before any statement of it runs when it contains, anywhere, an expression PPDS evaluates that is not a literal, a column or `NULL`. A negative number literal and `CAST` of a quoted GUID literal in the 36-character dashed form, with or without braces, are admitted; a GUID literal in any other spelling is refused | TBD | 🔲 |
| AC-24 | In a script that does not write, an expression that has no proven type rule, in whole or in part, is computed as a whole by the fallback, and the result marks that column as computed by the fallback; when a filter, join condition, grouping, `HAVING` condition or sort evaluated in PPDS used the fallback, the result as a whole is marked; when an earlier statement of the script used it for a variable, a cursor's query, a condition or a message, every later result is marked as a whole. The CLI, the TUI, the extension, the MCP query tool and the ADO.NET provider each show the mark | TBD | 🔲 |
| AC-25 | No value computed by the fallback can reach a write. A script with a statement that writes and, anywhere in it, an expression with no proven type rule is refused before any statement of it runs, and the criterion fails if any statement of it runs. Cases, each with the unproven expression in a different place: an `INSERT ... VALUES` item; an `INSERT ... SELECT` projection; an `UPDATE ... SET` value; a filter of an `UPDATE` or of a `DELETE` that PPDS evaluates; a `MERGE` clause; `SET @v = ...`, `SELECT @v = ...` and `DECLARE @v = ...` before the write; `SELECT ... INTO #t`; the query of a cursor whose `FETCH ... INTO` feeds the write; an `IF` or `WHILE` condition, including `IF EXISTS (SELECT ...)`; a statement inside `BEGIN TRY` before the write; a plain `SELECT` and a `PRINT` beside the write; the write in a branch of `IF` that does not run; a collation-dependent rule when the collation cannot be established | TBD | 🔲 |
| AC-26 | For each date column behaviour that has a rule, a live Dataverse case writes a literal and reads back the value after the documented conversion for that behaviour. A write to a behaviour without a rule and a live case is refused. A write of a literal without `Z` or an offset to a user-local column is refused; the criterion fails if such a literal is admitted by any rule. A user-local literal with an offset is stored at the UTC time the offset gives, and a date-only literal gives the same date under every session language | TBD | 🔲 |
| AC-27 | A value with more decimal places than the target column, and a decimal, floating-point or currency value written to an Integer column, is written as the value SQL Server's conversion for that pair of types gives (reference observations R07 to R09), and a live Dataverse case reads that value back unchanged | TBD | 🔲 |
| AC-28 | A `SELECT` in which an evaluation error occurs after rows have been returned raises the error when it is reached, keeps the rows already returned, and is marked incomplete on the CLI, the TUI, the extension, the MCP query tool and the ADO.NET provider | TBD | 🔲 |
| AC-29 | A DML statement that would write more rows than the DML row cap is refused before any value is computed. With the cap lifted (`--no-limit`), a statement whose pass before the first write would hold more rows than the engine's in-memory limit fails with `Query.MemoryLimitExceeded` and writes nothing | TBD | 🔲 |

Status key: ✅ covered by passing test · ⚠️ test exists but failing · ❌ no test yet · 🔲 not yet implemented

Criteria that wait on something outside this spec: AC-10 waits on the live result of Decision I for values made only of spaces; AC-18 waits on the CI job; AC-22 waits on the LocalDB run in [Reference server](#reference-server).

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
| 2 | `ROUND` | Of a whole number, returns a floating-point value; with a negative length, throws; the result does not keep the input's scale | Returns the input's type (`int` for `int`; `ROUND(1.005, 1)` is `1.000`, `decimal(4,3)`); `ROUND(748.58, -1)` is `750.00` ("ROUND", return types; row 10) | #1764, #1879 |
| 3 | Currency arithmetic | Keeps full precision, in a chain and in a single division | Money with money or a whole number: four places after each operation, a division truncated toward zero; money with a decimal is a decimal (rows 6 and 7) | #1764 |
| 4 | `ISNULL` | Does not convert its second value to the first value's type, precision, scale or length, so it neither shortens text nor raises the overflow; evaluates the second value when the first is not `NULL` | Converts the second value to the first's type and shortens it to the first's length (`ISNULL(CAST(NULL AS nvarchar(3)), N'a longer default')` is `'a l'`); does not evaluate it when the first is not `NULL` (`ISNULL(2.5, 2 % 0)` is `2.5`) ("ISNULL", Remarks; #1878) | #1764, #1878 |
| 5 | `ROUND` that overflows its type | Returns a value | Raises error 8115, for a negative, zero or positive length and for money (row 10) | #1764 |
| 6 | Whole-number literals above the 32-bit range | Does not reproduce on `main`: such a literal is computed as a .NET decimal. Kept because the DML safety branch showed integer division for it; what remains is the decimal scale (row 13) | Types it `decimal(p,0)` (row 2) | #1764 |
| 7 | Text without the `N` prefix, and `CAST` of `nvarchar` to `varchar` | Keeps characters the code page cannot hold (`CAST(N'āb' AS varchar(3))` is `'āb'`) | Converts through the database's code page and loses them silently (`'ab'`) ("nchar and nvarchar", Remarks; "char and varchar": "data loss during code page translations isn't reported") | #1764 |
| 8 | A decimal or float written to a currency column | Skips the conversion to four places | Rounds half away from zero to four places (row 8) | #1764 |
| 9 | `float` comparisons | Compares at 15 significant digits, through `decimal` (an overflow for the largest floats), against a decimal literal, a float or a whole number | Compares at full binary precision (row 19) | #1764 |
| 10 | Values that exceed the target column's length, range or precision | Not checked before the first write, so a rejection by Dataverse part-way through leaves earlier batches committed | Not a SQL Server rule; Dataverse behaviour, shown by a live case | #1764 |
| 11 | `UPPER` and `LOWER` | Use .NET's invariant casing (`LOWER(N'İ')` is unchanged, `UPPER(N'µ')` is `Μ`) | Follow the collation (row 9) | #1764 |
| 12 | The same expressions in a `SELECT` and in `INSERT ... SELECT` projections | Return the same wrong values | As the other rows | #1764 |
| 13 | Decimal arithmetic | Keeps .NET decimal's 28 digits; overflows where SQL Server fits a value in 38 digits | Gives each result the precision and scale of "Precision, scale, and length", reducing the scale at 38 digits (`2.50 / 2.00` is `1.250000`) | #1876 |
| 14 | A number plus text | Concatenates (`7 + N'5'` is `'75'`) | Converts the text to the number's type and adds (`12`), or raises a conversion error ("Data type precedence") | #1872 |
| 15 | Arithmetic overflow | Never raised: `int` arithmetic is computed in `bigint` (`2147483647 + 1` is `2147483648`), `bigint` wraps, money and float run out of range | Raises error 8115 ("+ (Addition)" and the other operators) | #1873 |
| 16 | `CAST`, `TRY_CAST` and `CONCAT` and the precision, scale and length of their result | Ignored: `CAST(2147483647 AS decimal(10,2))` is a value, `CAST(7 AS decimal(10,2))` is `7`, `TRY_CAST(12345.6 AS decimal(4,1))` is `12345.6`, `CAST(12345 AS nvarchar(3))` is `'123'`, `CONCAT` is not cut | Error 8115; `7.00`; `NULL`; error 8115; cut at 4,000 characters (rows 13, 15, 16) | #1874 |
| 17 | Conversions between types | .NET rules: money to integer truncates; float to text in round-trip form; GUID spellings SQL Server refuses are accepted, and a longer string is refused; `CAST(N'' AS int)` fails | Money to integer rounds (row 8); float to text with 6 digits; GUID as row 14; `CAST(N'' AS int)` is `0` ("CAST and CONVERT") | #1875 |
| 18 | A date and time string with an offset, to `datetime` | Applies the offset (`'...T10:00:00+05:00'` gives `05:00`, observed on a machine in UTC); a `datetimeoffset` cannot be converted | Refuses the string (241); drops the offset of a `datetimeoffset` (row 12) | #1877 |
| 19 | `TRIM`, `LTRIM`, `RTRIM` and `LEN` | Treat every white-space character as a space (.NET `Trim()`): `RTRIM('a' + CHAR(13) + CHAR(10))` is `'a'`, `LEN('ab' + CHAR(9))` is 2 | Remove and ignore only `char(32)`: `'a'+CR+LF`, 3 (rows 17 and 18) | #1632 |
| 20 | `LEN` of `nvarchar(max)` | `int` | `bigint` (row 18) | #1804 |
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
| Unsupported construct | A script that writes contains, anywhere, a construct, a combination of operand types, or a SQL type, outside the rule table and the closed type list, or a collation-dependent rule whose collation is not established | The script is refused by the binder before any statement runs. The message names the unsupported part and where it is. In a script that does not write this is not an error: the expression is computed by the labelled fallback | `Query.DmlUnsupportedConstruct` |
| Assignment-limit failure | A computed value, after conversion to the target column's SQL type, fails a check in [Limits the assignment check covers](#limits-the-assignment-check-covers) | Nothing is written. The message names the column and the limit | `Query.DmlColumnLimitExceeded` |
| Stored-differently refusal | Dataverse would store something other than the value sent (core requirement 4) | Nothing is written. The message says what to write instead | `Query.DmlValueStoredDifferently` |
| DML without metadata | A DML statement is planned and the target's column metadata is not available | Planning is refused | `Query.DmlMetadataUnavailable` |
| Evaluation error | Overflow, divide by zero, or a conversion that fails, while a value is computed | DML: nothing is written. `SELECT`: the error is raised when it is reached; rows already returned stay returned and every surface marks the result incomplete | Named in #1800 |
| Row cap exceeded | A DML statement would write more rows than the DML row cap | Refused before the pass that computes the values starts | `Query.DmlRowCapExceeded` (exists) |
| Pass over the in-memory limit | With the row cap lifted, the pass before the first write would hold more rows than the engine's in-memory limit | Fails before the first write | `Query.MemoryLimitExceeded` (exists) |

Decision G: "A code per class". The four DML refusal classes (the first four rows) each have a code of their own in `ErrorCodes.Query` (`src/PPDS.Cli/Infrastructure/Errors/ErrorCodes.cs`), named like the existing DML codes (`Query.DmlBlocked`, `Query.DmlRowCapExceeded`, `Query.DmlConfirmationRequired`), with the same string available to library callers as a `QueryErrorCode` constant, as existing query errors have (`src/PPDS.Dataverse/Query/Execution/QueryExecutionException.cs`). `Query.DmlBlocked` keeps its present meaning, the refusals of the DML safety guard (for example `DELETE` without `WHERE`, or a cross-environment write set to read-only), and is not used for these four. Application Services surface each as a `PpdsException` with that code (Constitution D4). The message contains no GUID and no stack trace; if a row's id is reported, it is carried in the error's `Context`, not in the message. How a failing row is named to the user is fixed in #1800. How a code becomes a CLI exit code is in [CONTRIBUTING.md](../CONTRIBUTING.md), "Errors and exit codes": the four codes take the exit code `Query.DmlBlocked` has.

Compatibility. An extension released before these codes, talking to a newer daemon, receives codes it does not know. The daemon therefore sends each of the four with `dmlBlocked: true` in the error data, as it does for `Query.DmlBlocked`. An older extension recognises a refusal by that flag or by a code it knows (`src/PPDS.Extension/src/utils/dmlSafety.ts`), so it shows each new code as a refusal with the daemon's message; it cannot tell the four classes apart, and it never offers to confirm or retry them, which it does only for `Query.DmlConfirmationRequired`. Nothing has been written when any of the four is raised. A script or MCP client that tests for `Query.DmlBlocked` no longer matches these refusals, a breaking change for it that the CLI and MCP changelogs state when the codes ship.

### Edge Cases

N/A here. The cases are in the [Edge Cases](#edge-cases) table under Acceptance Criteria.

### Recovery Strategies

- **Refusals and errors before the first write**: nothing has changed. The user corrects the script and runs it again.
- **A rejection by Dataverse after the first write** (see "What the check before the first write does not cover"): rows already written stay written. The failed rows are reported as [query.md](./query.md) and [data-explorer.md](./data-explorer.md) describe.

---

## API/Contracts

N/A. This spec adds no external API. The error codes above, and the `dmlBlocked` flag the daemon sends with them, are the contract other surfaces see.

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
| I | A write of text made only of spaces | "Run it live first": one write and read-back, then refuse exactly what Dataverse does not store unchanged | Core requirement 4 and its PLACEHOLDER; AC-10 |
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
| 2026-11-24 | The maintainer's decisions M, G, H1, H2, I, J, K and L written in: the whole-script rule replaces the table of places; a code per DML refusal class, with the compatibility note; the interim rule's literals; the memory ceiling on the pass before the first write. The SQL Server claims checked against the documentation and the differential harness's reference observations: LEN's type corrected; the silent and ambiguous behaviours replaced by the server's answers; the Edge Cases table checked and extended with the harness's findings; the reference server's settings recorded. No decision is pending; one live result (Decision I) is a placeholder |
| 2026-11-21 | The maintainer's decisions of 2026-11-21 written into the requirements; refusals added for values stored for a later statement and for conditions; pending decisions cut to G to L |
| 2026-11-20 | Initial spec |

---

## Roadmap

- A runtime cross-check through the Dataverse SQL (TDS) endpoint, revisited after the typed engine exists.
