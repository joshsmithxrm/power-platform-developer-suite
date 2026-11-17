import { describe, expect, it, vi } from 'vitest';
import { ResponseError } from 'vscode-jsonrpc/node';

vi.mock('vscode', () => ({ window: { showWarningMessage: vi.fn() } }));

import type { QueryResultResponse } from '../../types.js';
import { describeDmlFailure, dmlRefusalMessage, isDmlConfirmationRequired } from '../../utils/dmlSafety.js';

function response(overrides: Partial<QueryResultResponse> = {}): QueryResultResponse {
    return {
        success: true, entityName: 'account', columns: [], records: [], count: 0, totalCount: null,
        moreRecords: false, pagingCookie: null, pageNumber: 1, isAggregate: false, executedFetchXml: null, executionTimeMs: 1,
        ...overrides,
    };
}

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

    it('ignores successful DML and ordinary rows that merely resemble the summary', () => {
        expect(describeDmlFailure(response({ records: [{ affected_rows: 5, failed_rows: 0 }] }))).toBeUndefined();
        expect(describeDmlFailure(response({ records: [{ affected_rows: 5, failed_rows: 3, name: 'x' }] }))).toBeUndefined();
        expect(describeDmlFailure(response({ records: [{ name: 'x' }] }))).toBeUndefined();
    });

    it('reads formatted summary values and additive fields', () => {
        expect(describeDmlFailure(response({ records: [{ affected_rows: { value: 1, formatted: '1' }, failed_rows: { value: 2, formatted: '2' } }] })))
            .toContain('2 row(s) failed, 1 row(s) succeeded');
        expect(describeDmlFailure(response({ success: false, dmlError: 'reason only' }))).toContain('reason only');
    });
});
