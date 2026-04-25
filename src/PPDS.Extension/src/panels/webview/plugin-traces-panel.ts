// plugin-traces-panel.ts
// External webview script for the Plugin Traces panel.
// Built by esbuild as IIFE for browser, loaded via <script src="...">.

import { escapeHtml, escapeAttr, formatDateTime } from './shared/dom-utils.js';
import type {
    PluginTracesPanelWebviewToHost,
    PluginTracesPanelHostToWebview,
    PluginTraceViewDto,
    PluginTraceDetailViewDto,
    TimelineNodeViewDto,
    QueryConditionViewDto,
    AdvancedQueryViewDto,
} from './shared/message-types.js';
import { assertNever } from './shared/assert-never.js';
import { getVsCodeApi } from './shared/vscode-api.js';
import { installErrorHandler } from './shared/error-handler.js';
import { DataTable, formatStatusCount } from './shared/data-table.js';

const vscode = getVsCodeApi<PluginTracesPanelWebviewToHost>();
installErrorHandler((msg) => vscode.postMessage(msg as PluginTracesPanelWebviewToHost));

// ── DOM element references ──────────────────────────────────────────────
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
const detailTabOverview = document.getElementById('tab-overview') as HTMLElement;
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
function updateEnvironmentDisplay(profileName: string | undefined, name: string | null): void {
    const env = name || 'No environment';
    envPickerName.textContent = profileName ? `${profileName} · ${env}` : env;
}

// ── Format helpers ──────────────────────────────────────────────────────
function formatDuration(ms: number | null): string {
    if (ms === null || ms === undefined) return '\u2014';
    if (ms < 1000) return ms + 'ms';
    return (ms / 1000).toFixed(2) + 's';
}

