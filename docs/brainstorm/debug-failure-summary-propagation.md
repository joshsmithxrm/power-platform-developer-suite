# Debug Diagnosis: failure_summary not preserved when CI-fix agent omits it

**Date:** 2026-04-26
**Reproduced:** Yes
**Fix classification:** Trivial
**Fix status:** Already applied at `scripts/pr_monitor.py:1316-1317`

> **Note:** The two-line fix described below is already present in the codebase. The "Before fix" code block is historical, shown to explain the defect; the "Fix location" instruction is now satisfied. This document exists as the diagnosis record and challenger input.

## Observed Behavior

`TestCiFixFailureSummaryPropagation::test_ci_failure_routes_to_fix[0]` fails:

```
AssertionError: failure_summary must preserve failure_log; got: None
```

The test:
1. Sets `failure_log = "(run list failed: failed to determine base repo: failed to run git: fatal: not a git repository...)"` 
2. Mocks `triage_common.dispatch_subagent` to return a JSON decision that omits `failure_summary`
3. Calls `_dispatch_ci_fix_agent(wt, 42, failure_log, "unknown", round_num=1, gemini_comments=[])`
4. Asserts `"run list failed" in decision.get("failure_summary", "")`

The assertion fails because `decision` has no `failure_summary` key — the agent JSON response omitted it and the code did not fall back to `failure_log`.

## Expected Behavior

When the CI-fix agent's JSON response omits `failure_summary`, `_dispatch_ci_fix_agent` must preserve the original `failure_log` as `decision["failure_summary"]`. This ensures callers can always inspect the failure context regardless of what the agent chose to include.

## Root Cause

**`scripts/pr_monitor.py:1307-1318`** — after parsing the agent's JSON into `decision`, the code coalesces several nullable fields (`files_touched`, `lines_added`, `lines_removed`, `scope_violation`) but did NOT coalesce `failure_summary`. A JSON response that includes valid `action` but omits `failure_summary` passed through cleanly with no `failure_summary` key, violating the contract documented in the function's docstring (line 1233–1236) and enforced by the test.

```python
# Before fix — failure_summary missing from coalesce block:
if decision.get("files_touched") is None:
    decision["files_touched"] = []
if decision.get("lines_added") is None:
    decision["lines_added"] = 0
if decision.get("lines_removed") is None:
    decision["lines_removed"] = 0
if decision.get("scope_violation") is None:
    decision["scope_violation"] = False
# failure_summary never set here → missing if agent omits it
return decision
```

## Evidence

- Test parametrize id "0" agent JSON: `'{"action": "fix", "files_touched": [], "lines_added": 0, "lines_removed": 0, "escalation_reason": null, "scope_violation": false}'` — explicitly omits `failure_summary`
- `_dispatch_ci_fix_agent` returned `decision` from `parse_triage_json_obj` without touching `failure_summary` when the JSON parse succeeded
- The two fallback paths (dispatch failure at line 1290, JSON-parse failure at line 1305) both set `failure_summary` correctly — only the *success* path was missing the coalesce
- Test `1 passed in 0.02s` after the fix confirms the coalesce is sufficient for the reported case

## Rejected Hypotheses

- **Hypothesis A — `parse_triage_json_obj` strips `failure_summary`:** Ruled out. The test agent JSON does not include `failure_summary` at all; the parser returns exactly what the JSON contains. The issue is the post-parse coalesce, not the parser.
- **Hypothesis B — wrong field name / typo:** Ruled out. All fallback paths that do set the field use the same key `"failure_summary"`. The success path simply had no corresponding coalesce line.

## Proposed Fix

Add `failure_summary` to the post-parse coalesce block, mirroring the pattern already used for the other optional fields:

```python
if not decision.get("failure_summary"):
    decision["failure_summary"] = failure_log[:200]
```

**Truncation note:** The payload sent to the agent uses `failure_log[:4000]` (line 1266). The fallback coalesce uses `failure_log[:200]`, matching the existing dispatch-failure and JSON-parse-failure fallback paths (lines 1290, 1305). The truncation difference is intentional: the `:4000` limit gives the agent full context for diagnosis; the `:200` coalesce is only reached when the agent omitted the field entirely, so it is a last-resort fallback where brevity is acceptable. If the agent returns a non-empty `failure_summary`, `not decision.get("failure_summary")` is falsy and the coalesce does not run — the agent's value is preserved.

**Untested case (c):** The fix condition `if not decision.get("failure_summary")` also coalesces on `null` and `""` in addition to the missing-key case. The scenario where the agent returns a valid non-empty `failure_summary` that should NOT be overwritten has no parametrize case. Correctness is verified by code inspection: a truthy agent-supplied value causes `not ...` to be `False`, so the coalesce is skipped. Adding a parametrize case for this is recommended but not blocking.

**Fix location:** `scripts/pr_monitor.py` immediately before `return decision` — already applied at lines 1316-1317.

## Risk Assessment

- **Breaking changes:** None. The field was previously absent (undefined) in the success path; it is now always a non-empty string. All existing call sites either mock `_dispatch_ci_fix_agent` entirely or pass `"failure_summary": "ok"` in their mock return value — none depend on the field being absent.
  - Direct callers in tests confirmed by grep: `tests/test_pr_monitor.py:2354` (mocks agent return with explicit `failure_summary`), `tests/test_pr_monitor.py:2706` (the reproducing test). All other references (lines 94, 149, 2200, 2469, 2552, 2660, 2833, 2908) patch `_dispatch_ci_fix_agent` itself at the boundary.
  - Production caller: `scripts/pr_monitor.py:1705` — only call site in production code.
- **Side effects:** None — only touches the success-path coalesce block; dispatch-failure and JSON-parse-failure paths are unchanged.
- **Affected tests:** `TestCiFixFailureSummaryPropagation::test_ci_failure_routes_to_fix[0]` is the direct reproducer and passes after the fix.
- **Affected callers:** `_save_decision` at line 1356 reads `payload.get("failure_summary", "")` defensively — that guard is now redundant but harmless.

## Recommendation

**Trivial fix** — single file, two lines, no behavior change for any existing caller.

Hand off to `/implement` with `mode: tdd`. The reproducing test (`test_ci_failure_routes_to_fix[0]`) must fail against the unpatched code and pass after applying the fix. A second parametrize case covering agent-supplied non-empty `failure_summary` is recommended to close the untested case (c) gap noted by the challenger.
