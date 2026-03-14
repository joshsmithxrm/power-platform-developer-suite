# Data Explorer: Selection & Copy UX

**Status:** Draft
**Version:** 1.0
**Last Updated:** 2026-03-14
**Code:** [extension/src/panels/QueryPanel.ts](../extension/src/panels/QueryPanel.ts)

---

## Overview

The Data Explorer's query results grid has basic cell selection and copy, but it lacks the SSMS/Excel-style rectangular selection and smart copy behavior that Power Platform developers expect. This spec replaces the current arbitrary-cell-toggle model with an anchor+focus rectangular selection system, adds click+drag, Shift+click range extension, Ctrl+A select-all, and implements the TUI's smart copy semantics (headers toggled by Ctrl+Shift+C).

### Goals

- **Familiar selection UX**: Click, drag, Shift+click rectangular selection matching SSMS/Excel behavior
- **Smart copy defaults**: Single cell = value only; multi-cell = with headers; Ctrl+Shift+C inverts
- **Discoverable shortcuts**: Context menu and status bar hints teach users the available actions
- **Visual clarity**: Selection border edges (not just background) show the rectangular region

### Non-Goals

- Virtual scrolling (current record limits make this unnecessary)
- Column resize or reorder
- Cell editing
- Multi-region selection (Ctrl+click to add disjoint ranges)
- Keyboard arrow navigation for selection (Shift+Arrow to extend by one cell) — potential future enhancement

---

## Architecture

All changes are within the webview JS/CSS in `QueryPanel.ts`. No daemon or host-side changes needed.

```
QueryPanel.ts (webview)
├── Selection State: anchor {row, col}, focus {row, col}
├── Mouse Handlers: mousedown → set anchor, mousemove → update focus, mouseup → finalize
├── Keyboard Handlers: Ctrl+C, Ctrl+Shift+C, Ctrl+A, Escape
├── Context Menu: smart copy options based on selection state
├── Visual Rendering: CSS classes for selection background + edge borders
└── Status Bar Hints: dynamic text based on current selection
```

### Components

| Component | Responsibility |
|-----------|----------------|
| Selection state (`anchor`, `focus`) | Track rectangular selection as two corner coordinates |
| Mouse handlers | Click sets anchor, drag updates focus, Shift+click extends |
| `getSelectionRect()` | Normalize anchor/focus into {minRow, maxRow, minCol, maxCol} |
| `copySelection(withHeaders)` | Extract selected data as TSV, send to clipboard |
| `updateSelectionVisuals()` | Apply CSS classes for background + edge borders |
| Context menu | Right-click options with smart labeling |
| Status bar hint | Show copy shortcut hint based on selection size |

---

## Specification

### 1. Selection Model

#### Core Requirements

1. Selection is always a rectangle defined by `anchor` and `focus` coordinates (`{row, col}`)
2. Replace the current `selectedCells` Set with `anchor` and `focus` objects (or null when nothing selected)
3. `getSelectionRect()` normalizes anchor/focus into `{minRow, maxRow, minCol, maxCol}` regardless of drag direction

#### Mouse Interactions

| Action | Behavior |
|--------|----------|
| Click cell | Set anchor = focus = clicked cell. Clear previous selection. |
| Click + drag | mousedown sets anchor. mousemove updates focus (rectangular highlight follows). mouseup finalizes. |
| Shift+click | Keep existing anchor, set focus = clicked cell (extends/shrinks rectangle). |
| Click header (th) | Existing sort behavior (unchanged). |

#### Keyboard Interactions

| Shortcut | Behavior |
|----------|----------|
| Ctrl+A | Set anchor = {0, 0}, focus = {lastRow, lastCol}. Full table selected. For performance on large tables (5,000 rows), use a CSS class on tbody (`tbody.all-selected td`) instead of per-cell class application. |
| Escape | If filter bar is open: close filter bar only. If filter bar is closed and selection exists: clear selection. (Filter bar takes precedence — matches existing behavior.) |

#### Drag Details

- On `mousedown` on a `td[data-row]`: set anchor, begin tracking. Add `selecting` class to tbody (changes cursor to `cell`, disables text selection).
- On `mousemove` (while tracking): update focus to the cell under the mouse. Call `updateSelectionVisuals()`.
- On `mouseup`: stop tracking. Remove `selecting` class from tbody. **Remove the mousemove listener** (attach it on mousedown, remove on mouseup — do not leave a permanent global mousemove handler).
- If mouse leaves the results wrapper during drag, clamp focus to nearest edge cell.

