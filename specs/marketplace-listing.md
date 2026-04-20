# Marketplace Listing

**Status:** Draft
**Last Updated:** 2026-04-20
**Code:**
- Extension README (Marketplace listing body): [`src/PPDS.Extension/README.md`](../src/PPDS.Extension/README.md)
- Extension marketplace manifest fields: [`src/PPDS.Extension/package.json`](../src/PPDS.Extension/package.json)
- Extension release notes + review CTA: [`src/PPDS.Extension/CHANGELOG.md`](../src/PPDS.Extension/CHANGELOG.md)
- Marketplace image assets: [`src/PPDS.Extension/media/`](../src/PPDS.Extension/media/)
- Repository README (GitHub landing page): [`README.md`](../README.md)
- Publish workflow (image-URL pinning): [`.github/workflows/extension-publish.yml`](../.github/workflows/extension-publish.yml)
**Surfaces:** Extension

---

## Overview

The marketplace listing is the public-facing narrative for PPDS on the VS Code Marketplace and on GitHub. It governs two documents — the extension README (rendered at `marketplace.visualstudio.com/items?itemName=JoshSmithXRM.power-platform-developer-suite`) and the repository README (rendered at `github.com/joshsmithxrm/power-platform-developer-suite`) — plus the extension's `package.json` marketplace metadata, the v1.0 `CHANGELOG.md` release-notes conventions, and the publish-time image-pinning mechanics. This spec is a living document: every PPDS release that changes user-visible capabilities updates the listing under these rules.

### Goals

- **Accurate narrative:** listing content matches shipped capabilities — no under-selling, no overselling, no stale claims.
- **Discoverability:** categories, keywords, and title words match the searches a target user runs.
- **Trust signals:** badges, banner, images, and CHANGELOG CTA compound into a credible first impression for cold visitors.
- **Low drift:** content rules are mechanical enough that CI can enforce them, so each release updates the listing without manual audit.
- **Dual-audience split:** extension README answers "what does installing this give me?"; repo README answers "what is this platform?" — each document serves its readers without duplicating the other.

### Non-Goals

