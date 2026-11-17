import { beforeEach, describe, expect, it, vi } from 'vitest';
import type * as vscode from 'vscode';
import { ResponseError } from 'vscode-jsonrpc/node';
import type { DaemonClient } from '../../daemonClient.js';
import type { AuthWhoResponse, QueryResultResponse } from '../../types.js';

const host = vi.hoisted(() => ({
    controllers: [] as any[],
    executions: [] as any[],
    status: { text: '', tooltip: '', show: vi.fn(), hide: vi.fn(), dispose: vi.fn() },
    window: { activeNotebookEditor: undefined as any, showQuickPick: vi.fn(), showWarningMessage: vi.fn(), showErrorMessage: vi.fn() },
    notebooks: [] as any[],
    close: undefined as ((notebook: any) => void) | undefined,
    change: undefined as ((event: any) => void) | undefined,
    applyEdit: vi.fn(),
    openTextDocument: vi.fn(async (...args: any[]) => ({ options: args[0] })),
    showTextDocument: vi.fn(async (..._args: any[]) => undefined),
}));

vi.mock('vscode', () => ({
    NotebookCellKind: { Markup: 1, Code: 2 },
    StatusBarAlignment: { Right: 2 },
    ViewColumn: { One: 1, Beside: 2 },
    notebooks: {
        createNotebookController: vi.fn(() => {
            const controller = {
                dispose: vi.fn(),
                createNotebookCellExecution: vi.fn((cell: any) => {
                    let cancel: (() => void) | undefined;
                    const execution = {
                        cell, output: [] as any[], start: vi.fn(), end: vi.fn(),
                        token: { onCancellationRequested: vi.fn((callback: () => void) => { cancel = callback; return { dispose: vi.fn() }; }) },
                        replaceOutput: vi.fn(async (outputs: any[]) => { execution.output = outputs; }),
                        cancel: () => cancel?.(),
                    };
                    host.executions.push(execution);
                    return execution;
                }),
                executeHandler: undefined as any, interruptHandler: undefined as any,
            };
            host.controllers.push(controller);
            return controller;
        }),
    },
    window: {
        get activeNotebookEditor() { return host.window.activeNotebookEditor; },
        createStatusBarItem: () => host.status,
        onDidChangeActiveNotebookEditor: () => ({ dispose: vi.fn() }),
        showQuickPick: (...args: any[]) => host.window.showQuickPick(...args),
        showWarningMessage: (...args: any[]) => host.window.showWarningMessage(...args),
        showErrorMessage: (...args: any[]) => host.window.showErrorMessage(...args),
        showTextDocument: (...args: any[]) => host.showTextDocument(...args),
    },
    workspace: {
        get notebookDocuments() { return host.notebooks; },
        onDidOpenNotebookDocument: () => ({ dispose: vi.fn() }),
        onDidChangeNotebookDocument: (callback: any) => { host.change = callback; return { dispose: vi.fn() }; },
        onDidCloseNotebookDocument: (callback: any) => { host.close = callback; return { dispose: vi.fn() }; },
        onDidChangeTextDocument: () => ({ dispose: vi.fn() }),
        applyEdit: (...args: any[]) => host.applyEdit(...args),
        openTextDocument: (...args: any[]) => host.openTextDocument(...args),
    },
    WorkspaceEdit: class {
        changes: any[] = [];
        set(uri: any, edits: any[]) { this.changes.push({ uri, edits }); }
    },
    NotebookEdit: { updateNotebookMetadata: (metadata: any) => ({ metadata }) },
    NotebookCellOutput: class { constructor(public items: any[]) {} },
    NotebookCellOutputItem: {
        text: (value: string, mime: string) => ({ value, mime }),
        error: (error: Error) => ({ value: error.message, mime: 'application/vnd.code.notebook.error' }),
    },
}));
vi.mock('../../utils/errorNotify.js', () => ({ showErrorWithReport: vi.fn() }));

import { DataverseNotebookController } from '../DataverseNotebookController.js';
import { ExplainDocumentProvider } from '../../providers/explainDocumentProvider.js';

const A = 'https://a.crm.dynamics.com';
const B = 'https://b.crm.dynamics.com';
const C = 'https://c.crm.dynamics.com';

function deferred<T>() {
    let resolve!: (value: T) => void;
    const promise = new Promise<T>(accept => { resolve = accept; });
    return { promise, resolve };
}

function notebook(name: string, url?: string, languages = ['sql']): vscode.NotebookDocument {
    const uri = { toString: () => `file:///${name}.ppdsnb` };
    const value: any = { uri, notebookType: 'ppdsnb', isClosed: false, metadata: url ? { environmentUrl: url, environmentName: name } : {} };
    const cells = languages.map((language, i) => ({
        notebook: value, kind: 2,
        document: {
            uri: { scheme: 'vscode-notebook-cell', toString: () => `vscode-notebook-cell:/${name}/${i}` },
            languageId: language,
            getText: () => language === 'fetchxml' ? '<fetch><entity name="account" /></fetch>' : `SELECT ${i} FROM account`,
        },
    }));
    value.getCells = () => cells;
    host.notebooks.push(value);
    return value;
}

function result(value = 'one'): QueryResultResponse {
    return {
        success: true, entityName: 'account',
        columns: [{ logicalName: 'accountid', alias: null, displayName: 'Account', dataType: 'guid', linkedEntityAlias: null }],
        records: [{ accountid: value }], count: 1, totalCount: 1, moreRecords: false, pagingCookie: null,
        pageNumber: 1, isAggregate: false, executedFetchXml: null, executionTimeMs: 1, queryMode: null,
    };
}

function who(url: string | undefined = C, name = 'profile-one'): AuthWhoResponse {
    return {
        profileIdentity: 'identity-' + name,
        index: 1, name, authMethod: 'InteractiveBrowser', cloud: 'Public', tenantId: 'tenant', username: 'user',
        objectId: 'principal', applicationId: null, tokenExpiresOn: null, tokenStatus: null,
        environment: url ? { url, displayName: `Name ${url}`, type: 'Development', environmentId: 'env', organizationId: null, uniqueName: null, region: null } : null,
        createdAt: '2026-01-01', lastUsedAt: null,
    };
}

function setup() {
    const daemon = {
        authWho: vi.fn().mockResolvedValue(who()),
        envList: vi.fn().mockResolvedValue({ environments: [A, B, C].map((url, i) => ({ apiUrl: url, friendlyName: `Environment ${i}`, type: 'Development', id: String(i), environmentId: String(i) })) }),
        envConfigGet: vi.fn().mockResolvedValue({ resolvedType: 'Unknown' }),
        envSelect: vi.fn(),
        querySql: vi.fn().mockResolvedValue(result()),
        queryFetch: vi.fn().mockResolvedValue(result()),
        profilesInvalidate: vi.fn().mockResolvedValue({}),
    };
    const controller = new DataverseNotebookController(daemon as unknown as DaemonClient);
    const native = host.controllers.at(-1)!;
    return { daemon, controller, run: (nb: vscode.NotebookDocument) => native.executeHandler(nb.getCells(), nb, native) as Promise<void>, interrupt: (nb: vscode.NotebookDocument) => native.interruptHandler(nb) };
}

function outputFor(name: string): string {
    return host.executions.filter(execution => execution.cell.notebook.uri.toString().includes(name))
        .flatMap(execution => execution.output.flatMap((output: any) => output.items.map((item: any) => item.value))).join('\n');
}

