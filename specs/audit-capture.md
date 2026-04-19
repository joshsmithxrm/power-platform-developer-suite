# Audit Capture

**Status:** Draft
**Last Updated:** 2026-04-18
**Code:** [tools/audit-capture.mjs](../tools/audit-capture.mjs) | [tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs](../tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs) | [.claude/skills/audit-capture/](../.claude/skills/audit-capture/) | [.claude/audit-manifests/](../.claude/audit-manifests/)
**Surfaces:** TUI | Extension

---

## Overview

Reusable, low-friction capture pipeline that produces PNG snapshots of every user-facing TUI screen and extension panel, written to a contract-conforming directory tree that design-audit sessions (Claude web UI or human designer) can consume. Manifests are the single source of truth for "what counts as a surface."

The contract is defined by [`AUDIT-SCHEMA.md`](https://github.com/joshsmithxrm/ppds-design-system/blob/main/AUDIT-SCHEMA.md) in `ppds-design-system`. This spec describes how PPDS produces artifacts that conform to that contract for the `tui` and `extension` surfaces; `ppds-docs` produces `docs` artifacts under its own spec.

### Goals

- **Uniform capture artifact** — every TUI screen and extension panel emits PNG + `meta.json`, layout per schema
- **Manifest-driven** — adding a new screen means editing `.claude/audit-manifests/{surface}.yaml`, nothing else
- **Unattended** — once a profile+env is configured, `audit-capture run <surface>` walks the whole manifest with no prompts
- **Robust** — a broken entry marks itself `state: error`, the run continues, exit is non-zero
- **No new render engine for TUI** — reuse Playwright (already a dev dep) + xterm.js rather than adopting `agg` or native canvas libs
- **CI-ready** — deterministic output (fixed font, DPR, viewport) so the same commit produces byte-stable captures

### Non-Goals

- Capturing the docs site (separate spec, separate repo — `ppds-docs`)
- Writing the `manifest.json` / `meta.json` schema itself (defined in `ppds-design-system/AUDIT-SCHEMA.md`)
- Producing design-audit findings (produced downstream by a Claude audit session; findings schema also in `AUDIT-SCHEMA.md`)
- Visual regression testing (this is audit input, not regression output — different workflow)
- Running the capture automatically on every commit (Phase 4 GH Action is manual `workflow_dispatch` only)
- Light-theme extension captures (dark-only in v1; manifest schema reserves a `themes` field for future)

---

## Architecture

```
┌────────────────────────────────────────────────────────────────────┐
│  tools/audit-capture.mjs                                          │
│  (runner — orchestrates per-surface captures)                     │
└───┬─────────────────────────────────────────┬──────────────────────┘
    │                                         │
    │ shells out to                           │ shells out to
    ▼                                         ▼
┌──────────────────────────────┐   ┌──────────────────────────────┐
│ tests/PPDS.Tui.E2eTests/     │   │ src/PPDS.Extension/          │
│   tools/tui-verify.mjs        │   │   tools/webview-cdp.mjs      │
│                              │   │                              │
│ + NEW `render <file.png>`    │   │ (existing `screenshot`)      │
│   subcommand                 │   │                              │
│                              │   │                              │
│ daemon: tui-test Terminal    │   │ daemon: Playwright Electron  │
│ + Playwright page holding    │   │ + VS Code                    │
│   xterm.js for PNG render    │   │                              │
└──────────────────────────────┘   └──────────────────────────────┘

Inputs:   .claude/audit-manifests/{surface}.yaml
Outputs:  $AUDIT_OUT/{surface}/{entry-id}/{NN-name}.png
          $AUDIT_OUT/{surface}/{entry-id}/meta.json
          $AUDIT_OUT/manifest.json   (written last)
```

The runner is a thin orchestrator. All the heavy-lifting (PTY, VS Code, rendering) stays in the existing verify tools; the runner only walks manifests and shells out.

### Components

| Component | Responsibility |
|-----------|----------------|
| `tools/audit-capture.mjs` | Reads a manifest, drives the appropriate verify tool, writes schema-conformant output. Subcommands: `run`, `validate`, `list`. |
| `tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs` | Existing PTY harness. Gains a `render` subcommand that writes a PNG of current terminal state. |
| Render harness (internal to tui-verify daemon) | Headless Chromium page holding xterm.js. Kept warm across captures. |
| `.claude/audit-manifests/tui.yaml` | Inventory of TUI screens to capture. Version-controlled. |
| `.claude/audit-manifests/extension.yaml` | Inventory of extension panels to capture. Version-controlled. |
| `.claude/skills/audit-capture/SKILL.md` | Skill-authored documentation: usage, env vars, gotchas. |
| Theme pin | On extension launch, `audit-capture` writes `settings.json` into webview-cdp's profile dir to lock `workbench.colorTheme`. |

### Dependencies

- Depends on: [tui-verify-tool.md](./tui-verify-tool.md) — the PTY harness
- Depends on: [ext-verify-tool.md](./ext-verify-tool.md) — the VS Code webview harness
- Contract: [`AUDIT-SCHEMA.md`](https://github.com/joshsmithxrm/ppds-design-system/blob/main/AUDIT-SCHEMA.md) on `ppds-design-system` `main`

No new npm dependencies for the runner itself (uses `yaml`, already transitively available, or a tiny parser). For `tui-verify render`: reuses existing `@playwright/test` in `tests/PPDS.Tui.E2eTests/`. Adds `xterm` (the DOM terminal library) as a new dev dep there — tiny (~200 KB), pure JS, no native code. xterm.js is the terminal renderer VS Code itself uses, so it produces faithful output for our users.

---

## Specification

### Core Requirements

1. The runner MUST read manifests in YAML at `.claude/audit-manifests/{surface}.yaml`
2. The runner MUST write output conforming to `AUDIT-SCHEMA.md` v1: folder layout, `manifest.json`, `meta.json`
3. The runner MUST refuse to write inside the repo working tree — `$AUDIT_OUT` must be an absolute path outside the repo
4. The runner MUST write `manifest.json` **after** every entry directory is flushed, so a reader never sees a manifest referencing missing files
5. The runner MUST continue after an entry errors; final exit code is 0 iff every entry is `state=ok` or `state=skipped`
6. The runner MUST record redacted values in `meta.json` rather than raw values when `redactEnv: true` is configured (default true)
7. `tui-verify render <file.png>` MUST produce a PNG of the current terminal state, 120 cols × 30 rows, DPR 2.0, PPDS default palette
8. `tui-verify render` MUST NOT modify the existing `screenshot` JSON dump command — that command stays as-is for text-based verification
9. Extension captures MUST pin VS Code's color theme deterministically via a pre-launch `settings.json` write to webview-cdp's profile dir
10. Manifest entries MUST support `requires: connected` — the runner marks the entry `state=skipped` when `PPDS_PROFILE` or `PPDS_ENV` is unset

### Command Interface

**Runner (`tools/audit-capture.mjs`):**

| Command | Signature | Purpose |
|---------|-----------|---------|
| `run` | `run <surface>` | Walk the manifest for `tui` or `extension`, capture every entry, emit `manifest.json`. |
| `run all` | `run all` | Capture every surface in sequence. Writes one merged `manifest.json`. |
| `validate` | `validate <surface>` | Parse the manifest, dry-run every entry through the verify tool (no captures written). Exits non-zero on first broken entry. |
| `list` | `list <surface>` | Print the manifest entry ids and titles. Useful for quick inventory. |

**`tui-verify.mjs` additions:**

| Command | Signature | Purpose |
|---------|-----------|---------|
| `render` | `render <file.png>` | Write a PNG of the current terminal state to `<file.png>`. Uses xterm.js in a warm headless Chromium. |

### Environment Variables

| Name | Required | Purpose |
|------|----------|---------|
| `AUDIT_OUT` | yes | Absolute path to output directory. Must be outside the repo working tree. Runner creates it if missing. |
| `PPDS_PROFILE` | no | Profile name for connected captures. Entries with `requires: connected` are skipped when unset. |
| `PPDS_ENV` | no | Environment name for connected captures. Skipped same as above when unset. |
| `AUDIT_REDACT` | no | `true` (default) to mask env name + user principal in the TUI status bar and extension sidebar before writing PNG. `false` to keep raw values. |
| `AUDIT_SOURCE_REPO` | no | Overrides the `source.repo` field in `manifest.json`. Defaults to detecting from `origin` URL. |
| `AUDIT_SOURCE_REF` | no | Overrides the `source.ref` field. Defaults to current branch ref. |
| `AUDIT_SOURCE_COMMIT` | no | Overrides the `source.commit` field. Defaults to `HEAD`. |

### Manifest Format

YAML. One manifest per surface. Structure:

```yaml
# .claude/audit-manifests/tui.yaml
surface: tui
entries:
  - id: sql-query-main
    title: SQL Query screen — empty state
    requires: connected           # optional: connected | none (default)
    steps:
      - key: alt+t
      - key: enter
      - wait: { text: "SQL Query", timeout: 5000 }
      - screenshot: 01-empty
      - type: "SELECT TOP 5 name FROM account"
      - screenshot: 02-query-typed
      - key: F5
      - wait: { text: "rows", timeout: 30000 }
      - screenshot: 03-results
    masks:
      - { row: 24, colStart: 0, colEnd: 120, reason: "latency varies per run" }
```

Supported step types (TUI):

| Step | Shape | Maps to tui-verify command |
|------|-------|----------------------------|
| key | `{ key: "alt+t" }` | `key "alt+t"` |
| type | `{ type: "text" }` | `type "text"` |
| wait | `{ wait: { text: "...", timeout: 5000 } }` | `wait "..." 5000` |
| screenshot | `{ screenshot: "01-name" }` | `render $AUDIT_OUT/tui/<id>/01-name.png` |
| sleep | `{ sleep: 250 }` | `setTimeout(250)` in the runner — only for entries where a deterministic wait isn't possible |

Supported step types (extension):

| Step | Shape | Maps to webview-cdp command |
|------|-------|------------------------------|
| command | `{ command: "PPDS: Data Explorer" }` | `command "PPDS: Data Explorer"` |
| wait | `{ wait: { ext: "power-platform-developer-suite", timeout: 30000 } }` | `wait --ext ... --timeout ...` |
| click | `{ click: "#execute-btn", ext: "power-platform-developer-suite" }` | `click "#execute-btn" --ext ...` |
| eval | `{ eval: "monaco.editor.getEditors()[0].setValue('...')" }` | `eval "..."` |
| key | `{ key: "ctrl+enter" }` | `key "ctrl+enter"` |
| screenshot | `{ screenshot: "01-loaded" }` | `screenshot $AUDIT_OUT/extension/<id>/01-loaded.png` |
| sleep | `{ sleep: 500 }` | `setTimeout` — minimal use |

Masks (TUI): `{ row, colStart, colEnd, reason }` — blanks that cell range before PNG write. Coordinates in cell units.
Masks (extension): `{ x, y, width, height, reason }` — blanks that rect in pixels before PNG write.

### Runner Flow (TUI surface)

1. **Validate env**: `AUDIT_OUT` absolute, outside repo root.
2. **Parse manifest**: fail fast if malformed; dedupe entry ids.
3. **Launch tui-verify**: `tui-verify.mjs launch --build` once. Wait for splash.
4. **For each entry**:
    - If `requires: connected` and no profile/env: record `state=skipped`, skip.
    - Navigate to baseline (Esc, Esc, back to splash) — best-effort; fall back to full relaunch on failure.
    - For each step: translate → `sendToDaemon`. Collect steps in memory for `meta.json`.
    - On screenshot step: call `render` → writes PNG. After write, apply masks via canvas post-process (load PNG, fill masked rects with `#000`, re-encode).
    - On any step failure: capture stderr (4 KB cap), mark `state=error`, attempt teardown + relaunch for next entry, continue.
5. **Close tui-verify** after final entry.
6. **Write per-entry `meta.json`** with steps echo, masks applied, surfaceSpecific including the `serialize()` dump.
7. **Emit `manifest.json`** at `$AUDIT_OUT/manifest.json`.
8. Exit 0 iff every entry is `ok` or `skipped`; else 1.

### Runner Flow (Extension surface)

1. **Validate env**.
2. **Write theme pin**: ensure `src/PPDS.Extension/tools/.webview-cdp-profile/User/settings.json` contains `{ "workbench.colorTheme": "Default Dark+" }`. Create directories as needed.
3. **Launch webview-cdp**: `webview-cdp.mjs launch --build` once.
4. **For each entry** (same error/skip semantics as TUI):
    - Translate steps. Screenshot steps → `screenshot --page` (full VS Code window) OR `screenshot` (webview-only) depending on entry config.
    - Apply pixel-rect masks to PNG.
5. **Close webview-cdp**.
6. **Emit meta.json + manifest.json**.

### `tui-verify render` — Render Pipeline

1. **On first `render` call** in a daemon lifetime: spawn a Playwright Chromium page navigated to a data-URL HTML shell that loads xterm.js and instantiates a `Terminal({ rows: 30, cols: 120, fontFamily: "Cascadia Mono", fontSize: 16, theme: PPDS_THEME })`. The page stays open for the daemon's lifetime.
2. **On every `render` call**:
    - Call tui-test `terminal.serialize()` — returns `{ view, shifts }`.
    - Convert shifts to an SGR-annotated replay stream (per-cell escape sequences preceding each rendered char, reset at row end). Plain text only (no cursor position moves) because we `term.clear()` + write sequentially row by row.
    - `page.evaluate((stream) => window.__writeBuffer(stream), replayStream)`.
    - Screenshot the `.xterm-screen` canvas rect: `page.locator(".xterm-screen").screenshot({ path: outFile })`.
3. **Theme** is locked in the page shell:
    - Foreground `#f0f0f0`, background `#1e1e1e`, cursor `#f0f0f0`, 16-color ANSI mapped from PPDS design-system palette (`--c-black` `#1e1e1e`, `--c-cyan` `#00cccc`, etc., sourced from `ppds-design-system/colors_and_type.css`).
    - Font **bundled** as WOFF2 in the page shell via base64 data URL so the render has zero filesystem-font dependence and is byte-stable in CI.

### Constraints

- `$AUDIT_OUT` must be outside repo working tree. Runner rejects paths under the current git root with exit 1.
- All errors to stderr; stdout is schema-relevant data or silent.
- No `shell: true` anywhere (Constitution S2).
- Secret-carrying values (`clientSecret`, `password`, tokens) never logged (Constitution S3). Env name + user principal redacted by default (pre-PNG write for TUI status bar; post-capture for extension sidebar via pixel mask).
- Runner is single-process, single-surface-at-a-time. `run all` serializes (TUI then extension) to avoid contention.
- Render viewport: 120 cols × 30 rows × 16px Cascadia Mono × DPR 2.0 = fixed-size PNG (computed at render time; target ~2304 × 1080).
- Extension capture: default full-window shot (matches webview-cdp's existing behavior). `meta.json` records `webviewRect` so audit tooling can crop.
- Manifest `id` values must be kebab-case ASCII and unique within a surface. Runner rejects at parse time.

### Validation Rules

| Input | Rule | Error |
|-------|------|-------|
| `AUDIT_OUT` | absolute path, outside repo | "AUDIT_OUT must be an absolute path outside the repo working tree" |
| manifest path | file exists and parses as YAML | "Manifest not found" / "Manifest parse error: ..." |
| entry `id` | kebab-case ASCII, unique | "Invalid id: '...' (must be kebab-case)" / "Duplicate id: '...'" |
| step shape | matches one of the supported shapes | "Unknown step at entries[N].steps[M]: ..." |
| screenshot `name` | `<NN>-<kebab>` pattern; N is integer, monotonic | "Screenshot name must match NN-name pattern: '...'" |
| `render <file>` | parent dir exists or can be created | "Cannot write to: ..." |

---

## Acceptance Criteria

| ID | Criterion | Test | Status |
|----|-----------|------|--------|
| AC-01 | `tui-verify render <file.png>` writes a readable PNG containing the current terminal state's visible text | Manual: launch TUI, run `render $TEMP/out.png`, open PNG and verify title bar text is present | 🔲 |
| AC-02 | `tui-verify render` uses PPDS default palette (cyan accents, dark background) | Manual: render a screen with cyan highlights, visually confirm the palette matches `colors_and_type.css` | 🔲 |
| AC-03 | `tui-verify render` output is 120 cols × 30 rows at DPR 2.0 (approx 2304×1080 px) | Manual: render; check PNG dimensions via `file out.png` or image inspector | 🔲 |
| AC-04 | Existing `tui-verify screenshot <file.json>` command still dumps `serialize()` JSON unchanged | Manual: run both commands; diff JSON output against pre-change behavior | 🔲 |
| AC-05 | `audit-capture run tui` captures every entry in `tui.yaml` without prompts given `AUDIT_OUT` + connected profile | Manual: configure `PPDS_PROFILE`+`PPDS_ENV`, run; every entry should be `ok` or intentionally `skipped` | 🔲 |
| AC-06 | `audit-capture run extension` does the same for `extension.yaml` | Manual: same as AC-05 for extension surface | 🔲 |
| AC-07 | `$AUDIT_OUT/manifest.json` conforms to `AUDIT-SCHEMA.md` v1 | Manual: validate with a schema checker (e.g., hand-check fields per the schema doc) | 🔲 |
| AC-08 | Each entry's `meta.json` conforms to the surface-specific meta schema | Manual: pick one tui + one extension entry, diff against schema | 🔲 |
| AC-09 | When any entry's step fails, the run continues, that entry is `state=error` with stderr captured, and final exit is non-zero | Manual: deliberately break one entry (bad key); verify the rest complete and exit is 1 | 🔲 |
| AC-10 | Entries with `requires: connected` are marked `state=skipped` when `PPDS_PROFILE` is unset; run continues; exit is 0 if nothing else errored | Manual: unset env, run; verify skipped entries have `skipReason` | 🔲 |
| AC-11 | `audit-capture validate <surface>` dry-runs every entry through the verify tool without writing captures, exits non-zero on first broken step | Manual: run against a deliberately broken manifest | 🔲 |
| AC-12 | Adding a new screen requires only editing `tui.yaml` or `extension.yaml`; no runner changes needed | Manual: add a trivial entry, run; the new entry appears in the output | 🔲 |
| AC-13 | `.claude/skills/audit-capture/SKILL.md` documents the single-command example and the env vars | Manual: read the skill; verify it includes `AUDIT_OUT`, `PPDS_PROFILE`, `PPDS_ENV`, one-line usage | 🔲 |
| AC-14 | Runner rejects `AUDIT_OUT` pointing inside the repo working tree | Manual: set `AUDIT_OUT=./out`; run; expect exit 1 + clear error | 🔲 |
| AC-15 | TUI masks blank the configured cell range in the final PNG | Manual: mask row 24 cols 0-120; render; inspect PNG — status bar region is `#000` | 🔲 |
| AC-16 | Extension capture pins VS Code theme to `Default Dark+` via `settings.json` in profile dir | Manual: run capture; inspect `.webview-cdp-profile/User/settings.json` contains the theme setting | 🔲 |
| AC-17 | `manifest.json` is written *after* every entry directory is flushed (manifest never references missing files) | Manual: interrupt mid-run; `manifest.json` should not exist | 🔲 |
| AC-18 | Manifest entry `id` validation: kebab-case required, uniqueness enforced at parse time | Manual: add duplicate ids; run; expect clear parse error | 🔲 |
| AC-19 | `audit-capture list <surface>` prints id + title per entry | Manual: run; verify stdout lists entries | 🔲 |
| AC-20 | Runner detects source repo/ref/commit from git and records in `manifest.json`, overridable via env vars | Manual: run; check `source` block; override with `AUDIT_SOURCE_COMMIT=xyz` and verify | 🔲 |

### Edge Cases

| Scenario | Input | Expected Output |
|----------|-------|-----------------|
| Empty manifest | `entries: []` | stdout: "No entries to capture"; `manifest.json` written with empty surface; exit 0 |
| Manifest missing | No `tui.yaml` | stderr: "Manifest not found: ..."; exit 1 |
| `$AUDIT_OUT` inside repo | `AUDIT_OUT=./out` | stderr: "AUDIT_OUT must be outside repo working tree"; exit 1 |
| Render fails mid-run | xterm.js page crashes | Entry marked `error`, daemon recovers by recreating the render page, next entry proceeds |
| Screenshot name collision | Two `screenshot: 01-empty` in same entry | stderr: "Duplicate screenshot name ... in entry ..."; exit 1 |
| Profile unset, entry doesn't require | `requires: none` (default) | Entry runs normally — some screens work without a profile |
| Masks out of bounds | TUI mask row 50 | stderr: "Mask row 50 out of range (0-29)"; exit 1 at parse |

### Test Examples

```bash
# Capture everything locally
export AUDIT_OUT=/tmp/ppds-audit-$(date +%s)
export PPDS_PROFILE=dev
export PPDS_ENV=test-env
node tools/audit-capture.mjs run all

# Validate without capturing
node tools/audit-capture.mjs validate tui

# List entries
node tools/audit-capture.mjs list extension
```

---

## Core Types

### Manifest (TypeScript-style)

```ts
interface Manifest {
  surface: "tui" | "extension";
  entries: Entry[];
}

interface Entry {
  id: string;                  // kebab-case, unique within surface
  title: string;
  requires?: "connected" | "none";  // default "none"
  steps: Step[];
  masks?: Mask[];
}

type Step =
  | { key: string }
  | { type: string }
  | { wait: { text?: string; ext?: string; timeout: number } }
  | { click: string; ext?: string }
  | { eval: string }
  | { command: string }
  | { screenshot: string }
  | { sleep: number };

type Mask =
  | { row: number; colStart: number; colEnd: number; reason: string }  // tui
  | { x: number; y: number; width: number; height: number; reason: string };  // extension
```

### Runner Output (per-entry `meta.json`)

Conforms to [`AUDIT-SCHEMA.md`](https://github.com/joshsmithxrm/ppds-design-system/blob/main/AUDIT-SCHEMA.md#metajson--per-capture). See that doc for the canonical schema; this spec only describes what values PPDS populates:

| Field | Value source |
|-------|--------------|
| `surfaceSpecific.serialize` (TUI) | Direct output of `terminal.serialize()` at capture time |
| `surfaceSpecific.font` (TUI) | `"Cascadia Mono"` (bundled WOFF2) |
| `surfaceSpecific.theme` (TUI) | `"ppds-dark"` — our locked palette |
| `surfaceSpecific.vscodeTheme` (ext) | Echoes the pinned `workbench.colorTheme` |
| `surfaceSpecific.panel` (ext) | The command-id the entry invoked, derived from `command` step |
| `masks` | Echo of manifest masks (with `reason`) |

---

## Design Decisions

### Why Playwright + xterm.js for TUI rendering?

**Context:** PNG rendering of terminal state is the hard requirement in this spec. Original brief suggested `agg` (asciinema GIF generator); we rejected it for reasons in the table below. Options evaluated:

| Approach | Fidelity | Deps | Cross-plat | Verdict |
|---|---|---|---|---|
| Playwright + xterm.js | High — xterm.js is VS Code's integrated terminal renderer | `@playwright/test` (have), `xterm` (new, pure JS, small) | Yes | **chosen** |
| `node-canvas` hand-rendered | Medium — reinvents cell layout + attr handling | `canvas` native (flaky on Windows) | Yes (painfully) | rejected |
| `agg` + `.cast` synthesis + GIF→PNG extract | Medium | Rust toolchain or GitHub release binary, plus ImageMagick/ffmpeg | Awkward on Windows CI | rejected |
| `aha`/`ansi2html` + Playwright | Medium — loses stateful attr tracking | `aha` C binary (Linux/Mac only), Playwright (have) | Partial | rejected |

**Decision:** Playwright drives a headless Chromium page that loads xterm.js and renders into its canvas. The runner/daemon reuses this page across captures to amortize browser startup.

**Consequences:**
- Positive: no new native deps; rendering engine is exactly what users see in VS Code's terminal; byte-stable output with bundled font.
- Negative: ~1–2 s warm-up per run (amortized across 15+ captures).

### Why convert `serialize()` shifts back to ANSI, instead of accessing tui-test's internal xterm?

**Context:** tui-test's `terminal.serialize()` returns `{ view, shifts }` — plain text + a coordinates→attributes map. Rendering needs ANSI or equivalent state. Three paths:

- (a) Re-emit SGR ANSI from shifts, write into a fresh xterm.js instance. Chosen.
- (b) Reach into tui-test internals to grab its underlying xterm, attach `@xterm/addon-serialize`. Brittle — tui-test's API is unstable (`@0.0.1-rc.5`).
- (c) Intercept raw node-pty stream and tee it to our own xterm. Most invasive; requires patching tui-test's spawn.

**Decision:** (a). Small helper (~50 lines) iterates shifts row by row, emits SGR before char, resets at row end. Deterministic, tui-test-version-independent.

### Why YAML manifests (not JSON)?

- Manifests will be hand-edited — YAML's comments, block strings, and looser syntax matter.
- Every other `.claude/*` data file in PPDS uses plain text or YAML-friendly shapes; staying consistent.

### Why the runner lives at repo root (`tools/audit-capture.mjs`)?

It orchestrates tools from *two* subsystem dirs (`tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs` and `src/PPDS.Extension/tools/webview-cdp.mjs`). Placing the runner inside one subsystem would imply ownership it doesn't have. `tools/` at the root is where cross-cutting scripts already live in practice for this kind of role.

### Why reject `$AUDIT_OUT` inside the repo?

- Captures are large (tens of MB per run). Gitignore patterns are easy to forget; accidental commits are worse than a clear "nope."
- CI pushes captures to a *different* repo (`ppds-v1-audit`) — making "inside repo" an error early catches misconfigurations.

### Why default-redact env + user principal?

- Constitution S3: no secret logging. Env names and user principals aren't technically secrets, but a public audit repo could leak tenant info that customers don't want indexed. Default opt-out is safer; set `AUDIT_REDACT=false` explicitly to capture raw values.

### Why no light-theme extension captures in v1?

- Doubles capture count for a binary that most PPDS users don't use in light mode.
- Manifest schema reserves a `themes: [dark, light]` field so adding it later is configuration-only — no runner change.

### Why not generate screen lists automatically from code?

- Considered: crawl `src/PPDS.Cli/Tui/Screens/` + `package.json` `contributes.commands` and synthesize manifests.
- Rejected: the manifest is the *design* of what a designer audits, not the raw inventory. Generated manifests would miss important states (empty vs loaded, dialog open, error state) that a human must declare. Keeping it hand-authored makes the manifest itself an intentional artifact.

---

## Extension Points

### Adding a new TUI screen capture

1. Add an entry to `.claude/audit-manifests/tui.yaml`:
   ```yaml
   - id: my-new-screen
     title: My New Screen — initial state
     requires: none
     steps:
       - key: alt+t
       - key: down
       - key: enter
       - wait: { text: "My New Screen", timeout: 5000 }
       - screenshot: 01-initial
   ```
2. Run `node tools/audit-capture.mjs validate tui` to verify the steps reach the screen.
3. Run `node tools/audit-capture.mjs run tui` for a real capture. No runner code changes.

### Adding a new extension panel capture

Same pattern against `.claude/audit-manifests/extension.yaml`. Use `command:` to open the panel, `wait:` on the extension id, then `screenshot:`.

### Adding a new surface (e.g., `mcp`)

Out of scope for this spec. Would require: a new manifest file, a new runner surface handler, and a matching verify tool for that surface.

---

## Related Specs

- [tui-verify-tool.md](./tui-verify-tool.md) — the PTY harness this extends
- [ext-verify-tool.md](./ext-verify-tool.md) — the VS Code webview harness this drives
- `ppds-design-system/AUDIT-SCHEMA.md` — the output contract

---

## Changelog

| Date | Change |
|------|--------|
| 2026-04-18 | Initial spec |

---

## Roadmap

- **Light-theme extension captures** — turn on `themes: [dark, light]` on extension entries; runner toggles via `workbench.action.selectTheme` between captures.
- **Auto-validation CI gate** — run `audit-capture validate tui` + `validate extension` in PR CI to catch manifest drift when UI changes.
- **Pixel-diff regression** — compare captures across commits (not in Phase 1; the audit workflow is the primary consumer for v1).
