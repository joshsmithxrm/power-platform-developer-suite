# Workflow Enforcement

**Status:** Draft
**Version:** 1.0
**Last Updated:** 2026-03-16
**Code:** [.claude/](.claude/) | [scripts/hooks/](../scripts/hooks/)

---

## Overview

A mechanical enforcement system that ensures AI agents follow the PPDS development workflow — from design through PR creation — without human micromanagement. Skills define the required steps. Hooks enforce that steps actually happened before allowing commits and PRs. A workflow state file tracks progress.

### Goals

- **Process compliance without babysitting**: Gates, QA, verification, and code review happen automatically as part of the workflow. The user is involved at design and final review only.
- **Mechanical enforcement**: Critical checkpoints (commit, PR creation) are blocked if required steps were skipped. Prose instructions are backed by hooks that make non-compliance impossible at exit points.
- **Visibility without interruption**: The user can see workflow progress at any time but is not prompted until the work is ready for review.

### Non-Goals

- **Full state machine orchestration**: We enforce outcomes at exit points, not step-by-step sequencing. The AI can work in any order as long as all required steps complete before committing or creating a PR.
- **CI/CD pipeline changes**: GitHub Actions and external CI are out of scope. This spec covers the local development workflow only.
- **Headless/unattended execution**: Future goal (triggering implementation from GitHub issues, webhooks). This spec covers interactive Claude Code sessions.
- **Superpowers plugin replacement**: We disable superpowers for this repo and build our own skills, but we do not modify or fork the superpowers plugin itself.

---

## Architecture

```
┌──────────────────────────────────────────────────────┐
│                   Session Start                       │
│  SessionStart hook injects workflow state + sequence  │
└──────────────────┬───────────────────────────────────┘
                   │
                   ▼
┌──────────────────────────────────────────────────────┐
│              Skills (define the workflow)              │
│                                                       │
│  /design → /implement → /gates → /verify → /qa       │
│                          → /review → /converge → /pr  │
│                                                       │
│  Each skill writes timestamps to workflow-state.json  │
└──────────────────┬───────────────────────────────────┘
                   │
                   ▼
┌──────────────────────────────────────────────────────┐
│          workflow-state.json (gitignored)             │
│                                                       │
│  Tracks: gates, verify, qa, review, pr timestamps    │
│  Invalidates on new commits (gates must re-run)      │
└──────────────────┬───────────────────────────────────┘
                   │
                   ▼
┌──────────────────────────────────────────────────────┐
│              Hooks (enforce the workflow)              │
│                                                       │
│  git commit  → warn if gates stale (soft)            │
│  gh pr create → block if steps missing (hard)        │
│  session stop → emit completion summary (visible)    │
└──────────────────────────────────────────────────────┘
```

### Components

| Component | Responsibility |
|-----------|----------------|
| Workflow State File | Tracks which workflow steps have been completed and when. Invalidates stale entries on new commits. |
| SessionStart Hook | Injects current workflow state and required sequence into AI context at session start. |
| Pre-Commit Hook (enhanced) | Warns if `/gates` hasn't been run since last code changes. Soft gate — does not block. |
| PR Gate Hook | Blocks `gh pr create` unless gates, verify, QA, and review are all current. Hard gate. |
| Stop Hook | Emits workflow completion summary when session ends. Cannot block, but makes non-compliance visible. |
| Skills | Define each workflow step. Write their completion status to the workflow state file. |

### Dependencies

- Depends on: [CONSTITUTION.md](./CONSTITUTION.md)
- Uses patterns from: [architecture.md](./architecture.md)

---

## Specification

### Workflow State File

**Location:** `.claude/workflow-state.json` (added to `.gitignore`)

**Schema:**

