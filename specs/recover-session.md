# Recover Session

**Status:** Implemented
**Last Updated:** 2026-05-15
**Code:** [.claude/skills/recover-session/](../.claude/skills/recover-session/), [scripts/recover-session.py](../scripts/recover-session.py)
**Surfaces:** Skill (workflow tooling)

---

## Overview

A workflow skill that recovers a Claude Code session the operator can no longer find in their resume picker — typically because the session is archived, its worktree was deleted, or both. Identifies the session from a phrase the operator remembers, diagnoses why it is invisible, restores prerequisites, and hands the operator the exact command (or one-click background spawn) to resume it.

### Goals

- **Identify the right session reliably** — disambiguate using the transcript's first user message, not raw keyword frequency, so phrase-based search doesn't false-positive on UUID fragments or boilerplate.
- **Diagnose visibility independently from restoration** — distinguish `isArchived: true` (CCD UI hides it), missing worktree (Claude Desktop can't anchor), and both, because the remedies differ.
- **Restore safely** — recreate a deleted worktree using absolute paths and `git -C <repo-root>` so the skill cannot create a nested worktree by accident.
- **Hand off cleanly** — emit a one-line `claude --resume <uuid>` invocation the operator can paste, with the option to bg-spawn the resumed session as an Agent View entry (deferred to Phase 2).
- **Pre-write the catch-up message** — branch state vs. origin/main, merged-since-quiet PRs, related issue state, so the recovered session does not redispatch finished work.

### Non-Goals

- **Reviving sessions whose transcript JSONL has been deleted.** If the transcript is gone, recovery is impossible; the skill reports this and exits.
- **Unarchiving via state-store edits.** CCD does not expose a programmatic unarchive tool to a Claude Code session; the skill coaches the operator through the UI toggle. Direct edits to `$APPDATA/Claude/` are out of scope.
- **Migrating a session between machines.** Recovery is local-machine only.
- **Solving why sessions get archived/wiped unexpectedly.** That root cause belongs to a separate investigation (filed under `investigate/session-archive-cleanup`); this skill addresses the symptom.
- **One-click resume in the operator's foreground terminal.** Not technically possible — a spawned `claude` process is a sibling, not a takeover of the operator's shell. Phase 2 delivers a bg-spawned Agent View entry, which is the closest legitimate equivalent.

---

## Architecture

```
┌──────────────────────────────────────────────────────────────┐
│ recover-session/SKILL.md   (orchestration + decision points) │
└──────────────────────────────────────────────────────────────┘
              │
              │ delegates mechanical phases
              ▼
┌──────────────────────────────────────────────────────────────┐
│ scripts/recover-session.py (CLI helper, JSON I/O)             │
│   subcommands:                                                │
│     identify   — grep transcripts, parse line-1, rank         │
│     diagnose   — archive state + worktree state               │
│     restore    — git worktree add (absolute, via git -C)      │
│     prepare    — pre-write catch-up message                   │
└──────────────────────────────────────────────────────────────┘
              │
              │ reads / writes
              ▼
┌──────────────────────────────────────────────────────────────┐
│ ~/.claude/projects/<encoded-cwd>/<uuid>.jsonl   (read-only)   │
│ git worktree state                              (read/write)  │
│ mcp__ccd_session_mgmt__list_sessions            (read-only)   │
└──────────────────────────────────────────────────────────────┘
```

### Components

| Component | Responsibility |
|-----------|----------------|
| `recover-session/SKILL.md` | Orchestrates phases; presents disambiguation choices to the operator; emits the final resume command. |
| `recover-session/REFERENCE.md` | Failure-mode catalogue, pitfall callouts (relative-path worktree-add, keyword-count ranking), entrypoint taxonomy (Claude Desktop vs CLI). |
| `scripts/recover-session.py` | Pure mechanics — transcript filesystem walk, JSONL line-1 extraction, worktree state checks, `git worktree add` with safety guards. Returns JSON to stdout; status text to stderr. |

### Dependencies

- Depends on conventions defined in `.claude/skills/TWO-FILE-PATTERN.md` (≤150 LOC SKILL.md).
- Reuses dispatch conventions from [workflow-enforcement.md](./workflow-enforcement.md) — never invoke `claude -p` outside the sanctioned spawn path.
- Sibling to [`start-launch.md`](./start-launch.md) — `/start` creates new worktree+session; `/recover-session` adopts an existing one. Symmetric but disjoint.

---

## Specification

### Core Requirements

1. **Identification phase** must search `~/.claude/projects/**/*.jsonl` for an operator-supplied phrase and rank candidates by reading the first user message (the literal turn-1 content) of each match, not by raw hit count.
2. **Identification must surface the entrypoint** (`claude-desktop` vs CLI) extracted from the transcript so the resume coaching matches the surface the session was started in.
3. **Diagnosis phase** must report, for the chosen session, three independent booleans: `is_archived`, `worktree_exists`, `branch_exists`. The combination determines the restore path.
4. **Restoration phase** must invoke `git worktree add` with (a) an absolute path for the target and (b) `git -C <main-repo-root>` so the invocation cannot create a worktree relative to a nested or unrelated cwd.
5. **Restoration must skip silently** if the worktree already exists at the expected path and is registered with git. It must error explicitly if a stale `git worktree` registration exists without a corresponding directory (a prior incomplete cleanup).
6. **Handoff phase** must emit a copy-pasteable resume command (`claude --resume <uuid>`) and the absolute path to `cd` to first. It must also produce a catch-up message that summarizes branch-vs-main delta, merged-since-last-activity PRs touching the same area, and any open issues filed during the session.
7. **Skill must not mutate CCD state-store files.** Unarchive remains a UI-driven action; the skill instructs the operator and waits.
8. **Skill must not touch any session other than the one being recovered.** Read-only access to other sessions for cross-referencing the parent-child topology is permitted; modification is not.

### Primary Flows

**Flow A — Session is archived, worktree intact, branch intact:**

1. Operator: "I can't find my session about X."
2. Skill runs `recover-session.py identify --query "X"` → ranked list keyed by line-1.
3. Operator selects.
4. `recover-session.py diagnose --session <uuid>` → `{is_archived: true, worktree_exists: true, branch_exists: true}`.
5. Skill instructs: "Toggle 'Archived' filter in CCD UI, unarchive '<title>', then resume."
6. Emits catch-up message.

**Flow B — Worktree deleted, branch intact, not archived:**

1–3. As above.
4. Diagnose returns `{is_archived: false, worktree_exists: false, branch_exists: true}`.
5. Skill calls `recover-session.py restore --session <uuid>` → recreates worktree at the original path via `git -C <repo-root> worktree add <abs-path> <branch>`.
6. Emits resume command (`cd <abs-path> && claude --resume <uuid>`) and catch-up message.

**Flow C — Both (archived + worktree deleted):**

1–4. As above. Diagnose returns both true.
5. Skill restores worktree (step B5).
6. Skill instructs UI-side unarchive (step A5).
7. Emits resume command + catch-up message.

**Flow D — Transcript missing or branch deleted (unrecoverable):**

1–3. As above.
4. Diagnose returns `branch_exists: false` OR identify returns no transcript.
5. Skill reports specifically what is missing, suggests `git reflog` for branch recovery if branch is gone, and exits.

### Surface-Specific Behavior

This is a workflow skill, not a product feature. No CLI / TUI / Extension / MCP surface variations.

### Constraints

- `SKILL.md` must remain ≤150 lines per `.claude/skills/TWO-FILE-PATTERN.md`.
- `scripts/recover-session.py` must emit JSON to stdout (data) and prose to stderr (status) per Constitution **I1**.
- Restoration must use absolute paths and `git -C` to avoid the nested-worktree class of bug (pitfall observed in the originating session, 2026-05-15).
- Identification must read transcript line 1 — not match by keyword count — to avoid the UUID-fragment false-positive class (pitfall observed same date).

### Validation Rules

| Field | Rule | Error |
|-------|------|-------|
| `--query` | Min 4 chars | `query too short — provide a phrase the operator remembers` |
| `--session` (uuid) | Matches an existing `~/.claude/projects/**/<uuid>.jsonl` | `transcript not found at expected path` |
| Restore target path | Must be absolute, normalized | `restore requires absolute path` |
| Restore target path | Must not already exist as a populated directory | `target path exists and is non-empty — refusing to overwrite` |

---

## Acceptance Criteria

| ID | Criterion | Test | Status |
|----|-----------|------|--------|
| AC-01 | `recover-session.py identify --query "<phrase>"` returns JSON array of candidates, each containing `session_id`, `title`, `cwd`, `entrypoint`, `first_user_message` (line-1 content), `last_activity_ts`. Ranking is by line-1 content quality (literal phrase match in line-1 beats matches elsewhere in the transcript). | `test_identify_ranks_by_line_one` | ✅ |
| AC-02 | When two candidates both contain the query string but only one has it in the first user message, AC-01 ranks the line-1 match first. | `test_identify_prefers_line_one_over_body` | ✅ |
| AC-03 | `recover-session.py diagnose --session <uuid>` returns JSON with `is_archived` (from CCD list_sessions, via optional `--ccd-sessions-file`), `worktree_exists` (from git worktree list), `branch_exists` (from `git branch --list`), and `entrypoint`. | `test_cmd_diagnose_returns_three_booleans_against_live_repo` | ✅ |
| AC-04 | `recover-session.py restore --session <uuid>` invokes `git -C <repo-root> worktree add <absolute-path> <branch>` and returns `{"restored": true, "path": "<abs-path>"}`. Path is computed from the session's recorded `cwd`, not from the helper's own cwd. | `test_restore_creates_worktree_at_absolute_path` | ✅ |
| AC-05 | `recover-session.py restore` refuses to operate when the requested target is relative — guards against the nested-worktree pitfall. | `test_restore_rejects_relative_with_resolvable_branch` | ✅ |
| AC-06 | `recover-session.py restore` is idempotent — running twice in a row with the worktree already restored exits 0 with `{"restored": false, "reason": "already-present"}`. | `test_restore_is_idempotent_when_worktree_already_exists` | ✅ |
| AC-07 | `recover-session.py prepare --session <uuid>` emits a catch-up message including: branch-vs-`origin/main` ahead/behind count, list of PRs merged into main since session's last activity timestamp, and any GH issues referenced in the transcript. | `test_cmd_prepare_returns_catch_up_against_live_repo` | ✅ |
| AC-08 | `SKILL.md` is ≤150 lines (enforced by existing `skill-line-cap.py` hook). | `test_skill_md_under_line_cap` | ✅ |
| AC-09 | `SKILL.md` references `REFERENCE.md §N` for taxonomies (failure modes, entrypoint matrix) using the canonical reference-loading syntax, and every cited section exists. | `test_skill_md_references_reference_sections` + `test_reference_md_has_corresponding_sections` | ✅ |
| AC-10 | When the transcript is missing for a queried session, `diagnose` returns `{"recoverable": false, "next_action": "unrecoverable-transcript-missing"}` and `transcript_exists: false`. | `test_cmd_diagnose_returns_transcript_missing_for_unknown_uuid` | ✅ |
| AC-11 | The skill never mutates CCD session-store files (`$APPDATA/Claude/claude-code-sessions/**`) nor `~/.claude/projects/*.jsonl` transcripts. | `test_no_writes_to_ccd_state_store` (builtins.open audit) | ✅ |

### Edge Cases

| Scenario | Input | Expected Output |
|----------|-------|-----------------|
| Multiple sessions match the phrase, all in line-1 | `--query "filed as #1074"` matches 2 sessions where both first messages contain it | Returns both, ranked by `last_activity_ts` descending; operator chooses |
| Branch exists but is detached | `restore` against a branch that is checked out elsewhere | Returns `{"restored": false, "reason": "branch-in-use-elsewhere", "current_worktree": "<path>"}` |
| Worktree exists but at a different path than recorded | Recorded `cwd` is `A`; git worktree list has it at `B` | Returns `{"restored": false, "reason": "already-present", "path": "B"}` — do not move it |
| Transcript dir contains subagent JSONLs only | `identify` query matches only a subagent transcript | Skip — only top-level transcripts qualify as sessions |

### Test Examples

```python
# scripts/tests/test_recover_session.py
def test_identify_prefers_line_one_over_body(tmp_transcript_dir):
    # Two transcripts: A has the query in line 1, B only in line 50
    a = make_transcript(tmp_transcript_dir, line_one="we are working on #1074", body="")
    b = make_transcript(tmp_transcript_dir, line_one="hello world", body="#1074 elsewhere")
    result = identify(query="#1074", root=tmp_transcript_dir)
    assert result[0]["session_id"] == a
    assert result[1]["session_id"] == b
```

---

## Core Types

### `IdentifyResult`

```python
@dataclass(frozen=True)
class IdentifyResult:
    session_id: str           # transcript UUID
    title: str                # from CCD list_sessions
    cwd: str                  # original recorded cwd (may not exist)
    entrypoint: str           # "claude-desktop" | "cli" | "bg-spawn"
    first_user_message: str   # transcript line 1, truncated to 400 chars
    last_activity_ts: str     # ISO 8601
    match_score: int          # higher = stronger; line-1 hit > body hit
```

### `DiagnoseResult`

```python
@dataclass(frozen=True)
class DiagnoseResult:
    session_id: str
    is_archived: bool
    worktree_exists: bool
    branch_exists: bool
    transcript_exists: bool
    entrypoint: str
    recoverable: bool        # true iff transcript_exists and branch_exists
    next_action: str         # "unarchive-and-resume" | "restore-and-resume" | "restore-unarchive-resume" | "resume" | "unrecoverable"
```

### Usage Pattern

```bash
# Skill invokes the helper; helper returns JSON; skill renders to operator.
python scripts/recover-session.py identify --query "retro of the retro" \
  | python scripts/recover-session.py diagnose --stdin \
  | python scripts/recover-session.py prepare --stdin
```

---

## Error Handling

### Error Types

| Error | Condition | Recovery |
|-------|-----------|----------|
| `QueryTooShort` | `--query` shorter than 4 chars | Operator supplies a longer phrase |
| `TranscriptNotFound` | UUID does not match any JSONL under `~/.claude/projects/` | Session is unrecoverable — exit with explanation |
| `BranchNotFound` | Git branch deleted | Suggest `git reflog` to find the tip commit; manual recreation only |
| `NestedWorktreeRefused` | Restore would create a nested worktree (cwd is inside another worktree and path is relative) | Use absolute path; re-invoke |
| `BranchInUseElsewhere` | Target branch is checked out in a different worktree | Report the current worktree's path; do not duplicate |

### Recovery Strategies

- **Transcript missing:** unrecoverable. The skill exits and explains what was lost.
- **Branch missing but transcript present:** the transcript content can be read for archaeology but the working state is lost. Operator decides whether reflog recovery is worth pursuing.
- **Worktree at unexpected path:** report and stop. Operator decides whether to move it themselves.

### Edge Cases

| Scenario | Expected Behavior |
|----------|-------------------|
| Empty query | Reject with validation error before any FS access |
| Transcript exists but is empty (0 bytes) | Treat as `TranscriptNotFound` |
| Two sessions share a CCD title | Use `session_id` (UUID) for disambiguation in handoff prompt |

---

## Design Decisions

### Why a separate skill instead of extending `/start`?

**Context:** `/start` already manages worktree creation + session spawn. Adding `--resume <session>` would be discoverable.

**Decision:** Separate skill (`/recover-session`).

**Alternatives considered:**
- Extend `/start` with `--resume` flag: rejected — `/start` creates and registers (workflow-state init, in-flight register); recovery *adopts* an existing session and must not re-init state. The semantic conflict would proliferate special cases inside `/start`.
- Bolt onto `/cleanup`: rejected — `/cleanup` is destructive; recovery is restorative.

**Consequences:**
- Positive: each skill has a single clear domain.
- Negative: one more skill in the registry; trigger phrases must be precise to avoid colliding with `/start` or `/cleanup`.

### Why rank identification by transcript line-1 instead of keyword frequency?

**Context:** Originating-session bug, 2026-05-15: I (Claude) misidentified the operator's session by matching on `1074` hit-count. The losing candidate (`romantic-yonath-deb378`) had 4 hits but all were UUID fragments or URL substrings. The winning candidate's line 1 was the operator's actual opening prompt.

**Decision:** Always rank by first-user-message content. Line-1 match dominates body matches.

**Alternatives considered:**
- Keyword frequency / tf-idf: rejected — UUID fragments and serialized JSON inflate counts for irrelevant transcripts.
- Last-user-message: rejected — operators usually remember how a session *started*, not how it ended.
- LLM-based semantic match: deferred — adds dependency on a model call for what is fundamentally a substring problem.

**Consequences:**
- Positive: deterministic, fast, no false positives from boilerplate.
- Negative: an operator who remembers a mid-session decision but not the opening prompt may need to scan candidates manually. Mitigated by also returning body-match candidates with lower rank.

### Why restore worktrees with `git -C <repo-root>` and absolute paths?

**Context:** Originating-session bug, 2026-05-15: I (Claude) ran `git worktree add .claude/worktrees/<name> <branch>` while my shell sat inside another worktree. The relative path resolved against that worktree's cwd, producing a nested worktree at `peaceful-bartik-86c8cc/.claude/worktrees/xenodochial-margulis-5341ce` instead of the top-level path. The operator could not `cd` to the path they expected, and the resulting worktree was unusable for resumption (CWD mismatch with the recorded session cwd).

**Decision:** All `git worktree add` invocations in the helper script and SKILL.md (a) use `git -C <main-repo-root>` to fix the repo context and (b) pass an absolute target path. The helper rejects relative paths with `NestedWorktreeRefused`.

**Alternatives considered:**
- `cd` to repo root first: rejected — shell state in the SKILL is fragile (cd hooks, fnm activation, etc., as observed in originating session). `git -C` is purely flag-driven.
- Validate paths at runtime only (don't refuse): rejected — silent acceptance of relative paths invites the same bug class to recur.

**Consequences:**
- Positive: the bug class cannot recur.
- Negative: slightly more verbose call sites.

### Why no programmatic unarchive?

**Context:** CCD exposes `archive_session` to a Claude Code session but not its inverse. State-store edits to `$APPDATA/Claude/claude-code-sessions/` work but are unsupported.

**Decision:** Skill emits UI instructions, does not edit state files.

**Alternatives considered:**
- Direct state-store edit: rejected — undocumented schema, future CCD upgrades may invalidate the edit, risk of corrupting unrelated session metadata.
- Wait for upstream CCD to add an unarchive tool: accepted as the long-term path; this spec is forward-compatible with it.

**Consequences:**
- Positive: zero risk of corrupting CCD state.
- Negative: handoff includes a manual UI step. Acceptable — this is rare (operator only triggers archive intentionally).

### Phase split — what ships in Phase 1 vs Phase 2

**Context:** "Have you do the `claude --continue` for me" was an explicit operator ask. Delivering that cleanly requires `scripts/start-bg-spawn.py` (and underlying `claude_dispatch.py`) to support resuming a session by UUID, which is unverified.

**Decision:**
- **Phase 1 (this spec):** skill + helper script; final phase emits `claude --resume <uuid>` for the operator to run. Operator has agency over which surface (Desktop / terminal).
- **Phase 2 (separate issue):** verify and, if needed, extend the dispatch primitive to support `--resume <uuid>`; add a final phase to this skill that bg-spawns the resumed session as an Agent View entry.

**Alternatives considered:**
- Bundle Phase 2: rejected — Phase 2's scope depends on `claude_dispatch.py` behavior I have not audited; could be 5 LOC or 50 LOC. Don't block Phase 1.

**Consequences:**
- Positive: Phase 1 ships in days, not blocked on dispatch-layer work.
- Negative: handoff requires one command-line paste. Acceptable.

---

## Extension Points

### Adding a new failure mode to the diagnosis taxonomy

1. **Append to `REFERENCE.md §<N>`** with the new failure-mode entry (symptom, cause, remedy).
2. **Add a `recoverable` / `next_action` value** to `DiagnoseResult` in the helper.
3. **Add an AC** mirroring AC-03's structure for the new case.

### Adding a new entrypoint (e.g., a future surface)

1. Extend `IdentifyResult.entrypoint` taxonomy.
2. Update the handoff phase in `SKILL.md` to coach the operator toward the appropriate surface's resume UI.
3. Document in `REFERENCE.md` entrypoint matrix.

---

## Related Specs

- [start-launch.md](./start-launch.md) — sibling skill that creates new worktree+session. Recovery and creation share the worktree mechanics (`worktree-create.py` patterns) and the dispatch ground rules.
- [workflow-enforcement.md](./workflow-enforcement.md) — defines the `claude_dispatch.py:spawn()` sanctioned spawn path. Phase 2 will use it; Phase 1 emits a command for the operator.
- [skill-fixes-cleanup-start.md](./skill-fixes-cleanup-start.md) — prior precedent for skill-level workflow corrections; informs the conventions used here.

---

## Changelog

| Date | Change |
|------|--------|
| 2026-05-15 | Initial spec — Phase 1 (skill + helper script, manual resume handoff). |
| 2026-05-15 | Implementation landed: 4 subcommands (identify/diagnose/restore/prepare), SKILL.md (95 LOC, under cap), REFERENCE.md (7 sections), 54 tests passing. All 11 ACs covered. |
