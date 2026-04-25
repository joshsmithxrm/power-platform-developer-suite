import * as vscode from 'vscode';
import { CancellationTokenSource } from 'vscode-jsonrpc/node';

import type { DaemonClient } from '../daemonClient.js';
import { handleAuthError } from '../utils/errorUtils.js';
import { buildMakerUrl } from '../commands/browserCommands.js';

import { WebviewPanelBase } from './WebviewPanelBase.js';
import { getNonce } from './webviewUtils.js';
import { getEnvironmentPickerHtml } from './environmentPicker.js';
import type { EnvironmentVariablesPanelWebviewToHost, EnvironmentVariablesPanelHostToWebview } from './webview/shared/message-types.js';
import { assertNever } from './webview/shared/assert-never.js';

const SYNC_TIMEOUT_MS = 60_000;

export class EnvironmentVariablesPanel extends WebviewPanelBase<EnvironmentVariablesPanelWebviewToHost, EnvironmentVariablesPanelHostToWebview> {
    private static instances: EnvironmentVariablesPanel[] = [];
    private static nextId = 1;
    private readonly panelId: number;

    private static readonly MAX_PANELS = 5;

    protected readonly panelLabel = 'Environment Variables';

    private solutionFilter: string | null = null;
    private includeInactive = false;

    /**
     * Returns the number of open EnvironmentVariablesPanel instances.
     * Used by diagnostic commands for panel state inspection.
     */
    static get instanceCount(): number {
        return EnvironmentVariablesPanel.instances.length;
    }

    static show(extensionUri: vscode.Uri, daemon: DaemonClient, context: vscode.ExtensionContext, envUrl?: string, envDisplayName?: string): EnvironmentVariablesPanel {
        if (EnvironmentVariablesPanel.instances.length >= EnvironmentVariablesPanel.MAX_PANELS) {
            const oldest = EnvironmentVariablesPanel.instances[0];
            oldest.panel?.reveal();
            return oldest;
        }
        const panel = new EnvironmentVariablesPanel(extensionUri, daemon, context, envUrl, envDisplayName);
        return panel;
    }

