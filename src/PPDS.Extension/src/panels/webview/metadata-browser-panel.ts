// metadata-browser-panel.ts
// External webview script for the Metadata Browser panel.
// Built by esbuild as IIFE for browser, loaded via <script src="...">.

import { escapeHtml } from './shared/dom-utils.js';
import type {
    MetadataBrowserPanelWebviewToHost,
    MetadataBrowserPanelHostToWebview,
    MetadataEntityViewDto,
} from './shared/message-types.js';
import type {
    MetadataEntityDetailDto,
    MetadataAttributeDto,
} from '../../types.js';
import { assertNever } from './shared/assert-never.js';
import { getVsCodeApi } from './shared/vscode-api.js';
import { installErrorHandler } from './shared/error-handler.js';
import { DataTable } from './shared/data-table.js';

const vscode = getVsCodeApi<MetadataBrowserPanelWebviewToHost>();
installErrorHandler((msg) => vscode.postMessage(msg as MetadataBrowserPanelWebviewToHost));

// ── DOM elements ──
const entityListEl = document.getElementById('entity-list') as HTMLElement;
const entitySearchEl = document.getElementById('entity-search') as HTMLInputElement;
const tabBar = document.getElementById('tab-bar') as HTMLElement;
const tabContent = document.getElementById('tab-content') as HTMLElement;
const statusText = document.getElementById('status-text') as HTMLElement;
const filterCount = document.getElementById('filter-count') as HTMLElement;
const refreshBtn = document.getElementById('refresh-btn') as HTMLElement;
const makerBtn = document.getElementById('maker-btn') as HTMLElement;
const hideSystemToggle = document.getElementById('hide-system-toggle') as HTMLInputElement;

// ── State ──
let allEntities: MetadataEntityViewDto[] = [];
let filteredEntities: MetadataEntityViewDto[] = [];
let selectedEntityName: string | null = null;
let currentEntity: MetadataEntityDetailDto | null = null;
let activeTab = 'attributes';
let hideSystem = false;

const TAB_DEFS = [
    { id: 'attributes', label: 'Attributes' },
    { id: 'relationships', label: 'Relationships' },
    { id: 'keys', label: 'Keys' },
    { id: 'privileges', label: 'Privileges' },
    { id: 'choices', label: 'Choices' },
];

// ── Environment picker ──
const envPickerBtn = document.getElementById('env-picker-btn') as HTMLElement;
const envPickerName = document.getElementById('env-picker-name') as HTMLElement;
envPickerBtn.addEventListener('click', () => {
    vscode.postMessage({ command: 'requestEnvironmentList' });
});
function updateEnvironmentDisplay(name: string | null): void {
    envPickerName.textContent = name || 'No environment';
}

// ── Button handlers ──
refreshBtn.addEventListener('click', () => {
    vscode.postMessage({ command: 'refresh' });
});

makerBtn.addEventListener('click', () => {
    vscode.postMessage({ command: 'openInMaker', entityLogicalName: selectedEntityName ?? undefined });
});

document.getElementById('reconnect-refresh')!.addEventListener('click', (e) => {
    e.preventDefault();
    document.getElementById('reconnect-banner')!.style.display = 'none';
    vscode.postMessage({ command: 'refresh' });
});

hideSystemToggle.addEventListener('change', () => {
    hideSystem = hideSystemToggle.checked;
    applyFilter();
});

// ── Entity list rendering ──
function renderEntityList(): void {
    entityListEl.innerHTML = '';
    for (const e of filteredEntities) {
        const row = document.createElement('div');
        row.className = 'entity-row';
        if (e.logicalName === selectedEntityName) {
            row.classList.add('selected');
        }
        row.setAttribute('data-name', e.logicalName);

        const icon = document.createElement('span');
        icon.className = 'entity-icon';
        icon.textContent = e.isCustomEntity ? '\u25C6' : '\u25CB';
        row.appendChild(icon);

        const displayName = document.createElement('span');
        displayName.className = 'entity-display-name';
        displayName.textContent = e.displayName || e.logicalName;
        row.appendChild(displayName);

        const logicalName = document.createElement('span');
        logicalName.className = 'entity-logical-name';
        logicalName.textContent = e.logicalName;
        row.appendChild(logicalName);

        row.addEventListener('click', () => {
            selectedEntityName = e.logicalName;
            // Update selection visually
            entityListEl.querySelectorAll<HTMLElement>('.entity-row.selected')
                .forEach(r => r.classList.remove('selected'));
            row.classList.add('selected');
            vscode.postMessage({ command: 'selectEntity', logicalName: e.logicalName });
        });

        entityListEl.appendChild(row);
    }
}

