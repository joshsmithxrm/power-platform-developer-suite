import * as vscode from 'vscode';

import type { DaemonClient } from '../daemonClient.js';
import { handleAuthError } from '../utils/errorUtils.js';
import { buildMakerUrl } from '../commands/browserCommands.js';

import { WebviewPanelBase } from './WebviewPanelBase.js';
import { getNonce } from './webviewUtils.js';
import { getEnvironmentPickerHtml } from './environmentPicker.js';
import type { ImportJobsPanelWebviewToHost, ImportJobsPanelHostToWebview } from './webview/shared/message-types.js';
import { assertNever } from './webview/shared/assert-never.js';

export class ImportJobsPanel extends WebviewPanelBase<ImportJobsPanelWebviewToHost, ImportJobsPanelHostToWebview> {
    private static instances: ImportJobsPanel[] = [];
    private static nextId = 1;
    private readonly panelId: number;

    private static readonly MAX_PANELS = 5;

    protected readonly panelLabel = 'Import Jobs';

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

    protected async handleMessage(message: ImportJobsPanelWebviewToHost): Promise<void> {
        switch (message.command) {
            case 'ready':
                await this.initializePanel(this.daemon, this.panelId, ImportJobsPanel.instances.length > 1);
                break;
            case 'requestEnvironmentList':
                await this.handleEnvironmentPickerClick(this.daemon, this.panelId, ImportJobsPanel.instances.length > 1);
                break;
            case 'refresh':
                await this.loadImportJobs();
                break;
            case 'viewImportLog':
                // IJ-02: Open the import log XML in a VS Code editor tab.
                await this.openImportLog(message.id);
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
        void this.loadImportJobs();
    }

    override dispose(): void {
        const idx = ImportJobsPanel.instances.indexOf(this);
        if (idx >= 0) ImportJobsPanel.instances.splice(idx, 1);
        super.dispose();
    }

    protected override async onInitialized(): Promise<void> {
        await this.loadImportJobs();
    }

    protected override async onEnvironmentChanged(): Promise<void> {
        await this.loadImportJobs();
    }

    private async loadImportJobs(isRetry = false): Promise<void> {
        try {
            this.postMessage({ command: 'loading' });
            const result = await this.daemon.importJobsList(undefined, this.environmentUrl);

            this.postMessage({
                command: 'importJobsLoaded',
                // P2-IJ-01: Wire totalCount from the RPC response (ListResult<T> added in Step 1).
                totalCount: result.totalCount,
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
                    operationContext: j.operationContext ?? null,
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

    /**
     * IJ-02: Fetch the raw XML data for an import job and open it in a VS Code
     * editor tab with XML language mode. The XML is prettified before display.
     */
    private async openImportLog(id: string): Promise<void> {
        try {
            const result = await this.daemon.importJobsGet(id, this.environmentUrl);
            const raw = result.job.data;
            if (!raw) {
                void vscode.window.showInformationMessage('No import log data available for this job.');
                return;
            }

            const pretty = prettifyXml(raw);
            const doc = await vscode.workspace.openTextDocument({ content: pretty, language: 'xml' });
            await vscode.window.showTextDocument(doc, vscode.ViewColumn.Beside);
        } catch (error) {
            const msg = error instanceof Error ? error.message : String(error);
            void vscode.window.showErrorMessage(`Failed to open import log: ${msg}`);
        }
    }

    private async openInMaker(): Promise<void> {
        if (this.environmentId) {
            const url = buildMakerUrl(this.environmentId, '/solutionsHistory');
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
    <input id="search-input" type="text" placeholder="Search import jobs..." title="Filter by solution name, status, created by" class="toolbar-search" />
    <span class="toolbar-spacer"></span>
    ${getEnvironmentPickerHtml()}
</div>

<div class="content" id="content">
    <div class="empty-state" id="empty-state">Loading import jobs...</div>
</div>

<div class="status-bar">
    <span id="status-text">Loading...</span>
</div>

<script nonce="${nonce}" src="${panelJsUri}"></script>
</body>
</html>`;
    }
}

/**
 * Prettify XML by adding indentation. Falls back to returning the original
 * string if parsing fails. This is a minimal implementation that handles
 * well-formed XML without external dependencies.
 */
function prettifyXml(xml: string): string {
    try {
        const INDENT = '  ';
        let result = '';
        let depth = 0;
        // Tokenize into tags and text
        const tokens = xml.split(/(<[^>]+>)/);

        for (const token of tokens) {
            if (!token.trim()) continue;

            if (token.startsWith('</')) {
                // Closing tag — decrease depth before printing
                depth = Math.max(0, depth - 1);
                result += INDENT.repeat(depth) + token + '\n';
            } else if (token.startsWith('<?') || token.startsWith('<!')) {
                // Processing instruction / declaration
                result += INDENT.repeat(depth) + token + '\n';
            } else if (token.startsWith('<') && token.endsWith('/>')) {
                // Self-closing tag
                result += INDENT.repeat(depth) + token + '\n';
            } else if (token.startsWith('<')) {
                // Opening tag — print then increase depth
                result += INDENT.repeat(depth) + token + '\n';
                depth++;
            } else {
                // Text content — only output non-whitespace text
                const text = token.trim();
                if (text) {
                    result += INDENT.repeat(depth) + text + '\n';
                }
            }
        }

        return result.trim();
    } catch {
        return xml;
    }
}
