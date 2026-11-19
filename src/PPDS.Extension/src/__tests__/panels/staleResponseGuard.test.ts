import { afterEach, describe, expect, it, vi } from 'vitest';
import type * as vscode from 'vscode';

import type { DaemonClient } from '../../daemonClient.js';
import { EnvironmentVariablesPanel } from '../../panels/EnvironmentVariablesPanel.js';
import { ImportJobsPanel } from '../../panels/ImportJobsPanel.js';
import { SolutionsPanel } from '../../panels/SolutionsPanel.js';
import { showContextPicker } from '../../panels/environmentPicker.js';

const host = vi.hoisted(() => ({ panels: [] as any[] }));
vi.mock('vscode', () => ({
    ViewColumn: { One: 1 },
    Uri: { joinPath: (...parts: unknown[]) => parts.join('/') },
    env: { clipboard: { writeText: vi.fn() }, openExternal: vi.fn() },
    window: {
        showInformationMessage: vi.fn(),
        createWebviewPanel: vi.fn(() => {
            const panel = {
                reveal: vi.fn(), dispose: vi.fn(), title: '', onDidDispose: vi.fn(() => ({ dispose: vi.fn() })),
                webview: {
                    asWebviewUri: (uri: string) => uri, cspSource: 'test:', postMessage: vi.fn(),
                    onDidReceiveMessage: vi.fn(() => ({ dispose: vi.fn() })),
                },
            };
            host.panels.push(panel);
            return panel;
        }),
    },
}));
vi.mock('../../panels/environmentPicker.js', () => ({ getEnvironmentPickerHtml: () => '', showContextPicker: vi.fn() }));
vi.mock('../../utils/errorNotify.js', () => ({ showErrorWithReport: vi.fn() }));

const ENV_A = 'https://a.crm.dynamics.com';
const ENV_B = 'https://b.crm.dynamics.com';

interface Deferred<T> { promise: Promise<T>; resolve: (value: T) => void; reject: (reason: unknown) => void }
function deferred<T = any>(): Deferred<T> {
    let resolve!: (value: T) => void;
    let reject!: (reason: unknown) => void;
    const promise = new Promise<T>((res, rej) => { resolve = res; reject = rej; });
    return { promise, resolve, reject };
}

const flush = () => new Promise<void>(resolve => setTimeout(resolve, 0));

function job(id: string) {
    return {
        id, solutionName: id, status: 'Succeeded', progress: 100, createdBy: 'a', createdOn: '', startedOn: '',
        completedOn: '', duration: '', operationContext: null,
    };
}
const jobs = (...ids: string[]) => ({ jobs: ids.map(job), totalCount: ids.length });

const toEnv = (url: string) => ({ profileName: 'p', url, displayName: url, type: null }) as never;

const panels: { dispose(): void }[] = [];

afterEach(() => {
    for (const panel of panels.splice(0)) panel.dispose();
    host.panels.length = 0;
    vi.resetAllMocks();
});

function baseDaemon(extra: Record<string, unknown>) {
    return {
        onDidReconnect: vi.fn(() => ({ dispose: vi.fn() })),
        authList: vi.fn().mockResolvedValue({ profiles: [] }),
        envList: vi.fn().mockResolvedValue({ environments: [] }),
        envConfigGet: vi.fn().mockResolvedValue({}),
        ...extra,
    };
}

function messagesOf(webviewIndex = host.panels.length - 1) {
    const webview = host.panels[webviewIndex].webview;
    return () => webview.postMessage.mock.calls.map((call: any[]) => call[0]);
}

function importJobsPanel(importJobsList: ReturnType<typeof vi.fn>, extra: Record<string, unknown> = {}) {
    const daemon = baseDaemon({ importJobsList, ...extra });
    const panel: any = ImportJobsPanel.show({} as vscode.Uri, daemon as unknown as DaemonClient, ENV_A, 'A');
    panels.push(panel);
    const posted = messagesOf();
    const loaded = () => posted().filter((m: any) => m.command === 'importJobsLoaded').map((m: any) => m.jobs.map((j: any) => j.id));
    return { panel, daemon, posted, loaded };
}

