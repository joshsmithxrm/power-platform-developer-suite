// plugin-traces-panel.ts
// External webview script for the Plugin Traces panel.
// Built by esbuild as IIFE for browser, loaded via <script src="...">.

import { escapeHtml, escapeAttr } from './shared/dom-utils.js';
import type {
    PluginTracesPanelWebviewToHost,
    PluginTracesPanelHostToWebview,
    PluginTraceViewDto,
    PluginTraceDetailViewDto,
    TimelineNodeViewDto,
    TraceFilterViewDto,
} from './shared/message-types.js';
import { assertNever } from './shared/assert-never.js';
import { getVsCodeApi } from './shared/vscode-api.js';
import { installErrorHandler } from './shared/error-handler.js';
import { DataTable } from './shared/data-table.js';

const vscode = getVsCodeApi<PluginTracesPanelWebviewToHost>();
installErrorHandler((msg) => vscode.postMessage(msg as PluginTracesPanelWebviewToHost));

// ── DOM element references ──────────────────────────────────────────────
const filterBar = document.getElementById('filter-bar') as HTMLElement;
const tablePane = document.getElementById('table-pane') as HTMLElement;
const detailPane = document.getElementById('detail-pane') as HTMLElement;
const statusText = document.getElementById('status-text') as HTMLElement;
const refreshBtn = document.getElementById('refresh-btn') as HTMLElement;
const deleteBtn = document.getElementById('delete-btn') as HTMLElement;
const traceLevelBtn = document.getElementById('trace-level-btn') as HTMLElement;
const traceLevelIndicator = document.getElementById('trace-level-indicator') as HTMLElement;
const autoRefreshSelect = document.getElementById('auto-refresh-select') as HTMLSelectElement;

// Detail pane tabs
const detailTabs = document.querySelectorAll<HTMLElement>('.detail-tab');
const detailTabContents = document.querySelectorAll<HTMLElement>('.detail-tab-content');
const detailTabDetails = document.getElementById('tab-details') as HTMLElement;
const detailTabException = document.getElementById('tab-exception') as HTMLElement;
const detailTabMessageBlock = document.getElementById('tab-message-block') as HTMLElement;
const detailTabConfig = document.getElementById('tab-config') as HTMLElement;
const detailTabTimeline = document.getElementById('tab-timeline') as HTMLElement;

// ── Search input ─────────────────────────────────────────────────────────
const searchInput = document.getElementById('search-input') as HTMLInputElement;

// ── Environment picker ──────────────────────────────────────────────────
const envPickerBtn = document.getElementById('env-picker-btn') as HTMLElement;
const envPickerName = document.getElementById('env-picker-name') as HTMLElement;
envPickerBtn.addEventListener('click', () => {
    vscode.postMessage({ command: 'requestEnvironmentList' });
});
function updateEnvironmentDisplay(name: string | null): void {
    envPickerName.textContent = name || 'No environment';
}

// ── Filter bar controller ───────────────────────────────────────────────
const filterEntity = document.getElementById('filter-entity') as HTMLInputElement;
const filterMessage = document.getElementById('filter-message') as HTMLInputElement;
const filterPlugin = document.getElementById('filter-plugin') as HTMLInputElement;
const filterMode = document.getElementById('filter-mode') as HTMLSelectElement;
const filterExceptions = document.getElementById('filter-exceptions') as HTMLInputElement;
const filterStartDate = document.getElementById('filter-start-date') as HTMLInputElement;
const filterEndDate = document.getElementById('filter-end-date') as HTMLInputElement;

let filterDebounceTimer: ReturnType<typeof setTimeout> | null = null;

