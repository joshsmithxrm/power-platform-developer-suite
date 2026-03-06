# Spec Rigor Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Wire specs into every step of the PPDS workflow so the lazy path is also the rigorous path.

**Architecture:** Standalone custom skills in `.claude/commands/` that read a constitution and relevant specs before doing anything. Skills are superpowers-aware but owned by the project. No application code — all deliverables are markdown files.

**Tech Stack:** Claude Code skills (markdown), git

**Design doc:** `docs/plans/2026-03-03-spec-rigor-design.md`

---

## Phase 1: Foundation (Sequential — must complete before other phases)

### Task 1: Create branch and foundation files

**Files:**
- Create: `specs/CONSTITUTION.md`
- Modify: `specs/SPEC-TEMPLATE.md`
- Modify: `specs/README.md`

**Step 1: Create feature branch**

```bash
git checkout main
git checkout -b feature/spec-rigor
```

**Step 2: Create `specs/CONSTITUTION.md`**

Write exactly this content:

```markdown
# PPDS Constitution

Non-negotiable principles. Every spec, plan, implementation, and review MUST comply. Violations are defects — not style issues, not suggestions, not "nice to haves."

## How to Use This Document

- **Skills:** Read this before any implementation, review, or spec work
- **Subagents:** This is injected into your prompt — comply fully
- **Reviews:** Check every item — violation = finding

---

## Architecture Laws

**A1.** All business logic lives in Application Services (`src/PPDS.Cli/Services/`) — never in UI code. CLI commands, TUI screens, VS Code webviews, and MCP handlers are thin wrappers that call services.

**A2.** Application Services are the single code path — CLI, TUI, RPC, and MCP all call the same service methods. No duplicating logic across interfaces.

**A3.** Accept `IProgressReporter` for any operation expected to take >1 second. All UIs (CLI stderr, TUI status bar, VS Code progress, MCP notifications) need feedback.

## Dataverse Laws

**D1.** Use `IDataverseConnectionPool` for all Dataverse operations — never create `ServiceClient` directly. Creating a client per request is 42,000x slower than pooling.

**D2.** Never hold a pooled client across multiple operations. Pattern: get → use → dispose within a single method scope. Holding defeats pool parallelism.

**D3.** Use bulk APIs (`CreateMultiple`, `UpdateMultiple`) over `ExecuteMultiple`. Bulk APIs are 5x faster.

**D4.** Wrap all exceptions from Application Services in `PpdsException` with an `ErrorCode`. Raw exceptions prevent programmatic error handling by callers.

## Interface Laws

**I1.** CLI stdout is for data only — status messages, progress, and diagnostics go to `Console.Error.WriteLine` (stderr). Stdout must be pipeable.

**I2.** Generated entities in `src/PPDS.Dataverse/Generated/` are never hand-edited. They are regenerated from metadata.

**I3.** Every spec must have numbered acceptance criteria (AC-01, AC-02, ...) before implementation begins. No ACs = no implementation.

## Security Laws

**S1.** Never render untrusted data via `innerHTML` — use `textContent` or a proper escaping pipeline. Mixed escaped/unescaped data in the same structure is an architectural flaw, not a minor issue.

**S2.** Never use `shell: true` in process spawn without explicit justification documented in the spec. Default is `shell: false`.

**S3.** Never log secrets (`clientSecret`, `password`, `certificatePassword`, auth tokens). If RPC params contain secrets, they must be redacted before any tracing or logging.

## Resource Laws

**R1.** Every `IDisposable` gets disposed. No fire-and-forget subscriptions, no leaked event handlers, no orphaned timers. If a class holds disposable resources, it must implement `IDisposable` itself.

**R2.** `CancellationToken` must be threaded through the entire async call chain — never accepted as a parameter and then ignored. If a method accepts a token, it must pass it to every async call it makes.

**R3.** Event handlers and subscriptions must be cleaned up in `Dispose`. Every `+=` needs a corresponding `-=`. Every `.subscribe()` needs an `.unsubscribe()` or disposal mechanism.
```

**Step 3: Update `specs/SPEC-TEMPLATE.md`**

Read the current file first. Then make these changes:

