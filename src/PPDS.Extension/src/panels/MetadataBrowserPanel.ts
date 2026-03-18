import * as vscode from 'vscode';

import type { DaemonClient } from '../daemonClient.js';
import { buildMakerUrl } from '../commands/browserCommands.js';
import { handleAuthError } from '../utils/errorUtils.js';

import { WebviewPanelBase } from './WebviewPanelBase.js';
import { getNonce } from './webviewUtils.js';
import { getEnvironmentPickerHtml } from './environmentPicker.js';
import type { MetadataBrowserPanelWebviewToHost, MetadataBrowserPanelHostToWebview } from './webview/shared/message-types.js';
import { assertNever } from './webview/shared/assert-never.js';

export class MetadataBrowserPanel extends WebviewPanelBase<MetadataBrowserPanelWebviewToHost, MetadataBrowserPanelHostToWebview> {
    private static instances: MetadataBrowserPanel[] = [];
    private static nextId = 1;
    private readonly panelId: number;

    private static readonly MAX_PANELS = 5;
    protected readonly panelLabel = 'Metadata Browser';

    /**
     * Returns the number of open MetadataBrowserPanel instances.
     * Used by diagnostic commands for panel state inspection.
     */
    static get instanceCount(): number {
        return MetadataBrowserPanel.instances.length;
    }

    static show(extensionUri: vscode.Uri, daemon: DaemonClient, envUrl?: string, envDisplayName?: string): MetadataBrowserPanel {
        if (MetadataBrowserPanel.instances.length >= MetadataBrowserPanel.MAX_PANELS) {
            const oldest = MetadataBrowserPanel.instances[0];
            oldest.panel?.reveal();
            return oldest;
        }
        const panel = new MetadataBrowserPanel(extensionUri, daemon, envUrl, envDisplayName);
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

        this.panelId = MetadataBrowserPanel.nextId++;
        MetadataBrowserPanel.instances.push(this);

        const panel = vscode.window.createWebviewPanel(
            'ppds.metadataBrowser',
            `Metadata Browser #${this.panelId}`,
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

    protected async handleMessage(message: MetadataBrowserPanelWebviewToHost): Promise<void> {
        switch (message.command) {
            case 'ready':
                await this.initializePanel(this.daemon, this.panelId, MetadataBrowserPanel.instances.length > 1);
                break;
            case 'requestEnvironmentList':
                await this.handleEnvironmentPickerClick(this.daemon, this.panelId, MetadataBrowserPanel.instances.length > 1);
                break;
            case 'refresh':
                await this.loadEntities();
                break;
            case 'selectEntity':
                await this.loadEntityDetail(message.logicalName);
                break;
            case 'openInMaker':
                await this.openInMaker(message.entityLogicalName);
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
        void this.loadEntities();
    }

    override dispose(): void {
        const idx = MetadataBrowserPanel.instances.indexOf(this);
        if (idx >= 0) MetadataBrowserPanel.instances.splice(idx, 1);
        super.dispose();
    }

    protected override async onInitialized(): Promise<void> {
        await this.loadEntities();
    }

    protected override async onEnvironmentChanged(): Promise<void> {
        await this.loadEntities();
    }

    private async loadEntities(isRetry = false): Promise<void> {
        try {
            this.postMessage({ command: 'loading' });
            const result = await this.daemon.metadataEntities(this.environmentUrl);

            this.postMessage({
                command: 'entitiesLoaded',
                entities: result.entities.map(e => ({
                    logicalName: e.logicalName,
                    schemaName: e.schemaName,
                    displayName: e.displayName,
                    isCustomEntity: e.isCustomEntity,
                    isManaged: e.isManaged,
                    ownershipType: e.ownershipType,
                    description: e.description,
                })),
            });
        } catch (error) {
            const msg = error instanceof Error ? error.message : String(error);

            if (await handleAuthError(this.daemon, error, isRetry, () => this.loadEntities(true))) {
                return;
            }

            this.postMessage({ command: 'error', message: msg });
        }
    }

    private async loadEntityDetail(logicalName: string, isRetry = false): Promise<void> {
        try {
            this.postMessage({ command: 'entityDetailLoading', logicalName });
            const result = await this.daemon.metadataEntity(logicalName, true, this.environmentUrl);
            this.postMessage({
                command: 'entityDetailLoaded',
                entity: result.entity,
            });
        } catch (error) {
            const msg = error instanceof Error ? error.message : String(error);

            if (await handleAuthError(this.daemon, error, isRetry, () => this.loadEntityDetail(logicalName, true))) {
                return;
            }

            this.postMessage({ command: 'error', message: `Failed to load entity detail: ${msg}` });
        }
    }

    private async openInMaker(entityLogicalName?: string): Promise<void> {
        if (this.environmentId) {
            const url = entityLogicalName
                ? buildMakerUrl(this.environmentId, `/entities/${entityLogicalName}`)
                : buildMakerUrl(this.environmentId, '/entities');
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
            vscode.Uri.joinPath(this.extensionUri, 'dist', 'metadata-browser-panel.js')
        ).toString();
        const panelCssUri = webview.asWebviewUri(
            vscode.Uri.joinPath(this.extensionUri, 'dist', 'metadata-browser-panel.css')
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
    <vscode-button id="refresh-btn" appearance="secondary" title="Refresh entity list">Refresh</vscode-button>
    <vscode-button id="maker-btn" appearance="secondary" title="Open in Maker Portal">Maker Portal</vscode-button>
    <span class="toolbar-spacer"></span>
    ${getEnvironmentPickerHtml()}
</div>

<div class="split-pane">
    <div class="entity-list-pane" id="entity-list-pane">
        <div class="search-container">
            <input type="text" id="entity-search" placeholder="Search entities..." />
            <span id="filter-count"></span>
        </div>
        <div class="filter-toggle-container">
            <label class="filter-toggle-label">
                <input type="checkbox" id="hide-system-toggle" />
                <span>Custom Only</span>
            </label>
        </div>
        <div class="entity-list" id="entity-list"></div>
    </div>
    <div class="entity-detail-pane" id="entity-detail-pane">
        <div class="tab-bar" id="tab-bar"></div>
        <div class="tab-content" id="tab-content">
            <div class="empty-state">Select an entity to view details</div>
        </div>
    </div>
</div>

<div class="status-bar"><span id="status-text">Loading...</span></div>

<script nonce="${nonce}" src="${panelJsUri}"></script>
</body>
</html>`;
    }
}
