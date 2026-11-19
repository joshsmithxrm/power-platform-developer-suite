import { describe, expect, it, vi } from 'vitest';
import type * as vscode from 'vscode';

import type { DaemonClient } from '../../daemonClient.js';
import { ImportJobsPanel } from '../../panels/ImportJobsPanel.js';
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

const OLD_ENV = 'https://old.crm.dynamics.com';
const NEW_ENV = 'https://new.crm.dynamics.com';

function job(id: string) {
    return {
        id, solutionName: id, status: 'Succeeded', progress: 100, createdBy: 'a', createdOn: '', startedOn: '',
        completedOn: '', duration: '', operationContext: null,
    };
}

function deferred<T>() {
    let resolve!: (value: T) => void;
    let reject!: (reason: unknown) => void;
    const promise = new Promise<T>((res, rej) => { resolve = res; reject = rej; });
    return { promise, resolve, reject };
}

const flush = () => new Promise<void>(resolve => setTimeout(resolve, 0));

/** ImportJobsPanel is the simplest panel that loads through WebviewPanelBase.beginRequest. */
function setup(importJobsList: ReturnType<typeof vi.fn>) {
    const daemon = {
        onDidReconnect: vi.fn(() => ({ dispose: vi.fn() })),
        authList: vi.fn().mockResolvedValue({ profiles: [] }),
        envList: vi.fn().mockResolvedValue({ environments: [] }),
        envConfigGet: vi.fn().mockResolvedValue({}),
        importJobsList,
    };
    const panel: any = ImportJobsPanel.show({} as vscode.Uri, daemon as unknown as DaemonClient, OLD_ENV, 'Old');
    const webview = host.panels.at(-1).webview;
    const loaded = () => webview.postMessage.mock.calls
        .map((call: any[]) => call[0])
        .filter((m: any) => m.command === 'importJobsLoaded')
        .map((m: any) => m.jobs.map((j: any) => j.id));
    return { panel, loaded };
}

describe('WebviewPanelBase stale response guard', () => {
    it('drops a slow list response for the previous environment after an environment switch', async () => {
        const oldLoad = deferred<any>();
        const importJobsList = vi.fn()
            .mockReturnValueOnce(oldLoad.promise)
            .mockResolvedValueOnce({ jobs: [job('new-job')], totalCount: 1 });
        const { panel, loaded } = setup(importJobsList);
        vi.mocked(showContextPicker).mockResolvedValue(
            { profileName: 'p', url: NEW_ENV, displayName: 'New', type: null });

        void panel.handleMessage({ command: 'ready' });
        await flush();
        expect(importJobsList).toHaveBeenCalledTimes(1);

        await panel.handleMessage({ command: 'requestEnvironmentList' });
        expect(importJobsList).toHaveBeenLastCalledWith(undefined, NEW_ENV, 'p');
        expect(loaded()).toEqual([['new-job']]);

        // The old environment's response arrives last and must not repopulate the list.
        oldLoad.resolve({ jobs: [job('old-job')], totalCount: 1 });
        await flush();
        expect(loaded()).toEqual([['new-job']]);
        panel.dispose();
    });

    it('drops an out-of-order response from an earlier request in the same environment', async () => {
        const first = deferred<any>();
        const second = deferred<any>();
        const importJobsList = vi.fn().mockReturnValueOnce(first.promise).mockReturnValueOnce(second.promise);
        const { panel, loaded } = setup(importJobsList);

        void panel.handleMessage({ command: 'refresh' });
        void panel.handleMessage({ command: 'refresh' });
        await flush();

        second.resolve({ jobs: [job('second')], totalCount: 1 });
        await flush();
        first.resolve({ jobs: [job('first')], totalCount: 1 });
        await flush();

        expect(loaded()).toEqual([['second']]);
        panel.dispose();
    });

    it('drops a response that arrives after the panel is disposed', async () => {
        const pending = deferred<any>();
        const { panel, loaded } = setup(vi.fn().mockReturnValueOnce(pending.promise));

        void panel.handleMessage({ command: 'refresh' });
        await flush();
        panel.dispose();
        pending.resolve({ jobs: [job('late')], totalCount: 1 });
        await flush();

        expect(loaded()).toEqual([]);
    });
});
