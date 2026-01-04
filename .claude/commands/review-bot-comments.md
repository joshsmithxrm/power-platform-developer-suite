# Review Bot Comments

Systematically address PR review comments from Copilot, Gemini, and CodeQL.

## Usage

`/review-bot-comments [pr-number]`

## Process

### 1. Fetch All Bot Findings

Fetch BOTH PR comments AND code scanning alerts:

```bash
# 1a. PR comments from bots
gh api repos/joshsmithxrm/ppds-sdk/pulls/[PR]/comments \
  --jq '.[] | select(.user.login | test("gemini|Copilot|copilot|github-advanced")) | {id, user: .user.login, body: .body[:100], path, line}'

# 1b. Code scanning alerts (includes Copilot Autofix)
# First get the PR branch name
gh pr view [PR] --repo joshsmithxrm/ppds-sdk --json headRefName --jq '.headRefName'

# Then fetch alerts for that branch
gh api "repos/joshsmithxrm/ppds-sdk/code-scanning/alerts?ref=[BRANCH]&state=open" \
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
| **Dismiss as false positive** | `gh api repos/joshsmithxrm/ppds-sdk/code-scanning/alerts/{number} -X PATCH -f state=dismissed -f dismissed_reason="false positive" -f dismissed_comment="Reason"` |
| **Dismiss as won't fix** | `gh api repos/joshsmithxrm/ppds-sdk/code-scanning/alerts/{number} -X PATCH -f state=dismissed -f dismissed_reason="won't fix" -f dismissed_comment="Reason"` |
| **Dismiss as test code** | `gh api repos/joshsmithxrm/ppds-sdk/code-scanning/alerts/{number} -X PATCH -f state=dismissed -f dismissed_reason="used in tests" -f dismissed_comment="Test code"` |

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
gh api repos/joshsmithxrm/ppds-sdk/pulls/{pr}/comments/{comment_id}/replies \
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

### 6. Verify All Findings Addressed

Before completing, verify:

**PR comments** - Every comment has a reply:
```bash
gh api repos/joshsmithxrm/ppds-sdk/pulls/{pr}/comments --jq "length"
```

**Code scanning alerts** - All are fixed or dismissed:
```bash
gh api "repos/joshsmithxrm/ppds-sdk/code-scanning/alerts?ref=[BRANCH]&state=open" --jq "length"
# Should return 0
```

If any PR comments are missing replies or alerts remain open, address them before marking complete.

**Note:** Code scanning alerts don't need replies - they're resolved by fixing code or dismissing via API.

## When to Use

- After opening a PR (before requesting review)
- After new bot comments appear
- Before merging
