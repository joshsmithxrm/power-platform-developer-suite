import * as vscode from 'vscode';

import type { DaemonClient } from '../daemonClient.js';
import { handleAuthError } from '../utils/errorUtils.js';
import { buildMakerUrl } from '../commands/browserCommands.js';
import type { WebResourceFileSystemProvider } from '../providers/WebResourceFileSystemProvider.js';

import { WebviewPanelBase } from './WebviewPanelBase.js';
import { getNonce } from './webviewUtils.js';
import { getEnvironmentPickerHtml } from './environmentPicker.js';
import type { WebResourcesPanelWebviewToHost, WebResourcesPanelHostToWebview } from './webview/shared/message-types.js';
import { assertNever } from './webview/shared/assert-never.js';

export class WebResourcesPanel extends WebviewPanelBase<WebResourcesPanelWebviewToHost, WebResourcesPanelHostToWebview> {
    private static instances: WebResourcesPanel[] = [];
    private static nextId = 1;
    private readonly panelId: number;

    private static readonly MAX_PANELS = 5;

    protected readonly panelLabel = 'Web Resources';

    /** Solution filter state — persisted in globalState. */
    private solutionId: string | null = null;
    /** Text-only toggle — persisted in globalState. Default true. */
    private textOnly = true;
    /** Monotonically increasing request ID to discard stale responses. */
    private requestId = 0;

    /** File system provider for opening web resources in editor. */
    private readonly fsp: WebResourceFileSystemProvider | undefined;

    /**
     * Returns the number of open WebResourcesPanel instances.
     * Used by diagnostic commands for panel state inspection.
     */
    static get instanceCount(): number {
        return WebResourcesPanel.instances.length;
    }

    static show(
        extensionUri: vscode.Uri,
        daemon: DaemonClient,
        context: vscode.ExtensionContext,
        envUrl?: string,
        envDisplayName?: string,
        fsp?: WebResourceFileSystemProvider,
    ): WebResourcesPanel {
        if (WebResourcesPanel.instances.length >= WebResourcesPanel.MAX_PANELS) {
            const oldest = WebResourcesPanel.instances[0];
            oldest.panel?.reveal();
            return oldest;
        }
        const panel = new WebResourcesPanel(extensionUri, daemon, context, envUrl, envDisplayName, fsp);
        return panel;
    }

