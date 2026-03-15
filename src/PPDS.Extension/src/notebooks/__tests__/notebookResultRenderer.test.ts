import { describe, it, expect } from 'vitest';
import { renderResultsHtml } from '../notebookResultRenderer.js';
import type { QueryResultResponse } from '../../types.js';

function makeResult(overrides: Partial<QueryResultResponse> = {}): QueryResultResponse {
    return {
        success: true,
        entityName: 'account',
        columns: [{ logicalName: 'name', alias: null, displayName: 'Name', dataType: 'string', linkedEntityAlias: null }],
        records: [{ name: 'Test' }],
        count: 1,
        totalCount: null,
        moreRecords: false,
        pagingCookie: null,
        pageNumber: 1,
        isAggregate: false,
        executedFetchXml: null,
        executionTimeMs: 42,
        queryMode: null,
        ...overrides,
    };
}

describe('renderResultsHtml queryMode display', () => {
    it('shows "via TDS" when queryMode is tds', () => {
        const html = renderResultsHtml(makeResult({ queryMode: 'tds' }), undefined, 'test-id');
        expect(html).toContain('42ms via TDS');
    });

    it('shows "via Dataverse" when queryMode is dataverse', () => {
        const html = renderResultsHtml(makeResult({ queryMode: 'dataverse' }), undefined, 'test-id');
        expect(html).toContain('42ms via Dataverse');
    });

    it('shows no mode label when queryMode is null', () => {
        const html = renderResultsHtml(makeResult({ queryMode: null }), undefined, 'test-id');
        expect(html).toContain('42ms');
        expect(html).not.toContain('via TDS');
        expect(html).not.toContain('via Dataverse');
    });
});
