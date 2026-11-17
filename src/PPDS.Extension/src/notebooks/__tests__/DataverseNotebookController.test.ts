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
}));

vi.mock('vscode', () => ({
    NotebookCellKind: { Markup: 1, Code: 2 },
    StatusBarAlignment: { Right: 2 },
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
    },
    workspace: {
        get notebookDocuments() { return host.notebooks; },
        onDidOpenNotebookDocument: () => ({ dispose: vi.fn() }),
        onDidChangeNotebookDocument: (callback: any) => { host.change = callback; return { dispose: vi.fn() }; },
        onDidCloseNotebookDocument: (callback: any) => { host.close = callback; return { dispose: vi.fn() }; },
        onDidChangeTextDocument: () => ({ dispose: vi.fn() }),
        applyEdit: (...args: any[]) => host.applyEdit(...args),
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
    const confirmationRequired = () => new ResponseError(-32000, 'DML operations require --confirm to execute.', {
        code: 'Query.DmlConfirmationRequired', message: 'DML operations require --confirm to execute.', dmlConfirmationRequired: true,
    });

    /** Notebook whose cells hold the given SQL statements. */
    function sqlNotebook(name: string, statements: string[]): vscode.NotebookDocument {
        const nb = notebook(name, A, statements.map(() => 'sql'));
        nb.getCells().forEach((cell, i) => { (cell.document as any).getText = () => statements[i]; });
        return nb;
    }

    /** Mirrors the daemon guard: an unconfirmed DML statement is answered with confirmation-required. */
    function guardLikeDaemon(daemon: ReturnType<typeof setup>['daemon'], response: QueryResultResponse = result()) {
        daemon.querySql.mockImplementation(async (params: any) => {
            if (/^\s*(delete|update|insert)/i.test(params.sql) && params.dmlSafety?.isConfirmed !== true) throw confirmationRequired();
            return response;
        });
    }

    function dmlPrompts(): any[][] {
        return host.window.showWarningMessage.mock.calls.filter(call => String(call[0]).startsWith('Execute this DML statement'));
    }

    function answerDml(answer: string | undefined) {
        host.window.showWarningMessage.mockImplementation(async (message: string) =>
            message.startsWith('Execute this DML statement') ? answer : 'Run on this environment');
    }

    function executionsFor(nb: vscode.NotebookDocument, index: number): any[] {
        const uri = nb.getCells()[index].document.uri.toString();
        return host.executions.filter(execution => execution.cell.document.uri.toString() === uri);
    }

    function cellText(nb: vscode.NotebookDocument, index: number): string {
        const execution = executionsFor(nb, index).at(-1);
        return execution.output.flatMap((output: any) => output.items.map((item: any) => item.value)).join('\n');
    }

    it('asks for modal confirmation showing the statement and target, then sends a confirmed request', async () => {
        const { daemon, controller, run } = setup();
        guardLikeDaemon(daemon);
        answerDml(CONFIRM);
        const nb = sqlNotebook('dml', ["DELETE FROM account WHERE name = 'x'"]);
        await run(nb);
        expect(daemon.querySql.mock.calls.map(call => call[0].dmlSafety)).toEqual([{ isConfirmed: false }, { isConfirmed: true }]);
        const [prompt] = dmlPrompts();
        expect(prompt[0]).toContain('Environment 0');
        expect(prompt[1]).toMatchObject({ modal: true });
        expect(prompt[1].detail).toContain(A);
        expect(prompt[1].detail).toContain("DELETE FROM account WHERE name = 'x'");
        expect(prompt.slice(2)).toEqual([CONFIRM]);
        expect(executionsFor(nb, 0).at(-1).end).toHaveBeenCalledWith(true, expect.any(Number));
        controller.dispose();
    });

    it('asks again on every DML execution and never remembers a confirmation', async () => {
        const { daemon, controller, run } = setup();
        guardLikeDaemon(daemon);
        answerDml(CONFIRM);
        const nb = sqlNotebook('dml', ['UPDATE account SET name = 1 WHERE accountid = 1', 'UPDATE account SET name = 2 WHERE accountid = 2']);
        await run(nb);
        await run(nb);
        expect(dmlPrompts()).toHaveLength(4);
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
        expect(executionsFor(nb, 0).at(-1).end).toHaveBeenCalledWith(true, expect.any(Number));
        expect(cellText(nb, 1)).toContain('DML confirmation was declined');
        expect(executionsFor(nb, 1).at(-1).end).toHaveBeenCalledWith(false, expect.any(Number));
        expect(cellText(nb, 2)).toContain('Not run');
        expect(executionsFor(nb, 2).at(-1).end).toHaveBeenCalledWith(false, expect.any(Number));
        expect(controller.hasCellResults(nb.getCells()[1].document.uri.toString())).toBe(false);
        controller.dispose();
    });

    it('runs SELECT cells without a DML prompt and still sends unconfirmed safety options', async () => {
        const { daemon, controller, run } = setup();
        guardLikeDaemon(daemon);
        answerDml(CONFIRM);
        await run(sqlNotebook('select', ['SELECT name FROM account', 'SELECT name FROM contact']));
        expect(dmlPrompts()).toHaveLength(0);
        expect(daemon.querySql.mock.calls.map(call => call[0].dmlSafety)).toEqual([{ isConfirmed: false }, { isConfirmed: false }]);
        controller.dispose();
    });

    it('does not execute when the run is interrupted while the confirmation is open', async () => {
        const { daemon, controller, run, interrupt } = setup();
        guardLikeDaemon(daemon);
        const answer = deferred<string>();
        const opened = deferred<void>();
        host.window.showWarningMessage.mockImplementation((message: string) => {
            if (!message.startsWith('Execute this DML statement')) return Promise.resolve('Run on this environment');
            opened.resolve();
            return answer.promise;
        });
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

    it.each([
        ['missing WHERE', 'Query.DmlBlocked', 'DELETE without WHERE is not allowed.', { dmlBlocked: true }],
        ['protected target', 'Query.DmlBlocked', 'Cross-environment DML is set to read-only.', { dmlBlocked: true }],
        ['row cap', 'Query.DmlRowCapExceeded', 'DML would affect more than 10000 rows.', {}],
        ['shakedown', 'Safety.ShakedownActive', 'Mutations are disabled during shakedown.', {}],
    ])('renders the %s refusal as an error without prompting', async (_label, code, message, extra) => {
        const { daemon, controller, run } = setup();
        daemon.querySql.mockRejectedValue(new ResponseError(-32000, message, { code, message, ...extra }));
        const nb = sqlNotebook('refused', ['DELETE FROM account']);
        await run(nb);
        expect(dmlPrompts()).toHaveLength(0);
        const error = executionsFor(nb, 0).at(-1).output.at(-1).items[0];
        expect(error.mime).toBe('application/vnd.code.notebook.error');
        expect(error.value).toContain('DML safety guard refused');
        expect(error.value).toContain(message);
        expect(executionsFor(nb, 0).at(-1).end).toHaveBeenCalledWith(false, expect.any(Number));
        controller.dispose();
    });

    it('shows a confirmed request that is still confirmation-required as an error instead of prompting again', async () => {
        const { daemon, controller, run } = setup();
        daemon.querySql.mockRejectedValue(confirmationRequired());
        answerDml(CONFIRM);
        const nb = sqlNotebook('dml', ['DELETE FROM account WHERE name = 1']);
        await run(nb);
        expect(dmlPrompts()).toHaveLength(1);
        expect(daemon.querySql).toHaveBeenCalledTimes(2);
        expect(executionsFor(nb, 0).at(-1).output.at(-1).items[0].mime).toBe('application/vnd.code.notebook.error');
        expect(executionsFor(nb, 0).at(-1).end).toHaveBeenCalledWith(false, expect.any(Number));
        controller.dispose();
    });

    it('renders failed DML rows with counts and the reason reported by the daemon', async () => {
        const { daemon, controller, run } = setup();
        guardLikeDaemon(daemon, {
            ...result(), success: false, columns: [], records: [{ affected_rows: 2, failed_rows: 3 }],
            affectedRows: 2, failedRows: 3, dmlError: 'UPDATE account: 3 row(s) failed. First error: Access denied.',
        });
        answerDml(CONFIRM);
        const nb = sqlNotebook('failed', ['UPDATE account SET name = 1 WHERE statecode = 0']);
        await run(nb);
        const error = executionsFor(nb, 0).at(-1).output[1].items[0];
        expect(error.mime).toBe('application/vnd.code.notebook.error');
        expect(error.value).toContain('3 row(s) failed');
        expect(error.value).toContain('2 row(s) succeeded');
        expect(error.value).toContain('Access denied');
        expect(executionsFor(nb, 0).at(-1).end).toHaveBeenCalledWith(false, expect.any(Number));
        controller.dispose();
    });

    it('treats a legacy DML summary row with failed rows as a failure even when success is true', async () => {
        const { daemon, controller, run } = setup();
        guardLikeDaemon(daemon, { ...result(), columns: [], records: [{ affected_rows: 0, failed_rows: 4 }] });
        answerDml(CONFIRM);
        const nb = sqlNotebook('legacy', ['DELETE FROM account WHERE name = 1']);
        await run(nb);
        expect(cellText(nb, 0)).toContain('4 row(s) failed');
        expect(cellText(nb, 0)).toContain('did not report a failure reason');
        expect(executionsFor(nb, 0).at(-1).end).toHaveBeenCalledWith(false, expect.any(Number));
        controller.dispose();
    });
});
