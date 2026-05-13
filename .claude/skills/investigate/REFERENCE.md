# Investigate — Reference

## §1 - Ceremony Level Guidance

**Quick mode** (Steps 1, 3, 6, 7, 8) — appropriate when: scope is small, domain is familiar, or the human has strong opinions. Skips research, challenge, and triage.

**Full mode** (all 8 steps) — appropriate when: domain is unfamiliar, decision is high-risk architectural, or the human wants adversarial challenge.

The human chooses; do not downgrade ceremony to save tokens.

## §2 - Research Agent (Step 2)

Prompt template for the researcher agent:

```
Agent tool:
  subagent_type: general-purpose
  prompt: {research question with codebase context}
```

Tool restrictions — the prompt MUST include:
> You may ONLY use these tools: Read, Glob, Grep, WebSearch, WebFetch.
> Do NOT use Edit, Write, Bash, or Agent. You are read-only.

Auto-detect research need:
- Process/architecture changes → research external practices
- Bug investigation → skip research (not useful)
- Unfamiliar domain → research patterns and prior art

The human can override: "Skip research" or "Research X specifically."

## §3 - Synthesis Format (Step 3)

Standard format for the Investigation Summary:

```
## Investigation Summary
### Problem: {what we're exploring}
### Options: {2-3 approaches with tradeoffs}
### Assumptions: {explicit list}
### Constraints and Decisions: {numbered list of settled items}
```

## §4 - Challenger Agent (Step 4)

Prompt template:

```
Agent tool:
  subagent_type: challenger
  prompt: |
    Review this proposal:

    {Investigation Summary from Step 3}

    {Constraints and Decisions section from Step 3}

    Evaluate against all 8 mandatory dimensions.
```

**CRITICAL isolation**: Pass ONLY the Investigation Summary and Constraints/Decisions to the challenger. Do NOT pass the original problem statement, session transcript, user goals, or research findings.

Convergence rules:
- **Max 3 rounds** — stop after 3 rounds regardless
- **Fewer blockers each round** — if blockers increase (divergence), continue up to max 3 rounds, then escalate all findings to human
- **Zero blockers + implementation-detail concerns only** — stop when no BLOCKERs remain
- **Same findings reappear** — if challenger loops, stop and note "challenger converged (repeated findings)"

Between rounds: address BLOCKERs by updating the Investigation Summary and re-dispatching. Do NOT address CONCERNs or NITs between rounds — those go to triage.

## §5 - Triage Classification Rules (Step 5)

- **BLOCKERs**: ALWAYS present to human at Align step — never auto-resolved
- **CONCERNs with factual errors** (challenger cited wrong information): AI auto-resolves with correction
- **CONCERNs with design decisions** (legitimate tradeoff the challenger flagged): Present to human at Align step
- **NITs with missing details** (challenger noted something not mentioned): AI fills in the detail
- **NITs with style/values questions** (naming, conventions, preferences): Present to human at Align step

## §6 - Handoff Context Document Format (Step 8)

Standard format for the investigation context passed to `/start`:

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

The investigation context content stays in conversation memory — `/start` will write it to the worktree's `.plans/context.md` when invoked in the same conversation.

## §7 - Rules Rationale

1. **Never headless** — `/investigate` is always interactive; it exists to gather the human's judgment before committing
2. **Ceremony is the human's call** — the human chose quick or full; do not downgrade within that level to save tokens
3. **Challenger isolation** — the challenger must review the synthesis (not the conversation), or its findings collapse into confirmation bias
4. **Gather includes retro patterns** — recurring patterns in `.retros/summary.json` often surface the real constraint before research begins
5. **Research is read-only** — researcher agent may not edit, write, or execute commands
6. **Convergence is mandatory** — do not skip challenge rounds if blockers exist (up to max 3)
7. **BLOCKERs always to human** — the whole point of challenge is surfacing risks; auto-resolving BLOCKERs defeats that
8. **Handoff is conversation-based** — investigation context lives in conversation memory; `/start` writes it to `.plans/context.md`
