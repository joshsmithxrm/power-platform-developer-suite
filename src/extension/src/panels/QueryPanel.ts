import * as vscode from 'vscode';
import { CancellationTokenSource, ResponseError } from 'vscode-jsonrpc/node';

import type { DaemonClient } from '../daemonClient.js';
import type { QueryResultResponse } from '../types.js';
import { showQueryHistory } from '../commands/queryHistoryCommand.js';
import { handleAuthError } from '../utils/errorUtils.js';
import { queryDefaultTop } from '../utils/config.js';

import { getNonce } from './webviewUtils.js';
import { WebviewPanelBase } from './WebviewPanelBase.js';
import { getEnvironmentPickerHtml, showEnvironmentPicker } from './environmentPicker.js';
import type { QueryPanelWebviewToHost, QueryPanelHostToWebview } from './webview/shared/message-types.js';
import { assertNever } from './webview/shared/assert-never.js';

export class QueryPanel extends WebviewPanelBase<QueryPanelWebviewToHost, QueryPanelHostToWebview> {
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
    private readonly initialSql: string | undefined;

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
        this.initialSql = initialSql;

        const panel = vscode.window.createWebviewPanel(
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

        panel.webview.html = this.getHtmlContent(panel.webview);
        this.initPanel(panel);
        this.subscribeToDaemonReconnect(this.daemon);
    }

    protected async handleMessage(message: QueryPanelWebviewToHost): Promise<void> {
        switch (message.command) {
            case 'executeQuery':
                await this.executeQuery(message.sql, false, message.useTds, message.language);
                break;
            case 'showFetchXml':
                await this.showFetchXml(message.sql);
                break;
            case 'loadMore':
                await this.loadMore(message.pagingCookie, message.page);
                break;
            case 'explainQuery':
                await this.explainQuery(message.sql);
                break;
            case 'exportResults':
                await this.exportResults(message.format);
                break;
            case 'saveQuery':
                await this.saveQuery(message.sql, message.language);
                break;
            case 'loadQueryFromFile':
                await this.loadQueryFromFile();
                break;
            case 'openInNotebook':
                await vscode.commands.executeCommand('ppds.openQueryInNotebook', message.sql);
                break;
            case 'showHistory': {
                const sql = await showQueryHistory(this.daemon);
                if (sql) {
                    this.postMessage({ command: 'loadQuery', sql });
                }
                break;
            }
            case 'copyToClipboard':
                await vscode.env.clipboard.writeText(message.text);
                break;
            case 'openRecordUrl': {
                const parsed = vscode.Uri.parse(message.url);
                if (parsed.scheme === 'https' || parsed.scheme === 'http') {
                    await vscode.env.openExternal(parsed);
                }
                break;
            }
            case 'requestClipboard': {
                const clipText = await vscode.env.clipboard.readText();
                this.postMessage({ command: 'clipboardContent', text: clipText });
                break;
            }
            case 'requestCompletions': {
                const requestId = message.requestId;
                try {
                    const result = await this.daemon.queryComplete({
                        sql: message.sql,
                        cursorOffset: message.cursorOffset,
                        language: message.language,
                    });
                    this.postMessage({ command: 'completionResult', requestId, items: result.items });
                } catch {
                    this.postMessage({ command: 'completionResult', requestId, items: [] });
                }
                break;
            }
            case 'ready':
                if (this.initialSql) {
                    this.postMessage({ command: 'loadQuery', sql: this.initialSql });
                }
                // Initialize environment from active profile
                void this.initEnvironment();
                break;
            case 'webviewError':
                this.logWebviewError(message.error, message.stack);
                break;
            case 'cancelQuery':
                this.queryCts?.cancel();
                break;
            case 'convertQuery': {
                const { sql, toLanguage } = message;
                try {
                    let converted: string;
                    if (toLanguage === 'xml') {
                        // SQL → FetchXML: use daemon's explain endpoint (fetchXml field has clean XML)
                        const result = await this.daemon.queryExplain({
                            sql,
                            environmentUrl: this.environmentUrl ?? undefined,
                        });
                        converted = result.fetchXml ?? result.plan;
                    } else {
                        // FetchXML → SQL: use client-side transpiler
                        const { FetchXmlToSqlTranspiler } = await import('../utils/fetchXmlToSql.js');
                        const transpiler = new FetchXmlToSqlTranspiler();
                        const result = transpiler.transpile(sql);
                        if (!result.success) {
                            throw new Error(result.error || 'Transpilation failed');
                        }
                        converted = result.sql;
                    }
                    this.postMessage({
                        command: 'queryConverted',
                        content: converted,
                        language: toLanguage,
                    });
                } catch (error) {
                    const msg = error instanceof Error ? error.message : String(error);
                    vscode.window.showWarningMessage(`Conversion failed: ${msg}`);
                    this.postMessage({
                        command: 'conversionFailed',
                        error: msg,
                        language: toLanguage,
                    });
                }
                break;
            }
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
            default:
                assertNever(message);
        }
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
        const context = [this.profileName, this.environmentDisplayName].filter(Boolean).join(' \u00B7 ');
        const suffix = QueryPanel.instances.length > 1 ? ` ${this.panelId}` : '';
        this.panel.title = context ? `${context} \u2014 Data Explorer${suffix}` : `Data Explorer${suffix}`;
    }