1. Move the `## Testing` section (currently near the end) to right after `## Specification` (after the Constraints/Validation Rules subsections).
2. Rename `## Testing` to `## Acceptance Criteria`.
3. Replace the checkbox-style acceptance criteria with the table format.
4. Keep the Edge Cases and Test Examples subsections as-is.

The new `## Acceptance Criteria` section should look like:

```markdown
---

## Acceptance Criteria

{Required. Every spec must have numbered, testable acceptance criteria before implementation begins. See CONSTITUTION.md I3.}

| ID | Criterion | Test | Status |
|----|-----------|------|--------|
| AC-01 | {Specific, testable behavior} | `{TestClass.TestMethod}` | {status} |
| AC-02 | {Specific, testable behavior} | `{TestClass.TestMethod}` | {status} |

Status key: ✅ covered by passing test · ⚠️ test exists but failing · ❌ no test yet · 🔲 not yet implemented

{Write criteria that are:}
{- **Specific**: "Returns within 50ms" not "Performs well"}
{- **Testable**: Can be proven true or false by a single test}
{- **Independent**: Each criterion stands alone}
{- **Traceable**: Test column links to exact test method}

### Edge Cases

| Scenario | Input | Expected Output |
|----------|-------|-----------------|
| {case} | {input} | {output} |

### Test Examples

{language}
// Example test showing expected behavior
[Fact]
public void Should_DoSomething_When_Condition()
{
    // Arrange, Act, Assert
}
```

**Step 4: Add constitution reference to `specs/README.md`**

Add a line at the top of `specs/README.md`, before the existing content:

```markdown
> **Constitution:** All specs must comply with [CONSTITUTION.md](./CONSTITUTION.md). All specs must have numbered acceptance criteria (AC-01, AC-02, ...) before implementation.

```

**Step 5: Commit**

```bash
git add specs/CONSTITUTION.md specs/SPEC-TEMPLATE.md specs/README.md
git commit -m "docs(specs): add constitution and sharpen spec template

- Add CONSTITUTION.md with 16 non-negotiable principles covering
  architecture, Dataverse, interfaces, security, and resources
- Update SPEC-TEMPLATE.md: promote acceptance criteria section,
  add numbered AC-ID format with test traceability table
- Update README.md to reference constitution

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

## Phase 2-4: Skills (PARALLEL — all depend on Phase 1 only)

### Task 2: Create `/spec` skill

**Files:**
- Create: `.claude/commands/spec.md`

**Step 1: Write `.claude/commands/spec.md`**

```markdown
# Spec

Create or update a specification following PPDS conventions. Ensures consistency, cross-references related specs, and enforces numbered acceptance criteria.

## Input

$ARGUMENTS = spec name (e.g., `connection-pooling` for existing, `new-feature` for new)

## Process

### Step 1: Load Foundation

Read these files before doing anything else:
- `specs/CONSTITUTION.md` — non-negotiable principles
- `specs/SPEC-TEMPLATE.md` — structural template
- `specs/README.md` — index of all specs and their code mappings

### Step 2: Determine Mode

**If spec exists** (`specs/$ARGUMENTS.md` found):
- Read the existing spec
- Read the code files referenced in the spec's `Code:` header line
- Identify drift: does the code match what the spec describes?
- Identify missing ACs: does the spec have numbered acceptance criteria?
- Present findings to user before making changes

**If spec is new** (`specs/$ARGUMENTS.md` not found):
- Search existing specs for overlapping scope — check that this isn't already covered
- If overlap found, ask user: update existing spec or create new one?
- Proceed to authoring

### Step 3: Cross-Reference Related Specs

Based on the spec's domain, read related specs:
- Touches `src/PPDS.Dataverse/` → read `specs/connection-pooling.md`, `specs/architecture.md`
- Touches `src/PPDS.Cli/Tui/` → read `specs/tui.md`, `specs/tui-foundation.md`
- Touches `src/PPDS.Cli/Commands/` → read `specs/cli.md`
- Touches `src/PPDS.Mcp/` → read `specs/mcp.md`
- Touches `src/PPDS.Migration/` → read `specs/migration.md`
- Touches `src/PPDS.Auth/` → read `specs/authentication.md`
- Always read `specs/architecture.md` for cross-cutting patterns

