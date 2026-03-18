// connection-references-panel.ts
// External webview script for the Connection References panel.
// Built by esbuild as IIFE for browser, loaded via <script src="...">.

import { escapeHtml, escapeAttr, cssEscape } from './shared/dom-utils.js';
import type {
    ConnectionReferencesPanelWebviewToHost,
    ConnectionReferencesPanelHostToWebview,
    ConnectionReferenceViewDto,
    ConnectionReferenceDetailViewDto,
    ConnectionReferencesAnalyzeViewDto,
} from './shared/message-types.js';
import { assertNever } from './shared/assert-never.js';
import { getVsCodeApi } from './shared/vscode-api.js';
import { installErrorHandler } from './shared/error-handler.js';
import { DataTable } from './shared/data-table.js';
import { SolutionFilter } from './shared/solution-filter.js';
import { FilterBar } from './shared/filter-bar.js';

const vscode = getVsCodeApi<ConnectionReferencesPanelWebviewToHost>();
installErrorHandler((msg) => vscode.postMessage(msg as ConnectionReferencesPanelWebviewToHost));
const content = document.getElementById('content') as HTMLElement;
const statusText = document.getElementById('status-text') as HTMLElement;
const refreshBtn = document.getElementById('refresh-btn') as HTMLElement;
const analyzeBtn = document.getElementById('analyze-btn') as HTMLElement;
const syncBtn = document.getElementById('sync-btn') as HTMLElement;
const makerBtn = document.getElementById('maker-btn') as HTMLElement;
const searchInput = document.getElementById('search-input') as HTMLInputElement;

// ── Environment picker ──
const envPickerBtn = document.getElementById('env-picker-btn') as HTMLElement;
const envPickerName = document.getElementById('env-picker-name') as HTMLElement;
envPickerBtn.addEventListener('click', () => {
    vscode.postMessage({ command: 'requestEnvironmentList' });
});
function updateEnvironmentDisplay(name: string | null): void {
    envPickerName.textContent = name || 'No environment';
}

// ── Solution filter ──
const solutionFilterContainer = document.getElementById('solution-filter-container') as HTMLElement;
const solutionFilter = new SolutionFilter(solutionFilterContainer, {
    onChange: (solutionId) => {
        vscode.postMessage({ command: 'filterBySolution', solutionId });
    },
    getState: () => vscode.getState() as Record<string, unknown> | undefined,
    setState: (state) => vscode.setState(state),
    storageKey: 'connectionReferences.solutionFilter',
});

// Request solution list on load
vscode.postMessage({ command: 'requestSolutionList' });

// ── Expandable row state ──
const expandedRows = new Set<string>();
const rowDetails = new Map<string, ConnectionReferenceDetailViewDto>();

// ── Status badge helper ──
function statusBadgeHtml(status: string, connectionId: string | null | undefined): string {
    const lower = status.toLowerCase();
    let cls = 'status-badge';
    let label = status;
    let tooltip = '';

    if (lower === 'connected') {
        cls += ' status-connected';
    } else if (lower === 'error') {
        cls += ' status-error';
    } else if (lower === 'n/a') {
        cls += ' status-unknown';
        label = 'Unknown';
        tooltip = ' title="Status information is not available. This may be due to limited access permissions."';
    } else {
        cls += ' status-na';
    }

    // Show "Unbound" for CRs with no connectionId regardless of reported status
    if (!connectionId) {
        cls = 'status-badge status-unbound';
        label = 'Unbound';
        tooltip = ' title="No connection is bound to this connection reference."';
    }

    return '<span class="' + cls + '"' + tooltip + '>' + escapeHtml(label) + '</span>';
}

// ── Format date/time helper ──
function formatDateTime(isoString: string | null | undefined): string {
    if (!isoString) return '\u2014';
    try {
        const d = new Date(isoString);
        return d.toLocaleDateString(undefined, { year: 'numeric', month: 'short', day: 'numeric' })
            + ' ' + d.toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit' });
    } catch {
        return isoString;
    }
}

