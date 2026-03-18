# Converge

Orchestration loop that converges to PR-ready code. Runs gates, then impartial review, then fix, then repeat until clean. Tracks issue counts per cycle to prove convergence — if counts aren't decreasing, something is wrong.

## Why This Exists

Evidence from vscode-extension-mvp:
- Round 1 fixes introduced regressions (type errors, broken E2E)
- Round 2 "bugs-only" review (biased) found 1 issue
- Round 3 independent review found 30 issues
- Round 4 found 32 issues (2.7x more than Round 1)

Without convergence tracking, review cycles can actually diverge — each round creating more problems than it solves.

## Input

$ARGUMENTS = optional max cycles (default: 5). Set lower for small changes.

## Process

### Step 1: Initialize Convergence Tracking

```
Cycle | Gate | Review Critical | Review Important | Regressions | Verdict
------|------|----------------|-----------------|-------------|--------
```

### Step 2: Run Cycle

**A. Clear Stale State**
Before running gates, clear the previous gate result in `.workflow/state.json`:
1. Read the file (create `{}` if missing)
2. Set `gates.passed` to `null`
3. Set `gates.commit_ref` to `null`
4. Write the file back

This ensures that a fix cycle cannot accidentally rely on a stale gate pass.

**B. Quality Gates**
Invoke `/gates` (use the skill).
- If gates FAIL: fix the failures first (dispatch fix agent), re-run gates
- Gates must PASS before proceeding to review — `/gates` writes fresh state on pass
- Record: did fixes introduce any new gate failures? (= regression)

**C. Impartial Code Review**
Invoke `/review` (use the skill).
- Record: count of CRITICAL and IMPORTANT findings

**D. Evaluate Convergence**
Update the tracking table. Check:
- Are critical findings decreasing or zero?
- Are important findings decreasing?
- Were regressions introduced by the previous fix cycle?

**E. Fix or Finish**

**If 0 CRITICAL and 0 IMPORTANT:**
Done. CONVERGED. Report final tracking table. Ready for PR.

**If findings exist but counts are decreasing:**
Dispatch fix agents for CRITICAL findings first, then IMPORTANT. Each fix agent receives: the finding, the affected file, the constitution, and relevant spec ACs. After fixes, return to Step 2A (next cycle).

**If counts are NOT decreasing (stalled or diverging):**
STOP. Report to user:
```
Convergence stalled at cycle N.
Cycle N-1: X critical, Y important
Cycle N:   X critical, Y important (no improvement)

This usually means:
1. Fixes are introducing new issues at the same rate they solve old ones
2. The review is finding different issues each time (scope creep)
3. There's an architectural problem that point fixes can't address

Recommend: Review the findings for patterns. If the same category
of issue keeps appearing, the spec or design may need updating first.
```
Ask user how to proceed.

**If max cycles reached:**
STOP. Report tracking table and remaining findings. Ask user whether to continue or merge as-is with known issues documented.

### Step 3: Final Report

```
## Convergence Report

### Tracking
| Cycle | Gate | Critical | Important | Regressions | Verdict |
|-------|------|----------|-----------|-------------|---------|
| 1 | 2 errors | 5 | 8 | — | FIX |
| 2 | pass | 2 | 3 | 0 | FIX |
| 3 | pass | 0 | 1 | 0 | FIX |
| 4 | pass | 0 | 0 | 0 | CONVERGED |

### Outcome
Converged in 4 cycles. All quality gates pass. Impartial review
finds 0 critical, 0 important issues. Ready for PR.

### AC Status
| Spec | ACs Passing | ACs Failing |
|------|-------------|-------------|
| connection-pooling.md | 5/5 | 0 |
| tui-foundation.md | 3/4 | AC-04 (not in scope) |
```

## Rules

1. **Gates before review** — always. No point reviewing code that doesn't compile.
2. **Fix critical first** — don't fix suggestions while critical findings exist.
3. **Track regressions** — if a fix introduces a new gate failure, count it.
4. **Convergence is measurable** — if numbers aren't going down, stop and escalate.
5. **Max 5 cycles default** — if it hasn't converged in 5 cycles, it's an architectural problem, not a fix problem.
6. **Never skip the impartial review** — even if gates are green. Gates catch mechanical errors; reviews catch logic errors.
