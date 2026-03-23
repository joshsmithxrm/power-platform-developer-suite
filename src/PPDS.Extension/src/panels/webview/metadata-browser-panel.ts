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
    MetadataGlobalChoiceSummaryDto,
    MetadataOptionSetDto,
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
const includeIntersectToggle = document.getElementById('include-intersect-toggle') as HTMLInputElement;
const entityBreadcrumb = document.getElementById('entity-breadcrumb') as HTMLElement;

// ── State ──
let allEntities: MetadataEntityViewDto[] = [];
let filteredEntities: MetadataEntityViewDto[] = [];
let allGlobalChoices: MetadataGlobalChoiceSummaryDto[] = [];
let filteredGlobalChoices: MetadataGlobalChoiceSummaryDto[] = [];
let selectedEntityName: string | null = null;
let selectedChoiceName: string | null = null;
let currentEntity: MetadataEntityDetailDto | null = null;
let currentGlobalChoice: MetadataOptionSetDto | null = null;
let activeTab = 'attributes';
let hideSystem = false;
let intersectHiddenCount = 0;

// Per-tab search state
const tabSearchTerms: Record<string, string> = {};

// Section collapse state
let entitiesSectionCollapsed = false;
let choicesSectionCollapsed = false;

type TabId = 'attributes' | '1n' | 'n1' | 'nn' | 'keys' | 'privileges' | 'choices' | 'choice-detail';

const ENTITY_TAB_DEFS: { id: TabId; label: string }[] = [
    { id: 'attributes', label: 'Attributes' },
    { id: '1n', label: '1:N' },
    { id: 'n1', label: 'N:1' },
    { id: 'nn', label: 'N:N' },
    { id: 'keys', label: 'Keys' },
    { id: 'privileges', label: 'Privileges' },
    { id: 'choices', label: 'Choices' },
];

const CHOICE_TAB_DEFS: { id: TabId; label: string }[] = [
    { id: 'choice-detail', label: 'Values' },
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
    (refreshBtn as HTMLButtonElement).disabled = true;
    refreshBtn.textContent = 'Loading...';
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

includeIntersectToggle.addEventListener('change', () => {
    vscode.postMessage({ command: 'setIncludeIntersect', includeIntersect: includeIntersectToggle.checked });
});

// ── Entity list rendering ──
function renderEntityList(): void {
    entityListEl.innerHTML = '';

    // ── ENTITIES section ──
    const entitiesHeader = buildSectionHeader(
        'entities-header',
        `\uD83D\uDCCB ENTITIES (${filteredEntities.length})`,
        entitiesSectionCollapsed,
        () => {
            entitiesSectionCollapsed = !entitiesSectionCollapsed;
            renderEntityList();
        },
        filteredEntities.length !== allEntities.length
            ? `${filteredEntities.length} of ${allEntities.length}`
            : undefined,
    );
    entityListEl.appendChild(entitiesHeader);

    if (!entitiesSectionCollapsed) {
        for (const e of filteredEntities) {
            entityListEl.appendChild(buildEntityRow(e));
        }
    }

    // ── CHOICES section ──
    const choicesHeader = buildSectionHeader(
        'choices-header',
        `\uD83D\uDD3D CHOICES (${filteredGlobalChoices.length})`,
        choicesSectionCollapsed,
        () => {
            choicesSectionCollapsed = !choicesSectionCollapsed;
            renderEntityList();
        },
        filteredGlobalChoices.length !== allGlobalChoices.length
            ? `${filteredGlobalChoices.length} of ${allGlobalChoices.length}`
            : undefined,
    );
    entityListEl.appendChild(choicesHeader);

    if (!choicesSectionCollapsed) {
        for (const c of filteredGlobalChoices) {
            entityListEl.appendChild(buildChoiceRow(c));
        }
    }
}

function buildSectionHeader(
    id: string,
    label: string,
    collapsed: boolean,
    onToggle: () => void,
    badgeText?: string,
): HTMLElement {
    const header = document.createElement('div');
    header.className = 'section-header';
    header.id = id;

    const arrow = document.createElement('span');
    arrow.className = 'section-arrow';
    arrow.textContent = collapsed ? '\u25B6' : '\u25BC';
    header.appendChild(arrow);

    const labelSpan = document.createElement('span');
    labelSpan.className = 'section-label';
    labelSpan.textContent = label;
    header.appendChild(labelSpan);

    if (badgeText) {
        const badge = document.createElement('span');
        badge.className = 'section-badge';
        badge.textContent = badgeText;
        header.appendChild(badge);
    }

    header.addEventListener('click', onToggle);
    return header;
}

function buildEntityRow(e: MetadataEntityViewDto): HTMLElement {
    const row = document.createElement('div');
    row.className = 'entity-row';
    if (e.logicalName === selectedEntityName) {
        row.classList.add('selected');
    }
    row.setAttribute('data-name', e.logicalName);
    row.setAttribute('data-schema', e.schemaName);

    const icon = document.createElement('span');
    icon.className = 'entity-icon';
    // MB-07: match legacy emojis — 🏷️ custom, 📋 system
    icon.textContent = e.isCustomEntity ? '\uD83C\uDFF7\uFE0F' : '\uD83D\uDCCB';
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
        selectedChoiceName = null;
        currentGlobalChoice = null;
        // Update selection visually
        entityListEl.querySelectorAll<HTMLElement>('.entity-row.selected, .choice-row.selected')
            .forEach(r => r.classList.remove('selected'));
        row.classList.add('selected');
        vscode.postMessage({ command: 'selectEntity', logicalName: e.logicalName });
    });

    // MB-22: Right-click "Copy Schema Name"
    row.addEventListener('contextmenu', (evt) => {
        evt.preventDefault();
        showContextMenu(evt.clientX, evt.clientY, [
            {
                label: 'Copy Schema Name',
                action: () => vscode.postMessage({ command: 'copyToClipboard', text: e.schemaName }),
            },
            {
                label: 'Copy Logical Name',
                action: () => vscode.postMessage({ command: 'copyToClipboard', text: e.logicalName }),
            },
        ]);
    });

    return row;
}

