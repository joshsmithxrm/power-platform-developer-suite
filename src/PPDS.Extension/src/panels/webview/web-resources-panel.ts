// web-resources-panel.ts
// External webview script for the Web Resources panel.
// Built by esbuild as IIFE for browser, loaded via <script src="...">.

import { escapeHtml, formatDateTime } from './shared/dom-utils.js';
import type {
    WebResourcesPanelWebviewToHost,
    WebResourcesPanelHostToWebview,
    SolutionOptionDto,
} from './shared/message-types.js';
import type { WebResourceInfoDto } from '../../types.js';
import { assertNever } from './shared/assert-never.js';
import { getVsCodeApi } from './shared/vscode-api.js';
import { installErrorHandler } from './shared/error-handler.js';
import { DataTable } from './shared/data-table.js';
import { SolutionFilter } from './shared/solution-filter.js';

const vscode = getVsCodeApi<WebResourcesPanelWebviewToHost>();
installErrorHandler((msg) => vscode.postMessage(msg as WebResourcesPanelWebviewToHost));

const content = document.getElementById('content') as HTMLElement;
const statusText = document.getElementById('status-text') as HTMLElement;
const refreshBtn = document.getElementById('refresh-btn') as HTMLElement;
const publishBtn = document.getElementById('publish-btn') as HTMLButtonElement;
const makerBtn = document.getElementById('maker-btn') as HTMLElement;
const solutionFilterContainer = document.getElementById('solution-filter-container') as HTMLElement;
const textOnlyCb = document.getElementById('text-only-cb') as HTMLInputElement;
const searchInput = document.getElementById('wr-search') as HTMLInputElement;
const publishAllBtn = document.getElementById('publish-all-btn') as HTMLElement;

// ── Environment picker ──
const envPickerBtn = document.getElementById('env-picker-btn') as HTMLElement;
const envPickerName = document.getElementById('env-picker-name') as HTMLElement;
envPickerBtn.addEventListener('click', () => {
    vscode.postMessage({ command: 'requestEnvironmentList' });
});
function updateEnvironmentDisplay(profileName: string | undefined, name: string | null): void {
    const env = name || 'No environment';
    envPickerName.textContent = profileName ? `${profileName} · ${env}` : env;
}

// ── Request versioning ──
let lastRequestId = 0;

// ── Search state ──
let allResources: WebResourceInfoDto[] = [];
let searchTerm = '';

// ── WR-04: Progressive loading state ──
/** Total record count reported by the server (may exceed allResources.length during loading). */
let serverTotalCount = 0;
/** True while the host is still streaming pages for the current request. */
let isLoadingMore = false;

// ── WR-03: Server-side search banner ──
const serverSearchBanner = document.getElementById('server-search-banner');
const serverSearchBtn = document.getElementById('server-search-btn');

function updateServerSearchBanner(): void {
    if (!serverSearchBanner || !serverSearchBtn) return;
    // Show banner only when: search term active AND still loading more data
    if (searchTerm.length > 0 && isLoadingMore) {
        serverSearchBanner.style.display = '';
        serverSearchBtn.textContent = `Search all ${serverTotalCount.toLocaleString()} records`;
    } else {
        serverSearchBanner.style.display = 'none';
    }
}

serverSearchBtn?.addEventListener('click', () => {
    // Ask the host to re-send the full dataset so we can filter the complete set
    vscode.postMessage({ command: 'serverSearch', term: searchTerm });
    serverSearchBanner!.style.display = 'none';
});

// ── Type badge helper ──
function typeBadgeHtml(typeName: string): string {
    const lower = typeName.toLowerCase();
    let cls = 'type-badge';
    if (lower.includes('script') || lower.includes('jscript') || lower === 'js') cls += ' type-badge-js';
    else if (lower.includes('css') || lower.includes('style')) cls += ' type-badge-css';
    else if (lower.includes('html') || lower.includes('htm') || lower.includes('webpage')) cls += ' type-badge-html';
    else if (lower.includes('xml') || lower.includes('data')) cls += ' type-badge-xml';
    else if (lower.includes('png')) cls += ' type-badge-png';
    else if (lower.includes('jpg') || lower.includes('jpeg')) cls += ' type-badge-jpg';
    else if (lower.includes('gif')) cls += ' type-badge-gif';
    else if (lower.includes('svg') || lower.includes('vector')) cls += ' type-badge-svg';
    else if (lower.includes('ico')) cls += ' type-badge-ico';
    else if (lower.includes('xsl')) cls += ' type-badge-xsl';
    else if (lower.includes('resx')) cls += ' type-badge-resx';
    return '<span class="' + cls + '">' + escapeHtml(typeName) + '</span>';
}

// ── Managed badge helper ──
function managedBadgeHtml(isManaged: boolean): string {
    const cls = isManaged ? 'managed-badge managed-yes' : 'managed-badge managed-no';
    const label = isManaged ? 'Managed' : 'Unmanaged';
    return '<span class="' + cls + '">' + escapeHtml(label) + '</span>';
}

// ── Selected rows tracking ──
const selectedIds = new Set<string>();

