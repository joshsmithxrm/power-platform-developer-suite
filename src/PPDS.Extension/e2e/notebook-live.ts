import assert from 'node:assert/strict';
import { readFile, writeFile } from 'node:fs/promises';
import path from 'node:path';
import * as vscode from 'vscode';
import type { CancellationToken } from 'vscode-jsonrpc';
import { DaemonClient } from '../src/daemonClient.js';
import { DataverseNotebookController } from '../src/notebooks/DataverseNotebookController.js';
import { DataverseNotebookSerializer } from '../src/notebooks/DataverseNotebookSerializer.js';
import type { QueryResultResponse } from '../src/types.js';

const SQL = 'SELECT TOP 1 organizationid, name FROM organization';
const FETCH = '<fetch top="1"><entity name="organization"><attribute name="organizationid"/><attribute name="name"/></entity></fetch>';
interface Target { url: string; profileName: string }
interface Plan { targets: [Target, Target]; artifactDirectory: string }
interface Call { method: string; environmentUrl?: string; profileName?: string; profileIdentity?: string;
    startedAt: string; returnedAt?: string; organizationId?: string; failureType?: string; token?: CancellationToken }
function deferred(): { promise: Promise<void>; resolve: () => void } {
    let resolve!: () => void;
    return { promise: new Promise<void>(done => { resolve = done; }), resolve: () => resolve() };
}
function gate(): { dispatched: ReturnType<typeof deferred>; returned: ReturnType<typeof deferred>; release: ReturnType<typeof deferred> } {
    return { dispatched: deferred(), returned: deferred(), release: deferred() };
}
function output(cell: vscode.NotebookCell): string {
    return cell.outputs.flatMap(value => value.items.map(item => new TextDecoder().decode(item.data))).join('\n');
}
function organization(result: QueryResultResponse): string {
    assert.equal(result.success, true);
    assert.equal(result.count, 1);
    assert.equal(result.records.length, 1);
    assert.equal(result.moreRecords, false);
    assert.deepEqual(Object.keys(result.records[0]).sort(), ['name', 'organizationid']);
    const id = result.records[0].organizationid;
    assert.equal(typeof id, 'string');
    assert.match(id as string, /^[0-9a-f-]{36}$/i);
    return (id as string).toLowerCase();
}
async function waitFor(predicate: () => boolean): Promise<void> {
    const deadline = Date.now() + 15_000;
    while (!predicate()) {
        if (Date.now() >= deadline) throw new Error('Timed out waiting for native notebook output.');
        await new Promise(resolve => setTimeout(resolve, 25));
    }
}