function buildChoiceRow(c: MetadataGlobalChoiceSummaryDto): HTMLElement {
    const row = document.createElement('div');
    row.className = 'choice-row';
    if (c.name === selectedChoiceName) {
        row.classList.add('selected');
    }
    row.setAttribute('data-name', c.name);

    const icon = document.createElement('span');
    icon.className = 'entity-icon';
    // MB-07: 🔽 for option sets
    icon.textContent = '\uD83D\uDD3D';
    row.appendChild(icon);

    const displayName = document.createElement('span');
    displayName.className = 'entity-display-name';
    displayName.textContent = c.displayName || c.name;
    row.appendChild(displayName);

    const name = document.createElement('span');
    name.className = 'entity-logical-name';
    name.textContent = c.name;
    row.appendChild(name);

    row.addEventListener('click', () => {
        selectedChoiceName = c.name;
        selectedEntityName = null;
        currentEntity = null;
        entityListEl.querySelectorAll<HTMLElement>('.entity-row.selected, .choice-row.selected')
            .forEach(r => r.classList.remove('selected'));
        row.classList.add('selected');
        vscode.postMessage({ command: 'selectGlobalChoice', name: c.name });
    });

    row.addEventListener('contextmenu', (evt) => {
        evt.preventDefault();
        showContextMenu(evt.clientX, evt.clientY, [
            {
                label: 'Copy Name',
                action: () => vscode.postMessage({ command: 'copyToClipboard', text: c.name }),
            },
        ]);
    });

    return row;
}

