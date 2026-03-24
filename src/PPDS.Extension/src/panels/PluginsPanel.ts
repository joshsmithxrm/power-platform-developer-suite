import * as vscode from 'vscode';

import type { DaemonClient, PluginAssemblyInfoDto } from '../daemonClient.js';
import { handleAuthError } from '../utils/errorUtils.js';

import { WebviewPanelBase } from './WebviewPanelBase.js';
import { getNonce } from './webviewUtils.js';
import { getEnvironmentPickerHtml } from './environmentPicker.js';
import type {
    PluginsPanelWebviewToHost,
    PluginsPanelHostToWebview,
    PluginTreeData,
    PluginTreeNode,
    PluginEntityChild,
} from './webview/shared/message-types.js';
import { assertNever } from './webview/shared/assert-never.js';

export class PluginsPanel extends WebviewPanelBase<PluginsPanelWebviewToHost, PluginsPanelHostToWebview> {
    private static instances: PluginsPanel[] = [];
    private static nextId = 1;
    private readonly panelId: number;

    private static readonly MAX_PANELS = 5;

    protected readonly panelLabel = 'Plugin Registration';

    /**
     * Returns the number of open PluginsPanel instances.
     * Used by diagnostic commands for panel state inspection.
     */
    static get instanceCount(): number {
        return PluginsPanel.instances.length;
    }