export async function run(): Promise<void> {
    const plan = JSON.parse(await readFile(process.env.PPDS_NOTEBOOK_LIVE_PLAN!, 'utf8')) as Plan;
    const [aTarget, bTarget] = plan.targets;
    const normalize = (url: string): string => new URL(url).origin.toLowerCase();
    aTarget.url = normalize(aTarget.url); bTarget.url = normalize(bTarget.url);
    assert.notEqual(aTarget.url, bTarget.url, 'Two different URLs are mandatory.');
    const log = vscode.window.createOutputChannel('PPDS notebook live validation', { log: true });
    const daemon = new DaemonClient(process.env.PPDS_NOTEBOOK_LIVE_FIXTURE!, log);
    let authRequired = false;
    daemon.onDeviceCode(() => { authRequired = true; daemon.dispose(); });
    const calls: Call[] = [];
    const gates: ReturnType<typeof gate>[] = [];
    let nextSql: ReturnType<typeof gate> | undefined;
    let nextFetch: ReturnType<typeof gate> | undefined;
    const realSql = daemon.querySql.bind(daemon);
    const realFetch = daemon.queryFetch.bind(daemon);
    const record = async (method: string, params: { environmentUrl?: string; profileName?: string; profileIdentity?: string },
        token: CancellationToken | undefined, held: ReturnType<typeof gate> | undefined,
        invoke: () => Promise<QueryResultResponse>): Promise<QueryResultResponse> => {
        const call: Call = { method, ...params, startedAt: new Date().toISOString(), token };
        calls.push(call); held?.dispatched.resolve();
        try {
            const result = await invoke(); // unchanged production DaemonClient sends a real JSON-RPC request
            call.returnedAt = new Date().toISOString(); call.organizationId = organization(result);
            held?.returned.resolve();
            if (held) await held.release.promise; // delay delivery only; never invent a response
            return result;
        } catch (error) {
            call.failureType = error instanceof Error ? error.name : 'RPC failure';
            held?.returned.resolve(); // a rejected RPC must not strand the orchestration gate
            throw error;
        }
    };
    daemon.querySql = (params, token) => {
        const held = nextSql; nextSql = undefined;
        return record('query/sql', params, token, held, () => realSql(params, token));
    };
    daemon.queryFetch = (params, token) => {
        const held = nextFetch; nextFetch = undefined;
        return record('query/fetch', params, token, held, () => realFetch(params, token));
    };
    const serializer = vscode.workspace.registerNotebookSerializer('ppdsnb', new DataverseNotebookSerializer(), { transientOutputs: true });
    const controller = new DataverseNotebookController(daemon);
    const native = (controller as unknown as { controller: vscode.NotebookController }).controller;
    const workspace = vscode.workspace.workspaceFolders![0].uri;
    const evidence: Record<string, unknown> = { status: 'started', vscodeVersion: vscode.version,
        limitation: 'Test-only daemon metadata bootstrap. Response gates prove client ownership, not server-side cancellation.' };
    async function select(target: Target): Promise<void> {
        const client = daemon as unknown as { sendRequestQuiet<T>(method: string, params: unknown): Promise<T> };
        await client.sendRequestQuiet('fixture/select', { profileName: target.profileName, environmentUrl: target.url });
    }
    async function bind(notebook: vscode.NotebookDocument, target: Target): Promise<void> {
        const edit = new vscode.WorkspaceEdit();
        edit.set(notebook.uri, [vscode.NotebookEdit.updateNotebookMetadata({ environmentUrl: target.url, environmentName: target.url })]);
        assert.equal(await vscode.workspace.applyEdit(edit), true);
    }
    async function open(name: string, target?: Target, fetch = false, cells = 1): Promise<vscode.NotebookDocument> {
        const uri = vscode.Uri.joinPath(workspace, `${name}.ppdsnb`);
        await vscode.workspace.fs.writeFile(uri, new TextEncoder().encode(JSON.stringify({
            metadata: target ? { environmentUrl: target.url, environmentName: target.url } : undefined,
            cells: Array.from({ length: cells }, () => ({ kind: fetch ? 'fetchxml' : 'sql', source: fetch ? FETCH : SQL })),
        })));
        const notebook = await vscode.workspace.openNotebookDocument(uri);
        await vscode.window.showNotebookDocument(notebook, { preview: false });
        await vscode.commands.executeCommand('notebook.selectKernel', { id: 'ppdsnb-controller', extension: 'ppds-test.notebook-live-fixture' });
        return notebook;
    }
    const execute = (notebook: vscode.NotebookDocument, cells = notebook.getCells()): Promise<void> =>
        Promise.resolve(native.executeHandler!(cells, notebook, native));
    const cached = (cell: vscode.NotebookCell): QueryResultResponse => {
        const result = controller.getCellResults(cell.document.uri.toString());
        assert.ok(result, 'Native execution must cache a real result.');
        return result;
    };
    let timeout: ReturnType<typeof setTimeout> | undefined;
    try {
        await Promise.race([(async () => {
            await daemon.start();
            const a = await open('environment-a', aTarget, false, 2);
            const b = await open('environment-b', bTarget, true);
            const first = gate(); gates.push(first); nextSql = first;
            console.log('Starting A. Review any real Production/Unknown target prompt in the native host.');
            const runningA = execute(a);
            await first.dispatched.promise;
            await select(bTarget); // only the isolated profile metadata changes
            console.log('Starting B while A remains active.');
            const runningB = execute(b);
            await Promise.all([first.returned.promise, runningB]);
            await bind(a, bTarget);
            await vscode.window.showNotebookDocument(a, { preview: false });
            first.release.resolve();
            await runningA;
            await waitFor(() => a.cellAt(1).outputs.length === 2 && b.cellAt(0).outputs.length === 2);
            assert.equal(calls.length, 3);
            assert.equal(calls[0].environmentUrl, aTarget.url);
            assert.equal(calls[1].environmentUrl, bTarget.url);
            assert.equal(calls[2].environmentUrl, aTarget.url);
            assert.equal(calls[2].profileIdentity, calls[0].profileIdentity);
            assert.equal(calls[2].profileName, aTarget.profileName);
            const aId = organization(cached(a.cellAt(0)));
            const bId = organization(cached(b.cellAt(0)));
            assert.notEqual(aId, bId, 'Distinct URL aliases to the same organization do not satisfy two-environment evidence.');
            assert.equal(organization(cached(a.cellAt(1))), aId);
            for (const cell of a.getCells()) assert.ok(output(cell).includes(`${aTarget.url}/main.aspx`));
            assert.ok(output(b.cellAt(0)).includes(`${bTarget.url}/main.aspx`));
            evidence.initialOutputs = { a: a.getCells().map(output), b: output(b.cellAt(0)) };
            evidence.organizations = [{ environmentUrl: aTarget.url, organizationId: aId }, { environmentUrl: bTarget.url, organizationId: bId }];
            console.log('PASS real SQL/FetchXML results, distinct organizations, late A cell retains captured target/profile after binding/default/focus switch.');

            await bind(a, aTarget); await select(aTarget);
            const cancelA = gate(); gates.push(cancelA); nextSql = cancelA;
            const interrupted = execute(a, [a.cellAt(0)]);
            await cancelA.dispatched.promise;
            const queued = execute(a, [a.cellAt(0)]);
            await select(bTarget);
            const keepB = gate(); gates.push(keepB); nextFetch = keepB;
            const unaffected = execute(b);
            await Promise.all([cancelA.returned.promise, keepB.returned.promise]);
            await native.interruptHandler!(a); await queued;
            assert.equal(calls[3].token?.isCancellationRequested, true);
            assert.equal(calls[4].token?.isCancellationRequested, false);
            cancelA.release.resolve(); keepB.release.resolve();
            await Promise.all([interrupted, unaffected]);
            await waitFor(() => output(a.cellAt(0)).includes('Query cancelled'));
            assert.equal(controller.hasCellResults(a.cellAt(0).document.uri.toString()), false);
            assert.equal(organization(cached(b.cellAt(0))), bId);
            assert.equal(calls.length, 5, 'Queued A must not dispatch after interruption.');
            console.log('PASS notebook-only cancellation/queue clearing and late-result suppression; B retains its real result.');

            const legacy = await open('unbound');
            await execute(legacy);
            assert.equal(organization(cached(legacy.cellAt(0))), bId);
            assert.equal(calls[5].environmentUrl, bTarget.url);
            assert.equal(legacy.metadata.environmentUrl, undefined);
            await bind(a, bTarget); assert.equal(await a.save(), true);
            const persisted = JSON.parse(new TextDecoder().decode(await vscode.workspace.fs.readFile(a.uri))) as { metadata: Record<string, unknown> };
            assert.equal(persisted.metadata.environmentUrl, bTarget.url);
            assert.deepEqual(Object.keys(persisted.metadata).filter(key => /credential|identity|acknowledge/i.test(key)), []);
            const reopenedUri = vscode.Uri.joinPath(workspace, 'reopened.ppdsnb');
            await vscode.workspace.fs.copy(a.uri, reopenedUri);
            const reopened = await vscode.workspace.openNotebookDocument(reopenedUri);
            assert.equal(reopened.metadata.environmentUrl, bTarget.url);
            assert.equal(controller.hasCellResults(reopened.cellAt(0).document.uri.toString()), false);
            evidence.status = 'passed'; evidence.queryCount = calls.length;
            console.log('PASS unbound real query uses isolated session default; binding persists without identity/acknowledgement.');
        })(), new Promise<never>((_, reject) => {
            timeout = setTimeout(() => reject(new Error('Notebook live validation exceeded six minutes.')), 360_000);
        })]);
    } catch (error) {
        evidence.status = 'failed';
        evidence.error = authRequired ? 'Existing authentication is unavailable; no new login was performed.'
            : error instanceof Error ? error.message : String(error);
        throw error;
    } finally {
        clearTimeout(timeout);
        gates.forEach(value => value.release.resolve());
        controller.dispose(); daemon.dispose(); serializer.dispose(); log.dispose();
        evidence.calls = calls.map(({ token, ...call }) => ({ ...call, cancellationRequested: token?.isCancellationRequested }));
        await writeFile(path.join(plan.artifactDirectory, 'native-evidence.json'), JSON.stringify(evidence, null, 2));
    }
}