beforeEach(() => {
    vi.clearAllMocks();
    host.controllers.length = 0;
    host.executions.length = 0;
    host.notebooks.length = 0;
    host.window.activeNotebookEditor = undefined;
    host.window.showWarningMessage.mockResolvedValue('Run on this environment');
    host.applyEdit.mockImplementation(async (edit: any) => {
        for (const change of edit.changes) {
            const nb = host.notebooks.find(candidate => candidate.uri.toString() === change.uri.toString());
            nb.metadata = change.edits[0].metadata;
            host.change?.({ notebook: nb });
        }
        return true;
    });
});

describe('DataverseNotebookController environment isolation', () => {
    it('fails closed when an older daemon cannot bind profile identity', async () => {
        const { daemon, controller, run } = setup();
        daemon.authWho.mockResolvedValue({ ...who(), profileIdentity: undefined });
        await run(notebook('a', A));
        expect(daemon.querySql).not.toHaveBeenCalled();
        expect(daemon.queryFetch).not.toHaveBeenCalled();
        expect(outputFor('a.ppdsnb')).toContain('updated PPDS daemon');
        controller.dispose();
    });

    it('executes SQL and FetchXML concurrently against their notebook targets', async () => {
        const { daemon, controller, run } = setup();
        const a = notebook('a', A);
        const b = notebook('b', B, ['fetchxml']);
        const pending = deferred<QueryResultResponse>();
        const started = deferred<void>();
        daemon.querySql.mockImplementation(async () => { started.resolve(); return pending.promise; });
        const first = run(a);
        await started.promise;
        await run(b);
        expect(daemon.querySql.mock.calls[0][0]).toMatchObject({ environmentUrl: A, profileName: 'profile-one' });
        expect(daemon.queryFetch.mock.calls[0][0]).toMatchObject({ environmentUrl: B, profileName: 'profile-one' });
        pending.resolve(result());
        await first;
        expect(outputFor('a.ppdsnb')).toContain(A);
        expect(outputFor('b.ppdsnb')).toContain(B);
        controller.dispose();
    });

    it('captures one target and profile for the entire run despite focus binding and default changes', async () => {
        const { daemon, controller, run } = setup();
        const a = notebook('a', A, ['sql', 'sql']);
        const b = notebook('b', B);
        const pending = deferred<QueryResultResponse>();
        const started = deferred<void>();
        daemon.querySql.mockImplementationOnce(async () => { started.resolve(); return pending.promise; });
        const running = run(a);
        await started.promise;
        a.metadata.environmentUrl = B;
        host.change?.({ notebook: a });
        daemon.authWho.mockResolvedValue(who(C, 'profile-two'));
        host.window.activeNotebookEditor = { notebook: b };
        controller.updateStatusBarVisibility(host.window.activeNotebookEditor);
        pending.resolve(result('11111111-1111-1111-1111-111111111111'));
        await running;
        expect(daemon.querySql.mock.calls.map(call => call[0])).toEqual([
            expect.objectContaining({ environmentUrl: A, profileName: 'profile-one' }),
            expect.objectContaining({ environmentUrl: A, profileName: 'profile-one' }),
        ]);
        expect(outputFor('a.ppdsnb')).toContain(A + '/main.aspx');
        expect(outputFor('a.ppdsnb')).not.toContain(B);
        expect(controller.getCellExecutionContext(a.getCells()[0].document.uri.toString())?.context.environmentUrl).toBe(A);
        await run(a);
        expect(daemon.querySql.mock.calls[2][0]).toMatchObject({ environmentUrl: B, profileName: 'profile-two' });
        controller.dispose();
    });

    it('uses the session default for an unbound notebook without inheriting another binding', async () => {
        const { daemon, controller, run } = setup();
        const a = notebook('a', A);
        const unbound = notebook('unbound');
        await run(a);
        await run(unbound);
        expect(daemon.querySql.mock.calls[1][0].environmentUrl).toBe(C);
        expect(daemon.envSelect).not.toHaveBeenCalled();
        controller.dispose();
    });

    it('binds the originating notebook when focus changes while its picker is open', async () => {
        const { daemon, controller } = setup();
        const a = notebook('a', A);
        const b = notebook('b', B);
        host.window.activeNotebookEditor = { notebook: a };
        const choice = deferred<any>();
        const opened = deferred<void>();
        host.window.showQuickPick.mockImplementation(() => { opened.resolve(); return choice.promise; });
        const picking = controller.selectEnvironment();
        await opened.promise;
        host.window.activeNotebookEditor = { notebook: b };
        choice.resolve({ label: 'Third', apiUrl: C, id: 'id-c' });
        await picking;
        expect(a.metadata.environmentUrl).toBe(C);
        expect(b.metadata.environmentUrl).toBe(B);
        expect(daemon.envSelect).not.toHaveBeenCalled();
        controller.dispose();
    });

    it('clears the binding when Use session default is selected', async () => {
        const { daemon, controller, run } = setup();
        const a = notebook('a', A);
        host.window.showQuickPick.mockImplementation(async (items: any[]) => items[0]);
        await controller.selectEnvironment(a);
        expect(a.metadata.environmentUrl).toBeUndefined();
        await run(a);
        expect(daemon.querySql.mock.calls[0][0].environmentUrl).toBe(C);
        controller.dispose();
    });

    it('prompts once for a missing default and runs every cell on the selected binding', async () => {
        const { daemon, controller, run } = setup();
        const a = notebook('a', undefined, ['sql', 'fetchxml']);
        daemon.authWho.mockResolvedValue(who(undefined));
        // Explicitly clear the default: the helper's default parameter otherwise supplies C.
        daemon.authWho.mockResolvedValue({ ...who(), environment: null });
        host.window.showQuickPick.mockImplementation(async (items: any[]) => items[1]);
        await run(a);
        expect(host.window.showQuickPick).toHaveBeenCalledTimes(1);
        expect(daemon.querySql.mock.calls[0][0].environmentUrl).toBe(A);
        expect(daemon.queryFetch.mock.calls[0][0].environmentUrl).toBe(A);
        controller.dispose();
    });

    it('does not query or fall back when a missing-default picker is cancelled', async () => {
        const { daemon, controller, run } = setup();
        daemon.authWho.mockResolvedValue({ ...who(), environment: null });
        host.window.showQuickPick.mockResolvedValue(undefined);
        await run(notebook('a'));
        expect(daemon.querySql).not.toHaveBeenCalled();
        expect(outputFor('a.ppdsnb')).toContain('No environment selected');
        controller.dispose();
    });

    it('does not inherit another run default while its missing-default picker is open', async () => {
        const { daemon, controller, run } = setup();
        daemon.authWho.mockResolvedValue({ ...who(), environment: null });
        const choice = deferred<any>();
        const opened = deferred<void>();
        host.window.showQuickPick.mockImplementation(() => { opened.resolve(); return choice.promise; });
        const a = run(notebook('a'));
        await opened.promise;
        daemon.authWho.mockResolvedValue(who(B));
        await run(notebook('b'));
        choice.resolve(undefined);
        await a;
        expect(daemon.querySql).toHaveBeenCalledTimes(1);
        expect(daemon.querySql.mock.calls[0][0].environmentUrl).toBe(B);
        expect(outputFor('a.ppdsnb')).toContain('No environment selected');
        controller.dispose();
    });

    it('cancels only one notebook and preserves another concurrent result', async () => {
        const { daemon, controller, run, interrupt } = setup();
        const a = notebook('a', A);
        const b = notebook('b', B);
        const pending = deferred<QueryResultResponse>();
        const started = deferred<void>();
        daemon.querySql.mockImplementationOnce(async () => { started.resolve(); return pending.promise; });
        const first = run(a);
        await started.promise;
        await run(b);
        interrupt(a);
        expect(daemon.querySql.mock.calls[0][1].isCancellationRequested).toBe(true);
        pending.resolve(result());
        await first;
        expect(controller.hasCellResults(a.getCells()[0].document.uri.toString())).toBe(false);
        expect(controller.hasCellResults(b.getCells()[0].document.uri.toString())).toBe(true);
        expect(outputFor('a.ppdsnb')).toContain('Query cancelled');
        expect(outputFor('a.ppdsnb')).toContain(A);
        controller.dispose();
    });

    it('queues runs within a notebook and clears queued work on interrupt', async () => {
        const { daemon, controller, run, interrupt } = setup();
        const a = notebook('a', A);
        const pending = deferred<QueryResultResponse>();
        const started = deferred<void>();
        daemon.querySql.mockImplementationOnce(async () => { started.resolve(); return pending.promise; });
        const first = run(a);
        await started.promise;
        const queued = run(a);
        interrupt(a);
        await queued;
        expect(daemon.querySql).toHaveBeenCalledTimes(1);
        pending.resolve(result());
        await first;
        await run(a);
        expect(daemon.querySql).toHaveBeenCalledTimes(2);
        controller.dispose();
    });

    it('resolves the queued run target when it starts', async () => {
        const { daemon, controller, run } = setup();
        const a = notebook('a', A);
        const pending = deferred<QueryResultResponse>();
        const started = deferred<void>();
        daemon.querySql.mockImplementationOnce(async () => { started.resolve(); return pending.promise; });
        const first = run(a);
        await started.promise;
        const queued = run(a);
        a.metadata.environmentUrl = B;
        pending.resolve(result());
        await Promise.all([first, queued]);
        expect(daemon.querySql.mock.calls.map(call => call[0].environmentUrl)).toEqual([A, B]);
        controller.dispose();
    });

    it('does not let a closed notebook late response overwrite a reopened notebook result', async () => {
        const { daemon, controller, run } = setup();
        const a = notebook('same', A);
        const pending = deferred<QueryResultResponse>();
        const started = deferred<void>();
        daemon.querySql.mockImplementationOnce(async () => { started.resolve(); return pending.promise; });
        const first = run(a);
        await started.promise;
        (a as any).isClosed = true;
        host.close?.(a);
        const reopened = notebook('same', B);
        await run(reopened);
        pending.resolve(result('old'));
        await first;
        expect(controller.getCellResults(reopened.getCells()[0].document.uri.toString())?.records[0].accountid).toBe('one');
        expect(controller.getCellExecutionContext(reopened.getCells()[0].document.uri.toString())?.context.environmentUrl).toBe(B);
        expect(host.executions[0].replaceOutput).not.toHaveBeenCalled();
        controller.dispose();
    });

    it('individual-cell cancellation allows later cells without cancelling another notebook', async () => {
        const { daemon, controller, run } = setup();
        const a = notebook('a', A, ['sql', 'sql']);
        const pending = deferred<QueryResultResponse>();
        const started = deferred<void>();
        daemon.querySql.mockImplementationOnce(async () => { started.resolve(); return pending.promise; });
        const first = run(a);
        await started.promise;
        host.executions[0].cancel();
        pending.resolve(result());
        await first;
        expect(daemon.querySql).toHaveBeenCalledTimes(2);
        expect(host.executions[0].end).toHaveBeenCalledWith(false, expect.any(Number));
        expect(host.executions[1].end).toHaveBeenCalledWith(true, expect.any(Number));
        controller.dispose();
    });

    it.each(['Production', 'Unknown'])('requires acknowledgement for %s and cancellation sends no query', async type => {
        const { daemon, controller, run } = setup();
        daemon.envConfigGet.mockResolvedValue({ type, resolvedType: type });
        host.window.showWarningMessage.mockResolvedValue(undefined);
        await run(notebook('a', A));
        expect(host.window.showWarningMessage.mock.calls[0][0]).toContain(A);
        expect(daemon.querySql).not.toHaveBeenCalled();
        controller.dispose();
    });

    it('scopes acknowledgement to notebook target profile and type without approving DML', async () => {
        const { daemon, controller, run } = setup();
        daemon.envConfigGet.mockResolvedValue({ type: 'Production', resolvedType: 'Production' });
        const a = notebook('a', A);
        await run(a);
        await run(a);
        expect(host.window.showWarningMessage).toHaveBeenCalledTimes(1);
        await run(notebook('b', A));
        expect(host.window.showWarningMessage).toHaveBeenCalledTimes(2);
        daemon.authWho.mockResolvedValue(who(C, 'other-profile'));
        await run(a);
        expect(host.window.showWarningMessage).toHaveBeenCalledTimes(3);
        daemon.envConfigGet.mockResolvedValue({ type: 'Unknown', resolvedType: 'Unknown' });
        await run(a);
        expect(host.window.showWarningMessage).toHaveBeenCalledTimes(4);
        a.metadata.environmentUrl = B;
        host.change?.({ notebook: a });
        await run(a);
        expect(host.window.showWarningMessage).toHaveBeenCalledTimes(5);
        // The environment acknowledgement never approves DML: every request stays unconfirmed.
        expect(daemon.querySql.mock.calls.every(call => call[0].dmlSafety?.isConfirmed === false)).toBe(true);
        expect(a.metadata).not.toHaveProperty('acknowledgement');
        controller.dispose();
    });

    it('ignores an environment type and acknowledgement claimed by notebook metadata', async () => {
        const { daemon, controller, run } = setup();
        daemon.envConfigGet.mockResolvedValue({ type: 'Production', resolvedType: 'Production' });
        const a = notebook('a', A);
        a.metadata.environmentType = 'Development';
        a.metadata.acknowledgement = true;
        await run(a);
        expect(host.window.showWarningMessage).toHaveBeenCalledTimes(1);
        controller.dispose();
    });

    it('does not query when interrupted during a protected-target prompt', async () => {
        const { daemon, controller, run, interrupt } = setup();
        daemon.envConfigGet.mockResolvedValue({ type: 'Production', resolvedType: 'Production' });
        const answer = deferred<string>();
        const opened = deferred<void>();
        host.window.showWarningMessage.mockImplementation(() => { opened.resolve(); return answer.promise; });
        const a = notebook('a', A);
        const pending = run(a);
        await opened.promise;
        interrupt(a);
        answer.resolve('Run on this environment');
        await pending;
        expect(daemon.querySql).not.toHaveBeenCalled();
        controller.dispose();
    });

    it('labels error and empty outputs with the resolved target and execution time', async () => {
        const { daemon, controller, run } = setup();
        daemon.querySql.mockRejectedValueOnce(new Error('fixture failure'));
        const a = notebook('a', A);
        await run(a);
        expect(outputFor('a.ppdsnb')).toContain('fixture failure');
        expect(outputFor('a.ppdsnb')).toContain(A);
        expect(outputFor('a.ppdsnb')).toMatch(/Executed \d{4}-\d{2}-\d{2}T/);
        (a.getCells()[0].document as any).getText = () => '';
        await run(a);
        expect(host.executions.at(-1).output[0].items[0].value).toContain(A);
        controller.dispose();
    });

    it('never invalidates a mutable profile alias after a captured run authentication failure', async () => {
        const { daemon, controller, run } = setup();
        daemon.querySql.mockImplementation(async () => {
            daemon.authWho.mockResolvedValue(who(B, 'replacement-profile'));
            throw new Error('token expired');
        });
        await run(notebook('a', A));
        expect(daemon.profilesInvalidate).not.toHaveBeenCalled();
        expect(host.window.showErrorMessage).not.toHaveBeenCalled();
        expect(outputFor('a.ppdsnb')).toContain('profile captured by this run (profile-one)');
        expect(outputFor('a.ppdsnb')).toContain(A);
        expect(outputFor('a.ppdsnb')).toContain('Confirm the intended profile');
        controller.dispose();
    });

    it('renders target labels as plain text and does not interpolate them into HTML', async () => {
        const { daemon, controller, run } = setup();
        daemon.envConfigGet.mockResolvedValue({ label: '<img src=x onerror=bad()>', resolvedType: 'Development' });
        await run(notebook('a', A));
        expect(host.executions[0].output[0].items[0]).toMatchObject({ mime: 'text/plain', value: expect.stringContaining('<img') });
        expect(host.executions[0].output[1].items[0].value).not.toContain('onerror=bad');
        controller.dispose();
    });

    it('disposal cancels active requests and clears queued runs', async () => {
        const { daemon, controller, run } = setup();
        const a = notebook('a', A);
        const pending = deferred<QueryResultResponse>();
        const started = deferred<void>();
        daemon.querySql.mockImplementationOnce(async () => { started.resolve(); return pending.promise; });
        const first = run(a);
        await started.promise;
        const queued = run(a);
        controller.dispose();
        await queued;
        expect(daemon.querySql.mock.calls[0][1].isCancellationRequested).toBe(true);
        pending.resolve(result());
        await first;
        expect(host.executions[0].replaceOutput).not.toHaveBeenCalled();
    });

    it('uses the active run context for completion and conversion', async () => {
        const { daemon, controller, run } = setup();
        const a = notebook('a', A);
        const pending = deferred<QueryResultResponse>();
        const started = deferred<void>();
        daemon.querySql.mockImplementationOnce(async () => { started.resolve(); return pending.promise; });
        const first = run(a);
        await started.promise;
        a.metadata.environmentUrl = B;
        expect((await controller.getNotebookQueryContext(a)).environmentUrl).toBe(A);
        pending.resolve(result());
        await first;
        expect((await controller.getNotebookQueryContext(a)).environmentUrl).toBe(B);
        controller.dispose();
    });
});