    private async executeQuery(sql: string, isRetry = false, useTds?: boolean, language?: string, isConfirmed = false): Promise<void> {
        // Cancel any in-flight query and create a fresh token
        this.queryCts?.cancel();
        this.queryCts?.dispose();
        this.queryCts = new CancellationTokenSource();
        const token = this.queryCts.token;

        try {
            this.postMessage({ command: 'executionStarted' });
            const defaultTop = queryDefaultTop();
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

            if (await handleAuthError(this.daemon, error, isRetry, () =>
                this.executeQuery(sql, true, useTds, language, isConfirmed)
            )) {
                return;
            }

            this.postMessage({ command: 'queryError', error: msg });
        }
    }

    private async showFetchXml(sql: string): Promise<void> {
        try {
            const result = await this.daemon.queryExplain({ sql, environmentUrl: this.environmentUrl });
            if (result.plan) {
                const { ExplainDocumentProvider } = await import('../providers/explainDocumentProvider.js');
                if (ExplainDocumentProvider.instance) {
                    await ExplainDocumentProvider.instance.show(result.plan, 'xml');
                } else {
                    const doc = await vscode.workspace.openTextDocument({ content: result.plan, language: 'xml' });
                    await vscode.window.showTextDocument(doc, vscode.ViewColumn.Beside);
                }
            }
        } catch (error) {
            const msg = error instanceof Error ? error.message : String(error);
            vscode.window.showErrorMessage(`FetchXML preview failed: ${msg}`);
        }
    }