// ── Entity search / filter ──
let filterTimer: ReturnType<typeof setTimeout> | null = null;

entitySearchEl.addEventListener('input', () => {
    if (filterTimer) clearTimeout(filterTimer);
    filterTimer = setTimeout(() => {
        applyFilter();
    }, 150);
});

function applyFilter(): void {
    const term = entitySearchEl.value.toLowerCase().trim();
    let base = allEntities;

    if (hideSystem) {
        base = base.filter(e => e.isCustomEntity);
    }

    if (!term) {
        filteredEntities = base;
    } else {
        filteredEntities = base.filter(e =>
            e.displayName.toLowerCase().includes(term) ||
            e.logicalName.toLowerCase().includes(term) ||
            e.schemaName.toLowerCase().includes(term)
        );
    }

    if (filteredEntities.length !== allEntities.length) {
        filterCount.textContent = `${filteredEntities.length} of ${allEntities.length}`;
    } else {
        filterCount.textContent = '';
    }

    renderEntityList();
    updateStatusText();
}

function updateStatusText(): void {
    const total = allEntities.length;
    const custom = allEntities.filter(e => e.isCustomEntity).length;
    const system = total - custom;
    if (filteredEntities.length !== total) {
        statusText.textContent = `${filteredEntities.length} of ${total} entities (${custom} custom, ${system} system)`;
    } else {
        statusText.textContent = `${total} entities (${custom} custom, ${system} system)`;
    }
}

// ── Tab bar rendering ──
function renderTabBar(): void {
    tabBar.innerHTML = '';
    for (const tab of TAB_DEFS) {
        const btn = document.createElement('button');
        btn.className = 'tab-button';
        if (tab.id === activeTab) btn.classList.add('active');
        btn.textContent = tab.label;
        btn.addEventListener('click', () => {
            activeTab = tab.id;
            tabBar.querySelectorAll<HTMLElement>('.tab-button').forEach(b => b.classList.remove('active'));
            btn.classList.add('active');
            renderTabContent();
        });
        tabBar.appendChild(btn);
    }
}

// ── Tab content rendering ──
function renderTabContent(): void {
    if (!currentEntity) {
        tabContent.innerHTML = '<div class="empty-state">Select an entity to view details</div>';
        return;
    }

    switch (activeTab) {
        case 'attributes':
            renderAttributesTab();
            break;
        case 'relationships':
            renderRelationshipsTab();
            break;
        case 'keys':
            renderKeysTab();
            break;
        case 'privileges':
            renderPrivilegesTab();
            break;
        case 'choices':
            renderChoicesTab();
            break;
    }
}

// ── Attributes tab ──
function renderAttributesTab(): void {
    if (!currentEntity) return;
    tabContent.innerHTML = '';

    const table = new DataTable<MetadataAttributeDto>({
        container: tabContent,
        columns: [
            { key: 'logicalName', label: 'Name', render: (a) => escapeHtml(a.logicalName) },
            { key: 'displayName', label: 'Display Name', render: (a) => escapeHtml(a.displayName ?? '\u2014') },
            { key: 'attributeType', label: 'Type', render: (a) => escapeHtml(a.attributeType) },
            { key: 'requiredLevel', label: 'Required', render: (a) => escapeHtml(a.requiredLevel ?? '\u2014') },
            { key: 'isCustomAttribute', label: 'Custom', render: (a) => a.isCustomAttribute ? '\u2713' : '\u2014' },
            { key: 'maxLength', label: 'Max Length', render: (a) => a.maxLength !== null ? escapeHtml(String(a.maxLength)) : '\u2014' },
            { key: 'description', label: 'Description', render: (a) => escapeHtml(a.description ?? '\u2014') },
        ],
        getRowId: (a) => a.logicalName,
        defaultSortKey: 'logicalName',
        defaultSortDirection: 'asc',
        emptyMessage: 'No attributes',
    });
    table.setItems(currentEntity.attributes);
}

// ── Relationships tab ──
interface RelationshipRow {
    schemaName: string;
    type: string;
    relatedEntity: string;
    lookupField: string;
    cascadeDelete: string;
}

