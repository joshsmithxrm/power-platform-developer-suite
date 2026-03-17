---
name: pr
description: Create PR, wait for Gemini review, triage every comment, and present summary. Use when work is ready to ship — after gates, verify, QA, and review are complete.
---

# PR

End-to-end PR lifecycle: rebase, create, wait for Gemini review, triage comments, and present a summary to the user.

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

### 3. Wait for Gemini Review

Gemini posts review comments within 2-3 minutes of PR creation. Do NOT skip this step.

**Polling strategy:**
- Wait 30 seconds after PR creation (let GitHub register the PR)
- Poll every 30 seconds
- **Max wait: 10 minutes**
- On timeout: report that no review was received

**How to check:**
```bash
# Check for reviews
gh api repos/{owner}/{repo}/pulls/{number}/reviews --jq 'length'

# Check for review comments
gh api repos/{owner}/{repo}/pulls/{number}/comments --jq 'length'
```

Stop polling when reviews or comments appear (length > 0). CI status is NOT monitored — `/gates` already verified build/tests locally. The user checks CI on the PR page when ready to merge.

### 4. Triage EVERY Review Comment

This step is MANDATORY. Do not skip it. Do not defer it. Do not declare done without completing it.

**Get all review comments:**
```bash
gh api repos/{owner}/{repo}/pulls/{number}/comments --jq '.[] | {id, user: .user.login, path, line, body}'
```

**For each comment:**
1. Evaluate against constitution and codebase patterns
2. If valid (mechanical issue, real bug, correct suggestion) → fix it
3. If invalid (conflicts with our patterns, misunderstands codebase) → dismiss with rationale

**Reply directly to EACH comment** (threaded reply, not top-level):
```bash
# Reply to a specific review comment
gh api repos/{owner}/{repo}/pulls/{number}/comments/{comment_id}/replies -f body="..."
```

Reply text:
- Fixed: "Fixed in {commit SHA} — {brief description}"
- Dismissed: "Not applicable — {rationale referencing constitution/pattern}"

**Do NOT use `gh pr comment`** — that creates a top-level comment, not a threaded reply.

**Push fixes as a new commit:**
```bash
git add <files>
git commit -m "fix: address review feedback from {reviewer}"
git push
```

### 5. Present Summary

After comments are triaged and responded to:

```
PR ready for review: {url}

Gemini review: {N} comments
  Fixed: {count} ({brief list})
  Dismissed: {count} ({brief list})

CI: running — check PR page for status.

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

If Gemini doesn't post within 10 minutes:

```
PR created: {url}
Gemini: no review received within 10 minutes.
CI: running — check PR page for status.

Awaiting your review.
```

## Error Handling

| Error | Recovery |
|-------|----------|
| Rebase conflicts | Present conflicts to user, do NOT auto-resolve |
| PR creation fails | Check `gh auth status`, suggest `gh auth login` if needed |
| Push rejected | Check if branch is behind, suggest rebase |
