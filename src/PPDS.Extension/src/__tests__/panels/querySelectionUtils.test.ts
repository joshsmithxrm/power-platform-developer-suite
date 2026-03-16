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

    it('returns false for different cols', () => {
        expect(isSingleCell({ row: 1, col: 2 }, { row: 1, col: 3 })).toBe(false);
    });

    it('returns false when anchor is null', () => {
        expect(isSingleCell(null, { row: 0, col: 0 })).toBe(false);
    });

    it('returns false when both are null', () => {
        expect(isSingleCell(null, null)).toBe(false);
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

    it('leaves normal text untouched', () => {
        expect(sanitizeValue('hello world')).toBe('hello world');
    });
});

describe('buildTsv', () => {
    const cols = [
        { alias: null, logicalName: 'name' },
        { alias: null, logicalName: 'city' },
        { alias: null, logicalName: 'revenue' },
    ];
    const rows: Record<string, unknown>[] = [
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

    it('single cell without headers', () => {
        const result = buildTsv(rows, cols, { minRow: 1, maxRow: 1, minCol: 0, maxCol: 0 }, false, getValue);
        expect(result).toBe('Fabrikam');
    });

    it('single cell with headers', () => {
        const result = buildTsv(rows, cols, { minRow: 1, maxRow: 1, minCol: 0, maxCol: 0 }, true, getValue);
        expect(result).toBe('name\nFabrikam');
    });

    it('sanitizes values with tabs', () => {
        const dirtyRows: Record<string, unknown>[] = [{ name: 'Con\ttoso', city: 'Seattle', revenue: 100 }];
        const result = buildTsv(dirtyRows, cols, { minRow: 0, maxRow: 0, minCol: 0, maxCol: 0 }, false, getValue);
        expect(result).toBe('Con toso');
    });

    it('uses alias when available', () => {
        const aliasedCols = [
            { alias: 'Company', logicalName: 'name' },
            { alias: null, logicalName: 'city' },
        ];
        const result = buildTsv(rows, aliasedCols, { minRow: 0, maxRow: 0, minCol: 0, maxCol: 1 }, true, (row, colIdx) => {
            const key = aliasedCols[colIdx].alias || aliasedCols[colIdx].logicalName;
            const val = row[key === 'Company' ? 'name' : key];
            return val === null || val === undefined ? '' : String(val);
        });
        expect(result).toBe('Company\tcity\nContoso\tSeattle');
    });
});
