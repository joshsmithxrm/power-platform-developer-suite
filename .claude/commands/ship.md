# Ship

Commit, push, and create PR in one command.

## Usage

`/ship` - Commit all changes, push, and create PR
`/ship --amend` - Amend last commit, force push, update PR

## Workflow

### 1. Check Current State

```bash
git status
git log --oneline -3
```

Determine what needs to happen:
- Uncommitted changes? -> Commit them
- Unpushed commits? -> Push them
- No PR exists? -> Create one

### 2. Commit (if needed)

If there are uncommitted changes:

1. Stage all changes: `git add -A`
2. Generate commit message from changes:
   - Use conventional commit format: `type(scope): description`
   - Types: feat, fix, docs, refactor, test, chore
   - Reference related issues with `Closes #N` (one per line)
3. Commit with the message

### 3. Push (if needed)

```bash
# Check if branch has upstream
git rev-parse --abbrev-ref --symbolic-full-name @{u} 2>/dev/null

# Push with upstream tracking
git push -u origin "$(git rev-parse --abbrev-ref HEAD)"
```

If `--amend` was used and commits were amended:
```bash
git push --force-with-lease
```

### 4. Create PR (if none exists)

Check for existing PR:
```bash
gh pr view --json number 2>/dev/null
```

If no PR exists, create one:

```bash
gh pr create --title "PR title from commit" --body "$(cat <<'EOF'
## Summary
<bullet points summarizing changes>

## Test plan
- [ ] Unit tests pass
- [ ] Manual testing completed

Closes #N

🤖 Generated with [Claude Code](https://claude.com/claude-code)
EOF
)"
```

### 5. Report Result

```
Ship Complete
=============
[✓] Committed: feat(plugins): add IPluginRegistrationService interface
[✓] Pushed: feature/service-extractions -> origin
[✓] PR: https://github.com/owner/repo/pull/123

Ready for review!
```

## Behavior

- **Smart detection**: Only performs steps that are needed
- **Safe defaults**: Never force pushes unless `--amend` specified
- **Issue linking**: Extracts issue numbers from branch name or prompts
- **Draft option**: Add `--draft` to create as draft PR

## Examples

```bash
# Standard ship - commit, push, create PR
/ship

# Amend and update existing PR
/ship --amend

# Create as draft PR
/ship --draft
```

## When to Use

- After completing a feature or fix
- After `/pre-pr` passes all checks
- When you want to stop typing `git add && git commit && git push && gh pr create`
