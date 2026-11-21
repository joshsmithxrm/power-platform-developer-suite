# SQL Values

**Status:** Draft
**Last Updated:** 2026-11-21
**Code:** [src/PPDS.Query/Execution/](../src/PPDS.Query/Execution/), [src/PPDS.Query/Planning/](../src/PPDS.Query/Planning/), [src/PPDS.Dataverse/Query/Execution/Functions/](../src/PPDS.Dataverse/Query/Execution/Functions/)
**Surfaces:** CLI, TUI, Extension, MCP

---

## Overview

Every value PPDS computes from SQL text (an `UPDATE ... SET` value, an `INSERT` value, a computed column, a client-side comparison) has a SQL type that is known before anything runs, and is computed in that type the way SQL Server computes it. One type model decides both what is refused and how each value is computed. The model is checked against a real SQL Server.

There is one exception, for reading only. A `SELECT` that uses a construct with no proven type rule is not refused: that expression is computed by the earlier untyped evaluator, as a labelled fallback, and every surface says so. The fallback is never used for a value that is written.

Some choices in this spec are not made yet. Each is marked **Decision pending** where it applies and listed in [Decisions pending](#decisions-pending). Until one is decided, the sentence it is attached to is not a basis for implementation.

### Goals

- **One model**: the code that decides whether an expression is allowed is the code that evaluates it. No second model predicts the first.
- **SQL Server results**: a computed value equals the value SQL Server produces for the same expression over the same typed inputs. That includes errors: where SQL Server, under the reference server's session settings, raises an error (overflow and divide by zero are the usual cases), PPDS raises an error; where SQL Server shortens or rounds a value without an error, PPDS returns the same shortened or rounded value.
- **Fail closed**: in a statement that writes, an expression with a part that has no type rule is refused before any row is read or written, with a message naming that part. In a `SELECT`, it is computed by the labelled fallback and never passed off as a proven value.
- **Checked before the first write**: every value a statement would write is computed and checked against the target column's limits before the first row is written.
- **Proof that composes**: each rule is proven once; nesting is covered by generated expressions run on a real SQL Server, not by listing statement shapes.

### Non-Goals

- Supporting every T-SQL function. A function without a proven rule is refused in a statement that writes, and computed by the labelled fallback in a `SELECT`.
- Reproducing SQL Server query plans or performance.
- Collation-exact behaviour for every language. Text rules state which collation they are proven for and refuse outside it (core requirement 10).
- Deciding which statement shapes are allowed (joins, `TOP`, `OUTPUT`, `MERGE`, hints) and which rows a statement selects. The rule table in this spec admits value expressions only; statements are governed by [query.md](./query.md).
- A runtime cross-check through the Dataverse SQL (TDS) endpoint (computing the same values with Dataverse's own SQL and refusing on a difference). Not in 2.0; see [Roadmap](#roadmap).

---

## Architecture

```
SQL text
   |
   v
ScriptDom parse tree
   |
   v
Binder  <---- column types (Dataverse metadata through the column type map)
   |             each node gets a SQL type, or the statement is refused
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
| Binder | Assigns a SQL type to every node from the rules below; inserts conversion nodes. A node with no rule refuses a statement that writes, and sends a `SELECT` expression to the fallback |
| Evaluator | Computes a typed tree over a row. It never chooses an operation from the runtime type of a value |
| Assignment check | Converts each computed value to the target column's SQL type as SQL Server does, checks the column's length and range, and refuses the whole statement on the first failure |
| Rule table | The list of constructs that have a type rule, by operand types, with the collation each text rule was proven under. The binder, the tests and the documentation read the same table |
| Fallback | The untyped evaluator that exists today, kept for `SELECT` expressions with no proven rule. Its values are labelled. No statement that writes can reach it |

Apart from the fallback, none of these components exists yet; this spec is the description they are built and reviewed against. Until they exist, values are computed by the untyped `ExpressionCompiler` in `src/PPDS.Query/Execution/` and the function library in `src/PPDS.Dataverse/Query/Execution/Functions/`.

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
- ADO.NET parameter values and T-SQL variables (`DECLARE`, `SET @v`) where they are used in one of the above.

Out of scope:

- Conditions, sorts and aggregates that are sent to Dataverse as FetchXML. Dataverse evaluates them; [query.md](./query.md) governs how they are translated.
- Queries routed through the Dataverse SQL (TDS) endpoint. The endpoint computes their values.
- Which statements are allowed and which rows they select (see Non-Goals).

The engine also evaluates expressions in places no sub-issue of the typed value work names. They get the same answer as a `SELECT` computed column (core requirement 5): typed where every part has a proven rule; otherwise the labelled fallback where the expression is part of a `SELECT`, and a refusal where it is not. Place by place:

| Place | With a construct that has no proven rule |
|-------|------------------------------------------|
| Arguments of window functions and of aggregates computed in PPDS, in a `SELECT` | Labelled fallback |
| `OPENJSON` and `STRING_SPLIT` arguments, in a `SELECT` | Labelled fallback |
| Join conditions evaluated in PPDS, in a `SELECT` | Labelled fallback; the result as a whole is marked, because the condition decides which rows are returned |
| Filters on the metadata virtual tables, in a `SELECT` | Labelled fallback; the result as a whole is marked |
| Any of the above inside the `SELECT` of an `INSERT ... SELECT`, or in any other statement that writes | Refused. The fallback is never used for a write |
| `MERGE` clauses | Refused. `MERGE` writes |
| `IF` and `WHILE` conditions, and the value of `SET @v = ...` | Refused. They are not part of a `SELECT`, and they can decide whether or what a later statement writes |

Consequence for #1811 ("remove the untyped compiler"): the untyped compiler is not removed. It stays as the fallback, reachable only from a `SELECT` and only for an expression with no proven rule; no statement that writes can reach it. The rest of #1811 (ADO.NET parameters bound as typed values) is unchanged.

### Type rules

Each rule is implemented from the SQL Server documentation page named here and from tests against a real SQL Server. Do not read, copy, port or adapt source code from SQL 4 CDS or any other project.

| Area | Source of the rule |
|------|--------------------|
| Type of a literal | "Constants (Transact-SQL)" |
| Which operand converts to which | "Data type precedence (Transact-SQL)", "Data type conversion (Database Engine)" |
| Precision, scale and length of a result | "Precision, scale, and length (Transact-SQL)" |
| Currency arithmetic | "money and smallmoney (Transact-SQL)" |
| Explicit conversion | "CAST and CONVERT (Transact-SQL)", "TRY_CAST", "TRY_CONVERT" |
| Conditional values | "CASE", "IIF", "COALESCE", "NULLIF", "ISNULL" |
| Each function | That function's own page (result type, NULL handling, errors) |
| Comparisons, three-valued logic and ordering (inside `CASE` and `IIF`; filters and `ORDER BY` evaluated in PPDS), including the collation a text comparison uses | The pages are named in this row when the rules are written: #1803 for comparisons inside conditional values, #1810 for filters and `ORDER BY` |
| The type of an ADO.NET parameter and of a T-SQL variable | The pages are named in this row when the rule is written (#1811) |

Where the documentation does not state a behaviour (the known example is whether a single currency division rounds or truncates at four places), the rule says "documentation silent" and cites the differential case that establishes the behaviour.

### SQL types

The SQL type kinds are a closed list. A column whose type is not on it, a conversion to a type not on it, and a literal whose type is not on it have no type rule: they are refused in a statement that writes, and computed by the labelled fallback in a `SELECT`. The list is extended only by a change to this spec.

| Kind | Needed for |
|------|------------|
| `int` | whole-number columns and literals |
| `bigint` | big-integer columns |
| `decimal(p,s)` (SQL Server's `numeric` is the same type) | decimal columns, decimal literals, whole-number literals too large for `int` |
| `money` | currency columns |
| `float` | floating-point columns |
| `bit` | yes/no columns |
| `nvarchar(n)` and `nvarchar(max)` | text and multi-line text columns, literals written with the `N` prefix |
| `varchar(n)` | text literals written without the `N` prefix (#1804) |
| `datetime` | date and time columns. A date literal is a text literal that is converted to the target's type; the literal forms admitted for each date column behaviour are written by #1806 under core requirement 4 |
| `uniqueidentifier` | id and lookup columns |
| the type of `NULL` | the `NULL` constant |

The "Needed for" column says why a kind is on the list. It is not the column type map: which kind a Dataverse column has is established by the probe (#1799) and recorded in the map (#1800). If the probe of the endpoint reports a type that is not on this list (for example another date and time type), that column kind is refused until this list is extended.

### Core Requirements

1. A statement that writes (`INSERT`, `UPDATE`, `DELETE`, and the `SELECT` inside an `INSERT ... SELECT`) is refused when any value expression contains a construct, or a combination of operand types, that is not in the rule table. Nothing is read or written. The message names the unsupported part by its kind and, for a function, by its name (for example "the function DATEADD", "a CONVERT style", "arithmetic between a decimal and a floating-point value"), and says where it is in the statement (line and column). It does not repeat literal values from the statement.
2. A value is computed in the SQL type the binder gave its node. Overflow, divide by zero and a conversion that fails are errors for the whole statement wherever SQL Server raises an error for them under the reference server's session settings (fixed by #1798, see [Reference server](#reference-server)), unless the construct's own rule says otherwise (`TRY_CAST` and `TRY_CONVERT`). In a statement that writes, such an error means nothing is written. In a `SELECT`, the error is raised when it is reached: rows already returned stay returned, and every surface marks the result incomplete.
3. Before the first write, every value for every row the statement would write is converted to the target column's SQL type, as SQL Server converts to that type, and then checked against the column's length and range (defined in [Limits the assignment check covers](#limits-the-assignment-check-covers)). A value with more decimal places than the target column is not refused for that: the conversion gives it the column's scale, as it does in SQL Server. Any failure of a check refuses the whole statement. The values written are the values that were checked. The DML row cap ([query.md](./query.md)) bounds this pass: a statement over the cap is refused before the pass starts.
4. Where Dataverse would store something other than the value sent, the statement is refused with a message that says what to write instead. The known case is the empty string, which Dataverse stores as `NULL`: a write whose value evaluates to an empty string is refused, and the message says to write `NULL`. This is checked on each row's computed value in the pass before the first write, so an expression that yields an empty string for any row (for example `LTRIM('  ')`) refuses the whole statement. A further case is added only by a change to this spec. For a date and time column, "the value sent" is the value after the conversion Dataverse documents for the column's behaviour (user local, date only, time zone independent): a date write is stored unchanged when the value read back equals that converted value. The rule for each behaviour names the documentation page that states its conversion and has its own live case (#1806); a behaviour without both is refused.
5. `SELECT` computed values use the same typed path wherever every part of the expression has a proven type rule. An expression with a part that has no proven rule is computed, as a whole, by the untyped fallback, and labelled: the result says which computed columns came from the fallback, and says so for the result as a whole when a filter, join condition or sort evaluated in PPDS used it. Every surface shows that label. The fallback is never used by a statement that writes. No query that returned the right result before returns a different result; each change is a correction with proof or a new clear error.
6. The rule table is the only list. The admission tests, the differential generator and the documented list of supported constructs are generated from it. The documented list is a reference page for users, generated from the rule table by the documentation generators under `scripts/docs-gen/` ([docs-generation.md](./docs-generation.md)); its path is recorded here when the generator for it exists.
7. No other code decides whether a value expression is allowed or what type it has. No construct is admitted by one component and typed differently by another.
8. At the 2.0 release the rule table covers, for DML, every construct in [Constructs required for 2.0](#constructs-required-for-20), for the operand type combinations and the column kinds listed there.
9. A column, a conversion target or a literal whose SQL type is not on the closed list in [SQL types](#sql-types) is treated as a construct outside the rule table is: refused in a statement that writes, computed by the labelled fallback in a `SELECT`.
10. The rule table records, for each text rule whose result depends on collation, the collation it was proven under. When a statement runs, such a rule applies only if the organisation's collation has been established and is one the rule was proven under. When the collation cannot be established, or is another one, the rule counts as not proven: the statement is refused if it writes, and the expression goes to the labelled fallback in a `SELECT`. How the collation is established when a statement runs is identified by #1799 and #1804 and recorded here; until a means is recorded, it cannot be established.

> **Decision pending (I):** requirement 4 for text that is only spaces. "Evaluates to an empty string" is exact for a value of length zero. Whether a value made only of spaces is also refused depends on what Dataverse stores for it, which has not been run.

What the check before the first write does not cover: Dataverse can still reject a row or a later batch for a reason that is not a value's type or limits (a plug-in, a required column, a privilege, a duplicate rule). Earlier batches are then already written. This spec removes the failures that can be known from the statement and the column metadata; it does not make a multi-batch write atomic.

Interim rule: until the rule table covers a construct, DML on `main` allows only literals, columns and `NULL` as values. Those are the words of the recorded decision ("Literals, columns, NULL"). A literal is a text literal, a whole-number literal or a decimal literal; a column is a reference to a column of the target or source table. The rule is decided and not yet in force: today `main` computes any value expression without a check.

> **Decision pending (H):** two things the decision's words do not settle. First, whether "literals" includes a negative number (`-5` is a unary minus over a literal in the parse tree) and `CAST('<guid>' AS uniqueidentifier)`; the branch of pull request #1698 admits both in `INSERT ... VALUES`. Second, whether the DML safety work is cut down to the interim rule before it lands on `main`, since its branch admits the whole list in "Constructs required for 2.0".

### Limits the assignment check covers

The assignment check works from the target column's metadata. What it covers, by target column kind:

| Target column kind | Checked before the first write |
|--------------------|--------------------------------|
| Text (String, Memo) | The value's length against the column's maximum length |
| Whole number, decimal, currency, floating point | The value against the column's minimum and maximum, and against the range of the column's SQL type. Decimal places are not a check: the conversion to the column's SQL type sets them, as SQL Server does |
| Yes/no (Boolean) | The value is 0 or 1 |
| Choice, status, status reason (Picklist, State, Status) | The value is one of the column's option values. The DML safety work observed live that Dataverse stores a whole number that is not an option without an error, so this cannot be left to Dataverse. Status and status reason are not set to `NULL` |
| Lookup | The value is a GUID, and the lookup has exactly one target table, so the id says which table it refers to |
| Date and time | The literal has a form admitted for the column's behaviour, and the behaviour has a rule (core requirement 4) |

Left to Dataverse, and therefore able to fail after the first write: whether the row a lookup refers to exists; whether a status and status reason pair is valid beyond what the metadata states; required columns; plug-ins and business rules; privileges; duplicate detection.

### What "a proven type rule" means

A construct is in the rule table only when all of these hold:

1. The rule cites its SQL Server documentation page. Where the documentation does not state the behaviour, the rule says "documentation silent" and cites the differential case that establishes the behaviour.
2. A unit test covers the rule for each combination of operand types it admits.
3. The differential suite passes for the construct against the [reference server](#reference-server): generated expressions that nest it with the other admitted constructs to the configured depth, over boundary values, comparing the value, the error and the result type.
4. Every column kind the rule can read has its SQL type in the column type map. The type is established when the map is built, by the read-only probe of the Dataverse SQL (TDS) endpoint on the test environments (#1799). A column kind whose type could not be established is not in the map and is refused. This is a test-time property of the map, per column kind: nothing connects to the endpoint when a user's statement runs, and an environment whose endpoint is switched off behaves the same as any other.
5. For a value that is written: a live Dataverse case shows that a value of that SQL type sent to each admitted target column kind is stored unchanged.

### Reference server

The differential suite compares PPDS with a real SQL Server.

- It is a required check in CI. A pull request cannot merge while it fails.
- It runs on a developer machine against a local SQL Server (LocalDB on Windows, or the same container image CI uses), and in CI against a container.
- A deeper generated run (more nesting, more cases) runs nightly.

Not fixed by a recorded decision, and therefore not stated here: the server version, the compatibility level, the collation, the session `SET` options, the nesting depth of the CI run and of the nightly run, the seeds, and the boundary values. #1798 fixes them, using what the probe of the Dataverse SQL endpoint reports (#1799) where the two must match, and they are recorded in this section when #1798 lands. Until they are recorded, "the configured depth" in the definition above has no value and no rule counts as proven.

### Constructs required for 2.0

Requirement 8 names this section. It is what the DML grammar on the branch of pull request #1698 admits in a value today, read from two files on that branch at commit `28d113d7`: `src/PPDS.Query/Planning/DmlStatementSupport.cs`, which admits constructs and their forms, and `src/PPDS.Cli/Services/Query/DmlColumnValidator.cs`, which narrows them by operand type and by column kind. The 2.0 floor is each construct below, with at least the forms given, for the operand type combinations and the column kinds given. Each must be in the rule table and proven. The typed path may admit more; it may not admit less.

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
| `UPPER`, `LOWER`, `LTRIM`, `RTRIM`, `TRIM`, `LEN` | One argument (`TRIM` without `LEADING`, `TRAILING`, `BOTH` or `FROM`) |
| `CONCAT` | Two or more arguments |
| `SUBSTRING` | Text, a literal start of 1 or more, a literal length of 0 or more |
| `ABS` | One argument |
| `ROUND` | A value and a literal number of places from 0 to 10 |
| `INSERT ... VALUES` items | Text, whole-number or decimal literal, `NULL`, a negative number literal, `CAST('<guid>' AS uniqueidentifier)` |

Operand types. The branch sorts a value into one of these: whole number (a whole-number literal, an Integer column, `LEN`, a conversion to `int`), decimal (a decimal literal, a Decimal column, a conversion to `decimal(p,s)`), currency (a Money column), floating point (a Double column), text (a text literal, a String or Memo column, a text function, `CONCAT`, a conversion to `nvarchar(n)`), and `NULL`. It admits these combinations:

| Construct | Operand types the branch admits |
|-----------|---------------------------------|
| `+`, `-`, `*`, `/` | Two numbers of the same type, or a whole number with a decimal, a currency or a floating-point value. Not text. Not decimal with currency, decimal with floating point, or currency with floating point |
| `%` | Whole numbers and decimals, in the combinations above. Not currency, not floating point |
| Unary minus | A number |
| `COALESCE`, `IIF`, `CASE` results | All text, or all numbers. Among numbers: the same type, or a whole number with a decimal, a currency or a floating-point value. `NULL` beside other results, but not `NULL` for every result |
| Conditions inside `IIF` and `CASE`, and the comparison a simple `CASE` makes | Comparisons between numbers only, never with the `NULL` constant; `IS NULL` and `IS NOT NULL` of a value |
| `NULLIF` | Two numbers: the second of the first's type, or a whole number. Neither is the `NULL` constant |
| `ISNULL` | The first value is not the `NULL` constant. The second is `NULL`, of the first's type, or a whole number when the first is a decimal, a currency or a floating-point value |
| `CAST`, `CONVERT`, `TRY_CAST`, `TRY_CONVERT` to `int` or `decimal(p,s)` | A number, not text. Not currency to `int`. Not floating point to `decimal` |
| `CAST`, `CONVERT` to `nvarchar(n)` | Text or a whole number |
| `CAST` to `uniqueidentifier` | A quoted GUID literal, with dashes, with or without braces |
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

The Architecture diagram is the flow for every statement. There is no second flow.

### Surface-Specific Behavior

The CLI, the TUI, the daemon and the MCP query tool call the same binder. A caller that uses the libraries directly, the ADO.NET provider included, gets the same refusals. DML is not planned without column metadata: this is to be built (#1800); today the planner uses no column metadata and the value coercer passes a value through unchanged when metadata is missing.

Every surface, and the ADO.NET provider, shows two marks that come with a `SELECT` result: the fallback label (core requirement 5) and the incomplete mark after an evaluation error (core requirement 2). The result carries both; no surface works them out for itself. How each surface displays them is settled with #1809 and recorded in the subsections below.

#### CLI Surface

Shows the fallback label and the incomplete mark. Exit codes follow from the error codes (see [Error Handling](#error-handling)). Nothing else beyond the shared behaviour.

#### TUI Surface

Shows the fallback label and the incomplete mark. Nothing else beyond the shared behaviour.

#### Extension Surface

Shows the fallback label and the incomplete mark. How the extension shows a DML refusal and failed rows is in [data-explorer.md](./data-explorer.md); which codes it must recognise depends on decision G.

#### MCP Surface

Returns the fallback label and the incomplete mark in the tool result. Today the MCP SQL tool does not go through the shared query service or the planner; bringing it to the same binder is part of the requirement above.

### Constraints

- Implement from Microsoft's SQL Server documentation and from tests against a real SQL Server. Do not read, copy, port or adapt source code from SQL 4 CDS or any other project.
- Fail closed: in a statement that writes, what cannot be typed is refused before anything changes. The fallback serves `SELECT` only.
- A statement about SQL Server in this spec, in a rule or in a test is backed by the documentation page it names or by a differential case. Memory is not a source.

### Validation Rules

N/A. Refusals are listed under [Error Handling](#error-handling).

---

## Acceptance Criteria

| ID | Criterion | Test | Status |
|----|-----------|------|--------|
| AC-01 | In a statement that writes, a value expression containing a construct outside the rule table is refused before any read or write. The message names the unsupported part by kind (and, for a function, by name), gives its line and column, and repeats no literal value from the statement | TBD | 🔲 |
| AC-02 | Every construct in the rule table has a differential case set that passes against the reference server for value, error and result type | TBD | 🔲 |
| AC-03 | The differential generator, the admission test and the documented list read the rule table; adding a rule without cases fails the build | TBD | 🔲 |
| AC-04 | Each known wrong-value case in the [Edge Cases](#edge-cases) table produces the SQL Server value or a refusal, with a test that fails if it regresses | TBD | 🔲 |
| AC-05 | A statement whose computed value, after conversion to the target column's SQL type, exceeds the column's length or range for any row it would write writes nothing | TBD | 🔲 |
| AC-06 | The column type map has an entry for every Dataverse column kind in [Column kinds](#column-kinds): its SQL type with the evidence for it, or "refused" with the reason | TBD | 🔲 |
| AC-07 | An `INSERT ... SELECT` whose projection uses a construct outside the rule table is refused before any read or write, and each known wrong-value case of the Edge Cases table, written as an `INSERT ... SELECT` projection, stores the SQL Server value or is refused | TBD | 🔲 |
| AC-08 | Before `SELECT` moves to the typed path, the live SELECT suite's results are recorded as a baseline with each case marked right or wrong against the reference server. After the move, every case marked right returns the same result, whether it is computed on the typed path or by the fallback, and every case that differs is listed with its proof or its error | TBD | 🔲 |
| AC-09 | DML planned through the library without column metadata is refused | TBD | 🔲 |
| AC-10 | A statement in which the value for any row evaluates to an empty string writes nothing, and the message says to write `NULL` | TBD | 🔲 |
| AC-11 | A DML statement in which the value for any row raises an evaluation error (overflow, divide by zero, failed conversion) writes nothing | TBD | 🔲 |
| AC-12 | Every rule in the rule table names its documentation page, or says "documentation silent" and names the differential case that establishes the behaviour, and has a unit test for each combination of operand types it admits; a rule without these fails the build | TBD | 🔲 |
| AC-13 | For each admitted pair of SQL type and target column kind, a live Dataverse case writes a value and reads back the same value | TBD | 🔲 |
| AC-14 | The same unsupported statement is refused with the same error code through the CLI, the TUI, the daemon, the MCP query tool and the ADO.NET provider | TBD | 🔲 |
| AC-15 | Every text rule whose result depends on collation records the collation it was proven under. When the organisation's collation cannot be established, or is not one the rule was proven under, a statement that writes with that rule is refused and a `SELECT` expression with it goes to the labelled fallback | TBD | 🔲 |
| AC-16 | For every entry of the rule table, and for a generated set of constructs outside it, the binder's verdict and result type equal the table's. An architecture test fails if the product contains one of the earlier lists by name: the type categories of `DmlColumnValidator` or the value rules of `DmlStatementSupport` (`AllowedFunctions`, `CheckValue`), both from the DML safety work and removed by #1808. Once #1811 is done, the same test fails if any statement that writes can reach the untyped `ExpressionCompiler` | TBD | 🔲 |
| AC-17 | Every construct in "Constructs required for 2.0" is in the rule table for each operand type combination and each column kind listed there; a missing one fails the build | TBD | 🔲 |
| AC-18 | The differential suite is a required CI check, and a nightly run executes it at a greater depth | TBD | 🔲 |
| AC-19 | Each error class in the Error Handling table raises its own error code, and no message contains a GUID | TBD | 🔲 |
| AC-20 | A statement that writes and uses a column, a conversion target or a literal whose SQL type is not on the closed list is refused before any read or write | TBD | 🔲 |
| AC-21 | The values a statement writes equal the values its check before the first write computed: when the source rows change between the check and the write, the statement writes the checked values or writes nothing | TBD | 🔲 |
| AC-22 | The differential suite runs on a developer machine against a local SQL Server, on Windows and on Linux, and passes the same cases as the CI run | TBD | 🔲 |
| AC-23 | While the interim rule is in force, a DML statement with a value that is not a literal, a column or `NULL` is refused before any read or write | TBD | 🔲 |
| AC-24 | A `SELECT` with an expression that has no proven type rule returns the value the untyped fallback computes, and the result marks that column as computed by the fallback; when a filter, join condition or sort evaluated in PPDS used the fallback, the result as a whole is marked. The CLI, the TUI, the extension, the MCP query tool and the ADO.NET provider each show the mark | TBD | 🔲 |
| AC-25 | No statement that writes reaches the fallback: an `INSERT ... SELECT`, an `UPDATE` or a `MERGE` clause with an expression that has no proven type rule is refused before any read or write, as is an `IF` or `WHILE` condition or a `SET @v` value with one | TBD | 🔲 |
| AC-26 | For each date column behaviour that has a rule, a live Dataverse case writes a literal and reads back the value after the documented conversion for that behaviour; a write to a behaviour without a rule is refused | TBD | 🔲 |
| AC-27 | A value with more decimal places than the target column is written as the value SQL Server's conversion to the column's SQL type gives (a differential case), and a live Dataverse case reads that value back unchanged | TBD | 🔲 |
| AC-28 | A `SELECT` in which an evaluation error occurs after rows have been returned raises the error when it is reached, keeps the rows already returned, and is marked incomplete on the CLI, the TUI, the extension, the MCP query tool and the ADO.NET provider | TBD | 🔲 |
| AC-29 | A DML statement that would write more rows than the DML row cap is refused before any value is computed | TBD | 🔲 |

Status key: ✅ covered by passing test · ⚠️ test exists but failing · ❌ no test yet · 🔲 not yet implemented

Criteria that wait on a pending decision: AC-19 (G), AC-23 (H), AC-10 (I, for values made only of spaces).

### Test Examples

N/A until the differential harness exists (#1798). The first examples are the Edge Cases below, each as a case the harness runs on the reference server and through PPDS.

### Edge Cases

The known wrong-value cases, from #1764. Each is allowed by the engine today and gives a value SQL Server would not. For each, the expected outcome is the SQL Server value, or a refusal before anything changes.

What each row says SQL Server does has not been checked against the documentation yet. The last column names the page where it is to be checked; the page titles are themselves unverified. A row becomes a proven expectation when its page has been read or a differential case on the reference server shows the behaviour.

| # | Case | What the engine does today | SQL Server behaviour: page to check (not checked) |
|---|------|----------------------------|---------------------------------------------------|
| 1 | `COALESCE`, `IIF` or `CASE` mixing a whole number with decimal, currency or float, followed by `/` | Integer division, where SQL Server divides in decimal | "CASE", "IIF", "COALESCE" (return types); "Data type precedence"; "/ (Division)" |
| 2 | `ROUND` of a whole number | Returns a floating-point value, so a following `/` or `*` behaves differently and does not overflow where SQL Server does | "ROUND" (return types) |
| 3 | Chained currency arithmetic | Keeps full precision, where SQL Server keeps 4 decimal places after each operation. A single currency division may also differ; whether SQL Server rounds or truncates at 4 places is not yet established | "money and smallmoney"; "Precision, scale, and length" |
| 4 | `ISNULL` | Does not convert its second value to the first value's type, precision, scale or length | "ISNULL" (return types, remarks) |
| 5 | `ROUND` that overflows its decimal type | Writes a value, where SQL Server raises an error | "ROUND" (examples) |
| 6 | Whole-number literals above the 32-bit range | Treated as integers and divided as integers; SQL Server types them as decimal | "Constants"; "int, bigint, smallint, and tinyint" |
| 7 | Text literals without the `N` prefix | Not converted through the code page | "Constants" (character string constants, Unicode strings) |
| 8 | A decimal written to a currency column | Skips the conversion to 4 places | "CAST and CONVERT" (truncating and rounding results); "money and smallmoney" |
| 9 | A float compared with a decimal literal inside `IIF` or `CASE` | Compared at 15 significant digits | "Data type precedence"; "float and real" |
| 10 | Values that exceed the target column's length, range or precision | Not checked before the first write, so a rejection by Dataverse part-way through leaves earlier batches committed | Not a SQL Server rule; Dataverse behaviour, shown by a live case |
| 11 | `UPPER` and `LOWER` | Use .NET's casing rules, not the organisation's collation | "UPPER", "LOWER"; "Collation and Unicode support" |
| 12 | The same expressions in a `SELECT` and in `INSERT ... SELECT` projections | Return the same wrong values | As rows 1 to 9 and 11 |

### Column kinds

The Dataverse column kinds AC-06 means, by attribute type: String, Memo, Integer, BigInt, Decimal, Double, Money, Boolean, DateTime (each behaviour: user local, date only, time zone independent), Picklist, State, Status, Lookup, Customer, Owner, Uniqueidentifier, PartyList, EntityName, ManagedProperty, CalendarRules, Virtual, and the kinds identified by attribute type name: multi-select choice, image, file.

---

## Core Types

Not defined yet. The public shape of the SQL type, the typed tree, the rule table and the column type map, and the package each lives in, are fixed in #1800 and written back to this section.

---

## Error Handling

### Error Types

| Class | Condition | Outcome |
|-------|-----------|---------|
| Unsupported construct | A value expression contains a construct, a combination of operand types, or a SQL type, outside the rule table and the closed type list | DML: refused by the binder before any row is read or written. The message names the unsupported part and where it is. `SELECT`: not an error; the expression is computed by the labelled fallback |
| Evaluation error | Overflow, divide by zero, or a conversion that fails, while a value is computed | DML: nothing is written. `SELECT`: the error is raised when it is reached; rows already returned stay returned and every surface marks the result incomplete |
| Row cap exceeded | A DML statement would write more rows than the DML row cap | Refused before the pass that computes the values starts. The code is the existing `Query.DmlRowCapExceeded` |
| Assignment-limit failure | A computed value, after conversion to the target column's SQL type, fails a check in [Limits the assignment check covers](#limits-the-assignment-check-covers) | Nothing is written. The message names the column and the limit |
| Stored-differently refusal | Dataverse would store something other than the value sent (the empty string) | Nothing is written. The message says what to write instead |
| DML without metadata | A DML statement is planned and the target's column metadata is not available | Planning is refused |

Each class has one code of its own in `ErrorCodes.Query` (`src/PPDS.Cli/Infrastructure/Errors/ErrorCodes.cs`), with the same code available to library callers as a `QueryErrorCode` constant, as existing query errors have (`src/PPDS.Dataverse/Query/Execution/QueryExecutionException.cs`). Application Services surface it as a `PpdsException` with that code (Constitution D4). The message contains no GUID and no stack trace; if a row's id is reported, it is carried in the error's `Context`, not in the message. The exact code names, and how a failing row is named to the user, are fixed in #1800 and written back to this table. How a code becomes a CLI exit code is in [CONTRIBUTING.md](../CONTRIBUTING.md), "Errors and exit codes".

> **Decision pending (G):** DML refusals carry `Query.DmlBlocked` today: the DML safety guard on `main` raises it, the DML safety work raises it for every allowlist and column refusal, and the extension recognises it by code ([data-explorer.md](./data-explorer.md)). "One code of its own" for each class above means either the DML refusal classes keep `Query.DmlBlocked` and are told apart another way, or they get new codes and every surface that handles `Query.DmlBlocked` by code learns them.

### Edge Cases

N/A here. The cases are in the [Edge Cases](#edge-cases) table under Acceptance Criteria.

### Recovery Strategies

- **Refusals and errors before the first write**: nothing has changed. The user corrects the statement and runs it again.
- **A rejection by Dataverse after the first write** (see "What the check before the first write does not cover"): rows already written stay written. The failed rows are reported as [query.md](./query.md) and [data-explorer.md](./data-explorer.md) describe.

---

## API/Contracts

N/A. This spec adds no external API. The error codes above are the contract other surfaces see.

---

## Extension Points

### Adding a construct to the rule table

A construct is added by meeting every item of [What "a proven type rule" means](#what-a-proven-type-rule-means). There is no other way in: a function registered in the function library is not admitted in a statement that writes until it has a rule, and until then a `SELECT` computes it by the labelled fallback.

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

**Decision:** Expected values for expression semantics come from running the same expression on SQL Server. The live Dataverse suite proves only what is specific to Dataverse: which rows are selected and what is stored.

### Decisions recorded on #1764

The maintainer's decisions of 2026-11-19 on #1764 that this spec carries:

| Decision | Where it is in this spec |
|----------|--------------------------|
| The admission rule is "a construct with a proven type rule" | Core requirement 1; "What a proven type rule means" |
| Until the typed path covers it, DML on `main` allows literals, columns and `NULL` | The interim sentence after the Core Requirements |
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
| A `SELECT` that uses a construct with no proven type rule | "Keep a labelled untyped fallback for SELECT only" | Overview; core requirements 1, 5 and 9; Components (Fallback); Scope; AC-24, AC-25 |
| Date and time columns under the stored-differently rule | "Define the value sent as the value after the documented conversion" | Core requirement 4; Limits; AC-26 |
| How the binder learns the organisation's collation at run time | "Record the collation each text rule was proven under; refuse when it cannot be established" | Core requirement 10; Rule table; AC-15 |
| A value with more decimal places than the target column | "Convert as SQL Server does, then check length and range" | Core requirement 3; Assignment check; Limits; AC-05, AC-27 |
| A `SELECT` whose evaluation fails after rows have been returned | "Raise the error when it is reached; mark the result incomplete on every surface" | Core requirement 2; Error Types; AC-28 |
| Expressions the engine evaluates that no sub-issue names | "The same answer as Decision A, written per place into the spec's Scope section" | Scope, the table of places; AC-25 |
| What a rule cites when the documentation is silent | "The rule says "documentation silent" and cites the differential case that establishes the behaviour" | Type rules; "What a proven type rule means", item 1; AC-12 |
| How the pass before the first write is bounded | "The DML row cap bounds it; a statement over the cap is refused before the pass starts" | Core requirement 3; Error Types; AC-29 |
| Whether the 2.0 requirement binds column kinds as well as constructs | "Yes; otherwise "working in 2.0 DML" could be met for whole-number columns only" | Core requirement 8; Constructs required for 2.0; AC-17 |

### Decisions pending

Each is the maintainer's to make. The sentence it is attached to stands as drafted until then.

| ID | Question |
|----|----------|
| G | Whether DML refusals keep the code `Query.DmlBlocked` or get a code per error class, and then which surfaces must learn the new codes |
| H | Whether the interim rule's "literals" include a negative number and `CAST('<guid>' AS uniqueidentifier)`, and whether the DML safety work is cut down to the interim rule before it lands |
| I | Whether a value made only of spaces counts as an empty string under core requirement 4 |

### Prior art

SQL 4 CDS (MIT licence) takes a typed approach of this kind and is acknowledged as prior art for the idea. Its source code is not read, copied, ported or adapted.

---

## Related Specs

- [query.md](./query.md) - parsing, planning, DML safety, row selection
- [architecture.md](./architecture.md) - Application Services and structured errors
- [cli.md](./cli.md) - the CLI surface and its `ExceptionMapper`, which turns an error code into an exit code
- [data-explorer.md](./data-explorer.md) - how DML refusals and failed rows are shown in the extension
- [mcp.md](./mcp.md) - the MCP query tool, one of the callers of the binder
- [test-gates.md](./test-gates.md) - required CI checks, which the differential suite joins
- [docs-generation.md](./docs-generation.md) - the generators that publish the list of supported constructs

---

## Changelog

| Date | Change |
|------|--------|
| 2026-11-20 | Initial spec |

---

## Roadmap

- A runtime cross-check through the Dataverse SQL (TDS) endpoint, revisited after the typed engine exists.
