# Data Explorer Selection & Copy — Implementation Plan

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the arbitrary cell-toggle selection model in the Data Explorer with SSMS-style anchor+focus rectangular selection, click+drag, smart copy with header toggling, and an improved context menu.

**Architecture:** All changes are within the webview JS/CSS embedded in `QueryPanel.ts`. The selection state transitions from a `Set<string>` of cell IDs to two coordinate objects (`anchor` and `focus`). A `displayedRows` variable tracks the currently rendered (filtered/sorted) rows so copy always reads from what the user sees. Pure-function utilities (`getSelectionRect`, `getCellDisplayValue`, `sanitizeValue`) are extracted for testability.

**Tech Stack:** TypeScript (VS Code extension), inline webview JS/CSS, Vitest for pure-function unit tests

**Spec:** [`specs/vscode-data-explorer-selection-copy.md`](../../../specs/vscode-data-explorer-selection-copy.md)

---

## File Map

| Action | File | Responsibility |
|--------|------|----------------|
| Modify | `extension/src/panels/QueryPanel.ts` | All webview changes: CSS, selection state, mouse/keyboard handlers, copy logic, context menu, status bar |
| Create | `extension/src/__tests__/panels/querySelectionUtils.test.ts` | Unit tests for pure-function utilities (getSelectionRect, sanitizeValue, buildTsv) |

---

## Chunk 1: Selection Foundation

### Task 1: Selection CSS + State Variables + getSelectionRect

**Files:**
- Modify: `extension/src/panels/QueryPanel.ts` (CSS block ~lines 356-388, JS state ~line 458)

- [ ] **Step 1: Add selection CSS styles**

In the `<style>` block of `getHtmlContent()`, replace the existing `.results-table td.selected` rule (line 372):

```css
    .results-table td.selected { background: var(--vscode-editor-selectionBackground); }
```

With the full selection CSS block:

```css
    .results-table td.cell-selected { background: var(--vscode-editor-selectionBackground) !important; }
    .results-table td.cell-selected-top { border-top: 2px solid var(--vscode-focusBorder) !important; }
    .results-table td.cell-selected-bottom { border-bottom: 2px solid var(--vscode-focusBorder) !important; }
    .results-table td.cell-selected-left { border-left: 2px solid var(--vscode-focusBorder) !important; }
    .results-table td.cell-selected-right { border-right: 2px solid var(--vscode-focusBorder) !important; }
    tbody.selecting { cursor: cell !important; user-select: none !important; }
    tbody.all-selected td { background: var(--vscode-editor-selectionBackground) !important; }
```

- [ ] **Step 2: Add status bar hint span to HTML**

In the status bar HTML (line 424-428), add a `copyHintEl` span:

```html
<div class="status-bar">
    <span id="status-text">Ready</span>
    <span id="row-count"></span>
    <span id="execution-time"></span>
    <span class="toolbar-spacer"></span>
    <span id="copy-hint"></span>
</div>
```

Note: add a `.toolbar-spacer` span before `copy-hint` so the hint is right-aligned.

- [ ] **Step 3: Replace selection state variables**

In the webview JS, replace:
```javascript
    let selectedCells = new Set();
```

With:
```javascript
    let anchor = null;   // {row, col} or null
    let focus = null;    // {row, col} or null
    let isDragging = false;
    let displayedRows = []; // tracks the rows array last passed to renderTable
    const copyHintEl = document.getElementById('copy-hint');
```

- [ ] **Step 4: Add pure utility functions**

Add these utility functions in the webview JS, before the button handlers section:

