# SQL Values

**Status:** Draft
**Last Updated:** 2026-11-20
**Code:** [src/PPDS.Query/Execution/](../src/PPDS.Query/Execution/), [src/PPDS.Query/Planning/](../src/PPDS.Query/Planning/), [src/PPDS.Dataverse/Query/Execution/Functions/](../src/PPDS.Dataverse/Query/Execution/Functions/)
**Surfaces:** CLI, TUI, Extension, MCP

---

## Overview

Every value PPDS computes from SQL text (a `SET` value, an `INSERT` value, a computed column, a client-side comparison) has a SQL type that is known before anything runs, and is computed in that type the way SQL Server computes it. One type model decides both what is refused and how each value is computed. The model is checked against a real SQL Server.

### Goals

- **One model**: the code that decides whether an expression is allowed is the code that evaluates it. No second model predicts the first.
- **SQL Server results**: a computed value equals the value SQL Server produces for the same expression over the same typed inputs, including errors (overflow, divide by zero, truncation).
- **Fail closed**: an expression with a part that has no type rule is refused before any row is read or written, with a message naming that part.
- **Checked before the first write**: every value a statement would write is computed and checked against the target column's limits before the first row is written.
- **Proof that composes**: each rule is proven once; nesting is covered by generated expressions run on a real SQL Server, not by listing statement shapes.

### Non-Goals

- Supporting every T-SQL function. A function without a proven rule is refused.
- Reproducing SQL Server query plans or performance.
- Collation-exact behaviour for every language. Text rules state which collation they are proven for and refuse outside it.

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
| SQL type | Kind plus precision, scale and length: `int`, `bigint`, `decimal(p,s)`, `money`, `float`, `nvarchar(n)`, `bit`, `datetime`, `uniqueidentifier`, and the type of `NULL` |
| Column type map | The SQL type of each Dataverse column kind, with where its precision, scale and length come from. One table, verified through the Dataverse SQL (TDS) endpoint |
| Binder | Assigns a SQL type to every node from the rules below; inserts conversion nodes; refuses a node with no rule |
| Evaluator | Computes a typed tree over a row. It never chooses an operation from the runtime type of a value |
| Assignment check | Converts each computed value to the target column's SQL type, checks the column's length, range and precision, and refuses the whole statement on the first failure |
| Rule table | The list of constructs that have a type rule, by operand types. The binder, the tests and the documentation read the same table |

None of these components exists yet; this spec is the description they are built and reviewed against. Until they exist, values are computed by the untyped `ExpressionCompiler` in `src/PPDS.Query/Execution/` and the function library in `src/PPDS.Dataverse/Query/Execution/Functions/`.

### Dependencies

- Uses: [query.md](./query.md) for parsing, planning and which rows a statement selects.
- Uses: Dataverse column metadata.

---

## Specification

### Type rules

Each rule is implemented from the SQL Server documentation page named here and from tests against a real SQL Server. No rule is taken from the source code of another project.

| Area | Source of the rule |
|------|--------------------|
| Type of a literal | "Constants (Transact-SQL)" |
| Which operand converts to which | "Data type precedence (Transact-SQL)", "Data type conversion (Database Engine)" |
| Precision, scale and length of a result | "Precision, scale, and length (Transact-SQL)" |
| Currency arithmetic | "money and smallmoney (Transact-SQL)" |
| Explicit conversion | "CAST and CONVERT (Transact-SQL)", "TRY_CAST", "TRY_CONVERT" |
| Conditional values | "CASE", "IIF", "COALESCE", "NULLIF", "ISNULL" |
| Each function | That function's own page (result type, NULL handling, errors) |

### Core Requirements

1. A statement is refused when any value expression contains a construct, or a combination of operand types, that is not in the rule table. Nothing is read or written.
2. A value is computed in the SQL type the binder gave its node. Overflow, divide by zero and a conversion that fails are errors for the whole statement.
3. Before the first write, every value for every selected row is converted to the target column's SQL type and checked against the column's limits. Any failure refuses the whole statement.
4. Where Dataverse would store something other than the value sent (for example an empty string), the statement is refused with a message that says what to write instead, unless this spec lists the case as an exception with its live proof.
5. `SELECT` computed values use the same typed path. No query that returned the right result before returns a different result; each change is a correction with proof or a new clear error.
6. The rule table is the only list. The admission tests, the differential generator and the documented list of supported constructs are generated from it.

### What "a proven type rule" means

A construct is in the rule table only when all of these hold:

1. The rule cites its SQL Server documentation page.
2. A unit test covers the rule for each combination of operand types it admits.
3. The differential suite passes for the construct against a real SQL Server: generated expressions that nest it with the other admitted constructs to the configured depth, over boundary values, comparing the value, the error and the result type.
4. Every column kind the rule can read has its SQL type in the column type map, verified through the Dataverse SQL endpoint or, where the endpoint is not available, refused.
5. For a value that is written: a live Dataverse case shows that a value of that SQL type sent to each admitted target column kind is stored unchanged.

### Surface-Specific Behavior

The CLI, the TUI, the daemon and the MCP query tool call the same binder. A caller that uses the libraries directly gets the same refusals; DML cannot be planned without column metadata.

---

## Acceptance Criteria

| ID | Criterion | Test | Status |
|----|-----------|------|--------|
| AC-01 | A value expression containing a construct outside the rule table is refused before any read or write, and the message names the construct | TBD | 🔲 |
| AC-02 | Every construct in the rule table has a differential case set that passes against a real SQL Server for value, error and result type | TBD | 🔲 |
| AC-03 | The differential generator, the admission test and the documented list read the rule table; adding a rule without cases fails the build | TBD | 🔲 |
| AC-04 | Each known wrong-value case listed on #1764 produces the SQL Server value or a refusal, with a test that fails if it regresses | TBD | 🔲 |
| AC-05 | A statement whose computed value exceeds the target column's length, range or precision for any selected row writes nothing | TBD | 🔲 |
| AC-06 | The column type map lists every Dataverse column kind that can be read or written, with the evidence for its SQL type | TBD | 🔲 |
| AC-07 | `INSERT ... SELECT` projections are computed on the typed path | TBD | 🔲 |
| AC-08 | `SELECT` computed values are on the typed path and the live SELECT suite shows no previously correct case changed | TBD | 🔲 |
| AC-09 | DML planned through the library without column metadata is refused | TBD | 🔲 |

Status key: ✅ covered by passing test · ⚠️ test exists but failing · ❌ no test yet · 🔲 not yet implemented

---

## Design Decisions

### Why one model instead of an engine and a checker?

**Context:** Three review rounds of the DML safety work kept finding statements that were allowed and wrote a value SQL Server would not. The checker reasoned about SQL types in categories; the engine computed with .NET runtime types; nothing compared them.

**Decision:** The binder that refuses is the binder that types the tree the evaluator runs.

**Alternatives considered:** limiting expressions to one level (wrong values occur with one operation or none); patching each reported case (the list does not converge).

### Why a real SQL Server as the reference?

**Context:** The earlier live suite computed expected values with helper functions written by the same authors as the engine.

**Decision:** Expected values for expression semantics come from running the same expression on SQL Server. The live Dataverse suite proves only what is specific to Dataverse: which rows are selected and what is stored.

### Prior art

SQL 4 CDS (MIT licence) takes a typed approach of this kind and is acknowledged as prior art for the idea. No source code from it is copied, ported or adapted.

---

## Related Specs

- [query.md](./query.md) - parsing, planning, statement allowlist, row selection

---

## Changelog

| Date | Change |
|------|--------|
| 2026-11-20 | Initial spec |
