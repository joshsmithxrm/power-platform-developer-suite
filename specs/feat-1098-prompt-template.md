# Worker Spawn Prompt Contract and Model Passthrough

**Status:** Draft
**Last Updated:** 2026-05-15
**Code:** [.claude/skills/start/](../.claude/skills/start/), [scripts/start-bg-spawn.py](../scripts/start-bg-spawn.py), [scripts/pr_monitor.py](../scripts/pr_monitor.py)
**Surfaces:** N/A (workflow tooling)

---

## Overview

`/start`-spawned workers receive a task brief but no embedded workflow contract — they implement and stop, skipping `/gates → /verify → /pr`. This spec embeds a default workflow contract appendix into every worker's spawn-time prompt, adds `--model` passthrough to `start-bg-spawn.py`, defaults `pr_monitor.py`'s Gemini triage subagent to Haiku, and instructs workers to use `Bash run_in_background=true` for the pr_monitor launch so they produce one accurate final summary after the monitor exits.

### Goals

- **Self-contained worker contract**: every worker knows the full lifecycle (design → approve → pipeline → pr_monitor → final summary) without operator hand-rolling the prompt
- **Model plumbing**: `/start` skill can specify `--model` per-worker so orchestrators choose cheaper or more capable models by work type
- **Cost reduction**: Haiku replaces Sonnet for the hot-path Gemini triage dispatch (~10x cheaper, adequate for structured JSON output)
- **Accurate PR completion signal**: workers stay alive until pr_monitor exits and produce a final summary with actual PR state

### Non-Goals

- Full per-caller policy library / model-per-caller policy table (v1.3, separate issue)
- Changing `pr_monitor.py`'s CI-fix or retro model — only triage changes
- Changing the foreground operator `/pr` flow (pr_monitor stays OS-detached for REPL-return in operator sessions)
- Any pr_monitor.py code changes beyond the `model=` default on `run_triage` — how workers invoke it is a prompt-template change

---

## Architecture

```
/start skill (Step 6b: Build launch prompt)
   │
   ├─ Prompt body (task brief, issue details, investigation)
   │
   └─ Prompt appendix (Workflow contract — NEW)
         │
         ▼
   Worker session (claude --bg [--model <m>])
         │
         ├─ Step 1: Read CLAUDE.md + skill docs
         ├─ Step 2: /design → spec + plan
         ├─ Step 3: Present spec+plan, STOP (phase=blocked)
         │          (operator attaches via Claude Desktop, approves)
         ├─ Step 4: python scripts/pipeline.py (after approval)
         ├─ Step 5: Bash run_in_background=true → pr_monitor
         └─ Step 6: Re-engagement → read result.json → final summary

start-bg-spawn.py (updated: --model flag)
   │
   └─ argv: ["claude", [--permission-mode <m>], [--model <m>], "--bg", "--name", <branch>, "--", <prompt>]

pr_monitor.run_triage (updated: model="haiku")
```

### Components

| Component | Responsibility | Change |
|---|---|---|
| `.claude/skills/start/SKILL.md` | Skill instructions | Step 6b: prompt appendix subsection; Step 6c: `--model` flag |
| `.claude/skills/start/REFERENCE.md` | Reference docs | Add §7: Design-gate handoff procedure |
| `scripts/start-bg-spawn.py` | Spawn helper | Add `--model` arg; thread to `claude --bg` argv |
| `scripts/pr_monitor.py` | PR monitor | `run_triage`: `model="haiku"` (was `"sonnet"`) |
| `tests/scripts/test_start_bg_spawn.py` | Unit tests | Tests for `--model` passthrough (AC-01, AC-02, AC-03) |
| `tests/scripts/test_pr_monitor.py` | Unit tests | Test for haiku default (AC-04) |
| `tests/scripts/test_start_skill_text.py` | Text audits | Tests for SKILL.md and REFERENCE.md content (AC-05–AC-10) |

### Dependencies

- Extends: [start-launch.md](./start-launch.md) — spawn mechanics (unchanged); this spec adds flags and prompt structure
- Uses: `scripts/claude_dispatch.py:spawn()` — already accepts `model` parameter and threads `--model` to argv (verified at lines 491–492)

---

## Specification

### Core Requirements

