// import-jobs-panel.ts
// External webview script for the Import Jobs panel.
// Built by esbuild as IIFE for browser, loaded via <script src="...">.

import { escapeHtml, formatDateTime } from './shared/dom-utils.js';
import type { ImportJobsPanelWebviewToHost, ImportJobsPanelHostToWebview, ImportJobViewDto } from './shared/message-types.js';
import { assertNever } from './shared/assert-never.js';
import { getVsCodeApi } from './shared/vscode-api.js';
import { installErrorHandler } from './shared/error-handler.js';
import { DataTable, formatStatusCount } from './shared/data-table.js';

const vscode = getVsCodeApi<ImportJobsPanelWebviewToHost>();
installErrorHandler((msg) => vscode.postMessage(msg as ImportJobsPanelWebviewToHost));
const content = document.getElementById('content') as HTMLElement;
const statusText = document.getElementById('status-text') as HTMLElement;
const refreshBtn = document.getElementById('refresh-btn') as HTMLElement;
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

// ── Search + total count state ──
let allJobs: ImportJobViewDto[] = [];
let totalCount = 0;
let searchTerm = '';

// ── Status badge helper ──
// Daemon returns: "Succeeded", "Failed", "In Progress".
// Badge classes defined for all known + historical statuses (IJ-15).
function statusBadgeHtml(status: string): string {
    const lower = status.toLowerCase();
    let cls = 'status-badge';
    if (lower === 'succeeded') cls += ' status-succeeded';
    else if (lower === 'failed') cls += ' status-failed';
    else if (lower === 'in progress' || lower === 'inprogress' || lower === 'processing') cls += ' status-inprogress';
    else if (lower === 'cancelled' || lower === 'canceled') cls += ' status-cancelled';
    else if (lower === 'queued') cls += ' status-queued';
    else if (lower === 'completed with errors') cls += ' status-completed-errors';
    return '<span class="' + cls + '">' + escapeHtml(status) + '</span>';
}

// ── DataTable setup ──
const table = new DataTable<ImportJobViewDto>({
    container: content,
    columns: [
        {
            key: 'solutionName',
            label: 'Solution',
            render: (j) => {
                const name = escapeHtml(j.solutionName ?? '\u2014');
                // IJ-01: Solution name is a clickable link to open the import log.
                return '<a href="#" class="solution-link" data-id="' + escapeHtml(j.id) + '" title="Open import log">' + name + '</a>';
            },
        },
        {
            key: 'status',
            label: 'Status',
            render: (j) => statusBadgeHtml(j.status),
            sortValue: (j) => j.status,
        },
        {
            key: 'progress',
            label: 'Progress',
            render: (j) => escapeHtml(j.progress + '%'),
            sortValue: (j) => j.progress,
            className: '80px',
        },
        {
            key: 'createdBy',
            label: 'Created By',
            render: (j) => escapeHtml(j.createdBy ?? '\u2014'),
        },
        {
            key: 'createdOn',
            label: 'Created On',
            render: (j) => escapeHtml(formatDateTime(j.createdOn)),
            sortValue: (j) => j.createdOn ?? '',
        },
        {
            key: 'duration',
            label: 'Duration',
            render: (j) => escapeHtml(j.duration ?? '\u2014'),
            className: '90px',
        },
        {
            key: 'operationContext',
            label: 'Operation Context',
            render: (j) => escapeHtml(j.operationContext ?? '\u2014'),
        },
    ],
    getRowId: (j) => j.id,
    onRowClick: (j) => {
        // IJ-02: Row click opens the import log XML in a VS Code editor tab.
        vscode.postMessage({ command: 'viewImportLog', id: j.id });
    },
    defaultSortKey: 'createdOn',
    defaultSortDirection: 'desc',
    statusEl: statusText,
    formatStatus: (items) => {
        // IJ-45 / P2-IJ-01: Use formatStatusCount for "X of Y" pattern (Constitution I4).
        // Align status counts with actual badge matching logic.
        const succeeded = items.filter(j => j.status === 'Succeeded').length;
        const failed = items.filter(j => j.status === 'Failed').length;
        const inProgress = items.filter(j => (j.status === 'In Progress' || j.status === 'InProgress' || j.status === 'Processing')).length;
        const countLabel = formatStatusCount(items.length, totalCount, 'import job');
        const parts = [countLabel];
        if (succeeded > 0) parts.push(succeeded + ' succeeded');
        if (failed > 0) parts.push(failed + ' failed');
        if (inProgress > 0) parts.push(inProgress + ' in progress');
        return parts.join(' \u2014 ');
    },
    emptyMessage: 'No import jobs found',
});

// ── Solution link click handler (IJ-01): delegated on content ──
content.addEventListener('click', (e) => {
    const link = (e.target as HTMLElement).closest<HTMLElement>('a.solution-link');
    if (!link) return;
    e.preventDefault();
    e.stopPropagation();
    const id = link.dataset['id'];
    if (id) {
        vscode.postMessage({ command: 'viewImportLog', id });
    }
});

// ── Button handlers ──
refreshBtn.addEventListener('click', () => {
    (refreshBtn as HTMLButtonElement).disabled = true;
    refreshBtn.textContent = 'Loading...';
    vscode.postMessage({ command: 'refresh' });
});

makerBtn.addEventListener('click', () => {
    vscode.postMessage({ command: 'openInMaker' });
});

// ── Search filter ──
function applySearchFilter(): void {
    const term = searchTerm.toLowerCase();
    const filtered = term.length === 0
        ? allJobs
        : allJobs.filter(j =>
            (j.solutionName ?? '').toLowerCase().includes(term)
            || (j.createdBy ?? '').toLowerCase().includes(term)
            || (j.operationContext ?? '').toLowerCase().includes(term)
            || j.status.toLowerCase().includes(term)
        );
    table.setItems(filtered);
}

let searchDebounceTimer: ReturnType<typeof setTimeout> | null = null;
searchInput.addEventListener('input', () => {
    if (searchDebounceTimer !== null) clearTimeout(searchDebounceTimer);
    searchDebounceTimer = setTimeout(() => {
        searchTerm = searchInput.value.trim();
        applySearchFilter();
    }, 300);
});

document.getElementById('reconnect-refresh')!.addEventListener('click', (e) => {
    e.preventDefault();
    document.getElementById('reconnect-banner')!.style.display = 'none';
    vscode.postMessage({ command: 'refresh' });
});

// ── Message handling ──
window.addEventListener('message', (event: MessageEvent<ImportJobsPanelHostToWebview>) => {
    const msg = event.data;
    // VS Code sends internal messages that lack 'command' — ignore them
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
        case 'importJobsLoaded':
            (refreshBtn as HTMLButtonElement).disabled = false;
            refreshBtn.textContent = 'Refresh';
            allJobs = msg.jobs;
            totalCount = msg.totalCount;
            applySearchFilter();
            {
                const reconnectBanner = document.getElementById('reconnect-banner');
                if (reconnectBanner) reconnectBanner.style.display = 'none';
            }
            break;
        case 'loading':
            content.innerHTML = '<div class="loading-state"><div class="spinner"></div><div>Loading import jobs...</div></div>';
            statusText.textContent = 'Loading...';
            break;
        case 'error':
            (refreshBtn as HTMLButtonElement).disabled = false;
            refreshBtn.textContent = 'Refresh';
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