describe('DataverseNotebookController DML safety', () => {
    const CONFIRM = 'Execute Anyway';
    const SHOW = 'Show Full Statement';
    const CONTINUE = 'Continue to Confirmation';
    const confirmationRequired = () => new ResponseError(-32000, 'DML operations require --confirm to execute.', {
        code: 'Query.DmlConfirmationRequired', message: 'DML operations require --confirm to execute.', dmlConfirmationRequired: true,
    });
    const refused = (code: string, message: string, extra: Record<string, unknown> = {}) =>
        new ResponseError(-32000, message, { code, message, ...extra });

    /** Notebook whose cells hold the given SQL statements. */
    function sqlNotebook(name: string, statements: string[]): vscode.NotebookDocument {
        const nb = notebook(name, A, statements.map(() => 'sql'));
        nb.getCells().forEach((cell, i) => { (cell.document as any).getText = () => statements[i]; });
        return nb;
    }

    const isDml = (sql: string): boolean => /^\s*(delete|update|insert)/i.test(sql);

    /**
     * Mirrors today's daemon order: no-WHERE is refused before confirmation; every other unconfirmed
     * DML is answered confirmation-required; guards that run after planning (shakedown,
     * cross-environment policy) refuse only the confirmed request (`onConfirmed`).
     */
    function guardLikeDaemon(daemon: ReturnType<typeof setup>['daemon'], onConfirmed: (params: any) => QueryResultResponse = () => result()) {
        daemon.querySql.mockImplementation(async (params: any) => {
            if (/^\s*(delete|update)\b(?![\s\S]*\bwhere\b)/i.test(params.sql)) {
                throw refused('Query.DmlBlocked', 'DELETE without WHERE is not allowed.', { dmlBlocked: true });
            }
            if (!isDml(params.sql)) return result();
            if (params.dmlSafety?.isConfirmed !== true) throw confirmationRequired();
            return onConfirmed(params);
        });
    }

    const modalCalls = (): any[][] => host.window.showWarningMessage.mock.calls.filter(call => call[1]?.modal === true && call[0] !== undefined
        && !String(call[0]).startsWith('Run this notebook'));

    const confirmedCalls = (daemon: ReturnType<typeof setup>['daemon']): any[] =>
        daemon.querySql.mock.calls.map(call => call[0]).filter(request => request.dmlSafety?.isConfirmed === true);

    /** The review step is a quick pick; answer it with the item whose label is `label` (undefined = dismissed). */
    function answerReview(label: string | undefined) {
        host.window.showQuickPick.mockImplementation(async (items: any[]) => items.find(item => item.label === label));
    }

    /** Answers DML modals with `modal` (a value or per-call function) and the review quick pick with `review`. */
    function answerDml(modal: string | undefined | ((call: number) => string | undefined), review: string | undefined = CONTINUE) {
        let count = 0;
        host.window.showWarningMessage.mockImplementation(async (message: string, options?: any) => {
            if (String(message).startsWith('Run this notebook')) return 'Run on this environment';
            if (options?.modal) return typeof modal === 'function' ? modal(count++) : modal;
            return undefined;
        });
        answerReview(review);
    }

    function executionsFor(nb: vscode.NotebookDocument, index: number): any[] {
        const uri = nb.getCells()[index].document.uri.toString();
        return host.executions.filter(execution => execution.cell.document.uri.toString() === uri);
    }

    function cellText(nb: vscode.NotebookDocument, index: number): string {
        const execution = executionsFor(nb, index).at(-1);
        return execution.output.flatMap((output: any) => output.items.map((item: any) => item.value)).join('\n');
    }

    const endedWith = (nb: vscode.NotebookDocument, index: number, success: boolean) =>
        expect(executionsFor(nb, index).at(-1).end).toHaveBeenCalledWith(success, expect.any(Number));

    /** Opens a DML modal that stays open until the returned answer is resolved. */
    function holdModal() {
        const answer = deferred<string | undefined>();
        const opened = deferred<void>();
        host.window.showWarningMessage.mockImplementation((message: string) => {
            if (String(message).startsWith('Run this notebook')) return Promise.resolve('Run on this environment');
            opened.resolve();
            return answer.promise;
        });
        answerReview(CONTINUE);
        return { answer, opened };
    }

    it('asks once in a modal naming target, profile and statement, then sends the same text and target confirmed', async () => {
        const { daemon, controller, run } = setup();
        guardLikeDaemon(daemon);
        answerDml(CONFIRM);
        const nb = sqlNotebook('dml', ["DELETE FROM account WHERE name = 'x'"]);
        await run(nb);
        const [first, second] = daemon.querySql.mock.calls.map(call => call[0]);
        expect(first.dmlSafety).toEqual({ isConfirmed: false });
        expect(second.dmlSafety).toEqual({ isConfirmed: true });
        const { dmlSafety: _a, ...firstRest } = first;
        const { dmlSafety: _b, ...secondRest } = second;
        expect(secondRest).toEqual(firstRest);
        const [prompt] = modalCalls();
        expect(modalCalls()).toHaveLength(1);
        expect(prompt[0]).toContain('Environment 0');
        expect(prompt[1].detail).toContain(first.environmentUrl);
        expect(prompt[1].detail).toContain('Profile: ' + first.profileName);
        expect(prompt[1].detail).toContain(first.sql);
        expect(prompt[1].detail).toContain('also writes there');
        expect(prompt[1].detail).not.toContain('--confirm');
        // The action listed first never executes.
        expect(prompt.slice(2)).toEqual([SHOW, CONFIRM]);
        endedWith(nb, 0, true);
        controller.dispose();
    });

    it('asks again on every DML execution and never remembers a confirmation', async () => {
        const { daemon, controller, run } = setup();
        guardLikeDaemon(daemon);
        answerDml(CONFIRM);
        const nb = sqlNotebook('dml', ['UPDATE account SET name = 1 WHERE accountid = 1', 'UPDATE account SET name = 2 WHERE accountid = 2']);
        await run(nb);
        await run(nb);
        expect(modalCalls()).toHaveLength(4);
        expect(daemon.querySql.mock.calls.filter(call => call[0].dmlSafety.isConfirmed)).toHaveLength(4);
        controller.dispose();
    });

    it('declining runs nothing, marks the cell not executed and stops Run All', async () => {
        const { daemon, controller, run } = setup();
        guardLikeDaemon(daemon);
        answerDml(undefined);
        const nb = sqlNotebook('mixed', ['SELECT name FROM account', 'DELETE FROM account WHERE name = 1', 'SELECT name FROM contact']);
        await run(nb);
        expect(daemon.querySql.mock.calls.map(call => call[0].sql)).toEqual(['SELECT name FROM account', 'DELETE FROM account WHERE name = 1']);
        expect(daemon.querySql.mock.calls.some(call => call[0].dmlSafety?.isConfirmed === true)).toBe(false);
        endedWith(nb, 0, true);
        expect(cellText(nb, 1)).toContain('DML confirmation was declined');
        endedWith(nb, 1, false);
        expect(cellText(nb, 2)).toContain('Not run: this run stopped because a DML confirmation in an earlier cell was declined');
        endedWith(nb, 2, false);
        expect(controller.hasCellResults(nb.getCells()[1].document.uri.toString())).toBe(false);
        controller.dispose();
    });

    it('choosing Show Full Statement opens the exact text and asks again before anything runs', async () => {
        const { daemon, controller, run } = setup();
        guardLikeDaemon(daemon);
        answerDml(call => call === 0 ? SHOW : CONFIRM);
        const statement = 'UPDATE account SET name = 1 WHERE accountid = 1';
        await run(sqlNotebook('dml', [statement]));
        expect(host.openTextDocument).toHaveBeenCalledWith({ content: statement, language: 'sql' });
        expect(modalCalls()).toHaveLength(2);
        expect(daemon.querySql.mock.calls.filter(call => call[0].dmlSafety.isConfirmed)).toHaveLength(1);
        controller.dispose();
    });

    it('requires a long statement to be opened in full before Execute is offered, and shows its start and end', async () => {
        const { daemon, controller, run } = setup();
        guardLikeDaemon(daemon);
        const statement = ['-- padding', ...Array.from({ length: 60 }, (_, i) => `-- line ${i}`), 'DELETE FROM contact WHERE statecode = 0'].join('\n');
        answerDml(call => call === 0 ? SHOW : CONFIRM);
        await run(sqlNotebook('long', ['UPDATE account SET name = 1 WHERE accountid = 1\n' + statement]));
        const [first, second] = modalCalls();
        expect(first.slice(2)).toEqual([SHOW]);
        expect(first[0]).toContain('Review the full SQL');
        expect(first[1].detail).toContain('UPDATE account SET name = 1');
        expect(first[1].detail).toContain('DELETE FROM contact WHERE statecode = 0');
        expect(first[1].detail).toMatch(/\d+ character\(s\) on \d+ line\(s\) are hidden/);
        expect(first[1].detail).toContain('covers all of it');
        expect(second.slice(2)).toEqual([SHOW, CONFIRM]);
        expect(daemon.querySql.mock.calls.filter(call => call[0].dmlSafety.isConfirmed)).toHaveLength(1);
        controller.dispose();
    });

    it('never executes a long statement that was not opened in full, whatever the modal returns', async () => {
        const { daemon, controller, run } = setup();
        guardLikeDaemon(daemon);
        answerDml(CONFIRM);
        const statement = `UPDATE account SET name = '${'x'.repeat(3000)}' WHERE accountid = 1`;
        const nb = sqlNotebook('long', [statement]);
        await run(nb);
        expect(daemon.querySql.mock.calls.filter(call => call[0].dmlSafety.isConfirmed)).toHaveLength(0);
        expect(cellText(nb, 0)).toContain('declined');
        controller.dispose();
    });

    it('treats cancelling the review step as declining', async () => {
        const { daemon, controller, run } = setup();
        guardLikeDaemon(daemon);
        answerDml(SHOW, 'Cancel');
        const nb = sqlNotebook('dml', ['DELETE FROM account WHERE name = 1']);
        await run(nb);
        expect(daemon.querySql.mock.calls.filter(call => call[0].dmlSafety.isConfirmed)).toHaveLength(0);
        expect(cellText(nb, 0)).toContain('Not executed: cancelled at the review step');
        controller.dispose();
    });

    it('runs SELECT cells without a DML prompt and still sends unconfirmed safety options', async () => {
        const { daemon, controller, run } = setup();
        guardLikeDaemon(daemon);
        answerDml(CONFIRM);
        await run(sqlNotebook('select', ['SELECT name FROM account', 'SELECT name FROM contact']));
        expect(modalCalls()).toHaveLength(0);
        expect(daemon.querySql.mock.calls.map(call => call[0].dmlSafety)).toEqual([{ isConfirmed: false }, { isConfirmed: false }]);
        controller.dispose();
    });

    it('does not execute when the run is interrupted while the confirmation is open', async () => {
        const { daemon, controller, run, interrupt } = setup();
        guardLikeDaemon(daemon);
        const { answer, opened } = holdModal();
        const nb = sqlNotebook('dml', ['DELETE FROM account WHERE name = 1']);
        const pending = run(nb);
        await opened.promise;
        interrupt(nb);
        answer.resolve(CONFIRM);
        await pending;
        expect(daemon.querySql).toHaveBeenCalledTimes(1);
        expect(cellText(nb, 0)).toContain('Query cancelled');
        controller.dispose();
    });

    it('sends the confirmed request for the shown text and target even if the cell and binding change while the modal is open', async () => {
        const { daemon, controller, run } = setup();
        guardLikeDaemon(daemon);
        const { answer, opened } = holdModal();
        const nb = sqlNotebook('dml', ['DELETE FROM account WHERE name = 1']);
        const pending = run(nb);
        await opened.promise;
        (nb.getCells()[0].document as any).getText = () => 'DELETE FROM contact WHERE name = 2';
        nb.metadata.environmentUrl = B;
        host.change?.({ notebook: nb });
        daemon.authWho.mockResolvedValue(who(C, 'profile-two'));
        answer.resolve(CONFIRM);
        await pending;
        const [first, second] = daemon.querySql.mock.calls.map(call => call[0]);
        expect(second).toMatchObject({ sql: first.sql, environmentUrl: A, profileName: first.profileName, profileIdentity: first.profileIdentity, dmlSafety: { isConfirmed: true } });
        expect(second.sql).toBe('DELETE FROM account WHERE name = 1');
        controller.dispose();
    });

    it.each(['closed', 'disposed'])('sends nothing when the notebook is %s while the confirmation is open', async kind => {
        const { daemon, controller, run } = setup();
        guardLikeDaemon(daemon);
        const { answer, opened } = holdModal();
        const nb = sqlNotebook('dml', ['DELETE FROM account WHERE name = 1']);
        const pending = run(nb);
        await opened.promise;
        if (kind === 'closed') {
            (nb as any).isClosed = true;
            host.close?.(nb);
        } else {
            controller.dispose();
        }
        answer.resolve(CONFIRM);
        await pending;
        expect(daemon.querySql).toHaveBeenCalledTimes(1);
        controller.dispose();
    });

    it('refuses a missing WHERE before any prompt, shows the reason and stops the run', async () => {
        const { daemon, controller, run } = setup();
        guardLikeDaemon(daemon);
        answerDml(CONFIRM);
        const nb = sqlNotebook('refused', ['DELETE FROM account', 'SELECT name FROM account']);
        await run(nb);
        expect(modalCalls()).toHaveLength(0);
        const error = executionsFor(nb, 0).at(-1).output.at(-1).items[0];
        expect(error.mime).toBe('application/vnd.code.notebook.error');
        expect(error.value).toContain('DML safety guard refused');
        expect(error.value).toContain('DELETE without WHERE is not allowed.');
        endedWith(nb, 0, false);
        expect(daemon.querySql).toHaveBeenCalledTimes(1);
        expect(cellText(nb, 1)).toContain('Not run: this run stopped because the DML safety guard refused an earlier cell');
        controller.dispose();
    });

    it.each([
        ['cross-environment read-only', 'Query.DmlBlocked', 'Cross-environment DML is set to read-only. Source: [local], Target: [PROD].', { dmlBlocked: true }],
        ['shakedown', 'Safety.ShakedownActive', 'Mutations are disabled during shakedown.', {}],
        ['row cap (daemons that raise it)', 'Query.DmlRowCapExceeded', 'DML would affect more than 10000 rows.', {}],
    ])('shows a %s refusal of the confirmed request as an error and stops the run', async (_label, code, message, extra) => {
        const { daemon, controller, run } = setup();
        guardLikeDaemon(daemon, () => { throw refused(code, message, extra); });
        answerDml(CONFIRM);
        const nb = sqlNotebook('refused', ['DELETE FROM PROD.account WHERE name = 1', 'SELECT name FROM account']);
        await run(nb);
        expect(modalCalls()).toHaveLength(1);
        expect(daemon.querySql.mock.calls.map(call => call[0].dmlSafety)).toEqual([{ isConfirmed: false }, { isConfirmed: true }]);
        const error = executionsFor(nb, 0).at(-1).output.at(-1).items[0];
        expect(error.mime).toBe('application/vnd.code.notebook.error');
        expect(error.value).toContain('DML safety guard refused');
        expect(error.value).toContain(message);
        endedWith(nb, 0, false);
        expect(cellText(nb, 1)).toContain('Not run');
        controller.dispose();
    });

    it('shows a confirmed request that is still confirmation-required as an error instead of prompting again', async () => {
        const { daemon, controller, run } = setup();
        daemon.querySql.mockRejectedValue(confirmationRequired());
        answerDml(CONFIRM);
        const nb = sqlNotebook('dml', ['DELETE FROM account WHERE name = 1']);
        await run(nb);
        expect(modalCalls()).toHaveLength(1);
        expect(daemon.querySql).toHaveBeenCalledTimes(2);
        expect(executionsFor(nb, 0).at(-1).output.some((output: any) => output.items[0].mime === 'application/vnd.code.notebook.error')).toBe(true);
        // The daemon rejected it before execution, so it is not described as possibly applied.
        expect(cellText(nb, 0)).not.toContain('may have partly applied');
        endedWith(nb, 0, false);
        controller.dispose();
    });

    it('renders failed DML rows with counts and the reason a daemon reports, and stops the run', async () => {
        const { daemon, controller, run } = setup();
        guardLikeDaemon(daemon, () => ({
            ...result(), success: false, columns: [], records: [{ affected_rows: 2, failed_rows: 3 }],
            affectedRows: 2, failedRows: 3, dmlError: 'UPDATE account: 3 row(s) failed. First error: Access denied.',
        }));
        answerDml(CONFIRM);
        const nb = sqlNotebook('failed', ['UPDATE account SET name = 1 WHERE statecode = 0', 'SELECT name FROM account']);
        await run(nb);
        const error = executionsFor(nb, 0).at(-1).output[1].items[0];
        expect(error.mime).toBe('application/vnd.code.notebook.error');
        expect(error.value).toContain('3 row(s) failed');
        expect(error.value).toContain('2 row(s) succeeded');
        expect(error.value).toContain('Access denied');
        endedWith(nb, 0, false);
        expect(cellText(nb, 1)).toContain('Not run: this run stopped because rows failed in an earlier DML cell');
        controller.dispose();
    });

    it('reads the one-row DML summary of a confirmed request as failed rows even when success is true', async () => {
        const { daemon, controller, run } = setup();
        guardLikeDaemon(daemon, () => ({ ...result(), columns: [], records: [{ affected_rows: 0, failed_rows: 4 }] }));
        answerDml(CONFIRM);
        const nb = sqlNotebook('legacy', ['DELETE FROM account WHERE name = 1']);
        await run(nb);
        expect(cellText(nb, 0)).toContain('4 row(s) failed');
        expect(cellText(nb, 0)).toContain('did not report a failure reason');
        endedWith(nb, 0, false);
        controller.dispose();
    });

    it('never flags a SELECT whose columns look like the DML summary', async () => {
        const { daemon, controller, run } = setup();
        daemon.querySql.mockResolvedValue({ ...result(), columns: [], records: [{ affected_rows: 1, failed_rows: 3 }] });
        const nb = sqlNotebook('select', ['SELECT 1 AS affected_rows, 3 AS failed_rows']);
        await run(nb);
        expect(cellText(nb, 0)).not.toContain('DML failed');
        endedWith(nb, 0, true);
        controller.dispose();
    });

    it('marks skipped cells without touching another cell when one cannot be marked', async () => {
        const { daemon, controller, run } = setup();
        guardLikeDaemon(daemon);
        answerDml(undefined);
        const nb = sqlNotebook('mixed', ['SELECT name FROM account', 'DELETE FROM account WHERE name = 1', 'SELECT 2', 'SELECT 3']);
        const native = host.controllers.at(-1);
        const original = native.createNotebookCellExecution.getMockImplementation();
        native.createNotebookCellExecution.mockImplementation((cell: any) => {
            if (cell === nb.getCells()[2]) throw new Error('cell was removed');
            return original(cell);
        });
        await run(nb);
        expect(cellText(nb, 0)).not.toContain('cell was removed');
        endedWith(nb, 0, true);
        expect(cellText(nb, 3)).toContain('Not run');
        endedWith(nb, 3, false);
        controller.dispose();
    });

    it('keeps the review prompt open until answered and treats dismissing it as declining', async () => {
        const { daemon, controller, run } = setup();
        guardLikeDaemon(daemon);
        answerDml(SHOW);
        answerReview(undefined);
        const nb = sqlNotebook('dml', ['DELETE FROM account WHERE name = 1', 'SELECT 2']);
        await run(nb);
        const [items, options, token] = host.window.showQuickPick.mock.calls.at(-1)!;
        expect(items.map((item: any) => item.label)).toEqual([CONTINUE, 'Cancel']);
        expect(options).toMatchObject({ ignoreFocusOut: true });
        expect(token).toHaveProperty('isCancellationRequested');
        expect(confirmedCalls(daemon)).toHaveLength(0);
        expect(cellText(nb, 0)).toContain('review prompt was dismissed, which counts as declining');
        expect(cellText(nb, 1)).toContain('Not run');
        controller.dispose();
    });

    it('closes the review prompt when the run is interrupted', async () => {
        const { daemon, controller, run, interrupt } = setup();
        guardLikeDaemon(daemon);
        answerDml(SHOW);
        const opened = deferred<any>();
        host.window.showQuickPick.mockImplementation((_items: any[], _options: any, token: any) => {
            opened.resolve(token);
            return new Promise(() => undefined);
        });
        const nb = sqlNotebook('dml', ['DELETE FROM account WHERE name = 1']);
        const pending = run(nb);
        const token = await opened.promise;
        interrupt(nb);
        await pending;
        expect(token.isCancellationRequested).toBe(true);
        expect(confirmedCalls(daemon)).toHaveLength(0);
        expect(cellText(nb, 0)).toContain('Query cancelled. No records were changed.');
        controller.dispose();
    });

    it('opens the full statement through the read-only document provider when it is registered', async () => {
        const { daemon, controller, run } = setup();
        guardLikeDaemon(daemon);
        const show = vi.fn().mockResolvedValue(undefined);
        ExplainDocumentProvider.instance = { show } as unknown as ExplainDocumentProvider;
        try {
            answerDml(call => call === 0 ? SHOW : CONFIRM);
            const statement = 'UPDATE account SET name = 1 WHERE accountid = 1';
            await run(sqlNotebook('dml', [statement]));
            expect(show).toHaveBeenCalledWith(statement, 'sql', 'DML to confirm');
            expect(host.openTextDocument).not.toHaveBeenCalled();
            expect(host.window.showQuickPick.mock.calls.at(-1)![1].placeHolder).not.toContain('copy');
            expect(confirmedCalls(daemon)).toHaveLength(1);
        } finally {
            ExplainDocumentProvider.instance = undefined;
        }
        controller.dispose();
    });

    it('says the fallback editor is a copy whose edits are not executed', async () => {
        const { daemon, controller, run } = setup();
        guardLikeDaemon(daemon);
        answerDml(call => call === 0 ? SHOW : CONFIRM);
        await run(sqlNotebook('dml', ['UPDATE account SET name = 1 WHERE accountid = 1']));
        expect(host.window.showQuickPick.mock.calls.at(-1)![1].placeHolder).toContain('copy for reading only: edits there are not executed');
        controller.dispose();
    });

    it('reports a failure to open the full statement as that failure, and runs nothing', async () => {
        const { daemon, controller, run } = setup();
        guardLikeDaemon(daemon);
        answerDml(SHOW);
        host.openTextDocument.mockRejectedValueOnce(new Error('no editor available'));
        const nb = sqlNotebook('dml', ['DELETE FROM account WHERE name = 1', 'SELECT 2']);
        await run(nb);
        expect(confirmedCalls(daemon)).toHaveLength(0);
        const error = executionsFor(nb, 0).at(-1).output.at(-1).items[0];
        expect(error.mime).toBe('application/vnd.code.notebook.error');
        expect(error.value).toContain('the full statement could not be opened (no editor available)');
        expect(error.value).not.toContain('declined');
        expect(cellText(nb, 1)).toContain('Not run: this run stopped because the DML confirmation in an earlier cell could not be completed');
        controller.dispose();
    });

    it('reports a rejected confirmation dialog as a failure, and runs nothing', async () => {
        const { daemon, controller, run } = setup();
        guardLikeDaemon(daemon);
        host.window.showWarningMessage.mockImplementation(async (message: string, options?: any) => {
            if (String(message).startsWith('Run this notebook')) return 'Run on this environment';
            if (options?.modal) throw new Error('dialog service unavailable');
            return undefined;
        });
        const nb = sqlNotebook('dml', ['DELETE FROM account WHERE name = 1']);
        await run(nb);
        expect(confirmedCalls(daemon)).toHaveLength(0);
        expect(cellText(nb, 0)).toContain('the DML confirmation could not be shown (dialog service unavailable)');
        controller.dispose();
    });

    it('lists every reported write target, marking other and production environments', async () => {
        const { daemon, controller, run } = setup();
        daemon.querySql.mockImplementation(async (params: any) => {
            if (params.dmlSafety?.isConfirmed) return result();
            throw new ResponseError(-32000, 'confirm', {
                code: 'Query.DmlConfirmationRequired', dmlConfirmationRequired: true,
                dmlTargets: [
                    { label: 'local', url: A + '/', type: 'Development', protection: 'Development' },
                    { label: 'PROD', url: 'https://prod.crm.dynamics.com', type: 'Production', protection: 'Production' },
                ],
            });
        });
        answerDml(CONFIRM);
        await run(sqlNotebook('cross', ['DELETE FROM PROD.account WHERE name = 1']));
        const [prompt] = modalCalls();
        expect(prompt[0]).toContain('and 1 other environment(s)');
        expect(prompt[1].detail).toContain('Writes to 2 environment(s):');
        expect(prompt[1].detail).toMatch(/local .*\[this environment\]/);
        expect(prompt[1].detail).toMatch(/PROD .*https:\/\/prod\.crm\.dynamics\.com \[OTHER ENVIRONMENT; PRODUCTION \/ PROTECTED\]/);
        expect(prompt[1].detail).not.toContain('are not listed here');
        controller.dispose();
    });

    it('says other named environments are not listed when the daemon does not report write targets', async () => {
        const { daemon, controller, run } = setup();
        guardLikeDaemon(daemon);
        answerDml(CONFIRM);
        await run(sqlNotebook('dml', ['DELETE FROM account WHERE name = 1']));
        expect(modalCalls()[0][1].detail).toContain('other named environments are not listed here');
        controller.dispose();
    });

    it('shows control and bidirectional characters visibly but sends the captured text unchanged', async () => {
        const { daemon, controller, run } = setup();
        guardLikeDaemon(daemon);
        answerDml(CONFIRM);
        const statement = 'DELETE FROM account WHERE name = \u202E1 = 1 --\u202C';
        await run(sqlNotebook('bidi', [statement]));
        const detail = modalCalls()[0][1].detail;
        expect(detail).toContain('<U+202E>');
        expect(detail).not.toContain('\u202E');
        expect(detail).toContain('shown as <U+XXXX>. The statement that runs is unchanged.');
        expect(confirmedCalls(daemon)[0].sql).toBe(statement);
        controller.dispose();
    });

    it('keeps the result and says records may have changed when cancelled after the confirmed request returned', async () => {
        const { daemon, controller, run, interrupt } = setup();
        const nb = sqlNotebook('late', ['UPDATE account SET name = 1 WHERE statecode = 0', 'SELECT 2']);
        guardLikeDaemon(daemon, () => {
            interrupt(nb);
            return { ...result(), columns: [], records: [{ affected_rows: 9000, failed_rows: 40 }] };
        });
        answerDml(CONFIRM);
        await run(nb);
        const text = cellText(nb, 0);
        expect(text).toContain('Cancelled after the confirmed statement was submitted: records may already have been changed.');
        expect(text).toContain('40 row(s) failed');
        expect(executionsFor(nb, 0).at(-1).output.some((output: any) => output.items[0].mime === 'text/html')).toBe(true);
        expect(controller.hasCellResults(nb.getCells()[0].document.uri.toString())).toBe(true);
        // Interrupt ends the whole run: later cells are not touched (and not marked).
        expect(executionsFor(nb, 1)).toHaveLength(0);
        controller.dispose();
    });

    it('says records may have changed when the confirmed request is cancelled in flight', async () => {
        const { daemon, controller, run, interrupt } = setup();
        const nb = sqlNotebook('inflight', ['DELETE FROM account WHERE name = 1']);
        guardLikeDaemon(daemon, () => {
            interrupt(nb);
            throw new Error('Request cancelled');
        });
        answerDml(CONFIRM);
        await run(nb);
        expect(cellText(nb, 0)).toContain('records may already have been changed. Check the data before running it again.');
        controller.dispose();
    });

    it.each([
        ['a generic error', () => { throw new Error('connection reset'); }, 'an earlier DML cell failed', 'may have partly applied'],
        ['an authentication error', () => { throw new Error('token expired'); }, 'an earlier DML cell failed', 'may have partly applied'],
        ['success: false without counts', () => ({ ...result(), success: false }), 'an earlier DML cell did not succeed', undefined],
    ])('stops the run when the confirmed request ends with %s', async (_label, onConfirmed, reason, note) => {
        const { daemon, controller, run } = setup();
        guardLikeDaemon(daemon, onConfirmed as any);
        answerDml(CONFIRM);
        const nb = sqlNotebook('failing', ['DELETE FROM account WHERE name = 1', 'SELECT 2']);
        await run(nb);
        endedWith(nb, 0, false);
        if (note) expect(cellText(nb, 0)).toContain(note);
        expect(cellText(nb, 1)).toContain(`Not run: this run stopped because ${reason}`);
        expect(daemon.querySql.mock.calls.map(call => call[0].sql)).not.toContain('SELECT 2');
        controller.dispose();
    });

    it('stops the run when a DML cell is cancelled through its own execution token during the confirmation', async () => {
        const { daemon, controller, run } = setup();
        guardLikeDaemon(daemon);
        const { answer, opened } = holdModal();
        const nb = sqlNotebook('dml', ['DELETE FROM account WHERE name = 1', 'SELECT 2']);
        const pending = run(nb);
        await opened.promise;
        executionsFor(nb, 0).at(-1).cancel();
        answer.resolve(CONFIRM);
        await pending;
        expect(confirmedCalls(daemon)).toHaveLength(0);
        expect(cellText(nb, 1)).toContain('Not run: this run stopped because an earlier DML cell was cancelled');
        controller.dispose();
    });

    it('leaves later cells untouched when the notebook is interrupted during a DML confirmation', async () => {
        const { daemon, controller, run, interrupt } = setup();
        guardLikeDaemon(daemon);
        const { answer, opened } = holdModal();
        const nb = sqlNotebook('dml', ['DELETE FROM account WHERE name = 1', 'SELECT 2']);
        const pending = run(nb);
        await opened.promise;
        interrupt(nb);
        answer.resolve(CONFIRM);
        await pending;
        expect(executionsFor(nb, 1)).toHaveLength(0);
        expect(daemon.querySql).toHaveBeenCalledTimes(1);
        controller.dispose();
    });

    function targetsDaemon(daemon: ReturnType<typeof setup>['daemon'], dmlTargets: unknown) {
        daemon.querySql.mockImplementation(async (params: any) => {
            if (params.dmlSafety?.isConfirmed) return result();
            throw new ResponseError(-32000, 'confirm', { code: 'Query.DmlConfirmationRequired', dmlConfirmationRequired: true, dmlTargets });
        });
    }

    it('says reported but unreadable write targets could not be read', async () => {
        const { daemon, controller, run } = setup();
        targetsDaemon(daemon, []);
        answerDml(CONFIRM);
        await run(sqlNotebook('cross', ['DELETE FROM PROD.account WHERE name = 1']));
        const detail = modalCalls()[0][1].detail;
        expect(detail).toContain('The daemon reported write targets, but they could not be read');
        expect(detail).not.toContain('This daemon does not report');
        controller.dispose();
    });

    it('marks a target with no type as protected and a target with no URL as another environment', async () => {
        const { daemon, controller, run } = setup();
        const ls = String.fromCharCode(0x2028);
        targetsDaemon(daemon, [{ label: `REMOTE${ls}fake line`, url: 'https://remote.crm.dynamics.com' }, { label: 'NOURL', type: 'Development' }]);
        answerDml(CONFIRM);
        await run(sqlNotebook('cross', ['DELETE FROM REMOTE.account WHERE name = 1']));
        const [prompt] = modalCalls();
        expect(prompt[1].detail).toMatch(/REMOTE<U\+2028>fake line https:\/\/remote\.crm\.dynamics\.com \[OTHER ENVIRONMENT; TYPE UNKNOWN, TREATED AS PROTECTED\]/);
        expect(prompt[1].detail).toMatch(/NOURL \(Development\) \[OTHER ENVIRONMENT\]/);
        expect(prompt[1].detail).not.toContain(ls);
        expect(prompt[0]).toContain('and 2 other environment(s)');
        controller.dispose();
    });

    it('opens no confirmation when the notebook is interrupted before the modal', async () => {
        const { daemon, controller, run, interrupt } = setup();
        guardLikeDaemon(daemon);
        answerDml(CONFIRM);
        const nb = sqlNotebook('dml', ['DELETE FROM account WHERE name = 1']);
        const native = host.controllers.at(-1);
        const original = native.createNotebookCellExecution.getMockImplementation();
        native.createNotebookCellExecution.mockImplementation((cell: any) => {
            const execution = original(cell);
            const replace = execution.replaceOutput;
            execution.replaceOutput = vi.fn(async (outputs: any[]) => {
                if (outputs.some(output => output.items.some((item: any) => String(item.value).includes('Waiting for DML confirmation')))) interrupt(nb);
                return replace(outputs);
            });
            return execution;
        });
        await run(nb);
        expect(modalCalls()).toHaveLength(0);
        expect(confirmedCalls(daemon)).toHaveLength(0);
        controller.dispose();
    });
});
