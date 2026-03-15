import * as vscode from 'vscode';

import type { DaemonClient } from '../daemonClient.js';
import type { SolutionComponentInfoDto } from '../types.js';
import { isAuthError } from '../utils/errorUtils.js';

import { WebviewPanelBase } from './WebviewPanelBase.js';
import { getNonce } from './webviewUtils.js';
import { getEnvironmentPickerHtml, showEnvironmentPicker } from './environmentPicker.js';
import type { SolutionsPanelWebviewToHost, SolutionsPanelHostToWebview, ComponentGroupDto } from './webview/shared/message-types.js';
import { assertNever } from './webview/shared/assert-never.js';

export class SolutionsPanel extends WebviewPanelBase<SolutionsPanelWebviewToHost, SolutionsPanelHostToWebview> {
    private static instances: SolutionsPanel[] = [];
    private static nextId = 1;
    private readonly panelId: number;

    private static readonly MAX_PANELS = 5;

    private environmentUrl: string | undefined;
    private environmentDisplayName: string | undefined;
    private environmentId: string | null = null;
    private profileName: string | undefined;

    /**
     * Returns the number of open SolutionsPanel instances.
     * Used by diagnostic commands for panel state inspection.
     */
    static get instanceCount(): number {
        return SolutionsPanel.instances.length;
    }

    static show(extensionUri: vscode.Uri, daemon: DaemonClient, envUrl?: string, envDisplayName?: string, globalState?: vscode.Memento): SolutionsPanel {
        if (SolutionsPanel.instances.length >= SolutionsPanel.MAX_PANELS) {
            const oldest = SolutionsPanel.instances[0];
            oldest.panel?.reveal();
            return oldest;
        }
        const panel = new SolutionsPanel(extensionUri, daemon, envUrl, envDisplayName, globalState);
        return panel;
    }

