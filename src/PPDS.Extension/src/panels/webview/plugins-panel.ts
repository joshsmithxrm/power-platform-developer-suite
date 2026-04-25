// plugins-panel.ts
// External webview script for the Plugins Panel.
// Built by esbuild as IIFE for browser, loaded via <script src="...">.

import { getVsCodeApi } from './shared/vscode-api.js';
import { installErrorHandler } from './shared/error-handler.js';
import type {
    PluginsPanelWebviewToHost,
    PluginsPanelHostToWebview,
    PluginTreeData,
    PluginTreeNode,
} from './shared/message-types.js';
import { assertNever } from './shared/assert-never.js';

const vscode = getVsCodeApi<PluginsPanelWebviewToHost>();
installErrorHandler((msg) => vscode.postMessage(msg as PluginsPanelWebviewToHost));

// ── State ────────────────────────────────────────────────────────────────────
let treeData: PluginTreeData | null = null;
let flatNodes: FlatNode[] = [];
let selectedNodeId: string | null = null;
let hideHidden = false;
let hideMicrosoft = false;
let searchText = '';
let currentViewMode: 'assembly' | 'message' | 'entity' = 'assembly';
// Track expanded node IDs across re-renders
const expandedIds = new Set<string>();

// ── DOM references ───────────────────────────────────────────────────────────
const treeContainer = document.getElementById('tree-container') as HTMLElement;
const detailPanel = document.getElementById('detail-panel') as HTMLElement;
const statusText = document.getElementById('status-text') as HTMLElement;
const envPickerBtn = document.getElementById('env-picker-btn') as HTMLElement;
const envPickerName = document.getElementById('env-picker-name') as HTMLElement;
const refreshBtn = document.getElementById('refresh-btn') as HTMLElement;
const searchInput = document.getElementById('search-input') as HTMLInputElement;
const hideHiddenCheck = document.getElementById('hide-hidden-check') as HTMLInputElement;
const hideMicrosoftCheck = document.getElementById('hide-microsoft-check') as HTMLInputElement;
const viewModeAssembly = document.getElementById('view-mode-assembly') as HTMLElement;
const viewModeMessage = document.getElementById('view-mode-message') as HTMLElement;
const viewModeEntity = document.getElementById('view-mode-entity') as HTMLElement;
const expandAllBtn = document.getElementById('expand-all-btn') as HTMLElement;
const collapseAllBtn = document.getElementById('collapse-all-btn') as HTMLElement;

// ── Virtual scrolling constants ───────────────────────────────────────────────
interface FlatNode {
    node: PluginTreeNode;
    depth: number;
    expanded: boolean;
}

const NODE_HEIGHT = 30; // px
const OVERSCAN = 10;

// ── Environment picker ───────────────────────────────────────────────────────
if (envPickerBtn) {
    envPickerBtn.addEventListener('click', () => {
        vscode.postMessage({ command: 'requestEnvironmentList' });
    });
}

