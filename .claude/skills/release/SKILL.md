---
name: release
description: Walk the maintainer through a PPDS release by following docs/runbooks/release.md — scope advisory, CHANGELOGs, version bumps, tag pushes, CI monitoring and publish verification. Use when preparing a prerelease, stable or patch release of the CLI, TUI, MCP, Extension or NuGet libraries.
---

# Release

The release procedure lives in [docs/runbooks/release.md](../../../docs/runbooks/release.md).
Read it in full, then follow it step by step. The model and policy it relies on
are in [docs/RELEASE.md](../../../docs/RELEASE.md). This skill adds only what is
specific to an agent doing the work; it does not restate the procedure.

## When to Use

- "Let's cut a release", "time for a new prerelease", "ship v1.x.0"
- A patch after a `release:patch` fix has merged
- A release issue opened by the release-check workflows

Not for implementing a hot-fix (open a PR first) or documentation-only changes.

## Confirm With the User Before Running

Ask in chat and wait for an explicit yes, separately for each action. Approval
of one tag or wave does not cover the next.

- Every `git push` of a tag, one tag at a time, and the unified `v*` tag
- `gh release create`, `gh release edit` or any other GitHub release change
- `gh workflow run` for `extension-publish.yml` or `docs-release.yml`
  (including `dry_run=false`), and `gh run rerun` of a publish workflow
- Opening, merging or pushing to the release PR, and pushing to `main` or a
  `release/X.Y` branch
- Sending a contributor reply or closing a contributor's original PR
- Anything that deletes, unlists, moves or replaces a published tag or artifact

Reading state is fine without asking: `release_plan.py`, `gh run list`,
`gh release view`, `git tag -l`, the public verification probes.

## Working as an Agent

- Draft the per-package CHANGELOG entries in parallel if you can (one
  sub-agent per package, each given the package path and the commit range from
  runbook Step 3), then curate them yourself. Check every cited PR number with
  `gh pr view` before inserting it.
- For the stable-release security review gate, run `/security-review` on the
  diff since the last stable tag and save the result as the
  `docs/qa/security-review-*.md` artifact the runbook requires.
- Report each wave's public verification result before asking to push the next
  wave. Never batch tag pushes to save time.
- If a step in the runbook is wrong or missing, propose the correction to
  docs/runbooks/release.md at the end of the release.
