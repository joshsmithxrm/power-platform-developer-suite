# Investigation

**Status:** Draft (v1.1 — context-aware handoff, context.md consolidation)
**Version:** 1.1
**Last Updated:** 2026-03-27
**Code:** [.claude/skills/investigate/](../.claude/skills/investigate/), [.claude/agents/challenger.md](../.claude/agents/challenger.md), [.claude/hooks/](../.claude/hooks/), [scripts/](../scripts/)
**Surfaces:** N/A

---

## Overview

Pre-commitment exploration skill that gathers context, researches options, synthesizes tradeoffs, and runs adversarial challenge before the human commits to a direction. Bridges the gap between "I have an idea" and `/start` → `/design` by producing a validated design context. Includes supporting infrastructure: a challenger agent, a retro persistent store, a `/verify workflow` mode, and handoff modifications to `/start` and `/design`.

### Goals

- **Structured exploration**: Replace ad-hoc investigation with a repeatable skill that gathers context, researches, synthesizes, and challenges before commitment
- **Adversarial quality**: Catch blind spots via a structurally isolated challenger agent with a mandatory 8-dimension checklist
- **Retro feedback loop**: Persist retro findings across sessions so `/investigate` can detect recurring patterns
- **Workflow verification**: Validate `.claude/` and `scripts/` changes with the same rigor as product code
- **Seamless handoff**: Bridge `/investigate` → `/start` → `/design` via context content passed through conversation

### Non-Goals

- Headless or pipeline execution of `/investigate` — always interactive
- Metrics dashboards or trend analysis — not enough data yet (deferred)
- Auto-heal from retro findings — only 1 auto-fix finding exists (deferred)
- Spec-reviewer agent — build challenger first, extract reviewer later (deferred)

---

## Architecture

```
┌─────────────────────────────────────────────────────────┐
│                    /investigate                          │
│  (interactive skill, never headless)                    │
│                                                         │
│  ┌──────────┐  ┌───────────┐  ┌───────────┐           │
│  │ Gather   │─▶│ Research   │─▶│ Synthesize│           │
│  │ context  │  │ (full only)│  │ options + │           │
│  └──────────┘  │            │  │ tradeoffs │           │
│                │ ┌────────┐ │  └─────┬─────┘           │
│                │ │Research│ │        │                  │
│                │ │Agent   │ │        │                  │
│                │ │(inline)│ │        │                  │
│                │ └────────┘ │        │                  │
│                └────────────┘        │                  │
│       ┌──────────────────────────────┘                  │
│       ▼                                                 │
│  ┌──────────┐  ┌──────────┐  ┌──────────┐             │
│  │ Challenge │─▶│ Triage   │─▶│ Present  │             │
│  │ (full     │  │ AI auto- │  │ summary +│             │
│  │  only)    │  │ resolve  │  │ findings │             │
│  └─────┬─────┘  └──────────┘  └─────┬────┘             │
│        │                             │                  │
│        ▼                             ▼                  │
│  ┌───────────┐               ┌──────────┐              │
│  │ Challenger │               │  ALIGN   │              │
│  │ Agent     │               │ (human)  │              │
│  │ (Sonnet)  │               └─────┬────┘              │
│  └───────────┘                     │                   │
│   Receives ONLY:                   ▼                   │
│   - Summary                  ┌──────────┐              │
│   - Constraints              │ Handoff  │              │
│                              │ (convo)  │              │
│                              └──────────┘              │
└────────────────────────────────┬────────────────────────┘
                                 │ context in conversation
                                 ▼
┌──────────┐    ┌──────────┐    ┌──────────┐
│  /start  │───▶│ worktree │───▶│  /design │
│ writes   │    │ .plans/  │    │ loads    │
│ context  │    │ context  │    │ context  │
│ to .plans│    │ .md      │    │          │
└──────────┘    └──────────┘    └──────────┘
```

```
┌─────────────────────────────────────────────┐
│          Retro Persistent Store             │
│                                             │
│  /retro (pipeline stage)                    │
│    │                                        │
│    ├──▶ .workflow/retro-findings.json       │
│    │    (per-session, gitignored)           │
│    │                                        │
│    └──▶ .retros/summary.json               │
│         (git-tracked, append-only)          │
│         - findings_by_category              │
│         - per-occurrence timestamps         │
│         - windowed: 20 retros / 6 months   │
└─────────────────────────────────────────────┘
```

