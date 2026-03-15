// solutions-panel.ts
// External webview script for the Solutions panel.
// Built by esbuild as IIFE for browser, loaded via <script src="...">.

import { escapeHtml, escapeAttr, cssEscape, formatDate } from './shared/dom-utils.js';
import type { SolutionsPanelWebviewToHost, SolutionsPanelHostToWebview, SolutionViewDto, ComponentGroupDto } from './shared/message-types.js';
import { assertNever } from './shared/assert-never.js';
import { getVsCodeApi } from './shared/vscode-api.js';

const vscode = getVsCodeApi<SolutionsPanelWebviewToHost>();
const content = document.getElementById('content') as HTMLElement;
const statusText = document.getElementById('status-text') as HTMLElement;
const refreshBtn = document.getElementById('refresh-btn') as HTMLElement;
const managedBtn = document.getElementById('managed-btn') as HTMLElement;
const searchInput = document.getElementById('search-input') as HTMLInputElement;

let solutions: SolutionViewDto[] = [];
const expandedSolutions = new Set<string>();
const expandedGroups = new Set<string>();
let managedOn = false;

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

managedBtn.addEventListener('click', () => {
    managedOn = !managedOn;
    managedBtn.textContent = managedOn ? 'Managed: On' : 'Managed: Off';
    managedBtn.setAttribute('appearance', managedOn ? 'primary' : 'secondary');
    vscode.postMessage({ command: 'toggleManaged' });
});

document.getElementById('reconnect-refresh')!.addEventListener('click', (e) => {
    e.preventDefault();
    document.getElementById('reconnect-banner')!.style.display = 'none';
    vscode.postMessage({ command: 'refresh' });
});

let filterText = '';
let filterTimeout: ReturnType<typeof setTimeout> | null = null;

searchInput.addEventListener('input', () => {
    if (filterTimeout !== null) clearTimeout(filterTimeout);
    filterTimeout = setTimeout(() => {
        filterText = searchInput.value.trim().toLowerCase();
        applyFilter();
    }, 150);
});

function applyFilter(): void {
    if (!solutions.length) return;
    const rows = content.querySelectorAll<HTMLElement>('.solution-list > li');
    let visibleCount = 0;
    rows.forEach((li, idx) => {
        const sol = solutions[idx];
        if (!sol) return;
        const matches = !filterText ||
            sol.friendlyName.toLowerCase().includes(filterText) ||
            sol.uniqueName.toLowerCase().includes(filterText) ||
            (sol.publisherName && sol.publisherName.toLowerCase().includes(filterText));
        li.style.display = matches ? '' : 'none';
        if (matches) visibleCount++;
    });

    if (filterText) {
        statusText.textContent = visibleCount + ' of ' + solutions.length + ' solution' + (solutions.length !== 1 ? 's' : '');
    } else {
        statusText.textContent = solutions.length + ' solution' + (solutions.length !== 1 ? 's' : '');
    }

    if (filterText && visibleCount === 0) {
        let emptyEl = content.querySelector('.filter-empty');
        if (!emptyEl) {
            emptyEl = document.createElement('div');
            emptyEl.className = 'empty-state filter-empty';
            emptyEl.textContent = 'No solutions match filter';
            content.appendChild(emptyEl);
        }
    } else {
        const emptyEl = content.querySelector('.filter-empty');
        if (emptyEl) emptyEl.remove();
    }
}

// ── Delegated click handler for solution/component rows ──
content.addEventListener('click', (e) => {
    const makerBtn = (e.target as HTMLElement).closest<HTMLElement>('.open-maker-btn');
    if (makerBtn) {
        const solutionId = makerBtn.dataset.solutionId;
        vscode.postMessage({ command: 'openInMaker', solutionId });
        e.stopPropagation();
        return;
    }

    const solutionRow = (e.target as HTMLElement).closest<HTMLElement>('.solution-row');
    if (solutionRow) {
        const uniqueName = solutionRow.dataset.uniqueName!;
        if (expandedSolutions.has(uniqueName)) {
            expandedSolutions.delete(uniqueName);
            vscode.postMessage({ command: 'collapseSolution', uniqueName });
            updateSolutionExpansion(uniqueName, false);
        } else {
            expandedSolutions.add(uniqueName);
            vscode.postMessage({ command: 'expandSolution', uniqueName });
            updateSolutionExpansion(uniqueName, true);
        }
        return;
    }

    const groupRow = (e.target as HTMLElement).closest<HTMLElement>('.component-group');
    if (groupRow) {
        const groupKey = groupRow.dataset.groupKey!;
        const itemsEl = groupRow.nextElementSibling;
        const chevron = groupRow.querySelector('.chevron');
        if (expandedGroups.has(groupKey)) {
            expandedGroups.delete(groupKey);
            if (itemsEl) itemsEl.classList.remove('expanded');
            if (chevron) chevron.classList.remove('expanded');
        } else {
            expandedGroups.add(groupKey);
            if (itemsEl) itemsEl.classList.add('expanded');
            if (chevron) chevron.classList.add('expanded');
        }
    }
});

