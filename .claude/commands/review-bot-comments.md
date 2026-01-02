# Review Bot Comments

Systematically address PR review comments from Copilot, Gemini, and CodeQL.

## Usage

`/review-bot-comments [pr-number]`

## Process

### 1. Fetch Comments
```bash
gh api repos/joshsmithxrm/ppds-sdk/pulls/[PR]/comments
```

### 2. Triage Each Comment

For each bot comment, determine verdict and rationale:

| Verdict | Meaning |
|---------|---------|
| **Valid** | Bot is correct, code should be changed |
| **False Positive** | Bot is wrong, explain why |
| **Unclear** | Need to investigate before deciding |

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
# Reply to a specific review comment
gh api repos/joshsmithxrm/ppds-sdk/pulls/{pr}/comments \
  -f body="Fixed in abc123" \
  -F in_reply_to={comment_id}
```

**Important:** The `in_reply_to` parameter must be the comment ID (numeric), not a URL. Get IDs from the fetch step.

| Verdict | Reply Template |
|---------|----------------|
| Valid (fixed) | `Fixed in {commit_sha} - {brief description}` |
| Declined | `Declining - {reason}` |
| False positive | `False positive - {explanation}` |

## Common False Positives

| Bot Claim | Why It's Often Wrong |
|-----------|---------------------|
| "Use .Where() instead of foreach+if" | Preference, not correctness |
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

## When to Use

- After opening a PR (before requesting review)
- After new bot comments appear
- Before merging