```
┌─────────────────────────────────────────────┐
│          /verify workflow                   │
│                                             │
│  Triggers: changes in .claude/ or scripts/  │
│                                             │
│  Checks:                                    │
│  1. pytest: pipeline.py, workflow-state.py  │
│  2. Hook scripts: test inputs, exit codes   │
│  3. settings.json validation                │
│  4. Skill frontmatter (name, description)   │
│  5. Agent frontmatter (valid tool names)    │
│  6. Skill file references (no dead links)   │
│  7. .retros/summary.json schema validation  │
│  8. Writes verify.workflow to state         │
└─────────────────────────────────────────────┘
```

### Components

| Component | Responsibility |
|-----------|----------------|
| `/investigate` skill | Interactive pre-commitment exploration — gathers context, researches, synthesizes, challenges, presents, gets human alignment, hands off |
| Challenger agent | Adversarial review via 8-dimension checklist, structurally isolated from session context |
| Researcher agent | Read-only codebase + web research, dispatched inline via Agent tool (not a separate agent file) |
| `.retros/summary.json` | Persistent retro findings store — append-only, windowed, git-tracked |
| `/verify workflow` mode | Structural validation of `.claude/` and `scripts/` changes |
| `/start` modification | Writes context to worktree `.plans/` during creation |
| `/design` modification | Loads context from `.plans/` if present at Step 1 |
| Hook fixes | Stop-hook, pr-gate, session-start, protect-main changes |

### Dependencies

- Uses patterns from: [workflow-enforcement.md](./workflow-enforcement.md)

---

## Specification

### Core Requirements

1. `/investigate` is always interactive, never a pipeline stage, never headless
2. Human declares ceremony level upfront: "quick" (skip research + challenge) or "full" (all steps)
3. Challenger agent receives ONLY the investigation summary and constraints/decisions — no session transcript, no user goals, no original problem framing
4. Challenger uses the 8-dimension mandatory checklist: testability, verifiability, failure modes, observability, operability, reversibility, dependencies, scope
5. Retro persistent store (`.retros/summary.json`) is git-tracked, append-only, updated by `/retro` skill
6. `/verify workflow` validates all `.claude/` and `scripts/` structural properties
7. Handoff passes context through conversation memory — `/investigate` holds content, `/start` writes it to worktree `.plans/context.md`
8. Hook changes enforce workflow verification for process code changes

### Primary Flows

**Full Investigation:**

1. **Gather**: Read relevant skills, specs, code, retro findings (including `.retros/summary.json` for recurring patterns)
2. **Research**: Dispatch researcher agent (Read, Glob, Grep, WebSearch, WebFetch) for external practices — auto-detect need based on domain: process/architecture changes → research; bug investigation → no research; unfamiliar domain → research. Human can override.
3. **Synthesize**: Present options, tradeoffs, and an explicit assumptions list
4. **Challenge**: Dispatch challenger agent (Sonnet, isolated) with summary + constraints only. Convergence rules: round N+1 must have fewer blockers than round N; stop at zero blockers with only implementation-detail concerns; stop if same findings reappear; max 3 rounds.
5. **Triage**: AI auto-resolves factual errors and missing details. Design decisions and values questions go to human at align step.
6. **Present**: Investigation summary + challenge findings side by side
7. **Align**: Human says go / change X / not worth it
8. **Handoff**: Present the context summary. **Context-aware routing:** If already in a worktree, write `.plans/context.md` directly and instruct "Run `/design` to continue." If on main, instruct "Run `/start` to create a worktree — context will be written to `.plans/context.md` in the new worktree."

**Quick Investigation:**

Steps 1, 3, 6, 7, 8 only. Skips research (step 2) and challenge (steps 4-5).

**Retro Store Update (within `/retro` skill):**

1. `/retro` completes analysis and writes `.workflow/retro-findings.json` (per-session, existing behavior)
2. `/retro` also reads `.retros/summary.json` (or creates it if absent)
3. Appends new findings by category with date, branch, finding_id per occurrence
4. Updates aggregate metrics (avg_fix_ratio, pipeline_success_rate, avg_convergence_rounds)
5. Trims entries that fall outside both windows (older than 6 months AND beyond the last 20 retros)
6. Writes updated `.retros/summary.json`

**Verify Workflow:**

1. Detect trigger: changed files in `.claude/` or `scripts/`
2. Run all 8 checks (see Verify Workflow Checks below)
3. Report structured pass/fail results
4. On all-pass: write `verify.workflow` and `qa.workflow` to workflow state (structural validation IS the QA for process code)

### `/investigate` Skill Definition

