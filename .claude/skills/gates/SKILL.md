---
name: gates
description: Gates
---

# Gates

Mechanical pass/fail checks. No judgment, no opinions - compiler, linter, and tests either pass or they don't. Run before code reviews, after fix batches, and before PRs.

`Read REFERENCE.md §1 "Why mechanical gates"` for rationale.

## Input

$ARGUMENTS = optional scope hint (`extension` for TS-only, `dotnet` for C#-only). Default: all applicable gates.

## Process

### Step 1: Detect What Changed

```bash
git diff --name-only main...HEAD
```

Categorize: `.cs` -> .NET gates; `.ts`/`.js` -> TypeScript gates; both -> all. If $ARGUMENTS specifies a scope, use that.

### Step 2: Run Gates

**Gate 1: .NET Build** (if C# changed)
```bash
dotnet build PPDS.sln -v q
```
Pass: 0 errors. On "used by another process", `Read REFERENCE.md §2 "File-locking recovery"`.

**Gate 2: .NET Tests** (if C# changed)
```bash
dotnet test PPDS.sln --filter "Category!=Integration" -v q --no-build
```
Pass: 0 failures. Fail: report failing test names and assertion messages.

**Gate 3: TypeScript Build** (if TS/JS changed)
```bash
npm run compile --prefix src/PPDS.Extension
```
Pass: 0 errors.

**Gate 3.5: TypeScript Type Check** (if TS/JS changed)
```bash
npm run typecheck:all --prefix src/PPDS.Extension
```
Pass: 0 errors across host and webview tsconfigs. `Read REFERENCE.md §3 "Compile vs typecheck"` for why this is separate.

**Gate 4: TypeScript Lint** (if TS/JS changed)
```bash
npm run lint --prefix src/PPDS.Extension
```
Pass: 0 errors.

**Gate 4.5: CSS Lint** (if CSS changed)
```bash
npm run lint:css --prefix src/PPDS.Extension
```
Pass: 0 errors.

**Gate 4.6: Dead Code Analysis** (if TS/JS changed)
```bash
npm run dead-code --prefix src/PPDS.Extension
```
Pass: 0 unused exports.

**Gate 5: TypeScript Tests** (if TS/JS changed)
```bash
npm test --prefix src/PPDS.Extension
```
Pass: 0 failures.

**Gate 5.5: TUI Snapshot Tests** (if `src/PPDS.Cli/Tui/` changed)
```bash
npm run tui:test
```
Pass: all snapshots match. `Read REFERENCE.md §4 "TUI snapshot baselines"` before regenerating.

**Gate 6: AC Verification** (if specs with ACs are relevant)

Grep `specs/*.md` for `**Code:**` lines. Match changed paths against code prefixes. For each relevant spec:
- Extract test method names from the AC table
- .NET: `dotnet test --filter "FullyQualifiedName~{method}" -v q --no-build`
- TS: `npx vitest run -t "{method}" --prefix src/PPDS.Extension`
- Report which ACs pass/fail.

**Gate 7: Enforcement Audit** (always)
```bash
python scripts/audit-enforcement.py --strict
```
Pass: every T1 marker references a hook that exists and is wired in `.claude/settings.json`.

### Step 3: Report

Markdown table, one row per gate (PASS/FAIL/SKIP). `### Failures` block listing exact errors for any FAIL. `### Verdict: PASS|FAIL`. Binary - never "PASS with warnings."

## Workflow State

After all gates pass:
```bash
python scripts/workflow-state.py set gates.passed now
python scripts/workflow-state.py set gates.commit_ref "$(git rev-parse HEAD)"
```

## Workflow Continuation - MANDATORY <!-- enforcement: T1 hook:session-stop-workflow -->

After gates pass, check whether `/implement` is driving:
```bash
python scripts/workflow-state.py get phase
```

- **If phase is `implementing`:** return results to `/implement`. It manages remaining steps.
- **Otherwise:** continue the chain yourself. Do NOT stop after reporting gate results.
  1. `/verify` for each affected surface
  2. `/pr` to create the pull request

`Read REFERENCE.md §5 "Post-gate reminder"` and `§6 "Workflow continuation rationale"` before stopping early.

Exception: if gates FAIL, fix first, re-run `/gates`, then resume the chain.

## Rules

1. **Mechanical only** - no subjective judgments
2. **All gates run** - don't skip "they probably pass"
3. **Exact output** - report exact errors
4. **Binary verdict** - PASS or FAIL
5. **No fixes** - report only

`Read REFERENCE.md §7 "Rules (rationale)"` for the why.