    static show(extensionUri: vscode.Uri, daemon: DaemonClient, environmentUrl?: string, environmentDisplayName?: string): PluginsPanel {
        if (PluginsPanel.instances.length >= PluginsPanel.MAX_PANELS) {
            const oldest = PluginsPanel.instances[0];
            oldest.panel?.reveal();
            return oldest;
        }
        const panel = new PluginsPanel(extensionUri, daemon, environmentUrl, environmentDisplayName);
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

        this.panelId = PluginsPanel.nextId++;
        PluginsPanel.instances.push(this);

        const panel = vscode.window.createWebviewPanel(
            'ppds.plugins',
            `Plugin Registration #${this.panelId}`,
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

    protected async handleMessage(message: PluginsPanelWebviewToHost): Promise<void> {
        switch (message.command) {
            case 'ready':
                await this.initializePanel(this.daemon, this.panelId, PluginsPanel.instances.length > 1);
                break;
            case 'setViewMode':
                // client-side view mode — no server round-trip needed
                break;
            case 'expandNode':
                await this.loadChildren(message.nodeId, message.nodeType);
                break;
            case 'selectNode':
                await this.loadDetail(message.nodeId, message.nodeType);
                break;
            case 'search':
                // client-side filtering — no server round-trip needed
                break;
            case 'applyFilter':
                // client-side filtering — no server round-trip needed
                break;
            case 'registerEntity':
                await this.handleRegister(message);
                break;
            case 'updateEntity':
                await this.handleUpdate(message);
                break;
            case 'toggleStep':
                await this.handleToggle(message.id, message.enabled);
                break;
            case 'unregister':
                await this.handleUnregister(message);
                break;
            case 'downloadBinary':
                await this.handleDownload(message);
                break;
            case 'requestEnvironmentList':
                await this.handleEnvironmentPickerClick(this.daemon, this.panelId, PluginsPanel.instances.length > 1);
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
        void this.loadTree();
    }

    override dispose(): void {
        const idx = PluginsPanel.instances.indexOf(this);
        if (idx >= 0) PluginsPanel.instances.splice(idx, 1);
        super.dispose();
    }

    protected override async onInitialized(): Promise<void> {
        await this.loadTree();
    }

    protected override async onEnvironmentChanged(): Promise<void> {
        await this.loadTree();
    }

    private async loadTree(isRetry = false): Promise<void> {
        try {
            this.postMessage({ command: 'loading' });

            // plugins/list now returns all domain data in a single consolidated call
            const [pluginsResult, dataProvidersResult] = await Promise.all([
                this.daemon.pluginsList(this.environmentUrl),
                this.daemon.dataProvidersList(undefined, this.environmentUrl),
            ]);

            // Build assemblies tree nodes
            const assemblies: PluginTreeNode[] = pluginsResult.assemblies.map(asm => ({
                id: `assembly:${asm.name}`,
                name: `${asm.name}${asm.version ? ` (${asm.version})` : ''}`,
                nodeType: 'assembly',
                hasChildren: asm.types.length > 0,
                children: asm.types.map(t => ({
                    id: `type:${t.typeName}`,
                    name: t.typeName,
                    nodeType: 'type',
                    hasChildren: t.steps.length > 0,
                    children: t.steps.map(s => ({
                        id: `step:${s.name}`,
                        name: s.name,
                        nodeType: 'step',
                        isEnabled: s.isEnabled,
                        badge: s.isEnabled ? undefined : 'Disabled',
                    })),
                })),
            }));

            // Build packages tree nodes
            const mapAssemblyNode = (asm: PluginAssemblyInfoDto): PluginTreeNode => ({
                id: `assembly:${asm.name}`,
                name: `${asm.name}${asm.version ? ` (${asm.version})` : ''}`,
                nodeType: 'assembly',
                hasChildren: asm.types.length > 0,
                children: asm.types.map(t => ({
                    id: `type:${t.typeName}`,
                    name: t.typeName,
                    nodeType: 'type',
                    hasChildren: t.steps.length > 0,
                    children: t.steps.map(s => ({
                        id: `step:${s.name}`,
                        name: s.name,
                        nodeType: 'step',
                        isEnabled: s.isEnabled,
                        badge: s.isEnabled ? undefined : 'Disabled',
                    })),
                })),
            });

            const packages: PluginTreeNode[] = pluginsResult.packages.map(pkg => ({
                id: `package:${pkg.name}`,
                name: `${pkg.name}${pkg.version ? ` (${pkg.version})` : ''}`,
                nodeType: 'package',
                hasChildren: pkg.assemblies.length > 0,
                children: pkg.assemblies.map(mapAssemblyNode),
            }));

            // Build service endpoints tree nodes (from consolidated plugins/list response)
            const serviceEndpoints: PluginTreeNode[] = pluginsResult.serviceEndpoints.map(ep => ({
                id: `serviceendpoint:${ep.id}`,
                name: ep.name,
                nodeType: 'serviceEndpoint',
                isManaged: ep.isManaged,
                badge: ep.isWebhook ? 'Webhook' : ep.contractType,
            }));

            // Build custom APIs tree nodes (from consolidated plugins/list response)
            const customApis: PluginTreeNode[] = pluginsResult.customApis.map(api => ({
                id: `customapi:${api.id}`,
                name: api.displayName || api.uniqueName,
                nodeType: 'customApi',
                isManaged: api.isManaged,
                badge: api.isFunction ? 'Function' : undefined,
                hasChildren: (api.requestParameters.length + api.responseProperties.length) > 0,
            }));

            // Build data sources tree nodes (from consolidated plugins/list response, with data providers as children)
            const dataSources: PluginTreeNode[] = pluginsResult.dataSources.map(ds => {
                const providers = dataProvidersResult.providers.filter(p => p.dataSourceId === ds.id);
                return {
                    id: `datasource:${ds.id}`,
                    name: ds.displayName || ds.name,
                    nodeType: 'dataSource',
                    isManaged: ds.isManaged,
                    hasChildren: providers.length > 0,
                    children: providers.map(p => ({
                        id: `dataprovider:${p.id}`,
                        name: p.name,
                        nodeType: 'dataProvider',
                        isManaged: p.isManaged,
                    })),
                };
            });

            // Add orphaned data providers (no matching data source)
            const orphanedProviders = dataProvidersResult.providers.filter(
                p => !p.dataSourceId || !pluginsResult.dataSources.some(ds => ds.id === p.dataSourceId)
            );
            for (const p of orphanedProviders) {
                dataSources.push({
                    id: `dataprovider:${p.id}`,
                    name: p.name,
                    nodeType: 'dataProvider',
                    isManaged: p.isManaged,
                });
            }

            const data: PluginTreeData = {
                assemblies,
                packages,
                serviceEndpoints,
                customApis,
                dataSources,
            };

            this.postMessage({ command: 'treeLoaded', data });
        } catch (error) {
            const msg = error instanceof Error ? error.message : String(error);

            if (await handleAuthError(this.daemon, error, isRetry, () => this.loadTree(true))) {
                return;
            }

            this.postMessage({ command: 'error', message: msg });
        }
    }

    private async loadChildren(nodeId: string, nodeType: string): Promise<void> {
        try {
            // Extract type and id from composite nodeId (e.g. "assembly:some-name" → type="assembly", id="some-name")
            const colonIdx = nodeId.indexOf(':');
            const id = colonIdx >= 0 ? nodeId.slice(colonIdx + 1) : nodeId;

            const result = await this.daemon.pluginsGet(nodeType, id, this.environmentUrl);
            const entity = result.entity;

            // Build child nodes from entity data
            const children: PluginTreeNode[] = [];
            const childArray = entity['children'];
            if (Array.isArray(childArray)) {
                for (const child of childArray) {
                    if (child && typeof child === 'object') {
                        const c = child as PluginEntityChild;
                        children.push({
                            id: String(c.id ?? ''),
                            name: String(c.name ?? c.typeName ?? c.id ?? ''),
                            nodeType: String(c.nodeType ?? nodeType),
                            isEnabled: typeof c.isEnabled === 'boolean' ? c.isEnabled : undefined,
                            isManaged: typeof c.isManaged === 'boolean' ? c.isManaged : undefined,
                            hasChildren: typeof c.hasChildren === 'boolean' ? c.hasChildren : false,
                        });
                    }
                }
            }

            this.postMessage({ command: 'childrenLoaded', parentId: nodeId, children });
        } catch (error) {
            const msg = error instanceof Error ? error.message : String(error);
            this.postMessage({ command: 'error', message: `Failed to load children: ${msg}` });
        }
    }

    private async loadDetail(nodeId: string, nodeType: string): Promise<void> {
        try {
            const colonIdx = nodeId.indexOf(':');
            const id = colonIdx >= 0 ? nodeId.slice(colonIdx + 1) : nodeId;

            const result = await this.daemon.pluginsGet(nodeType, id, this.environmentUrl);
            this.postMessage({ command: 'detailLoaded', detail: result.entity });
        } catch (error) {
            const msg = error instanceof Error ? error.message : String(error);
            this.postMessage({ command: 'error', message: `Failed to load detail: ${msg}` });
        }
    }

    private async handleToggle(id: string, enabled: boolean): Promise<void> {
        try {
            await vscode.window.withProgress(
                { location: vscode.ProgressLocation.Notification, title: `${enabled ? 'Enabling' : 'Disabling'} step...` },
                async () => {
                    await this.daemon.pluginsToggleStep(id, enabled, this.environmentUrl);
                }
            );

            // Reload detail for the updated node to get fresh state
            const result = await this.daemon.pluginsGet('step', id, this.environmentUrl);
            const entity = result.entity;
            this.postMessage({
                command: 'nodeUpdated',
                node: {
                    id: `step:${id}`,
                    name: String(entity['name'] ?? id),
                    nodeType: 'step',
                    isEnabled: enabled,
                },
            });
        } catch (error) {
            const msg = error instanceof Error ? error.message : String(error);
            this.postMessage({ command: 'error', message: `Failed to toggle step: ${msg}` });
        }
    }

    private async handleUnregister(message: Extract<PluginsPanelWebviewToHost, { command: 'unregister' }>): Promise<void> {
        const { entityType, id, force } = message;

        const confirm = await vscode.window.showWarningMessage(
            `Unregister ${entityType} '${id}'? This cannot be undone.`,
            { modal: true },
            'Unregister',
        );
        if (confirm !== 'Unregister') return;

        try {
            await vscode.window.withProgress(
                { location: vscode.ProgressLocation.Notification, title: `Unregistering ${entityType}...` },
                async () => {
                    await this.daemon.pluginsUnregister(entityType, id, force, this.environmentUrl);
                }
            );
            this.postMessage({ command: 'nodeRemoved', nodeId: `${entityType}:${id}` });
        } catch (error) {
            const msg = error instanceof Error ? error.message : String(error);
            this.postMessage({ command: 'error', message: `Failed to unregister: ${msg}` });
        }
    }

    private async handleDownload(message: Extract<PluginsPanelWebviewToHost, { command: 'downloadBinary' }>): Promise<void> {
        const { entityType, id } = message;

        try {
            const result = await vscode.window.withProgress(
                { location: vscode.ProgressLocation.Notification, title: 'Downloading binary...' },
                async () => this.daemon.pluginsDownloadBinary(entityType, id, this.environmentUrl)
            );

            const uri = await vscode.window.showSaveDialog({
                defaultUri: vscode.Uri.file(result.fileName),
                filters: { 'DLL Files': ['dll'], 'All Files': ['*'] },
            });
            if (!uri) return;

            const bytes = Buffer.from(result.content, 'base64');
            await vscode.workspace.fs.writeFile(uri, bytes);
            vscode.window.showInformationMessage(`Downloaded to ${uri.fsPath}`);
        } catch (error) {
            const msg = error instanceof Error ? error.message : String(error);
            this.postMessage({ command: 'error', message: `Failed to download binary: ${msg}` });
        }
    }

    private async handleRegister(message: Extract<PluginsPanelWebviewToHost, { command: 'registerEntity' }>): Promise<void> {
        const { entityType, fields } = message;

        try {
            await vscode.window.withProgress(
                { location: vscode.ProgressLocation.Notification, title: `Registering ${entityType}...` },
                async () => {
                    switch (entityType) {
                        case 'assembly':
                            await this.daemon.pluginsRegisterAssembly(
                                String(fields['content'] ?? ''),
                                fields['solutionName'] !== undefined ? String(fields['solutionName']) : undefined,
                                this.environmentUrl,
                            );
                            break;
                        case 'package':
                            await this.daemon.pluginsRegisterPackage(
                                String(fields['content'] ?? ''),
                                fields['solutionName'] !== undefined ? String(fields['solutionName']) : undefined,
                                this.environmentUrl,
                            );
                            break;
                        case 'step':
                            await this.daemon.pluginsRegisterStep(fields, this.environmentUrl);
                            break;
                        case 'image':
                            await this.daemon.pluginsRegisterImage(fields, this.environmentUrl);
                            break;
                        case 'serviceEndpoint':
                            await this.daemon.serviceEndpointsRegister(fields, this.environmentUrl);
                            break;
                        case 'customApi':
                            await this.daemon.customApisRegister(fields, this.environmentUrl);
                            break;
                        default:
                            throw new Error(`Unknown entity type for registration: ${entityType}`);
                    }
                }
            );

            // Reload tree to reflect new registration
            await this.loadTree();
        } catch (error) {
            const msg = error instanceof Error ? error.message : String(error);
            this.postMessage({ command: 'error', message: `Failed to register ${entityType}: ${msg}` });
        }
    }

    private async handleUpdate(message: Extract<PluginsPanelWebviewToHost, { command: 'updateEntity' }>): Promise<void> {
        const { entityType, id, fields } = message;

        try {
            await vscode.window.withProgress(
                { location: vscode.ProgressLocation.Notification, title: `Updating ${entityType}...` },
                async () => {
                    switch (entityType) {
                        case 'assembly':
                            await this.daemon.pluginsRegisterAssembly(
                                String(fields['content'] ?? ''),
                                fields['solutionName'] !== undefined ? String(fields['solutionName']) : undefined,
                                this.environmentUrl,
                            );
                            break;
                        case 'step':
                            await this.daemon.pluginsUpdateStep(id, fields, this.environmentUrl);
                            break;
                        case 'image':
                            await this.daemon.pluginsUpdateImage(id, fields, this.environmentUrl);
                            break;
                        case 'serviceEndpoint':
                            await this.daemon.serviceEndpointsUpdate(id, fields, this.environmentUrl);
                            break;
                        case 'customApi':
                            await this.daemon.customApisUpdate(id, fields, this.environmentUrl);
                            break;
                        default:
                            throw new Error(`Unknown entity type for update: ${entityType}`);
                    }
                }
            );

            // Reload detail for the updated node
            const result = await this.daemon.pluginsGet(entityType, id, this.environmentUrl);
            this.postMessage({
                command: 'nodeUpdated',
                node: {
                    id: `${entityType}:${id}`,
                    name: String(result.entity['name'] ?? id),
                    nodeType: entityType,
                },
            });
        } catch (error) {
            const msg = error instanceof Error ? error.message : String(error);
            this.postMessage({ command: 'error', message: `Failed to update ${entityType}: ${msg}` });
        }
    }

    getHtmlContent(webview: vscode.Webview): string {
        const toolkitUri = webview.asWebviewUri(
            vscode.Uri.joinPath(this.extensionUri, 'node_modules', '@vscode', 'webview-ui-toolkit', 'dist', 'toolkit.min.js')
        ).toString();
        const panelJsUri = webview.asWebviewUri(
            vscode.Uri.joinPath(this.extensionUri, 'dist', 'plugins-panel.js')
        ).toString();
        const panelCssUri = webview.asWebviewUri(
            vscode.Uri.joinPath(this.extensionUri, 'dist', 'plugins-panel.css')
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
    <vscode-button id="refresh-btn" appearance="secondary" title="Refresh plugin registrations">Refresh</vscode-button>
    <div class="view-mode-group">
        <vscode-button id="view-mode-assembly" appearance="secondary" title="View by assembly">Assembly</vscode-button>
        <vscode-button id="view-mode-message" appearance="secondary" title="View by message">Message</vscode-button>
        <vscode-button id="view-mode-entity" appearance="secondary" title="View by entity">Entity</vscode-button>
    </div>
    <vscode-button id="expand-all-btn" appearance="secondary" title="Expand all nodes">Expand All</vscode-button>
    <vscode-button id="collapse-all-btn" appearance="secondary" title="Collapse all nodes">Collapse All</vscode-button>
    <vscode-button id="register-btn" appearance="secondary" title="Register new plugin assembly or step">Register</vscode-button>
    <span class="toolbar-spacer"></span>
    <input type="text" id="search-input" class="toolbar-search" placeholder="Search registrations..." title="Filter by name, message, or entity" />
    <label class="filter-checkbox" title="Hide disabled steps">
        <input type="checkbox" id="hide-hidden-check" /> Hide Disabled
    </label>
    <label class="filter-checkbox" title="Hide Microsoft-managed assemblies">
        <input type="checkbox" id="hide-microsoft-check" /> Hide Microsoft
    </label>
    ${getEnvironmentPickerHtml()}
</div>

<div class="panel-content">
    <div id="tree-container" class="tree-pane">
        <div class="empty-state" id="empty-state">Loading plugin registrations...</div>
    </div>
    <div id="resize-handle" class="resize-handle"></div>
    <div id="detail-panel" class="detail-pane">
        <div class="detail-tabs-bar">
            <button class="detail-tab active" data-tab="details">Details</button>
            <button class="detail-tab" data-tab="steps">Steps</button>
            <button class="detail-tab" data-tab="images">Images</button>
        </div>
        <div class="detail-tab-content" id="tab-details"></div>
        <div class="detail-tab-content" id="tab-steps"></div>
        <div class="detail-tab-content" id="tab-images"></div>
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