function updatePublishButton(): void {
    publishBtn.disabled = selectedIds.size === 0;
    publishBtn.title = selectedIds.size > 0
        ? `Publish ${selectedIds.size} selected web resource${selectedIds.size !== 1 ? 's' : ''}`
        : 'Select web resources to publish';
}

// ── DataTable setup ──
const table = new DataTable<WebResourceInfoDto>({
    container: content,
    columns: [
        {
            key: 'name',
            label: 'Name',
            render: (r) => {
                if (r.isTextType) {
                    return '<span class="wr-name-link" data-wr-id="'
                        + escapeHtml(r.id) + '" data-wr-name="'
                        + escapeHtml(r.name) + '" data-wr-text="true"'
                        + ' data-wr-type="' + String(r.type) + '">'
                        + escapeHtml(r.name) + '</span>';
                }
                return escapeHtml(r.name);
            },
        },
        {
            key: 'displayName',
            label: 'Display Name',
            render: (r) => escapeHtml(r.displayName ?? '\u2014'),
        },
        {
            key: 'typeName',
            label: 'Type',
            render: (r) => typeBadgeHtml(r.typeName),
        },
        {
            key: 'isManaged',
            label: 'Managed',
            render: (r) => managedBadgeHtml(r.isManaged),
            className: '100px',
        },
        {
            key: 'createdBy',
            label: 'Created By',
            render: (r) => escapeHtml(r.createdBy ?? '\u2014'),
        },
        {
            key: 'createdOn',
            label: 'Created On',
            render: (r) => escapeHtml(formatDateTime(r.createdOn)),
        },
        {
            key: 'modifiedBy',
            label: 'Modified By',
            render: (r) => escapeHtml(r.modifiedBy ?? '\u2014'),
        },
        {
            key: 'modifiedOn',
            label: 'Modified On',
            render: (r) => escapeHtml(formatDateTime(r.modifiedOn)),
        },
    ],
    getRowId: (r) => r.id,
    onRowClick: (r) => {
        // Toggle selection for publish
        if (selectedIds.has(r.id)) {
            selectedIds.delete(r.id);
        } else {
            selectedIds.add(r.id);
        }
        updatePublishButton();
        // Update row visual
        const row = content.querySelector(`[data-id="${CSS.escape(r.id)}"]`);
        if (row) {
            row.classList.toggle('selected', selectedIds.has(r.id));
        }
    },
    defaultSortKey: 'name',
    defaultSortDirection: 'asc',
    statusEl: statusText,
    formatStatus: (items) => {
        const text = items.filter(r => r.isTextType).length;
        const managed = items.filter(r => r.isManaged).length;
        const isFiltered = searchTerm.length > 0 && items.length !== allResources.length;
        // WR-04: Show progress when still loading more from host
        const loadedOf = isLoadingMore
            ? ` \u2014 Loading... ${allResources.length.toLocaleString()} of ${serverTotalCount.toLocaleString()}`
            : '';
        const countLabel = isFiltered
            ? items.length + ' of ' + allResources.length + ' web resources'
            : items.length + ' web resource' + (items.length !== 1 ? 's' : '');
        const parts = [countLabel + loadedOf];
        if (text > 0) parts.push(text + ' text');
        if (managed > 0) parts.push(managed + ' managed');
        return parts.join(' \u2014 ');
    },
    emptyMessage: 'No web resources found',
});

// ── Clickable name links (delegated) ──
content.addEventListener('click', (e) => {
    const link = (e.target as HTMLElement).closest<HTMLElement>('.wr-name-link');
    if (link) {
        e.stopPropagation();
        const id = link.dataset.wrId;
        const name = link.dataset.wrName;
        const isTextType = link.dataset.wrText === 'true';
        const webResourceType = Number(link.dataset.wrType ?? '0');
        if (id && name) {
            vscode.postMessage({ command: 'openWebResource', id, name, isTextType, webResourceType });
        }
    }
});

// ── Button handlers ──
refreshBtn.addEventListener('click', () => {
    (refreshBtn as HTMLButtonElement).disabled = true;
    refreshBtn.textContent = 'Loading...';
    vscode.postMessage({ command: 'refresh' });
});

publishBtn.addEventListener('click', () => {
    if (selectedIds.size > 0) {
        vscode.postMessage({ command: 'publishSelected', ids: [...selectedIds] });
    }
});

makerBtn.addEventListener('click', () => {
    vscode.postMessage({ command: 'openInMaker' });
});

// ── Solution filter ──
const solutionFilter = new SolutionFilter(solutionFilterContainer, {
    onChange: (solutionId) => {
        vscode.postMessage({ command: 'selectSolution', solutionId });
    },
    getState: () => vscode.getState() as Record<string, unknown> | undefined,
    setState: (state) => vscode.setState(state),
    storageKey: 'webResources.solutionFilter',
});

// Request solution list on load
vscode.postMessage({ command: 'requestSolutionList' });

// ── Text-only toggle ──
textOnlyCb.addEventListener('change', () => {
    vscode.postMessage({ command: 'toggleTextOnly', textOnly: textOnlyCb.checked });
});

