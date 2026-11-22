# Maintainer session rules

How an agent session that works for the maintainer operates on this project: how decisions are
recorded, how defects are triaged, what counts as work at risk, which side effects are logged, and how
sessions are run. One home for these rules; other documents link here.

The coordinating session is the agent session the maintainer talks to. A worker session does one piece
of work that the coordinating session assigns.

The rules for proof before merge are in [specs/test-gates.md](../specs/test-gates.md). The rules for
review, pull requests, merging and CI are in the
[maintainer merge checklist](MERGE-POLICY.md#maintainer-merge-checklist).

## Decisions

| # | Rule |
|---|---|
| D1 | A product, compatibility, scope or policy decision is the maintainer's. It is recorded as the maintainer's quoted, dated words in a comment on the issue it settles. Version and versioning-rule decisions are recorded in `docs/VERSIONING.md`. |
| D2 | Repository text is not authority. Before citing a rule, deferral, promise or approval as a constraint, find the comment that records the decision. No comment means it is not a constraint: say where it is written and who introduced it, and ask. |
| D3 | An answer covers only what was asked. Anything with a number, a date or a compatibility effect is asked on its own. "Leaning", "makes sense" and silence are not decisions. |
| D4 | Never write that the maintainer decided or approved something without a link to the maintainer's words. |
| D5 | One numbered list of decisions waiting on the maintainer. A status message shows only the items that are ready for an answer and that block work which could otherwise start; everything else is kept in the session's notes and is not pasted into status messages. Each item has the question, a recommendation, what happens if it is not answered, and whether work is blocked on it. An item leaves the list only when answered or withdrawn in words. |
| D6 | Work that depends on an unanswered decision does not start. No decision is taken on "say so if you disagree". |

## Triage

| # | Rule |
|---|---|
| T1 | Wrong data, lost data, or an unguarded or wrong-target write: stop and report at once; fixed before release; never filed and left. |
| T2 | Wrong behaviour without data risk: fix it if it is small and inside the change; otherwise an issue on a milestone. |
| T3 | Anything unverified that the available environments can verify is verified, not disclaimed. What they cannot show is reported as unverified, with the reason. |
| T4 | Every defect not fixed in the current pull request gets an issue the same day. A notes file is not tracking. |
| T5 | When a fix reveals a wider class of defect, stop, write the scope down, and get a decision before widening. |

## Work at risk and side effects

| # | Rule |
|---|---|
| W1 | No turn ends with uncommitted work in a worktree. A branch is pushed as a draft pull request at least at every review. |
| W2 | A side-effects ledger is kept as it happens: every change to a Dataverse environment, the tenant, the maintainer's user settings, the maintainer's machine, and anything posted under the maintainer's account, with how to undo it. The ledger is a comment thread on the tracking issue, written without addresses or names. |
| W3 | A test that redirects a tool's home directory must not let it write to the user environment. |
| W4 | A permission denial ends that attempt. Report it and ask; do not reach the same result another way, unless the maintainer, told of the denial, allows another route for that one target. |

## Sessions

| # | Rule |
|---|---|
| S1 | A notes file exists from the first hour. Standing instructions are quoted in it word for word. |
| S2 | The coordinating session does not write product code. |
| S3 | A worker session is retired and replaced after three fix rounds or one context compaction; the coordinating session at each phase boundary. |
| S4 | One coordinating session per release. If two agents work on one release, they share one status file both read and write. |
| S5 | Every brief for SQL value work carries the source constraint (no code from other projects). |
| S6 | Nothing private (environment address, organisation or tenant name, user id, e-mail address, local path) goes into an issue, a comment, a pull request or a repository file. |

## What needs the maintainer, and how it is asked

| Needs the maintainer | How |
|---|---|
| Any decision (D1) | The list of decisions waiting (D5); the answer is quoted on the issue. |
| A merge outside the standing authorisations | The list of decisions waiting (D5). The repository's settings do not ask before `gh pr merge`; the agent does not merge until the maintainer has answered. |
| Anything posted under the maintainer's account that the authorisations mark "ask" | The list of decisions waiting (D5), before posting. The repository's settings also ask before closing a pull request or an issue. |
| A change that persists in an environment or on the maintainer's machine | Asked before, with what cannot be undone (P7 in [specs/test-gates.md](../specs/test-gates.md)). |
| Tags, publication, release creation, signing policy, production keys, advisories, NuGet deprecation | Never done by an agent. The agent prepares the text and the checklist. |
