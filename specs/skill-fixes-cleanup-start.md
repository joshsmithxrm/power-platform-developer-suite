# Skill Fixes: Cleanup Orphan Sweep and Start Discoverability

**Status:** Draft
**Last Updated:** 2026-03-29
**Code:** [.claude/skills/cleanup/](../.claude/skills/cleanup/), [.claude/skills/start/](../.claude/skills/start/), [.claude/skills/design/](../.claude/skills/design/)
**Surfaces:** N/A

---

## Overview

Three targeted fixes to skill definition files. (1) The cleanup skill has no step to remove orphan directories — directories in `.worktrees/` that are not registered git worktrees accumulate when `git worktree remove` deregisters but fails to delete the directory. (2) The start skill lacks natural-language trigger phrases, causing the agent to miss user intent when the request doesn't use the exact `/start` command. (3) The design skill's handoff step presents three options and waits for user input after the user already approved the spec and plan — in interactive mode it should just proceed to `/implement` automatically.

### Goals

- **Orphan sweep**: Cleanup skill detects and removes directories in `.worktrees/` that are not registered worktrees
- **Start discoverability**: Agent reliably invokes `/start` when the user expresses intent to begin new work, even without saying `/start`
- **Design auto-proceed**: Design skill proceeds directly to `/implement` in interactive mode after spec and plan are approved, without stopping to ask

### Non-Goals

- Changing cleanup's merge-detection logic or rebase behavior
- Adding hook-based auto-invocation of skills
- Constitutional-level workflow rules

---

## Architecture

No code architecture — these are markdown skill definition edits.

```
.claude/skills/cleanup/SKILL.md  ← add orphan sweep step
.claude/skills/start/SKILL.md    ← add "When to Use" section
.claude/skills/design/SKILL.md   ← auto-proceed in interactive handoff
```

---

## Specification

### Fix 1: Cleanup Orphan Directory Sweep

Add a new step **"4b. Sweep Orphan Directories"** between the current step 4 (Remove Merged Worktrees) and step 5 (Delete Local Branches).

**Logic:**

1. List all directories in `.worktrees/`:
   ```bash
   ls -d .worktrees/*/ 2>/dev/null
   ```

2. Parse registered worktree paths from `git worktree list --porcelain` (already run in step 3). Extract the `worktree` lines.

3. For each directory in `.worktrees/` that does NOT appear in the registered worktree list:
   - Classify as **orphan**
   - If `--dry-run`: report it, do not delete
   - Otherwise: `rm -rf .worktrees/<name>`

4. Add an **"Orphans Removed"** section to the final report (step 7):
   ```
   ### Orphans Removed
   | Directory | Status |
   |-----------|--------|
   | .worktrees/old-thing | Removed |
   | .worktrees/stale-dir | Failed (permission denied) |
   ```

   If no orphans found, omit this section.

5. Update the Summary line to include orphan count:
   ```
   - Orphans: N removed, N failed
   ```

**Error handling:**

| Error | Recovery |
|-------|----------|
| `rm -rf` fails (permission denied) | Log as failed in report, continue with next orphan |
| `.worktrees/` directory doesn't exist | Skip step — nothing to sweep |
| Orphan directory is empty | Remove it (still an orphan) |

**Safety rules:**
- NEVER remove the main worktree directory (the repo root). Guard: compare each candidate orphan directory's resolved absolute path against the main worktree path (the first `worktree` entry in `git worktree list --porcelain`). If they match, skip it.
- Respect `--dry-run` (existing cleanup flag) — report orphans but do not delete.
- Locked worktree check does not apply — orphans are not registered worktrees, so they have no lock attribute. They are unconditionally eligible for removal.

### Fix 2: Start Skill Discoverability

**A. Add "When to Use" section** after the description heading, before "Usage":

```markdown
## When to Use

- "Start a worktree for..."
- "Create a worktree for..."
- "Set up a worktree"
- "I need to work on..." (when no worktree exists yet)
- "Let's start on..."
- "Begin work on..."
- "New feature/bug/task for..."
- Any request that implies beginning new work in a fresh worktree
```

**B. Strengthen the frontmatter description** to include the key matching phrase "worktree":

Current:
```
description: Create a worktree for new work and open a terminal there. Use when starting any new feature, bug fix, or task. Accepts freeform input — issues, descriptions, context. Runs from main.
```

Updated:
```
description: Create a worktree for new work and open a terminal there. Use when starting any new feature, bug fix, or task — including when the user says "start a worktree", "create a worktree", or describes work they want to begin. Accepts freeform input — issues, descriptions, context. Runs from main.
```

### Fix 3: Design Skill Auto-Proceed on Handoff

Replace the current Step 6 (Handoff) in the design skill. The current step presents three options and waits for user input. The user has already approved the spec (Step 3) and the plan (Step 4) — asking again is redundant.

**Current behavior (Step 6):**
```
Present three options:
  1. Launch headless pipeline (recommended)
  2. Continue interactively → /implement
  3. Defer (pick up later)
If the user chooses option 1/2/3...
```

**New behavior (Step 6):**
After committing the spec and setting phase to `implementing`, proceed directly to `/implement`. No options presented, no user input required. The user's approval of the plan in Step 4 is the signal to proceed.

The pipeline option (option 1) is removed — if the user wanted headless execution, they would have launched the pipeline directly instead of running `/design` interactively. The defer option (option 3) is unnecessary — the user can interrupt at any time.

### Constraints

- Edits are limited to `.claude/skills/cleanup/SKILL.md`, `.claude/skills/start/SKILL.md`, and `.claude/skills/design/SKILL.md`
- No new files created
- No changes to any other skill or spec

---

## Acceptance Criteria

