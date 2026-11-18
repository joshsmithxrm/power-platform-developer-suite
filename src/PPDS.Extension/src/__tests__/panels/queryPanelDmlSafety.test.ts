import { afterEach, describe, expect, it, vi } from 'vitest';
import * as vscode from 'vscode';
import { ResponseError } from 'vscode-jsonrpc/node';

import type { DaemonClient } from '../../daemonClient.js';
import { QueryPanel } from '../../panels/QueryPanel.js';
import type { QueryResultResponse } from '../../types.js';

const host = vi.hoisted(() => ({ panels: [] as any[], warning: vi.fn(), error: vi.fn(), quickPick: vi.fn() }));
vi.mock('vscode', () => ({
    ViewColumn: { One: 1, Beside: 2 },
    Uri: { joinPath: (...parts: unknown[]) => parts.join('/'), parse: (value: string) => ({ scheme: new URL(value).protocol.replace(':', '') }) },
    window: {
        showWarningMessage: host.warning, showErrorMessage: host.error, showQuickPick: host.quickPick, showTextDocument: vi.fn(),
        createWebviewPanel: vi.fn(() => {
            const panel = { reveal: vi.fn(), dispose: vi.fn(), onDidDispose: vi.fn(() => ({ dispose: vi.fn() })),
                webview: { asWebviewUri: (uri: string) => uri, cspSource: 'test:', postMessage: vi.fn(),
                    onDidReceiveMessage: vi.fn(() => ({ dispose: vi.fn() })) } };
            host.panels.push(panel);
            return panel;
        }),
    },
    workspace: { getConfiguration: () => ({ get: (_key: string, fallback: unknown) => fallback }), openTextDocument: vi.fn(async () => ({})) },
}));
vi.mock('../../panels/environmentPicker.js', () => ({ getEnvironmentPickerHtml: () => '', showContextPicker: vi.fn() }));
vi.mock('../../utils/errorNotify.js', () => ({ showErrorWithReport: vi.fn() }));
vi.mock('../../commands/queryHistoryCommand.js', () => ({ showQueryHistory: vi.fn() }));

const ENV = 'https://target.crm.dynamics.com';
const OTHER = 'https://other.crm.dynamics.com';
const CONFIRM = 'Execute Anyway';
const SHOW = 'Show Full Statement';
const DML = 'DELETE FROM account WHERE name = 1';

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
function setupPanel(captured = true): { panel: any; daemon: any; posted: () => any[] } {
    const daemon = {
        onDidReconnect: vi.fn(() => ({ dispose: vi.fn() })),
        authList: vi.fn().mockResolvedValue({ profiles: [] }),
        authWho: vi.fn().mockResolvedValue({ name: 'p', index: 1, profileIdentity: 'id-p' }),
        profilesInvalidate: vi.fn().mockResolvedValue({}),
        envConfigGet: vi.fn().mockResolvedValue({ resolvedType: 'Production' }),
        querySql: vi.fn().mockResolvedValue(response()), queryFetch: vi.fn().mockResolvedValue(response()),
    };
    const panel: any = QueryPanel.show('test:' as unknown as vscode.Uri, daemon as unknown as DaemonClient, undefined, ENV, 'Target Env',
        captured ? { profileName: 'p', profileIdentity: 'id' } : undefined);
    panel.environmentType = 'Production';
    // initEnvironment is not run in these tests; a non-captured panel shows the active profile's name.
    if (!captured) panel.profileName = 'p';
    panels.push(panel);
    const webview = host.panels.at(-1).webview;
    return { panel, daemon, posted: () => webview.postMessage.mock.calls.map((call: any[]) => call[0]) };
}

/** Mirrors the daemon guard: unconfirmed DML is answered with confirmation-required. */
function guardLike(daemon: any, dmlResult: () => QueryResultResponse = () => response({ records: [{ affected_rows: 1, failed_rows: 0 }] })) {
    daemon.querySql.mockImplementation(async (params: any) => {
        if (/^\s*(delete|update|insert)/i.test(params.sql)) {
            if (params.dmlSafety?.isConfirmed !== true) throw confirmationRequired();
            return dmlResult();
        }
        return response({ moreRecords: true, pagingCookie: 'cookie' });
    });
}

