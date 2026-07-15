import { describe, it, expect } from 'vitest';
import { readFileSync } from 'fs';
import { resolve } from 'path';

import { DataTable } from '../../../panels/webview/shared/data-table.js';

/**
 * Regression guard for the virtual-scroll "empty void" bug.
 *
 * DataTable virtualises rows and reads `scrollContainer.clientHeight` to decide the
 * visible window. The inner `.data-table-scroll` is `flex:1; min-height:0`, so it only
 * gets a real (bounded) height when its host is a flex column. If the host is a plain
 * block, `.data-table-scroll` expands to the full content height, never scrolls, its
 * scroll handler never fires, and only the initial row buffer renders — the rest of the
 * list becomes an empty void. (Introduced with virtual scrolling in PR #651 /
 * commit f809c0e0; shipped in every Extension release since v0.7.0.)
 *
 * The fix has two coupled halves — DataTable tags its host `.data-table-host`, and
 * shared.css makes `.data-table-host` a flex column. Both must stay in place, so both
 * are asserted here.
 */
describe('DataTable virtual-scroll host contract', () => {
    it('tags its host element with .data-table-host', () => {
        // Minimal container stub — the constructor only calls classList.add + addEventListener.
        const classes = new Set<string>();
        const container = {
            classList: {
                add: (c: string) => classes.add(c),
                remove: (c: string) => classes.delete(c),
                contains: (c: string) => classes.has(c),
            },
            addEventListener: () => { /* no-op */ },
        };

        new DataTable<{ id: string }>({
            columns: [{ key: 'id', label: 'Id', render: (r) => r.id }],
            getRowId: (r) => r.id,
            container,
        });

        // The class the CSS below hangs off of must be applied to the host.
        expect(classes.has('data-table-host')).toBe(true);
    });

    it('shared.css makes .data-table-host a bounded flex column', () => {
        const css = readFileSync(
            resolve(__dirname, '../../../panels/styles/shared.css'),
            'utf-8',
        );
        const rule = css.match(/\.data-table-host\s*\{([^}]*)\}/);
        expect(rule, '.data-table-host rule missing from shared.css').not.toBeNull();
        const body = rule![1];
        // Without a flex column + min-height:0, the inner .data-table-scroll cannot be bounded.
        expect(body).toMatch(/display\s*:\s*flex/);
        expect(body).toMatch(/flex-direction\s*:\s*column/);
        expect(body).toMatch(/min-height\s*:\s*0/);
    });
});
