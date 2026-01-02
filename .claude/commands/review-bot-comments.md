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

For each bot comment, determine:

| Verdict | Action |
|---------|--------|
| **Valid** | Fix the issue, reply "Fixed in [commit]" |
| **False Positive** | Reply with reason, dismiss |
| **Unclear** | Investigate before deciding |

### 3. Common False Positives

| Bot Claim | Why It's Often Wrong |
|-----------|---------------------|
| "Use .Where() instead of foreach+if" | Preference, not correctness |
| "Volatile needed with Interlocked" | Interlocked provides barriers |
| "OR should be AND" | Logic may be intentionally inverted (DeMorgan) |
| "Static field not thread-safe" | May be set once at startup |

### 4. Common Valid Findings

| Pattern | Usually Valid |
|---------|---------------|
| Unused variable/parameter | Yes - dead code |
| Missing null check | Check context |
| Resource not disposed | Yes - leak |
| Generic catch clause | Context-dependent |

## Output

```markdown
## Bot Review Triage - PR #82

| # | Bot | Finding | Verdict | Action |
|---|-----|---------|---------|--------|
| 1 | Gemini | Use constants in dict | Valid | Fixed in abc123 |
| 2 | Copilot | Add validation tests | Valid | Fixed in def456 |
| 3 | Copilot | Use .Where() | False Positive | Style preference |
| 4 | CodeQL | Generic catch | Valid (low) | Acceptable for disposal |

All findings addressed: Yes
```

## When to Use

- After opening a PR (before requesting review)
- After new bot comments appear
- Before merging
