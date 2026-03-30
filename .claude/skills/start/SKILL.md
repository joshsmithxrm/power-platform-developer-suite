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

Wait for user confirmation. If the user suggests a different name, work type, or launch command, use that instead. The confirmed launch command is used in Steps 5, 5b, 6, and 7 — do not re-derive it from work type.

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

### Step 5b: Write Context File

Write `.plans/context.md` to the new worktree with issue details and routing guidance:

1. Create `.plans/` directory in the new worktree:
   ```bash
   mkdir -p .worktrees/<name>/.plans
   ```

2. For each extracted issue, fetch details:
   ```bash
   gh issue view <N> --json title,body
   ```

3. Write `.plans/context.md` with the following structure:
   ```markdown
   # Context: <name>

   ## Issues
   ### #N: <title>
   <body>

   ## Work Type
   <confirmed work type>

   ## Recommended Next Step
   <routing guidance based on work type>
   ```

   Routing guidance values (derived from the confirmed launch command, not re-derived from work type):
   - `claude` (bug fix) → "Code the fix + regression test, then run `/gates` → `/verify` → `/pr`"
   - `claude '/implement'` → "Run `/implement`"
   - `claude '/design'` → "Run `/design`"
   - `claude` (docs) → "Edit docs and commit. No design or implement needed. When done: `/pr`"

4. If the conversation contains investigation context from a prior `/investigate` session, include it in the same `.plans/context.md` file under a `## Investigation Context` section.

5. If `gh` is not authenticated or issue fetch fails, write context with issue numbers and titles from args only. Warn the user.

If no issues and no investigation context in conversation — skip this step, no file written.

### Step 6: Open Terminal with Claude

Detect platform:

```bash
uname -s
```

Use the launch command confirmed in Step 3.

**Windows (MINGW/MSYS):**
```bash
start pwsh -NoExit -Command "Set-Location '<absolute-path-to-worktree>'; <claude-command>"
```

**Linux/Mac:** No reliable cross-distro terminal launch. Print instructions:
```
Worktree created. Open a terminal there:
  cd <path-to-worktree>
  <claude-command>
```

### Step 7: Print Guidance

After terminal launch (or if launch fails, after printing cd instructions):

**Bug fix:**
```
Worktree ready at .worktrees/<name> (branch feat/<name>)
Issues linked: #N, #M
Work type: Bug fix

Claude opened in worktree. Code the fix + regression test.
When done: `/gates` → `/verify` → `/pr`
```

**Enhancement/refactor:**
```
Worktree ready at .worktrees/<name> (branch feat/<name>)
Issues linked: #N, #M
Work type: Enhancement/refactor

Claude launched with /implement in worktree.
```

**New feature:**
```
Worktree ready at .worktrees/<name> (branch feat/<name>)
Issues linked: #N, #M
Work type: New feature

Claude launched with /design in worktree.
```

**Docs:**
```
Worktree ready at .worktrees/<name> (branch feat/<name>)
Issues linked: #N, #M
Work type: Docs

Claude opened in worktree. Edit docs and commit.
When done: `/pr`
```

If terminal launch failed, replace the launch confirmation with:
```
Could not open terminal automatically. Run:
  cd .worktrees/<name>
  <claude-command>
```

## Rules

1. **Works from any branch** — if on a feature branch, resolves main repo root automatically.
2. **Always confirm** — propose name, issues, work type, and launch command, wait for user approval.
3. **No duplicate worktrees** — check before creating, offer resume.
4. **Platform detection** — use `uname -s`, not hardcoded assumptions.
5. **Workflow state** — always initialize state in the new worktree.
6. **Freeform input** — never require structured flags. Parse what the user gives you.
7. **Labels are hints** — work-type pre-selection from labels is a convenience; user confirms.
8. **Context file** — write `.plans/context.md` with issue details and routing guidance.
