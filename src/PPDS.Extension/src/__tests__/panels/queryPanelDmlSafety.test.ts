import { afterEach, describe, expect, it, vi } from 'vitest';
import type * as vscode from 'vscode';
import { ResponseError } from 'vscode-jsonrpc/node';

import type { DaemonClient } from '../../daemonClient.js';
import { QueryPanel } from '../../panels/QueryPanel.js';
import type { QueryResultResponse } from '../../types.js';

const host = vi.hoisted(() => ({ panels: [] as any[], warning: vi.fn() }));
vi.mock('vscode', () => ({
    ViewColumn: { One: 1, Beside: 2 },
    Uri: { joinPath: (...parts: unknown[]) => parts.join('/'), parse: (value: string) => ({ scheme: new URL(value).protocol.replace(':', '') }) },
    window: {
        showWarningMessage: host.warning, showTextDocument: vi.fn(),
        createWebviewPanel: vi.fn(() => {
            const panel = { reveal: vi.fn(), dispose: vi.fn(), onDidDispose: vi.fn(() => ({ dispose: vi.fn() })),
                webview: { asWebviewUri: (uri: string) => uri, cspSource: 'test:', postMessage: vi.fn(),
                    onDidReceiveMessage: vi.fn(() => ({ dispose: vi.fn() })) } };
            host.panels.push(panel);
            return panel;
        }),
    },
    workspace: { getConfiguration: () => ({ get: (_key: string, fallback: unknown) => fallback }), openTextDocument: vi.fn() },
}));
vi.mock('../../panels/environmentPicker.js', () => ({ getEnvironmentPickerHtml: () => '', showContextPicker: vi.fn() }));
vi.mock('../../utils/errorNotify.js', () => ({ showErrorWithReport: vi.fn() }));
vi.mock('../../commands/queryHistoryCommand.js', () => ({ showQueryHistory: vi.fn() }));

const ENV = 'https://target.crm.dynamics.com';
const CONFIRM = 'Execute Anyway';

function response(overrides: Partial<QueryResultResponse> = {}): QueryResultResponse {
    return {
        success: true, entityName: 'account', columns: [], records: [{ name: 'Contoso' }], count: 1, totalCount: null,
        moreRecords: false, pagingCookie: null, pageNumber: 1, isAggregate: false, executedFetchXml: null, executionTimeMs: 1,
        ...overrides,
    };
}

const confirmationRequired = () => new ResponseError(-32000, 'DML operations require --confirm to execute.', {
    code: 'Query.DmlConfirmationRequired', message: 'DML operations require --confirm to execute.', dmlConfirmationRequired: true,
});

const panels: QueryPanel[] = [];
function setupPanel(): { panel: any; daemon: any; posted: () => any[] } {
    const daemon = {
        onDidReconnect: vi.fn(() => ({ dispose: vi.fn() })),
        authList: vi.fn().mockResolvedValue({ profiles: [] }),
        envConfigGet: vi.fn().mockResolvedValue({ resolvedType: 'Production' }),
        querySql: vi.fn().mockResolvedValue(response()), queryFetch: vi.fn().mockResolvedValue(response()),
    };
    const panel: any = QueryPanel.show('test:' as unknown as vscode.Uri, daemon as unknown as DaemonClient, undefined, ENV, 'Target Env',
        { profileName: 'p', profileIdentity: 'id' });
    panel.environmentType = 'Production';
    panels.push(panel);
    const webview = host.panels.at(-1).webview;
    return { panel, daemon, posted: () => webview.postMessage.mock.calls.map((call: any[]) => call[0]) };
}

/** Mirrors the daemon guard: unconfirmed DML is answered with confirmation-required. */
function guardLike(daemon: any, dmlResult: QueryResultResponse = response({ records: [{ affected_rows: 1, failed_rows: 0 }] })) {
    daemon.querySql.mockImplementation(async (params: any) => {
        if (/^\s*(delete|update|insert)/i.test(params.sql)) {
            if (params.dmlSafety?.isConfirmed !== true) throw confirmationRequired();
            return dmlResult;
        }
        return response({ moreRecords: true, pagingCookie: 'cookie' });
    });
}

afterEach(() => {
    for (const panel of panels.splice(0)) panel.dispose();
    host.panels.length = 0;
    vi.clearAllMocks();
});

