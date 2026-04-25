// environment-variables-panel.ts
// External webview script for the Environment Variables panel.
// Built by esbuild as IIFE for browser, loaded via <script src="...">.

import { escapeHtml, formatDateTime } from './shared/dom-utils.js';
import { FilterBar } from './shared/filter-bar.js';
import type {
    EnvironmentVariablesPanelWebviewToHost,
    EnvironmentVariablesPanelHostToWebview,
    EnvironmentVariableViewDto,
    EnvironmentVariableDetailViewDto,
} from './shared/message-types.js';
import { assertNever } from './shared/assert-never.js';
import { getVsCodeApi } from './shared/vscode-api.js';
import { installErrorHandler } from './shared/error-handler.js';
import { DataTable } from './shared/data-table.js';
import { SolutionFilter } from './shared/solution-filter.js';

const vscode = getVsCodeApi<EnvironmentVariablesPanelWebviewToHost>();
installErrorHandler((msg) => vscode.postMessage(msg as EnvironmentVariablesPanelWebviewToHost));
const content = document.getElementById('content') as HTMLElement;
const statusText = document.getElementById('status-text') as HTMLElement;
const refreshBtn = document.getElementById('refresh-btn') as HTMLElement;
const exportBtn = document.getElementById('export-btn') as HTMLElement;
const syncBtn = document.getElementById('sync-btn') as HTMLElement;
const makerBtn = document.getElementById('maker-btn') as HTMLElement;
const searchInput = document.getElementById('search-input') as HTMLInputElement;
const detailPane = document.getElementById('detail-pane') as HTMLElement;
const detailContent = document.getElementById('detail-content') as HTMLElement;
const detailClose = document.getElementById('detail-close') as HTMLElement;

// ── Edit dialog elements ──
const editDialog = document.getElementById('edit-dialog') as HTMLElement;
const editDialogTitle = document.getElementById('edit-dialog-title') as HTMLElement;
const editDialogInputContainer = document.getElementById('edit-dialog-input-container') as HTMLElement;
const editDialogSave = document.getElementById('edit-dialog-save') as HTMLElement;
const editDialogCancel = document.getElementById('edit-dialog-cancel') as HTMLElement;
const editDialogClose = document.getElementById('edit-dialog-close') as HTMLElement;

let editingSchemaName: string | null = null;
let editingType: string | null = null;

// ── Data state ──
let totalCount = 0;
let includeInactive = false;

// ── Inactive toggle ──
const inactiveToggle = document.getElementById('inactive-toggle') as HTMLElement;
const inactiveToggleLabel = document.getElementById('inactive-toggle-label') as HTMLElement;
inactiveToggle.addEventListener('click', () => {
    includeInactive = !includeInactive;
    inactiveToggleLabel.textContent = includeInactive ? 'All' : 'Active Only';
    inactiveToggle.classList.toggle('active', includeInactive);
    inactiveToggle.title = includeInactive
        ? 'Showing all variables including deactivated. Click to show active only.'
        : 'Active = enabled in Dataverse (statecode 0). Toggle to include deactivated variables.';
    vscode.postMessage({ command: 'setIncludeInactive', includeInactive });
});

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

// ── Solution filter ──
const solutionFilterContainer = document.getElementById('solution-filter-container') as HTMLElement;
const solutionFilter = new SolutionFilter(solutionFilterContainer, {
    onChange: (solutionId) => {
        vscode.postMessage({ command: 'filterBySolution', solutionId });
    },
    getState: () => vscode.getState() as Record<string, unknown> | undefined,
    setState: (state) => vscode.setState(state),
    storageKey: 'environmentVariables.solutionFilter',
});

// Request solution list on load
vscode.postMessage({ command: 'requestSolutionList' });

