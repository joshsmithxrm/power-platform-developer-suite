import * as vscode from 'vscode';
import { WebviewPanelBase } from './WebviewPanelBase.js';
import { getNonce } from './getWebviewContent.js';
import type { DaemonClient } from '../daemonClient.js';
import type { QueryResultResponse } from '../types.js';
import { showQueryHistory } from '../commands/queryHistoryCommand.js';

export class QueryPanel extends WebviewPanelBase {
    private static instances: QueryPanel[] = [];
    private static nextId = 1;
    private readonly panelId: number;

    static show(extensionUri: vscode.Uri, daemon: DaemonClient, initialSql?: string): QueryPanel {
        // Always create a new instance (no singleton reuse)
        const panel = new QueryPanel(extensionUri, daemon, initialSql);
        return panel;
    }

    private allRecords: Record<string, unknown>[] = [];
    private lastResult: QueryResultResponse | undefined;
    private lastSql: string | undefined;

    private constructor(
        private readonly extensionUri: vscode.Uri,
        private readonly daemon: DaemonClient,
        initialSql?: string,
    ) {
        super();

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

        this.panel.webview.onDidReceiveMessage(
            async (message: { command: string; [key: string]: unknown }) => {
                switch (message.command) {
                    case 'executeQuery':
                        await this.executeQuery(message.sql as string, message.useTds as boolean | undefined);
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
                        break;
                }
            },
            undefined,
            this.disposables,
        );

        this.panel.onDidDispose(() => {
            const idx = QueryPanel.instances.indexOf(this);
            if (idx >= 0) QueryPanel.instances.splice(idx, 1);
            this.dispose();
        }, null, this.disposables);
    }

    private async executeQuery(sql: string, useTds?: boolean): Promise<void> {
        try {
            this.postMessage({ command: 'executionStarted' });
            const defaultTop = vscode.workspace.getConfiguration('ppds').get<number>('queryDefaultTop', 100);
            const result = await this.daemon.querySql({ sql, useTds, top: defaultTop });
            this.lastSql = sql;
            this.lastResult = result;
            this.allRecords = [...result.records];
            this.postMessage({
                command: 'queryResult',
                data: result,
            });
        } catch (error) {
            const msg = error instanceof Error ? error.message : String(error);

            // Check for auth errors and offer re-authentication
            if (this.isAuthError(error)) {
                const action = await vscode.window.showErrorMessage(
                    'Session expired. Re-authenticate?',
                    'Re-authenticate', 'Cancel'
                );
                if (action === 'Re-authenticate') {
                    try {
                        await this.daemon.profilesInvalidate('');  // Invalidate active profile
                        // Retry the query
                        await this.executeQuery(sql, useTds);
                        return;
                    } catch {
                        // Fall through to show error
                    }
                }
            }

            this.postMessage({ command: 'queryError', error: msg });
        }
    }

    private isAuthError(error: unknown): boolean {
        const msg = error instanceof Error ? error.message : String(error);
        return msg.toLowerCase().includes('auth') ||
               msg.toLowerCase().includes('token') ||
               msg.toLowerCase().includes('unauthorized') ||
               msg.toLowerCase().includes('401');
    }

