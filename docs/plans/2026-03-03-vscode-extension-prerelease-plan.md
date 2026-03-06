# VS Code Extension Pre-Release Channel Setup — Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Prepare the rebuilt VS Code extension for marketplace pre-release publishing with proper versioning, marketplace-facing README, CHANGELOG, and updated release pipeline.

**Architecture:** All changes are in the `feature/vscode-extension-mvp` worktree at `.worktrees/vscode-extension-mvp/`. The extension lives under `extension/` within that worktree. The release workflow lives at `.github/workflows/extension-publish.yml` on main (shared across branches).

**Tech Stack:** TypeScript, VS Code Extension API, esbuild, vsce, GitHub Actions

**Design doc:** `docs/plans/2026-03-03-vscode-extension-prerelease-design.md`

---

### Task 1: Update package.json — Version and Preview

**Files:**
- Modify: `.worktrees/vscode-extension-mvp/extension/package.json:5-7`

**Step 1: Update version and remove preview field**

Change lines 5-7 from:

```json
  "version": "0.4.0-alpha.1",
  "publisher": "JoshSmithXRM",
  "preview": true,
```

To:

```json
  "version": "0.5.0",
  "publisher": "JoshSmithXRM",
```

Remove the `"preview": true` line entirely. The pre-release channel is controlled by the `--pre-release` flag at publish time, not by this field. The `preview` field shows a separate "Preview" badge which is a different concept.

**Step 2: Verify JSON is valid**

Run: `cd .worktrees/vscode-extension-mvp/extension && node -e "JSON.parse(require('fs').readFileSync('package.json', 'utf8')); console.log('Valid JSON')"`
Expected: `Valid JSON`

**Step 3: Commit**

```bash
cd .worktrees/vscode-extension-mvp
git add extension/package.json
git commit -m "chore: set extension version to 0.5.0 for pre-release channel

Remove preview field (handled by --pre-release flag at publish time).
Update version from 0.4.0-alpha.1 to 0.5.0 following odd/even minor
convention (odd = pre-release, even = stable)."
```

---

### Task 2: Update package.json — Copy Legacy Keywords

**Files:**
- Modify: `.worktrees/vscode-extension-mvp/extension/package.json:15-21`

**Step 1: Replace keywords array**

Replace the current 5 keywords:

```json
  "keywords": [
    "Power Platform",
    "Dataverse",
    "Dynamics 365",
    "CRM",
    "Microsoft"
  ],
```

With the full legacy keyword list:

```json
  "keywords": [
    "power platform",
    "power-platform",
    "dynamics 365",
    "d365",
    "dynamics365",
    "dataverse",
    "powerapps",
    "power apps",
    "power automate",
    "powerautomate",
    "crm",
    "dynamics crm",
    "common data service",
    "cds",
    "microsoft",
    "development",
    "admin",
    "administration",
    "devtools",
    "developer tools",
    "plugin",
    "plugins",
    "solution",
    "solutions",
    "metadata",
    "import",
    "export",
    "environment",
    "environments",
    "connection references",
    "environment variables",
    "trace",
    "debugging",
    "toolkit",
    "suite"
  ],
```

**Step 2: Verify JSON is valid**

Run: `cd .worktrees/vscode-extension-mvp/extension && node -e "JSON.parse(require('fs').readFileSync('package.json', 'utf8')); console.log('Valid JSON')"`
Expected: `Valid JSON`

**Step 3: Commit**

```bash
cd .worktrees/vscode-extension-mvp
git add extension/package.json
git commit -m "chore: restore full keyword list from legacy extension

Copy all 35 marketplace keywords from the archived extension for
search discoverability."
```

---

### Task 3: Update package.json — Enhance Description

**Files:**
- Modify: `.worktrees/vscode-extension-mvp/extension/package.json:4`

**Step 1: Update description to match legacy quality**

Replace:

```json
  "description": "Comprehensive development tools for Microsoft Dataverse and Power Platform",
```

With:

```json
  "description": "A comprehensive VS Code extension for Power Platform development and administration - your complete toolkit for Dynamics 365, Dataverse, and Power Platform solutions",
```

This matches the legacy description which was more search-friendly.

**Step 2: Verify JSON is valid**