```javascript
    // ── Selection utilities ──
    function getSelectionRect() {
        if (!anchor || !focus) return null;
        return {
            minRow: Math.min(anchor.row, focus.row),
            maxRow: Math.max(anchor.row, focus.row),
            minCol: Math.min(anchor.col, focus.col),
            maxCol: Math.max(anchor.col, focus.col),
        };
    }

    function isSingleCell() {
        if (!anchor || !focus) return false;
        return anchor.row === focus.row && anchor.col === focus.col;
    }

    function clearSelection() {
        anchor = null;
        focus = null;
        const tbody = resultsWrapper.querySelector('tbody');
        if (tbody) tbody.classList.remove('all-selected');
        resultsWrapper.querySelectorAll('td').forEach(td => {
            td.classList.remove('cell-selected', 'cell-selected-top', 'cell-selected-bottom', 'cell-selected-left', 'cell-selected-right');
        });
        copyHintEl.textContent = '';
    }

    function getCellDisplayValue(row, colIdx) {
        const key = columns[colIdx].alias || columns[colIdx].logicalName;
        const rawVal = row ? row[key] : undefined;
        if (rawVal === null || rawVal === undefined) return '';
        if (typeof rawVal === 'object' && 'formatted' in rawVal) return String(rawVal.formatted || rawVal.value || '');
        if (typeof rawVal === 'object' && 'entityId' in rawVal) return String(rawVal.formatted || rawVal.value || '');
        return String(rawVal);
    }

    function sanitizeValue(val) {
        return val.replace(/\\t/g, ' ').replace(/\\r?\\n/g, ' ');
    }

    function updateCopyHint() {
        if (!anchor) { copyHintEl.textContent = ''; return; }
        if (isSingleCell()) {
            copyHintEl.textContent = 'Ctrl+C: copy value | Ctrl+Shift+C: with header';
        } else {
            copyHintEl.textContent = 'Ctrl+C: copy with headers | Ctrl+Shift+C: values only';
        }
    }
```

- [ ] **Step 5: Update renderTable to track displayedRows and clear selection**

In the `renderTable` function, add at the very start:
```javascript
    function renderTable(rows) {
        displayedRows = rows;
        clearSelection();
```

Also update the `td` rendering to remove the old `isSelected` logic. Change:
```javascript
                const cellId = rowIdx + ':' + colIdx;
                const isSelected = selectedCells.has(cellId) ? ' selected' : '';
```
```javascript
                html += '<td class="' + isSelected + '" data-row="' + rowIdx + '" data-col="' + colIdx + '">' + escapeHtml(display) + '</td>';
```

To:
```javascript
                html += '<td data-row="' + rowIdx + '" data-col="' + colIdx + '">' + escapeHtml(display) + '</td>';
```

(Remove the `cellId` and `isSelected` lines entirely.)

- [ ] **Step 6: Update handleQueryResult and handleAppendResults to clear selection**

In `handleQueryResult`, replace `selectedCells.clear();` with `clearSelection();` (already handled by renderTable, but keep explicit for clarity — actually just remove the `selectedCells.clear()` line since `renderTable` now calls `clearSelection()`).

In `handleAppendResults`, add `clearSelection();` before `renderTable(allRows);` (renderTable already calls it, so just remove any old selectedCells references).

- [ ] **Step 7: Verify compilation**

Run: `cd extension && npx tsc --noEmit`
Expected: No errors

- [ ] **Step 8: Commit**

```bash
git add extension/src/panels/QueryPanel.ts
git commit -m "refactor(extension): replace cell Set with anchor/focus selection state and CSS"
```

### Task 2: Click + Shift+click + updateSelectionVisuals

**Files:**
- Modify: `extension/src/panels/QueryPanel.ts`

- [ ] **Step 1: Implement updateSelectionVisuals**

Add this function after the utility functions:

```javascript
    function updateSelectionVisuals() {
        const rect = getSelectionRect();
        const tbody = resultsWrapper.querySelector('tbody');
        if (!tbody) return;

        // Fast path for Ctrl+A (full table)
        tbody.classList.remove('all-selected');
        if (rect && rect.minRow === 0 && rect.minCol === 0 &&
            rect.maxRow === displayedRows.length - 1 && rect.maxCol === columns.length - 1) {
            tbody.classList.add('all-selected');
            updateCopyHint();
            return;
        }

        // Clear all selection classes
        tbody.querySelectorAll('td').forEach(td => {
            td.classList.remove('cell-selected', 'cell-selected-top', 'cell-selected-bottom', 'cell-selected-left', 'cell-selected-right');
        });

        if (!rect) { updateCopyHint(); return; }

        // Apply selection classes
        for (let r = rect.minRow; r <= rect.maxRow; r++) {
            for (let c = rect.minCol; c <= rect.maxCol; c++) {
                const td = tbody.querySelector('td[data-row="' + r + '"][data-col="' + c + '"]');
                if (!td) continue;
                td.classList.add('cell-selected');
                if (r === rect.minRow) td.classList.add('cell-selected-top');
                if (r === rect.maxRow) td.classList.add('cell-selected-bottom');
                if (c === rect.minCol) td.classList.add('cell-selected-left');
                if (c === rect.maxCol) td.classList.add('cell-selected-right');
            }
        }
        updateCopyHint();
    }
```

