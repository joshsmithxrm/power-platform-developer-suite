---
name: start
description: Create a worktree for new work and open a terminal there. Use when starting any new feature, bug fix, or task — including when the user says "start a worktree", "create a worktree", or describes work they want to begin. Accepts freeform input — issues, descriptions, context. Runs from main.
---

# Start

Bootstrap a feature worktree for new work. Parses freeform input to extract a name and issue numbers, creates the worktree, initializes workflow state, and opens a terminal in the worktree directory.

## When to Use

- "Start a worktree for..."
- "Create a worktree for..."
- "Set up a worktree"
- "I need to work on..." (when no worktree exists yet)
- "Let's start on..."
- "Begin work on..."
- "New feature/bug/task for..."
- Any request that implies beginning new work in a fresh worktree

## Usage

`/start` — with freeform description in the prompt
`/start I need to work on env var auth, issues 40 and 659`
`/start plugin registration v1 completion #660 #63 #65 #68 #427`
`/start fix the CSS class inconsistency in SolutionsPanel`

## Prerequisites

- Git repository with worktree support. Works from main or any feature branch/worktree.

## Process

### Step 1: Determine Repo Root

```bash
git rev-parse --abbrev-ref HEAD
```

If on `main` or `master`: proceed normally (create worktree from current directory).

If on a feature branch or in a worktree: resolve the main repo root:
```bash
git worktree list --porcelain
```
Parse the first entry's `worktree` line — that's the main repo root. Run all worktree creation commands from that root directory (pass as `cwd` to Bash tool).

### Step 2: Parse Input

From `$ARGUMENTS`, extract:

**Name:** Derive a kebab-case worktree name from the key nouns in the input. Examples:
- "env var auth and CSS fix" → `env-var-auth`
- "plugin registration v1 completion" → `plugin-registration-v1`
- "fix the CSS class inconsistency" → `css-fix`

**Issues:** Extract numbers that appear to be issue references. Match patterns:
- `#NNN` → issue NNN
- `issue NNN` or `issues NNN` → issue NNN
- Bare numbers near context words (bug, fix, feat, issue) → issue NNN

### Step 2b: Work-Type Classification

If issues were extracted in Step 2, attempt to classify the work type from labels:

```bash
gh issue view <N> --json labels --jq '.labels[].name'
```

Map labels to work type:
- `type:bug` → bug fix
- `type:enhancement`, `type:refactor`, `type:performance` → enhancement
- `type:docs` → docs

If multiple issues have different label types, use the most common. If tied, leave pre-selection empty.

If no labels found, `gh` not authenticated, or no issues extracted — leave pre-selection empty.

### Step 3: Propose and Confirm

Determine the claude launch command based on work type:
- **Bug fix:** `claude` (no auto-skill — user codes the fix manually)
- **Enhancement/refactor:** `claude '/implement'`
- **New feature:** `claude '/design'`
- **Docs:** `claude` (no auto-skill — user edits docs manually)

If no work type is pre-selected (Step 2b left it empty), show `Launch: (depends on work type)` and resolve the launch command after the user picks a work type.

Present the extracted name, issues, work type, and launch command to the user:

```
I'll create:
  Worktree:  .worktrees/<name>
  Branch:    feat/<name>
  Issues:    #N, #M
  Work type: (1) Bug fix  (2) Enhancement/refactor  (3) New feature  (4) Docs
             [pre-selected: Bug fix based on type:bug label]
  Launch:    <claude-command>

Good?
```

Wait for user confirmation. If the user suggests a different name, work type, or launch command, use that instead. The confirmed launch command is used in Steps 5, 6, and 7 — do not re-derive it from work type.

### Step 4: Check for Existing Worktree

```bash
ls -d .worktrees/<name> 2>/dev/null
```

If the worktree already exists:
- Show the existing branch name: `git -C .worktrees/<name> rev-parse --abbrev-ref HEAD`
- Ask: "Found existing worktree `.worktrees/<name>` on branch `<branch>`. Resume it, or create a new one with a different name?"
- If resume: skip creation, proceed to Step 6 (open terminal)
- If new: ask for an alternative name, loop back to Step 4

### Step 4b: Check for In-Flight Conflicts