Run: `cd .worktrees/vscode-extension-mvp/extension && node -e "JSON.parse(require('fs').readFileSync('package.json', 'utf8')); console.log('Valid JSON')"`
Expected: `Valid JSON`

**Step 3: Commit**

```bash
cd .worktrees/vscode-extension-mvp
git add extension/package.json
git commit -m "chore: use legacy extension description for marketplace SEO"
```

---

### Task 4: Write Marketplace-Facing README

**Files:**
- Rewrite: `.worktrees/vscode-extension-mvp/extension/README.md`

**Step 1: Write the marketplace README**

Replace the entire contents of `extension/README.md` with:

```markdown
# Power Platform Developer Suite

A comprehensive VS Code extension for Power Platform development and administration — your complete toolkit for Dynamics 365, Dataverse, and Power Platform solutions.

> **Pre-Release:** This is a pre-release version of the rebuilt extension. The stable version (0.3.x) remains available for users who prefer a production-ready experience. Switch between channels in the VS Code Extensions view.

## Prerequisites

- [PPDS CLI](https://github.com/joshsmithxrm/power-platform-developer-suite) installed and on your PATH
- At least one authentication profile configured (`ppds auth create`)

## Features

### Profile Management

Create, select, rename, and delete authentication profiles directly from the VS Code sidebar. View profile details including identity, authentication method, cloud, and connected environment.

### Environment Discovery

Browse and select Dataverse environments associated with your profile. The active environment is shown in the VS Code status bar.

### Solutions Browser

Explore solutions in your connected environment with expandable component groups. Toggle visibility of managed solutions.

### Dataverse Notebooks (.ppdsnb)

Query Dataverse using an SSMS-like notebook experience:

- **SQL and FetchXML** — write queries in either language, toggle per cell
- **IntelliSense** — autocomplete for tables, columns, and FetchXML elements
- **Results** — inline display with virtual scrolling for large datasets
- **Export** — save results as CSV or JSON
- **History** — query history is saved automatically

### Data Explorer

Quick ad-hoc query panel for one-off Dataverse queries without creating a notebook.

## Quick Start

1. Install the extension (pre-release channel)
2. Open the PPDS sidebar (activity bar icon)
3. Create or select an authentication profile
4. Select an environment
5. Create a new notebook (`Ctrl+Shift+P` → "PPDS: New Notebook")
6. Write a SQL query and execute the cell

## Settings

| Setting | Default | Description |
|---------|---------|-------------|
| `ppds.queryDefaultTop` | 100 | Default TOP value for SQL queries (1-5000) |
| `ppds.autoStartDaemon` | true | Auto-start the ppds daemon on activation |
| `ppds.showEnvironmentInStatusBar` | true | Show active environment in status bar |

## Known Limitations (Pre-Release)

The following features from the stable version are not yet available in this rebuild:

- Plugin Trace viewer
- Connection References viewer
- Environment Variables viewer
- Metadata Browser
- Web Resources viewer
- Import Job viewer

These will be added in future pre-release updates before the next stable release.

## Feedback

- [Report issues](https://github.com/joshsmithxrm/power-platform-developer-suite/issues)
- [Documentation](https://github.com/joshsmithxrm/power-platform-developer-suite)

## License

[MIT](LICENSE)
```

**Step 2: Verify the README renders correctly**

Visually review: the README should have no broken markdown, all links should be valid relative paths or full URLs.

**Step 3: Commit**

```bash
cd .worktrees/vscode-extension-mvp
git add extension/README.md
git commit -m "docs: write marketplace-facing README for pre-release

Replace developer-oriented README with user-facing content that
renders as the marketplace listing description. Includes features,
prerequisites, quick start, settings, and known limitations."
```

---

### Task 5: Create CHANGELOG.md

**Files:**
- Create: `.worktrees/vscode-extension-mvp/extension/CHANGELOG.md`

**Step 1: Write the changelog**

Create `extension/CHANGELOG.md` with:

