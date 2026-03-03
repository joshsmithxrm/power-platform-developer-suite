import * as vscode from 'vscode';
import type { DaemonClient } from '../daemonClient.js';
import type { QueryResultResponse } from '../types.js';
import { renderResultsHtml } from './notebookResultRenderer.js';
import { isAuthError } from '../utils/errorUtils.js';

const AUTO_SWITCH_THRESHOLD = 30;

export class DataverseNotebookController implements vscode.Disposable {
    private readonly controller: vscode.NotebookController;
    private readonly disposables: vscode.Disposable[] = [];

    /**
     * Current environment URL. Shared across all notebooks in this session.
     * Each notebook persists its preference in metadata, and the controller
     * loads it when the notebook gains focus. However, queries always run
     * against this single active environment.
     * TODO: Support true per-notebook environment isolation.
     */
    private selectedEnvironmentUrl: string | undefined;
    private selectedEnvironmentName: string | undefined;
    private statusBarItem: vscode.StatusBarItem;

    private readonly cellResults = new Map<string, QueryResultResponse>();
    private readonly activeExecutions = new Map<string, AbortController>();
    private executionInterrupted = false;
    private executionOrder = 0;

    constructor(private readonly daemon: DaemonClient) {
        this.controller = vscode.notebooks.createNotebookController(
            'ppdsnb-controller', 'ppdsnb', 'Power Platform Developer Suite'
        );
        this.controller.supportedLanguages = ['sql', 'fetchxml'];
        this.controller.supportsExecutionOrder = true;
        this.controller.executeHandler = this.executeHandler.bind(this);
        this.controller.interruptHandler = this.interruptHandler.bind(this);

        this.statusBarItem = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Right, 100);
        this.statusBarItem.command = 'ppds.selectNotebookEnvironment';
        this.updateStatusBar();

        this.disposables.push(
            vscode.window.onDidChangeActiveNotebookEditor(editor => {
                this.updateStatusBarVisibility(editor);
            })
        );

        this.disposables.push(
            vscode.workspace.onDidOpenNotebookDocument(notebook => {
                if (notebook.notebookType === 'ppdsnb') {
                    this.loadEnvironmentFromNotebook(notebook);
                    this.updateStatusBarVisibility(vscode.window.activeNotebookEditor);
                }
            })
        );

        this.disposables.push(
            vscode.workspace.onDidCloseNotebookDocument(notebook => {
                if (notebook.notebookType === 'ppdsnb') {
                    this.interruptHandler(notebook);
                    // Clean up cached results for cells in this notebook
                    for (const cell of notebook.getCells()) {
                        this.cellResults.delete(cell.document.uri.toString());
                    }
                }
            })
        );

