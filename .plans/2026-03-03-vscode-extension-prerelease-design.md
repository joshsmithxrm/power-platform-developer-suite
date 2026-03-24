# VS Code Extension Pre-Release Channel Setup

**Date:** 2026-03-03
**Status:** Approved

## Context

The legacy VS Code extension (`ppds-extension-archived/`, v0.3.4) is published to the marketplace as a stable release under `JoshSmithXRM.power-platform-developer-suite`. It was a self-contained TypeScript app with webpack, direct MSAL auth, and direct Dataverse API calls.

The new extension (`extension/` on `feature/vscode-extension-mvp` branch) is a ground-up rebuild: thin UI layer delegating to `ppds serve` daemon via JSON-RPC. It has 23 commands (profiles, environments, solutions, notebooks, data explorer, query history) but is missing several legacy features (plugin traces, connection references, environment variables, metadata browser, web resources, import jobs).

We need to publish the new extension to the marketplace pre-release channel so early adopters can test it, while existing stable users remain on v0.3.4 until we achieve feature parity.

## Versioning Strategy

### Convention: Odd/Even Minor

The VS Code marketplace rejects SemVer pre-release tags (e.g., `0.4.0-alpha.1`). Only `major.minor.patch` (three integers) is accepted.

VS Code auto-updates extensions to the highest available version number. To prevent pre-release users from accidentally landing on a stable release (or vice versa), VS Code recommends the odd/even minor convention:

| Channel | Minor Version | Examples |
|---------|---------------|---------|
| **Stable** | Even | `0.4.x`, `0.6.x`, `1.0.x` |
| **Pre-release** | Odd | `0.5.x`, `0.7.x`, `1.1.x` |

Pre-release (odd) is always numerically higher than the corresponding stable (even), so users never cross channels via auto-update.

### Version Progression

```
0.3.4   (legacy stable — current marketplace)
0.5.0   (new pre-release — rebuilt extension)
0.5.1   (pre-release iteration)
0.5.x   (continued pre-release development)
  ...feature parity achieved...
0.7.0   (pre-release — published first to keep pre-release users on channel)
0.6.0   (stable — published second, stable users jump from 0.3.4 to 0.6.0)
```

The three-step dance at stable release time:
1. Publish final pre-release on current odd minor (e.g., `0.5.10`)
2. Publish new pre-release on NEXT odd minor (`0.7.0` with `--pre-release`)
3. Publish stable on next even minor (`0.6.0` without `--pre-release`)
4. Pre-release users: `0.5.10` -> `0.7.0` (stay on pre-release)
5. Stable users: `0.3.4` -> `0.6.0`

### Why Not 0.4.0?

`0.4.x` is an even minor, reserved for stable releases by convention. Starting pre-release at `0.5.0` means the eventual stable at `0.6.0` follows naturally. Starting at `0.4.0` would invert the convention permanently.

## Package.json Changes

| Field | Current | Target |
|-------|---------|--------|
| `version` | `0.4.0-alpha.1` | `0.5.0` |
| `preview` | `true` | Remove (channel is set by `--pre-release` flag at publish time, not by this field) |
| `keywords` | 5 keywords | Copy all keywords from legacy extension for marketplace discoverability |

No changes needed to: `icon`, `repository`, `bugs`, `homepage`, `license`, `engines`, `categories`, `publisher`.

## README

Replace the current developer-oriented README with a marketplace-facing README:

- What the extension does (1-2 sentences)
- Prerequisites (`ppds` CLI required)
- Feature highlights (profiles, notebooks, solutions, data explorer)
- Quick start guide
- Pre-release notice with known limitations
- Links to documentation and issue tracker

The README renders as the marketplace listing description. Developer setup instructions move to CONTRIBUTING.md or stay in the repo wiki.

## CHANGELOG

Create `CHANGELOG.md` following [Keep a Changelog](https://keepachangelog.com/) format:

```markdown
## [0.5.0] - 2026-03-XX

### Added
- Complete ground-up rebuild using JSON-RPC daemon architecture
- Profile management (create, delete, rename, select)
- Environment discovery and selection
- Solutions browser with component groups
- Dataverse notebooks (.ppdsnb) with SQL and FetchXML
- SQL and FetchXML IntelliSense
- Data Explorer webview panel
- Query history
- Results export (CSV/JSON)
- FetchXML syntax highlighting

### Changed
- Architecture: thin UI layer delegating to `ppds serve` daemon
- Build system: esbuild (was webpack)
- Testing: Vitest + Playwright (was Jest)
```

## Release Pipeline

### Current State

`.github/workflows/extension-publish.yml` already uses `--pre-release` for packaging and publishing. Triggers on `Extension-v*` tags or manual dispatch with dry-run option.

### Changes Needed

1. **Add channel input** to workflow_dispatch for choosing pre-release vs stable
2. **Add test step** (`npm test`) between lint and build
3. **Condition `--pre-release` flag** based on channel selection or tag pattern
4. **Tag convention:** `Extension-v0.5.0` for pre-release, `Extension-v0.6.0` for stable (the workflow handles the flag)

### Updated Pipeline Flow

```
Trigger (tag or manual dispatch)
  -> Checkout
  -> Setup Node.js 20
  -> npm ci
  -> npm run lint
  -> npm test          (NEW)
  -> npm run compile
  -> vsce package [--pre-release]
  -> Upload VSIX artifact
  -> vsce publish [--pre-release] (conditional)
```

## Pre-Publish Verification Checklist

Before first pre-release publish:

1. `npm run lint` passes
2. `npm test` passes (Vitest)
3. `npm run compile` produces `dist/extension.js`
4. `vsce package --pre-release` produces `.vsix` without errors
5. Install `.vsix` locally, verify:
   - Extension activates
   - Daemon starts (if `ppds` CLI is installed)
   - Profile tree view loads
   - Notebook opens and executes
6. Verify `ADO_MARKETPLACE_PAT` secret exists in GitHub repo settings with Marketplace > Manage scope, "All accessible organizations"

## Marketplace Image

The square icon (`images/icon.png`) is published automatically as part of the package. The marketplace banner/header image is managed manually through the [Visual Studio Marketplace publisher portal](https://marketplace.visualstudio.com/manage) and persists across version updates.

## Feature Parity Checklist (for graduating to 0.6.0 stable)

Legacy features not yet in the new extension:

- [ ] Plugin Trace viewer
- [ ] Connection References viewer
- [ ] Environment Variables viewer
- [ ] Metadata Browser
- [ ] Web Resources viewer
- [ ] Import Job viewer