- [ ] **Step 2: Replace the click handler**

Replace the existing delegated click handler (lines ~498-516):

```javascript
    resultsWrapper.addEventListener('click', (e) => {
        const th = e.target.closest('th[data-col]');
        if (th) {
            const col = parseInt(th.dataset.col);
            if (sortColumn === col) { sortAsc = !sortAsc; }
            else { sortColumn = col; sortAsc = true; }
            clearSelection();
            sortAndRender();
            return;
        }
        const td = e.target.closest('td[data-row]');
        if (td) {
            const cellId = td.dataset.row + ':' + td.dataset.col;
            if (!e.ctrlKey && !e.metaKey) selectedCells.clear();
            if (selectedCells.has(cellId)) selectedCells.delete(cellId);
            else selectedCells.add(cellId);
            updateCellSelection();
        }
    });
```

With:

```javascript
    // ── Click handler (sort headers + cell selection) ──
    resultsWrapper.addEventListener('mousedown', (e) => {
        const th = e.target.closest('th[data-col]');
        if (th) {
            const col = parseInt(th.dataset.col);
            if (sortColumn === col) { sortAsc = !sortAsc; }
            else { sortColumn = col; sortAsc = true; }
            clearSelection();
            sortAndRender();
            return;
        }
        const td = e.target.closest('td[data-row]');
        if (!td) return;

        const row = parseInt(td.dataset.row);
        const col = parseInt(td.dataset.col);

        if (e.shiftKey && anchor) {
            // Shift+click: extend selection from anchor
            focus = { row, col };
        } else {
            // Normal click: new single-cell selection + start drag tracking
            anchor = { row, col };
            focus = { row, col };
            isDragging = true;
            const tbody = resultsWrapper.querySelector('tbody');
            if (tbody) tbody.classList.add('selecting');

            const onMouseMove = (ev) => {
                const target = ev.target.closest ? ev.target.closest('td[data-row]') : null;
                if (target) {
                    focus = { row: parseInt(target.dataset.row), col: parseInt(target.dataset.col) };
                    updateSelectionVisuals();
                }
            };
            const onMouseUp = () => {
                isDragging = false;
                const tb = resultsWrapper.querySelector('tbody');
                if (tb) tb.classList.remove('selecting');
                document.removeEventListener('mousemove', onMouseMove);
                document.removeEventListener('mouseup', onMouseUp);
            };
            document.addEventListener('mousemove', onMouseMove);
            document.addEventListener('mouseup', onMouseUp);
        }
        updateSelectionVisuals();
    });
```

- [ ] **Step 3: Remove old updateCellSelection function**

Delete the entire `updateCellSelection()` function (lines ~727-731) — it's replaced by `updateSelectionVisuals()`.

- [ ] **Step 4: Verify compilation**

Run: `cd extension && npx tsc --noEmit`
Expected: No errors

- [ ] **Step 5: Commit**

```bash
git add extension/src/panels/QueryPanel.ts
git commit -m "feat(extension): add anchor/focus click, Shift+click, and drag selection"
```

### Task 3: Keyboard Shortcuts (Ctrl+A, Escape, Ctrl+C, Ctrl+Shift+C)

**Files:**
- Modify: `extension/src/panels/QueryPanel.ts`

- [ ] **Step 1: Rewrite keyboard handler**

Replace the existing `document.addEventListener('keydown', ...)` block (lines ~527-558) with:

```javascript
    document.addEventListener('keydown', (e) => {
        // Filter toggle
        if (e.key === '/' && document.activeElement !== sqlEditor && document.activeElement !== filterInput) {
            e.preventDefault();
            toggleFilter();
            return;
        }

        // Escape: filter bar takes precedence, then selection
        if (e.key === 'Escape') {
            if (filterBar.classList.contains('visible')) {
                hideFilter();
            } else if (anchor) {
                clearSelection();
            }
            return;
        }

        // Ctrl+A: select all (when not in text inputs)
        if ((e.ctrlKey || e.metaKey) && e.key === 'a' &&
            document.activeElement !== sqlEditor &&
            document.activeElement !== filterInput) {
            if (displayedRows.length > 0 && columns.length > 0) {
                e.preventDefault();
                anchor = { row: 0, col: 0 };
                focus = { row: displayedRows.length - 1, col: columns.length - 1 };
                updateSelectionVisuals();
            }
            return;
        }

        // Ctrl+C / Ctrl+Shift+C: copy selection
        if ((e.ctrlKey || e.metaKey) && e.key === 'c' &&
            document.activeElement !== sqlEditor &&
            document.activeElement !== filterInput) {
            if (anchor) {
                e.preventDefault();
                copySelection(e.shiftKey);
            }
            return;
        }

        // Ctrl+Shift+F: FetchXML preview
        if ((e.ctrlKey || e.metaKey) && e.shiftKey && e.key === 'F') {
            e.preventDefault();
            const sql = sqlEditor.value.trim();
            if (sql) vscode.postMessage({ command: 'showFetchXml', sql });
            return;
        }

        // Ctrl+E: Export
        if ((e.ctrlKey || e.metaKey) && !e.shiftKey && e.key === 'e') {
            e.preventDefault();
            vscode.postMessage({ command: 'exportResults' });
            return;
        }

        // Ctrl+Shift+H: History
        if ((e.ctrlKey || e.metaKey) && e.shiftKey && e.key === 'H') {
            e.preventDefault();
            vscode.postMessage({ command: 'showHistory' });
        }
    });
```

- [ ] **Step 2: Implement copySelection function**

Replace the old `copySelectedCells` function (lines ~734-768) with:

```javascript
    function copySelection(invertHeaders) {
        if (!anchor) return;
        const rect = getSelectionRect();
        if (!rect) return;

        const single = isSingleCell();
        // Smart default: single cell = no headers; multi-cell = headers. invertHeaders flips it.
        const withHeaders = single ? invertHeaders : !invertHeaders;

        let text = '';

        // Headers
        if (withHeaders) {
            const headers = [];
            for (let c = rect.minCol; c <= rect.maxCol; c++) {
                headers.push(columns[c].alias || columns[c].logicalName);
            }
            text += headers.join('\\t') + '\\n';
        }

        // Data rows
        for (let r = rect.minRow; r <= rect.maxRow; r++) {
            const vals = [];
            for (let c = rect.minCol; c <= rect.maxCol; c++) {
                vals.push(sanitizeValue(getCellDisplayValue(displayedRows[r], c)));
            }
            text += vals.join('\\t') + '\\n';
        }

        vscode.postMessage({ command: 'copyToClipboard', text: text.trim() });

        // Post-copy feedback in status bar
        showCopyFeedback(rect, single, withHeaders);
    }

    function showCopyFeedback(rect, single, withHeaders) {
        if (single) {
            const val = getCellDisplayValue(displayedRows[rect.minRow], rect.minCol);
            const truncated = val.length > 40 ? val.substring(0, 40) + '...' : val;
            copyHintEl.textContent = 'Copied: ' + truncated;
        } else {
            const rowCount = rect.maxRow - rect.minRow + 1;
            const colCount = rect.maxCol - rect.minCol + 1;
            copyHintEl.textContent = 'Copied ' + rowCount + ' rows x ' + colCount + ' cols ' + (withHeaders ? 'with headers' : 'without headers');
        }
        // Restore hint after 2 seconds
        setTimeout(() => { updateCopyHint(); }, 2000);
    }
```

- [ ] **Step 3: Verify compilation**

Run: `cd extension && npx tsc --noEmit`
Expected: No errors

- [ ] **Step 4: Commit**

```bash
git add extension/src/panels/QueryPanel.ts
git commit -m "feat(extension): add Ctrl+A, Escape, smart copy with Ctrl+Shift+C header toggle"
```

---

## Chunk 2: Context Menu + Tests

### Task 4: Context Menu Overhaul

**Files:**
- Modify: `extension/src/panels/QueryPanel.ts`

- [ ] **Step 1: Replace context menu**

Replace the entire context menu block (lines ~771-837) with:

```javascript
    // ── Right-click context menu ──
    let contextMenu = null;
    document.addEventListener('contextmenu', (e) => {
        const td = e.target.closest('td[data-row]');
        if (!td) return;
        e.preventDefault();
        removeContextMenu();

        const clickRow = parseInt(td.dataset.row);
        const clickCol = parseInt(td.dataset.col);

        // If right-clicked cell is outside current selection, move selection there
        const rect = getSelectionRect();
        if (!rect || clickRow < rect.minRow || clickRow > rect.maxRow ||
            clickCol < rect.minCol || clickCol > rect.maxCol) {
            anchor = { row: clickRow, col: clickCol };
            focus = { row: clickRow, col: clickCol };
            updateSelectionVisuals();
        }

        const single = isSingleCell();
        const inverseLabel = single ? 'Copy (with header)' : 'Copy (no headers)';

        contextMenu = document.createElement('div');
        contextMenu.className = 'context-menu';

        const items = [
            { label: 'Copy', shortcut: 'Ctrl+C', action: 'copy' },
            { label: inverseLabel, shortcut: 'Ctrl+Shift+C', action: 'copyInverse' },
            { label: '─', action: 'separator' },
            { label: 'Copy Cell Value', shortcut: '', action: 'cell' },
            { label: 'Copy Row', shortcut: '', action: 'row' },
            { label: 'Copy All Results', shortcut: '', action: 'all' },
        ];

        let html = '';
        for (const item of items) {
            if (item.action === 'separator') {
                html += '<div style="border-top: 1px solid var(--vscode-menu-separatorBackground, var(--vscode-panel-border)); margin: 4px 0;"></div>';
            } else {
                html += '<div class="context-menu-item" data-action="' + item.action + '">';
                html += '<span>' + item.label + '</span>';
                if (item.shortcut) html += '<span style="margin-left:auto;padding-left:24px;opacity:0.6;font-size:11px;">' + item.shortcut + '</span>';
                html += '</div>';
            }
        }
        contextMenu.innerHTML = html;

        // Make items flex for shortcut alignment
        contextMenu.querySelectorAll('.context-menu-item').forEach(el => {
            el.style.display = 'flex';
            el.style.alignItems = 'center';
        });

        contextMenu.style.left = e.clientX + 'px';
        contextMenu.style.top = e.clientY + 'px';
        document.body.appendChild(contextMenu);

        contextMenu.addEventListener('click', (ev) => {
            const actionEl = ev.target.closest('[data-action]');
            if (!actionEl) return;
            const action = actionEl.dataset.action;

            if (action === 'copy') {
                copySelection(false);
            } else if (action === 'copyInverse') {
                copySelection(true);
            } else if (action === 'cell') {
                const val = getCellDisplayValue(displayedRows[clickRow], clickCol);
                vscode.postMessage({ command: 'copyToClipboard', text: sanitizeValue(val) });
                copyHintEl.textContent = 'Copied: ' + (val.length > 40 ? val.substring(0, 40) + '...' : val);
                setTimeout(() => { updateCopyHint(); }, 2000);
            } else if (action === 'row') {
                const vals = [];
                for (let c = 0; c < columns.length; c++) {
                    vals.push(sanitizeValue(getCellDisplayValue(displayedRows[clickRow], c)));
                }
                vscode.postMessage({ command: 'copyToClipboard', text: vals.join('\\t') });
                copyHintEl.textContent = 'Copied row';
                setTimeout(() => { updateCopyHint(); }, 2000);
            } else if (action === 'all') {
                const headers = columns.map(c => c.alias || c.logicalName);
                let text = headers.join('\\t') + '\\n';
                displayedRows.forEach(row => {
                    const vals = [];
                    for (let c = 0; c < columns.length; c++) {
                        vals.push(sanitizeValue(getCellDisplayValue(row, c)));
                    }
                    text += vals.join('\\t') + '\\n';
                });
                vscode.postMessage({ command: 'copyToClipboard', text: text.trim() });
                copyHintEl.textContent = 'Copied all ' + displayedRows.length + ' rows';
                setTimeout(() => { updateCopyHint(); }, 2000);
            }
            removeContextMenu();
        });
    });
    document.addEventListener('click', (e) => {
        if (contextMenu && !contextMenu.contains(e.target)) removeContextMenu();
    });
    function removeContextMenu() {
        if (contextMenu) { contextMenu.remove(); contextMenu = null; }
    }
```