Before creating the worktree, check whether another concurrent session is
already working on the same issue or area (prevents the duplicate-work
pattern that produced #802):

```bash
# For each extracted issue:
python scripts/inflight-check.py --issue <N>

# Plus the primary code area implied by the work (best-effort guess from
# the worktree name or first known affected path):
python scripts/inflight-check.py --area <area>
```

If exit code is `1`, the script prints a JSON list of conflicting
sessions on stdout. Show the operator the conflicting `session_id`,
`branch`, `intent`, and `started` timestamp, and ask:

> "Session `<id>` is already working on this. Continue anyway, coordinate
> with that session, or abort?"

Do not silently proceed. If the operator chooses to continue, fall
through to Step 5; otherwise stop here.

### Step 5: Create Worktree and Initialize State

```bash
# Check if branch already exists
git branch --list "feat/<name>"

# Create worktree (omit -b if branch exists)
git worktree add .worktrees/<name> -b feat/<name>
# or if branch exists:
git worktree add .worktrees/<name> feat/<name>

# Initialize workflow state in the worktree
python scripts/workflow-state.py init "feat/<name>"
python scripts/workflow-state.py set phase starting
```

For each extracted issue number:
```bash
python scripts/workflow-state.py append issues <N>
```

Record the confirmed work type and launch command:
```bash
python scripts/workflow-state.py set work_type <type>
python scripts/workflow-state.py set launch_command "<confirmed-claude-command>"
```

Run these commands from within the worktree directory (use the `cwd` parameter when executing via Bash tool).

#### Step 5c: Register the Session as In-Flight

After workflow state is initialized, announce this session in the cross-
session in-flight registry so sibling sessions can detect overlap:

```bash
python scripts/inflight-register.py --branch feat/<name> --worktree .worktrees/<name> --issue <N> --area <area> --intent "<short description>"
```

Repeat `--issue` / `--area` flags as needed. Run from the main repo
root (the file lives at `.claude/state/in-flight-issues.json` and is
shared across all worktrees).

Deregistration is handled by `/cleanup` after the branch merges.

### Step 6: Launch New Session with Inline Prompt

Construct a complete launch prompt from the current conversation and deliver it inline to the new session via `pwsh Start-Process` + here-string. **No context file is written.** The prompt is the entire handoff payload.

Why inline: file-based handoffs (`.plans/context.md`) were observed being ignored or deprioritized by the receiving session. Content in the initial user prompt receives higher attention weight and is followed reliably.

#### 6a: Gather conversation context

From the current conversation, collect:

- **Issue details** — for each extracted issue, run `gh issue view <N> --json title,body` and capture title + body. If `gh` is unauthenticated or fails, fall back to issue numbers only and warn the user.
- **Investigation context** — if the conversation contains output from a prior `/investigate` session (findings, options, decisions), capture the relevant portions verbatim.
- **User intent** — any specific instructions, constraints, or scope the user stated in the original `/start` input or follow-up messages.

Skip 6a if no issues and no investigation context — the prompt will contain only the task name and routing.

#### 6b: Build the launch prompt

Target 1–5K chars. Hard cap 30K (PowerShell/CreateProcess limit is ~32K). Structure:

```
Task: <one-sentence description of the work>

Branch: feat/<name>
Worktree: .worktrees/<name>
Work type: <confirmed work type>
Issues: #N, #M

Issue details:

### #N: <title>
<body>

### #M: <title>
<body>

Investigation context (from prior /investigate):
<verbatim relevant excerpts, if any>

User intent:
<anything the user stated that the new session needs to know>

First action: <routing instruction based on confirmed launch command — see table below>

Project context:
- Constitution: specs/CONSTITUTION.md
- Spec template: specs/SPEC-TEMPLATE.md
- Tech stack and conventions: CLAUDE.md
```

Routing instruction by launch command:
- `claude` (bug fix) → "Reproduce the bug, write a regression test that fails, fix the code to make it pass, then run `/gates` → `/verify` → `/pr`."
- `claude '/implement'` → "Read the spec and plan referenced above, then invoke `/implement` to execute end-to-end."
- `claude '/design'` → "Invoke `/design` to size the work and route to `/spec` → `/plan` → `/implement`."
- `claude` (docs) → "Edit the relevant docs and commit. No design or implement needed. When done: `/pr`."

**Prompt rules:**
- No `'@` at the start of a line in prompt content — that exact sequence terminates PowerShell's single-quoted here-string (`@' ... '@`). Apostrophes mid-line (`it's`, `Claude's`, `don't`) are fine. If a line must literally begin with `'@`, prefix it with a space or reword.
- All paths relative to worktree root unless the reference is outside it.
- Critical overrides go directly in the prompt — do not bury them in referenced files.

#### 6c: Write launch script and spawn new window

Platform detection:

```bash
uname -s
```

**Windows (MINGW/MSYS) — primary path:**

1. Create launch-script directory: `mkdir -p "$LOCALAPPDATA/ppds"`
2. Resolve the worktree absolute Windows path: `cygpath -w "<worktree-absolute-path>"`
3. Resolve the claude binary's full Windows path: `cygpath -w "$(which claude)"` — node version managers (fnm, nvm, volta) use session-scoped shims that may not persist in the spawned shell, so embed the absolute path.
4. Write `$LOCALAPPDATA/ppds/launch-<name>.ps1` with a PowerShell single-quoted here-string (`@' ... '@`) wrapping the prompt content verbatim:

   ```powershell
   Set-Location -Path "<worktree-windows-path>"

   $prompt = @'
   <full prompt content from 6b, verbatim>
   '@

   & "<claude-absolute-windows-path>" $prompt
   ```

   Use the Write tool to produce this file — do not shell-escape the prompt content manually.

5. Spawn a new console window by invoking `Start-Process` from the current shell:

   ```bash
   pwsh -Command "Start-Process pwsh -ArgumentList '-NoExit','-File','<launch-script-windows-path>'"
   ```

   `Start-Process` opens a new console; on Windows 11 with Windows Terminal as default, the OS delegates to WT automatically.

**Windows fallbacks, in order:**

- If `pwsh` is not on PATH or execution policy is `Restricted`/`AllSigned` (check with `pwsh -Command "Get-ExecutionPolicy"`), skip to manual fallback.
- If running under WSL2 (`uname -r` contains `microsoft`), do not use WSL-local pwsh — fall through to manual fallback.

**Linux/Mac:** No reliable cross-distro terminal launch. Print the manual fallback (see 6d).

#### 6d: Manual fallback

If the launcher fails or the platform has no reliable terminal-spawn path, print:

```
Could not open a new terminal automatically. Open PowerShell and run:

  cd <worktree-absolute-path>
  $prompt = @'
  <full prompt content>
  '@
  claude $prompt
```

On Linux/Mac, substitute shell-appropriate syntax (bash here-doc).

### Step 7: Return Control to User

After the new session is launched (or the manual fallback printed), summarize what happened and return control. The new session is self-contained — the inline prompt carries issues, investigation context, work-type routing, and the first-action instruction. Do NOT continue the work in this session.

```
Worktree ready at .worktrees/<name> (branch feat/<name>)
Issues linked: #N, #M
Work type: <work type>
Prompt: <chars> chars delivered inline to new session

New session spawned. It will <first-action from routing table>.
This session's job is done — continue in the new window.
```

If the launcher fell through to the manual fallback, replace the "New session spawned" line with:

```
Could not spawn a new window automatically. Manual launch command printed above — paste it into a PowerShell terminal to start the session.
```

Do not re-derive or restate the first-action instructions — the prompt already contains them. The user only needs to know the session is ready and where to find it.

## Rules

1. **Works from any branch** — if on a feature branch, resolves main repo root automatically.
2. **Always confirm** — propose name, issues, work type, and launch command, wait for user approval.
3. **No duplicate worktrees** — check before creating, offer resume.
4. **Platform detection** — use `uname -s`, not hardcoded assumptions.
5. **Workflow state** — always initialize state in the new worktree.
6. **Freeform input** — never require structured flags. Parse what the user gives you.
7. **Labels are hints** — work-type pre-selection from labels is a convenience; user confirms.
8. **Inline prompt handoff — no context files.** The new session receives its task, issues, investigation context, and routing via an inline CLI-argument prompt. Do not write `.plans/context.md` or any other handoff file — file-based handoffs are deprioritized by the receiving session.
9. **Return control after launch** — this session does not continue the work once the new session is spawned.
