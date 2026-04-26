# Backlog - Reference

Rationale, taxonomies, and worked examples for `/backlog`. The procedure lives in `.claude/skills/backlog/SKILL.md`; the "why" lives here.

Always re-read `docs/BACKLOG.md` first - it is the canonical label and milestone reference. This file documents the *interpretation* and the dispatch heuristics that don't fit there.

## §1 - Label taxonomy

Every issue must have at minimum: one `type:` + one `area:` + either a milestone or `status:` label.

### type:

- `type:bug` - regression, defect, broken behavior. Reproduce-able by anyone.
- `type:enhancement` - new feature, capability, or expansion of existing.
- `type:docs` - documentation only. SKILL.md, README, comments.
- `type:performance` - optimization or measured slowdown. Quantified before and after.
- `type:refactor` - structural change with no observable behavior change. Pure cleanup.

### area:

- `area:auth` - authentication, identity, environment switching.
- `area:cli` - CLI commands, output formatting, argument parsing.
- `area:data` - Dataverse client, pooling, query engine.
- `area:extension` - VS Code webview panels, RPC, extension activation.
- `area:plugins` - plugin registration, traces, profiles.
- `area:tui` - terminal UI screens, widgets, status bar.

If a single issue genuinely spans two areas, apply both labels - but consider whether it should be split.

### status:

- `status:backlog` - identified, not yet milestoned. The triage queue lives here.
- `status:in-progress` - actively being worked. Pairs with an in-flight registry entry.
- `status:blocked` - external dependency or upstream change required.
- `status:needs-repro` - reported, but maintainer cannot reproduce locally.

### priority:

Optional. `priority:p0` (broken release / security), `priority:p1` (important; do this milestone), `priority:p2` (nice to have).

### epic:

Optional. Use when an issue is part of a multi-PR initiative. The epic label is the umbrella; individual issues are still classified normally.

## §2 - Mandatory verification (triage)

Before presenting ANY issue to the user during triage:

1. Verify the file paths in the issue body still exist.
2. Verify the symptom still reproduces (or note "needs repro" for issues older than the last milestone).
3. Match the area label against the actual code path. If wrong, propose the correct label.
4. Check if a duplicate exists: `gh issue list --search "in:title <keyword>"`.
5. Check the in-flight registry for a sibling working on the same area.

Skipping verification produces stale or duplicate issues - retrospective B3 / #802 documents the cost.

## §3 - Triage decision rules

For each untriaged issue, decide:

- **Promote to milestone** - clear, scoped, ready to work. Add type/area/milestone, drop `status:backlog`.
- **Defer to backlog** - real but not now. Keep `status:backlog`, add type/area.
- **Close as duplicate** - merge into the canonical issue.
- **Close as not-reproducible** - if the symptom no longer reproduces and no consumer is asking. Add `status:needs-repro` if reasonable doubt.
- **Ask** - if the issue is unclear, comment asking for repro steps; do not promote until clarified.

## §4 - Promotion rules

An issue is ready to promote when:

- AC list is concrete (numbered, checkable).
- Dependencies are merged (or the dependency is the next issue in line).
- Area label matches actual code path.
- A consumer or user is asking, or it blocks something.

Promote = move from `status:backlog` to a milestone. Drop `status:backlog`.

## §5 - Validation checklist

Periodically (or on `/backlog validate`):

- For each open issue, verify symptom still reproduces.
- Re-check area label against actual code path.
- Detect duplicates added since last validation pass.
- Close stale issues that have been in `status:backlog` for >180 days with no consumer.

## §6 - Dispatch heuristics

When and how to spawn parallel worktrees:

- Default wave size: <=3 worktrees. Ask before exceeding (the `taskcreate-cap.py` hook will block a 4th).
- Group by `area:` to maximize parallelism without merge conflicts.
- Each dispatched worktree gets a single issue (not a batch). One issue, one PR, one merge.
- Inline-prompt requirement: every dispatch carries the issue text, the AC list, and the worktree branch name in the prompt body. Spawning without an inline prompt is what forced retrospective B3.
- Register every dispatch in `.claude/state/in-flight-issues.json` via `scripts/inflight-register.py`. The auto-deregister hook (PR-4) clears the entry on PR merge.

## §7 - Inline-prompt examples

### Dispatch a single-issue worktree

```
/start <branch-name>
issue: #NNN
title: <title>
area: <area>
acs:
  - AC-NN: <text>
  - AC-NN: <text>
intent: <one-line intent>
```

The receiving Claude session reads this from the launch script, registers itself in the in-flight registry, and runs `/design -> /implement -> /pr` on the issue.

### Dispatch a parallel wave

```
/backlog dispatch
issues: #NNN, #NNN, #NNN
wave: 1 of 1
```

Backlog skill plans the wave, generates one inline prompt per issue, and spawns up to 3 worktrees. The 4th would be blocked by the taskcreate-cap hook.

## §8 - Conflict-resolution examples

### Sibling session detected on `gh issue create`

```
$ python scripts/inflight-check.py --area extension
Session abc123 (branch feat/ext-data-explorer) is actively working on
area:extension with intent "data explorer monaco editor"
```

Action: ASK the user before filing. Decisions: file anyway and link the sibling work, coordinate with the sibling and add to their PR, or drop the new issue.

### Sibling session detected on `gh issue close`

If the closer is not in the registry but a sibling is, the closer must surface the sibling branch and ask whether the close should attribute the fix to that PR (set `closed-by` reference correctly) or whether the issue is actually being addressed elsewhere.

## §9 - Meta-retro references

Backlog operations have produced these retros worth re-reading:

- B3 / #802 - duplicate issue creation due to skipped sibling check; led to T2 enforcement marker.
- B5 - dispatch wave too large (5 worktrees) led to TaskCreate cap; enforced by `taskcreate-cap.py`.
- B7 - issues closed without reading the in-flight registry; led to second sibling check on close.

## §10 - NEVER list (with rationale)

- NEVER create an issue without `type:` + `area:` labels (orphan issues are unfindable in triage).
- NEVER close an issue without checking the in-flight registry (misattributes fixes).
- NEVER spawn a 4th parallel worktree without explicit user permission (CLAUDE.md cap; enforced).
- NEVER skip the `cat docs/BACKLOG.md` step (label rules drift; the file is the source of truth).
- NEVER dispatch a worktree without an inline prompt (retrospective B3).
