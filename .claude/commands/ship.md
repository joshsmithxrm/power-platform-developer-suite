# Ship

Complete work: validate, commit, push, create PR, and handle CI/bot feedback autonomously.

## Usage

`/ship` - Full autonomous shipping flow
`/ship --amend` - Amend last commit, force push, update PR
`/ship --draft` - Create as draft PR

## Full Workflow

```
/ship
    ↓
Pre-PR Validation (absorbed from /pre-pr)
    ↓
Commit changes
    ↓
Push to remote
    ↓
Create PR
    ↓
Wait for CI
    ↓
If CI fails: Debug and fix (up to 3 attempts)
    ↓
If bot comments: Triage and address
    ↓
Update session status to pr_ready
```

## Process

### 1. Pre-PR Validation

Run all validation checks before creating the PR:

**Build & Test:**
```bash
dotnet build -c Release --warnaserror
dotnet test --no-build -c Release
```

**Test Coverage:**
```powershell
.\scripts\Test-NewCodeCoverage.ps1
```

If tests are missing, write them (not stubs).

**Base Branch Check:**
```bash
git fetch origin
git rev-list --count HEAD..origin/main
```

If behind, rebase onto origin/main.

**Scaffolding Cleanup:**
```bash
git rm .claude/design.md 2>/dev/null || true
git rm docs/tui/POLISH_TRACKER.md 2>/dev/null || true
```

### 2. Commit (if needed)

If uncommitted changes exist:
1. Stage all: `git add -A`
2. Generate conventional commit message
3. Commit with `Co-Authored-By: Claude Opus 4.5 <noreply@anthropic.com>`

### 3. Push (if needed)

```bash
git push -u origin "$(git rev-parse --abbrev-ref HEAD)"
```

If `--amend` was used:
```bash
git push --force-with-lease
```

### 4. Create PR (if none exists)

```bash
gh pr create --title "PR title" --body "$(cat <<'EOF'
## Summary
<bullet points>

## Test plan
- [ ] Unit tests pass
- [ ] Manual testing completed

Closes #N

🤖 Generated with [Claude Code](https://claude.com/claude-code)
EOF
)"
```

### 5. Wait for All Automated Reviews

After PR creation, poll for all automated reviewers to complete:

```bash
# 1. Wait for CI to complete
gh pr checks --watch

# 2. Poll for Copilot review
gh api repos/{owner}/{repo}/pulls/{pr}/reviews \
  --jq '.[] | select(.user.login | test("copilot|github-copilot"))'

# 3. Poll for Gemini review
gh api repos/{owner}/{repo}/pulls/{pr}/reviews \
  --jq '.[] | select(.user.login | test("gemini|google-gemini"))'

# 4. Check for CodeQL alerts
gh api "repos/{owner}/{repo}/code-scanning/alerts?ref=refs/pull/{pr}/merge&state=open"
```

**Wait until ALL of these complete before proceeding to fix issues:**
- GitHub Actions CI
- Copilot review comments (if enabled)
- Gemini Code Assist review (if enabled)
- CodeQL security scan

**If CI fails (up to 3 attempts):**

1. Fetch failed logs:
```bash
gh run view [run-id] --log-failed
```

2. Analyze failure patterns:
| Pattern | Likely Cause | Action |
|---------|--------------|--------|
| Test hung | DPAPI/SecureCredentialStore | Check env vars |
| `TimeoutException` | CLI timeout | Fix infinite loop |
| `JsonException` | Output format mismatch | Update test |
| Test assertion failed | Implementation bug | Fix code |

3. Apply fix and push
4. Wait for CI again

After 3 failed attempts, update session status to `stuck` and escalate.

### 6. Handle Bot Comments

After CI passes (or while waiting), check for bot comments:

```bash
# PR comments from bots
gh api repos/{owner}/{repo}/pulls/{pr}/comments \
  --jq '.[] | select(.user.login | test("gemini|Copilot|copilot|github-advanced"))'

# Code scanning alerts
gh api "repos/{owner}/{repo}/code-scanning/alerts?ref=refs/pull/{pr}/merge&state=open"
```

**For each finding, determine verdict:**

| Verdict | Action |
|---------|--------|
| **Valid** | Fix code |
| **False Positive** | Reply with explanation |
| **Duplicate** | Same reply as original |

**For code scanning alerts:**
- If fixing: Make change, alert auto-closes
- If dismissing:
```bash
gh api repos/{owner}/{repo}/code-scanning/alerts/{number} \
  -X PATCH -f state=dismissed -f dismissed_reason="false positive"
```

**Reply to each comment:**
```bash
gh api repos/{owner}/{repo}/pulls/{pr}/comments/{id}/replies \
  -f body="Fixed in {sha}"
```

**Resolve threads:**
```bash
gh api graphql -f query='mutation { resolveReviewThread(input: {threadId: "ID"}) { thread { isResolved } } }'
```

### 7. Update Session Status

After all checks pass:

Update `~/.ppds/sessions/work-{issue}.json`:
```json
{
  "status": "pr_ready",
  "pr": "https://github.com/.../pull/123",
  "lastUpdate": "<ISO-timestamp>"
}
```

## Output

```
Ship
====
[✓] Build: PASS
[✓] Tests: 47 passed
[✓] Coverage: OK
[✓] Base branch: Up to date
[✓] Committed: feat(plugins): add registration service
[✓] Pushed: feature/plugin-service -> origin
[✓] PR: https://github.com/.../pull/123
[⏳] Waiting for CI...
[✓] CI: PASS
[✓] Bot comments: 2 addressed
[✓] Session status updated: pr_ready

Ready for review!
```

## Autonomous Limits

| Situation | Autonomous? | Escalate After |
|-----------|-------------|----------------|
| Test failures | Yes | 5 fix attempts |
| CI failures | Yes | 3 fix attempts |
| Bot comments - obvious fix | Yes | - |
| Bot comments - unclear | No | Immediately ask |
| Build errors | Yes | 3 fix attempts |

## What NOT To Do

- Don't merge the PR (human gate)
- Don't skip failing tests
- Don't dismiss valid security findings
- Don't push to main directly

## When to Use

- After completing feature work
- After `/test` passes
- When ready to create PR

## Related Commands

| Command | Purpose |
|---------|---------|
| `/start-work` | Begin work session |
| `/test` | Run tests before shipping |
| `/prune` | Clean up after merge |

## Reference

- [Autonomous Session](.claude/workflows/autonomous-session.md)
- [Human Gates](.claude/workflows/human-gates.md)