1. Every `/start`-spawned worker prompt must end with the workflow contract appendix (6 numbered steps) defined in the Prompt Appendix Text section below.
2. `start-bg-spawn.py` must accept `--model <name>` and, when set, include `["--model", <name>]` in the `claude --bg` argv immediately before `--bg`.
3. `pr_monitor.py run_triage()` must pass `model="haiku"` to `claude_dispatch.spawn()` by default. `run_retro()` keeps `model="sonnet"` unchanged.
4. `.claude/skills/start/SKILL.md` Step 6b must document the prompt appendix and Step 6c must show `--model` as an optional flag.
5. `.claude/skills/start/REFERENCE.md` must gain a §7 section titled "Design-Gate Handoff Procedure".

### Prompt Appendix Text

The following block is appended verbatim to every worker's launch prompt, after the task brief, issue details, and investigation context. The skill fills the `<branch>` and `<worktree-path>` placeholders before writing to the temp file.

```
Workflow contract:
1. Read CLAUDE.md, specs/CONSTITUTION.md, .claude/interaction-patterns.md, and any
   skills referenced in the task brief above.
2. Run /design. Author spec at specs/<branch-name>.md and plan at .plans/<branch-name>.md.
   Use the branch name with slashes replaced by hyphens as the filename
   (e.g. branch `feat/my-feature` → `specs/feat-my-feature.md`).
   Spec must cover all acceptance criteria from the issue. Plan must cover all spec ACs.
3. Present spec + plan summary in your final message of this turn. STOP after /design.
   Set workflow-state:
     python scripts/workflow-state.py set phase blocked
     python scripts/workflow-state.py set needs "spec ready for review"
   The operator will attach via Claude Desktop, review, and approve via a reply.
4. After operator approval: run `python scripts/pipeline.py` (invoked from the worktree
   after /design commits the spec; pipeline resolves the spec path automatically).
   On failure: python scripts/pipeline.py --resume (or --from <stage>).
5. After `python scripts/pipeline.py` exits successfully (pipeline.py includes /pr
   internally): launch pr_monitor via Bash run_in_background=true:
     python scripts/pr_monitor.py --worktree <worktree-path> --pr <PR-number>
   Claude Code will re-engage you when pr_monitor exits.
6. At re-engagement: read .workflow/pr-monitor-result.json and produce a final summary
   covering actual PR state (ready / merged / escalated / error / blocked). Terminate.
```

Placeholder resolution:
- `<branch-name>` — branch name with slashes replaced by hyphens (e.g. branch `feat/my-feature` → `feat-my-feature`)
- `<worktree-path>` — absolute worktree path (same value as `--worktree-abs`)

### `--model` Passthrough

Updated `spawn()` signature in `scripts/start-bg-spawn.py`:

```python
def spawn(
    worktree_abs: str,
    branch: str,
    prompt: str,
    jobs_dir: Path | None = None,
    permission_mode: str | None = None,
    model: str | None = None,       # NEW
) -> SpawnResult:
```

Argv construction:
```python
cmd = ["claude"]
if permission_mode:
    cmd.extend(["--permission-mode", permission_mode])
if model:                          # NEW
    cmd.extend(["--model", model]) # NEW
cmd.extend(["--bg", "--name", branch, "--", prompt])
```

`main()` gains:
```python
p.add_argument(
    "--model",
    default=None,
    help="Pass-through to `claude --model <name>`. Omit for ambient default.",
)
```

And passes `model=args.model` to `spawn()`. No validation of the model string — Claude CLI rejects unknown names with a clear error.

### Haiku Default for Triage

In `scripts/pr_monitor.py`, in the `run_triage()` function, change:
```python
model="sonnet",
```
to:
```python
model="haiku",
```

No other changes to `pr_monitor.py`. The `run_retro()` dispatch stays `model="sonnet"`.

### SKILL.md Updates

**Step 6b** — after the existing field-set list, add a subsection that embeds the workflow contract verbatim (do not reference this spec by filename; embed the full 6-step text). The subsection heading and placeholder instructions should read:

```markdown
#### Prompt appendix — workflow contract

After all task brief fields, append the following workflow contract verbatim.
Fill placeholders before writing to the temp file:
- `<branch-name>` → branch name with slashes as hyphens (e.g. `feat-my-feature`)
- `<worktree-path>` → the absolute worktree path from Step 5

[6-step contract text from §Prompt Appendix Text above]
```

**Step 6c** — update spawn invocation to show optional flags:

```bash
python scripts/start-bg-spawn.py \
  --worktree-abs "<worktree-absolute-path>" \
  --branch "<branch>" \
  --prompt-file "<temp-path>" \
  [--permission-mode bypassPermissions|acceptEdits|auto|default|dontAsk|plan] \
  [--model sonnet|opus|haiku|<full-model-id>]
```

### REFERENCE.md §7 — Design-Gate Handoff Procedure

```markdown
## §7 — Design-Gate Handoff Procedure

When a worker reaches Step 3 of its workflow contract (spec + plan authored, phase=blocked):

1. **Worker presents and stops** — the worker's last message in Agent View summarizes the
   spec and plan. Workflow state is `phase=blocked`, `needs="spec ready for review"`.

2. **Operator attaches** — `claude attach <short>` from any terminal, or click the session
   row in Agent View. The worker resumes from the operator's reply.

3. **Operator approval forms:**
   - Approve: any affirmative reply ("approved", "looks good", "proceed") → worker runs
     `python scripts/pipeline.py`
   - Request changes: worker incorporates them, re-presents spec, waits again
   - Abort: worker sets `phase=abandoned` and stops

4. **No interruptions after approval** — pipeline.py runs unattended:
   /implement → /gates → /verify → /qa → /review → /converge → /pr.
   pr_monitor.py handles Gemini triage and CI-fix rounds automatically.

5. **Final summary** — when pr_monitor exits, Claude Code re-engages the worker via
   Bash run_in_background=true completion. The worker reads
   `.workflow/pr-monitor-result.json` and produces one final message with actual PR state.

**Operator touch-points per worker:** design approval (one reply) + final PR review.
All automation runs between those two touch-points.
```

### Surface-Specific Behavior

N/A — workflow tooling only.

### Constraints

- `--model` accepts any string; Claude CLI validates it. No pre-validation in `start-bg-spawn.py`.
- When both `--permission-mode` and `--model` are set, argv order is: `--permission-mode` first, `--model` second, both before `--bg` (AC-03).
- The `run_triage` model change is Haiku only. Retro, CI-fix, and other dispatches are unchanged.
- The worker pr_monitor launch (Step 5) uses `Bash run_in_background=true` — not `subprocess.Popen`. The distinction is structural: Bash `run_in_background` re-engages the worker session when the command exits; OS-detach does not.
- The prompt appendix adds ~600 characters. Within the 30K hard cap defined in REFERENCE.md §4.

---

## Acceptance Criteria

| ID | Criterion | Test | Status |
|----|-----------|------|--------|
| AC-01 | `start-bg-spawn.py --model sonnet` produces argv containing `["--model", "sonnet"]` immediately before `"--bg"` | `test_start_bg_spawn.py::test_spawn_with_model_flag_in_argv` | ✅ |
| AC-02 | `start-bg-spawn.py` with no `--model` flag produces argv without any `"--model"` element | `test_start_bg_spawn.py::test_spawn_without_model_flag_in_argv` | ✅ |
| AC-03 | `start-bg-spawn.py` with both `--permission-mode bypassPermissions` and `--model haiku` produces argv with `--permission-mode` before `--model`, both before `"--bg"` | `test_start_bg_spawn.py::test_spawn_model_and_permission_mode_order` | ✅ |
| AC-04 | `pr_monitor.run_triage()` calls `claude_dispatch.spawn()` with `model="haiku"` when no override is passed | `scripts/test_pr_monitor.py::TestMonitorModelRouting::test_triage_uses_haiku` | ✅ |
| AC-05 | `/start` SKILL.md Step 6c invocation block shows `--model` as an optional flag | `tests/scripts/test_prompt_template.py::test_skill_step6c_shows_model_flag` | ✅ |
| AC-06 | `/start` SKILL.md Step 6b contains a prompt appendix or workflow contract subsection | `tests/scripts/test_prompt_template.py::test_skill_step6b_prompt_appendix_subsection` | ✅ |
| AC-07 | `/start` REFERENCE.md contains a `§7` section with heading "Design-Gate Handoff Procedure" | `tests/scripts/test_prompt_template.py::test_reference_section7_design_gate` | ✅ |
| AC-08 | Workflow contract text includes the literal command `python scripts/workflow-state.py set phase blocked` and a `set needs` command | `tests/scripts/test_prompt_template.py::test_workflow_contract_phase_blocked_command` | ✅ |
| AC-09 | Workflow contract text includes `run_in_background=true` for the pr_monitor launch instruction | `tests/scripts/test_prompt_template.py::test_workflow_contract_bg_launch_instruction` | ✅ |
| AC-10 | Workflow contract text includes `.workflow/pr-monitor-result.json` and a "final summary" instruction | `tests/scripts/test_prompt_template.py::test_workflow_contract_result_json_reference` | ✅ |
| AC-11 | Manual QA: worker spawned via `/start` runs `/design`, stops with spec+plan summary, `workflow-state phase == blocked` after `/design` completes | Manual — verify with `python scripts/workflow-state.py show` | 🔲 |