```json
{
  "branch": "feature/import-jobs",
  "spec": "specs/import-jobs.md",
  "plan": "docs/plans/2026-03-16-import-jobs.md",
  "started": "2026-03-16T15:00:00Z",
  "last_commit": "abc1234",
  "gates": {
    "passed": "2026-03-16T16:00:00Z",
    "commit_ref": "abc1234"
  },
  "verify": {
    "ext": "2026-03-16T16:10:00Z",
    "tui": "2026-03-16T16:15:00Z"
  },
  "qa": {
    "ext": "2026-03-16T16:30:00Z"
  },
  "review": {
    "passed": "2026-03-16T16:45:00Z",
    "findings": 0
  },
  "pr": {
    "url": "https://github.com/joshsmithxrm/ppds/pull/123",
    "created": "2026-03-16T17:00:00Z"
  }
}
```

**Core Requirements:**

1. Created when any skill first writes to it (e.g., `/implement` sets `branch`, `spec`, `plan`).
2. `gates.commit_ref` stores the commit SHA that gates were verified against. Hooks compare this to HEAD.
3. When a new commit is made, `gates.passed` is cleared (set to `null`) because the codebase has changed since gates last ran. `verify`, `qa`, and `review` timestamps are NOT automatically cleared on commit — they track cumulative coverage across the session.
4. Skills are responsible for writing their own entries. No central coordinator.
5. File is gitignored — it is per-session state, not committed.

### Hook Specifications

#### SessionStart Hook

**Trigger:** Session start on any branch.

**Behavior:**
1. Read `.claude/workflow-state.json` if it exists.
2. Read current branch name.
3. Determine workflow state: which steps have been completed, which are stale, which are pending.
4. Inject into AI context:
   - Current branch and workflow state summary.
   - Required workflow sequence (the decision tree from CLAUDE.md).
   - Any stale entries (e.g., "gates passed but new commits since — gates must re-run").
5. If no workflow state file exists, inject the full required workflow sequence as a reminder.

**Output format:**
```
WORKFLOW STATE for branch feature/import-jobs:
  ✓ Gates passed (commit abc1234, current)
  ✓ Extension verified
  ✗ TUI not verified
  ✗ QA not completed
  ✗ Review not completed
  Required before PR: /verify tui, /qa, /review
```

#### Pre-Commit Hook (Enhanced)

**Trigger:** PreToolUse on `Bash(git commit:*)`.

**Behavior:**
1. Check if files under `src/` are staged.
2. If yes, read `.claude/workflow-state.json`.
3. If `gates.commit_ref` does not match the current staging state (i.e., code has changed since gates last ran), emit a warning.
4. **Does not block the commit.** This is a soft gate — WIP commits during implementation are expected.

**Output on warn:**
```
⚠ Warning: /gates has not been run since your last changes. Run /gates before creating a PR.
```

#### PR Gate Hook

**Trigger:** PreToolUse on `Bash(gh pr create:*)`.

**Behavior:**
1. Read `.claude/workflow-state.json`.
2. Verify ALL of the following:
   - `gates.commit_ref` matches current HEAD (gates ran against the current code).
   - `verify` has at least one surface with a timestamp (visual verification happened).
   - `qa` has at least one surface with a timestamp (blind verification happened).
   - `review.passed` has a timestamp (code review completed).
3. If any check fails, exit code 2 with a specific message listing missing steps.
4. **This is a hard gate.** PR creation is blocked until all checks pass.

**Output on block:**
```
PR blocked. Missing workflow steps:
  ✗ /gates not run against current HEAD (last ran against abc1234, HEAD is def5678)
  ✗ /qa not completed for any surface
Run these before creating a PR.
```

#### Stop Hook

**Trigger:** Session end (Stop event).

**Behavior:**
1. Read `.claude/workflow-state.json` if it exists.
2. Check for uncommitted changes (`git status`).
3. Emit a workflow completion summary.
4. **Cannot block session end.** The user always has the right to stop.