    private constructor(
        private readonly extensionUri: vscode.Uri,
        private readonly daemon: DaemonClient,
        private readonly context: vscode.ExtensionContext,
        initialEnvUrl?: string,
        initialEnvDisplayName?: string,
    ) {
        super();

        if (initialEnvUrl) {
            this.environmentUrl = initialEnvUrl;
            this.environmentDisplayName = initialEnvDisplayName ?? initialEnvUrl;
        }

        // Restore persisted filter state
        this.solutionFilter = this.context.globalState.get<string | null>('ppds.environmentVariables.solutionFilter', null);

        this.panelId = EnvironmentVariablesPanel.nextId++;
        EnvironmentVariablesPanel.instances.push(this);

        const panel = vscode.window.createWebviewPanel(
            'ppds.environmentVariables',
            `Environment Variables #${this.panelId}`,
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

    protected async handleMessage(message: EnvironmentVariablesPanelWebviewToHost): Promise<void> {
        switch (message.command) {
            case 'ready':
                await this.initializePanel(this.daemon, this.panelId, EnvironmentVariablesPanel.instances.length > 1);
                break;
            case 'refresh':
                await this.loadEnvironmentVariables();
                break;
            case 'selectVariable':
                await this.loadEnvironmentVariableDetail(message.schemaName);
                break;
            case 'editVariable':
                await this.startEditVariable(message.schemaName);
                break;
            case 'saveVariable':
                await this.saveEnvironmentVariable(message.schemaName, message.value);
                break;
            case 'filterBySolution':
                this.solutionFilter = message.solutionId;
                void this.context.globalState.update('ppds.environmentVariables.solutionFilter', this.solutionFilter);
                await this.loadEnvironmentVariables();
                break;
            case 'requestSolutionList':
                await this.loadSolutionList();
                break;
            case 'exportDeploymentSettings':
                await this.exportDeploymentSettings();
                break;
            case 'syncDeploymentSettings':
                await this.syncDeploymentSettings();
                break;
            case 'setIncludeInactive':
                this.includeInactive = message.includeInactive;
                await this.loadEnvironmentVariables();
                break;
            case 'requestEnvironmentList':
                await this.handleEnvironmentPickerClick(this.daemon, this.panelId, EnvironmentVariablesPanel.instances.length > 1);
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
        void this.loadEnvironmentVariables();
    }

    override dispose(): void {
        const idx = EnvironmentVariablesPanel.instances.indexOf(this);
        if (idx >= 0) EnvironmentVariablesPanel.instances.splice(idx, 1);
        super.dispose();
    }

    protected override async onInitialized(): Promise<void> {
        await this.loadEnvironmentVariables();
    }

    protected override async onEnvironmentChanged(): Promise<void> {
        // Reset solution filter on environment change
        this.solutionFilter = null;
        void this.context.globalState.update('ppds.environmentVariables.solutionFilter', null);
        await this.loadEnvironmentVariables();
    }

    private async loadEnvironmentVariables(isRetry = false): Promise<void> {
        try {
            this.postMessage({ command: 'loading' });
            const result = await this.daemon.environmentVariablesList(
                this.solutionFilter ?? undefined,
                this.environmentUrl,
                this.includeInactive,
            );

            this.postMessage({
                command: 'environmentVariablesLoaded',
                variables: result.variables.map(v => ({
                    schemaName: v.schemaName,
                    displayName: v.displayName,
                    type: v.type,
                    defaultValue: v.defaultValue,
                    currentValue: v.currentValue,
                    isManaged: v.isManaged,
                    isRequired: v.isRequired,
                    modifiedOn: v.modifiedOn,
                    hasOverride: v.hasOverride,
                    isMissing: v.isMissing,
                })),
                totalCount: result.totalCount,
                filtersApplied: result.filtersApplied ?? [],
            });
        } catch (error) {
            const msg = error instanceof Error ? error.message : String(error);

            if (await handleAuthError(this.daemon, error, isRetry, () => this.loadEnvironmentVariables(true))) {
                return;
            }

            this.postMessage({ command: 'error', message: msg });
        }
    }

    private async loadEnvironmentVariableDetail(schemaName: string): Promise<void> {
        try {
            const result = await this.daemon.environmentVariablesGet(schemaName, this.environmentUrl);
            const v = result.variable;
            this.postMessage({
                command: 'environmentVariableDetailLoaded',
                detail: {
                    schemaName: v.schemaName,
                    displayName: v.displayName,
                    type: v.type,
                    defaultValue: v.defaultValue,
                    currentValue: v.currentValue,
                    isManaged: v.isManaged,
                    isRequired: v.isRequired,
                    modifiedOn: v.modifiedOn,
                    hasOverride: v.hasOverride,
                    isMissing: v.isMissing,
                    description: v.description,
                    createdOn: v.createdOn,
                },
            });
        } catch (error) {
            const msg = error instanceof Error ? error.message : String(error);
            this.postMessage({ command: 'error', message: `Failed to load detail: ${msg}` });
        }
    }

    private async startEditVariable(schemaName: string): Promise<void> {
        try {
            const result = await this.daemon.environmentVariablesGet(schemaName, this.environmentUrl);
            const v = result.variable;
            this.postMessage({
                command: 'editVariableDialog',
                schemaName: v.schemaName,
                displayName: v.displayName,
                type: v.type,
                currentValue: v.currentValue,
            });
        } catch (error) {
            const msg = error instanceof Error ? error.message : String(error);
            this.postMessage({ command: 'error', message: `Failed to load variable for editing: ${msg}` });
        }
    }

    private async saveEnvironmentVariable(schemaName: string, value: string): Promise<void> {
        try {
            const result = await this.daemon.environmentVariablesSet(schemaName, value, this.environmentUrl);
            this.postMessage({ command: 'variableSaved', schemaName, success: result.success });
            await this.loadEnvironmentVariables();
        } catch (error) {
            const msg = error instanceof Error ? error.message : String(error);
            this.postMessage({ command: 'error', message: `Failed to save variable: ${msg}` });
        }
    }

    private async exportDeploymentSettings(): Promise<void> {
        try {
            const result = await this.daemon.environmentVariablesList(
                this.solutionFilter ?? undefined,
                this.environmentUrl,
            );

            const settings = {
                EnvironmentVariables: result.variables.map(v => ({
                    SchemaName: v.schemaName,
                    Value: v.currentValue ?? v.defaultValue ?? '',
                })),
            };

            const content = JSON.stringify(settings, null, 2);
            const defaultUri = vscode.workspace.workspaceFolders?.[0]?.uri;
            const uri = await vscode.window.showSaveDialog({
                defaultUri: defaultUri ? vscode.Uri.joinPath(defaultUri, 'deployment-settings.json') : undefined,
                filters: { 'JSON Files': ['json'] },
                title: 'Export Deployment Settings',
            });

            if (uri) {
                await vscode.workspace.fs.writeFile(uri, Buffer.from(content, 'utf-8'));
                this.postMessage({ command: 'deploymentSettingsExported', filePath: uri.fsPath });
            }
        } catch (error) {
            const msg = error instanceof Error ? error.message : String(error);
            this.postMessage({ command: 'error', message: `Export failed: ${msg}` });
        }
    }

    private async syncDeploymentSettings(): Promise<void> {
        if (!this.solutionFilter) {
            vscode.window.showWarningMessage('Please select a solution before syncing deployment settings.');
            return;
        }

        const defaultUri = vscode.workspace.workspaceFolders?.[0]?.uri;
        const uri = await vscode.window.showSaveDialog({
            defaultUri: defaultUri ? vscode.Uri.joinPath(defaultUri, 'deployment-settings.json') : undefined,
            filters: { 'JSON Files': ['json'] },
            title: 'Sync Deployment Settings — Save To',
        });

        if (!uri) return;

        const cts = new CancellationTokenSource();
        const timeout = setTimeout(() => cts.cancel(), SYNC_TIMEOUT_MS);

        try {
            const result = await vscode.window.withProgress(
                {
                    location: vscode.ProgressLocation.Notification,
                    title: 'Syncing deployment settings…',
                    cancellable: true,
                },
                async (_progress, progressToken) => {
                    const tokenDisposable = progressToken.onCancellationRequested(() => cts.cancel());
                    try {
                        return await this.daemon.environmentVariablesSyncDeploymentSettings(
                            this.solutionFilter!,
                            uri.fsPath,
                            this.environmentUrl,
                            this.profileName,
                            cts.token,
                        );
                    } finally {
                        tokenDisposable.dispose();
                    }
                },
            );

            this.postMessage({
                command: 'deploymentSettingsSynced',
                filePath: result.filePath,
                envVars: result.environmentVariables,
                connectionRefs: result.connectionReferences,
            });
        } catch (error) {
            if (cts.token.isCancellationRequested) {
                this.postMessage({ command: 'error', message: 'Sync cancelled — the operation timed out or was cancelled by the user.' });
                return;
            }
            const msg = error instanceof Error ? error.message : String(error);
            this.postMessage({ command: 'error', message: `Sync failed: ${msg}` });
        } finally {
            clearTimeout(timeout);
            cts.dispose();
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
            const url = buildMakerUrl(this.environmentId, '/solutions/environmentvariables');
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
            vscode.Uri.joinPath(this.extensionUri, 'dist', 'environment-variables-panel.js')
        ).toString();
        const panelCssUri = webview.asWebviewUri(
            vscode.Uri.joinPath(this.extensionUri, 'dist', 'environment-variables-panel.css')
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
    <vscode-button id="refresh-btn" appearance="secondary" title="Refresh environment variables">Refresh</vscode-button>
    <vscode-button id="export-btn" appearance="secondary" title="Export deployment settings">Export</vscode-button>
    <vscode-button id="sync-btn" appearance="secondary" title="Sync deployment settings">Sync Settings</vscode-button>
    <vscode-button id="maker-btn" appearance="secondary" title="Open Environment Variables in Maker Portal">Maker Portal</vscode-button>
    <span class="toolbar-spacer"></span>
    <input id="search-input" type="text" placeholder="Search variables..." class="toolbar-search" />
    <div id="solution-filter-container" class="solution-filter-container"></div>
    <div class="toolbar-toggle" id="inactive-toggle" title="Active = enabled in Dataverse (statecode 0). Toggle to include deactivated variables.">
        <span id="inactive-toggle-label">Active Only</span>
    </div>
    ${getEnvironmentPickerHtml()}
</div>

<div class="content" id="content">
    <div class="empty-state" id="empty-state">Loading environment variables...</div>
</div>

<div class="detail-pane" id="detail-pane" style="display: none;">
    <div class="detail-header">
        <span id="detail-title">Environment Variable Detail</span>
        <button class="detail-close-btn" id="detail-close" title="Close detail">\u00D7</button>
    </div>
    <div class="detail-content" id="detail-content"></div>
</div>

<div id="edit-dialog" class="edit-dialog-overlay" style="display: none;">
    <div class="edit-dialog">
        <div class="edit-dialog-header">
            <span id="edit-dialog-title">Edit Variable</span>
            <button class="detail-close-btn" id="edit-dialog-close" title="Close">\u00D7</button>
        </div>
        <div class="edit-dialog-body">
            <div id="edit-dialog-input-container"></div>
        </div>
        <div class="edit-dialog-footer">
            <vscode-button id="edit-dialog-save" appearance="primary">Save</vscode-button>
            <vscode-button id="edit-dialog-cancel" appearance="secondary">Cancel</vscode-button>
        </div>
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
