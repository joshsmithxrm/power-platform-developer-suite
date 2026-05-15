// connection-references-panel.ts
// External webview script for the Connection References panel.
// Built by esbuild as IIFE for browser, loaded via <script src="...">.

import { escapeHtml, escapeAttr, cssEscape, formatDateTime } from './shared/dom-utils.js';
import type {
    ConnectionReferencesPanelWebviewToHost,
    ConnectionReferencesPanelHostToWebview,
    ConnectionReferenceViewDto,
    ConnectionReferenceDetailViewDto,
    ConnectionReferencesAnalyzeViewDto,
    ConnectionPickerOptionDto,
} from './shared/message-types.js';
import { assertNever } from './shared/assert-never.js';
import { getVsCodeApi } from './shared/vscode-api.js';
import { installErrorHandler } from './shared/error-handler.js';
import { DataTable } from './shared/data-table.js';
import { SolutionFilter } from './shared/solution-filter.js';
import { FilterBar } from './shared/filter-bar.js';

/** Default Solution GUID — must match the constant in ConnectionReferencesPanel.ts (CR-09). */
const DEFAULT_SOLUTION_ID = 'fd140aaf-4df4-11dd-bd17-0019b9312238';

const vscode = getVsCodeApi<ConnectionReferencesPanelWebviewToHost>();
installErrorHandler((msg) => vscode.postMessage(msg));
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
function updateEnvironmentDisplay(profileName: string | undefined, name: string | null): void {
    const env = name || 'No environment';
    envPickerName.textContent = profileName ? `${profileName} · ${env}` : env;
}

// ── Active/All segmented toggle (V-16) ──
let includeInactive = false;
const segActive = document.getElementById('seg-active') as HTMLButtonElement;
const segAll = document.getElementById('seg-all') as HTMLButtonElement;

function setActiveSegment(active: boolean): void {
    includeInactive = !active;
    segActive.classList.toggle('seg-active', active);
    segAll.classList.toggle('seg-active', !active);
    vscode.postMessage({ command: 'setIncludeInactive', includeInactive });
}

segActive.addEventListener('click', () => setActiveSegment(true));
segAll.addEventListener('click', () => setActiveSegment(false));

// ── Solution filter (CR-09) ──
const solutionFilterContainer = document.getElementById('solution-filter-container') as HTMLElement;
const solutionFilter = new SolutionFilter(solutionFilterContainer, {
    onChange: (solutionId) => {
        vscode.postMessage({ command: 'filterBySolution', solutionId });
    },
    getState: () => vscode.getState() as Record<string, unknown> | undefined,
    setState: (state) => vscode.setState(state),
    storageKey: 'connectionReferences.solutionFilter',
    defaultValue: DEFAULT_SOLUTION_ID,
});

// Request solution list on load
vscode.postMessage({ command: 'requestSolutionList' });

// ── Expandable row state ──
const expandedRows = new Set<string>();
const rowDetails = new Map<string, ConnectionReferenceDetailViewDto>();
/** environmentId forwarded with each detail response for Maker deep links (CR-03). */
let currentEnvironmentId: string | null = null;

