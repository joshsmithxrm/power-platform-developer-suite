# Release - Reference

Rationale, taxonomies, and worked details for `/release`. The procedure lives in `.claude/skills/release/SKILL.md`; the channel/version/signing details live here.

## §1 - Channel and tag layout

PPDS publishes from **git tags**, and the tag *prefix* selects the package and the workflow —
there is no single `v`-prefix-for-everything scheme.

- **Per-package tags** are the source of truth for versions. Seven NuGet packages version via
  MinVer from their own prefix (`Auth-v*`, `Cli-v*`, `Dataverse-v*`, `Mcp-v*`, `Migration-v*`,
  `Plugins-v*`, `Query-v*`); the Extension versions from `package.json` (`Extension-v*`). These
  tags trigger `publish-nuget.yml` / `release-cli.yml` / `extension-publish.yml`.
- **Unified `v<X.Y.Z>` tag** (e.g. `v1.1.0`) is pushed once per coordinated release and only
  triggers `docs-release.yml` (docs regen + ppds-docs PR). It does **not** version packages.

Channels:

- **stable**: plain semver on each prefix (`Auth-v1.1.0`, `Extension-v1.4.0`).
- **prerelease**: `-beta.N` suffix (`Mcp-v1.0.0-beta.2`). The Extension pre-release channel is
  chosen by the **odd/even minor** rule (odd minor = pre-release, even = stable), not a suffix.

PPDS.Plugins is on its own lineage (it reached `1.0.0` ahead of the unified `1.x` line and is
now `3.x`) — never regress its major to reconcile it with the other packages.

### Cli ↔ Extension co-release rule

**A `Cli-v*` release includes an `Extension-v*` bundled-CLI refresh in the same train.** The
Extension bundles the CLI at publish time (`extension-publish.yml` → `npm run bundle:cli`), so
marketplace users only pick up new CLI behavior when a *new* `Extension-v*` tag is cut. Whenever a
release train ships a `Cli-v*` bump, cut an `Extension-v*` refresh that re-bundles it — even if the
Extension's own source is unchanged (bump the Extension patch version to force a fresh vsix). Opt
out **only** with a recorded reason (e.g. the CLI change cannot affect bundled behavior), noted in
the release PR.

This is the default that Cli-v1.3.0 + Mcp-v1.1.0 missed on 2026-07-14: no Extension release rode
along, so marketplace users kept running ~1.2.0-era CLI behavior — including a bug 1.3.0 fixes. A
mechanical backstop (`post-merge-release-check.yml` → `scripts/ci/check_extension_corelease.py`)
files a `release:extension-corelease` issue whenever the latest stable `Cli-v*` tag postdates the
latest `Extension-v*` tag, but the refresh belongs in the same train — treat the issue as a missed
default, not the intended path.

## §2 - Signing matrix

Which keys sign which surfaces:

| Surface | Signing input | Workflow source |
|---------|---------------|-----------------|
| .NET assemblies (non-Plugins) | not strong-name signed at release time | n/a |
| Plugins assembly strong-name | `PLUGINS_SNK_BASE64` secret (decoded at pack time, `Plugins-v*` tags only) | `.github/workflows/publish-nuget.yml` |
| NuGet packages | `NUGET_API_KEY` secret | `.github/workflows/publish-nuget.yml` |
| VS Code extension | `ADO_MARKETPLACE_PAT` secret (vsce PAT) | `.github/workflows/extension-publish.yml` |
| GitHub releases / CLI binaries | `GITHUB_TOKEN` (auto) | `.github/workflows/release-cli.yml` |
| Windows code-signing (CLI `.exe`) | **NOT IMPLEMENTED** — `release-cli.yml` ships **unsigned** `.exe`; no `WINSIGN_*` secrets or `signtool` step exist (tracked follow-up) | n/a |

If a signing job fails, do NOT retag. Investigate the secret, fix it via repo settings, then re-run the workflow:

```bash
gh run rerun <run-id>
```

## §3 - Version-bump matrix

Rules for picking the next version:

- **patch** (X.Y.Z+1): bug fixes only. No new features. No behavior change for working code.
- **minor** (X.Y+1.0): new features. Backwards-compatible changes. Deprecations allowed.
- **major** (X+1.0.0): breaking changes. Removed features, renamed APIs, changed defaults that affect existing callers.

Heuristic: if any commit in the range is `feat:`, bump minor. If any commit is `feat!:` or `fix!:` (breaking marker), bump major. Otherwise patch.

For prereleases, increment the `-beta.N` segment without bumping the base version - until the prerelease is promoted to stable.

## §4 - CHANGELOG format

Sections (in order): Added, Changed, Fixed, Deprecated, Removed, Security. Drop empty sections from the published version - the rendered changelog should not show "(none)" entries.

