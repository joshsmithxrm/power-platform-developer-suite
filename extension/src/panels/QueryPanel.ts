import * as vscode from 'vscode';
import { CancellationTokenSource, ResponseError } from 'vscode-jsonrpc/node';
import { WebviewPanelBase } from './WebviewPanelBase.js';
import { getNonce } from './webviewUtils.js';
import type { DaemonClient } from '../daemonClient.js';
import type { QueryResultResponse } from '../types.js';
import { showQueryHistory } from '../commands/queryHistoryCommand.js';
import { isAuthError } from '../utils/errorUtils.js';
import { getEnvironmentPickerCss, getEnvironmentPickerHtml, getEnvironmentPickerJs, showEnvironmentPicker } from './environmentPicker.js';

export class QueryPanel extends WebviewPanelBase {
    private static instances: QueryPanel[] = [];
    private static nextId = 1;
    private readonly panelId: number;

    private static readonly MAX_PANELS = 10;
    private static readonly MAX_CLIENT_RECORDS = 5_000;

    /**
     * Returns the number of open QueryPanel instances.
     * Used by diagnostic commands for panel state inspection.
     */
    static get instanceCount(): number {
        return QueryPanel.instances.length;
    }

    static show(extensionUri: vscode.Uri, daemon: DaemonClient, initialSql?: string, envUrl?: string, envDisplayName?: string): QueryPanel {
        if (QueryPanel.instances.length >= QueryPanel.MAX_PANELS) {
            const oldest = QueryPanel.instances[0];
            oldest.panel?.reveal();
            if (initialSql) {
                oldest.postMessage({ command: 'loadQuery', sql: initialSql });
            }
            return oldest;
        }
        const panel = new QueryPanel(extensionUri, daemon, initialSql, envUrl, envDisplayName);
        return panel;
    }

    private queryCts: CancellationTokenSource | undefined;
    private allRecords: Record<string, unknown>[] = [];
    private lastResult: QueryResultResponse | undefined;
    private lastSql: string | undefined;
    private lastUseTds = false;
    /** Language used for the last query (for loadMore to use the same execution path). */
    private lastLanguage = 'sql';
    private environmentUrl: string | undefined;
    private environmentDisplayName: string | undefined;
    private profileName: string | undefined;

    private constructor(
        private readonly extensionUri: vscode.Uri,
        private readonly daemon: DaemonClient,
        initialSql?: string,
        initialEnvUrl?: string,
        initialEnvDisplayName?: string,
    ) {
        super();

        // Pre-set environment if provided (from tree context menu)
        if (initialEnvUrl) {
            this.environmentUrl = initialEnvUrl;
            this.environmentDisplayName = initialEnvDisplayName ?? initialEnvUrl;
        }

        this.panelId = QueryPanel.nextId++;
        QueryPanel.instances.push(this);

        this.panel = vscode.window.createWebviewPanel(
            'ppds.dataExplorer',
            'Data Explorer #' + this.panelId,
            vscode.ViewColumn.One,
            {
                enableScripts: true,
                retainContextWhenHidden: true,
                localResourceRoots: [
                    vscode.Uri.joinPath(extensionUri, 'node_modules'),
                    vscode.Uri.joinPath(extensionUri, 'dist'),
                ],
            }
        );

        this.panel.webview.html = this.getHtmlContent(this.panel.webview);

        this.disposables.push(
            this.panel.webview.onDidReceiveMessage(
                async (message: { command: string; [key: string]: unknown }) => {
                    switch (message.command) {
                        case 'executeQuery':
                            await this.executeQuery(message.sql as string, false, message.useTds as boolean | undefined, message.language as string | undefined);
                            break;
                        case 'showFetchXml':
                            await this.showFetchXml(message.sql as string);
                            break;
                        case 'loadMore':
                            await this.loadMore(message.pagingCookie as string, message.page as number);
                            break;
                        case 'explainQuery':
                            await this.explainQuery(message.sql as string);
                            break;
                        case 'exportResults':
                            await this.exportResults();
                            break;
                        case 'openInNotebook':
                            await vscode.commands.executeCommand('ppds.openQueryInNotebook', message.sql as string);
                            break;
                        case 'showHistory': {
                            const sql = await showQueryHistory(this.daemon);
                            if (sql) {
                                this.postMessage({ command: 'loadQuery', sql });
                            }
                            break;
                        }
                        case 'copyToClipboard':
                            await vscode.env.clipboard.writeText(message.text as string);
                            break;
                        case 'openRecordUrl':
                            if (message.url) {
                                await vscode.env.openExternal(vscode.Uri.parse(message.url as string));
                            }
                            break;
                        case 'requestClipboard': {
                            const clipText = await vscode.env.clipboard.readText();
                            this.postMessage({ command: 'clipboardContent', text: clipText });
                            break;
                        }
                        case 'requestCompletions': {
                            const requestId = message.requestId as number;
                            try {
                                const result = await this.daemon.queryComplete({
                                    sql: message.sql as string,
                                    cursorOffset: message.cursorOffset as number,
                                    language: message.language as string,
                                });
                                this.postMessage({ command: 'completionResult', requestId, items: result.items });
                            } catch {
                                this.postMessage({ command: 'completionResult', requestId, items: [] });
                            }
                            break;
                        }
                        case 'ready':
                            if (initialSql) {
                                this.postMessage({ command: 'loadQuery', sql: initialSql });
                            }
                            // Initialize environment from active profile
                            this.initEnvironment();
                            break;
                        case 'cancelQuery':
                            this.queryCts?.cancel();
                            break;
                        case 'refresh':
                            if (this.lastSql) {
                                await this.executeQuery(this.lastSql, false, this.lastUseTds, this.lastLanguage);
                            }
                            break;
                        case 'requestEnvironmentList': {
                            const env = await showEnvironmentPicker(this.daemon, this.environmentUrl);
                            if (env) {
                                this.environmentUrl = env.url;
                                this.environmentDisplayName = env.displayName;
                                this.postMessage({ command: 'updateEnvironment', name: env.displayName, url: env.url });
                                this.updateTitle();
                            }
                            break;
                        }
                    }
                }
            )
        );

        this.disposables.push(
            this.panel.onDidDispose(() => this.dispose())
        );

        this.subscribeToDaemonReconnect(this.daemon);
    }

    override dispose(): void {
        // Guard: super.dispose() checks _disposed, but we must also guard
        // the instances splice to prevent re-entrant onDidDispose removing
        // the wrong panel (splice(-1, 1) removes last element).
        const idx = QueryPanel.instances.indexOf(this);
        if (idx >= 0) QueryPanel.instances.splice(idx, 1);
        this.queryCts?.cancel();
        this.queryCts?.dispose();
        this.lastSql = undefined;
        this.lastUseTds = false;
        this.lastResult = undefined;
        this.allRecords = [];
        // super.dispose() has its own _disposed guard to prevent re-entrancy
        super.dispose();
    }

    private async initEnvironment(): Promise<void> {
        // Always fetch profile name for the title
        try {
            const who = await this.daemon.authWho();
            this.profileName = who.name ?? `Profile ${who.index}`;
            if (!this.environmentUrl && who.environment) {
                this.environmentUrl = who.environment.url;
                this.environmentDisplayName = who.environment.displayName;
            }
        } catch {
            // No active profile or environment
        }
        this.postMessage({ command: 'updateEnvironment', name: this.environmentDisplayName ?? 'No environment', url: this.environmentUrl ?? null });
        this.updateTitle();
    }