function collectFilter(): TraceFilterViewDto {
    const filter: TraceFilterViewDto = {};
    if (filterEntity.value.trim()) filter.primaryEntity = filterEntity.value.trim();
    if (filterMessage.value.trim()) filter.messageName = filterMessage.value.trim();
    if (filterPlugin.value.trim()) filter.typeName = filterPlugin.value.trim();
    if (filterMode.value && filterMode.value !== 'All') filter.mode = filterMode.value;
    if (filterExceptions.checked) filter.hasException = true;

    // Date range from inputs
    if (filterStartDate.value) {
        filter.startDate = new Date(filterStartDate.value).toISOString();
    }
    if (filterEndDate.value) {
        filter.endDate = new Date(filterEndDate.value).toISOString();
    }

    // Merge active quick filter state
    const longRunningPill = document.getElementById('qf-long-running') as HTMLElement;
    if (longRunningPill.classList.contains('active')) {
        filter.minDurationMs = 1000;
    }
    return filter;
}

function applyFilterDebounced(): void {
    if (filterDebounceTimer) clearTimeout(filterDebounceTimer);
    filterDebounceTimer = setTimeout(() => {
        vscode.postMessage({ command: 'applyFilter', filter: collectFilter() });
    }, 300);
}

filterEntity.addEventListener('input', applyFilterDebounced);
filterMessage.addEventListener('input', applyFilterDebounced);
filterPlugin.addEventListener('input', applyFilterDebounced);
filterMode.addEventListener('change', applyFilterDebounced);
filterExceptions.addEventListener('change', () => {
    // Sync the Exceptions Only pill with the checkbox
    const exPill = document.getElementById('qf-exceptions') as HTMLElement;
    if (filterExceptions.checked) {
        exPill.classList.add('active');
    } else {
        exPill.classList.remove('active');
    }
    applyFilterDebounced();
});
filterStartDate.addEventListener('change', applyFilterDebounced);
filterEndDate.addEventListener('change', applyFilterDebounced);

// Quick filter pills
const qfLastHour = document.getElementById('qf-last-hour') as HTMLElement;
const qfExceptions = document.getElementById('qf-exceptions') as HTMLElement;
const qfLongRunning = document.getElementById('qf-long-running') as HTMLElement;
const qfClearAll = document.getElementById('qf-clear-all') as HTMLElement;

qfLastHour.addEventListener('click', () => {
    qfLastHour.classList.toggle('active');
    if (qfLastHour.classList.contains('active')) {
        filterStartDate.value = new Date(Date.now() - 3600000).toISOString().slice(0, 16);
        filterEndDate.value = '';
    } else {
        filterStartDate.value = '';
        filterEndDate.value = '';
    }
    applyFilterDebounced();
});

qfExceptions.addEventListener('click', () => {
    qfExceptions.classList.toggle('active');
    filterExceptions.checked = qfExceptions.classList.contains('active');
    applyFilterDebounced();
});

qfLongRunning.addEventListener('click', () => {
    qfLongRunning.classList.toggle('active');
    applyFilterDebounced();
});

qfClearAll.addEventListener('click', () => {
    filterEntity.value = '';
    filterMessage.value = '';
    filterPlugin.value = '';
    filterMode.value = '';
    filterExceptions.checked = false;
    filterStartDate.value = '';
    filterEndDate.value = '';
    qfLastHour.classList.remove('active');
    qfExceptions.classList.remove('active');
    qfLongRunning.classList.remove('active');
    vscode.postMessage({ command: 'applyFilter', filter: {} });
});

// Filter collapse toggle
const filterToggleBtn = document.getElementById('filter-toggle-btn') as HTMLElement;
filterToggleBtn.addEventListener('click', () => {
    filterBar.classList.toggle('collapsed');
    filterToggleBtn.textContent = filterBar.classList.contains('collapsed') ? 'Show Filters' : 'Hide Filters';
});

// ── Format duration helper ──────────────────────────────────────────────
function formatDuration(ms: number | null): string {
    if (ms === null || ms === undefined) return '\u2014';
    if (ms < 1000) return ms + 'ms';
    return (ms / 1000).toFixed(2) + 's';
}