Entry style:

- One bullet per logically-related change (not one per commit).
- Past tense ("Added X"), not imperative ("Add X").
- Reference the PR or issue in parens: `(#NNN)`.
- Group by surface where applicable: `**CLI:** ...`, `**Extension:** ...`.

Example:

```
## [3.4.0] - 2026-04-26

### Added
- Plugin Registration panel data refresh button (#945).
- TUI Plugin Traces filter by environment (#950).

### Fixed
- **Extension:** Webview reload no longer loses scroll state (#956).
- **CLI:** Connection pool no longer holds clients across parallel queries (#960).
```

## §5 - Platform-specific notes

### Windows code-signing (not yet implemented)

CLI `.exe` binaries (`release-cli.yml`, `win-x64`/`win-arm64`) currently ship **unsigned** — there is no `signtool` step and no `WINSIGN_CERT`/`WINSIGN_PWD` secrets. Users may see SmartScreen warnings. Wiring Authenticode signing (cert + secrets + signtool step) is a tracked follow-up; until then, do not assume a signing job exists.

### NuGet v3-flatcontainer URL casing

The NuGet v3-flatcontainer path segment MUST be lowercase regardless of how the package is cased on display:

```
https://api.nuget.org/v3-flatcontainer/ppds.auth/index.json   # CORRECT
https://api.nuget.org/v3-flatcontainer/PPDS.Auth/index.json   # WILL 404
```

This is a NuGet protocol quirk. Test post-publish probes use the lowercase form.

### Marketplace listing

After publish, manually verify the VS Code marketplace listing. Sometimes the `package.json` `displayName` regression isn't caught until users see it. Check the README rendering - the marketplace strips some markdown that GitHub renders.

### macOS notarization (future)

Not currently shipped. When/if added, notarization runs as a separate post-sign step in `release-cli.yml` (which builds the macOS binaries).

## §6 - NEVER list (with rationale)

- NEVER manually retag a published version - it confuses package indexes and breaks consumer caches. Investigate publish failure, then re-run the workflow on the existing tag.
- NEVER bump major without a CHANGELOG entry under "Removed" or a `feat!:` / `fix!:` commit explaining what broke. Major is a contract break with consumers.
- NEVER skip the CI watch step. The publish workflow can succeed for one surface (NuGet) and fail for another (marketplace) - tag bump without verification leaves the channel inconsistent.
- NEVER commit a strong-name `.snk` to the tree (snk-protect.py blocks it). The Plugins signing key lives only in the `PLUGINS_SNK_BASE64` secret, decoded to runner temp at pack time.
- NEVER tag a release from a feature branch. Tag from `main` (or a `release/X.Y` stabilization branch — see SKILL.md) so the published commit is on the mainline.
- NEVER share `NUGET_API_KEY`, `ADO_MARKETPLACE_PAT`, or `PLUGINS_SNK_BASE64` in commits, comments, or chat. They live in repo secrets only.

## §7 - Post-publish smoke checks

Beyond the v3-flatcontainer probe in SKILL.md Section 10:

- `dotnet add package PPDS.Auth --version <X.Y.Z>` in a throwaway project - verify the dependency resolves.
- `code --install-extension JoshSmithXRM.power-platform-developer-suite` (add `--pre-release` for the pre-release channel) - verify marketplace install works.
- Download CLI binary from GitHub releases page; run `./ppds --version`; confirm it matches the tag.

If any smoke check fails, file a hotfix issue and prepare a patch release. Do not attempt to alter the existing release - just publish a new one.

## §8 - GitHub Releases

Only the CLI gets an automatic GitHub Release (`release-cli.yml`, with binaries attached). Every other released surface (NuGet libraries + Extension) needs one created by hand from its CHANGELOG section.

For each released `<Prefix>-vX.Y.Z` **except `Cli`**:

1. Extract the `## [X.Y.Z]` section from `src/PPDS.<Surface>/CHANGELOG.md` (for the Extension, `src/PPDS.Extension/CHANGELOG.md`) into a notes file. The section runs from the version heading to the next `## [` heading.
2. Create the release with that file as the body:

```bash
gh release create <Prefix>-vX.Y.Z \
  --title "PPDS.<Surface> X.Y.Z" \
  --notes-file <notes.md> \
  --verify-tag --latest=false
```

`--latest=false` keeps the per-surface library releases from hijacking the "Latest" badge on a multi-package repo. Pass the notes via `--notes-file` (not inline) — CHANGELOG bodies contain `ppds ...` command examples that trip the stdout/env safety hooks when placed directly in a shell command.

Then verify every released tag has a release:

```bash
for t in <every tag you pushed>; do
  gh release view "$t" >/dev/null 2>&1 && echo "$t OK" || echo "$t MISSING"
done
```