// ── Value rendering helpers ──
function renderCurrentValue(v: EnvironmentVariableViewDto): string {
    if (v.isMissing) {
        return '<span class="value-missing" title="Required value is missing">Missing</span>';
    }
    if (v.hasOverride) {
        return '<span class="value-override" title="Value has been overridden">' + escapeHtml(v.currentValue ?? '\u2014') + '</span>';
    }
    return escapeHtml(v.currentValue ?? '\u2014');
}

// ── DataTable setup ──
const table = new DataTable<EnvironmentVariableViewDto>({
    container: content,
    columns: [
        {
            key: 'schemaName',
            label: 'Schema Name',
            render: (v) => escapeHtml(v.schemaName),
        },
        {
            key: 'displayName',
            label: 'Display Name',
            render: (v) => escapeHtml(v.displayName ?? '\u2014'),
        },
        {
            key: 'type',
            label: 'Type',
            render: (v) => escapeHtml(v.type),
            className: '100px',
        },
        {
            key: 'defaultValue',
            label: 'Default Value',
            render: (v) => escapeHtml(v.defaultValue ?? '\u2014'),
        },
        {
            key: 'currentValue',
            label: 'Current Value',
            render: renderCurrentValue,
        },
        {
            key: 'modifiedOn',
            label: 'Modified On',
            render: (v) => escapeHtml(formatDateTime(v.modifiedOn)),
            className: '140px',
        },
        {
            key: 'isManaged',
            label: 'Managed',
            render: (v) => escapeHtml(v.isManaged ? 'Yes' : 'No'),
            className: '80px',
        },
    ],
    getRowId: (v) => v.schemaName,
    onRowClick: (v) => {
        vscode.postMessage({ command: 'selectVariable', schemaName: v.schemaName });
    },
    defaultSortKey: 'schemaName',
    defaultSortDirection: 'asc',
    statusEl: statusText,
    formatStatus: (items) => {
        const overridden = items.filter(v => v.hasOverride).length;
        const missing = items.filter(v => v.isMissing).length;
        const noun = 'environment variable' + (totalCount !== 1 ? 's' : '');
        let countStr: string;
        if (items.length < totalCount) {
            countStr = items.length + ' of ' + totalCount + ' ' + noun;
        } else {
            countStr = totalCount + ' ' + noun;
        }
        const parts = [countStr];
        if (overridden > 0) parts.push(overridden + ' overridden');
        if (missing > 0) parts.push(missing + ' missing');
        return parts.join(' \u2014 ');
    },
    emptyMessage: 'No environment variables found',
});

// ── Button handlers ──
refreshBtn.addEventListener('click', () => {
    (refreshBtn as HTMLButtonElement).disabled = true;
    refreshBtn.textContent = 'Loading...';
    vscode.postMessage({ command: 'refresh' });
});

exportBtn.addEventListener('click', () => {
    vscode.postMessage({ command: 'exportDeploymentSettings' });
});