function formatDateTime(isoString: string | null | undefined): string {
    if (!isoString) return '';
    try {
        return new Date(isoString).toLocaleString(undefined, {
            year: 'numeric', month: 'short', day: 'numeric',
            hour: '2-digit', minute: '2-digit', second: '2-digit',
        });
    } catch {
        return isoString;
    }
}

// ── DataTable setup ─────────────────────────────────────────────────────
let selectedTraceId: string | null = null;
let currentTraces: PluginTraceViewDto[] = [];

const table = new DataTable<PluginTraceViewDto>({
    container: tablePane,
    columns: [
        {
            key: 'status',
            label: 'Status',
            render: (item) => '<span class="trace-status-cell">'
                + '<span class="trace-status-icon ' + escapeAttr(item.hasException ? 'exception' : 'success') + '"></span>'
                + '<span class="trace-status-label">' + escapeHtml(item.hasException ? 'Exception' : 'Success') + '</span>'
                + '</span>',
            className: '100px',
            sortable: true,
        },
        {
            key: 'createdOn',
            label: 'Time',
            render: (item) => escapeHtml(formatDateTime(item.createdOn)),
            className: '140px',
        },
        {
            key: 'durationMs',
            label: 'Duration',
            render: (item) => {
                const text = escapeHtml(formatDuration(item.durationMs));
                if (item.durationMs !== null && item.durationMs > 1000) {
                    return '<span style="color: var(--vscode-editorWarning-foreground, #cca700);">' + text + '</span>';
                }
                return text;
            },
            className: '80px',
        },
        {
            key: 'typeName',
            label: 'Plugin',
            render: (item) => '<span title="' + escapeAttr(item.typeName) + '">' + escapeHtml(item.typeName) + '</span>',
            className: '200px',
        },
        {
            key: 'primaryEntity',
            label: 'Entity',
            render: (item) => escapeHtml(item.primaryEntity || '\u2014'),
            className: '120px',
        },
        {
            key: 'messageName',
            label: 'Message',
            render: (item) => escapeHtml(item.messageName || '\u2014'),
            className: '100px',
        },
        {
            key: 'depth',
            label: 'Depth',
            render: (item) => escapeHtml(String(item.depth)),
            className: '50px',
        },
        {
            key: 'mode',
            label: 'Mode',
            render: (item) => escapeHtml(item.mode),
            className: '60px',
        },
    ],
    getRowId: (item) => item.id,
    onRowClick: (item) => {
        selectedTraceId = item.id;
        vscode.postMessage({ command: 'selectTrace', id: item.id });
    },
    defaultSortKey: 'createdOn',
    defaultSortDirection: 'desc',
    statusEl: statusText,
    formatStatus: (items) => {
        const exceptions = items.filter(t => t.hasException).length;
        const parts = [items.length + ' trace' + (items.length !== 1 ? 's' : '')];
        if (exceptions > 0) parts.push(exceptions + ' exception' + (exceptions !== 1 ? 's' : ''));
        return parts.join(' \u2014 ');
    },
    emptyMessage: 'No plugin traces found',
});

/** Apply client-side search filter and re-render the table. */
function applySearchFilter(): void {
    const query = searchInput.value.trim().toLowerCase();
    if (!query) {
        table.setItems(currentTraces);
    } else {
        const filtered = currentTraces.filter(t =>
            t.typeName.toLowerCase().includes(query)
            || (t.primaryEntity && t.primaryEntity.toLowerCase().includes(query))
            || (t.messageName && t.messageName.toLowerCase().includes(query))
        );
        table.setItems(filtered);
    }
    applyRowClasses();
}

function applyRowClasses(): void {
    tablePane.querySelectorAll<HTMLElement>('.data-table-row').forEach(row => {
        const id = row.dataset.id;
        const item = currentTraces.find(t => t.id === id);
        if (!item) return;
        if (item.hasException) row.classList.add('trace-row-exception');
        if (item.durationMs !== null && item.durationMs > 1000) row.classList.add('trace-row-slow');
    });
}

