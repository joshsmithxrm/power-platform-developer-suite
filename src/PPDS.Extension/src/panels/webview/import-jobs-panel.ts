// import-jobs-panel.ts
// External webview script for the Import Jobs panel.
// Built by esbuild as IIFE for browser, loaded via <script src="...">.

import { escapeHtml } from './shared/dom-utils.js';
import type { ImportJobsPanelWebviewToHost, ImportJobsPanelHostToWebview, ImportJobViewDto } from './shared/message-types.js';
import { assertNever } from './shared/assert-never.js';
import { getVsCodeApi } from './shared/vscode-api.js';
import { installErrorHandler } from './shared/error-handler.js';
import { DataTable } from './shared/data-table.js';

const vscode = getVsCodeApi<ImportJobsPanelWebviewToHost>();
installErrorHandler((msg) => vscode.postMessage(msg as ImportJobsPanelWebviewToHost));
const content = document.getElementById('content') as HTMLElement;
const statusText = document.getElementById('status-text') as HTMLElement;
const refreshBtn = document.getElementById('refresh-btn') as HTMLElement;
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

// ── Status badge helper ──
function statusBadgeHtml(status: string): string {
    const lower = status.toLowerCase();
    let cls = 'status-badge';
    if (lower === 'succeeded' || lower === 'completed') cls += ' status-succeeded';
    else if (lower === 'failed') cls += ' status-failed';
    else if (lower === 'in progress' || lower === 'inprogress' || lower === 'processing') cls += ' status-inprogress';
    return '<span class="' + cls + '">' + escapeHtml(status) + '</span>';
}

// ── DataTable setup ──
const table = new DataTable<ImportJobViewDto>({
    container: content,
    columns: [
        {
            key: 'solutionName',
            label: 'Solution',
            render: (j) => escapeHtml(j.solutionName ?? '\u2014'),
        },
        {
            key: 'status',
            label: 'Status',
            render: (j) => statusBadgeHtml(j.status),
        },
        {
            key: 'progress',
            label: 'Progress',
            render: (j) => escapeHtml(j.progress + '%'),
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
            render: (j) => escapeHtml(j.createdOn ?? '\u2014'),
        },
        {
            key: 'duration',
            label: 'Duration',
            render: (j) => escapeHtml(j.duration ?? '\u2014'),
            className: '90px',
        },
    ],
    getRowId: (j) => j.id,
    onRowClick: (j) => {
        vscode.postMessage({ command: 'selectJob', id: j.id });
    },
    defaultSortKey: 'createdOn',
    defaultSortDirection: 'desc',
    statusEl: statusText,
    formatStatus: (items) => {
        const succeeded = items.filter(j => j.status === 'Succeeded').length;
        const failed = items.filter(j => j.status === 'Failed').length;
        const inProgress = items.filter(j => j.status === 'In Progress').length;
        const parts = [items.length + ' import job' + (items.length !== 1 ? 's' : '')];
        if (succeeded > 0) parts.push(succeeded + ' succeeded');
        if (failed > 0) parts.push(failed + ' failed');
        if (inProgress > 0) parts.push(inProgress + ' in progress');
        return parts.join(' \u2014 ');
    },
    emptyMessage: 'No import jobs found',
});

// ── Button handlers ──
refreshBtn.addEventListener('click', () => {
    vscode.postMessage({ command: 'refresh' });
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
// Note: Ctrl+R is NOT used because it conflicts with VS Code's native reload.
// Refresh is available via the toolbar button.
document.addEventListener('keydown', (e) => {
    // Escape = close detail pane
    if (e.key === 'Escape' && detailPane.style.display !== 'none') {
        detailPane.style.display = 'none';
    }
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
            table.setItems(msg.jobs);
            detailPane.style.display = 'none';
            {
                const reconnectBanner = document.getElementById('reconnect-banner');
                if (reconnectBanner) reconnectBanner.style.display = 'none';
            }
            break;
        case 'importJobDetailLoaded':
            // Show detail in the detail pane using textContent (safe)
            detailContent.textContent = msg.data ?? 'No detail data available';
            detailPane.style.display = '';
            break;
        case 'loading':
            content.innerHTML = '<div class="loading-state"><div class="spinner"></div><div>Loading import jobs...</div></div>';
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