function buildRelationshipRows(): RelationshipRow[] {
    if (!currentEntity) return [];
    const rows: RelationshipRow[] = [];

    for (const rel of currentEntity.oneToManyRelationships) {
        rows.push({
            schemaName: rel.schemaName,
            type: '1:N',
            relatedEntity: rel.referencingEntity ?? '\u2014',
            lookupField: rel.referencingAttribute ?? '\u2014',
            cascadeDelete: rel.cascadeDelete ?? '\u2014',
        });
    }

    for (const rel of currentEntity.manyToOneRelationships) {
        rows.push({
            schemaName: rel.schemaName,
            type: 'N:1',
            relatedEntity: rel.referencedEntity ?? '\u2014',
            lookupField: rel.referencingAttribute ?? '\u2014',
            cascadeDelete: rel.cascadeDelete ?? '\u2014',
        });
    }

    for (const rel of currentEntity.manyToManyRelationships) {
        const related = rel.entity1LogicalName === currentEntity.logicalName
            ? rel.entity2LogicalName
            : rel.entity1LogicalName;
        rows.push({
            schemaName: rel.schemaName,
            type: 'N:N',
            relatedEntity: related ?? '\u2014',
            lookupField: rel.intersectEntityName ?? '\u2014',
            cascadeDelete: '\u2014',
        });
    }

    return rows;
}

function renderRelationshipsTab(): void {
    if (!currentEntity) return;
    tabContent.innerHTML = '';

    const rows = buildRelationshipRows();

    const table = new DataTable<RelationshipRow>({
        container: tabContent,
        columns: [
            { key: 'schemaName', label: 'Schema Name', render: (r) => escapeHtml(r.schemaName) },
            { key: 'type', label: 'Type', render: (r) => escapeHtml(r.type) },
            { key: 'relatedEntity', label: 'Related Entity', render: (r) => escapeHtml(r.relatedEntity) },
            { key: 'lookupField', label: 'Lookup Field', render: (r) => escapeHtml(r.lookupField) },
            { key: 'cascadeDelete', label: 'Cascade Delete', render: (r) => escapeHtml(r.cascadeDelete) },
        ],
        getRowId: (r) => r.schemaName + ':' + r.type,
        defaultSortKey: 'schemaName',
        defaultSortDirection: 'asc',
        emptyMessage: 'No relationships',
    });
    table.setItems(rows);
}

// ── Keys tab ──
interface KeyRow {
    displayName: string;
    schemaName: string;
    keyAttributes: string;
    indexStatus: string;
}

function renderKeysTab(): void {
    if (!currentEntity) return;
    tabContent.innerHTML = '';

    const rows: KeyRow[] = currentEntity.keys.map(k => ({
        displayName: k.displayName ?? '\u2014',
        schemaName: k.schemaName,
        keyAttributes: k.keyAttributes.join(', '),
        indexStatus: k.entityKeyIndexStatus ?? '\u2014',
    }));

    const table = new DataTable<KeyRow>({
        container: tabContent,
        columns: [
            { key: 'displayName', label: 'Display Name', render: (k) => escapeHtml(k.displayName) },
            { key: 'schemaName', label: 'Schema Name', render: (k) => escapeHtml(k.schemaName) },
            { key: 'keyAttributes', label: 'Key Attributes', render: (k) => escapeHtml(k.keyAttributes) },
            { key: 'indexStatus', label: 'Index Status', render: (k) => escapeHtml(k.indexStatus) },
        ],
        getRowId: (k) => k.schemaName,
        defaultSortKey: 'schemaName',
        defaultSortDirection: 'asc',
        emptyMessage: 'No alternate keys',
    });
    table.setItems(rows);
}

// ── Privileges tab ──
interface PrivilegeRow {
    name: string;
    privilegeType: string;
    canBeLocal: boolean;
    canBeDeep: boolean;
    canBeGlobal: boolean;
    canBeBasic: boolean;
}