// ── Split pane resize ───────────────────────────────────────────────────
const resizeHandle = document.getElementById('resize-handle') as HTMLElement;

interface SplitState {
    tableBasis: string;
    detailBasis: string;
}

function restoreSplitState(): void {
    const state = vscode.getState() as { split?: SplitState } | null;
    if (state?.split) {
        tablePane.style.flexBasis = state.split.tableBasis;
        detailPane.style.flexBasis = state.split.detailBasis;
    }
}

restoreSplitState();

resizeHandle.addEventListener('mousedown', (e: MouseEvent) => {
    e.preventDefault();
    resizeHandle.classList.add('active');
    const startY = e.clientY;
    const tablePaneRect = tablePane.getBoundingClientRect();
    const detailPaneRect = detailPane.getBoundingClientRect();
    const startTableHeight = tablePaneRect.height;
    const startDetailHeight = detailPaneRect.height;

    function onMouseMove(ev: MouseEvent): void {
        const delta = ev.clientY - startY;
        const newTableHeight = Math.max(100, startTableHeight + delta);
        const newDetailHeight = Math.max(100, startDetailHeight - delta);
        tablePane.style.flexBasis = newTableHeight + 'px';
        detailPane.style.flexBasis = newDetailHeight + 'px';
    }

    function onMouseUp(): void {
        resizeHandle.classList.remove('active');
        document.removeEventListener('mousemove', onMouseMove);
        document.removeEventListener('mouseup', onMouseUp);
        // Persist split ratio
        const currentState = (vscode.getState() as Record<string, unknown>) || {};
        vscode.setState({
            ...currentState,
            split: {
                tableBasis: tablePane.style.flexBasis,
                detailBasis: detailPane.style.flexBasis,
            },
        });
    }

    document.addEventListener('mousemove', onMouseMove);
    document.addEventListener('mouseup', onMouseUp);
});

// ── Detail pane tab switching ───────────────────────────────────────────
let currentDetail: PluginTraceDetailViewDto | null = null;

function activateTab(tabName: string): void {
    detailTabs.forEach(t => {
        if (t.dataset.tab === tabName) {
            t.classList.add('active');
        } else {
            t.classList.remove('active');
        }
    });
    detailTabContents.forEach(c => {
        if (c.id === 'tab-' + tabName) {
            c.classList.add('active');
        } else {
            c.classList.remove('active');
        }
    });

    // Load timeline on tab activation
    if (tabName === 'timeline' && currentDetail?.correlationId) {
        vscode.postMessage({ command: 'loadTimeline', correlationId: currentDetail.correlationId });
    }
}

detailTabs.forEach(tab => {
    tab.addEventListener('click', () => {
        const tabName = tab.dataset.tab;
        if (tabName) activateTab(tabName);
    });
});

// ── Detail pane render functions ────────────────────────────────────────
function renderDetailsTab(trace: PluginTraceDetailViewDto): void {
    const kvPairs: [string, string][] = [
        ['Type', trace.typeName],
        ['Message', trace.messageName || '\u2014'],
        ['Entity', trace.primaryEntity || '\u2014'],
        ['Mode', trace.mode],
        ['Operation', trace.operationType],
        ['Depth', String(trace.depth)],
        ['Duration', formatDuration(trace.durationMs)],
        ['Created', formatDateTime(trace.createdOn)],
        ['Correlation ID', trace.correlationId || '\u2014'],
        ['Request ID', trace.requestId || '\u2014'],
        ['Constructor', formatDuration(trace.constructorDurationMs)],
        ['Execution Start', formatDateTime(trace.executionStartTime)],
    ];

    let html = '<div class="detail-kv">';
    for (const [label, value] of kvPairs) {
        html += '<span class="detail-kv-label">' + escapeHtml(label) + '</span>';
        html += '<span>' + escapeHtml(value) + '</span>';
    }
    html += '</div>';
    detailTabDetails.innerHTML = html;
}