describe('Data Explorer DML safety', () => {
    it('confirms DML with the statement and target before a confirmed retry', async () => {
        const { panel, daemon } = setupPanel();
        guardLike(daemon);
        host.warning.mockResolvedValue(CONFIRM);
        await panel.handleMessage({ command: 'executeQuery', sql: 'DELETE FROM account WHERE name = 1', language: 'sql' });
        expect(daemon.querySql.mock.calls.map((call: any[]) => call[0].dmlSafety)).toEqual([{ isConfirmed: false }, { isConfirmed: true }]);
        const [message, options, button] = host.warning.mock.calls[0];
        expect(message).toContain('Target Env (Production)');
        expect(options).toMatchObject({ modal: true });
        expect(options.detail).toContain(ENV);
        expect(options.detail).toContain('DELETE FROM account WHERE name = 1');
        expect(button).toBe(CONFIRM);
    });

    it('declining sends no confirmed request', async () => {
        const { panel, daemon, posted } = setupPanel();
        guardLike(daemon);
        host.warning.mockResolvedValue(undefined);
        await panel.handleMessage({ command: 'executeQuery', sql: 'DELETE FROM account WHERE name = 1', language: 'sql' });
        expect(daemon.querySql).toHaveBeenCalledTimes(1);
        expect(posted()).toContainEqual({ command: 'queryCancelled' });
    });

    it('shows a refusal as an error without prompting', async () => {
        const { panel, daemon, posted } = setupPanel();
        daemon.querySql.mockRejectedValue(new ResponseError(-32000, 'DELETE without WHERE is not allowed.', {
            code: 'Query.DmlBlocked', message: 'DELETE without WHERE is not allowed.', dmlBlocked: true,
        }));
        await panel.handleMessage({ command: 'executeQuery', sql: 'DELETE FROM account', language: 'sql' });
        expect(host.warning).not.toHaveBeenCalled();
        expect(posted()).toContainEqual({ command: 'queryError', error: expect.stringContaining('DELETE without WHERE is not allowed.') });
    });

    it('posts the failure reason and counts for a DML result with failed rows', async () => {
        const { panel, daemon, posted } = setupPanel();
        guardLike(daemon, response({
            success: false, records: [{ affected_rows: 1, failed_rows: 2 }], affectedRows: 1, failedRows: 2,
            dmlError: 'UPDATE account: 2 row(s) failed. First error: Privilege check failed.',
        }));
        host.warning.mockResolvedValue(CONFIRM);
        await panel.handleMessage({ command: 'executeQuery', sql: 'UPDATE account SET name = 1 WHERE statecode = 0', language: 'sql' });
        const result = posted().find(message => message.command === 'queryResult');
        expect(result.dmlFailure).toContain('2 row(s) failed');
        expect(result.dmlFailure).toContain('1 row(s) succeeded');
        expect(result.dmlFailure).toContain('Privilege check failed');
    });

    it('does not flag a successful SELECT as a DML failure', async () => {
        const { panel, posted } = setupPanel();
        await panel.handleMessage({ command: 'executeQuery', sql: 'SELECT name FROM account', language: 'sql' });
        expect(posted().find(message => message.command === 'queryResult')).not.toHaveProperty('dmlFailure');
    });

    it('load more after a SELECT sends unconfirmed safety options', async () => {
        const { panel, daemon } = setupPanel();
        guardLike(daemon);
        await panel.handleMessage({ command: 'executeQuery', sql: 'SELECT name FROM account', language: 'sql' });
        await panel.handleMessage({ command: 'loadMore', pagingCookie: 'cookie', page: 2 });
        expect(daemon.querySql.mock.calls[1][0]).toMatchObject({ sql: 'SELECT name FROM account', page: 2, dmlSafety: { isConfirmed: false } });
    });

    it('never offers or performs load more for a confirmed DML result', async () => {
        const { panel, daemon, posted } = setupPanel();
        guardLike(daemon, response({ moreRecords: true, pagingCookie: 'cookie', records: [{ affected_rows: 1, failed_rows: 0 }] }));
        host.warning.mockResolvedValue(CONFIRM);
        await panel.handleMessage({ command: 'executeQuery', sql: 'DELETE FROM account WHERE name = 1', language: 'sql' });
        const result = posted().find(message => message.command === 'queryResult');
        expect(result.data.moreRecords).toBe(false);
        expect(result.data.pagingCookie).toBeNull();
        expect(result.data.warnings).toContainEqual(expect.stringContaining('Load More is not available'));
        await panel.handleMessage({ command: 'loadMore', pagingCookie: 'cookie', page: 2 });
        expect(daemon.querySql).toHaveBeenCalledTimes(2);
        expect(daemon.querySql.mock.calls.filter((call: any[]) => call[0].dmlSafety?.isConfirmed === true)).toHaveLength(1);
        expect(daemon.querySql.mock.calls.every((call: any[]) => call[0].page === undefined)).toBe(true);
    });
});