File: `.claude/skills/investigate/SKILL.md`

Frontmatter:
- name: investigate
- description: Pre-commitment exploration with optional adversarial challenge. Use when exploring ideas, evaluating approaches, or researching before committing to a direction.

Permissions by phase:
- Gather phase: main agent reads codebase directly (Read, Glob, Grep)
- Research phase: researcher agent with Read, Glob, Grep, WebSearch, WebFetch only — no Edit, Write, Bash
- Challenge phase: challenger agent receives ONLY investigation summary + constraints/decisions section

### Challenger Agent Definition

File: `.claude/agents/challenger.md`

Frontmatter:
- name: challenger
- model: sonnet
- tools: Read, Grep, Glob (read-only — no Edit, Write, Bash)

**Dispatch mechanism:** The `/investigate` skill invokes the challenger via the Agent tool, referencing the agent file by name (`subagent_type: challenger`). The agent file defines model, tools, and prompt. The skill constructs the prompt payload with ONLY the investigation summary and constraints/decisions.

Mandatory dimensions checklist (baked into agent prompt):
1. **Testability**: How is this tested? Can the tests run headless?
2. **Verifiability**: How do you know it works after shipping?
3. **Failure modes**: What breaks? How do you detect and recover?
4. **Observability**: How do you see what is happening?
5. **Operability**: What is the maintenance burden? Who runs it?
6. **Reversibility**: Can you undo this easily?
7. **Dependencies**: What must exist first?
8. **Scope**: What does this explicitly NOT cover?

Finding classification:
- BLOCKER: Architectural flaw, missing critical property, will cause failure
- CONCERN: Non-trivial gap, should be addressed but not a showstopper
- NIT: Style, preference, minor improvement

Convergence stopping rules:
- Round N+1 has fewer blockers than round N → converging
- Zero blockers + all remaining concerns are implementation-detail level → stop
- Same findings reappear across rounds → challenger is looping, stop
- Max 3 rounds (safety valve)

Finding triage convention (by classification):
- BLOCKERs always go to the human at the align checkpoint — never auto-resolved
- CONCERNs: factual errors → AI auto-resolves; design decisions → human at align
- NITs: AI auto-resolves missing details; style/values questions → human at align

### Researcher Agent (Inline Dispatch)

The researcher is NOT a separate agent file. It is dispatched inline within the `/investigate` skill using the Agent tool with explicit tool restrictions. This avoids creating a permanent agent definition for what is a single-use, skill-internal dispatch.

Dispatch parameters:
- subagent_type: general-purpose (default)
- Tools permitted: Read, Glob, Grep, WebSearch, WebFetch
- Tools excluded: Edit, Write, Bash, Agent (no nested agents)
- Prompt: constructed by the skill with the research question and codebase context
- Model: inherits from parent (no override)

### Retro Persistent Store

File: `.retros/summary.json` — git-tracked in main repo, updated in-place.

Schema:
```json
{
  "schema_version": 1,
  "last_updated": "2026-03-27",
  "total_retros": 1,
  "findings_by_category": {
    "fix-ratio": [
      {
        "date": "2026-03-26",
        "branch": "feat/example",
        "finding_id": "R-01"
      }
    ]
  },
  "metrics": {
    "avg_fix_ratio": 0.0,
    "pipeline_success_rate": 0.0,
    "avg_convergence_rounds": 0.0
  }
}
```

Category vocabulary (seed, extensible):
- `fix-ratio` — high ratio of fix commits to feature commits
- `timeout` — pipeline or QA timeouts
- `stale-reference` — dead links, outdated paths
- `thrashing` — 3+ commits on same fix
- `pipeline-scheduling` — orchestrator timing/ordering issues
- `missing-validation` — gaps in verification coverage
- `false-positive` — incorrect findings from review or QA
- `convergence-failure` — review/fix cycles that don't converge
- `daemon-orphan` — leaked processes

Retention window: retain entries that fall within the last 20 retros OR the last 6 months, whichever criterion retains more entries. On each update, trim entries that fall outside both windows.

### Verify Workflow Checks

All checks are headless-capable. Triggered when changed files are in `.claude/` or `scripts/`.

