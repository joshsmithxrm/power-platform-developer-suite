---
name: recover-session
description: Recover a Claude Code session that disappeared from the resume picker — archived, worktree deleted, or both. Identifies by transcript line-1 (not keyword count), diagnoses visibility independent of restoration, restores worktrees with absolute paths via `git -C`, emits a copy-pasteable resume command plus a pre-written catch-up message.
---

# Recover Session

Adopt an existing session whose transcript still exists but which is invisible to the operator's resume picker. Sibling to `/start` (which creates new sessions); this skill never creates state, only restores it.

## When to Use

- "I can't find my session about X"
- "Session disappeared from recents"
- "Session not in the resume list"
- "Lost session from last night"
- "I archived something I shouldn't have"

Do NOT use for: a session whose transcript JSONL is gone (unrecoverable), a session you want to *clone* (use `/start`), or for general worktree cleanup (use `/cleanup`).

## Process

### Step 1: Identify

Ask the operator for a phrase they remember — typically the opening prompt, an issue number, or a decision they recall.

```bash
python scripts/recover-session.py identify --query "<phrase>"
```

Returns a JSON array of candidates, ranked by transcript line-1 match first, body match second. Read REFERENCE.md §1 "Disambiguation rules" before presenting choices — never rank by raw keyword frequency.

Present candidates to the operator showing `first_user_message` (the disambiguator), `entrypoint`, and `last_activity_ts`. Wait for selection.

### Step 2: Diagnose

```bash
python scripts/recover-session.py diagnose --session <uuid> \
  [--ccd-sessions-file <path-to-list_sessions-json>]
```

To fill in `is_archived`, first call `mcp__ccd_session_mgmt__list_sessions` (with `include_archived: true`), write the result to a temp file, and pass it via `--ccd-sessions-file`. Without it the script returns `is_archived: null` and you'll need to coach the operator through CCD UI confirmation.

Read REFERENCE.md §2 "Diagnosis state matrix" to interpret the `next_action` field. If `unrecoverable-*`, stop and report.

### Step 3: Restore (conditional)

If `next_action` contains `"restore"`:

```bash
python scripts/recover-session.py restore --session <uuid>
```

Refuses cleanly on `NestedWorktreeRefused`, `BranchInUseElsewhere`, or `BranchNotFound`. Idempotent on `already-present`. Read REFERENCE.md §5 "Pitfalls and anti-patterns" before troubleshooting any failure mode.

### Step 4: Coach unarchive (conditional)

If `next_action` contains `"unarchive"`:

The skill does not edit CCD state-store files (`$APPDATA/Claude/claude-code-sessions/**`). Instead, instruct the operator. Read REFERENCE.md §3 "Unarchive UI by surface" for the exact step path keyed off `entrypoint` (Claude Desktop vs CLI vs bg-spawn).

Wait for operator confirmation that the unarchive completed before Step 5.

### Step 5: Prepare catch-up

```bash
python scripts/recover-session.py prepare --session <uuid>
```

Returns the resume command and a catch-up message containing: branch-vs-`origin/main` delta, PRs merged since session's last activity, and `#NNN` issue refs from the transcript. The catch-up text already includes the CLAUDE.md PublicAPI rebase hint.

### Step 6: Handoff

Emit to the operator, verbatim from Step 5's JSON output:

```
cd <recorded_cwd>
claude --resume <uuid>
```

Followed by the catch-up message. Read REFERENCE.md §4 "Handoff template" for the canonical render.

Stop here. The operator runs the command in their preferred terminal or Claude Desktop folder. Do not invoke `claude --resume` yourself — your process is a sibling, not a takeover of the operator's shell. (Phase 2 of this skill will add an optional bg-spawn that gives the operator an Agent View entry to click; see spec Design Decisions.)

## Rules

1. **Never edit CCD session-store files.** Unarchive is UI-only.
2. **Always use absolute paths** with `git worktree add` — the helper enforces this; do not work around the refusal by retrying with `--force`.
3. **Disambiguate by line-1, not keyword count.** The helper ranks correctly; do not re-rank in the skill.
4. **Stop at unrecoverable conditions.** `unrecoverable-transcript-missing` and `unrecoverable-branch-missing` are terminal — do not guess at the missing state.
5. **Never touch other sessions.** Read-only access to cross-reference parent-child topology is fine; modification is not.
6. **Failures are JSON.** `restored: false` with a `reason` is information for the operator, not a retry signal. Read REFERENCE.md §5 before any workaround.

## Output contract

This skill is read-only on user state. Its only side effect outside the prepared message is a single `git worktree add` invocation (in Step 3, conditionally). Filesystem audit (AC-11) covers this: no writes to `~/.claude/projects/`, no writes to `$APPDATA/Claude/claude-code-sessions/`.