### Step 4: Author/Update Spec

**For new specs:** Walk through the template section by section with the user. One section at a time, ask if it looks right before moving to the next. Follow the brainstorming pattern — multiple choice questions when possible, open-ended when needed.

**For existing specs:** Present the drift analysis and proposed changes. Get user approval before modifying.

**For both:**
- Ensure every section from the template is addressed (even if just "N/A" for optional sections)
- Cross-check against constitution — hard stop on violations
- Cross-check against related specs — flag contradictions

### Step 5: Enforce Acceptance Criteria

This is a HARD GATE. The spec is not complete without:
- Numbered AC IDs (AC-01, AC-02, ...)
- Each criterion is specific and testable (not vague prose)
- Test column populated where tests exist (can be "TBD" for new specs)
- Status column accurate

If the user tries to skip ACs, remind them: Constitution I3 requires numbered acceptance criteria before implementation begins.

### Step 6: Finalize

1. Write/update the spec file at `specs/$ARGUMENTS.md`
2. Update `specs/README.md` if this is a new spec (add to appropriate table)
3. Commit:
   ```
   docs(specs): add/update {spec-name} specification

   Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
   ```

## Rules

1. **Always read foundation first** — constitution, template, README. No exceptions.
2. **Always cross-reference** — no spec exists in isolation.
3. **ACs are mandatory** — the spec is incomplete without them.
4. **One section at a time** — don't dump the entire spec at once.
5. **Constitution violations are hard stops** — if a spec proposes something that violates the constitution, flag it immediately.
6. **Don't invent requirements** — ask the user. The spec captures their intent, not your assumptions.
```

**Step 2: Commit**

```bash
git add .claude/commands/spec.md
git commit -m "feat(skills): add /spec skill for spec creation and updates

Standardizes how specs are created and updated. Reads constitution
and cross-references related specs before authoring. Enforces
numbered acceptance criteria as a hard gate.

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

### Task 3: Create `/spec-audit` skill

**Files:**
- Create: `.claude/commands/spec-audit.md`

**Step 1: Write `.claude/commands/spec-audit.md`**

```markdown
# Spec Audit

Compare specifications against reality. Find drift, gaps, and undocumented behavior. Produces actionable findings — does not auto-fix.

## Input

$ARGUMENTS = spec name for single audit (e.g., `connection-pooling`), or empty for full audit of all specs

## Process

### Step 1: Load Foundation

Read these files:
- `specs/CONSTITUTION.md` — principles to check against
- `specs/README.md` — index of all specs and their code mappings

### Step 2: Determine Scope

**Single spec** (`$ARGUMENTS` provided):
- Read `specs/$ARGUMENTS.md`
- Proceed to Step 3 with this one spec

**Full audit** (`$ARGUMENTS` empty):
- Read `specs/README.md` to get list of all specs
- For each spec, dispatch a parallel subagent (use `Agent` tool with `subagent_type: "general-purpose"`) with the audit prompt below
- Collect all results and produce a summary

### Step 3: Audit a Single Spec

For each spec, check these categories:

**A. Acceptance Criteria Coverage**
- Does the spec have an `## Acceptance Criteria` section with numbered IDs?
- For each AC that references a test method: does that test file/method exist? (use Grep to search for the method name)
- For each AC with status ✅: is the status accurate? (run the specific test if possible: `dotnet test --filter "FullyQualifiedName~{TestMethod}" -v q`)
- Report: ✅ verified · ⚠️ test exists but status wrong · ❌ test not found · 🔲 no AC section

**B. Code-to-Spec Alignment**
- Read the code files listed in the spec's `Code:` header
- Check Core Requirements: does each requirement have corresponding code?
- Check Architecture diagram: does the actual code structure match?
- Check Core Types: do the interfaces/classes described still exist with the same signatures?
- Report: matches · drifted (describe how) · missing (spec claims, code doesn't have) · extra (code has, spec doesn't describe)

