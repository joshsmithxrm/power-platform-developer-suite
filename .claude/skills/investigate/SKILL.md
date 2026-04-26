---
name: investigate
description: Pre-commitment exploration with optional adversarial challenge. Use when exploring ideas, evaluating approaches, or researching before committing to a direction.
---

# Investigate

Pre-commitment exploration that gathers context, researches options, synthesizes tradeoffs, and runs adversarial challenge before the human commits to a direction. Produces a validated design context for handoff to `/start` and `/design`.

## Usage

`/investigate` — with freeform description of what to explore
`/investigate Should we use Terminal.Gui v2 or stay on v1?`
`/investigate How should we structure the MCP tool registration?`

## Process

### Preamble: Set Phase

```bash
python scripts/workflow-state.py set phase investigating
```

### Preamble: Ceremony Level

Ask the user: **"Quick exploration or full investigation?"**

- **Quick**: Steps 1, 3, 6, 7, 8 only (Gather, Synthesize, Present, Align, Handoff)
- **Full**: All 8 steps (Gather, Research, Synthesize, Challenge, Triage, Present, Align, Handoff)

Quick mode is appropriate for small scope changes, familiar domains, or when the human already has strong opinions. Full mode is appropriate for unfamiliar domains, high-risk architectural decisions, or when the human wants adversarial challenge.

### Step 1: Gather

Read relevant context from the codebase:

1. **Specs**: Search `specs/*.md` for overlapping scope — check file names, overview sections, and `**Code:**` frontmatter
2. **Skills**: Read any skills that will be affected by the proposal
3. **Code**: Read key source files in the affected area
4. **Retro patterns**: Read `.retros/summary.json` if it exists — check `findings_by_category` for recurring patterns relevant to this investigation. If the file doesn't exist or has invalid JSON, skip without error.
5. **Design context from args**: If `$ARGUMENTS` contains a context reference, read it

Present a brief summary of what was gathered: "Found N specs, M skills, K source files relevant to this topic."

### Step 2: Research (full mode only)

Dispatch a researcher agent for external practices and patterns:

```
Agent tool:
  subagent_type: general-purpose
  prompt: {research question with codebase context}
```

**Researcher tool restrictions** — the prompt MUST include: <!-- enforcement: T3 -->
> You may ONLY use these tools: Read, Glob, Grep, WebSearch, WebFetch.
> Do NOT use Edit, Write, Bash, or Agent. You are read-only.

**Auto-detect research need:**
- Process/architecture changes → research external practices
- Bug investigation → skip research (not useful)
- Unfamiliar domain → research patterns and prior art

The human can override: "Skip research" or "Research X specifically."

### Step 3: Synthesize

Present the investigation synthesis:

1. **Options**: 2-3 approaches with tradeoffs for each
2. **Recommendation**: Lead with the recommended option and explain why
3. **Assumptions**: Explicit list of assumptions being made
4. **Constraints and Decisions**: Numbered list of items that are settled or must be settled

Format as:
```
## Investigation Summary
### Problem: {what we're exploring}
### Options: {2-3 approaches with tradeoffs}
### Assumptions: {explicit list}
### Constraints and Decisions: {numbered list of settled items}
```

### Step 4: Challenge (full mode only)

Dispatch the challenger agent for adversarial review:

```
Agent tool:
  subagent_type: challenger
  prompt: |
    Review this proposal:

    {Investigation Summary from Step 3}

    {Constraints and Decisions section from Step 3}

    Evaluate against all 8 mandatory dimensions.
```

**CRITICAL**: Pass ONLY the Investigation Summary and Constraints/Decisions to the challenger. Do NOT pass:
- The original problem statement or user's question
- Session transcript or conversation history
- User goals or preferences
- Research findings (the challenger reviews the synthesis, not the raw research)

**Convergence rules:**
- **Max 3 rounds** — safety valve, stop after 3 rounds regardless
- **Fewer blockers each round** — round N+1 must have fewer BLOCKERs than round N to indicate convergence. If blockers increase (divergence), continue up to max 3 rounds, then stop and note "challenger diverged — escalate all findings to human"
- **Zero blockers + implementation-detail concerns only** — stop when no BLOCKERs remain and all CONCERNs are implementation-detail level (not architectural)
- **Same findings reappear** — if the challenger raises the same findings across rounds, it is looping; stop and note "challenger converged (repeated findings)"

Between rounds, address the BLOCKERs by updating the Investigation Summary and re-dispatching. Do NOT address CONCERNs or NITs between rounds — those go to triage.

### Step 5: Triage (full mode only)

Classify and route challenge findings:

- **BLOCKERs**: ALWAYS present to human at Align step — never auto-resolved <!-- enforcement: T3 -->
- **CONCERNs with factual errors** (challenger cited wrong information): AI auto-resolves with correction
- **CONCERNs with design decisions** (legitimate tradeoff the challenger flagged): Present to human at Align step
- **NITs with missing details** (challenger noted something not mentioned): AI fills in the detail
- **NITs with style/values questions** (naming, conventions, preferences): Present to human at Align step

Present triage results: "Auto-resolved N findings (M factual corrections, K detail additions). Presenting P findings for your review."

### Step 6: Present

Present the investigation results side by side:

**Left column: Investigation Summary** (from Step 3, updated after challenge rounds if applicable)
**Right column: Challenge Findings** (from Step 4, after triage in Step 5)

If quick mode: present only the Investigation Summary (no challenge findings).

### Step 7: Align

Ask the human for a decision:

1. **Go** — proceed with the recommended approach
2. **Change X** — modify a specific aspect and re-evaluate (loops back to relevant step)
3. **Not worth it** — abandon the investigation

Wait for the human's response. Do not proceed until alignment is reached.

### Step 8: Handoff

Present the investigation context summary that will be passed to `/start`:

```markdown
# Design Context: {topic}

**Source:** /investigate session on {date}
**Validated:** {challenge round summary, or "Quick mode — no adversarial challenge"}

## Problem Statement
{what we're exploring and why}

## Scope
{deliverables with brief descriptions}

## Constraints and Decisions
{numbered list of settled items from the investigation}

## Known Concerns
{items needing spec-level answers — from challenge findings marked for human review}

## Evidence
{key data points that informed the investigation}
```

Detect current context and route accordingly:

**If on main or master:**
> Run `/start` to create a worktree. The investigation context will be passed from
> this conversation to `/start`, which writes it to `.plans/context.md`.
> Then run `/design` to continue.

**If already in a worktree (feature branch):**
> Write the investigation context content to `.plans/context.md` in the current
> worktree directory. Then instruct: "Context written to `.plans/context.md`.
> Run `/design` to continue."

The investigation context content stays in conversation memory — `/start` will write it to the worktree's `.plans/context.md` when invoked in the same conversation.

## Rules

1. **Never headless** — `/investigate` is always interactive, never a pipeline stage
2. **Ceremony is the human's call** — within a chosen level, do not skip steps to save tokens
3. **Challenger isolation** — only summary + constraints, never raw conversation context
4. **Gather includes retro patterns** — always check `.retros/summary.json` for recurring issues
5. **Research is read-only** — researcher agent may not edit, write, or execute commands
6. **Convergence is mandatory** — do not skip challenge rounds if blockers exist (up to max 3)
7. **BLOCKERs always to human** — never auto-resolve a BLOCKER finding
8. **Handoff is conversation-based** — investigation context lives in conversation memory, written by `/start`
