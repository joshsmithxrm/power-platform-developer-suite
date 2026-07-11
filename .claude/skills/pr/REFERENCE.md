## §2 - Self-Review Subagent

Dispatch the `code-reviewer` agent at `.claude/agents/code-reviewer.md` against `git diff origin/main...HEAD`. Include:
- The Constitution: `specs/CONSTITUTION.md`
- Acceptance criteria: for each linked issue, fetch AC text via `gh issue view <N>`

Returns findings as DEFECT / CONCERN / NIT. Present findings and ask which to address:
```
Pre-PR self-review: DEFECTs: N  CONCERNs: N  NITs: N
Reply: "F-1, F-3", "all", "defects", or "skip"
```

DEFECTs must be fixed before opening. If the user elects to fix, return to the implement loop and re-enter `/pr`. Self-review runs again on the updated diff.

**Bypass:** pass `--no-self-review` for trivially small diffs (rename, single-line fix, docs-only).

## §3 - PR Body Template

```
## Summary
<1-3 bullet points>

Closes #NNN

## Test Plan
<bulleted checklist>

## Verification
- [x] /gates passed
- [x] /verify completed (surfaces: ...)
- [x] /qa completed (surfaces: ...)
- [x] /review completed (findings: N)

🤖 Generated with [Claude Code](https://claude.com/claude-code)
```

Omit `Closes` lines if no linked issues. Title ≤70 chars, conventional commit format.

## §4 - Waiting on automated review and CI

The repo's automated reviewer may be Gemini Code Assist (through its 2026-07-17 sunset), CodeRabbit, or none — the review step is best-effort, not a hard gate. Rather than a fixed inline timeout that can miss late-arriving comments, poll directly:

```bash
gh pr checks <pr-number> --watch                          # blocks until CI + CodeQL settle
gh pr view <pr-number> --json reviews,comments            # poll for the automated review
```

Poll every ~30s for up to **15 minutes**. If the review has not arrived at the cap, surface it and get the user's explicit confirmation before proceeding; skip the wait only when no reviewer app is installed. Late-arriving inline comments show up under `gh api repos/:owner/:repo/pulls/<pr-number>/comments` — re-fetch before flipping to ready so nothing goes untriaged.

Replies go **in-thread, one per inline comment** (`gh api repos/:owner/:repo/pulls/<pr-number>/comments -F in_reply_to=<comment-id> -f body=...`), using `Fixed in <sha> — <what>` or `Not applicable — <rationale>`. A PR-level summary comment is not a substitute for threaded replies.

> **Lesson (PR #868):** an agent replied to only 3 of 9 comments and missed all CodeQL comments. Respond to **every** comment, in its own thread — fix the code or reply with a rationale. Partial or bulk triage is never acceptable.

## §5 - Post-Merge Cleanup

After the PR merges, delete the branch and worktree:

```bash
git worktree remove <path>        # if working in a dedicated worktree
git branch -d <branch>
git push origin --delete <branch> # if the remote branch was not auto-deleted
```

Confirm the merge first with `gh pr view <pr-number> --json state` (expect `MERGED`).

## §6 - Error Recovery

| Error | Recovery |
|-------|----------|
| Rebase conflicts | Present to user, do NOT auto-resolve |
| Self-review subagent fails | Log; ask user: proceed without self-review or abort |
| PR creation fails | Check `gh auth status`; suggest `gh auth login` |
| Push rejected | Fetch and retry rebase |