```markdown
# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [0.5.0] - 2026-03-03

Complete ground-up rebuild of the extension. The new architecture uses a thin VS Code UI layer that delegates all operations to the `ppds serve` daemon via JSON-RPC, replacing the previous self-contained approach.

### Added

- Profile management — create, delete, rename, select profiles from the sidebar
- Environment discovery — browse and select Dataverse environments
- Solutions browser — explore solutions with expandable component groups
- Dataverse notebooks (.ppdsnb) — SSMS-like query experience with SQL and FetchXML
- SQL IntelliSense — autocomplete for tables and columns
- FetchXML IntelliSense — autocomplete for elements and attributes
- FetchXML syntax highlighting
- Data Explorer — webview panel for quick ad-hoc queries
- Query history — automatic persistence of executed queries
- Results export — save query results as CSV or JSON
- Environment status bar indicator
- Virtual scrolling for large result sets

### Changed

- Architecture: thin UI layer delegating to `ppds serve` daemon via JSON-RPC (was self-contained with direct Dataverse API calls)
- Build system: esbuild (was webpack)
- Test framework: Vitest + Playwright (was Jest)
- Authentication: managed by CLI profiles (was MSAL in-extension)

### Removed

- Direct Dataverse API calls (now handled by daemon)
- In-extension MSAL authentication (now managed by CLI)
- Plugin Trace viewer (will be re-added in a future release)
- Connection References viewer (will be re-added in a future release)
- Environment Variables viewer (will be re-added in a future release)
- Metadata Browser (will be re-added in a future release)
- Web Resources viewer (will be re-added in a future release)
- Import Job viewer (will be re-added in a future release)

## [0.3.4] - 2026-01-01

_Last stable release of the legacy architecture. See [archived repository](https://github.com/joshsmithxrm/power-platform-developer-suite/tree/archived) for full history._
```

**Step 2: Verify .vscodeignore does NOT exclude CHANGELOG.md**

Check `.vscodeignore` — it excludes `docs/**` but does not exclude `CHANGELOG.md`. The comment on line 93 says "keep README, LICENSE, CHANGELOG". Confirmed: CHANGELOG.md will be included in the .vsix package.

**Step 3: Commit**

```bash
cd .worktrees/vscode-extension-mvp
git add extension/CHANGELOG.md
git commit -m "docs: create CHANGELOG.md for 0.5.0 pre-release

Document the ground-up rebuild, new features, architecture changes,
and features removed pending re-implementation."
```

---

### Task 6: Update Release Pipeline

**Files:**
- Modify: `.github/workflows/extension-publish.yml`

**Step 1: Update the workflow**

Replace the entire file with:

```yaml
name: Publish Extension

on:
  release:
    types: [published]
  workflow_dispatch:
    inputs:
      dry_run:
        description: 'Dry run (package only, no publish)'
        required: false
        default: true
        type: boolean
      channel:
        description: 'Release channel'
        required: true
        default: 'pre-release'
        type: choice
        options:
          - pre-release
          - stable

jobs:
  publish:
    runs-on: ubuntu-latest
    # Only run on Extension-v* tags or manual dispatch
    if: |
      github.event_name == 'workflow_dispatch' ||
      startsWith(github.ref, 'refs/tags/Extension-v')

    steps:
      - name: Checkout code
        uses: actions/checkout@v6

      - name: Setup Node.js
        uses: actions/setup-node@v6
        with:
          node-version: '20'
          cache: 'npm'
          cache-dependency-path: extension/package-lock.json

      - name: Install dependencies
        working-directory: extension
        run: npm ci

      - name: Lint
        working-directory: extension
        run: npm run lint

      - name: Test
        working-directory: extension
        run: npm test

      - name: Build
        working-directory: extension
        run: npm run compile

      - name: Determine release channel
        id: channel
        run: |
          if [ "${{ github.event_name }}" = "workflow_dispatch" ]; then
            echo "is_prerelease=${{ inputs.channel == 'pre-release' }}" >> $GITHUB_OUTPUT
          else
            # For tag-triggered releases, check the GitHub release prerelease flag
            echo "is_prerelease=${{ github.event.release.prerelease }}" >> $GITHUB_OUTPUT
          fi

      - name: Package extension (pre-release)
        if: steps.channel.outputs.is_prerelease == 'true'
        working-directory: extension
        run: npx vsce package --pre-release

      - name: Package extension (stable)
        if: steps.channel.outputs.is_prerelease != 'true'
        working-directory: extension
        run: npx vsce package

      - name: Upload VSIX artifact
        uses: actions/upload-artifact@v7
        with:
          name: extension-vsix
          path: extension/*.vsix
          retention-days: 30

      - name: Publish to VS Code Marketplace (pre-release)
        if: |
          steps.channel.outputs.is_prerelease == 'true' &&
          (github.event_name == 'release' || (github.event_name == 'workflow_dispatch' && !inputs.dry_run))
        working-directory: extension
        run: npx vsce publish --pre-release -p ${{ secrets.ADO_MARKETPLACE_PAT }}
        env:
          VSCE_PAT: ${{ secrets.ADO_MARKETPLACE_PAT }}

      - name: Publish to VS Code Marketplace (stable)
        if: |
          steps.channel.outputs.is_prerelease != 'true' &&
          (github.event_name == 'release' || (github.event_name == 'workflow_dispatch' && !inputs.dry_run))
        working-directory: extension
        run: npx vsce publish -p ${{ secrets.ADO_MARKETPLACE_PAT }}
        env:
          VSCE_PAT: ${{ secrets.ADO_MARKETPLACE_PAT }}
```