| # | Check | Method | Pass Criteria |
|---|-------|--------|---------------|
| 1 | Python tests | `pytest tests/test_pipeline.py tests/test_workflow_state.py` | Exit code 0 |
| 2 | Hook script tests | Execute each hook in `.claude/hooks/` with test inputs, check exit codes | All hooks exit 0 on valid input, exit 2 on invalid input |
| 3 | settings.json validation | Parse `.claude/settings.json` as JSON, validate hook entries reference existing files | Valid JSON, all hook `command` paths exist |
| 4 | Skill frontmatter | Parse each `.claude/skills/*/SKILL.md` YAML frontmatter | `name` and `description` fields present and non-empty |
| 5 | Agent frontmatter | Parse each `.claude/agents/*.md` YAML frontmatter | `tools` list present, each tool name is a known valid tool |
| 6 | Skill file references | Scan skill markdown for file paths, verify targets exist | No dead links to non-existent files |
| 7 | Retro store schema | Validate `.retros/summary.json` against expected schema | Valid JSON matching schema (if file exists) |
| 8 | Workflow state write | Write `verify.workflow` and `qa.workflow` to `.workflow/state.json` | State file updated with both keys |

### `/start` Modification

After worktree creation (current Step 5), before opening terminal (Step 6):

The AI has the investigation output in conversation context. `/start` writes it directly — no scanning or sentinels needed.

