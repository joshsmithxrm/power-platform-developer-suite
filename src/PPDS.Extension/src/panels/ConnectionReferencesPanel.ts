import * as vscode from 'vscode';

import type { DaemonClient } from '../daemonClient.js';
import { handleAuthError } from '../utils/errorUtils.js';
import { buildMakerUrl } from '../commands/browserCommands.js';

import { WebviewPanelBase } from './WebviewPanelBase.js';
import { getNonce } from './webviewUtils.js';
import { getEnvironmentPickerHtml } from './environmentPicker.js';
import type { ConnectionReferencesPanelWebviewToHost, ConnectionReferencesPanelHostToWebview } from './webview/shared/message-types.js';
import { assertNever } from './webview/shared/assert-never.js';

/** Default Solution GUID — always present in every environment. */
const DEFAULT_SOLUTION_ID = 'fd140aaf-4df4-11dd-bd17-0019b9312238';

export class ConnectionReferencesPanel extends WebviewPanelBase<ConnectionReferencesPanelWebviewToHost, ConnectionReferencesPanelHostToWebview> {
    private static instances: ConnectionReferencesPanel[] = [];
    private static nextId = 1;
    private readonly panelId: number;

    private static readonly MAX_PANELS = 5;

    protected readonly panelLabel = 'Connection References';

    /** Current solution filter (CR-09: defaults to Default Solution GUID). */
    private solutionFilter: string | null = DEFAULT_SOLUTION_ID;
    /** Include inactive CRs (V-16). */
    private includeInactive = false;
    /** AbortController for cancelling in-flight list requests (CR-11). */
    private _loadAbortController: AbortController | null = null;

    /**
     * Returns the number of open ConnectionReferencesPanel instances.
     * Used by diagnostic commands for panel state inspection.
     */
    static get instanceCount(): number {
        return ConnectionReferencesPanel.instances.length;
    }

