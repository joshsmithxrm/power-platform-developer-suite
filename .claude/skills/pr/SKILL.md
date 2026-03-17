---
name: pr
description: Create PR, monitor CI and code reviews, triage comments, and present summary. Use when work is ready to ship — after gates, verify, QA, and review are complete.
---

# PR

End-to-end PR lifecycle: rebase, create, monitor CI + external reviews, triage comments, and present a summary to the user.

## Prerequisites

Before running `/pr`, the following must be complete (enforced by the PR gate hook):
- `/gates` passed against current HEAD
- `/verify` completed for at least one surface
- `/qa` completed for at least one surface
- `/review` completed

Run `/status` to check current workflow state.

## Process

### 1. Rebase on Main

```bash
git fetch origin main
git rebase origin/main
```

If conflicts exist, present them to the user — do NOT auto-resolve.

### 2. Create PR

```bash
gh pr create --title "<title>" --body "$(cat <<'EOF'
## Summary
<1-3 bullet points>

## Test Plan
<bulleted checklist>

## Verification
- [x] /gates passed
- [x] /verify completed (surfaces: ...)
- [x] /qa completed (surfaces: ...)
- [x] /review completed (findings: N)

🤖 Generated with [Claude Code](https://claude.com/claude-code)
EOF
)"
```

Keep title under 70 characters. Use conventional commit format.

### 3. Monitor CI + Reviews

**Polling strategy:**
- Wait 30 seconds after PR creation (let GitHub register checks)
- Poll every 30 seconds for the first 2 minutes
- Poll every 2 minutes after that
- **Max wait: 15 minutes total**
- On timeout: report current status and what's still pending

**Check CI:**
```bash
gh pr checks <pr-number>
```

**Check reviews:**
```bash
gh api repos/{owner}/{repo}/pulls/{number}/reviews
```

### 4. Triage Review Comments

When external reviews (Gemini, etc.) are complete:

**For each comment:**
1. Evaluate against constitution and codebase patterns
2. If valid (mechanical issue, real bug, correct suggestion) → fix it
3. If invalid (conflicts with our patterns, misunderstands codebase) → dismiss with rationale
4. **Reply directly to EACH review comment** (not a top-level PR comment):
   ```bash
   # Get review comments
   gh api repos/{owner}/{repo}/pulls/{number}/comments --jq '.[] | {id, path, body}'

   # Reply to a specific comment (threads the reply under the original)
   gh api repos/{owner}/{repo}/pulls/{number}/comments/{comment_id}/replies -f body="..."
   ```
   Reply text:
   - Fixed: "Fixed in {commit SHA} — {brief description}"
   - Dismissed: "Not applicable — {rationale referencing constitution/pattern}"

   Do NOT use `gh pr comment` — that creates a top-level comment, not a threaded reply.

**Push fixes as a new commit:**
```bash
git add <files>
git commit -m "fix: address review feedback from {reviewer}"
git push
```

### 5. Present Summary

After CI passes and comments are addressed, present to the user:

```
PR ready for review: {url}

CI: ✓ All checks passed
Reviews: {N} comments from {reviewer}
  Fixed: {count} ({brief list})
  Dismissed: {count} ({brief list})

Awaiting your review.
```

### 6. Write Workflow State

After PR is created:
```json
{
  "pr": {
    "url": "https://github.com/...",
    "created": "2026-03-16T17:00:00Z"
  }
}
```

## Timeout Behavior

If CI or reviews don't complete within 15 minutes:

```
PR created: {url}
CI: ⏳ Still running ({list pending checks})
Reviews: ⏳ No reviews yet

Check back later or run /status.
```

## Error Handling

| Error | Recovery |
|-------|----------|
| Rebase conflicts | Present conflicts to user, do NOT auto-resolve |
| PR creation fails | Check `gh auth status`, suggest `gh auth login` if needed |
| CI fails | Report which checks failed, suggest fixes |
| Push rejected | Check if branch is behind, suggest rebase |
