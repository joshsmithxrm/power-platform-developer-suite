# Cross-Repo Conventions: PPDS to PPDS-DOCS

How to do work that spans the `power-platform-developer-suite` (PPDS) and `ppds-docs` repositories without losing skill access, fragmenting context, or creating drift between code and docs.

## Why this exists

PPDS-DOCS is intentionally lean on tooling: 2 skills versus PPDS's ~29. Most non-trivial docs work needs PPDS skills (`/audit-capture`, `/release`, `/spec`, `/verify`, etc.) or touches code/captures/CLI output that lives in PPDS. Working from the wrong cwd means duplicating effort or shipping incomplete changes.

This document codifies the convention surfaced in the v1-launch retro (items #6, #14, #15; finding D1).

## 1. Decision matrix

| Work type | Where | Why |
|-----------|-------|-----|
| Pure prose / content edits | `ppds-docs` cwd | No PPDS skills needed; faster context |
| Anything touching code, tooling, captures, or needing PPDS skills | `ppds` cwd, with `ppds-docs` as sibling checkout | Skill access; cross-repo coordination |
| Release notes / CHANGELOG mirroring | `ppds`, propagate to docs in same session | Atomic; avoids drift |
| New docs that depend on new PPDS code | `ppds` cwd, both PRs in same session | Coupling lives in one head |

If in doubt, see [section 4](#4-when-in-doubt).

## 2. Sibling checkout convention

Cross-repo work assumes `ppds-docs` is checked out as a sibling directory next to `ppds`. The canonical layout is:

```
C:\Users\josh_\source\repos\
  power-platform-developer-suite\   # this repo (PPDS)
  ppds-docs\                        # docs site
```

Or on macOS/Linux:

```
~/src/
  power-platform-developer-suite/
  ppds-docs/
```

### Discovery

PPDS skills that touch docs locate the sibling in this order:

1. `PPDS_DOCS_PATH` environment variable (absolute path)
2. Sibling directory `../ppds-docs` relative to the PPDS repo root
3. Fail with a clear error pointing to this document

Set the env var if your layout differs:

```bash
# bash / zsh
export PPDS_DOCS_PATH=/path/to/ppds-docs

# Windows (PowerShell, persistent)
[Environment]::SetEnvironmentVariable("PPDS_DOCS_PATH", "C:\path\to\ppds-docs", "User")
```

### Cloning

```bash
cd <sibling-parent>
git clone https://github.com/joshsmithxrm/power-platform-developer-suite.git
git clone https://github.com/joshsmithxrm/ppds-docs.git
```

## 3. Cross-repo PR convention

When a single logical change requires PRs in both repos:

1. **One PR per repo**, opened in the same session.
2. **Tracking issue first.** File a single issue (in either repo, but PPDS is preferred for code-coupled work). Label it `cross-repo`.
3. **Label both PRs `cross-repo:N`** where `N` matches the tracking issue number. If the label does not yet exist in a repo, note it in the PR body and create the label when convenient.
4. **Cross-link in PR descriptions:**
   - PPDS PR body: `Companion: joshsmithxrm/ppds-docs#X`
   - PPDS-DOCS PR body: `Companion: joshsmithxrm/power-platform-developer-suite#Y`
5. **Merge order: code first, docs second.** PPDS lands, then PPDS-DOCS. This avoids docs describing behavior that does not yet exist on `main`.
   - **Exception:** docs-only refactors that happen to mirror an existing PPDS doc (e.g. this file) can merge in either order. State the order in the PR body if it matters.
   - **Exception:** if a PPDS change is gated behind a release tag, docs may merge first to be ready when the tag ships. Call this out explicitly.
6. **Close the tracking issue** when the second PR merges, not the first.

### PR body template (minimal)

```markdown
## Summary
<one or two bullets>

Companion: joshsmithxrm/<other-repo>#<num>
Tracking: #<tracking-issue>

## Test plan
<...or N/A for docs-only>
```

## 4. When in doubt

**Default to PPDS cwd.**

The cost of being in the wrong repo is ~5 minutes to switch worktrees. The cost of working in PPDS-DOCS without skills is ~an hour of duplicated effort, missed conventions, or rework when you discover a skill would have done the job.

The asymmetry is real: PPDS has every tool PPDS-DOCS does, plus ~27 more. PPDS-DOCS does not gain anything from being the canonical workspace except marginally faster `npm start` for a docs-only edit.

## 5. Future: `/cross-repo-pr` skill (post-v1)

Tracked as a backlog item, not built yet. The skill would:

- Detect cross-repo work from staged changes (e.g. files in both `<ppds>/...` and `$PPDS_DOCS_PATH/...`)
- Open both PRs with cross-linked bodies
- Apply `cross-repo:N` labels (creating them in either repo if missing)
- Set merge order (code-first by default, with override flag)
- Surface the tracking issue and update it when both merge

Filed under `needs-design` until someone has time to spec it. Until then, follow [section 3](#3-cross-repo-pr-convention) by hand.

## Out of scope (decided, not done)

- **No CLAUDE.md pointer.** A pointer to this doc fails the CLAUDE.md hygiene 4-question test (situational, not globally relevant). If you find yourself needing the convention, you are already doing cross-repo work and will discover this file via `docs/`.
- **No skill duplication.** PPDS-DOCS will not be seeded with copies of PPDS skills. Working from PPDS canonical is the convention.
