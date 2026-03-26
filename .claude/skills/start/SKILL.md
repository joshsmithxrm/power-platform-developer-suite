---
name: start
description: Create a worktree for new work and open a terminal there. Use when starting any new feature, bug fix, or task. Accepts freeform input — issues, descriptions, context. Runs from main.
---

# Start

Bootstrap a feature worktree for new work. Parses freeform input to extract a name and issue numbers, creates the worktree, initializes workflow state, and opens a terminal in the worktree directory.

## Usage

`/start` — with freeform description in the prompt
`/start I need to work on env var auth, issues 40 and 659`
`/start plugin registration v1 completion #660 #63 #65 #68 #427`
`/start fix the CSS class inconsistency in SolutionsPanel`

## Prerequisites

- Must be on `main` branch. If on a feature branch, you're already in a worktree — just run `/design` or `/implement`.

## Process

### Step 1: Validate Branch

```bash
git rev-parse --abbrev-ref HEAD
```

If not on `main` or `master`: error with "You're already on branch `<name>`. Run `/design` or `/implement` from here."

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

### Step 3: Propose and Confirm

Present the extracted name and issues to the user:

```
I'll create:
  Worktree: .worktrees/<name>
  Branch:   feat/<name>
  Issues:   #N, #M

Good?
```

Wait for user confirmation. If the user suggests a different name, use that instead.

### Step 4: Check for Existing Worktree

```bash
ls -d .worktrees/<name> 2>/dev/null
```

If the worktree already exists:
- Show the existing branch name: `git -C .worktrees/<name> rev-parse --abbrev-ref HEAD`
- Ask: "Found existing worktree `.worktrees/<name>` on branch `<branch>`. Resume it, or create a new one with a different name?"
- If resume: skip creation, proceed to Step 6 (open terminal)
- If new: ask for an alternative name, loop back to Step 4

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
```

For each extracted issue number:
```bash
python scripts/workflow-state.py append issues <N>
```

Run these commands with `cwd` set to the worktree path (pass via `-C` flag or absolute path to `workflow-state.py`).

### Step 6: Open Terminal

Detect platform:

```bash
uname -s
```

**Windows (MINGW/MSYS):**
```bash
start pwsh -NoExit -WorkingDirectory "<absolute-path-to-worktree>"
```

**Linux/Mac:** No reliable cross-distro terminal launch. Print instructions:
```
Worktree created. Open a terminal there:
  cd <path-to-worktree>
  claude
```

### Step 7: Print Guidance

After terminal launch (or if launch fails, after printing cd instructions):

```
Worktree ready at .worktrees/<name> (branch feat/<name>)
Issues linked: #N, #M

Terminal opened. Run `claude` then `/design` to start.
```

If terminal launch failed:
```
Worktree ready at .worktrees/<name> (branch feat/<name>)
Issues linked: #N, #M

Could not open terminal automatically. Run:
  cd .worktrees/<name>
  claude

Then run `/design` to start.
```

## Rules

1. **Main branch only** — refuse to run on feature branches.
2. **Always confirm** — propose name and issues, wait for user approval.
3. **No duplicate worktrees** — check before creating, offer resume.
4. **Platform detection** — use `uname -s`, not hardcoded assumptions.
5. **Workflow state** — always initialize state in the new worktree.
6. **Freeform input** — never require structured flags. Parse what the user gives you.
