import * as vscode from 'vscode';

import type { DaemonClient } from '../daemonClient.js';
import type { SolutionComponentInfoDto } from '../types.js';
import { handleAuthError } from '../utils/errorUtils.js';
import { buildMakerUrl } from '../commands/browserCommands.js';

import { WebviewPanelBase } from './WebviewPanelBase.js';
import { getNonce } from './webviewUtils.js';
import { getEnvironmentPickerHtml } from './environmentPicker.js';
import type { SolutionsPanelWebviewToHost, SolutionsPanelHostToWebview, ComponentGroupDto } from './webview/shared/message-types.js';
import { assertNever } from './webview/shared/assert-never.js';
import { groupComponentsByType } from './webview/shared/group-components.js';

export class SolutionsPanel extends WebviewPanelBase<SolutionsPanelWebviewToHost, SolutionsPanelHostToWebview> {
    private static instances: SolutionsPanel[] = [];
    private static nextId = 1;
    private readonly panelId: number;

    private static readonly MAX_PANELS = 5;

    protected readonly panelLabel = 'Solutions';

    /** Whether to include internal (hidden) solutions. Default false — show only visible solutions. */
    private includeInternal = false;

    /**
     * Returns the number of open SolutionsPanel instances.
     * Used by diagnostic commands for panel state inspection.
     */
    static get instanceCount(): number {
        return SolutionsPanel.instances.length;
    }

