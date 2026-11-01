import * as vscode from 'vscode';
import { CancellationTokenSource } from 'vscode-jsonrpc/node';

import type { DaemonClient } from '../daemonClient.js';
import type { QueryResultResponse } from '../types.js';
import { isAuthError } from '../utils/errorUtils.js';
import { showErrorWithReport } from '../utils/errorNotify.js';

import { renderResultsHtml } from './notebookResultRenderer.js';

const AUTO_SWITCH_THRESHOLD = 30;

export interface NotebookExecutionContext {
    readonly environmentUrl: string;
    readonly environmentName: string;
    readonly environmentType: string;
    readonly profileName: string;
    /** Authentication metadata only; never persisted in the notebook. */
    readonly profileIdentity: string;
}

interface PendingRun {
    cells: vscode.NotebookCell[];
    complete: () => void;
}

interface NotebookRun {
    readonly id: number;
    cancelled: boolean;
    context?: NotebookExecutionContext;
}

interface NotebookState {
    readonly notebook: vscode.NotebookDocument;
    readonly queue: PendingRun[];
    active?: NotebookRun;
    processing: boolean;
    binding: string | undefined;
    acknowledgement?: string;
    acknowledgementTarget?: string;
}

interface CellExecutionState {
    readonly run: NotebookRun;
    readonly abort: AbortController;
    readonly cts: CancellationTokenSource;
}

interface CachedCellResult {
    readonly result: QueryResultResponse;
    readonly context: NotebookExecutionContext;
    readonly executedAt: string;
}

/** URL validation never falls back to a different environment. The daemon owns access checks. */
function normalizeEnvironmentUrl(value: string): string {
    const url = new URL(value);
    if (!['https:', 'http:'].includes(url.protocol) || url.username || url.password || url.search || url.hash) {
        throw new Error('Notebook environment must be an organization URL without credentials, query, or fragment.');
    }
    return url.toString().replace(/\/+$/, '');
}

function bindingUrl(notebook: vscode.NotebookDocument): string | undefined {
    const value: unknown = notebook.metadata?.environmentUrl;
    return typeof value === 'string' && value.trim() ? value.trim() : undefined;
}

export class DataverseNotebookController implements vscode.Disposable {
    private readonly controller: vscode.NotebookController;
    private readonly disposables: vscode.Disposable[] = [];
    private readonly statusBarItem: vscode.StatusBarItem;
    private readonly states = new Map<string, NotebookState>();
    private readonly cellResults = new Map<string, CachedCellResult>();
    private readonly activeExecutions = new Map<string, CellExecutionState>();
    private readonly environmentTypes = new Map<string, string>();
    private sessionDefault: { url: string; name: string } | undefined;
    private executionOrder = 0;
    private runSequence = 0;
    private disposed = false;

    constructor(private readonly daemon: DaemonClient) {
        this.controller = vscode.notebooks.createNotebookController('ppdsnb-controller', 'ppdsnb', 'Power Platform Developer Suite');
        this.controller.supportedLanguages = ['sql', 'fetchxml'];
        this.controller.supportsExecutionOrder = true;
        this.controller.executeHandler = this.executeHandler.bind(this);
        this.controller.interruptHandler = this.interruptHandler.bind(this);
        this.statusBarItem = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Right, 100);
        this.statusBarItem.command = 'ppds.selectNotebookEnvironment';