Status key: ✅ covered by passing test · ⚠️ test exists but failing · ❌ no test yet · 🔲 not yet implemented

### Edge Cases

| Scenario | Input | Expected Output |
|----------|-------|-----------------|
| `--model` value rejected by Claude | Invalid model name string | `claude --bg` exits non-zero; `start-bg-spawn.py` exits 2 with stderr surfaced verbatim |
| Worker pipeline fails mid-run | A pipeline stage exits non-zero | Worker runs `python scripts/pipeline.py --resume`; does not abort |
| pr_monitor exits with stuck state | `result.json` status = `"stuck-ci-fix-exhausted"` | Worker's re-engagement summary reports escalated state with last decision path |
| pr_monitor crashes | `result.json` status = `"monitor-crash"` | Worker re-engagement (if it fires) reports crash state and error field |
| Operator reply is ambiguous | Worker unsure if operator approved | Worker asks one clarifying question, then proceeds or waits per response |

---

## Design Decisions

### Why embed the contract in the prompt rather than a context file?

**Context:** SKILL.md Rule 8 explicitly states "No handoff file — the prompt is the handoff."

**Decision:** Append the contract verbatim to the worker's spawn-time prompt. The worker has the complete contract in its first context window, no file read required.

**Alternatives considered:**
- **A context handoff file in the worktree**: violates SKILL.md Rule 8 ("No handoff file — the prompt is the handoff"), explicitly rejected in the skill design.
- **Separate `contract.md` committed to the worktree**: requires a file read on every spawn; the prompt already contains the task.

**Consequences:**
- Positive: zero extra file reads; the contract is available from token 1.
- Negative: every worker's prompt is ~600 characters longer. Within the 30K hard cap.

### Why Haiku for triage but Sonnet for retro?

**Context:** Gemini triage produces structured JSON output from a bounded set of review comments. Retro synthesizes a full session transcript into qualitative observations.

**Decision:** Haiku for triage (structured, bounded, cheapest adequate model), Sonnet for retro (synthesis, open-ended reasoning).

**Alternatives considered:**
- **Haiku for both**: retro quality degrades on complex multi-file sessions; savings minimal relative to triage.
- **Sonnet for both**: forgoes ~10x triage cost savings with no benefit.

**Consequences:**
- Positive: ~10x cost reduction on hot-path triage (fires on every PR with Gemini comments).
- Negative: if Haiku produces malformed JSON, `parse_triage_jsonl` already handles it gracefully (returns None, treated as "triage failed").

### Why `Bash run_in_background=true` for pr_monitor (not OS-detach)?

**Context:** Issue #1098 scope addition (PR #1103 evidence): workers using OS-detach terminate before pr_monitor finishes; no worker message fires with the actual PR-ready signal.

**Decision:** The prompt template instructs workers to use `Bash run_in_background=true`. Claude Code's Bash tool with that flag re-engages the session when the command exits.

**Alternatives considered:**
- **OS-detach**: correct for the operator REPL context (returns control immediately); wrong for bg-worker (no re-engagement).
- **Agent tool with pr_monitor**: heavier, not needed — the Bash background tool is the right primitive.

**Consequences:**
- Positive: worker produces one accurate final message after pr_monitor exits.
- Negative: worker session stays alive ~30–60 min longer per PR. Negligible cost; accurate operator signal is the whole purpose of the supervisor pattern.

---

## Related Specs

- [start-launch.md](./start-launch.md) — spawn mechanics; this spec adds flags and prompt structure
- [workflow-enforcement.md](./workflow-enforcement.md) — phase/state management used by the workflow contract

---

## Changelog

| Date | Change |
|------|--------|
| 2026-05-15 | Initial spec (issue #1098, epic #1066 Phase 1a) |