    private async explainQuery(sql: string): Promise<void> {
        try {
            const result = await this.daemon.queryExplain({ sql, environmentUrl: this.environmentUrl });
            const language = result.format === 'fetchxml' ? 'xml' : 'plaintext';
            const { ExplainDocumentProvider } = await import('../providers/explainDocumentProvider.js');
            if (ExplainDocumentProvider.instance) {
                await ExplainDocumentProvider.instance.show(result.plan, language);
            } else {
                // Fallback if provider not registered
                const doc = await vscode.workspace.openTextDocument({ content: result.plan, language });
                await vscode.window.showTextDocument(doc, vscode.ViewColumn.Beside);
            }
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

    private async exportResults(preselectedFormat?: string): Promise<void> {
        if (!this.lastResult || this.allRecords.length === 0 || !this.lastSql) {
            vscode.window.showWarningMessage('No results to export. Run a query first.');
            return;
        }

        // If clipboard was selected from dropdown, copy immediately
        if (preselectedFormat === 'clipboard') {
            try {
                const exportParams = this.buildExportParams('tsv', true);
                const result = await this.daemon.queryExport(exportParams);
                await vscode.env.clipboard.writeText(result.content);
                vscode.window.showInformationMessage(`Copied ${result.rowCount} rows to clipboard`);
            } catch (error) {
                const msg = error instanceof Error ? error.message : String(error);
                vscode.window.showErrorMessage(`Export failed: ${msg}`);
            }
            return;
        }

        // Resolve format: use preselected from dropdown or show QuickPick
        let format: string;
        let isClipboard = false;
        if (preselectedFormat && ['csv', 'tsv', 'json'].includes(preselectedFormat)) {
            format = preselectedFormat;
        } else {
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
            format = formatPick.format;
            isClipboard = formatPick.isClipboard;
        }

        // Headers toggle — skip for JSON (always keyed objects)
        let includeHeaders = true;
        if (format !== 'json') {
            const headersPick = await vscode.window.showQuickPick([
                { label: 'Include column headers', includeHeaders: true },
                { label: 'Data only (no headers)', includeHeaders: false },
            ], {
                title: 'Column Headers',
                placeHolder: 'Include headers in export?',
            });
            if (!headersPick) return;
            includeHeaders = headersPick.includeHeaders;
        }

        try {
            const exportParams = this.buildExportParams(format, includeHeaders);
            const result = await this.daemon.queryExport(exportParams);

            if (isClipboard) {
                await vscode.env.clipboard.writeText(result.content);
                vscode.window.showInformationMessage(`Copied ${result.rowCount} rows to clipboard`);
                return;
            }

            const fileExt = format === 'tsv' ? 'tsv' : format === 'json' ? 'json' : 'csv';
            const filterName = format === 'tsv' ? 'TSV Files' : format === 'json' ? 'JSON Files' : 'CSV Files';

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

    private buildExportParams(format: string, includeHeaders: boolean): Parameters<typeof this.daemon.queryExport>[0] {
        const params: Parameters<typeof this.daemon.queryExport>[0] = {
            sql: this.lastSql!,
            format,
            includeHeaders,
            environmentUrl: this.environmentUrl,
        };
        if (this.lastLanguage === 'xml') {
            params.fetchXml = this.lastSql!;
            params.sql = '';
        }
        return params;
    }

    private async saveQuery(sql: string, language: string): Promise<void> {
        if (!sql.trim()) {
            vscode.window.showWarningMessage('No query to save.');
            return;
        }
        const ext = language === 'xml' ? 'xml' : 'sql';
        const filterName = language === 'xml' ? 'FetchXML Files' : 'SQL Files';
        const uri = await vscode.window.showSaveDialog({
            defaultUri: vscode.Uri.file(`query.${ext}`),
            filters: { [filterName]: [ext] },
        });
        if (uri) {
            await vscode.workspace.fs.writeFile(uri, new TextEncoder().encode(sql));
            vscode.window.showInformationMessage(`Query saved to ${uri.fsPath}`);
        }
    }

    private async loadQueryFromFile(): Promise<void> {
        const uris = await vscode.window.showOpenDialog({
            filters: { 'Query Files': ['sql', 'xml', 'fetchxml'] },
            canSelectMany: false,
        });
        if (uris && uris.length > 0) {
            const content = new TextDecoder().decode(await vscode.workspace.fs.readFile(uris[0]));
            this.postMessage({ command: 'loadQuery', sql: content });
        }
    }

    getHtmlContent(webview: vscode.Webview): string {
        const toolkitUri = webview.asWebviewUri(
            vscode.Uri.joinPath(this.extensionUri, 'node_modules', '@vscode', 'webview-ui-toolkit', 'dist', 'toolkit.min.js')
        ).toString();
        const monacoUri = webview.asWebviewUri(
            vscode.Uri.joinPath(this.extensionUri, 'dist', 'monaco-editor.js')
        ).toString();
        const monacoCssUri = webview.asWebviewUri(
            vscode.Uri.joinPath(this.extensionUri, 'dist', 'monaco-editor.css')
        ).toString();
        const workerUri = webview.asWebviewUri(
            vscode.Uri.joinPath(this.extensionUri, 'dist', 'editor.worker.js')
        ).toString();
        const queryPanelJsUri = webview.asWebviewUri(
            vscode.Uri.joinPath(this.extensionUri, 'dist', 'query-panel.js')
        ).toString();
        const queryPanelCssUri = webview.asWebviewUri(
            vscode.Uri.joinPath(this.extensionUri, 'dist', 'query-panel.css')
        ).toString();
        const nonce = getNonce();

        return `<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="UTF-8">
    <!-- 'unsafe-inline' is required by @vscode/webview-ui-toolkit for dynamic styles -->
    <meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src ${webview.cspSource} 'unsafe-inline'; script-src 'nonce-${nonce}'; font-src ${webview.cspSource}; worker-src blob:;">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <link rel="stylesheet" href="${queryPanelCssUri}">
    <script type="module" nonce="${nonce}" src="${toolkitUri}"></script>
    <link rel="stylesheet" href="${monacoCssUri}">
    <script nonce="${nonce}">self.__MONACO_WORKER_URL__ = '${workerUri}';</script>
    <script nonce="${nonce}" src="${monacoUri}"></script>
</head>
<body>

<div class="toolbar">
    <vscode-button id="execute-btn" appearance="primary">Execute</vscode-button>
    <vscode-button id="cancel-btn" appearance="secondary" style="display:none;" title="Cancel query (Escape)">Cancel</vscode-button>
    <vscode-button id="export-btn" appearance="secondary" title="Export results or save query">Export \u25BE</vscode-button>
    <vscode-button id="history-btn" appearance="secondary" title="Query history (Ctrl+Shift+H)">History</vscode-button>
    <vscode-button id="more-btn" appearance="icon" title="More actions">
        <span class="codicon codicon-ellipsis"></span>
    </vscode-button>
    <span class="toolbar-spacer"></span>
    ${getEnvironmentPickerHtml()}
</div>

<div id="reconnect-banner" style="display:none; background: var(--vscode-inputValidation-infoBackground, #063b49); color: var(--vscode-foreground); padding: 6px 12px; text-align: center; font-size: 12px;">
    Connection restored. Data may be stale. <a href="#" id="reconnect-refresh" style="color: var(--vscode-textLink-foreground);">Refresh</a>
</div>

<div class="editor-container">
    <div class="editor-wrapper">
        <div id="sql-editor"></div>
        <div class="lang-toggle" id="lang-toggle">
            <button class="lang-seg active" data-lang="sql">SQL</button>
            <button class="lang-seg" data-lang="xml">FetchXML</button>
        </div>
    </div>
</div>

<div class="resize-handle" id="resize-handle"></div>

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

<script nonce="${nonce}" src="${queryPanelJsUri}"></script>
</body>
</html>`;
    }
}