    static show(extensionUri: vscode.Uri, daemon: DaemonClient, envUrl?: string, envDisplayName?: string): ConnectionReferencesPanel {
        if (ConnectionReferencesPanel.instances.length >= ConnectionReferencesPanel.MAX_PANELS) {
            const oldest = ConnectionReferencesPanel.instances[0];
            oldest.panel?.reveal();
            return oldest;
        }
        const panel = new ConnectionReferencesPanel(extensionUri, daemon, envUrl, envDisplayName);
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

        this.panelId = ConnectionReferencesPanel.nextId++;
        ConnectionReferencesPanel.instances.push(this);

        const panel = vscode.window.createWebviewPanel(
            'ppds.connectionReferences',
            `Connection References #${this.panelId}`,
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

    protected async handleMessage(message: ConnectionReferencesPanelWebviewToHost): Promise<void> {
        switch (message.command) {
            case 'ready':
                await this.initializePanel(this.daemon, this.panelId, ConnectionReferencesPanel.instances.length > 1);
                break;
            case 'refresh':
                await this.loadConnectionReferences();
                break;
            case 'selectReference':
                await this.loadConnectionReferenceDetail(message.logicalName);
                break;
            case 'getDetail':
                await this.loadConnectionReferenceDetail(message.logicalName);
                break;
            case 'analyze':
                await this.runAnalysis();
                break;
            case 'filterBySolution':
                this.solutionFilter = message.solutionId;
                this.abortPendingLoad();
                await this.loadConnectionReferences();
                break;
            case 'setIncludeInactive':
                this.includeInactive = message.includeInactive;
                this.abortPendingLoad();
                await this.loadConnectionReferences();
                break;
            case 'requestSolutionList':
                await this.loadSolutionList();
                break;
            case 'requestEnvironmentList':
                await this.handleEnvironmentPickerClick(this.daemon, this.panelId, ConnectionReferencesPanel.instances.length > 1);
                break;
            case 'openInMaker':
                await this.openInMaker();
                break;
            case 'openFlowInMaker':
                await vscode.env.openExternal(vscode.Uri.parse(message.url));
                break;
            case 'syncDeploymentSettings':
                vscode.window.showInformationMessage('Sync Deployment Settings coming soon');
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

    /** Cancel any in-flight list request (CR-11). */
    private abortPendingLoad(): void {
        if (this._loadAbortController) {
            this._loadAbortController.abort();
            this._loadAbortController = null;
        }
    }

    protected override onDaemonReconnected(): void {
        void this.loadConnectionReferences();
    }

    override dispose(): void {
        this.abortPendingLoad();
        const idx = ConnectionReferencesPanel.instances.indexOf(this);
        if (idx >= 0) ConnectionReferencesPanel.instances.splice(idx, 1);
        super.dispose();
    }

    protected override async onInitialized(): Promise<void> {
        await this.loadConnectionReferences();
    }

    protected override async onEnvironmentChanged(): Promise<void> {
        // Reset solution filter to default on environment change (CR-09)
        this.solutionFilter = DEFAULT_SOLUTION_ID;
        await this.loadConnectionReferences();
    }

    private async loadConnectionReferences(isRetry = false): Promise<void> {
        // CR-11: abort any previous in-flight request
        this.abortPendingLoad();
        this._loadAbortController = new AbortController();
        const signal = this._loadAbortController.signal;

        try {
            this.postMessage({ command: 'loading' });
            const result = await this.daemon.connectionReferencesList(
                this.solutionFilter ?? undefined,
                this.environmentUrl,
                this.includeInactive,
            );

            // Check if request was aborted before we got the result
            if (signal.aborted) return;

            this.postMessage({
                command: 'connectionReferencesLoaded',
                references: result.references.map(r => ({
                    logicalName: r.logicalName,
                    displayName: r.displayName,
                    connectorId: r.connectorId,
                    connectionId: r.connectionId,
                    isManaged: r.isManaged,
                    modifiedOn: r.modifiedOn,
                    connectionStatus: r.connectionStatus,
                    connectorDisplayName: r.connectorDisplayName,
                    // CR-05: flag unbound CRs as health warnings
                    hasHealthWarning: !r.connectionId || r.connectionStatus?.toLowerCase() === 'error',
                })),
                totalCount: result.totalCount ?? result.references.length,
                filtersApplied: result.filtersApplied ?? [],
            });
        } catch (error) {
            if (signal.aborted) return;
            const msg = error instanceof Error ? error.message : String(error);

            if (await handleAuthError(this.daemon, error, isRetry, () => this.loadConnectionReferences(true))) {
                return;
            }

            this.postMessage({ command: 'error', message: msg });
        }
    }

    private async loadConnectionReferenceDetail(logicalName: string): Promise<void> {
        try {
            const result = await this.daemon.connectionReferencesGet(logicalName, this.environmentUrl);
            const ref = result.reference;
            this.postMessage({
                command: 'connectionReferenceDetailLoaded',
                environmentId: this.environmentId,
                detail: {
                    logicalName: ref.logicalName,
                    displayName: ref.displayName,
                    connectorId: ref.connectorId,
                    connectionId: ref.connectionId,
                    isManaged: ref.isManaged,
                    modifiedOn: ref.modifiedOn,
                    connectionStatus: ref.connectionStatus,
                    connectorDisplayName: ref.connectorDisplayName,
                    description: ref.description,
                    isBound: ref.isBound,
                    createdOn: ref.createdOn,
                    flows: ref.flows.map(f => ({
                        flowId: f.flowId,
                        uniqueName: f.uniqueName,
                        displayName: f.displayName,
                        state: f.state,
                    })),
                    connectionOwner: ref.connectionOwner,
                    connectionIsShared: ref.connectionIsShared,
                    // Propagate flow count to the detail DTO for status colum update
                    flowCount: ref.flows.length,
                    hasHealthWarning: !ref.isBound || ref.connectionStatus?.toLowerCase() === 'error',
                },
            });
        } catch (error) {
            const msg = error instanceof Error ? error.message : String(error);
            this.postMessage({ command: 'error', message: `Failed to load detail: ${msg}` });
        }
    }

    private async runAnalysis(): Promise<void> {
        try {
            this.postMessage({ command: 'loading' });
            const result = await this.daemon.connectionReferencesAnalyze(this.environmentUrl);
            this.postMessage({
                command: 'analyzeResult',
                result: {
                    orphanedReferences: result.orphanedReferences.map(r => ({
                        logicalName: r.logicalName,
                        displayName: r.displayName,
                        connectorId: r.connectorId,
                    })),
                    orphanedFlows: result.orphanedFlows.map(f => ({
                        uniqueName: f.uniqueName,
                        displayName: f.displayName,
                        missingReference: f.missingReference,
                    })),
                    totalReferences: result.totalReferences,
                    totalFlows: result.totalFlows,
                },
            });
        } catch (error) {
            const msg = error instanceof Error ? error.message : String(error);
            this.postMessage({ command: 'error', message: `Analysis failed: ${msg}` });
        }
    }

    private async loadSolutionList(): Promise<void> {
        try {
            const result = await this.daemon.solutionsList(undefined, false, this.environmentUrl);
            this.postMessage({
                command: 'solutionListLoaded',
                solutions: result.solutions.map(s => ({
                    id: s.id,
                    uniqueName: s.uniqueName,
                    friendlyName: s.friendlyName,
                })),
            });
        } catch (error) {
            const msg = error instanceof Error ? error.message : String(error);
            this.postMessage({ command: 'error', message: `Failed to load solutions: ${msg}` });
        }
    }

    private async openInMaker(): Promise<void> {
        if (this.environmentId) {
            const url = buildMakerUrl(this.environmentId, '/connections');
            await vscode.env.openExternal(vscode.Uri.parse(url));
        } else {
            vscode.window.showInformationMessage('Environment ID not available \u2014 cannot open Maker Portal.');
        }
    }

    getHtmlContent(webview: vscode.Webview): string {
        const toolkitUri = webview.asWebviewUri(
            vscode.Uri.joinPath(this.extensionUri, 'node_modules', '@vscode', 'webview-ui-toolkit', 'dist', 'toolkit.min.js')
        ).toString();
        const panelJsUri = webview.asWebviewUri(
            vscode.Uri.joinPath(this.extensionUri, 'dist', 'connection-references-panel.js')
        ).toString();
        const panelCssUri = webview.asWebviewUri(
            vscode.Uri.joinPath(this.extensionUri, 'dist', 'connection-references-panel.css')
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
    <vscode-button id="refresh-btn" appearance="secondary" title="Refresh connection references">Refresh</vscode-button>
    <vscode-button id="analyze-btn" appearance="secondary" title="Analyze orphaned references">Analyze</vscode-button>
    <vscode-button id="sync-btn" appearance="secondary" title="Sync deployment settings for connection references">Sync Deployment Settings</vscode-button>
    <vscode-button id="maker-btn" appearance="secondary" title="Open Connections in Maker Portal">Maker Portal</vscode-button>
    <div id="solution-filter-container" class="solution-filter-container"></div>
    <div class="toolbar-segmented" id="inactive-toggle" title="Toggle active/all connection references">
        <button class="seg-btn seg-active" id="seg-active" data-value="active">Active Only</button>
        <button class="seg-btn" id="seg-all" data-value="all">All</button>
    </div>
    <input id="search-input" type="text" placeholder="Search connection references..." class="toolbar-search" />
    <span class="toolbar-spacer"></span>
    ${getEnvironmentPickerHtml()}
</div>

<div class="content" id="content">
    <div class="empty-state" id="empty-state">Loading connection references...</div>
</div>

<div class="status-bar">
    <span id="status-text">Loading...</span>
</div>

<script nonce="${nonce}" src="${panelJsUri}"></script>
</body>
</html>`;
    }
}