**C. Constitution Compliance**
- Check the spec's design against each relevant constitution principle
- Focus on: architecture laws (A1-A3) for any spec, Dataverse laws (D1-D4) for Dataverse specs, security laws (S1-S3) for UI specs
- Report: compliant · violation (cite principle and issue)

**D. Cross-Spec Consistency**
- Read related specs (from the `Related Specs` section)
- Check for contradictions between this spec and related ones
- Report: consistent · contradiction (describe)

### Step 4: Produce Report

**Single spec report format:**

```
## {spec-name}.md — Audit Report

**Last Updated:** {date from spec header}
**Code:** {code path from spec header}

### Acceptance Criteria
| ID | Criterion | Finding |
|----|-----------|---------|
| AC-01 | {criterion text} | ✅ test exists and passes |
| AC-02 | {criterion text} | ❌ test method not found |
| — | No AC section | 🔲 MISSING — needs AC table |

### Code Alignment
- {finding 1}
- {finding 2}

### Undocumented Behavior
- {code behavior not covered by any spec section}

### Constitution
- {compliant or violation with citation}

### Remediation Priority
1. {highest priority fix}
2. {next priority}
```

**Full audit summary format:**

```
## PPDS Spec Audit Summary — {date}

### Overview
| Spec | ACs | Alignment | Constitution | Priority |
|------|-----|-----------|--------------|----------|
| connection-pooling.md | 5/5 ✅ | 2 drifted | compliant | LOW |
| tui-foundation.md | 🔲 no ACs | 3 missing | A1 violation | HIGH |
| ... | ... | ... | ... | ... |

### High Priority Remediation
1. {spec}: {issue}
2. {spec}: {issue}

### Stats
- Specs with ACs: N/21
- Specs fully aligned: N/21
- Constitution violations: N
```

### Subagent Prompt (for parallel full audit)

When dispatching a subagent for a single spec during full audit, use this prompt:

```
You are auditing the PPDS specification at specs/{name}.md against the actual codebase.

## Constitution (check compliance)
{paste full CONSTITUTION.md content}

## Your Job
1. Read specs/{name}.md
2. Read the code files referenced in the spec's Code: header
3. Check each acceptance criterion — does the referenced test exist? Search with Grep.
4. Check core requirements — does the code match what the spec describes?
5. Check for undocumented behavior — code that does things the spec doesn't mention
6. Check constitution compliance

Report your findings in this format:
[paste single spec report format from above]

Do NOT fix anything. Just report findings.
```

## Rules

1. **Read-only** — this skill produces findings, never modifies code or specs
2. **Parallel for full audit** — dispatch one subagent per spec for throughput
3. **Evidence-based** — every finding must cite specific code or test references
4. **Actionable** — every finding includes what needs to change
5. **Prioritized** — HIGH = constitution violation or missing ACs, MEDIUM = drift, LOW = minor gaps
```

**Step 2: Commit**

```bash
git add .claude/commands/spec-audit.md
git commit -m "feat(skills): add /spec-audit skill for spec-vs-reality comparison

Repeatable audit that compares specs against code, checks AC coverage,
identifies drift and undocumented behavior, and verifies constitution
compliance. Supports single-spec and full-audit modes with parallel
subagent dispatch.

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

### Task 4: Update `/implement` to be spec-aware

**Files:**
- Modify: `.claude/commands/implement.md`

**Step 1: Read current implement.md**

Read `.claude/commands/implement.md` to see current content.

**Step 2: Add spec context injection**

Insert a new step between the current "Step 1: Read & Analyze the Plan" and "Step 2: Assess Current State." This becomes the new Step 2, and all subsequent steps renumber.

Add this section after the current Step 1:

```markdown
### Step 2: Load Spec Context

Before dispatching any agents, load the specification context that will be injected into every subagent prompt.

**A. Read Foundation**
- Read `specs/CONSTITUTION.md` — full content will be injected into every subagent prompt
- Read `specs/README.md` — maps code paths to specs

**B. Identify Relevant Specs**
- From the plan, identify which source directories/files will be touched
- Map each to its spec using the README.md code column:
  - `src/PPDS.Dataverse/Pooling/` → `specs/connection-pooling.md`
  - `src/PPDS.Dataverse/Query/` → `specs/query.md`
  - `src/PPDS.Cli/Tui/` → `specs/tui.md` + `specs/tui-foundation.md`
  - `src/PPDS.Cli/Commands/` → `specs/cli.md`
  - `src/PPDS.Mcp/` → `specs/mcp.md`
  - `src/PPDS.Migration/` → `specs/migration.md`
  - `src/PPDS.Auth/` → `specs/authentication.md`
- Always include `specs/architecture.md`
- Read each relevant spec and extract the `## Acceptance Criteria` section