### 2. Copy Behavior

#### Smart Defaults (matching TUI)

| Selection | Ctrl+C (default) | Ctrl+Shift+C (inverted) |
|-----------|-------------------|--------------------------|
| Single cell | Copy displayed (formatted) value only | Copy value with column header above |
| Multi-cell | Copy selected rectangle WITH column headers | Copy selected rectangle WITHOUT headers |
| Full table (Ctrl+A) | Copy all data WITH headers | Copy all data WITHOUT headers |

#### Format

- Tab-separated values (TSV) with `\n` row separators
- Values sanitized: tabs replaced with spaces, newlines replaced with spaces
- All copy uses the **displayed (formatted) value** — same as what `renderTable` shows in the grid. For cells with `{value, formatted}` objects, the formatted string is used. This matches what users see.
- Only columns within the selection rectangle are included (not all columns)
- **Selection coordinates are relative to the currently rendered view** (which may be filtered or sorted), not the raw `allRows` array. The copy function reads from the same row array that `renderTable` used.

#### Post-Copy Feedback

After a successful copy, the status bar briefly shows confirmation matching the TUI pattern:
- Single cell: `"Copied: {truncated_value}"`
- Multi-cell: `"Copied {rowCount} rows x {colCount} cols with headers"` (or "without headers")

The confirmation replaces the copy hint for 2 seconds, then the hint returns.

#### Clipboard

- Use `vscode.postMessage({ command: 'copyToClipboard', text })` (existing pattern)
- Host side calls `vscode.env.clipboard.writeText(text)` (existing)

### 3. Context Menu

Right-click on a cell shows:

```
Copy                    Ctrl+C
Copy (no headers)       Ctrl+Shift+C     ← when multi-cell selected
  OR
Copy (with header)      Ctrl+Shift+C     ← when single cell selected
─────────────────────────────────────
Copy Cell Value
Copy Row
Copy All Results
```

- **Copy**: Smart default (same as Ctrl+C)
- **Copy (no headers)** / **Copy (with header)**: The inverse of the default, labeled dynamically
- **Copy Cell Value**: Always the raw value of the right-clicked cell (ignores selection)
- **Copy Row**: Full row of the right-clicked cell as TSV (no headers)
- **Copy All Results**: Entire table with headers

If right-clicking a cell outside the current selection, the selection moves to that cell first (single-cell select), then the menu opens.

### 4. Visual Styling

#### Selection Background

```css
td.cell-selected {
    background: var(--vscode-editor-selectionBackground) !important;
}
```

#### Selection Edge Borders

```css
td.cell-selected-top {
    border-top: 2px solid var(--vscode-focusBorder) !important;
}
td.cell-selected-bottom {
    border-bottom: 2px solid var(--vscode-focusBorder) !important;
}
td.cell-selected-left {
    border-left: 2px solid var(--vscode-focusBorder) !important;
}
td.cell-selected-right {
    border-right: 2px solid var(--vscode-focusBorder) !important;
}
```

Edge classes are applied only to cells on the perimeter of the selection rectangle. Interior cells get `cell-selected` only (background, no border).

#### Drag Cursor

```css
tbody.selecting {
    cursor: cell !important;
    user-select: none !important;
}
```

### 5. Status Bar Hints

The status bar already has `statusText`, `rowCountEl`, and `executionTimeEl` spans. Add a `copyHintEl` span that shows context-sensitive hints:

| Selection State | Hint Text |
|-----------------|-----------|
| No selection | (empty) |
| Single cell selected | `Ctrl+C: copy value | Ctrl+Shift+C: with header` |
| Multi-cell selected | `Ctrl+C: copy with headers | Ctrl+Shift+C: values only` |

The hint updates whenever the selection changes.

---

## Acceptance Criteria

