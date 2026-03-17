// web-resources-panel.ts
// External webview script for the Web Resources panel.
// Built by esbuild as IIFE for browser, loaded via <script src="...">.

import { escapeHtml } from './shared/dom-utils.js';
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

const vscode = getVsCodeApi<WebResourcesPanelWebviewToHost>();
installErrorHandler((msg) => vscode.postMessage(msg as WebResourcesPanelWebviewToHost));

const content = document.getElementById('content') as HTMLElement;
const statusText = document.getElementById('status-text') as HTMLElement;
const refreshBtn = document.getElementById('refresh-btn') as HTMLElement;
const publishBtn = document.getElementById('publish-btn') as HTMLButtonElement;
const makerBtn = document.getElementById('maker-btn') as HTMLElement;
const solutionSelect = document.getElementById('solution-select') as HTMLSelectElement;
const textOnlyCb = document.getElementById('text-only-cb') as HTMLInputElement;

// ── Environment picker ──
const envPickerBtn = document.getElementById('env-picker-btn') as HTMLElement;
const envPickerName = document.getElementById('env-picker-name') as HTMLElement;
envPickerBtn.addEventListener('click', () => {
    vscode.postMessage({ command: 'requestEnvironmentList' });
});
function updateEnvironmentDisplay(name: string | null): void {
    envPickerName.textContent = name || 'No environment';
}

// ── Request versioning ──
let lastRequestId = 0;

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
            key: 'modifiedBy',
            label: 'Modified By',
            render: (r) => escapeHtml(r.modifiedBy ?? '\u2014'),
        },
        {
            key: 'modifiedOn',
            label: 'Modified On',
            render: (r) => escapeHtml(r.modifiedOn ?? '\u2014'),
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
        const parts = [items.length + ' web resource' + (items.length !== 1 ? 's' : '')];
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
solutionSelect.addEventListener('change', () => {
    const val = solutionSelect.value || null;
    vscode.postMessage({ command: 'selectSolution', solutionId: val });
});

// ── Text-only toggle ──
textOnlyCb.addEventListener('change', () => {
    vscode.postMessage({ command: 'toggleTextOnly', textOnly: textOnlyCb.checked });
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
        case 'solutionListLoaded':
            populateSolutionDropdown(msg.solutions);
            break;
        case 'webResourcesLoaded':
            // Discard stale responses
            if (msg.requestId < lastRequestId) break;
            lastRequestId = msg.requestId;
            selectedIds.clear();
            updatePublishButton();
            table.setItems(msg.resources);
            {
                const reconnectBanner = document.getElementById('reconnect-banner');
                if (reconnectBanner) reconnectBanner.style.display = 'none';
            }
            break;
        case 'loading':
            content.innerHTML = '<div class="loading-state"><div class="spinner"></div><div>Loading web resources...</div></div>';
            statusText.textContent = 'Loading...';
            break;
        case 'error':
            content.innerHTML = '<div class="error-state">' + escapeHtml(msg.message) + '</div>';
            statusText.textContent = 'Error';
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
    // Preserve current selection
    const currentValue = solutionSelect.value;

    // Clear all options except "All Solutions"
    while (solutionSelect.options.length > 1) {
        solutionSelect.remove(1);
    }

    for (const sol of solutions) {
        const option = document.createElement('option');
        option.value = sol.id;
        option.textContent = sol.friendlyName;
        solutionSelect.appendChild(option);
    }

    // Restore selection if it still exists
    if (currentValue && [...solutionSelect.options].some(o => o.value === currentValue)) {
        solutionSelect.value = currentValue;
    }
}

// Signal ready
vscode.postMessage({ command: 'ready' });
