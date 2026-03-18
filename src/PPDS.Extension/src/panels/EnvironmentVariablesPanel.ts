import * as vscode from 'vscode';

import type { DaemonClient } from '../daemonClient.js';
import { handleAuthError } from '../utils/errorUtils.js';

import { WebviewPanelBase } from './WebviewPanelBase.js';
import { getNonce } from './webviewUtils.js';
import { getEnvironmentPickerHtml, showEnvironmentPicker } from './environmentPicker.js';
import type { EnvironmentVariablesPanelWebviewToHost, EnvironmentVariablesPanelHostToWebview } from './webview/shared/message-types.js';
import { assertNever } from './webview/shared/assert-never.js';

export class EnvironmentVariablesPanel extends WebviewPanelBase<EnvironmentVariablesPanelWebviewToHost, EnvironmentVariablesPanelHostToWebview> {
    private static instances: EnvironmentVariablesPanel[] = [];
    private static nextId = 1;
    private readonly panelId: number;

    private static readonly MAX_PANELS = 5;

    private environmentUrl: string | undefined;
    private environmentDisplayName: string | undefined;
    private environmentType: string | null = null;
    private environmentColor: string | null = null;
    private environmentId: string | null = null;
    private profileName: string | undefined;
    private solutionFilter: string | null = null;

    /**
     * Returns the number of open EnvironmentVariablesPanel instances.
     * Used by diagnostic commands for panel state inspection.
     */
    static get instanceCount(): number {
        return EnvironmentVariablesPanel.instances.length;
    }

    static show(extensionUri: vscode.Uri, daemon: DaemonClient, envUrl?: string, envDisplayName?: string): EnvironmentVariablesPanel {
        if (EnvironmentVariablesPanel.instances.length >= EnvironmentVariablesPanel.MAX_PANELS) {
            const oldest = EnvironmentVariablesPanel.instances[0];
            oldest.panel?.reveal();
            return oldest;
        }
        const panel = new EnvironmentVariablesPanel(extensionUri, daemon, envUrl, envDisplayName);
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

        this.panelId = EnvironmentVariablesPanel.nextId++;
        EnvironmentVariablesPanel.instances.push(this);

        const panel = vscode.window.createWebviewPanel(
            'ppds.environmentVariables',
            `Environment Variables #${this.panelId}`,
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

    protected async handleMessage(message: EnvironmentVariablesPanelWebviewToHost): Promise<void> {
        switch (message.command) {
            case 'ready':
                await this.initialize();
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
                await this.loadEnvironmentVariables();
                break;
            case 'requestSolutionList':
                await this.loadSolutionList();
                break;
            case 'exportDeploymentSettings':
                await this.exportDeploymentSettings();
                break;
            case 'requestEnvironmentList':
                await this.handleEnvironmentPicker();
                break;
            case 'openInMaker':
                await this.openInMaker();
                break;
            case 'copyToClipboard':
                await vscode.env.clipboard.writeText(message.text);
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

    private async initialize(): Promise<void> {
        try {
            const who = await this.daemon.authWho();
            this.profileName = who.name ?? `Profile ${who.index}`;
            if (!this.environmentUrl && who.environment?.url) {
                this.environmentUrl = who.environment.url;
                this.environmentDisplayName = who.environment.displayName || who.environment.url;
            }
            this.environmentType = who.environment?.type ?? null;
            if (who.environment?.environmentId) {
                this.environmentId = who.environment.environmentId;
            } else {
                this.environmentId = await this.resolveEnvironmentId();
            }
            if (this.environmentUrl) {
                try {
                    const config = await this.daemon.envConfigGet(this.environmentUrl);
                    this.environmentColor = config.resolvedColor ?? null;
                } catch {
                    this.environmentColor = null;
                }
            }
            this.updatePanelTitle();
            this.postMessage({ command: 'updateEnvironment', name: this.environmentDisplayName ?? 'No environment', envType: this.environmentType, envColor: this.environmentColor });
            await this.loadEnvironmentVariables();
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
            this.environmentType = result.type;
            this.environmentId = await this.resolveEnvironmentId();
            try {
                const config = await this.daemon.envConfigGet(result.url);
                this.environmentColor = config.resolvedColor ?? null;
            } catch {
                this.environmentColor = null;
            }
            this.updatePanelTitle();
            this.postMessage({ command: 'updateEnvironment', name: result.displayName, envType: result.type, envColor: this.environmentColor });
            await this.loadEnvironmentVariables();
        }
    }

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
        const context = [this.profileName, this.environmentDisplayName].filter(Boolean).join(' \u00B7 ');
        const suffix = EnvironmentVariablesPanel.instances.length > 1 ? ` ${this.panelId}` : '';
        this.panel.title = context ? `${context} \u2014 Environment Variables${suffix}` : `Environment Variables${suffix}`;
    }

    private async loadEnvironmentVariables(isRetry = false): Promise<void> {
        try {
            this.postMessage({ command: 'loading' });
            const result = await this.daemon.environmentVariablesList(
                this.solutionFilter ?? undefined,
                this.environmentUrl,
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
            const url = `https://make.powerapps.com/environments/${this.environmentId}/solutions/environmentvariables`;
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
    <vscode-button id="maker-btn" appearance="secondary" title="Open Environment Variables in Maker Portal">Maker Portal</vscode-button>
    <div id="solution-filter-container" class="solution-filter-container"></div>
    <span class="toolbar-spacer"></span>
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
