# Review Bot Comments

Systematically address PR review comments from Copilot, Gemini, and CodeQL.

## Usage

`/review-bot-comments [pr-number]`

## Process

### 1. Fetch Comments
```bash
gh api repos/joshsmithxrm/ppds-sdk/pulls/[PR]/comments
```

### Bot Usernames

Look for comments from these `user.login` values:

| Bot | Username |
|-----|----------|
| Gemini | `gemini-code-assist[bot]` |
| Copilot (line comments) | `Copilot` |
| Copilot (PR review) | `copilot-pull-request-reviewer[bot]` |
| CodeQL/GHAS | `github-advanced-security[bot]` |

**Note:** CodeQL and Copilot frequently report **duplicate findings** (same file, same line, same issue). Group comments by file+line to identify duplicates before triaging.

### 1b. Check Code Scanning Alerts with Copilot Autofix

Copilot Autofix suggestions appear in the GitHub UI as "replyable" threads but are **not** traditional PR comments. They're accessed via the code scanning API:

```bash
# List open code scanning alerts for the PR branch (replace BRANCH with actual branch name)
gh api "repos/joshsmithxrm/ppds-sdk/code-scanning/alerts?ref=BRANCH&state=open" \
  --jq '.[] | {number, rule: .rule.description, path: .most_recent_instance.location.path, line: .most_recent_instance.location.start_line}'

# Get Autofix suggestion for a specific alert (if available)
gh api repos/joshsmithxrm/ppds-sdk/code-scanning/alerts/{alert_number}/autofix
```

**Responding to Autofix suggestions:**
- **Fix the code**: Alert auto-closes when CI runs on the fix
- **Dismiss alert**: `gh api repos/joshsmithxrm/ppds-sdk/code-scanning/alerts/{number} -X PATCH -f state=dismissed -f dismissed_reason=won\'t\ fix -f dismissed_comment="Reason here"`

Valid `dismissed_reason` values: `false positive`, `won't fix`, `used in tests`

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

Present a summary table to the user:

```markdown
## Bot Review Triage - PR #XX

| # | Bot | Finding | Verdict | Recommendation | Rationale |
|---|-----|---------|---------|----------------|-----------|
| 1 | Gemini | Missing Dispose | Valid | Add dispose call | Prevents resource leak |
| 2 | Copilot | Use .Where() | False Positive | Decline | Style preference |
```

**STOP HERE. Wait for user to review and approve before making ANY changes.**

Bot suggestions can be wrong (e.g., suggesting methods that don't exist on an interface).
Always get user approval, then verify changes compile before committing.

### 4. Implement Approved Changes

After user approval:
1. Make the approved code changes
2. **Build and verify** - `dotnet build` must succeed
3. Run tests to confirm no regressions
4. Commit with descriptive message

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

### 6. Verify All Comments Addressed

Before completing, verify every comment has a reply:

```bash
# Count original comments vs replies
gh api repos/joshsmithxrm/ppds-sdk/pulls/{pr}/comments --jq "length"
```

If any comments are missing replies, address them before marking complete.

## When to Use

- After opening a PR (before requesting review)
- After new bot comments appear
- Before merging
