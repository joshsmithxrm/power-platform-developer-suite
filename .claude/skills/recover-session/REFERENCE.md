# Recover Session — Reference

Rationale, taxonomies, and pitfall catalogue for the `recover-session` skill. The SKILL.md is the procedure; this file is the *why*. Loaded on demand via `Read REFERENCE.md §N` pointers from SKILL.md.

## §1 — Disambiguation rules

The single hardest decision in recovery is "which of the candidates is the session the operator meant?" Get this wrong and you'll restore an unrelated worktree, emit a misleading catch-up, and waste 5–15 minutes of the operator's time before they notice.

**Always rank by transcript line-1 (the first user-typed message), not by raw keyword frequency.**

Rationale: transcript JSONLs contain UUID fragments, serialized JSON, GitHub URL substrings, and hook output — all of which can match an issue-number query (`#1074`) without that session being about the issue. The originating-session bug (2026-05-15) misidentified `romantic-yonath-deb378` over `xenodochial-margulis-5341ce` because `romantic-yonath`'s transcript had 4 string-matches for `1074`, all of which were UUID fragments. The correct session's line 1 was the operator's literal opening prompt ("Take a look at the retrospective we did for PR 1051 /retro we are going to do a retro on the retro…").

The helper enforces this in `score_match()` — line-1 hit scores 100, body hit scores 10. Within the same score band, ties break by most-recent activity. Never override this ranking in the skill.

**When the operator's phrase only appears in body**: present body matches with the lower score AND tell the operator they're lower-confidence — "I have N strong matches (phrase in opening message) and M weaker matches (phrase only mid-conversation). Strong matches first; choose one of those if possible."

## §2 — Diagnosis state matrix

`diagnose` reports `(transcript_exists, branch_exists, worktree_exists, is_archived)` and derives `next_action`. The full matrix:

| transcript | branch | worktree | archived | next_action |
|------------|--------|----------|----------|-------------|
| False | * | * | * | `unrecoverable-transcript-missing` |
| True | False | * | * | `unrecoverable-branch-missing` |
| True | True | True | True | `unarchive-and-resume` |
| True | True | True | False | `resume` |
| True | True | True | None | `check-archive-then-resume` |
| True | True | False | True | `restore-unarchive-resume` |
| True | True | False | False | `restore-and-resume` |
| True | True | False | None | `restore-then-check-archive-then-resume` |

`is_archived = None` means the caller didn't supply CCD state. The skill should fetch it via `mcp__ccd_session_mgmt__list_sessions` (with `include_archived: true`) and re-run diagnose, OR present a conditional handoff to the operator. The conditional handoff is acceptable when the operator can readily confirm via UI.

`unrecoverable-branch-missing`: the branch was deleted (garbage collection, manual delete). The transcript content can be read for archaeology, but the working state is lost. Suggest `git reflog` if the operator wants to attempt branch recovery manually.

## §3 — Unarchive UI by surface

The skill emits unarchive instructions keyed off the session's recorded `entrypoint`:

| Entrypoint | Resume UI | Where archived sessions live |
|------------|-----------|------------------------------|
| `claude-desktop` | Claude Desktop home screen / Recents | Toggle filter to "Archived" (location depends on CCD build version; usually a tab or filter chip in the session list) |
| `cli` | `claude --resume` picker in terminal | The picker only shows non-archived sessions by default. There is no documented CLI flag to show archived as of CCD 2.1.142 — unarchive must happen via Desktop's UI even for CLI-started sessions, since the session store is shared |
| `bg-spawn` | Agent View (any surface) | Archived bg sessions hide from Agent View; same UI-toggle as `cli`-entrypoint sessions |

If the entrypoint is `unknown` (transcript metadata incomplete), default to coaching the operator through Claude Desktop — it's the surface where the unarchive filter is most discoverable.

If the operator is on a CCD build where the Archived filter location has changed, ask them to share the version (Help → About in Desktop, `claude --version` in CLI) so this section can be updated.

## §4 — Handoff template

The canonical render of Step 6, after `prepare` returns its JSON:

