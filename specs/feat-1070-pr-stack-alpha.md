# PR-Stack Envelope — /design Plan Decomposition (Alpha)

**Status:** Draft
**Last Updated:** 2026-05-16
**Code:** [scripts/pr_stack.py](../scripts/pr_stack.py) | [.claude/skills/design/](../.claude/skills/design/)
**Surfaces:** N/A (workflow tooling)
**Verification:** `python -m pytest tests/scripts/test_pr_stack.py -q`
**Verification Max Iterations:** 5

---

## Overview

When `/design` produces a plan with phases that can ship as independent PRs, the operator may optionally decompose the plan into a PR stack. `/design` then (a) writes a `## PR Stack` section into the parent plan file and (b) emits a validated JSON sidecar at `.plans/<date>-<spec-name>-stack.json`. The JSON sidecar is the **canonical envelope schema** for two consumers: this alpha feature (PR-stack decomposition) and issue #1069 (supervisor pattern + `pipeline.py --stack`). See §Forward Compatibility.

The alpha scope is strictly **plan-step decomposition + dual-artifact emission** — the downstream consumer (`pipeline.py --stack`, supervisor spawning) is Phase 3 and is explicitly out of scope here.

### Goals

- **Canonical envelope schema**: define the single JSON contract that both PR-stack (alpha) and goal-supervisor (#1069) will use without requiring a schema change on either side
- **Dual artifact**: the plan file gets a human-readable `## PR Stack` section; the JSON sidecar is machine-validatable
- **Plan decomposition**: `/design` can split a multi-phase plan into N independent sub-plans with an explicit user opt-in
- **Pure-Python validator**: `scripts/pr_stack.py` validates and writes envelopes; unit-testable without a running AI session

### Non-Goals

- `pipeline.py --stack` mode or supervisor spawning (Phase 3 — tracked separately under epic #1066; depends on #1069)
- Automatic decomposition without user confirmation
- Cross-worktree or cross-repo PR stacks
- Goal-loop integration per stack entry (separate concern)

---

## Architecture

```
/design Step 4 (Write Plan)
    │
    ├─ A. Write plan (.plans/<date>-<name>.md)
    ├─ B. Review plan (/review)
    ├─ C. Present to user → approval
    └─ D. Decomposition check (are any phases independently shippable?)
           │
           ├─ No / user declines ──→ original plan, no envelope (existing behavior)
           └─ User accepts → Step 4.E
                  │
                  ├─ Write N sub-plans (.plans/<date>-<name>-pr<N>.md)
                  ├─ Append ## PR Stack section to parent plan file
                  └─ Validate + write JSON sidecar (.plans/<date>-<name>-stack.json)
                                    │
                                    └─ scripts/pr_stack.py validate
```

### Components

| Component | Responsibility |
|-----------|----------------|
| `.claude/skills/design/SKILL.md` | Gains Step 4.D (decomposition trigger) and Step 4.E (sub-plan split + dual artifact emit) |
| `scripts/pr_stack.py` | Pure-Python helper: `build_envelope`, `validate_envelope`, `write_envelope`; CLI: `python scripts/pr_stack.py validate <path>` exits 0/1 |
| `## PR Stack` section in parent plan | Human-readable table in the plan file; written by Claude in Step 4.E |
| `.plans/<date>-<name>-stack.json` | Machine-validatable JSON sidecar (gitignored, worktree-local) |
| `tests/scripts/test_pr_stack.py` | Pytest suite mapping every AC to a behavioral test |

### Dependencies

- Uses patterns from: [goal-driven-implement.md](./goal-driven-implement.md) — stdlib-only helper convention from `goal_loop.py`
- Phase 2 consumer: [dispatch-routing.md](./dispatch-routing.md) — `BlockedSessionError`/`needs` semantics that the supervisor will use

---

## Specification

### Core Requirements

1. **Decomposition trigger.** After plan approval in Step 4.C, `/design` evaluates whether any phases can ship as independent PRs. Heuristic: each candidate phase maps to a disjoint set of spec ACs AND the phases do not share the primary modified files. The AI applies this heuristic. If no phases are independently shippable, the trigger does not fire.

2. **User opt-in.** When the trigger fires, `/design` asks exactly one yes/no question. Declining produces no envelope and leaves the original plan unchanged. Accepting proceeds to Step 4.E.

3. **Dual artifact emission.** When the user accepts:
   - The parent plan file gains a `## PR Stack` section (see §PR Stack Section Format) listing all stack entries in a human-readable table.
   - A JSON sidecar is written at `.plans/<date>-<spec-name>-stack.json` and validated via `python scripts/pr_stack.py validate`.
   - N sub-plan files are written at `.plans/<date>-<spec-name>-pr<N>.md` (1-based), each covering only the phases/ACs assigned to it. The original full plan file is retained.

4. **Single-entry stacks are allowed with justification.** A single-entry stack represents a decision that decomposition was evaluated but deemed unnecessary. The envelope-level `justification` field (non-empty string) is required when `stack` has exactly 1 entry; it is optional (but permitted) when `stack` has ≥2 entries. The `justification` text forces the author to record why full decomposition was not done.

5. **Envelope schema v1.0.** The JSON contract shared between PR-stack alpha and goal-supervisor #1069. See §Envelope Schema and §Forward Compatibility.

6. **Validation before write.** `pr_stack.py:validate_envelope()` enforces schema validity and raises `ValueError` on any violation. `write_envelope()` calls `validate_envelope()` before opening the file; no partial file is created on validation failure.

7. **No new Python dependencies.** `pr_stack.py` uses stdlib only (`json`, `pathlib`, `datetime`, `typing`, `collections`). No additions to `pyproject.toml`.

8. **Stdout discipline (Constitution I1).** `pr_stack.py` writes progress/error messages to stderr. The `validate` subcommand writes nothing to stdout on success; prints the `ValueError` message to stderr and exits 1 on failure.

### Envelope Schema

```json
{
  "schema_version": "1.0",
  "spec": "specs/<name>.md",
  "created_at": "<ISO-8601>",
  "justification": "<why single-entry or optional note>",
  "stack": [
    {
      "id": "<slug>",
      "title": "<conventional-commit style title>",
      "branch_suffix": "<short-suffix-no-slashes>",
      "plan": ".plans/<date>-<name>-pr<N>.md",
      "files": ["src/path/to/file.py", "tests/path/to/test_file.py"],
      "size_estimate": "~150 LOC",
      "depends_on": [],
      "ac_refs": ["AC-01", "AC-02"],
      "phase_label": "<Phase N: Description>"
    }
  ]
}
```

**Required envelope fields:** `schema_version`, `spec`, `created_at`, `stack`

**Conditional envelope field:** `justification` — required (non-empty) when `len(stack) == 1`; optional when `len(stack) >= 2`

**Required stack entry fields:** `id`, `title`, `branch_suffix`, `plan`, `files`, `size_estimate`, `depends_on`, `ac_refs`

**Optional stack entry field:** `phase_label`

`files` is a non-empty list of primary file paths touched by this PR (relative to repo root). `size_estimate` is a human-readable string (e.g., `"~150 LOC"`, `"~200 LOC net"`) used for MAX_LOC gate review in #988.

Phase 2 (#1069) must check `schema_version.startswith("1.")` to accept any v1.x without changes — additive fields are allowed; breaking changes increment the major version.

### PR Stack Section Format

The `## PR Stack` section appended to the parent plan file is a markdown table:

```markdown
## PR Stack

| # | ID | Branch Suffix | Title | Files | Size | Depends On | ACs |
|---|----|--------------|-------|-------|------|------------|-----|
| 1 | pr-1 | `pr1` | feat(name): phase 1 | `src/a.py` | ~80 LOC | — | AC-01, AC-02 |
| 2 | pr-2 | `pr2` | feat(name): phase 2 | `src/b.py` | ~120 LOC | pr-1 | AC-03, AC-04 |
```

Multi-file entries list the primary file in the table cell; the full list is in the JSON sidecar `files` array. The section is appended at the end of the plan file after all phase content.

### Primary Flows

**No decomposable phases:**
1. Trigger does not fire.
2. `/design` proceeds to Step 5 commit as before. No `## PR Stack` section, no JSON sidecar.

**Decomposable phases, user declines:**
1. Trigger fires.
2. Claude asks: "Would you like to decompose this into a PR stack? (yes/no)"
3. User says no → original plan unchanged, no `## PR Stack` section, no sub-plans, no JSON sidecar.
4. `/design` proceeds to Step 5 commit as before.

**Decomposable phases, user accepts:**
1. Trigger fires.
2. User says yes.
3. Claude writes N sub-plan files at `.plans/<date>-<spec-name>-pr<N>.md`.
4. Claude appends `## PR Stack` section to the parent plan file.
5. Claude writes the JSON sidecar to `.plans/<date>-<spec-name>-stack.json`, then calls:
   ```
   python scripts/pr_stack.py validate .plans/<date>-<spec-name>-stack.json
   ```
   On failure: Claude fixes the JSON and retries until exit 0.
6. Claude confirms: "Stack envelope written to `.plans/<date>-<spec-name>-stack.json`."
7. `/design` proceeds to Step 5 commit (spec only — plans and sidecar are gitignored).

**Single-entry stack (decomposition attempted, one PR only):**
1. Trigger fires but only one independently-shippable phase is identified (or all phases are tightly coupled except one extracted foundation).
2. User accepts.
3. Claude writes the single sub-plan + `## PR Stack` section + JSON sidecar with `justification` explaining why a full decomposition wasn't possible.

### Surface-Specific Behavior

This feature is workflow tooling only (N/A for CLI, TUI, Extension, MCP surfaces).

### Constraints

- `pr_stack.py` has zero runtime dependencies beyond stdlib.
- The JSON sidecar is worktree-local (`.plans/` is gitignored). It is NOT committed.
- `depends_on` must form a DAG (no cycles). `validate_envelope` enforces this via topological sort.
- `branch_suffix` must contain no forward slashes (it is appended to the parent branch name by the Phase 2 consumer).
- `files` must be a non-empty list (at least one file per PR entry).
- When `stack` has exactly 1 entry, `justification` is required and must be non-empty.

### Validation Rules

| Field | Rule | Error |
|-------|------|-------|
| `schema_version` | Must start with `"1."` (major version 1; additive minor bumps accepted) | `ValueError: expected schema_version major 1, got ...` |
| `spec` | Non-empty string | `ValueError: spec must be a non-empty string` |
| `created_at` | Non-empty string | `ValueError: created_at must be a non-empty string` |
| `stack` | Non-empty list | `ValueError: stack must be a non-empty list` |
| `justification` | Required non-empty string when `len(stack) == 1` | `ValueError: justification required for single-entry stack` |
| entry `id` | Non-empty string, unique within stack | `ValueError: stack entry id must be unique: ...` |
| entry `title` | Non-empty string | `ValueError: entry 'id' title must be non-empty` |
| entry `branch_suffix` | Non-empty string, no `/` | `ValueError: entry 'id' branch_suffix must not contain slashes` |
| entry `plan` | Non-empty string | `ValueError: entry 'id' plan must be non-empty` |
| entry `files` | Non-empty list of strings | `ValueError: entry 'id' files must be a non-empty list` |
| entry `size_estimate` | Non-empty string | `ValueError: entry 'id' size_estimate must be non-empty` |
| entry `depends_on` | List of valid `id` values in stack | `ValueError: entry 'id' depends_on references unknown id: ...` |
| `depends_on` DAG | No cycles | `ValueError: circular dependency detected involving: ...` |
| entry `ac_refs` | List (may be empty) | `ValueError: entry 'id' ac_refs must be a list` |
| entry `phase_label` | If present, non-empty string | `ValueError: entry 'id' phase_label must be non-empty if present` |

---

## Forward Compatibility

This spec defines the **canonical PR-stack envelope schema**. It is shared between two consumers:

| Consumer | How it uses the envelope |
|----------|--------------------------|
| `/design` alpha (this spec) | Writes the envelope during plan decomposition |
| `pipeline.py --stack` + goal-supervisor (#1069) | Reads the envelope to spawn per-PR worker sessions |

Issue #1069 specifies its goal-supervisor needs. Any fields #1069 adds must be additive (no existing fields removed or retyped); the minor version bumps from `1.0` to `1.1` when that happens. The major version stays at 1 until a breaking change is required.

**Fields added by #1069 (anticipated, not yet specified):**

The supervisor in #1069 will likely need to track per-entry runtime state (session IDs, PR URLs, completion status). These will be added as optional fields at the entry level (e.g., `session_id`, `pr_url`, `state`). The alpha validator will continue to pass since `validate_envelope` only checks required fields; unknown optional fields are ignored.

**Linking:** Issue #1069's spec must reference `specs/feat-1070-pr-stack-alpha.md` as the authoritative schema source. If #1069 needs to extend the schema, it amends this spec (SL2 — specs are living documents) rather than defining a parallel schema.

---

## Acceptance Criteria

| ID | Criterion | Test | Status |
|----|-----------|------|--------|
| AC-01 | `build_envelope(spec, entries)` returns a dict with `schema_version="1.0"`, `spec`, `created_at`, and `stack` keys | `TestPrStack.test_build_envelope_returns_required_keys` | ✅ |
| AC-02 | `build_envelope` raises `ValueError` when `entries` list has 1 item and no `justification` kwarg is provided | `TestPrStack.test_build_envelope_single_entry_requires_justification` | ✅ |
| AC-03 | `validate_envelope` raises `ValueError` with a descriptive message when any required stack-entry field (`id`, `title`, `branch_suffix`, `plan`, `files`, `size_estimate`, `depends_on`, `ac_refs`) is absent | `TestPrStack.test_validate_envelope_missing_required_field` | ✅ |
| AC-04 | `validate_envelope` raises `ValueError` when `depends_on` references an `id` not present in the stack | `TestPrStack.test_validate_envelope_unknown_depends_on` | ✅ |
| AC-05 | `validate_envelope` raises `ValueError` containing the word "circular" when a dependency cycle exists | `TestPrStack.test_validate_envelope_circular_dependency` | ✅ |
| AC-06 | `write_envelope` writes valid JSON with 2-space indentation and a trailing newline to the given path | `TestPrStack.test_write_envelope_writes_json` | ✅ |
| AC-07 | `write_envelope` raises `ValueError` (via `validate_envelope`) without writing any bytes when the envelope is invalid | `TestPrStack.test_write_envelope_does_not_write_on_invalid` | ✅ |
| AC-08 | `.claude/skills/design/SKILL.md` documents Step 4.D: decomposition trigger fires when any phases are independently shippable | `TestPrStackSkill.test_design_skill_documents_step_4d` | ✅ |
| AC-09 | `.claude/skills/design/SKILL.md` documents Step 4.E: writes sub-plans, appends `## PR Stack` section to parent plan, AND calls `scripts/pr_stack.py` to validate + write JSON sidecar | `TestPrStackSkill.test_design_skill_documents_step_4e` | ✅ |
| AC-10 | `.claude/skills/design/SKILL.md` specifies the JSON sidecar output path pattern as `-stack.json` within `.plans/` | `TestPrStackSkill.test_design_skill_specifies_stack_json_path` | ✅ |
| AC-11 | `.claude/skills/design/SKILL.md` specifies that declining decomposition leaves the original plan unchanged with no artifacts written | `TestPrStackSkill.test_design_skill_documents_decline_path` | ✅ |
| AC-12 | `build_envelope` with a single-entry list AND a non-empty `justification` kwarg returns a valid envelope with `justification` and a 1-entry `stack` | `TestPrStack.test_build_envelope_single_entry_with_justification` | ✅ |
| AC-13 | `validate_envelope` raises `ValueError` containing "justification" when `stack` has 1 entry and `justification` is absent or empty | `TestPrStack.test_validate_envelope_single_entry_no_justification` | ✅ |
| AC-14 | `.claude/skills/design/SKILL.md` specifies that the `## PR Stack` section in the parent plan includes `files` and `size_estimate` columns | `TestPrStackSkill.test_design_skill_pr_stack_section_has_files_and_size` | ✅ |

Status key: ✅ covered by passing test · ⚠️ test exists but failing · ❌ no test yet · 🔲 not yet implemented

### Edge Cases

| Scenario | Input | Expected Output |
|----------|-------|-----------------|
| No independently shippable phases | Single tightly coupled plan | Trigger does not fire; no artifacts |
| User declines | Trigger fires, user says No | No sub-plans, no `## PR Stack` section, no JSON sidecar |
| Single-entry stack + justification | 1 entry + non-empty justification | Valid envelope, passes validation |
| Single-entry stack, no justification | 1 entry, justification absent | `ValueError: justification required for single-entry stack` |
| Single-entry stack, empty justification | 1 entry, `justification: ""` | `ValueError: justification required for single-entry stack` |
| All entries `depends_on: []` | Valid parallel stack | Passes validation (all PRs can land in any order) |
| `files` is empty list | `"files": []` | `ValueError: entry 'id' files must be a non-empty list` |
| Direct cycle | pr-1 depends_on pr-2, pr-2 depends_on pr-1 | `ValueError` containing "circular" |
| Long chain cycle | pr-1→pr-2→pr-3→pr-1 | `ValueError` containing "circular" |
| `branch_suffix` with slash | `"pr1/alpha"` | `ValueError: branch_suffix must not contain slashes` |
| Duplicate `id` | Two entries with same id | `ValueError: stack entry id must be unique` |
| Unknown optional field | Extra key in envelope or entry | Passes validation (additive fields allowed) |

### Test Examples

```python
# AC-01: build_envelope returns required keys
def test_build_envelope_returns_required_keys():
    entries = [
        {"id": "pr-1", "title": "feat: phase 1", "branch_suffix": "pr1",
         "plan": ".plans/2026-05-15-foo-pr1.md", "files": ["src/a.py"],
         "size_estimate": "~80 LOC", "depends_on": [], "ac_refs": ["AC-01"]},
        {"id": "pr-2", "title": "feat: phase 2", "branch_suffix": "pr2",
         "plan": ".plans/2026-05-15-foo-pr2.md", "files": ["src/b.py"],
         "size_estimate": "~120 LOC", "depends_on": ["pr-1"], "ac_refs": ["AC-02"]},
    ]
    envelope = pr_stack.build_envelope("specs/foo.md", entries)
    assert envelope["schema_version"] == "1.0"
    assert envelope["spec"] == "specs/foo.md"
    assert "created_at" in envelope
    assert len(envelope["stack"]) == 2
```

```python
# AC-02 / AC-12: single-entry requires justification
def test_build_envelope_single_entry_requires_justification():
    entry = {"id": "pr-1", "title": "feat: all phases", "branch_suffix": "pr1",
             "plan": ".plans/foo-pr1.md", "files": ["src/a.py"],
             "size_estimate": "~200 LOC", "depends_on": [], "ac_refs": []}
    with pytest.raises(ValueError, match="justification"):
        pr_stack.build_envelope("specs/foo.md", [entry])

def test_build_envelope_single_entry_with_justification():
    entry = {"id": "pr-1", "title": "feat: all phases", "branch_suffix": "pr1",
             "plan": ".plans/foo-pr1.md", "files": ["src/a.py"],
             "size_estimate": "~200 LOC", "depends_on": [], "ac_refs": []}
    envelope = pr_stack.build_envelope("specs/foo.md", [entry], justification="phases share migrations")
    assert envelope["justification"] == "phases share migrations"
    assert len(envelope["stack"]) == 1
```

```python
# AC-05: circular dependency detected
def test_validate_envelope_circular_dependency():
    envelope = {
        "schema_version": "1.0", "spec": "specs/foo.md", "created_at": "2026-05-15T00:00:00Z",
        "stack": [
            {"id": "pr-1", "title": "t1", "branch_suffix": "pr1", "plan": "p1.md",
             "files": ["a.py"], "size_estimate": "~10 LOC", "depends_on": ["pr-2"], "ac_refs": []},
            {"id": "pr-2", "title": "t2", "branch_suffix": "pr2", "plan": "p2.md",
             "files": ["b.py"], "size_estimate": "~10 LOC", "depends_on": ["pr-1"], "ac_refs": []},
        ]
    }
    with pytest.raises(ValueError, match="circular"):
        pr_stack.validate_envelope(envelope)
```

```python
# AC-07: write_envelope does not write partial file on invalid envelope
def test_write_envelope_does_not_write_on_invalid(tmp_path):
    invalid = {"schema_version": "1.0", "spec": "", "created_at": "x", "stack": []}
    out = tmp_path / "stack.json"
    with pytest.raises(ValueError):
        pr_stack.write_envelope(invalid, out)
    assert not out.exists()
```

---

## Core Types

### Envelope dict

```python
# Illustrative shape
envelope: dict = {
    "schema_version": "1.0",       # str — must start with "1."
    "spec": "specs/foo.md",         # str, non-empty
    "created_at": "ISO-8601",       # str, non-empty
    "justification": "...",         # str — required when len(stack)==1
    "stack": [stack_entry, ...]     # list[dict], non-empty
}

stack_entry: dict = {
    "id": "pr-1",                   # str, unique, non-empty
    "title": "feat: ...",           # str, non-empty
    "branch_suffix": "pr1",         # str, no slashes
    "plan": ".plans/...-pr1.md",    # str, non-empty
    "files": ["src/a.py"],          # list[str], non-empty
    "size_estimate": "~150 LOC",    # str, non-empty
    "depends_on": [],               # list[str] — valid ids in stack
    "ac_refs": ["AC-01"],           # list[str], may be empty
    "phase_label": "Phase 1: ...",  # str, optional
}
```

### Public API

```python
def build_envelope(
    spec: str,
    entries: list[dict],
    *,
    justification: str | None = None,
) -> dict:
    """Construct and return a validated envelope dict.
    Raises ValueError if entries == 1 and justification is absent/empty."""

def validate_envelope(envelope: dict) -> None:
    """Raise ValueError if the envelope fails any validation rule."""

def write_envelope(envelope: dict, path: Path | str) -> None:
    """Validate then write JSON with 2-space indent + trailing newline.
    Raises ValueError (via validate_envelope) before any file I/O."""
```

### CLI

```
python scripts/pr_stack.py validate <path>
```

Exits 0 on valid JSON that passes `validate_envelope`. Exits 1 and prints the `ValueError` message to stderr on any failure. No stdout output (Constitution I1).

---

## Error Handling

### Error Types

| Error | Condition | Recovery |
|-------|-----------|----------|
| `ValueError` | Schema validation failure | Fix the envelope dict and retry |
| `json.JSONDecodeError` | Path contains invalid JSON | Regenerate the JSON |
| `OSError` | Cannot write to path | Check disk space / permissions |

### Recovery Strategies

- **Validation failure in `/design`**: Claude fixes the JSON sidecar and retries `python scripts/pr_stack.py validate` before confirming to the user.
- **User changes their mind after accept**: Operator deletes the sub-plans, `## PR Stack` section (edit the plan), and JSON sidecar manually — all in `.plans/`, gitignored.

---

## Design Decisions

### Why emit both `## PR Stack` section AND JSON sidecar?

**Context:** Issue #1070 body mandates a `## PR Stack` section in the plan template. A JSON sidecar is machine-validatable and provides the Phase 2 consumer (#1069) with a structured contract. Neither alone satisfies both requirements.

**Decision:** Both. The markdown section satisfies the issue body literally and remains human-readable in the plan; the JSON sidecar is the canonical machine-readable contract. They are kept in sync by the `/design` Step 4.E process (both are written at the same time from the same source data).

**Alternatives considered:**
- JSON sidecar only: does not satisfy the issue body's `## PR Stack` section mandate
- Markdown section only: not machine-validatable; Phase 2 (#1069) would need a markdown parser

**Consequences:**
- Positive: satisfies both human-readable (plan review) and machine-readable (Phase 2) requirements
- Negative: two representations of the same data; they can drift if the plan is edited by hand after Step 4.E. The validator is the source of truth; the markdown section is informational.

### Why `justification` instead of rejecting single-entry stacks?

**Context:** A single-entry stack represents a case where decomposition was evaluated and declined for a specific reason (e.g., phases share a DB migration, or a shared public API surface that can't be split without a breaking change). Rejecting it silently forces the author to either lie (split it artificially) or skip the envelope entirely.

**Decision:** Allow single-entry stacks with a required non-empty `justification` field. The requirement forces the author to articulate the reason; the validator enforces it. Phase 2 (#1069) can use the justification field to annotate its supervisor session.

**Alternatives considered:**
- Always reject single-entry: forces artificial splits or suppresses honest single-PR stacks
- Allow without justification: loses the forcing function — authors may forget to think about it

**Consequences:**
- Positive: captures honest single-PR decisions; `justification` is visible in the plan's `## PR Stack` section
- Negative: AC-02 becomes a conditional check; the test must cover both the error and the happy path

### Why `files` and `size_estimate` as required fields?

**Context:** Issue #988 MAX_LOC gating depends on `size_estimate` per stack entry. Reviewer audit (for Phase 2 supervisor assignment) needs `files` to know which files each PR touches without reading the sub-plan.

**Decision:** Both required. `files` is a non-empty list (at minimum the primary file); `size_estimate` is a human string (not an integer) to remain flexible as estimation conventions evolve.

**Alternatives considered:**
- Optional fields: silently absent values would cause Phase 2 to fail or misroute
- Integer `loc_estimate` instead of string: brittle; `"~150 LOC net"` is more expressive and matches existing reviewer conventions

**Consequences:**
- Positive: Phase 2 can gate on `size_estimate` and assign reviewers by `files` without reading sub-plans
- Negative: `files` list may go stale if the implementation changes files after `/design`; the list is a planning estimate, not a guarantee

### Why opt-in decomposition?

**Context:** The trigger heuristic (disjoint ACs + separate primary files) is a signal, not a guarantee. Tight coupling may exist even with disjoint ACs.

**Decision:** Opt-in with a single yes/no question. The user can override the AI's judgment in either direction.

**Consequences:**
- Positive: no false stacks for tightly coupled plans
- Negative: the AI must exercise judgment, which may vary between sessions

### Why `schema_version = "1.0"` validated as `startswith("1.")`?

**Context:** Phase 2 (#1069) extends the schema with optional fields. The validator must not reject forward-compatible additions.

**Decision:** `startswith("1.")` not equality. Major version 2 would be a breaking change and is handled by the caller.

**Consequences:**
- Positive: Phase 2 can extend with `"1.1"` fields without breaking this validator
- Negative: requires discipline; there is no enforced registry of minor bumps

### Why is the JSON sidecar NOT committed?

**Context:** `.plans/` is gitignored; plans are ephemeral workflow artifacts.

**Decision:** Sidecar lives in `.plans/`. Phase 2 runs in the same worktree.

**Consequences:**
- Positive: consistent with plan conventions; no git ceremony
- Negative: sidecar is lost when the worktree is deleted; operator must not prune the worktree between Phase 1 design and Phase 2 (#1069) run

---

## Related Specs

- [goal-driven-implement.md](./goal-driven-implement.md) — stdlib-only helper pattern
- [dispatch-routing.md](./dispatch-routing.md) — BlockedSessionError and supervisor crash resilience options Phase 2 (#1069) must resolve

---

## Changelog

| Date | Change |
|------|--------|
| 2026-05-16 | Rev 2 — add dual artifact (markdown + JSON), `files`/`size_estimate` fields, `justification` for single-entry stacks, Forward Compatibility section |
| 2026-05-15 | Initial spec — alpha scope only |

---

## Roadmap

- **Phase 2 (#1069)**: `pipeline.py --stack` mode — supervisor spawning that consumes this envelope; extends schema to `"1.1"` with optional runtime-state fields
- **Phase 3**: end-to-end integration test of the full stack pipeline