// ── DataTable setup ──
const table = new DataTable<ConnectionReferenceViewDto>({
    container: content,
    columns: [
        {
            key: '_chevron',
            label: '',
            render: (r) => {
                const expanded = expandedRows.has(r.logicalName);
                return '<span class="cr-chevron' + (expanded ? ' expanded' : '') + '" data-logical-name="' + escapeAttr(r.logicalName) + '">&#9654;</span>';
            },
            sortable: false,
            className: '28px',
        },
        {
            key: 'displayName',
            label: 'Display Name',
            render: (r) => escapeHtml(r.displayName ?? '\u2014'),
        },
        {
            key: 'logicalName',
            label: 'Logical Name',
            render: (r) => escapeHtml(r.logicalName),
        },
        {
            key: 'connectorDisplayName',
            label: 'Connector',
            render: (r) => escapeHtml(r.connectorDisplayName ?? r.connectorId ?? '\u2014'),
        },
        {
            key: 'connectionStatus',
            label: 'Status',
            render: (r) => statusBadgeHtml(r.connectionStatus, r.connectionId),
        },
        {
            key: 'isManaged',
            label: 'Managed',
            render: (r) => escapeHtml(r.isManaged ? 'Yes' : 'No'),
            className: '80px',
        },
        {
            key: 'modifiedOn',
            label: 'Modified On',
            render: (r) => escapeHtml(formatDateTime(r.modifiedOn)),
        },
    ],
    getRowId: (r) => r.logicalName,
    onRowClick: (r) => {
        toggleRowExpansion(r.logicalName);
    },
    defaultSortKey: 'logicalName',
    defaultSortDirection: 'asc',
    statusEl: statusText,
    formatStatus: (items) => {
        const bound = items.filter(r => !!r.connectionId).length;
        const unbound = items.length - bound;
        const parts = [items.length + ' connection reference' + (items.length !== 1 ? 's' : '')];
        if (bound > 0) parts.push(bound + ' bound');
        if (unbound > 0) parts.push(unbound + ' unbound');
        return parts.join(' \u2014 ');
    },
    emptyMessage: 'No connection references found',
});

// ── Search / Filter ──
// Dummy element for count since we manage status text ourselves
const _filterCountSink = document.createElement('span');

const searchFilter = new FilterBar<ConnectionReferenceViewDto>({
    input: searchInput,
    countEl: _filterCountSink,
    getSearchableText: (r) => [
        r.displayName || '',
        r.logicalName,
        r.connectorDisplayName || '',
        r.connectorId || '',
    ],
    onFilter: (filtered, total) => {
        const rows = content.querySelectorAll<HTMLElement>('.data-table-row');
        const isFiltered = searchInput.value.trim().length > 0;
        const filteredIds = new Set(filtered.map(r => r.logicalName));

        rows.forEach(row => {
            const id = row.dataset.id;
            if (!id) return;
            const visible = filteredIds.has(id);
            row.style.display = visible ? '' : 'none';
            // Also hide any expanded detail card beneath the row
            const detailCard = row.nextElementSibling as HTMLElement | null;
            if (detailCard && detailCard.classList.contains('cr-inline-detail')) {
                detailCard.style.display = visible ? '' : 'none';
            }
        });

        if (isFiltered) {
            statusText.textContent = filtered.length + ' of ' + total + ' connection reference' + (total !== 1 ? 's' : '');
        } else {
            const bound = filtered.filter(r => !!r.connectionId).length;
            const unbound = filtered.length - bound;
            const parts = [filtered.length + ' connection reference' + (filtered.length !== 1 ? 's' : '')];
            if (bound > 0) parts.push(bound + ' bound');
            if (unbound > 0) parts.push(unbound + ' unbound');
            statusText.textContent = parts.join(' \u2014 ');
        }

        if (isFiltered && filtered.length === 0) {
            let emptyEl = content.querySelector('.filter-empty');
            if (!emptyEl) {
                emptyEl = document.createElement('div');
                emptyEl.className = 'empty-state filter-empty';
                emptyEl.textContent = 'No connection references match filter';
                content.appendChild(emptyEl);
            }
        } else {
            const emptyEl = content.querySelector('.filter-empty');
            if (emptyEl) emptyEl.remove();
        }
    },
    itemLabel: 'connection references',
});

// ── Expandable row logic ──
function toggleRowExpansion(logicalName: string): void {
    if (expandedRows.has(logicalName)) {
        // Collapse
        expandedRows.delete(logicalName);
        const card = content.querySelector<HTMLElement>('.cr-inline-detail[data-for="' + cssEscape(logicalName) + '"]');
        if (card) card.remove();
        // Update chevron
        const chevron = content.querySelector<HTMLElement>('.cr-chevron[data-logical-name="' + cssEscape(logicalName) + '"]');
        if (chevron) chevron.classList.remove('expanded');
    } else {
        // Expand — request detail from host
        expandedRows.add(logicalName);
        // Update chevron
        const chevron = content.querySelector<HTMLElement>('.cr-chevron[data-logical-name="' + cssEscape(logicalName) + '"]');
        if (chevron) chevron.classList.add('expanded');

        // If we already have cached detail, show it immediately
        const cached = rowDetails.get(logicalName);
        if (cached) {
            insertInlineDetail(logicalName, cached);
        } else {
            // Insert a loading placeholder
            insertInlineDetailLoading(logicalName);
        }

        // Always fetch fresh detail from the host
        vscode.postMessage({ command: 'getDetail', logicalName });
    }
}

