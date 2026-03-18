import * as vscode from 'vscode';

import type { DaemonClient } from '../daemonClient.js';
import { handleAuthError } from '../utils/errorUtils.js';
import { buildMakerUrl } from '../commands/browserCommands.js';

import { WebviewPanelBase } from './WebviewPanelBase.js';
import { getNonce } from './webviewUtils.js';
import { getEnvironmentPickerHtml } from './environmentPicker.js';
import type { ConnectionReferencesPanelWebviewToHost, ConnectionReferencesPanelHostToWebview } from './webview/shared/message-types.js';
import { assertNever } from './webview/shared/assert-never.js';

export class ConnectionReferencesPanel extends WebviewPanelBase<ConnectionReferencesPanelWebviewToHost, ConnectionReferencesPanelHostToWebview> {
    private static instances: ConnectionReferencesPanel[] = [];
    private static nextId = 1;
    private readonly panelId: number;

    private static readonly MAX_PANELS = 5;

    protected readonly panelLabel = 'Connection References';

    private solutionFilter: string | null = null;

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
                retainContextWhenHidden: false,
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
            case 'analyze':
                await this.runAnalysis();
                break;
            case 'filterBySolution':
                this.solutionFilter = message.solutionId;
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
        void this.loadConnectionReferences();
    }

    override dispose(): void {
        const idx = ConnectionReferencesPanel.instances.indexOf(this);
        if (idx >= 0) ConnectionReferencesPanel.instances.splice(idx, 1);
        super.dispose();
    }

    protected override async onInitialized(): Promise<void> {
        await this.loadConnectionReferences();
    }

    protected override async onEnvironmentChanged(): Promise<void> {
        await this.loadConnectionReferences();
    }

    private async loadConnectionReferences(isRetry = false): Promise<void> {
        try {
            this.postMessage({ command: 'loading' });
            const result = await this.daemon.connectionReferencesList(
                this.solutionFilter ?? undefined,
                this.environmentUrl,
            );

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
                })),
            });
        } catch (error) {
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
                        uniqueName: f.uniqueName,
                        displayName: f.displayName,
                        state: f.state,
                    })),
                    connectionOwner: ref.connectionOwner,
                    connectionIsShared: ref.connectionIsShared,
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
    <vscode-button id="maker-btn" appearance="secondary" title="Open Connections in Maker Portal">Maker Portal</vscode-button>
    <div id="solution-filter-container" class="solution-filter-container"></div>
    <span class="toolbar-spacer"></span>
    ${getEnvironmentPickerHtml()}
</div>

<div class="content" id="content">
    <div class="empty-state" id="empty-state">Loading connection references...</div>
</div>

<div class="detail-pane" id="detail-pane" style="display: none;">
    <div class="detail-header">
        <span id="detail-title">Connection Reference Detail</span>
        <button class="detail-close-btn" id="detail-close" title="Close detail">\u00D7</button>
    </div>
    <div class="detail-content" id="detail-content"></div>
</div>

<div class="status-bar">
    <span id="status-text">Loading...</span>
</div>

<script nonce="${nonce}" src="${panelJsUri}"></script>
</body>
</html>`;
    }
}