// ── Search filter ──
function applySearchFilter(): void {
    const term = searchTerm.toLowerCase();
    const filtered = term.length === 0
        ? allResources
        : allResources.filter(r =>
            r.name.toLowerCase().includes(term)
            || (r.displayName ?? '').toLowerCase().includes(term)
            || r.typeName.toLowerCase().includes(term)
        );
    table.setItems(filtered);
    // WR-03: Update server-search banner visibility
    updateServerSearchBanner();
}

let searchDebounceTimer: ReturnType<typeof setTimeout> | null = null;
searchInput.addEventListener('input', () => {
    if (searchDebounceTimer !== null) clearTimeout(searchDebounceTimer);
    searchDebounceTimer = setTimeout(() => {
        searchTerm = searchInput.value.trim();
        applySearchFilter();
    }, 300);
});

// ── Publish All ──
publishAllBtn.addEventListener('click', () => {
    vscode.postMessage({ command: 'publishAll' });
});

// ── Reconnect banner ──
document.getElementById('reconnect-refresh')!.addEventListener('click', (e) => {
    e.preventDefault();
    document.getElementById('reconnect-banner')!.style.display = 'none';
    vscode.postMessage({ command: 'refresh' });
});

// ── Message handling ──
window.addEventListener('message', (event: MessageEvent<WebResourcesPanelHostToWebview>) => {
    const msg = event.data;
    // VS Code sends internal messages that lack 'command' -- ignore them
    if (!msg || typeof msg !== 'object' || !('command' in msg)) return;
    switch (msg.command) {
        case 'updateEnvironment':
            updateEnvironmentDisplay(msg.profileName, msg.name);
            {
                const toolbar = document.querySelector('.toolbar');
                if (toolbar) {
                    if (msg.envType) {
                        toolbar.setAttribute('data-env-type', msg.envType.toLowerCase());
                    } else {
                        toolbar.removeAttribute('data-env-type');
                    }
                    if (msg.envColor) {
                        toolbar.setAttribute('data-env-color', msg.envColor.toLowerCase());
                    } else {
                        toolbar.removeAttribute('data-env-color');
                    }
                }
            }
            break;
        case 'solutionListLoaded':
            populateSolutionDropdown(msg.solutions);
            break;
        case 'webResourcesLoaded':
            // Discard stale responses
            if (msg.requestId < lastRequestId) break;
            lastRequestId = msg.requestId;
            (refreshBtn as HTMLButtonElement).disabled = false;
            refreshBtn.textContent = 'Refresh';
            selectedIds.clear();
            updatePublishButton();
            // WR-04: First page — reset accumulated list
            serverTotalCount = msg.totalCount;
            isLoadingMore = msg.resources.length < msg.totalCount;
            allResources = msg.resources;
            applySearchFilter();
            {
                const reconnectBanner = document.getElementById('reconnect-banner');
                if (reconnectBanner) reconnectBanner.style.display = 'none';
            }
            break;
        case 'webResourcesPage':
            // WR-04: Subsequent page — append to accumulated list, discard stale
            if (msg.requestId !== lastRequestId) break;
            serverTotalCount = msg.totalCount;
            isLoadingMore = msg.loadedSoFar < msg.totalCount;
            allResources = allResources.concat(msg.resources);
            applySearchFilter();
            break;
        case 'webResourcesLoadComplete':
            // WR-04: All pages received for this request
            if (msg.requestId !== lastRequestId) break;
            serverTotalCount = msg.totalCount;
            isLoadingMore = false;
            // Re-render status bar and hide the server-search banner if showing
            applySearchFilter();
            break;
        case 'filterState':
            solutionFilter.setSelectedId(msg.solutionId);
            textOnlyCb.checked = msg.textOnly;
            break;
        case 'loading':
            content.innerHTML = '<div class="loading-state"><div class="spinner"></div><div>Loading web resources...</div></div>';
            statusText.textContent = 'Loading...';
            isLoadingMore = true;
            serverTotalCount = 0;
            allResources = [];
            if (serverSearchBanner) serverSearchBanner.style.display = 'none';
            break;
        case 'error':
            (refreshBtn as HTMLButtonElement).disabled = false;
            refreshBtn.textContent = 'Refresh';
            content.innerHTML = '<div class="error-state">' + escapeHtml(msg.message) + '</div>';
            statusText.textContent = 'Error';
            isLoadingMore = false;
            break;
        case 'publishResult':
            if (msg.error) {
                statusText.textContent = 'Publish failed: ' + msg.error;
            } else {
                statusText.textContent = `Published ${msg.count} web resource${msg.count !== 1 ? 's' : ''} successfully`;
                selectedIds.clear();
                updatePublishButton();
            }
            break;
        case 'daemonReconnected':
            document.getElementById('reconnect-banner')!.style.display = '';
            break;
        default:
            assertNever(msg);
    }
});

// ── Solution dropdown population ──
function populateSolutionDropdown(solutions: SolutionOptionDto[]): void {
    solutionFilter.setSolutions(solutions.map(s => ({
        id: s.id,
        uniqueName: s.uniqueName,
        friendlyName: s.friendlyName,
    })));
}

// Signal ready
vscode.postMessage({ command: 'ready' });
