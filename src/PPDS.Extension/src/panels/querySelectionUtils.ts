/**
 * Re-exports selection and DOM utilities from shared webview modules.
 * Kept as a stable import path for host-side tests.
 */
export { getSelectionRect, isSingleCell } from './webview/shared/selection-utils.js';
export type { SelectionRect, CellCoord } from './webview/shared/selection-utils.js';

// sanitizeValue is also defined in dom-utils.ts (webview) — kept inline here to
// avoid pulling a browser-only dependency (CSS.escape) into the host tsconfig.
export function sanitizeValue(val: string): string {
    return val.replace(/\t/g, ' ').replace(/\r?\n/g, ' ');
}

// buildTsv is host-only (used for copy/paste in test utilities)
export function buildTsv(
    rows: Record<string, unknown>[],
    columns: { alias: string | null; logicalName: string }[],
    rect: { minRow: number; maxRow: number; minCol: number; maxCol: number },
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
