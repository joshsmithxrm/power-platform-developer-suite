// connection-references-panel.ts
// External webview script for the Connection References panel.
// Built by esbuild as IIFE for browser, loaded via <script src="...">.

import { escapeHtml } from './shared/dom-utils.js';
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

const vscode = getVsCodeApi<ConnectionReferencesPanelWebviewToHost>();
installErrorHandler((msg) => vscode.postMessage(msg as ConnectionReferencesPanelWebviewToHost));
const content = document.getElementById('content') as HTMLElement;
const statusText = document.getElementById('status-text') as HTMLElement;
const refreshBtn = document.getElementById('refresh-btn') as HTMLElement;
const analyzeBtn = document.getElementById('analyze-btn') as HTMLElement;
const makerBtn = document.getElementById('maker-btn') as HTMLElement;
const detailPane = document.getElementById('detail-pane') as HTMLElement;
const detailContent = document.getElementById('detail-content') as HTMLElement;
const detailClose = document.getElementById('detail-close') as HTMLElement;

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
});

// Request solution list on load
vscode.postMessage({ command: 'requestSolutionList' });

// ── Status badge helper ──
function statusBadgeHtml(status: string): string {
    const lower = status.toLowerCase();
    let cls = 'status-badge';
    if (lower === 'connected') cls += ' status-connected';
    else if (lower === 'error') cls += ' status-error';
    else cls += ' status-na';
    return '<span class="' + cls + '">' + escapeHtml(status) + '</span>';
}

// ── DataTable setup ──
const table = new DataTable<ConnectionReferenceViewDto>({
    container: content,
    columns: [
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
            render: (r) => statusBadgeHtml(r.connectionStatus),
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
            render: (r) => escapeHtml(r.modifiedOn ?? '\u2014'),
        },
    ],
    getRowId: (r) => r.logicalName,
    onRowClick: (r) => {
        vscode.postMessage({ command: 'selectReference', logicalName: r.logicalName });
    },
    defaultSortKey: 'logicalName',
    defaultSortDirection: 'asc',
    statusEl: statusText,
    formatStatus: (items) => {
        const bound = items.filter(r => r.connectionStatus.toLowerCase() === 'connected').length;
        const unbound = items.length - bound;
        const parts = [items.length + ' connection reference' + (items.length !== 1 ? 's' : '')];
        if (bound > 0) parts.push(bound + ' bound');
        if (unbound > 0) parts.push(unbound + ' unbound');
        return parts.join(' \u2014 ');
    },
    emptyMessage: 'No connection references found',
});

// ── Button handlers ──
refreshBtn.addEventListener('click', () => {
    vscode.postMessage({ command: 'refresh' });
});

analyzeBtn.addEventListener('click', () => {
    vscode.postMessage({ command: 'analyze' });
});

makerBtn.addEventListener('click', () => {
    vscode.postMessage({ command: 'openInMaker' });
});

detailClose.addEventListener('click', () => {
    detailPane.style.display = 'none';
});

document.getElementById('reconnect-refresh')!.addEventListener('click', (e) => {
    e.preventDefault();
    document.getElementById('reconnect-banner')!.style.display = 'none';
    vscode.postMessage({ command: 'refresh' });
});

// ── Keyboard shortcuts ──
document.addEventListener('keydown', (e) => {
    if (e.key === 'Escape' && detailPane.style.display !== 'none') {
        detailPane.style.display = 'none';
    }
});

// ── Detail rendering ──
function renderDetail(detail: ConnectionReferenceDetailViewDto): void {
    let html = '<div class="detail-section">';
    html += '<div class="detail-row"><span class="detail-label">Logical Name:</span> <span class="detail-value">' + escapeHtml(detail.logicalName) + '</span></div>';
    html += '<div class="detail-row"><span class="detail-label">Display Name:</span> <span class="detail-value">' + escapeHtml(detail.displayName ?? '\u2014') + '</span></div>';
    html += '<div class="detail-row"><span class="detail-label">Connector:</span> <span class="detail-value">' + escapeHtml(detail.connectorDisplayName ?? detail.connectorId ?? '\u2014') + '</span></div>';
    html += '<div class="detail-row"><span class="detail-label">Status:</span> <span class="detail-value">' + statusBadgeHtml(detail.connectionStatus) + '</span></div>';
    html += '<div class="detail-row"><span class="detail-label">Bound:</span> <span class="detail-value">' + escapeHtml(detail.isBound ? 'Yes' : 'No') + '</span></div>';
    html += '<div class="detail-row"><span class="detail-label">Managed:</span> <span class="detail-value">' + escapeHtml(detail.isManaged ? 'Yes' : 'No') + '</span></div>';
    if (detail.connectionOwner) {
        html += '<div class="detail-row"><span class="detail-label">Owner:</span> <span class="detail-value">' + escapeHtml(detail.connectionOwner) + '</span></div>';
    }
    if (detail.connectionIsShared !== null) {
        html += '<div class="detail-row"><span class="detail-label">Shared:</span> <span class="detail-value">' + escapeHtml(detail.connectionIsShared ? 'Yes' : 'No') + '</span></div>';
    }
    if (detail.description) {
        html += '<div class="detail-row"><span class="detail-label">Description:</span> <span class="detail-value">' + escapeHtml(detail.description) + '</span></div>';
    }
    if (detail.createdOn) {
        html += '<div class="detail-row"><span class="detail-label">Created On:</span> <span class="detail-value">' + escapeHtml(detail.createdOn) + '</span></div>';
    }
    if (detail.modifiedOn) {
        html += '<div class="detail-row"><span class="detail-label">Modified On:</span> <span class="detail-value">' + escapeHtml(detail.modifiedOn) + '</span></div>';
    }
    html += '</div>';

    if (detail.flows.length > 0) {
        html += '<div class="detail-section">';
        html += '<div class="detail-section-title">Flows (' + detail.flows.length + ')</div>';
        for (const flow of detail.flows) {
            html += '<div class="detail-flow-item">';
            html += '<span class="detail-flow-name">' + escapeHtml(flow.displayName ?? flow.uniqueName) + '</span>';
            if (flow.state) {
                html += ' <span class="detail-flow-state">' + escapeHtml(flow.state) + '</span>';
            }
            html += '</div>';
        }
        html += '</div>';
    }

    detailContent.innerHTML = html;
    detailPane.style.display = '';
}

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
            table.setItems(msg.references);
            detailPane.style.display = 'none';
            {
                const reconnectBanner = document.getElementById('reconnect-banner');
                if (reconnectBanner) reconnectBanner.style.display = 'none';
            }
            break;
        case 'connectionReferenceDetailLoaded':
            renderDetail(msg.detail);
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
