---
name: await-operator
description: Pause a bg/headless worker to wait for operator input. Use when a worker needs a real human decision before continuing — ratifying a draft, choosing between options, escalating ambiguity. THE primitive for bg/headless sessions; AskUserQuestion does NOT pause in --bg sessions (the daemon auto-answers within ~15-60s — see #1137 / smoking gun #1105).
---

# /await-operator

`/await-operator` is the only correct way for a bg or headless worker to wait
on a human. `AskUserQuestion` does not block in those contexts.

## Arguments

- `artifact_path` (required) — path (in the worktree, repo-relative) to the
  draft artifact the operator should review before deciding.
- `question` (required) — one sentence, the decision the operator must make.
- `options` (optional) — list of suggested choice labels. Informational
  only; the operator answers freeform via `/resume-with-answer`.

## Process

### Step 1 — Make sure the artifact exists

The operator opens `artifact_path` to make the decision. If you have not
already written the draft, write it now. The artifact must be on disk
before you invoke the helper.

### Step 2 — Invoke the helper

```bash
python scripts/await_operator.py \
  --artifact-path "<repo-relative artifact path>" \
  --question "<one-sentence decision>" \
  --option "<label 1>" --option "<label 2>"
```

The helper writes `.workflow/pending-ratification.json` and flips the daemon
session state to `state=blocked` with `needs:` populated. The supervisor's
existing poll (`scripts/claude_dispatch.py` `BgHandle.wait`,
`scripts/goal_supervisor.py:_evaluate_entry`) escalates on that shape with
no further wiring.

### Step 3 — EXIT THE TURN

**After the helper returns, the worker MUST exit the current turn.** Do not
call further tools. Do not write a summary. Do not advance to the next
phase. The session must be quiescent so the daemon-poll snapshots
`state=blocked` and the supervisor escalates.

If you do anything after the helper, the daemon may overwrite `state` back
to `working` and the operator never sees the question.

### Step 4 — Operator answers (out-of-band)

The operator (or supervisor on autonomous loop) reads
`.workflow/pending-ratification.json` and the draft at `artifact_path`,
then runs:

```bash
/resume-with-answer <short> <choice>
```

This writes `.workflow/operator-answer.json` and clears the daemon
`state=blocked`. See `.claude/skills/resume-with-answer/SKILL.md`.

### Step 5 — Resumed worker re-reads on next turn

When this session is re-engaged (the operator runs `claude attach <short>`
or the supervisor re-polls and sees state cleared), the **first action the
worker takes** is to read `.workflow/operator-answer.json` from the
worktree root and act on `choice`. Then delete (or archive) both
`.workflow/pending-ratification.json` and `.workflow/operator-answer.json`
before continuing, so a later `/await-operator` call starts from a clean
slate.

## Contract summary

| Stage | Who writes | Where | Daemon `state` after |
|-------|-----------|-------|----------------------|
| 1. pause | worker (this skill) | `.workflow/pending-ratification.json` + daemon `state.json` | `blocked` |
| 2. escalate | supervisor poll | (none — reads only) | `blocked` |
| 3. answer | operator (`/resume-with-answer`) | `.workflow/operator-answer.json` + clears daemon `state` | `working` |
| 4. consume | worker (next turn) | reads `operator-answer.json`, deletes both files | `working` → `done` |

## When NOT to use

- **Interactive sessions** (you are at the terminal): use `AskUserQuestion`.
  The bg-pause primitive adds latency for no benefit.
- **Trivial choices you can default**: pick a sensible default and document
  the assumption in the PR body. Pausing is expensive — the worker tears
  down, the operator context-switches.
- **Hard failures**: just fail. `state=blocked` + `needs:` is for *I need
  input*, not *I crashed*. For crashes, exit non-zero and let the
  supervisor surface the transcript.

## Schema — `.workflow/pending-ratification.json`

```json
{
  "question": "string — the operator-facing question",
  "artifact_path": "string — repo-relative path to draft",
  "options": ["string", "..."],
  "created_at": "ISO 8601 UTC",
  "session_short": "8-char daemon session short"
}
```

Idempotent — re-invoking the skill overwrites the file.

## Schema — `.workflow/operator-answer.json`

```json
{
  "choice": "string — operator's freeform answer",
  "answered_at": "ISO 8601 UTC",
  "operator": "string — handle / email"
}
```

Written by `/resume-with-answer`. Read by the resumed worker.

## Related

- Issue #1137 — design rationale and acceptance criteria.
- `.workflow/investigation-bg-pause-primitive.md` (branch
  `feat/investigate-bg-pause-primitive`) — why `AskUserQuestion` doesn't
  work in bg sessions.
- `.claude/skills/resume-with-answer/SKILL.md` — the operator-side half.
- `.claude/interaction-patterns.md` §"Pause primitives by session type".
- `scripts/await_operator.py` — the helper this skill wraps.