    private updateTitle(): void {
        if (!this.panel) return;
        const parts = [`Data Explorer #${this.panelId}`];
        if (this.profileName) parts.push(this.profileName);
        if (this.environmentDisplayName) parts.push(this.environmentDisplayName);
        this.panel.title = parts.join(' — ');
    }

    private async executeQuery(sql: string, isRetry = false, useTds?: boolean, language?: string, isConfirmed = false): Promise<void> {
        // Cancel any in-flight query and create a fresh token
        this.queryCts?.cancel();
        this.queryCts?.dispose();
        this.queryCts = new CancellationTokenSource();
        const token = this.queryCts.token;

        try {
            this.postMessage({ command: 'executionStarted' });
            const defaultTop = vscode.workspace.getConfiguration('ppds').get<number>('queryDefaultTop', 100);
            const tds = useTds ?? false;

            let result: QueryResultResponse;
            if (language === 'xml') {
                result = await this.daemon.queryFetch({ fetchXml: sql, top: defaultTop, environmentUrl: this.environmentUrl }, token);
            } else {
                result = await this.daemon.querySql({ sql, top: defaultTop, useTds: tds, environmentUrl: this.environmentUrl, dmlSafety: { isConfirmed } }, token);
            }
            this.lastSql = sql;
            this.lastUseTds = tds;
            this.lastLanguage = language ?? 'sql';
            this.lastResult = result;
            this.allRecords = [...result.records];
            this.postMessage({
                command: 'queryResult',
                data: result,
            });
        } catch (error) {
            // If the token was cancelled, notify the webview and return early
            if (token.isCancellationRequested) {
                this.postMessage({ command: 'queryCancelled' });
                return;
            }

            // Check for DML safety errors from the daemon
            if (error instanceof ResponseError) {
                const data = error.data as { dmlBlocked?: boolean; dmlConfirmationRequired?: boolean; message?: string; code?: string } | undefined;

                // DML confirmation required — prompt the user
                if (data?.dmlConfirmationRequired || data?.code === 'Query.DmlConfirmationRequired') {
                    const choice = await vscode.window.showWarningMessage(
                        data?.message || 'This DML operation requires confirmation.',
                        { modal: true },
                        'Execute Anyway'
                    );
                    if (choice === 'Execute Anyway') {
                        await this.executeQuery(sql, isRetry, useTds, language, true);
                    } else {
                        this.postMessage({ command: 'queryCancelled' });
                    }
                    return;
                }

                // DML blocked outright — show error
                if (data?.dmlBlocked || data?.code === 'Query.DmlBlocked') {
                    this.postMessage({ command: 'queryError', error: data?.message || 'DML operation blocked.' });
                    return;
                }
            }

            const msg = error instanceof Error ? error.message : String(error);

            // Check for auth errors and offer re-authentication (only on first attempt)
            if (isAuthError(error) && !isRetry) {
                const action = await vscode.window.showErrorMessage(
                    'Session expired. Re-authenticate?',
                    'Re-authenticate', 'Cancel'
                );
                if (action === 'Re-authenticate') {
                    try {
                        const who = await this.daemon.authWho();
                        const profileId = who.name ?? String(who.index);
                        await this.daemon.profilesInvalidate(profileId);
                    } catch {
                        // If authWho fails, we can't invalidate - just proceed with re-auth
                    }
                    try {
                        // Retry the query with isRetry=true to prevent recursive re-auth prompts
                        await this.executeQuery(sql, true);
                        return;
                    } catch {
                        // Fall through to show error
                    }
                }
            }

            this.postMessage({ command: 'queryError', error: msg });
        }
    }

    private async showFetchXml(sql: string): Promise<void> {
        try {
            const result = await this.daemon.queryExplain({ sql, environmentUrl: this.environmentUrl });
            if (result.plan) {
                const doc = await vscode.workspace.openTextDocument({
                    content: result.plan,
                    language: 'xml',
                });
                await vscode.window.showTextDocument(doc, vscode.ViewColumn.Beside);
            }
        } catch (error) {
            const msg = error instanceof Error ? error.message : String(error);
            vscode.window.showErrorMessage(`FetchXML preview failed: ${msg}`);
        }
    }

    private async explainQuery(sql: string): Promise<void> {
        try {
            const result = await this.daemon.queryExplain({ sql, environmentUrl: this.environmentUrl });
            const language = result.format === 'fetchxml' ? 'xml' : 'text';
            const doc = await vscode.workspace.openTextDocument({
                content: result.plan,
                language,
            });
            await vscode.window.showTextDocument(doc, vscode.ViewColumn.Beside);
        } catch (error) {
            const msg = error instanceof Error ? error.message : String(error);
            vscode.window.showErrorMessage(`EXPLAIN failed: ${msg}`);
        }
    }

    private async loadMore(pagingCookie: string, page: number): Promise<void> {
        if (!this.lastResult || !this.lastSql) return;
        try {
            this.postMessage({ command: 'executionStarted' });

            let result: QueryResultResponse;
            if (this.lastLanguage === 'xml') {
                result = await this.daemon.queryFetch({
                    fetchXml: this.lastSql,
                    page,
                    pagingCookie,
                    environmentUrl: this.environmentUrl,
                });
            } else {
                result = await this.daemon.querySql({
                    sql: this.lastSql,
                    page,
                    pagingCookie,
                    useTds: this.lastUseTds,
                    environmentUrl: this.environmentUrl,
                });
            }

            if (this.allRecords.length + result.records.length > QueryPanel.MAX_CLIENT_RECORDS) {
                this.postMessage({
                    command: 'queryError',
                    error: `Record limit reached (${QueryPanel.MAX_CLIENT_RECORDS.toLocaleString()}). Export your results for larger datasets.`,
                });
                return;
            }

            this.lastResult = result;
            this.allRecords.push(...result.records);
            this.postMessage({ command: 'appendResults', data: result });
        } catch (error) {
            const msg = error instanceof Error ? error.message : String(error);
            this.postMessage({ command: 'queryError', error: msg });
        }
    }

    private async exportResults(): Promise<void> {
        if (!this.lastResult || this.allRecords.length === 0 || !this.lastSql) {
            vscode.window.showWarningMessage('No results to export. Run a query first.');
            return;
        }

        const formatPick = await vscode.window.showQuickPick([
            { label: 'CSV', description: 'Comma-separated values', format: 'csv', isClipboard: false },
            { label: 'TSV', description: 'Tab-separated values', format: 'tsv', isClipboard: false },
            { label: 'JSON', description: 'JSON array', format: 'json', isClipboard: false },
            { label: 'Clipboard', description: 'Copy as TSV to clipboard', format: 'tsv', isClipboard: true },
        ], {
            title: `Export ${this.allRecords.length} rows`,
            placeHolder: 'Select export format',
        });

        if (!formatPick) return;

        // Headers toggle
        const headersPick = await vscode.window.showQuickPick([
            { label: 'Include column headers', includeHeaders: true },
            { label: 'Data only (no headers)', includeHeaders: false },
        ], {
            title: 'Column Headers',
            placeHolder: 'Include headers in export?',
        });

        if (!headersPick) return;

        try {
            const result = await this.daemon.queryExport({
                sql: this.lastSql,
                format: formatPick.format,
                includeHeaders: headersPick.includeHeaders,
                environmentUrl: this.environmentUrl,
            });

            if (formatPick.isClipboard) {
                await vscode.env.clipboard.writeText(result.content);
                vscode.window.showInformationMessage(`Copied ${result.rowCount} rows to clipboard`);
                return;
            }

            const fileExt = formatPick.format === 'tsv' ? 'tsv' : formatPick.format === 'json' ? 'json' : 'csv';
            const filterName = formatPick.format === 'tsv' ? 'TSV Files' : formatPick.format === 'json' ? 'JSON Files' : 'CSV Files';

            const uri = await vscode.window.showSaveDialog({
                defaultUri: vscode.Uri.file(`query_results.${fileExt}`),
                filters: { [filterName]: [fileExt] },
            });
            if (uri) {
                await vscode.workspace.fs.writeFile(uri, new TextEncoder().encode(result.content));
                vscode.window.showInformationMessage(`Exported ${result.rowCount} rows to ${uri.fsPath}`);
            }
        } catch (error) {
            const msg = error instanceof Error ? error.message : String(error);
            vscode.window.showErrorMessage(`Export failed: ${msg}`);
        }
    }