function updateEnvironment(msg: { name: string; envType: string | null; envColor: string | null }): void {
    if (envPickerName) {
        envPickerName.textContent = msg.name || 'No environment';
    }
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

// ── Toolbar handlers ──────────────────────────────────────────────────────────
if (refreshBtn) {
    refreshBtn.addEventListener('click', () => {
        vscode.postMessage({ command: 'ready' });
    });
}

if (viewModeAssembly) {
    viewModeAssembly.addEventListener('click', () => {
        setViewMode('assembly');
    });
}
if (viewModeMessage) {
    viewModeMessage.addEventListener('click', () => {
        setViewMode('message');
    });
}
if (viewModeEntity) {
    viewModeEntity.addEventListener('click', () => {
        setViewMode('entity');
    });
}

function setViewMode(mode: 'assembly' | 'message' | 'entity'): void {
    currentViewMode = mode;
    [viewModeAssembly, viewModeMessage, viewModeEntity].forEach(btn => {
        if (btn) btn.classList.remove('active');
    });
    const activeBtn = mode === 'assembly' ? viewModeAssembly
        : mode === 'message' ? viewModeMessage
        : viewModeEntity;
    if (activeBtn) activeBtn.classList.add('active');
    vscode.postMessage({ command: 'setViewMode', mode });
    // Reorganize client-side and re-render (no server round-trip needed)
    expandedIds.clear();
    rebuildAndRender();
}

// ── Filter handlers ──────────────────────────────────────────────────────────
if (hideHiddenCheck) {
    hideHiddenCheck.addEventListener('change', () => {
        hideHidden = hideHiddenCheck.checked;
        vscode.postMessage({ command: 'applyFilter', hideHidden, hideMicrosoft });
        rebuildAndRender();
    });
}

if (hideMicrosoftCheck) {
    hideMicrosoftCheck.addEventListener('change', () => {
        hideMicrosoft = hideMicrosoftCheck.checked;
        vscode.postMessage({ command: 'applyFilter', hideHidden, hideMicrosoft });
        rebuildAndRender();
    });
}

// ── Search handler ────────────────────────────────────────────────────────────
let searchDebounceTimer: ReturnType<typeof setTimeout> | null = null;
if (searchInput) {
    searchInput.addEventListener('input', () => {
        if (searchDebounceTimer) clearTimeout(searchDebounceTimer);
        searchDebounceTimer = setTimeout(() => {
            searchText = searchInput.value.trim().toLowerCase();
            vscode.postMessage({ command: 'search', text: searchText });
            rebuildAndRender();
        }, 200);
    });
}

// ── Expand / Collapse all ─────────────────────────────────────────────────────

function expandAll(): void {
    if (!treeData) return;
    // Collect all node IDs in the current view's root set (recursively)
    const collectIds = (nodes: PluginTreeNode[]): void => {
        for (const node of nodes) {
            if (node.hasChildren || (node.children && node.children.length > 0)) {
                expandedIds.add(node.id);
                // Request lazy children if not yet loaded
                if (node.hasChildren && (!node.children || node.children.length === 0)) {
                    vscode.postMessage({ command: 'expandNode', nodeId: node.id, nodeType: node.nodeType });
                }
            }
            if (node.children && node.children.length > 0) {
                collectIds(node.children);
            }
        }
    };
    collectIds(getViewRootNodes());
    rebuildAndRender();
}

function collapseAll(): void {
    expandedIds.clear();
    rebuildAndRender();
}

if (expandAllBtn) {
    expandAllBtn.addEventListener('click', () => expandAll());
}
if (collapseAllBtn) {
    collapseAllBtn.addEventListener('click', () => collapseAll());
}

// ── View mode reorganization ──────────────────────────────────────────────────

/** Walk all nodes recursively and collect step nodes (nodeType === 'step'). */
function collectSteps(nodes: PluginTreeNode[]): PluginTreeNode[] {
    const steps: PluginTreeNode[] = [];
    for (const node of nodes) {
        if (node.nodeType === 'step') {
            steps.push(node);
        }
        if (node.children && node.children.length > 0) {
            for (const s of collectSteps(node.children)) {
                steps.push(s);
            }
        }
    }
    return steps;
}

/** Parse message and entity out of a step node name/badge. Returns { message, entity }. */
function parseStepLabel(node: PluginTreeNode): { message: string; entity: string } {
    // badge may carry "Create on account" style info; fall back to name parsing
    const badge = node.badge ?? '';
    // Try "MessageName on entity" pattern first
    const onMatch = badge.match(/^(.+?)\s+on\s+(.+)$/i);
    if (onMatch) {
        return { message: onMatch[1].trim(), entity: onMatch[2].trim() };
    }
    // Try extracting from node name: "TypeName.MethodName" → use name as message
    const nameParts = node.name.split('.');
    return {
        message: nameParts.length > 1 ? nameParts[nameParts.length - 1] : node.name,
        entity: 'none',
    };
}

/**
 * Create a synthetic virtual node (not backed by treeData) for grouping.
 * Uses a prefix to avoid ID collisions with real nodes.
 */
function makeGroupNode(id: string, name: string, nodeType: string, children: PluginTreeNode[]): PluginTreeNode {
    return {
        id: `__group__${id}`,
        name,
        nodeType,
        hasChildren: children.length > 0,
        children,
    };
}

/**
 * Reorganize the raw treeData roots for message view or entity view.
 * Assembly view just returns the raw roots (default order).
 */
function reorganizeForView(data: PluginTreeData, mode: 'assembly' | 'message' | 'entity'): PluginTreeNode[] {
    if (mode === 'assembly') {
        // Default: packages → assemblies → service endpoints → custom APIs → data sources
        const roots: PluginTreeNode[] = [];
        for (const pkg of data.packages) roots.push(pkg);
        for (const asm of data.assemblies) roots.push(asm);
        for (const se of data.serviceEndpoints) roots.push(se);
        for (const ca of data.customApis) roots.push(ca);
        for (const ds of data.dataSources) roots.push(ds);
        return roots;
    }

    // Collect all steps from assembly/package trees
    const allAssemblyRoots: PluginTreeNode[] = [
        ...data.packages,
        ...data.assemblies,
    ];
    const steps = collectSteps(allAssemblyRoots);

    if (mode === 'message') {
        // Group: message → entity → steps
        const messageMap = new Map<string, Map<string, PluginTreeNode[]>>();
        for (const step of steps) {
            const { message, entity } = parseStepLabel(step);
            if (!messageMap.has(message)) messageMap.set(message, new Map());
            const entityMap = messageMap.get(message)!;
            if (!entityMap.has(entity)) entityMap.set(entity, []);
            entityMap.get(entity)!.push(step);
        }
        const roots: PluginTreeNode[] = [];
        for (const [message, entityMap] of [...messageMap.entries()].sort(([a], [b]) => a.localeCompare(b))) {
            const entityNodes: PluginTreeNode[] = [];
            for (const [entity, stepList] of [...entityMap.entries()].sort(([a], [b]) => a.localeCompare(b))) {
                const entityDisplayName = entity === 'none' ? 'Global (no entity)' : entity;
                entityNodes.push(makeGroupNode(`msg_${message}_${entity}`, entityDisplayName, 'entityGroup', stepList));
            }
            roots.push(makeGroupNode(`msg_${message}`, message, 'messageGroup', entityNodes));
        }
        // Append non-step roots (service endpoints, custom APIs, data sources) at the end
        for (const se of data.serviceEndpoints) roots.push(se);
        for (const ca of data.customApis) roots.push(ca);
        for (const ds of data.dataSources) roots.push(ds);
        return roots;
    }

    if (mode === 'entity') {
        // Group: entity → message → steps
        const entityMap = new Map<string, Map<string, PluginTreeNode[]>>();
        for (const step of steps) {
            const { message, entity } = parseStepLabel(step);
            if (!entityMap.has(entity)) entityMap.set(entity, new Map());
            const messageMap = entityMap.get(entity)!;
            if (!messageMap.has(message)) messageMap.set(message, []);
            messageMap.get(message)!.push(step);
        }
        const roots: PluginTreeNode[] = [];
        for (const [entity, messageMap] of [...entityMap.entries()].sort(([a], [b]) => a.localeCompare(b))) {
            const messageNodes: PluginTreeNode[] = [];
            for (const [message, stepList] of [...messageMap.entries()].sort(([a], [b]) => a.localeCompare(b))) {
                messageNodes.push(makeGroupNode(`ent_${entity}_${message}`, message, 'messageGroup', stepList));
            }
            // Replace "none" key with a friendly label for the display name
            const displayName = entity === 'none' ? 'Global (no entity)' : entity;
            roots.push(makeGroupNode(`ent_${entity}`, displayName, 'entityGroup', messageNodes));
        }
        // Append non-step roots
        for (const se of data.serviceEndpoints) roots.push(se);
        for (const ca of data.customApis) roots.push(ca);
        for (const ds of data.dataSources) roots.push(ds);
        return roots;
    }

    return [];
}

// ── Tree flattening ───────────────────────────────────────────────────────────

/**
 * Test whether a node directly matches the current search/filter predicates.
 * Does NOT consider descendants — use subtreeMatchesFilters for that.
 */
function nodeMatchesFilters(node: PluginTreeNode): boolean {
    if (hideHidden && node.isHidden) return false;
    // hideMicrosoft applies to assembly-level nodes and package nodes that wrap Microsoft assemblies.
    // Assembly names start with "Microsoft." (e.g. Microsoft.Crm.*).
    // Package names for Microsoft bundles start with "mspp_microsoft" (case-insensitive).
    if (hideMicrosoft) {
        const lc = node.name.toLowerCase();
        if (node.nodeType === 'assembly') {
            if (lc.startsWith('microsoft.') && lc !== 'microsoft.crm.servicebus') return false;
        }
        if (node.nodeType === 'package') {
            if (lc.startsWith('mspp_microsoft')) return false;
        }
    }
    return true;
}

/**
 * Returns true if this node or any descendant matches search text.
 * Used to keep ancestor nodes visible when a descendant matches.
 */
function subtreeMatchesSearch(node: PluginTreeNode): boolean {
    if (!searchText) return true;
    if (node.name.toLowerCase().includes(searchText)) return true;
    if (node.children) {
        for (const child of node.children) {
            if (subtreeMatchesSearch(child)) return true;
        }
    }
    return false;
}

/** Collect all root nodes for the current view mode. */
function getViewRootNodes(): PluginTreeNode[] {
    if (!treeData) return [];
    return reorganizeForView(treeData, currentViewMode);
}

/**
 * Flatten the tree recursively into FlatNode[], respecting expanded state and filters.
 * When searchText is active, ancestor nodes of matching descendants are kept visible
 * and their branches are auto-expanded.
 */
function flattenTree(nodes: PluginTreeNode[], depth: number): FlatNode[] {
    const result: FlatNode[] = [];
    for (const node of nodes) {
        if (!nodeMatchesFilters(node)) continue;
        // When searching, skip nodes whose entire subtree has no match
        if (searchText && !subtreeMatchesSearch(node)) continue;
        // Auto-expand this node if it contains a search match in a descendant
        // (but the node itself doesn't match — we want to show the matching leaf)
        const hasDescendantMatch = searchText
            && !node.name.toLowerCase().includes(searchText)
            && node.children
            && node.children.some(c => subtreeMatchesSearch(c));
        if (hasDescendantMatch) {
            expandedIds.add(node.id);
        }
        const expanded = expandedIds.has(node.id);
        result.push({ node, depth, expanded });
        if (expanded && node.children && node.children.length > 0) {
            const childFlat = flattenTree(node.children, depth + 1);
            for (const child of childFlat) {
                result.push(child);
            }
        }
    }
    return result;
}

function rebuildFlatNodes(): void {
    if (!treeData) {
        flatNodes = [];
        return;
    }
    flatNodes = flattenTree(getViewRootNodes(), 0);
}

function rebuildAndRender(): void {
    rebuildFlatNodes();
    renderTree();
}

// ── Virtual scrolling ─────────────────────────────────────────────────────────

let scrollRafId: number | null = null;

if (treeContainer) {
    treeContainer.addEventListener('scroll', () => {
        if (scrollRafId) return;
        scrollRafId = requestAnimationFrame(() => {
            renderTree();
            scrollRafId = null;
        });
    });
}

function renderTree(): void {
    if (!treeContainer) return;

    const totalHeight = flatNodes.length * NODE_HEIGHT;
    const containerHeight = treeContainer.clientHeight;
    const scrollTop = treeContainer.scrollTop;

    const firstVisible = Math.max(0, Math.floor(scrollTop / NODE_HEIGHT) - OVERSCAN);
    const lastVisible = Math.min(
        flatNodes.length - 1,
        Math.ceil((scrollTop + containerHeight) / NODE_HEIGHT) + OVERSCAN,
    );

    // Build the new content in a fragment
    const fragment = document.createDocumentFragment();

    // Top spacer
    if (firstVisible > 0) {
        const topSpacer = document.createElement('div');
        topSpacer.style.height = `${firstVisible * NODE_HEIGHT}px`;
        topSpacer.style.flexShrink = '0';
        fragment.appendChild(topSpacer);
    }

    // Visible nodes
    for (let i = firstVisible; i <= lastVisible && i < flatNodes.length; i++) {
        const flatNode = flatNodes[i];
        const row = renderNode(flatNode);
        if (flatNode.node.id === selectedNodeId) {
            row.classList.add('selected');
        }
        fragment.appendChild(row);
    }

    // Bottom spacer
    const renderedCount = lastVisible - firstVisible + 1;
    const remainingCount = flatNodes.length - firstVisible - renderedCount;
    if (remainingCount > 0) {
        const bottomSpacer = document.createElement('div');
        bottomSpacer.style.height = `${remainingCount * NODE_HEIGHT}px`;
        bottomSpacer.style.flexShrink = '0';
        fragment.appendChild(bottomSpacer);
    }

    // Wrap in a flex column so spacers + visible nodes stack correctly
    const inner = document.createElement('div');
    inner.style.display = 'flex';
    inner.style.flexDirection = 'column';
    inner.style.minHeight = `${totalHeight}px`;
    inner.appendChild(fragment);

    // Replace content
    treeContainer.innerHTML = '';
    treeContainer.appendChild(inner);

    // Update status — show descriptive breakdown from host when available, else item count
    if (statusText) {
        if (treeData?.statusSummary) {
            statusText.textContent = treeData.statusSummary;
        } else {
            statusText.textContent = flatNodes.length === 0
                ? 'No items'
                : `${flatNodes.length} item${flatNodes.length !== 1 ? 's' : ''}`;
        }
    }
}

function getNodeIcon(nodeType: string): string {
    switch (nodeType) {
        case 'package': return '\uD83D\uDCE6';             // 📦
        case 'assembly': return '\u2699\uFE0F';             // ⚙️
        case 'type': return '\uD83D\uDD0C';                 // 🔌
        case 'step': return '\u26A1';                       // ⚡
        case 'image': return '\uD83D\uDDBC\uFE0F';         // 🖼️
        case 'webhook': return '\uD83C\uDF10';              // 🌐
        case 'serviceEndpoint': return '\uD83D\uDCE1';      // 📡
        case 'customApi': return '\uD83D\uDCE8';            // 📨
        case 'dataSource': return '\uD83D\uDDC3\uFE0F';    // 🗃️
        case 'dataProvider': return '\uD83D\uDDC4\uFE0F';  // 🗄️
        case 'messageGroup': return '\uD83D\uDCEC';         // 📬 (message group)
        case 'entityGroup': return '\uD83D\uDDC2\uFE0F';   // 🗂️ (entity group)
        default: return '\uD83D\uDCC4';                     // 📄
    }
}

function renderNode(flatNode: FlatNode): HTMLElement {
    const row = document.createElement('div');
    row.className = 'tree-node';
    row.style.height = `${NODE_HEIGHT}px`;
    row.style.paddingLeft = `${flatNode.depth * 16 + 4}px`;
    row.dataset['nodeId'] = flatNode.node.id;
    row.dataset['nodeType'] = flatNode.node.nodeType;

    // Toggle arrow (if has children)
    if (flatNode.node.hasChildren || (flatNode.node.children && flatNode.node.children.length > 0)) {
        const toggle = document.createElement('span');
        toggle.className = 'tree-toggle';
        toggle.textContent = flatNode.expanded ? '▾' : '▸'; // ▾ ▸
        toggle.addEventListener('click', (e) => {
            e.stopPropagation();
            toggleNode(flatNode);
        });
        row.appendChild(toggle);
    } else {
        const spacer = document.createElement('span');
        spacer.className = 'tree-toggle-spacer';
        row.appendChild(spacer);
    }

    // Icon
    const icon = document.createElement('span');
    icon.className = 'tree-icon';
    icon.textContent = getNodeIcon(flatNode.node.nodeType);
    row.appendChild(icon);

    // Name — textContent only, never innerHTML with untrusted data
    const name = document.createElement('span');
    name.className = 'tree-name';
    name.textContent = flatNode.node.name;
    row.appendChild(name);

    // Badge
    if (flatNode.node.badge) {
        const badge = document.createElement('span');
        badge.className = 'tree-badge';
        badge.textContent = flatNode.node.badge;
        row.appendChild(badge);
    }

    // Status indicators
    if (flatNode.node.isEnabled === false) {
        row.classList.add('disabled');
        if (flatNode.node.nodeType === 'step') {
            const disabledIcon = document.createElement('span');
            disabledIcon.className = 'status-disabled';
            disabledIcon.textContent = '\uD83D\uDEAB'; // 🚫
            disabledIcon.title = 'Disabled';
            row.appendChild(disabledIcon);
        }
    }
    if (flatNode.node.isManaged) {
        const managedLabel = document.createElement('span');
        managedLabel.className = 'managed-label';
        managedLabel.textContent = '(managed)';
        row.appendChild(managedLabel);
    }

    // Right-click context menu data for VS Code
    row.setAttribute('data-vscode-context', JSON.stringify({
        webviewSection: 'pluginTreeNode',
        nodeId: flatNode.node.id,
        nodeType: flatNode.node.nodeType,
        preventDefaultContextMenuItems: true,
    }));

    // Click to select
    row.addEventListener('click', () => {
        selectNode(flatNode);
    });

    return row;
}

// ── Node interactions ─────────────────────────────────────────────────────────

function toggleNode(flatNode: FlatNode): void {
    const nodeId = flatNode.node.id;

    if (expandedIds.has(nodeId)) {
        expandedIds.delete(nodeId);
        rebuildAndRender();
    } else {
        expandedIds.add(nodeId);
        // If children not yet loaded, request them from the host
        if (flatNode.node.hasChildren && (!flatNode.node.children || flatNode.node.children.length === 0)) {
            vscode.postMessage({ command: 'expandNode', nodeId, nodeType: flatNode.node.nodeType });
            showLoadingInNode(nodeId);
        } else {
            rebuildAndRender();
        }
    }
}

function selectNode(flatNode: FlatNode): void {
    selectedNodeId = flatNode.node.id;
    // Re-render to reflect selection highlight
    renderTree();
    // Request detail from host
    vscode.postMessage({ command: 'selectNode', nodeId: flatNode.node.id, nodeType: flatNode.node.nodeType });
}

/** Show a loading indicator inside a specific node row (children pending). */
function showLoadingInNode(nodeId: string): void {
    const row = treeContainer?.querySelector<HTMLElement>(`[data-node-id="${CSS.escape(nodeId)}"]`);
    if (row) {
        const spinner = document.createElement('span');
        spinner.className = 'tree-loading-spinner';
        spinner.textContent = '\u29D7'; // ⧗
        row.appendChild(spinner);
    }
}

// ── Loading / Error states ────────────────────────────────────────────────────

function showLoading(): void {
    if (!treeContainer) return;
    treeContainer.innerHTML = '';
    const state = document.createElement('div');
    state.className = 'loading-state';

    const spinner = document.createElement('div');
    spinner.className = 'spinner';
    state.appendChild(spinner);

    const text = document.createElement('div');
    text.textContent = 'Loading plugin registrations\u2026';
    state.appendChild(text);

    treeContainer.appendChild(state);

    if (statusText) {
        statusText.textContent = 'Loading\u2026';
    }
}

function showError(message: string): void {
    if (!treeContainer) return;
    treeContainer.innerHTML = '';
    const state = document.createElement('div');
    state.className = 'error-state';
    state.textContent = message;
    treeContainer.appendChild(state);

    if (statusText) {
        statusText.textContent = 'Error';
    }
}

// ── Merge helpers ─────────────────────────────────────────────────────────────

/** Find a node by ID anywhere in the tree (recursive). Returns null if not found. */
function findNode(nodes: PluginTreeNode[], id: string): PluginTreeNode | null {
    for (const node of nodes) {
        if (node.id === id) return node;
        if (node.children) {
            const found = findNode(node.children, id);
            if (found) return found;
        }
    }
    return null;
}

/** Find a node by ID in treeData across all root arrays. */
function findNodeInTree(id: string): PluginTreeNode | null {
    if (!treeData) return null;
    return findNode(treeData.assemblies, id)
        ?? findNode(treeData.packages, id)
        ?? findNode(treeData.serviceEndpoints, id)
        ?? findNode(treeData.customApis, id)
        ?? findNode(treeData.dataSources, id);
}

/** Remove a node by ID from any node array (recursive, mutates in place). */
function removeNodeFromArray(nodes: PluginTreeNode[], id: string): boolean {
    for (let i = 0; i < nodes.length; i++) {
        if (nodes[i].id === id) {
            nodes.splice(i, 1);
            return true;
        }
        if (nodes[i].children && removeNodeFromArray(nodes[i].children!, id)) {
            return true;
        }
    }
    return false;
}

// ── Modal infrastructure ──────────────────────────────────────────────────────

interface ModalHandle {
    overlay: HTMLElement;
    content: HTMLElement;
    close: () => void;
}

function createModal(title: string): ModalHandle {
    const overlay = document.createElement('div');
    overlay.className = 'modal-overlay';

    const modal = document.createElement('div');
    modal.className = 'modal-dialog';

    const header = document.createElement('div');
    header.className = 'modal-header';

    const titleEl = document.createElement('h3');
    titleEl.textContent = title;
    header.appendChild(titleEl);

    const closeBtn = document.createElement('button');
    closeBtn.className = 'modal-close';
    closeBtn.textContent = '\u00D7'; // ×
    closeBtn.addEventListener('click', () => close());
    header.appendChild(closeBtn);

    const content = document.createElement('div');
    content.className = 'modal-content';

    modal.appendChild(header);
    modal.appendChild(content);
    overlay.appendChild(modal);
    document.body.appendChild(overlay);

    function close(): void { overlay.remove(); }
    overlay.addEventListener('click', (e) => { if (e.target === overlay) close(); });

    return { overlay, content, close };
}

function createField(label: string, input: HTMLElement): HTMLElement {
    const field = document.createElement('div');
    field.className = 'form-field';
    const labelEl = document.createElement('label');
    labelEl.textContent = label;
    field.appendChild(labelEl);
    field.appendChild(input);
    return field;
}

function createTextInput(value = ''): HTMLInputElement {
    const input = document.createElement('input');
    input.type = 'text';
    input.className = 'form-input';
    input.value = value;
    return input;
}

function createPasswordInput(value = ''): HTMLInputElement {
    const input = document.createElement('input');
    input.type = 'password';
    input.className = 'form-input';
    input.value = value;
    return input;
}

function createNumberInput(value = 1): HTMLInputElement {
    const input = document.createElement('input');
    input.type = 'number';
    input.className = 'form-input';
    input.value = String(value);
    return input;
}

function createTextarea(value = ''): HTMLTextAreaElement {
    const ta = document.createElement('textarea');
    ta.className = 'form-textarea';
    ta.value = value;
    return ta;
}

function createSelect(options: { value: string; label: string }[], selected = ''): HTMLSelectElement {
    const sel = document.createElement('select');
    sel.className = 'form-select';
    for (const opt of options) {
        const el = document.createElement('option');
        el.value = opt.value;
        el.textContent = opt.label;
        if (opt.value === selected) el.selected = true;
        sel.appendChild(el);
    }
    return sel;
}

function createCheckbox(checked = false): HTMLInputElement {
    const cb = document.createElement('input');
    cb.type = 'checkbox';
    cb.className = 'form-checkbox';
    cb.checked = checked;
    return cb;
}

function createFormActions(submitLabel: string, onSubmit: () => void, onCancel: () => void): HTMLElement {
    const actions = document.createElement('div');
    actions.className = 'form-actions';

    const submitBtn = document.createElement('button');
    submitBtn.className = 'form-btn form-btn-primary';
    submitBtn.textContent = submitLabel;
    submitBtn.addEventListener('click', onSubmit);

    const cancelBtn = document.createElement('button');
    cancelBtn.className = 'form-btn form-btn-secondary';
    cancelBtn.textContent = 'Cancel';
    cancelBtn.addEventListener('click', onCancel);

    actions.appendChild(submitBtn);
    actions.appendChild(cancelBtn);
    return actions;
}

// ── Step registration form ────────────────────────────────────────────────────

/** Messages that support filtering attributes. */
const MESSAGES_SUPPORTING_FILTERING = new Set([
    'create', 'update', 'delete', 'retrieve', 'retrievemultiple',
    'associate', 'disassociate', 'merge', 'setstate', 'setstatedynamicentity',
]);

function showStepForm(parentTypeId?: string, existingStep?: Record<string, unknown>): void {
    const isUpdate = !!existingStep;
    const modal = createModal(isUpdate ? 'Update Step' : 'Register Step');
    const c = modal.content;

    // Message
    const messageInput = createTextInput(String(existingStep?.['message'] ?? ''));
    c.appendChild(createField('Message *', messageInput));

    // Primary Entity
    const primaryEntityInput = createTextInput(String(existingStep?.['primaryEntity'] ?? ''));
    c.appendChild(createField('Primary Entity *', primaryEntityInput));

    // Secondary Entity
    const secondaryEntityInput = createTextInput(String(existingStep?.['secondaryEntity'] ?? ''));
    c.appendChild(createField('Secondary Entity', secondaryEntityInput));

    // Step Name
    const stepNameInput = createTextInput(String(existingStep?.['name'] ?? ''));
    c.appendChild(createField('Step Name (auto-generated if empty)', stepNameInput));

    // Description
    const descriptionInput = createTextInput(String(existingStep?.['description'] ?? ''));
    c.appendChild(createField('Description', descriptionInput));

    // Stage
    const stageSelect = createSelect([
        { value: 'PreValidation', label: 'PreValidation (10)' },
        { value: 'PreOperation', label: 'PreOperation (20)' },
        { value: 'PostOperation', label: 'PostOperation (40)' },
    ], String(existingStep?.['stage'] ?? 'PreValidation'));
    c.appendChild(createField('Stage *', stageSelect));

    // Mode
    const modeSelect = createSelect([
        { value: 'Synchronous', label: 'Synchronous' },
        { value: 'Asynchronous', label: 'Asynchronous' },
    ], String(existingStep?.['mode'] ?? 'Synchronous'));
    c.appendChild(createField('Mode *', modeSelect));

    // Execution Order
    const executionOrderInput = createNumberInput(Number(existingStep?.['executionOrder'] ?? 1));
    c.appendChild(createField('Execution Order *', executionOrderInput));

    // Filtering Attributes
    const filteringAttrsInput = createTextarea(String(existingStep?.['filteringAttributes'] ?? ''));
    const filteringAttrsField = createField('Filtering Attributes (comma-separated)', filteringAttrsInput);
    c.appendChild(filteringAttrsField);

    // Deployment
    const deploymentSelect = createSelect([
        { value: 'ServerOnly', label: 'Server Only' },
        { value: 'Offline', label: 'Offline' },
        { value: 'Both', label: 'Both' },
    ], String(existingStep?.['deployment'] ?? 'ServerOnly'));
    c.appendChild(createField('Deployment', deploymentSelect));

    // Unsecure Configuration
    const unsecureConfigInput = createTextarea(String(existingStep?.['unsecureConfiguration'] ?? ''));
    c.appendChild(createField('Unsecure Configuration', unsecureConfigInput));

    // Secure Configuration (shows indicator if already set on existing step)
    const secureConfigInput = createTextarea('');
    const secureConfigLabel = existingStep?.['hasSecureConfig']
        ? 'Secure Configuration (currently set — leave blank to keep)'
        : 'Secure Configuration';
    c.appendChild(createField(secureConfigLabel, secureConfigInput));

    // Async Auto Delete
    const asyncAutoDeleteCheck = createCheckbox(Boolean(existingStep?.['asyncAutoDelete'] ?? false));
    c.appendChild(createField('Async Auto Delete', asyncAutoDeleteCheck));

    // Can Be Bypassed
    const canBeBypassedCheck = createCheckbox(Boolean(existingStep?.['canBeBypassed'] ?? true));
    c.appendChild(createField('Can Be Bypassed', canBeBypassedCheck));

    // Can Use Read-Only Connection
    const readOnlyConnectionCheck = createCheckbox(Boolean(existingStep?.['canUseReadOnlyConnection'] ?? false));
    c.appendChild(createField('Can Use Read-Only Connection', readOnlyConnectionCheck));

    // ── Conditional logic ────────────────────────────────────────────────────

    function applyStageConstraints(): void {
        const stage = stageSelect.value;
        const asyncOption = modeSelect.querySelector<HTMLOptionElement>('option[value="Asynchronous"]');
        if (stage !== 'PostOperation') {
            modeSelect.value = 'Synchronous';
            if (asyncOption) asyncOption.disabled = true;
        } else {
            if (asyncOption) asyncOption.disabled = false;
        }
        applyModeConstraints();
    }

    function applyModeConstraints(): void {
        const isAsync = modeSelect.value === 'Asynchronous';
        asyncAutoDeleteCheck.disabled = !isAsync;
        if (!isAsync) asyncAutoDeleteCheck.checked = false;
    }

    function applyMessageConstraints(): void {
        const msg = messageInput.value.trim().toLowerCase();
        const supportsFiltering = MESSAGES_SUPPORTING_FILTERING.has(msg);
        filteringAttrsInput.disabled = !supportsFiltering;
        if (!supportsFiltering) filteringAttrsInput.value = '';
    }

    stageSelect.addEventListener('change', applyStageConstraints);
    modeSelect.addEventListener('change', applyModeConstraints);
    messageInput.addEventListener('input', applyMessageConstraints);

    // Apply initial constraints
    applyStageConstraints();
    applyMessageConstraints();

    // ── Actions ──────────────────────────────────────────────────────────────

    function onSubmit(): void {
        const messageName = messageInput.value.trim();
        const primaryEntity = primaryEntityInput.value.trim();

        if (!messageName) {
            messageInput.focus();
            return;
        }
        if (!primaryEntity) {
            primaryEntityInput.focus();
            return;
        }

        const fields: Record<string, unknown> = {
            message: messageName,
            primaryEntity,
            secondaryEntity: secondaryEntityInput.value.trim(),
            name: stepNameInput.value.trim(),
            description: descriptionInput.value.trim(),
            stage: stageSelect.value,
            mode: modeSelect.value,
            executionOrder: Number(executionOrderInput.value),
            filteringAttributes: filteringAttrsInput.value.trim(),
            deployment: deploymentSelect.value,
            unsecureConfiguration: unsecureConfigInput.value.trim(),
            asyncAutoDelete: asyncAutoDeleteCheck.checked,
            canBeBypassed: canBeBypassedCheck.checked,
            canUseReadOnlyConnection: readOnlyConnectionCheck.checked,
        };

        // Only include secure config if something was typed
        const secureConfigValue = secureConfigInput.value.trim();
        if (secureConfigValue) {
            fields['secureConfiguration'] = secureConfigValue;
        }

        if (isUpdate && existingStep) {
            vscode.postMessage({
                command: 'updateEntity',
                entityType: 'step',
                id: String(existingStep['id']),
                fields,
            });
        } else {
            vscode.postMessage({
                command: 'registerEntity',
                entityType: 'step',
                parentId: parentTypeId,
                fields,
            });
        }
        modal.close();
    }

    c.appendChild(createFormActions(isUpdate ? 'Update' : 'Register', onSubmit, modal.close));
}

// ── Image registration form ───────────────────────────────────────────────────

function showImageForm(parentStepId: string, existingImage?: Record<string, unknown>): void {
    const isUpdate = !!existingImage;
    const modal = createModal(isUpdate ? 'Update Image' : 'Register Image');
    const c = modal.content;

    // ImageType checkboxes (Pre / Post)
    const imageTypeField = document.createElement('div');
    imageTypeField.className = 'form-field';
    const imageTypeLabel = document.createElement('label');
    imageTypeLabel.textContent = 'Image Type * (at least one required)';
    imageTypeField.appendChild(imageTypeLabel);

    const imageTypeRow = document.createElement('div');
    imageTypeRow.className = 'form-checkbox-row';

    const preCheck = createCheckbox(Boolean(existingImage?.['imageTypePre'] ?? false));
    const preLabel = document.createElement('label');
    preLabel.className = 'form-checkbox-label';
    preLabel.textContent = 'Pre';
    preLabel.prepend(preCheck);

    const postCheck = createCheckbox(Boolean(existingImage?.['imageTypePost'] ?? false));
    const postLabel = document.createElement('label');
    postLabel.className = 'form-checkbox-label';
    postLabel.textContent = 'Post';
    postLabel.prepend(postCheck);

    imageTypeRow.appendChild(preLabel);
    imageTypeRow.appendChild(postLabel);
    imageTypeField.appendChild(imageTypeRow);
    c.appendChild(imageTypeField);

    // Name
    const nameInput = createTextInput(String(existingImage?.['name'] ?? ''));
    c.appendChild(createField('Name *', nameInput));

    // Entity Alias (defaults to Name)
    const entityAliasInput = createTextInput(String(existingImage?.['entityAlias'] ?? ''));
    const entityAliasField = createField('Entity Alias (defaults to Name)', entityAliasInput);
    c.appendChild(entityAliasField);

    // Keep entity alias in sync with name if user hasn't changed it
    let aliasModifiedByUser = !!existingImage?.['entityAlias'];
    nameInput.addEventListener('input', () => {
        if (!aliasModifiedByUser) {
            entityAliasInput.value = nameInput.value;
        }
    });
    entityAliasInput.addEventListener('input', () => {
        aliasModifiedByUser = true;
    });

    // Attributes
    const attributesInput = createTextarea(String(existingImage?.['attributes'] ?? ''));
    c.appendChild(createField('Attributes * (comma-separated logical names)', attributesInput));

    // Description
    const descriptionInput = createTextInput(String(existingImage?.['description'] ?? ''));
    c.appendChild(createField('Description', descriptionInput));

    // Message Property Name
    const messagePropertyInput = createTextInput(String(existingImage?.['messagePropertyName'] ?? ''));
    c.appendChild(createField('Message Property Name (override auto-inferred)', messagePropertyInput));

    // ── Actions ──────────────────────────────────────────────────────────────

    function onSubmit(): void {
        const name = nameInput.value.trim();
        const attributes = attributesInput.value.trim();

        if (!preCheck.checked && !postCheck.checked) {
            preCheck.focus();
            return;
        }
        if (!name) {
            nameInput.focus();
            return;
        }
        if (!attributes) {
            attributesInput.focus();
            return;
        }

        const fields: Record<string, unknown> = {
            imageTypePre: preCheck.checked,
            imageTypePost: postCheck.checked,
            name,
            entityAlias: entityAliasInput.value.trim() || name,
            attributes,
            description: descriptionInput.value.trim(),
            messagePropertyName: messagePropertyInput.value.trim(),
        };

        if (isUpdate && existingImage) {
            vscode.postMessage({
                command: 'updateEntity',
                entityType: 'image',
                id: String(existingImage['id']),
                fields,
            });
        } else {
            vscode.postMessage({
                command: 'registerEntity',
                entityType: 'image',
                parentId: parentStepId,
                fields,
            });
        }
        modal.close();
    }

    c.appendChild(createFormActions(isUpdate ? 'Update' : 'Register', onSubmit, modal.close));
}

// ── Assembly / Package binary update form ─────────────────────────────────────

function showBinaryUpdateForm(type: 'assembly' | 'package', id: string): void {
    const modal = createModal(`Update ${type === 'assembly' ? 'Assembly' : 'Package'} Binary`);
    const c = modal.content;

    const instruction = document.createElement('p');
    instruction.className = 'form-instruction';
    instruction.textContent = `Select the updated ${type === 'assembly' ? '.dll' : '.nupkg'} file to upload.`;
    c.appendChild(instruction);

    const fileInput = document.createElement('input');
    fileInput.type = 'file';
    fileInput.className = 'form-file-input';
    fileInput.accept = type === 'assembly' ? '.dll' : '.nupkg';
    c.appendChild(createField('Select file', fileInput));

    const statusEl = document.createElement('p');
    statusEl.className = 'form-status';
    c.appendChild(statusEl);

    function onSubmit(): void {
        const file = fileInput.files?.[0];
        if (!file) {
            statusEl.textContent = 'Please select a file.';
            return;
        }
        statusEl.textContent = 'Reading file\u2026';

        const reader = new FileReader();
        reader.onload = () => {
            const dataUrl = reader.result as string;
            // dataUrl is "data:<mime>;base64,<content>" — extract the base64 part
            const base64 = dataUrl.split(',')[1] ?? '';
            vscode.postMessage({
                command: 'registerEntity',
                entityType: type,
                parentId: id,
                fields: {
                    id,
                    fileName: file.name,
                    content: base64,
                },
            });
            modal.close();
        };
        reader.onerror = () => {
            statusEl.textContent = 'Failed to read file.';
        };
        reader.readAsDataURL(file);
    }

    c.appendChild(createFormActions('Upload', onSubmit, modal.close));
}

// ── Webhook registration form ─────────────────────────────────────────────────

function showWebhookForm(existing?: Record<string, unknown>): void {
    const isUpdate = !!existing;
    const modal = createModal(isUpdate ? 'Update Webhook' : 'Register Webhook');
    const c = modal.content;

    // Name
    const nameInput = createTextInput(String(existing?.['name'] ?? ''));
    c.appendChild(createField('Name *', nameInput));

    // URL
    const urlInput = createTextInput(String(existing?.['url'] ?? ''));
    c.appendChild(createField('URL *', urlInput));

    // Auth Type
    const authTypeSelect = createSelect([
        { value: 'HttpHeader', label: 'HTTP Header' },
        { value: 'WebhookKey', label: 'Webhook Key' },
        { value: 'HttpQueryString', label: 'HTTP Query String' },
    ], String(existing?.['authType'] ?? 'HttpHeader'));
    c.appendChild(createField('Auth Type', authTypeSelect));

    // ── Conditional auth fields ──────────────────────────────────────────────

    // WebhookKey → single Value field
    const webhookKeyField = createField('Value', (() => {
        const inp = document.createElement('input');
        inp.type = 'password';
        inp.className = 'form-input';
        inp.value = '';
        return inp;
    })());
    const webhookKeyInput = webhookKeyField.querySelector('input') as HTMLInputElement;
    c.appendChild(webhookKeyField);

    // HttpHeader / HttpQueryString → key-value pair list
    const kvContainer = document.createElement('div');
    kvContainer.className = 'form-field';
    const kvLabel = document.createElement('label');
    kvLabel.textContent = 'Key-Value Pairs';
    kvContainer.appendChild(kvLabel);

    const kvList = document.createElement('div');
    kvList.className = 'kv-list';
    kvContainer.appendChild(kvList);

    const addKvBtn = document.createElement('button');
    addKvBtn.className = 'form-btn form-btn-secondary form-btn-small';
    addKvBtn.textContent = '+ Add Pair';
    kvContainer.appendChild(addKvBtn);
    c.appendChild(kvContainer);

    // Populate existing kv pairs if any
    const existingPairs = Array.isArray(existing?.['keyValuePairs'])
        ? (existing['keyValuePairs'] as { key: string; value: string }[])
        : [];
    for (const pair of existingPairs) {
        addKvRow(kvList, pair.key, pair.value);
    }

    addKvBtn.addEventListener('click', () => addKvRow(kvList, '', ''));

    function updateAuthVisibility(): void {
        const authType = authTypeSelect.value;
        webhookKeyField.style.display = authType === 'WebhookKey' ? '' : 'none';
        kvContainer.style.display = authType !== 'WebhookKey' ? '' : 'none';
    }

    authTypeSelect.addEventListener('change', updateAuthVisibility);
    updateAuthVisibility();

    // ── Actions ──────────────────────────────────────────────────────────────

    function onSubmit(): void {
        const name = nameInput.value.trim();
        const url = urlInput.value.trim();
        if (!name) { nameInput.focus(); return; }
        if (!url) { urlInput.focus(); return; }

        const authType = authTypeSelect.value;
        const fields: Record<string, unknown> = { name, url, authType };

        if (authType === 'WebhookKey') {
            fields['value'] = webhookKeyInput.value;
        } else {
            const rows = kvList.querySelectorAll<HTMLElement>('.kv-row');
            const pairs: { key: string; value: string }[] = [];
            rows.forEach(row => {
                const inputs = row.querySelectorAll<HTMLInputElement>('input');
                const key = inputs[0]?.value.trim() ?? '';
                const val = inputs[1]?.value.trim() ?? '';
                if (key) pairs.push({ key, value: val });
            });
            fields['keyValuePairs'] = pairs;
        }

        if (isUpdate && existing) {
            vscode.postMessage({ command: 'updateEntity', entityType: 'webhook', id: String(existing['id']), fields });
        } else {
            vscode.postMessage({ command: 'registerEntity', entityType: 'webhook', fields });
        }
        modal.close();
    }

    c.appendChild(createFormActions(isUpdate ? 'Update' : 'Register', onSubmit, modal.close));
}

function addKvRow(container: HTMLElement, key: string, value: string): void {
    const row = document.createElement('div');
    row.className = 'kv-row';

    const keyInput = createTextInput(key);
    keyInput.placeholder = 'Key';
    row.appendChild(keyInput);

    const valInput = createTextInput(value);
    valInput.placeholder = 'Value';
    row.appendChild(valInput);

    const removeBtn = document.createElement('button');
    removeBtn.className = 'form-btn form-btn-secondary form-btn-small';
    removeBtn.textContent = '\u00D7';
    removeBtn.addEventListener('click', () => row.remove());
    row.appendChild(removeBtn);

    container.appendChild(row);
}

// ── Service Bus endpoint registration form ────────────────────────────────────

function showServiceBusForm(contract: string, existing?: Record<string, unknown>): void {
    const isUpdate = !!existing;
    const contractLabel = contract === 'queue' ? 'Queue'
        : contract === 'topic' ? 'Topic'
        : contract === 'eventhub' ? 'EventHub'
        : 'Queue';
    const isEventHub = contract === 'eventhub';

    const modal = createModal(isUpdate ? `Update ${contractLabel} Endpoint` : `Register ${contractLabel} Endpoint`);
    const c = modal.content;

    // Name
    const nameInput = createTextInput(String(existing?.['name'] ?? ''));
    c.appendChild(createField('Name *', nameInput));

    // Namespace Address
    const namespaceInput = createTextInput(String(existing?.['namespaceAddress'] ?? ''));
    namespaceInput.placeholder = 'sb://...';
    c.appendChild(createField('Namespace Address *', namespaceInput));

    // Path (label varies by contract)
    const pathInput = createTextInput(String(existing?.['path'] ?? ''));
    c.appendChild(createField(`${contractLabel} Name *`, pathInput));

    // Auth Type
    const authTypeSelect = createSelect([
        { value: 'SASKey', label: 'SAS Key' },
        { value: 'SASToken', label: 'SAS Token' },
    ], String(existing?.['authType'] ?? 'SASKey'));
    c.appendChild(createField('Auth Type', authTypeSelect));

    // SASKey fields
    const sasKeyNameInput = createTextInput(String(existing?.['sasKeyName'] ?? ''));
    const sasKeyNameField = createField('SAS Key Name', sasKeyNameInput);
    c.appendChild(sasKeyNameField);

    const sasKeyInput = createPasswordInput(String(existing?.['sasKey'] ?? ''));
    const sasKeyField = createField('SAS Key', sasKeyInput);
    c.appendChild(sasKeyField);

    // SASToken field
    const sasTokenInput = createPasswordInput(String(existing?.['sasToken'] ?? ''));
    const sasTokenField = createField('SAS Token', sasTokenInput);
    c.appendChild(sasTokenField);

    // Message Format (EventHub excludes .NETBinary)
    const messageFormatOptions = isEventHub
        ? [{ value: 'XML', label: 'XML' }, { value: 'JSON', label: 'JSON' }]
        : [{ value: '.NETBinary', label: '.NET Binary' }, { value: 'XML', label: 'XML' }, { value: 'JSON', label: 'JSON' }];
    const messageFormatSelect = createSelect(messageFormatOptions, String(existing?.['messageFormat'] ?? (isEventHub ? 'JSON' : '.NETBinary')));
    c.appendChild(createField('Message Format', messageFormatSelect));

    // User Claim
    const userClaimSelect = createSelect([
        { value: 'None', label: 'None' },
        { value: 'UserId', label: 'User ID' },
    ], String(existing?.['userClaim'] ?? 'None'));
    c.appendChild(createField('User Claim', userClaimSelect));

    // Description
    const descriptionInput = createTextInput(String(existing?.['description'] ?? ''));
    c.appendChild(createField('Description', descriptionInput));

    function updateAuthVisibility(): void {
        const isSASKey = authTypeSelect.value === 'SASKey';
        sasKeyNameField.style.display = isSASKey ? '' : 'none';
        sasKeyField.style.display = isSASKey ? '' : 'none';
        sasTokenField.style.display = isSASKey ? 'none' : '';
    }

    authTypeSelect.addEventListener('change', updateAuthVisibility);
    updateAuthVisibility();

    // ── Actions ──────────────────────────────────────────────────────────────

    function onSubmit(): void {
        const name = nameInput.value.trim();
        const namespaceAddress = namespaceInput.value.trim();
        const path = pathInput.value.trim();
        if (!name) { nameInput.focus(); return; }
        if (!namespaceAddress) { namespaceInput.focus(); return; }
        if (!path) { pathInput.focus(); return; }

        const authType = authTypeSelect.value;
        const fields: Record<string, unknown> = {
            name, namespaceAddress, path, authType,
            messageFormat: messageFormatSelect.value,
            userClaim: userClaimSelect.value,
            description: descriptionInput.value.trim(),
            contract,
        };

        if (authType === 'SASKey') {
            fields['sasKeyName'] = sasKeyNameInput.value.trim();
            fields['sasKey'] = sasKeyInput.value.trim();
        } else {
            fields['sasToken'] = sasTokenInput.value.trim();
        }

        const entityType = contract === 'queue' ? 'serviceendpoint_queue'
            : contract === 'topic' ? 'serviceendpoint_topic'
            : 'serviceendpoint_eventhub';

        if (isUpdate && existing) {
            vscode.postMessage({ command: 'updateEntity', entityType, id: String(existing['id']), fields });
        } else {
            vscode.postMessage({ command: 'registerEntity', entityType, fields });
        }
        modal.close();
    }

    c.appendChild(createFormActions(isUpdate ? 'Update' : 'Register', onSubmit, modal.close));
}

// ── Service endpoint contract picker ─────────────────────────────────────────

function showServiceEndpointContractPicker(): void {
    const modal = createModal('Register Service Endpoint');
    const c = modal.content;

    const instruction = document.createElement('p');
    instruction.className = 'form-instruction';
    instruction.textContent = 'Select the Service Bus contract type:';
    c.appendChild(instruction);

    const contracts: { label: string; value: string }[] = [
        { label: 'Queue', value: 'queue' },
        { label: 'Topic', value: 'topic' },
        { label: 'Event Hub', value: 'eventhub' },
    ];

    for (const contract of contracts) {
        const btn = document.createElement('button');
        btn.className = 'form-btn form-btn-secondary';
        btn.textContent = contract.label;
        btn.addEventListener('click', () => {
            modal.close();
            showServiceBusForm(contract.value);
        });
        c.appendChild(btn);
    }

    const cancelActions = document.createElement('div');
    cancelActions.className = 'form-actions';
    const cancelBtn = document.createElement('button');
    cancelBtn.className = 'form-btn form-btn-secondary';
    cancelBtn.textContent = 'Cancel';
    cancelBtn.addEventListener('click', modal.close);
    cancelActions.appendChild(cancelBtn);
    c.appendChild(cancelActions);
}

// ── Custom API registration form ──────────────────────────────────────────────

const CUSTOM_API_PARAM_TYPES = [
    'Boolean', 'DateTime', 'Decimal', 'Entity', 'EntityCollection',
    'EntityReference', 'Float', 'Integer', 'Money', 'Picklist',
    'String', 'StringArray', 'Guid',
];

interface CustomApiParam {
    name: string;
    type: string;
    direction: 'Input' | 'Output';
    isOptional: boolean;
    logicalEntityName: string;
}

function showCustomApiForm(existing?: Record<string, unknown>): void {
    const isUpdate = !!existing;
    const modal = createModal(isUpdate ? 'Update Custom API' : 'Register Custom API');
    const c = modal.content;

    // Unique Name
    const uniqueNameInput = createTextInput(String(existing?.['uniqueName'] ?? ''));
    c.appendChild(createField('Unique Name *', uniqueNameInput));

    // Display Name
    const displayNameInput = createTextInput(String(existing?.['displayName'] ?? ''));
    c.appendChild(createField('Display Name *', displayNameInput));

    // Name (auto-filled from DisplayName)
    const nameInput = createTextInput(String(existing?.['name'] ?? ''));
    c.appendChild(createField('Name (auto-filled)', nameInput));

    let nameModifiedByUser = !!existing?.['name'];
    displayNameInput.addEventListener('input', () => {
        if (!nameModifiedByUser) {
            nameInput.value = displayNameInput.value;
        }
    });
    nameInput.addEventListener('input', () => { nameModifiedByUser = true; });

    // Description
    const descriptionInput = createTextInput(String(existing?.['description'] ?? ''));
    c.appendChild(createField('Description', descriptionInput));

    // Assembly (for new only)
    const assemblyInput = createTextInput(String(existing?.['assembly'] ?? ''));
    c.appendChild(createField('Assembly *', assemblyInput));

    // Plugin Type Name
    const pluginTypeNameInput = createTextInput(String(existing?.['pluginTypeName'] ?? ''));
    c.appendChild(createField('Plugin Type Name *', pluginTypeNameInput));

    // Binding Type
    const bindingTypeSelect = createSelect([
        { value: 'Global', label: 'Global' },
        { value: 'Entity', label: 'Entity' },
        { value: 'EntityCollection', label: 'Entity Collection' },
    ], String(existing?.['bindingType'] ?? 'Global'));
    c.appendChild(createField('Binding Type', bindingTypeSelect));

    // Bound Entity (conditional on Entity binding)
    const boundEntityInput = createTextInput(String(existing?.['boundEntity'] ?? ''));
    const boundEntityField = createField('Bound Entity', boundEntityInput);
    c.appendChild(boundEntityField);

    function updateBoundEntityVisibility(): void {
        boundEntityField.style.display = bindingTypeSelect.value === 'Entity' ? '' : 'none';
        boundEntityInput.disabled = bindingTypeSelect.value !== 'Entity';
    }

    bindingTypeSelect.addEventListener('change', updateBoundEntityVisibility);
    updateBoundEntityVisibility();

    // Is Function
    const isFunctionCheck = createCheckbox(Boolean(existing?.['isFunction'] ?? false));
    c.appendChild(createField('Is Function', isFunctionCheck));

    // Is Private
    const isPrivateCheck = createCheckbox(Boolean(existing?.['isPrivate'] ?? false));
    c.appendChild(createField('Is Private', isPrivateCheck));

    // Execute Privilege Name
    const executePrivilegeInput = createTextInput(String(existing?.['executePrivilegeName'] ?? ''));
    c.appendChild(createField('Execute Privilege Name', executePrivilegeInput));

    // Allowed Processing Step Type
    const allowedStepTypeSelect = createSelect([
        { value: 'None', label: 'None' },
        { value: 'AsyncOnly', label: 'Async Only' },
        { value: 'SyncAndAsync', label: 'Sync and Async' },
    ], String(existing?.['allowedProcessingStepType'] ?? 'SyncAndAsync'));
    c.appendChild(createField('Allowed Processing Step Type', allowedStepTypeSelect));

    // ── Parameters section ───────────────────────────────────────────────────

    const paramsSection = document.createElement('div');
    paramsSection.className = 'form-section';

    const paramsSectionHeader = document.createElement('div');
    paramsSectionHeader.className = 'form-section-header';

    const paramsSectionTitle = document.createElement('span');
    paramsSectionTitle.textContent = 'Parameters';
    paramsSectionHeader.appendChild(paramsSectionTitle);

    const addParamBtn = document.createElement('button');
    addParamBtn.className = 'form-btn form-btn-secondary form-btn-small';
    addParamBtn.textContent = '+ Add Parameter';
    paramsSectionHeader.appendChild(addParamBtn);

    paramsSection.appendChild(paramsSectionHeader);

    const paramsList = document.createElement('div');
    paramsList.className = 'params-list';
    paramsSection.appendChild(paramsList);
    c.appendChild(paramsSection);

    const params: CustomApiParam[] = Array.isArray(existing?.['parameters'])
        ? (existing['parameters'] as CustomApiParam[])
        : [];
    for (const p of params) {
        addParamRow(paramsList, p);
    }

    addParamBtn.addEventListener('click', () => {
        addParamRow(paramsList, { name: '', type: 'String', direction: 'Input', isOptional: false, logicalEntityName: '' });
    });

    // ── Actions ──────────────────────────────────────────────────────────────

    function onSubmit(): void {
        const uniqueName = uniqueNameInput.value.trim();
        const displayName = displayNameInput.value.trim();
        const assembly = assemblyInput.value.trim();
        const pluginTypeName = pluginTypeNameInput.value.trim();

        if (!uniqueName) { uniqueNameInput.focus(); return; }
        if (!displayName) { displayNameInput.focus(); return; }
        if (!assembly) { assemblyInput.focus(); return; }
        if (!pluginTypeName) { pluginTypeNameInput.focus(); return; }

        const collectedParams: CustomApiParam[] = [];
        const paramRows = paramsList.querySelectorAll<HTMLElement>('.param-row');
        paramRows.forEach(row => {
            const inputs = row.querySelectorAll<HTMLInputElement>('input');
            const selects = row.querySelectorAll<HTMLSelectElement>('select');
            const nameVal = inputs[0]?.value.trim() ?? '';
            if (!nameVal) return;
            const typeVal = selects[0]?.value ?? 'String';
            const dirVal = (selects[1]?.value ?? 'Input') as 'Input' | 'Output';
            const isOptionalVal = inputs[1]?.checked ?? false;
            const logicalEntityVal = inputs[2]?.value.trim() ?? '';
            collectedParams.push({ name: nameVal, type: typeVal, direction: dirVal, isOptional: isOptionalVal, logicalEntityName: logicalEntityVal });
        });

        const fields: Record<string, unknown> = {
            uniqueName,
            displayName,
            name: nameInput.value.trim() || displayName,
            description: descriptionInput.value.trim(),
            assembly,
            pluginTypeName,
            bindingType: bindingTypeSelect.value,
            boundEntity: bindingTypeSelect.value === 'Entity' ? boundEntityInput.value.trim() : '',
            isFunction: isFunctionCheck.checked,
            isPrivate: isPrivateCheck.checked,
            executePrivilegeName: executePrivilegeInput.value.trim(),
            allowedProcessingStepType: allowedStepTypeSelect.value,
            parameters: collectedParams,
        };

        if (isUpdate && existing) {
            vscode.postMessage({ command: 'updateEntity', entityType: 'customapi', id: String(existing['id']), fields });
        } else {
            vscode.postMessage({ command: 'registerEntity', entityType: 'customapi', fields });
        }
        modal.close();
    }

    c.appendChild(createFormActions(isUpdate ? 'Update' : 'Register', onSubmit, modal.close));
}

function addParamRow(container: HTMLElement, param: CustomApiParam): void {
    const ENTITY_TYPES = new Set(['Entity', 'EntityCollection', 'EntityReference']);

    const row = document.createElement('div');
    row.className = 'param-row';

    // Name
    const nameInput = createTextInput(param.name);
    nameInput.placeholder = 'Name';
    row.appendChild(nameInput);

    // Type
    const typeSelect = createSelect(
        CUSTOM_API_PARAM_TYPES.map(t => ({ value: t, label: t })),
        param.type,
    );
    row.appendChild(typeSelect);

    // Direction
    const dirSelect = createSelect([
        { value: 'Input', label: 'Input' },
        { value: 'Output', label: 'Output' },
    ], param.direction);
    row.appendChild(dirSelect);

    // IsOptional checkbox (only relevant for Input)
    const isOptionalCheck = createCheckbox(param.isOptional);
    const isOptionalLabel = document.createElement('label');
    isOptionalLabel.className = 'form-checkbox-label form-checkbox-label-inline';
    isOptionalLabel.textContent = 'Optional';
    isOptionalLabel.prepend(isOptionalCheck);
    row.appendChild(isOptionalLabel);

    // Logical Entity Name (for Entity/EntityRef/EntityCollection types)
    const logicalEntityInput = createTextInput(param.logicalEntityName);
    logicalEntityInput.placeholder = 'Entity Logical Name';
    row.appendChild(logicalEntityInput);

    function updateParamVisibility(): void {
        isOptionalLabel.style.display = dirSelect.value === 'Input' ? '' : 'none';
        isOptionalCheck.disabled = dirSelect.value !== 'Input';
        const showEntityName = ENTITY_TYPES.has(typeSelect.value);
        logicalEntityInput.style.display = showEntityName ? '' : 'none';
        logicalEntityInput.disabled = !showEntityName;
    }

    typeSelect.addEventListener('change', updateParamVisibility);
    dirSelect.addEventListener('change', updateParamVisibility);
    updateParamVisibility();

    // Remove button
    const removeBtn = document.createElement('button');
    removeBtn.className = 'form-btn form-btn-secondary form-btn-small';
    removeBtn.textContent = '\u00D7';
    removeBtn.addEventListener('click', () => row.remove());
    row.appendChild(removeBtn);

    container.appendChild(row);
}

// ── Data Provider registration form ──────────────────────────────────────────

function showDataProviderForm(existing?: Record<string, unknown>): void {
    const isUpdate = !!existing;
    const modal = createModal(isUpdate ? 'Update Data Provider' : 'Register Data Provider');
    const c = modal.content;

    // Name
    const nameInput = createTextInput(String(existing?.['name'] ?? ''));
    c.appendChild(createField('Name *', nameInput));

    // Data Source
    const dataSourceInput = createTextInput(String(existing?.['dataSource'] ?? ''));
    c.appendChild(createField('Data Source *', dataSourceInput));

    // Plugin fields
    const retrieveInput = createTextInput(String(existing?.['retrievePlugin'] ?? ''));
    c.appendChild(createField('Retrieve Plugin', retrieveInput));

    const retrieveMultipleInput = createTextInput(String(existing?.['retrieveMultiplePlugin'] ?? ''));
    c.appendChild(createField('Retrieve Multiple Plugin', retrieveMultipleInput));

    const createInput = createTextInput(String(existing?.['createPlugin'] ?? ''));
    c.appendChild(createField('Create Plugin', createInput));

    const updateInput = createTextInput(String(existing?.['updatePlugin'] ?? ''));
    c.appendChild(createField('Update Plugin', updateInput));

    const deleteInput = createTextInput(String(existing?.['deletePlugin'] ?? ''));
    c.appendChild(createField('Delete Plugin', deleteInput));

    // ── Actions ──────────────────────────────────────────────────────────────

    function onSubmit(): void {
        const name = nameInput.value.trim();
        const dataSource = dataSourceInput.value.trim();
        if (!name) { nameInput.focus(); return; }
        if (!dataSource) { dataSourceInput.focus(); return; }

        const fields: Record<string, unknown> = {
            name,
            dataSource,
            retrievePlugin: retrieveInput.value.trim(),
            retrieveMultiplePlugin: retrieveMultipleInput.value.trim(),
            createPlugin: createInput.value.trim(),
            updatePlugin: updateInput.value.trim(),
            deletePlugin: deleteInput.value.trim(),
        };

        if (isUpdate && existing) {
            vscode.postMessage({ command: 'updateEntity', entityType: 'dataprovider', id: String(existing['id']), fields });
        } else {
            vscode.postMessage({ command: 'registerEntity', entityType: 'dataprovider', fields });
        }
        modal.close();
    }

    c.appendChild(createFormActions(isUpdate ? 'Update' : 'Register', onSubmit, modal.close));
}

// ── Register toolbar dropdown ─────────────────────────────────────────────────

function buildRegisterDropdown(): void {
    const toolbar = document.querySelector('.toolbar');
    if (!toolbar) return;

    const wrapper = document.createElement('div');
    wrapper.className = 'register-dropdown register-dropdown-wrapper';

    const btn = document.createElement('vscode-button');
    btn.setAttribute('appearance', 'secondary');
    btn.className = 'register-dropdown-btn';
    btn.textContent = 'Register \u25BE'; // ▾
    btn.setAttribute('aria-haspopup', 'true');
    btn.setAttribute('aria-expanded', 'false');
    wrapper.appendChild(btn);

    const menu = document.createElement('div');
    menu.className = 'register-dropdown-menu hidden';
    wrapper.appendChild(menu);

    const items: { label: string; action: () => void }[] = [
        {
            label: 'Register Step',
            action: () => {
                const nodeId = selectedNodeId ?? undefined;
                showStepForm(nodeId);
            },
        },
        {
            label: 'Register Image',
            action: () => {
                if (!selectedNodeId) return;
                showImageForm(selectedNodeId);
            },
        },
        {
            label: 'Update Assembly Binary',
            action: () => {
                if (!selectedNodeId) return;
                showBinaryUpdateForm('assembly', selectedNodeId);
            },
        },
        {
            label: 'Update Package Binary',
            action: () => {
                if (!selectedNodeId) return;
                showBinaryUpdateForm('package', selectedNodeId);
            },
        },
        {
            label: 'Register Webhook',
            action: () => showWebhookForm(),
        },
        {
            label: 'Register Service Endpoint',
            action: () => showServiceEndpointContractPicker(),
        },
        {
            label: 'Register Custom API',
            action: () => showCustomApiForm(),
        },
        {
            label: 'Register Data Provider',
            action: () => showDataProviderForm(),
        },
    ];

    for (const item of items) {
        const menuItem = document.createElement('button');
        menuItem.className = 'register-dropdown-item';
        menuItem.textContent = item.label;
        menuItem.addEventListener('click', () => {
            menu.classList.add('hidden');
            btn.setAttribute('aria-expanded', 'false');
            item.action();
        });
        menu.appendChild(menuItem);
    }

    btn.addEventListener('click', (e) => {
        e.stopPropagation();
        const isOpen = !menu.classList.contains('hidden');
        menu.classList.toggle('hidden', isOpen);
        btn.setAttribute('aria-expanded', String(!isOpen));
    });

    // Close on outside click
    document.addEventListener('click', () => {
        menu.classList.add('hidden');
        btn.setAttribute('aria-expanded', 'false');
    });

    // Insert before the first existing toolbar separator or at the end
    toolbar.appendChild(wrapper);
}

// ── Message handlers ──────────────────────────────────────────────────────────

function handleTreeLoaded(data: PluginTreeData): void {
    treeData = data;
    // Collapse all nodes on full reload
    expandedIds.clear();
    selectedNodeId = null;
    clearDetailPanel();
    rebuildAndRender();
}

function handleChildrenLoaded(parentId: string, children: PluginTreeNode[]): void {
    if (!treeData) return;
    const parent = findNodeInTree(parentId);
    if (parent) {
        parent.children = children;
        parent.hasChildren = children.length > 0;
    }
    rebuildAndRender();
}

function handleNodeUpdated(node: PluginTreeNode): void {
    if (!treeData) return;
    const existing = findNodeInTree(node.id);
    if (existing) {
        // Preserve children, update everything else
        const existingChildren = existing.children;
        Object.assign(existing, node);
        if (existingChildren && (!node.children || node.children.length === 0)) {
            existing.children = existingChildren;
        }
    }
    rebuildAndRender();
}

function handleNodeRemoved(nodeId: string): void {
    if (!treeData) return;
    removeNodeFromArray(treeData.assemblies, nodeId);
    removeNodeFromArray(treeData.packages, nodeId);
    removeNodeFromArray(treeData.serviceEndpoints, nodeId);
    removeNodeFromArray(treeData.customApis, nodeId);
    removeNodeFromArray(treeData.dataSources, nodeId);
    expandedIds.delete(nodeId);
    if (selectedNodeId === nodeId) {
        selectedNodeId = null;
        clearDetailPanel();
    }
    rebuildAndRender();
}

/**
 * Convert a camelCase or PascalCase key to "Title Case With Spaces".
 * e.g. "executionOrder" → "Execution Order", "isEnabled" → "Is Enabled"
 */
function formatKey(key: string): string {
    // Insert space before each uppercase letter that follows a lowercase letter or digit
    return key
        .replace(/([a-z\d])([A-Z])/g, '$1 $2')
        .replace(/([A-Z]+)([A-Z][a-z])/g, '$1 $2')
        .replace(/^./, (c) => c.toUpperCase());
}

function renderDetail(detail: Record<string, unknown> | null | undefined): void {
    if (!detailPanel) return;
    detailPanel.innerHTML = ''; // Safe: we control all content via createElement

    if (!detail) {
        const msg = document.createElement('div');
        msg.className = 'detail-placeholder';
        msg.textContent = 'No details available.';
        detailPanel.appendChild(msg);
        return;
    }

    const table = document.createElement('table');
    table.className = 'detail-table';

    for (const [key, value] of Object.entries(detail)) {
        // Skip null/undefined/empty values
        if (value === null || value === undefined || value === '') continue;

        const row = document.createElement('tr');

        const keyCell = document.createElement('td');
        keyCell.className = 'detail-key';
        keyCell.textContent = formatKey(key);

        const valueCell = document.createElement('td');
        valueCell.className = 'detail-value';

        // Format booleans as Yes/No
        if (typeof value === 'boolean') {
            valueCell.textContent = value ? 'Yes' : 'No';
        } else if (typeof value === 'string' && /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}/.test(value)) {
            // Format ISO date strings to locale-friendly format
            try {
                valueCell.textContent = new Date(value).toLocaleString();
            } catch {
                valueCell.textContent = value;
            }
        } else {
            valueCell.textContent = String(value);
        }

        row.appendChild(keyCell);
        row.appendChild(valueCell);
        table.appendChild(row);
    }

    detailPanel.appendChild(table);
}

