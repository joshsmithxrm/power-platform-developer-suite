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

**Location:** `.workflow/state.json` (added to `.gitignore`)

**Schema:**

```json
{
  "branch": "feature/import-jobs",
  "spec": "specs/import-jobs.md",
  "plan": ".plans/2026-03-16-import-jobs.md",
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
3. **State invalidation on commit:** The Post-Commit Hook (see below) clears `gates.passed` (sets to `null`) after every successful commit because the codebase has changed since gates last ran. `verify`, `qa`, and `review` timestamps are NOT automatically cleared on commit — they track cumulative coverage across the session.
4. Skills are responsible for writing their own entries. No central coordinator.
5. File is gitignored — it is per-session state, not committed.
6. **Converge cycle handling:** `/converge` clears `gates.passed` before starting its fix cycle. After the final fix cycle completes and all fixes are committed, `/converge` runs `/gates` one final time. The Post-Commit Hook clears `gates.passed` on each fix commit, but `/converge`'s final `/gates` run writes a fresh `gates.passed` + `gates.commit_ref` matching the final HEAD. This prevents deadlock: the sequence is always fix → commit → (gates cleared) → final gates → (gates fresh against HEAD).

### Hook Specifications

#### SessionStart Hook

**Trigger:** Session start on any branch.

**Behavior:**
1. Read `.workflow/state.json` if it exists.
2. Read current branch name.
3. Determine workflow state: which steps have been completed, which are stale, which are pending.
4. Inject into AI context:
   - Current branch and workflow state summary.
   - Required workflow sequence (the decision tree from CLAUDE.md).
   - Any stale entries (e.g., "gates passed but new commits since — gates must re-run").
5. If no workflow state file exists, inject the full required workflow sequence as a reminder.
6. If on `main` or `master`: list active worktrees (via `git worktree list`), suggest `/start` for new work. Do NOT skip — provide guidance.

**Output format (feature branch):**
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

**Relationship to existing hook:** The existing `.claude/hooks/pre-commit-validate.py` runs `dotnet build`, `dotnet test`, and `npm run lint` as a hard gate (exits code 2 on failure). That behavior is unchanged. The workflow state check below is added to the same hook file as an additional, non-blocking check that runs after the existing build/test/lint validation.

**Added behavior (workflow state warning):**
1. Check if files under `src/` are staged.
2. If yes, read `.workflow/state.json`.
3. If `gates.commit_ref` does not match the current staging state (i.e., code has changed since gates last ran), emit a warning.
4. **The workflow state warning does not block the commit.** The existing build/test/lint validation continues to block on failure. This is a soft gate — WIP commits during implementation are expected.

**Output on warn:**
```
⚠ Warning: /gates has not been run since your last changes. Run /gates before creating a PR.
```

#### Post-Commit Hook

**Trigger:** PostToolUse on `Bash(git commit:*)`.

**Behavior:**
1. Read `.workflow/state.json` if it exists.
2. Clear `gates.passed` (set to `null`) — the codebase has changed since gates last ran.
3. Update `last_commit` to current HEAD.
4. Write updated state file.

**Implementation:** `.claude/hooks/post-commit-state.py`, triggered via a new `PostToolUse` section in `.claude/settings.json` (see Implementation Notes below).

This is the component responsible for state invalidation on commit (Core Requirement 3). Without it, stale `gates.passed` timestamps would persist across commits.

#### PR Gate Hook

**Trigger:** PreToolUse on `Bash(gh pr create:*)`.

**Behavior:**
1. Read `.workflow/state.json`.
2. Verify ALL of the following:
   - `gates.commit_ref` matches current HEAD (gates ran against the current code).
   - `verify` has at least one surface with a timestamp (visual verification happened).
   - `qa` has at least one surface with a timestamp (blind verification happened).
   - `review.passed` has a timestamp (code review completed).
3. If any check fails, exit code 2 with a specific message listing missing steps.
4. **This is a hard gate.** PR creation is blocked until all checks pass.

**Implementation:** `.claude/hooks/pr-gate.py`, triggered via `.claude/settings.json` PreToolUse matcher on `Bash(gh pr create:*)`.

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
1. Read `.workflow/state.json` if it exists.
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

Each skill writes its own entry to `.workflow/state.json` upon successful completion:

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
| `/implement` | `/implement` | Remove all superpowers references (currently references `superpowers:using-superpowers`, `superpowers:dispatching-parallel-agents`, `superpowers:verification-before-completion`, `superpowers:requesting-code-review`, `superpowers:systematic-debugging`). Replace with PPDS-native equivalents. Add mandatory tail (gates → verify → QA → review → converge). Add workflow state writes. |
| `/review` | `/review` | Remove superpowers reference (currently dispatches via `subagent_type: 'superpowers:code-reviewer'`). Dispatch reviewer as a general-purpose subagent with the same isolation constraints (diff + constitution + ACs only, no implementation context). |
| `/converge` | `/converge` | Retain all existing convergence tracking, cycle evaluation, and stall detection. Additionally: add workflow state integration — clear `gates.passed` on cycle start, run `/gates` after final fix cycle, write fresh `gates.passed` + `gates.commit_ref` on completion. |
| `/debug` | `/debug` | Major rewrite. Absorb systematic-debugging discipline from superpowers: 4-phase process (Root Cause → Pattern Analysis → Hypothesis → Implementation), Iron Law (no fixes without investigation), 3-fix escalation rule, red flags table, root-cause tracing technique, defense-in-depth validation, condition-based waiting. Keep PPDS-specific surface detection and build commands. Remove superpowers references. |

#### New Skills

| Skill | Purpose | Key Behavior |
|-------|---------|-------------|
| `/design` | Brainstorm → spec. Replaces `superpowers:brainstorming`. | Auto-loads constitution and spec template into context. Uses its own approval gates (one question at a time, incremental validation) — do NOT use plan mode. Uses `specs/` for output location. Creates worktree when ready to commit spec. Outputs a committed spec file. |
| `/pr` | Rebase → PR → monitor → summarize. | Rebases on main. Creates PR with structured body. Polls CI status and Gemini reviews (every 30s for 2 min, then every 2 min, max 15 min total). When complete: triages Gemini comments (fix valid ones, dismiss invalid with rationale), replies to EACH comment individually on the PR with action taken, presents summary to user. On timeout: reports current status and what's still pending. Writes `pr.url` and `pr.created` to workflow state. |
| `/shakedown` | Multi-surface product validation. | Structured phases: scope declaration → test matrix creation → interactive verification per surface → parity comparison → architecture audit → findings document. Requires explicit test matrix before testing begins. Collaborative (user + AI). Outputs findings to `docs/qa/`. |
| `/write-skill` | Author new skills following PPDS conventions. | Encodes naming convention (`{action}` or `{action}-{qualifier}`, kebab-case). Encodes directory structure (skills/ with SKILL.md + supporting files). Encodes frontmatter patterns. Encodes description writing for AI discoverability. Encodes integration with workflow state (when and how to write state entries). |
| `/mcp-verify` | How to verify MCP tools. | Supporting knowledge for `/verify` and `/qa`. Documents: MCP Inspector usage, direct tool invocation patterns, response validation, session option testing. |
| `/cli-verify` | How to verify CLI commands. | Supporting knowledge for `/verify` and `/qa`. Documents: build and run patterns, stdout (data) vs stderr (status), exit code validation, pipe testing. |
| `/status` | Display current workflow state. | Reads `.workflow/state.json` and displays the same summary as SessionStart hook. On-demand visibility into what's been done and what's pending. No state writes. |
| `/start` | Bootstrap a feature worktree. | Accepts feature name, creates worktree at `.worktrees/<name>` with branch `feature/<name>`, initializes `workflow-state.json` with `branch` and `started` fields, opens new tab in current Windows Terminal window via `wt -w 0 new-tab`, prints required workflow sequence. Handles: existing branch (no `-b`), existing worktree (offer switch), missing `wt` (warn + skip). |

### Main Branch Bootstrap

The workflow enforcement system must prevent accidental work on main and guide users to feature worktrees.

#### SessionStart Hook on Main (modified)

**Current behavior:** Skip entirely on main/master (exit 0).

**New behavior:** On main/master, show active worktrees and guidance:

```
You are on main. Active worktrees:
  .worktrees/panel-env-connref  [feature/panel-env-connref]
  .worktrees/panel-metadata     [feature/panel-metadata]