    private constructor(
        private readonly extensionUri: vscode.Uri,
        private readonly daemon: DaemonClient,
        initialEnvUrl?: string,
        initialEnvDisplayName?: string,
        private readonly globalState?: vscode.Memento,
    ) {
        super();

        if (initialEnvUrl) {
            this.environmentUrl = initialEnvUrl;
            this.environmentDisplayName = initialEnvDisplayName ?? initialEnvUrl;
        }

        this.panelId = SolutionsPanel.nextId++;
        SolutionsPanel.instances.push(this);

        this.panel = vscode.window.createWebviewPanel(
            'ppds.solutionsPanel',
            `Solutions #${this.panelId}`,
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

        this.panel.webview.html = this.getHtmlContent(this.panel.webview);

        this.disposables.push(
            this.panel.webview.onDidReceiveMessage(
                async (message: SolutionsPanelWebviewToHost) => {
                    switch (message.command) {
                        case 'ready':
                            await this.initialize();
                            break;
                        case 'requestEnvironmentList':
                            await this.handleEnvironmentPicker();
                            break;
                        case 'refresh':
                            await this.loadSolutions();
                            break;
                        case 'expandSolution':
                            await this.loadComponents(message.uniqueName);
                            break;
                        case 'collapseSolution':
                            // No-op on host side; collapse is handled in webview JS
                            break;
                        case 'copyToClipboard':
                            await vscode.env.clipboard.writeText(message.text);
                            break;
                        case 'openInMaker': {
                            if (this.environmentId && message.solutionId) {
                                const url = `https://make.powerapps.com/environments/${this.environmentId}/solutions/${message.solutionId}`;
                                await vscode.env.openExternal(vscode.Uri.parse(url));
                            } else if (this.environmentId) {
                                const url = `https://make.powerapps.com/environments/${this.environmentId}/solutions`;
                                await vscode.env.openExternal(vscode.Uri.parse(url));
                            } else {
                                vscode.window.showInformationMessage('Environment ID not available \u2014 cannot open Maker Portal.');
                            }
                            break;
                        }
                        default:
                            assertNever(message);
                    }
                }
            )
        );

        this.disposables.push(
            this.panel.onDidDispose(() => this.dispose())
        );

        this.subscribeToDaemonReconnect(this.daemon);
    }

    protected override onDaemonReconnected(): void {
        void this.loadSolutions();
    }

    override dispose(): void {
        const idx = SolutionsPanel.instances.indexOf(this);
        if (idx >= 0) SolutionsPanel.instances.splice(idx, 1);
        super.dispose();
    }

    private async initialize(): Promise<void> {
        try {
            // Always fetch profile name for the title
            const who = await this.daemon.authWho();
            this.profileName = who.name ?? `Profile ${who.index}`;
            if (!this.environmentUrl && who.environment?.url) {
                this.environmentUrl = who.environment.url;
                this.environmentDisplayName = who.environment.displayName || who.environment.url;
            }
            if (who.environment?.environmentId) {
                this.environmentId = who.environment.environmentId;
            } else {
                this.environmentId = await this.resolveEnvironmentId();
            }
            this.updatePanelTitle();
            this.postMessage({ command: 'updateEnvironment', name: this.environmentDisplayName ?? 'No environment' });
            await this.loadSolutions();
        } catch (error) {
            const msg = error instanceof Error ? error.message : String(error);
            this.postMessage({ command: 'error', message: `Failed to initialize: ${msg}` });
        }
    }

    private async handleEnvironmentPicker(): Promise<void> {
        const result = await showEnvironmentPicker(this.daemon, this.environmentUrl);
        if (result) {
            this.environmentUrl = result.url;
            this.environmentDisplayName = result.displayName;
            this.environmentId = await this.resolveEnvironmentId();
            this.updatePanelTitle();
            this.postMessage({ command: 'updateEnvironment', name: result.displayName });
            await this.loadSolutions();
        }
    }

    /**
     * Resolves the Power Platform environment ID from the current environment URL
     * by looking it up in the environment list.
     */
    private async resolveEnvironmentId(): Promise<string | null> {
        if (!this.environmentUrl) return null;
        try {
            const normalise = (u: string): string => u.replace(/\/+$/, '').toLowerCase();
            const targetUrl = normalise(this.environmentUrl);
            const envResult = await this.daemon.envList();
            const match = envResult.environments.find(
                e => normalise(e.apiUrl) === targetUrl || (e.url && normalise(e.url) === targetUrl)
            );
            return match?.environmentId ?? null;
        } catch {
            return null;
        }
    }

    private updatePanelTitle(): void {
        if (!this.panel) return;
        const parts = [`Solutions #${this.panelId}`];
        if (this.profileName) parts.push(this.profileName);
        if (this.environmentDisplayName) parts.push(this.environmentDisplayName);
        this.panel.title = parts.join(' \u2014 ');
    }

    private async loadSolutions(isRetry = false): Promise<void> {
        try {
            this.postMessage({ command: 'loading' });
            const allResult = await this.daemon.solutionsList(undefined, true, this.environmentUrl);

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
                })),
            });
        } catch (error) {
            const msg = error instanceof Error ? error.message : String(error);

            if (isAuthError(error) && !isRetry) {
                const action = await vscode.window.showErrorMessage(
                    'Session expired. Re-authenticate?',
                    'Re-authenticate', 'Cancel'
                );
                if (action === 'Re-authenticate') {
                    try {
                        const who = await this.daemon.authWho();
                        const profileId = who.name ?? String(who.index);
                        await this.daemon.profilesInvalidate(profileId);
                    } catch {
                        // If authWho fails, proceed
                    }
                    try {
                        await this.loadSolutions(true);
                        return;
                    } catch {
                        // Fall through to show error
                    }
                }
            }

            this.postMessage({ command: 'error', message: msg });
        }
    }

    private async loadComponents(uniqueName: string): Promise<void> {
        try {
            this.postMessage({ command: 'componentsLoading', uniqueName });
            const result = await this.daemon.solutionsComponents(uniqueName, undefined, this.environmentUrl);

            // Group components by type name (same logic as solutionsTreeView.ts)
            const groupMap = new Map<string, SolutionComponentInfoDto[]>();
            for (const component of result.components) {
                const typeName = component.componentTypeName;
                const group = groupMap.get(typeName);
                if (group) {
                    group.push(component);
                } else {
                    groupMap.set(typeName, [component]);
                }
            }

            // Sort groups by name
            const groups: ComponentGroupDto[] = Array.from(groupMap.entries())
                .sort(([a], [b]) => a.localeCompare(b))
                .map(([typeName, components]) => ({
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
    <input id="search-input" type="text" placeholder="Filter solutions..." style="flex: 1; min-width: 120px; max-width: 300px; padding: 3px 8px; background: var(--vscode-input-background); color: var(--vscode-input-foreground); border: 1px solid var(--vscode-input-border, transparent); border-radius: 2px; font-size: 12px; outline: none;" />
    <span class="toolbar-spacer"></span>
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
