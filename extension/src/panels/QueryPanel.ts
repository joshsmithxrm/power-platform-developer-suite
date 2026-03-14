import * as vscode from 'vscode';
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

    private allRecords: Record<string, unknown>[] = [];
    private lastResult: QueryResultResponse | undefined;
    private lastSql: string | undefined;
    private lastUseTds = false;
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
                localResourceRoots: [vscode.Uri.joinPath(extensionUri, 'node_modules')],
            }
        );

        this.panel.webview.html = this.getHtmlContent(this.panel.webview);

        this.disposables.push(
            this.panel.webview.onDidReceiveMessage(
                async (message: { command: string; [key: string]: unknown }) => {
                    switch (message.command) {
                        case 'executeQuery':
                            await this.executeQuery(message.sql as string, false, message.useTds as boolean | undefined);
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
                        case 'ready':
                            if (initialSql) {
                                this.postMessage({ command: 'loadQuery', sql: initialSql });
                            }
                            // Initialize environment from active profile
                            this.initEnvironment();
                            break;
                        case 'requestEnvironmentList': {
                            const env = await showEnvironmentPicker(this.daemon, this.environmentUrl);
                            if (env) {
                                this.environmentUrl = env.url;
                                this.environmentDisplayName = env.displayName;
                                this.postMessage({ command: 'updateEnvironment', name: env.displayName });
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
    }

    override dispose(): void {
        // Guard: super.dispose() checks _disposed, but we must also guard
        // the instances splice to prevent re-entrant onDidDispose removing
        // the wrong panel (splice(-1, 1) removes last element).
        const idx = QueryPanel.instances.indexOf(this);
        if (idx >= 0) QueryPanel.instances.splice(idx, 1);
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
        this.postMessage({ command: 'updateEnvironment', name: this.environmentDisplayName ?? 'No environment' });
        this.updateTitle();
    }

    private updateTitle(): void {
        if (!this.panel) return;
        const parts = [`Data Explorer #${this.panelId}`];
        if (this.profileName) parts.push(this.profileName);
        if (this.environmentDisplayName) parts.push(this.environmentDisplayName);
        this.panel.title = parts.join(' — ');
    }

    private async executeQuery(sql: string, isRetry = false, useTds?: boolean): Promise<void> {
        try {
            this.postMessage({ command: 'executionStarted' });
            const defaultTop = vscode.workspace.getConfiguration('ppds').get<number>('queryDefaultTop', 100);
            const tds = useTds ?? false;
            const result = await this.daemon.querySql({ sql, top: defaultTop, useTds: tds, environmentUrl: this.environmentUrl });
            this.lastSql = sql;
            this.lastUseTds = tds;
            this.lastResult = result;
            this.allRecords = [...result.records];
            this.postMessage({
                command: 'queryResult',
                data: result,
            });
        } catch (error) {
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
            const result = await this.daemon.querySql({
                sql: this.lastSql,
                page,
                pagingCookie,
                useTds: this.lastUseTds,
                environmentUrl: this.environmentUrl,
            });

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
        const nonce = getNonce();

        return `<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="UTF-8">
    <!-- 'unsafe-inline' is required by @vscode/webview-ui-toolkit for dynamic styles -->
    <meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src ${webview.cspSource} 'unsafe-inline'; script-src 'nonce-${nonce}'; font-src ${webview.cspSource};">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <script type="module" nonce="${nonce}" src="${toolkitUri}"></script>
</head>
<body>
<style>
    body { margin: 0; padding: 0; display: flex; flex-direction: column; height: 100vh; font-family: var(--vscode-font-family); color: var(--vscode-foreground); background: var(--vscode-editor-background); }
    .toolbar { display: flex; gap: 8px; padding: 8px 12px; border-bottom: 1px solid var(--vscode-panel-border); flex-shrink: 0; align-items: center; }
    .toolbar-spacer { flex: 1; }
    .editor-container { padding: 8px 12px; flex-shrink: 0; }
    #sql-editor { width: 100%; min-height: 120px; max-height: 300px; resize: vertical; font-family: var(--vscode-editor-font-family); font-size: var(--vscode-editor-font-size); background: var(--vscode-input-background); color: var(--vscode-input-foreground); border: 1px solid var(--vscode-input-border, var(--vscode-panel-border)); padding: 8px; border-radius: 2px; outline: none; tab-size: 4; }
    #sql-editor:focus { border-color: var(--vscode-focusBorder); }
    .results-wrapper { flex: 1; overflow: auto; position: relative; }
    .results-table { width: max-content; min-width: 100%; border-collapse: collapse; }
    .results-table thead { position: sticky; top: 0; z-index: 1; }
    .results-table th { padding: 6px 12px; text-align: left; font-weight: 600; background: var(--vscode-button-background); color: var(--vscode-button-foreground); border-bottom: 2px solid var(--vscode-panel-border); border-right: 1px solid rgba(255,255,255,0.1); white-space: nowrap; cursor: pointer; user-select: none; }
    .results-table th:last-child { border-right: none; }
    .results-table th .sort-indicator { margin-left: 4px; opacity: 0.7; }
    .results-table td { padding: 6px 12px; white-space: nowrap; border-bottom: 1px solid var(--vscode-panel-border); }
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
    <vscode-button id="fetchxml-btn" appearance="secondary">FetchXML</vscode-button>
    <vscode-button id="explain-btn" appearance="secondary">EXPLAIN</vscode-button>
    <vscode-button id="export-btn" appearance="secondary">Export</vscode-button>
    <vscode-button id="history-btn" appearance="secondary">History</vscode-button>
    <vscode-button id="notebook-btn" appearance="secondary" title="Open in Notebook">Notebook</vscode-button>
    <vscode-button id="tds-btn" appearance="secondary" title="Toggle TDS Read Replica mode (direct SQL via port 5558)">TDS: Off</vscode-button>
    <span class="toolbar-spacer"></span>
    ${getEnvironmentPickerHtml()}
    <vscode-button id="filter-btn" appearance="icon" title="Filter results (/)">
        <span class="codicon codicon-filter"></span>
    </vscode-button>
</div>

<div class="editor-container">
    <textarea id="sql-editor" placeholder="SELECT TOP 10 * FROM account" spellcheck="false"></textarea>
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
    <span class="toolbar-spacer"></span>
    <span id="copy-hint"></span>
</div>

<script nonce="${nonce}">
(function() {
    const vscode = acquireVsCodeApi();
    const sqlEditor = document.getElementById('sql-editor');
    const executeBtn = document.getElementById('execute-btn');
    const fetchxmlBtn = document.getElementById('fetchxml-btn');
    const explainBtn = document.getElementById('explain-btn');
    const exportBtn = document.getElementById('export-btn');
    const historyBtn = document.getElementById('history-btn');
    const notebookBtn = document.getElementById('notebook-btn');
    const tdsBtn = document.getElementById('tds-btn');
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

    function getCellDisplayValue(row, colIdx) {
        const key = columns[colIdx].alias || columns[colIdx].logicalName;
        const rawVal = row ? row[key] : undefined;
        if (rawVal === null || rawVal === undefined) return '';
        if (typeof rawVal === 'object' && 'formatted' in rawVal) return String(rawVal.formatted || rawVal.value || '');
        if (typeof rawVal === 'object' && 'entityId' in rawVal) return String(rawVal.formatted || rawVal.value || '');
        return String(rawVal);
    }

    function sanitizeValue(val) {
        return val.replace(/\\t/g, ' ').replace(/\\r?\\n/g, ' ');
    }

    function updateCopyHint() {
        if (!copyHintEl) return;
        if (!anchor) { copyHintEl.textContent = ''; return; }
        if (isSingleCell()) {
            copyHintEl.textContent = 'Ctrl+C: copy value | Ctrl+Shift+C: with header';
        } else {
            copyHintEl.textContent = 'Ctrl+C: copy with headers | Ctrl+Shift+C: values only';
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

    // ── Button handlers ──
    executeBtn.addEventListener('click', () => {
        const sql = sqlEditor.value.trim();
        if (sql) vscode.postMessage({ command: 'executeQuery', sql, useTds });
    });
    fetchxmlBtn.addEventListener('click', () => {
        const sql = sqlEditor.value.trim();
        if (sql) vscode.postMessage({ command: 'showFetchXml', sql });
    });
    explainBtn.addEventListener('click', () => {
        const sql = sqlEditor.value.trim();
        if (sql) vscode.postMessage({ command: 'explainQuery', sql });
    });
    exportBtn.addEventListener('click', () => {
        vscode.postMessage({ command: 'exportResults' });
    });
    historyBtn.addEventListener('click', () => {
        vscode.postMessage({ command: 'showHistory' });
    });
    notebookBtn.addEventListener('click', () => {
        const sql = sqlEditor.value.trim();
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

    // ── Keyboard shortcuts ──
    sqlEditor.addEventListener('keydown', (e) => {
        if ((e.ctrlKey || e.metaKey) && e.key === 'Enter') {
            e.preventDefault();
            const sql = sqlEditor.value.trim();
            if (sql) vscode.postMessage({ command: 'executeQuery', sql, useTds });
        }
    });

    document.addEventListener('keydown', (e) => {
        // Filter toggle
        if (e.key === '/' && document.activeElement !== sqlEditor && document.activeElement !== filterInput) {
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
            document.activeElement !== sqlEditor &&
            document.activeElement !== filterInput) {
            if (displayedRows.length > 0 && columns.length > 0) {
                e.preventDefault();
                anchor = { row: 0, col: 0 };
                focus = { row: displayedRows.length - 1, col: columns.length - 1 };
                updateSelectionVisuals();
            }
            return;
        }

        // Ctrl+C / Ctrl+Shift+C: copy selection
        if ((e.ctrlKey || e.metaKey) && e.key === 'c' &&
            document.activeElement !== sqlEditor &&
            document.activeElement !== filterInput) {
            if (anchor) {
                e.preventDefault();
                copySelection(e.shiftKey);
            }
            return;
        }

        // Ctrl+Shift+F: FetchXML preview
        if ((e.ctrlKey || e.metaKey) && e.shiftKey && e.key === 'F') {
            e.preventDefault();
            const sql = sqlEditor.value.trim();
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
                executeBtn.setAttribute('disabled', '');
                executeBtn.textContent = 'Executing...';
                resultsWrapper.innerHTML = '<div class="empty-state"><div class="spinner" style="width:24px;height:24px;margin:0 auto 12px;"></div><div>Executing query...</div></div>';
                statusText.innerHTML = '<span class="spinner"></span> Executing...';
                loadMoreBar.style.display = 'none';
                rowCountEl.textContent = '';
                executionTimeEl.textContent = '';
                break;
            case 'loadQuery':
                sqlEditor.value = msg.sql;
                break;
            case 'updateEnvironment':
                updateEnvironmentDisplay(msg.name);
                break;
        }
    });

    function resetExecuteBtn() {
        executeBtn.removeAttribute('disabled');
        executeBtn.textContent = 'Execute';
    }

    function handleQueryResult(data) {
        resetExecuteBtn();
        columns = data.columns || [];
        allRows = data.records || [];
        pagingCookie = data.pagingCookie || null;
        currentPage = 1;
        moreRecords = data.moreRecords || false;
        sortColumn = -1;
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
                html += '<td data-row="' + rowIdx + '" data-col="' + colIdx + '">' + escapeHtml(getCellDisplayValue(row, colIdx)) + '</td>';
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
        // Smart default: single cell = no headers; multi-cell = headers. invertHeaders flips it.
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

        const items = [
            { label: 'Copy', shortcut: 'Ctrl+C', action: 'copy' },
            { label: inverseLabel, shortcut: 'Ctrl+Shift+C', action: 'copyInverse' },
            { label: 'separator', shortcut: '', action: 'separator' },
            { label: 'Copy Cell Value', shortcut: '', action: 'cell' },
            { label: 'Copy Row', shortcut: '', action: 'row' },
            { label: 'Copy All Results', shortcut: '', action: 'all' },
        ];

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

    // Signal ready
    vscode.postMessage({ command: 'ready' });
})();
</script>
</body>
</html>`;
    }
}