    private async showFetchXml(sql: string): Promise<void> {
        try {
            const result = await this.daemon.querySql({ sql, showFetchXml: true });
            if (result.executedFetchXml) {
                const doc = await vscode.workspace.openTextDocument({
                    content: result.executedFetchXml,
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
            const result = await this.daemon.queryExplain(sql);
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
            });
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
    .results-table td.selected { background: var(--vscode-editor-selectionBackground); }
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
</style>

<div class="toolbar">
    <vscode-button id="execute-btn" appearance="primary">Execute</vscode-button>
    <vscode-button id="fetchxml-btn" appearance="secondary">FetchXML</vscode-button>
    <vscode-button id="explain-btn" appearance="secondary">EXPLAIN</vscode-button>
    <vscode-button id="export-btn" appearance="secondary">Export</vscode-button>
    <vscode-button id="history-btn" appearance="secondary">History</vscode-button>
    <vscode-button id="notebook-btn" appearance="secondary" title="Open in Notebook">Notebook</vscode-button>
    <vscode-button id="tds-toggle-btn" appearance="secondary" title="Toggle TDS endpoint">TDS Off</vscode-button>
    <span class="toolbar-spacer"></span>
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
    let selectedCells = new Set();
    let sortColumn = -1;
    let sortAsc = true;
    let useTds = false;
    const tdsBtn = document.getElementById('tds-toggle-btn');
    tdsBtn.addEventListener('click', () => {
        useTds = !useTds;
        tdsBtn.textContent = useTds ? 'TDS On' : 'TDS Off';
        tdsBtn.setAttribute('appearance', useTds ? 'primary' : 'secondary');
    });

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
    loadMoreBtn.addEventListener('click', () => {
        if (pagingCookie) {
            vscode.postMessage({ command: 'loadMore', pagingCookie, page: currentPage + 1 });
        }
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
        if (e.key === '/' && document.activeElement !== sqlEditor && document.activeElement !== filterInput) {
            e.preventDefault();
            toggleFilter();
        }
        if (e.key === 'Escape' && filterBar.classList.contains('visible')) {
            hideFilter();
        }
        if ((e.ctrlKey || e.metaKey) && e.key === 'c' && document.activeElement !== sqlEditor) {
            e.preventDefault();
            copySelectedCells(e.shiftKey);
        }
        // Ctrl+Shift+F → FetchXML preview
        if ((e.ctrlKey || e.metaKey) && e.shiftKey && e.key === 'F') {
            e.preventDefault();
            const sql = sqlEditor.value.trim();
            if (sql) vscode.postMessage({ command: 'showFetchXml', sql });
        }
        // Ctrl+E → Export
        if ((e.ctrlKey || e.metaKey) && !e.shiftKey && e.key === 'e') {
            e.preventDefault();
            vscode.postMessage({ command: 'exportResults' });
        }
        // Ctrl+Shift+H → History
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
                statusText.innerHTML = '<span class="spinner"></span> Executing...';
                break;
            case 'loadQuery':
                sqlEditor.value = msg.sql;
                break;
        }
    });

    function handleQueryResult(data) {
        columns = data.columns || [];
        allRows = data.records || [];
        pagingCookie = data.pagingCookie || null;
        currentPage = 1;
        moreRecords = data.moreRecords || false;
        selectedCells.clear();
        sortColumn = -1;
        renderTable(allRows);
        updateStatus(data);
        loadMoreBar.style.display = moreRecords ? '' : 'none';
    }

    function handleAppendResults(data) {
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
                const key = col.alias || col.logicalName;
                const rawVal = row[key];
                const cellId = rowIdx + ':' + colIdx;
                const isSelected = selectedCells.has(cellId) ? ' selected' : '';
                let display = '';
                if (rawVal !== null && rawVal !== undefined) {
                    if (typeof rawVal === 'object' && 'formatted' in rawVal) {
                        display = String(rawVal.formatted || rawVal.value || '');
                    } else if (typeof rawVal === 'object' && 'entityId' in rawVal) {
                        display = String(rawVal.formatted || rawVal.value || '');
                    } else {
                        display = String(rawVal);
                    }
                }
                html += '<td class="' + isSelected + '" data-row="' + rowIdx + '" data-col="' + colIdx + '">' + escapeHtml(display) + '</td>';
            });
            html += '</tr>';
        });
        html += '</tbody></table>';
        resultsWrapper.innerHTML = html;

        // Attach sort handlers
        resultsWrapper.querySelectorAll('th').forEach(th => {
            th.addEventListener('click', () => {
                const col = parseInt(th.dataset.col);
                if (sortColumn === col) { sortAsc = !sortAsc; }
                else { sortColumn = col; sortAsc = true; }
                sortAndRender();
            });
        });

        // Attach cell click for selection
        resultsWrapper.querySelectorAll('td').forEach(td => {
            td.addEventListener('click', (e) => {
                const cellId = td.dataset.row + ':' + td.dataset.col;
                if (!e.ctrlKey && !e.metaKey) selectedCells.clear();
                if (selectedCells.has(cellId)) selectedCells.delete(cellId);
                else selectedCells.add(cellId);
                updateCellSelection();
            });
        });
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

    function updateCellSelection() {
        resultsWrapper.querySelectorAll('td').forEach(td => {
            const cellId = td.dataset.row + ':' + td.dataset.col;
            td.classList.toggle('selected', selectedCells.has(cellId));
        });
    }

    function copySelectedCells(withHeaders) {
        if (selectedCells.size === 0) return;
        const cells = Array.from(selectedCells).map(id => {
            const [r, c] = id.split(':').map(Number);
            return { row: r, col: c };
        });
        const minRow = Math.min(...cells.map(c => c.row));
        const maxRow = Math.max(...cells.map(c => c.row));
        const minCol = Math.min(...cells.map(c => c.col));
        const maxCol = Math.max(...cells.map(c => c.col));

        const includeHeaders = selectedCells.size === 1 ? withHeaders : !withHeaders;
        let text = '';
        if (includeHeaders) {
            const headers = [];
            for (let c = minCol; c <= maxCol; c++) {
                headers.push(columns[c].alias || columns[c].logicalName);
            }
            text += headers.join('\\t') + '\\n';
        }
        for (let r = minRow; r <= maxRow; r++) {
            const vals = [];
            for (let c = minCol; c <= maxCol; c++) {
                const key = columns[c].alias || columns[c].logicalName;
                const val = allRows[r] ? allRows[r][key] : undefined;
                let display = '';
                if (val !== null && val !== undefined) {
                    if (typeof val === 'object' && 'formatted' in val) display = String(val.formatted || val.value || '');
                    else display = String(val);
                }
                vals.push(display);
            }
            text += vals.join('\\t') + '\\n';
        }
        vscode.postMessage({ command: 'copyToClipboard', text: text.trim() });
    }

    // ── Right-click context menu ──
    let contextMenu = null;
    document.addEventListener('contextmenu', (e) => {
        const td = e.target.closest('td.selected, td[data-row]');
        if (!td || !td.dataset.row) return;
        e.preventDefault();
        removeContextMenu();
        // If right-clicked cell is not selected, select only it
        const cellId = td.dataset.row + ':' + td.dataset.col;
        if (!selectedCells.has(cellId)) {
            selectedCells.clear();
            selectedCells.add(cellId);
            updateCellSelection();
        }
        contextMenu = document.createElement('div');
        contextMenu.className = 'context-menu';
        contextMenu.innerHTML =
            '<div class="context-menu-item" data-action="cell">Copy Cell Value</div>' +
            '<div class="context-menu-item" data-action="row">Copy Row</div>' +
            '<div class="context-menu-item" data-action="all">Copy All Results</div>';
        contextMenu.style.left = e.clientX + 'px';
        contextMenu.style.top = e.clientY + 'px';
        document.body.appendChild(contextMenu);
        contextMenu.addEventListener('click', (ev) => {
            const action = ev.target.dataset.action;
            if (action === 'cell') {
                const r = parseInt(td.dataset.row);
                const c = parseInt(td.dataset.col);
                const key = columns[c].alias || columns[c].logicalName;
                const val = allRows[r] ? allRows[r][key] : '';
                let display = '';
                if (val !== null && val !== undefined) {
                    display = typeof val === 'object' && 'formatted' in val ? String(val.formatted || val.value || '') : String(val);
                }
                vscode.postMessage({ command: 'copyToClipboard', text: display });
            } else if (action === 'row') {
                const r = parseInt(td.dataset.row);
                const vals = columns.map(col => {
                    const key = col.alias || col.logicalName;
                    const val = allRows[r] ? allRows[r][key] : '';
                    if (val === null || val === undefined) return '';
                    return typeof val === 'object' && 'formatted' in val ? String(val.formatted || val.value || '') : String(val);
                });
                vscode.postMessage({ command: 'copyToClipboard', text: vals.join('\\t') });
            } else if (action === 'all') {
                const headers = columns.map(c => c.alias || c.logicalName);
                let text = headers.join('\\t') + '\\n';
                allRows.forEach(row => {
                    const vals = columns.map(col => {
                        const key = col.alias || col.logicalName;
                        const val = row[key];
                        if (val === null || val === undefined) return '';
                        return typeof val === 'object' && 'formatted' in val ? String(val.formatted || val.value || '') : String(val);
                    });
                    text += vals.join('\\t') + '\\n';
                });
                vscode.postMessage({ command: 'copyToClipboard', text: text.trim() });
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
        if (!str) return '';
        return str.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
    }

    // Signal ready
    vscode.postMessage({ command: 'ready' });
})();
</script>
</body>
</html>`;
    }
}
