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

## §9 - Step 3.B.2 Scope-Conformance Review Protocol

Runs after the bias-isolated design-fidelity review (Step 3.B). Goal: verify the spec faithfully covers the issue body — no scope drops, no reframings. This reviewer sees the issue body (non-bias-isolated by design; separate contract from Step 3.B).

### 1. Get Issue Number

```bash
python scripts/workflow-state.py get issues
```

Result is a JSON array, e.g. `[1113]`. Skip Step 3.B.2 entirely if the output is empty string (key absent), `null`, or `[]`. If the array has more than one entry, concatenate all bodies under separate `## Issue #N` headings and pass the combined text as `<ISSUE_BODY>` in a single reviewer call.

### 2. Fetch Issue Body

For each issue N in the array:

```bash
gh issue view <N> --json title,body --jq '"# Issue #\(.) — " + .title + "\n\n" + .body'
```

### 3. Spawn the Reviewer

Read the current spec file content. Then use the Agent tool with this prompt (substitute `<ISSUE_BODY>` and `<SPEC_CONTENT>` with the actual text):

---

You are a scope-conformance reviewer. Your ONLY job: verify that the spec covers the issue body faithfully — no scope drops, no reframings.

**Issue Body**

```
<ISSUE_BODY>
```

**Spec Content**

```
<SPEC_CONTENT>
```

**Mandate**

1. Extract every scope item, acceptance criterion, and requirement from the issue body. Include: explicit ACs/checkboxes, bulleted requirements, MUST/SHALL statements, named schemas/fields, and items listed in "Out of scope" (to verify they appear in spec Non-Goals).
2. For each extracted item, identify the spec AC or Non-Goals entry that covers it.
3. Flag any item where the spec rewrites or reframes the issue's intent (issue says X, spec does Y — even if spec's Y is technically valid).
4. Output a Markdown table in exactly this format:

| Issue Item | Spec Coverage | Status |
|-----------|---------------|--------|
| \<verbatim issue text, truncated to 80 chars\> | AC-NN or Non-Goals §N or "none" | covered / missing / reframed / in-non-goals |

5. After the table, output a one-line summary:
   `Covered: N, Missing: N, Reframed: N, In-Non-Goals: N`

Do not fix anything. Do not suggest improvements. Enumerate and classify only.

---

### 4. Handle Findings

**If Missing > 0 or Reframed > 0:**
1. Present the findings table and summary to the user.
2. Block — do not proceed to Step 3.C.
3. For each `missing` item: worker must add a spec AC that covers it.
4. For each `reframed` item: worker must either (a) align the spec with the issue's original intent, or (b) add the item to `### Non-Goals` with a rationale explaining why the reframing is acceptable.
5. After revisions: re-run Step 3.B.2 only (not Step 3.B) — re-read the updated spec and re-spawn the reviewer.
6. Repeat until all items are `covered` or `in-non-goals`.

**If Missing = 0 and Reframed = 0:** all items are covered or explicitly out-of-scope. Proceed to Step 3.C.

After completion (pass or after all items resolved), restore phase:
```bash
python scripts/workflow-state.py set phase design
```