    static show(extensionUri: vscode.Uri, daemon: DaemonClient, envUrl?: string, envDisplayName?: string): SolutionsPanel {
        if (SolutionsPanel.instances.length >= SolutionsPanel.MAX_PANELS) {
            const oldest = SolutionsPanel.instances[0];
            oldest.panel?.reveal();
            return oldest;
        }
        const panel = new SolutionsPanel(extensionUri, daemon, envUrl, envDisplayName);
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

        this.panelId = SolutionsPanel.nextId++;
        SolutionsPanel.instances.push(this);

        const panel = vscode.window.createWebviewPanel(
            'ppds.solutionsPanel',
            `Solutions #${this.panelId}`,
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

    protected async handleMessage(message: SolutionsPanelWebviewToHost): Promise<void> {
        switch (message.command) {
            case 'ready':
                await this.initializePanel(this.daemon, this.panelId, SolutionsPanel.instances.length > 1);
                break;
            case 'requestEnvironmentList':
                await this.handleEnvironmentPickerClick(this.daemon, this.panelId, SolutionsPanel.instances.length > 1);
                break;
            case 'refresh':
                await this.loadSolutions();
                break;
            case 'setVisibilityFilter':
                this.includeInternal = message.includeInternal;
                await this.loadSolutions();
                break;
            case 'expandSolution':
                await this.loadComponents(message.uniqueName);
                break;
            case 'collapseSolution':
                // No-op on host side; collapse is handled in webview JS
                break;
            case 'copyToClipboard':
                this.handleCopyToClipboard(message.text);
                break;
            case 'openInMaker': {
                if (this.environmentId) {
                    let url = buildMakerUrl(this.environmentId);
                    if (message.solutionId) {
                        url = `${url}/${message.solutionId}`;
                    }
                    await vscode.env.openExternal(vscode.Uri.parse(url));
                } else {
                    vscode.window.showInformationMessage('Environment ID not available \u2014 cannot open Maker Portal.');
                }
                break;
            }
            case 'webviewError':
                this.logWebviewError(message.error, message.stack);
                break;
            default:
                assertNever(message);
        }
    }

    protected override onDaemonReconnected(): void {
        void this.loadSolutions();
    }

    override dispose(): void {
        const idx = SolutionsPanel.instances.indexOf(this);
        if (idx >= 0) SolutionsPanel.instances.splice(idx, 1);
        super.dispose();
    }

    protected override async onInitialized(): Promise<void> {
        await this.loadSolutions();
    }

    protected override async onEnvironmentChanged(): Promise<void> {
        await this.loadSolutions();
    }

    private async loadSolutions(isRetry = false): Promise<void> {
        try {
            this.postMessage({ command: 'loading' });
            const allResult = await this.daemon.solutionsList(undefined, true, this.environmentUrl, this.includeInternal);

            this.postMessage({
                command: 'solutionsLoaded',
                solutions: allResult.solutions.map(s => ({
                    id: s.id,
                    uniqueName: s.uniqueName,
                    friendlyName: s.friendlyName,
                    version: s.version ?? '',
                    publisherName: s.publisherName ?? '',
                    isManaged: s.isManaged,
                    description: s.description ?? '',
                    createdOn: s.createdOn ?? null,
                    modifiedOn: s.modifiedOn ?? null,
                    installedOn: s.installedOn ?? null,
                    isVisible: s.isVisible ?? true,
                    isApiManaged: s.isApiManaged ?? false,
                })),
                totalCount: allResult.totalCount ?? allResult.solutions.length,
                filtersApplied: allResult.filtersApplied ?? [],
            });
        } catch (error) {
            const msg = error instanceof Error ? error.message : String(error);

            if (await handleAuthError(this.daemon, error, isRetry, () => this.loadSolutions(true))) {
                return;
            }

            this.postMessage({ command: 'error', message: msg });
        }
    }

    private async loadComponents(uniqueName: string): Promise<void> {
        try {
            this.postMessage({ command: 'componentsLoading', uniqueName });
            const result = await this.daemon.solutionsComponents(uniqueName, undefined, this.environmentUrl);

            // Group components by type name using the shared utility
            // (mirrors `PPDS.Cli.Services.SolutionComponentGrouper`).
            const groups: ComponentGroupDto[] = groupComponentsByType<SolutionComponentInfoDto>(result.components)
                .map(({ typeName, components }) => ({
                    typeName,
                    components: components.map(c => ({
                        objectId: c.objectId,
                        isMetadata: c.isMetadata,
                        logicalName: c.logicalName,
                        schemaName: c.schemaName,
                        displayName: c.displayName,
                        rootComponentBehavior: c.rootComponentBehavior,
                    })),
                }));

            this.postMessage({ command: 'componentsLoaded', uniqueName, groups });
        } catch (error) {
            const msg = error instanceof Error ? error.message : String(error);
            this.postMessage({ command: 'error', message: `Failed to load components for ${uniqueName}: ${msg}` });
        }
    }

    getHtmlContent(webview: vscode.Webview): string {
        const toolkitUri = webview.asWebviewUri(
            vscode.Uri.joinPath(this.extensionUri, 'node_modules', '@vscode', 'webview-ui-toolkit', 'dist', 'toolkit.min.js')
        ).toString();
        const solutionsPanelJsUri = webview.asWebviewUri(
            vscode.Uri.joinPath(this.extensionUri, 'dist', 'solutions-panel.js')
        ).toString();
        const solutionsPanelCssUri = webview.asWebviewUri(
            vscode.Uri.joinPath(this.extensionUri, 'dist', 'solutions-panel.css')
        ).toString();
        const nonce = getNonce();

        return `<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="UTF-8">
    <!-- 'unsafe-inline' is required by @vscode/webview-ui-toolkit for dynamic styles -->
    <meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src ${webview.cspSource} 'unsafe-inline'; script-src 'nonce-${nonce}'; font-src ${webview.cspSource};">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <link rel="stylesheet" href="${solutionsPanelCssUri}">
    <script type="module" nonce="${nonce}" src="${toolkitUri}"></script>
</head>
<body>

<div id="reconnect-banner" style="display:none; background: var(--vscode-inputValidation-infoBackground, #063b49); color: var(--vscode-foreground); padding: 6px 12px; text-align: center; font-size: 12px;">
    Connection restored. Data may be stale. <a href="#" id="reconnect-refresh" style="color: var(--vscode-textLink-foreground);">Refresh</a>
</div>

<div class="toolbar">
    <vscode-button id="refresh-btn" appearance="secondary" title="Refresh solutions">Refresh</vscode-button>
    <vscode-button id="maker-portal-btn" appearance="secondary" title="Open in Maker Portal">Maker Portal</vscode-button>
    <span class="toolbar-spacer"></span>
    <input id="search-input" type="text" placeholder="Search solutions..." class="toolbar-search" />
    <div class="segmented-control" id="visibility-filter" title="Show visible solutions only, or all including internal">
        <button class="seg-btn active" data-value="visible">Visible</button>
        <button class="seg-btn" data-value="all">All</button>
    </div>
    <label class="toolbar-checkbox" title="Show managed solutions">
        <input type="checkbox" id="include-managed-cb" checked />
        <span>Include Managed</span>
    </label>
    <select id="sort-select" title="Sort solutions" class="toolbar-select">
        <option value="name">Sort: Name</option>
        <option value="version">Sort: Version</option>
        <option value="publisher">Sort: Publisher</option>
        <option value="modifiedOn">Sort: Modified</option>
    </select>
    ${getEnvironmentPickerHtml()}
</div>

<div class="content" id="content">
    <div class="empty-state" id="empty-state">Loading solutions...</div>
</div>

<div class="status-bar">
    <span id="status-text">Loading...</span>
</div>

<script nonce="${nonce}" src="${solutionsPanelJsUri}"></script>
</body>
</html>`;
    }
}
