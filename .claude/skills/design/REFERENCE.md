# Design — Reference

## §1 - When to Use

Use `/design` for:
- "I have an idea for..." / "Let's design..." / "We need to figure out how to..."
- Starting any new feature or non-trivial change
- After `/investigate` produces a design context

Do NOT use for bug fixes (code + test + `/gates` + `/verify` + `/pr`) or for enhancements with an existing spec that just need `/implement`.

## §2 - Key Principles

- **One question at a time** — don't overwhelm
- **Multiple choice preferred** — easier to answer than open-ended
- **YAGNI ruthlessly** — remove unnecessary features
- **Explore alternatives** — always propose 2-3 approaches before settling
- **Incremental validation** — present design, get approval, then proceed
- **Constitution compliance** — every design must comply with the constitution
- **Review before presenting** — specs and plans go through /review before the user sees them
- **Do NOT use plan mode** — /design has its own approval gates. Exit plan mode before running /design.

## §3 - Step 1 Constraint Checking

Before presenting the architecture (Step 2 or Step 3), verify the proposal against each Constraint and each Known Concern in `context.md`. Flag conflicts — e.g., "Constraint #3 says X, but the proposed architecture does Y."

## §4 - Step 2 Present Design Detail

Present in sections, scaled to complexity. Ask after each section: "Does this look right?" Cover: architecture, components, data flow, error handling, testing. Check against constitution principles — flag any tensions.

## §5 - Step 6 Historical Note

This Step 6 used to unconditionally run `workflow-state.py set phase implementing`, which caused issue #800 — pausing between `/design` and `/implement` triggered the session-stop hook to block with a spurious "gates/verify/QA missing" message on a spec-only commit.

The fix: leave phase as `design` and let the downstream skill own its transition:
- `/implement` Step 3.5 sets `phase=implementing`
- `scripts/pipeline.py` sets `phase=pipeline`
- User choosing Defer: phase stays `design` (stop hook bypass phase)

## §6 - Anti-Patterns

| Pattern | Fix |
|---------|-----|
| "This is too simple for a design" | Every new feature goes through this. Bug fixes skip design entirely. Short designs are fine. |
| Jumping to implementation | Design MUST be approved before any code |
| Asking 5 questions at once | One question per message |
| Proposing only one approach | Always propose 2-3 with trade-offs |
| Skipping the spec | The spec IS the deliverable of this skill |
| Skipping the plan | The plan is the second deliverable — spec alone isn't enough |
| Using plan mode with /design | /design has its own approval gates. Exit plan mode first. |
| Running on main | /design requires a worktree. Run /start first. |
| Skipping spec search | Always search existing specs before creating new ones. |

## §7 - Step 4 PR-Stack Decomposition

See `specs/feat-1070-pr-stack-alpha.md` — that spec is the canonical envelope schema for both PR-stack alpha and the Phase 2 goal-supervisor (#1069).

**Independently-shippable heuristic** (Step 4.D):

A phase is independently shippable when its spec ACs are disjoint from other phases AND it does not share the primary modified files. Single tightly-coupled phases should not be decomposed even if ACs are disjoint (e.g., shared service class, shared DB migration, shared public API surface).

**`## PR Stack` table format** (appended to parent plan file):

```markdown
## PR Stack

| # | ID | Branch Suffix | Title | Files | Size | Depends On | ACs |
|---|----|--------------|-------|-------|------|------------|-----|
| 1 | pr-1 | `pr1` | feat(name): phase 1 | `src/a.py` | ~80 LOC | — | AC-01, AC-02 |
| 2 | pr-2 | `pr2` | feat(name): phase 2 | `src/b.py` | ~120 LOC | pr-1 | AC-03, AC-04 |
```

For multi-file entries, list the primary file in the table cell; the full list goes in the JSON sidecar `files` array. The `Size` column carries the `size_estimate` value (e.g., `~150 LOC`).

**Single-entry stacks:** allowed but require a non-empty `justification` field in the envelope explaining why full decomposition was not possible (e.g., "phases share a DB migration"). Record the justification text below the `## PR Stack` table in the parent plan.

**User decline path:** if the user says no to the Step 4.D question, leave the original plan unchanged, write no sub-plans, write no `## PR Stack` section, write no JSON sidecar. Proceed to Step 5 as if 4.D never fired.

**Envelope schema source of truth:** `specs/feat-1070-pr-stack-alpha.md`. Phase 2 (#1069) must extend this spec with additive fields and bump `schema_version` from `1.0` to `1.1`, not define a parallel schema.

## §8 - Supervisor Inbox Protocol (Step 3.C)

At Step 3.C, before presenting the spec to the user, poll the worker inbox:

```bash
python scripts/supervisor_msg.py read --consume
```

Process messages in the order returned (chronological). Message kinds:

| Kind | Action |
|------|--------|
| `abort` | Stop immediately. Report the abort directive (and any `message` field) to the user. Do not proceed to Step 4. |
| `revise` | Apply the feedback in the `message`/`payload` fields to the spec. Re-run spec-review (Step 3.B) if changes are substantial. Then continue to present. |
| `approve` | Skip the "Wait for approval" pause — supervisor has already approved; proceed directly to Step 4. |
| `note` | Surface the `message` field to the user as an informational note. Continue normally. |

If the inbox is empty (`[]`), proceed with the normal user-approval flow.

**Supervisor send syntax (orchestrator side):**
```bash
python scripts/supervisor_msg.py send <worktree-abs-path> <kind> [--message "text"] [--payload-file f.json]
```