After worktree creation, `/start` writes the context content from conversation to `.plans/context.md` in the new worktree:
1. Create `.plans/` directory in the new worktree (if it doesn't exist)
2. Write the context content to `.plans/context.md` in the worktree
3. Include this in the Step 7 guidance: "Design context written to `.plans/context.md`. Run `/design` to continue."

If `/start` is invoked without prior `/investigate` (no context in conversation) — existing flow unchanged, no file written.

### `/design` Modification

At the end of Step 1 (after loading constitution and spec template):

1. Check for `.plans/context.md` in current working directory
2. If found: read it, present a summary to the user, ask: "Design context loaded from investigation. Proceed to spec writing, or brainstorm further?"
3. If "proceed": skip brainstorm (Step 2), go directly to Step 3 (write spec)
4. If "brainstorm": continue with normal Step 2 flow, using the design context as input
5. If not found: normal brainstorm flow, no change

**Constraint checking (when context is loaded):**
Before presenting the architecture (Step 2 or Step 3), verify the proposal against each Constraint and each Known Concern listed in `context.md`. Flag any conflicts — e.g., "Constraint #3 says X, but the proposed architecture does Y." This catches drift between the investigation decisions and the spec being written.

### Hook Changes

**session-stop-workflow.py (line 102):**
Remove `.claude/` from `non_code_prefixes`. Process code in `.claude/` requires workflow enforcement (gates, verify, review) just like product code.

Before:
```python
non_code_prefixes = (
    "specs/",
    ".plans/",
    "docs/",
    ".claude/",
    "README",
    "CLAUDE.md",
)
```

After:
```python
non_code_prefixes = (
    "specs/",
    ".plans/",
    "docs/",
    "README",
    "CLAUDE.md",
)
```

**session-stop-workflow.py (line 147) and pr-gate.py (line 104):**
Add `"workflow"` to `valid_surfaces` tuple. Allows `verify.workflow` to satisfy the "at least one surface verified" check.

Before:
```python
valid_surfaces = ("ext", "tui", "mcp", "cli")
```

After:
```python
valid_surfaces = ("ext", "tui", "mcp", "cli", "workflow")
```

**session-start-workflow.py (line 66):**
Fix stale reference: `/spec` → `/design`.

Before:
```python
"  For new features: /spec → /implement → /gates → /verify → /qa → /review → /pr\n"
```

After:
```python
"  For new features: /design → /implement → /gates → /verify → /qa → /review → /pr\n"
```

**session-stop-workflow.py and pr-gate.py — QA gate for workflow-only changes:**
The `valid_surfaces` tuple is used for both `/verify` and `/qa` gates. Adding `"workflow"` to `valid_surfaces` means `verify.workflow` satisfies the verify gate, but the QA gate also needs a pathway. For workflow-only changes (only `.claude/` and `scripts/` files changed), the `/verify workflow` check IS the QA — structural validation of process code doesn't need a separate interactive QA surface. The QA gate is satisfied by `qa.workflow`, which `/verify workflow` writes alongside `verify.workflow` when all checks pass. Both hooks use the same `valid_surfaces` tuple, so adding `"workflow"` covers both gates.

**protect-main-branch.py — MSYS path normalization fix:**
The `is_allowed_path` function (line 34) normalizes paths but the `.worktrees/` substring check can fail when `CLAUDE_PROJECT_DIR` contains an MSYS-style path that doesn't normalize consistently. The fix: normalize both the file path and project dir through `normalize_msys_path()` before the prefix strip, and also check the un-stripped path for allowed substrings.

### Constraints

- `/investigate` must never be added to the pipeline orchestrator or called via `claude -p`
- Within a chosen ceremony level, token/compute cost is secondary to process quality — do not optimize by skipping steps
- Challenger agent prompt must be explicitly constructed with only summary + constraints — never pass raw conversation context
- Category vocabulary in `.retros/summary.json` is extensible — new categories can be added by the retro skill without a spec change
- `.retros/summary.json` merge conflicts are accepted risk — append-only semantics makes them rare and mechanically resolvable

---

## Acceptance Criteria

| ID | Criterion | Test | Status |
|----|-----------|------|--------|
| AC-01 | `/investigate` skill definition exists with correct frontmatter (name, description) | `VerifyWorkflow.SkillFrontmatter` (check 4) | 🔲 |
| AC-02 | `/investigate` skill contains quick mode flow that excludes research and challenge steps | `grep "quick" .claude/skills/investigate/SKILL.md` matches flow definition listing only gather/synthesize/present/align/handoff | 🔲 |
| AC-03 | `/investigate` skill contains full mode flow that includes all 8 steps in order | `grep -c "Gather\|Research\|Synthesize\|Challenge\|Triage\|Present\|Align\|Handoff" .claude/skills/investigate/SKILL.md` returns 8 (one per step) | 🔲 |
| AC-04 | Challenger agent definition exists at `.claude/agents/challenger.md` with model: sonnet and read-only tools | `VerifyWorkflow.AgentFrontmatter` (check 5) | 🔲 |
| AC-05 | Challenger agent prompt contains all 8 mandatory dimensions (one per line) | 8 individual grep checks: `grep "Testability" .claude/agents/challenger.md`, `grep "Verifiability"`, ..., `grep "Scope"` — all 8 return matches | 🔲 |
| AC-06 | Challenger agent dispatch in skill passes only summary + constraints, not session context | `grep -A5 "challenger\|Agent.*challenger" .claude/skills/investigate/SKILL.md` shows prompt constructed from summary + constraints sections only | 🔲 |
| AC-07 | Skill contains max 3 round guard for challenger convergence | `grep -E "max.*3\|3.*round\|round.*3" .claude/skills/investigate/SKILL.md` returns match | 🔲 |
| AC-08 | Skill contains fewer-blockers stopping condition for challenger | `grep -i "fewer.*blocker" .claude/skills/investigate/SKILL.md` returns match | 🔲 |
| AC-09 | Skill contains same-findings-loop stopping condition for challenger | `grep -i "same.*finding\|loop\|reappear" .claude/skills/investigate/SKILL.md` returns match | 🔲 |
| AC-10 | Skill contains zero-blockers stopping condition for challenger | `grep -i "zero.*blocker" .claude/skills/investigate/SKILL.md` returns match | 🔲 |
| AC-11 | Researcher dispatched via Agent tool with only Read, Glob, Grep, WebSearch, WebFetch | `grep -E "WebSearch\|WebFetch" .claude/skills/investigate/SKILL.md` returns match AND `grep -E "Edit\|Write\|Bash" .claude/skills/investigate/SKILL.md` returns no match in researcher section | 🔲 |
| AC-12 | `.retros/summary.json` schema includes schema_version, last_updated, total_retros, findings_by_category, metrics | `VerifyWorkflow.RetroStoreSchema` (check 7) | 🔲 |
| AC-13 | `/retro` skill contains dual-write logic for both `.workflow/retro-findings.json` and `.retros/summary.json` | `grep "summary.json" .claude/skills/retro/SKILL.md` returns match in write/update section | 🔲 |
| AC-14 | `.retros/summary.json` entries include date, branch, finding_id per occurrence | `VerifyWorkflow.RetroStoreSchema` (check 7) | 🔲 |
| AC-15 | Retro store is append-only — existing entries are never mutated, only new entries appended and old entries trimmed by retention window | `grep -i "append" .claude/skills/retro/SKILL.md` returns match in store update section | 🔲 |
| AC-16 | Retention window trims entries older than both 20 retros and 6 months | `grep -E "20.*retro\|6.*month" .claude/skills/retro/SKILL.md` returns match | 🔲 |
| AC-17 | `/verify workflow` runs all 8 checks when triggered by `.claude/` or `scripts/` changes | `grep -c "Check\|check" .claude/skills/verify/SKILL.md` shows workflow section with 8 checks listed | 🔲 |
| AC-18 | `/verify workflow` writes `verify.workflow` and `qa.workflow` to state on all-pass | `grep -E "verify.workflow\|qa.workflow" .claude/skills/verify/SKILL.md` returns matches | 🔲 |
| AC-19 | Skill frontmatter validation catches missing name or description | `test_verify_workflow.py::test_skill_frontmatter_missing_name` | 🔲 |
| AC-20 | Agent frontmatter validation catches invalid tool names | `test_verify_workflow.py::test_agent_frontmatter_invalid_tool` | 🔲 |
| AC-21 | Dead link detection finds references to non-existent files | `test_verify_workflow.py::test_dead_link_detection` | 🔲 |
| AC-22 | `/start` skill writes `.plans/context.md` to worktree when context is present in conversation (canonical: workflow-enforcement AC-95, AC-97) | `grep "context.md" .claude/skills/start/SKILL.md` returns match in write logic | 🔲 |
| AC-23 | `/design` skill loads `.plans/context.md` at Step 1 and offers proceed/brainstorm choice (canonical: workflow-enforcement AC-98) | `grep "context.md" .claude/skills/design/SKILL.md` returns match in Step 1 section | 🔲 |
| AC-24 | session-stop-workflow.py `non_code_prefixes` does not contain `.claude/` | `python -c "exec(open('.claude/hooks/session-stop-workflow.py').read()); assert '.claude/' not in non_code_prefixes"` or equivalent grep | 🔲 |
| AC-25 | session-stop-workflow.py and pr-gate.py `valid_surfaces` contains `"workflow"` | `grep '"workflow"' .claude/hooks/session-stop-workflow.py .claude/hooks/pr-gate.py` returns matches in valid_surfaces tuples | 🔲 |
| AC-26 | session-start-workflow.py references `/design` not `/spec` in guidance text | `grep '/spec' .claude/hooks/session-start-workflow.py` returns no matches | 🔲 |
| AC-27 | protect-main-branch.py correctly allows `.worktrees/` paths under MSYS normalization | `test_protect_main_branch.py::test_msys_worktree_path_allowed` | 🔲 |
| AC-28 | `/investigate` skill references `.retros/summary.json` in gather step for pattern awareness | `grep "summary.json" .claude/skills/investigate/SKILL.md` returns match in gather section | 🔲 |
| AC-29 | `.claude/` file changes trigger workflow enforcement (end-to-end: commit .claude/ change, session-stop blocks without verify) | `test_session_stop_workflow.py::test_claude_dir_requires_enforcement` | 🔲 |
| AC-30 | `/design` skill verifies proposal against each Constraint and Known Concern from context before presenting architecture | `grep -i "constraint.*flag\|known concern\|conflict" .claude/skills/design/SKILL.md` returns match | 🔲 |
| AC-31 | Workflow-only change passes QA gate when `qa.workflow` is set by `/verify workflow` | `test_session_stop_workflow.py::test_qa_workflow_surface_accepted` | 🔲 |
| AC-32 | Missing `.retros/summary.json` does not cause error during `/investigate` gather phase | `test_verify_workflow.py::test_missing_retro_store_no_error` | 🔲 |
| AC-33 | `.retros/summary.json` with wrong `schema_version` triggers rebuild, not crash | `test_verify_workflow.py::test_retro_store_schema_mismatch_rebuild` | 🔲 |
| AC-34 | `/investigate` Step 8 handoff detects if already in a worktree and writes `.plans/context.md` directly instead of saying "Run `/start`" | Manual: run `/investigate` in a worktree, verify context file written and guidance says "Run `/design`" | 🔲 |
| AC-35 | `/investigate` Step 8 handoff detects main branch and instructs "Run `/start`" with context in conversation memory | Manual: run `/investigate` on main, verify guidance says "Run `/start`" | 🔲 |

### Edge Cases

| Scenario | Input | Expected Output |
|----------|-------|-----------------|
| No retro store exists | First `/investigate` run, `.retros/summary.json` absent | Gather phase skips retro patterns, no error |
| Challenger finds zero issues in round 1 | Clean proposal | Stop after round 1, present with "no concerns found" |
| Challenger loops (same findings reappear) | Round 2 repeats round 1 findings | Stop, note "challenger converged (repeated findings)" |
| Quick mode on complex topic | Human chooses quick for architecture change | Skill proceeds without challenge — human's judgment call |
| No context in conversation | `/start` invoked without prior `/investigate` | Normal `/start` flow, no `.plans/context.md` written |
| `.plans/context.md` already exists | `/design` finds stale context from prior investigation | Present it, ask "proceed or brainstorm?" — human decides |
| `.retros/summary.json` has merge conflict | Two branches update concurrently | Append-only makes conflict mechanically resolvable — both additions are valid |
| MSYS path with mixed separators | `/c/VS/ppdsw/ppds/.worktrees/foo/bar.md` | `protect-main-branch.py` normalizes to `C:/VS/...` and allows |
| Challenger diverges (more blockers each round) | Round 2 has more blockers than round 1 | Stop at max 3 rounds, present all findings, note divergence to human |
| Retro store schema version mismatch | `summary.json` has `schema_version: 2` but code expects 1 | Log warning, rebuild fresh file with current schema |
| No context in new session | Human runs `/start` in a new session without prior `/investigate` | No `.plans/context.md` written, normal `/start` flow |
| `/investigate` run in worktree | Already on a feature branch | Handoff writes `.plans/context.md` directly, says "Run `/design`" — skips `/start` |
| `/investigate` run on main with existing worktree for this work | Worktree exists for the topic | Handoff says "Run `/start`" — resume will be offered when worktree exists |

---

## Core Types

### Investigation Summary

The output of the synthesize step, input to the challenger agent. Plain markdown, not a formal type — lives in conversation memory.

```markdown
## Investigation Summary
### Problem: {what we're exploring}
### Options: {2-3 approaches with tradeoffs}
### Assumptions: {explicit list}
### Constraints and Decisions: {numbered list of settled items}
```

### Design Context

The file written by `/start` to `.plans/context.md`. Contains the investigation output that `/design` consumes.

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
{items needing spec-level answers}

## Evidence
{key data points that informed the investigation}
```

### Retro Summary Store

```json
{
  "schema_version": "number (currently 1)",
  "last_updated": "string (ISO date)",
  "total_retros": "number",
  "findings_by_category": {
    "<category>": [
      {
        "date": "string (ISO date)",
        "branch": "string",
        "finding_id": "string (R-NN)"
      }
    ]
  },
  "metrics": {
    "avg_fix_ratio": "number",
    "pipeline_success_rate": "number",
    "avg_convergence_rounds": "number"
  }
}
```

---

## Error Handling

### Error Types

| Error | Condition | Recovery |
|-------|-----------|----------|
| No codebase context found | Gather phase finds no relevant files | Ask human for more specific direction |
| Researcher agent timeout | WebSearch/WebFetch takes too long | Present what was gathered, skip remaining research |
| Challenger agent failure | Agent crashes or returns malformed output | Present investigation summary without challenge findings, note challenger was skipped |
| `.retros/summary.json` corrupted | Invalid JSON | Log warning, create fresh file, do not lose per-session findings |
| `.retros/summary.json` schema mismatch | Valid JSON but `schema_version` differs from expected | Log warning with version numbers, create fresh file with current schema version. Old data is lost but per-session files remain as source of truth |
| `/verify workflow` partial failure | Some checks pass, others fail | Report all results, do not write state, list failing checks |

### Recovery Strategies

- **Agent failures**: Degrade gracefully — the investigation summary is still valuable without the challenger. Present what exists and let the human decide.
- **Store corruption**: Rebuild from scratch — the per-session files in `.workflow/` are the source of truth. The persistent store is a convenience aggregation.

---

## Design Decisions

### Why one spec for five deliverables?

**Context:** Five deliverables (skill, agent, store, verify route, handoff mods) could be 1-5 specs.

**Decision:** One spec named `investigation.md`.

**Alternatives considered:**
- Separate specs per deliverable: Rejected — deliverables 2-5 exist only to serve deliverable 1. No independent consumer exists for any of them. SL1 says "one spec per domain concept" and the domain concept is investigation.
- Three specs (skill, infrastructure, hooks): Rejected — arbitrary split that doesn't match domain boundaries.

**Consequences:**
- Positive: Single AC table, single review pass, single implementation plan
- Negative: Larger spec, but all five deliverables are small modifications except the skill itself

### Why human declares ceremony level?

**Context:** Ceremony should scale with risk. Could auto-detect or let human decide.

**Decision:** Human declares "quick" or "full" at the start of `/investigate`.

**Alternatives considered:**
- Auto-detect from scope: Rejected — heuristics can misfire (is this skill tweak risky or not?). The human knows the risk.
- Progressive escalation (ask after synthesize): Rejected — adds a decision point mid-flow. Simpler to decide upfront.

**Consequences:**
- Positive: Simplest implementation, human is always right about their own risk tolerance
- Negative: Human might choose quick when full was warranted — accepted risk, human judgment call

### Why Sonnet for the challenger?

**Context:** Challenger needs to find real blind spots — judgment-heavy work.

**Decision:** Sonnet. The task is bounded by the 8-dimension checklist and structured output format. The input is constrained (summary + constraints only). The human is the final judge.

**Alternatives considered:**
- Opus: Maximum quality, but higher cost/latency for an interactive skill where the human is waiting. The checklist constrains the task enough that Sonnet performs well.
- Configurable: Default Sonnet with Opus override. Adds a parameter for marginal benefit.

**Consequences:**
- Positive: Faster, cheaper, good enough for structured adversarial review
- Negative: May miss subtle architectural issues that Opus would catch — mitigated by human review at align step

### Why conversation-based handoff?

**Context:** `/investigate` needs to pass context to `/start` → `/design`. Can't write to main (protect-main-branch hook blocks it).

**Decision:** `/investigate` holds context in conversation memory. Human runs `/start`, which writes it to the new worktree's `.plans/context.md`.

**Alternatives considered:**
- Write intermediate file on main: Rejected — protect-main-branch hook blocks it.
- CLI argument: Rejected — content too large for command-line args.
- Clipboard: Rejected — fragile, platform-dependent, loses content on copy.

**Consequences:**
- Positive: No file on main, no hook conflict, content travels with the conversation
- Negative: Requires the human to run `/start` in the same conversation as `/investigate`. If they start a new session, the context is lost — but they can re-run `/investigate` or manually create the file.

### Why accept merge conflict risk on summary.json?

**Context:** `.retros/summary.json` is git-tracked and updated by `/retro` on PR branches. Concurrent PRs could conflict.

**Decision:** Accept the risk. The retro skill writes to both per-session (`.workflow/retro-findings.json`) and persistent (`.retros/summary.json`) in the same pass.

**Alternatives considered:**
- Post-merge update only: Rejected — adds a second code path (post-merge hook) that must understand the findings schema. Two producers of the same file is worse than occasional merge conflicts.
- Separate aggregation script: Rejected — YAGNI. One consumer, one producer.

**Consequences:**
- Positive: Single producer, single moment of update, simple implementation
- Negative: Occasional merge conflicts — mitigated by append-only semantics (both sides' additions are valid, mechanically resolvable)

### Why 8-dimension checklist as core mechanism?

**Context:** Adversarial review needs structure to prevent blind spots. An unconstrained "find problems" prompt produces inconsistent coverage.

**Decision:** 8 mandatory dimensions baked into the challenger agent prompt: testability, verifiability, failure modes, observability, operability, reversibility, dependencies, scope.

**Alternatives considered:**
- Unconstrained adversarial prompt: Rejected — produces inconsistent coverage. Some dimensions (observability, operability) are systematically overlooked without explicit prompting.
- Domain-specific checklists: Rejected — too many checklists to maintain. The 8 dimensions are universal enough to apply to any proposal.

**Consequences:**
- Positive: Consistent coverage, prevents omission blind spots, makes challenger output predictable and comparable across investigations
- Negative: May force commentary on dimensions that aren't relevant (e.g., "observability" for a typo fix) — mitigated by ceremony scaling (quick mode skips challenge entirely)

---

## Related Specs

- [workflow-enforcement.md](./workflow-enforcement.md) — Workflow state, pipeline orchestrator, hook infrastructure that this spec extends

---

## Changelog

| Date | Change |
|------|--------|
| 2026-03-27 | Initial spec |
| 2026-03-27 | v1.1 — context-aware handoff: (1) Step 8 detects worktree vs main and routes accordingly, (2) context file renamed from `design-context.md` to `context.md` for consolidation with `/start` work-type routing, (3) worktree handoff writes context file directly instead of requiring `/start`. |

---

## Roadmap

- **Spec-reviewer agent**: Extract a dedicated reviewer from the challenger pattern — reviews specs against constitution and template, not proposals against dimensions
- **Metrics layer**: Dashboard or summary view of `.retros/summary.json` trends once enough data exists (target: after 10+ retros)
- **Auto-heal from retro findings**: Automatically apply `auto-fix` tier findings from retro store — currently only 1 auto-fix finding exists, not enough to justify the machinery
- **Ceremony auto-detection**: Infer quick vs full from scope/risk heuristics instead of human declaration — deferred because heuristics misfire and human judgment is cheap