function renderExceptionTab(trace: PluginTraceDetailViewDto): void {
    detailTabException.innerHTML = '';
    const pre = document.createElement('pre');
    pre.className = 'monospace-content';
    pre.textContent = trace.exceptionDetails || 'No exception';
    detailTabException.appendChild(pre);
}

function renderMessageBlockTab(trace: PluginTraceDetailViewDto): void {
    detailTabMessageBlock.innerHTML = '';
    const pre = document.createElement('pre');
    pre.className = 'monospace-content';
    pre.textContent = trace.messageBlock || 'No message block';
    detailTabMessageBlock.appendChild(pre);
}

function renderConfigTab(trace: PluginTraceDetailViewDto): void {
    detailTabConfig.innerHTML = '';

    const unsecuredLabel = document.createElement('h4');
    unsecuredLabel.textContent = 'Unsecured Configuration';
    unsecuredLabel.style.marginTop = '0';
    detailTabConfig.appendChild(unsecuredLabel);

    const unsecuredPre = document.createElement('pre');
    unsecuredPre.className = 'monospace-content';
    unsecuredPre.textContent = trace.configuration || 'No configuration';
    detailTabConfig.appendChild(unsecuredPre);

    const securedLabel = document.createElement('h4');
    securedLabel.textContent = 'Secured Configuration';
    detailTabConfig.appendChild(securedLabel);

    const securedPre = document.createElement('pre');
    securedPre.className = 'monospace-content';
    securedPre.textContent = trace.secureConfiguration || 'No secured configuration';
    detailTabConfig.appendChild(securedPre);
}

// ── Timeline waterfall renderer ─────────────────────────────────────────
interface FlatTimelineRow {
    node: TimelineNodeViewDto;
}

function flattenTimeline(nodes: TimelineNodeViewDto[]): FlatTimelineRow[] {
    const rows: FlatTimelineRow[] = [];
    function walk(nodeList: TimelineNodeViewDto[]): void {
        for (const node of nodeList) {
            rows.push({ node });
            if (node.children && node.children.length > 0) {
                walk(node.children);
            }
        }
    }
    walk(nodes);
    return rows;
}

function renderTimeline(nodes: TimelineNodeViewDto[]): void {
    const rows = flattenTimeline(nodes);

    if (rows.length === 0) {
        detailTabTimeline.innerHTML = '<div class="empty-state">' + escapeHtml('No timeline data available') + '</div>';
        return;
    }

    let html = '<div class="timeline-container">';
    html += '<div class="timeline-header">';
    html += '<span class="timeline-header-label">' + escapeHtml('Plugin') + '</span>';
    html += '<span class="timeline-header-bar">' + escapeHtml('Duration') + '</span>';
    html += '</div>';

    for (const row of rows) {
        const n = row.node;
        const paddingLeft = n.hierarchyDepth * 16;
        const labelText = escapeHtml(n.typeName + ' / ' + (n.messageName || '\u2014'));
        const barClass = n.hasException ? 'exception' : (n.durationMs !== null && n.durationMs > 1000 ? 'slow' : 'normal');
        const durationText = escapeHtml(n.durationMs !== null ? n.durationMs + 'ms' : '\u2014');

        html += '<div class="timeline-row">';
        html += '<span class="timeline-label" style="padding-left: ' + escapeAttr(String(paddingLeft)) + 'px;" title="' + escapeAttr(n.typeName) + '">' + labelText + '</span>';
        html += '<div class="timeline-bar-container">';
        html += '<div class="timeline-bar ' + escapeAttr(barClass) + '" style="left: ' + escapeAttr(String(n.offsetPercent)) + '%; width: ' + escapeAttr(String(n.widthPercent)) + '%;"></div>';
        html += '</div>';
        html += '<span class="timeline-duration">' + durationText + '</span>';
        html += '</div>';
    }

    html += '</div>';
    detailTabTimeline.innerHTML = html;
}