// ── Context menu ──
function showContextMenu(x: number, y: number, items: { label: string; action: () => void }[]): void {
    // Remove any existing menu
    document.querySelector('.context-menu')?.remove();

    const menu = document.createElement('div');
    menu.className = 'context-menu';
    menu.style.left = `${x}px`;
    menu.style.top = `${y}px`;

    for (const item of items) {
        const menuItem = document.createElement('div');
        menuItem.className = 'context-menu-item';
        menuItem.textContent = item.label;
        menuItem.addEventListener('click', () => {
            item.action();
            menu.remove();
        });
        menu.appendChild(menuItem);
    }

    document.body.appendChild(menu);

    // Close on next click anywhere
    const closeMenu = (): void => {
        menu.remove();
        document.removeEventListener('click', closeMenu);
    };
    setTimeout(() => document.addEventListener('click', closeMenu), 0);
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

    // Filter entities
    let entityBase = allEntities;
    if (hideSystem) {
        entityBase = entityBase.filter(e => e.isCustomEntity);
    }
    if (!term) {
        filteredEntities = entityBase;
    } else {
        filteredEntities = entityBase.filter(e =>
            e.displayName.toLowerCase().includes(term) ||
            e.logicalName.toLowerCase().includes(term) ||
            e.schemaName.toLowerCase().includes(term)
        );
    }

    // Filter global choices
    if (!term) {
        filteredGlobalChoices = allGlobalChoices;
    } else {
        filteredGlobalChoices = allGlobalChoices.filter(c =>
            c.displayName.toLowerCase().includes(term) ||
            c.name.toLowerCase().includes(term)
        );
    }

    const totalShown = filteredEntities.length + filteredGlobalChoices.length;
    const totalAll = allEntities.length + allGlobalChoices.length;
    if (totalShown !== totalAll) {
        filterCount.textContent = `${totalShown} of ${totalAll}`;
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

    let text: string;
    if (filteredEntities.length !== total) {
        text = `${filteredEntities.length} of ${total} entities (${custom} custom, ${system} system)`;
    } else {
        text = `${total} entities (${custom} custom, ${system} system)`;
    }

    if (intersectHiddenCount > 0) {
        text += ` \u2014 ${intersectHiddenCount} intersection hidden`;
    }

    statusText.textContent = text;
}

// ── Breadcrumb (MB-06) ──
function updateBreadcrumb(): void {
    if (currentEntity) {
        entityBreadcrumb.style.display = '';
        entityBreadcrumb.textContent = '';
        const displayPart = document.createElement('span');
        displayPart.className = 'breadcrumb-display';
        displayPart.textContent = currentEntity.displayName || currentEntity.logicalName;
        entityBreadcrumb.appendChild(displayPart);

        const sep = document.createTextNode(' \u2014 ');
        entityBreadcrumb.appendChild(sep);

        const logicalPart = document.createElement('span');
        logicalPart.className = 'breadcrumb-logical';
        logicalPart.textContent = currentEntity.logicalName;
        entityBreadcrumb.appendChild(logicalPart);
    } else if (currentGlobalChoice) {
        entityBreadcrumb.style.display = '';
        entityBreadcrumb.textContent = '';
        const icon = document.createElement('span');
        icon.textContent = '\uD83D\uDD3D ';
        entityBreadcrumb.appendChild(icon);
        const namePart = document.createElement('span');
        namePart.className = 'breadcrumb-display';
        namePart.textContent = currentGlobalChoice.displayName || currentGlobalChoice.name;
        entityBreadcrumb.appendChild(namePart);

        const sep = document.createTextNode(' \u2014 ');
        entityBreadcrumb.appendChild(sep);

        const logicalPart = document.createElement('span');
        logicalPart.className = 'breadcrumb-logical';
        logicalPart.textContent = currentGlobalChoice.name;
        entityBreadcrumb.appendChild(logicalPart);
    } else {
        entityBreadcrumb.style.display = 'none';
    }
}

// ── Tab bar rendering ──
function renderTabBar(): void {
    tabBar.innerHTML = '';

    const tabDefs = currentGlobalChoice ? CHOICE_TAB_DEFS : ENTITY_TAB_DEFS;

    for (const tab of tabDefs) {
        const btn = document.createElement('button');
        btn.className = 'tab-button';
        if (tab.id === activeTab) btn.classList.add('active');

        // Label with tab-specific search indicator
        const searchTerm = tabSearchTerms[tab.id];
        btn.textContent = searchTerm ? `${tab.label} \uD83D\uDD0D` : tab.label;

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
    if (!currentEntity && !currentGlobalChoice) {
        tabContent.innerHTML = '';
        const empty = document.createElement('div');
        empty.className = 'empty-state';
        empty.textContent = 'Select an entity to view details';
        tabContent.appendChild(empty);
        return;
    }

    if (currentGlobalChoice) {
        renderGlobalChoiceDetailTab();
        return;
    }

    switch (activeTab as TabId) {
        case 'attributes':
            renderAttributesTab();
            break;
        case '1n':
            renderOneToManyTab();
            break;
        case 'n1':
            renderManyToOneTab();
            break;
        case 'nn':
            renderManyToManyTab();
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
        case 'choice-detail':
            renderGlobalChoiceDetailTab();
            break;
    }
}

// ── Per-tab search bar ──
function buildTabSearchBar(tabId: TabId, placeholder: string, onSearch: (term: string) => void): HTMLElement {
    const container = document.createElement('div');
    container.className = 'tab-search-container';

    const input = document.createElement('input');
    input.type = 'text';
    input.className = 'tab-search-input';
    input.placeholder = placeholder;
    input.value = tabSearchTerms[tabId] ?? '';

    let searchTimer: ReturnType<typeof setTimeout> | null = null;
    input.addEventListener('input', () => {
        if (searchTimer) clearTimeout(searchTimer);
        searchTimer = setTimeout(() => {
            tabSearchTerms[tabId] = input.value;
            onSearch(input.value.toLowerCase().trim());
            // Update tab label to show search indicator
            renderTabBar();
        }, 150);
    });
    // Stop Ctrl+A from selecting the whole page
    input.addEventListener('keydown', (e) => e.stopPropagation());

    container.appendChild(input);
    return container;
}

// ── Attributes tab (MB-10 — displayName first) ──
function renderAttributesTab(): void {
    if (!currentEntity) return;
    tabContent.innerHTML = '';

    const searchBar = buildTabSearchBar('attributes', 'Search attributes...', () => {
        renderAttributeTable(tabContent, currentEntity!.attributes);
    });
    tabContent.appendChild(searchBar);

    renderAttributeTable(tabContent, currentEntity.attributes);
}

function getFilteredAttributes(attrs: MetadataAttributeDto[]): MetadataAttributeDto[] {
    const term = (tabSearchTerms['attributes'] ?? '').toLowerCase().trim();
    if (!term) return attrs;
    return attrs.filter(a =>
        (a.displayName ?? '').toLowerCase().includes(term) ||
        a.logicalName.toLowerCase().includes(term) ||
        a.attributeType.toLowerCase().includes(term)
    );
}

function renderAttributeTable(container: HTMLElement, attrs: MetadataAttributeDto[]): void {
    // Remove existing table if any
    container.querySelector('.attr-table-wrapper')?.remove();

    const wrapper = document.createElement('div');
    wrapper.className = 'attr-table-wrapper';
    container.appendChild(wrapper);

    const filtered = getFilteredAttributes(attrs);

    const table = new DataTable<MetadataAttributeDto>({
        container: wrapper,
        columns: [
            // MB-10: displayName first
            { key: 'displayName', label: 'Display Name', render: (a) => escapeHtml(a.displayName ?? '\u2014') },
            { key: 'logicalName', label: 'Logical Name', render: (a) => escapeHtml(a.logicalName) },
            { key: 'attributeType', label: 'Type', render: (a) => escapeHtml(a.attributeType) },
            { key: 'requiredLevel', label: 'Required', render: (a) => escapeHtml(a.requiredLevel ?? '\u2014') },
            { key: 'isCustomAttribute', label: 'Custom', render: (a) => a.isCustomAttribute ? '\u2713' : '\u2014' },
            { key: 'maxLength', label: 'Max Length', render: (a) => a.maxLength !== null ? escapeHtml(String(a.maxLength)) : '\u2014' },
            { key: 'description', label: 'Description', render: (a) => escapeHtml(a.description ?? '\u2014') },
        ],
        getRowId: (a) => a.logicalName,
        defaultSortKey: 'displayName',
        defaultSortDirection: 'asc',
        emptyMessage: 'No attributes',
        // MB-05: click to show properties panel
        onRowClick: (a) => showAttributePropertiesPanel(a, wrapper),
    });
    table.setItems(filtered);

    // MB-22: right-click context menu on attribute rows
    wrapper.addEventListener('contextmenu', (evt) => {
        const row = (evt.target as HTMLElement).closest<HTMLElement>('.data-table-row');
        if (!row) return;
        evt.preventDefault();
        const logicalName = row.dataset['id'] ?? '';
        const attr = filtered.find(a => a.logicalName === logicalName);
        if (!attr) return;
        showContextMenu(evt.clientX, evt.clientY, [
            {
                label: 'Copy Schema Name',
                action: () => vscode.postMessage({ command: 'copyToClipboard', text: attr.schemaName ?? attr.logicalName }),
            },
            {
                label: 'Copy Logical Name',
                action: () => vscode.postMessage({ command: 'copyToClipboard', text: attr.logicalName }),
            },
        ]);
    });
}

// ── MB-05: Properties detail panel ──
function showAttributePropertiesPanel(attr: MetadataAttributeDto, container: HTMLElement): void {
    container.querySelector('.attr-properties-panel')?.remove();

    const panel = document.createElement('div');
    panel.className = 'attr-properties-panel';

    const title = document.createElement('div');
    title.className = 'prop-panel-title';
    title.textContent = attr.displayName || attr.logicalName;
    panel.appendChild(title);

    const closeBtn = document.createElement('button');
    closeBtn.className = 'prop-panel-close';
    closeBtn.textContent = '\u00D7';
    closeBtn.title = 'Close';
    closeBtn.addEventListener('click', () => panel.remove());
    title.appendChild(closeBtn);

    const props: [string, string | number | boolean | null][] = [
        ['Display Name', attr.displayName],
        ['Logical Name', attr.logicalName],
        ['Schema Name', attr.schemaName],
        ['Type', attr.attributeType],
        ['Type Name', attr.attributeTypeName],
        ['Required Level', attr.requiredLevel],
        ['Is Custom', attr.isCustomAttribute ? 'Yes' : 'No'],
        ['Is Secured', attr.isSecured ? 'Yes' : 'No'],
    ];

    // Type-specific properties (MB-32/33)
    if (attr.maxLength !== null && attr.maxLength !== undefined) {
        props.push(['Max Length', attr.maxLength]);
    }
    if (attr.precision !== null && attr.precision !== undefined) {
        props.push(['Precision', attr.precision]);
    }
    if (attr.minValue !== null && attr.minValue !== undefined) {
        props.push(['Min Value', attr.minValue]);
    }
    if (attr.maxValue !== null && attr.maxValue !== undefined) {
        props.push(['Max Value', attr.maxValue]);
    }
    if (attr.format) {
        props.push(['Format', attr.format]);
    }
    if (attr.dateTimeBehavior) {
        props.push(['Date/Time Behavior', attr.dateTimeBehavior]);
    }
    if (attr.autoNumberFormat) {
        props.push(['Auto Number Format', attr.autoNumberFormat]);
    }
    if (attr.sourceType !== null && attr.sourceType !== undefined) {
        props.push(['Source Type', attr.sourceType]);
    }
    if (attr.description) {
        props.push(['Description', attr.description]);
    }

    // Lookup targets
    if (attr.targets && attr.targets.length > 0) {
        props.push(['Targets', attr.targets.join(', ')]);
    }

    // Option set info
    if (attr.optionSetName) {
        props.push(['Option Set Name', attr.optionSetName]);
        props.push(['Is Global Option Set', attr.isGlobalOptionSet ? 'Yes' : 'No']);
    }
    if (attr.options && attr.options.length > 0) {
        props.push(['Option Count', attr.options.length]);
    }

    const table = document.createElement('table');
    table.className = 'prop-panel-table';

    for (const [label, value] of props) {
        if (value === null || value === undefined) continue;
        const tr = document.createElement('tr');
        const labelTd = document.createElement('td');
        labelTd.className = 'prop-label';
        labelTd.textContent = label;
        tr.appendChild(labelTd);

        const valueTd = document.createElement('td');
        valueTd.className = 'prop-value';
        valueTd.textContent = String(value);
        tr.appendChild(valueTd);

        table.appendChild(tr);
    }

    panel.appendChild(table);

    // Show options table if present
    if (attr.options && attr.options.length > 0) {
        const optHeader = document.createElement('div');
        optHeader.className = 'prop-section-header';
        optHeader.textContent = 'Options';
        panel.appendChild(optHeader);

        const optTable = document.createElement('table');
        optTable.className = 'prop-panel-table';

        const headerRow = document.createElement('tr');
        for (const h of ['Value', 'Label', 'Color']) {
            const th = document.createElement('th');
            th.textContent = h;
            headerRow.appendChild(th);
        }
        optTable.appendChild(headerRow);

        for (const opt of attr.options) {
            const row = document.createElement('tr');

            const valTd = document.createElement('td');
            valTd.textContent = String(opt.value);
            row.appendChild(valTd);

            const labelTd2 = document.createElement('td');
            labelTd2.textContent = opt.label;
            row.appendChild(labelTd2);

            const colorTd = document.createElement('td');
            if (opt.color && /^#[0-9a-fA-F]{3,8}$/.test(opt.color)) {
                const swatch = document.createElement('span');
                swatch.className = 'color-swatch';
                swatch.style.backgroundColor = opt.color;
                colorTd.appendChild(swatch);
                colorTd.appendChild(document.createTextNode(opt.color));
            } else {
                colorTd.textContent = '\u2014';
            }
            row.appendChild(colorTd);

            optTable.appendChild(row);
        }
        panel.appendChild(optTable);
    }

    container.appendChild(panel);
}

// ── 1:N Relationships tab ──
function renderOneToManyTab(): void {
    if (!currentEntity) return;
    tabContent.innerHTML = '';

    const searchBar = buildTabSearchBar('1n', 'Search 1:N relationships...', () => {
        renderOneToManyTable(tabContent);
    });
    tabContent.appendChild(searchBar);
    renderOneToManyTable(tabContent);
}

interface OneToManyRow {
    schemaName: string;
    referencingEntity: string;
    referencingAttribute: string;
    cascadeDelete: string;
}

function getFiltered1N(): OneToManyRow[] {
    if (!currentEntity) return [];
    const rows: OneToManyRow[] = currentEntity.oneToManyRelationships.map(rel => ({
        schemaName: rel.schemaName,
        referencingEntity: rel.referencingEntity ?? '\u2014',
        referencingAttribute: rel.referencingAttribute ?? '\u2014',
        cascadeDelete: rel.cascadeDelete ?? '\u2014',
    }));

    const term = (tabSearchTerms['1n'] ?? '').toLowerCase().trim();
    if (!term) return rows;
    return rows.filter(r =>
        r.schemaName.toLowerCase().includes(term) ||
        r.referencingEntity.toLowerCase().includes(term) ||
        r.referencingAttribute.toLowerCase().includes(term)
    );
}

function renderOneToManyTable(container: HTMLElement): void {
    container.querySelector('.rel-table-wrapper')?.remove();
    const wrapper = document.createElement('div');
    wrapper.className = 'rel-table-wrapper';
    container.appendChild(wrapper);

    const rows = getFiltered1N();
    const table = new DataTable<OneToManyRow>({
        container: wrapper,
        columns: [
            { key: 'schemaName', label: 'Schema Name', render: (r) => escapeHtml(r.schemaName) },
            {
                key: 'referencingEntity', label: 'Related Entity',
                // MB-09: clickable related entity
                render: (r) => r.referencingEntity !== '\u2014'
                    ? `<span class="entity-link" data-entity="${escapeHtml(r.referencingEntity)}">${escapeHtml(r.referencingEntity)}</span>`
                    : '\u2014',
            },
            { key: 'referencingAttribute', label: 'Referencing Attribute', render: (r) => escapeHtml(r.referencingAttribute) },
            { key: 'cascadeDelete', label: 'Cascade Delete', render: (r) => escapeHtml(r.cascadeDelete) },
        ],
        getRowId: (r) => r.schemaName,
        defaultSortKey: 'schemaName',
        defaultSortDirection: 'asc',
        emptyMessage: 'No 1:N relationships',
    });
    table.setItems(rows);

    // Wire entity link clicks (MB-09)
    wireRelationshipEntityLinks(wrapper);
}

// ── N:1 Relationships tab ──
function renderManyToOneTab(): void {
    if (!currentEntity) return;
    tabContent.innerHTML = '';

    const searchBar = buildTabSearchBar('n1', 'Search N:1 relationships...', () => {
        renderManyToOneTable(tabContent);
    });
    tabContent.appendChild(searchBar);
    renderManyToOneTable(tabContent);
}

interface ManyToOneRow {
    schemaName: string;
    referencedEntity: string;
    referencingAttribute: string;
    cascadeDelete: string;
}

function getFilteredN1(): ManyToOneRow[] {
    if (!currentEntity) return [];
    const rows: ManyToOneRow[] = currentEntity.manyToOneRelationships.map(rel => ({
        schemaName: rel.schemaName,
        referencedEntity: rel.referencedEntity ?? '\u2014',
        referencingAttribute: rel.referencingAttribute ?? '\u2014',
        cascadeDelete: rel.cascadeDelete ?? '\u2014',
    }));

    const term = (tabSearchTerms['n1'] ?? '').toLowerCase().trim();
    if (!term) return rows;
    return rows.filter(r =>
        r.schemaName.toLowerCase().includes(term) ||
        r.referencedEntity.toLowerCase().includes(term) ||
        r.referencingAttribute.toLowerCase().includes(term)
    );
}

function renderManyToOneTable(container: HTMLElement): void {
    container.querySelector('.rel-table-wrapper')?.remove();
    const wrapper = document.createElement('div');
    wrapper.className = 'rel-table-wrapper';
    container.appendChild(wrapper);

    const rows = getFilteredN1();
    const table = new DataTable<ManyToOneRow>({
        container: wrapper,
        columns: [
            { key: 'schemaName', label: 'Schema Name', render: (r) => escapeHtml(r.schemaName) },
            {
                key: 'referencedEntity', label: 'Referenced Entity',
                render: (r) => r.referencedEntity !== '\u2014'
                    ? `<span class="entity-link" data-entity="${escapeHtml(r.referencedEntity)}">${escapeHtml(r.referencedEntity)}</span>`
                    : '\u2014',
            },
            { key: 'referencingAttribute', label: 'Referencing Attribute', render: (r) => escapeHtml(r.referencingAttribute) },
            { key: 'cascadeDelete', label: 'Cascade Delete', render: (r) => escapeHtml(r.cascadeDelete) },
        ],
        getRowId: (r) => r.schemaName,
        defaultSortKey: 'schemaName',
        defaultSortDirection: 'asc',
        emptyMessage: 'No N:1 relationships',
    });
    table.setItems(rows);

    wireRelationshipEntityLinks(wrapper);
}

// ── N:N Relationships tab ──
function renderManyToManyTab(): void {
    if (!currentEntity) return;
    tabContent.innerHTML = '';

    const searchBar = buildTabSearchBar('nn', 'Search N:N relationships...', () => {
        renderManyToManyTable(tabContent);
    });
    tabContent.appendChild(searchBar);
    renderManyToManyTable(tabContent);
}

interface ManyToManyRow {
    schemaName: string;
    relatedEntity: string;
    intersectEntity: string;
    entity1Attribute: string;
    entity2Attribute: string;
}

function getFilteredNN(): ManyToManyRow[] {
    if (!currentEntity) return [];
    const rows: ManyToManyRow[] = currentEntity.manyToManyRelationships.map(rel => {
        const related = rel.entity1LogicalName === currentEntity!.logicalName
            ? rel.entity2LogicalName
            : rel.entity1LogicalName;
        return {
            schemaName: rel.schemaName,
            relatedEntity: related ?? '\u2014',
            intersectEntity: rel.intersectEntityName ?? '\u2014',
            entity1Attribute: rel.entity1IntersectAttribute ?? '\u2014',
            entity2Attribute: rel.entity2IntersectAttribute ?? '\u2014',
        };
    });

    const term = (tabSearchTerms['nn'] ?? '').toLowerCase().trim();
    if (!term) return rows;
    return rows.filter(r =>
        r.schemaName.toLowerCase().includes(term) ||
        r.relatedEntity.toLowerCase().includes(term) ||
        r.intersectEntity.toLowerCase().includes(term)
    );
}

function renderManyToManyTable(container: HTMLElement): void {
    container.querySelector('.rel-table-wrapper')?.remove();
    const wrapper = document.createElement('div');
    wrapper.className = 'rel-table-wrapper';
    container.appendChild(wrapper);

    const rows = getFilteredNN();
    const table = new DataTable<ManyToManyRow>({
        container: wrapper,
        columns: [
            { key: 'schemaName', label: 'Schema Name', render: (r) => escapeHtml(r.schemaName) },
            {
                key: 'relatedEntity', label: 'Related Entity',
                render: (r) => r.relatedEntity !== '\u2014'
                    ? `<span class="entity-link" data-entity="${escapeHtml(r.relatedEntity)}">${escapeHtml(r.relatedEntity)}</span>`
                    : '\u2014',
            },
            { key: 'intersectEntity', label: 'Intersect Entity', render: (r) => escapeHtml(r.intersectEntity) },
            { key: 'entity1Attribute', label: 'Entity1 Attribute', render: (r) => escapeHtml(r.entity1Attribute) },
            { key: 'entity2Attribute', label: 'Entity2 Attribute', render: (r) => escapeHtml(r.entity2Attribute) },
        ],
        getRowId: (r) => r.schemaName,
        defaultSortKey: 'schemaName',
        defaultSortDirection: 'asc',
        emptyMessage: 'No N:N relationships',
    });
    table.setItems(rows);

    wireRelationshipEntityLinks(wrapper);
}

// MB-09: wire clicks on entity-link spans to navigate to the entity
function wireRelationshipEntityLinks(container: HTMLElement): void {
    container.addEventListener('click', (evt) => {
        const target = evt.target as HTMLElement;
        if (target.classList.contains('entity-link')) {
            const entityName = target.getAttribute('data-entity');
            if (entityName) {
                // Select the entity in the list and load its detail
                selectEntityByName(entityName);
            }
        }
    });
}

function selectEntityByName(logicalName: string): void {
    const entity = allEntities.find(e => e.logicalName === logicalName);
    if (!entity) return;

    selectedEntityName = logicalName;
    selectedChoiceName = null;
    currentGlobalChoice = null;

    // Update selection in list
    entityListEl.querySelectorAll<HTMLElement>('.entity-row.selected, .choice-row.selected')
        .forEach(r => r.classList.remove('selected'));
    const row = entityListEl.querySelector<HTMLElement>(`.entity-row[data-name="${CSS.escape(logicalName)}"]`);
    if (row) {
        row.classList.add('selected');
        row.scrollIntoView({ block: 'nearest' });
    }

    vscode.postMessage({ command: 'selectEntity', logicalName });
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

    const searchBar = buildTabSearchBar('keys', 'Search keys...', () => {
        renderKeysTable(tabContent);
    });
    tabContent.appendChild(searchBar);
    renderKeysTable(tabContent);
}

function renderKeysTable(container: HTMLElement): void {
    container.querySelector('.keys-table-wrapper')?.remove();
    const wrapper = document.createElement('div');
    wrapper.className = 'keys-table-wrapper';
    container.appendChild(wrapper);

    const term = (tabSearchTerms['keys'] ?? '').toLowerCase().trim();
    let rows: KeyRow[] = currentEntity!.keys.map(k => ({
        displayName: k.displayName ?? '\u2014',
        schemaName: k.schemaName,
        keyAttributes: k.keyAttributes.join(', '),
        indexStatus: k.entityKeyIndexStatus ?? '\u2014',
    }));

    if (term) {
        rows = rows.filter(r =>
            r.displayName.toLowerCase().includes(term) ||
            r.schemaName.toLowerCase().includes(term) ||
            r.keyAttributes.toLowerCase().includes(term)
        );
    }

    const table = new DataTable<KeyRow>({
        container: wrapper,
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

    const searchBar = buildTabSearchBar('privileges', 'Search privileges...', () => {
        renderPrivilegesTable(tabContent);
    });
    tabContent.appendChild(searchBar);
    renderPrivilegesTable(tabContent);
}

function renderPrivilegesTable(container: HTMLElement): void {
    container.querySelector('.priv-table-wrapper')?.remove();
    const wrapper = document.createElement('div');
    wrapper.className = 'priv-table-wrapper';
    container.appendChild(wrapper);

    const term = (tabSearchTerms['privileges'] ?? '').toLowerCase().trim();
    let rows: PrivilegeRow[] = currentEntity!.privileges.map(p => ({
        name: p.name,
        privilegeType: p.privilegeType,
        canBeLocal: p.canBeLocal,
        canBeDeep: p.canBeDeep,
        canBeGlobal: p.canBeGlobal,
        canBeBasic: p.canBeBasic,
    }));

    if (term) {
        rows = rows.filter(r =>
            r.name.toLowerCase().includes(term) ||
            r.privilegeType.toLowerCase().includes(term)
        );
    }

    const table = new DataTable<PrivilegeRow>({
        container: wrapper,
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

// ── Choices tab (entity-level) ──
function renderChoicesTab(): void {
    if (!currentEntity) return;
    tabContent.innerHTML = '';

    const searchBar = buildTabSearchBar('choices', 'Search choices...', () => {
        renderChoicesContent(tabContent);
    });
    tabContent.appendChild(searchBar);
    renderChoicesContent(tabContent);
}

function renderChoicesContent(container: HTMLElement): void {
    container.querySelector('.choices-content')?.remove();
    const wrapper = document.createElement('div');
    wrapper.className = 'choices-content';
    container.appendChild(wrapper);

    const term = (tabSearchTerms['choices'] ?? '').toLowerCase().trim();

    // Entity Choices: attributes with local option sets
    let entityChoiceAttrs = currentEntity!.attributes.filter(
        a => a.options && a.options.length > 0 && !a.isGlobalOptionSet
    );
    if (term) {
        entityChoiceAttrs = entityChoiceAttrs.filter(a =>
            a.logicalName.toLowerCase().includes(term) ||
            (a.optionSetName ?? '').toLowerCase().includes(term)
        );
    }

    // Global Option Sets
    let globalOptionSets = currentEntity!.globalOptionSets ?? [];
    if (term) {
        globalOptionSets = globalOptionSets.filter(os =>
            os.name.toLowerCase().includes(term) ||
            (os.displayName ?? '').toLowerCase().includes(term)
        );
    }

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

    wrapper.appendChild(entitySection);

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

    wrapper.appendChild(globalSection);
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

// ── Global Choice detail tab ──
function renderGlobalChoiceDetailTab(): void {
    if (!currentGlobalChoice) return;
    tabContent.innerHTML = '';

    const searchBar = buildTabSearchBar('choice-detail', 'Search options...', () => {
        renderGlobalChoiceOptionsTable(tabContent);
    });
    tabContent.appendChild(searchBar);
    renderGlobalChoiceOptionsTable(tabContent);
}

interface OptionValueRow {
    value: number;
    label: string;
    color: string;
    description: string;
}

function renderGlobalChoiceOptionsTable(container: HTMLElement): void {
    container.querySelector('.choice-detail-wrapper')?.remove();

    const wrapper = document.createElement('div');
    wrapper.className = 'choice-detail-wrapper';

    // Summary info
    const summary = document.createElement('div');
    summary.className = 'choice-summary';
    const props: [string, string][] = [
        ['Display Name', currentGlobalChoice!.displayName || '\u2014'],
        ['Name', currentGlobalChoice!.name],
        ['Type', currentGlobalChoice!.optionSetType],
        ['Options', String(currentGlobalChoice!.options.length)],
        ['Is Global', 'Yes'],
    ];
    for (const [label, value] of props) {
        const row = document.createElement('div');
        row.className = 'choice-summary-row';
        const lbl = document.createElement('span');
        lbl.className = 'choice-summary-label';
        lbl.textContent = label + ': ';
        const val = document.createElement('span');
        val.textContent = value;
        row.appendChild(lbl);
        row.appendChild(val);
        summary.appendChild(row);
    }
    wrapper.appendChild(summary);

    const term = (tabSearchTerms['choice-detail'] ?? '').toLowerCase().trim();
    let rows: OptionValueRow[] = currentGlobalChoice!.options.map(o => ({
        value: o.value,
        label: o.label,
        color: o.color ?? '\u2014',
        description: o.description ?? '\u2014',
    }));

    if (term) {
        rows = rows.filter(r =>
            String(r.value).includes(term) ||
            r.label.toLowerCase().includes(term)
        );
    }

    const table = new DataTable<OptionValueRow>({
        container: wrapper,
        columns: [
            { key: 'value', label: 'Value', render: (o) => escapeHtml(String(o.value)) },
            { key: 'label', label: 'Label', render: (o) => escapeHtml(o.label) },
            {
                key: 'color', label: 'Color',
                render: (o) => {
                    if (o.color !== '\u2014' && /^#[0-9a-fA-F]{3,8}$/.test(o.color)) {
                        return `<span class="color-swatch" style="background-color:${escapeHtml(o.color)}"></span>${escapeHtml(o.color)}`;
                    }
                    return '\u2014';
                },
            },
            { key: 'description', label: 'Description', render: (o) => escapeHtml(o.description) },
        ],
        getRowId: (o) => String(o.value),
        defaultSortKey: 'value',
        defaultSortDirection: 'asc',
        emptyMessage: 'No options',
    });
    table.setItems(rows);

    container.appendChild(wrapper);
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
            (refreshBtn as HTMLButtonElement).disabled = false;
            refreshBtn.textContent = 'Refresh';
            allEntities = msg.entities;
            intersectHiddenCount = msg.intersectHiddenCount;
            entitySearchEl.value = '';
            applyFilter();
            if (!currentEntity && !currentGlobalChoice) {
                tabContent.innerHTML = '';
                const empty = document.createElement('div');
                empty.className = 'empty-state';
                empty.textContent = 'Select an entity to view details';
                tabContent.appendChild(empty);
            }
            {
                const reconnectBanner = document.getElementById('reconnect-banner');
                if (reconnectBanner) reconnectBanner.style.display = 'none';
            }
            break;
        case 'globalChoicesLoaded':
            allGlobalChoices = msg.choices;
            applyFilter();
            break;
        case 'entityDetailLoading':
            tabContent.innerHTML = '';
            {
                const loadingDiv = document.createElement('div');
                loadingDiv.className = 'loading-state';
                const spinner = document.createElement('div');
                spinner.className = 'spinner';
                const loadingText = document.createElement('div');
                loadingText.textContent = 'Loading ' + msg.logicalName + '...';
                loadingDiv.appendChild(spinner);
                loadingDiv.appendChild(loadingText);
                tabContent.appendChild(loadingDiv);
            }
            break;
        case 'globalChoiceDetailLoading':
            tabContent.innerHTML = '';
            {
                const loadingDiv = document.createElement('div');
                loadingDiv.className = 'loading-state';
                const spinner = document.createElement('div');
                spinner.className = 'spinner';
                const loadingText = document.createElement('div');
                loadingText.textContent = 'Loading ' + msg.name + '...';
                loadingDiv.appendChild(spinner);
                loadingDiv.appendChild(loadingText);
                tabContent.appendChild(loadingDiv);
            }
            break;
        case 'entityDetailLoaded':
            currentEntity = msg.entity;
            currentGlobalChoice = null;
            activeTab = 'attributes';
            updateBreadcrumb();
            renderTabBar();
            renderTabContent();
            break;
        case 'globalChoiceDetailLoaded':
            currentGlobalChoice = msg.choice;
            currentEntity = null;
            activeTab = 'choice-detail';
            updateBreadcrumb();
            renderTabBar();
            renderTabContent();
            break;
        case 'loading':
            entityListEl.innerHTML = '';
            tabContent.innerHTML = '';
            {
                const loadingDiv = document.createElement('div');
                loadingDiv.className = 'loading-state';
                const spinner = document.createElement('div');
                spinner.className = 'spinner';
                const loadingText = document.createElement('div');
                loadingText.textContent = 'Loading entities...';
                loadingDiv.appendChild(spinner);
                loadingDiv.appendChild(loadingText);
                tabContent.appendChild(loadingDiv);
            }
            statusText.textContent = 'Loading...';
            entityBreadcrumb.style.display = 'none';
            break;
        case 'error':
            (refreshBtn as HTMLButtonElement).disabled = false;
            refreshBtn.textContent = 'Refresh';
            tabContent.innerHTML = '';
            {
                const errorDiv = document.createElement('div');
                errorDiv.className = 'error-state';
                errorDiv.textContent = msg.message;
                tabContent.appendChild(errorDiv);
            }
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
