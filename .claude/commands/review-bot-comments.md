# Review Bot Comments

Systematically address PR review comments from Copilot, Gemini, and CodeQL.

## Usage

`/review-bot-comments [pr-number]`

## Process

### 1. Fetch All Bot Findings

Fetch BOTH PR comments AND code scanning alerts:

```bash
# 1a. PR comments from bots
gh api repos/joshsmithxrm/power-platform-developer-suite/pulls/[PR]/comments \
  --jq '.[] | select(.user.login | test("gemini|Copilot|copilot|github-advanced")) | {id, user: .user.login, body: .body[:100], path, line}'

# 1b. Code scanning alerts on the PR merge ref
# IMPORTANT: Use refs/pull/[PR]/merge to get alerts introduced by the PR
# Using just the branch name returns empty or wrong results!
gh api "repos/joshsmithxrm/power-platform-developer-suite/code-scanning/alerts?ref=refs/pull/[PR]/merge&state=open" \
  --jq '.[] | {number, rule: .rule.description, path: .most_recent_instance.location.path, line: .most_recent_instance.location.start_line, severity: .rule.severity}'
```

### Bot Sources

| Source | Type | How to Identify |
|--------|------|-----------------|
| Gemini | PR comment | `user.login = gemini-code-assist[bot]` |
| Copilot (line) | PR comment | `user.login = Copilot` |
| Copilot (review) | PR comment | `user.login = copilot-pull-request-reviewer[bot]` |
| CodeQL/GHAS | PR comment | `user.login = github-advanced-security[bot]` |
| Copilot Autofix | Code scanning alert | Via code-scanning API (not PR comments) |

**Note:** CodeQL and Copilot frequently report **duplicate findings** (same file, same line, same issue). Group by file+line to identify duplicates before triaging.

### Responding to Autofix Alerts

Autofix suggestions are NOT PR comments - they require different handling:

| Action | Command |
|--------|---------|
| **Fix the code** | Alert auto-closes when CI runs on the fix |
| **Dismiss as false positive** | `gh api repos/joshsmithxrm/power-platform-developer-suite/code-scanning/alerts/{number} -X PATCH -f state=dismissed -f dismissed_reason="false positive" -f dismissed_comment="Reason"` |
| **Dismiss as won't fix** | `gh api repos/joshsmithxrm/power-platform-developer-suite/code-scanning/alerts/{number} -X PATCH -f state=dismissed -f dismissed_reason="won't fix" -f dismissed_comment="Reason"` |
| **Dismiss as test code** | `gh api repos/joshsmithxrm/power-platform-developer-suite/code-scanning/alerts/{number} -X PATCH -f state=dismissed -f dismissed_reason="used in tests" -f dismissed_comment="Test code"` |

### 2. Triage Each Comment

For each bot comment, determine verdict and rationale:

| Verdict | Meaning |
|---------|---------|
| **Valid** | Bot is correct, code should be changed |
| **False Positive** | Bot is wrong, explain why |
| **Duplicate** | Same issue reported by another bot - still needs reply |
| **Unclear** | Need to investigate before deciding |

**IMPORTANT:** Every comment needs a reply, including duplicates. Track all comment IDs to ensure none are missed.

### 3. Present Summary and WAIT FOR APPROVAL

**CRITICAL: Do NOT implement fixes automatically.**

Present a unified summary table including BOTH PR comments AND code scanning alerts:

```markdown
## Bot Review Triage - PR #XX

### PR Comments

| # | Bot | File:Line | Finding | Verdict | Action |
|---|-----|-----------|---------|---------|--------|
| 1 | Gemini | Foo.cs:42 | Missing Dispose | Valid | Fix |
| 2 | Copilot | Bar.cs:10 | Use .Where() | False Positive | Decline |

### Code Scanning Alerts (Autofix)

| Alert # | Severity | File:Line | Rule | Verdict | Action |
|---------|----------|-----------|------|---------|--------|
| 15 | warning | Baz.cs:25 | Generic catch clause | Valid | Fix code |
| 16 | note | Test.cs:50 | Path.Combine call | False Positive | Dismiss |
```

**STOP HERE. Wait for user to review and approve before making ANY changes.**