// ── DataTable setup (6d: Operation Type + Correlation ID columns) ─────
let selectedTraceId: string | null = null;
let currentTraces: PluginTraceViewDto[] = [];
let totalTraceCount = 0;

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
            className: '90px',
            sortable: true,
        },
        {
            key: 'createdOn',
            label: 'Time',
            render: (item) => escapeHtml(formatDateTime(item.createdOn)),
            sortValue: (item) => new Date(item.createdOn).getTime(),
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
            sortValue: (item) => item.durationMs ?? 0,
            className: '80px',
        },
        {
            key: 'operationType',
            label: 'Operation',
            render: (item) => escapeHtml(item.operationType || '\u2014'),
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
            sortValue: (item) => item.depth,
            className: '50px',
        },
        {
            key: 'mode',
            label: 'Mode',
            render: (item) => escapeHtml(item.mode),
            className: '60px',
        },
        {
            key: 'correlationId',
            label: 'Correlation ID',
            render: (item) => escapeHtml(item.correlationId || '\u2014'),
            className: '120px',
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
        // V-21: Show "X of Y traces" when search or filter is active
        const searchActive = searchInput.value.trim().length > 0;
        let base: string;
        if (searchActive || items.length < totalTraceCount) {
            base = formatStatusCount(items.length, totalTraceCount, 'trace');
        } else {
            base = items.length + ' trace' + (items.length !== 1 ? 's' : '');
        }
        if (exceptions > 0) base += ' \u2014 ' + exceptions + ' exception' + (exceptions !== 1 ? 's' : '');
        return base;
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
            || (t.correlationId && t.correlationId.toLowerCase().includes(query))
            || t.operationType.toLowerCase().includes(query)
            || t.mode.toLowerCase().includes(query)
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

// ── 6a: Split pane resize (horizontal) ───────────────────────────────
const resizeHandle = document.getElementById('resize-handle') as HTMLElement;

function restoreSplitState(): void {
    const state = vscode.getState() as { split?: { tableBasis: string; detailBasis: string } } | null;
    if (state?.split) {
        tablePane.style.flexBasis = state.split.tableBasis;
        detailPane.style.flexBasis = state.split.detailBasis;
    }
}

restoreSplitState();

resizeHandle.addEventListener('mousedown', (e: MouseEvent) => {
    e.preventDefault();
    resizeHandle.classList.add('active');
    const startX = e.clientX;
    const tablePaneRect = tablePane.getBoundingClientRect();
    const detailPaneRect = detailPane.getBoundingClientRect();
    const startTableWidth = tablePaneRect.width;
    const startDetailWidth = detailPaneRect.width;

    function onMouseMove(ev: MouseEvent): void {
        const delta = ev.clientX - startX;
        const newTableWidth = Math.max(200, startTableWidth + delta);
        const newDetailWidth = Math.max(200, startDetailWidth - delta);
        tablePane.style.flexBasis = newTableWidth + 'px';
        detailPane.style.flexBasis = newDetailWidth + 'px';
    }

    function onMouseUp(): void {
        resizeHandle.classList.remove('active');
        document.removeEventListener('mousemove', onMouseMove);
        document.removeEventListener('mouseup', onMouseUp);
        // Persist split ratio (PT-20)
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

// ── Detail pane close button (PT-59) ─────────────────────────────────
const detailCloseBtn = document.getElementById('detail-close-btn');
if (detailCloseBtn) {
    detailCloseBtn.addEventListener('click', () => {
        detailPane.classList.remove('visible');
        table.clearSelection();
        selectedTraceId = null;
        currentDetail = null;
    });
}

// ── Detail pane tab switching ───────────────────────────────────────
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

// ── 6c: Overview tab render ──────────────────────────────────────────
function renderOverviewTab(trace: PluginTraceDetailViewDto): void {
    detailTabOverview.innerHTML = '';

    // Status badge
    const statusSection = document.createElement('div');
    statusSection.className = 'overview-section';
    const badge = document.createElement('span');
    badge.className = 'overview-status-badge ' + (trace.hasException ? 'exception' : 'success');
    const icon = document.createElement('span');
    icon.className = 'trace-status-icon ' + (trace.hasException ? 'exception' : 'success');
    badge.appendChild(icon);
    const label = document.createElement('span');
    label.textContent = trace.hasException ? 'Exception' : 'Success';
    badge.appendChild(label);

    const metaEl = document.createElement('span');
    metaEl.style.marginLeft = '12px';
    metaEl.style.fontSize = '12px';
    metaEl.style.color = 'var(--vscode-descriptionForeground)';
    metaEl.textContent = trace.typeName + ' / ' + (trace.messageName || '\u2014')
        + ' (' + (trace.primaryEntity || '\u2014') + ') \u2014 '
        + formatDuration(trace.durationMs);

    statusSection.appendChild(badge);
    statusSection.appendChild(metaEl);
    detailTabOverview.appendChild(statusSection);

    // Exception text (if any)
    if (trace.exceptionDetails) {
        const excSection = document.createElement('div');
        excSection.className = 'overview-section';
        const excTitle = document.createElement('div');
        excTitle.className = 'overview-section-title';
        excTitle.textContent = 'Exception Details';
        excSection.appendChild(excTitle);
        const excPre = document.createElement('pre');
        excPre.className = 'monospace-content';
        excPre.setAttribute('data-selection-zone', 'exception-details');
        excPre.textContent = trace.exceptionDetails;
        excSection.appendChild(excPre);
        detailTabOverview.appendChild(excSection);
    }

    // Message block (if any)
    if (trace.messageBlock) {
        const msgSection = document.createElement('div');
        msgSection.className = 'overview-section';
        const msgTitle = document.createElement('div');
        msgTitle.className = 'overview-section-title';
        msgTitle.textContent = 'Message Block';
        msgSection.appendChild(msgTitle);
        const msgPre = document.createElement('pre');
        msgPre.className = 'monospace-content';
        msgPre.setAttribute('data-selection-zone', 'message-block');
        msgPre.textContent = trace.messageBlock;
        msgSection.appendChild(msgPre);
        detailTabOverview.appendChild(msgSection);
    }

    if (!trace.exceptionDetails && !trace.messageBlock) {
        const emptyNote = document.createElement('div');
        emptyNote.style.color = 'var(--vscode-descriptionForeground)';
        emptyNote.style.fontStyle = 'italic';
        emptyNote.style.marginTop = '12px';
        emptyNote.textContent = 'No exception or message block for this trace.';
        detailTabOverview.appendChild(emptyNote);
    }
}

// ── 6b: Detail tabs with all fields ──────────────────────────────────
function renderDetailsTab(trace: PluginTraceDetailViewDto): void {
    const kvPairs: [string, string][] = [
        ['Type', trace.typeName],
        ['Message', trace.messageName || '\u2014'],
        ['Entity', trace.primaryEntity || '\u2014'],
        ['Mode', trace.mode],
        ['Operation', trace.operationType],
        ['Stage', trace.stage || trace.operationType],
        ['Depth', String(trace.depth)],
        ['Duration', formatDuration(trace.durationMs)],
        ['Created', formatDateTime(trace.createdOn)],
        ['Correlation ID', trace.correlationId || '\u2014'],
        ['Request ID', trace.requestId || '\u2014'],
        ['Constructor Duration', formatDuration(trace.constructorDurationMs)],
        ['Execution Start', formatDateTime(trace.executionStartTime)],
        ['Constructor Start', formatDateTime(trace.constructorStartTime)],
        ['Is System Created', trace.isSystemCreated ? 'Yes' : 'No'],
        ['Created By', trace.createdById || '\u2014'],
        ['Created On Behalf By', trace.createdOnBehalfById || '\u2014'],
        ['Plugin Step ID', trace.pluginStepId || '\u2014'],
        ['Persistence Key', trace.persistenceKey || '\u2014'],
        ['Organization ID', trace.organizationId || '\u2014'],
        ['Profile', trace.profile || '\u2014'],
    ];

    // Build using DOM (S1: textContent, not innerHTML for user data)
    detailTabDetails.innerHTML = '';
    const grid = document.createElement('div');
    grid.className = 'detail-kv';
    for (const [lbl, value] of kvPairs) {
        const labelEl = document.createElement('span');
        labelEl.className = 'detail-kv-label';
        labelEl.textContent = lbl;
        const valueEl = document.createElement('span');
        valueEl.textContent = value;
        grid.appendChild(labelEl);
        grid.appendChild(valueEl);
    }
    detailTabDetails.appendChild(grid);
}

function renderExceptionTab(trace: PluginTraceDetailViewDto): void {
    detailTabException.innerHTML = '';
    const pre = document.createElement('pre');
    pre.className = 'monospace-content';
    pre.setAttribute('data-selection-zone', 'exception-details');
    pre.textContent = trace.exceptionDetails || 'No exception';
    detailTabException.appendChild(pre);
}

function renderMessageBlockTab(trace: PluginTraceDetailViewDto): void {
    detailTabMessageBlock.innerHTML = '';
    const pre = document.createElement('pre');
    pre.className = 'monospace-content';
    pre.setAttribute('data-selection-zone', 'message-block');
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

// ── 6e: Timeline waterfall renderer (overhaul) ──────────────────────
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
    detailTabTimeline.innerHTML = '';

    if (rows.length === 0) {
        const empty = document.createElement('div');
        empty.className = 'timeline-empty';
        const title = document.createElement('div');
        title.className = 'timeline-empty-title';
        title.textContent = 'No timeline data available';
        const hint = document.createElement('div');
        hint.className = 'timeline-empty-hint';
        hint.textContent = 'Timeline requires traces with a correlation ID. Select a trace that has related executions.';
        empty.appendChild(title);
        empty.appendChild(hint);
        detailTabTimeline.appendChild(empty);
        return;
    }

    const container = document.createElement('div');
    container.className = 'timeline-container';

    // Header with total duration + trace count
    const totalDurationMs = rows.reduce((sum, r) => sum + (r.node.durationMs || 0), 0);
    const headerInfo = document.createElement('div');
    headerInfo.className = 'timeline-header-info';
    const durSpan = document.createElement('span');
    durSpan.innerHTML = '<strong>Total Duration:</strong> ' + escapeHtml(formatDuration(totalDurationMs));
    const countSpan = document.createElement('span');
    countSpan.innerHTML = '<strong>Traces:</strong> ' + rows.length;
    headerInfo.appendChild(durSpan);
    headerInfo.appendChild(countSpan);
    container.appendChild(headerInfo);

    // Content
    const content = document.createElement('div');
    content.className = 'timeline-content';

    for (const row of rows) {
        const n = row.node;
        const depthClass = 'timeline-depth-' + Math.min(n.hierarchyDepth, 4);
        const statusClass = n.hasException ? 'exception' : 'success';

        const item = document.createElement('div');
        item.className = 'timeline-item ' + depthClass;
        item.dataset.traceId = n.traceId;

        // Header row
        const header = document.createElement('div');
        header.className = 'timeline-item-header';
        const titleEl = document.createElement('span');
        titleEl.className = 'timeline-item-title';
        titleEl.textContent = n.typeName;
        header.appendChild(titleEl);
        if (n.messageName) {
            const msgEl = document.createElement('span');
            msgEl.className = 'timeline-item-message';
            msgEl.textContent = n.messageName;
            header.appendChild(msgEl);
        }
        item.appendChild(header);

        // Duration bar
        const barContainer = document.createElement('div');
        barContainer.className = 'timeline-bar-container';
        const bar = document.createElement('div');
        bar.className = 'timeline-bar ' + statusClass;
        bar.style.left = n.offsetPercent + '%';
        bar.style.width = Math.max(0.5, n.widthPercent) + '%';
        bar.dataset.traceId = n.traceId;
        const fill = document.createElement('div');
        fill.className = 'timeline-bar-fill';
        bar.appendChild(fill);
        barContainer.appendChild(bar);
        item.appendChild(barContainer);

        // Metadata
        const meta = document.createElement('div');
        meta.className = 'timeline-item-meta';
        const durEl = document.createElement('span');
        durEl.className = 'timeline-item-duration';
        durEl.textContent = formatDuration(n.durationMs);
        meta.appendChild(durEl);
        const modeEl = document.createElement('span');
        modeEl.className = 'timeline-mode-badge';
        modeEl.textContent = 'D' + n.depth;
        meta.appendChild(modeEl);
        if (n.hasException) {
            const excEl = document.createElement('span');
            excEl.style.color = 'var(--vscode-errorForeground, #f44336)';
            excEl.textContent = '\u26A0 Exception';
            meta.appendChild(excEl);
        }
        item.appendChild(meta);

        // Click navigation: click bar → select the corresponding trace in the table
        item.addEventListener('click', () => {
            const traceId = n.traceId;
            selectedTraceId = traceId;
            // Select the row in the table
            tablePane.querySelectorAll<HTMLElement>('.data-table-row.selected').forEach(r => r.classList.remove('selected'));
            const targetRow = tablePane.querySelector<HTMLElement>('[data-id="' + CSS.escape(traceId) + '"]');
            if (targetRow) {
                targetRow.classList.add('selected');
                targetRow.scrollIntoView({ block: 'center', behavior: 'smooth' });
            }
            // Load this trace's detail
            vscode.postMessage({ command: 'selectTrace', id: traceId });
        });

        content.appendChild(item);
    }

    container.appendChild(content);

    // Legend
    const legend = document.createElement('div');
    legend.className = 'timeline-legend';
    legend.innerHTML = '<div class="timeline-legend-item"><div class="timeline-legend-swatch success"></div><span>Success</span></div>'
        + '<div class="timeline-legend-item"><div class="timeline-legend-swatch exception"></div><span>Exception</span></div>';
    container.appendChild(legend);

    detailTabTimeline.appendChild(container);
}

// ── 6f: Advanced Query Builder ──────────────────────────────────────
const filterPanel = document.getElementById('filter-panel') as HTMLElement;
const filterPanelHeader = document.getElementById('filter-panel-header') as HTMLElement;
const filterPanelBody = document.getElementById('filter-panel-body') as HTMLElement;
const filterPanelTabs = document.getElementById('filter-panel-tabs') as HTMLElement;
const filterConditions = document.getElementById('filter-conditions') as HTMLElement;
const queryPreviewText = document.getElementById('query-preview-text') as HTMLElement;

// Collapse/expand filter panel
filterPanelHeader.addEventListener('click', () => {
    filterPanel.classList.toggle('collapsed');
    // Persist
    const currentState = (vscode.getState() as Record<string, unknown>) || {};
    vscode.setState({ ...currentState, filterCollapsed: filterPanel.classList.contains('collapsed') });
});

// Restore filter panel state
{
    const state = vscode.getState() as { filterCollapsed?: boolean; filterHeight?: number } | null;
    if (state?.filterCollapsed === false) {
        filterPanel.classList.remove('collapsed');
    }
    if (state?.filterHeight) {
        filterPanel.style.height = state.filterHeight + 'px';
    }
}

// Filter panel tabs
filterPanelTabs.addEventListener('click', (e) => {
    const tab = (e.target as HTMLElement).closest<HTMLElement>('.filter-panel-tab');
    if (!tab) return;
    const tabName = tab.dataset.filterTab;
    if (!tabName) return;
    filterPanelTabs.querySelectorAll('.filter-panel-tab').forEach(t => t.classList.remove('active'));
    tab.classList.add('active');
    filterPanelBody.querySelectorAll<HTMLElement>('.filter-tab-content').forEach(c => {
        if (c.dataset.filterTab === tabName) {
            c.classList.add('active');
        } else {
            c.classList.remove('active');
        }
    });
    // Update query preview when switching to that tab
    if (tabName === 'preview') updateQueryPreview();
});

// Quick filter checkboxes — auto-apply
const quickFilterCheckboxes = filterPanelBody.querySelectorAll<HTMLInputElement>('[data-qf]');
quickFilterCheckboxes.forEach(cb => {
    cb.addEventListener('change', () => {
        // Mutually exclusive: exceptions and success
        if (cb.dataset.qf === 'exceptions' && cb.checked) {
            const successCb = filterPanelBody.querySelector<HTMLInputElement>('[data-qf="success"]');
            if (successCb) successCb.checked = false;
        }
        if (cb.dataset.qf === 'success' && cb.checked) {
            const excCb = filterPanelBody.querySelector<HTMLInputElement>('[data-qf="exceptions"]');
            if (excCb) excCb.checked = false;
        }
        // Mutually exclusive: async and sync
        if (cb.dataset.qf === 'async' && cb.checked) {
            const syncCb = filterPanelBody.querySelector<HTMLInputElement>('[data-qf="sync"]');
            if (syncCb) syncCb.checked = false;
        }
        if (cb.dataset.qf === 'sync' && cb.checked) {
            const asyncCb = filterPanelBody.querySelector<HTMLInputElement>('[data-qf="async"]');
            if (asyncCb) asyncCb.checked = false;
        }
        applyAdvancedFilter();
    });
});

// Condition field definitions
const FILTER_FIELDS = ['Plugin Name', 'Entity', 'Message', 'Status', 'Created On', 'Duration', 'Mode', 'Stage'] as const;
const FIELD_TYPES: Record<string, string> = {
    'Plugin Name': 'text', 'Entity': 'text', 'Message': 'text',
    'Status': 'enum', 'Mode': 'enum', 'Stage': 'enum',
    'Created On': 'date', 'Duration': 'number',
};
const OPERATORS_BY_TYPE: Record<string, string[]> = {
    text: ['Contains', 'Equals', 'Not Equals', 'Starts With', 'Ends With'],
    enum: ['Equals', 'Not Equals'],
    date: ['Equals', 'Greater Than', 'Less Than', 'Greater Than or Equal', 'Less Than or Equal'],
    number: ['Equals', 'Greater Than', 'Less Than', 'Greater Than or Equal', 'Less Than or Equal'],
};
const ENUM_OPTIONS: Record<string, string[]> = {
    'Status': ['Success', 'Exception'],
    'Mode': ['Sync', 'Async'],
    'Stage': ['Plugin', 'WorkflowActivity'],
};

let conditionIdCounter = 0;

function addConditionRow(): void {
    const id = 'cond-' + (conditionIdCounter++);
    const row = document.createElement('div');
    row.className = 'filter-condition-row';
    row.dataset.conditionId = id;

    // Enabled checkbox
    const enabledCb = document.createElement('input');
    enabledCb.type = 'checkbox';
    enabledCb.className = 'condition-enabled';
    enabledCb.checked = true;
    enabledCb.title = 'Enable/disable this condition';
    row.appendChild(enabledCb);

    // Field select
    const fieldSelect = document.createElement('select');
    fieldSelect.className = 'condition-field';
    for (const f of FILTER_FIELDS) {
        const opt = document.createElement('option');
        opt.value = f;
        opt.textContent = f;
        fieldSelect.appendChild(opt);
    }
    row.appendChild(fieldSelect);

    // Operator select
    const operatorSelect = document.createElement('select');
    operatorSelect.className = 'condition-operator';
    updateOperators(operatorSelect, 'text');
    row.appendChild(operatorSelect);

    // Value input
    const valueInput = document.createElement('input');
    valueInput.type = 'text';
    valueInput.className = 'condition-value';
    valueInput.placeholder = 'Enter value...';
    row.appendChild(valueInput);

    // Remove button
    const removeBtn = document.createElement('button');
    removeBtn.className = 'remove-condition-btn';
    removeBtn.textContent = '\u00D7';
    removeBtn.title = 'Remove condition';
    removeBtn.addEventListener('click', () => {
        row.remove();
        updateFilterCount();
    });
    row.appendChild(removeBtn);

    // Field change → update operators and value input type
    fieldSelect.addEventListener('change', () => {
        const fieldType = FIELD_TYPES[fieldSelect.value] || 'text';
        updateOperators(operatorSelect, fieldType);
        updateValueInput(row, fieldType, fieldSelect.value);
    });

    // PT-49: stopPropagation on keyboard events in filter inputs
    [valueInput, fieldSelect, operatorSelect, enabledCb].forEach(el => {
        el.addEventListener('keydown', ((e: KeyboardEvent) => {
            if (e.ctrlKey && (e.key === 'a' || e.key === 'c' || e.key === 'v' || e.key === 'x')) {
                e.stopPropagation();
            }
        }) as EventListener, true);
    });

    filterConditions.appendChild(row);
    updateFilterCount();
}

function updateOperators(select: HTMLSelectElement, fieldType: string): void {
    const ops = OPERATORS_BY_TYPE[fieldType] || OPERATORS_BY_TYPE.text;
    // Add universal operators
    const allOps = [...ops, 'Is Null', 'Is Not Null'];
    select.innerHTML = '';
    for (const op of allOps) {
        const opt = document.createElement('option');
        opt.value = op;
        opt.textContent = op;
        select.appendChild(opt);
    }
}

function updateValueInput(row: HTMLElement, fieldType: string, fieldName: string): void {
    const oldInput = row.querySelector('.condition-value');
    if (!oldInput) return;

    let newInput: HTMLElement;
    if (fieldType === 'enum') {
        const sel = document.createElement('select');
        sel.className = 'condition-value';
        const emptyOpt = document.createElement('option');
        emptyOpt.value = '';
        emptyOpt.textContent = 'Select...';
        sel.appendChild(emptyOpt);
        for (const opt of (ENUM_OPTIONS[fieldName] || [])) {
            const o = document.createElement('option');
            o.value = opt;
            o.textContent = opt;
            sel.appendChild(o);
        }
        newInput = sel;
    } else if (fieldType === 'date') {
        const inp = document.createElement('input');
        inp.type = 'datetime-local';
        inp.className = 'condition-value';
        newInput = inp;
    } else if (fieldType === 'number') {
        const inp = document.createElement('input');
        inp.type = 'number';
        inp.className = 'condition-value';
        inp.placeholder = fieldName === 'Duration' ? 'Duration in ms' : 'Enter number...';
        inp.min = '0';
        newInput = inp;
    } else {
        const inp = document.createElement('input');
        inp.type = 'text';
        inp.className = 'condition-value';
        inp.placeholder = 'Enter value...';
        newInput = inp;
    }

    // PT-49: stopPropagation
    newInput.addEventListener('keydown', (e: KeyboardEvent) => {
        if (e.ctrlKey && (e.key === 'a' || e.key === 'c' || e.key === 'v' || e.key === 'x')) {
            e.stopPropagation();
        }
    }, true);

    oldInput.replaceWith(newInput);
}

function collectAdvancedQuery(): AdvancedQueryViewDto {
    const quickFilterIds: string[] = [];
    quickFilterCheckboxes.forEach(cb => {
        if (cb.checked && cb.dataset.qf) quickFilterIds.push(cb.dataset.qf);
    });

    const conditions: QueryConditionViewDto[] = [];
    const logicRadio = filterPanelBody.querySelector<HTMLInputElement>('input[name="filter-logic"]:checked');
    const logic: 'and' | 'or' = logicRadio?.value === 'or' ? 'or' : 'and';

    filterConditions.querySelectorAll<HTMLElement>('.filter-condition-row').forEach(row => {
        const id = row.dataset.conditionId || '';
        const enabled = row.querySelector<HTMLInputElement>('.condition-enabled')?.checked ?? true;
        const field = row.querySelector<HTMLSelectElement>('.condition-field')?.value ?? '';
        const operator = row.querySelector<HTMLSelectElement>('.condition-operator')?.value ?? '';
        const valueEl = row.querySelector<HTMLInputElement>('.condition-value');
        const value = valueEl?.value ?? '';
        conditions.push({ id, enabled, field, operator, value, logicalOperator: logic });
    });

    return { quickFilterIds, conditions };
}

function applyAdvancedFilter(): void {
    const query = collectAdvancedQuery();
    vscode.postMessage({ command: 'applyAdvancedFilter', query });
    updateFilterCount();
    updateQueryPreview();
}

function updateFilterCount(): void {
    const query = collectAdvancedQuery();
    const totalQF = query.quickFilterIds.length;
    const enabledConditions = query.conditions.filter(c => c.enabled).length;
    const totalConditions = query.conditions.length;
    const active = totalQF + enabledConditions;
    const total = totalQF + totalConditions;

    const titleEl = filterPanel.querySelector('.filter-panel-title');
    if (titleEl) {
        titleEl.innerHTML = '<span class="filter-chevron">&#x25B6;</span> Filters (' + active + ' / ' + total + ')';
    }
}

function updateQueryPreview(): void {
    const query = collectAdvancedQuery();
    const lines: string[] = [];

    if (query.quickFilterIds.length > 0) {
        lines.push('Quick Filters:');
        for (const qf of query.quickFilterIds) {
            lines.push('  - ' + qf);
        }
    }

    const enabledConditions = query.conditions.filter(c => c.enabled);
    if (enabledConditions.length > 0) {
        if (lines.length > 0) lines.push('');
        const logic = enabledConditions[0]?.logicalOperator?.toUpperCase() || 'AND';
        lines.push('Advanced Conditions (' + logic + '):');
        for (const c of enabledConditions) {
            const opNeedsValue = c.operator !== 'Is Null' && c.operator !== 'Is Not Null';
            if (opNeedsValue && c.value) {
                lines.push('  ' + c.field + ' ' + c.operator + ' "' + c.value + '"');
            } else if (!opNeedsValue) {
                lines.push('  ' + c.field + ' ' + c.operator);
            }
        }
    }

    if (lines.length === 0) {
        lines.push('No filters applied');
    }

    queryPreviewText.textContent = lines.join('\n');
}

// Add condition button
document.getElementById('add-condition-btn')?.addEventListener('click', () => addConditionRow());

// Clear conditions button
document.getElementById('clear-conditions-btn')?.addEventListener('click', () => {
    filterConditions.innerHTML = '';
    quickFilterCheckboxes.forEach(cb => { cb.checked = false; });
    applyAdvancedFilter();
});

// Copy query button
document.getElementById('copy-query-btn')?.addEventListener('click', () => {
    const text = queryPreviewText.textContent || '';
    vscode.postMessage({ command: 'copyToClipboard', text });
});

// Apply on Enter in condition value inputs
filterConditions.addEventListener('keypress', (e) => {
    if ((e.target as HTMLElement).classList.contains('condition-value') && e.key === 'Enter') {
        e.preventDefault();
        applyAdvancedFilter();
    }
});

// Filter panel resize handle
const filterResizeHandle = document.getElementById('filter-panel-resize');
if (filterResizeHandle) {
    filterResizeHandle.addEventListener('mousedown', (e: MouseEvent) => {
        e.preventDefault();
        filterResizeHandle.classList.add('active');
        const startY = e.clientY;
        const startHeight = filterPanel.offsetHeight;

        function onMove(ev: MouseEvent): void {
            const delta = ev.clientY - startY;
            const newHeight = Math.max(120, Math.min(window.innerHeight * 0.6, startHeight + delta));
            filterPanel.style.height = newHeight + 'px';
        }

        function onUp(): void {
            filterResizeHandle?.classList.remove('active');
            document.removeEventListener('mousemove', onMove);
            document.removeEventListener('mouseup', onUp);
            const currentState = (vscode.getState() as Record<string, unknown>) || {};
            vscode.setState({ ...currentState, filterHeight: filterPanel.offsetHeight });
        }

        document.addEventListener('mousemove', onMove);
        document.addEventListener('mouseup', onUp);
    });
}

// ── Message handler ─────────────────────────────────────────────────
window.addEventListener('message', (event: MessageEvent<PluginTracesPanelHostToWebview>) => {
    const msg = event.data;
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

        case 'tracesLoaded':
            (refreshBtn as HTMLButtonElement).disabled = false;
            refreshBtn.textContent = 'Refresh';
            currentTraces = msg.traces;
            totalTraceCount = msg.totalCount;
            {
                const previousSelectedId = selectedTraceId;
                applySearchFilter();
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
            renderOverviewTab(msg.trace);
            renderDetailsTab(msg.trace);
            renderExceptionTab(msg.trace);
            renderMessageBlockTab(msg.trace);
            renderConfigTab(msg.trace);
            // Clear timeline tab — will be loaded on click
            detailTabTimeline.innerHTML = '';
            {
                const hint = document.createElement('div');
                hint.className = 'empty-state';
                hint.textContent = 'Click the Timeline tab to load execution chain';
                detailTabTimeline.appendChild(hint);
            }
            // Show detail pane and select Overview tab (6c default)
            detailPane.classList.add('visible');
            activateTab('overview');
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

        case 'selectTraceById':
            {
                const traceId = msg.id;
                selectedTraceId = traceId;
                tablePane.querySelectorAll<HTMLElement>('.data-table-row.selected').forEach(r => r.classList.remove('selected'));
                const targetRow = tablePane.querySelector<HTMLElement>('[data-id="' + CSS.escape(traceId) + '"]');
                if (targetRow) {
                    targetRow.classList.add('selected');
                    targetRow.scrollIntoView({ block: 'center', behavior: 'smooth' });
                }
            }
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

// ── Toolbar handlers ────────────────────────────────────────────────
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

    if (interval !== null) {
        autoRefreshSelect.classList.add('auto-refresh-active');
    } else {
        autoRefreshSelect.classList.remove('auto-refresh-active');
    }
});

// ── Search input handler ──────────────────────────────────────────────
let searchDebounceTimer: ReturnType<typeof setTimeout> | null = null;
searchInput.addEventListener('input', () => {
    if (searchDebounceTimer) clearTimeout(searchDebounceTimer);
    searchDebounceTimer = setTimeout(() => {
        applySearchFilter();
    }, 200);
});

// PT-49: stopPropagation on search input keyboard events
searchInput.addEventListener('keydown', (e: KeyboardEvent) => {
    if (e.ctrlKey && (e.key === 'a' || e.key === 'c' || e.key === 'v' || e.key === 'x')) {
        e.stopPropagation();
    }
}, true);

// ── Delete dropdown menu ─────────────────────────────────────────────
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

// Inline input for "Delete older than"
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

// ── Export dropdown menu ─────────────────────────────────────────────
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

// ── Trace Level dropdown menu ─────────────────────────────────────────
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

traceLevelDropdown.insertAdjacentElement('afterend', traceLevelIndicator);

traceLevelBtn.addEventListener('click', () => {
    vscode.postMessage({ command: 'requestTraceLevel' });
    const isVisible = traceLevelDropdown.style.display !== 'none';
    traceLevelDropdown.style.display = isVisible ? 'none' : '';
});

traceLevelDropdown.addEventListener('click', (e) => {
    const target = (e.target as HTMLElement).closest<HTMLElement>('.trace-level-dropdown-item');
    if (target?.dataset.level) {
        const level = target.dataset.level;
        traceLevelDropdown.style.display = 'none';

        // Optimistic UI update — show the new level immediately so the user
        // gets instant feedback while the host round-trips to Dataverse.
        traceLevelBtn.textContent = 'Trace Level: ' + level;
        traceLevelIndicator.textContent = level;
        traceLevelIndicator.className = level === 'Off'
            ? 'trace-level-indicator trace-level-off'
            : 'trace-level-indicator trace-level-on';
        traceLevelIndicator.title = level === 'Off'
            ? 'Trace level is Off — no new traces will be recorded'
            : 'Trace level: ' + level;
        traceLevelDropdown.querySelectorAll<HTMLElement>('.trace-level-dropdown-item').forEach(item => {
            item.classList.toggle('active', item.dataset.level === level);
        });

        vscode.postMessage({ command: 'setTraceLevel', level });
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

// ── Keyboard shortcuts ──────────────────────────────────────────────
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

// PT-43: Ctrl+A in data-selection-zone selects just that block
document.addEventListener('keydown', (e) => {
    if (e.ctrlKey && e.key === 'a') {
        const active = document.activeElement;
        // If focus is in an input/textarea, let normal behavior happen
        if (active instanceof HTMLInputElement || active instanceof HTMLTextAreaElement) return;

        // Check if the click target is inside a selection zone
        const zone = (e.target as HTMLElement).closest?.('[data-selection-zone]');
        if (zone) {
            e.preventDefault();
            const selection = window.getSelection();
            if (selection) {
                const range = document.createRange();
                range.selectNodeContents(zone);
                selection.removeAllRanges();
                selection.addRange(range);
            }
        }
    }
}, true);

// ── Ready signal ────────────────────────────────────────────────────
vscode.postMessage({ command: 'ready' });
