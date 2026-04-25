import * as vscode from 'vscode';

import type { DaemonClient } from '../daemonClient.js';
import { handleAuthError } from '../utils/errorUtils.js';
import { buildMakerUrl } from '../commands/browserCommands.js';
import type { WebResourceFileSystemProvider } from '../providers/WebResourceFileSystemProvider.js';
import type { WebResourceInfoDto } from '../types.js';

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

    /**
     * WR-23: Cache loaded resources keyed by "<solutionId>|<textOnly>|<envUrl>".
     * Invalidated on explicit Refresh or environment change.
     */
    private readonly resourceCache = new Map<string, { resources: WebResourceInfoDto[]; totalCount: number }>();

    /** Batch size for progressive webview delivery (WR-04). */
    private static readonly PAGE_SIZE = 250;

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
                // WR-23: Explicit refresh invalidates the cache for this key
                this.resourceCache.delete(this.cacheKey());
                await this.loadWebResources();
                break;
            case 'serverSearch':
                // WR-03: Server-side search — re-send all cached resources so the webview
                // can filter from the full set (all records are already loaded server-side).
                await this.handleServerSearch(message.term);
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
        this.postMessage({ command: 'filterState', solutionId: this.solutionId, textOnly: this.textOnly });
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
        // WR-23: Environment change invalidates all cached data
        this.resourceCache.clear();
        await this.loadSolutionList();
        this.postMessage({ command: 'filterState', solutionId: this.solutionId, textOnly: this.textOnly });
        await this.loadWebResources();
    }

    /** WR-23: Cache key combining all factors that determine the result set. */
    private cacheKey(): string {
        return `${this.solutionId ?? ''}|${String(this.textOnly)}|${this.environmentUrl ?? ''}`;
    }

    private async loadSolutionList(): Promise<void> {
        try {
            const result = await this.daemon.solutionsList(undefined, true, this.environmentUrl);
            const solutions = result.solutions.map(s => ({
                id: s.id,
                uniqueName: s.uniqueName,
                friendlyName: s.friendlyName,
            }));

            if (this.solutionId && !solutions.some(s => s.id === this.solutionId)) {
                this.solutionId = null;
                void this.context.globalState.update('ppds.webResources.solutionId', null);
            }

            this.postMessage({
                command: 'solutionListLoaded',
                solutions,
            });
        } catch {
            // Non-critical — solution filter will just be empty
        }
    }

    private async loadWebResources(isRetry = false): Promise<void> {
        const key = this.cacheKey();

        // WR-23: Serve from cache when available (skips the RPC call entirely).
        const cached = this.resourceCache.get(key);
        if (cached) {
            const currentRequestId = ++this.requestId;
            await this.sendProgressivePages(cached.resources, currentRequestId, cached.totalCount);
            return;
        }

        try {
            this.postMessage({ command: 'loading' });
            const currentRequestId = ++this.requestId;

            const result = await this.daemon.webResourcesList(
                this.solutionId ?? undefined,
                this.textOnly,
                this.environmentUrl,
            );

            // WR-23: Store in cache before streaming to webview.
            this.resourceCache.set(key, { resources: result.resources, totalCount: result.totalCount });

            // WR-04: Send first page immediately so the UI renders fast,
            // then stream remaining pages in the background.
            await this.sendProgressivePages(result.resources, currentRequestId, result.totalCount);

        } catch (error) {
            const msg = error instanceof Error ? error.message : String(error);

            if (await handleAuthError(this.daemon, error, isRetry, () => this.loadWebResources(true))) {
                return;
            }

            this.postMessage({ command: 'error', message: msg });
        }
    }

    /**
     * WR-04: Streams all resources to the webview in batches of PAGE_SIZE.
     *
     * The first batch is sent as `webResourcesLoaded` (resets the table).
     * Subsequent batches are sent as `webResourcesPage` (appended).
     * A final `webResourcesLoadComplete` is sent when done.
     *
     * Sending in chunks prevents the webview message bridge from blocking
     * on a single large JSON payload (60K records ≈ several MB serialised).
     */
    private async sendProgressivePages(
        resources: WebResourceInfoDto[],
        requestId: number,
        totalCount: number,
    ): Promise<void> {
        const pageSize = WebResourcesPanel.PAGE_SIZE;
        const firstPage = resources.slice(0, pageSize);

        // Send the first page — this replaces any existing table content.
        this.postMessage({
            command: 'webResourcesLoaded',
            resources: firstPage,
            requestId,
            totalCount,
        });

        // Yield to the VS Code event loop between each batch so the UI stays
        // responsive. Use setImmediate-equivalent via Promise microtask.
        for (let offset = pageSize; offset < resources.length; offset += pageSize) {
            // Guard: discard if a newer request has started
            if (requestId !== this.requestId) return;

            const batch = resources.slice(offset, offset + pageSize);
            this.postMessage({
                command: 'webResourcesPage',
                resources: batch,
                requestId,
                loadedSoFar: offset + batch.length,
                totalCount,
            });

            // Yield to the event loop between batches (avoids blocking)
            await new Promise<void>(resolve => setImmediate(resolve));
        }

        if (requestId === this.requestId) {
            this.postMessage({ command: 'webResourcesLoadComplete', requestId, totalCount });
        }
    }

    /**
     * WR-03: Server-side search fallback.
     *
     * Since the service already loads ALL records server-side, "server-side search"
     * simply means: ensure the full dataset is loaded and apply the filter client-side.
     * If the cache is warm (all data loaded), re-send all resources so the webview
     * can re-filter with the new term across the complete set.
     * If the cache is cold (loading still in progress), trigger a fresh load.
     */
    private async handleServerSearch(_term: string): Promise<void> {
        const key = this.cacheKey();
        const cached = this.resourceCache.get(key);
        if (cached) {
            // Cache hit: re-send full dataset so the webview can filter locally.
            const currentRequestId = ++this.requestId;
            await this.sendProgressivePages(cached.resources, currentRequestId, cached.totalCount);
        } else {
            // Cache miss: trigger a full load (which will cache and stream the result).
            await this.loadWebResources();
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
        const confirm = await vscode.window.showWarningMessage(
            'Publish all customizations? This publishes everything, not just web resources.',
            { modal: true },
            'Publish All',
        );
        if (confirm !== 'Publish All') return;

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
    <input type="text" id="wr-search" class="toolbar-search" placeholder="Search web resources..." title="Filter by name" />
    <span class="toolbar-spacer"></span>
    ${getEnvironmentPickerHtml()}
</div>

<div id="server-search-banner" class="server-search-banner" style="display:none;">
    <span class="server-search-banner-text">Search term active while data is still loading.</span>
    <button id="server-search-btn" class="server-search-btn"></button>
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