**Output:**
```
SESSION END — Workflow status for feature/import-jobs:
  ✓ Gates passed
  ✓ Extension verified
  ✗ QA not completed — /qa was never run
  ✗ Review not completed — /review was never run
  ⚠ PR not created
  ⚠ Uncommitted changes in 3 files
```

### Skill Updates

#### Existing Skills — Workflow State Integration

Each skill writes its own entry to `.claude/workflow-state.json` upon successful completion:

| Skill | Writes to state |
|-------|----------------|
| `/implement` | `branch`, `spec`, `plan`, `started`. Mandatory tail: runs `/gates` → `/verify` → `/qa` → `/review` → `/converge` after final phase. |
| `/gates` | `gates.passed`, `gates.commit_ref` |
| `/verify` | `verify.{surface}` (ext, tui, mcp, cli) |
| `/qa` | `qa.{surface}` |
| `/review` | `review.passed`, `review.findings` |
| `/converge` | Clears `gates.passed` when starting a fix cycle (code is changing). Re-runs gates at end. |

#### Existing Skills — Renames and Restructuring

| Current Name | New Name | Change |
|-------------|----------|--------|
| `/retrospective` | `/retro` | Rename only. |
| `/webview-cdp` | `/ext-verify` | Rename. Update description for discoverability: "How to interact with VS Code extension webview panels for verification — Playwright Electron, screenshots, clicks, keyboard." |
| `/webview-panels` | `/ext-panels` | Rename. Absorb content from `/panel-design`. |
| `/panel-design` | (deleted) | Content merged into `/ext-panels`. |
| `/debug` | `/debug` | Major rewrite. Absorb systematic-debugging discipline from superpowers: 4-phase process (Root Cause → Pattern Analysis → Hypothesis → Implementation), Iron Law (no fixes without investigation), 3-fix escalation rule, red flags table, root-cause tracing technique, defense-in-depth validation, condition-based waiting. Keep PPDS-specific surface detection and build commands. Remove superpowers references. |

#### New Skills

| Skill | Purpose | Key Behavior |
|-------|---------|-------------|
| `/design` | Brainstorm → spec. Replaces `superpowers:brainstorming`. | Enters plan mode for design thinking. Auto-loads constitution and spec template into context. Uses `specs/` for output location. Creates worktree when ready to commit spec. Outputs a committed spec file. |
| `/pr` | Rebase → PR → monitor → summarize. | Rebases on main. Creates PR with structured body. Polls CI status and Gemini reviews (every 30s for 2 min, then every 2 min). When complete: triages Gemini comments (fix valid ones, dismiss invalid with rationale), replies to EACH comment individually on the PR with action taken, presents summary to user. Writes `pr.url` and `pr.created` to workflow state. |
| `/shakedown` | Multi-surface product validation. | Structured phases: scope declaration → test matrix creation → interactive verification per surface → parity comparison → architecture audit → findings document. Requires explicit test matrix before testing begins. Collaborative (user + AI). Outputs findings to `docs/qa/`. |
| `/write-skill` | Author new skills following PPDS conventions. | Encodes naming convention (`{action}` or `{action}-{qualifier}`, kebab-case). Encodes directory structure (skills/ with SKILL.md + supporting files). Encodes frontmatter patterns. Encodes description writing for AI discoverability. Encodes integration with workflow state (when and how to write state entries). |
| `/mcp-verify` | How to verify MCP tools. | Supporting knowledge for `/verify` and `/qa`. Documents: MCP Inspector usage, direct tool invocation patterns, response validation, session option testing. |
| `/cli-verify` | How to verify CLI commands. | Supporting knowledge for `/verify` and `/qa`. Documents: build and run patterns, stdout (data) vs stderr (status), exit code validation, pipe testing. |

### CLAUDE.md Workflow Section Rewrite

Replace the current workflow bullet list with:

