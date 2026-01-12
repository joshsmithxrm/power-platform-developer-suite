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
Detect session context (set status: shipping)
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
If bot comments: Triage and address (set status: reviews_in_progress)
    ↓
Update session status: complete (with PR URL)
```

## Process

### 0. Session Context Detection

**Check if running in a worker session:**
```bash
# Check for session prompt file
if [ -f ".claude/session-prompt.md" ]; then
  # Extract issue number from session prompt (first line format: "# Session: Issue #NNN")
  ISSUE_NUMBER=$(head -1 .claude/session-prompt.md | grep -oP '#\K\d+')
  echo "Running in session context for issue #$ISSUE_NUMBER"

  # Update status to shipping
  ppds session update --id "$ISSUE_NUMBER" --status shipping
fi
```

If running in a session, status updates will be called at key phases:
- `shipping` - at start of /ship
- `reviews_in_progress` - when handling bot comments
- `complete` - when PR is ready for human review

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
# Fetch latest from origin
git fetch origin

# Check how many commits we're behind origin/main
BEHIND_COUNT=$(git rev-list --count HEAD..origin/main)

if [ "$BEHIND_COUNT" -gt 0 ]; then
  echo "Branch is $BEHIND_COUNT commits behind origin/main. Rebasing..."
  git rebase origin/main

  # If rebase fails (conflicts), abort and escalate
  if [ $? -ne 0 ]; then
    git rebase --abort
    echo "ERROR: Rebase failed due to conflicts. Manual resolution required."
    exit 1
  fi
fi
```

**Important:** Always check against `origin/main` (not local `main`) because worktrees may have been created from a stale local main branch.

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

### 5. Wait for Required CI Checks

After PR creation, poll only REQUIRED checks (don't wait for optional ones).

**Timing:**
- **Initial wait:** 10 minutes (600 seconds) before first check - CI typically takes 5-8 minutes
- **Subsequent checks:** 3 minutes (180 seconds) between each check if still running
- **Timeout:** 20 minutes total - if required checks don't complete, continue to bot review phase

**Required checks (must pass):**
- `build` or `build-status`
- `test` or `test-status`
- `extension`
- `Analyze C#` (CodeQL)
- `dependency-review`

**Optional checks (don't block):**
- `Integration Tests` - requires live Dataverse credentials
- `claude`, `claude-review` - optional AI review
- `codecov/*` - informational coverage

**Polling approach:**
```bash
# Get SHA of PR head commit
SHA=$(gh pr view {pr} --json headRefOid --jq '.headRefOid')

# Wait 10 minutes before first check (CI typically takes 5-8 minutes)
sleep 600

# Check required CI status
gh api repos/{owner}/{repo}/commits/$SHA/check-runs \
  --jq '.check_runs[] | select(.name | test("^(build|test|extension|Analyze|CodeQL|dependency)"))
        | {name: .name, status: .status, conclusion: .conclusion}'

# If any required checks still running, wait 3 minutes and check again
# Repeat until all complete or 20-minute timeout reached
```

**Important:** Do NOT use `gh pr checks --watch` - it waits for ALL checks including optional ones.

### 5b. Wait for Bot Reviews (parallel to CI)

Bot reviews can complete before or after CI. Check for them independently:

```bash
# List bot reviewers who have commented
gh api repos/{owner}/{repo}/pulls/{pr}/comments \
  --jq '[.[] | .user.login] | unique | map(select(test("gemini|copilot|Copilot|github-advanced"))) | .[]'

# Also check for CodeQL alerts
gh api "repos/{owner}/{repo}/code-scanning/alerts?ref=refs/pull/{pr}/merge&state=open"
```

**Expected bots:**
- `gemini-code-assist` - Gemini code review
- `copilot-pull-request-reviewer` - Copilot review
- `github-advanced-security` - Code scanning alerts

**Timing:**
- Minimum wait: 3 minutes after PR creation (bots need time to analyze)
- Maximum wait: If no bot comments after 10 minutes, proceed anyway (bots may be disabled)

Update session status to `ReviewsInProgress` once at least one bot has commented:

```bash
if [ -n "$ISSUE_NUMBER" ]; then
  ppds session update --id "$ISSUE_NUMBER" --status reviews_in_progress
fi
```

### 6. Enumerate ALL Bot Reviewers

**CRITICAL: Before addressing ANY comments, enumerate all reviewers:**

```bash
# List all reviewers and their comment counts
gh api repos/{owner}/{repo}/pulls/{pr}/comments \
  --jq '[.[] | .user.login] | group_by(.) | map({reviewer: .[0], count: length}) | .[]'
```

**Checklist - confirm you have captured comments from ALL:**
- [ ] Copilot (user.login contains "Copilot" or "copilot")
- [ ] Gemini (user.login contains "gemini")
- [ ] Any other bot reviewers shown in the list

**For EACH reviewer with comments, fetch their full feedback:**

```bash
# Copilot comments
gh api repos/{owner}/{repo}/pulls/{pr}/comments \
  --jq '.[] | select(.user.login | test("Copilot|copilot")) | {id: .id, file: .path, line: .line, body: .body}'

# Gemini comments
gh api repos/{owner}/{repo}/pulls/{pr}/comments \
  --jq '.[] | select(.user.login | test("gemini")) | {id: .id, file: .path, line: .line, body: .body}'
```

**DO NOT proceed to fix issues until you have explicitly reviewed comments from EVERY bot reviewer listed.**

### 7. Handle CI Failures

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

### 8. Address Bot Comments

Using the comments enumerated in step 6, for each finding determine verdict:

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

### 9. Update Session Status

After all checks pass, update session status via CLI (if in session context):

```bash
if [ -n "$ISSUE_NUMBER" ]; then
  # Get PR URL
  PR_URL=$(gh pr view --json url --jq '.url')

  # Update session to complete with PR URL
  ppds session update --id "$ISSUE_NUMBER" --status complete --pr "$PR_URL"
fi
```

**Important:** Use the CLI command, not direct JSON editing. This ensures proper heartbeat updates and orchestrator visibility.

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
[✓] Session status updated: complete

Ready for human review!
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