function insertInlineDetailLoading(logicalName: string): void {
    const row = content.querySelector<HTMLElement>('.data-table-row[data-id="' + cssEscape(logicalName) + '"]');
    if (!row) return;

    // Remove existing card if any
    const existing = content.querySelector<HTMLElement>('.cr-inline-detail[data-for="' + cssEscape(logicalName) + '"]');
    if (existing) existing.remove();

    const colCount = 7; // chevron + 6 data columns
    const tr = document.createElement('tr');
    tr.className = 'cr-inline-detail';
    tr.setAttribute('data-for', logicalName);
    const td = document.createElement('td');
    td.colSpan = colCount;
    td.innerHTML = '<div class="cr-detail-card"><div class="loading-state"><div class="spinner"></div><div>Loading details...</div></div></div>';
    tr.appendChild(td);
    row.after(tr);
}

function insertInlineDetail(logicalName: string, detail: ConnectionReferenceDetailViewDto): void {
    const row = content.querySelector<HTMLElement>('.data-table-row[data-id="' + cssEscape(logicalName) + '"]');
    if (!row) return;

    // Remove existing card if any
    const existing = content.querySelector<HTMLElement>('.cr-inline-detail[data-for="' + cssEscape(logicalName) + '"]');
    if (existing) existing.remove();

    const colCount = 7; // chevron + 6 data columns
    const tr = document.createElement('tr');
    tr.className = 'cr-inline-detail';
    tr.setAttribute('data-for', logicalName);
    const td = document.createElement('td');
    td.colSpan = colCount;

    let html = '<div class="cr-detail-card">';
    html += '<div class="cr-detail-grid">';

    // Connection info
    const connectionLabel = detail.isBound
        ? escapeHtml(detail.connectionOwner ? detail.connectionOwner : 'Bound')
        : 'Unbound';
    const connectionClass = detail.isBound ? '' : ' class="status-unbound-text"';
    html += '<div class="cr-detail-item"><span class="detail-label">Connection:</span> <span' + connectionClass + '>' + connectionLabel + '</span></div>';

    // Connector
    html += '<div class="cr-detail-item"><span class="detail-label">Connector:</span> <span>' + escapeHtml(detail.connectorDisplayName ?? detail.connectorId ?? '\u2014') + '</span></div>';

    // Status
    html += '<div class="cr-detail-item"><span class="detail-label">Status:</span> ' + statusBadgeHtml(detail.connectionStatus, detail.connectionId) + '</div>';

    // Managed
    html += '<div class="cr-detail-item"><span class="detail-label">Managed:</span> <span>' + escapeHtml(detail.isManaged ? 'Yes' : 'No') + '</span></div>';

    // Shared
    if (detail.connectionIsShared !== null) {
        html += '<div class="cr-detail-item"><span class="detail-label">Shared:</span> <span>' + escapeHtml(detail.connectionIsShared ? 'Yes' : 'No') + '</span></div>';
    }

    // Description
    if (detail.description) {
        html += '<div class="cr-detail-item cr-detail-full"><span class="detail-label">Description:</span> <span>' + escapeHtml(detail.description) + '</span></div>';
    }

    // Dates
    if (detail.createdOn) {
        html += '<div class="cr-detail-item"><span class="detail-label">Created:</span> <span>' + escapeHtml(formatDateTime(detail.createdOn)) + '</span></div>';
    }
    if (detail.modifiedOn) {
        html += '<div class="cr-detail-item"><span class="detail-label">Modified:</span> <span>' + escapeHtml(formatDateTime(detail.modifiedOn)) + '</span></div>';
    }

    html += '</div>';

    // Flows section
    if (detail.flows.length > 0) {
        html += '<div class="cr-detail-flows">';
        html += '<div class="detail-section-title">Flows using this CR (' + detail.flows.length + ')</div>';
        html += '<ul class="cr-flow-list">';
        for (const flow of detail.flows) {
            html += '<li class="cr-flow-item">';
            html += '<span class="cr-flow-name">' + escapeHtml(flow.displayName ?? flow.uniqueName) + '</span>';
            if (flow.state) {
                const stateClass = flow.state.toLowerCase() === 'activated' ? 'cr-flow-state-active' : 'cr-flow-state-inactive';
                html += ' <span class="cr-flow-state ' + stateClass + '">' + escapeHtml(flow.state) + '</span>';
            }
            html += '</li>';
        }
        html += '</ul>';
        html += '</div>';
    } else {
        html += '<div class="cr-detail-flows"><div class="cr-no-flows">No flows use this connection reference.</div></div>';
    }

    html += '</div>';
    td.innerHTML = html;
    tr.appendChild(td);
    row.after(tr);
}

