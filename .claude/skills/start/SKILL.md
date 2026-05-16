---
name: start
description: Create a worktree for new work and spawn a background session visible in Agent View. Use when starting any new feature, bug fix, or task — including when the user says "start a worktree", "create a worktree", or describes work they want to begin. Accepts freeform input — issues, descriptions, context. Runs from main.
---

# Start

Bootstrap a feature worktree. Parses freeform input, creates the worktree, initializes workflow state, and spawns a background Claude session visible in Agent View.

## When to Use

Any request implying new work in a fresh worktree: "start a worktree", "create a worktree", "I need to work on...", "begin work on...", or any new feature/bug/task.

## Process

### Step 1: Determine Repo Root

`git rev-parse --abbrev-ref HEAD`. If on main/master, proceed. Otherwise resolve root via `git worktree list --porcelain` (first entry's `worktree` line). Run all creation commands from that root.

### Step 2: Parse Input

From `$ARGUMENTS` extract:
- **Name:** kebab-case from key nouns. Read REFERENCE.md §1 "Name Derivation Examples" for guidance.
- **Issues:** `#NNN`, `issue NNN`, or bare numbers near bug/fix/issue context words.

### Step 2a: Shipped-Source Check

If `$ARGUMENTS` names an existing branch as work to finalize/resume (a kebab ref like `fix/<x>`, `feat/<x>`, `chore/<x>` that is **not** the new branch being created), verify that source hasn't already merged before spawning a wrong-brief agent. Read REFERENCE.md §6 "Shipped-Source Check" for the exact procedure and abort message.

### Step 2b: Work-Type Classification

If issues extracted:
```bash
gh issue view <N> --json labels --jq '.labels[].name'
```
Map: `type:bug`→bug fix, `type:enhancement/refactor/performance`→enhancement, `type:docs`→docs. Most common wins. Leave empty if tied, no labels, or `gh` fails.

### Step 3: Propose and Confirm

Launch command by work type: bug→`claude`; enhancement→`claude '/implement'`; feature→`claude '/design'`; docs→`claude`. If no pre-selection, resolve after user picks work type.

Present name/branch/issues/work-type/launch-command. Read REFERENCE.md §2 "Confirmation Template" for exact format. Wait for confirmation. The confirmed launch command is used in Steps 5–7 — do not re-derive.

### Step 4: Check for Existing Worktree

`ls -d .worktrees/<name>` — if exists, show current branch (`git -C .worktrees/<name> rev-parse --abbrev-ref HEAD`) and ask "Resume or create with different name?" Resume → skip to Step 6; new → ask for alternative, loop.

### Step 4b: Check for In-Flight Conflicts

`python scripts/inflight-check.py --issue <N>` (per issue) and `--area <area>`. Exit 1 → show conflicting session (id, branch, intent, started), ask continue/coordinate/abort.

### Step 5: Create Worktree and Initialize State

Use `python scripts/worktree-create.py --name <name>` — do NOT open-code `git worktree add` (see REFERENCE.md §3). Stop on non-zero and surface stderr verbatim.

Initialize state, passing `--worktree-path` with the absolute path so CWD inheritance can't write to the caller's `.workflow/state.json`:
```bash
WT="<absolute-worktree-path>"   # e.g. C:/.../ppds/.worktrees/<name>
python scripts/workflow-state.py --worktree-path "$WT" init "feat/<name>"
python scripts/workflow-state.py --worktree-path "$WT" set phase starting
python scripts/workflow-state.py --worktree-path "$WT" append issues <N>   # per issue
python scripts/workflow-state.py --worktree-path "$WT" set work_type <type>
python scripts/workflow-state.py --worktree-path "$WT" set launch_command "<confirmed-command>"
```

### Step 6: Spawn the Background Session

Gather context (6a), build prompt (6b), spawn (6c), register (6d). Registration after spawn so the daemon short ID is recorded on first write.

#### 6a: Gather context

For each issue: `gh issue view <N> --json title,body`. Capture `/investigate` findings and user intent verbatim. Skip if no issues and no investigation context.

#### 6b: Build launch prompt

Read REFERENCE.md §4 "Launch Prompt Structure" for the field set. Target 1–5K chars; hard cap 30K. Rules: relative paths; critical overrides inline; no PowerShell escaping needed (argv delivery).

#### Prompt appendix — workflow contract

After all task brief fields, append the following workflow contract verbatim.
Fill placeholders before writing to the temp file:
- `<branch-name>` → branch name with slashes replaced by hyphens (e.g. `feat-my-feature`)
- `<worktree-path>` → absolute worktree path from Step 5

```
Workflow contract:
1. Read CLAUDE.md, specs/CONSTITUTION.md, .claude/interaction-patterns.md, and any
   skills referenced in the task brief above.
2. Run /design. Author spec at specs/<branch-name>.md and plan at .plans/<branch-name>.md.
   Use the branch name with slashes replaced by hyphens as the filename
   (e.g. branch feat/my-feature → specs/feat-my-feature.md).
   Spec must cover all acceptance criteria from the issue. Plan must cover all spec ACs.
3. Present spec + plan summary in your final message of this turn. STOP after /design.
   Set workflow-state:
     python scripts/workflow-state.py set phase blocked
     python scripts/workflow-state.py set needs "spec ready for review"
   The operator will attach via Claude Desktop, review, and approve via a reply.
4. After operator approval: run `python scripts/pipeline.py` (invoked from the worktree
   after /design commits the spec; pipeline resolves the spec path automatically).
   On failure: python scripts/pipeline.py --resume (or --from <stage>).
5. After `python scripts/pipeline.py` exits successfully (pipeline includes /pr
   internally): launch pr_monitor via Bash run_in_background=true:
     python scripts/pr_monitor.py --worktree <worktree-path> --pr <PR-number>
   Claude Code will re-engage you when pr_monitor exits.
6. At re-engagement: read .workflow/pr-monitor-result.json and produce a final summary
   covering actual PR state (ready / merged / escalated / error / blocked). Terminate.
```

#### 6c: Spawn

Write the prompt to a temp file using the **Write tool** — never a shell heredoc (see REFERENCE.md §8). Temp path: `$env:TEMP/start-prompt-<8-hex>.txt` on Windows, `/tmp/start-prompt-<8-hex>.txt` elsewhere. Invoke:

```bash
python scripts/start-bg-spawn.py \
  --worktree-abs "<worktree-absolute-path>" \
  --branch "<branch>" \
  --prompt-file "<temp-path>" \
  [--permission-mode bypassPermissions|acceptEdits|auto|default|dontAsk|plan] \
  [--model sonnet|opus|haiku|<full-model-id>]
```

Parse single-line stdout JSON for `short`, then delete the temp file. Exit: `0`=spawned (`{short,sessionId,cwd}`); `1`=caller error; `2`=daemon error. Surface stderr verbatim and stop on non-zero.

#### 6d: Register as In-Flight

```bash
python scripts/inflight-register.py \
  --session <short-from-6c> \
  --branch feat/<name> --worktree .worktrees/<name> \
  --issue <N> --area <area> --intent "<short description>"
```

Run from main repo root. Idempotent on `--branch`. Deregistration handled by `/cleanup`.

### Step 7: Return Control to User

Summarize and return control per REFERENCE.md §5 — the new session is visible in Agent View on every Claude Code surface. On non-zero from the helper: surface stderr verbatim and stop.

## Rules

1. Works from any branch — resolve main repo root automatically.
2. Always confirm name/issues/work-type/launch-command; wait for approval.
3. No duplicate worktrees — check before creating, offer resume.
4. Workflow state — always initialize in the new worktree.
5. Freeform input — never require structured flags.
6. Inline prompt handoff via `claude --bg` — no handoff file.
7. Return control after launch — do not continue the work once the session spawns.