// Component item click -> toggle detail card
content.addEventListener('click', (e) => {
    const copyBtn = (e.target as HTMLElement).closest<HTMLElement>('.copy-btn');
    if (copyBtn) {
        const text = copyBtn.dataset.copy!;
        vscode.postMessage({ command: 'copyToClipboard', text });
        copyBtn.textContent = '\u2713';
        setTimeout(() => { copyBtn.textContent = '\u{1F4CB}'; }, 1500);
        e.stopPropagation();
        return;
    }

    const item = (e.target as HTMLElement).closest<HTMLElement>('.component-item');
    if (!item) return;

    const objectId = item.dataset.objectId!;
    const detailCard = content.querySelector<HTMLElement>('.component-detail-card[data-detail-for="' + cssEscape(objectId) + '"]');
    if (!detailCard) return;

    detailCard.classList.toggle('expanded');
});

// Keyboard: Enter/Space on component item
content.addEventListener('keydown', (e) => {
    if (e.key !== 'Enter' && e.key !== ' ') return;
    const item = (e.target as HTMLElement).closest<HTMLElement>('.component-item');
    if (!item) return;
    e.preventDefault();
    item.click();
});

function updateSolutionExpansion(uniqueName: string, expanded: boolean): void {
    const row = content.querySelector<HTMLElement>('.solution-row[data-unique-name="' + cssEscape(uniqueName) + '"]');
    if (!row) return;
    const chevron = row.querySelector('.chevron');
    const container = row.nextElementSibling;
    if (chevron) {
        if (expanded) chevron.classList.add('expanded');
        else chevron.classList.remove('expanded');
    }
    if (container && container.classList.contains('components-container')) {
        if (expanded) container.classList.add('expanded');
        else container.classList.remove('expanded');
    }
}

