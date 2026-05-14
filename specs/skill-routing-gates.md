# Skill Routing Gates

**Status:** Draft
**Last Updated:** 2026-05-13
**Code:** [.claude/skills/backlog/](../.claude/skills/backlog/) | [.claude/skills/investigate/](../.claude/skills/investigate/) | [.claude/skills/design/](../.claude/skills/design/) | [scripts/workflow-state.py](../scripts/workflow-state.py)
**Surfaces:** N/A

---

## Overview

Routing and readiness gates inside three skills (`/backlog`, `/investigate`, `/design`) so each skill detects when the user has invoked it at the wrong scope and offers to chain to the correct skill via the Skill tool. Each gate is T3 advisory text plus a telemetry counter so we can later measure honor rate (issue #1023).

### Goals

- **Catch scope mismatch at skill entry**: `/backlog` redirects exploratory descriptions to `/investigate`; `/investigate` offers a `/backlog` epic path when research decomposes; `/design` flags multi-concern brainstorms and proposes N separate designs.
- **Chain via Skill tool**: User-approved redirects invoke the target skill, not just suggest it verbally. Same pattern as `/design` Step 6 today.
- **Single chaining convention**: A "Skill Chaining Graph" table in this spec defines every routing relationship so future edits land here, not as ad-hoc PRs.
- **Build telemetry for T3 → T2 decision**: Gate-fire / honored / overridden counters in `.workflow/state.json` feed the investigation tracked by #1023.

### Non-Goals

- **Hook enforcement of routing**: The gates are T3 (advisory). Promoting to T2 (UserPromptSubmit hook) is deferred to issue #1023 pending telemetry.
- **Spec / plan template multi-concern checkpoints**: Issue #989 mentions templates; that is follow-up scope, not this spec.
- **`/design` Step 4 PR-stack decomposition**: Tracked by issue #988, separate scope.
- **Generic skill metadata or dispatcher pattern**: Premature without honor-rate evidence (per #1023).

---

## Architecture

```
┌──────────────────────────────────────────────────────────┐
│        User invokes skill at wrong scope                  │
│                                                            │
│  /backlog "broad strategic concept"   (needs exploration) │
│  /investigate "decomposes into N items" (needs epic path) │
│  /design "5 unrelated concerns" (needs N designs)         │
└────────────────────┬─────────────────────────────────────┘
                     ▼
        ┌──────────────────────────────┐
        │  Gate fires (detection)      │
        │  - keywords + judgment, OR   │
        │  - explicit option in flow   │
        └────────────┬─────────────────┘
                     ▼
        ┌──────────────────────────────┐
        │  Numbered options; redirect  │
        │  is option 1 when gate fires │
        └────────────┬─────────────────┘
                     ▼
        ┌──────────────────────────────┐
        │  User approves → Skill tool  │
        │  invokes target skill with   │
        │  serialized context          │
        │  User overrides → continue   │
        │  original skill flow         │
        └────────────┬─────────────────┘
                     ▼
        ┌──────────────────────────────┐
        │  Telemetry: bump counters    │
        │  under routing_gates.* in    │
        │  .workflow/state.json         │
        │  (see Telemetry table)       │
        └──────────────────────────────┘
```

### Components

| Component | Responsibility |
|-----------|----------------|
| `/backlog` Step 0 (new) | Readiness gate: redirect exploratory descriptions to `/investigate` |
| `/investigate` Step 7 option (4) (new) | Epic handoff: route decomposing investigations to `/backlog` |
| `/design` Step 2 checkpoint (new) | Multi-concern detection: route multi-feature brainstorms to `/backlog` for N issues |
| `scripts/workflow-state.py bump <key>` (new sub-command) | Increment a numeric counter in `.workflow/state.json` for telemetry |

### Dependencies

- Builds on patterns in: [workflow-enforcement.md](./workflow-enforcement.md) (T1/T2/T3 tier model)
- Relates to: [spec-governance.md](./spec-governance.md) (skill-text vs hook enforcement)

---

## Specification

### Skill Chaining Graph

This is the canonical routing table for PPDS skills. Every routing edit must update this table.

| Source skill | Expected scope                      | Chains to               | Trigger                                        | Status |
|--------------|-------------------------------------|-------------------------|-----------------------------------------------|--------|
| `/backlog`   | Concrete deliverable filing         | `/investigate`          | Description is exploratory (keywords + judgment) | **New (G1)** |
| `/backlog`   | "                                   | (terminal)              | Concrete issue creation                       | Existing |
| `/investigate` | Pre-commitment exploration        | `/start` → `/design`    | Step 7 user picks (1) Go — single deliverable | Existing |
| `/investigate` | "                                 | (terminal: Not worth it)| Step 7 user picks (3)                         | Existing |
| `/investigate` | "                                 | (loops to earlier step) | Step 7 user picks (2) Change X                | Existing |
| `/investigate` | "                                 | `/backlog` (epic)       | Step 7 user picks (4) — research decomposes   | **New (G2)** |
| `/design`    | Single cohesive feature             | `/implement` or pipeline| Spec + plan approved (Step 6)                 | Existing |
| `/design`    | "                                   | `/backlog` (N issues)   | Multi-concern brainstorm detected (Step 2)    | **New (G3)** |
| `/start`     | Worktree creation                   | `/design` or `/implement`| Worktree ready                                | Existing |

Non-routing skills (`/gates`, `/verify`, `/pr`, etc.) appear only as targets of `/implement`, never as sources. Rows marked **New (G1/G2/G3)** are introduced by this spec; rows marked Existing are documented here for context — they are not modified.

### Gate Specifications

#### G1: `/backlog` Readiness Gate (Step 0)

**Placement:** Insert a new "Step 0: Readiness Gate" in `.claude/skills/backlog/SKILL.md` before the existing "Step 1: Parse Arguments". Existing step numbers do not change (Step 1 remains Parse Arguments).

**Scope:** Fires only on `/backlog create <description>`. Skipped for `triage`, `review`, `validate`, `dispatch`, and no-arg invocations.

**Detection (hybrid):**
1. **Keyword match** — case-insensitive substring match against any of seven phrases: `broad concept`, `think out loud`, `need to figure out`, `let's explore`, `strategic`, `not sure what`, `should we`. The phrase `let's explore` is used instead of bare `explore` to avoid false positives on concrete-but-explore-themed descriptions (e.g., `/backlog create Add --explore flag to query command`).
2. **Judgment fallback** — if no keyword match, the model evaluates whether the description names a concrete deliverable with implicit acceptance criteria. If it does not, the gate fires.

**Prompt:** "This sounds like it needs exploration first. Want me to run `/investigate`? (1) Run `/investigate` with this description as input, (2) Continue with `/backlog create` as-is."

**On (1):** Invoke `/investigate` via Skill tool, passing the original description as args.
**On (2):** Continue to Step 1 (Parse Arguments) of `/backlog`.

**Telemetry:** `bump routing_gates.backlog.fired_count` whenever the gate fires; `honored_count` on (1); `overridden_count` on (2).

#### G2: `/investigate` Epic Handoff (Step 7 option 4)

**Placement:** Add a fourth option to the existing "Step 7: Align" block in `.claude/skills/investigate/SKILL.md`. Also modify Step 8 (Handoff) to branch on the new option.

**Existing Step 7 options:** (1) Go — proceed with recommended approach; (2) Change X — modify and re-evaluate; (3) Not worth it — abandon.

**New Step 7 option:** (4) File as epic — research decomposes into N implementable items, file them via `/backlog` rather than committing to a single design.

**Trigger:** User selects option (4) at Step 7. The option is always offered (not auto-detected) — it is a peer of the existing three.

**Step 8 behavior on (4):**
- Build an epic body from the investigation summary (Problem Statement + Scope sections produced earlier in the investigation).
- Each numbered item in "Constraints and Decisions" becomes a candidate child-issue title.
- Invoke `/backlog` via Skill tool with args: epic title + body, and the list of candidate children. `/backlog` files the epic first, then iterates the children (one issue per item) with the epic number as parent link.
- Step 8's existing on-main vs in-worktree routing applies only to options (1)/(2)/(3), not (4).

**Telemetry:** `bump routing_gates.investigate.epic_offered_count` whenever Step 7 renders; `bump routing_gates.investigate.epic_chosen_count` on (4).

#### G3: `/design` Multi-Concern Checkpoint (Step 2)

**Placement:** New checkpoint inside `.claude/skills/design/SKILL.md` Step 2, **after** the "Understand the idea" sub-block (clarifying questions), **before** the "Explore approaches" sub-block.

**Detection heuristic (OR logic):**
- The clarified scope contains **more than 3 distinct sub-features**, OR
- **Two or more sub-features could ship independently** (i.e. one does not require the other to be valuable).

**Prompt:** "You raised N concerns: {bulleted list}. Are these (1) one cohesive feature with a shared trigger event, or (2) N separate features that share a trigger event but ship independently?"

**On (1):** Continue Step 2 with "Explore approaches" for the single cohesive feature. The heuristic was a false positive; the user has confirmed cohesion.
**On (2):** Present recommendation: "Recommend filing N separate issues and running `/design` per issue. (a) File issues via `/backlog`, (b) Continue with this design anyway."
- On (a): invoke `/backlog` via Skill tool with N issue specs. After filing, exit `/design`; instruct user to `/start` a new worktree per issue.
- On (b): continue Step 2 with "Explore approaches" but flag in the spec that this is a deliberate multi-concern design (cross-reference issue #989 rationale).

**Telemetry:** `bump routing_gates.design.fired_count` when the heuristic trips; `bump routing_gates.design.split_count` on (2)(a) — the user split into N issues; `bump routing_gates.design.cohesion_confirmed_count` on (1) — user confirmed it is one feature; `bump routing_gates.design.proceed_anyway_count` on (2)(b) — user chose multi-concern design. Three outcome counters distinguish "false-positive heuristic" from "true multi-concern" from "deliberate override".

### Chaining Convention (Cross-Cutting)

All gates that offer a redirect follow this exact pattern:

1. Detect → present numbered options. Redirect is always option 1 when a gate fires automatically (G1, G3); option 3 when offered alongside existing options (G2).
2. On approval → invoke target skill via Skill tool with serialized context (Skill tool `args` field).
3. On override → continue the original skill's flow without further prompting.

Each gate block in the three SKILL.md files MUST include the marker:
```html
<!-- enforcement: T3 advisory — see specs/skill-routing-gates.md and issue #1023 -->
```

### Telemetry

`scripts/workflow-state.py` gains a new sub-command:

```bash
python scripts/workflow-state.py bump <dotted.key>
```

Reads `.workflow/state.json`, increments the integer at `<dotted.key>` by 1 (initializing to 1 if the key is absent), writes the file using the same `write_state` call path as existing `set`/`append`/`init`. The write is **not atomic** today (existing `write_state` opens the destination directly; no temp+rename) — `bump` does not introduce atomicity and inherits the same last-writer-wins behavior. Atomicity is out of scope for this spec; introduce it for all commands together if a race condition is observed in practice.

**Key validation:** Dotted keys must match `[A-Za-z0-9_]+(\.[A-Za-z0-9_]+)*`. This spec only uses keys under the `routing_gates.*` namespace.

Telemetry keys this spec introduces:

| Key                                                       | Increments when                                          |
|-----------------------------------------------------------|---------------------------------------------------------|
| `routing_gates.backlog.fired_count`                       | G1 fires                                                |
| `routing_gates.backlog.honored_count`                     | G1 fires, user picks (1) — redirect to `/investigate`    |
| `routing_gates.backlog.overridden_count`                  | G1 fires, user picks (2) — continue with `/backlog`      |
| `routing_gates.investigate.epic_offered_count`            | Step 7 of `/investigate` renders                        |
| `routing_gates.investigate.epic_chosen_count`             | Step 7 user picks (4) — File as epic                    |
| `routing_gates.design.fired_count`                        | G3 heuristic trips                                      |
| `routing_gates.design.cohesion_confirmed_count`           | G3 fires, user picks (1) — false-positive heuristic     |
| `routing_gates.design.split_count`                        | G3 fires, user picks (2)(a) — split to N issues         |
| `routing_gates.design.proceed_anyway_count`               | G3 fires, user picks (2)(b) — deliberate multi-concern   |

Note that G2 emits two counters (offered/chosen) rather than a fired/honored/overridden triple — its option is always offered (not detection-based), so "fired" semantics do not apply. G3 emits four counters to distinguish the three downstream outcomes from gate firing.

### Constraints

- All gate text lives in SKILL.md files; no new Python modules or hooks added in this PR.
- The `bump` sub-command is the only `workflow-state.py` change permitted by this spec.
- Skill tool invocation MUST pass serialized context (description text, investigation summary, or issue list) via `args` — not via shared state files.
- Gate text must include the `<!-- enforcement: T3 advisory -->` marker (see G1/G2/G3).

---

## Acceptance Criteria

| ID | Criterion | Test | Grep target (when grep-based) | Status |
|----|-----------|------|-------------------------------|--------|
| AC-01 | `.claude/skills/backlog/SKILL.md` contains a `### Step 0` heading whose body includes `<!-- enforcement: T3 advisory` and the literal phrase `run /investigate`, positioned before the existing `### 1. Parse Arguments` heading | `test_backlog_step0_present_and_ordered` | Assert (a) file contains regex `^### Step 0\b`, (b) the next `^### ` heading after Step 0 is `### 1. Parse Arguments`, (c) Step 0 block contains `<!-- enforcement: T3 advisory`, (d) Step 0 block contains `run /investigate` | 🔲 |
| AC-02 | `/backlog` Step 0 lists all seven trigger keyword phrases verbatim: `broad concept`, `think out loud`, `need to figure out`, `let's explore`, `strategic`, `not sure what`, `should we` | `test_backlog_step0_lists_keywords` | Assert each of the seven literal phrases (wrapped in backticks or quotes) appears verbatim inside the Step 0 block | 🔲 |
| AC-03 | `/backlog` Step 0 explicitly documents the skip-list: `triage`, `review`, `validate`, `dispatch`, and no-args invocations are not gated | `test_backlog_step0_documents_skip_list` | Assert Step 0 block contains all five literal tokens (`triage`, `review`, `validate`, `dispatch`, `no-arg` or `no arguments`) within a sentence containing `skip` or `does not apply` | 🔲 |
| AC-04 | `.claude/skills/investigate/SKILL.md` Step 7 (Align) lists four options including `(4) File as epic`, and Step 8 (Handoff) contains a branch for option (4) that invokes `/backlog`, with the T3 advisory marker present in both blocks | `test_investigate_step7_step8_epic_option` | Assert (a) Step 7 block contains literal `(4) File as epic` or `4. File as epic`, (b) Step 8 block contains both `option (4)` (or similar reference) and `/backlog`, (c) `<!-- enforcement: T3 advisory` appears in each block | 🔲 |
| AC-05 | `/investigate` epic-handoff block explicitly states that numbered items in "Constraints and Decisions" become candidate child-issue titles | `test_investigate_epic_decomposition_documented` | Assert Step 8 block contains both `Constraints and Decisions` and `child-issue` (or `child issue`) within the same paragraph | 🔲 |
| AC-06 | `.claude/skills/design/SKILL.md` Step 2 contains a `Multi-Concern Checkpoint` block positioned between the `Understand the idea` sub-block and the `Explore approaches` sub-block, with the T3 advisory marker | `test_design_step2_checkpoint_ordered` | Assert file contains, in source order: `Understand the idea`, then `Multi-Concern Checkpoint`, then `Explore approaches`. Checkpoint block contains `<!-- enforcement: T3 advisory` | 🔲 |
| AC-07 | `/design` Step 2 checkpoint documents both heuristics verbatim: (a) more than 3 sub-features, (b) two or more sub-features that could ship independently | `test_design_checkpoint_documents_heuristics` | Assert checkpoint block contains both `more than 3 sub-features` (or `> 3 distinct sub-features`) and `ship independently` | 🔲 |
| AC-08 | `scripts/workflow-state.py bump <key>` initializes the key to 1 if absent and increments by 1 on each call. Two sequential invocations on a fresh state file leave the key at value 2 | `test_workflow_state_bump_initializes_to_one`, `test_workflow_state_bump_increments` | (Behavioral test — invoke the script in a temp `.workflow/`) | 🔲 |
| AC-09 | `workflow-state.py bump <key>` errors with non-zero exit and a stderr message containing `non-integer` when the existing value at `<key>` is a string, list, or dict | `test_workflow_state_bump_rejects_non_integer` | (Behavioral test — seed state.json with a non-integer value, invoke `bump`, assert exit code ≠ 0 and stderr contains `non-integer`) | 🔲 |
| AC-10 | `workflow-state.py bump <key>` rejects keys that do not match `^[A-Za-z0-9_]+(\.[A-Za-z0-9_]+)*$` with a non-zero exit and stderr containing `invalid key` | `test_workflow_state_bump_validates_key` | (Behavioral test — invoke with key `foo bar`, assert exit code ≠ 0 and stderr contains `invalid key`) | 🔲 |
| AC-11 | Each of the three SKILL.md gate blocks invokes `python scripts/workflow-state.py bump <key>` for every counter listed in the Telemetry table for that gate (G1: 3 counters; G2: 2 counters; G3: 4 counters) | `test_skill_gates_emit_documented_telemetry` | For each of the nine telemetry keys, assert the literal string `bump routing_gates.<full.key>` appears in the SKILL.md file that owns that gate | 🔲 |
| AC-12 | This spec's "Skill Chaining Graph" table contains one row marked `**New (G1)**`, one row marked `**New (G2)**`, and one row marked `**New (G3)**` | `test_spec_chaining_graph_covers_new_gates` | Assert `specs/skill-routing-gates.md` contains all three literal strings `**New (G1)**`, `**New (G2)**`, `**New (G3)**` | 🔲 |

Status key: ✅ covered by passing test · ⚠️ test exists but failing · ❌ no test yet · 🔲 not yet implemented

### Edge Cases

| Scenario | Input | Expected Output |
|----------|-------|-----------------|
| `/backlog create` with concrete description | `/backlog create Add --json flag to plugin-traces` | Gate does not fire; proceeds to Step 1 normally |
| `/backlog create` with one trigger keyword | `/backlog create explore agentic monitoring` | Gate fires; user offered redirect |
| `/backlog create` with judgment-only ambiguity | `/backlog create We should rethink how migrations work` | Gate fires (no concrete deliverable named) |
| `/backlog triage` | (no description) | Gate skipped; proceeds to triage flow |
| `/design` with single-feature brainstorm | "Add per-environment connection caching" | Checkpoint does not fire |
| `/design` with 4 sub-features but tightly coupled | All 4 require shared state | Checkpoint fires (>3 sub-features); user picks (1); `cohesion_confirmed_count` incremented; design continues |
| `/design` with 2 independently-shippable features | A monitors, B repairs; neither depends on the other | Checkpoint fires; user picks (2)(a); `/backlog` invoked; `split_count` incremented |
| `/investigate` Step 7 with all four options offered | Investigation summary present | All four options shown including (4) File as epic; user picks (4); Step 8 branches to `/backlog` invocation; `epic_chosen_count` incremented |
| `bump` on missing key | `bump foo.bar.baz` when `foo` absent | Initializes nested path; sets value to 1 |
| `bump` on non-integer value | `bump foo` when state holds `foo = "string"` | Exits non-zero; stderr contains `non-integer`; state file unchanged |
| `bump` on invalid key shape | `bump "foo bar"` (contains space) | Exits non-zero; stderr contains `invalid key`; state file unchanged |

---

## Design Decisions

### Why one spec for three gates instead of three specs?

**Context:** Three independent SKILL.md edits could each land as a tiny standalone spec.

**Decision:** Single spec — `skill-routing-gates.md` — covering all three.

**Alternatives considered:**
- Three separate specs (`backlog-readiness-gate.md`, `investigate-epic-handoff.md`, `design-multi-concern-checkpoint.md`): rejected because the gates share one design pattern (skill detects scope mismatch → chains via Skill tool). Constitution SL1 calls for naming after the thing; the thing is "skill routing gates," not three unrelated skill edits.
- Fold into `workflow-enforcement.md`: rejected because workflow-enforcement is about T1/T2 hook gates at commit/PR exit points. T3 advisory routing within skills is a distinct concern that would dilute that spec.

**Consequences:**
- Positive: Skill Chaining Graph table has one home; future routing additions update one place.
- Negative: Spec is broader than each individual gate, so reviewers must hold all three in mind.

### Why T3 advisory rather than T2 hook enforcement?

**Context:** PPDS's enforcement tier model (workflow-enforcement.md) prefers hook-backed enforcement: "Directives that exist only in skill text are documented escape hatches."

**Decision:** Ship as T3 (skill text + telemetry). Defer T2 promotion to issue #1023.

**Alternatives considered:**
- T2 regex hook in `UserPromptSubmit`: rejected — duplicates the gate text in two places (skill + hook regex); maintenance burden; no evidence yet that the model ignores the gate.
- T2 LLM-classifier hook: rejected — adds extra model call on every prompt submit; latency, cost, failure-mode complexity. No evidence justifies this complexity yet.

**Consequences:**
- Positive: Cheap to ship; reversible; produces the telemetry needed to make the T2 decision on data.
- Negative: Gates can be ignored by the model. Mitigated by the explicit advisory marker and the telemetry counters.

### Why hybrid (keywords + judgment) for `/backlog` detection?

**Context:** Pure keyword match is brittle (misses paraphrased exploratory requests). Pure judgment is hard to test deterministically.

**Decision:** Hybrid. Keywords are a fast positive signal; judgment is the fallback.

**Consequences:**
- Positive: Catches the #1010 case (strategic-but-not-using-keywords descriptions). AC-02 makes keyword presence testable; the judgment fallback is documented but not unit-tested.
- Negative: Judgment fallback is non-deterministic; relies on the model honoring the gate. This is the central risk #1023 will measure.

### Why bump-based telemetry rather than a dedicated state file?

**Context:** Telemetry counters could live in a separate `.claude/state/routing-gates.json`.

**Decision:** Reuse `.workflow/state.json` via a new `bump <key>` sub-command on the existing `workflow-state.py` script.

**Alternatives considered:**
- Dedicated routing-gates JSON: rejected — adds a new state file and new read/write tooling for nine counters. The existing state machinery already handles nested keys, gitignore, and worktree-vs-main routing.

**Consequences:**
- Positive: One state file, one tool, no new state-file boilerplate.
- Negative: Telemetry mixes with workflow stage state; namespacing under `routing_gates.*` keeps them separate logically. Existing `write_state` is not atomic; this spec inherits that limitation. If a race condition is observed, atomicity should be added to `write_state` for all commands together — not as a `bump`-specific feature.

---

## Error Handling

| Error | Condition | Recovery |
|-------|-----------|----------|
| Skill tool invocation fails on chain | Target skill (`/investigate`, `/backlog`) returns error | Surface the error to user; offer to retry or to continue with original skill |
| `bump` on non-integer value | Existing key holds a string, list, or dict | Exit non-zero; print `ERROR: cannot bump non-integer value at <key>` to stderr; state file unchanged |
| `bump` on invalid key shape | Key does not match `[A-Za-z0-9_]+(\.[A-Za-z0-9_]+)*` | Exit non-zero; print `ERROR: invalid key <key>` to stderr; state file unchanged |
| Telemetry write failure | `.workflow/state.json` unwritable (read-only filesystem, permissions) | Telemetry is best-effort: log warning to stderr; do not block the gate flow |
| `bump` race condition | Two processes bump same key concurrently | Last-writer-wins (existing `write_state` is not atomic); accepted because workflow-state writes are serialized per session. If observed in practice, add temp-file+rename to `write_state` for all commands together — out of scope here |

### Recovery Strategies

- **Chain failure:** If `/backlog` Step 0 invokes `/investigate` and `/investigate` errors, the user is returned to a clean state and can re-invoke `/backlog create` to override the gate.
- **Telemetry failure:** A failed `bump` call must not block the gate flow. If state.json is unwritable, the skill logs a warning to stderr and continues with the user-facing gate behavior.

---

## Related Specs

- [workflow-enforcement.md](./workflow-enforcement.md) — T1/T2/T3 enforcement tier model; this spec's gates are T3.
- [spec-governance.md](./spec-governance.md) — spec naming conventions (SL1) applied here.
- [investigation.md](./investigation.md) — `/investigate` skill spec; G2 modifies its Step 8.

---

## Changelog

| Date | Change |
|------|--------|
| 2026-05-13 | Initial spec — three T3 routing gates, telemetry, skill chaining graph, T2-promotion roadmap link to #1023. Post-review revisions: G2 relocated from Step 8 to Step 7 (Align), atomicity claim dropped (existing `write_state` is non-atomic), AC table augmented with explicit grep targets, G3 telemetry expanded to four counters distinguishing false-positive / split / proceed-anyway. Components table row for G2 reconciled with Gate Specification (Step 7 option 4, not Step 8 option 3) during plan review. |

---

## Roadmap

- **Promote to T2 (hook-enforced) if telemetry justifies** — issue #1023 will read `routing_gates.*.honored_count / fired_count` ratios after 10+ fires per gate. Target: <80% honor rate triggers T2 design.
- **Maintain Skill Chaining Graph** — every new routing relationship updates the table in this spec rather than adding ad-hoc gate text to skills.
- **Spec / plan template multi-concern checkpoints** — out of scope here; follow-up to #989.