**C. Build Spec Context Block**
Construct a text block that will be prepended to every subagent prompt:

```
## Constitution (MUST comply — violations are defects)

{full CONSTITUTION.md content}

## Relevant Specifications — Acceptance Criteria

### {spec-name}.md
| ID | Criterion | Test | Status |
|----|-----------|------|--------|
{AC table rows}

### {spec-name-2}.md
{AC table rows}

Your implementation MUST satisfy these criteria. If your task conflicts
with a spec or constitution principle, STOP and report the conflict
to the orchestrator — do not silently deviate.
```
```

Then modify the existing "Step 4: Execute Each Phase" section's "A. Dispatch Agents" bullet list. Add this bullet to the list of things each agent prompt MUST include:

```markdown
  - The spec context block from Step 2 (constitution + relevant AC tables)
```

And add to section "D. Review":

```markdown
- The code-reviewer agent MUST also receive the spec context block (constitution + ACs) but NO implementation context (no plan, no task descriptions) — it reviews code against specs, not against the plan
```

And add to section "C. Verify Phase Gate" after the test commands:

```markdown
- If specs with ACs are relevant to this phase, check: do the AC test methods pass?
  Run: `dotnet test --filter "FullyQualifiedName~{TestMethodFromAC}" -v q --no-build`
  for each AC referenced by this phase's tasks
```

**Step 3: Commit**

```bash
git add .claude/commands/implement.md
git commit -m "feat(skills): make /implement spec-aware

Add spec context injection step that loads constitution and relevant
spec acceptance criteria before dispatching subagents. Constitution
and AC tables are included in every subagent prompt. Phase gates
now verify AC test methods pass.

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

### Task 5: Create `automated-quality-gates` skill

**Files:**
- Create: `.claude/commands/automated-quality-gates.md`

**Step 1: Write `.claude/commands/automated-quality-gates.md`**

```markdown
# Automated Quality Gates

Mechanical pass/fail checks. No judgment, no opinions — compiler, linter, and tests either pass or they don't. Run this before code reviews, after fix batches, and before PRs.

## Input

$ARGUMENTS = optional scope hint (e.g., `extension` for TypeScript-only, `dotnet` for C#-only). Default: run all applicable gates.

## Process

### Step 1: Detect What Changed

```bash
git diff --name-only HEAD~1
```

Categorize changed files:
- `.cs` files → run .NET gates
- `.ts`/`.js` files → run TypeScript gates
- Both → run all gates

If $ARGUMENTS specifies a scope, use that instead of auto-detection.

### Step 2: Run Gates

**Gate 1: .NET Build** (if C# files changed)

```bash
dotnet build PPDS.sln -v q
```

Pass: 0 errors, 0 warnings treated as errors
Fail: any error → report exact error messages with file:line

**Gate 2: .NET Tests** (if C# files changed)

```bash
dotnet test PPDS.sln --filter "Category!=Integration" -v q --no-build
```

Pass: 0 failures
Fail: report failing test names and assertion messages

**Gate 3: TypeScript Build** (if TS/JS files changed)

```bash
npm run compile --prefix extension
```

Pass: 0 errors
Fail: report exact error messages with file:line

**Gate 4: TypeScript Lint** (if TS/JS files changed)

```bash
npm run lint --prefix extension
```

Pass: 0 errors
Fail: report lint violations

**Gate 5: TypeScript Tests** (if TS/JS files changed)

```bash
npm test --prefix extension
```

Pass: 0 failures
Fail: report failing test names and messages

**Gate 6: AC Verification** (if specs with ACs are relevant)

Read `specs/README.md` to map changed files to specs. For each relevant spec with ACs:
- Extract test method names from the AC table
- Run each: `dotnet test --filter "FullyQualifiedName~{method}" -v q --no-build`
- Report which ACs pass and which fail

### Step 3: Report

```
## Quality Gate Results

