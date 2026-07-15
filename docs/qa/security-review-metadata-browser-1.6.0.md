# Security Review — Metadata Browser round 2 (stable 1.6.0 train)

- **Date:** 2026-07-15
- **Reviewer:** Claude Opus 4.8 (security-review skill) + adversarial verification sub-agent
- **Release train:** Extension 1.6.0 (stable), Cli 1.4.0, Dataverse 1.3.0, Mcp 1.2.0
- **Scope (immutable reviewed delta):** `git diff 85e1328ff..3534a06f3` over
  `src/PPDS.Cli`, `src/PPDS.Dataverse`, `src/PPDS.Mcp`, `src/PPDS.Extension`
  (14 files, +718/-113), where `85e1328ff` = tag `Extension-v1.4.1` (last stable)
  and `3534a06f3` = the round-2 merge on `main` (#1373) that this review signs off.
  Covers PR #1366 (scroll fix) lineage and #1373 (metadata browser round 2:
  Choices tab, wire fidelity, mark-don't-mask auxiliaries, tab order).

## Result: PASS — no HIGH or MEDIUM findings

No concretely-exploitable vulnerability was identified at ≥8/10 confidence. The two
plausible attack surfaces were traced and both hold up.

## Surfaces examined

### 1. Webview XSS — not exploitable

The DataTable cell trust boundary is `td.innerHTML = col.render(item)`
(`data-table.ts:335`), so column `render` functions must escape.

- **New auxiliary Display Name render** (`metadata-browser-panel.ts`): emits
  `'<span class="attr-aux-label">aux of ' + escapeHtml(a.attributeOf ?? '') + '</span>'`.
  `attributeOf` passes through `escapeHtml` (`dom-utils.ts`, a correct entity encoder for
  `& < > " '`); the `<span>`/class are static literals. Dataverse logical names (the
  source of `attributeOf`) are constrained to `[a-z0-9_]`, so markup cannot be smuggled
  in even before escaping. Closed.
- **Rewritten properties panel** (`showAttributePropertiesPanel`): all ~40 pass-through
  fields (incl. `formulaDefinition`, `externalName`, descriptions, dates) are written via
  `textContent`, which cannot execute markup. Safer than the surrounding legacy code.
- **`rowClassName`** (`data-table.ts`): concatenated into `tr.className` (not innerHTML);
  the only caller returns the static literal `'attr-aux'` or `null`. No attribute injection.
- **`toggleOptionValues` colSpan**: computed integer assigned to a numeric DOM property.

### 2. Data exposure via widened RPC — not a new exposure

The ~18 added `MetadataAttributeDto` fields are **entity schema metadata**, not row data.
The daemon RPC is localhost (extension ↔ CLI daemon) for the same authenticated
user/token. Every field is sourced from the standard `AttributeMetadata` the same caller
can already retrieve directly (`ppds metadata attributes`, MCP, or a raw metadata request).
No cross-tenant/cross-user content, no PII, no secrets. `formulaDefinition` is a
calculated-column authoring artifact; audit fields are boolean flags, not audit records.
The widening changes only which already-authorized metadata crosses an internal boundary.

### 3. CLI / TUI — clean

- `AttributesCommand.cs` `--exclude-auxiliary`: a `bool` driving `.Where(a => a.AttributeOf == null)`; the `aux:{AttributeOf}` flag is written to stdout as data (no shell/eval).
- `MetadataExplorerScreen.cs`: `aux of {AttributeOf}` into a Terminal.Gui data cell (rendered, not interpreted).

## Exclusions applied

Per the review rubric: DoS/resource-exhaustion, on-disk secrets, rate limiting, outdated
third-party libraries, and test-only files were out of scope. No findings were suppressed
under these exclusions — none were identified in the first place.

## Sign-off

Delta is safe to ship to the stable channel. Gate satisfied for the 1.6.0 train.
