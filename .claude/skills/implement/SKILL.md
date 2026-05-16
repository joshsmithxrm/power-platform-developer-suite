---
name: implement
description: Execute a checked-in implementation plan end-to-end using parallel agents. Use when a spec and plan exist in .plans/ and you're ready to build.
---

# Implement Plan

`/implement` — read spec and plan from `.plans/`, execute phases
`/implement specs/my-feature.md` — explicit spec path

If `PPDS_PIPELINE=1`: execute Steps 1–5 only (skip Step 6). Read REFERENCE.md §1.

## Prerequisites

Agent tool, `/review`, `/verify`, `/qa`, `/debug`, `/gates`.

## Input

`$ARGUMENTS` = path to plan file. If omitted: use spec from `.workflow/state.json`, generate plan, save to `.plans/`, proceed.

**Fallback — no spec:** prompt user: run `/design` or continue without spec?

## Process

### Step 1: Read & Analyze Plan
- Identify phases, dependencies, parallelization opportunities (sequential vs. parallel).
- **Findings reconciliation:** Cross-reference every finding ID (CC-01, V-15) from any findings doc against plan text; report gaps.
- **Shared-infrastructure scan:** Identify files in multiple phases; flag conflicts (must serialize or designate owner). See REFERENCE.md §4.

### Step 2: Load Spec Context
Read `specs/CONSTITUTION.md` + relevant specs (grep `**Code:**` frontmatter). Build spec context block for every subagent. See REFERENCE.md §3.

### Step 3: Assess Current State
Check git status, branch, existing worktrees, prior phase commits.

### Step 3.5: Initialize Workflow State
```bash
python scripts/workflow-state.py set branch "$(git rev-parse --abbrev-ref HEAD)"
python scripts/workflow-state.py set spec "{spec-path}"
python scripts/workflow-state.py set plan "$ARGUMENTS"
python scripts/workflow-state.py set started now
python scripts/workflow-state.py set phase implementing
```

### Step 4: Create Task Tracking
Build task list from plan phases; mark already-completed work done.

### Step 4.5: Assess Model Selection
Read REFERENCE.md §2 for Opus vs. Sonnet guidance.

### Step 5: Execute Each Phase

**Phase-entry inbox check (before dispatching any agents):**
```bash
python scripts/supervisor_msg.py read --consume
```
Handle each message kind per REFERENCE.md §9. Empty inbox → proceed normally.

**A. Dispatch Agents** — parallel for independent tasks; see REFERENCE.md §4.

**B. Collect Results** — wait for all agents; review summaries.

**B2. Cross-Agent Consistency Check** — verify cross-surface contract consistency; see REFERENCE.md §6.

**C. Verify Phase Gate** — build → tests → AC coverage → surface-specific verify/qa → review. See REFERENCE.md §5. Fix before advancing. Restore phase after sub-skills: `python scripts/workflow-state.py set phase implementing`

**D. Review** — invoke `/review` (reviewer sees diff + constitution + ACs only). Fix issues. Restore phase.

**E. Commit** — `git add` specific files; commit per REFERENCE.md §7.

**F. Advance** — move to next phase after commit. Update task tracking.

**G. Goal Verification** — per-phase fast feedback if spec has `**Verification:**` frontmatter; see REFERENCE.md §8.

### Step 5.5: Pre-Tail Goal Loop
Full goal loop with spec's `verification_max_iterations` (default 10). See REFERENCE.md §8.

### Step 6: Mandatory Tail — Full Verification Pipeline

**A. Gates** — `/gates`
**B. Verify** — `/verify extension|tui|cli|mcp` per changed surfaces
**C. QA** — `/qa extension|cli|mcp|tui` per changed surfaces
**D. Review** — `/review` final comprehensive review
**E. Converge** — if critical/important findings: gates→review→fix loop (max 5 cycles)
**F. Final State Check** — git log clean; `.workflow/state.json` timestamps all post-`started`
**G. Submit** — proceed IMMEDIATELY to `/pr`; do not stop to summarize
