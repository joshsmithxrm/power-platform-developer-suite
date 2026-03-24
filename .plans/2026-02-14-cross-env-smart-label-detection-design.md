# Cross-Environment Smart Label Detection

**Date:** 2026-02-14
**Status:** Approved

## Problem

Cross-environment queries require `[LABEL].dbo.entity` (3-part name) because ScriptDom only sets `DatabaseIdentifier` with 3+ parts. The `dbo` schema is meaningless in Dataverse — it's ceremony that users will forget, and forgetting it causes **silent wrong-environment execution** (`[QA].account` queries locally instead of QA).

## Design

### Resolution Logic

In `ExecutionPlanBuilder.PlanTableReference()`, resolve table references in this order:

| Step | Condition | Result |
|------|-----------|--------|
| 1 | `ServerIdentifier` or `DatabaseIdentifier` set | Cross-env (existing 3/4-part logic) |
| 2 | `SchemaIdentifier == "dbo"` | Local (ignore schema) |
| 3 | `SchemaIdentifier != null` → `RemoteExecutorFactory(schema)` returns executor | Cross-env |
| 3b | `SchemaIdentifier != null` → `RemoteExecutorFactory(schema)` returns null | **FAIL**: actionable error |
| 4 | `BaseIdentifier` only | Local |

### Syntax Examples

| Query | Behavior |
|-------|----------|
| `SELECT * FROM account` | Local |
| `SELECT * FROM dbo.account` | Local |
| `SELECT * FROM [QA].account` | Cross-env (QA label found) |
| `SELECT * FROM [QA].dbo.account` | Cross-env (3-part, existing logic) |
| `SELECT * FROM [STAGING].account` | Error: no label "STAGING" |
| `SELECT * FROM [SERVER].[DB].dbo.account` | Cross-env (4-part, existing logic) |

### Error Messages

Unknown label (step 3b):
```
No environment found matching 'STAGING'. Configure a profile with label 'STAGING' to use cross-environment queries.
```

### Label Validation

`dbo` is reserved and cannot be used as an environment label. Enforce at configuration time:
```
Error: 'dbo' cannot be used as an environment label (reserved for SQL schema convention).
```

### Safety

- `ContainsCrossEnvironmentReference()` must also check `SchemaIdentifier` (not just `ServerIdentifier`/`DatabaseIdentifier`) to correctly detect cross-env joins for DML safety.
- Unknown non-`dbo` schemas always fail — never silently execute locally.

## Scope

- Modify `ExecutionPlanBuilder.PlanTableReference()` to check `SchemaIdentifier` against `RemoteExecutorFactory`
- Modify `ContainsCrossEnvironmentReferenceInTableRef()` to detect 2-part cross-env references
- Add label validation to reject `dbo` as a profile label
- Tests for all syntax variants
