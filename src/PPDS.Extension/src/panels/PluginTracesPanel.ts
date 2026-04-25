import * as vscode from 'vscode';

import type { DaemonClient } from '../daemonClient.js';
import type { TraceFilterDto } from '../types.js';
import { handleAuthError } from '../utils/errorUtils.js';

import { WebviewPanelBase } from './WebviewPanelBase.js';
import { getNonce } from './webviewUtils.js';
import { getEnvironmentPickerHtml } from './environmentPicker.js';
import type {
    PluginTracesPanelWebviewToHost,
    PluginTracesPanelHostToWebview,
    PluginTraceViewDto,
    PluginTraceDetailViewDto,
    TimelineNodeViewDto,
    AdvancedQueryViewDto,
} from './webview/shared/message-types.js';
import { assertNever } from './webview/shared/assert-never.js';

export class PluginTracesPanel extends WebviewPanelBase<PluginTracesPanelWebviewToHost, PluginTracesPanelHostToWebview> {
    private static instances: PluginTracesPanel[] = [];
    private static nextId = 1;
    private readonly panelId: number;

    private static readonly MAX_PANELS = 5;

    protected readonly panelLabel = 'Plugin Traces';

    private currentFilter: TraceFilterDto | undefined;
    private autoRefreshTimer: ReturnType<typeof setInterval> | null = null;
    private lastLoadedTraces: PluginTraceViewDto[] = [];

    /**
     * Returns the number of open PluginTracesPanel instances.
     * Used by diagnostic commands for panel state inspection.
     */
    static get instanceCount(): number {
        return PluginTracesPanel.instances.length;
    }

    static show(extensionUri: vscode.Uri, daemon: DaemonClient, envUrl?: string, envDisplayName?: string): PluginTracesPanel {
        if (PluginTracesPanel.instances.length >= PluginTracesPanel.MAX_PANELS) {
            const oldest = PluginTracesPanel.instances[0];
            oldest.panel?.reveal();
            return oldest;
        }
        const panel = new PluginTracesPanel(extensionUri, daemon, envUrl, envDisplayName);
        return panel;
    }