| Gate | Status | Details |
|------|--------|---------|
| .NET Build | ✅ PASS | 0 errors |
| .NET Tests | ❌ FAIL | 2 failures (see below) |
| TS Build | ✅ PASS | 0 errors |
| TS Lint | ⏭️ SKIP | No TS files changed |
| TS Tests | ⏭️ SKIP | No TS files changed |
| AC Verify | ⚠️ PARTIAL | 3/5 ACs pass |

### Failures
- `ConnectionPoolTests.GetClient_Throttled_SkipsSource`: Expected source-b, got source-a
- AC-03 (connection-pooling): Test method not found

### Verdict: ❌ FAIL — 2 issues must be resolved before proceeding
```

## Rules

1. **Mechanical only** — no subjective judgments, no code review, no style opinions
2. **All gates run** — don't skip gates because "they probably pass"
3. **Exact output** — report exact error messages, not summaries
4. **Binary verdict** — PASS (all green) or FAIL (any red). No "PASS with warnings."
5. **No fixes** — report problems, don't fix them. That's the implementer's job.
```

**Step 2: Commit**

```bash
git add .claude/commands/automated-quality-gates.md
git commit -m "feat(skills): add /automated-quality-gates for mechanical pass/fail checks

Runs compiler, linter, tests, and AC verification as binary gates.
Auto-detects scope from changed files. Reports exact errors. No
judgment, no fixes — just pass or fail.

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

### Task 6: Create `impartial-code-review` skill

**Files:**
- Create: `.claude/commands/impartial-code-review.md`

**Step 1: Write `.claude/commands/impartial-code-review.md`**

```markdown
# Impartial Code Review

Code review with structural bias prevention. The reviewer gets NO implementation context — no plan, no task descriptions, no "what we were trying to do." They get ONLY: the diff, the file contents, the constitution, and relevant spec ACs. They read code and find bugs.

## Why This Exists

Evidence from vscode-extension-mvp worktree:
- Biased review (reviewer had implementation context): found **1 issue**
- Independent review (fresh session, no context): found **30 issues**

Same code. 30x difference. Reviewer bias is measurable and severe.

## Input

$ARGUMENTS = optional scope (e.g., `src/PPDS.Cli/Tui/` to limit review). Default: review all staged/recent changes.

## Process

### Step 1: Gather Review Material

```bash
# Get the diff to review
git diff HEAD~1 --stat
git diff HEAD~1
```

If $ARGUMENTS specifies a scope, filter the diff to those paths only.

### Step 2: Load Spec Context (NO implementation context)

- Read `specs/CONSTITUTION.md`
- Map changed files to specs via `specs/README.md`
- Read each relevant spec — extract ONLY the `## Acceptance Criteria` section
- Do NOT read any plan files, task descriptions, or implementation notes

### Step 3: Dispatch Impartial Reviewer

Dispatch a subagent (use `Agent` tool with `subagent_type: "superpowers:code-reviewer"`) with this prompt:

```
You are reviewing code changes for defects. You have NO context about what
the developer was trying to build. You don't know the plan. You don't know
the task. You only know what good code looks like.

## Constitution (violations are defects)

{full CONSTITUTION.md content}

## Relevant Spec Acceptance Criteria

{AC tables from relevant specs}

## The Diff

{git diff output}

## Your Job

Read every changed file in full (not just the diff — read the whole file for context).
Look for:

**Critical (must fix before merge):**
- Logic errors, wrong behavior, broken functionality
- Security issues (XSS, injection, secret exposure)
- Resource leaks (undisposed IDisposable, unremoved handlers, leaked timers)
- Race conditions (concurrent access without sync, CancellationToken ignored)
- Constitution violations (cite the specific principle)
- AC failures (cite the specific AC-ID)

**Important (should fix before merge):**
- Error handling gaps (bare catches, swallowed exceptions)
- Missing null/edge-case handling on public API boundaries
- Type mismatches between contracts (DTO shapes, RPC params)

**Suggestion (consider for follow-up):**
- Naming improvements
- Minor simplifications
- Documentation gaps

## Report Format

For each finding:
- **Severity:** CRITICAL / IMPORTANT / SUGGESTION
- **File:Line:** exact location
- **Issue:** what's wrong (be specific, not vague)
- **Evidence:** the actual code that demonstrates the problem
- **Fix:** what should change (brief, actionable)

If you find ZERO issues, say so explicitly — "No issues found" is a valid
and welcome outcome. Do not invent problems to justify your existence.

CRITICAL: Do NOT trust that code "probably works." Read it. Trace the logic.
Check the types. Verify disposal. The previous reviewer found 1 issue.
The independent reviewer found 30. Be the independent reviewer.
```