    private constructor(
        private readonly extensionUri: vscode.Uri,
        private readonly daemon: DaemonClient,
        private readonly context: vscode.ExtensionContext,
        initialEnvUrl?: string,
        initialEnvDisplayName?: string,
        fsp?: WebResourceFileSystemProvider,
    ) {
        super();
        this.fsp = fsp;

        if (initialEnvUrl) {
            this.environmentUrl = initialEnvUrl;
            this.environmentDisplayName = initialEnvDisplayName ?? initialEnvUrl;
        }

        // Restore persisted filter state
        this.solutionId = this.context.globalState.get<string | null>('ppds.webResources.solutionId', null);
        this.textOnly = this.context.globalState.get<boolean>('ppds.webResources.textOnly', true);

        this.panelId = WebResourcesPanel.nextId++;
        WebResourcesPanel.instances.push(this);

        const panel = vscode.window.createWebviewPanel(
            'ppds.webResources',
            `Web Resources #${this.panelId}`,
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

        // Auto-refresh when a web resource is saved via the FSP
        if (this.fsp) {
            this.disposables.push(
                this.fsp.onDidSaveWebResource(() => {
                    void this.loadWebResources();
                }),
            );
        }
    }

    protected async handleMessage(message: WebResourcesPanelWebviewToHost): Promise<void> {
        switch (message.command) {
            case 'ready':
                await this.initializePanel(this.daemon, this.panelId, WebResourcesPanel.instances.length > 1);
                break;
            case 'requestEnvironmentList':
                await this.handleEnvironmentPickerClick(this.daemon, this.panelId, WebResourcesPanel.instances.length > 1);
                break;
            case 'requestSolutionList':
                await this.loadSolutionList();
                break;
            case 'selectSolution':
                this.solutionId = message.solutionId;
                void this.context.globalState.update('ppds.webResources.solutionId', this.solutionId);
                await this.loadWebResources();
                break;
            case 'toggleTextOnly':
                this.textOnly = message.textOnly;
                void this.context.globalState.update('ppds.webResources.textOnly', this.textOnly);
                await this.loadWebResources();
                break;
            case 'refresh':
                await this.loadWebResources();
                break;
            case 'openWebResource':
                await this.openWebResource(message.id, message.name, message.webResourceType);
                break;
            case 'publishSelected':
                await this.publishSelected(message.ids);
                break;
            case 'publishAll':
                await this.publishAll();
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
        void this.loadWebResources();
    }

    override dispose(): void {
        const idx = WebResourcesPanel.instances.indexOf(this);
        if (idx >= 0) WebResourcesPanel.instances.splice(idx, 1);
        super.dispose();
    }

    protected override async onInitialized(): Promise<void> {
        // Register environment mapping for FSP
        if (this.fsp && this.environmentId && this.environmentUrl) {
            this.fsp.registerEnvironment(this.environmentId, this.environmentUrl);
        }
        await this.loadSolutionList();
        await this.loadWebResources();
    }

    protected override async onEnvironmentChanged(): Promise<void> {
        // Register environment mapping for FSP
        if (this.fsp && this.environmentId && this.environmentUrl) {
            this.fsp.registerEnvironment(this.environmentId, this.environmentUrl);
        }
        // Reset solution filter on environment change
        this.solutionId = null;
        void this.context.globalState.update('ppds.webResources.solutionId', null);
        await this.loadSolutionList();
        await this.loadWebResources();
    }

    private async loadSolutionList(): Promise<void> {
        try {
            const result = await this.daemon.solutionsList(undefined, true, this.environmentUrl);
            this.postMessage({
                command: 'solutionListLoaded',
                solutions: result.solutions.map(s => ({
                    id: s.id,
                    uniqueName: s.uniqueName,
                    friendlyName: s.friendlyName,
                })),
            });
        } catch {
            // Non-critical — solution filter will just be empty
        }
    }

    private async loadWebResources(isRetry = false): Promise<void> {
        try {
            this.postMessage({ command: 'loading' });
            const currentRequestId = ++this.requestId;
            const result = await this.daemon.webResourcesList(
                this.solutionId ?? undefined,
                this.textOnly,
                5000,
                this.environmentUrl,
            );

            this.postMessage({
                command: 'webResourcesLoaded',
                resources: result.resources,
                requestId: currentRequestId,
            });
        } catch (error) {
            const msg = error instanceof Error ? error.message : String(error);

            if (await handleAuthError(this.daemon, error, isRetry, () => this.loadWebResources(true))) {
                return;
            }

            this.postMessage({ command: 'error', message: msg });
        }
    }

    private async openWebResource(id: string, name: string, webResourceType: number): Promise<void> {
        if (this.fsp && this.environmentId && this.environmentUrl) {
            await this.fsp.openWebResource(
                this.environmentId,
                this.environmentUrl,
                id,
                name,
                webResourceType,
            );
        } else if (this.environmentId) {
            // Fallback: open in Maker Portal when FSP is not available
            const baseUrl = buildMakerUrl(this.environmentId);
            const url = `${baseUrl}?app=d365default&forceUCI=1`;
            await vscode.env.openExternal(vscode.Uri.parse(url));
        } else {
            vscode.window.showInformationMessage(
                `Cannot open "${name}" \u2014 environment ID not available.`
            );
        }
    }

    private async publishSelected(ids: string[]): Promise<void> {
        try {
            const result = await this.daemon.webResourcesPublish(ids, this.environmentUrl);
            this.postMessage({
                command: 'publishResult',
                count: result.publishedCount,
            });
        } catch (error) {
            const msg = error instanceof Error ? error.message : String(error);
            this.postMessage({
                command: 'publishResult',
                count: 0,
                error: msg,
            });
        }
    }

    private async publishAll(): Promise<void> {
        try {
            this.postMessage({ command: 'loading' });
            await this.daemon.webResourcesPublishAll(this.environmentUrl);
            await this.loadWebResources();
        } catch (error) {
            const msg = error instanceof Error ? error.message : String(error);
            this.postMessage({ command: 'error', message: `Publish All failed: ${msg}` });
        }
    }

    private async openInMaker(): Promise<void> {
        if (this.environmentId) {
            const url = buildMakerUrl(this.environmentId);
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
            vscode.Uri.joinPath(this.extensionUri, 'dist', 'web-resources-panel.js')
        ).toString();
        const panelCssUri = webview.asWebviewUri(
            vscode.Uri.joinPath(this.extensionUri, 'dist', 'web-resources-panel.css')
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
    <vscode-button id="refresh-btn" appearance="secondary" title="Refresh web resources">Refresh</vscode-button>
    <vscode-button id="publish-btn" appearance="secondary" title="Publish selected web resources" disabled>Publish</vscode-button>
    <vscode-button id="publish-all-btn" appearance="secondary" title="Publish all customizations">Publish All</vscode-button>
    <vscode-button id="maker-btn" appearance="secondary" title="Open Web Resources in Maker Portal">Maker Portal</vscode-button>
    <div class="solution-filter">
        <span class="solution-filter-label">Solution:</span>
        <div id="solution-filter-container"></div>
    </div>
    <label class="text-only-toggle" title="Show only text-based web resources (JS, CSS, HTML, XML)">
        <input type="checkbox" id="text-only-cb" checked>
        Text only
    </label>
    <input type="text" id="wr-search" class="search-input" placeholder="Search web resources..." title="Filter by name" />
    <span class="toolbar-spacer"></span>
    ${getEnvironmentPickerHtml()}
</div>

<div class="content" id="content">
    <div class="empty-state" id="empty-state">Loading web resources...</div>
</div>

<div class="status-bar">
    <span id="status-text">Loading...</span>
</div>

<script nonce="${nonce}" src="${panelJsUri}"></script>
</body>
</html>`;
    }
}