        this.registerAutoSwitchListener();
        this.checkOpenNotebooks();
    }

    // ========== ENVIRONMENT MANAGEMENT ==========

    async selectEnvironment(): Promise<void> {
        try {
            const envResult = await this.daemon.envList();
            if (envResult.environments.length === 0) {
                vscode.window.showErrorMessage('No environments found. Create a profile and select an environment first.');
                return;
            }

            const items = envResult.environments.map(env => ({
                label: env.friendlyName,
                description: `${env.type ?? ''} ${env.region ?? ''}`.trim(),
                detail: env.apiUrl,
                apiUrl: env.apiUrl,
                picked: env.isActive,
            }));

            const selected = await vscode.window.showQuickPick(items, {
                placeHolder: 'Select Dataverse environment for this notebook',
            });

            if (selected) {
                await this.daemon.envSelect(selected.apiUrl);
                this.selectedEnvironmentUrl = selected.apiUrl;
                this.selectedEnvironmentName = selected.label;
                this.updateStatusBar();

                const activeEditor = vscode.window.activeNotebookEditor;
                if (activeEditor?.notebook.notebookType === 'ppdsnb') {
                    const edit = new vscode.WorkspaceEdit();
                    edit.set(activeEditor.notebook.uri, [
                        vscode.NotebookEdit.updateNotebookMetadata({
                            ...activeEditor.notebook.metadata,
                            environmentName: this.selectedEnvironmentName,
                            environmentUrl: this.selectedEnvironmentUrl,
                        }),
                    ]);
                    await vscode.workspace.applyEdit(edit);
                }
            }
        } catch (err) {
            const msg = err instanceof Error ? err.message : String(err);
            vscode.window.showErrorMessage(`Failed to select environment: ${msg}`);
        }
    }

    public updateEnvironment(url: string): void {
        this.selectedEnvironmentUrl = url;
    }

    loadEnvironmentFromNotebook(notebook: vscode.NotebookDocument): void {
        const metadata = notebook.metadata as { environmentName?: string; environmentUrl?: string } | undefined;
        if (metadata?.environmentUrl) {
            this.selectedEnvironmentUrl = metadata.environmentUrl;
            this.selectedEnvironmentName = metadata.environmentName;
            this.updateStatusBar();
        }
    }

    updateStatusBarVisibility(editor: vscode.NotebookEditor | undefined): void {
        if (editor?.notebook.notebookType === 'ppdsnb') {
            this.loadEnvironmentFromNotebook(editor.notebook);
            this.statusBarItem.show();
        } else {
            this.statusBarItem.hide();
        }
    }

    private updateStatusBar(): void {
        this.statusBarItem.text = this.selectedEnvironmentName
            ? `$(database) ${this.selectedEnvironmentName}`
            : '$(database) Select Environment';
        this.statusBarItem.tooltip = this.selectedEnvironmentName
            ? `Dataverse: ${this.selectedEnvironmentName}\nClick to change`
            : 'Click to select Dataverse environment';
    }

    private checkOpenNotebooks(): void {
        for (const notebook of vscode.workspace.notebookDocuments) {
            if (notebook.notebookType === 'ppdsnb') {
                this.loadEnvironmentFromNotebook(notebook);
                this.statusBarItem.show();
                if (!this.selectedEnvironmentUrl) {
                    this.promptForEnvironment();
                }
                return;
            }
        }
    }

    private promptForEnvironment(): void {
        vscode.window.showInformationMessage(
            'Select a Dataverse environment to run queries.',
            'Select Environment'
        ).then(selection => {
            if (selection === 'Select Environment') {
                void this.selectEnvironment();
            }
        });
    }

    // ========== CELL EXECUTION ==========

    private async executeHandler(
        cells: vscode.NotebookCell[],
        _notebook: vscode.NotebookDocument,
        _controller: vscode.NotebookController
    ): Promise<void> {
        this.executionInterrupted = false;
        for (const cell of cells) {
            if (this.executionInterrupted) break;
            await this.executeCell(cell);
        }
    }

    private interruptHandler(notebook: vscode.NotebookDocument): void {
        this.executionInterrupted = true;
        for (const cell of notebook.getCells()) {
            const cellUri = cell.document.uri.toString();
            const abort = this.activeExecutions.get(cellUri);
            if (abort) {
                abort.abort();
                this.activeExecutions.delete(cellUri);
            }
        }
    }

    private async executeCell(cell: vscode.NotebookCell): Promise<void> {
        const execution = this.controller.createNotebookCellExecution(cell);
        execution.executionOrder = ++this.executionOrder;
        execution.start(Date.now());

        const cellUri = cell.document.uri.toString();
        const existing = this.activeExecutions.get(cellUri);
        if (existing) {
            existing.abort();
        }
        const abortController = new AbortController();
        this.activeExecutions.set(cellUri, abortController);

        const tokenDisposable = execution.token.onCancellationRequested(() => {
            abortController.abort();
        });

        try {
            if (!this.selectedEnvironmentUrl) {
                await this.selectEnvironment();
                if (!this.selectedEnvironmentUrl) {
                    execution.replaceOutput([new vscode.NotebookCellOutput([
                        vscode.NotebookCellOutputItem.text('No environment selected. Click the environment selector in the status bar.', 'text/plain'),
                    ])]);
                    execution.end(false, Date.now());
                    return;
                }
            }

            const content = cell.document.getText().trim();
            if (!content) {
                execution.replaceOutput([new vscode.NotebookCellOutput([
                    vscode.NotebookCellOutputItem.text('Empty query', 'text/plain'),
                ])]);
                execution.end(true, Date.now());
                return;
            }

            if (abortController.signal.aborted) {
                execution.end(false, Date.now());
                return;
            }

            const language = cell.document.languageId;
            const isFetchXml = language === 'fetchxml' || language === 'xml' || this.looksLikeFetchXml(content);

            let result: QueryResultResponse;
            if (isFetchXml) {
                result = await this.daemon.queryFetch({ fetchXml: content });
            } else {
                result = await this.daemon.querySql({ sql: content });
            }

            if (abortController.signal.aborted) {
                execution.replaceOutput([new vscode.NotebookCellOutput([
                    vscode.NotebookCellOutputItem.text('Query cancelled', 'text/plain'),
                ])]);
                execution.end(false, Date.now());
                // tokenDisposable is cleaned up in the finally block
                return;
            }

            this.cellResults.set(cellUri, result);

            const html = renderResultsHtml(result, this.selectedEnvironmentUrl);
            execution.replaceOutput([new vscode.NotebookCellOutput([
                vscode.NotebookCellOutputItem.text(html, 'text/html'),
            ])]);
            execution.end(true, Date.now());

        } catch (error) {
            if (abortController.signal.aborted) {
                execution.replaceOutput([new vscode.NotebookCellOutput([
                    vscode.NotebookCellOutputItem.text('Query cancelled', 'text/plain'),
                ])]);
                execution.end(false, Date.now());
                return;
            }

            // Check for auth errors and offer re-authentication
            if (isAuthError(error)) {
                const action = await vscode.window.showErrorMessage(
                    'Session expired. Re-authenticate?',
                    'Re-authenticate', 'Cancel'
                );
                if (action === 'Re-authenticate') {
                    try {
                        const who = await this.daemon.authWho();
                        if (who.name) {
                            await this.daemon.profilesInvalidate(who.name);
                        }
                    } catch {
                        // If authWho fails, we can't invalidate - just proceed with re-auth
                    }
                    try {
                        // Don't retry automatically for notebooks - user can re-execute
                        execution.replaceOutput([new vscode.NotebookCellOutput([
                            vscode.NotebookCellOutputItem.text('Re-authenticated. Please re-execute the cell.', 'text/plain'),
                        ])]);
                        execution.end(false, Date.now());
                        return;
                    } catch { /* fall through */ }
                }
            }

            execution.replaceOutput([
                new vscode.NotebookCellOutput([
                    vscode.NotebookCellOutputItem.error(error instanceof Error ? error : new Error(String(error))),
                ]),
            ]);
            execution.end(false, Date.now());
        } finally {
            this.activeExecutions.delete(cellUri);
            tokenDisposable.dispose();
        }
    }

    private looksLikeFetchXml(content: string): boolean {
        const trimmed = content.trimStart().toLowerCase();
        return trimmed.startsWith('<fetch') || trimmed.startsWith('<?xml');
    }

    // ========== AUTO LANGUAGE SWITCHING ==========

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

                if (shouldBeFetchXml && currentLanguage !== 'fetchxml') {
                    void vscode.languages.setTextDocumentLanguage(event.document, 'fetchxml');
                } else if (!shouldBeFetchXml && currentLanguage === 'fetchxml') {
                    void vscode.languages.setTextDocumentLanguage(event.document, 'sql');
                }
            })
        );
    }

    // ========== EXPORT SUPPORT ==========

    getCellResults(cellUri: string): QueryResultResponse | undefined {
        return this.cellResults.get(cellUri);
    }

    hasCellResults(cellUri: string): boolean {
        return this.cellResults.has(cellUri);
    }

    // ========== DISPOSAL ==========

    dispose(): void {
        this.cellResults.clear();
        this.controller.dispose();
        this.statusBarItem.dispose();
        for (const d of this.disposables) d.dispose();
    }
}
