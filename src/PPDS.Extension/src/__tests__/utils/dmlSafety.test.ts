import { describe, expect, it, vi } from 'vitest';
import { ResponseError } from 'vscode-jsonrpc/node';

vi.mock('vscode', () => ({ window: { showWarningMessage: vi.fn() } }));

import * as vscode from 'vscode';

import type { QueryResultResponse } from '../../types.js';
import {
    confirmDmlExecution, describeDmlFailure, dmlMayHaveApplied, dmlRefusalMessage, dmlWriteTargets, isDmlConfirmationRequired, previewStatement, visibleText,
} from '../../utils/dmlSafety.js';

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
        expect(preview).toMatchObject({ truncated: false, text: 'DELETE FROM account WHERE name = 1', hiddenChars: 0, hiddenLines: 0 });
    });

    it.each([['LF', '\n'], ['CRLF', '\r\n']])('counts exactly what is hidden in a long %s statement', (_label, eol) => {
        const lines = Array.from({ length: 40 }, (_, i) => `line ${i}`);
        const preview = previewStatement(lines.join(eol));
        const head = lines.slice(0, 14).join('\n');
        const tail = lines.slice(32).join('\n');
        const hidden = lines.slice(14, 32).join('\n');
        expect(preview).toMatchObject({ truncated: true, totalLines: 40, hiddenLines: 18, hiddenChars: hidden.length });
        expect(preview.totalChars).toBe(lines.join('\n').length);
        // Shown + hidden + the two line breaks that separate them add up to the whole statement.
        expect(head.length + tail.length + preview.hiddenChars + 2).toBe(preview.totalChars);
        expect(14 + 8 + preview.hiddenLines).toBe(preview.totalLines);
        expect(preview.text).toBe(`${head}\n… ${hidden.length} character(s) on 18 line(s) not shown …\n${tail}`);
    });

    it('cuts a single very long line by characters at both ends', () => {
        const statement = `UPDATE account SET name = '${'x'.repeat(5000)}' WHERE accountid = 1`;
        const preview = previewStatement(statement);
        expect(preview.truncated).toBe(true);
        expect(preview.text.startsWith('UPDATE account SET name')).toBe(true);
        expect(preview.text.endsWith('WHERE accountid = 1')).toBe(true);
        expect(preview.hiddenChars).toBe(statement.length - 900 - 500);
        expect(preview.hiddenLines).toBe(1);
    });

    it('reads dmlTargets defensively', () => {
        const error = (dmlTargets: unknown) => new ResponseError(1, 'x', { code: 'Query.DmlConfirmationRequired', dmlTargets });
        expect(dmlWriteTargets(new ResponseError(1, 'x', { code: 'Query.DmlConfirmationRequired' }))).toBeUndefined();
        expect(dmlWriteTargets(error(undefined))).toBeUndefined();
        expect(dmlWriteTargets(error(null))).toBeUndefined();
        expect(dmlWriteTargets(error('PROD'))).toBe('unreadable');
        expect(dmlWriteTargets(error([]))).toBe('unreadable');
        expect(dmlWriteTargets(error([{ label: 1 }]))).toBe('unreadable');
        expect(dmlWriteTargets(error([{ label: 'ok', url: 'https://a' }, 'bad']))).toBe('unreadable');
        expect(dmlWriteTargets(error([{ label: 'PROD', url: 'https://p', type: 'Production', protection: 5 }])))
            .toEqual([{ label: 'PROD', url: 'https://p', type: 'Production', protection: undefined }]);
    });

    it('makes control and bidirectional characters visible and keeps line breaks', () => {
        expect(visibleText('a\u202Eb\nc\u0007')).toEqual({ text: 'a<U+202E>b\nc<U+0007>', changed: true });
        expect(visibleText('plain\ttext')).toEqual({ text: 'plain\ttext', changed: false });
    });

    it.each([['CR', '\r']])('counts exactly what is hidden in a long %s-only statement', (_label, eol) => {
        const lines = Array.from({ length: 40 }, (_, i) => `line ${i}`);
        const preview = previewStatement(lines.join(eol));
        expect(preview).toMatchObject({ truncated: true, totalLines: 40, hiddenLines: 18, hiddenChars: lines.slice(14, 32).join('\n').length });
        expect(14 + 8 + preview.hiddenLines).toBe(preview.totalLines);
    });

    it('counts a trailing line break as starting one more (empty) line', () => {
        const lines = Array.from({ length: 40 }, (_, i) => `line ${i}`);
        const preview = previewStatement(`${lines.join('\n')}\n`);
        const all = [...lines, ''];
        const head = all.slice(0, 14).join('\n');
        const tail = all.slice(33).join('\n');
        expect(preview).toMatchObject({ truncated: true, totalLines: 41, hiddenLines: 19 });
        expect(head.length + tail.length + preview.hiddenChars + 2).toBe(preview.totalChars);
        expect(14 + 8 + preview.hiddenLines).toBe(preview.totalLines);
    });

    it('counts U+2028 and U+2029 as line breaks and marks them visibly', () => {
        const ls = String.fromCharCode(0x2028);
        const ps = String.fromCharCode(0x2029);
        const preview = previewStatement(`DELETE FROM account WHERE statecode = 0 -- note${ls}AND name = 'test'${ps}`);
        expect(preview).toMatchObject({ truncated: false, totalLines: 3, hasSeparators: true });
        expect(preview.text).toBe("DELETE FROM account WHERE statecode = 0 -- note<U+2028>\nAND name = 'test'<U+2029>\n");
        expect(preview.text).not.toContain(ls);
        const many = previewStatement(Array.from({ length: 300 }, (_, i) => `x${i}`).join(ls));
        expect(many).toMatchObject({ truncated: true, totalLines: 300 });
        expect(many.text).not.toContain(ls);
    });

    it('opens no dialog when the execution is already cancelled', async () => {
        const abort = new AbortController();
        abort.abort();
        await expect(confirmDmlExecution('DELETE FROM account WHERE name = 1', { environmentUrl: 'https://a' }, { signal: abort.signal }))
            .resolves.toEqual({ kind: 'aborted' });
        expect(vscode.window.showWarningMessage).not.toHaveBeenCalled();
    });

    it('treats only pre-execution answers as having changed nothing', () => {
        const coded = (code: string) => new ResponseError(1, 'x', { code });
        expect(dmlMayHaveApplied(coded('Query.DmlConfirmationRequired'))).toBe(false);
        expect(dmlMayHaveApplied(coded('Auth.ProfileNotFound'))).toBe(false);
        expect(dmlMayHaveApplied(coded('Query.ExecutionFailed'))).toBe(true);
        expect(dmlMayHaveApplied(new Error('connection reset'))).toBe(true);
    });

    it('never labels a target without a URL as this environment, even when the bound URL is unknown', async () => {
        await confirmDmlExecution('DELETE FROM X.account WHERE name = 1', { environmentName: 'Bound' }, {
            writeTargets: [{ label: 'NOURL', type: 'Development' }],
        });
        const [title, options] = (vscode.window.showWarningMessage as any).mock.calls.at(-1);
        expect(options.detail).toMatch(/NOURL \(Development\) \[OTHER ENVIRONMENT\]/);
        expect(options.detail).not.toContain('this environment');
        expect(title).toContain('and 1 other environment(s)');
    });
});