// ── Status badge helper (CR-05) ──
function statusBadgeHtml(status: string, connectionId: string | null | undefined, hasHealthWarning?: boolean): string {
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

    // CR-05: override with warning badge if health warning is set
    if (hasHealthWarning && connectionId) {
        cls = 'status-badge status-warning';
        label = 'Warning';
        tooltip = ' title="This connection reference has a health issue (error status or orphaned flows)."';
    }

    return '<span class="' + cls + '"' + tooltip + '>' + escapeHtml(label) + '</span>';
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
            // CR-01/CR-02: Flow count column
            key: 'flowCount',
            label: 'Flows',
            render: (r) => {
                const count = r.flowCount;
                if (count === undefined) return '<span class="cr-flow-count-unknown">\u2014</span>';
                if (count === 0) return '<span class="cr-flow-count-zero">\u2014</span>';
                return '<span class="cr-flow-count">' + escapeHtml(count + ' flow' + (count !== 1 ? 's' : '')) + '</span>';
            },
            className: '80px',
        },
        {
            // CR-05: Status column reflects relationship health
            key: 'connectionStatus',
            label: 'Status',
            render: (r) => statusBadgeHtml(r.connectionStatus, r.connectionId, r.hasHealthWarning),
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

/** Number of columns: chevron + displayName + logicalName + connector + flows + status + managed + modifiedOn = 8 */
const COLUMN_COUNT = 8;

function insertInlineDetailLoading(logicalName: string): void {
    const row = content.querySelector<HTMLElement>('.data-table-row[data-id="' + cssEscape(logicalName) + '"]');
    if (!row) return;

    // Remove existing card if any
    const existing = content.querySelector<HTMLElement>('.cr-inline-detail[data-for="' + cssEscape(logicalName) + '"]');
    if (existing) existing.remove();

    const tr = document.createElement('tr');
    tr.className = 'cr-inline-detail';
    tr.setAttribute('data-for', logicalName);
    const td = document.createElement('td');
    td.colSpan = COLUMN_COUNT;
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

    const tr = document.createElement('tr');
    tr.className = 'cr-inline-detail';
    tr.setAttribute('data-for', logicalName);
    const td = document.createElement('td');
    td.colSpan = COLUMN_COUNT;

    let html = '<div class="cr-detail-card">';
    html += '<div class="cr-detail-grid">';

    // Connection info — Change Connection action lives inline so the picker is one click away.
    const connectionLabel = detail.isBound
        ? escapeHtml(detail.connectionOwner ? detail.connectionOwner : 'Bound')
        : escapeHtml('Unbound');
    const connectionClass = detail.isBound ? '' : ' class="status-unbound-text"';
    html += '<div class="cr-detail-item"><span class="detail-label">Connection:</span> <span' + connectionClass + '>' + connectionLabel + '</span> '
        + '<button type="button" class="cr-bind-btn" data-logical-name="' + escapeAttr(detail.logicalName) + '" '
        + 'data-connector-id="' + escapeAttr(detail.connectorId ?? '') + '" '
        + 'data-managed="' + (detail.isManaged ? '1' : '0') + '" '
        + (detail.isManaged ? 'disabled title="Managed connection references cannot be rebound from the panel."' : 'title="Pick a connection to bind"')
        + '>' + (detail.isBound ? 'Change…' : 'Bind…') + '</button>'
        + '</div>';

    // Connector
    html += '<div class="cr-detail-item"><span class="detail-label">Connector:</span> <span>' + escapeHtml(detail.connectorDisplayName ?? detail.connectorId ?? '\u2014') + '</span></div>';

    // Status (CR-05: health-aware)
    html += '<div class="cr-detail-item"><span class="detail-label">Status:</span> ' + statusBadgeHtml(detail.connectionStatus, detail.connectionId, detail.hasHealthWarning) + '</div>';

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

    // Flows section (CR-03/CR-30: per-flow Maker Portal deep links)
    if (detail.flows.length > 0) {
        html += '<div class="cr-detail-flows">';
        html += '<div class="detail-section-title">Flows using this CR (' + detail.flows.length + ')</div>';
        html += '<ul class="cr-flow-list">';
        for (const flow of detail.flows) {
            html += '<li class="cr-flow-item">';

            // CR-03/CR-30: clickable deep link if we have envId and flowId
            const flowName = escapeHtml(flow.displayName ?? flow.uniqueName);
            if (currentEnvironmentId && flow.flowId) {
                const deepLinkUrl = 'https://make.powerautomate.com/environments/'
                    + escapeAttr(currentEnvironmentId)
                    + '/flows/'
                    + escapeAttr(flow.flowId)
                    + '/details';
                html += '<a class="cr-flow-link" href="' + deepLinkUrl + '" title="Open in Maker Portal" data-url="' + deepLinkUrl + '">'
                    + flowName + '</a>';
            } else {
                html += '<span class="cr-flow-name">' + flowName + '</span>';
            }

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

    // Wire deep link clicks to open in external browser via message (CR-03)
    td.querySelectorAll<HTMLAnchorElement>('.cr-flow-link').forEach(link => {
        link.addEventListener('click', (e) => {
            e.preventDefault();
            const url = link.dataset.url;
            if (url) vscode.postMessage({ command: 'openFlowInMaker', url });
        });
    });

    // Wire Change/Bind button to open the connection picker (issue #592).
    td.querySelectorAll<HTMLButtonElement>('.cr-bind-btn').forEach(btn => {
        btn.addEventListener('click', (e) => {
            e.stopPropagation();
            if (btn.disabled) return;
            const logicalName = btn.dataset.logicalName ?? '';
            const connectorId = btn.dataset.connectorId || null;
            openConnectionPicker(logicalName, connectorId, detail.connectionId);
        });
    });

    tr.appendChild(td);
    row.after(tr);
}

// ── Connection picker dialog (issue #592) ──
let pickerState: {
    logicalName: string;
    connectorId: string | null;
    currentConnectionId: string | null | undefined;
    options: ConnectionPickerOptionDto[];
} | null = null;
let pickerKeydownHandler: ((e: KeyboardEvent) => void) | null = null;

function openConnectionPicker(logicalName: string, connectorId: string | null, currentConnectionId: string | null | undefined): void {
    pickerState = { logicalName, connectorId, currentConnectionId, options: [] };
    renderPickerLoading(logicalName);
    // Escape closes the modal — universal expectation; helps keyboard-only users.
    pickerKeydownHandler = (e: KeyboardEvent) => {
        if (e.key === 'Escape' && pickerState) {
            e.preventDefault();
            closeConnectionPicker();
        }
    };
    document.addEventListener('keydown', pickerKeydownHandler);
    vscode.postMessage({ command: 'requestConnections', logicalName, connectorId });
}

function closeConnectionPicker(): void {
    pickerState = null;
    if (pickerKeydownHandler) {
        document.removeEventListener('keydown', pickerKeydownHandler);
        pickerKeydownHandler = null;
    }
    const overlay = document.getElementById('cr-picker-overlay');
    if (overlay) overlay.remove();
}

function renderPickerLoading(logicalName: string): void {
    let overlay = document.getElementById('cr-picker-overlay');
    if (!overlay) {
        overlay = document.createElement('div');
        overlay.id = 'cr-picker-overlay';
        overlay.className = 'cr-picker-overlay';
        document.body.appendChild(overlay);
        overlay.addEventListener('click', (e) => {
            if (e.target === overlay) closeConnectionPicker();
        });
    }
    overlay.innerHTML =
        '<div class="cr-picker-dialog" role="dialog" aria-modal="true" aria-label="Connection picker">'
        + '<div class="cr-picker-header">Bind connection to <code>' + escapeHtml(logicalName) + '</code></div>'
        + '<div class="cr-picker-body"><div class="loading-state"><div class="spinner"></div><div>Loading connections…</div></div></div>'
        + '<div class="cr-picker-footer">'
        + '<button type="button" id="cr-picker-cancel">Cancel</button>'
        + '</div>'
        + '</div>';
    const cancel = document.getElementById('cr-picker-cancel');
    if (cancel) cancel.addEventListener('click', closeConnectionPicker);
}

function renderPickerOptions(): void {
    if (!pickerState) return;
    const overlay = document.getElementById('cr-picker-overlay');
    if (!overlay) return;

    const { logicalName, connectorId, currentConnectionId, options } = pickerState;

    // Show the connector name (last segment) plus the friendly name if any
    // connection carries one. The full admin-scoped URL is noisy for users.
    const friendlyConnector = options.find(o => o.connectorDisplayName)?.connectorDisplayName ?? null;
    const connectorSuffix = (() => {
        const src = connectorId ?? options[0]?.connectorId ?? '';
        const apiSlash = src.lastIndexOf('/apis/');
        return apiSlash >= 0 ? src.substring(apiSlash + '/apis/'.length) : src;
    })();
    const connectorLabel = friendlyConnector
        ? friendlyConnector + ' (' + connectorSuffix + ')'
        : connectorSuffix;

    let body = '';
    if (options.length === 0) {
        body = '<div class="empty-state">No connections found for connector <code>'
            + escapeHtml(connectorLabel)
            + '</code>. Create one in the Maker Portal first.</div>';
    } else {
        body += '<label for="cr-picker-select" class="cr-picker-label">Connection</label>';
        body += '<select id="cr-picker-select" class="cr-picker-select">';
        body += '<option value="">— Unbind (no connection) —</option>';
        for (const opt of options) {
            const selected = opt.connectionId === currentConnectionId ? ' selected' : '';
            const status = opt.status ? ' [' + opt.status + ']' : '';
            const shared = opt.isShared ? ' (shared)' : '';
            const label = (opt.displayName ?? opt.connectionId) + status + shared;
            body += '<option value="' + escapeAttr(opt.connectionId) + '"' + selected + '>'
                + escapeHtml(label) + '</option>';
        }
        body += '</select>';
        body += '<div class="cr-picker-hint">Filtered by connector <code>'
            + escapeHtml(connectorLabel)
            + '</code>. Selecting the empty option clears the binding.</div>';
    }

    overlay.innerHTML =
        '<div class="cr-picker-dialog" role="dialog" aria-modal="true" aria-label="Connection picker">'
        + '<div class="cr-picker-header">Bind connection to <code>' + escapeHtml(logicalName) + '</code></div>'
        + '<div class="cr-picker-body">' + body + '</div>'
        + '<div class="cr-picker-footer">'
        + '<button type="button" id="cr-picker-cancel">Cancel</button>'
        + (options.length > 0
            ? '<button type="button" id="cr-picker-save" class="cr-picker-primary">Save</button>'
            : '')
        + '</div>'
        + '</div>';

    const overlayEl = document.getElementById('cr-picker-overlay');
    if (overlayEl) {
        overlayEl.addEventListener('click', (e) => {
            if (e.target === overlayEl) closeConnectionPicker();
        });
    }
    const cancel = document.getElementById('cr-picker-cancel');
    if (cancel) cancel.addEventListener('click', closeConnectionPicker);
    const save = document.getElementById('cr-picker-save');
    const select = document.getElementById('cr-picker-select') as HTMLSelectElement | null;
    if (save && select) {
        save.addEventListener('click', () => {
            const value = select.value;
            const newConnectionId = value === '' ? null : value;
            (save as HTMLButtonElement).disabled = true;
            save.textContent = 'Saving…';
            vscode.postMessage({
                command: 'bindConnection',
                logicalName,
                connectionId: newConnectionId,
            });
        });
    }
}

// ── Button handlers ──
refreshBtn.addEventListener('click', () => {
    (refreshBtn as HTMLButtonElement).disabled = true;
    refreshBtn.textContent = 'Loading...';
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
            updateEnvironmentDisplay(msg.profileName, msg.name);
            // CR-09: update solution filter environment key for per-env persistence
            solutionFilter.setEnvironmentKey(msg.name);
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
            (refreshBtn as HTMLButtonElement).disabled = false;
            refreshBtn.textContent = 'Refresh';
            expandedRows.clear();
            rowDetails.clear();
            table.setItems(msg.references);
            searchFilter.setItems(msg.references);
            // V-16: show "X of Y" when filtered
            {
                const total = msg.totalCount;
                const displayed = msg.references.length;
                const filters = msg.filtersApplied ?? [];
                if (displayed < total || filters.length > 0) {
                    const filterNote = filters.length > 0 ? ' (' + escapeHtml(filters.join(', ')) + ')' : '';
                    statusText.textContent = displayed + ' of ' + total + ' connection reference' + (total !== 1 ? 's' : '') + filterNote;
                }
            }
            {
                const reconnectBanner = document.getElementById('reconnect-banner');
                if (reconnectBanner) reconnectBanner.style.display = 'none';
            }
            break;
        case 'connectionReferenceDetailLoaded':
            // Store environment ID for deep links (CR-03)
            currentEnvironmentId = msg.environmentId;
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
            (refreshBtn as HTMLButtonElement).disabled = false;
            refreshBtn.textContent = 'Refresh';
            // If the picker is open (bind failed), re-enable Save so the user can retry.
            if (pickerState) {
                const save = document.getElementById('cr-picker-save') as HTMLButtonElement | null;
                if (save) {
                    save.disabled = false;
                    save.textContent = 'Save';
                }
            }
            // Preserve loaded table state on operational errors (bind/detail/analyze/sync).
            // Only replace the main content if the table is empty — i.e. an initial-load failure.
            if (table.getItems().length === 0) {
                content.innerHTML = '<div class="error-state">' + escapeHtml(msg.message) + '</div>';
            }
            statusText.textContent = 'Error: ' + msg.message;
            break;
        case 'connectionsLoaded':
            if (pickerState && pickerState.logicalName === msg.logicalName) {
                pickerState.options = msg.connections;
                renderPickerOptions();
            }
            break;
        case 'connectionBound': {
            currentEnvironmentId = msg.environmentId;
            rowDetails.set(msg.detail.logicalName, msg.detail);

            // Refresh the main row so the Status badge mirrors the new bound state.
            const items = table.getItems();
            const idx = items.findIndex(it => it.logicalName === msg.detail.logicalName);
            if (idx !== -1) {
                const updated = {
                    ...items[idx],
                    connectionId: msg.detail.connectionId,
                    connectionStatus: msg.detail.connectionStatus,
                    connectorDisplayName: msg.detail.connectorDisplayName,
                    modifiedOn: msg.detail.modifiedOn,
                    hasHealthWarning: !msg.detail.isBound || msg.detail.connectionStatus?.toLowerCase() === 'error',
                };
                const next = items.slice();
                next[idx] = updated;
                table.setItems(next);
                searchFilter.setItems(next);
            }

            if (expandedRows.has(msg.detail.logicalName)) {
                insertInlineDetail(msg.detail.logicalName, msg.detail);
            }
            closeConnectionPicker();
            statusText.textContent = msg.detail.isBound
                ? 'Connection bound to ' + msg.detail.logicalName
                : 'Connection cleared on ' + msg.detail.logicalName;
            break;
        }
        case 'deploymentSettingsSynced':
            (syncBtn as HTMLButtonElement).disabled = false;
            syncBtn.textContent = 'Sync Deployment Settings';
            {
                const ev = msg.envVars;
                const cr = msg.connectionRefs;
                const summary = [
                    'Sync complete.',
                    'Env vars: +' + ev.added + ' -' + ev.removed + ' =' + ev.preserved,
                    'Connection refs: +' + cr.added + ' -' + cr.removed + ' =' + cr.preserved,
                ].join('  ');
                statusText.textContent = summary;
            }
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