```markdown
## Workflow (REQUIRED SEQUENCE)

### New feature or non-trivial change
1. /spec (or verify spec exists with numbered ACs)
2. /spec-audit (verify spec matches codebase reality)
3. Write implementation plan → user approves
4. /implement <plan-path>
5. /gates — STOP on failure, fix before proceeding
6. /verify for EVERY affected surface — you MUST use the product:
   - Extension changed → /ext-verify (screenshots required)
   - TUI changed → /tui-verify (PTY interaction required)
   - MCP changed → /mcp-verify (tool invocation required)
   - CLI changed → /cli-verify (run the command)
7. /qa for at least one affected surface (blind verification)
8. /review → /converge until 0 critical, 0 important
9. /pr (rebase, create PR, monitor CI + reviews)

### Bug fix or small change
1. /gates before committing
2. If UI/output changed → /verify for affected surface
3. /pr when ready

### STOP conditions
- DO NOT skip steps 5-8 because "tests pass." Tests are necessary, not sufficient.
- DO NOT declare work complete without visual verification of affected surfaces.

### Autonomy scope
"Don't ask, just do it" applies to: committing after tasks, running gates,
running verification, running QA, running review, triaging external review
comments (fix valid ones, dismiss invalid ones with rationale).
"Don't ask, just do it" does NOT apply to: skipping any workflow step,
filing/closing issues, creating PRs without passing gates.

After external review: respond to EACH comment individually on the PR with
the action taken (fixed in <commit>, or dismissed with rationale). Include
a summary of all comments and actions in the PR status report.
```

### Superpowers Disposition

After all skills in this spec are implemented:

1. Add to `.claude/settings.json`:
   ```json
   { "enabledPlugins": { "superpowers@claude-plugins-official": false } }
   ```
2. This disables superpowers for the ppds repo only. It remains installed globally for other repos.
3. `/debug` absorbs the only actively-used superpowers skill (`systematic-debugging`, 15 invocations across 12 sessions).

---

## Acceptance Criteria

| ID | Criterion | Test | Status |
|----|-----------|------|--------|
| AC-01 | Workflow state file is created when `/gates` runs and contains `gates.passed` timestamp and `gates.commit_ref` matching HEAD | Manual: run `/gates`, read `.claude/workflow-state.json` | 🔲 |
| AC-02 | Workflow state file is created when `/verify ext` runs and contains `verify.ext` timestamp | Manual: run `/verify ext`, read state file | 🔲 |
| AC-03 | Workflow state file is created when `/qa` runs and contains `qa.{surface}` timestamp | Manual: run `/qa`, read state file | 🔲 |
| AC-04 | Workflow state file is created when `/review` runs and contains `review.passed` timestamp | Manual: run `/review`, read state file | 🔲 |
| AC-05 | `gates.passed` is cleared (set to null) when a new commit is made after gates passed | Manual: run `/gates`, commit, read state file | 🔲 |
| AC-06 | PR Gate hook blocks `gh pr create` when `gates.commit_ref` does not match HEAD | Manual: run `/gates`, commit new code, attempt `gh pr create` | 🔲 |
| AC-07 | PR Gate hook blocks `gh pr create` when `qa` has no entries | Manual: skip `/qa`, attempt `gh pr create` | 🔲 |
| AC-08 | PR Gate hook blocks `gh pr create` when `review.passed` is null | Manual: skip `/review`, attempt `gh pr create` | 🔲 |
| AC-09 | PR Gate hook allows `gh pr create` when all checks pass | Manual: complete full workflow, attempt `gh pr create` | 🔲 |
| AC-10 | SessionStart hook injects workflow state summary into AI context | Manual: start session on feature branch with existing state file | 🔲 |
| AC-11 | SessionStart hook injects required workflow sequence when no state file exists | Manual: start session on new feature branch | 🔲 |
| AC-12 | Stop hook emits workflow completion summary on session end | Manual: end session with incomplete workflow | 🔲 |
| AC-13 | Pre-commit hook warns when gates are stale and `src/` files are staged | Manual: modify src/ file, commit without running `/gates` | 🔲 |
| AC-14 | `/implement` runs `/gates`, `/verify`, `/qa`, `/review` as mandatory tail after final phase | Manual: run `/implement` on a plan, verify tail steps execute | 🔲 |
| AC-15 | `/pr` responds to each Gemini comment individually on the PR | Manual: create PR with Gemini review, verify per-comment replies | 🔲 |
| AC-16 | `/pr` includes summary of all review comments and actions in status report | Manual: create PR, verify summary output | 🔲 |
| AC-17 | `/design` enters plan mode and auto-loads constitution + spec template | Manual: run `/design`, verify plan mode and loaded context | 🔲 |
| AC-18 | `/design` creates worktree and commits spec when design is approved | Manual: complete design flow, verify worktree + committed spec | 🔲 |
| AC-19 | Superpowers is disabled for ppds repo after all skills are implemented | Verify `.claude/settings.json` contains `"superpowers@claude-plugins-official": false` | 🔲 |
| AC-20 | `/debug` includes 4-phase systematic debugging process, 3-fix escalation, red flags table | Read `/debug` skill content, verify sections present | 🔲 |
| AC-21 | All renamed skills (`/ext-verify`, `/ext-panels`, `/retro`) are discoverable by AI via natural language | Manual: say "test the extension", verify `/ext-verify` is loaded | 🔲 |
| AC-22 | `/shakedown` requires explicit test matrix before testing begins | Manual: run `/shakedown`, verify matrix creation step | 🔲 |
| AC-23 | `/write-skill` encodes naming convention and outputs skills in correct directory structure | Manual: use `/write-skill` to create a skill, verify output | 🔲 |