        this.disposables.push(
            vscode.window.onDidChangeActiveNotebookEditor(editor => this.updateStatusBarVisibility(editor)),
            vscode.workspace.onDidOpenNotebookDocument(notebook => {
                if (notebook.notebookType === 'ppdsnb') this.updateStatusBarVisibility(vscode.window.activeNotebookEditor);
            }),
            vscode.workspace.onDidChangeNotebookDocument(event => {
                if (event.notebook.notebookType === 'ppdsnb') {
                    this.syncBinding(this.getState(event.notebook));
                    this.updateStatusBarVisibility(vscode.window.activeNotebookEditor);
                }
            }),
            vscode.workspace.onDidCloseNotebookDocument(notebook => {
                if (notebook.notebookType !== 'ppdsnb') return;
                this.interruptHandler(notebook);
                this.states.delete(notebook.uri.toString());
                for (const cell of notebook.getCells()) this.cellResults.delete(cell.document.uri.toString());
                this.updateStatusBarVisibility(vscode.window.activeNotebookEditor);
            }),
        );
        this.registerAutoSwitchListener();
        this.updateStatusBarVisibility(vscode.window.activeNotebookEditor);
    }

    private getState(notebook: vscode.NotebookDocument): NotebookState {
        const key = notebook.uri.toString();
        let state = this.states.get(key);
        if (!state) {
            state = { notebook, queue: [], processing: false, binding: bindingUrl(notebook) };
            this.states.set(key, state);
        }
        return state;
    }

    private syncBinding(state: NotebookState): void {
        const binding = bindingUrl(state.notebook);
        if (state.binding !== binding) {
            state.binding = binding;
            state.acknowledgement = undefined;
            state.acknowledgementTarget = undefined;
        }
    }

    async selectEnvironment(notebook = vscode.window.activeNotebookEditor?.notebook): Promise<void> {
        if (!notebook || notebook.notebookType !== 'ppdsnb') return;
        try {
            // Capture the originating document before opening asynchronous UI.
            const profile = await this.daemon.authWho();
            const profileName = profile.name ?? String(profile.index);
            const envResult = await this.daemon.envList(undefined, undefined, profileName);
            const items = [
                { label: 'Use session default', description: profile.environment?.displayName ?? 'No default selected', detail: profile.environment?.url, apiUrl: undefined as string | undefined, id: undefined as string | undefined },
                ...envResult.environments.map(env => ({
                    label: env.friendlyName, description: env.type ?? 'Unknown', detail: env.apiUrl,
                    apiUrl: env.apiUrl, id: env.environmentId ?? env.id,
                })),
            ];
            const selected = await vscode.window.showQuickPick(items, {
                placeHolder: 'Select Dataverse environment for this notebook', ignoreFocusOut: true,
            });
            if (!selected || notebook.isClosed || this.disposed) return;
            if (selected.apiUrl) this.environmentTypes.set(normalizeEnvironmentUrl(selected.apiUrl), selected.description);
            const edit = new vscode.WorkspaceEdit();
            edit.set(notebook.uri, [vscode.NotebookEdit.updateNotebookMetadata({
                ...notebook.metadata,
                environmentId: selected.id,
                environmentName: selected.apiUrl ? selected.label : undefined,
                environmentUrl: selected.apiUrl ? normalizeEnvironmentUrl(selected.apiUrl) : undefined,
            })]);
            await vscode.workspace.applyEdit(edit);
            this.syncBinding(this.getState(notebook));
            this.updateStatusBarVisibility(vscode.window.activeNotebookEditor);
        } catch (error) {
            void showErrorWithReport(`Failed to select environment: ${error instanceof Error ? error.message : String(error)}`);
        }
    }

    async getNotebookQueryContext(notebook: vscode.NotebookDocument): Promise<NotebookExecutionContext> {
        return this.states.get(notebook.uri.toString())?.active?.context ?? this.resolveNotebookContext(notebook);
    }

    /** Also used by completion and conversion so they never use another notebook's binding. */
    async resolveNotebookContext(notebook: vscode.NotebookDocument, promptIfMissing = false): Promise<NotebookExecutionContext> {
        let requestedUrl = bindingUrl(notebook);
        const profile = await this.daemon.authWho();
        const profileName = profile.name ?? String(profile.index);
        if (!profile.profileIdentity) throw new Error('Notebook isolation requires an updated PPDS daemon. Restart the daemon with this release before running.');
        const sessionDefault = profile.environment?.url
            ? { url: profile.environment.url, name: profile.environment.displayName || profile.environment.url }
            : undefined;
        this.sessionDefault = sessionDefault;
        if (!requestedUrl && !sessionDefault && promptIfMissing) {
            await this.selectEnvironment(notebook);
            requestedUrl = bindingUrl(notebook);
        }
        const effectiveUrl = requestedUrl ?? sessionDefault?.url;
        if (!effectiveUrl) throw new Error('No environment selected. Choose a notebook binding or a session default.');
        const environmentUrl = normalizeEnvironmentUrl(effectiveUrl);
        const environments = await this.daemon.envList(undefined, undefined, profileName);
        const environment = environments.environments.find(env => normalizeEnvironmentUrl(env.apiUrl) === environmentUrl);
        const config = await this.daemon.envConfigGet(environmentUrl);
        const defaultMatches = profile.environment?.url && normalizeEnvironmentUrl(profile.environment.url) === environmentUrl;
        const environmentType = config.type || environment?.type
            || (defaultMatches ? profile.environment?.type : undefined) || config.resolvedType || 'Unknown';
        const environmentName = config.label || environment?.friendlyName
            || (defaultMatches ? profile.environment?.displayName : undefined) || environmentUrl;
        this.environmentTypes.set(environmentUrl, environmentType);
        return Object.freeze({
            environmentUrl, environmentName, environmentType, profileName,
            profileIdentity: profile.profileIdentity,
        });
    }

    updateStatusBarVisibility(editor: vscode.NotebookEditor | undefined): void {
        if (editor?.notebook.notebookType !== 'ppdsnb') {
            this.statusBarItem.hide();
            return;
        }
        const notebook = editor.notebook;
        const state = this.states.get(notebook.uri.toString());
        const bound = bindingUrl(notebook);
        const name = typeof notebook.metadata?.environmentName === 'string' ? notebook.metadata.environmentName : bound;
        const type = bound ? this.environmentTypes.get(bound.replace(/\/+$/, '')) ?? 'Unknown' : undefined;
        const selection = bound ? `${name || bound} · ${type}` : `Session default${this.sessionDefault ? `: ${this.sessionDefault.name}` : ''}`;
        const running = state?.active?.context;
        this.statusBarItem.text = `$(database) ${selection}${running ? ` · Running on ${running.environmentName}` : ''}`;
        this.statusBarItem.tooltip = `${bound ?? this.sessionDefault?.url ?? 'No default selected'}\nClick to change this notebook's binding.${running ? `\nCurrent run: ${running.environmentName} · ${running.environmentType}\n${running.environmentUrl}\nBinding changes apply to the next run.` : ''}`;
        this.statusBarItem.show();
    }

    private executeHandler(cells: vscode.NotebookCell[], notebook: vscode.NotebookDocument): Promise<void> {
        if (this.disposed || notebook.isClosed) return Promise.resolve();
        const state = this.getState(notebook);
        return new Promise<void>(complete => {
            state.queue.push({ cells: cells.filter(cell => cell.kind === vscode.NotebookCellKind.Code), complete });
            void this.processQueue(state);
        });
    }

    private ownsRun(state: NotebookState, run: NotebookRun): boolean {
        return !this.disposed && this.states.get(state.notebook.uri.toString()) === state && state.active === run;
    }

    private async processQueue(state: NotebookState): Promise<void> {
        if (state.processing) return;
        state.processing = true;
        try {
            while (state.queue.length && !this.disposed && !state.notebook.isClosed) {
                const pending = state.queue.shift()!;
                if (pending.cells.length === 0) {
                    pending.complete();
                    continue;
                }
                const run: NotebookRun = { id: ++this.runSequence, cancelled: false };
                state.active = run;
                for (const cell of pending.cells) this.cellResults.delete(cell.document.uri.toString());
                try {
                    this.syncBinding(state);
                    const initialBinding = state.binding;
                    run.context = await this.resolveNotebookContext(state.notebook, true);
                    if (!this.ownsRun(state, run) || run.cancelled) continue;
                    const key = JSON.stringify([run.context.profileIdentity, run.context.environmentUrl, run.context.environmentType]);
                    if (state.acknowledgementTarget !== key) state.acknowledgement = undefined;
                    state.acknowledgementTarget = key;
                    const protectedTarget = ['production', 'unknown', ''].includes(run.context.environmentType.toLowerCase());
                    if (protectedTarget && state.acknowledgement !== key) {
                        const answer = await vscode.window.showWarningMessage(
                            `Run this notebook on ${run.context.environmentName} (${run.context.environmentType})?\n${run.context.environmentUrl}`,
                            { modal: true }, 'Run on this environment',
                        );
                        if (answer !== 'Run on this environment' || !this.ownsRun(state, run) || run.cancelled) continue;
                        if (initialBinding === bindingUrl(state.notebook)) state.acknowledgement = key;
                    }
                    this.updateStatusBarVisibility(vscode.window.activeNotebookEditor);
                    for (const cell of pending.cells) {
                        if (!this.ownsRun(state, run) || run.cancelled || state.notebook.isClosed) break;
                        await this.executeCell(cell, state, run, run.context);
                    }
                } catch (error) {
                    const cell = pending.cells[0];
                    if (cell && this.ownsRun(state, run) && !run.cancelled) {
                        const execution = this.controller.createNotebookCellExecution(cell);
                        execution.start(Date.now());
                        const outputs = run.context ? this.contextOutputs(run.context, new Date().toISOString()) : [];
                        await execution.replaceOutput([...outputs, this.errorOutput(error)]);
                        execution.end(false, Date.now());
                    }
                } finally {
                    if (state.active === run) state.active = undefined;
                    pending.complete();
                    this.updateStatusBarVisibility(vscode.window.activeNotebookEditor);
                }
            }
        } finally {
            state.processing = false;
        }
    }

    private interruptHandler(notebook: vscode.NotebookDocument): void {
        const state = this.states.get(notebook.uri.toString());
        if (!state) return;
        if (state.active) state.active.cancelled = true;
        for (const pending of state.queue.splice(0)) pending.complete();
        for (const entry of this.activeExecutions.values()) {
            if (entry.run === state.active) {
                entry.abort.abort();
                entry.cts.cancel();
            }
        }
    }

    private contextOutputs(context: NotebookExecutionContext, executedAt: string): vscode.NotebookCellOutput[] {
        return [new vscode.NotebookCellOutput([vscode.NotebookCellOutputItem.text(
            `${context.environmentName} · ${context.environmentType}\n${context.environmentUrl}\nExecuted ${executedAt}`, 'text/plain',
        )])];
    }

    private errorOutput(error: unknown): vscode.NotebookCellOutput {
        return new vscode.NotebookCellOutput([vscode.NotebookCellOutputItem.error(error instanceof Error ? error : new Error(String(error)))]);
    }

    private async executeCell(cell: vscode.NotebookCell, state: NotebookState, run: NotebookRun, context: NotebookExecutionContext): Promise<void> {
        const execution = this.controller.createNotebookCellExecution(cell);
        execution.executionOrder = ++this.executionOrder;
        const startedAt = Date.now();
        const executedAt = new Date(startedAt).toISOString();
        execution.start(startedAt);
        const cellUri = cell.document.uri.toString();
        this.cellResults.delete(cellUri);
        const entry: CellExecutionState = { run, abort: new AbortController(), cts: new CancellationTokenSource() };
        this.activeExecutions.set(cellUri, entry);
        const tokenDisposable = execution.token.onCancellationRequested(() => {
            entry.abort.abort();
            entry.cts.cancel();
        });
        const ownsExecution = (): boolean => this.ownsRun(state, run) && this.activeExecutions.get(cellUri) === entry;
        const writeOutput = async (output: vscode.NotebookCellOutput): Promise<void> => {
            if (ownsExecution()) await execution.replaceOutput([...this.contextOutputs(context, executedAt), output]);
        };
        const textOutput = (text: string): vscode.NotebookCellOutput =>
            new vscode.NotebookCellOutput([vscode.NotebookCellOutputItem.text(text, 'text/plain')]);
        let success = false;
        try {
            const content = cell.document.getText().trim();
            if (!content) {
                await writeOutput(textOutput('Empty query'));
                success = true;
                return;
            }
            const isFetchXml = ['fetchxml', 'xml'].includes(cell.document.languageId) || this.looksLikeFetchXml(content);
            const target = { environmentUrl: context.environmentUrl, profileName: context.profileName, profileIdentity: context.profileIdentity };
            const result = isFetchXml
                ? await this.daemon.queryFetch({ fetchXml: content, ...target }, entry.cts.token)
                : await this.daemon.querySql({ sql: content, ...target }, entry.cts.token);
            if (entry.abort.signal.aborted || run.cancelled) {
                await writeOutput(textOutput('Query cancelled'));
                return;
            }
            if (!ownsExecution()) return;
            this.cellResults.set(cellUri, { result, context, executedAt });
            await writeOutput(new vscode.NotebookCellOutput([
                vscode.NotebookCellOutputItem.text(renderResultsHtml(result, context.environmentUrl), 'text/html'),
            ]));
            success = result.success;
        } catch (error) {
            if (entry.abort.signal.aborted || run.cancelled) {
                await writeOutput(textOutput('Query cancelled'));
                return;
            }
            if (!ownsExecution()) return;
            if (isAuthError(error)) {
                // Name/index may have been reused since this run captured its profile.
                // Recovery must never invalidate whichever profile now owns that alias.
                const message = error instanceof Error ? error.message : String(error);
                await writeOutput(this.errorOutput(new Error(
                    `${message}\nAuthentication failed for the profile captured by this run (${context.profileName}) on ${context.environmentName}. Confirm the intended profile in PPDS Profiles, re-authenticate it, then run the cell again.`,
                )));
                return;
            }
            await writeOutput(this.errorOutput(error));
        } finally {
            if (this.activeExecutions.get(cellUri) === entry) this.activeExecutions.delete(cellUri);
            tokenDisposable.dispose();
            entry.cts.dispose();
            execution.end(success, Date.now());
        }
    }

    private looksLikeFetchXml(content: string): boolean {
        const trimmed = content.trimStart().toLowerCase();
        return trimmed.startsWith('<fetch') || trimmed.startsWith('<?xml');
    }
    // ========== AUTO LANGUAGE SWITCHING ==========

    private autoSwitchTimer: ReturnType<typeof setTimeout> | undefined;
    private static readonly AUTO_SWITCH_DEBOUNCE_MS = 100;

    private registerAutoSwitchListener(): void {
        this.disposables.push(
            vscode.workspace.onDidChangeTextDocument(event => {
                if (event.document.uri.scheme !== 'vscode-notebook-cell') return;

                const notebook = vscode.workspace.notebookDocuments.find(
                    nb => nb.notebookType === 'ppdsnb' &&
                          nb.getCells().some(c => c.document.uri.toString() === event.document.uri.toString())
                );
                if (!notebook) return;

                const content = event.document.getText().trim();
                if (!content) return;

                let contentToAnalyze: string;
                if (event.contentChanges.length === 1) {
                    const change = event.contentChanges[0];
                    if (change && change.text.length > AUTO_SWITCH_THRESHOLD && change.text.trim().length > 0) {
                        contentToAnalyze = change.text.trim();
                    } else if (content.length <= AUTO_SWITCH_THRESHOLD) {
                        contentToAnalyze = content;
                    } else {
                        return;
                    }
                } else if (content.length <= AUTO_SWITCH_THRESHOLD) {
                    contentToAnalyze = content;
                } else {
                    return;
                }

                const currentLanguage = event.document.languageId;
                // Heuristic: content starting with '<' is likely FetchXML.
                // May misfire for SQL with leading comments, but user can manually toggle.
                const shouldBeFetchXml = contentToAnalyze.charAt(0) === '<';

                const targetLanguage = shouldBeFetchXml ? 'fetchxml' : 'sql';
                if (currentLanguage === targetLanguage) return;

                // Debounce to avoid flicker during rapid typing
                clearTimeout(this.autoSwitchTimer);
                this.autoSwitchTimer = setTimeout(() => {
                    void vscode.languages.setTextDocumentLanguage(event.document, targetLanguage);
                }, DataverseNotebookController.AUTO_SWITCH_DEBOUNCE_MS);
            })
        );
    }

    // ========== EXPORT SUPPORT ==========

    getCellResults(cellUri: string): QueryResultResponse | undefined {
        return this.cellResults.get(cellUri)?.result;
    }

    getCellExecutionContext(cellUri: string): { context: NotebookExecutionContext; executedAt: string } | undefined {
        const cached = this.cellResults.get(cellUri);
        return cached ? { context: cached.context, executedAt: cached.executedAt } : undefined;
    }

    hasCellResults(cellUri: string): boolean {
        return this.cellResults.has(cellUri);
    }

    // ========== DISPOSAL ==========

    dispose(): void {
        this.disposed = true;
        for (const state of this.states.values()) this.interruptHandler(state.notebook);
        this.states.clear();
        this.environmentTypes.clear();
        clearTimeout(this.autoSwitchTimer);
        this.cellResults.clear();
        this.controller.dispose();
        this.statusBarItem.dispose();
        for (const d of this.disposables) d.dispose();
    }
}
