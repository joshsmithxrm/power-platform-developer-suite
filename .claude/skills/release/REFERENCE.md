# Release - Reference

Rationale, taxonomies, and worked details for `/release`. The procedure lives in `.claude/skills/release/SKILL.md`; the channel/version/signing details live here.

## §1 - Channel layout

PPDS ships three channels:

- **stable** (`vX.Y.Z`): production-ready. Tagged from `main`. NuGet, marketplace, GitHub releases.
- **prerelease** (`vX.Y.Z-rc.N`): release-candidate. Tagged from `main` after stabilization. Marketplace flagged as pre-release; NuGet pushes to the `-rc` channel.
- **preview** (`vX.Y.Z-preview.N`): early access. Tagged from a feature branch. NuGet preview channel. Not surfaced to default users.

Tag prefix is always `v` (semver). The publish workflow keys off the tag prefix to decide channel.

## §2 - Signing matrix

Which keys sign which surfaces:

| Surface | Signing input | Workflow source |
|---------|---------------|-----------------|
| .NET assemblies (.dll) | strong-name SNK in `pks/PPDS.snk` (committed; protected by `snk-protect.py`) | Built into csproj |
| NuGet packages | NuGet API key from secret `NUGET_API_KEY` | `.github/workflows/publish.yml` |
| VS Code extension | `vsce` token from secret `VSCE_TOKEN` | `.github/workflows/publish.yml` |
| GitHub releases | GITHUB_TOKEN (auto) | `.github/workflows/publish.yml` |
| Code-signing (Windows .exe) | Cert + password from secrets `WINSIGN_CERT`, `WINSIGN_PWD` | `.github/workflows/publish.yml` |

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

For prereleases, increment the `-rc.N` or `-preview.N` segment without bumping the base version - until the prerelease is promoted to stable.

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

### Windows code-signing

The cert lives in repo secret `WINSIGN_CERT` (base64-encoded PFX). The password is `WINSIGN_PWD`. If signing fails with "cert not found", verify the secret is base64 and decodable. The publish workflow does the decode.

If the cert expires, request a new one from the cert authority and update both secrets. Test with a manual workflow_dispatch before tagging.

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

Not currently shipped. When/if added, notarization runs as a separate post-sign step in publish.yml.

## §6 - NEVER list (with rationale)

- NEVER manually retag a published version - it confuses package indexes and breaks consumer caches. Investigate publish failure, then re-run the workflow on the existing tag.
- NEVER bump major without a CHANGELOG entry under "Removed" or a `feat!:` / `fix!:` commit explaining what broke. Major is a contract break with consumers.
- NEVER skip the CI watch step. The publish workflow can succeed for one surface (NuGet) and fail for another (marketplace) - tag bump without verification leaves the channel inconsistent.
- NEVER edit `pks/PPDS.snk` (snk-protect.py blocks it). Strong-name keys are immutable across the project lifetime.
- NEVER cherry-pick a release commit onto a feature branch. Releases are tagged from main only - the publish workflow checks for it.
- NEVER share `NUGET_API_KEY` or `VSCE_TOKEN` in commits, comments, or chat. They live in repo secrets only.

## §7 - Post-publish smoke checks

Beyond the v3-flatcontainer probe in SKILL.md Step 7:

- `dotnet add package PPDS.Auth --version <X.Y.Z>` in a throwaway project - verify the dependency resolves.
- `code --install-extension joshsmithxrm.ppds@<X.Y.Z>` - verify marketplace install works.
- Download CLI binary from GitHub releases page; run `./ppds --version`; confirm it matches the tag.

If any smoke check fails, file a hotfix issue and prepare a patch release. Do not attempt to alter the existing release - just publish a new one.
