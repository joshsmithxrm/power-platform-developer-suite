---
name: pr
description: Create PR, wait for the configured automated review, triage every comment, and present a summary. Use when work is ready to ship — after gates, verify, QA, and review are complete.
---

# PR

End-to-end PR lifecycle: rebase, create, wait for the configured automated review, triage every comment, and present a summary to the user.

## Process

### Step 1: Rebase and Push

```bash
git fetch origin main && git rebase origin/main
```

Conflicts → present to user, do NOT auto-resolve. Then:

```bash
git merge-base --is-ancestor origin/main HEAD
git push --force-with-lease origin HEAD
```

### Step 2: Linked Issues

Include `Closes #NNN` for each issue this PR resolves. If you do not know the issue numbers (interactive), ask the user.

### Step 3: Pre-PR Self-Review

Dispatch the `code-reviewer` agent against `git diff origin/main...HEAD`. See REFERENCE.md §2 for inputs and finding triage. DEFECTs must be fixed before opening. Skip with `--no-self-review`.

### Step 4: Create PR (Draft)

Write body to temp file (see REFERENCE.md §3 for template), then:

```bash
gh pr create --draft --title "<title>" --body-file "$PR_BODY"
```

### Step 5: Wait for Automated Review and CI

Poll for the configured automated review (e.g. Gemini or CodeRabbit; some repos disable it) and check status directly:

```bash
gh pr checks <pr-number> --watch          # CI + CodeQL status
gh pr view <pr-number> --json reviews,comments,statusCheckRollup
```

Review timing is unpredictable (2–10+ minutes). Poll `gh pr view <pr-number> --json reviews,comments` every ~30s until the review appears, up to a few minutes. If no reviewer is configured or it never arrives, note that and proceed — do not block indefinitely.

### Step 6: Triage Every Comment

Fetch review comments and respond to **every** one — this is the discipline that captures knowledge:

```bash
gh pr view <pr-number> --json reviews,comments
gh api repos/:owner/:repo/pulls/<pr-number>/comments      # inline review comments
```

For each comment: either fix the code (and reply noting the commit) or reply with a rationale for not changing it. Do not leave any comment unaddressed. Include CodeQL and CI-surfaced findings in the same pass.

### Step 7: Flip to Ready and Present Summary

Once CI is green, the automated review is triaged, and every comment is answered:

```bash
gh pr ready <pr-number>
```

Present:

```
PR created: {url}
CI: <status>   Review: <triaged N comments, or "no reviewer configured">
```

### Step 8: Post-Merge Cleanup

When the PR merges, delete the branch and worktree:

```bash
git worktree remove <path>        # if working in a dedicated worktree
git branch -d <branch>
git push origin --delete <branch> # if the remote branch was not auto-deleted
```
