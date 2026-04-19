# PPDS Notifications

How and when the PPDS workflow surfaces events to the developer outside
of the active terminal. Codifies the criteria matrix from the v1-launch
retrospective so future hooks know what bar to clear.

## Principles

- **Notify on completion or interruption, never on routine progress.**
  Toast spam trains the user to ignore the channel.
- **Notify the human when an autonomous action stops needing their
  attention** — PR ready, gate failed, agent crashed, awaiting input.
- **Prefer click-through.** Every notification carries a URL or follow-up
  action. A toast that cannot be acted on is useless.

## Criteria Matrix

| Event | Notify? | Latency target | Channel | Implementing hook |
|-------|---------|----------------|---------|-------------------|
| PR opened by `/pr` skill | yes | < 5s | toast | `.claude/hooks/notify.py` direct invocation |
| `/converge` finishes (PR ready or stuck) | yes | < 5s | toast | `.claude/hooks/notify.py` (Notification + idle_prompt matcher) |
| Pipeline stage failed (auto-heal or stop) | yes | < 5s | toast | `.claude/hooks/notify.py` via `pipeline.py` |
| Agent crashed (no progress for N min) | yes | up to N min (watchdog) | toast | future `agent-watchdog.py` (TODO) |
| Pre-commit / pre-push gate failed | no | n/a | terminal stderr only | none (gate exit code surfaces in attached shell) |
| Tests passing routinely | no | n/a | n/a | none |
| Build progress / step-by-step | no | n/a | n/a | none |
| Background pipeline progress every N seconds | no | n/a | n/a | none |
| `/pr` review surfaced new comments | future | TBD | toast | future `pr-comment-watch.py` (TODO) |
| Long-running command finished (any) | future | TBD | toast | future general "long-cmd-done" notifier |

## Batching and dedup

A 5-second batching window collapses identical notifications into a
single toast. Two pipeline stages failing within 5s of each other
should produce one notification, not two. Implementation lives in
`.claude/hooks/notify.py` (or its successor) — currently a TODO.

The user has reported (per retro) that quiet hours are handled by
their OS-level Do-Not-Disturb. We do NOT layer our own quiet-hours
gate on top — that just surprises the user when toasts disappear in
ways their OS does not predict.

## Watchdog Pattern

When an autonomous agent stops making progress, the surfacing channel is
**a toast, not a retry.** Auto-retry is fragile (loops on real bugs) and
hides incidents. The pattern is:

1. Watchdog detects no progress for N minutes (TODO: pick N per
   workflow type — pipeline ~10 min, converge ~15 min).
2. Toast fires with the agent's branch + last log line.
3. Human investigates. If safe to retry, human re-runs the workflow.
4. The hook does NOT auto-retry, even if the failure looks transient.

This is a deliberate trade — slower on real transient failures, much
safer when the failure is structural.

## Implementation TODOs (filed for retro PR-A or follow-on)

- **PushNotification capabilities.** Currently `notify.py` only does
  Windows toasts via `winotify`. Cross-platform parity (macOS Notification
  Center, Linux libnotify) is needed before we can rely on this for
  multi-developer scenarios.
- **Cross-session dedup mechanics.** Two parallel sessions (different
  worktrees) hitting the same notify path should not produce two toasts
  for the same underlying PR. The state file currently lives at
  `.workflow/state.json` per worktree — a global de-dup key (e.g. PR
  URL hash) needs a small SQLite or JSON store under `~/.claude/`.
- **Quiet hours.** Confirmed unnecessary — Josh's OS DND handles this.
  Document here for future contributors so it is not "fixed" again.

## Channel: Why Toast, Not Email/Slack/Webhook

Toasts are local to the developer's machine, require no infrastructure,
and clear themselves. Email piles up in inboxes; Slack/webhooks require
shared infra and create cross-machine privacy concerns. Toast is the
right primitive for "this dev's PR is ready" — for team-wide events
(release, security incident), use the existing GitHub Actions workflows
which already notify via configured channels.

## When to Add a New Notification

1. Apply the criteria from the matrix — does the event reach a stable
   end-state that needs a human?
2. Pick latency budget (most notifications should be < 5s; watchdogs
   are minutes).
3. Hook implementation goes in `.claude/hooks/<name>.py` matching the
   `notify.py` pattern (direct invocation + hook mode).
4. Update this matrix in the same PR. The matrix is the source of truth
   for what gets notified.

## References

- `.claude/hooks/notify.py` — current implementation.
- v1-launch retro item #8 — origin of this doc.
- `scripts/pipeline.py` — pipeline orchestrator that invokes notify on
  failure.