| ID | Criterion | Test | Status |
|----|-----------|------|--------|
| AC-01 | Cleanup SKILL.md contains a step that lists `.worktrees/` directory entries and compares them against `git worktree list` output to identify orphans (directories not registered as worktrees) | `grep "worktree list" .claude/skills/cleanup/SKILL.md` | 🔲 |
| AC-02 | Cleanup SKILL.md specifies `rm -rf` removal for orphan directories and includes the guard comparing against the main worktree path | `grep "rm -rf" .claude/skills/cleanup/SKILL.md` | 🔲 |
| AC-03 | Cleanup SKILL.md orphan sweep step references `--dry-run` and specifies report-only behavior when active | `grep -A2 "dry-run.*orphan\|orphan.*dry-run" .claude/skills/cleanup/SKILL.md` | 🔲 |
| AC-04 | Cleanup SKILL.md final report template includes an "Orphans Removed" table section | `grep "Orphans Removed" .claude/skills/cleanup/SKILL.md` | 🔲 |
| AC-05 | Cleanup SKILL.md error handling table includes `rm -rf` failure with "continue with next orphan" recovery | `grep "rm.*rf.*fail\|permission denied" .claude/skills/cleanup/SKILL.md` | 🔲 |
| AC-06 | Start SKILL.md contains a "When to Use" section with at least 5 natural-language trigger phrases including "start a worktree" and "create a worktree" | `grep -c "When to Use\|worktree" .claude/skills/start/SKILL.md` | 🔲 |
| AC-07 | Start SKILL.md frontmatter `description:` field contains the phrases "start a worktree" and "create a worktree" | `head -5 .claude/skills/start/SKILL.md \| grep "start a worktree"` | 🔲 |
| AC-08 | Design SKILL.md Step 6 invokes `/implement` directly after committing the spec, without presenting options or waiting for user input | `grep "implement" .claude/skills/design/SKILL.md` | 🔲 |
| AC-09 | Design SKILL.md Step 6 does not contain "Present three options" or conditional "If the user chooses" language | `grep -c "Present three options\|If the user chooses" .claude/skills/design/SKILL.md` returns 0 | 🔲 |

### Edge Cases

| Scenario | Input | Expected Output |
|----------|-------|-----------------|
| No orphans | All `.worktrees/` dirs are registered | Orphans section omitted from report |
| `.worktrees/` doesn't exist | Fresh repo, no worktrees ever created | Step skipped silently |
| Orphan dir has locked files | OS-level lock on files inside orphan dir | `rm -rf` fails, logged in report, continue |
| All worktrees are orphans | Git tracking lost | All removed, reported |
| Symlink/junction in `.worktrees/` | Windows junction point or symlink | Remove the link itself, do not follow into the target directory |

---

## Design Decisions

### Why `rm -rf` for orphans?

**Context:** Orphan directories are not registered git worktrees, so `git worktree remove` cannot operate on them.

**Decision:** Use `rm -rf` — the only option for unregistered directories.

**Alternatives considered:**
- `git worktree remove`: Cannot work — git doesn't know about the directory
- Report-only: Leaves the accumulation problem unsolved; user must manually clean up
- Interactive per-orphan: Unnecessary friction — `--dry-run` provides the preview escape hatch

**Consequences:**
- Positive: Orphans are cleaned up automatically
- Negative: If a directory was manually placed in `.worktrees/` for non-worktree purposes, it would be deleted. This is acceptable — `.worktrees/` is exclusively for git worktrees.

### Why a "When to Use" section for start?

**Context:** The agent failed to invoke `/start` when the user said "start a worktree." The cleanup skill has a "When to Use" section and is reliably invoked.

**Decision:** Add matching trigger phrases to the start skill, following the same pattern as cleanup.

**Alternatives considered:**
- Constitutional rule: Too heavy for a discoverability fix
- Hook-based detection: Fragile pattern matching, hard to maintain
- Agent-side fix: Not actionable — we can only change skill definitions

### Why auto-proceed instead of options?

**Context:** The design skill's Step 6 presents three options (pipeline, interactive, defer) after the user has already approved the spec and plan. In practice, the user approved twice (spec in Step 3, plan in Step 4) — asking a third time is redundant friction.

**Decision:** In interactive mode, proceed directly to `/implement` after committing the spec. The user's plan approval is the go signal.

**Alternatives considered:**
- Keep options but default to one: Still presents unnecessary UI; the default would always be chosen
- Remove only the defer option: Doesn't solve the core problem — the user still has to confirm

**Consequences:**
- Positive: Seamless flow from design → implement → gates → review → PR
- Negative: User cannot defer at the handoff point. Acceptable — they can interrupt or Ctrl+C at any time.

### Why two fixes in one spec?

**Context:** Constitution SL1 says "one spec per domain concept." These are two independent fixes.

**Decision:** Keep as one spec. Both fixes are paragraph-level edits to markdown skill definitions — no executable code, no shared architecture, no complex interactions. The overhead of two full specs (each with template sections, review cycles, and plans) exceeds the total implementation work. The domain concept is "skill definition maintenance."

**Alternatives considered:**
- Split into two specs: Correct per SL1's letter, but the spirit targets domain features with real architecture, not one-paragraph markdown edits.

### Why I6 (test methods) doesn't apply here

**Context:** Constitution I6 requires every AC to have a corresponding passing test method. All changes in this spec are to markdown skill definition files — no executable code is produced.

**Decision:** ACs reference grep-based content assertions against the SKILL.md files. These are verifiable commands, not test class methods, because there is no code under test. The deliverable is prose instructions, not behavior.

**Alternatives considered:**
- Write a test harness that parses SKILL.md and asserts content: Over-engineered for static markdown files. The grep commands in the AC table serve the same purpose.

---

## Changelog

| Date | Change |
|------|--------|
| 2026-03-29 | Initial spec |
