/**
 * Shared cell selection manager for DataTable instances.
 * Provides click, drag, Shift+click range selection, Ctrl+A, and Ctrl+C (TSV copy).
 *
 * Extracted from query-panel.ts so every panel gets selection + copy for free.
 */

import { getSelectionRect, isSingleCell } from './selection-utils.js';
import type { CellCoord } from './selection-utils.js';
import { sanitizeValue } from './dom-utils.js';

export interface SelectionManagerOptions {
    /** The scroll container that holds the table */
    container: HTMLElement;
    /** Called to get the number of visible columns */
    getColumnCount: () => number;
    /** Called to get the number of visible rows */
    getRowCount: () => number;
    /** Called to get the plain-text value of a cell for TSV copy */
    getCellText: (row: number, col: number) => string;
    /** Called to get the column header label for TSV copy */
    getHeaderLabel: (col: number) => string;
    /** Called to copy text (webview cannot access clipboard directly) */
    onCopy: (text: string) => void;
}

export class SelectionManager {
    private anchor: CellCoord | null = null;
    private focus: CellCoord | null = null;
    private readonly opts: SelectionManagerOptions;
    private disposed = false;

    // Bound references for cleanup
    private readonly handleMouseDown: (e: MouseEvent) => void;
    private readonly handleKeyDown: (e: KeyboardEvent) => void;
    private readonly handleKeyDownCapture: (e: KeyboardEvent) => void;

    constructor(opts: SelectionManagerOptions) {
        this.opts = opts;

        this.handleMouseDown = this.onMouseDown.bind(this);
        this.handleKeyDown = this.onKeyDown.bind(this);
        this.handleKeyDownCapture = this.onKeyDownCapture.bind(this);

        opts.container.addEventListener('mousedown', this.handleMouseDown);
        document.addEventListener('keydown', this.handleKeyDown);
        // Capture phase for Ctrl+C so we intercept before other handlers
        document.addEventListener('keydown', this.handleKeyDownCapture, true);
    }

    clearSelection(): void {
        this.anchor = null;
        this.focus = null;
        const tbody = this.opts.container.querySelector('tbody');
        if (tbody) tbody.classList.remove('all-selected');
        this.opts.container.querySelectorAll('td').forEach(td => {
            td.classList.remove(
                'cell-selected', 'cell-selected-top', 'cell-selected-bottom',
                'cell-selected-left', 'cell-selected-right'
            );
        });
    }

    dispose(): void {
        if (this.disposed) return;
        this.disposed = true;
        this.opts.container.removeEventListener('mousedown', this.handleMouseDown);
        document.removeEventListener('keydown', this.handleKeyDown);
        document.removeEventListener('keydown', this.handleKeyDownCapture, true);
    }

    /** Re-apply visual selection classes after a re-render */
    refreshVisuals(): void {
        this.updateSelectionVisuals();
    }

    private onMouseDown(e: MouseEvent): void {
        const target = e.target as HTMLElement;
        // Skip header clicks and link clicks
        if (target.closest('th') || target.closest('a[href]')) return;

        const td = target.closest<HTMLElement>('td[data-row]');
        if (!td) return;

        const row = parseInt(td.dataset['row']!);
        const col = parseInt(td.dataset['col']!);

        if (e.shiftKey && this.anchor) {
            // Shift+click: extend selection from anchor
            this.focus = { row, col };
        } else {
            // Normal click: new single-cell selection + start drag tracking
            this.anchor = { row, col };
            this.focus = { row, col };
            const tbody = this.opts.container.querySelector('tbody');
            if (tbody) tbody.classList.add('selecting');

            const onMouseMove = (ev: MouseEvent) => {
                const moveTarget = (ev.target as HTMLElement).closest
                    ? (ev.target as HTMLElement).closest<HTMLElement>('td[data-row]')
                    : null;
                if (moveTarget) {
                    this.focus = {
                        row: parseInt(moveTarget.dataset['row']!),
                        col: parseInt(moveTarget.dataset['col']!),
                    };
                    this.updateSelectionVisuals();
                }
            };
            const onMouseUp = () => {
                const tb = this.opts.container.querySelector('tbody');
                if (tb) tb.classList.remove('selecting');
                document.removeEventListener('mousemove', onMouseMove);
                document.removeEventListener('mouseup', onMouseUp);
            };
            document.addEventListener('mousemove', onMouseMove);
            document.addEventListener('mouseup', onMouseUp);
        }
        this.updateSelectionVisuals();
    }