### Step 4: Present Findings

Present the reviewer's findings grouped by severity:
1. CRITICAL findings (must fix)
2. IMPORTANT findings (should fix)
3. SUGGESTION findings (optional)

Include total counts and a clear verdict:
- **PASS**: 0 critical, 0 important
- **PASS WITH FINDINGS**: 0 critical, N important (reviewer judgment)
- **FAIL**: any critical findings

## Rules

1. **No implementation context** — the reviewer never sees the plan, the task list, or what you were "trying to do." This is the entire point.
2. **Read full files** — diffs miss context. The reviewer reads the complete changed files.
3. **Spec-aware** — the reviewer checks against constitution and ACs, not against vibes.
4. **Honest zeros** — if there are no issues, say so. Don't pad findings.
5. **Structural isolation** — the reviewer subagent is dispatched fresh. It does not have access to the current conversation's implementation context.
```

**Step 2: Commit**

```bash
git add .claude/commands/impartial-code-review.md
git commit -m "feat(skills): add /impartial-code-review with structural bias prevention

Dispatches reviewer subagent with NO implementation context — only
diff, file contents, constitution, and spec ACs. Addresses the
measured 30x issue-detection gap between biased and independent reviews.

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

### Task 7: Create `review-fix-converge` skill

**Files:**
- Create: `.claude/commands/review-fix-converge.md`

**Step 1: Write `.claude/commands/review-fix-converge.md`**

```markdown
# Review Fix Converge

Orchestration loop that converges to PR-ready code. Runs gates → impartial review → fix → repeat until clean. Tracks issue counts per cycle to prove convergence — if counts aren't decreasing, something is wrong.

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

**A. Quality Gates**
Invoke `/automated-quality-gates` (use the skill).
- If gates FAIL: fix the failures first (dispatch fix agent), re-run gates
- Gates must PASS before proceeding to review
- Record: did fixes introduce any new gate failures? (= regression)

**B. Impartial Code Review**
Invoke `/impartial-code-review` (use the skill).
- Record: count of CRITICAL and IMPORTANT findings

**C. Evaluate Convergence**
Update the tracking table. Check:
- Are critical findings decreasing or zero?
- Are important findings decreasing?
- Were regressions introduced by the previous fix cycle?

**D. Fix or Finish**

**If 0 CRITICAL and 0 IMPORTANT:**
→ **CONVERGED.** Report final tracking table. Ready for PR.

**If findings exist but counts are decreasing:**
→ Dispatch fix agents for CRITICAL findings first, then IMPORTANT.
→ Each fix agent receives: the finding, the affected file, the constitution, and relevant spec ACs.
→ After fixes, return to Step 2A (next cycle).

**If counts are NOT decreasing (stalled or diverging):**
→ **STOP.** Report to user:
```
⚠️ Convergence stalled at cycle N.
Cycle N-1: X critical, Y important
Cycle N:   X critical, Y important (no improvement)

This usually means:
1. Fixes are introducing new issues at the same rate they solve old ones
2. The review is finding different issues each time (scope creep)
3. There's an architectural problem that point fixes can't address

Recommend: Review the findings for patterns. If the same category
of issue keeps appearing, the spec or design may need updating first.
```
→ Ask user how to proceed.

**If max cycles reached:**
→ **STOP.** Report tracking table and remaining findings. Ask user whether to continue or merge as-is with known issues documented.

### Step 3: Final Report

```
## Convergence Report

