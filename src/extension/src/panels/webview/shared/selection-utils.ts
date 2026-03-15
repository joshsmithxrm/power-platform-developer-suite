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
