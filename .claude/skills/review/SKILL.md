---
name: review
description: Bias-isolated code review — reviewer sees only the diff, constitution, and spec ACs, never the implementation plan or task context
---

# Review

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
# Ensure local main ref is current (worktrees can be stale)
git fetch origin main --quiet

git diff main...HEAD --stat
git diff main...HEAD
```

If $ARGUMENTS specifies a scope, filter the diff to those paths only.

### Step 2: Load Spec Context (NO implementation context)

- Read `specs/CONSTITUTION.md`
- Map changed files to specs by grepping all `specs/*.md` files for `**Code:**` frontmatter lines. Match changed file paths against code path prefixes to find governing specs.
- Read each relevant spec — extract ONLY the `## Acceptance Criteria` section
- Do NOT read any plan files, task descriptions, or implementation notes

### Step 2b: Load QA Findings for Dedup

Before dispatching reviewers, read existing QA findings from state:

```bash
python scripts/workflow-state.py get qa_findings
```

Parse the output as JSON. Pass these findings to each reviewer subagent as "already found by QA" context. Reviewers should NOT re-report QA findings that were fixed (`fixed: true`) unless the fix introduced a new problem.

### Step 3: Dispatch Impartial Reviewer
For large diffs (>10 files), use per-file chunking instead of a single subagent:

1. Group changed files by directory (max 5 files per chunk, or files ≤50 lines can be grouped together)
2. Dispatch up to 5 parallel subagents, one per chunk — each gets the same constitution + ACs but only their file subset
3. Stall timeout: if a subagent makes no progress for 3 minutes, skip that chunk with a "review incomplete" note (not a hard failure)
4. After all per-file reviews complete, run one cross-file consistency pass checking: type mismatches, missing imports, interface/caller drift
5. Merge findings from all subagents, deduplicate by file:line


Dispatch a subagent using the `Agent` tool. The subagent MUST NOT have implementation context — give it ONLY the diff, constitution, and ACs. This is the bias prevention mechanism: the reviewer sees code, not intent.

Subagent prompt:

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

## Workflow State

On review start, set the phase:

```bash
python scripts/workflow-state.py set phase reviewing
```

After review completes (all findings evaluated and verdict rendered), run:

```bash
python scripts/workflow-state.py set review.passed now
python scripts/workflow-state.py set review.commit_ref "$(git rev-parse HEAD)"
python scripts/workflow-state.py set review.findings {count}
```

Where `{count}` is the total findings (critical + important + suggestion).

## Rules

1. **No implementation context** — the reviewer never sees the plan, the task list, or what you were "trying to do." This is the entire point.
2. **Read full files** — diffs miss context. The reviewer reads the complete changed files.
3. **Spec-aware** — the reviewer checks against constitution and ACs, not against vibes.
4. **Honest zeros** — if there are no issues, say so. Don't pad findings.
5. **Structural isolation** — the reviewer subagent is dispatched fresh. It does not have access to the current conversation's implementation context.