To start new work: /start <feature-name>
Planning and exploration are fine on main. Implementation requires a worktree.
```

If no worktrees exist, skip the list, show `/start` guidance only.

#### Pre-Commit Guard on Main (modified)

**Current behavior:** Pre-commit hook runs build/test/lint, then soft workflow warning.

**New behavior:** Before build/test/lint, check branch. If on main/master:

```
❌ Cannot commit to main. Use /start <name> to create a feature worktree.
```

Exit code 2 (hard gate). Build/test/lint do not run.

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

### Enforcement
Steps 5-8 are enforced by hooks. The PR gate hook will block `gh pr create`
if these steps are incomplete. Run `/status` to check current workflow state.

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
| AC-01 | Workflow state file is created when `/gates` runs and contains `gates.passed` timestamp and `gates.commit_ref` matching HEAD | Manual: run `/gates`, read `.workflow/state.json` | 🔲 |
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
| AC-17 | `/design` auto-loads constitution + spec template and uses its own approval gates (NOT plan mode) | Manual: run `/design`, verify context loaded and incremental approval flow | 🔲 |
| AC-18 | `/design` creates worktree and commits spec when design is approved | Manual: complete design flow, verify worktree + committed spec | 🔲 |
| AC-19 | Superpowers is disabled for ppds repo after all skills are implemented | Verify `.claude/settings.json` contains `"superpowers@claude-plugins-official": false` | 🔲 |
| AC-20 | `/debug` includes 4-phase systematic debugging process, 3-fix escalation, red flags table | Read `/debug` skill content, verify sections present | 🔲 |
| AC-21 | All renamed skills (`/ext-verify`, `/ext-panels`, `/retro`) are discoverable by AI via natural language | Manual: say "test the extension", verify `/ext-verify` is loaded | 🔲 |
| AC-22 | `/shakedown` requires explicit test matrix before testing begins | Manual: run `/shakedown`, verify matrix creation step | 🔲 |
| AC-23 | `/write-skill` encodes naming convention and outputs skills in correct directory structure | Manual: use `/write-skill` to create a skill, verify output | 🔲 |
| AC-24 | Post-Commit Hook clears `gates.passed` after a successful commit | Manual: run `/gates`, commit, verify `gates.passed` is null in state file | 🔲 |
| AC-25 | `/converge` clears `gates.passed` on fix cycle start | Manual: run `/converge` with findings, verify state cleared before fixes | 🔲 |
| AC-26 | `/converge` writes fresh `gates.passed` and `gates.commit_ref` after final cycle passes | Manual: complete `/converge`, verify state file has fresh gates matching HEAD | 🔲 |
| AC-27 | `.workflow/state.json` is listed in `.gitignore` and not tracked by git | Verify `.gitignore` contains entry, `git status` does not show state file | 🔲 |
| AC-28 | `/implement` contains no superpowers references after rewrite | Read `/implement` skill content, grep for "superpowers" — zero matches | 🔲 |
| AC-29 | `/review` dispatches reviewer without superpowers dependency | Run `/review`, verify subagent launches as general-purpose with isolation constraints | 🔲 |
| AC-30 | `/pr` stops polling after 15 minutes with graceful timeout message | Manual: create PR with slow CI, verify timeout behavior | 🔲 |
| AC-31 | `/status` displays current workflow state summary on demand | Manual: run `/status` mid-session, verify output matches state file | 🔲 |
| AC-32 | `/shakedown` outputs findings document to `docs/qa/` | Manual: complete `/shakedown`, verify findings file created | 🔲 |
| AC-33 | `/shakedown` includes parity comparison across tested surfaces | Manual: run `/shakedown` on 2+ surfaces, verify parity matrix in output | 🔲 |
| AC-34 | `/start foo` creates worktree at `.worktrees/foo` with branch `feature/foo` | Manual: run `/start foo`, verify worktree + branch | 🔲 |
| AC-35 | `/start foo` initializes `workflow-state.json` with `branch` and `started` fields | Manual: run `/start foo`, read state file in worktree | 🔲 |
| AC-36 | `/start foo` opens new tab in current window via `wt -w 0 new-tab` | Manual: run `/start foo`, verify tab opens in current window | 🔲 |
| AC-37 | `/start foo` when worktree already exists offers to switch instead of creating | Manual: run `/start foo` twice, verify no duplicate | 🔲 |
| AC-38 | SessionStart hook on main shows active worktrees and `/start` guidance | Manual: start session on main with existing worktrees | 🔲 |
| AC-39 | Pre-commit hook blocks `git commit` on main with guidance message | Manual: attempt commit on main, verify exit code 2 | 🔲 |
| AC-40 | `/start` gracefully handles missing `wt` command (warns, continues) | Manual: test with `wt` unavailable | 🔲 |

### Edge Cases

| Scenario | Expected Behavior |
|----------|-------------------|
| No workflow state file exists when hook fires | SessionStart: inject full workflow sequence. Pre-commit: no warning. PR gate: block (no evidence of any steps). Stop: no summary. |
| Workflow state file is corrupted/invalid JSON | All hooks: treat as "no state file" — safe default is to block PR and warn. |
| Session is on `main` branch | SessionStart: show active worktrees and `/start` guidance. Pre-commit: block commits (hard gate). PR gate: skip (no PRs from main). |
| Multiple surfaces verified but one is missing | PR gate: passes — requires "at least one" verified surface, not all. The user chose which surfaces to test. |
| User runs `/gates` but doesn't commit — runs `/gates` again | Second run overwrites the first timestamp. No accumulation issues. |
| WIP commit during implementation | Pre-commit warns (soft gate). PR gate is not triggered. Workflow continues. |
| `/converge` fix cycle introduces new commits | `/converge` clears `gates.passed` on start. Post-Commit Hook clears on each fix commit. `/converge` runs final `/gates` after last fix, writing fresh state. No deadlock. |
| Multiple worktrees active simultaneously | Each worktree has its own `.workflow/state.json`. State files are independent — no cross-worktree interference. |
| `/pr` CI or Gemini review takes longer than 15 minutes | `/pr` stops polling, reports current status and what's still pending. User can re-check later with natural language. |

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

## Error Handling

| Error | Condition | Recovery |
|-------|-----------|----------|
| Python not available | Hook script cannot execute | Graceful skip — hooks should not block the workflow if Python is missing. Emit a warning and allow the operation. |
| JSON parse failure | `workflow-state.json` exists but is invalid JSON | Treat as "no state file." PR gate blocks (safe default). Emit warning suggesting the file may be corrupted. |
| File system permission error | Cannot read/write `workflow-state.json` | Skills: emit warning, continue without state tracking. Hooks: treat as "no state file" (PR gate blocks). |
| Concurrent access | Two processes write to state file simultaneously | Last-writer-wins. Acceptable for single-session state. The file is per-session, not shared across sessions. |
| Git HEAD cannot be resolved | Detached HEAD or corrupted git state | Hooks: skip enforcement, emit warning. Skills: skip state writes, emit warning. |

---

## Implementation Notes

### settings.json Changes Required

All hook-related `settings.json` changes consolidated:

1. **PostToolUse section** (new): Add `PostToolUse` matcher array to `.claude/settings.json`. Currently only `PreToolUse` exists.
   ```json
   "hooks": {
     "PostToolUse": [
       {
         "matcher": "Bash(git commit:*)",
         "hooks": [{ "type": "command", "command": ".claude/hooks/post-commit-state.py" }]
       }
     ]
   }
   ```

2. **PreToolUse additions**: Add PR gate hook entry to existing `PreToolUse` array:
   ```json
   {
     "matcher": "Bash(gh pr create:*)",
     "hooks": [{ "type": "command", "command": ".claude/hooks/pr-gate.py" }]
   }
   ```

3. **Superpowers disabling** (after all skills are implemented):
   ```json
   { "enabledPlugins": { "superpowers@claude-plugins-official": false } }
   ```

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
- **Devcontainer support for `/start`:** Offer to open worktree in devcontainer as alternative to Windows Terminal split pane.