// ── Message handler ─────────────────────────────────────────────────────
window.addEventListener('message', (event: MessageEvent<PluginTracesPanelHostToWebview>) => {
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

        case 'tracesLoaded':
            (refreshBtn as HTMLButtonElement).disabled = false;
            refreshBtn.textContent = 'Refresh';
            currentTraces = msg.traces;
            {
                const previousSelectedId = selectedTraceId;
                // Apply search filter (renders filtered subset or all traces)
                applySearchFilter();
                // Preserve selection if the trace still exists
                if (previousSelectedId) {
                    const stillExists = msg.traces.some(t => t.id === previousSelectedId);
                    if (stillExists) {
                        selectedTraceId = previousSelectedId;
                        const row = tablePane.querySelector<HTMLElement>('[data-id="' + CSS.escape(previousSelectedId) + '"]');
                        if (row) {
                            row.classList.add('selected');
                        }
                    } else {
                        selectedTraceId = null;
                    }
                }
            }
            {
                const reconnectBanner = document.getElementById('reconnect-banner');
                if (reconnectBanner) reconnectBanner.style.display = 'none';
            }
            break;

        case 'traceDetailLoaded':
            currentDetail = msg.trace;
            renderDetailsTab(msg.trace);
            renderExceptionTab(msg.trace);
            renderMessageBlockTab(msg.trace);
            renderConfigTab(msg.trace);
            // Clear timeline tab — will be loaded on click
            detailTabTimeline.innerHTML = '<div class="empty-state">' + escapeHtml('Click the Timeline tab to load execution chain') + '</div>';
            // Show detail pane and select Details tab
            detailPane.classList.add('visible');
            activateTab('details');
            break;

        case 'timelineLoaded':
            renderTimeline(msg.nodes);
            break;

        case 'traceLevelLoaded':
            traceLevelBtn.textContent = 'Trace Level: ' + msg.level;
            traceLevelIndicator.textContent = msg.level;
            if (msg.levelValue === 0) {
                traceLevelIndicator.title = 'Trace level is Off \u2014 no new traces will be recorded';
                traceLevelIndicator.className = 'trace-level-indicator trace-level-off';
            } else {
                traceLevelIndicator.title = 'Trace level: ' + msg.level;
                traceLevelIndicator.className = 'trace-level-indicator trace-level-on';
            }
            // Highlight the active level in the dropdown
            traceLevelDropdown.querySelectorAll<HTMLElement>('.trace-level-dropdown-item').forEach(item => {
                if (item.dataset.level === msg.level) {
                    item.classList.add('active');
                } else {
                    item.classList.remove('active');
                }
            });
            break;

        case 'deleteComplete':
            statusText.textContent = msg.deletedCount + ' trace' + (msg.deletedCount !== 1 ? 's' : '') + ' deleted';
            setTimeout(() => {
                vscode.postMessage({ command: 'refresh' });
            }, 1000);
            break;

        case 'loading':
            tablePane.innerHTML = '<div class="loading-state"><div class="spinner"></div><div>Loading plugin traces...</div></div>';
            statusText.textContent = 'Loading...';
            break;

        case 'error':
            (refreshBtn as HTMLButtonElement).disabled = false;
            refreshBtn.textContent = 'Refresh';
            tablePane.innerHTML = '<div class="error-state">' + escapeHtml(msg.message) + '</div>';
            statusText.textContent = 'Error';
            break;

        case 'daemonReconnected':
            {
                const banner = document.getElementById('reconnect-banner');
                if (banner) banner.style.display = '';
            }
            break;

        default:
            assertNever(msg);
    }
});

// ── Toolbar handlers ────────────────────────────────────────────────────
refreshBtn.addEventListener('click', () => {
    (refreshBtn as HTMLButtonElement).disabled = true;
    refreshBtn.textContent = 'Loading...';
    vscode.postMessage({ command: 'refresh' });
});

