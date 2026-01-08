# Create Worktree

Create a git worktree and get to work.

## Usage

`/create-worktree <description> [--fix|--docs|--chore]`

## Arguments

`$ARGUMENTS` - Description of the work, optionally followed by a type flag

## Process

### 1. Parse and Slug

Extract description from `$ARGUMENTS`. Create slug:
- Lowercase
- Spaces/special chars → hyphens
- Strip leading/trailing hyphens

Default prefix: `feature/`
Override with: `--fix`, `--docs`, `--chore`

### 2. Fetch and Create

```bash
git fetch origin main
git worktree add -b <prefix>/<slug> ../ppds-<slug> origin/main
```

### 3. Output

```
cd ../ppds-<slug> && claude
```

That's it. Copy the command, paste it, start working.
