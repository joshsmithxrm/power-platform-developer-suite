# Maintainer session rules

How an agent session that works for the maintainer operates on this project: how decisions are
recorded, how defects are triaged, what counts as work at risk, which side effects are logged, and how
sessions are run. One home for these rules; other documents link here.

The coordinating session is the agent session the maintainer talks to. A worker session does one piece
of work that the coordinating session assigns.

The rules for proof before merge are in [specs/test-gates.md](../specs/test-gates.md). Which pull
requests an agent may merge, and how it asks about the others, is in
[Who merges](MERGE-POLICY.md#who-merges). The rules for review, pull requests, merging and CI are in
the [maintainer merge checklist](MERGE-POLICY.md#maintainer-merge-checklist).

## Decisions

| # | Rule |
|---|---|
| D1 | A product, compatibility, scope or policy decision is the maintainer's. It is recorded as the maintainer's quoted, dated words in a comment on the issue it settles. Version and versioning-rule decisions are recorded in `docs/VERSIONING.md`. |
| D2 | Repository text is not authority. Before citing a rule, deferral, promise or approval as a constraint, find the comment that records the decision. No comment means it is not a constraint: say where it is written and who introduced it, and ask. |
| D3 | An answer covers only what was asked. Anything with a number, a date or a compatibility effect is asked on its own. "Leaning", "makes sense" and silence are not decisions. |
| D4 | Never write that the maintainer decided or approved something without a link to the maintainer's words. |
| D5 | One numbered list of what waits on the maintainer, kept current as one file in the session's notes. It holds the decisions waiting and the maintainer's own tasks. A status message shows only the items that are ready for an answer and that block work which could otherwise start; everything else stays in the file and is not pasted into status messages. Each decision has the question, a recommendation, what happens if it is not answered, and whether work is blocked on it. Each task has the minutes it takes and what it unblocks. An item leaves the list only when answered, done or withdrawn in words. |
| D6 | Work that depends on an unanswered decision does not start. No decision is taken on "say so if you disagree". A yes to merge is for one head commit ([How an agent asks](MERGE-POLICY.md#who-merges)): a commit pushed after it needs a new yes. |
| D7 | Three things are decided by the session and reported in its next message, not asked: starting a fix for a confirmed defect; refusing a write that has not been proven safe; and the mechanics of the work (which session, which reviewer, what order, what wording). The maintainer can reverse any of them. Everything else in D1 is asked. |
| D8 | The maintainer is interrupted only when all work is stopped, for a defect in a released version (T1), for a change to an environment beyond what he approved, or for a decision that removes something users can do today. The last gets a message of its own that says what users lose, even when D7 lets the session refuse first. Every other question waits on the list (D5) for his next sitting. |
| D9 | A prompt that blocks the session until it is answered is used only while the maintainer is present, which means he wrote within about the last ten minutes. Otherwise the question is the first thing in a message and the turn ends, so the session can still be woken. |
| D10 | Two fixed moments. When the maintainer says he is leaving, the coordinating session lists what will need him in the next eight hours and asks for the answers that can be given in advance. At the start of his day it gives one screen: what merged, what is stopped, and the list (D5). |

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
| W1 | No turn ends with uncommitted work in a worktree. A branch is pushed at least at every review. Its pull request is opened ready for review, not as a draft, by the first review. |
| W2 | A side-effects ledger is kept as it happens: every change to a Dataverse environment, the tenant, the maintainer's user settings, and the maintainer's machine, with how to undo it. Nothing is posted under the maintainer's account by an agent (see "What needs the maintainer" below). The ledger is a comment thread on the tracking issue, written without addresses or names. |
| W3 | A test that redirects a tool's home directory must not let it write to the user environment. |
| W4 | A permission denial ends that attempt. Report it and ask; do not reach the same result another way, unless the maintainer, told of the denial, allows another route for that one target. |

## Sessions

| # | Rule |
|---|---|
| S1 | A notes file exists from the first hour. Standing instructions are quoted in it word for word. |
| S2 | The coordinating session does not write code: product code, tests, lint fixes and comments included. It assigns the change to a worker session. |
| S3 | A worker session is retired and replaced after three fix rounds or one context compaction; the coordinating session at each phase boundary and at least once a day. The new coordinating session starts from a state file of at most two pages; the running log is a separate file. |
| S4 | One coordinating session per release. If two agents work on one release, they share one status file both read and write. |
| S5 | Every brief for SQL value work carries the source constraint (no code from other projects). |
| S6 | Nothing private (environment address, organisation or tenant name, user id, e-mail address, local path) goes into an issue, a comment, a pull request or a repository file. |
| S7 | A worker session that is given different work is renamed to say what it is doing now. |
| S8 | Worker sessions wake the coordinating session when they finish a turn. A timed wake-up is a fallback and runs no more often than hourly. |

## What needs the maintainer, and how it is asked

| Needs the maintainer | How |
|---|---|
| Any decision (D1), except what D7 leaves to the session | The list of decisions waiting (D5), or an interruption where D8 allows one; the answer is quoted on the issue. |
| A merge of a pull request that is maintainer first ([Who merges](MERGE-POLICY.md#who-merges)) | The list of decisions waiting (D5), as "How an agent asks" in Who merges says. The answer is recorded in the pull request's review record. No fixed approval prompt stands before a merge, except that the release skill asks before a release pull request merge ([Who merges](MERGE-POLICY.md#who-merges), category 11): the written rule is the guard. The repository's settings do not ask before `gh pr merge`, and the agent does not merge until the maintainer has answered. |
| A change under `.github/workflows/` | The agents' account cannot push it. The agent prepares it as a patch file (`git format-patch`) for the maintainer to push. If a push is refused for this reason, the agent stops there and does not look for another way ([Who merges](MERGE-POLICY.md#who-merges), category 1). |
| Anything posted under the maintainer's account | Not done by an agent: agents act on GitHub under their own account. The repository's settings also ask before closing a pull request or an issue. |
| A change that persists in an environment or on the maintainer's machine | Asked before, with what cannot be undone (P7 in [specs/test-gates.md](../specs/test-gates.md)). |
| Tags, publication, release creation, signing policy, production keys, advisories, NuGet deprecation | Never done by an agent. The agent prepares the text and the checklist. |