function handleDetailLoaded(detail: Record<string, unknown>): void {
    if (!detailPanel) return;
    detailPanel.classList.add('visible');
    renderDetail(detail);
}

function clearDetailPanel(): void {
    if (detailPanel) {
        detailPanel.innerHTML = '';
        detailPanel.classList.remove('visible');
    }
}


function showDetailError(message: string): void {
    if (!detailPanel) return;
    detailPanel.innerHTML = ''; // Safe: we control all content via createElement
    detailPanel.classList.add('visible');
    const state = document.createElement('div');
    state.className = 'error-state';
    state.textContent = message;
    detailPanel.appendChild(state);
}

// ── Message router ────────────────────────────────────────────────────────────

window.addEventListener('message', (event: MessageEvent<PluginsPanelHostToWebview>) => {
    const msg = event.data;
    // VS Code sends internal messages that lack 'command' — ignore them
    if (!msg || typeof msg !== 'object' || !('command' in msg)) return;

    switch (msg.command) {
        case 'treeLoaded':
            handleTreeLoaded(msg.data);
            break;
        case 'childrenLoaded':
            handleChildrenLoaded(msg.parentId, msg.children);
            break;
        case 'nodeUpdated':
            handleNodeUpdated(msg.node);
            break;
        case 'nodeRemoved':
            handleNodeRemoved(msg.nodeId);
            break;
        case 'detailLoaded':
            handleDetailLoaded(msg.detail);
            break;
        case 'detailError':
            showDetailError(msg.message);
            break;
        case 'loading':
            showLoading();
            break;
        case 'error':
            showError(msg.message);
            break;
        case 'updateEnvironment':
            updateEnvironment(msg);
            break;
        case 'daemonReconnected':
            // Re-send ready to trigger a fresh load
            vscode.postMessage({ command: 'ready' });
            break;
        case 'messagesLoaded':
            // Reserved for future message-mode view support
            break;
        case 'entitiesLoaded':
            // Reserved for future entity-mode view support
            break;
        case 'attributesLoaded':
            // Reserved for future step image attribute support
            break;
        case 'showRegisterForm':
            if (msg.formType === 'step') {
                showStepForm(msg.parentId);
            } else if (msg.formType === 'image') {
                showImageForm(msg.parentId ?? '');
            } else if (msg.formType === 'assembly') {
                showBinaryUpdateForm('assembly', msg.parentId ?? '');
            } else if (msg.formType === 'package') {
                showBinaryUpdateForm('package', msg.parentId ?? '');
            } else if (msg.formType === 'webhook') {
                showWebhookForm();
            } else if (msg.formType === 'serviceendpoint') {
                showServiceBusForm(msg.contract ?? 'queue');
            } else if (msg.formType === 'customapi') {
                showCustomApiForm();
            } else if (msg.formType === 'dataprovider') {
                showDataProviderForm();
            }
            break;
        case 'showUpdateForm':
            if (msg.formType === 'step') {
                showStepForm(undefined, msg.data);
            } else if (msg.formType === 'image') {
                showImageForm(msg.id, msg.data);
            } else if (msg.formType === 'assembly') {
                showBinaryUpdateForm('assembly', msg.id);
            } else if (msg.formType === 'package') {
                showBinaryUpdateForm('package', msg.id);
            } else if (msg.formType === 'webhook') {
                showWebhookForm(msg.data);
            } else if (msg.formType === 'serviceendpoint') {
                showServiceBusForm(msg.contract ?? 'queue', msg.data);
            } else if (msg.formType === 'customapi') {
                showCustomApiForm(msg.data);
            } else if (msg.formType === 'dataprovider') {
                showDataProviderForm(msg.data);
            }
            break;
        default:
            assertNever(msg);
    }
});

// ── Keyboard shortcuts ────────────────────────────────────────────────────────
document.addEventListener('keydown', (e: KeyboardEvent) => {
    // Escape = clear selection and hide detail panel
    if (e.key === 'Escape' && selectedNodeId !== null) {
        selectedNodeId = null;
        clearDetailPanel();
        renderTree();
    }

    // F5 = refresh
    if (e.key === 'F5') {
        e.preventDefault();
        vscode.postMessage({ command: 'ready' });
    }
});

// ── Help button ───────────────────────────────────────────────────────────────

function buildHelpButton(): void {
    const toolbar = document.querySelector('.toolbar');
    if (!toolbar) return;

    const helpBtn = document.createElement('vscode-button');
    helpBtn.setAttribute('appearance', 'secondary');
    helpBtn.textContent = '?';
    helpBtn.title = 'Plugin Registration Help';
    helpBtn.setAttribute('aria-label', 'Open plugin registration documentation');
    helpBtn.addEventListener('click', () => {
        vscode.postMessage({ command: 'openHelp' });
    });

    toolbar.appendChild(helpBtn);
}

// ── Ready signal ──────────────────────────────────────────────────────────────
buildRegisterDropdown();
buildHelpButton();
vscode.postMessage({ command: 'ready' });