### Edge Cases

| Scenario | Expected Behavior |
|----------|-------------------|
| No workflow state file exists when hook fires | SessionStart: inject full workflow sequence. Pre-commit: no warning. PR gate: block (no evidence of any steps). Stop: no summary. |
| Workflow state file is corrupted/invalid JSON | All hooks: treat as "no state file" — safe default is to block PR and warn. |
| Session is on `main` branch (no feature work) | SessionStart: skip workflow injection. Hooks: skip enforcement. |
| Multiple surfaces verified but one is missing | PR gate: passes — requires "at least one" verified surface, not all. The user chose which surfaces to test. |
| User runs `/gates` but doesn't commit — runs `/gates` again | Second run overwrites the first timestamp. No accumulation issues. |
| WIP commit during implementation | Pre-commit warns (soft gate). PR gate is not triggered. Workflow continues. |
| `/converge` fix cycle introduces new commits | `/converge` clears `gates.passed` on start. Re-runs gates at end of cycle. Final state has fresh gates. |

---

## Design Decisions

### Why Hook Gates Over Full State Machine?

**Context:** Need to enforce workflow compliance without over-constraining the AI or breaking on edge cases (hotfixes, partial work, WIP commits).

**Decision:** Enforce outcomes at exit points (commit, PR), not step-by-step sequencing.

**Alternatives considered:**
- **Prose-only enforcement (Approach A):** Two retrospectives proved this fails. AI optimizes for completion over compliance and skips steps when not mechanically blocked.
- **Full state machine (Approach C):** Every transition gated, can't proceed without completing current step. Too brittle for real development (hotfixes, partial work, context switches). Over-engineered for the actual failure mode, which is "steps don't happen at all" not "steps happen out of order."

**Consequences:**
- Positive: Simple, targeted, solves the actual problem. AI can work flexibly within phases.
- Negative: Cannot enforce skill invocation order. Cannot verify that screenshots were actually examined vs. opened and closed. Relies on skills honestly writing to state file.

### Why Soft Gate on Commit, Hard Gate on PR?

**Context:** Commits happen frequently during implementation, including WIP saves. PRs are the exit point where work becomes visible to others.

