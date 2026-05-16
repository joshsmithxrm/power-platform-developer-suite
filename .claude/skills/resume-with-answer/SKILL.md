---
name: resume-with-answer
description: Deliver an operator decision to a bg/headless worker that paused via /await-operator. Use after reading the worker's .workflow/pending-ratification.json and deciding the answer. Operator-side companion to /await-operator (#1137).
---

# /resume-with-answer

`/resume-with-answer <short> <choice>` — write the operator's answer to a
paused bg/headless worker and clear its `state=blocked` flag so the
supervisor (and `claude attach`) re-engage it.

## When to use

- A bg/headless worker invoked `/await-operator` and is now sitting at
  `state=blocked` with `needs:` populated. You see it in `claude` Agent
  View as blocked, or `goal_supervisor.py` escalated it.
- You have read `.workflow/pending-ratification.json` (in that worker's
  worktree) and the draft artifact it references, and you have decided
  the answer.

## Arguments

- `<short>` (required) — the 8-character daemon session short (visible in
  Agent View or in the supervisor escalation entry).
- `<choice>` (required) — the freeform answer. Quote it if it contains
  spaces. The label may be one of the suggested `options` from the pending
  artifact, or a fresh sentence — the resumed worker treats it as text.

## Process

### Step 1 — Read the pending artifact

```bash
cat <worktree>/.workflow/pending-ratification.json
```

The file gives you `question`, `artifact_path`, and any suggested
`options`. Read `artifact_path` (the actual draft under review) before
deciding.

### Step 2 — Decide

Compose the choice. Default to one of the listed `options` when one fits;
otherwise write a freeform sentence the worker can act on.

### Step 3 — Deliver the answer

```bash
python scripts/resume_with_answer.py <short> "<choice>"
```

This:

1. Writes `.workflow/operator-answer.json` in the worker's worktree (the
   worktree is read from the daemon state's `cwd` field; override with
   `--worktree <path>` if stale).
2. Clears the daemon state — `state=working`, `needs=""`.

### Step 4 — Re-engage the worker

```bash
claude attach <short>
```

…or do nothing: the supervisor's next poll sees the cleared state and
re-engages the worker automatically. The worker's first action on its
next turn is to read `.workflow/operator-answer.json` and act on `choice`.

## Schema — `.workflow/operator-answer.json`

```json
{
  "choice": "string — your answer",
  "answered_at": "ISO 8601 UTC",
  "operator": "string — your handle / $USER"
}
```

The resumed worker is expected to delete this file (and the matching
`pending-ratification.json`) after consuming the answer, so a later
`/await-operator` call starts from a clean slate. If you see stale
`operator-answer.json` files lingering, the worker did not consume them —
that is a worker-side bug, not a resume bug.

## When NOT to use

- The worker is `state=working` (not blocked). It hasn't asked you
  anything. Leave it alone.
- The worker is `state=error`. That is a crash, not a question. Read the
  transcript via `claude transcript <short>` and decide whether to
  re-spawn.
- You want to inject arbitrary instructions into a running worker. This
  skill is narrow — it answers a specific outstanding question. To direct
  a running worker, use `claude attach <short>` and send a message.

## Related

- `.claude/skills/await-operator/SKILL.md` — the worker-side half.
- Issue #1137 — design rationale.
- `scripts/resume_with_answer.py` — the helper this skill wraps.
- `.claude/interaction-patterns.md` §"Pause primitives by session type".