syncBtn.addEventListener('click', () => {
    (syncBtn as HTMLButtonElement).disabled = true;
    syncBtn.textContent = 'Syncing...';
    vscode.postMessage({ command: 'syncDeploymentSettings' });
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

// ── Search filter ──
const searchFilterCount = document.createElement('span');
searchFilterCount.className = 'filter-count-label';
searchInput.insertAdjacentElement('afterend', searchFilterCount);
const searchFilter = new FilterBar<EnvironmentVariableViewDto>({
    input: searchInput,
    countEl: searchFilterCount,
    itemLabel: 'variables',
    getSearchableText: (v) => [
        v.schemaName,
        v.displayName ?? '',
        v.currentValue ?? '',
    ],
    onFilter: (filtered) => {
        table.setItems(filtered);
    },
});

// ── Edit dialog handlers ──
editDialogCancel.addEventListener('click', () => {
    editDialog.style.display = 'none';
    editingSchemaName = null;
});

editDialogClose.addEventListener('click', () => {
    editDialog.style.display = 'none';
    editingSchemaName = null;
});

editDialogSave.addEventListener('click', () => {
    if (!editingSchemaName) return;
    const input = editDialogInputContainer.querySelector<HTMLInputElement | HTMLSelectElement | HTMLTextAreaElement>('input, select, textarea');
    if (!input) return;
    const val = input.value;

    // Type-aware validation (AC-EV-05)
    if (editingType === 'json' && val.trim()) {
        try { JSON.parse(val); } catch {
            const existing = editDialogInputContainer.querySelector('.edit-validation-error');
            if (existing) existing.remove();
            const err = document.createElement('div');
            err.className = 'edit-validation-error';
            err.textContent = 'Invalid JSON syntax';
            editDialogInputContainer.appendChild(err);
            return;
        }
    }
    if (editingType === 'number' && val.trim() && isNaN(Number(val))) {
        const existing = editDialogInputContainer.querySelector('.edit-validation-error');
        if (existing) existing.remove();
        const err = document.createElement('div');
        err.className = 'edit-validation-error';
        err.textContent = 'Invalid number';
        editDialogInputContainer.appendChild(err);
        return;
    }

    // Clear any previous validation error
    const existing = editDialogInputContainer.querySelector('.edit-validation-error');
    if (existing) existing.remove();

    vscode.postMessage({ command: 'saveVariable', schemaName: editingSchemaName, value: val });
});

// ── Keyboard shortcuts ──
document.addEventListener('keydown', (e) => {
    if (e.key === 'Escape') {
        if (editDialog.style.display !== 'none') {
            editDialog.style.display = 'none';
            editingSchemaName = null;
        } else if (detailPane.style.display !== 'none') {
            detailPane.style.display = 'none';
        }
    }
});

// ── Detail rendering ──
function renderDetail(detail: EnvironmentVariableDetailViewDto): void {
    let html = '<div class="detail-section">';
    html += '<div class="detail-row"><span class="detail-label">Schema Name:</span> <span class="detail-value">' + escapeHtml(detail.schemaName) + '</span></div>';
    html += '<div class="detail-row"><span class="detail-label">Display Name:</span> <span class="detail-value">' + escapeHtml(detail.displayName ?? '\u2014') + '</span></div>';
    html += '<div class="detail-row"><span class="detail-label">Type:</span> <span class="detail-value">' + escapeHtml(detail.type) + '</span></div>';
    html += '<div class="detail-row"><span class="detail-label">Default Value:</span> <span class="detail-value">' + escapeHtml(detail.defaultValue ?? '\u2014') + '</span></div>';
    html += '<div class="detail-row"><span class="detail-label">Current Value:</span> <span class="detail-value">' + renderCurrentValue(detail) + '</span></div>';
    html += '<div class="detail-row"><span class="detail-label">Managed:</span> <span class="detail-value">' + escapeHtml(detail.isManaged ? 'Yes' : 'No') + '</span></div>';
    html += '<div class="detail-row"><span class="detail-label">Required:</span> <span class="detail-value">' + escapeHtml(detail.isRequired ? 'Yes' : 'No') + '</span></div>';
    html += '<div class="detail-row"><span class="detail-label">Has Override:</span> <span class="detail-value">' + escapeHtml(detail.hasOverride ? 'Yes' : 'No') + '</span></div>';
    if (detail.description) {
        html += '<div class="detail-row"><span class="detail-label">Description:</span> <span class="detail-value">' + escapeHtml(detail.description) + '</span></div>';
    }
    if (detail.createdOn) {
        html += '<div class="detail-row"><span class="detail-label">Created On:</span> <span class="detail-value">' + escapeHtml(formatDateTime(detail.createdOn)) + '</span></div>';
    }
    if (detail.modifiedOn) {
        html += '<div class="detail-row"><span class="detail-label">Modified On:</span> <span class="detail-value">' + escapeHtml(formatDateTime(detail.modifiedOn)) + '</span></div>';
    }
    html += '</div>';

    // Edit button for editable types
    const editableTypes = ['String', 'Number', 'Boolean', 'JSON'];
    const lower = detail.type.toLowerCase();
    if (editableTypes.some(t => t.toLowerCase() === lower)) {
        html += '<div class="detail-actions">';
        html += '<button class="detail-edit-btn" id="detail-edit-btn">Edit Value</button>';
        html += '</div>';
    }

    detailContent.innerHTML = html;
    detailPane.style.display = '';

    // Wire edit button
    const editBtn = document.getElementById('detail-edit-btn');
    if (editBtn) {
        editBtn.addEventListener('click', () => {
            vscode.postMessage({ command: 'editVariable', schemaName: detail.schemaName });
        });
    }
}

// ── Edit dialog rendering ──
function showEditDialog(schemaName: string, displayName: string | null, type: string, currentValue: string | null): void {
    editingSchemaName = schemaName;
    editingType = type.toLowerCase();
    editDialogTitle.textContent = 'Edit: ' + (displayName ?? schemaName);
    const lower = editingType;
    let inputHtml: string;

    if (lower === 'boolean') {
        const isTrue = currentValue?.toLowerCase() === 'true';
        inputHtml = '<select class="edit-input">'
            + '<option value="true"' + (isTrue ? ' selected' : '') + '>true</option>'
            + '<option value="false"' + (!isTrue ? ' selected' : '') + '>false</option>'
            + '</select>';
    } else if (lower === 'number') {
        inputHtml = '<input type="number" class="edit-input" value="' + escapeHtml(currentValue ?? '') + '" />';
    } else if (lower === 'json') {
        inputHtml = '<textarea class="edit-input edit-textarea">' + escapeHtml(currentValue ?? '') + '</textarea>';
    } else if (lower === 'datasource' || lower === 'secret') {
        inputHtml = '<div class="edit-readonly">This variable type cannot be edited here.</div>';
        editDialogSave.style.display = 'none';
    } else {
        // String or unknown
        inputHtml = '<input type="text" class="edit-input" value="' + escapeHtml(currentValue ?? '') + '" />';
    }

    editDialogInputContainer.innerHTML = inputHtml;
    editDialogSave.style.display = '';
    if (lower === 'datasource' || lower === 'secret') {
        editDialogSave.style.display = 'none';
    }
    editDialog.style.display = '';
}

// ── Message handling ──
window.addEventListener('message', (event: MessageEvent<EnvironmentVariablesPanelHostToWebview>) => {
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
        case 'environmentVariablesLoaded':
            (refreshBtn as HTMLButtonElement).disabled = false;
            refreshBtn.textContent = 'Refresh';
            totalCount = msg.totalCount;
            searchFilter.setItems(msg.variables);
            detailPane.style.display = 'none';
            {
                const reconnectBanner = document.getElementById('reconnect-banner');
                if (reconnectBanner) reconnectBanner.style.display = 'none';
            }
            break;
        case 'environmentVariableDetailLoaded':
            renderDetail(msg.detail);
            break;
        case 'editVariableDialog':
            showEditDialog(msg.schemaName, msg.displayName, msg.type, msg.currentValue);
            break;
        case 'variableSaved':
            editDialog.style.display = 'none';
            editingSchemaName = null;
            break;
        case 'solutionListLoaded':
            solutionFilter.setSolutions(msg.solutions);
            break;
        case 'deploymentSettingsExported':
            // No special webview handling needed -- user saw the save dialog
            break;
        case 'deploymentSettingsSynced':
            (syncBtn as HTMLButtonElement).disabled = false;
            syncBtn.textContent = 'Sync Settings';
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
        case 'loading':
            content.innerHTML = '<div class="loading-state"><div class="spinner"></div><div>Loading environment variables...</div></div>';
            statusText.textContent = 'Loading...';
            break;
        case 'error':
            (refreshBtn as HTMLButtonElement).disabled = false;
            refreshBtn.textContent = 'Refresh';
            (syncBtn as HTMLButtonElement).disabled = false;
            syncBtn.textContent = 'Sync Settings';
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
