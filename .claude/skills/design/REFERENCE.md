# Design — Reference

## §1 - When to Use

Use `/design` for:
- "I have an idea for..." / "Let's design..." / "We need to figure out how to..."
- Starting any new feature or non-trivial change

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
| Running on main | Work on a feature branch (a worktree keeps parallel sessions isolated). |
| Skipping spec search | Always search existing specs before creating new ones. |

## §9 - Step 3.B.2 Scope-Conformance Review Protocol

Runs after the bias-isolated design-fidelity review (Step 3.B). Goal: verify the spec faithfully covers the issue body — no scope drops, no reframings. This reviewer sees the issue body (non-bias-isolated by design; separate contract from Step 3.B).

### 1. Get Issue Number

Determine the linked issue number(s) from the branch name, the design context, or by asking the user. Skip Step 3.B.2 entirely if the design has no linked issue. If there is more than one issue, concatenate all bodies under separate `## Issue #N` headings and pass the combined text as `<ISSUE_BODY>` in a single reviewer call.

### 2. Fetch Issue Body

For each issue N in the array:

```bash
gh issue view <N> --json title,body --template '# {{.title}}\n\n{{.body}}'
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
5. After revisions: re-run Step 3.B.2 (and re-run Step 3.B if changes are substantial) — re-read the updated spec and re-spawn the reviewer.
6. Repeat until all items are `covered` or `in-non-goals`.

**If Missing = 0 and Reframed = 0:** all items are covered or explicitly out-of-scope. Proceed to Step 3.C.