// ── Button handlers ──
refreshBtn.addEventListener('click', () => {
    vscode.postMessage({ command: 'refresh' });
});

analyzeBtn.addEventListener('click', () => {
    vscode.postMessage({ command: 'analyze' });
});

syncBtn.addEventListener('click', () => {
    vscode.postMessage({ command: 'syncDeploymentSettings' });
});

makerBtn.addEventListener('click', () => {
    vscode.postMessage({ command: 'openInMaker' });
});

document.getElementById('reconnect-refresh')!.addEventListener('click', (e) => {
    e.preventDefault();
    document.getElementById('reconnect-banner')!.style.display = 'none';
    vscode.postMessage({ command: 'refresh' });
});

// ── Analysis rendering ──
function renderAnalysis(result: ConnectionReferencesAnalyzeViewDto): void {
    let html = '<div class="analysis-results">';
    html += '<div class="analysis-summary">';
    html += '<strong>Analysis Complete:</strong> ' + escapeHtml(String(result.totalReferences)) + ' references, '
        + escapeHtml(String(result.totalFlows)) + ' flows examined';
    html += '</div>';

    if (result.orphanedReferences.length > 0) {
        html += '<div class="analysis-section">';
        html += '<div class="analysis-section-title">Orphaned References (' + result.orphanedReferences.length + ')</div>';
        for (const r of result.orphanedReferences) {
            html += '<div class="analysis-item orphaned">';
            html += escapeHtml(r.displayName ?? r.logicalName);
            if (r.connectorId) html += ' <span class="analysis-item-detail">' + escapeHtml(r.connectorId) + '</span>';
            html += '</div>';
        }
        html += '</div>';
    }

    if (result.orphanedFlows.length > 0) {
        html += '<div class="analysis-section">';
        html += '<div class="analysis-section-title">Orphaned Flows (' + result.orphanedFlows.length + ')</div>';
        for (const f of result.orphanedFlows) {
            html += '<div class="analysis-item orphaned">';
            html += escapeHtml(f.displayName ?? f.uniqueName);
            if (f.missingReference) html += ' <span class="analysis-item-detail">missing: ' + escapeHtml(f.missingReference) + '</span>';
            html += '</div>';
        }
        html += '</div>';
    }

    if (result.orphanedReferences.length === 0 && result.orphanedFlows.length === 0) {
        html += '<div class="analysis-section"><div class="analysis-clean">No orphaned references or flows found.</div></div>';
    }

    html += '</div>';
    content.innerHTML = html;
    statusText.textContent = 'Analysis complete';
}

// ── Message handling ──
window.addEventListener('message', (event: MessageEvent<ConnectionReferencesPanelHostToWebview>) => {
    const msg = event.data;
    // VS Code sends internal messages that lack 'command' -- ignore them
    if (!msg || typeof msg !== 'object' || !('command' in msg)) return;
    switch (msg.command) {
        case 'updateEnvironment':
            updateEnvironmentDisplay(msg.name);
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
        case 'connectionReferencesLoaded':
            expandedRows.clear();
            rowDetails.clear();
            table.setItems(msg.references);
            searchFilter.setItems(msg.references);
            {
                const reconnectBanner = document.getElementById('reconnect-banner');
                if (reconnectBanner) reconnectBanner.style.display = 'none';
            }
            break;
        case 'connectionReferenceDetailLoaded':
            // Cache the detail
            rowDetails.set(msg.detail.logicalName, msg.detail);
            // If the row is expanded, update its inline detail card
            if (expandedRows.has(msg.detail.logicalName)) {
                insertInlineDetail(msg.detail.logicalName, msg.detail);
            }
            break;
        case 'analyzeResult':
            renderAnalysis(msg.result);
            break;
        case 'solutionListLoaded':
            solutionFilter.setSolutions(msg.solutions);
            break;
        case 'loading':
            content.innerHTML = '<div class="loading-state"><div class="spinner"></div><div>Loading connection references...</div></div>';
            statusText.textContent = 'Loading...';
            break;
        case 'error':
            content.innerHTML = '<div class="error-state">' + escapeHtml(msg.message) + '</div>';
            statusText.textContent = 'Error';
            break;
        case 'daemonReconnected':
            document.getElementById('reconnect-banner')!.style.display = '';
            break;
        default:
            assertNever(msg);
    }
});

// Signal ready
vscode.postMessage({ command: 'ready' });
