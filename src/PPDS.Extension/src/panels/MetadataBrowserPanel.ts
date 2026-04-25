import * as vscode from 'vscode';

import type { DaemonClient } from '../daemonClient.js';
import { buildMakerUrl } from '../commands/browserCommands.js';
import { handleAuthError } from '../utils/errorUtils.js';
import { showErrorWithReport } from '../utils/errorNotify.js';

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

    private includeIntersect = false;

    protected async handleMessage(message: MetadataBrowserPanelWebviewToHost): Promise<void> {
        switch (message.command) {
            case 'ready':
                await this.initializePanel(this.daemon, this.panelId, MetadataBrowserPanel.instances.length > 1);
                break;
            case 'requestEnvironmentList':
                await this.handleEnvironmentPickerClick(this.daemon, this.panelId, MetadataBrowserPanel.instances.length > 1);
                break;
            case 'refresh':
                await this.refreshAll();
                break;
            case 'setIncludeIntersect':
                this.includeIntersect = message.includeIntersect;
                await this.loadEntities();
                break;
            case 'selectEntity':
                await this.loadEntityDetail(message.logicalName);
                break;
            case 'selectGlobalChoice':
                await this.loadGlobalChoiceDetail(message.name);
                break;
            case 'openInMaker':
                await this.openInMaker(message.entityLogicalName);
                break;
            case 'createTable':
                await this.handleCreateTable();
                break;
            case 'deleteTable':
                await this.handleDeleteTable(message.entityLogicalName);
                break;
            case 'createColumn':
                await this.handleCreateColumn(message.entityLogicalName);
                break;
            case 'deleteColumn':
                await this.handleDeleteColumn(message.entityLogicalName, message.columnLogicalName);
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
        void this.refreshAll();
    }

    private async refreshAll(): Promise<void> {
        this.postMessage({ command: 'loading' });
        await Promise.all([this.loadEntities(), this.loadGlobalChoices()]);
    }

    override dispose(): void {
        const idx = MetadataBrowserPanel.instances.indexOf(this);
        if (idx >= 0) MetadataBrowserPanel.instances.splice(idx, 1);
        super.dispose();
    }

    protected override async onInitialized(): Promise<void> {
        await this.refreshAll();
    }

    protected override async onEnvironmentChanged(): Promise<void> {
        await this.refreshAll();
    }

    private async loadEntities(isRetry = false): Promise<void> {
        try {
            const result = await this.daemon.metadataEntities(this.environmentUrl, this.includeIntersect, this.profileName);

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
                intersectHiddenCount: result.intersectHiddenCount ?? 0,
            });
        } catch (error) {
            const msg = error instanceof Error ? error.message : String(error);

            if (await handleAuthError(this.daemon, error, isRetry, () => this.loadEntities(true))) {
                return;
            }

            this.postMessage({ command: 'error', message: msg });
        }
    }

    private async loadGlobalChoices(isRetry = false): Promise<void> {
        try {
            const result = await this.daemon.metadataGlobalOptionSets(this.environmentUrl, this.profileName);
            this.postMessage({
                command: 'globalChoicesLoaded',
                choices: result.optionSets,
            });
        } catch (error) {
            if (await handleAuthError(this.daemon, error, isRetry, () => this.loadGlobalChoices(true))) {
                return;
            }

            // Non-fatal — choices section just stays empty
            this.postMessage({ command: 'globalChoicesLoaded', choices: [] });
        }
    }

    private async loadGlobalChoiceDetail(name: string, isRetry = false): Promise<void> {
        try {
            this.postMessage({ command: 'globalChoiceDetailLoading', name });
            const result = await this.daemon.metadataGlobalOptionSet(name, this.environmentUrl, this.profileName);
            this.postMessage({
                command: 'globalChoiceDetailLoaded',
                choice: result.optionSet,
            });
        } catch (error) {
            const msg = error instanceof Error ? error.message : String(error);

            if (await handleAuthError(this.daemon, error, isRetry, () => this.loadGlobalChoiceDetail(name, true))) {
                return;
            }

            this.postMessage({ command: 'error', message: `Failed to load choice detail: ${msg}` });
        }
    }

    private async loadEntityDetail(logicalName: string, isRetry = false): Promise<void> {
        try {
            this.postMessage({ command: 'entityDetailLoading', logicalName });
            const result = await this.daemon.metadataEntity(logicalName, true, this.environmentUrl, this.profileName);
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

    private async handleCreateTable(): Promise<void> {
        const solutionUniqueName = await vscode.window.showInputBox({ title: 'Create Table — Solution', prompt: 'Solution unique name', placeHolder: 'e.g. contoso_core' });
        if (!solutionUniqueName) return;
        const schemaName = await vscode.window.showInputBox({ title: 'Create Table — Schema Name', prompt: 'Table schema name (with publisher prefix)', placeHolder: 'e.g. contoso_widget' });
        if (!schemaName) return;
        const displayName = await vscode.window.showInputBox({ title: 'Create Table — Display Name', prompt: 'Display name', placeHolder: 'e.g. Widget' });
        if (!displayName) return;
        const pluralDisplayName = await vscode.window.showInputBox({ title: 'Create Table — Plural Name', prompt: 'Plural display name', placeHolder: 'e.g. Widgets' });
        if (!pluralDisplayName) return;
        const description = await vscode.window.showInputBox({ title: 'Create Table — Description', prompt: 'Description (optional)', placeHolder: '' }) ?? '';
        const ownershipType = await vscode.window.showQuickPick(['UserOwned', 'OrganizationOwned'], { title: 'Create Table — Ownership', placeHolder: 'Select ownership type' });
        if (!ownershipType) return;

        try {
            const params = { solutionUniqueName, schemaName, displayName, pluralDisplayName, description, ownershipType };
            const result = await this.daemon.metadataCreateTable(params, this.environmentUrl, this.profileName);
            this.postMessage({ command: 'authoringResult', result });
            if (result.success) {
                vscode.window.showInformationMessage(`Table '${result.logicalName ?? schemaName}' created successfully.`);
                await this.refreshAll();
            }
        } catch (error) {
            const msg = error instanceof Error ? error.message : String(error);
            this.postMessage({ command: 'error', message: `Failed to create table: ${msg}` });
        }
    }

    private async handleDeleteTable(entityLogicalName: string): Promise<void> {
        const solutionUniqueName = await vscode.window.showInputBox({ title: 'Delete Table — Solution', prompt: 'Solution unique name', placeHolder: 'e.g. contoso_core' });
        if (!solutionUniqueName) return;

        const confirmation = await vscode.window.showWarningMessage(
            `Are you sure you want to delete table '${entityLogicalName}'? This action cannot be undone.`,
            { modal: true },
            'Delete',
        );
        if (confirmation !== 'Delete') return;

        try {
            const result = await this.daemon.metadataDeleteTable(
                { solutionUniqueName, entityLogicalName },
                this.environmentUrl,
                this.profileName,
            );
            this.postMessage({ command: 'deleteResult', result });
            if (result.success) {
                vscode.window.showInformationMessage(`Table '${entityLogicalName}' deleted successfully.`);
                await this.refreshAll();
            } else {
                void showErrorWithReport(result.error ?? 'Delete failed');
            }
        } catch (error) {
            const msg = error instanceof Error ? error.message : String(error);
            this.postMessage({ command: 'error', message: `Failed to delete table: ${msg}` });
        }
    }

    private async handleCreateColumn(entityLogicalName: string): Promise<void> {
        const solutionUniqueName = await vscode.window.showInputBox({ title: 'Create Column — Solution', prompt: 'Solution unique name', placeHolder: 'e.g. contoso_core' });
        if (!solutionUniqueName) return;
        const columnType = await vscode.window.showQuickPick(
            ['String', 'Memo', 'Integer', 'BigInt', 'Decimal', 'Double', 'Money', 'Boolean', 'DateTime', 'Choice', 'Choices', 'Image', 'File'],
            { title: 'Create Column — Type', placeHolder: 'Select column type' },
        );
        if (!columnType) return;
        const schemaName = await vscode.window.showInputBox({ title: 'Create Column — Schema Name', prompt: 'Column schema name (with publisher prefix)', placeHolder: 'e.g. contoso_widgetcount' });
        if (!schemaName) return;
        const displayName = await vscode.window.showInputBox({ title: 'Create Column — Display Name', prompt: 'Display name', placeHolder: 'e.g. Widget Count' });
        if (!displayName) return;
        const description = await vscode.window.showInputBox({ title: 'Create Column — Description', prompt: 'Description (optional)', placeHolder: '' }) ?? '';

        try {
            const params = { solutionUniqueName, entityLogicalName, schemaName, displayName, description, columnType };
            const result = await this.daemon.metadataCreateColumn(params, this.environmentUrl, this.profileName);
            this.postMessage({ command: 'authoringResult', result });
            if (result.success) {
                vscode.window.showInformationMessage(`Column '${result.logicalName ?? schemaName}' created successfully.`);
                await this.loadEntityDetail(entityLogicalName);
            }
        } catch (error) {
            const msg = error instanceof Error ? error.message : String(error);
            this.postMessage({ command: 'error', message: `Failed to create column: ${msg}` });
        }
    }

    private async handleDeleteColumn(entityLogicalName: string, columnLogicalName: string): Promise<void> {
        const solutionUniqueName = await vscode.window.showInputBox({ title: 'Delete Column — Solution', prompt: 'Solution unique name', placeHolder: 'e.g. contoso_core' });
        if (!solutionUniqueName) return;

        const confirmation = await vscode.window.showWarningMessage(
            `Are you sure you want to delete column '${columnLogicalName}' from '${entityLogicalName}'? This action cannot be undone.`,
            { modal: true },
            'Delete',
        );
        if (confirmation !== 'Delete') return;

        try {
            const result = await this.daemon.metadataDeleteColumn(
                { solutionUniqueName, entityLogicalName, columnLogicalName },
                this.environmentUrl,
                this.profileName,
            );
            this.postMessage({ command: 'deleteResult', result });
            if (result.success) {
                vscode.window.showInformationMessage(`Column '${columnLogicalName}' deleted successfully.`);
                await this.loadEntityDetail(entityLogicalName);
            } else {
                void showErrorWithReport(result.error ?? 'Delete failed');
            }
        } catch (error) {
            const msg = error instanceof Error ? error.message : String(error);
            this.postMessage({ command: 'error', message: `Failed to delete column: ${msg}` });
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
    <vscode-button id="new-table-btn" appearance="secondary" title="Create a new table">New Table</vscode-button>
    <vscode-button id="new-column-btn" appearance="secondary" title="Create a new column on the selected table">New Column</vscode-button>
    <vscode-button id="maker-btn" appearance="secondary" title="Open in Maker Portal">Maker Portal</vscode-button>
    <span class="toolbar-spacer"></span>
    <input type="text" id="entity-search" class="toolbar-search" placeholder="Search entities and choices..." />
    <span id="filter-count"></span>
    <label class="toolbar-checkbox" title="Show only custom entities">
        <input type="checkbox" id="hide-system-toggle" />
        <span>Custom Only</span>
    </label>
    <label class="toolbar-checkbox" title="Include many-to-many intersect entities">
        <input type="checkbox" id="include-intersect-toggle" />
        <span>Include Intersect</span>
    </label>
    ${getEnvironmentPickerHtml()}
</div>

<div class="split-pane">
    <div class="entity-list-pane" id="entity-list-pane">
        <div class="entity-list" id="entity-list"></div>
    </div>
    <div class="entity-detail-pane" id="entity-detail-pane">
        <div id="entity-breadcrumb" class="entity-breadcrumb" style="display:none;"></div>
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
