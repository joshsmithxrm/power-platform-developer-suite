import * as vscode from 'vscode';
import { WebviewPanelBase } from './WebviewPanelBase.js';
import { getNonce } from './webviewUtils.js';
import { getEnvironmentPickerCss, getEnvironmentPickerHtml, getEnvironmentPickerJs, showEnvironmentPicker } from './environmentPicker.js';
import type { DaemonClient } from '../daemonClient.js';
import type { SolutionComponentInfoDto } from '../types.js';
import { isAuthError } from '../utils/errorUtils.js';

interface ComponentGroup {
    typeName: string;
    components: {
        objectId: string;
        isMetadata: boolean;
        logicalName?: string;
        schemaName?: string;
        displayName?: string;
        rootComponentBehavior: number;
    }[];
}

export class SolutionsPanel extends WebviewPanelBase {
    private static instances: SolutionsPanel[] = [];
    private static nextId = 1;
    private readonly panelId: number;

    private static readonly MAX_PANELS = 5;

    private includeManaged = false;
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
        // Restore managed toggle state
        this.includeManaged = this.globalState?.get<boolean>('ppds.solutionsPanel.includeManaged') ?? false;

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
                localResourceRoots: [vscode.Uri.joinPath(extensionUri, 'node_modules')],
            }
        );

        this.panel.webview.html = this.getHtmlContent(this.panel.webview);

        this.disposables.push(
            this.panel.webview.onDidReceiveMessage(
                async (message: { command: string; [key: string]: unknown }) => {
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
                        case 'toggleManaged':
                            this.includeManaged = !this.includeManaged;
                            void this.globalState?.update('ppds.solutionsPanel.includeManaged', this.includeManaged);
                            await this.loadSolutions();
                            break;
                        case 'expandSolution':
                            await this.loadComponents(message.uniqueName as string);
                            break;
                        case 'collapseSolution':
                            // No-op on host side; collapse is handled in webview JS
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
            this.postMessage({ command: 'updateManagedState', includeManaged: this.includeManaged });
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
            const normalise = (u: string) => u.replace(/\/+$/, '').toLowerCase();
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
            const result = await this.daemon.solutionsList(undefined, this.includeManaged, this.environmentUrl);

            // Count managed solutions that are hidden (when includeManaged is false)
            let managedCount = 0;
            if (!this.includeManaged) {
                // We need to query again with includeManaged=true to get the count,
                // but that's expensive. Instead, just report what we know.
                // The managed count is the difference between total and unmanaged.
                // Since the API already filters, we can make a separate count call.
                try {
                    const allResult = await this.daemon.solutionsList(undefined, true, this.environmentUrl);
                    managedCount = allResult.solutions.filter(s => s.isManaged).length;
                } catch {
                    // If the count call fails, just use 0
                    managedCount = 0;
                }
            }

            this.postMessage({
                command: 'solutionsLoaded',
                solutions: result.solutions.map(s => ({
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
                managedCount,
                includeManaged: this.includeManaged,
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
            const groups: ComponentGroup[] = Array.from(groupMap.entries())
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
        );
        const nonce = getNonce();

        return `<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="UTF-8">
    <!-- 'unsafe-inline' is required by @vscode/webview-ui-toolkit for dynamic styles -->
    <meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src ${webview.cspSource} 'unsafe-inline'; script-src 'nonce-${nonce}'; font-src ${webview.cspSource};">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <script type="module" nonce="${nonce}" src="${toolkitUri}"></script>
</head>
<body>
<style>
    body { margin: 0; padding: 0; display: flex; flex-direction: column; height: 100vh; font-family: var(--vscode-font-family); color: var(--vscode-foreground); background: var(--vscode-editor-background); }

    .toolbar { display: flex; gap: 8px; padding: 8px 12px; border-bottom: 1px solid var(--vscode-panel-border); flex-shrink: 0; align-items: center; }
    .toolbar-spacer { flex: 1; }

    ${getEnvironmentPickerCss()}

    .content { flex: 1; overflow: auto; }

    .solution-list { list-style: none; margin: 0; padding: 0; }

    .solution-row {
        display: flex; align-items: center; gap: 6px;
        padding: 6px 12px; cursor: pointer; user-select: none;
        border-bottom: 1px solid var(--vscode-panel-border);
    }
    .solution-row:hover { background: var(--vscode-list-hoverBackground); }
    .solution-row .chevron { width: 16px; text-align: center; flex-shrink: 0; font-size: 10px; transition: transform 0.15s; }
    .solution-row .chevron.expanded { transform: rotate(90deg); }
    .solution-row .icon { flex-shrink: 0; font-size: 14px; }
    .solution-row .name { font-weight: 500; }
    .solution-row .version { color: var(--vscode-descriptionForeground); font-size: 12px; }
    .solution-row .publisher { color: var(--vscode-descriptionForeground); font-size: 12px; margin-left: 4px; }
    .solution-row .managed-badge { font-size: 10px; padding: 1px 4px; border-radius: 2px; background: var(--vscode-badge-background); color: var(--vscode-badge-foreground); }

    .open-maker-btn {
        background: none;
        border: none;
        cursor: pointer;
        opacity: 0.5;
        padding: 2px 4px;
        font-size: 12px;
        color: var(--vscode-foreground);
    }
    .open-maker-btn:hover { opacity: 1; }

    .components-container { display: none; padding-left: 22px; border-bottom: 1px solid var(--vscode-panel-border); }
    .components-container.expanded { display: block; }

    .component-group {
        padding: 4px 12px 4px 16px; cursor: pointer; user-select: none;
        display: flex; align-items: center; gap: 6px;
    }
    .component-group:hover { background: var(--vscode-list-hoverBackground); }
    .component-group .chevron { width: 16px; text-align: center; flex-shrink: 0; font-size: 10px; transition: transform 0.15s; }
    .component-group .chevron.expanded { transform: rotate(90deg); }
    .component-group .group-name { font-size: 13px; }
    .component-group .group-count { color: var(--vscode-descriptionForeground); font-size: 12px; }

    .component-items { display: none; padding-left: 22px; }
    .component-items.expanded { display: block; }

    .component-item {
        padding: 3px 12px 3px 16px; font-size: 12px;
        color: var(--vscode-foreground);
        display: flex; align-items: center; gap: 6px;
    }
    .component-item:hover { background: var(--vscode-list-hoverBackground); }
    .component-item .metadata-badge { font-size: 10px; color: var(--vscode-descriptionForeground); }

    .component-detail-card {
        display: none;
        margin: 0 12px 4px 28px;
        padding: 6px 10px;
        background: var(--vscode-textBlockQuote-background);
        border-left: 3px solid var(--vscode-textBlockQuote-border);
        border-radius: 2px;
        font-size: 12px;
    }
    .component-detail-card.expanded {
        display: grid;
        grid-template-columns: auto 1fr;
        gap: 2px 12px;
    }
    .component-detail-card .detail-label { color: var(--vscode-descriptionForeground); white-space: nowrap; }
    .component-detail-card .detail-value { overflow: hidden; text-overflow: ellipsis; }
    .component-item { cursor: pointer; }
    .component-item:focus { outline: 1px solid var(--vscode-focusBorder); outline-offset: -1px; }
    .copy-btn {
        background: none;
        border: none;
        cursor: pointer;
        padding: 0 2px;
        color: var(--vscode-descriptionForeground);
        font-size: 11px;
    }
    .copy-btn:hover { color: var(--vscode-textLink-foreground); }

    .status-bar { display: flex; gap: 16px; padding: 4px 12px; border-top: 1px solid var(--vscode-panel-border); font-size: 12px; color: var(--vscode-descriptionForeground); flex-shrink: 0; }

    .empty-state { padding: 40px; text-align: center; color: var(--vscode-descriptionForeground); font-style: italic; }
    .error-state { padding: 12px; background: var(--vscode-inputValidation-errorBackground, rgba(255,0,0,0.1)); border: 1px solid var(--vscode-inputValidation-errorBorder, red); border-radius: 4px; margin: 8px 12px; color: var(--vscode-errorForeground); }

    .spinner { display: inline-block; width: 16px; height: 16px; border: 2px solid var(--vscode-descriptionForeground); border-top-color: transparent; border-radius: 50%; animation: spin 0.8s linear infinite; }
    @keyframes spin { to { transform: rotate(360deg); } }

    .loading-state { padding: 40px; text-align: center; color: var(--vscode-descriptionForeground); }
    .loading-state .spinner { width: 24px; height: 24px; margin-bottom: 12px; }

    .components-loading { padding: 8px 16px; color: var(--vscode-descriptionForeground); display: flex; align-items: center; gap: 8px; }

    .detail-card {
        margin: 8px 12px; padding: 8px 12px;
        background: var(--vscode-textBlockQuote-background);
        border-left: 3px solid var(--vscode-textBlockQuote-border);
        border-radius: 2px; font-size: 12px;
        display: grid; grid-template-columns: auto 1fr; gap: 2px 12px;
    }
    .detail-card .detail-label { color: var(--vscode-descriptionForeground); white-space: nowrap; }
    .detail-card .detail-value { overflow: hidden; text-overflow: ellipsis; }
    .detail-card .detail-description {
        grid-column: 1 / -1; margin-top: 4px; padding-top: 4px;
        border-top: 1px solid var(--vscode-panel-border);
        display: -webkit-box; -webkit-line-clamp: 3; -webkit-box-orient: vertical; overflow: hidden;
    }
</style>

<div id="reconnect-banner" style="display:none; background: var(--vscode-inputValidation-infoBackground, #063b49); color: var(--vscode-foreground); padding: 6px 12px; text-align: center; font-size: 12px;">
    Connection restored. Data may be stale. <a href="#" id="reconnect-refresh" style="color: var(--vscode-textLink-foreground);">Refresh</a>
</div>

<div class="toolbar">
    <vscode-button id="refresh-btn" appearance="secondary" title="Refresh solutions">Refresh</vscode-button>
    <vscode-button id="managed-btn" appearance="secondary" title="Toggle managed solutions visibility">Managed: Off</vscode-button>
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

<script nonce="${nonce}">
(function() {
    const vscode = acquireVsCodeApi();
    const content = document.getElementById('content');
    const statusText = document.getElementById('status-text');
    const refreshBtn = document.getElementById('refresh-btn');
    const managedBtn = document.getElementById('managed-btn');
    const searchInput = document.getElementById('search-input');

    let solutions = [];
    let expandedSolutions = new Set();
    let expandedGroups = new Set();
    let managedOn = false;

    ${getEnvironmentPickerJs()}

    // ── Button handlers ──
    refreshBtn.addEventListener('click', () => {
        vscode.postMessage({ command: 'refresh' });
    });

    managedBtn.addEventListener('click', () => {
        managedOn = !managedOn;
        managedBtn.textContent = managedOn ? 'Managed: On' : 'Managed: Off';
        managedBtn.setAttribute('appearance', managedOn ? 'primary' : 'secondary');
        vscode.postMessage({ command: 'toggleManaged' });
    });

    document.getElementById('reconnect-refresh').addEventListener('click', function(e) {
        e.preventDefault();
        document.getElementById('reconnect-banner').style.display = 'none';
        vscode.postMessage({ command: 'refresh' });
    });

    let filterText = '';
    let filterTimeout = null;

    searchInput.addEventListener('input', () => {
        clearTimeout(filterTimeout);
        filterTimeout = setTimeout(() => {
            filterText = searchInput.value.trim().toLowerCase();
            applyFilter();
        }, 150);
    });

    function applyFilter() {
        if (!solutions.length) return;
        const rows = content.querySelectorAll('.solution-list > li');
        let visibleCount = 0;
        rows.forEach((li, idx) => {
            const sol = solutions[idx];
            if (!sol) return;
            const matches = !filterText ||
                sol.friendlyName.toLowerCase().includes(filterText) ||
                sol.uniqueName.toLowerCase().includes(filterText) ||
                (sol.publisherName && sol.publisherName.toLowerCase().includes(filterText));
            li.style.display = matches ? '' : 'none';
            if (matches) visibleCount++;
        });

        if (filterText) {
            statusText.textContent = visibleCount + ' of ' + solutions.length + ' solution' + (solutions.length !== 1 ? 's' : '');
        } else {
            let statusMsg = solutions.length + ' solution' + (solutions.length !== 1 ? 's' : '');
            statusText.textContent = statusMsg;
        }

        if (filterText && visibleCount === 0) {
            let emptyEl = content.querySelector('.filter-empty');
            if (!emptyEl) {
                emptyEl = document.createElement('div');
                emptyEl.className = 'empty-state filter-empty';
                emptyEl.textContent = 'No solutions match filter';
                content.appendChild(emptyEl);
            }
        } else {
            const emptyEl = content.querySelector('.filter-empty');
            if (emptyEl) emptyEl.remove();
        }
    }

    // ── Delegated click handler for solution/component rows ──
    content.addEventListener('click', (e) => {
        var makerBtn = e.target.closest('.open-maker-btn');
        if (makerBtn) {
            var solutionId = makerBtn.dataset.solutionId;
            vscode.postMessage({ command: 'openInMaker', solutionId: solutionId });
            e.stopPropagation();
            return;
        }

        const solutionRow = e.target.closest('.solution-row');
        if (solutionRow) {
            const uniqueName = solutionRow.dataset.uniqueName;
            if (expandedSolutions.has(uniqueName)) {
                expandedSolutions.delete(uniqueName);
                vscode.postMessage({ command: 'collapseSolution', uniqueName });
                updateSolutionExpansion(uniqueName, false);
            } else {
                expandedSolutions.add(uniqueName);
                vscode.postMessage({ command: 'expandSolution', uniqueName });
                updateSolutionExpansion(uniqueName, true);
            }
            return;
        }

        const groupRow = e.target.closest('.component-group');
        if (groupRow) {
            const groupKey = groupRow.dataset.groupKey;
            const itemsEl = groupRow.nextElementSibling;
            const chevron = groupRow.querySelector('.chevron');
            if (expandedGroups.has(groupKey)) {
                expandedGroups.delete(groupKey);
                if (itemsEl) itemsEl.classList.remove('expanded');
                if (chevron) chevron.classList.remove('expanded');
            } else {
                expandedGroups.add(groupKey);
                if (itemsEl) itemsEl.classList.add('expanded');
                if (chevron) chevron.classList.add('expanded');
            }
        }
    });

    // Component item click → toggle detail card
    content.addEventListener('click', function(e) {
        var copyBtn = e.target.closest('.copy-btn');
        if (copyBtn) {
            var text = copyBtn.dataset.copy;
            navigator.clipboard.writeText(text).then(function() {
                copyBtn.textContent = '\u2713';
                setTimeout(function() { copyBtn.textContent = '\u{1F4CB}'; }, 1500);
            });
            e.stopPropagation();
            return;
        }

        var item = e.target.closest('.component-item');
        if (!item) return;

        var objectId = item.dataset.objectId;
        var detailCard = content.querySelector('.component-detail-card[data-detail-for="' + cssEscape(objectId) + '"]');
        if (!detailCard) return;

        // Collapse any other expanded card
        var expanded = content.querySelector('.component-detail-card.expanded');
        if (expanded && expanded !== detailCard) {
            expanded.classList.remove('expanded');
        }

        detailCard.classList.toggle('expanded');
    });

    // Keyboard: Enter/Space on component item
    content.addEventListener('keydown', function(e) {
        if (e.key !== 'Enter' && e.key !== ' ') return;
        var item = e.target.closest('.component-item');
        if (!item) return;
        e.preventDefault();
        item.click();
    });

    function updateSolutionExpansion(uniqueName, expanded) {
        const row = content.querySelector('.solution-row[data-unique-name="' + cssEscape(uniqueName) + '"]');
        if (!row) return;
        const chevron = row.querySelector('.chevron');
        const container = row.nextElementSibling;
        if (chevron) {
            if (expanded) chevron.classList.add('expanded');
            else chevron.classList.remove('expanded');
        }
        if (container && container.classList.contains('components-container')) {
            if (expanded) container.classList.add('expanded');
            else container.classList.remove('expanded');
        }
    }

    // ── Message handling ──
    window.addEventListener('message', (event) => {
        const msg = event.data;
        switch (msg.command) {
            case 'updateEnvironment':
                updateEnvironmentDisplay(msg.name);
                break;
            case 'solutionsLoaded':
                renderSolutions(msg.solutions, msg.managedCount, msg.includeManaged);
                var reconnectBanner = document.getElementById('reconnect-banner');
                if (reconnectBanner) reconnectBanner.style.display = 'none';
                break;
            case 'componentsLoading':
                showComponentsLoading(msg.uniqueName);
                break;
            case 'componentsLoaded':
                renderComponents(msg.uniqueName, msg.groups);
                break;
            case 'updateManagedState':
                managedOn = msg.includeManaged;
                managedBtn.textContent = managedOn ? 'Managed: On' : 'Managed: Off';
                managedBtn.setAttribute('appearance', managedOn ? 'primary' : 'secondary');
                break;
            case 'loading':
                content.innerHTML = '<div class="loading-state"><div class="spinner"></div><div>Loading solutions...</div></div>';
                statusText.textContent = 'Loading...';
                break;
            case 'error':
                content.innerHTML = '<div class="error-state">' + escapeHtml(msg.message) + '</div>';
                statusText.textContent = 'Error';
                break;
            case 'daemonReconnected':
                document.getElementById('reconnect-banner').style.display = '';
                break;
        }
    });

    function renderSolutions(sols, managedCount, includeManaged) {
        solutions = sols;
        searchInput.value = '';
        filterText = '';

        if (sols.length === 0) {
            content.innerHTML = '<div class="empty-state">No solutions found</div>';
            statusText.textContent = 'No solutions';
            return;
        }

        let html = '<ul class="solution-list">';
        for (const sol of sols) {
            const isExpanded = expandedSolutions.has(sol.uniqueName);
            html += '<li>';
            html += '<div class="solution-row" data-unique-name="' + escapeAttr(sol.uniqueName) + '">';
            html += '<span class="chevron' + (isExpanded ? ' expanded' : '') + '">&#9654;</span>';
            html += '<span class="icon">' + (sol.isManaged ? '&#128274;' : '&#128230;') + '</span>';
            html += '<span class="name">' + escapeHtml(sol.friendlyName) + '</span>';
            if (sol.version) {
                html += '<span class="version">(' + escapeHtml(sol.version) + ')</span>';
            }
            if (sol.publisherName) {
                html += '<span class="publisher">' + escapeHtml('— ' + sol.publisherName) + '</span>';
            }
            if (sol.isManaged) {
                html += '<span class="managed-badge">managed</span>';
            }
            html += '<button class="open-maker-btn" data-solution-id="' + escapeAttr(sol.id || '') + '" title="Open in Maker Portal">\uD83D\uDD17</button>';
            html += '</div>';
            html += '<div class="components-container' + (isExpanded ? ' expanded' : '') + '" id="components-' + escapeAttr(sol.uniqueName) + '">';
            html += '<div class="detail-card">';
            html += '<span class="detail-label">Unique Name</span><span class="detail-value">' + escapeHtml(sol.uniqueName) + '</span>';
            html += '<span class="detail-label">Publisher</span><span class="detail-value">' + escapeHtml(sol.publisherName || '\u2014') + '</span>';
            html += '<span class="detail-label">Type</span><span class="detail-value">' + (sol.isManaged ? 'Managed' : 'Unmanaged') + '</span>';
            if (sol.installedOn) {
                html += '<span class="detail-label">Installed</span><span class="detail-value">' + formatDate(sol.installedOn) + '</span>';
            }
            if (sol.modifiedOn) {
                html += '<span class="detail-label">Modified</span><span class="detail-value">' + formatDate(sol.modifiedOn) + '</span>';
            }
            if (sol.description) {
                html += '<div class="detail-description">' + escapeHtml(sol.description) + '</div>';
            }
            html += '</div>';
            html += '<div class="components-loading"><span class="spinner"></span> Loading components...</div>';
            html += '</div>';
            html += '</li>';
        }
        html += '</ul>';
        content.innerHTML = html;

        // Update status
        let statusMsg = sols.length + ' solution' + (sols.length !== 1 ? 's' : '');
        if (!includeManaged && managedCount > 0) {
            statusMsg += ' (' + managedCount + ' managed hidden)';
        }
        statusText.textContent = statusMsg;

        // Re-expand solutions that were previously expanded and re-request components
        for (const uniqueName of expandedSolutions) {
            const found = sols.find(s => s.uniqueName === uniqueName);
            if (found) {
                vscode.postMessage({ command: 'expandSolution', uniqueName });
            }
        }
    }

    function showComponentsLoading(uniqueName) {
        const container = document.getElementById('components-' + uniqueName);
        if (container) {
            container.innerHTML = '<div class="components-loading"><span class="spinner"></span> Loading components...</div>';
        }
    }

    function renderComponents(uniqueName, groups) {
        const container = document.getElementById('components-' + uniqueName);
        if (!container) return;

        if (!groups || groups.length === 0) {
            container.innerHTML = '<div class="components-loading">No components</div>';
            return;
        }

        let html = '';
        for (const group of groups) {
            const groupKey = uniqueName + '::' + group.typeName;
            const isGroupExpanded = expandedGroups.has(groupKey);

            html += '<div class="component-group" data-group-key="' + escapeAttr(groupKey) + '">';
            html += '<span class="chevron' + (isGroupExpanded ? ' expanded' : '') + '">&#9654;</span>';
            html += '<span class="group-name">' + escapeHtml(group.typeName) + '</span>';
            html += '<span class="group-count">(' + group.components.length + ')</span>';
            html += '</div>';

            html += '<div class="component-items' + (isGroupExpanded ? ' expanded' : '') + '">';
            for (const comp of group.components) {
                var name = comp.logicalName || comp.schemaName || comp.displayName || comp.objectId;
                var subtitle = '';
                if (comp.logicalName && comp.displayName && comp.displayName !== comp.logicalName) {
                    subtitle = ' (' + escapeHtml(comp.displayName) + ')';
                }

                html += '<div class="component-item" tabindex="0" data-object-id="' + escapeAttr(comp.objectId) + '">';
                html += '<span class="component-name">' + escapeHtml(name) + subtitle + '</span>';
                if (comp.isMetadata) {
                    html += ' <span class="metadata-badge">metadata</span>';
                }
                html += '</div>';

                // Detail card (hidden by default)
                html += '<div class="component-detail-card" data-detail-for="' + escapeAttr(comp.objectId) + '">';
                if (comp.logicalName) {
                    html += '<span class="detail-label">Logical Name</span><span class="detail-value">' + escapeHtml(comp.logicalName) + '</span>';
                }
                if (comp.schemaName) {
                    html += '<span class="detail-label">Schema Name</span><span class="detail-value">' + escapeHtml(comp.schemaName) + '</span>';
                }
                if (comp.displayName) {
                    html += '<span class="detail-label">Display Name</span><span class="detail-value">' + escapeHtml(comp.displayName) + '</span>';
                }
                html += '<span class="detail-label">Object ID</span><span class="detail-value">' + escapeHtml(comp.objectId) + ' <button class="copy-btn" data-copy="' + escapeAttr(comp.objectId) + '">&#128203;</button></span>';
                html += '<span class="detail-label">Root Behavior</span><span class="detail-value">' + comp.rootComponentBehavior + '</span>';
                html += '<span class="detail-label">Metadata</span><span class="detail-value">' + (comp.isMetadata ? 'Yes' : 'No') + '</span>';
                html += '</div>';
            }
            html += '</div>';
        }
        container.innerHTML = html;
    }

    // ── Utilities ──
    function escapeHtml(str) {
        if (str === null || str === undefined) return '';
        var s = String(str);
        return s.replace(/&/g, '&amp;').replace(/</g, '&lt;')
            .replace(/>/g, '&gt;').replace(/"/g, '&quot;')
            .replace(/'/g, '&#039;');
    }

    function escapeAttr(str) {
        if (str === null || str === undefined) return '';
        var s = String(str);
        return s.replace(/&/g, '&amp;').replace(/"/g, '&quot;')
            .replace(/'/g, '&#039;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
    }

    function cssEscape(str) {
        if (str === null || str === undefined) return '';
        return CSS.escape(String(str));
    }

    function formatDate(isoString) {
        if (!isoString) return '';
        try {
            return new Date(isoString).toLocaleDateString(undefined, { year: 'numeric', month: 'short', day: 'numeric' });
        } catch { return isoString; }
    }

    // Signal ready
    vscode.postMessage({ command: 'ready' });
})();
</script>
</body>
</html>`;
    }
}
