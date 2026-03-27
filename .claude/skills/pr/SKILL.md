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

### 1. Rebase on Main and Push

```bash
git fetch origin main
git rebase origin/main
```

If conflicts exist, present them to the user — do NOT auto-resolve.

After successful rebase, verify and push:

```bash
# Verify rebase succeeded — origin/main must be an ancestor of HEAD
git merge-base --is-ancestor origin/main HEAD

# Push rebased branch (force-with-lease is safe — only overwrites our own commits)
git push --force-with-lease origin HEAD
```

If `merge-base` fails, the rebase didn't apply correctly — investigate before proceeding.
If push is rejected, fetch and retry the rebase.

### 2. Check for Linked Issues

Before creating the PR, check if there are GitHub issues to close:

```bash
python scripts/workflow-state.py get issues
```

If the result is a JSON array (e.g., `[602, 596]`), include `Closes #NNN` lines in the PR body for each issue.

If no issues are in workflow state and this is an **interactive session** (not headless `claude -p`), ask the user:
> "Does this PR close any GitHub issues? If so, provide the numbers (comma-separated), or press Enter to skip."

Parse the response as a comma-separated list of integers. Store them:
```bash
python scripts/workflow-state.py append issues <N>
```

### 3. Create PR (Draft)

```bash
gh pr create --draft --title "<title>" --body "$(cat <<'EOF'
## Summary
<1-3 bullet points>

Closes #NNN
Closes #NNN

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

Omit the `Closes` lines if there are no linked issues. Keep title under 70 characters. Use conventional commit format.

### 4. Wait for Gemini Review

Gemini posts review comments within 2-3 minutes of PR creation. Do NOT skip this step.

**Polling strategy:**
- Wait 90 seconds minimum after PR creation (Gemini takes 2-3 minutes)
- Poll every 30 seconds
- **Stabilization check:** stop polling when comment count is identical on two consecutive 30-second polls (Gemini is done posting)
- **Max wait: 5 minutes**
- On timeout: report that no review was received

**How to check:**
```bash
# Check for reviews
gh api repos/{owner}/{repo}/pulls/{number}/reviews --jq 'length'

# Check for review comments
gh api repos/{owner}/{repo}/pulls/{number}/comments --jq 'length'
```

Stop polling when reviews or comments appear (length > 0) AND comment count has stabilized (same count on two consecutive polls). CI status is NOT monitored — `/gates` already verified build/tests locally. The user checks CI on the PR page when ready to merge.

### 5. Triage EVERY Review Comment

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

**Common mistakes:**
- WRONG: `gh pr comment 123 --body "Addressed all feedback"` → this is a single top-level comment, not per-comment replies
- WRONG: Posting a summary issue comment that groups all findings → reviewers can't see which comment was addressed
- RIGHT: Loop over each `comment_id` from the API response and POST a reply to each one individually
- RIGHT: Each reply references the specific commit SHA or explains why the finding was dismissed

**Push fixes as a new commit:**
```bash
git add <files>
git commit -m "fix: address review feedback from {reviewer}"
git push
```

### 5.5. Convert to Ready

After triage is complete, convert the draft PR to ready for review:

```bash
# Convert draft PR to ready for review
gh pr ready {N}
```

> **Note:** This is when GitHub sends the reviewer notification — after triage is complete, not at PR creation. By using draft→ready flow, reviewers are notified only when the PR is fully triaged and ready for human review, avoiding noisy intermediate notifications.

### 6. Present Summary

After comments are triaged and responded to:

```
PR ready for review: {url}

Gemini review: {N} comments
  Fixed: {count} ({brief list})
  Dismissed: {count} ({brief list})

CI: running — check PR page for status.

Awaiting your review.
```

### 7. Write Workflow State

After PR is created:

```bash
python scripts/workflow-state.py set pr.url "{pr-url}"
python scripts/workflow-state.py set pr.created now
```

After Gemini comments are triaged (step 5 complete):

```bash
python scripts/workflow-state.py set pr.gemini_triaged true
```

The stop hook will BLOCK the session from ending if `gemini_triaged` is not set after PR creation. Do not skip step 5.

### 8. Notify

Fire a desktop toast so the user knows the PR is ready:

```bash
python .claude/hooks/notify.py --title "PR Ready" --msg "Gemini triaged — click to review" --url "{pr-url}"
```

This fires after `gh pr ready` converts the draft to ready for review. Notification timing is correct because the PR remains in draft state until triage completes — reviewers and the user are notified simultaneously when the PR is actually ready.

## Timeout Behavior

If Gemini doesn't post within 5 minutes:

```
PR created: {url}
Gemini: no review received within 5 minutes.
CI: running — check PR page for status.

Awaiting your review.
```

## Error Handling

| Error | Recovery |
|-------|----------|
| Rebase conflicts | Present conflicts to user, do NOT auto-resolve |
| PR creation fails | Check `gh auth status`, suggest `gh auth login` if needed |
| Push rejected | Check if branch is behind, suggest rebase |