**Decision:** Warn on commit (don't block), block on PR (hard gate).

**Alternatives considered:**
- **Hard gate on every commit:** Would block WIP commits, frustrating during implementation. Would require a "WIP mode" escape hatch, defeating the purpose.
- **No commit gate at all:** Misses the opportunity to remind the AI that gates need re-running.

**Consequences:**
- Positive: Natural workflow — implement freely, enforce before shipping.
- Negative: AI could accumulate many commits without running gates. Mitigated by SessionStart hook showing stale state.

### Why Disable Superpowers Instead of Layering?

**Context:** Superpowers provides generic process skills. PPDS has domain-specific skills that cover the same ground but with codebase awareness.

**Decision:** Disable superpowers for this repo after replacing all used skills.

**Evidence:**
- 74% of superpowers invocations (78/106) are skills we're replacing: `writing-plans` (34), `brainstorming` (24), `subagent-driven-development` (20).
- The "safety net" skills (`verification-before-completion`, `test-driven-development`, `finishing-a-development-branch`) have zero invocations — they never fire.
- Only `systematic-debugging` (15 invocations) provides value we don't replicate, and it's being absorbed into `/debug`.

**Alternatives considered:**
- **Keep as safety net:** Data shows the safety net skills never trigger. Keeping superpowers adds token overhead (session start hook, skill discovery) for zero demonstrated value.

**Consequences:**
- Positive: Full control over skill behavior. No external dependency changes breaking our workflow. Reduced token overhead.
- Negative: Lose automatic access to future superpowers improvements. Mitigated by periodic review of superpowers changelog.

### Why Absorb Systematic Debugging Into /debug?

**Context:** `systematic-debugging` is the only superpowers skill with significant usage (15 invocations) that isn't already replaced by a PPDS command.

**Decision:** Merge its discipline (4-phase process, Iron Law, escalation rules, supporting techniques) into `/debug`, which already has PPDS-specific surface detection and build commands.

**Alternatives considered:**
- **Keep superpowers just for this skill:** Requires keeping the entire plugin enabled for one skill. Adds session start overhead for all sessions.
- **Write a separate `/debug-systematic` skill:** Creates two debugging skills with unclear scoping. Users wouldn't know which to use.

**Consequences:**
- Positive: Single debugging skill with both discipline and domain specificity.
- Negative: We must maintain the debugging discipline content ourselves. Mitigated by the content being stable (debugging fundamentals don't change).

---

## Extension Points

### Adding a New Verification Surface

When a new UI surface is added to PPDS (e.g., a web dashboard):

1. **Create `/surface-verify` skill** in `.claude/skills/surface-verify/SKILL.md` following `/write-skill` conventions.
2. **Update `/verify`** to recognize the new surface as a mode.
3. **Update `/qa`** to support the new surface for blind verification.
4. **Update CLAUDE.md** workflow section to include the new surface in step 6.
5. **No hook changes needed** — hooks check for "at least one surface verified," so new surfaces are automatically accepted.

### Adding a New Workflow Step

When a new required step is added (e.g., security scanning):

1. **Create or update the skill** to write its own entry to `workflow-state.json`.
2. **Update PR Gate hook** to check for the new entry.
3. **Update CLAUDE.md** workflow section.
4. **Update SessionStart hook** output format to include the new step.

---

## Related Specs

- [CONSTITUTION.md](./CONSTITUTION.md) — Non-negotiable principles that the workflow enforces
- [architecture.md](./architecture.md) — System architecture that skills reference

---

## Roadmap

- **Headless execution (Option C):** Trigger implementation from GitHub issues or webhooks. Requires persisted workflow state and session handoff mechanism.
- **PR monitoring webhook:** Replace polling in `/pr` with GitHub webhook notification when CI/reviews complete.
- **Worktree auto-cleanup:** SessionStart hook checks for stale worktrees (no commits in >7 days) and prompts for cleanup.
- **Cross-session workflow continuity:** Persist workflow state to git (not gitignored) so a new session can pick up where a previous session left off.
