# Commit

Create a phase-aware intermediate commit during long sessions.

## Usage

`/commit` - Auto-detect phase and create appropriate commit
`/commit --phase planning` - Force planning phase commit
`/commit --phase implementation` - Force implementation phase commit
`/commit --phase testing` - Force testing phase commit

## Purpose

Create recovery checkpoints during long work sessions. Unlike `/ship`, this:
- Does NOT push to remote
- Does NOT create a PR
- Just commits locally for safety

## Process

### 1. Detect Current Phase

Analyze the work context to determine phase:

| Indicator | Phase |
|-----------|-------|
| Plan file exists, no implementation | `planning` |
| Code changes, no test changes | `implementation` |
| Test files changed | `testing` |

### 2. Stage All Changes

```bash
git add -A
```

### 3. Generate Commit Message

Based on detected phase:

**Planning Phase:**
```
chore(issue-N): planning complete

- Explored codebase structure
- Identified files to modify
- Created implementation plan

Co-Authored-By: Claude Opus 4.5 <noreply@anthropic.com>
```

**Implementation Phase:**
```
feat/fix(scope): brief description

- Key change 1
- Key change 2

Co-Authored-By: Claude Opus 4.5 <noreply@anthropic.com>
```

**Testing Phase:**
```
test(scope): add tests for feature

- Test case 1
- Test case 2

Co-Authored-By: Claude Opus 4.5 <noreply@anthropic.com>
```

### 4. Create Commit

```bash
git commit -m "$(cat <<'EOF'
<generated message>
EOF
)"
```

## Output

```
Commit
======
[✓] Phase detected: implementation
[✓] Files staged: 5
[✓] Committed: feat(plugins): add registration service

Checkpoint saved. Continue working or run /ship when ready.
```

## When to Use

Use `/commit` at natural checkpoints:

| Checkpoint | Why |
|------------|-----|
| Plan complete | Save exploration work before coding |
| Feature implemented | Save code before running tests |
| Tests passing | Save working state before shipping |

## When NOT to Use

- **Ready to ship** - Use `/ship` instead (handles push + PR)
- **No changes** - Nothing to commit
- **Want to push** - `/commit` is local only

## Recovery Benefits

If a session crashes or you need to restart:

1. Work is saved locally
2. Can continue from last checkpoint
3. Review incremental progress via `git log`

## Commit Guidelines

Follows conventional commits:
- `feat`: New feature
- `fix`: Bug fix
- `chore`: Maintenance (planning, refactoring)
- `test`: Test additions
- `docs`: Documentation

Always includes `Co-Authored-By` trailer for attribution.

## Example Session Flow

```
/start-work 123
    ↓
[Explore codebase, create plan]
    ↓
/commit                          ← "chore(issue-123): planning complete"
    ↓
[Implement feature]
    ↓
/commit                          ← "feat(auth): add token refresh"
    ↓
[Run tests, fix failures]
    ↓
/commit                          ← "test(auth): add token refresh tests"
    ↓
/ship                            ← Final commit + push + PR
```
