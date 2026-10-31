import assert from 'node:assert/strict';
import * as vscode from 'vscode';
import type { CancellationToken } from 'vscode-jsonrpc';
import { DataverseNotebookController } from '../src/notebooks/DataverseNotebookController.js';
import { DataverseNotebookSerializer } from '../src/notebooks/DataverseNotebookSerializer.js';
import type { DaemonClient } from '../src/daemonClient.js';
import type { QueryResultResponse } from '../src/types.js';

const A = 'https://notebook-a.crm.dynamics.com';
const B = 'https://notebook-b.crm.dynamics.com';
const C = 'https://session-default.crm.dynamics.com';

function deferred<T>(): { promise: Promise<T>; resolve: (value: T) => void } {
    let resolve!: (value: T) => void;
    return { promise: new Promise<T>(done => { resolve = done; }), resolve: value => resolve(value) };
}

async function waitFor(predicate: () => boolean): Promise<void> {
    const deadline = Date.now() + 10_000;
    while (!predicate()) {
        if (Date.now() > deadline) throw new Error('Timed out waiting for notebook host state.');
        await new Promise(resolve => setTimeout(resolve, 25));
    }
}

function result(): QueryResultResponse {
    return {
        success: true, entityName: 'account',
        columns: [{ logicalName: 'accountid', alias: null, displayName: 'Account', dataType: 'guid', linkedEntityAlias: null }],
        records: [{ accountid: '11111111-1111-1111-1111-111111111111' }], count: 1, totalCount: 1,
        moreRecords: false, pagingCookie: null, pageNumber: 1, isAggregate: false,
        executedFetchXml: null, executionTimeMs: 1, queryMode: null,
    };
}

function outputs(notebook: vscode.NotebookDocument): string {
    return notebook.getCells().flatMap(cell => cell.outputs.flatMap(output =>
        output.items.map(item => new TextDecoder().decode(item.data)))).join('\n');
}