// ── Message handling ──
window.addEventListener('message', (event: MessageEvent<SolutionsPanelHostToWebview>) => {
    const msg = event.data;
    switch (msg.command) {
        case 'updateEnvironment':
            updateEnvironmentDisplay(msg.name);
            break;
        case 'solutionsLoaded':
            renderSolutions(msg.solutions, msg.managedCount, msg.includeManaged);
            {
                const reconnectBanner = document.getElementById('reconnect-banner');
                if (reconnectBanner) reconnectBanner.style.display = 'none';
            }
            break;
        case 'componentsLoading':
            showComponentsLoading(msg.uniqueName);
            break;
        case 'componentsLoaded':
            renderComponents(msg.uniqueName, msg.groups);
            break;
        case 'updateManagedState':
            managedOn = msg.includeManaged;
            managedBtn.textContent = managedOn ? 'Managed: On' : 'Managed: Off';
            managedBtn.setAttribute('appearance', managedOn ? 'primary' : 'secondary');
            break;
        case 'loading':
            content.innerHTML = '<div class="loading-state"><div class="spinner"></div><div>Loading solutions...</div></div>';
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

function renderSolutions(sols: SolutionViewDto[], managedCount: number, includeManaged: boolean): void {
    solutions = sols;
    searchInput.value = '';
    filterText = '';

    if (sols.length === 0) {
        content.innerHTML = '<div class="empty-state">No solutions found</div>';
        statusText.textContent = 'No solutions';
        return;
    }

    let html = '<ul class="solution-list">';
    for (const sol of sols) {
        const isExpanded = expandedSolutions.has(sol.uniqueName);
        html += '<li>';
        html += '<div class="solution-row" data-unique-name="' + escapeAttr(sol.uniqueName) + '">';
        html += '<span class="chevron' + (isExpanded ? ' expanded' : '') + '">&#9654;</span>';
        html += '<span class="icon">' + (sol.isManaged ? '&#128274;' : '&#128230;') + '</span>';
        html += '<span class="name">' + escapeHtml(sol.uniqueName) + (sol.friendlyName !== sol.uniqueName ? ' (' + escapeHtml(sol.friendlyName) + ')' : '') + '</span>';
        if (sol.version) {
            html += '<span class="version">(' + escapeHtml(sol.version) + ')</span>';
        }
        if (sol.publisherName) {
            html += '<span class="publisher">' + escapeHtml('\u2014 ' + sol.publisherName) + '</span>';
        }
        if (sol.isManaged) {
            html += '<span class="managed-badge">managed</span>';
        }
        html += '<button class="open-maker-btn" data-solution-id="' + escapeAttr(sol.id || '') + '" title="Open in Maker Portal">\uD83D\uDD17</button>';
        html += '</div>';
        html += '<div class="components-container' + (isExpanded ? ' expanded' : '') + '" id="components-' + escapeAttr(sol.uniqueName) + '">';
        html += '<div class="detail-card">';
        html += '<span class="detail-label">Unique Name</span><span class="detail-value">' + escapeHtml(sol.uniqueName) + '</span>';
        html += '<span class="detail-label">Publisher</span><span class="detail-value">' + escapeHtml(sol.publisherName || '\u2014') + '</span>';
        html += '<span class="detail-label">Type</span><span class="detail-value">' + (sol.isManaged ? 'Managed' : 'Unmanaged') + '</span>';
        if (sol.installedOn) {
            html += '<span class="detail-label">Installed</span><span class="detail-value">' + formatDate(sol.installedOn) + '</span>';
        }
        if (sol.modifiedOn) {
            html += '<span class="detail-label">Modified</span><span class="detail-value">' + formatDate(sol.modifiedOn) + '</span>';
        }
        if (sol.description) {
            html += '<div class="detail-description">' + escapeHtml(sol.description) + '</div>';
        }
        html += '</div>';
        html += '<div class="components-loading"><span class="spinner"></span> Loading components...</div>';
        html += '</div>';
        html += '</li>';
    }
    html += '</ul>';
    content.innerHTML = html;

    // Update status
    let statusMsg = sols.length + ' solution' + (sols.length !== 1 ? 's' : '');
    if (!includeManaged && managedCount > 0) {
        statusMsg += ' (' + managedCount + ' managed hidden)';
    }
    statusText.textContent = statusMsg;

    // Re-expand solutions that were previously expanded and re-request components
    for (const uniqueName of expandedSolutions) {
        const found = sols.find(s => s.uniqueName === uniqueName);
        if (found) {
            vscode.postMessage({ command: 'expandSolution', uniqueName });
        }
    }
}

function showComponentsLoading(uniqueName: string): void {
    const container = document.getElementById('components-' + uniqueName);
    if (container) {
        container.innerHTML = '<div class="components-loading"><span class="spinner"></span> Loading components...</div>';
    }
}

function renderComponents(uniqueName: string, groups: ComponentGroupDto[]): void {
    const container = document.getElementById('components-' + uniqueName);
    if (!container) return;

    if (!groups || groups.length === 0) {
        container.innerHTML = '<div class="components-loading">No components</div>';
        return;
    }

    let html = '';
    for (const group of groups) {
        const groupKey = uniqueName + '::' + group.typeName;
        const isGroupExpanded = expandedGroups.has(groupKey);

        html += '<div class="component-group" data-group-key="' + escapeAttr(groupKey) + '">';
        html += '<span class="chevron' + (isGroupExpanded ? ' expanded' : '') + '">&#9654;</span>';
        html += '<span class="group-name">' + escapeHtml(group.typeName) + '</span>';
        html += '<span class="group-count">(' + group.components.length + ')</span>';
        html += '</div>';

        html += '<div class="component-items' + (isGroupExpanded ? ' expanded' : '') + '">';
        for (const comp of group.components) {
            const name = comp.logicalName || comp.schemaName || comp.displayName || comp.objectId;
            let subtitle = '';
            if (comp.logicalName && comp.displayName && comp.displayName !== comp.logicalName) {
                subtitle = ' (' + escapeHtml(comp.displayName) + ')';
            }

            html += '<div class="component-item" tabindex="0" data-object-id="' + escapeAttr(comp.objectId) + '">';
            html += '<span class="item-chevron">&#8250;</span>';
            html += '<span class="component-name">' + escapeHtml(name) + subtitle + '</span>';
            if (comp.isMetadata) {
                html += ' <span class="metadata-badge">metadata</span>';
            }
            html += '</div>';

            // Detail card (hidden by default)
            html += '<div class="component-detail-card" data-detail-for="' + escapeAttr(comp.objectId) + '">';
            if (comp.logicalName) {
                html += '<span class="detail-label">Logical Name</span><span class="detail-value">' + escapeHtml(comp.logicalName) + '</span>';
            }
            if (comp.schemaName) {
                html += '<span class="detail-label">Schema Name</span><span class="detail-value">' + escapeHtml(comp.schemaName) + '</span>';
            }
            if (comp.displayName) {
                html += '<span class="detail-label">Display Name</span><span class="detail-value">' + escapeHtml(comp.displayName) + '</span>';
            }
            html += '<span class="detail-label">Object ID</span><span class="detail-value">' + escapeHtml(comp.objectId) + ' <button class="copy-btn" data-copy="' + escapeAttr(comp.objectId) + '">&#128203;</button></span>';
            html += '<span class="detail-label">Root Behavior</span><span class="detail-value">' + comp.rootComponentBehavior + '</span>';
            html += '<span class="detail-label">Metadata</span><span class="detail-value">' + (comp.isMetadata ? 'Yes' : 'No') + '</span>';
            html += '</div>';
        }
        html += '</div>';
    }
    container.innerHTML = html;
}

// Signal ready
vscode.postMessage({ command: 'ready' });
