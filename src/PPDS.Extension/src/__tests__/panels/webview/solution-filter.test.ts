import { describe, it, expect } from 'vitest';
import { readFileSync } from 'fs';
import { resolve } from 'path';

describe('SolutionFilter (#892)', () => {
    const source = readFileSync(
        resolve(__dirname, '../../../panels/webview/shared/solution-filter.ts'),
        'utf-8',
    );

    it('does not use fabricated "All Solutions" label', () => {
        expect(source).not.toContain('All Solutions');
    });

    it('uses "(No filter)" for the unfiltered option', () => {
        expect(source).toContain('(No filter)');
    });
});