const confirmed = (daemon: any): any[] => daemon.querySql.mock.calls.map((call: any[]) => call[0]).filter((request: any) => request.dmlSafety?.isConfirmed === true);
const modals = (): any[][] => host.warning.mock.calls.filter(call => call[1]?.modal === true);

afterEach(() => {
    for (const panel of panels.splice(0)) panel.dispose();
    host.panels.length = 0;
    // mockReset, not clearAllMocks: answers from one test must not leak into the next.
    host.warning.mockReset();
    host.error.mockReset();
    host.quickPick.mockReset();
    vi.clearAllMocks();
});

describe('Data Explorer DML safety', () => {
    it('confirms DML with statement and target, and sends the confirmed request for the same pair', async () => {
        const { panel, daemon } = setupPanel();
        guardLike(daemon);
        host.warning.mockResolvedValue(CONFIRM);
        await panel.handleMessage({ command: 'executeQuery', sql: DML, language: 'sql' });
        const [first, second] = daemon.querySql.mock.calls.map((call: any[]) => call[0]);
        expect([first.dmlSafety, second.dmlSafety]).toEqual([{ isConfirmed: false }, { isConfirmed: true }]);
        expect(second).toMatchObject({ sql: first.sql, environmentUrl: first.environmentUrl, profileName: first.profileName, profileIdentity: first.profileIdentity });
        const [message, options, ...buttons] = modals()[0];
        expect(message).toContain('Target Env (Production)');
        expect(options.detail).toContain(ENV);
        expect(options.detail).toContain(DML);
        expect(buttons).toEqual([SHOW, CONFIRM]);
    });

    it('declining sends no confirmed request', async () => {
        const { panel, daemon, posted } = setupPanel();
        guardLike(daemon);
        await panel.handleMessage({ command: 'executeQuery', sql: DML, language: 'sql' });
        expect(daemon.querySql).toHaveBeenCalledTimes(1);
        expect(posted()).toContainEqual({ command: 'queryCancelled', message: expect.stringContaining('DML confirmation was declined') });
    });

    it('does not send the confirmed request when the environment changed while the modal was open', async () => {
        const { panel, daemon, posted } = setupPanel();
        guardLike(daemon);
        host.warning.mockImplementation(async () => { panel.environmentUrl = OTHER; return CONFIRM; });
        await panel.handleMessage({ command: 'executeQuery', sql: DML, language: 'sql' });
        expect(confirmed(daemon)).toHaveLength(0);
        expect(posted()).toContainEqual({ command: 'queryError', error: expect.stringContaining('changed while the confirmation') });
    });

    it('sends nothing confirmed when the panel is disposed while the modal is open', async () => {
        const { panel, daemon } = setupPanel();
        guardLike(daemon);
        host.warning.mockImplementation(async () => { panel.dispose(); return CONFIRM; });
        await panel.handleMessage({ command: 'executeQuery', sql: DML, language: 'sql' });
        expect(confirmed(daemon)).toHaveLength(0);
    });

    it('after re-authentication sends the statement unconfirmed again and asks again for the target of that attempt', async () => {
        const { panel, daemon } = setupPanel(false);
        let confirmedCalls = 0;
        daemon.querySql.mockImplementation(async (params: any) => {
            if (params.dmlSafety?.isConfirmed !== true) throw confirmationRequired();
            if (++confirmedCalls === 1) throw new Error('token expired');
            return response({ records: [{ affected_rows: 1, failed_rows: 0 }] });
        });
        host.warning.mockImplementation(async (_message: string, options: any) => {
            // Second modal: the panel moved to another environment before the re-authentication retry.
            return options?.modal ? CONFIRM : undefined;
        });
        host.error.mockImplementation(async () => { panel.environmentUrl = OTHER; return 'Re-authenticate'; });
        await panel.handleMessage({ command: 'executeQuery', sql: DML, language: 'sql' });
        const requests = daemon.querySql.mock.calls.map((call: any[]) => call[0]);
        expect(requests.map((request: any) => request.dmlSafety.isConfirmed)).toEqual([false, true, false, true]);
        expect(requests[2].environmentUrl).toBe(OTHER);
        expect(requests[3].environmentUrl).toBe(OTHER);
        expect(modals()).toHaveLength(2);
        expect(modals()[1][1].detail).toContain(OTHER);
        expect(modals()[1][1].detail).toContain('may have partly applied');
        expect(requests[3].profileIdentity).toBe('id-p');
    });

    it('refresh of a confirmed DML asks again', async () => {
        const { panel, daemon } = setupPanel();
        guardLike(daemon);
        host.warning.mockResolvedValue(CONFIRM);
        await panel.handleMessage({ command: 'executeQuery', sql: DML, language: 'sql' });
        await panel.handleMessage({ command: 'refresh' });
        expect(modals()).toHaveLength(2);
        expect(daemon.querySql.mock.calls.map((call: any[]) => call[0].dmlSafety.isConfirmed)).toEqual([false, true, false, true]);
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

    it('shows a refusal of the confirmed request as an error', async () => {
        const { panel, daemon, posted } = setupPanel();
        guardLike(daemon, () => { throw new ResponseError(-32000, 'Mutations are disabled during shakedown.', { code: 'Safety.ShakedownActive', message: 'Mutations are disabled during shakedown.' }); });
        host.warning.mockResolvedValue(CONFIRM);
        await panel.handleMessage({ command: 'executeQuery', sql: DML, language: 'sql' });
        expect(modals()).toHaveLength(1);
        expect(posted()).toContainEqual({ command: 'queryError', error: expect.stringContaining('Mutations are disabled during shakedown.') });
    });

    it('posts the failure reason and counts for a DML result with failed rows', async () => {
        const { panel, daemon, posted } = setupPanel();
        guardLike(daemon, () => response({
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

    it('never flags a SELECT, even one whose columns look like the DML summary', async () => {
        const { panel, daemon, posted } = setupPanel();
        daemon.querySql.mockResolvedValue(response({ records: [{ affected_rows: 1, failed_rows: 3 }] }));
        await panel.handleMessage({ command: 'executeQuery', sql: 'SELECT 1 AS affected_rows, 3 AS failed_rows', language: 'sql' });
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
        guardLike(daemon, () => response({ moreRecords: true, pagingCookie: 'cookie', records: [{ affected_rows: 1, failed_rows: 0 }] }));
        host.warning.mockResolvedValue(CONFIRM);
        await panel.handleMessage({ command: 'executeQuery', sql: DML, language: 'sql' });
        const result = posted().find(message => message.command === 'queryResult');
        expect(result.data.moreRecords).toBe(false);
        expect(result.data.pagingCookie).toBeNull();
        expect(result.data.warnings).toContainEqual(expect.stringContaining('Load More is not available'));
        await panel.handleMessage({ command: 'loadMore', pagingCookie: 'cookie', page: 2 });
        expect(daemon.querySql).toHaveBeenCalledTimes(2);
        expect(confirmed(daemon)).toHaveLength(1);
        expect(daemon.querySql.mock.calls.every((call: any[]) => call[0].page === undefined)).toBe(true);
    });

    it('pins a non-captured profile by identity for the confirmed request', async () => {
        const { panel, daemon } = setupPanel(false);
        guardLike(daemon);
        host.warning.mockResolvedValue(CONFIRM);
        await panel.handleMessage({ command: 'executeQuery', sql: DML, language: 'sql' });
        const [first, second] = daemon.querySql.mock.calls.map((call: any[]) => call[0]);
        expect(first.profileIdentity).toBeUndefined();
        expect(second).toMatchObject({ profileName: 'p', profileIdentity: 'id-p', dmlSafety: { isConfirmed: true } });
        expect(modals()[0][1].detail).toContain('Profile: p');
    });

    it.each([
        ['the profile is not the active one', (panel: any, daemon: any) => { daemon.authWho.mockResolvedValue({ name: 'other', index: 2, profileIdentity: 'id-o' }); }, 'not the active profile'],
        ['the profile is not known yet', (panel: any) => { panel.profileName = undefined; }, 'profile of this panel is not known'],
        ['the environment URL is not known', (panel: any) => { panel.environmentUrl = undefined; }, 'environment URL of this panel is not known'],
        ['the daemon reports no identity', (panel: any, daemon: any) => { daemon.authWho.mockResolvedValue({ name: 'p', index: 1 }); }, 'does not report a profile identity'],
        ['auth/who fails', (panel: any, daemon: any) => { daemon.authWho.mockRejectedValue(new Error('daemon restarting')); }, 'could not be resolved (daemon restarting)'],
        ['the panel shows "Profile 2" for an unnamed active profile 2', (panel: any, daemon: any) => { panel.profileName = 'Profile 2'; daemon.authWho.mockResolvedValue({ name: null, index: 2, profileIdentity: 'id-2' }); }, 'not the active profile'],
        ['the panel profile "2" is an index while the active profile is named "2"', (panel: any, daemon: any) => { panel.profileName = '2'; daemon.authWho.mockResolvedValue({ name: '2', index: 5, profileIdentity: 'id-5' }); }, 'not the active profile'],
        ['the panel profile has a bracket form that is not an index', (panel: any, daemon: any) => { panel.profileName = '[x] p'; }, 'not the active profile'],
    ])('refuses without offering Execute when %s', async (_label, arrange, message) => {
        const { panel, daemon, posted } = setupPanel(false);
        guardLike(daemon);
        arrange(panel, daemon);
        host.warning.mockResolvedValue(CONFIRM);
        await panel.handleMessage({ command: 'executeQuery', sql: DML, language: 'sql' });
        expect(modals()).toHaveLength(0);
        expect(confirmed(daemon)).toHaveLength(0);
        expect(posted()).toContainEqual({ command: 'queryError', error: expect.stringContaining(message) });
    });

    it.each([
        ['profile name', (panel: any) => { panel.profileName = 'renamed'; }],
        ['profile identity', (panel: any) => { panel.profileIdentity = 'other-identity'; }],
    ])('does not send the confirmed request when the %s changed while the modal was open', async (_label, change) => {
        const { panel, daemon, posted } = setupPanel();
        guardLike(daemon);
        host.warning.mockImplementation(async () => { change(panel); return CONFIRM; });
        await panel.handleMessage({ command: 'executeQuery', sql: DML, language: 'sql' });
        expect(confirmed(daemon)).toHaveLength(0);
        expect(posted()).toContainEqual({ command: 'queryError', error: expect.stringContaining('changed while the confirmation') });
    });

    it('runs the review loop: Show Full Statement, continue, then Execute', async () => {
        const { panel, daemon } = setupPanel();
        guardLike(daemon);
        let modal = 0;
        host.warning.mockImplementation(async () => (modal++ === 0 ? SHOW : CONFIRM));
        host.quickPick.mockImplementation(async (items: any[]) => items[0]);
        await panel.handleMessage({ command: 'executeQuery', sql: DML, language: 'sql' });
        expect(host.quickPick).toHaveBeenCalledTimes(1);
        expect(host.quickPick.mock.calls[0][1]).toMatchObject({ ignoreFocusOut: true });
        expect(modals()).toHaveLength(2);
        expect(confirmed(daemon)).toHaveLength(1);
    });

    it('sends nothing when the target changes during the review step', async () => {
        const { panel, daemon, posted } = setupPanel();
        guardLike(daemon);
        let modal = 0;
        host.warning.mockImplementation(async () => (modal++ === 0 ? SHOW : CONFIRM));
        host.quickPick.mockImplementation(async (items: any[]) => { panel.environmentUrl = OTHER; return items[0]; });
        await panel.handleMessage({ command: 'executeQuery', sql: DML, language: 'sql' });
        expect(confirmed(daemon)).toHaveLength(0);
        expect(posted()).toContainEqual({ command: 'queryError', error: expect.stringContaining('changed while the confirmation') });
    });

    it('a dismissed review step says so and runs nothing', async () => {
        const { panel, daemon, posted } = setupPanel();
        guardLike(daemon);
        host.warning.mockResolvedValue(SHOW);
        host.quickPick.mockResolvedValue(undefined);
        await panel.handleMessage({ command: 'executeQuery', sql: DML, language: 'sql' });
        expect(confirmed(daemon)).toHaveLength(0);
        expect(posted()).toContainEqual({ command: 'queryCancelled', message: expect.stringContaining('review prompt was dismissed') });
    });

    it('a superseded confirmation neither sends nor posts over the newer execution', async () => {
        const { panel, daemon, posted } = setupPanel();
        guardLike(daemon);
        let answer!: (value: string) => void;
        const opened = new Promise<void>(resolve => {
            host.warning.mockImplementation(() => { resolve(); return new Promise<string>(accept => { answer = accept; }); });
        });
        const first = panel.handleMessage({ command: 'executeQuery', sql: DML, language: 'sql' });
        await opened;
        await panel.handleMessage({ command: 'executeQuery', sql: 'SELECT name FROM account', language: 'sql' });
        const afterSecond = posted().length;
        answer(CONFIRM);
        await first;
        expect(confirmed(daemon)).toHaveLength(0);
        expect(posted().slice(afterSecond)).toEqual([]);
        expect(posted().at(-1).command).toBe('queryResult');
    });

    it('says records may have changed when cancelled after the confirmed request was sent', async () => {
        const { panel, daemon, posted } = setupPanel();
        guardLike(daemon, () => {
            void panel.handleMessage({ command: 'cancelQuery' });
            throw new Error('Request cancelled');
        });
        host.warning.mockResolvedValue(CONFIRM);
        await panel.handleMessage({ command: 'executeQuery', sql: DML, language: 'sql' });
        expect(posted()).toContainEqual({ command: 'queryError', error: expect.stringContaining('Cancelled after the confirmed statement was submitted: records may already have been changed.') });
        expect(posted().some(message => message.command === 'queryCancelled')).toBe(false);
    });

    it.each([
        ['an index', '2', { name: 'x', index: 2 }],
        ['a bracketed index', '[2] x', { name: 'x', index: 2 }],
        ['a name in another case', 'P', { name: 'p', index: 7 }],
    ])('pins the active profile when the panel names it by %s, as the daemon resolves it', async (_label, profileName, who) => {
        const { panel, daemon } = setupPanel(false);
        guardLike(daemon);
        panel.profileName = profileName;
        daemon.authWho.mockResolvedValue({ ...who, profileIdentity: 'id-active' });
        host.warning.mockResolvedValue(CONFIRM);
        await panel.handleMessage({ command: 'executeQuery', sql: DML, language: 'sql' });
        expect(confirmed(daemon)).toEqual([expect.objectContaining({ profileIdentity: 'id-active' })]);
    });

    it('opens no modal when cancelled while the profile is being resolved', async () => {
        const { panel, daemon, posted } = setupPanel(false);
        guardLike(daemon);
        daemon.authWho.mockImplementation(async () => {
            await panel.handleMessage({ command: 'cancelQuery' });
            return { name: 'p', index: 1, profileIdentity: 'id-p' };
        });
        await panel.handleMessage({ command: 'executeQuery', sql: DML, language: 'sql' });
        expect(modals()).toHaveLength(0);
        expect(posted()).toContainEqual({ command: 'queryCancelled', message: 'Query cancelled. No records were changed.' });
    });

    it('cancelling while the modal is open sends nothing and says no records were changed', async () => {
        const { panel, daemon, posted } = setupPanel();
        guardLike(daemon);
        host.warning.mockImplementation(async () => { await panel.handleMessage({ command: 'cancelQuery' }); return CONFIRM; });
        await panel.handleMessage({ command: 'executeQuery', sql: DML, language: 'sql' });
        expect(confirmed(daemon)).toHaveLength(0);
        expect(posted()).toContainEqual({ command: 'queryCancelled', message: 'Query cancelled. No records were changed.' });
    });

    it('cancelling during the review step closes it and sends nothing', async () => {
        const { panel, daemon } = setupPanel();
        guardLike(daemon);
        host.warning.mockResolvedValue(SHOW);
        let reviewToken: any;
        host.quickPick.mockImplementation(async (items: any[], _options: any, token: any) => {
            reviewToken = token;
            await panel.handleMessage({ command: 'cancelQuery' });
            await new Promise(resolve => setTimeout(resolve, 0));
            return items[0];
        });
        await panel.handleMessage({ command: 'executeQuery', sql: DML, language: 'sql' });
        expect(reviewToken.isCancellationRequested).toBe(true);
        expect(confirmed(daemon)).toHaveLength(0);
    });

    it('reports a rejected dialog as a failure and sends nothing', async () => {
        const { panel, daemon, posted } = setupPanel();
        guardLike(daemon);
        host.warning.mockRejectedValue(new Error('dialog service unavailable'));
        await panel.handleMessage({ command: 'executeQuery', sql: DML, language: 'sql' });
        expect(confirmed(daemon)).toHaveLength(0);
        expect(posted()).toContainEqual({ command: 'queryError', error: expect.stringContaining('could not be shown (dialog service unavailable)') });
    });

    it('reports a failure to open the full statement as a failure and sends nothing', async () => {
        const { panel, daemon, posted } = setupPanel();
        guardLike(daemon);
        host.warning.mockResolvedValue(SHOW);
        (vscode.workspace.openTextDocument as any).mockRejectedValueOnce(new Error('no editor available'));
        await panel.handleMessage({ command: 'executeQuery', sql: DML, language: 'sql' });
        expect(confirmed(daemon)).toHaveLength(0);
        expect(posted()).toContainEqual({ command: 'queryError', error: expect.stringContaining('could not be opened (no editor available)') });
    });

    it('shows reported write targets and makes hidden characters in names visible', async () => {
        const { panel, daemon } = setupPanel();
        const rlo = String.fromCharCode(0x202E);
        panel.environmentDisplayName = `Target${rlo}veD`;
        daemon.querySql.mockImplementation(async (params: any) => {
            if (params.dmlSafety?.isConfirmed) return response();
            throw new ResponseError(-32000, 'confirm', {
                code: 'Query.DmlConfirmationRequired', dmlConfirmationRequired: true,
                dmlTargets: [{ label: 'local', url: ENV, type: 'Production' }, { label: `QA${rlo}`, url: OTHER, type: 'Test' }],
            });
        });
        host.warning.mockResolvedValue(CONFIRM);
        await panel.handleMessage({ command: 'executeQuery', sql: DML, language: 'sql' });
        const [title, options] = modals()[0];
        expect(title).toContain('Target<U+202E>veD');
        expect(title).toContain('and 1 other environment(s)');
        expect(options.detail).toContain('Writes to 2 environment(s):');
        expect(options.detail).toContain('QA<U+202E>');
        expect(options.detail).toMatch(/local \(Production\) https:\/\/target\.crm\.dynamics\.com \[this environment; PRODUCTION \/ PROTECTED\]/);
        expect(`${title}${options.detail}`).not.toContain(rlo);
        expect(options.detail).toContain('shown as <U+XXXX>');
    });

    it('a confirmed request replaced by a newer execution still says it was submitted, without posting over the newer one', async () => {
        const { panel, daemon, posted } = setupPanel();
        daemon.querySql.mockImplementation((params: any, token: any) => {
            if (!/^\s*delete/i.test(params.sql)) return Promise.resolve(response());
            if (params.dmlSafety?.isConfirmed !== true) return Promise.reject(confirmationRequired());
            return new Promise((_resolve, reject) => token.onCancellationRequested(() => reject(new Error('Request cancelled'))));
        });
        host.warning.mockResolvedValue(CONFIRM);
        const first = panel.handleMessage({ command: 'executeQuery', sql: DML, language: 'sql' });
        await vi.waitFor(() => expect(confirmed(daemon)).toHaveLength(1));
        await panel.handleMessage({ command: 'executeQuery', sql: 'SELECT name FROM account', language: 'sql' });
        await first;
        await vi.waitFor(() => expect(host.warning).toHaveBeenCalledWith(expect.stringContaining('A newer execution replaced it')));
        expect(posted().at(-1).command).toBe('queryResult');
        expect(posted().some(message => message.command === 'queryError')).toBe(false);
    });
});
