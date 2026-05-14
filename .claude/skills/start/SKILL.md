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

```bash
git rev-parse --abbrev-ref HEAD
```

If on main/master: proceed. If on a feature branch or worktree: resolve root via `git worktree list --porcelain` (first entry's `worktree` line). Run all creation commands from that root.

### Step 2: Parse Input

From `$ARGUMENTS` extract:
- **Name:** kebab-case from key nouns. Read REFERENCE.md §1 "Name Derivation Examples" for guidance.
- **Issues:** `#NNN`, `issue NNN`, or bare numbers near bug/fix/issue context words.

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

```bash
ls -d .worktrees/<name> 2>/dev/null
```

If exists: show current branch (`git -C .worktrees/<name> rev-parse --abbrev-ref HEAD`), ask "Resume or create with different name?"
- If resume: skip creation, proceed to Step 6 (spawn background session)
- If new: ask for alternative name, loop back to Step 4

### Step 4b: Check for In-Flight Conflicts

```bash
python scripts/inflight-check.py --issue <N>   # repeat per issue
python scripts/inflight-check.py --area <area>
```

Exit code 1 → show conflicting session (id, branch, intent, started), ask continue/coordinate/abort. Do not silently proceed.

### Step 5: Create Worktree and Initialize State

Use `worktree-create.py` — do NOT open-code `git worktree add`. Read REFERENCE.md §3 "worktree-create.py Rationale" for safety properties and exit code detail.

```bash
python scripts/worktree-create.py --name <name>
```

Stop on non-zero and surface stderr verbatim.

Initialize state (run from worktree directory):
```bash
python scripts/workflow-state.py init "feat/<name>"
python scripts/workflow-state.py set phase starting
python scripts/workflow-state.py append issues <N>   # repeat per issue
python scripts/workflow-state.py set work_type <type>
python scripts/workflow-state.py set launch_command "<confirmed-command>"
```

### Step 6: Spawn the Background Session

Gather context (6a), build prompt (6b), spawn (6c), register (6d). Registration happens **after** spawn so the daemon short ID is recorded on first write — no re-invocation.

#### 6a: Gather context

For each issue: `gh issue view <N> --json title,body`. Capture `/investigate` findings and user intent verbatim. Skip if no issues and no investigation context.

#### 6b: Build launch prompt

Read REFERENCE.md §4 "Launch Prompt Structure" for the field set. Target 1–5K chars; hard cap 30K. Rules: relative paths; critical overrides inline; no PowerShell escaping needed (argv delivery).

#### 6c: Spawn

Write the prompt to a temp file using the **Write tool** — never a shell heredoc. A heredoc terminator (`PROMPT_EOF`) appearing as a line inside the prompt would close the heredoc early; writing via the Write tool is byte-exact and immune to terminator collisions.

1. Choose a temp path: `$env:TEMP/start-prompt-<8-hex>.txt` on Windows, `/tmp/start-prompt-<8-hex>.txt` elsewhere.
2. Use the Write tool to write the prompt verbatim to that path.
3. Invoke the helper:

```bash
python scripts/start-bg-spawn.py \
  --worktree-abs "<worktree-absolute-path>" \
  --branch "<branch>" \
  --prompt-file "<temp-path>"
```

4. Parse the single-line stdout JSON to extract `short`, then delete the temp file.

Exit: `0`=spawned (`{short,sessionId,cwd}`); `1`=caller error (version/PATH/arg/prompt); `2`=daemon error. Surface stderr verbatim and stop on non-zero.

#### 6d: Register as In-Flight

```bash
python scripts/inflight-register.py \
  --session <short-from-6c> \
  --branch feat/<name> --worktree .worktrees/<name> \
  --issue <N> --area <area> --intent "<short description>"
```

Run from main repo root. Repeat `--issue`/`--area` as needed. The register script is idempotent on `--branch` (it replaces any existing entry for the same branch), so re-running is safe but unnecessary here. Deregistration handled by `/cleanup`.

### Step 7: Return Control to User

Summarize and return control. The new session is visible in Agent View on every Claude Code surface. Read REFERENCE.md §5 "Return Summary Template" for the exact format. On non-zero from the helper: surface stderr verbatim and stop — no manual fallback.

## Rules

1. **Works from any branch** — resolve main repo root automatically.
2. **Always confirm** — name, issues, work type, launch command; wait for approval.
3. **No duplicate worktrees** — check before creating, offer resume.
4. **Platform detection** — use `uname -s`, not hardcoded assumptions.
5. **Workflow state** — always initialize in the new worktree.
6. **Freeform input** — never require structured flags.
7. **Labels are hints** — work-type pre-selection is a convenience; user confirms.
8. **Inline prompt handoff via `claude --bg`.** New session receives task via positional prompt argument. Do not write `.plans/context.md` or any handoff file.
9. **Return control after launch** — do not continue the work once the session spawns.