### Tracking
| Cycle | Gate | Critical | Important | Regressions | Verdict |
|-------|------|----------|-----------|-------------|---------|
| 1 | ❌ 2 errors | 5 | 8 | — | FIX |
| 2 | ✅ | 2 | 3 | 0 | FIX |
| 3 | ✅ | 0 | 1 | 0 | FIX |
| 4 | ✅ | 0 | 0 | 0 | ✅ CONVERGED |

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
```

**Step 2: Commit**

```bash
git add .claude/commands/review-fix-converge.md
git commit -m "feat(skills): add /review-fix-converge convergence loop

Orchestrates gates → impartial review → fix cycles with convergence
tracking. Detects stalled/diverging review loops and escalates.
Addresses the measured review divergence problem from the VS Code
extension worktree (5 cycles, increasing issue counts).

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

---

## Phase 5: Integration (Sequential — depends on all above)

### Task 8: Update CLAUDE.md and finalize

**Files:**
- Modify: `CLAUDE.md` (repo root)

**Step 1: Read current CLAUDE.md**

Read `CLAUDE.md` to see the current Commands table.

**Step 2: Update Commands table**

Add the new skills to the Commands table. The current table has:

```markdown
| Command | Purpose |
|---------|---------|
| `ppds --help` | Full CLI reference |
| `ppds serve` | RPC server for IDE integration |
| `/ship` | Validate, commit, PR, handle CI |
| `/debug` | Interactive feedback loop for CLI/TUI/MCP |
```

Update it to:

```markdown
| Command | Purpose |
|---------|---------|
| `ppds --help` | Full CLI reference |
| `ppds serve` | RPC server for IDE integration |
| `/implement` | Execute implementation plan with spec-aware subagents |
| `/spec` | Create or update a specification |
| `/spec-audit` | Audit specs against code reality |
| `/debug` | Interactive feedback loop for CLI/TUI/MCP |
| `/automated-quality-gates` | Mechanical pass/fail build/test/lint checks |
| `/impartial-code-review` | Bias-free code review against specs |
| `/review-fix-converge` | Gates → review → fix loop with convergence tracking |
```

**Step 3: Add spec workflow reference**

After the Testing section, add:

```markdown
## Spec Workflow

- Constitution: `specs/CONSTITUTION.md` — non-negotiable principles
- Template: `specs/SPEC-TEMPLATE.md` — required structure for all specs
- New spec: `/spec {name}` — guided creation with cross-referencing
- Audit: `/spec-audit` — compare all specs against code
- Implement: `/implement` — loads constitution + relevant specs into subagent context
```

**Step 4: Commit**

```bash
git add CLAUDE.md
git commit -m "docs: update CLAUDE.md with spec workflow and new skills

Add /spec, /spec-audit, /automated-quality-gates, /impartial-code-review,
and /review-fix-converge to commands table. Add spec workflow section
documenting the constitution-driven development process.

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
```

**Step 5: Verify**

```bash
# Check all new files exist
ls -la specs/CONSTITUTION.md
ls -la .claude/commands/spec.md
ls -la .claude/commands/spec-audit.md
ls -la .claude/commands/automated-quality-gates.md
ls -la .claude/commands/impartial-code-review.md
ls -la .claude/commands/review-fix-converge.md

# Check commit history
git log --oneline -10
```

---

## Parallelization Guide

```
Phase 1 (Task 1): Branch + Constitution + Template     ──── SEQUENTIAL FIRST
                          │
         ┌────────────────┼────────────────┐
         │                │                │
Phase 2-4 (PARALLEL):    │                │
  Task 2: /spec          Task 3: /spec-audit    Task 4: /implement update
  Task 5: quality-gates  Task 6: impartial-review
                          │                │
                          └────────┬───────┘
                                   │
Phase 5:                  Task 7: review-fix-converge
                                   │
                          Task 8: CLAUDE.md + finalize     ──── SEQUENTIAL LAST
```

Tasks 2, 3, 4, 5, 6 are fully independent and can run as parallel subagents.
Task 7 should run after 5 and 6 (it references both skills).
Task 8 must run last (it references all skills).
