---
name: investigate
description: Pre-commitment exploration with optional adversarial challenge. Use when exploring ideas, evaluating approaches, or researching before committing to a direction.
---

# Investigate

Pre-commitment exploration that gathers context, researches options, synthesizes tradeoffs, and runs adversarial challenge before the human commits to a direction. Produces a validated design context for handoff to `/start` and `/design`.

## Usage

`/investigate <description>` — freeform description of what to explore.

## Process

### Preamble: Set Phase

```bash
python scripts/workflow-state.py set phase investigating
```

### Preamble: Ceremony Level

Ask: **"Quick or full investigation?"** Quick: Steps 1, 3, 6, 7, 8. Full: all 8 steps. Read REFERENCE.md §1 for mode-selection guidance.

### Step 1: Gather

Read relevant context from the codebase:

1. **Specs**: Search `specs/*.md` for overlapping scope — check file names, overview sections, and `**Code:**` frontmatter
2. **Skills**: Read any skills that will be affected by the proposal
3. **Code**: Read key source files in the affected area
4. **Retro patterns**: Read `.retros/summary.json` if it exists — check `findings_by_category` for recurring patterns. Skip on error.
5. **Design context from args**: If `$ARGUMENTS` contains a context reference, read it

### Step 2: Research (full mode only)

Dispatch a `general-purpose` agent with the research question and codebase context. <!-- enforcement: T3 -->
Read REFERENCE.md §2 for the agent prompt template, tool restrictions, and auto-detection rules.

### Step 3: Synthesize

Present the investigation synthesis with four sections: Options (2-3 with tradeoffs), Recommendation, Assumptions, and Constraints and Decisions (numbered settled items). Read REFERENCE.md §3 for the standard format.

### Step 4: Challenge (full mode only)

Dispatch the `challenger` agent. Read REFERENCE.md §4 for the prompt template, isolation rules (CRITICAL), and convergence criteria. Between rounds: address BLOCKERs only; skip CONCERNs/NITs until triage. Stop after 3 rounds.

### Step 5: Triage (full mode only)

Classify challenge findings. Read REFERENCE.md §5 for the triage classification rules. <!-- enforcement: T3 -->
Present triage summary: "Auto-resolved N findings (M corrections, K additions). Presenting P findings for your review."

### Step 6: Present

Present side by side: **Investigation Summary** (Step 3, updated after challenge) and **Challenge Findings** (Step 4, after triage). Quick mode: Investigation Summary only.

### Step 7: Align

<!-- enforcement: T3 advisory — see specs/skill-routing-gates.md and issue #1023 -->

As Step 7 renders, emit BEFORE presenting the options (so the counter fires every time Step 7 runs, not as an afterthought):

```bash
python scripts/workflow-state.py bump routing_gates.investigate.epic_offered_count
```

Then ask the human for a decision:

1. **Go** — proceed with the recommended approach
2. **Change X** — modify a specific aspect and re-evaluate (loops back to relevant step)
3. **Not worth it** — abandon the investigation
4. **File as epic** — research decomposes into N implementable items rather than a single deliverable; file the epic + child issues via `/backlog`

If the human picks option (4), additionally emit:

```bash
python scripts/workflow-state.py bump routing_gates.investigate.epic_chosen_count
```

Wait for the human's response. Do not proceed until alignment is reached.

### Step 8: Handoff

**If the human selected option (4) (File as epic) at Step 7:**

<!-- enforcement: T3 advisory — see specs/skill-routing-gates.md and issue #1023 -->

1. Build the epic body from the investigation summary (Problem Statement + Scope from Step 3).
2. For each numbered item in "Constraints and Decisions" produced at Step 3, prepare a candidate child issue title.
3. Invoke `/backlog` via the Skill tool with the epic title, body, and candidate children. `/backlog` files the epic first, then one child issue per item, linked to the epic.

The existing on-main vs in-worktree routing (below) applies only to options (1), (2), (3) — not option (4).

---

Present the investigation context summary that will be passed to `/start`. Read REFERENCE.md §6 for the context document format. Detect context and route:

**If on main or master:** Run `/start` to create a worktree. The investigation context passes via conversation to `/start` → `.plans/context.md`. Then run `/design`.

**If in a worktree:** Write context to `.plans/context.md`. Instruct: "Context written to `.plans/context.md`. Run `/design` to continue."

## Rules

Key: never headless; ceremony is human's call; challenger isolation (summary + constraints only, never raw context); BLOCKERs always to human; handoff is conversation-based. Read REFERENCE.md §7 for full rationale.