/** Native VS Code objects, serialization and output lifecycle; all RPCs are deterministic fakes. */
async function runScenarios(): Promise<void> {
    console.log(`Notebook host executing on VS Code ${vscode.version}`);
    const calls: Array<{ environmentUrl?: string; profileIdentity?: string; token: CancellationToken | undefined }> = [];
    let identity = 'identity-first';
    let defaultUrl = C;
    let pending: ReturnType<typeof deferred<QueryResultResponse>> | undefined;
    const daemon = {
        authWho: async () => ({ name: 'profile', index: 1, profileIdentity: identity,
            environment: { url: defaultUrl, displayName: 'Default', type: 'Development' } }),
        envList: async () => ({ environments: [A, B, C].map((apiUrl, i) => ({ apiUrl, friendlyName: `Environment ${i}`, type: 'Development' })) }),
        envConfigGet: async () => ({ resolvedType: 'Unknown' }),
        querySql: async (target: { environmentUrl?: string; profileIdentity?: string }, token?: CancellationToken) => {
            calls.push({ ...target, token });
            return pending ? pending.promise : result();
        },
        queryFetch: async (target: { environmentUrl?: string; profileIdentity?: string }, token?: CancellationToken) => {
            calls.push({ ...target, token });
            return result();
        },
    };
    const serializer = vscode.workspace.registerNotebookSerializer('ppdsnb', new DataverseNotebookSerializer(), { transientOutputs: true });
    const controller = new DataverseNotebookController(daemon as unknown as DaemonClient);
    const native = (controller as unknown as { controller: vscode.NotebookController }).controller;
    const workspace = vscode.workspace.workspaceFolders![0].uri;
    async function open(name: string, environmentUrl?: string, fetch = false): Promise<vscode.NotebookDocument> {
        const uri = vscode.Uri.joinPath(workspace, `${name}.ppdsnb`);
        const source = { metadata: environmentUrl ? { environmentUrl, environmentName: name } : undefined,
            cells: [{ kind: fetch ? 'fetchxml' : 'sql', source: fetch ? '<fetch><entity name="account"/></fetch>' : 'SELECT accountid FROM account' }] };
        await vscode.workspace.fs.writeFile(uri, new TextEncoder().encode(JSON.stringify(source)));
        console.log(`Opening native notebook ${name}`);
        const notebook = await vscode.workspace.openNotebookDocument(uri);
        await vscode.window.showNotebookDocument(notebook, { preview: false });
        console.log(`Selecting native controller for ${name}`);
        await vscode.commands.executeCommand('notebook.selectKernel', { id: 'ppdsnb-controller', extension: 'ppds-test.notebook-host-fixture' });
        return notebook;
    }
    function execute(notebook: vscode.NotebookDocument): Promise<void> {
        return Promise.resolve(native.executeHandler!(notebook.getCells(), notebook, native));
    }
    try {
        const a = await open('a', A);
        const b = await open('b', B, true);
        pending = deferred<QueryResultResponse>();
        const runningA = execute(a);
        await waitFor(() => calls.length === 1);
        const runningB = execute(b);
        await runningB;
        assert.equal(calls[0].environmentUrl, A);
        assert.equal(calls[1].environmentUrl, B);
        identity = 'identity-second';
        defaultUrl = B;
        const edit = new vscode.WorkspaceEdit();
        edit.set(a.uri, [vscode.NotebookEdit.updateNotebookMetadata({ environmentUrl: C, environmentName: 'New binding' })]);
        assert.equal(await vscode.workspace.applyEdit(edit), true);
        pending.resolve(result());
        await runningA;
        pending = undefined;
        await waitFor(() => a.cellAt(0).outputs.length === 2 && b.cellAt(0).outputs.length === 2);
        assert.match(outputs(a), /https:\/\/notebook-a\.crm\.dynamics\.com/);
        assert.match(outputs(a), /notebook-a\.crm\.dynamics\.com\/main\.aspx/);
        assert.match(outputs(b), /https:\/\/notebook-b\.crm\.dynamics\.com/);
        assert.equal(controller.getCellExecutionContext(a.cellAt(0).document.uri.toString())!.context.profileIdentity, 'identity-first');
        await waitFor(() => a.cellAt(0).executionSummary?.success === true);
        assert.equal(a.cellAt(0).executionSummary?.success, true);
        console.log('PASS native concurrent SQL/FetchXML, captured binding/profile, attributed outputs and links');

        const before = calls.length;
        pending = deferred<QueryResultResponse>();
        const cancelled = execute(a);
        await waitFor(() => calls.length === before + 1);
        const queued = execute(a);
        await native.interruptHandler!(a);
        await queued;
        await execute(b);
        assert.equal(calls[before].token!.isCancellationRequested, true);
        assert.equal(calls.at(-1)!.token!.isCancellationRequested, false);
        pending.resolve(result());
        await cancelled;
        pending = undefined;
        await waitFor(() => outputs(a).includes('Query cancelled'));
        assert.equal(controller.hasCellResults(a.cellAt(0).document.uri.toString()), false);
        assert.equal(calls.length, before + 2);
        console.log('PASS native notebook interruption clears only its queue and preserves other notebook execution');

        const unbound = await open('legacy');
        await execute(unbound);
        await waitFor(() => unbound.cellAt(0).outputs.length === 2);
        assert.equal(calls.at(-1)!.environmentUrl, B);
        assert.equal(unbound.metadata.environmentUrl, undefined);
        assert.equal(await a.save(), true);
        const persisted = JSON.parse(new TextDecoder().decode(await vscode.workspace.fs.readFile(a.uri))) as { metadata: Record<string, unknown> };
        assert.equal(persisted.metadata.environmentUrl, C);
        assert.equal(persisted.metadata.profileIdentity, undefined);
        console.log('Closing saved notebook documents');
        await Promise.all([a, b, unbound].map(document => document.save()));
        assert.equal(await vscode.window.tabGroups.close(vscode.window.tabGroups.all.flatMap(group => group.tabs)), true);
        // VS Code may keep the closed editor's document model alive. A fresh URI
        // forces the native serializer to read persisted bytes instead of its cache.
        const reopenedUri = vscode.Uri.joinPath(workspace, 'reopened.ppdsnb');
        await vscode.workspace.fs.copy(a.uri, reopenedUri);
        const reopened = await vscode.workspace.openNotebookDocument(reopenedUri);
        assert.equal(reopened.metadata.environmentUrl, C);
        assert.equal(controller.hasCellResults(reopened.cellAt(0).document.uri.toString()), false);
        console.log('PASS native legacy load, session default, binding save/reload and fresh-document result isolation');
        console.log('Notebook host: 3 scenarios passed. No live Dataverse calls.');
    } finally {
        controller.dispose();
        serializer.dispose();
    }
}

export async function run(): Promise<void> {
    let timeout: ReturnType<typeof setTimeout> | undefined;
    try {
        await Promise.race([runScenarios(), new Promise<never>((_, reject) => {
            timeout = setTimeout(() => reject(new Error('Notebook host scenarios exceeded 60 seconds.')), 60_000);
        })]);
    } finally {
        clearTimeout(timeout);
    }
}
