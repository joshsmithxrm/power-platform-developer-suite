import { describe, expect, it, vi } from 'vitest';
import { ResponseError } from 'vscode-jsonrpc/node';

vi.mock('vscode', () => ({ window: { showWarningMessage: vi.fn() } }));

import type { QueryResultResponse } from '../../types.js';
import { describeDmlFailure, dmlRefusalMessage, isDmlConfirmationRequired, previewStatement } from '../../utils/dmlSafety.js';

function response(overrides: Partial<QueryResultResponse> = {}): QueryResultResponse {
    return {
        success: true, entityName: 'account', columns: [], records: [], count: 0, totalCount: null,
        moreRecords: false, pagingCookie: null, pageNumber: 1, isAggregate: false, executedFetchXml: null, executionTimeMs: 1,
        ...overrides,
    };
}

const confirmedDml = { confirmedDml: true };

describe('dmlSafety helpers', () => {
    it('recognises confirmation-required by flag or code only', () => {
        expect(isDmlConfirmationRequired(new ResponseError(1, 'x', { dmlConfirmationRequired: true }))).toBe(true);
        expect(isDmlConfirmationRequired(new ResponseError(1, 'x', { code: 'Query.DmlConfirmationRequired' }))).toBe(true);
        expect(isDmlConfirmationRequired(new ResponseError(1, 'x', { code: 'Query.DmlBlocked' }))).toBe(false);
        expect(isDmlConfirmationRequired(new Error('Query.DmlConfirmationRequired'))).toBe(false);
    });

    it('returns no refusal message for unrelated errors', () => {
        expect(dmlRefusalMessage(new ResponseError(1, 'x', { code: 'Query.ParseError' }))).toBeUndefined();
        expect(dmlRefusalMessage(new Error('boom'))).toBeUndefined();
    });

    it('ignores successful DML and rows that merely resemble the summary', () => {
        expect(describeDmlFailure(response({ records: [{ affected_rows: 5, failed_rows: 0 }] }), confirmedDml)).toBeUndefined();
        expect(describeDmlFailure(response({ records: [{ affected_rows: 5, failed_rows: 3, name: 'x' }] }), confirmedDml)).toBeUndefined();
        expect(describeDmlFailure(response({ records: [{ affected_rows: 5, failed_rows: 3 }] }), { confirmedDml: false })).toBeUndefined();
    });

    it('reads formatted summary values and additive fields', () => {
        expect(describeDmlFailure(response({ records: [{ affected_rows: { value: 1, formatted: '1' }, failed_rows: { value: 2, formatted: '2' } }] }), confirmedDml))
            .toContain('2 row(s) failed, 1 row(s) succeeded');
        expect(describeDmlFailure(response({ success: false, dmlError: 'reason only' }), { confirmedDml: false })).toContain('reason only');
    });

    it('shows a short statement whole', () => {
        const preview = previewStatement('DELETE FROM account WHERE name = 1');
        expect(preview).toMatchObject({ truncated: false, text: 'DELETE FROM account WHERE name = 1', hiddenChars: 0 });
    });

    it('shows the start and end of a long statement and counts exactly what is hidden', () => {
        const lines = Array.from({ length: 40 }, (_, i) => `line ${i}`);
        const preview = previewStatement(lines.join('\n'));
        expect(preview.truncated).toBe(true);
        expect(preview.text.startsWith('line 0\n')).toBe(true);
        expect(preview.text.endsWith('line 39')).toBe(true);
        const hidden = lines.slice(14, 32).join('\n');
        expect(preview.hiddenChars).toBe(hidden.length + 2);
        expect(preview.text).toContain(`${preview.hiddenChars} character(s)`);
        expect(preview.text).not.toContain('line 20');
    });

    it('cuts a single very long line by characters at both ends', () => {
        const statement = `UPDATE account SET name = '${'x'.repeat(5000)}' WHERE accountid = 1`;
        const preview = previewStatement(statement);
        expect(preview.truncated).toBe(true);
        expect(preview.text.startsWith('UPDATE account SET name')).toBe(true);
        expect(preview.text.endsWith('WHERE accountid = 1')).toBe(true);
        expect(preview.hiddenChars).toBe(statement.length - 900 - 500);
    });
});