const makerBtn = document.getElementById('maker-btn');
if (makerBtn) {
    makerBtn.addEventListener('click', () => {
        vscode.postMessage({ command: 'openInMaker' });
    });
}

autoRefreshSelect.addEventListener('change', () => {
    const value = autoRefreshSelect.value;
    const interval = value ? parseInt(value, 10) : null;
    vscode.postMessage({ command: 'setAutoRefresh', intervalSeconds: interval });

    // Visual indicator for active auto-refresh
    if (interval !== null) {
        autoRefreshSelect.classList.add('auto-refresh-active');
    } else {
        autoRefreshSelect.classList.remove('auto-refresh-active');
    }
});

// ── Search input handler ──────────────────────────────────────────────────
let searchDebounceTimer: ReturnType<typeof setTimeout> | null = null;
searchInput.addEventListener('input', () => {
    if (searchDebounceTimer) clearTimeout(searchDebounceTimer);
    searchDebounceTimer = setTimeout(() => {
        applySearchFilter();
    }, 200);
});

// ── Delete dropdown menu ─────────────────────────────────────────────────
const deleteDropdown = document.createElement('div');
deleteDropdown.className = 'delete-dropdown';
deleteDropdown.style.display = 'none';
deleteDropdown.innerHTML = [
    '<button class="delete-dropdown-item" id="delete-selected">Delete selected</button>',
    '<button class="delete-dropdown-item" id="delete-older-than">Delete older than...</button>',
].join('');
deleteBtn.parentElement!.style.position = 'relative';
deleteBtn.insertAdjacentElement('afterend', deleteDropdown);

deleteBtn.addEventListener('click', () => {
    const isVisible = deleteDropdown.style.display !== 'none';
    deleteDropdown.style.display = isVisible ? 'none' : '';
});

document.getElementById('delete-selected')!.addEventListener('click', () => {
    deleteDropdown.style.display = 'none';
    const selId = table.getSelectedId();
    if (selId) {
        vscode.postMessage({ command: 'deleteTraces', ids: [selId] });
    }
});

// Inline input for "Delete older than" (prompt() is unavailable in webviews)
const deleteOlderDialog = document.createElement('div');
deleteOlderDialog.className = 'delete-older-dialog';
deleteOlderDialog.style.display = 'none';
deleteOlderDialog.innerHTML = [
    '<label>Delete traces older than</label>',
    '<input type="number" id="delete-older-days" min="1" value="7" />',
    '<label>day(s)</label>',
    '<button id="delete-older-confirm" class="delete-dropdown-item">Delete</button>',
    '<button id="delete-older-cancel" class="delete-dropdown-item">Cancel</button>',
].join(' ');
deleteBtn.parentElement!.appendChild(deleteOlderDialog);

document.getElementById('delete-older-than')!.addEventListener('click', () => {
    deleteDropdown.style.display = 'none';
    deleteOlderDialog.style.display = '';
    const daysInput = document.getElementById('delete-older-days') as HTMLInputElement;
    daysInput.value = '7';
    daysInput.focus();
});

document.getElementById('delete-older-confirm')!.addEventListener('click', () => {
    const daysInput = document.getElementById('delete-older-days') as HTMLInputElement;
    const days = parseInt(daysInput.value, 10);
    deleteOlderDialog.style.display = 'none';
    if (!isNaN(days) && days > 0) {
        vscode.postMessage({ command: 'deleteOlderThan', days });
    }
});

document.getElementById('delete-older-cancel')!.addEventListener('click', () => {
    deleteOlderDialog.style.display = 'none';
});

// ── Export dropdown menu ─────────────────────────────────────────────────
const exportBtn = document.getElementById('export-btn') as HTMLElement;
const exportDropdown = document.createElement('div');
exportDropdown.className = 'export-dropdown';
exportDropdown.style.display = 'none';
exportDropdown.innerHTML = [
    '<div class="export-dropdown-item" data-format="csv">Export as CSV\u2026</div>',
    '<div class="export-dropdown-item" data-format="json">Export as JSON\u2026</div>',
    '<div class="export-dropdown-item" data-format="clipboard">Copy to Clipboard</div>',
].join('');
exportBtn.parentElement!.style.position = 'relative';
exportBtn.insertAdjacentElement('afterend', exportDropdown);

