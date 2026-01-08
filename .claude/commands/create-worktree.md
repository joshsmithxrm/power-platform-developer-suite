# Create Worktree

Create a git worktree for ad-hoc work that isn't tied to an existing GitHub issue.

## Usage

`/create-worktree <description> [options]`

Examples:
- `/create-worktree "add authentication caching"` - Creates feature/add-authentication-caching
- `/create-worktree "fix null pointer in bulk ops" --fix` - Forces fix/ prefix
- `/create-worktree "update ADR for file formats" --issue` - Also creates GitHub issue

## Arguments

`$ARGUMENTS` - Description of the work (required), optionally followed by flags

## Options

| Option | Description |
|--------|-------------|
| `--feature` | Force `feature/` prefix |
| `--fix` | Force `fix/` prefix |
| `--docs` | Force `docs/` prefix |
| `--chore` | Force `chore/` prefix |
| `--issue` | Also create GitHub issue with the description |
| `--name <name>` | Override generated branch/folder name |

## Process

### 1. Parse Arguments

Extract description and flags from `$ARGUMENTS`.

If no description provided:
```
Error: Description required.

Usage: /create-worktree "description of work" [options]
```

### 2. Infer Branch Type

Unless explicit flag provided, infer from description:

| Description contains | Inferred type |
|---------------------|---------------|
| "add", "implement", "create", "new" | `feature/` |
| "fix", "bug", "broken", "error", "issue" | `fix/` |
| "doc", "readme", "adr", "guide", "update doc" | `docs/` |
| "refactor", "clean", "rename", "reorganize" | `chore/` |
| Default | `feature/` |

### 3. Generate Names

**Branch name:** `{type}/{slugified-description}`
- Lowercase
- Spaces → hyphens
- Remove special characters
- Max 50 chars

**Folder name:** `ppds-{short-slug}`
- Use 2-3 key words from description
- Example: "add authentication caching" → `ppds-auth-caching`

Example transformations:
| Description | Branch | Folder |
|-------------|--------|--------|
| "add authentication caching" | `feature/add-authentication-caching` | `ppds-auth-caching` |
| "fix null pointer in bulk ops" | `fix/null-pointer-bulk-ops` | `ppds-null-bulk-ops` |
| "update ADR for file formats" | `docs/adr-file-formats` | `ppds-adr-file-formats` |

### 4. Validate

Check that branch and folder don't already exist:

```bash
# Check branch
git branch --list "<branch-name>"
git branch -r --list "origin/<branch-name>"

# Check folder
test -d ../<folder-name>
```

If exists:
```
Error: Branch 'feature/add-authentication-caching' already exists.
Use --name <different-name> to specify a different name.
```

### 5. Check Current Branch

```bash
git branch --show-current
```

If not on `main`:
```
Warning: Currently on branch '{current}', not 'main'.
Worktree will be based on 'main' regardless.
Continue? (yes/no)
```

### 6. Create Worktree

```bash
git worktree add -b <branch-name> ../<folder-name> main
```

### 7. Optional: Create Issue

If `--issue` flag provided:

```bash
gh issue create \
  --title "<description>" \
  --body "Created via /create-worktree

Branch: \`<branch-name>\`
Folder: \`<folder-name>\`"
```

Capture issue number from output.

### 8. Output Summary

```
================================================================================
WORKTREE CREATED
================================================================================

Branch:  <branch-name>
Folder:  ../<folder-name>
Issue:   #<number> (if created)

--------------------------------------------------------------------------------
NEXT STEPS
--------------------------------------------------------------------------------

1. Navigate to worktree:
   cd ../<folder-name>

2. Start Claude:
   claude

3. Enter plan mode to flesh out your session context

--------------------------------------------------------------------------------

Quick start:
  cd ../<folder-name> && claude
```

## When to Use

- Starting work that isn't tied to a GitHub issue yet
- Quick experiments or explorations
- Ad-hoc documentation updates
- Work that may become an issue later (use `--issue` flag)

## When NOT to Use

- Working on existing GitHub issues → use `/plan-work`
- Quick fixes on current branch → just make the changes directly

## Related Commands

| Command | Purpose |
|---------|---------|
| `/plan-work` | Create worktrees from GitHub issues |
| `/start-work` | Begin session in prepared worktree |
| `/prune` | Clean up merged worktrees |

## Edge Cases

**Empty description:**
```
Error: Description required.
```

**Branch already exists:**
```
Error: Branch 'feature/xyz' already exists.
Options:
  - Use --name <different-name> to specify a different name
  - Or work in existing branch: cd ../<existing-folder>
```

**Folder already exists:**
```
Error: Folder '../ppds-xyz' already exists.
Use --name <different-name> to specify a different name.
```

**Not in git repository:**
```
Error: Must be in a git repository to create worktree.
```

**No remote configured:**
```
Warning: No remote 'origin' configured.
Worktree created locally. Push branch when ready.
```