**Step 2: Validate YAML syntax**

Run: `cd /c/VS/ppdsw/ppds && python -c "import yaml; yaml.safe_load(open('.github/workflows/extension-publish.yml')); print('Valid YAML')"` or if python/pyyaml not available: `npx yaml-lint .github/workflows/extension-publish.yml`

**Step 3: Commit**

```bash
cd /c/VS/ppdsw/ppds
git add .github/workflows/extension-publish.yml
git commit -m "ci: add channel selection and test step to extension publish workflow

- Add 'channel' input (pre-release/stable) for manual dispatch
- Add npm test step between lint and build
- Condition --pre-release flag on channel selection or GitHub release flag
- Tag-triggered releases use the GitHub release prerelease flag"
```

Note: This file lives on main, not in the worktree. It's a shared workflow.

---

### Task 7: Clean up .vscodeignore

**Files:**
- Modify: `.worktrees/vscode-extension-mvp/extension/.vscodeignore`

**Step 1: Fix stale references**

The .vscodeignore references files from the legacy build setup that no longer exist. Replace these lines:

```
webpack.config.js
webpack.webview.config.js
jest.config.js
```

With:

```
esbuild.js
vitest.config.*
playwright.config.*
```

Also update the comment on line 39 from "Node Modules (bundled by webpack)" to "Node Modules (bundled by esbuild)".

And update the comment on line 44 from "Unbundled Output (we use dist/ from webpack)" to "Unbundled Output (we use dist/ from esbuild)".

**Step 2: Commit**

```bash
cd .worktrees/vscode-extension-mvp
git add extension/.vscodeignore
git commit -m "chore: update .vscodeignore for esbuild build system

Replace webpack/jest references with esbuild/vitest/playwright.
Update comments to reflect current build toolchain."
```

---

### Task 8: Pre-Publish Verification

This task is manual — the implementer runs these checks and reports results.

**Step 1: Run lint**

Run: `cd .worktrees/vscode-extension-mvp/extension && npm run lint`
Expected: No errors

**Step 2: Run tests**

Run: `cd .worktrees/vscode-extension-mvp/extension && npm test`
Expected: All tests pass

**Step 3: Build**

Run: `cd .worktrees/vscode-extension-mvp/extension && npm run compile`
Expected: `dist/extension.js` is produced

**Step 4: Package as pre-release**

Run: `cd .worktrees/vscode-extension-mvp/extension && npx vsce package --pre-release`
Expected: Produces `power-platform-developer-suite-0.5.0.vsix` without errors

**Step 5: Inspect package contents**

Run: `cd .worktrees/vscode-extension-mvp/extension && npx vsce ls --pre-release`
Expected output should include:
- `extension/dist/extension.js`
- `extension/images/icon.png`
- `extension/images/activity-bar-icon.svg`
- `extension/README.md`
- `extension/CHANGELOG.md`
- `extension/LICENSE` (or root LICENSE)
- `extension/package.json`

Should NOT include:
- `src/**` (TypeScript source)
- `node_modules/**`
- `**/*.map` (source maps)
- `**/*.ts` files

**Step 6: Report results**

Document: lint status, test count/results, build output, .vsix file size, package contents check. The user will then do manual testing (install .vsix, verify activation, test features) before triggering the publish workflow.