exportBtn.addEventListener('click', () => {
    const isVisible = exportDropdown.style.display !== 'none';
    exportDropdown.style.display = isVisible ? 'none' : '';
});

exportDropdown.addEventListener('click', (e) => {
    const target = (e.target as HTMLElement).closest<HTMLElement>('.export-dropdown-item');
    if (target?.dataset.format) {
        exportDropdown.style.display = 'none';
        vscode.postMessage({ command: 'exportTraces', format: target.dataset.format });
    }
});

// ── Trace Level dropdown menu ─────────────────────────────────────────────
const traceLevelDropdown = document.createElement('div');
traceLevelDropdown.className = 'trace-level-dropdown';
traceLevelDropdown.style.display = 'none';
traceLevelDropdown.innerHTML = [
    '<button class="trace-level-dropdown-item" data-level="Off">Off</button>',
    '<button class="trace-level-dropdown-item" data-level="Exception">Exception</button>',
    '<button class="trace-level-dropdown-item" data-level="All">All</button>',
].join('');
traceLevelBtn.parentElement!.style.position = 'relative';
traceLevelBtn.insertAdjacentElement('afterend', traceLevelDropdown);

// Keep the indicator after the dropdown in the DOM (move it)
traceLevelDropdown.insertAdjacentElement('afterend', traceLevelIndicator);

traceLevelBtn.addEventListener('click', () => {
    // Request fresh trace level data, then show the dropdown
    vscode.postMessage({ command: 'requestTraceLevel' });
    const isVisible = traceLevelDropdown.style.display !== 'none';
    traceLevelDropdown.style.display = isVisible ? 'none' : '';
});

traceLevelDropdown.addEventListener('click', (e) => {
    const target = (e.target as HTMLElement).closest<HTMLElement>('.trace-level-dropdown-item');
    if (target?.dataset.level) {
        traceLevelDropdown.style.display = 'none';
        vscode.postMessage({ command: 'setTraceLevel', level: target.dataset.level });
    }
});

// Close dropdowns when clicking elsewhere
document.addEventListener('click', (e) => {
    if (!deleteBtn.contains(e.target as Node) && !deleteDropdown.contains(e.target as Node)) {
        deleteDropdown.style.display = 'none';
    }
    if (!exportBtn.contains(e.target as Node) && !exportDropdown.contains(e.target as Node)) {
        exportDropdown.style.display = 'none';
    }
    if (!traceLevelBtn.contains(e.target as Node) && !traceLevelDropdown.contains(e.target as Node)) {
        traceLevelDropdown.style.display = 'none';
    }
});

// Reconnect banner refresh
const reconnectRefresh = document.getElementById('reconnect-refresh');
if (reconnectRefresh) {
    reconnectRefresh.addEventListener('click', (e) => {
        e.preventDefault();
        const banner = document.getElementById('reconnect-banner');
        if (banner) banner.style.display = 'none';
        vscode.postMessage({ command: 'refresh' });
    });
}

// ── Keyboard shortcuts ──────────────────────────────────────────────────
document.addEventListener('keydown', (e) => {
    // Escape = hide detail pane and clear selection
    if (e.key === 'Escape' && detailPane.classList.contains('visible')) {
        detailPane.classList.remove('visible');
        table.clearSelection();
        selectedTraceId = null;
        currentDetail = null;
    }

    // F5 = refresh
    if (e.key === 'F5') {
        e.preventDefault();
        vscode.postMessage({ command: 'refresh' });
    }
});

// ── Ready signal ────────────────────────────────────────────────────────
vscode.postMessage({ command: 'ready' });
