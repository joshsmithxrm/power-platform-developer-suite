# Review Convergence Skills Design

**Problem:** AI review→fix→review loops don't converge. Each cycle finds 20-30 new issues because:
1. No automated gates — type errors, regressions slip through
2. Reviewer bias — same context as implementer, confirms rather than challenges
3. Fixes introduce regressions — no mechanical safety net
4. No convergence tracking — can't tell if cycles are progressing

**Solution:** Three skills that enforce mechanical quality and structural isolation.

## Skills

### 1. `automated-quality-gates`
Runs compiler, linter, tests as pass/fail gates. Used before reviews and after every fix batch. Prevents regressions mechanically.

### 2. `impartial-code-review`
Code review with structural bias prevention. Reviewer subagent gets NO implementation context (no plan, no task descriptions). Gets ONLY: git diff, file contents, CLAUDE.md rules. Reads code, finds bugs.

### 3. `review-fix-converge`
Orchestration loop: gates → impartial review → fix → gates → review. Tracks issue count per cycle. Done when gates clean + review finds 0 critical + 0 important.

## Baseline Evidence (RED Phase)

From this session's actual failure data across 3 review cycles:
- Round 1 review found 27 issues
- Round 1 fixes (subagent-driven) introduced regressions: `who.profileName` type error, E2E `--disable-extensions`, query history resolve/hide ordering
- Round 2 "bugs-only" review (biased — reviewer told what to look for) found 1 issue
- Independent Round 3 review (user-initiated, fresh session, no bias) found 30 issues

Root causes confirmed: no automated gates, reviewer bias, fix-introduced regressions, no convergence tracking.