| ID | Criterion | Test | Status |
|----|-----------|------|--------|
| AC-01 | Click cell selects single cell with anchor = focus | Manual | 🔲 |
| AC-02 | Click + drag creates rectangular selection from anchor to current cell | Manual | 🔲 |
| AC-03 | Shift+click extends selection from existing anchor | Manual | 🔲 |
| AC-04 | Ctrl+A selects entire table | Manual | 🔲 |
| AC-05 | Escape clears selection | Manual | 🔲 |
| AC-06 | Ctrl+C on single cell copies displayed (formatted) value (no header) | Manual | 🔲 |
| AC-07 | Ctrl+C on multi-cell copies with headers as TSV | Manual | 🔲 |
| AC-08 | Ctrl+Shift+C inverts header behavior | Manual | 🔲 |
| AC-09 | Context menu shows smart copy options with correct labels | Manual | 🔲 |
| AC-10 | Context menu "Copy Cell Value" copies right-clicked cell value | Manual | 🔲 |
| AC-11 | Selection shows background highlight + edge borders on perimeter | Manual | 🔲 |
| AC-12 | Cursor changes to cell during drag | Manual | 🔲 |
| AC-13 | Status bar shows copy hint based on selection state | Manual | 🔲 |
| AC-14 | Right-clicking outside selection moves selection to that cell first | Manual | 🔲 |
| AC-15 | Only columns within selection rectangle are copied (not all columns) | Manual | 🔲 |
| AC-16 | Values with tabs/newlines are sanitized (replaced with spaces) in copy output | `getSelectionRect` / `copySelection` unit tests | 🔲 |
| AC-17 | Copy on filtered/sorted view uses displayed row order, not raw allRows | Manual | 🔲 |
| AC-18 | Post-copy confirmation appears in status bar for 2 seconds | Manual | 🔲 |
| AC-19 | Escape with filter bar open closes filter bar only (does not clear selection) | Manual | 🔲 |

**Testing approach:** Most ACs require manual verification since they involve webview DOM interaction. However, the pure-function logic (`getSelectionRect()` normalization, `copySelection()` TSV formatting, value sanitization) should be extracted as testable utility functions and covered with Vitest unit tests (AC-16). Mouse/keyboard interaction is manual-only.

### Edge Cases

| Scenario | Expected Behavior |
|----------|-------------------|
| Drag starts on header (th) | No selection started — header click triggers sort |
| Drag outside results wrapper | Clamp focus to nearest edge cell |
| Copy with no selection | No-op (existing behavior) |
| Copy single cell with null/undefined value | Copy empty string |
| Ctrl+A with no results | No-op |
| Selection persists after re-sort | Selection cleared on sort (anchor/focus reset) |
| Selection persists after filter | Selection cleared on filter change |
| Selection persists after new query | Selection cleared on new results |
| Selection persists after Load More | Selection cleared on append results |
| Copy while filtered | Copies from filtered/sorted view, not raw allRows |
| Escape with filter open + selection | Closes filter bar only; second Escape clears selection |

---

## Design Decisions

### Why anchor+focus model over arbitrary cell Set?

**Context:** The current implementation uses a `Set<string>` of cell IDs (`"row:col"`), allowing arbitrary non-rectangular selections via Ctrl+click toggling.

**Decision:** Replace with anchor+focus coordinates defining a rectangle.

**Alternatives considered:**
- Keep Set model and add drag: Would need to enumerate all cells in the rectangle on every mousemove, creating O(rows*cols) operations per frame. The anchor+focus model only stores two points and computes the rectangle on demand.
- Multi-region selection (Ctrl+click adds disjoint rectangles): Over-engineering for a data explorer. SSMS doesn't support it. Excel does, but it's rarely used for copy-paste workflows.

**Consequences:**
- Positive: Simpler state, faster updates, natural drag/Shift+click behavior
- Negative: Can't select non-contiguous cells (acceptable — not a real use case for query results)

### Why smart defaults matching TUI?

**Context:** Need to decide when headers are included in copy.

**Decision:** Single cell = no header by default; multi-cell = headers by default. Ctrl+Shift+C inverts.

**Rationale:** When you copy a single cell, you want the value (to paste into a filter, a URL, a variable). When you copy a range, you're building a dataset (paste into Excel, share with someone) and headers provide context. This matches the TUI's design in `TableCopyHelper.cs` and the self-teaching status bar hint pattern.

---

## Related Specs

- [vscode-persistence-and-solutions-polish.md](./vscode-persistence-and-solutions-polish.md) — Parallel work on the same extension
- TUI copy design: `docs/plans/2026-02-08-copy-paste-design.md`
- TUI implementation: `src/PPDS.Cli/Tui/Helpers/TableCopyHelper.cs`