    getHtmlContent(webview: vscode.Webview): string {
        const toolkitUri = webview.asWebviewUri(
            vscode.Uri.joinPath(this.extensionUri, 'node_modules', '@vscode', 'webview-ui-toolkit', 'dist', 'toolkit.min.js')
        );
        const monacoUri = webview.asWebviewUri(
            vscode.Uri.joinPath(this.extensionUri, 'dist', 'monaco-editor.js')
        );
        const monacoCssUri = webview.asWebviewUri(
            vscode.Uri.joinPath(this.extensionUri, 'dist', 'monaco-editor.css')
        );
        const workerUri = webview.asWebviewUri(
            vscode.Uri.joinPath(this.extensionUri, 'dist', 'editor.worker.js')
        );
        const nonce = getNonce();

        return `<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="UTF-8">
    <!-- 'unsafe-inline' is required by @vscode/webview-ui-toolkit for dynamic styles -->
    <meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src ${webview.cspSource} 'unsafe-inline'; script-src 'nonce-${nonce}'; font-src ${webview.cspSource}; worker-src blob:;">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <script type="module" nonce="${nonce}" src="${toolkitUri}"></script>
    <link rel="stylesheet" href="${monacoCssUri}">
    <script nonce="${nonce}">self.__MONACO_WORKER_URL__ = '${workerUri}';</script>
    <script nonce="${nonce}" src="${monacoUri}"></script>
</head>
<body>
<style>
    body { margin: 0; padding: 0; display: flex; flex-direction: column; height: 100vh; font-family: var(--vscode-font-family); color: var(--vscode-foreground); background: var(--vscode-editor-background); }
    .toolbar { display: flex; gap: 8px; padding: 8px 12px; border-bottom: 1px solid var(--vscode-panel-border); flex-shrink: 0; align-items: center; }
    .toolbar-spacer { flex: 1; }
    .editor-container { flex-shrink: 0; border-bottom: 1px solid var(--vscode-panel-border); }
    .editor-wrapper { height: 150px; min-height: 120px; max-height: 300px; overflow: hidden; resize: vertical; position: relative; }
    #sql-editor { position: absolute; top: 0; left: 0; right: 0; bottom: 0; }
    .results-wrapper { flex: 1; overflow: auto; position: relative; min-height: 0; }
    .results-table { width: max-content; min-width: 100%; border-collapse: collapse; }
    .results-table thead { position: sticky; top: 0; z-index: 1; }
    .results-table th { padding: 6px 12px; text-align: left; font-weight: 600; background: var(--vscode-button-background); color: var(--vscode-button-foreground); border-bottom: 2px solid var(--vscode-panel-border); border-right: 1px solid rgba(255,255,255,0.1); white-space: nowrap; cursor: pointer; user-select: none; }
    .results-table th:last-child { border-right: none; }
    .results-table th .sort-indicator { margin-left: 4px; opacity: 0.7; }
    .results-table td { padding: 6px 12px; white-space: nowrap; border-bottom: 1px solid var(--vscode-panel-border); user-select: none; }
    .results-table tr:nth-child(even) { background: var(--vscode-list-inactiveSelectionBackground); }
    .results-table tr:hover { background: var(--vscode-list-hoverBackground); }
    .results-table td.cell-selected { background: var(--vscode-editor-selectionBackground) !important; }
    .results-table td.cell-selected-top { border-top: 2px solid var(--vscode-focusBorder) !important; }
    .results-table td.cell-selected-bottom { border-bottom: 2px solid var(--vscode-focusBorder) !important; }
    .results-table td.cell-selected-left { border-left: 2px solid var(--vscode-focusBorder) !important; }
    .results-table td.cell-selected-right { border-right: 2px solid var(--vscode-focusBorder) !important; }
    tbody.selecting { cursor: cell !important; user-select: none !important; }
    tbody.all-selected td { background: var(--vscode-editor-selectionBackground) !important; }
    .results-table td a { color: var(--vscode-textLink-foreground); text-decoration: none; }
    .results-table td a:hover { text-decoration: underline; }
    .context-menu { position: fixed; z-index: 1000; background: var(--vscode-menu-background, var(--vscode-editor-background)); border: 1px solid var(--vscode-menu-border, var(--vscode-panel-border)); border-radius: 4px; padding: 4px 0; box-shadow: 0 2px 8px rgba(0,0,0,0.3); min-width: 160px; }
    .context-menu-item { padding: 4px 16px; cursor: pointer; font-size: 13px; color: var(--vscode-menu-foreground, var(--vscode-foreground)); white-space: nowrap; }
    .context-menu-item:hover { background: var(--vscode-menu-selectionBackground, var(--vscode-list-hoverBackground)); color: var(--vscode-menu-selectionForeground, var(--vscode-foreground)); }
    .status-bar { display: flex; gap: 16px; padding: 4px 12px; border-top: 1px solid var(--vscode-panel-border); font-size: 12px; color: var(--vscode-descriptionForeground); flex-shrink: 0; }
    .filter-bar { display: none; padding: 4px 12px; border-bottom: 1px solid var(--vscode-panel-border); }
    .filter-bar.visible { display: flex; align-items: center; gap: 8px; }
    .filter-bar input { flex: 1; background: var(--vscode-input-background); color: var(--vscode-input-foreground); border: 1px solid var(--vscode-input-border, var(--vscode-panel-border)); padding: 4px 8px; font-size: 13px; border-radius: 2px; outline: none; }
    .filter-bar input:focus { border-color: var(--vscode-focusBorder); }
    .load-more-bar { padding: 8px 12px; text-align: center; border-top: 1px solid var(--vscode-panel-border); }
    .empty-state { padding: 40px; text-align: center; color: var(--vscode-descriptionForeground); font-style: italic; }
    .error-state { padding: 12px; background: var(--vscode-inputValidation-errorBackground, rgba(255,0,0,0.1)); border: 1px solid var(--vscode-inputValidation-errorBorder, red); border-radius: 4px; margin: 8px 12px; color: var(--vscode-errorForeground); }
    .spinner { display: inline-block; width: 16px; height: 16px; border: 2px solid var(--vscode-descriptionForeground); border-top-color: transparent; border-radius: 50%; animation: spin 0.8s linear infinite; }
    @keyframes spin { to { transform: rotate(360deg); } }
    ${getEnvironmentPickerCss()}
</style>

<div class="toolbar">
    <vscode-button id="execute-btn" appearance="primary">Execute</vscode-button>
    <vscode-button id="cancel-btn" appearance="secondary" style="display:none;" title="Cancel query (Escape)">Cancel</vscode-button>
    <vscode-button id="fetchxml-btn" appearance="secondary">FetchXML</vscode-button>
    <vscode-button id="explain-btn" appearance="secondary">EXPLAIN</vscode-button>
    <vscode-button id="export-btn" appearance="secondary">Export</vscode-button>
    <vscode-button id="history-btn" appearance="secondary">History</vscode-button>
    <vscode-button id="notebook-btn" appearance="secondary" title="Open in Notebook">Notebook</vscode-button>
    <vscode-button id="tds-btn" appearance="secondary" title="Toggle TDS Read Replica mode (direct SQL via port 5558)">TDS: Off</vscode-button>
    <vscode-button id="lang-btn" appearance="secondary" title="Toggle SQL / FetchXML language">SQL</vscode-button>
    <span class="toolbar-spacer"></span>
    ${getEnvironmentPickerHtml()}
    <vscode-button id="filter-btn" appearance="icon" title="Filter results (/)">
        <span class="codicon codicon-filter"></span>
    </vscode-button>
</div>

<div id="reconnect-banner" style="display:none; background: var(--vscode-inputValidation-infoBackground, #063b49); color: var(--vscode-foreground); padding: 6px 12px; text-align: center; font-size: 12px;">
    Connection restored. Data may be stale. <a href="#" id="reconnect-refresh" style="color: var(--vscode-textLink-foreground);">Refresh</a>
</div>

<div class="editor-container">
    <div class="editor-wrapper">
        <div id="sql-editor"></div>
    </div>
</div>

<div class="filter-bar" id="filter-bar">
    <span class="codicon codicon-filter"></span>
    <input type="text" id="filter-input" placeholder="Filter results..." />
    <span id="filter-count"></span>
</div>

<div class="results-wrapper" id="results-wrapper">
    <div class="empty-state" id="empty-state">Run a query to see results</div>
</div>

<div class="load-more-bar" id="load-more-bar" style="display:none">
    <vscode-button id="load-more-btn" appearance="secondary">Load More</vscode-button>
</div>

<div class="status-bar">
    <span id="status-text">Ready</span>
    <span id="row-count"></span>
    <span id="execution-time"></span>
    <span id="copy-hint" style="margin-left:auto;"></span>
</div>

<script nonce="${nonce}">
(function() {
    const vscode = acquireVsCodeApi();

    // ── Monaco Editor initialization ──
    // VS Code adds vscode-dark/vscode-light as body classes in webviews
    const bodyClasses = document.body.className;
    const monacoTheme = (bodyClasses.includes('vscode-light') || bodyClasses.includes('vscode-high-contrast-light')) ? 'vs' : 'vs-dark';
    const editor = monaco.editor.create(document.getElementById('sql-editor'), {
        language: 'sql',
        theme: monacoTheme,
        value: '',
        minimap: { enabled: false },
        lineNumbers: 'off',
        scrollBeyondLastLine: false,
        wordWrap: 'on',
        fontSize: 13,
        automaticLayout: true,
        suggestOnTriggerCharacters: true,
        glyphMargin: false,
        folding: false,
        lineDecorationsWidth: 0,
        lineNumbersMinChars: 0,
        renderLineHighlight: 'none',
        overviewRulerLanes: 0,
        hideCursorInOverviewRuler: true,
        overviewRulerBorder: false,
        scrollbar: { vertical: 'auto', horizontal: 'auto' },
    });

    let currentLanguage = 'sql';
    let manualOverride = false;

    const executeBtn = document.getElementById('execute-btn');
    const cancelBtn = document.getElementById('cancel-btn');
    const fetchxmlBtn = document.getElementById('fetchxml-btn');
    const explainBtn = document.getElementById('explain-btn');
    const exportBtn = document.getElementById('export-btn');
    const historyBtn = document.getElementById('history-btn');
    const notebookBtn = document.getElementById('notebook-btn');
    const tdsBtn = document.getElementById('tds-btn');
    const langBtn = document.getElementById('lang-btn');
    const filterBtn = document.getElementById('filter-btn');
    const filterBar = document.getElementById('filter-bar');
    const filterInput = document.getElementById('filter-input');
    const filterCount = document.getElementById('filter-count');
    const resultsWrapper = document.getElementById('results-wrapper');
    const emptyState = document.getElementById('empty-state');
    const loadMoreBar = document.getElementById('load-more-bar');
    const loadMoreBtn = document.getElementById('load-more-btn');
    const statusText = document.getElementById('status-text');
    const rowCountEl = document.getElementById('row-count');
    const executionTimeEl = document.getElementById('execution-time');

    let allRows = [];
    let columns = [];
    let pagingCookie = null;
    let currentPage = 1;
    let moreRecords = false;
    let sortColumn = -1;
    let sortAsc = true;
    let useTds = false;
    let isExecuting = false;

    // ── Record URL state ──
    var lastEntityName = null;
    var lastIsAggregate = false;
    var currentEnvironmentUrl = null;

    function getRecordId(row) {
        if (!lastEntityName || !row) return null;
        var idKey = lastEntityName + 'id';
        return row[idKey] || null;
    }

    function buildRecordUrl(entityName, recordId) {
        if (!currentEnvironmentUrl || !entityName || !recordId) return null;
        var baseUrl = currentEnvironmentUrl.replace(/\\/+$/, '');
        return baseUrl + '/main.aspx?pagetype=entityrecord&etn=' +
            encodeURIComponent(entityName) + '&id=' + encodeURIComponent(recordId);
    }

    // ── Selection state (anchor+focus rectangle) ──
    let anchor = null;   // {row, col} or null
    let focus = null;    // {row, col} or null
    let isDragging = false;
    let displayedRows = []; // tracks the rows array last passed to renderTable
    const copyHintEl = document.getElementById('copy-hint');

    // ── Selection utilities ──
    function getSelectionRect() {
        if (!anchor || !focus) return null;
        return {
            minRow: Math.min(anchor.row, focus.row),
            maxRow: Math.max(anchor.row, focus.row),
            minCol: Math.min(anchor.col, focus.col),
            maxCol: Math.max(anchor.col, focus.col),
        };
    }

    function isSingleCell() {
        if (!anchor || !focus) return false;
        return anchor.row === focus.row && anchor.col === focus.col;
    }

    function clearSelection() {
        anchor = null;
        focus = null;
        const tbody = resultsWrapper.querySelector('tbody');
        if (tbody) tbody.classList.remove('all-selected');
        resultsWrapper.querySelectorAll('td').forEach(td => {
            td.classList.remove('cell-selected', 'cell-selected-top', 'cell-selected-bottom', 'cell-selected-left', 'cell-selected-right');
        });
        if (copyHintEl) copyHintEl.textContent = '';
    }

    function isGuid(val) {
        return /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(val);
    }

    function isUrl(val) {
        return /^https?:\/\/.+/i.test(val);
    }

    function escapeAttr(str) {
        if (str === null || str === undefined) return '';
        return String(str).replace(/&/g, '&amp;').replace(/"/g, '&quot;')
            .replace(/'/g, '&#039;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
    }

    function getCellDisplayValue(row, colIdx) {
        const key = columns[colIdx].alias || columns[colIdx].logicalName;
        const rawVal = row ? row[key] : undefined;
        if (rawVal === null || rawVal === undefined) return '';
        if (typeof rawVal === 'object' && 'formatted' in rawVal) return String(rawVal.formatted || rawVal.value || '');
        if (typeof rawVal === 'object' && 'entityId' in rawVal) return String(rawVal.formatted || rawVal.value || '');
        return String(rawVal);
    }

    /** Returns { text, url?, entityType?, entityId? } for rendering and context menu */
    function getCellRichValue(row, colIdx) {
        const col = columns[colIdx];
        const key = col.alias || col.logicalName;
        const rawVal = row ? row[key] : undefined;
        if (rawVal === null || rawVal === undefined) return { text: '' };

        // Structured lookup value — clickable link to target record
        if (typeof rawVal === 'object' && rawVal !== null && 'entityId' in rawVal) {
            const text = String(rawVal.formatted || rawVal.value || '');
            if (currentEnvironmentUrl && rawVal.entityType && rawVal.entityId) {
                return {
                    text,
                    url: buildRecordUrl(rawVal.entityType, rawVal.entityId),
                    entityType: rawVal.entityType,
                    entityId: rawVal.entityId,
                };
            }
            return { text };
        }

        // Structured formatted value (optionsets, booleans)
        if (typeof rawVal === 'object' && rawVal !== null && 'formatted' in rawVal) {
            return { text: String(rawVal.formatted || rawVal.value || '') };
        }

        const stringValue = String(rawVal);

        // Primary key column — clickable link to own record
        if (lastEntityName && currentEnvironmentUrl && !lastIsAggregate) {
            const pkCol = lastEntityName + 'id';
            if (col.logicalName && col.logicalName.toLowerCase() === pkCol.toLowerCase() && isGuid(stringValue)) {
                return {
                    text: stringValue,
                    url: buildRecordUrl(lastEntityName, stringValue),
                    entityType: lastEntityName,
                    entityId: stringValue,
                };
            }
        }

        // Plain URL string — clickable external link
        if (isUrl(stringValue)) {
            return { text: stringValue, url: stringValue };
        }

        return { text: stringValue };
    }

    function sanitizeValue(val) {
        return val.replace(/\\t/g, ' ').replace(/\\r?\\n/g, ' ');
    }

    function updateCopyHint() {
        if (!copyHintEl) return;
        if (!anchor) { copyHintEl.textContent = ''; return; }
        if (isSingleCell()) {
            copyHintEl.textContent = 'Ctrl+C: copy value | Right-click: with header';
        } else {
            copyHintEl.textContent = 'Ctrl+C: copy with headers | Right-click: values only';
        }
    }

    function updateSelectionVisuals() {
        const rect = getSelectionRect();
        const tbody = resultsWrapper.querySelector('tbody');
        if (!tbody) return;

        // Fast path for Ctrl+A (full table)
        tbody.classList.remove('all-selected');
        if (rect && rect.minRow === 0 && rect.minCol === 0 &&
            rect.maxRow === displayedRows.length - 1 && rect.maxCol === columns.length - 1 &&
            displayedRows.length > 0) {
            tbody.classList.add('all-selected');
            updateCopyHint();
            return;
        }

        // Clear all selection classes
        tbody.querySelectorAll('td').forEach(td => {
            td.classList.remove('cell-selected', 'cell-selected-top', 'cell-selected-bottom', 'cell-selected-left', 'cell-selected-right');
        });

        if (!rect) { updateCopyHint(); return; }

        // Apply selection classes
        for (let r = rect.minRow; r <= rect.maxRow; r++) {
            for (let c = rect.minCol; c <= rect.maxCol; c++) {
                const td = tbody.querySelector('td[data-row="' + r + '"][data-col="' + c + '"]');
                if (!td) continue;
                td.classList.add('cell-selected');
                if (r === rect.minRow) td.classList.add('cell-selected-top');
                if (r === rect.maxRow) td.classList.add('cell-selected-bottom');
                if (c === rect.minCol) td.classList.add('cell-selected-left');
                if (c === rect.maxCol) td.classList.add('cell-selected-right');
            }
        }
        updateCopyHint();
    }

    // ── Language auto-detection ──
    function detectLang(content) {
        const trimmed = content.trimStart();
        return trimmed.startsWith('<') ? 'xml' : 'sql';
    }

    function updateLanguage(lang) {
        if (lang !== currentLanguage) {
            currentLanguage = lang;
            monaco.editor.setModelLanguage(editor.getModel(), lang);
            langBtn.textContent = lang === 'xml' ? 'FetchXML' : 'SQL';
        }
    }

    // Clear table selection when user clicks into the editor
    editor.onDidFocusEditorText(() => {
        if (anchor) clearSelection();
    });

    editor.onDidChangeModelContent(() => {
        if (!manualOverride) {
            const detected = detectLang(editor.getValue());
            updateLanguage(detected);
        }
    });

    langBtn.addEventListener('click', () => {
        manualOverride = true;
        updateLanguage(currentLanguage === 'sql' ? 'xml' : 'sql');
    });

    // ── Monaco clipboard bridge ──
    // VS Code webview sandbox blocks Monaco's native clipboard access.
    // Route copy/paste through postMessage to the host extension.
    editor.addAction({
        id: 'ppds.editorCopy',
        label: 'Copy',
        keybindings: [monaco.KeyMod.CtrlCmd | monaco.KeyCode.KeyC],
        run: (ed) => {
            const selection = ed.getModel().getValueInRange(ed.getSelection());
            if (selection) {
                vscode.postMessage({ command: 'copyToClipboard', text: selection });
            }
        },
    });

    editor.addAction({
        id: 'ppds.editorCut',
        label: 'Cut',
        keybindings: [monaco.KeyMod.CtrlCmd | monaco.KeyCode.KeyX],
        run: (ed) => {
            const sel = ed.getSelection();
            const text = ed.getModel().getValueInRange(sel);
            if (text) {
                vscode.postMessage({ command: 'copyToClipboard', text });
                ed.executeEdits('cut', [{ range: sel, text: '' }]);
            }
        },
    });

    editor.addAction({
        id: 'ppds.editorPaste',
        label: 'Paste',
        keybindings: [monaco.KeyMod.CtrlCmd | monaco.KeyCode.KeyV],
        run: (ed) => {
            vscode.postMessage({ command: 'requestClipboard' });
        },
    });

    // ── Monaco keybinding: Escape to cancel query ──
    editor.addCommand(monaco.KeyCode.Escape, function() {
        if (isExecuting) {
            vscode.postMessage({ command: 'cancelQuery' });
        }
    });

    // ── Cancel button handler ──
    cancelBtn.addEventListener('click', function() {
        vscode.postMessage({ command: 'cancelQuery' });
    });

    // ── Monaco keybinding: Ctrl+Enter to execute ──
    editor.addAction({
        id: 'ppds.executeQuery',
        label: 'Execute Query',
        keybindings: [monaco.KeyMod.CtrlCmd | monaco.KeyCode.Enter],
        run: () => {
            const sql = editor.getValue().trim();
            if (sql) vscode.postMessage({ command: 'executeQuery', sql, useTds, language: currentLanguage });
        },
    });

    // ── Completion provider bridge ──
    let completionRequestId = 0;
    const pendingCompletions = new Map();

    function requestCompletions(model, position, language) {
        // Compute the word range for replacement
        const wordInfo = model.getWordUntilPosition(position);
        const range = {
            startLineNumber: position.lineNumber,
            startColumn: wordInfo.startColumn,
            endLineNumber: position.lineNumber,
            endColumn: wordInfo.endColumn,
        };

        return new Promise((resolve) => {
            const id = ++completionRequestId;
            const cursorOffset = model.getOffsetAt(position);
            pendingCompletions.set(id, { resolve, range });
            vscode.postMessage({ command: 'requestCompletions', requestId: id, sql: model.getValue(), cursorOffset, language });
            setTimeout(() => {
                const p = pendingCompletions.get(id);
                if (p) {
                    pendingCompletions.delete(id);
                    p.resolve({ suggestions: [] });
                }
            }, 3000);
        });
    }

    function mapKind(kind) {
        switch (kind) {
            case 'entity': return monaco.languages.CompletionItemKind.Class;
            case 'attribute': return monaco.languages.CompletionItemKind.Field;
            case 'keyword': return monaco.languages.CompletionItemKind.Keyword;
            default: return monaco.languages.CompletionItemKind.Text;
        }
    }

    monaco.languages.registerCompletionItemProvider('sql', {
        triggerCharacters: [' ', ',', '.'],
        provideCompletionItems: async (model, position) => {
            return await requestCompletions(model, position, 'sql');
        },
    });

    monaco.languages.registerCompletionItemProvider('xml', {
        triggerCharacters: [' ', '<', '"'],
        provideCompletionItems: async (model, position) => {
            return await requestCompletions(model, position, 'fetchxml');
        },
    });

    // ── Button handlers ──
    executeBtn.addEventListener('click', () => {
        const sql = editor.getValue().trim();
        if (sql) vscode.postMessage({ command: 'executeQuery', sql, useTds, language: currentLanguage });
    });
    fetchxmlBtn.addEventListener('click', () => {
        const sql = editor.getValue().trim();
        if (sql) vscode.postMessage({ command: 'showFetchXml', sql });
    });
    explainBtn.addEventListener('click', () => {
        const sql = editor.getValue().trim();
        if (sql) vscode.postMessage({ command: 'explainQuery', sql });
    });
    exportBtn.addEventListener('click', () => {
        vscode.postMessage({ command: 'exportResults' });
    });
    historyBtn.addEventListener('click', () => {
        vscode.postMessage({ command: 'showHistory' });
    });
    notebookBtn.addEventListener('click', () => {
        const sql = editor.getValue().trim();
        if (sql) vscode.postMessage({ command: 'openInNotebook', sql });
    });
    tdsBtn.addEventListener('click', () => {
        useTds = !useTds;
        tdsBtn.textContent = useTds ? 'TDS: On' : 'TDS: Off';
        tdsBtn.setAttribute('appearance', useTds ? 'primary' : 'secondary');
    });
    ${getEnvironmentPickerJs()}
    loadMoreBtn.addEventListener('click', () => {
        if (pagingCookie) {
            vscode.postMessage({ command: 'loadMore', pagingCookie, page: currentPage + 1 });
        }
    });

    // ── Click + drag selection ──
    resultsWrapper.addEventListener('mousedown', (e) => {
        const th = e.target.closest('th[data-col]');
        if (th) {
            const col = parseInt(th.dataset.col);
            if (sortColumn === col) { sortAsc = !sortAsc; }
            else { sortColumn = col; sortAsc = true; }
            clearSelection();
            sortAndRender();
            return;
        }
        const td = e.target.closest('td[data-row]');
        if (!td) return;

        // Remove focus from Monaco so Ctrl+C goes to our handler, not Monaco's
        if (editor.hasTextFocus() && document.activeElement && document.activeElement.blur) {
            document.activeElement.blur();
        }

        const row = parseInt(td.dataset.row);
        const col = parseInt(td.dataset.col);

        if (e.shiftKey && anchor) {
            // Shift+click: extend selection from anchor
            focus = { row, col };
        } else {
            // Normal click: new single-cell selection + start drag tracking
            anchor = { row, col };
            focus = { row, col };
            isDragging = true;
            const tbody = resultsWrapper.querySelector('tbody');
            if (tbody) tbody.classList.add('selecting');

            const onMouseMove = (ev) => {
                const target = ev.target.closest ? ev.target.closest('td[data-row]') : null;
                if (target) {
                    focus = { row: parseInt(target.dataset.row), col: parseInt(target.dataset.col) };
                    updateSelectionVisuals();
                }
            };
            const onMouseUp = () => {
                isDragging = false;
                const tb = resultsWrapper.querySelector('tbody');
                if (tb) tb.classList.remove('selecting');
                document.removeEventListener('mousemove', onMouseMove);
                document.removeEventListener('mouseup', onMouseUp);
            };
            document.addEventListener('mousemove', onMouseMove);
            document.addEventListener('mouseup', onMouseUp);
        }
        updateSelectionVisuals();
    });

    // ── Link click handler ──
    // VS Code webviews block external navigations by default.
    // Intercept <a> clicks in the results table and open via the extension host.
    resultsWrapper.addEventListener('click', (e) => {
        const link = e.target.closest('a[href]');
        if (link) {
            e.preventDefault();
            e.stopPropagation();
            vscode.postMessage({ command: 'openRecordUrl', url: link.getAttribute('href') });
        }
    });

    // ── Keyboard shortcuts ──
    // Ctrl+C with table selection: use CAPTURE phase to intercept before Monaco.
    // When the user has selected cells in the results grid, Ctrl+C should copy
    // from the grid — not from Monaco's editor. Monaco would otherwise intercept
    // the event and copy its own (empty) selection.
    document.addEventListener('keydown', (e) => {
        // Only intercept Ctrl+C for table copy when Monaco does NOT have focus.
        // If Monaco has focus, let it handle copy/paste normally.
        if ((e.ctrlKey || e.metaKey) && e.key === 'c' && anchor && !editor.hasTextFocus()) {
            e.preventDefault();
            e.stopPropagation();
            copySelection(e.shiftKey);
            return;
        }
    }, true); // capture phase — fires before Monaco's handlers

    document.addEventListener('keydown', (e) => {
        // Filter toggle
        if (e.key === '/' && !editor.hasTextFocus() && document.activeElement !== filterInput) {
            e.preventDefault();
            toggleFilter();
            return;
        }

        // Escape: filter bar takes precedence, then selection
        if (e.key === 'Escape') {
            if (filterBar.classList.contains('visible')) {
                hideFilter();
            } else if (anchor) {
                clearSelection();
            }
            return;
        }

        // Ctrl+A: select all (when not in text inputs)
        if ((e.ctrlKey || e.metaKey) && e.key === 'a' &&
            !editor.hasTextFocus() &&
            document.activeElement !== filterInput) {
            if (displayedRows.length > 0 && columns.length > 0) {
                e.preventDefault();
                anchor = { row: 0, col: 0 };
                focus = { row: displayedRows.length - 1, col: columns.length - 1 };
                updateSelectionVisuals();
            }
            return;
        }

        // Ctrl+Shift+F: FetchXML preview
        if ((e.ctrlKey || e.metaKey) && e.shiftKey && e.key === 'F') {
            e.preventDefault();
            const sql = editor.getValue().trim();
            if (sql) vscode.postMessage({ command: 'showFetchXml', sql });
            return;
        }

        // Ctrl+E: Export
        if ((e.ctrlKey || e.metaKey) && !e.shiftKey && e.key === 'e') {
            e.preventDefault();
            vscode.postMessage({ command: 'exportResults' });
            return;
        }

        // Ctrl+Shift+H: History
        if ((e.ctrlKey || e.metaKey) && e.shiftKey && e.key === 'H') {
            e.preventDefault();
            vscode.postMessage({ command: 'showHistory' });
        }
    });

    // ── Filter ──
    filterBtn.addEventListener('click', toggleFilter);
    function toggleFilter() {
        if (filterBar.classList.contains('visible')) {
            hideFilter();
        } else {
            filterBar.classList.add('visible');
            filterInput.focus();
        }
    }
    function hideFilter() {
        filterBar.classList.remove('visible');
        filterInput.value = '';
        renderTable(allRows);
        filterCount.textContent = '';
    }
    filterInput.addEventListener('input', () => {
        const term = filterInput.value.toLowerCase();
        if (!term) { renderTable(allRows); filterCount.textContent = ''; return; }
        const filtered = allRows.filter(row =>
            columns.some(col => {
                const key = col.alias || col.logicalName;
                const val = row[key];
                if (val === null || val === undefined) return false;
                const str = typeof val === 'object' && 'formatted' in val ? String(val.formatted || val.value || '') : String(val);
                return str.toLowerCase().includes(term);
            })
        );
        renderTable(filtered);
        filterCount.textContent = 'Showing ' + filtered.length + ' of ' + allRows.length + ' rows';
    });

    // ── Message handling ──
    window.addEventListener('message', (event) => {
        const msg = event.data;
        switch (msg.command) {
            case 'queryResult':
                handleQueryResult(msg.data);
                break;
            case 'appendResults':
                handleAppendResults(msg.data);
                break;
            case 'queryError':
                showError(msg.error);
                break;
            case 'executionStarted':
                isExecuting = true;
                executeBtn.style.display = 'none';
                cancelBtn.style.display = '';
                resultsWrapper.innerHTML = '<div class="empty-state"><div class="spinner" style="width:24px;height:24px;margin:0 auto 12px;"></div><div>Executing query...</div></div>';
                statusText.innerHTML = '<span class="spinner"></span> Executing...';
                loadMoreBar.style.display = 'none';
                rowCountEl.textContent = '';
                executionTimeEl.textContent = '';
                break;
            case 'loadQuery':
                editor.setValue(msg.sql);
                manualOverride = false;
                break;
            case 'updateEnvironment':
                updateEnvironmentDisplay(msg.name);
                currentEnvironmentUrl = msg.url || null;
                break;
            case 'clipboardContent':
                if (msg.text && editor.hasTextFocus()) {
                    editor.trigger('clipboard', 'type', { text: msg.text });
                }
                break;
            case 'queryCancelled':
                isExecuting = false;
                cancelBtn.style.display = 'none';
                executeBtn.style.display = '';
                statusText.textContent = 'Query cancelled';
                resultsWrapper.innerHTML = '<div class="empty-state">Query cancelled</div>';
                break;
            case 'completionResult': {
                const pending = pendingCompletions.get(msg.requestId);
                if (pending) {
                    pendingCompletions.delete(msg.requestId);
                    const suggestions = (msg.items || []).map(item => ({
                        label: item.label,
                        insertText: item.insertText,
                        kind: mapKind(item.kind),
                        detail: item.detail || '',
                        sortText: String(item.sortOrder || 0).padStart(5, '0'),
                        range: pending.range,
                    }));
                    pending.resolve({ suggestions });
                }
                break;
            }
            case 'daemonReconnected':
                document.getElementById('reconnect-banner').style.display = '';
                break;
        }
    });

    function resetExecuteBtn() {
        isExecuting = false;
        cancelBtn.style.display = 'none';
        executeBtn.style.display = '';
    }

    function handleQueryResult(data) {
        resetExecuteBtn();
        columns = data.columns || [];
        allRows = data.records || [];
        pagingCookie = data.pagingCookie || null;
        currentPage = 1;
        moreRecords = data.moreRecords || false;
        sortColumn = -1;
        lastEntityName = data.entityName || null;
        lastIsAggregate = data.isAggregate || false;
        renderTable(allRows);
        updateStatus(data);
        loadMoreBar.style.display = moreRecords ? '' : 'none';
    }

    function handleAppendResults(data) {
        resetExecuteBtn();
        const newRecords = data.records || [];
        allRows = allRows.concat(newRecords);
        pagingCookie = data.pagingCookie || null;
        currentPage++;
        moreRecords = data.moreRecords || false;
        renderTable(allRows);
        updateStatus(data);
        loadMoreBar.style.display = moreRecords ? '' : 'none';
    }

    function showError(error) {
        resetExecuteBtn();
        resultsWrapper.innerHTML = '<div class="error-state">' + escapeHtml(error) + '</div>';
        statusText.textContent = 'Error';
        loadMoreBar.style.display = 'none';
    }

    function updateStatus(data) {
        statusText.textContent = 'Ready';
        rowCountEl.textContent = allRows.length + ' row' + (allRows.length !== 1 ? 's' : '') + (moreRecords ? ' (more available)' : '');
        executionTimeEl.textContent = data.executionTimeMs ? 'in ' + data.executionTimeMs + 'ms' : '';
    }

    // ── Rendering ──
    function renderTable(rows) {
        displayedRows = rows;
        clearSelection();

        if (rows.length === 0 && allRows.length === 0) {
            resultsWrapper.innerHTML = '<div class="empty-state">Run a query to see results</div>';
            return;
        }
        if (rows.length === 0) {
            resultsWrapper.innerHTML = '<div class="empty-state">No matching rows</div>';
            return;
        }

        let html = '<table class="results-table"><thead><tr>';
        columns.forEach((col, idx) => {
            const label = col.alias || col.displayName || col.logicalName;
            const indicator = sortColumn === idx ? (sortAsc ? ' \\u25B2' : ' \\u25BC') : '';
            html += '<th data-col="' + idx + '">' + escapeHtml(label) + '<span class="sort-indicator">' + indicator + '</span></th>';
        });
        html += '</tr></thead><tbody>';

        rows.forEach((row, rowIdx) => {
            html += '<tr>';
            columns.forEach((col, colIdx) => {
                const rich = getCellRichValue(row, colIdx);
                if (rich.url) {
                    html += '<td data-row="' + rowIdx + '" data-col="' + colIdx + '"><a href="' + escapeAttr(rich.url) + '" target="_blank">' + escapeHtml(rich.text) + '</a></td>';
                } else {
                    html += '<td data-row="' + rowIdx + '" data-col="' + colIdx + '">' + escapeHtml(rich.text) + '</td>';
                }
            });
            html += '</tr>';
        });
        html += '</tbody></table>';
        resultsWrapper.innerHTML = html;
    }

    function sortAndRender() {
        if (sortColumn < 0) return;
        const key = columns[sortColumn].alias || columns[sortColumn].logicalName;
        const sorted = [...allRows].sort((a, b) => {
            let va = a[key], vb = b[key];
            if (va && typeof va === 'object' && 'formatted' in va) va = va.formatted || va.value;
            if (vb && typeof vb === 'object' && 'formatted' in vb) vb = vb.formatted || vb.value;
            if (va === null || va === undefined) return 1;
            if (vb === null || vb === undefined) return -1;
            if (typeof va === 'number' && typeof vb === 'number') return sortAsc ? va - vb : vb - va;
            return sortAsc ? String(va).localeCompare(String(vb)) : String(vb).localeCompare(String(va));
        });
        renderTable(sorted);
    }

    function copySelection(invertHeaders) {
        if (!anchor) return;
        const rect = getSelectionRect();
        if (!rect) return;

        const single = isSingleCell();
        const withHeaders = single ? invertHeaders : !invertHeaders;

        let text = '';

        if (withHeaders) {
            const headers = [];
            for (let c = rect.minCol; c <= rect.maxCol; c++) {
                headers.push(columns[c].alias || columns[c].logicalName);
            }
            text += headers.join('\\t') + '\\n';
        }

        for (let r = rect.minRow; r <= rect.maxRow; r++) {
            const vals = [];
            for (let c = rect.minCol; c <= rect.maxCol; c++) {
                vals.push(sanitizeValue(getCellDisplayValue(displayedRows[r], c)));
            }
            text += vals.join('\\t') + '\\n';
        }

        vscode.postMessage({ command: 'copyToClipboard', text: text.trim() });
        showCopyFeedback(rect, single, withHeaders);
    }

    function showCopyFeedback(rect, single, withHeaders) {
        if (!copyHintEl) return;
        if (single) {
            const val = getCellDisplayValue(displayedRows[rect.minRow], rect.minCol);
            const truncated = val.length > 40 ? val.substring(0, 40) + '...' : val;
            copyHintEl.textContent = 'Copied: ' + truncated;
        } else {
            const rowCount = rect.maxRow - rect.minRow + 1;
            const colCount = rect.maxCol - rect.minCol + 1;
            copyHintEl.textContent = 'Copied ' + rowCount + ' rows x ' + colCount + ' cols ' + (withHeaders ? 'with headers' : 'without headers');
        }
        setTimeout(() => { updateCopyHint(); }, 2000);
    }

    // ── Right-click context menu ──
    let contextMenu = null;
    document.addEventListener('contextmenu', (e) => {
        const td = e.target.closest('td[data-row]');
        if (!td) return;
        e.preventDefault();
        removeContextMenu();

        const clickRow = parseInt(td.dataset.row);
        const clickCol = parseInt(td.dataset.col);

        // If right-clicked cell is outside current selection, move selection there
        const rect = getSelectionRect();
        if (!rect || clickRow < rect.minRow || clickRow > rect.maxRow ||
            clickCol < rect.minCol || clickCol > rect.maxCol) {
            anchor = { row: clickRow, col: clickCol };
            focus = { row: clickRow, col: clickCol };
            updateSelectionVisuals();
        }

        const single = isSingleCell();
        const inverseLabel = single ? 'Copy (with header)' : 'Copy (no headers)';

        contextMenu = document.createElement('div');
        contextMenu.className = 'context-menu';

        // Determine the best record link for the clicked cell:
        // 1. If the cell itself is a lookup/primary key, use that (cell-aware)
        // 2. Otherwise fall back to the row's primary key
        var cellRich = getCellRichValue(displayedRows[clickRow], clickCol);
        var contextRecordUrl = null;
        var contextRecordLabel = 'Open Record in Dynamics';
        if (cellRich.url && cellRich.entityType) {
            contextRecordUrl = cellRich.url;
            contextRecordLabel = 'Open ' + cellRich.entityType + ' Record';
        } else {
            var rowPkId = getRecordId(displayedRows[clickRow]);
            if (lastEntityName && rowPkId && !lastIsAggregate) {
                contextRecordUrl = buildRecordUrl(lastEntityName, rowPkId);
            }
        }

        const items = [
            { label: 'Copy', shortcut: 'Ctrl+C', action: 'copy' },
            { label: inverseLabel, shortcut: '', action: 'copyInverse' },
            { label: 'separator', shortcut: '', action: 'separator' },
            { label: 'Copy Cell Value', shortcut: '', action: 'cell' },
            { label: 'Copy Row', shortcut: '', action: 'row' },
            { label: 'Copy All Results', shortcut: '', action: 'all' },
        ];

        if (contextRecordUrl) {
            items.push({ label: 'separator', shortcut: '', action: 'separator' });
            items.push({ label: contextRecordLabel, shortcut: '', action: 'openRecord' });
            items.push({ label: 'Copy Record URL', shortcut: '', action: 'copyRecordUrl' });
        }

        let html = '';
        for (const item of items) {
            if (item.action === 'separator') {
                html += '<div style="border-top: 1px solid var(--vscode-menu-separatorBackground, var(--vscode-panel-border)); margin: 4px 0;"></div>';
            } else {
                html += '<div class="context-menu-item" data-action="' + item.action + '" style="display:flex;align-items:center;">';
                html += '<span>' + item.label + '</span>';
                if (item.shortcut) html += '<span style="margin-left:auto;padding-left:24px;opacity:0.6;font-size:11px;">' + item.shortcut + '</span>';
                html += '</div>';
            }
        }
        contextMenu.innerHTML = html;
        contextMenu.style.left = e.clientX + 'px';
        contextMenu.style.top = e.clientY + 'px';
        document.body.appendChild(contextMenu);

        contextMenu.addEventListener('click', (ev) => {
            const actionEl = ev.target.closest('[data-action]');
            if (!actionEl) return;
            const action = actionEl.dataset.action;

            if (action === 'copy') {
                copySelection(false);
            } else if (action === 'copyInverse') {
                copySelection(true);
            } else if (action === 'cell') {
                const val = getCellDisplayValue(displayedRows[clickRow], clickCol);
                vscode.postMessage({ command: 'copyToClipboard', text: sanitizeValue(val) });
                if (copyHintEl) {
                    copyHintEl.textContent = 'Copied: ' + (val.length > 40 ? val.substring(0, 40) + '...' : val);
                    setTimeout(() => { updateCopyHint(); }, 2000);
                }
            } else if (action === 'row') {
                const vals = [];
                for (let c = 0; c < columns.length; c++) {
                    vals.push(sanitizeValue(getCellDisplayValue(displayedRows[clickRow], c)));
                }
                vscode.postMessage({ command: 'copyToClipboard', text: vals.join('\\t') });
                if (copyHintEl) {
                    copyHintEl.textContent = 'Copied row';
                    setTimeout(() => { updateCopyHint(); }, 2000);
                }
            } else if (action === 'all') {
                const headers = columns.map(c => c.alias || c.logicalName);
                let text = headers.join('\\t') + '\\n';
                displayedRows.forEach(row => {
                    const vals = [];
                    for (let c = 0; c < columns.length; c++) {
                        vals.push(sanitizeValue(getCellDisplayValue(row, c)));
                    }
                    text += vals.join('\\t') + '\\n';
                });
                vscode.postMessage({ command: 'copyToClipboard', text: text.trim() });
                if (copyHintEl) {
                    copyHintEl.textContent = 'Copied all ' + displayedRows.length + ' rows';
                    setTimeout(() => { updateCopyHint(); }, 2000);
                }
            } else if (action === 'openRecord') {
                if (contextRecordUrl) vscode.postMessage({ command: 'openRecordUrl', url: contextRecordUrl });
            } else if (action === 'copyRecordUrl') {
                if (contextRecordUrl) {
                    vscode.postMessage({ command: 'copyToClipboard', text: contextRecordUrl });
                    if (copyHintEl) {
                        copyHintEl.textContent = 'Copied record URL';
                        setTimeout(() => { updateCopyHint(); }, 2000);
                    }
                }
            }
            removeContextMenu();
        });
    });
    document.addEventListener('click', (e) => {
        if (contextMenu && !contextMenu.contains(e.target)) removeContextMenu();
    });
    function removeContextMenu() {
        if (contextMenu) { contextMenu.remove(); contextMenu = null; }
    }

    function escapeHtml(str) {
        if (str === null || str === undefined) return '';
        const s = String(str);
        return s.replace(/&/g, '&amp;').replace(/</g, '&lt;')
            .replace(/>/g, '&gt;').replace(/"/g, '&quot;')
            .replace(/'/g, '&#039;');
    }

    document.getElementById('reconnect-refresh').addEventListener('click', function(e) {
        e.preventDefault();
        document.getElementById('reconnect-banner').style.display = 'none';
        vscode.postMessage({ command: 'refresh' });
    });

    // Signal ready
    vscode.postMessage({ command: 'ready' });
})();
</script>
</body>
</html>`;
    }
}
