import { afterEach, describe, expect, it, vi } from 'vitest';
import type * as vscode from 'vscode';

import type { DaemonClient } from '../../daemonClient.js';
import { ConnectionReferencesPanel } from '../../panels/ConnectionReferencesPanel.js';
import { PluginsPanel } from '../../panels/PluginsPanel.js';
import { showContextPicker } from '../../panels/environmentPicker.js';

const host = vi.hoisted(() => ({ panels: [] as any[] }));
vi.mock('vscode', () => ({
    ViewColumn: { One: 1 },
    ProgressLocation: { Notification: 15 },
    Uri: { joinPath: (...parts: unknown[]) => parts.join('/') },
    env: { clipboard: { writeText: vi.fn() }, openExternal: vi.fn() },
    window: {
        showInformationMessage: vi.fn(),
        showWarningMessage: vi.fn(),
        withProgress: (_options: unknown, task: (progress: unknown, token: unknown) => unknown) =>
            task({}, { onCancellationRequested: () => ({ dispose: vi.fn() }) }),
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

function deferred<T = any>() {
    let resolve!: (value: T) => void;
    let reject!: (reason: unknown) => void;
    const promise = new Promise<T>((res, rej) => { resolve = res; reject = rej; });
    return { promise, resolve, reject };
}
const flush = () => new Promise<void>(resolve => setTimeout(resolve, 0));
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
const posted = () => host.panels.at(-1).webview.postMessage.mock.calls.map((call: any[]) => call[0]);
const of = (command: string) => posted().filter((m: any) => m.command === command);

function pluginsPanel(extra: Record<string, unknown>) {
    const daemon = baseDaemon({
        pluginsList: vi.fn().mockResolvedValue({ assemblies: [], packages: [], serviceEndpoints: [], customApis: [], dataSources: [] }),
        dataProvidersList: vi.fn().mockResolvedValue({ providers: [] }),
        ...extra,
    });
    const panel: any = PluginsPanel.show({} as vscode.Uri, daemon as unknown as DaemonClient, ENV_A, 'A');
    panels.push(panel);
    return panel;
}

describe('Plugins toggle: a write\'s outcome is never gated', () => {
    it('reports a failed write when another toggle started while it was in flight', async () => {
        const writeA = deferred();
        const panel = pluginsPanel({
            pluginsToggleStep: vi.fn().mockReturnValueOnce(writeA.promise).mockResolvedValueOnce({}),
            pluginsGet: vi.fn().mockResolvedValue({ step: { name: 's' } }),
        });

        const a = panel.handleMessage({ command: 'toggleStep', id: 'A', enabled: false });
        await panel.handleMessage({ command: 'toggleStep', id: 'B', enabled: false });
        writeA.reject(new Error('write A failed'));
        await a;

        expect(of('error').map((m: any) => m.message)).toEqual(['Failed to toggle step: write A failed']);
        expect(of('nodeUpdated').map((m: any) => m.node.id)).toEqual(['B']);
    });

    it('updates both nodes when overlapping toggles both succeed', async () => {
        const writeA = deferred();
        const panel = pluginsPanel({
            pluginsToggleStep: vi.fn().mockReturnValueOnce(writeA.promise).mockResolvedValueOnce({}),
            pluginsGet: vi.fn().mockResolvedValue({ step: { name: 's' } }),
        });

        const a = panel.handleMessage({ command: 'toggleStep', id: 'A', enabled: false });
        await panel.handleMessage({ command: 'toggleStep', id: 'B', enabled: false });
        writeA.resolve({});
        await a;

        expect(of('nodeUpdated').map((m: any) => m.node.id).sort()).toEqual(['A', 'B']);
    });

    it('still reports a write that fails after an environment switch, and posts no nodeUpdated for the old environment', async () => {
        const write = deferred();
        const pluginsGet = vi.fn().mockResolvedValue({ step: { name: 's' } });
        const panel = pluginsPanel({ pluginsToggleStep: vi.fn().mockReturnValueOnce(write.promise), pluginsGet });
        vi.mocked(showContextPicker).mockResolvedValue(toEnv(ENV_B));

        const toggle = panel.handleMessage({ command: 'toggleStep', id: 'A', enabled: false });
        await flush();
        await panel.handleMessage({ command: 'requestEnvironmentList' });
        write.reject(new Error('write failed'));
        await toggle;

        expect(of('error').map((m: any) => m.message)).toContain('Failed to toggle step: write failed');
        expect(of('nodeUpdated')).toEqual([]);
    });

    it('posts no nodeUpdated for the old environment when the write succeeds after a switch', async () => {
        const write = deferred();
        const panel = pluginsPanel({
            pluginsToggleStep: vi.fn().mockReturnValueOnce(write.promise),
            pluginsGet: vi.fn().mockResolvedValue({ step: { name: 's' } }),
        });
        vi.mocked(showContextPicker).mockResolvedValue(toEnv(ENV_B));

        const toggle = panel.handleMessage({ command: 'toggleStep', id: 'A', enabled: false });
        await flush();
        await panel.handleMessage({ command: 'requestEnvironmentList' });
        write.resolve({});
        await toggle;

        expect(of('nodeUpdated')).toEqual([]);
        expect(of('error')).toEqual([]);
    });
});

describe('Plugins update: a write\'s outcome is never gated', () => {
    it('reports a failed update when a toggle of another node started meanwhile', async () => {
        const writeUpdate = deferred();
        const panel = pluginsPanel({
            pluginsUpdateStep: vi.fn().mockReturnValueOnce(writeUpdate.promise),
            pluginsToggleStep: vi.fn().mockResolvedValue({}),
            pluginsGet: vi.fn().mockResolvedValue({ step: { name: 's' } }),
        });

        const update = panel.handleMessage({ command: 'updateEntity', entityType: 'step', id: 'A', fields: {} });
        await panel.handleMessage({ command: 'toggleStep', id: 'B', enabled: true });
        writeUpdate.reject(new Error('update failed'));
        await update;

        expect(of('error').map((m: any) => m.message)).toEqual(['Failed to update step: update failed']);
        expect(of('nodeUpdated').map((m: any) => m.node.id)).toEqual(['B']);
    });
});

describe('stale detail responses', () => {
    it('Plugins: drops a detail response for the previous environment', async () => {
        const oldDetail = deferred();
        const panel = pluginsPanel({ pluginsGet: vi.fn().mockReturnValueOnce(oldDetail.promise) });
        vi.mocked(showContextPicker).mockResolvedValue(toEnv(ENV_B));

        void panel.handleMessage({ command: 'selectNode', nodeId: 'step:1', nodeType: 'step' });
        await flush();
        await panel.handleMessage({ command: 'requestEnvironmentList' });
        oldDetail.resolve({ step: { id: 'old-env-step' } });
        await flush();

        expect(of('detailLoaded')).toEqual([]);
    });

    it('Connection References: drops a detail response for the previous environment', async () => {
        const oldDetail = deferred();
        const daemon = baseDaemon({
            connectionReferencesGet: vi.fn().mockReturnValueOnce(oldDetail.promise),
            connectionReferencesList: vi.fn().mockResolvedValue({ references: [], totalCount: 0 }),
        });
        const panel: any = ConnectionReferencesPanel.show({} as vscode.Uri, daemon as unknown as DaemonClient, ENV_A, 'A');
        panels.push(panel);
        vi.mocked(showContextPicker).mockResolvedValue(toEnv(ENV_B));

        void panel.handleMessage({ command: 'selectReference', logicalName: 'cr_old' });
        await flush();
        await panel.handleMessage({ command: 'requestEnvironmentList' });
        oldDetail.resolve({ reference: { logicalName: 'cr_old', flows: [] } });
        await flush();

        expect(of('connectionReferenceDetailLoaded')).toEqual([]);
    });
});
