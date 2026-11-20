# SQL Values

**Status:** Draft
**Last Updated:** 2026-11-20
**Code:** [src/PPDS.Query/Execution/](../src/PPDS.Query/Execution/), [src/PPDS.Query/Planning/](../src/PPDS.Query/Planning/), [src/PPDS.Dataverse/Query/Execution/Functions/](../src/PPDS.Dataverse/Query/Execution/Functions/)
**Surfaces:** CLI, TUI, Extension, MCP

---

## Overview

Every value PPDS computes from SQL text (an `UPDATE ... SET` value, an `INSERT` value, a computed column, a client-side comparison) has a SQL type that is known before anything runs, and is computed in that type the way SQL Server computes it. One type model decides both what is refused and how each value is computed. The model is checked against a real SQL Server.

Some choices in this spec are not made yet. Each is marked **Decision pending** where it applies and listed in [Decisions pending](#decisions-pending). Until one is decided, the sentence it is attached to is not a basis for implementation.

### Goals

- **One model**: the code that decides whether an expression is allowed is the code that evaluates it. No second model predicts the first.
- **SQL Server results**: a computed value equals the value SQL Server produces for the same expression over the same typed inputs. That includes errors: where SQL Server, under the reference server's session settings, raises an error (overflow and divide by zero are the usual cases), PPDS raises an error; where SQL Server shortens or rounds a value without an error, PPDS returns the same shortened or rounded value.
- **Fail closed**: an expression with a part that has no type rule is refused before any row is read or written, with a message naming that part.
- **Checked before the first write**: every value a statement would write is computed and checked against the target column's limits before the first row is written.
- **Proof that composes**: each rule is proven once; nesting is covered by generated expressions run on a real SQL Server, not by listing statement shapes.

### Non-Goals

- Supporting every T-SQL function. A function without a proven rule is refused.
- Reproducing SQL Server query plans or performance.
- Collation-exact behaviour for every language. Text rules state which collation they are proven for and refuse outside it.
- Deciding which statement shapes are allowed (joins, `TOP`, `OUTPUT`, `MERGE`, hints) and which rows a statement selects. The rule table in this spec admits value expressions only; statements are governed by [query.md](./query.md).
- A runtime cross-check through the Dataverse SQL (TDS) endpoint (computing the same values with Dataverse's own SQL and refusing on a difference). Not in 2.0; see [Roadmap](#roadmap).

> **Decision pending (C):** how the binder learns an organisation's collation when a statement runs, and what is refused when it cannot. The third Non-Goal above depends on it.

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
| Binder | Assigns a SQL type to every node from the rules below; inserts conversion nodes; refuses a node with no rule |
| Evaluator | Computes a typed tree over a row. It never chooses an operation from the runtime type of a value |
| Assignment check | Converts each computed value to the target column's SQL type, checks the column's length, range and precision, and refuses the whole statement on the first failure |
| Rule table | The list of constructs that have a type rule, by operand types. The binder, the tests and the documentation read the same table |

None of these components exists yet; this spec is the description they are built and reviewed against. Until they exist, values are computed by the untyped `ExpressionCompiler` in `src/PPDS.Query/Execution/` and the function library in `src/PPDS.Dataverse/Query/Execution/Functions/`.

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

> **Decision pending (F1):** the engine also evaluates expressions in places no part of the typed value work names: arguments of window functions and of aggregates computed in PPDS, `OPENJSON` and `STRING_SPLIT` arguments, join conditions evaluated in PPDS, `MERGE` clauses, `IF` and `WHILE` conditions, and filters on the metadata virtual tables. #1811 removes the untyped compiler, so each of these is typed, refused, or kept on a labelled fallback. This follows from decision A.

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

> **Decision pending (F2):** what a rule cites when the documentation does not state the behaviour (the known example is whether a single currency division rounds or truncates at four places). "What a proven type rule means", item 1, has no answer for it.

### SQL types

The SQL type kinds are a closed list. A column whose type is not on it, a conversion to a type not on it, and a literal whose type is not on it are refused. The list is extended only by a change to this spec.

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
| `datetime` | date and time columns, date literals in ISO form (#1806) |
| `uniqueidentifier` | id and lookup columns |
| the type of `NULL` | the `NULL` constant |

The "Needed for" column says why a kind is on the list. It is not the column type map: which kind a Dataverse column has is established by the probe (#1799) and recorded in the map (#1800). If the probe of the endpoint reports a type that is not on this list (for example another date and time type), that column kind is refused until this list is extended.

> **Decision pending (B):** which date and time kinds are needed beyond `datetime` depends on what "the value sent" means for each date column behaviour.

### Core Requirements

1. A statement is refused when any value expression contains a construct, or a combination of operand types, that is not in the rule table. Nothing is read or written.
2. A value is computed in the SQL type the binder gave its node. Overflow, divide by zero and a conversion that fails are errors for the whole statement, unless the construct's own rule says otherwise (`TRY_CAST` and `TRY_CONVERT`).
3. Before the first write, every value for every row the statement would write is converted to the target column's SQL type and checked against the column's limits. Any failure refuses the whole statement. The values written are the values that were checked; they are not computed a second time.
4. Where Dataverse would store something other than the value sent, the statement is refused with a message that says what to write instead. The known case is the empty string, which Dataverse stores as `NULL`: a write whose value evaluates to an empty string is refused, and the message says to write `NULL`. This is checked on each row's computed value in the pass before the first write, so an expression that yields an empty string for any row (for example `LTRIM('  ')`) refuses the whole statement. A further case is added only by a change to this spec.
5. `SELECT` computed values use the same typed path. No query that returned the right result before returns a different result; each change is a correction with proof or a new clear error.
6. The rule table is the only list. The admission tests, the differential generator and the documented list of supported constructs are generated from it. The documented list is a reference page for users, generated from the rule table by the documentation generators under `scripts/docs-gen/` ([docs-generation.md](./docs-generation.md)); its path is recorded here when the generator for it exists.
7. No other code decides whether a value expression is allowed or what type it has. No construct is admitted by one component and typed differently by another.
8. At the 2.0 release the rule table covers, for DML, every construct in [Constructs required for 2.0](#constructs-required-for-20).

> **Decision pending (A):** requirements 1 and 5 together. Once `SELECT` is on the typed path, a `SELECT` that uses a construct with no proven rule is either refused, kept on a labelled untyped fallback for `SELECT` only, or every construct `SELECT` runs today is typed before 2.0.
>
> **Decision pending (B):** requirement 4 for date and time columns. Dataverse converts and stores a date and time according to the column's behaviour (user local, date only, time zone independent), so what "the value sent" means for each behaviour has to be defined before a date write can be called stored unchanged.
>
> **Decision pending (D):** requirement 3 when a value has more decimal places than the target column. Either it is converted as SQL Server converts to that column type, or the statement is refused.
>
> **Decision pending (E):** requirement 2 for a `SELECT`: what happens when an evaluation error occurs after rows have already been returned to the caller.
>
> **Decision pending (F3):** requirement 3 holds every computed value until all rows are checked. How that is bounded, and how it works with the DML row cap in [query.md](./query.md), is not stated.

What the check before the first write does not cover: Dataverse can still reject a row or a later batch for a reason that is not a value's type or limits (a plug-in, a required column, a privilege, a duplicate rule). Earlier batches are then already written. This spec removes the failures that can be known from the statement and the column metadata; it does not make a multi-batch write atomic.

Interim rule: until the rule table covers a construct, DML on `main` allows only literals, column references and `NULL` as values. The rule is decided and not yet in force: it takes effect when the DML safety work (pull request #1698) lands on `main`. Today `main` computes any value expression without a check.

### What "a proven type rule" means

A construct is in the rule table only when all of these hold:

1. The rule cites its SQL Server documentation page.
2. A unit test covers the rule for each combination of operand types it admits.
3. The differential suite passes for the construct against the [reference server](#reference-server): generated expressions that nest it with the other admitted constructs to the configured depth, over boundary values, comparing the value, the error and the result type.
4. Every column kind the rule can read has its SQL type in the column type map. The type is established when the map is built, by the read-only probe of the Dataverse SQL (TDS) endpoint on the test environments (#1799). A column kind whose type could not be established is not in the map and is refused. This is a test-time property of the map, per column kind: nothing connects to the endpoint when a user's statement runs, and an environment whose endpoint is switched off behaves the same as any other.
5. For a value that is written: a live Dataverse case shows that a value of that SQL type sent to each admitted target column kind is stored unchanged.

### Reference server

The differential suite compares PPDS with a real SQL Server.

- It is a required check in CI. A pull request cannot merge while it fails.
- It runs on a developer machine against a local SQL Server (LocalDB), and in CI against a container.
- A deeper generated run (more nesting, more cases) runs nightly.

Not fixed by a recorded decision, and therefore not stated here: the server version, the compatibility level, the collation, the session `SET` options, the nesting depth of the CI run and of the nightly run, the seeds, and the boundary values. #1798 fixes them, using what the probe of the Dataverse SQL endpoint reports (#1799) where the two must match, and they are recorded in this section when #1798 lands. Until they are recorded, "the configured depth" in the definition above has no value and no rule counts as proven.

### Constructs required for 2.0

Requirement 8 names this list. It is every construct the DML grammar on the branch of pull request #1698 admits in a value, read from `src/PPDS.Query/Planning/DmlStatementSupport.cs` on that branch at commit `28d113d7`. Each must be in the rule table, proven, with at least the forms given.

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
| `CAST`, `CONVERT` without a style | To `nvarchar(n)` with n from 1 to 4000, to `int`, to `decimal(p,s)` with both given and p at most 28; `CAST` of a text literal to `uniqueidentifier` |
| `TRY_CAST`, `TRY_CONVERT` without a style | To `int`, to `decimal(p,s)` with both given and p at most 28 |
| `UPPER`, `LOWER`, `LTRIM`, `RTRIM`, `TRIM`, `LEN` | One argument (`TRIM` without `LEADING`, `TRAILING`, `BOTH` or `FROM`) |
| `CONCAT` | Two or more arguments |
| `SUBSTRING` | Text, a literal start of 1 or more, a literal length of 0 or more |
| `ABS` | One argument |
| `ROUND` | A value and a literal number of places from 0 to 10 |
| `INSERT ... VALUES` items | Text, whole-number or decimal literal, `NULL`, a negative number literal, `CAST('<guid>' AS uniqueidentifier)` |

On that branch a value may read columns of these Dataverse kinds: String, Memo, Integer, Decimal, Money, Double; and may be written to columns of these kinds: String, Memo, Integer, Decimal, Money, Double, Boolean, Picklist, DateTime, Lookup, State, Status (`src/PPDS.Cli/Services/Query/DmlColumnValidator.cs`, same commit).

> **Decision pending (F4):** whether requirement 8 also binds those column kinds (each construct proven for each kind it can read and be written to on that branch), or the constructs only.

### Primary Flows

The Architecture diagram is the flow for every statement. There is no second flow.

### Surface-Specific Behavior

The CLI, the TUI, the daemon and the MCP query tool call the same binder. A caller that uses the libraries directly, the ADO.NET provider included, gets the same refusals. DML is not planned without column metadata: this is to be built (#1800); today the planner uses no column metadata and the value coercer passes a value through unchanged when metadata is missing.

### Constraints

- Implement from Microsoft's SQL Server documentation and from tests against a real SQL Server. Do not read, copy, port or adapt source code from SQL 4 CDS or any other project.
- Fail closed: what cannot be typed is refused before anything changes.
- A statement about SQL Server in this spec, in a rule or in a test is backed by the documentation page it names or by a differential case. Memory is not a source.

### Validation Rules

N/A. Refusals are listed under [Error Handling](#error-handling).

---

## Acceptance Criteria

| ID | Criterion | Test | Status |
|----|-----------|------|--------|
| AC-01 | A value expression containing a construct outside the rule table is refused before any read or write, and the message names the construct | TBD | 🔲 |
| AC-02 | Every construct in the rule table has a differential case set that passes against the reference server for value, error and result type | TBD | 🔲 |
| AC-03 | The differential generator, the admission test and the documented list read the rule table; adding a rule without cases fails the build | TBD | 🔲 |
| AC-04 | Each known wrong-value case in the [Edge Cases](#edge-cases) table produces the SQL Server value or a refusal, with a test that fails if it regresses | TBD | 🔲 |
| AC-05 | A statement whose computed value exceeds the target column's length, range or precision for any row it would write writes nothing | TBD | 🔲 |
| AC-06 | The column type map has an entry for every Dataverse column kind in [Column kinds](#column-kinds): its SQL type with the evidence for it, or "refused" with the reason | TBD | 🔲 |
| AC-07 | An `INSERT ... SELECT` whose projection uses a construct outside the rule table is refused before any read or write, and each known wrong-value case of the Edge Cases table, written as an `INSERT ... SELECT` projection, stores the SQL Server value or is refused | TBD | 🔲 |
| AC-08 | Before `SELECT` moves to the typed path, the live SELECT suite's results are recorded as a baseline with each case marked right or wrong against the reference server. After the move, every case marked right returns the same result, and every case that differs is listed with its proof or its error | TBD | 🔲 |
| AC-09 | DML planned through the library without column metadata is refused | TBD | 🔲 |
| AC-10 | A statement in which the value for any row evaluates to an empty string writes nothing, and the message says to write `NULL` | TBD | 🔲 |
| AC-11 | A DML statement in which the value for any row raises an evaluation error (overflow, divide by zero, failed conversion) writes nothing | TBD | 🔲 |
| AC-12 | Every rule in the rule table names its documentation page and has a unit test for each combination of operand types it admits; a rule without either fails the build | TBD | 🔲 |
| AC-13 | For each admitted pair of SQL type and target column kind, a live Dataverse case writes a value and reads back the same value | TBD | 🔲 |
| AC-14 | The same unsupported statement is refused with the same error code through the CLI, the TUI, the daemon, the MCP query tool and the ADO.NET provider | TBD | 🔲 |
| AC-15 | A text rule whose result depends on collation is refused when the collation is not one the rule is proven for | TBD | 🔲 |
| AC-16 | For every entry of the rule table, and for a generated set of constructs outside it, the binder's verdict and result type equal the table's; no other list of admitted value constructs exists in the product | TBD | 🔲 |
| AC-17 | Every row of the "Constructs required for 2.0" table is in the rule table; a missing one fails the build | TBD | 🔲 |
| AC-18 | The differential suite is a required CI check, and a nightly run executes it at a greater depth | TBD | 🔲 |
| AC-19 | Each error class in the Error Handling table raises its own error code, and no message contains a GUID | TBD | 🔲 |

Status key: ✅ covered by passing test · ⚠️ test exists but failing · ❌ no test yet · 🔲 not yet implemented

AC-15 depends on decision C. AC-08 depends on decision A. AC-05 and AC-13 depend on decisions B and D.

### Edge Cases

The known wrong-value cases. Each is allowed by the engine today and gives a value SQL Server would not. For each, the expected outcome is the SQL Server value, or a refusal before anything changes.

| # | Case | What the engine does today |
|---|------|----------------------------|
| 1 | `COALESCE`, `IIF` or `CASE` mixing a whole number with decimal, currency or float, followed by `/` | Integer division, where SQL Server divides in decimal |
| 2 | `ROUND` of a whole number | Returns a floating-point value, so a following `/` or `*` behaves differently and does not overflow where SQL Server does |
| 3 | Chained currency arithmetic | Keeps full precision, where SQL Server keeps 4 decimal places after each operation. A single currency division may also differ; whether SQL Server rounds or truncates at 4 places is not yet established |
| 4 | `ISNULL` | Does not convert its second value to the first value's type, precision, scale or length |
| 5 | `ROUND` that overflows its decimal type | Writes a value, where SQL Server raises an error |
| 6 | Whole-number literals above the 32-bit range | Treated as integers and divided as integers; SQL Server types them as decimal |
| 7 | Text literals without the `N` prefix | Not converted through the code page |
| 8 | A decimal written to a currency column | Skips the conversion to 4 places |
| 9 | A float compared with a decimal literal inside `IIF` or `CASE` | Compared at 15 significant digits |
| 10 | Values that exceed the target column's length, range or precision | Not checked before the first write, so a rejection by Dataverse part-way through leaves earlier batches committed |
| 11 | `UPPER` and `LOWER` | Use .NET's casing rules, not the organisation's collation |
| 12 | The same expressions in a `SELECT` and in `INSERT ... SELECT` projections | Return the same wrong values |

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
| Unsupported construct | A value expression contains a construct, or a combination of operand types, outside the rule table | Refused by the binder before any row is read or written. The message names the construct |
| Evaluation error | Overflow, divide by zero, or a conversion that fails, while a value is computed | DML: nothing is written. `SELECT`: see decision E |
| Assignment-limit failure | A computed value does not fit the target column's length, range or precision | Nothing is written. The message names the column and the limit |
| Stored-differently refusal | Dataverse would store something other than the value sent (the empty string) | Nothing is written. The message says what to write instead |
| DML without metadata | A DML statement is planned and the target's column metadata is not available | Planning is refused |

Each class has one code of its own in `ErrorCodes.Query` (`src/PPDS.Cli/Infrastructure/Errors/ErrorCodes.cs`), with the same code available to library callers as a `QueryErrorCode` constant, as existing query errors have (`src/PPDS.Dataverse/Query/Execution/QueryExecutionException.cs`). Application Services surface it as a `PpdsException` with that code (Constitution D4). The message contains no GUID and no stack trace; if a row's id is reported, it is carried in the error's `Context`, not in the message. The exact code names, and how a failing row is named to the user, are fixed in #1800 and written back to this table. How a code becomes a CLI exit code is in [cli.md](./cli.md).

### Recovery Strategies

- **Refusals and errors before the first write**: nothing has changed. The user corrects the statement and runs it again.
- **A rejection by Dataverse after the first write** (see "What the check before the first write does not cover"): rows already written stay written. The failed rows are reported as [query.md](./query.md) and [data-explorer.md](./data-explorer.md) describe.

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

### Decisions pending

Each is the maintainer's to make. The sentence it is attached to stands as drafted until then.

| ID | Question |
|----|----------|
| A | A `SELECT` that uses a construct with no proven rule, once `SELECT` is on the typed path: refuse it, keep a labelled untyped fallback for `SELECT` only, or type everything `SELECT` runs today before 2.0 |
| B | Date and time columns under core requirement 4: what "the value sent" means for each date column behaviour, and which date and time SQL types the closed list needs |
| C | How the binder learns the organisation's collation when a statement runs, and what is refused when it cannot |
| D | A value with more decimal places than the target column: convert as SQL Server does for that type, or refuse |
| E | What a `SELECT` does when an evaluation error occurs after rows have been returned |
| F1 | Expressions the engine evaluates in places no part of the typed value work names (see Scope) |
| F2 | What a rule cites when the documentation does not state the behaviour |
| F3 | How the pass before the first write is bounded, and how it works with the DML row cap |
| F4 | Whether the 2.0 requirement binds the column kinds of the earlier grammar as well as its constructs |

### Prior art

SQL 4 CDS (MIT licence) takes a typed approach of this kind and is acknowledged as prior art for the idea. Its source code is not read, copied, ported or adapted.

---

## Related Specs

- [query.md](./query.md) - parsing, planning, DML safety, row selection
- [architecture.md](./architecture.md) - Application Services and structured errors
- [cli.md](./cli.md) - how an error code becomes an exit code
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