- [ ] **Step 2: Verify compilation**

Run: `cd extension && npx tsc --noEmit`
Expected: No errors

- [ ] **Step 3: Commit**

```bash
git add extension/src/panels/QueryPanel.ts
git commit -m "feat(extension): replace context menu with smart copy options and shortcuts"
```

### Task 5: Unit Tests for Pure Functions

**Files:**
- Create: `extension/src/__tests__/panels/querySelectionUtils.test.ts`

Note: Since the pure functions are embedded in the webview IIFE, we can't import them directly. To make them testable, extract them into a separate module.

- [ ] **Step 1: Create selection utilities module**

Create `extension/src/panels/querySelectionUtils.ts`:

```typescript
/**
 * Pure utility functions for query results selection and copy.
 * Extracted from QueryPanel webview JS for testability.
 * These are duplicated in the webview IIFE — keep in sync.
 */

export interface SelectionRect {
    minRow: number;
    maxRow: number;
    minCol: number;
    maxCol: number;
}

export interface CellCoord {
    row: number;
    col: number;
}

export function getSelectionRect(anchor: CellCoord | null, focus: CellCoord | null): SelectionRect | null {
    if (!anchor || !focus) return null;
    return {
        minRow: Math.min(anchor.row, focus.row),
        maxRow: Math.max(anchor.row, focus.row),
        minCol: Math.min(anchor.col, focus.col),
        maxCol: Math.max(anchor.col, focus.col),
    };
}

export function isSingleCell(anchor: CellCoord | null, focus: CellCoord | null): boolean {
    if (!anchor || !focus) return false;
    return anchor.row === focus.row && anchor.col === focus.col;
}

export function sanitizeValue(val: string): string {
    return val.replace(/\t/g, ' ').replace(/\r?\n/g, ' ');
}

export function buildTsv(
    rows: Record<string, unknown>[],
    columns: { alias: string | null; logicalName: string }[],
    rect: SelectionRect,
    withHeaders: boolean,
    getDisplayValue: (row: Record<string, unknown>, colIdx: number) => string,
): string {
    let text = '';
    if (withHeaders) {
        const headers: string[] = [];
        for (let c = rect.minCol; c <= rect.maxCol; c++) {
            headers.push(columns[c].alias || columns[c].logicalName);
        }
        text += headers.join('\t') + '\n';
    }
    for (let r = rect.minRow; r <= rect.maxRow; r++) {
        const vals: string[] = [];
        for (let c = rect.minCol; c <= rect.maxCol; c++) {
            vals.push(sanitizeValue(getDisplayValue(rows[r], c)));
        }
        text += vals.join('\t') + '\n';
    }
    return text.trimEnd();
}
```

- [ ] **Step 2: Write tests**

Create `extension/src/__tests__/panels/querySelectionUtils.test.ts`:

```typescript
import { describe, it, expect } from 'vitest';
import { getSelectionRect, isSingleCell, sanitizeValue, buildTsv } from '../../panels/querySelectionUtils.js';

describe('getSelectionRect', () => {
    it('returns null when anchor is null', () => {
        expect(getSelectionRect(null, { row: 0, col: 0 })).toBeNull();
    });

    it('returns null when focus is null', () => {
        expect(getSelectionRect({ row: 0, col: 0 }, null)).toBeNull();
    });

    it('normalizes when anchor is top-left', () => {
        const rect = getSelectionRect({ row: 1, col: 2 }, { row: 3, col: 4 });
        expect(rect).toEqual({ minRow: 1, maxRow: 3, minCol: 2, maxCol: 4 });
    });

    it('normalizes when anchor is bottom-right (drag up-left)', () => {
        const rect = getSelectionRect({ row: 5, col: 4 }, { row: 2, col: 1 });
        expect(rect).toEqual({ minRow: 2, maxRow: 5, minCol: 1, maxCol: 4 });
    });

    it('handles single cell (anchor = focus)', () => {
        const rect = getSelectionRect({ row: 3, col: 2 }, { row: 3, col: 2 });
        expect(rect).toEqual({ minRow: 3, maxRow: 3, minCol: 2, maxCol: 2 });
    });
});

describe('isSingleCell', () => {
    it('returns true for same coordinates', () => {
        expect(isSingleCell({ row: 1, col: 2 }, { row: 1, col: 2 })).toBe(true);
    });

    it('returns false for different rows', () => {
        expect(isSingleCell({ row: 1, col: 2 }, { row: 2, col: 2 })).toBe(false);
    });

    it('returns false when anchor is null', () => {
        expect(isSingleCell(null, { row: 0, col: 0 })).toBe(false);
    });
});

describe('sanitizeValue', () => {
    it('replaces tabs with spaces', () => {
        expect(sanitizeValue('hello\tworld')).toBe('hello world');
    });

    it('replaces newlines with spaces', () => {
        expect(sanitizeValue('line1\nline2')).toBe('line1 line2');
    });

    it('replaces carriage returns', () => {
        expect(sanitizeValue('line1\r\nline2')).toBe('line1 line2');
    });

    it('handles mixed tabs and newlines', () => {
        expect(sanitizeValue('a\tb\nc\r\nd')).toBe('a b c d');
    });

    it('returns empty string for empty input', () => {
        expect(sanitizeValue('')).toBe('');
    });
});

describe('buildTsv', () => {
    const cols = [
        { alias: null, logicalName: 'name' },
        { alias: null, logicalName: 'city' },
        { alias: null, logicalName: 'revenue' },
    ];
    const rows = [
        { name: 'Contoso', city: 'Seattle', revenue: 1000 },
        { name: 'Fabrikam', city: 'Portland', revenue: 500 },
        { name: 'Northwind', city: 'Chicago', revenue: 750 },
    ];
    const getValue = (row: Record<string, unknown>, colIdx: number) => {
        const key = cols[colIdx].alias || cols[colIdx].logicalName;
        const val = row[key];
        return val === null || val === undefined ? '' : String(val);
    };

    it('builds TSV with headers for full selection', () => {
        const result = buildTsv(rows, cols, { minRow: 0, maxRow: 2, minCol: 0, maxCol: 2 }, true, getValue);
        expect(result).toBe('name\tcity\trevenue\nContoso\tSeattle\t1000\nFabrikam\tPortland\t500\nNorthwind\tChicago\t750');
    });

    it('builds TSV without headers', () => {
        const result = buildTsv(rows, cols, { minRow: 0, maxRow: 1, minCol: 0, maxCol: 1 }, false, getValue);
        expect(result).toBe('Contoso\tSeattle\nFabrikam\tPortland');
    });

    it('respects column range (partial columns)', () => {
        const result = buildTsv(rows, cols, { minRow: 0, maxRow: 0, minCol: 1, maxCol: 2 }, true, getValue);
        expect(result).toBe('city\trevenue\nSeattle\t1000');
    });

    it('sanitizes values with tabs/newlines', () => {
        const dirtyRows = [{ name: 'Con\ttoso', city: 'Sea\nttle', revenue: 100 }];
        const result = buildTsv(dirtyRows, cols, { minRow: 0, maxRow: 0, minCol: 0, maxCol: 2 }, false, getValue);
        // Note: getValue returns the raw string, buildTsv calls sanitizeValue
        // But getValue returns 'Con\ttoso' which sanitizeValue converts to 'Con toso'
        expect(result).toContain('Con toso');
        expect(result).toContain('Sea ttle');
    });
});
```

- [ ] **Step 3: Run tests**

Run: `cd extension && npx vitest run src/__tests__/panels/querySelectionUtils.test.ts`
Expected: All PASS

- [ ] **Step 4: Commit**

```bash
git add extension/src/panels/querySelectionUtils.ts extension/src/__tests__/panels/querySelectionUtils.test.ts
git commit -m "feat(extension): extract and test selection utility functions"
```

---

## Final Verification

### Task 6: Full Build + Test Pass

- [ ] **Step 1: Run all extension tests**

Run: `cd extension && npx vitest run`
Expected: All new tests PASS, pre-existing failures unchanged

- [ ] **Step 2: Compile extension**

Run: `cd extension && npx tsc --noEmit`
Expected: No errors

- [ ] **Step 3: Cleanup any dead code**

Search for any remaining references to `selectedCells`, `updateCellSelection`, or `copySelectedCells` in QueryPanel.ts. Remove if found.

- [ ] **Step 4: Final commit if needed**

```bash
git add -A
git commit -m "chore: remove dead selection code"
```