- Rebranding the project. "Power Platform Developer Suite" / PPDS is the retained brand (trademark risk accepted).
- Logo or icon design (tracked in #302; deferred to v1.1).
- Demo GIF production (tracked in #832; deferred to v1.1).
- Publisher verification flow itself (tracked in #786; runs in parallel, not blocking).
- Designing the `audit-capture` pipeline (separate spec: [audit-capture.md](./audit-capture.md)).
- The Marketplace publish mechanics beyond image-URL pinning (owned by `.github/workflows/extension-publish.yml`).

---

## Architecture

```
 ┌─────────────────────┐     ┌────────────────────────┐     ┌─────────────────────┐
 │  audit-capture run  │────▶│  ppds-v1-audit repo   │────▶│ src/PPDS.Extension/ │
 │  extension          │     │  (or local fallback)   │     │   media/*.png       │
 └─────────────────────┘     └────────────────────────┘     └──────────┬──────────┘
                                                                        │ referenced via
                                                                        │ relative paths
                                                                        ▼
 ┌─────────────────────┐                                 ┌──────────────────────────┐
 │  src/PPDS.Extension/│                                 │  src/PPDS.Extension/     │
 │    package.json     │──┐                              │    README.md             │
 │  (categories,       │  │                              │  (extension-centric,     │
 │   keywords, banner, │  │                              │   hybrid pattern)        │
 │   preview, qna,     │  │                              └──────────┬───────────────┘
 │   description)      │  │                                         │
 └─────────────────────┘  │                                         │
                          │      ┌─────────────────────────────┐    │
                          └─────▶│ .github/workflows/          │◀───┘
                                 │   extension-publish.yml     │
                                 │ (vsce publish               │
                                 │   --target <matrix.target>  │
                                 │   --baseImagesUrl <sha>/... │
                                 │   --pre-release? )          │
                                 └──────────────┬──────────────┘
                                                │
                                                ▼
                                 ┌─────────────────────────────┐
                                 │ marketplace.visualstudio.com│
                                 │ items?itemName=             │
                                 │   JoshSmithXRM.power-       │
                                 │   platform-developer-suite  │
                                 └─────────────────────────────┘

 ┌─────────────────────┐
 │  README.md (repo)   │── rendered on github.com (platform-centric)
 └─────────────────────┘
```

### Components

| Component | Responsibility | v1.0.0 implementation state |
|-----------|----------------|-----------------------------|
| `src/PPDS.Extension/README.md` | Marketplace listing body. Extension-centric hero, Features, CLI section, AI section, platform signpost. | Exists; must be rewritten to conform to Specification. |
| `src/PPDS.Extension/package.json` marketplace fields | `displayName`, `description`, `categories`, `keywords`, `galleryBanner`, `preview`, `qna`, `icon`, `repository`, `bugs`, `homepage`. | Partial: `displayName`, `description`, `categories` (non-conformant), `keywords` (non-conformant), `icon`, `repository`, `bugs`, `homepage` present. **`galleryBanner`, `preview`, `qna` must be added.** |
| `src/PPDS.Extension/CHANGELOG.md` | Release notes. Each release's version block may carry a "please rate" CTA. | Exists; must be updated to add CTA line. |
| `src/PPDS.Extension/media/*.png` | Images referenced by README. Produced by `audit-capture`. | **Directory does not exist; must be created and populated.** |
| `README.md` (repo root) | GitHub landing page. Platform-centric pitch; separate audience from extension README. | Exists at 370 lines; polish pass per Surface-Specific Behavior. |
| `.github/workflows/extension-publish.yml` | Packages and publishes the 4 platform-specific VSIX files. **This spec scopes the addition of `--baseImagesUrl` + SHA pinning to the publish steps** (lines 114, 123 of the workflow). | Exists; `--baseImagesUrl` flag is **not** present today and must be added as part of this spec's implementation. |

### Dependencies

- Depends on: [audit-capture.md](./audit-capture.md) — produces the PNGs consumed by `media/`. In particular, this spec relies on `audit-capture`'s `AUDIT_REDACT=true` default to mask env name and user principal; if that default changes in `audit-capture.md`, this spec must be re-validated against the new behavior.
- Depends on: [authentication.md](./authentication.md) — source of truth for the 9 auth methods referenced in listing copy. If new auth methods are added there, this spec's copy must be updated.
- Depends on: [publish.md](./publish.md) — `ppds publish` CLI command (mentioned in extension README CLI section). Name collision with "Marketplace publish" is handled in Surface-Specific Behavior copy.
- Uses patterns from: [architecture.md](./architecture.md) — the platform framing in both READMEs aligns with the architecture spec.

---

## Specification

### Core Requirements

1. The extension README MUST open with a hero sentence naming (a) the platform, (b) the extension's role as one surface, (c) the bundled `ppds` CLI daemon, (d) the MCP/AI integration.
2. The extension README MUST declare the extension as self-contained — no separately installed prerequisite tooling — under a section that appears before Features and after the hero image.
3. The extension README MUST NOT contain the string "PPDS CLI installed and on your PATH" or any equivalent phrasing that implies a separate CLI install is required by the end user.
4. The extension README MUST list all nine v1.0 panels in a single uniform grid: Data Explorer, Solutions, Plugin Traces, Metadata Browser, Connection References, Environment Variables, Web Resources, Import Jobs, Plugin Registration.
5. The extension README MUST include a dedicated "The `ppds` CLI" section explaining the extension delegates to the CLI daemon.
6. The extension README MUST include a dedicated AI section covering the MCP server and CLI scriptability.
7. The extension README MUST include a "Part of PPDS" signpost linking out to the CLI, MCP, and library install targets — without inlining their install commands.
8. The extension README target length is 900–1200 words (verified at publish time).
9. All image references in the extension README MUST use relative paths (`media/<file>.png`). No absolute URLs.
10. The extension README MUST NOT reference SVG image files in its body (Marketplace rejects SVG for security, except from whitelisted badge providers).
11. `package.json` `categories` MUST equal `["AI", "Notebooks", "Data Science", "Other"]`. Values outside the VS Code Marketplace enum are forbidden.
12. `package.json` `keywords` MUST be 10–14 entries, MUST include `dataverse`, `dynamics-365`, `power-platform`, `pac`, `mcp`, and MUST NOT include redundant variants (`d365` + `dynamics 365`, `powerapps` + `power apps`, etc.).
13. `package.json` MUST include `galleryBanner: { "color": "#25c2a0", "theme": "dark" }`.
14. `package.json` MUST set `"preview": false` explicitly.
15. `package.json` MUST set `"qna": false` (routes Marketplace Q&A to the GitHub Issues tracker via the existing `bugs.url`).
16. `package.json` `description` MUST align with the extension README hero: mentions the platform, bundled CLI, and AI/MCP integration. MUST NOT lead with generic phrasing like "comprehensive toolkit."
17. `package.json` `displayName` MUST remain "Power Platform Developer Suite" (brand retained per investigation).
18. The `src/PPDS.Extension/media/` directory MUST contain the three release-gated PNGs: `notebook-hero.png`, `metadata-browser.png`, `plugin-traces.png`. PNG format only; dimensions uniform across the three.
19. Images MUST be produced by `audit-capture run extension` (from the shared `ppds-v1-audit` repository output, or a local-fallback run against a dev profile with the same tool). Redaction of env name and user principal is delegated to `audit-capture` and guaranteed by its `AUDIT_REDACT=true` default; this spec does not restate that guarantee. If `audit-capture.md` changes the default, this spec's implementation must re-audit its images.
20. The `CHANGELOG.md` version block for any stable release MAY include a single "please rate" CTA line as the first line of the version block (before any prose). For v1.0.0 specifically, this CTA MUST be present.
21. The Marketplace publish step MUST invoke `vsce publish --target <matrix.target> --baseImagesUrl https://raw.githubusercontent.com/joshsmithxrm/power-platform-developer-suite/<release-tag-commit-sha>/src/PPDS.Extension/media/`, pinning images to the release commit. The tag MUST exist before the publish step runs; the SHA in `--baseImagesUrl` MUST match the tag commit. **This requires modifying `.github/workflows/extension-publish.yml` lines 114 and 123 as part of this spec's implementation.**
22. The repo-root `README.md` MUST lead with a platform-centric hero (not extension-centric), MUST include a Marketplace-install link near the top, and MUST NOT duplicate extension-specific feature documentation.
23. Marketing claims in both READMEs MUST align with verified capabilities. Specifically: the auth-methods claim reads "covers every `pac auth create` method, plus stateless env-var auth for CI/CD"; ALM framing reads "provides the pipeline building blocks — stateless env-var authentication, PAC-compatible deployment settings, multi-profile bulk data — complements `pac solution` for packaging, does not replace it." Phrases "PAC parity", "full ALM replacement", and "superset of PAC" are forbidden.
24. The publish workflow's target matrix MUST equal exactly the four platform-specific values `win32-x64`, `linux-x64`, `darwin-x64`, `darwin-arm64`. Adding a generic target would invalidate Core Requirement 2 (self-contained install) because generic-target VSIX files do not bundle the CLI.

### Primary Flows

**Release-time listing update:**

1. **Feature PR lands on `main`:** implementer updates `src/PPDS.Extension/README.md` and `CHANGELOG.md` as part of the PR if the change alters user-visible capabilities. Reviewer verifies ACs affected by the change are still green.
2. **Release PR opens:** release skill runs the listing-verification test (see Acceptance Criteria), fixing any AC drift before merge.
3. **Tag + publish:** release skill creates the `Extension-v<X.Y.Z>` annotated tag on the release commit (prefix required by `.github/workflows/extension-publish.yml:28` trigger filter). CI workflow resolves the tag SHA and invokes `vsce publish --target ... --baseImagesUrl .../<sha>/...`. Publish fails if the SHA does not match the tag commit. `workflow_dispatch` invocations MUST be blocked from running the publish steps when `github.ref` is not a tag — the SHA resolution MUST error out rather than silently pinning to a branch HEAD.
4. **Post-publish verification:** a smoke step opens the live Marketplace listing, confirms all images load and the rendered content matches the committed README (modulo Markdown rendering differences).

**Image refresh flow:**

1. **Shakedown or audit captures** `ppds-v1-audit` with fresh extension PNGs.
2. **Curator** selects the three release images (or runs `audit-capture` locally against a dev profile if the shared repo isn't populated), crops/renames to the canonical filenames, commits under `src/PPDS.Extension/media/`.
3. **README** already references `media/<file>.png` via relative paths; no README edit needed unless caption wording changes.

**Listing-drift detection:**

Each release, the release skill grep-checks the extension README against the spec's negative-constraint list (e.g., forbidden phrases from AC-23) and the structural ACs (e.g., nine-panel grid). Drift surfaces as gate findings, not silent drift.

### Surface-Specific Behavior

#### Extension Surface

**Extension README (`src/PPDS.Extension/README.md`) section order:**

```
# Power Platform Developer Suite

[badges: version · installs · license · build]

<hero sentence — Option 2>
![Notebook hero](media/notebook-hero.png)

## Zero-config install
Short paragraph: extension is self-contained; ppds CLI bundled per platform
in the VSIX; no separate install, no .NET runtime required. Only prereq is
a Dataverse authentication profile (created on first run).

## Features

### SQL and FetchXML Notebooks
.ppdsnb notebook files. SQL or FetchXML per cell. IntelliSense, syntax
highlighting, virtual-scrolled results, CSV/JSON export per cell, query
history.

### Metadata Browser
![Metadata Browser](media/metadata-browser.png)
Five-tab entity explorer with global option-set aggregation.

### Plugin Traces
![Plugin Traces](media/plugin-traces.png)
Timeline waterfall with trace-level management, filters, age cleanup.

### All nine panels
Uniform text-only grid (emoji glyphs, not codicons):
  - Data Explorer
  - Solutions
  - Plugin Registration
  - Connection References
  - Environment Variables
  - Web Resources
  - Import Jobs
  - Metadata Browser (above)
  - Plugin Traces (above)

### Profile and environment management
Sidebar tree, status-bar indicator, color theming.

## The ppds CLI daemon
Short paragraph (4–6 lines). Extension is a thin UI over ppds serve; every
capability is also available headlessly. Link to CLI docs.

## AI-ready: MCP + scriptable CLI
Short paragraph (3–4 lines). PPDS MCP server exposes 20+ Dataverse tools to
Claude/Copilot/agents. CLI is stdout/stderr-separated and JSON-capable for
AI coding tools. Link to MCP install.

## Quick Start
1. Install the extension.
2. Open the PPDS sidebar.
3. Create an authentication profile.
4. Select an environment.
5. Create a notebook and run a query.

## Settings
2–3 rows: ppds.queryDefaultTop, ppds.autoStartDaemon, ppds.showEnvironmentInStatusBar.

## Part of PPDS — a developer platform
Bulleted signpost:
  - ppds CLI — headless automation, CI/CD
  - ppds-mcp-server — 20+ Dataverse tools for AI assistants
  - PPDS.* NuGet libraries — embed in .NET apps
  - ppds-docs — platform documentation

## Support
Report issues · Docs · Known limitations (CHANGELOG)

## License
MIT
```

**Hero sentence (frozen verbatim):**

> **Power Platform Developer Suite (PPDS)** is a developer platform for Microsoft Power Platform and Dataverse. This extension puts SQL/FetchXML notebooks, plugin registration, metadata browsing, and 7 other panels into VS Code — self-contained, with the `ppds` CLI daemon bundled — and an MCP server that makes your environments queryable by AI agents.

**Badges (4 only):**

- Marketplace version: `https://img.shields.io/visual-studio-marketplace/v/JoshSmithXRM.power-platform-developer-suite`
- Installs: `https://img.shields.io/visual-studio-marketplace/i/JoshSmithXRM.power-platform-developer-suite`
- License: `https://img.shields.io/github/license/joshsmithxrm/power-platform-developer-suite`
- Build: GitHub Actions status badge for the `build.yml` workflow

Skip rating (0 reviews), sponsors, stars for v1. Revisit at v1.1 once the numbers exist.

**Repo README (`/README.md`) cleanup scope:**

- Tighten the hero sentence to align platform framing with the extension README's positioning (without duplicating extension-specific content).
- Add a Marketplace-install link badge near the top of the repo README.
- Audit the badge row: keep Build, License, .NET; consider dropping Codecov and "PRs Welcome" for signal-to-noise.
- Consolidate the duplicate "CLI Commands" table (currently at `/README.md:127-139`) with the CLI section above it.
- Remove any references to deferred items (logo, publisher verification) as if they ship in v1.
- Keep per-library sections (PPDS.Plugins, PPDS.Dataverse, PPDS.Migration, PPDS.Query) as-is — updating their sample code belongs to each library's own spec, not this one.

**Boundary rule — extension README vs repo README:**

| Content | Extension README | Repo README |
|---|---|---|
| What the VS Code extension does | ✅ Primary | ❌ One-line cross-link only |
| What the platform is | ❌ One-paragraph signpost | ✅ Primary |
| Per-panel feature details | ✅ | ❌ |
| Per-library API/code samples | ❌ | ✅ |
| CLI commands reference | ❌ Link out | ✅ |
| MCP install and capabilities | Short section + link | ✅ |
| Development / contributing | ❌ | ✅ |

#### CLI / TUI / MCP Surfaces

Not applicable — this spec governs public-facing documentation of the Extension surface plus the cross-cutting GitHub repo landing page. Other surfaces' docs live in their respective specs.

### Constraints

- Images in `src/PPDS.Extension/media/` are not regenerated during normal PR work; they are refreshed as part of release gating when capabilities change visually.
- `--baseImagesUrl` cannot be used with a mutable ref (branch name) — only a commit SHA. Relative paths plus branch refs cause silent image drift when `main` reshuffles; that's why the SHA pin is mandatory.
- The extension README and repo README share the same positioning decisions (brand, platform framing, AI messaging) — changes to these across releases must be applied to both documents in the same PR to prevent drift.
- The platform-specific publish path (`matrix.target` in the workflow) is load-bearing for the self-contained-install claim — if a generic-target publish ever gets added, AC-3 (no "install CLI separately") would become false. The workflow is therefore constrained to platform-specific targets only.

### Validation Rules

See Core Requirements for the normative `package.json` field rules; the table below only enumerates the mechanical checks that validators must apply.

| Field | Rule | Error |
|-------|------|-------|
| `package.json.categories` | All values in VS Code Marketplace enum | "`<value>` is not a valid Marketplace category. Allowed: Programming Languages, Snippets, Linters, Themes, Debuggers, Formatters, Keymaps, SCM Providers, Other, Extension Packs, Language Packs, Data Science, Machine Learning, Visualization, Notebooks, Education, Testing, AI, Chat (plus newer additions)." |
| `package.json.keywords` | 10–14 entries; no redundant variants (see table below) | "Keywords count out of bounds" / "Redundant variant pair detected: `<a>` + `<b>`" |
| `package.json.galleryBanner.color` | Hex string | "Banner color must be a hex string (e.g. `#25c2a0`)" |
| `package.json.galleryBanner.theme` | `"dark"` or `"light"` | "Banner theme must be 'dark' or 'light'" |
| README image refs (README body only; excludes `package.json.icon`) | Relative paths, no SVG | "Absolute URL or SVG reference detected in README body" |
| `media/*.png` | PNG magic bytes; uniform dimensions across the three | "Image format / dimensions mismatch" |
| `--baseImagesUrl` | Pinned to release tag SHA (40-char hex) | "`--baseImagesUrl` contains a branch ref, not a commit SHA" |

**Redundant-variant pairs forbidden in `keywords`** (keep at most one per row):

| Canonical (keep) | Variants forbidden alongside it |
|---|---|
| `dynamics-365` | `d365`, `dynamics365`, `dynamics 365`, `dynamics`, `crm`, `dynamics-crm`, `dynamics crm` |
| `power-platform` | `powerplatform`, `power platform` |
| `power-apps` | `powerapps`, `power apps` |
| `power-automate` | `powerautomate`, `power automate` |
| `dataverse` | `common-data-service`, `common data service`, `cds` |
| `developer-tools` | `devtools`, `dev-tools`, `dev tools` |
| `administration` | `admin` |
| `plugin-registration` | `plugins`, `plugin` |
| `solutions` | `solution` |
| `environments` | `environment` |

Validators use this exact pair list.

---

## Acceptance Criteria

All ACs are verified by deterministic scripts under `tests/extension_listing/`. README content is normalized before matching: collapse runs of whitespace to a single space, strip leading/trailing whitespace per line, case-preserving unless noted. Exact-equality ACs operate on the canonical-normalized string.

| ID | Criterion | Test | Status |
|----|-----------|------|--------|
| AC-01 | After normalizing `src/PPDS.Extension/README.md` (collapse whitespace, strip Markdown image/badge syntax), the canonical hero sentence appears as a contiguous substring within the first 10 non-empty content lines (excluding the H1 title and badge row). Hero text frozen in Surface-Specific Behavior → Extension Surface → Hero. | `tests/extension_listing/test_readme_hero.py` | ✅ |
| AC-02 | `src/PPDS.Extension/README.md` contains a `## Zero-config install` H2 header. That header's byte offset is greater than the byte offset of the hero image reference and less than the byte offset of the `## Features` H2 header. | `tests/extension_listing/test_readme_sections.py` | ✅ |
| AC-03 | `src/PPDS.Extension/README.md` does NOT contain any of the following substrings (case-insensitive): "PPDS CLI installed", "CLI installed and on your PATH", "install the ppds CLI first", "prerequisite: PPDS CLI". | `tests/extension_listing/test_readme_forbidden_phrases.py` | ✅ |
| AC-04 | `src/PPDS.Extension/README.md` contains all nine v1.0 panel labels (case-insensitive substring): "Data Explorer", "Solutions", "Plugin Traces", "Metadata Browser", "Connection References", "Environment Variables", "Web Resources", "Import Jobs", "Plugin Registration". All nine appear within a single contiguous section bounded by `## Features` and the next `## ` H2. | `tests/extension_listing/test_readme_panel_grid.py` | ✅ |
| AC-05 | `src/PPDS.Extension/README.md` contains an H2 header whose text (case-insensitive, after collapsing backticks) contains "ppds cli". | `tests/extension_listing/test_readme_sections.py` | ✅ |
| AC-06 | `src/PPDS.Extension/README.md` contains an H2 header whose text (case-insensitive) contains both "AI" and "MCP". | `tests/extension_listing/test_readme_sections.py` | ✅ |
| AC-07 | `src/PPDS.Extension/README.md` contains an H2 header with text "Part of PPDS" (or "Part of the PPDS platform") followed by an unordered list of ≥ 3 items mentioning each of: "CLI", "MCP", "NuGet". | `tests/extension_listing/test_readme_sections.py` | ✅ |
| AC-08 | Word count of `src/PPDS.Extension/README.md` (Markdown rendered to plain text, headers and list markers stripped, code blocks excluded) is in `[900, 1200]` inclusive. | `tests/extension_listing/test_readme_length.py` | ✅ |
| AC-09 | Every image reference in `src/PPDS.Extension/README.md` body (Markdown `![…](…)` and `<img src="…">`) uses a path starting with `media/`. No image URL starts with `http://` or `https://`. | `tests/extension_listing/test_readme_image_refs.py` | ✅ |
| AC-10 | No image reference in `src/PPDS.Extension/README.md` body has a `.svg` extension. Scope is README body only; `package.json` `icon` references and activity-bar SVG registrations are out of scope. | `tests/extension_listing/test_readme_image_refs.py` | ✅ |
| AC-11 | `src/PPDS.Extension/package.json` `categories` equals `["AI", "Notebooks", "Data Science", "Other"]` (order-sensitive equality). | `tests/extension_listing/test_package_json.py` | ✅ |
| AC-12 | `src/PPDS.Extension/package.json` `keywords` array length is in `[10, 14]` inclusive AND contains all of: `dataverse`, `dynamics-365`, `power-platform`, `pac`, `mcp`. | `tests/extension_listing/test_package_json.py` | ✅ |
| AC-13 | For every row in the redundant-variant-pairs table (Specification → Validation Rules), at most one of the canonical or any listed variant appears in `package.json` `keywords`. | `tests/extension_listing/test_package_json.py` | ✅ |
| AC-14 | `src/PPDS.Extension/package.json` `galleryBanner` deep-equals `{"color":"#25c2a0","theme":"dark"}`. | `tests/extension_listing/test_package_json.py` | ✅ |
| AC-15 | `src/PPDS.Extension/package.json` `preview` field is present and equals `false`. | `tests/extension_listing/test_package_json.py` | ✅ |
| AC-16 | `src/PPDS.Extension/package.json` `qna` field is present and equals `false`. | `tests/extension_listing/test_package_json.py` | ✅ |
| AC-17 | `src/PPDS.Extension/package.json` `description` (case-insensitive) contains "Power Platform" AND "Dataverse" AND at least one of "MCP" / "AI" AND does NOT contain "comprehensive toolkit". | `tests/extension_listing/test_package_json.py` | ✅ |
| AC-18 | `src/PPDS.Extension/package.json` `displayName` equals the exact string "Power Platform Developer Suite". | `tests/extension_listing/test_package_json.py` | ✅ |
| AC-19 | `src/PPDS.Extension/media/notebook-hero.png`, `.../metadata-browser.png`, `.../plugin-traces.png` all exist. | `tests/extension_listing/test_media_assets.py` | ✅ |
| AC-20 | All three PNGs have identical pixel dimensions (read via PNG IHDR chunk). | `tests/extension_listing/test_media_assets.py` | ✅ |
| AC-21 | All three files begin with the PNG magic bytes `89 50 4E 47 0D 0A 1A 0A`; none are SVG or JPEG. | `tests/extension_listing/test_media_assets.py` | ✅ |
| AC-22 | In `src/PPDS.Extension/CHANGELOG.md`, the v1.0.0 version block (content between `## [1.0.0]` and the next `## [...]` header) contains a Markdown link whose URL matches the regex `https://marketplace\.visualstudio\.com/items\?itemName=JoshSmithXRM\.power-platform-developer-suite.*review`. The link appears before any H3 subheader (`### `) within the block. | `tests/extension_listing/test_changelog_cta.py` | ✅ |
| AC-23 | Neither `src/PPDS.Extension/README.md` nor `README.md` (repo root) contains any of the forbidden phrases (case-insensitive): "PAC parity", "full ALM replacement", "superset of PAC". | `tests/extension_listing/test_forbidden_claims.py` | ✅ |
| AC-24 | Both publish steps of `.github/workflows/extension-publish.yml` (currently lines 114 and 123) include `--baseImagesUrl` whose URL value matches the regex `https://raw\.githubusercontent\.com/joshsmithxrm/power-platform-developer-suite/[0-9a-f]{40}/src/PPDS\.Extension/media/`. The 40-hex-char SHA component MUST be resolved from the release tag at workflow runtime (not a hard-coded value committed to the workflow). | `tests/extension_listing/test_publish_workflow.py` | ✅ |
| AC-25 | The workflow's target matrix equals exactly the set `{win32-x64, linux-x64, darwin-x64, darwin-arm64}` — no additions, no omissions. | `tests/extension_listing/test_publish_workflow.py` | ✅ |
| AC-26 | `README.md` (repo root) contains, within its first paragraph (content between H1 and first H2 `## `), a link to `https://marketplace.visualstudio.com/items?itemName=JoshSmithXRM.power-platform-developer-suite`. | `tests/extension_listing/test_repo_readme.py` | ✅ |
| AC-27 | `README.md` (repo root) does NOT contain any of the extension-specific panel-descriptor phrases that belong to the extension README: "Five-tab entity explorer", "Timeline waterfall with trace-level management", "Virtual-scrolled results", "Notebook cell · SQL and FetchXML", "Maker Portal buttons on Solutions and Plugin Traces". (Exact, case-insensitive substring match.) | `tests/extension_listing/test_repo_readme.py` | ✅ |
| AC-28 | `src/PPDS.Extension/package.json` `icon` file exists on disk, is a PNG (magic bytes), and has exactly 128×128 pixel dimensions (PNG IHDR chunk). | `tests/extension_listing/test_package_json.py` | ✅ |
| AC-29 | A `release-validation` gate exists (CI job or script invocable pre-publish) that runs AC-01 through AC-28 and fails the release if any AC is not green. | `tests/extension_listing/test_release_gate.py` | ✅ |

Status key: ✅ covered by passing test · ⚠️ test exists but failing · ❌ no test yet · 🔲 not yet implemented

### Edge Cases

| Scenario | Input | Expected Output |
|----------|-------|-----------------|
| `media/` missing a required PNG at publish time | Image file absent | CI publish workflow fails AC-19; no publish. |
| `--baseImagesUrl` uses branch ref (e.g., `main`) | Workflow invokes with `.../main/...` | AC-24 fails; publish blocked. |
| Feature PR adds a new panel but does not update the nine-panel list | README shows 9, code has 10 | AC-04 fails at release gate; requires README update. |
| Future category added to VS Code enum | Upstream change | Validation rule's error message cites the enum — author updates validator and spec together. |
| User rebases `main` after tag creation, shifting SHA | Release tag points to old commit | Retag on new commit; `--baseImagesUrl` SHA is resolved from the tag at publish time, so it follows. |
| README exceeds 1200 words | e.g., 1400 | AC-08 fails; author trims content or moves to per-library section in repo README. |
| Marketplace changes rendering rules (e.g., disallows new HTML) | Upstream change | Validation rule / AC-10 updated together with a supporting `specs/marketplace-listing.md` changelog entry. |

---

## Configuration

| Setting | Location | Type | Required | Default | Description |
|---------|----------|------|----------|---------|-------------|
| `categories` | `src/PPDS.Extension/package.json` | array | Yes | `["AI","Notebooks","Data Science","Other"]` | Marketplace filter categories. |
| `keywords` | `src/PPDS.Extension/package.json` | array | Yes | 12 targeted terms (see AC-12) | Marketplace search terms. |
| `galleryBanner.color` | `src/PPDS.Extension/package.json` | string (hex) | Yes | `#25c2a0` | Banner background color. |
| `galleryBanner.theme` | `src/PPDS.Extension/package.json` | `"dark"` or `"light"` | Yes | `"dark"` | Banner text theme. |
| `preview` | `src/PPDS.Extension/package.json` | boolean | Yes | `false` | Marketplace Preview badge off for stable releases. |
| `qna` | `src/PPDS.Extension/package.json` | boolean or URL | Yes | `false` | Q&A routing (redirects to `bugs.url`). |
| `description` | `src/PPDS.Extension/package.json` | string | Yes | (see AC-17) | Marketplace subtitle. |
| `displayName` | `src/PPDS.Extension/package.json` | string | Yes | "Power Platform Developer Suite" | Marketplace title. |
| `icon` | `src/PPDS.Extension/package.json` | path | Yes | `images/icon.png` (placeholder for v1; #302 for v1.1) | 128×128 PNG icon. |

---

## Design Decisions

### Why keep "Power Platform Developer Suite" as the brand?

**Context:** Microsoft's trademark guidelines say third-party tools should not "begin with" a Microsoft mark. "Power Platform Developer Suite" begins with "Power Platform" and uses "Suite" (a noun that implies first-party offerings). This is a MEDIUM-risk name structurally.

**Decision:** Keep the brand. Accept the residual trademark risk.

**Alternatives considered:**
- **Full rebrand to a distinctive name** (e.g., Supabase/Neon/Prisma-style coined word): eliminates trademark risk but costs the 644 installs of equity, forces NuGet package ID renames (breaking for consumers), resets the immutable VS Code extension install count, and requires ~12 months of SEO investment. Rejected for v1.
- **Reframe to "X for Power Platform"**: the "for" softener is on Microsoft's approved list. Lower-cost than rebrand but creates confusion (repo name says one thing, Marketplace says another). Rejected.

**Consequences:**
- Positive: zero migration cost; SEO-aligned; consistent with peer tools (Dataverse DevTools, Power Platform ToolBox) that live on Microsoft's own Community Tools Learn page.
- Negative: if Microsoft Legal contacts the project, a forced rename under duress is a large-blast-radius event. Mitigation: have a prepared fallback name (documented in this spec's Roadmap) so a rename can execute quickly.

### Why hybrid (extension-centric + platform signpost) instead of pure extension-centric or pure platform-centric?

**Context:** The Marketplace reader's question is "what does installing this give me?" The repo reader's question is "what is this platform?" Different audiences, same product.

**Decision:** Extension README is extension-centric with a platform signpost near the bottom (Power Platform Tools model). Repo README is platform-centric.

**Alternatives considered:**
- **Pure extension-centric** (GitHub PRs / Prisma model): omits the platform story entirely. Works when the platform brand is already universally known. PPDS is not.
- **Pure platform-centric on the Marketplace** (Supabase repo README ported to Marketplace): violates the "what does installing this give me?" norm. 0 of 7 benchmarked extensions do this.

**Consequences:**
- Positive: each document optimized for its reader. 6 of 7 benchmarked extensions converge on this pattern.
- Negative: two documents to keep aligned on positioning decisions — mitigated by the boundary rule in Surface-Specific Behavior.

### Why `["AI", "Notebooks", "Data Science", "Other"]` for categories?

**Context:** Current `["Azure", "Other"]` is broken — `Azure` is not in the VS Code Marketplace categories enum (verified against https://code.visualstudio.com/api/references/extension-manifest). The category surface is load-bearing for filter discoverability.

**Decision:** Four categories that are all valid and all authentic.
- **AI** — PPDS ships an MCP server and the CLI is intentionally consumable by AI coding tools. Authentic.
- **Notebooks** — `.ppdsnb` files are real notebook types. No comparator in the Dataverse space uses this category; differentiator.
- **Data Science** — SQL/FetchXML query engine fits.
- **Other** — safe fallback. Both Microsoft's Power Platform Tools and Dataverse DevTools use `Other`.

**Alternatives considered:**
- **Keep `Azure`** — invalid value; probably ignored by Marketplace filter (user never appears under the Azure filter).
- **Drop `Other`** — "Other" is the universal fallback; dropping it reduces net discoverability without offsetting gain.
- **Add `Visualization`** — fits tabular/split-pane views but not well-known as a Dataverse-adjacent filter. No benchmark uses it.

**Consequences:**
- Positive: valid enum; authentic; differentiated from competitors on `AI` + `Notebooks`.
- Negative: none material.

### Why 12 keywords, not 30 (the limit)?

**Context:** Marketplace allows up to 30 keywords. Current `package.json` ships 35 (invalid at publish). Microsoft's own Power Platform Tools uses 5; Dataverse DevTools uses 7.

**Decision:** 10–14 targeted terms. Specific inclusions: `dataverse`, `dynamics-365`, `power-platform`, `pac`, `mcp` (mandatory via AC-12).

**Alternatives considered:**
- **Max out at 30** — dilutes signal; Marketplace ranks quality matches over count.
- **Go ultra-narrow (5 like Microsoft)** — Microsoft has a verified-publisher badge carrying trust; PPDS doesn't yet.

**Consequences:**
- Positive: hits high-intent queries (`pac CLI VS Code`, `dataverse notebook`, `mcp dataverse`) without dilution.
- Negative: misses some long-tail queries — acceptable.

### Why drop the "install the CLI separately" prerequisite?

**Context:** The extension bundles a self-contained, single-file, compressed `ppds` CLI binary per platform via `tools/bundle-cli.mjs`, and the publish workflow ships four platform-specific VSIX files. Marketplace install is zero-config. The current README line 9 ("PPDS CLI installed and on your PATH") is misleading.

**Decision:** Remove the prerequisite entirely from the extension README. Frame the zero-config install as a differentiator (Microsoft's Power Platform Tools requires separate PAC CLI install).

**Alternatives considered:**
- **Keep a "CLI is bundled" note without removing the prerequisite wording** — retains misleading phrasing.
- **Keep the prerequisite as a fallback note** ("in rare cases...") — adds noise for a corner case that doesn't occur in marketplace installs.

**Consequences:**
- Positive: honest; reduces reader friction; turns a prior liability into a genuine differentiator.
- Negative: if the publish workflow ever loses its platform-specific targets, AC-3 becomes false overnight. Mitigated by the Constraint in the Specification.

### Why relative paths + `--baseImagesUrl`, not absolute SHA URLs in README source?

**Context:** Three ways to keep Marketplace image rendering stable:
- Absolute URLs pinned to SHA in README source (rewritten every release)
- Relative paths + default `main`-branch rewrite (silently breaks when images move on `main`)
- Relative paths + `--baseImagesUrl <sha>/...` at publish time (single mechanism, pin at publish only)

**Decision:** Option 3 — relative paths in README source, SHA pin at publish time via `--baseImagesUrl`.

**Alternatives considered:**
- **Absolute URLs in README** — duplicates the SHA in two places; GitHub rendering breaks unless the SHA is always kept fresh; harder to author.
- **Default `main` rewrite** — silently breaks when `main` reshuffles `media/`; no warning.

**Consequences:**
- Positive: single SHA-pinning surface (the publish command); README renders correctly on both GitHub and Marketplace; low authoring friction.
- Negative: requires the publish step to be disciplined about SHA resolution (enforced by AC-24).

### Why "please rate" CTA in CHANGELOG, not in-extension toast?

**Context:** Marketplace currently has 0 reviews on 644 installs. Reviews are the #1 conversion signal for cold visitors. The user explicitly chose "silent upgrade" for the 644 existing users — no in-extension welcome modal.

**Decision:** Add a single "please rate" line to the v1.0 CHANGELOG entry. CHANGELOG is the only free channel to existing users that doesn't break the silent-upgrade decision.

**Alternatives considered:**
- **In-extension toast on first v1.0 activation** — violates user's silent-upgrade call.
- **No CTA** — 0 reviews remains the conversion floor.

**Consequences:**
- Positive: free; cheap to author; a few users act on it.
- Negative: some users may see the CTA as noise — mitigated by keeping it to one line.

### Why include this spec at all instead of treating it as a plan?

**Context:** Constitution SL2: "Plans (`.plans/`) are ephemeral and consumed by implementation. Project coordination documents (parity, polish, audit) are plans, not specs." A marketplace listing launch could be framed as a one-shot launch plan.

**Decision:** Living spec. The marketplace listing is an ongoing surface maintained at every release, not a one-shot deliverable. The spec describes how PPDS maintains the listing — format, rules, validation — not a v1-launch project.

**Alternatives considered:**
- **One-shot plan in `.plans/`** — ephemeral; no mechanism for drift detection on future releases; every release re-discovers the same decisions from scratch.
- **Split: living spec for format rules + one-shot plan for v1 launch** — two artifacts for one workflow; splits the AC set across documents; harder to review together.

**Consequences:**
- Positive: rules outlive the launch; each release validates against the same ACs; drift surfaces as gate findings.
- Negative: the spec is longer than a launch plan would be. Acceptable — the rules need to exist somewhere.

---

## Related Specs

- [audit-capture.md](./audit-capture.md) — produces the PNGs this spec consumes.
- [authentication.md](./authentication.md) — source of truth for the 9 auth methods referenced in listing copy.
- [publish.md](./publish.md) — the `ppds publish` CLI command (distinct from Marketplace publish; mentioned here to avoid naming collision in the extension README).
- [architecture.md](./architecture.md) — platform framing aligned in both READMEs.

---

## Changelog

| Date | Change |
|------|--------|
| 2026-04-20 | Initial spec from /design session. Covers extension README, repo README, package.json marketplace fields, CHANGELOG v1.0.0 CTA, image inventory, and publish-time image-URL pinning. Absorbs investigation findings (brand retention, hybrid pattern, `["AI","Notebooks","Data Science","Other"]` categories, 12 keywords, bundled-CLI framing, honest PAC parity copy). |
| 2026-04-20 | Post-`/review` revisions: tightened ambiguous ACs (hero-normalization, section-ordering via byte offsets, CHANGELOG/repo-README link positions via semantic anchors rather than line counts); replaced AC-27 content-overlap heuristic with explicit forbidden-substring list; removed unverifiable "audit-capture redaction" AC (delegated to `audit-capture.md`); added explicit workflow-change scope for `--baseImagesUrl`; added matrix-target equality AC-25; added icon-dimensions AC-28; added release-validation gate AC-29; labeled Code paths; moved redundant-variant pair list into spec body; clarified SVG scope to README body only. |
| 2026-04-20 | Post-plan-review: corrected release-tag prefix from `v<X.Y.Z>` to `Extension-v<X.Y.Z>` to match the existing publish workflow's tag-filter at `.github/workflows/extension-publish.yml:28`; added constraint that `workflow_dispatch` must not silently pin to a branch HEAD. |
| 2026-04-20 | Implementation landed (phases 1–5). Tests live at `tests/extension_listing/` (Python-module underscore, not the spec's earlier hyphenated draft); `tests/extension-listing/` path references updated to match. All 29 ACs flipped from 🔲 to ✅. Media PNGs are placeholder solid-color captures pending real `audit-capture run extension` output before the Extension-v1.0.0 tag. |

---

## Roadmap

- **Logo / icon swap (#302, v1.1):** replace placeholder `images/icon.png`; update `galleryBanner` if a companion color emerges.
- **Publisher verification (#786):** verified-publisher badge lands; no spec change; trust signal improves.
- **Demo GIF (#832, v1.1):** add a short Notebook GIF inline (kept under 10 MB per Marketplace guidance); AC added for size and duration at that time.
- **Brand-fallback trigger:** if Microsoft Legal ever contacts the project, rename candidates to consider (in preference order): "PPDS — Tools for Power Platform", "PPDS for Power Platform", distinctive coined name. Repo/package-ID migration plan would be a separate spec at that time.
- **Marketplace Q&A re-enable:** if GitHub Issues volume drops below a threshold, consider re-enabling `qna` to surface Marketplace-specific feedback.
- **Light-theme screenshots:** add companion `media/*-light.png` if Marketplace adds theme-aware image rendering.