function renderPrivilegesTab(): void {
    if (!currentEntity) return;
    tabContent.innerHTML = '';

    const rows: PrivilegeRow[] = currentEntity.privileges.map(p => ({
        name: p.name,
        privilegeType: p.privilegeType,
        canBeLocal: p.canBeLocal,
        canBeDeep: p.canBeDeep,
        canBeGlobal: p.canBeGlobal,
        canBeBasic: p.canBeBasic,
    }));

    const table = new DataTable<PrivilegeRow>({
        container: tabContent,
        columns: [
            { key: 'name', label: 'Name', render: (p) => escapeHtml(p.name) },
            { key: 'privilegeType', label: 'Type', render: (p) => escapeHtml(p.privilegeType) },
            { key: 'canBeLocal', label: 'Local', render: (p) => p.canBeLocal ? '\u2713' : '\u2014' },
            { key: 'canBeDeep', label: 'Deep', render: (p) => p.canBeDeep ? '\u2713' : '\u2014' },
            { key: 'canBeGlobal', label: 'Global', render: (p) => p.canBeGlobal ? '\u2713' : '\u2014' },
            { key: 'canBeBasic', label: 'Basic', render: (p) => p.canBeBasic ? '\u2713' : '\u2014' },
        ],
        getRowId: (p) => p.name,
        defaultSortKey: 'name',
        defaultSortDirection: 'asc',
        emptyMessage: 'No privileges',
    });
    table.setItems(rows);
}

// ── Choices tab ──
function renderChoicesTab(): void {
    if (!currentEntity) return;
    tabContent.innerHTML = '';

    // Entity Choices: attributes with local option sets
    const entityChoiceAttrs = currentEntity.attributes.filter(
        a => a.options && a.options.length > 0 && !a.isGlobalOptionSet
    );

    // Global Option Sets
    const globalOptionSets = currentEntity.globalOptionSets ?? [];

    // -- Entity Choices section --
    const entitySection = document.createElement('div');
    entitySection.className = 'choices-section';

    const entityHeader = document.createElement('div');
    entityHeader.className = 'choices-section-header';
    entityHeader.textContent = 'Entity Choices';
    entitySection.appendChild(entityHeader);

    if (entityChoiceAttrs.length === 0) {
        const empty = document.createElement('div');
        empty.className = 'empty-state';
        empty.textContent = 'No entity-level choice attributes';
        entitySection.appendChild(empty);
    } else {
        const tableContainer = document.createElement('div');
        entitySection.appendChild(tableContainer);

        const table = new DataTable<MetadataAttributeDto>({
            container: tableContainer,
            columns: [
                { key: 'logicalName', label: 'Attribute', render: (a) => escapeHtml(a.logicalName) },
                { key: 'optionSetName', label: 'Option Set Name', render: (a) => escapeHtml(a.optionSetName ?? '\u2014') },
                { key: 'optionsCount', label: 'Values Count', render: (a) => escapeHtml(String(a.options?.length ?? 0)) },
            ],
            getRowId: (a) => a.logicalName,
            onRowClick: (a) => toggleOptionValues(a.logicalName, a.options ?? [], tableContainer),
            defaultSortKey: 'logicalName',
            defaultSortDirection: 'asc',
            emptyMessage: 'No entity choices',
        });
        table.setItems(entityChoiceAttrs);
    }

    tabContent.appendChild(entitySection);

    // -- Global Option Sets section --
    const globalSection = document.createElement('div');
    globalSection.className = 'choices-section';

    const globalHeader = document.createElement('div');
    globalHeader.className = 'choices-section-header';
    globalHeader.textContent = 'Global Option Sets';
    globalSection.appendChild(globalHeader);

    if (globalOptionSets.length === 0) {
        const empty = document.createElement('div');
        empty.className = 'empty-state';
        empty.textContent = 'No global option sets';
        globalSection.appendChild(empty);
    } else {
        interface GlobalOptionSetRow {
            name: string;
            displayName: string;
            optionSetType: string;
            valuesCount: number;
            options: { value: number; label: string; color: string | null; description: string | null }[];
        }

        const globalRows: GlobalOptionSetRow[] = globalOptionSets.map(os => ({
            name: os.name,
            displayName: os.displayName ?? '\u2014',
            optionSetType: os.optionSetType,
            valuesCount: os.options.length,
            options: os.options,
        }));

        const globalTableContainer = document.createElement('div');
        globalSection.appendChild(globalTableContainer);

        const globalTable = new DataTable<GlobalOptionSetRow>({
            container: globalTableContainer,
            columns: [
                { key: 'name', label: 'Name', render: (o) => escapeHtml(o.name) },
                { key: 'displayName', label: 'Display Name', render: (o) => escapeHtml(o.displayName) },
                { key: 'optionSetType', label: 'Type', render: (o) => escapeHtml(o.optionSetType) },
                { key: 'valuesCount', label: 'Values Count', render: (o) => escapeHtml(String(o.valuesCount)) },
            ],
            getRowId: (o) => o.name,
            onRowClick: (o) => toggleOptionValues(o.name, o.options, globalTableContainer),
            defaultSortKey: 'name',
            defaultSortDirection: 'asc',
            emptyMessage: 'No global option sets',
        });
        globalTable.setItems(globalRows);
    }

    tabContent.appendChild(globalSection);
}