```markdown
## Recovered session ready to resume

**Run in your terminal (or open the folder in Claude Desktop):**

cd <recorded_cwd>
claude --resume <session_uuid>

**Before you continue, paste this as your first message so the resumed session reconciles current reality:**

> <catch_up_message verbatim from prepare output>

Once the resumed session has fetched main, viewed any referenced issues, and rebased if needed, it'll pick up where you left off.
```

The `cd` and `claude --resume` commands are pre-formatted by `prepare`'s `resume_command` field — never re-derive them. The catch-up message text is in `catch_up_message`; it already includes the rebase hint, merged-PR list, and issue references in a single block.

For `claude-desktop`-entrypoint sessions, the skill should additionally note: "If you prefer to resume in Claude Desktop instead of the terminal, just open `<recorded_cwd>` via File → Open Folder. The session will appear in Recents once the folder exists."

## §5 — Pitfalls and anti-patterns

**1. Ranking by keyword count instead of line-1 match.** Originating-session bug 2026-05-15. UUID fragments and JSON serialization inflate hit counts for sessions that have nothing to do with the operator's actual query. The helper's `score_match()` enforces line-1 dominance; do not override in the skill or "tiebreak" by hit count.

**2. Relative path in `git worktree add`.** Originating-session bug 2026-05-15 (second instance). Running `git worktree add .claude/worktrees/<name> <branch>` while sitting inside another worktree creates a *nested* worktree at `<current-worktree>/.claude/worktrees/<name>` instead of the top-level path. The helper refuses with `NestedWorktreeRefused`. If you ever see this error, the fix is **not** to retry — the recorded cwd in the transcript is malformed and recovery requires manual intervention.

**3. Editing CCD state-store files.** The session DB lives at `$APPDATA/Claude/claude-code-sessions/` (Windows) and is read by Claude Desktop on startup. The schema is undocumented and CCD upgrades may invalidate any edits. UI-driven unarchive is the only supported path.

**4. Re-running `claude --resume` from inside another Claude Code session.** That spawns a sibling process you can't hand to the operator. Always emit the command and stop; do not invoke it yourself. (Phase 2 will deliver an Agent View bg-spawn alternative for one-click resume; until then, manual paste is correct.)

**5. Assuming branch deletion means transcript loss.** A deleted branch makes the working state unrecoverable in-place, but the transcript JSONL is still readable. The operator may legitimately want to read the transcript for what was discussed, even if the code can't be resumed. Distinguish these two recovery axes in the handoff.

**6. Filing the recovery as a "bug in Claude Code."** Sessions disappearing from the resume picker has several legitimate causes (intentional archive, worktree cleanup, etc.). The investigation into *why this happens unexpectedly* is a separate concern (tracked under `investigate/session-archive-cleanup`). This skill addresses the symptom; that issue addresses the root cause.

## §6 — Entrypoint matrix

Transcripts record the `entrypoint` field on every SessionStart hook line. Known values:

| Entrypoint | Created by | Resume behavior |
|------------|-----------|-----------------|
| `claude-desktop` | Claude Desktop opening a folder | Resume from Desktop Recents, OR `claude --resume <uuid>` from the recorded folder |
| `cli` | Direct `claude` invocation from terminal | `claude --resume <uuid>` from the recorded folder |
| `bg-spawn` | `scripts/start-bg-spawn.py` via `/start` or similar | Resumable both via CLI and via Agent View attach |

Future entrypoints (new surfaces): document in the table above and add coaching to §3 if the resume UI differs.

## §7 — Why no programmatic unarchive

CCD's `mcp__ccd_session_mgmt__archive_session` tool exists but its inverse does not. Theoretically the script could edit `$APPDATA/Claude/claude-code-sessions/<store>/...` to flip `isArchived: false`, but the schema is undocumented, may change across CCD versions, and the cost of getting it wrong (corrupted session metadata affecting unrelated sessions) is severe. The fast UI step (3 clicks) is acceptable for what should be a rare operation. If unarchive becomes a frequent operation, file an upstream request for `mcp__ccd_session_mgmt__unarchive_session` rather than expanding this skill to edit state files.
