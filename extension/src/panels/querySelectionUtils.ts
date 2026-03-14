/**
 * Pure utility functions for query results selection and copy.
 * Extracted from QueryPanel webview JS for testability.
 * The webview IIFE has inline copies of these — keep in sync.
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
