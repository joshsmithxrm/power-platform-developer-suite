import { afterEach, describe, expect, it, vi } from 'vitest';
import * as vscode from 'vscode';
import type { DaemonClient } from '../../daemonClient.js';
import type { DataverseNotebookController, NotebookExecutionContext } from '../../notebooks/DataverseNotebookController.js';
import { openCellInDataExplorer } from '../../commands/notebookCommands.js';
import { QueryPanel } from '../../panels/QueryPanel.js';

const host = vi.hoisted(() => ({ panels: [] as any[], warning: vi.fn(), activeEditor: undefined as any }));
vi.mock('vscode', () => ({
    ViewColumn: { One: 1, Beside: 2 }, NotebookCellKind: { Code: 2 },
    Uri: { joinPath: (...parts: unknown[]) => parts.join('/'), parse: (value: string) => ({ scheme: new URL(value).protocol.replace(':', '') }) },
    window: {
        get activeNotebookEditor() { return host.activeEditor; },
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

const target: NotebookExecutionContext = {
    environmentUrl: 'https://executed.crm.dynamics.com', environmentName: 'Executed', environmentType: 'Development',
    profileName: 'captured-profile', profileIdentity: 'captured-identity',
};
const panels: QueryPanel[] = [];
function setupPanel(context = target): { panel: any; daemon: any } {
    const queryResult = { records: [], columns: [], success: true, count: 0 };
    const daemon = {
        onDidReconnect: vi.fn(() => ({ dispose: vi.fn() })),
        authList: vi.fn().mockResolvedValue({ profiles: [{ name: 'different-active', isActive: true }] }),
        authWho: vi.fn(), profilesInvalidate: vi.fn(),
        envConfigGet: vi.fn().mockResolvedValue({ resolvedType: 'Development' }),
        querySql: vi.fn().mockResolvedValue(queryResult), queryFetch: vi.fn().mockResolvedValue(queryResult),
        queryComplete: vi.fn().mockResolvedValue({ items: [] }), queryExplain: vi.fn().mockResolvedValue({ plan: '<fetch/>', fetchXml: '<fetch/>' }),
    };
    const panel = QueryPanel.show('test:' as unknown as vscode.Uri, daemon as unknown as DaemonClient, 'SELECT name FROM account', context.environmentUrl, context.environmentName, context);
    panels.push(panel);
    return { panel, daemon };
}

afterEach(() => {
    for (const panel of panels.splice(0)) panel.dispose();
    host.panels.length = 0;
    host.activeEditor = undefined;
    vi.clearAllMocks();
});

describe('Notebook to Data Explorer target transfer', () => {
    it('preserves executed context when the notebook has since changed binding', async () => {
        const cell = { kind: 2, document: { uri: { toString: () => 'cell' }, getText: () => 'SELECT name FROM account' } };
        host.activeEditor = { notebook: { notebookType: 'ppdsnb', cellAt: () => cell }, selections: [{ start: 0 }] };
        const controller = { getCellExecutionContext: vi.fn(() => ({ context: target })), getNotebookQueryContext: vi.fn() };
        const open = vi.fn();
        await openCellInDataExplorer(controller as unknown as DataverseNotebookController, open);
        expect(open).toHaveBeenCalledWith('SELECT name FROM account', target);
        expect(controller.getNotebookQueryContext).not.toHaveBeenCalled();
    });

    it('resolves a not-yet-executed notebook and refuses transfer if target resolution fails', async () => {
        const cell = { kind: 2, document: { uri: { toString: () => 'cell' }, getText: () => 'SELECT name FROM account' } };
        const notebook = { notebookType: 'ppdsnb', cellAt: () => cell };
        host.activeEditor = { notebook, selections: [{ start: 0 }] };
        const controller = { getCellExecutionContext: vi.fn(), getNotebookQueryContext: vi.fn().mockResolvedValue(target) };
        const open = vi.fn();
        await openCellInDataExplorer(controller as unknown as DataverseNotebookController, open);
        expect(controller.getNotebookQueryContext).toHaveBeenCalledWith(notebook);
        expect(open).toHaveBeenCalledWith('SELECT name FROM account', target);
        controller.getNotebookQueryContext.mockRejectedValueOnce(new Error('Removed profile'));
        await openCellInDataExplorer(controller as unknown as DataverseNotebookController, open);
        expect(open).toHaveBeenCalledTimes(1);
        expect(host.warning).toHaveBeenCalledWith(expect.stringContaining('Removed profile'));
    });

    it('retains the transferred profile across initialization, completion, conversion, SQL/FetchXML and paging', async () => {
        const { panel, daemon } = setupPanel();
        await panel.initEnvironment();
        expect(daemon.authList).not.toHaveBeenCalled();
        await panel.handleMessage({ command: 'requestCompletions', requestId: 1, sql: 'SELECT ', cursorOffset: 7 });
        await panel.handleMessage({ command: 'convertQuery', sql: 'SELECT name FROM account', toLanguage: 'xml' });
        await panel.handleMessage({ command: 'executeQuery', sql: 'SELECT name FROM account', language: 'sql' });
        await panel.loadMore('cookie', 2);
        await panel.handleMessage({ command: 'executeQuery', sql: '<fetch/>', language: 'xml' });
        await panel.loadMore('cookie', 2);
        for (const rpc of [daemon.querySql, daemon.queryFetch, daemon.queryComplete, daemon.queryExplain]) {
            expect(rpc).toHaveBeenCalled();
            for (const [request] of rpc.mock.calls) {
                expect(request).toMatchObject({ environmentUrl: target.environmentUrl, profileName: target.profileName, profileIdentity: target.profileIdentity });
            }
        }
    });

    it.each(['environment', 'profile'])('refuses mismatched %s reuse at the panel cap', kind => {
        const { daemon } = setupPanel();
        for (let i = 1; i < 10; i++) setupPanel();
        const first = host.panels[0];
        first.webview.postMessage.mockClear();
        const context = kind === 'environment' ? { ...target, environmentUrl: 'https://different.crm.dynamics.com' }
            : { ...target, profileIdentity: 'different-identity' };
        QueryPanel.show('test:' as unknown as vscode.Uri, daemon, 'new query', context.environmentUrl, context.environmentName, context);
        expect(host.panels).toHaveLength(10);
        expect(first.webview.postMessage).not.toHaveBeenCalled();
        expect(host.warning).toHaveBeenCalledWith(expect.stringContaining('different environment or profile'));
    });

    it('never invalidates the global active profile to recover a captured notebook profile', async () => {
        const { panel, daemon } = setupPanel();
        daemon.querySql.mockRejectedValueOnce(new Error('token expired'));
        await panel.handleMessage({ command: 'executeQuery', sql: 'SELECT name FROM account' });
        expect(daemon.authWho).not.toHaveBeenCalled();
        expect(daemon.profilesInvalidate).not.toHaveBeenCalled();
        expect(host.panels[0].webview.postMessage).toHaveBeenCalledWith({ command: 'queryError', error: expect.stringContaining('captured notebook profile on ' + target.environmentUrl) });
    });

    it('can reuse a panel at the cap only when the captured target and profile match', () => {
        const { daemon } = setupPanel();
        for (let i = 1; i < 10; i++) setupPanel();
        QueryPanel.show('test:' as unknown as vscode.Uri, daemon, 'new query', target.environmentUrl + '/', target.environmentName, target);
        expect(host.panels).toHaveLength(10);
        expect(host.panels[0].webview.postMessage).toHaveBeenCalledWith({ command: 'loadQuery', sql: 'new query' });
        expect(host.warning).not.toHaveBeenCalled();
    });
});
