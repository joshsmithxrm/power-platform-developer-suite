# Backlog - Reference

Rationale, taxonomies, and worked examples for `/backlog`. The procedure lives in `.claude/skills/backlog/SKILL.md`; the "why" lives here.

Always re-read `docs/BACKLOG.md` first - it is the canonical label and milestone reference. This file documents the *interpretation* that doesn't fit there.

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
- `status:in-progress` - actively being worked.
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

Skipping verification produces stale or duplicate issues.

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

## §6 - NEVER list (with rationale)

- NEVER create an issue without `type:` + `area:` labels (orphan issues are unfindable in triage).
- NEVER skip the `cat docs/BACKLOG.md` step (label rules drift; the file is the source of truth).