    private onKeyDownCapture(e: KeyboardEvent): void {
        // Ctrl+C in capture phase — copy selection as TSV
        if ((e.ctrlKey || e.metaKey) && e.key === 'c' && this.anchor) {
            // Only intercept if the active element is not a text input
            const active = document.activeElement;
            if (active && (active.tagName === 'INPUT' || active.tagName === 'TEXTAREA')) return;

            e.preventDefault();
            e.stopPropagation();
            this.copySelection(false);
        }
    }

    private onKeyDown(e: KeyboardEvent): void {
        // Only handle when the active element is not a text input
        const active = document.activeElement;
        if (active && (active.tagName === 'INPUT' || active.tagName === 'TEXTAREA')) return;

        // Ctrl+A: select all visible rows
        if ((e.ctrlKey || e.metaKey) && e.key === 'a') {
            const rowCount = this.opts.getRowCount();
            const colCount = this.opts.getColumnCount();
            if (rowCount > 0 && colCount > 0) {
                e.preventDefault();
                this.anchor = { row: 0, col: 0 };
                this.focus = { row: rowCount - 1, col: colCount - 1 };
                this.updateSelectionVisuals();
            }
            return;
        }

        // Escape: clear selection
        if (e.key === 'Escape' && this.anchor) {
            this.clearSelection();
        }
    }

    private updateSelectionVisuals(): void {
        const rect = getSelectionRect(this.anchor, this.focus);
        const tbody = this.opts.container.querySelector('tbody');
        if (!tbody) return;

        // Fast path for Ctrl+A (full table)
        tbody.classList.remove('all-selected');
        const rowCount = this.opts.getRowCount();
        const colCount = this.opts.getColumnCount();
        if (rect && rect.minRow === 0 && rect.minCol === 0 &&
            rect.maxRow === rowCount - 1 && rect.maxCol === colCount - 1 &&
            rowCount > 0) {
            tbody.classList.add('all-selected');
            return;
        }

        // Clear all selection classes
        tbody.querySelectorAll('td').forEach(td => {
            td.classList.remove(
                'cell-selected', 'cell-selected-top', 'cell-selected-bottom',
                'cell-selected-left', 'cell-selected-right'
            );
        });

        if (!rect) return;

        // Apply selection classes
        for (let r = rect.minRow; r <= rect.maxRow; r++) {
            for (let c = rect.minCol; c <= rect.maxCol; c++) {
                const td = tbody.querySelector<HTMLElement>(
                    'td[data-row="' + r + '"][data-col="' + c + '"]'
                );
                if (!td) continue;
                td.classList.add('cell-selected');
                if (r === rect.minRow) td.classList.add('cell-selected-top');
                if (r === rect.maxRow) td.classList.add('cell-selected-bottom');
                if (c === rect.minCol) td.classList.add('cell-selected-left');
                if (c === rect.maxCol) td.classList.add('cell-selected-right');
            }
        }
    }

    private copySelection(invertHeaders: boolean): void {
        if (!this.anchor) return;
        const rect = getSelectionRect(this.anchor, this.focus);
        if (!rect) return;

        const single = isSingleCell(this.anchor, this.focus);
        const withHeaders = single ? invertHeaders : !invertHeaders;

        let text = '';

        if (withHeaders) {
            const headers: string[] = [];
            for (let c = rect.minCol; c <= rect.maxCol; c++) {
                headers.push(this.opts.getHeaderLabel(c));
            }
            text += headers.join('\t') + '\n';
        }

        for (let r = rect.minRow; r <= rect.maxRow; r++) {
            const vals: string[] = [];
            for (let c = rect.minCol; c <= rect.maxCol; c++) {
                vals.push(sanitizeValue(this.opts.getCellText(r, c)));
            }
            text += vals.join('\t') + '\n';
        }

        this.opts.onCopy(text.trim());
    }
}
