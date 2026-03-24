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
                entityNodes.push(makeGroupNode(`msg_${message}_${entity}`, entity, 'entityGroup', stepList));
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
            roots.push(makeGroupNode(`ent_${entity}`, entity, 'entityGroup', messageNodes));
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
    // hideMicrosoft applies to assembly-level nodes only; group nodes are synthetic
    if (hideMicrosoft && node.nodeType === 'assembly') {
        const lc = node.name.toLowerCase();
        if (lc.startsWith('microsoft.') && lc !== 'microsoft.crm.servicebus') return false;
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

    // Wrap in scroll container of fixed total height
    const inner = document.createElement('div');
    inner.style.height = `${totalHeight}px`;
    inner.style.position = 'relative';
    inner.appendChild(fragment);

    // Replace content
    treeContainer.innerHTML = '';
    treeContainer.appendChild(inner);

    // Update status
    if (statusText) {
        statusText.textContent = flatNodes.length === 0
            ? 'No items'
            : `${flatNodes.length} item${flatNodes.length !== 1 ? 's' : ''}`;
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
        toggle.textContent = flatNode.expanded ? '\u25BC' : '\u25BA'; // ▼ or ►
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

function renderDetail(detail: Record<string, unknown>): void {
    if (!detailPanel) return;
    detailPanel.innerHTML = ''; // Safe: we control all content via createElement

    const table = document.createElement('table');
    table.className = 'detail-table';

    for (const [key, value] of Object.entries(detail)) {
        const row = document.createElement('tr');

        const keyCell = document.createElement('td');
        keyCell.className = 'detail-key';
        keyCell.textContent = formatKey(key);

        const valueCell = document.createElement('td');
        valueCell.className = 'detail-value';
        valueCell.textContent = String(value ?? '');

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

// ── Ready signal ──────────────────────────────────────────────────────────────
vscode.postMessage({ command: 'ready' });