Bot suggestions can be wrong (e.g., suggesting methods that don't exist on an interface).
Always get user approval, then verify changes compile before committing.

### 4. Implement Approved Changes

After user approval:

**For PR comments (fix in code):**
1. Make the approved code changes
2. **Build and verify** - `dotnet build` must succeed
3. Run tests to confirm no regressions
4. Commit with descriptive message

**For code scanning alerts:**
- **If fixing:** Make code change, alert auto-closes on next CI run
- **If dismissing:** Use the dismiss command from step 1 with appropriate reason

### 5. Reply to Each Comment Individually

After changes are committed, reply to each bot comment. Do NOT batch responses into a single PR comment.

```bash
# Reply to a specific review comment (note: uses /replies endpoint)
gh api repos/joshsmithxrm/power-platform-developer-suite/pulls/{pr}/comments/{comment_id}/replies \
  -f body="Fixed in abc123"
```

**Important:** Use the `/comments/{comment_id}/replies` endpoint, NOT the base comments endpoint with `in_reply_to`. The comment ID is numeric - get IDs from the fetch step.

| Verdict | Reply Template |
|---------|----------------|
| Valid (fixed) | `Fixed in {commit_sha} - {brief description}` |
| Declined | `Declining - {reason}` |
| False positive | `False positive - {explanation}` |
| Duplicate | Same reply as original (reference the fix commit) |

## Common False Positives

| Bot Claim | Why It's Often Wrong |
|-----------|---------------------|
| "Use .Where() instead of foreach+if" | Preference, not correctness |
| "Use .Select() instead of foreach" | Using Select for side effects is an anti-pattern |
| "Volatile needed with Interlocked" | Interlocked provides barriers |
| "OR should be AND" | Logic may be intentionally inverted (DeMorgan) |
| "Static field not thread-safe" | May be set once at startup |
| "Call Dispose on X" | Interface may not actually implement IDisposable |

## Common Valid Findings

| Pattern | Usually Valid |
|---------|---------------|
| Unused variable/parameter | Yes - dead code |
| Missing null check | Check context |
| Resource not disposed | Yes - but verify interface first |
| Generic catch clause | Context-dependent |

### 6. Resolve All Review Threads

After addressing comments, resolve the threads so the PR can be merged:

```bash
# Get thread IDs
gh api graphql -f query='
query {
  repository(owner: "joshsmithxrm", name: "power-platform-developer-suite") {
    pullRequest(number: {PR}) {
      reviewThreads(first: 20) {
        nodes {
          id
          isResolved
          comments(first: 1) {
            nodes { author { login } }
          }
        }
      }
    }
  }
}'

# Resolve each unresolved thread
gh api graphql -f query='
mutation {
  resolveReviewThread(input: {threadId: "THREAD_ID"}) { thread { isResolved } }
}'
```

Resolve ALL threads (bot and human) that have been addressed.

### 7. Verify All Findings Addressed

Before completing, verify:

**PR comments** - Every comment has a reply:
```bash
gh api repos/joshsmithxrm/power-platform-developer-suite/pulls/{pr}/comments --jq "length"
```

**Code scanning alerts** - All are fixed or dismissed:
```bash
gh api "repos/joshsmithxrm/power-platform-developer-suite/code-scanning/alerts?ref=refs/pull/[PR]/merge&state=open" --jq "length"
# Should return 0
```

**Review threads** - All resolved:
```bash
gh api graphql -f query='query { repository(owner: "joshsmithxrm", name: "power-platform-developer-suite") { pullRequest(number: {PR}) { reviewThreads(first: 20) { nodes { isResolved } } } } }' --jq '[.data.repository.pullRequest.reviewThreads.nodes[] | select(.isResolved == false)] | length'
# Should return 0
```

If any PR comments are missing replies, alerts remain open, or threads are unresolved, address them before marking complete.

**Note:** Code scanning alerts don't need replies - they're resolved by fixing code or dismissing via API.

### 8. Base Branch Check & Push

Before pushing fixes, ensure the branch is based on latest `origin/main`:

```bash
# Fetch latest from origin
git fetch origin

# Check if current branch is behind origin/main
git rev-list --count HEAD..origin/main
```

**If the count is > 0, the branch is behind `origin/main`.** Rebase before pushing:

```bash
# Rebase onto latest main
git rebase origin/main

# If conflicts, resolve them and continue
git rebase --continue
```

**Only after rebasing (if needed)**, push the fixes:

```bash
# Push (or force-push after rebase)
git push
# If rebased, may need: git push --force-with-lease
```

## When to Use

- After opening a PR (before requesting review)
- After new bot comments appear
- Before merging