describe('WebviewPanelBase stale response guard', () => {
    it('drops a slow list response for the previous environment after an environment switch', async () => {
        const oldLoad = deferred();
        const { panel, loaded } = importJobsPanel(vi.fn()
            .mockReturnValueOnce(oldLoad.promise)
            .mockResolvedValueOnce(jobs('new-job')));
        vi.mocked(showContextPicker).mockResolvedValue(toEnv(ENV_B));

        void panel.handleMessage({ command: 'ready' });
        await flush();
        await panel.handleMessage({ command: 'requestEnvironmentList' });
        expect(loaded()).toEqual([['new-job']]);

        oldLoad.resolve(jobs('old-job'));
        await flush();
        expect(loaded()).toEqual([['new-job']]);
    });

    it('drops an old-environment response that lands during the picker awaits, before the reload starts', async () => {
        // Only the environment check can drop this one: no newer load has started yet.
        const oldLoad = deferred();
        const envList = deferred();
        const { panel, loaded, daemon } = importJobsPanel(vi.fn()
            .mockReturnValueOnce(oldLoad.promise)
            .mockResolvedValueOnce(jobs('new-job')));
        daemon.envList.mockReturnValue(envList.promise);
        vi.mocked(showContextPicker).mockResolvedValue(toEnv(ENV_B));

        void panel.handleMessage({ command: 'refresh' });
        await flush();
        const picking = panel.handleMessage({ command: 'requestEnvironmentList' });
        await flush();
        expect(daemon.importJobsList).toHaveBeenCalledTimes(1);

        oldLoad.resolve(jobs('old-job'));
        await flush();
        expect(loaded()).toEqual([]);

        envList.resolve({ environments: [] });
        await picking;
        expect(loaded()).toEqual([['new-job']]);
    });

    it('drops an out-of-order response from an earlier request in the same environment', async () => {
        const first = deferred();
        const second = deferred();
        const { panel, loaded } = importJobsPanel(vi.fn().mockReturnValueOnce(first.promise).mockReturnValueOnce(second.promise));

        void panel.handleMessage({ command: 'refresh' });
        void panel.handleMessage({ command: 'refresh' });
        await flush();
        second.resolve(jobs('second'));
        await flush();
        first.resolve(jobs('first'));
        await flush();

        expect(loaded()).toEqual([['second']]);
    });

    it('drops a response that arrives after the panel is disposed', async () => {
        const pending = deferred();
        const { panel, loaded } = importJobsPanel(vi.fn().mockReturnValueOnce(pending.promise));

        void panel.handleMessage({ command: 'refresh' });
        await flush();
        panel.dispose();
        pending.resolve(jobs('late'));
        await flush();

        expect(loaded()).toEqual([]);
    });

    it('does not post an error from a request for the previous environment', async () => {
        const oldLoad = deferred();
        const { panel, posted } = importJobsPanel(vi.fn()
            .mockReturnValueOnce(oldLoad.promise)
            .mockResolvedValueOnce(jobs('new-job')));
        vi.mocked(showContextPicker).mockResolvedValue(toEnv(ENV_B));

        void panel.handleMessage({ command: 'refresh' });
        await flush();
        await panel.handleMessage({ command: 'requestEnvironmentList' });
        oldLoad.reject(new Error('old environment failed'));
        await flush();

        expect(posted().filter((m: any) => m.command === 'error')).toEqual([]);
    });

    it('shows only the latest load when the user switches away and back while the first load is in flight', async () => {
        const loadA1 = deferred();
        const loadB = deferred();
        const { panel, loaded, daemon } = importJobsPanel(vi.fn()
            .mockReturnValueOnce(loadA1.promise)
            .mockReturnValueOnce(loadB.promise)
            .mockResolvedValueOnce(jobs('a-second')));

        void panel.handleMessage({ command: 'refresh' });
        await flush();
        vi.mocked(showContextPicker).mockResolvedValueOnce(toEnv(ENV_B));
        void panel.handleMessage({ command: 'requestEnvironmentList' });
        await flush();
        vi.mocked(showContextPicker).mockResolvedValueOnce(toEnv(ENV_A));
        await panel.handleMessage({ command: 'requestEnvironmentList' });
        expect(daemon.importJobsList).toHaveBeenCalledTimes(3);
        expect(loaded()).toEqual([['a-second']]);

        // The first load targeted the same environment as the current one, but is still stale.
        loadA1.resolve(jobs('a-first'));
        loadB.resolve(jobs('b'));
        await flush();
        expect(loaded()).toEqual([['a-second']]);
    });
});

describe('per-item scopes', () => {
    it('loads components for every expanded solution, not only the last one', async () => {
        const solutionsComponents = vi.fn().mockImplementation(async (name: string) => ({
            components: [{ objectId: name, isMetadata: false, logicalName: name, schemaName: name, displayName: name, rootComponentBehavior: 0, componentType: 1, componentTypeName: 'Entity' }],
        }));
        const daemon = baseDaemon({ solutionsList: vi.fn().mockResolvedValue({ solutions: [] }), solutionsComponents });
        const panel: any = SolutionsPanel.show({} as vscode.Uri, daemon as unknown as DaemonClient, ENV_A, 'A');
        panels.push(panel);
        const posted = messagesOf();

        await Promise.all([
            panel.handleMessage({ command: 'expandSolution', uniqueName: 'A' }),
            panel.handleMessage({ command: 'expandSolution', uniqueName: 'B' }),
        ]);

        const loaded = posted().filter((m: any) => m.command === 'componentsLoaded').map((m: any) => m.uniqueName).sort();
        expect(loaded).toEqual(['A', 'B']);
    });

    it('does not open the edit dialog with the previous environment\'s value, and a later save still works', async () => {
        const oldGet = deferred();
        const environmentVariablesGet = vi.fn().mockReturnValueOnce(oldGet.promise);
        const environmentVariablesSet = vi.fn().mockResolvedValue({ success: true });
        const daemon = baseDaemon({
            environmentVariablesGet, environmentVariablesSet,
            environmentVariablesList: vi.fn().mockResolvedValue({ variables: [], totalCount: 0 }),
        });
        const context = { globalState: { get: (_k: string, d: unknown) => d, update: vi.fn() } };
        const panel: any = EnvironmentVariablesPanel.show(
            {} as vscode.Uri, daemon as unknown as DaemonClient, context as unknown as vscode.ExtensionContext, ENV_A, 'A');
        panels.push(panel);
        const posted = messagesOf();
        vi.mocked(showContextPicker).mockResolvedValue(toEnv(ENV_B));

        void panel.handleMessage({ command: 'editVariable', schemaName: 'ppds_secret' });
        await flush();
        await panel.handleMessage({ command: 'requestEnvironmentList' });
        oldGet.resolve({ variable: { schemaName: 'ppds_secret', displayName: 'x', type: 'String', currentValue: 'old-env-value' } });
        await flush();
        await panel.handleMessage({ command: 'saveVariable', schemaName: 'ppds_secret', value: 'new-value' });

        expect(posted().filter((m: any) => m.command === 'editVariableDialog')).toEqual([]);
        expect(environmentVariablesSet).toHaveBeenCalledWith('ppds_secret', 'new-value', ENV_B, 'p');
        expect(posted().some((m: any) => m.command === 'variableSaved')).toBe(true);
    });
});