/** Toggle visibility of option values sub-table for a choice row. */
function toggleOptionValues(
    key: string,
    options: { value: number; label: string; color: string | null; description: string | null }[],
    container: HTMLElement,
): void {
    const existingId = 'option-values-' + CSS.escape(key);
    const existing = container.querySelector<HTMLElement>('#' + existingId);
    if (existing) {
        existing.classList.toggle('expanded');
        return;
    }

    // Find the clicked row to insert the sub-table after it
    const selectedRow = container.querySelector<HTMLElement>('.data-table-row.selected');
    if (!selectedRow) return;

    const tr = document.createElement('tr');
    tr.id = 'option-values-' + key;
    tr.className = 'option-values-row expanded';
    const td = document.createElement('td');
    td.colSpan = 99;

    if (options.length === 0) {
        td.textContent = 'No option values';
    } else {
        const subTable = document.createElement('table');
        subTable.className = 'data-table option-values-table';

        const thead = document.createElement('thead');
        const headerRow = document.createElement('tr');
        for (const label of ['Value', 'Label', 'Color', 'Description']) {
            const th = document.createElement('th');
            th.textContent = label;
            headerRow.appendChild(th);
        }
        thead.appendChild(headerRow);
        subTable.appendChild(thead);

        const tbody = document.createElement('tbody');
        for (const opt of options) {
            const row = document.createElement('tr');

            const valTd = document.createElement('td');
            valTd.textContent = String(opt.value);
            row.appendChild(valTd);

            const labelTd = document.createElement('td');
            labelTd.textContent = opt.label;
            row.appendChild(labelTd);

            const colorTd = document.createElement('td');
            if (opt.color && /^#[0-9a-fA-F]{3,8}$/.test(opt.color)) {
                const swatch = document.createElement('span');
                swatch.className = 'color-swatch';
                swatch.style.backgroundColor = opt.color;
                colorTd.appendChild(swatch);
                const colorText = document.createTextNode(opt.color);
                colorTd.appendChild(colorText);
            } else {
                colorTd.textContent = '\u2014';
            }
            row.appendChild(colorTd);

            const descTd = document.createElement('td');
            descTd.textContent = opt.description ?? '\u2014';
            row.appendChild(descTd);

            tbody.appendChild(row);
        }
        subTable.appendChild(tbody);
        td.appendChild(subTable);
    }

    tr.appendChild(td);
    // Insert after the selected row in the table body
    if (selectedRow.nextSibling) {
        selectedRow.parentNode!.insertBefore(tr, selectedRow.nextSibling);
    } else {
        selectedRow.parentNode!.appendChild(tr);
    }
}

// ── Message handling ──
window.addEventListener('message', (event: MessageEvent<MetadataBrowserPanelHostToWebview>) => {
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
        case 'entitiesLoaded':
            allEntities = msg.entities;
            entitySearchEl.value = '';
            applyFilter();
            renderTabBar();
            if (!currentEntity) {
                tabContent.innerHTML = '<div class="empty-state">Select an entity to view details</div>';
            }
            {
                const reconnectBanner = document.getElementById('reconnect-banner');
                if (reconnectBanner) reconnectBanner.style.display = 'none';
            }
            break;
        case 'entityDetailLoading':
            tabContent.innerHTML = '<div class="loading-state"><div class="spinner"></div><div>Loading ' + escapeHtml(msg.logicalName) + '...</div></div>';
            break;
        case 'entityDetailLoaded':
            currentEntity = msg.entity;
            renderTabBar();
            renderTabContent();
            break;
        case 'loading':
            entityListEl.innerHTML = '';
            tabContent.innerHTML = '<div class="loading-state"><div class="spinner"></div><div>Loading entities...</div></div>';
            statusText.textContent = 'Loading...';
            break;
        case 'error':
            tabContent.innerHTML = '<div class="error-state">' + escapeHtml(msg.message) + '</div>';
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