    private constructor(
        private readonly extensionUri: vscode.Uri,
        private readonly daemon: DaemonClient,
        initialEnvUrl?: string,
        initialEnvDisplayName?: string,
    ) {
        super();

        if (initialEnvUrl) {
            this.environmentUrl = initialEnvUrl;
            this.environmentDisplayName = initialEnvDisplayName ?? initialEnvUrl;
        }

        this.panelId = PluginTracesPanel.nextId++;
        PluginTracesPanel.instances.push(this);

        const panel = vscode.window.createWebviewPanel(
            'ppds.pluginTraces',
            `Plugin Traces #${this.panelId}`,
            vscode.ViewColumn.One,
            {
                enableScripts: true,
                enableFindWidget: true,
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

    protected async handleMessage(message: PluginTracesPanelWebviewToHost): Promise<void> {
        switch (message.command) {
            case 'ready':
                await this.initializePanel(this.daemon, this.panelId, PluginTracesPanel.instances.length > 1);
                break;
            case 'refresh':
                await this.loadTraces();
                break;
            case 'applyFilter':
                this.currentFilter = message.filter;
                await this.loadTraces();
                break;
            case 'selectTrace':
                await this.loadTraceDetail(message.id);
                break;
            case 'loadTimeline':
                await this.loadTimeline(message.correlationId);
                break;
            case 'deleteTraces':
                await this.confirmAndDeleteByIds(message.ids);
                break;
            case 'deleteOlderThan':
                await this.confirmAndDeleteByAge(message.days);
                break;
            case 'requestTraceLevel':
                await this.loadTraceLevel();
                break;
            case 'setTraceLevel':
                await this.setTraceLevel(message.level);
                break;
            case 'setAutoRefresh':
                this.setupAutoRefresh(message.intervalSeconds);
                break;
            case 'requestEnvironmentList':
                await this.handleEnvironmentPickerClick(this.daemon, this.panelId, PluginTracesPanel.instances.length > 1);
                break;
            case 'openInMaker':
                await this.openInMaker();
                break;
            case 'exportTraces':
                await this.exportTraces(message.format);
                break;
            case 'applyAdvancedFilter':
                this.currentFilter = this.convertAdvancedQuery(message.query);
                await this.loadTraces();
                break;
            case 'persistState':
                // State is persisted on the webview side via vscode.getState/setState
                break;
            case 'copyToClipboard':
                this.handleCopyToClipboard(message.text);
                break;
            case 'webviewError':
                this.logWebviewError(message.error, message.stack);
                break;
            default:
                assertNever(message);
        }
    }

    protected override onDaemonReconnected(): void {
        void this.loadTraces();
    }

    override dispose(): void {
        if (this.autoRefreshTimer !== null) {
            clearInterval(this.autoRefreshTimer);
            this.autoRefreshTimer = null;
        }
        const idx = PluginTracesPanel.instances.indexOf(this);
        if (idx >= 0) PluginTracesPanel.instances.splice(idx, 1);
        super.dispose();
    }

    protected override async onInitialized(): Promise<void> {
        await this.loadTraces();
    }

    protected override async onEnvironmentChanged(): Promise<void> {
        await this.loadTraces();
    }

    private async openInMaker(): Promise<void> {
        if (this.environmentUrl) {
            const baseUrl = this.environmentUrl.replace(/\/+$/, '');
            const url = `${baseUrl}/main.aspx?pagetype=entitylist&etn=plugintracelog`;
            await vscode.env.openExternal(vscode.Uri.parse(url));
        } else {
            vscode.window.showInformationMessage('Environment URL not available \u2014 cannot open Plugin Trace Logs.');
        }
    }

    private async loadTraces(isRetry = false): Promise<void> {
        try {
            this.postMessage({ command: 'loading' });
            const result = await this.daemon.pluginTracesList(this.currentFilter, undefined, this.environmentUrl, this.profileName);

            const traces: PluginTraceViewDto[] = result.traces.map(t => ({
                id: t.id,
                typeName: t.typeName,
                messageName: t.messageName,
                primaryEntity: t.primaryEntity,
                mode: t.mode,
                operationType: t.operationType,
                depth: t.depth,
                createdOn: t.createdOn,
                durationMs: t.durationMs,
                hasException: t.hasException,
                correlationId: t.correlationId,
            }));

            this.lastLoadedTraces = traces;
            this.postMessage({ command: 'tracesLoaded', traces, totalCount: result.totalCount ?? traces.length });

            // Also check trace level to inform the user if tracing is Off
            void this.loadTraceLevel();
        } catch (error) {
            const msg = error instanceof Error ? error.message : String(error);

            if (await handleAuthError(this.daemon, error, isRetry, () => this.loadTraces(true))) {
                return;
            }

            this.postMessage({ command: 'error', message: msg });
        }
    }

    private async loadTraceDetail(id: string): Promise<void> {
        try {
            const result = await this.daemon.pluginTracesGet(id, this.environmentUrl, this.profileName);
            const t = result.trace;
            const trace: PluginTraceDetailViewDto = {
                id: t.id,
                typeName: t.typeName,
                messageName: t.messageName,
                primaryEntity: t.primaryEntity,
                mode: t.mode,
                operationType: t.operationType,
                depth: t.depth,
                createdOn: t.createdOn,
                durationMs: t.durationMs,
                hasException: t.hasException,
                correlationId: t.correlationId,
                constructorDurationMs: t.constructorDurationMs,
                executionStartTime: t.executionStartTime,
                exceptionDetails: t.exceptionDetails,
                messageBlock: t.messageBlock,
                configuration: t.configuration,
                secureConfiguration: t.secureConfiguration,
                requestId: t.requestId,
                // Additional fields (PT-01 through PT-09)
                stage: t.stage ?? t.operationType,
                constructorStartTime: t.constructorStartTime ?? null,
                isSystemCreated: t.isSystemCreated ?? false,
                createdById: t.createdById ?? null,
                createdOnBehalfById: t.createdOnBehalfById ?? null,
                pluginStepId: t.pluginStepId ?? null,
                persistenceKey: t.persistenceKey ?? null,
                organizationId: t.organizationId ?? null,
                profile: t.profile ?? null,
            };
            this.postMessage({ command: 'traceDetailLoaded', trace });
        } catch (error) {
            const msg = error instanceof Error ? error.message : String(error);
            this.postMessage({ command: 'error', message: `Failed to load trace detail: ${msg}` });
        }
    }

    private async loadTimeline(correlationId: string): Promise<void> {
        try {
            const result = await this.daemon.pluginTracesTimeline(correlationId, this.environmentUrl, this.profileName);
            const mapNodes = (nodes: typeof result.nodes): TimelineNodeViewDto[] =>
                nodes.map(n => ({
                    traceId: n.traceId,
                    typeName: n.typeName,
                    messageName: n.messageName,
                    depth: n.depth,
                    durationMs: n.durationMs,
                    hasException: n.hasException,
                    offsetPercent: n.offsetPercent,
                    widthPercent: n.widthPercent,
                    hierarchyDepth: n.hierarchyDepth,
                    children: mapNodes(n.children),
                }));
            this.postMessage({ command: 'timelineLoaded', nodes: mapNodes(result.nodes) });
        } catch (error) {
            const msg = error instanceof Error ? error.message : String(error);
            this.postMessage({ command: 'error', message: `Failed to load timeline: ${msg}` });
        }
    }

    private async confirmAndDeleteByIds(ids: string[]): Promise<void> {
        const confirm = await vscode.window.showWarningMessage(
            `Delete ${ids.length} trace(s)?`,
            { modal: true },
            'Delete',
        );
        if (confirm !== 'Delete') return;

        try {
            const result = await this.daemon.pluginTracesDelete(ids, undefined, this.environmentUrl, this.profileName);
            this.postMessage({ command: 'deleteComplete', deletedCount: result.deletedCount });
            await this.loadTraces();
        } catch (error) {
            const msg = error instanceof Error ? error.message : String(error);
            this.postMessage({ command: 'error', message: `Failed to delete traces: ${msg}` });
        }
    }

    private async confirmAndDeleteByAge(days: number): Promise<void> {
        const confirm = await vscode.window.showWarningMessage(
            `Delete all traces older than ${days} day(s)?`,
            { modal: true },
            'Delete',
        );
        if (confirm !== 'Delete') return;

        try {
            const result = await this.daemon.pluginTracesDelete(undefined, days, this.environmentUrl, this.profileName);
            this.postMessage({ command: 'deleteComplete', deletedCount: result.deletedCount });
            await this.loadTraces();
        } catch (error) {
            const msg = error instanceof Error ? error.message : String(error);
            this.postMessage({ command: 'error', message: `Failed to delete traces: ${msg}` });
        }
    }

    private async loadTraceLevel(): Promise<void> {
        try {
            const result = await this.daemon.pluginTracesTraceLevel(this.environmentUrl, this.profileName);
            this.postMessage({ command: 'traceLevelLoaded', level: result.level, levelValue: result.levelValue });
        } catch (error) {
            const msg = error instanceof Error ? error.message : String(error);
            this.postMessage({ command: 'error', message: `Failed to load trace level: ${msg}` });
        }
    }

    private async setTraceLevel(level: string): Promise<void> {
        if (level === 'All') {
            const confirm = await vscode.window.showWarningMessage(
                "Setting trace level to 'All' generates significant log volume and may impact performance. Continue?",
                'Set to All',
                'Cancel',
            );
            if (confirm !== 'Set to All') return;
        }

        try {
            await this.daemon.pluginTracesSetTraceLevel(level, this.environmentUrl, this.profileName);
            await this.loadTraceLevel();
        } catch (error) {
            const msg = error instanceof Error ? error.message : String(error);
            this.postMessage({ command: 'error', message: `Failed to set trace level: ${msg}` });
        }
    }

    private setupAutoRefresh(seconds: number | null): void {
        if (this.autoRefreshTimer !== null) {
            clearInterval(this.autoRefreshTimer);
            this.autoRefreshTimer = null;
        }
        if (seconds !== null && seconds > 0) {
            this.autoRefreshTimer = setInterval(() => {
                void this.loadTraces();
            }, seconds * 1000);
        }
    }

    private tracesToCsv(traces: PluginTraceViewDto[]): string {
        const headers = ['Time', 'Plugin', 'Entity', 'Message', 'Mode', 'Duration', 'Status'];
        const csvEscape = (val: string): string => {
            if (val.includes(',') || val.includes('"') || val.includes('\n')) {
                return '"' + val.replace(/"/g, '""') + '"';
            }
            return val;
        };
        const rows = traces.map(t => [
            t.createdOn ?? '',
            t.typeName,
            t.primaryEntity ?? '',
            t.messageName ?? '',
            t.mode,
            t.durationMs !== null ? String(t.durationMs) + 'ms' : '',
            t.hasException ? 'Exception' : 'Success',
        ].map(csvEscape).join(','));
        return [headers.join(','), ...rows].join('\n');
    }

    private async exportTraces(format: string): Promise<void> {
        const traces = this.lastLoadedTraces;
        if (traces.length === 0) {
            vscode.window.showInformationMessage('No traces to export.');
            return;
        }

        if (format === 'clipboard') {
            const csv = this.tracesToCsv(traces);
            await vscode.env.clipboard.writeText(csv);
            vscode.window.showInformationMessage(`${traces.length} trace(s) copied to clipboard as CSV.`);
            return;
        }

        if (format === 'csv') {
            const uri = await vscode.window.showSaveDialog({
                filters: { 'CSV Files': ['csv'] },
                defaultUri: vscode.Uri.file('plugin-traces.csv'),
            });
            if (!uri) return;
            const csv = this.tracesToCsv(traces);
            await vscode.workspace.fs.writeFile(uri, Buffer.from(csv, 'utf-8'));
            vscode.window.showInformationMessage(`Exported ${traces.length} trace(s) to ${uri.fsPath}`);
            return;
        }

        if (format === 'json') {
            const uri = await vscode.window.showSaveDialog({
                filters: { 'JSON Files': ['json'] },
                defaultUri: vscode.Uri.file('plugin-traces.json'),
            });
            if (!uri) return;
            const json = JSON.stringify(traces, null, 2);
            await vscode.workspace.fs.writeFile(uri, Buffer.from(json, 'utf-8'));
            vscode.window.showInformationMessage(`Exported ${traces.length} trace(s) to ${uri.fsPath}`);
            return;
        }
    }

    /** Convert advanced query builder state to a TraceFilterDto for the daemon. */
    private convertAdvancedQuery(query: AdvancedQueryViewDto): TraceFilterDto {
        const filter: TraceFilterDto = {};

        // Apply quick filters
        for (const qfId of query.quickFilterIds) {
            switch (qfId) {
                case 'exceptions': filter.hasException = true; break;
                case 'success': filter.hasException = false; break;
                case 'last-hour': filter.startDate = new Date(Date.now() - 3600000).toISOString(); break;
                case 'last-24h': filter.startDate = new Date(Date.now() - 86400000).toISOString(); break;
                case 'today': {
                    const today = new Date();
                    today.setHours(0, 0, 0, 0);
                    filter.startDate = today.toISOString();
                    break;
                }
                case 'async': filter.mode = 'Async'; break;
                case 'sync': filter.mode = 'Sync'; break;
                case 'recursive': filter.minDurationMs = undefined; break; // handled by depth filter in conditions
            }
        }

        // Apply enabled advanced conditions (simplified — the daemon supports basic filter fields)
        for (const cond of query.conditions) {
            if (!cond.enabled) continue;
            const val = cond.value;
            switch (cond.field) {
                case 'Plugin Name': filter.typeName = val; break;
                case 'Entity': filter.primaryEntity = val; break;
                case 'Message': filter.messageName = val; break;
                case 'Duration': if (val) filter.minDurationMs = parseInt(val, 10) || undefined; break;
                case 'Created On': if (val) filter.startDate = new Date(val).toISOString(); break;
                case 'Mode': filter.mode = val; break;
            }
        }

        return filter;
    }

    /** Select a trace by ID and load its detail — used for timeline click navigation. */
    async selectTraceFromTimeline(traceId: string): Promise<void> {
        this.postMessage({ command: 'selectTraceById', id: traceId });
        await this.loadTraceDetail(traceId);
    }

    getHtmlContent(webview: vscode.Webview): string {
        const toolkitUri = webview.asWebviewUri(
            vscode.Uri.joinPath(this.extensionUri, 'node_modules', '@vscode', 'webview-ui-toolkit', 'dist', 'toolkit.min.js')
        ).toString();
        const panelJsUri = webview.asWebviewUri(
            vscode.Uri.joinPath(this.extensionUri, 'dist', 'plugin-traces-panel.js')
        ).toString();
        const panelCssUri = webview.asWebviewUri(
            vscode.Uri.joinPath(this.extensionUri, 'dist', 'plugin-traces-panel.css')
        ).toString();
        const nonce = getNonce();

        return `<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="UTF-8">
    <!-- 'unsafe-inline' is required by @vscode/webview-ui-toolkit for dynamic styles -->
    <meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src ${webview.cspSource} 'unsafe-inline'; script-src 'nonce-${nonce}'; font-src ${webview.cspSource};">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <link rel="stylesheet" href="${panelCssUri}">
    <script type="module" nonce="${nonce}" src="${toolkitUri}"></script>
</head>
<body>

<div id="reconnect-banner" style="display:none; background: var(--vscode-inputValidation-infoBackground, #063b49); color: var(--vscode-foreground); padding: 6px 12px; text-align: center; font-size: 12px;">
    Connection restored. Data may be stale. <a href="#" id="reconnect-refresh" style="color: var(--vscode-textLink-foreground);">Refresh</a>
</div>

<div class="toolbar">
    <vscode-button id="refresh-btn" appearance="secondary" title="Refresh plugin traces">Refresh</vscode-button>
    <select id="auto-refresh-select" title="Auto-refresh interval">
        <option value="">Off</option>
        <option value="5">5s</option>
        <option value="15">15s</option>
        <option value="30">30s</option>
        <option value="60">1m</option>
        <option value="300">5m</option>
    </select>
    <vscode-button id="delete-btn" appearance="secondary" title="Delete traces">Delete</vscode-button>
    <vscode-button id="trace-level-btn" appearance="secondary" title="View/change trace level">Trace Level</vscode-button>
    <span id="trace-level-indicator" class="trace-level-indicator"></span>
    <vscode-button id="export-btn" appearance="secondary" title="Export traces">Export</vscode-button>
    <vscode-button id="maker-btn" appearance="secondary" title="Open Plugin Trace Logs in Maker Portal">Maker Portal</vscode-button>
    <span class="toolbar-spacer"></span>
    <input type="text" id="search-input" class="toolbar-search" placeholder="Search traces..." title="Filter traces by plugin name, entity, or message" />
    ${getEnvironmentPickerHtml()}
</div>

<!-- 6f: Advanced Query Builder -->
<div id="filter-panel" class="filter-panel collapsed">
    <div id="filter-panel-header" class="filter-panel-header">
        <span class="filter-panel-title"><span class="filter-chevron">&#x25B6;</span> Filters (0 / 0)</span>
    </div>
    <div class="filter-panel-tabs" id="filter-panel-tabs">
        <button class="filter-panel-tab active" data-filter-tab="quick">Quick Filters</button>
        <button class="filter-panel-tab" data-filter-tab="advanced">Advanced</button>
        <button class="filter-panel-tab" data-filter-tab="preview">Query Preview</button>
    </div>
    <div class="filter-panel-body" id="filter-panel-body">
        <!-- Quick Filters tab -->
        <div class="filter-tab-content active" data-filter-tab="quick">
            <div class="quick-filter-grid">
                <label class="quick-filter-checkbox-item"><input type="checkbox" data-qf="exceptions" /> Exceptions</label>
                <label class="quick-filter-checkbox-item"><input type="checkbox" data-qf="success" /> Success Only</label>
                <label class="quick-filter-checkbox-item"><input type="checkbox" data-qf="last-hour" /> Last Hour</label>
                <label class="quick-filter-checkbox-item"><input type="checkbox" data-qf="last-24h" /> Last 24 Hours</label>
                <label class="quick-filter-checkbox-item"><input type="checkbox" data-qf="today" /> Today</label>
                <label class="quick-filter-checkbox-item"><input type="checkbox" data-qf="async" /> Async Only</label>
                <label class="quick-filter-checkbox-item"><input type="checkbox" data-qf="sync" /> Sync Only</label>
                <label class="quick-filter-checkbox-item"><input type="checkbox" data-qf="recursive" /> Recursive Calls</label>
            </div>
        </div>
        <!-- Advanced tab -->
        <div class="filter-tab-content" data-filter-tab="advanced">
            <div class="advanced-filter-controls">
                <span class="advanced-filter-logic">
                    <label><input type="radio" name="filter-logic" value="and" checked /> AND</label>
                    <label><input type="radio" name="filter-logic" value="or" /> OR</label>
                </span>
                <button id="add-condition-btn" class="filter-action-btn">+ Add Condition</button>
                <button id="clear-conditions-btn" class="filter-action-btn">Clear</button>
            </div>
            <div id="filter-conditions" class="filter-conditions"></div>
        </div>
        <!-- Query Preview tab -->
        <div class="filter-tab-content" data-filter-tab="preview">
            <div class="query-preview-section">
                <div class="query-preview-label">Generated Filter Conditions</div>
                <pre id="query-preview-text" class="query-preview-text">No filters applied</pre>
                <button id="copy-query-btn" class="filter-action-btn">Copy</button>
            </div>
        </div>
    </div>
    <div id="filter-panel-resize" class="filter-panel-resize-handle"></div>
</div>

<!-- 6a: Side-by-side layout -->
<div class="content">
    <div id="table-pane" class="table-pane">
        <div class="empty-state" id="empty-state">Loading plugin traces...</div>
    </div>
    <div id="resize-handle" class="resize-handle"></div>
    <div id="detail-pane" class="detail-pane">
        <div class="detail-header">
            <div class="detail-tabs-bar">
                <button class="detail-tab active" data-tab="overview">Overview</button>
                <button class="detail-tab" data-tab="details">Details</button>
                <button class="detail-tab" data-tab="exception">Exception</button>
                <button class="detail-tab" data-tab="message-block">Message Block</button>
                <button class="detail-tab" data-tab="config">Configuration</button>
                <button class="detail-tab" data-tab="timeline">Timeline</button>
            </div>
            <button id="detail-close-btn" class="detail-close-btn" title="Close detail pane">&times;</button>
        </div>
        <div class="detail-tab-content active" id="tab-overview"></div>
        <div class="detail-tab-content" id="tab-details"></div>
        <div class="detail-tab-content" id="tab-exception"></div>
        <div class="detail-tab-content" id="tab-message-block"></div>
        <div class="detail-tab-content" id="tab-config"></div>
        <div class="detail-tab-content" id="tab-timeline"></div>
    </div>
</div>

<div class="status-bar">
    <span id="status-text">Loading...</span>
</div>

<script nonce="${nonce}" src="${panelJsUri}"></script>
</body>
</html>`;
    }
}
