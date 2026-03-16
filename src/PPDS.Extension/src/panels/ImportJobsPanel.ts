import * as vscode from 'vscode';

import type { DaemonClient } from '../daemonClient.js';
import { handleAuthError } from '../utils/errorUtils.js';

import { WebviewPanelBase } from './WebviewPanelBase.js';
import { getNonce } from './webviewUtils.js';
import { getEnvironmentPickerHtml, showEnvironmentPicker } from './environmentPicker.js';
import type { ImportJobsPanelWebviewToHost, ImportJobsPanelHostToWebview } from './webview/shared/message-types.js';
import { assertNever } from './webview/shared/assert-never.js';

export class ImportJobsPanel extends WebviewPanelBase<ImportJobsPanelWebviewToHost, ImportJobsPanelHostToWebview> {
    private static instances: ImportJobsPanel[] = [];
    private static nextId = 1;
    private readonly panelId: number;

    private static readonly MAX_PANELS = 5;

    private environmentUrl: string | undefined;
    private environmentDisplayName: string | undefined;
    private environmentType: string | null = null;
    private environmentColor: string | null = null;
    private environmentId: string | null = null;
    private profileName: string | undefined;

    /**
     * Returns the number of open ImportJobsPanel instances.
     * Used by diagnostic commands for panel state inspection.
     */
    static get instanceCount(): number {
        return ImportJobsPanel.instances.length;
    }

    static show(extensionUri: vscode.Uri, daemon: DaemonClient, envUrl?: string, envDisplayName?: string): ImportJobsPanel {
        if (ImportJobsPanel.instances.length >= ImportJobsPanel.MAX_PANELS) {
            const oldest = ImportJobsPanel.instances[0];
            oldest.panel?.reveal();
            return oldest;
        }
        const panel = new ImportJobsPanel(extensionUri, daemon, envUrl, envDisplayName);
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

        this.panelId = ImportJobsPanel.nextId++;
        ImportJobsPanel.instances.push(this);

        const panel = vscode.window.createWebviewPanel(
            'ppds.importJobs',
            `Import Jobs #${this.panelId}`,
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

    protected async handleMessage(message: ImportJobsPanelWebviewToHost): Promise<void> {
        switch (message.command) {
            case 'ready':
                await this.initialize();
                break;
            case 'requestEnvironmentList':
                await this.handleEnvironmentPicker();
                break;
            case 'refresh':
                await this.loadImportJobs();
                break;
            case 'selectJob':
                await this.loadJobDetail(message.id);
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
        void this.loadImportJobs();
    }

    override dispose(): void {
        const idx = ImportJobsPanel.instances.indexOf(this);
        if (idx >= 0) ImportJobsPanel.instances.splice(idx, 1);
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
            await this.loadImportJobs();
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
            await this.loadImportJobs();
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
        const suffix = ImportJobsPanel.instances.length > 1 ? ` ${this.panelId}` : '';
        this.panel.title = context ? `${context} \u2014 Import Jobs${suffix}` : `Import Jobs${suffix}`;
    }

    private async loadImportJobs(isRetry = false): Promise<void> {
        try {
            this.postMessage({ command: 'loading' });
            const result = await this.daemon.importJobsList(undefined, this.environmentUrl);

            this.postMessage({
                command: 'importJobsLoaded',
                jobs: result.jobs.map(j => ({
                    id: j.id,
                    solutionName: j.solutionName,
                    status: j.status,
                    progress: j.progress,
                    createdBy: j.createdBy,
                    createdOn: j.createdOn,
                    startedOn: j.startedOn,
                    completedOn: j.completedOn,
                    duration: j.duration,
                })),
            });
        } catch (error) {
            const msg = error instanceof Error ? error.message : String(error);

            if (await handleAuthError(this.daemon, error, isRetry, () => this.loadImportJobs(true))) {
                return;
            }

            this.postMessage({ command: 'error', message: msg });
        }
    }

    private async loadJobDetail(id: string): Promise<void> {
        try {
            const result = await this.daemon.importJobsGet(id, this.environmentUrl);
            this.postMessage({
                command: 'importJobDetailLoaded',
                id,
                data: result.job.data,
            });
        } catch (error) {
            const msg = error instanceof Error ? error.message : String(error);
            this.postMessage({ command: 'error', message: `Failed to load job detail: ${msg}` });
        }
    }

    private async openInMaker(): Promise<void> {
        if (this.environmentId) {
            const url = `https://make.powerapps.com/environments/${this.environmentId}/solutionsHistory`;
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
            vscode.Uri.joinPath(this.extensionUri, 'dist', 'import-jobs-panel.js')
        ).toString();
        const panelCssUri = webview.asWebviewUri(
            vscode.Uri.joinPath(this.extensionUri, 'dist', 'import-jobs-panel.css')
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
    <vscode-button id="refresh-btn" appearance="secondary" title="Refresh import jobs">Refresh</vscode-button>
    <vscode-button id="maker-btn" appearance="secondary" title="Open Solution History in Maker Portal">Maker Portal</vscode-button>
    <span class="toolbar-spacer"></span>
    ${getEnvironmentPickerHtml()}
</div>

<div class="content" id="content">
    <div class="empty-state" id="empty-state">Loading import jobs...</div>
</div>

<div class="detail-pane" id="detail-pane" style="display: none;">
    <div class="detail-header">
        <span id="detail-title">Import Job Detail</span>
        <button class="detail-close-btn" id="detail-close" title="Close detail">\u00D7</button>
    </div>
    <pre class="detail-content" id="detail-content"></pre>
</div>

<div class="status-bar">
    <span id="status-text">Loading...</span>
</div>

<script nonce="${nonce}" src="${panelJsUri}"></script>
</body>
</html>`;
    }
}
