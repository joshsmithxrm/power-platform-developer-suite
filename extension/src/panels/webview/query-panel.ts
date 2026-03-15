// query-panel.ts
// External webview script for the Data Explorer panel.
// Built by esbuild as IIFE for browser, loaded via <script src="...">.

// Error tracking — must be set up before any imports execute.
// Declared on window for diagnostic access from the host.
declare global {
    interface Window {
        __ppds_errors: { msg: string; src: string; line: number | null; col: number | null; stack: string }[];
    }
}
window.__ppds_errors = [];
window.onerror = function (msg, src, line, col, err) {
    window.__ppds_errors.push({
        msg: String(msg),
        src: String(src ?? ''),
        line: line ?? null,
        col: col ?? null,
        stack: err && err.stack ? err.stack.substring(0, 500) : '',
    });
    const el = document.getElementById('sql-editor');
    if (el) el.setAttribute('data-error', String(msg));
};

import { escapeHtml, escapeAttr, sanitizeValue } from './shared/dom-utils.js';
import type { QueryPanelWebviewToHost, QueryPanelHostToWebview } from './shared/message-types.js';
import type { QueryResultResponse, QueryColumnInfo } from '../../types.js';
import { assertNever } from './shared/assert-never.js';
import { getVsCodeApi } from './shared/vscode-api.js';

// Monaco is loaded as a global script before this module.
declare const monaco: typeof import('monaco-editor');

const vscode = getVsCodeApi<QueryPanelWebviewToHost>();

// ── Monaco Editor initialization ──
let editor: import('monaco-editor').editor.IStandaloneCodeEditor | null = null;
try {
    const bodyClasses = document.body.className;
    const monacoTheme = (bodyClasses.includes('vscode-light') || bodyClasses.includes('vscode-high-contrast-light')) ? 'vs' : 'vs-dark';
    editor = monaco.editor.create(document.getElementById('sql-editor')!, {
        language: 'sql',
        theme: monacoTheme,
        value: '',
        minimap: { enabled: false },
        lineNumbers: 'off',
        scrollBeyondLastLine: false,
        wordWrap: 'on',
        fontSize: 13,
        automaticLayout: true,
        suggestOnTriggerCharacters: true,
        glyphMargin: false,
        folding: false,
        lineDecorationsWidth: 0,
        lineNumbersMinChars: 0,
        renderLineHighlight: 'none',
        overviewRulerLanes: 0,
        hideCursorInOverviewRuler: true,
        overviewRulerBorder: false,
        scrollbar: { vertical: 'auto', horizontal: 'auto' },
    });
} catch (monacoError: unknown) {
    const errMsg = monacoError instanceof Error ? monacoError.message : String(monacoError);
    const errStack = monacoError instanceof Error ? monacoError.stack : undefined;
    console.error('[PPDS] Monaco editor failed to initialize:', errMsg);
    vscode.postMessage({ command: 'webviewError', error: 'Monaco init failed: ' + errMsg, stack: errStack });
    document.getElementById('sql-editor')!.textContent = 'Editor failed to load: ' + errMsg;
}

let currentLanguage = 'sql';
let manualOverride = false;

const executeBtn = document.getElementById('execute-btn') as HTMLElement;
const cancelBtn = document.getElementById('cancel-btn') as HTMLElement;
const exportBtn = document.getElementById('export-btn') as HTMLElement;
const historyBtn = document.getElementById('history-btn') as HTMLElement;
const moreBtn = document.getElementById('more-btn') as HTMLElement;
const langToggle = document.getElementById('lang-toggle') as HTMLElement;
const filterBar = document.getElementById('filter-bar') as HTMLElement;
const filterInput = document.getElementById('filter-input') as HTMLInputElement;
const filterCount = document.getElementById('filter-count') as HTMLElement;
const resultsWrapper = document.getElementById('results-wrapper') as HTMLElement;
const loadMoreBar = document.getElementById('load-more-bar') as HTMLElement;
const loadMoreBtn = document.getElementById('load-more-btn') as HTMLElement;
const statusText = document.getElementById('status-text') as HTMLElement;
const rowCountEl = document.getElementById('row-count') as HTMLElement;
const executionTimeEl = document.getElementById('execution-time') as HTMLElement;

let allRows: Record<string, unknown>[] = [];
let columns: QueryColumnInfo[] = [];
let pagingCookie: string | null = null;
let currentPage = 1;
let moreRecords = false;
let sortColumn = -1;
let sortAsc = true;
let useTds = false;
let isExecuting = false;

// ── Record URL state ──
let lastEntityName: string | null = null;
let lastIsAggregate = false;
let currentEnvironmentUrl: string | null = null;

function getRecordId(row: Record<string, unknown>): unknown {
    if (!lastEntityName || !row) return null;
    const idKey = lastEntityName + 'id';
    return row[idKey] || null;
}

function buildRecordUrl(entityName: string, recordId: unknown): string | null {
    if (!currentEnvironmentUrl || !entityName || !recordId) return null;
    const baseUrl = currentEnvironmentUrl.replace(/\/+$/, '');
    return baseUrl + '/main.aspx?pagetype=entityrecord&etn=' +
        encodeURIComponent(entityName) + '&id=' + encodeURIComponent(String(recordId));
}

// ── Selection state (anchor+focus rectangle) ──
interface CellPosition { row: number; col: number }
interface SelectionRect { minRow: number; maxRow: number; minCol: number; maxCol: number }
interface CellRichValue { text: string; url?: string | null; entityType?: string; entityId?: string }

let anchor: CellPosition | null = null;
let focus: CellPosition | null = null;
let displayedRows: Record<string, unknown>[] = [];
const copyHintEl = document.getElementById('copy-hint');

// ── Selection utilities ──
function getSelectionRect(): SelectionRect | null {
    if (!anchor || !focus) return null;
    return {
        minRow: Math.min(anchor.row, focus.row),
        maxRow: Math.max(anchor.row, focus.row),
        minCol: Math.min(anchor.col, focus.col),
        maxCol: Math.max(anchor.col, focus.col),
    };
}

function isSingleCell(): boolean {
    if (!anchor || !focus) return false;
    return anchor.row === focus.row && anchor.col === focus.col;
}

function clearSelection(): void {
    anchor = null;
    focus = null;
    const tbody = resultsWrapper.querySelector('tbody');
    if (tbody) tbody.classList.remove('all-selected');
    resultsWrapper.querySelectorAll('td').forEach(td => {
        td.classList.remove('cell-selected', 'cell-selected-top', 'cell-selected-bottom', 'cell-selected-left', 'cell-selected-right');
    });
    if (copyHintEl) copyHintEl.textContent = '';
}

function isGuid(val: string): boolean {
    return /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(val);
}

function isUrl(val: string): boolean {
    return /^https?:\/\/.+/i.test(val);
}

function getCellDisplayValue(row: Record<string, unknown>, colIdx: number): string {
    return getCellRichValue(row, colIdx).text;
}

/** Returns { text, url?, entityType?, entityId? } for rendering and context menu */
function getCellRichValue(row: Record<string, unknown>, colIdx: number): CellRichValue {
    const col = columns[colIdx];
    const key = col.alias || col.logicalName;
    const rawVal = row ? row[key] : undefined;
    if (rawVal === null || rawVal === undefined) return { text: '' };

    // Structured lookup value — clickable link to target record
    if (typeof rawVal === 'object' && rawVal !== null && 'entityId' in rawVal) {
        const obj = rawVal as Record<string, unknown>;
        const text = String(obj.formatted || obj.value || '');
        if (currentEnvironmentUrl && obj.entityType && obj.entityId) {
            return {
                text,
                url: buildRecordUrl(String(obj.entityType), obj.entityId),
                entityType: String(obj.entityType),
                entityId: String(obj.entityId),
            };
        }
        return { text };
    }

    // Structured formatted value (optionsets, booleans)
    if (typeof rawVal === 'object' && rawVal !== null && 'formatted' in rawVal) {
        const obj = rawVal as Record<string, unknown>;
        return { text: String(obj.formatted || obj.value || '') };
    }

    const stringValue = String(rawVal);

    // Primary key column — clickable link to own record
    if (lastEntityName && currentEnvironmentUrl && !lastIsAggregate) {
        const pkCol = lastEntityName + 'id';
        if (col.logicalName && col.logicalName.toLowerCase() === pkCol.toLowerCase() && isGuid(stringValue)) {
            return {
                text: stringValue,
                url: buildRecordUrl(lastEntityName, stringValue),
                entityType: lastEntityName,
                entityId: stringValue,
            };
        }
    }

    // Plain URL string — clickable external link
    if (isUrl(stringValue)) {
        return { text: stringValue, url: stringValue };
    }

    return { text: stringValue };
}

function updateCopyHint(): void {
    if (!copyHintEl) return;
    if (!anchor) { copyHintEl.textContent = ''; return; }
    if (isSingleCell()) {
        copyHintEl.textContent = 'Ctrl+C: copy value | Right-click: with header';
    } else {
        copyHintEl.textContent = 'Ctrl+C: copy with headers | Right-click: values only';
    }
}

function updateSelectionVisuals(): void {
    const rect = getSelectionRect();
    const tbody = resultsWrapper.querySelector('tbody');
    if (!tbody) return;

    // Fast path for Ctrl+A (full table)
    tbody.classList.remove('all-selected');
    if (rect && rect.minRow === 0 && rect.minCol === 0 &&
        rect.maxRow === displayedRows.length - 1 && rect.maxCol === columns.length - 1 &&
        displayedRows.length > 0) {
        tbody.classList.add('all-selected');
        updateCopyHint();
        return;
    }

    // Clear all selection classes
    tbody.querySelectorAll('td').forEach(td => {
        td.classList.remove('cell-selected', 'cell-selected-top', 'cell-selected-bottom', 'cell-selected-left', 'cell-selected-right');
    });

    if (!rect) { updateCopyHint(); return; }

    // Apply selection classes
    for (let r = rect.minRow; r <= rect.maxRow; r++) {
        for (let c = rect.minCol; c <= rect.maxCol; c++) {
            const td = tbody.querySelector('td[data-row="' + r + '"][data-col="' + c + '"]');
            if (!td) continue;
            td.classList.add('cell-selected');
            if (r === rect.minRow) td.classList.add('cell-selected-top');
            if (r === rect.maxRow) td.classList.add('cell-selected-bottom');
            if (c === rect.minCol) td.classList.add('cell-selected-left');
            if (c === rect.maxCol) td.classList.add('cell-selected-right');
        }
    }
    updateCopyHint();
}

// ── Language auto-detection ──
function detectLang(content: string): string {
    const trimmed = content.trimStart();
    return trimmed.startsWith('<') ? 'xml' : 'sql';
}

function updateLanguage(lang: string): void {
    if (lang !== currentLanguage) {
        currentLanguage = lang;
        if (editor) monaco.editor.setModelLanguage(editor.getModel()!, lang);
    }
    // Update pill toggle state
    langToggle.querySelectorAll('.lang-seg').forEach(btn => {
        btn.classList.toggle('active', (btn as HTMLElement).dataset.lang === lang);
    });
}

// Clear table selection when user clicks into the editor
if (editor) editor.onDidFocusEditorText(() => {
    if (anchor) clearSelection();
});

if (editor) editor.onDidChangeModelContent(() => {
    if (!manualOverride) {
        const detected = detectLang(editor.getValue());
        updateLanguage(detected);
    }
});

// Language toggle pill — triggers conversion
langToggle.addEventListener('click', (e) => {
    const seg = (e.target as HTMLElement).closest('.lang-seg') as HTMLElement | null;
    if (!seg || seg.classList.contains('active')) return;
    const targetLang = seg.dataset.lang!;
    const content = editor ? editor.getValue().trim() : '';
    if (!content) {
        // Empty editor — just switch mode
        manualOverride = true;
        updateLanguage(targetLang);
        return;
    }
    // Request conversion from host
    vscode.postMessage({
        command: 'convertQuery',
        sql: content,
        fromLanguage: currentLanguage,
        toLanguage: targetLang,
    });
});

// ── Monaco clipboard bridge ──
// VS Code webview sandbox blocks Monaco's native clipboard access.
// Route copy/paste through postMessage to the host extension.
if (editor) editor.addAction({
    id: 'ppds.editorCopy',
    label: 'Copy',
    keybindings: [monaco.KeyMod.CtrlCmd | monaco.KeyCode.KeyC],
    run: (ed) => {
        const selection = ed.getModel()!.getValueInRange(ed.getSelection()!);
        if (selection) {
            vscode.postMessage({ command: 'copyToClipboard', text: selection });
        }
    },
});

if (editor) editor.addAction({
    id: 'ppds.editorCut',
    label: 'Cut',
    keybindings: [monaco.KeyMod.CtrlCmd | monaco.KeyCode.KeyX],
    run: (ed) => {
        const sel = ed.getSelection()!;
        const text = ed.getModel()!.getValueInRange(sel);
        if (text) {
            vscode.postMessage({ command: 'copyToClipboard', text });
            ed.executeEdits('cut', [{ range: sel, text: '' }]);
        }
    },
});

if (editor) editor.addAction({
    id: 'ppds.editorPaste',
    label: 'Paste',
    keybindings: [monaco.KeyMod.CtrlCmd | monaco.KeyCode.KeyV],
    run: () => {
        vscode.postMessage({ command: 'requestClipboard' });
    },
});

// ── Monaco keybinding: Escape to cancel query ──
if (editor) editor.addCommand(monaco.KeyCode.Escape, function () {
    if (isExecuting) {
        vscode.postMessage({ command: 'cancelQuery' });
    }
});

// ── Cancel button handler ──
cancelBtn.addEventListener('click', function () {
    vscode.postMessage({ command: 'cancelQuery' });
});

// ── Monaco keybinding: Ctrl+Enter to execute ──
if (editor) editor.addAction({
    id: 'ppds.executeQuery',
    label: 'Execute Query',
    keybindings: [monaco.KeyMod.CtrlCmd | monaco.KeyCode.Enter],
    run: () => {
        const sql = editor.getValue().trim();
        if (sql) vscode.postMessage({ command: 'executeQuery', sql, useTds, language: currentLanguage });
    },
});

// ── Completion provider bridge ──
let completionRequestId = 0;
interface PendingCompletion {
    resolve: (result: import('monaco-editor').languages.CompletionList) => void;
    range: { startLineNumber: number; startColumn: number; endLineNumber: number; endColumn: number };
}
const pendingCompletions = new Map<number, PendingCompletion>();

function requestCompletions(
    model: import('monaco-editor').editor.ITextModel,
    position: import('monaco-editor').Position,
    language: string,
): Promise<import('monaco-editor').languages.CompletionList> {
    // Compute the word range for replacement
    const wordInfo = model.getWordUntilPosition(position);
    const range = {
        startLineNumber: position.lineNumber,
        startColumn: wordInfo.startColumn,
        endLineNumber: position.lineNumber,
        endColumn: wordInfo.endColumn,
    };

    return new Promise((resolve) => {
        const id = ++completionRequestId;
        const cursorOffset = model.getOffsetAt(position);
        pendingCompletions.set(id, { resolve, range });
        vscode.postMessage({ command: 'requestCompletions', requestId: id, sql: model.getValue(), cursorOffset, language });
        setTimeout(() => {
            const p = pendingCompletions.get(id);
            if (p) {
                pendingCompletions.delete(id);
                p.resolve({ suggestions: [] });
            }
        }, 3000);
    });
}

function mapKind(kind: string): number {
    switch (kind) {
        case 'entity': return monaco.languages.CompletionItemKind.Class;
        case 'attribute': return monaco.languages.CompletionItemKind.Field;
        case 'keyword': return monaco.languages.CompletionItemKind.Keyword;
        default: return monaco.languages.CompletionItemKind.Text;
    }
}

if (typeof monaco !== 'undefined') {
    monaco.languages.registerCompletionItemProvider('sql', {
        triggerCharacters: [' ', ',', '.'],
        provideCompletionItems: async (model, position) => {
            return await requestCompletions(model, position, 'sql');
        },
    });

    monaco.languages.registerCompletionItemProvider('xml', {
        triggerCharacters: [' ', '<', '"'],
        provideCompletionItems: async (model, position) => {
            return await requestCompletions(model, position, 'fetchxml');
        },
    });
}

// ── Button handlers ──
executeBtn.addEventListener('click', () => {
    const sql = editor ? editor.getValue().trim() : '';
    if (sql) vscode.postMessage({ command: 'executeQuery', sql, useTds, language: currentLanguage });
});
historyBtn.addEventListener('click', () => {
    vscode.postMessage({ command: 'showHistory' });
});

// ── Dropdown menu helper ──
let activeDropdown: HTMLElement | null = null;

function showDropdown(anchorEl: HTMLElement, items: { label: string; action: string; checked?: boolean }[]): void {
    closeDropdown();
    const menu = document.createElement('div');
    menu.className = 'dropdown-menu';
    let html = '';
    for (const item of items) {
        if (item.action === 'separator') {
            html += '<div class="dropdown-separator"></div>';
        } else {
            const cls = item.checked === true ? 'checked' : (item.checked === false ? 'unchecked' : '');
            html += '<div class="dropdown-item ' + cls + '" data-action="' + escapeAttr(item.action) + '">' + escapeHtml(item.label) + '</div>';
        }
    }
    menu.innerHTML = html;
    const rect = anchorEl.getBoundingClientRect();
    menu.style.position = 'fixed';
    menu.style.left = rect.left + 'px';
    menu.style.top = rect.bottom + 2 + 'px';
    document.body.appendChild(menu);
    activeDropdown = menu;
    return;
}

function closeDropdown(): void {
    if (activeDropdown) { activeDropdown.remove(); activeDropdown = null; }
}

document.addEventListener('click', (e) => {
    if (activeDropdown && !activeDropdown.contains(e.target as Node) &&
        e.target !== exportBtn && e.target !== moreBtn &&
        !(exportBtn.contains(e.target as Node)) && !(moreBtn.contains(e.target as Node))) {
        closeDropdown();
    }
});

// ── Export dropdown ──
exportBtn.addEventListener('click', (e) => {
    e.stopPropagation();
    if (activeDropdown) { closeDropdown(); return; }
    showDropdown(exportBtn, [
        { label: 'Results as CSV\u2026', action: 'exportCsv' },
        { label: 'Results as TSV\u2026', action: 'exportTsv' },
        { label: 'Results as JSON\u2026', action: 'exportJson' },
        { label: 'Copy to Clipboard', action: 'exportClipboard' },
        { label: '', action: 'separator' },
        { label: 'Save Query\u2026', action: 'saveQuery' },
    ]);
});

// ── Overflow menu ──
moreBtn.addEventListener('click', (e) => {
    e.stopPropagation();
    if (activeDropdown) { closeDropdown(); return; }
    showDropdown(moreBtn, [
        { label: 'Load Query\u2026', action: 'loadQuery' },
        { label: 'Open in Notebook', action: 'openInNotebook' },
        { label: '', action: 'separator' },
        { label: 'Explain Query', action: 'explain' },
        { label: '', action: 'separator' },
        { label: 'TDS Read Replica', action: 'toggleTds', checked: useTds },
    ]);
});

// ── Dropdown action handler ──
document.addEventListener('click', (e) => {
    const item = (e.target as HTMLElement).closest('.dropdown-item') as HTMLElement | null;
    if (!item || !activeDropdown) return;
    const action = item.dataset.action;
    closeDropdown();
    const sql = editor ? editor.getValue().trim() : '';
    switch (action) {
        case 'exportCsv':
        case 'exportTsv':
        case 'exportJson':
        case 'exportClipboard':
            vscode.postMessage({ command: 'exportResults', format: action.replace('export', '').toLowerCase() });
            break;
        case 'saveQuery':
            vscode.postMessage({ command: 'saveQuery', sql, language: currentLanguage });
            break;
        case 'loadQuery':
            vscode.postMessage({ command: 'loadQueryFromFile' });
            break;
        case 'openInNotebook':
            if (sql) vscode.postMessage({ command: 'openInNotebook', sql });
            break;
        case 'explain':
            if (sql) vscode.postMessage({ command: 'explainQuery', sql });
            break;
        case 'toggleTds':
            useTds = !useTds;
            break;
    }
});

// ── Environment picker ──
const envPickerBtn = document.getElementById('env-picker-btn') as HTMLElement;
const envPickerName = document.getElementById('env-picker-name') as HTMLElement;

envPickerBtn.addEventListener('click', () => {
    vscode.postMessage({ command: 'requestEnvironmentList' });
});

function updateEnvironmentDisplay(name: string | null): void {
    envPickerName.textContent = name || 'No environment';
}

loadMoreBtn.addEventListener('click', () => {
    if (pagingCookie) {
        vscode.postMessage({ command: 'loadMore', pagingCookie, page: currentPage + 1 });
    }
});

// ── Click + drag selection ──
resultsWrapper.addEventListener('mousedown', (e) => {
    const target = e.target as HTMLElement;
    const th = target.closest<HTMLElement>('th[data-col]');
    if (th) {
        const col = parseInt(th.dataset.col!);
        if (sortColumn === col) { sortAsc = !sortAsc; }
        else { sortColumn = col; sortAsc = true; }
        clearSelection();
        sortAndRender();
        return;
    }
    // Skip selection when clicking a link — let the click handler open it
    if (target.closest('a[href]')) return;

    const td = target.closest<HTMLElement>('td[data-row]');
    if (!td) return;

    // Remove focus from Monaco so Ctrl+C goes to our handler, not Monaco's
    if (editor && editor.hasTextFocus() && document.activeElement && (document.activeElement as HTMLElement).blur) {
        (document.activeElement as HTMLElement).blur();
    }

    const row = parseInt(td.dataset.row!);
    const col = parseInt(td.dataset.col!);

    if (e.shiftKey && anchor) {
        // Shift+click: extend selection from anchor
        focus = { row, col };
    } else {
        // Normal click: new single-cell selection + start drag tracking
        anchor = { row, col };
        focus = { row, col };
        const tbody = resultsWrapper.querySelector('tbody');
        if (tbody) tbody.classList.add('selecting');

        const onMouseMove = (ev: MouseEvent) => {
            const moveTarget = (ev.target as HTMLElement).closest ? (ev.target as HTMLElement).closest<HTMLElement>('td[data-row]') : null;
            if (moveTarget) {
                focus = { row: parseInt(moveTarget.dataset.row!), col: parseInt(moveTarget.dataset.col!) };
                updateSelectionVisuals();
            }
        };
        const onMouseUp = () => {
            const tb = resultsWrapper.querySelector('tbody');
            if (tb) tb.classList.remove('selecting');
            document.removeEventListener('mousemove', onMouseMove);
            document.removeEventListener('mouseup', onMouseUp);
        };
        document.addEventListener('mousemove', onMouseMove);
        document.addEventListener('mouseup', onMouseUp);
    }
    updateSelectionVisuals();
});

// ── Link click handler ──
// VS Code webviews block external navigations by default.
// Intercept <a> clicks in the results table and open via the extension host.
resultsWrapper.addEventListener('click', (e) => {
    const link = (e.target as HTMLElement).closest<HTMLAnchorElement>('a[href]');
    if (link) {
        e.preventDefault();
        e.stopPropagation();
        vscode.postMessage({ command: 'openRecordUrl', url: link.getAttribute('href')! });
    }
});

// ── Keyboard shortcuts ──
// Ctrl+C with table selection: use CAPTURE phase to intercept before Monaco.
// When the user has selected cells in the results grid, Ctrl+C should copy
// from the grid — not from Monaco's editor. Monaco would otherwise intercept
// the event and copy its own (empty) selection.
document.addEventListener('keydown', (e) => {
    // Only intercept Ctrl+C for table copy when Monaco does NOT have focus.
    // If Monaco has focus, let it handle copy/paste normally.
    if ((e.ctrlKey || e.metaKey) && e.key === 'c' && anchor && (!editor || !editor.hasTextFocus())) {
        e.preventDefault();
        e.stopPropagation();
        copySelection(e.shiftKey);
        return;
    }
}, true); // capture phase — fires before Monaco's handlers

document.addEventListener('keydown', (e) => {
    // Filter focus
    if (e.key === '/' && (!editor || !editor.hasTextFocus()) && document.activeElement !== filterInput) {
        e.preventDefault();
        filterInput.focus();
        return;
    }

    // Escape: blur filter input, then clear selection
    if (e.key === 'Escape') {
        if (document.activeElement === filterInput) {
            hideFilter();
            filterInput.blur();
        } else if (anchor) {
            clearSelection();
        }
        return;
    }

    // Ctrl+A: select all (when not in text inputs)
    if ((e.ctrlKey || e.metaKey) && e.key === 'a' &&
        (!editor || !editor.hasTextFocus()) &&
        document.activeElement !== filterInput) {
        if (displayedRows.length > 0 && columns.length > 0) {
            e.preventDefault();
            anchor = { row: 0, col: 0 };
            focus = { row: displayedRows.length - 1, col: columns.length - 1 };
            updateSelectionVisuals();
        }
        return;
    }

    // Ctrl+Shift+F: FetchXML preview
    if ((e.ctrlKey || e.metaKey) && e.shiftKey && e.key === 'F') {
        e.preventDefault();
        const sql = editor ? editor.getValue().trim() : '';
        if (sql) vscode.postMessage({ command: 'showFetchXml', sql });
        return;
    }

    // Ctrl+E: Export
    if ((e.ctrlKey || e.metaKey) && !e.shiftKey && e.key === 'e') {
        e.preventDefault();
        vscode.postMessage({ command: 'exportResults' });
        return;
    }

    // Ctrl+Shift+H: History
    if ((e.ctrlKey || e.metaKey) && e.shiftKey && e.key === 'H') {
        e.preventDefault();
        vscode.postMessage({ command: 'showHistory' });
    }
});

// ── Filter ──
function hideFilter(): void {
    filterInput.value = '';
    renderTable(allRows);
    filterCount.textContent = '';
}
filterInput.addEventListener('input', () => {
    const term = filterInput.value.toLowerCase();
    if (!term) { renderTable(allRows); filterCount.textContent = ''; return; }
    const filtered = allRows.filter(row =>
        columns.some(col => {
            const key = col.alias || col.logicalName;
            const val = row[key];
            if (val === null || val === undefined) return false;
            const str = typeof val === 'object' && val !== null && 'formatted' in val
                ? String((val as Record<string, unknown>).formatted || (val as Record<string, unknown>).value || '')
                : String(val);
            return str.toLowerCase().includes(term);
        })
    );
    renderTable(filtered);
    filterCount.textContent = 'Showing ' + filtered.length + ' of ' + allRows.length + ' rows';
});

// ── Message handling ──
window.addEventListener('message', (event: MessageEvent<QueryPanelHostToWebview>) => {
    const msg = event.data;
    switch (msg.command) {
        case 'queryResult':
            handleQueryResult(msg.data);
            break;
        case 'appendResults':
            handleAppendResults(msg.data);
            break;
        case 'queryError':
            showError(msg.error);
            break;
        case 'executionStarted':
            isExecuting = true;
            executeBtn.style.display = 'none';
            cancelBtn.style.display = '';
            resultsWrapper.innerHTML = '<div class="empty-state"><div class="spinner" style="width:24px;height:24px;margin:0 auto 12px;"></div><div>Executing query...</div></div>';
            statusText.innerHTML = '<span class="spinner"></span> Executing...';
            loadMoreBar.style.display = 'none';
            rowCountEl.textContent = '';
            executionTimeEl.textContent = '';
            break;
        case 'loadQuery':
            if (editor) editor.setValue(msg.sql);
            manualOverride = false;
            break;
        case 'updateEnvironment':
            updateEnvironmentDisplay(msg.name);
            currentEnvironmentUrl = msg.url || null;
            break;
        case 'clipboardContent':
            if (msg.text && editor && editor.hasTextFocus()) {
                editor.trigger('clipboard', 'type', { text: msg.text });
            }
            break;
        case 'queryCancelled':
            isExecuting = false;
            cancelBtn.style.display = 'none';
            executeBtn.style.display = '';
            statusText.textContent = 'Query cancelled';
            resultsWrapper.innerHTML = '<div class="empty-state">Query cancelled</div>';
            break;
        case 'completionResult': {
            const pending = pendingCompletions.get(msg.requestId);
            if (pending) {
                pendingCompletions.delete(msg.requestId);
                const suggestions = (msg.items || []).map(item => ({
                    label: item.label,
                    insertText: item.insertText,
                    kind: mapKind(item.kind),
                    detail: item.detail || '',
                    sortText: String(item.sortOrder || 0).padStart(5, '0'),
                    range: pending.range,
                }));
                pending.resolve({ suggestions });
            }
            break;
        }
        case 'daemonReconnected':
            document.getElementById('reconnect-banner')!.style.display = '';
            break;
        case 'queryConverted':
            manualOverride = true;
            if (editor) {
                editor.setValue(msg.content);
                updateLanguage(msg.language);
            }
            break;
        case 'conversionFailed':
            // Conversion failed — just toggle the syntax mode anyway
            manualOverride = true;
            updateLanguage(msg.language);
            break;
        default:
            assertNever(msg);
    }
});

function resetExecuteBtn(): void {
    isExecuting = false;
    cancelBtn.style.display = 'none';
    executeBtn.style.display = '';
}

function handleQueryResult(data: QueryResultResponse): void {
    resetExecuteBtn();
    columns = data.columns || [];
    allRows = data.records || [];
    pagingCookie = data.pagingCookie || null;
    currentPage = 1;
    moreRecords = data.moreRecords || false;
    sortColumn = -1;
    lastEntityName = data.entityName || null;
    lastIsAggregate = data.isAggregate || false;
    renderTable(allRows);
    updateStatus(data);
    loadMoreBar.style.display = moreRecords ? '' : 'none';
    filterBar.classList.add('visible');
}

function handleAppendResults(data: QueryResultResponse): void {
    resetExecuteBtn();
    const newRecords = data.records || [];
    allRows = allRows.concat(newRecords);
    pagingCookie = data.pagingCookie || null;
    currentPage++;
    moreRecords = data.moreRecords || false;
    renderTable(allRows);
    updateStatus(data);
    loadMoreBar.style.display = moreRecords ? '' : 'none';
}

function showError(error: string): void {
    resetExecuteBtn();
    resultsWrapper.innerHTML = '<div class="error-state">' + escapeHtml(error) + '</div>';
    statusText.textContent = 'Error';
    loadMoreBar.style.display = 'none';
}

function updateStatus(data: QueryResultResponse): void {
    statusText.textContent = 'Ready';
    rowCountEl.textContent = allRows.length + ' row' + (allRows.length !== 1 ? 's' : '') + (moreRecords ? ' (more available)' : '');
    const timeText = data.executionTimeMs ? 'in ' + data.executionTimeMs + 'ms' : '';
    const modeText = data.queryMode === 'tds' ? ' via TDS' : data.queryMode === 'dataverse' ? ' via Dataverse' : '';
    executionTimeEl.textContent = timeText + modeText;
}

// ── Rendering ──
function renderTable(rows: Record<string, unknown>[]): void {
    displayedRows = rows;
    clearSelection();

    if (rows.length === 0 && allRows.length === 0) {
        resultsWrapper.innerHTML = '<div class="empty-state">Run a query to see results</div>';
        return;
    }
    if (rows.length === 0) {
        resultsWrapper.innerHTML = '<div class="empty-state">No matching rows</div>';
        return;
    }

    let html = '<table class="results-table"><thead><tr>';
    columns.forEach((col, idx) => {
        const label = col.alias || col.displayName || col.logicalName;
        const indicator = sortColumn === idx ? (sortAsc ? ' \u25B2' : ' \u25BC') : '';
        html += '<th data-col="' + idx + '">' + escapeHtml(label) + '<span class="sort-indicator">' + indicator + '</span></th>';
    });
    html += '</tr></thead><tbody>';

    rows.forEach((row, rowIdx) => {
        html += '<tr>';
        columns.forEach((_col, colIdx) => {
            const rich = getCellRichValue(row, colIdx);
            if (rich.url) {
                html += '<td data-row="' + rowIdx + '" data-col="' + colIdx + '"><a href="' + escapeAttr(rich.url) + '" target="_blank">' + escapeHtml(rich.text) + '</a></td>';
            } else {
                html += '<td data-row="' + rowIdx + '" data-col="' + colIdx + '">' + escapeHtml(rich.text) + '</td>';
            }
        });
        html += '</tr>';
    });
    html += '</tbody></table>';
    resultsWrapper.innerHTML = html;
}

function sortAndRender(): void {
    if (sortColumn < 0) return;
    const key = columns[sortColumn].alias || columns[sortColumn].logicalName;
    const sorted = [...allRows].sort((a, b) => {
        let va: unknown = a[key], vb: unknown = b[key];
        if (va && typeof va === 'object' && 'formatted' in (va as Record<string, unknown>)) va = (va as Record<string, unknown>).formatted || (va as Record<string, unknown>).value;
        if (vb && typeof vb === 'object' && 'formatted' in (vb as Record<string, unknown>)) vb = (vb as Record<string, unknown>).formatted || (vb as Record<string, unknown>).value;
        if (va === null || va === undefined) return 1;
        if (vb === null || vb === undefined) return -1;
        if (typeof va === 'number' && typeof vb === 'number') return sortAsc ? va - vb : vb - va;
        return sortAsc ? String(va).localeCompare(String(vb)) : String(vb).localeCompare(String(va));
    });
    renderTable(sorted);
}

function copySelection(invertHeaders: boolean): void {
    if (!anchor) return;
    const rect = getSelectionRect();
    if (!rect) return;

    const single = isSingleCell();
    const withHeaders = single ? invertHeaders : !invertHeaders;

    let text = '';

    if (withHeaders) {
        const headers: string[] = [];
        for (let c = rect.minCol; c <= rect.maxCol; c++) {
            headers.push(columns[c].alias || columns[c].logicalName);
        }
        text += headers.join('\t') + '\n';
    }

    for (let r = rect.minRow; r <= rect.maxRow; r++) {
        const vals: string[] = [];
        for (let c = rect.minCol; c <= rect.maxCol; c++) {
            vals.push(sanitizeValue(getCellDisplayValue(displayedRows[r], c)));
        }
        text += vals.join('\t') + '\n';
    }

    vscode.postMessage({ command: 'copyToClipboard', text: text.trim() });
    showCopyFeedback(rect, single, withHeaders);
}

function showCopyFeedback(rect: SelectionRect, single: boolean, withHeaders: boolean): void {
    if (!copyHintEl) return;
    if (single) {
        const val = getCellDisplayValue(displayedRows[rect.minRow], rect.minCol);
        const truncated = val.length > 40 ? val.substring(0, 40) + '...' : val;
        copyHintEl.textContent = 'Copied: ' + truncated;
    } else {
        const rowCount = rect.maxRow - rect.minRow + 1;
        const colCount = rect.maxCol - rect.minCol + 1;
        copyHintEl.textContent = 'Copied ' + rowCount + ' rows x ' + colCount + ' cols ' + (withHeaders ? 'with headers' : 'without headers');
    }
    setTimeout(() => { updateCopyHint(); }, 2000);
}

// ── Right-click context menu ──
let contextMenu: HTMLElement | null = null;
document.addEventListener('contextmenu', (e) => {
    const td = (e.target as HTMLElement).closest<HTMLElement>('td[data-row]');
    if (!td) return;
    e.preventDefault();
    removeContextMenu();

    const clickRow = parseInt(td.dataset.row!);
    const clickCol = parseInt(td.dataset.col!);

    // If right-clicked cell is outside current selection, move selection there
    const rect = getSelectionRect();
    if (!rect || clickRow < rect.minRow || clickRow > rect.maxRow ||
        clickCol < rect.minCol || clickCol > rect.maxCol) {
        anchor = { row: clickRow, col: clickCol };
        focus = { row: clickRow, col: clickCol };
        updateSelectionVisuals();
    }

    const single = isSingleCell();
    const inverseLabel = single ? 'Copy (with header)' : 'Copy (no headers)';

    contextMenu = document.createElement('div');
    contextMenu.className = 'context-menu';

    // Determine the best record link for the clicked cell:
    // 1. If the cell itself is a lookup/primary key, use that (cell-aware)
    // 2. Otherwise fall back to the row's primary key
    const cellRich = getCellRichValue(displayedRows[clickRow], clickCol);
    let contextRecordUrl: string | null = null;
    let contextRecordLabel = 'Open Record in Dynamics';
    if (cellRich.url && cellRich.entityType) {
        contextRecordUrl = cellRich.url;
        contextRecordLabel = 'Open ' + escapeHtml(cellRich.entityType) + ' Record';
    } else {
        const rowPkId = getRecordId(displayedRows[clickRow]);
        if (lastEntityName && rowPkId && !lastIsAggregate) {
            contextRecordUrl = buildRecordUrl(lastEntityName, rowPkId);
            contextRecordLabel = 'Open ' + escapeHtml(lastEntityName) + ' Record';
        }
    }

    const items: { label: string; shortcut: string; action: string }[] = [
        { label: 'Copy', shortcut: 'Ctrl+C', action: 'copy' },
        { label: inverseLabel, shortcut: '', action: 'copyInverse' },
        { label: 'separator', shortcut: '', action: 'separator' },
        { label: 'Copy Cell Value', shortcut: '', action: 'cell' },
        { label: 'Copy Row', shortcut: '', action: 'row' },
        { label: 'Copy All Results', shortcut: '', action: 'all' },
        { label: 'separator', shortcut: '', action: 'separator' },
        { label: 'Filter to This Value', shortcut: '/', action: 'filterToValue' },
    ];

    if (contextRecordUrl) {
        items.push({ label: 'separator', shortcut: '', action: 'separator' });
        items.push({ label: contextRecordLabel, shortcut: '', action: 'openRecord' });
        items.push({ label: 'Copy Record URL', shortcut: '', action: 'copyRecordUrl' });
    }

    let html = '';
    for (const item of items) {
        if (item.action === 'separator') {
            html += '<div style="border-top: 1px solid var(--vscode-menu-separatorBackground, var(--vscode-panel-border)); margin: 4px 0;"></div>';
        } else {
            html += '<div class="context-menu-item" data-action="' + item.action + '" style="display:flex;align-items:center;">';
            html += '<span>' + item.label + '</span>';
            if (item.shortcut) html += '<span style="margin-left:auto;padding-left:24px;opacity:0.6;font-size:11px;">' + item.shortcut + '</span>';
            html += '</div>';
        }
    }
    contextMenu.innerHTML = html;
    contextMenu.style.left = e.clientX + 'px';
    contextMenu.style.top = e.clientY + 'px';
    document.body.appendChild(contextMenu);

    contextMenu.addEventListener('click', (ev) => {
        const actionEl = (ev.target as HTMLElement).closest<HTMLElement>('[data-action]');
        if (!actionEl) return;
        const action = actionEl.dataset.action;

        if (action === 'copy') {
            copySelection(false);
        } else if (action === 'copyInverse') {
            copySelection(true);
        } else if (action === 'cell') {
            const val = getCellDisplayValue(displayedRows[clickRow], clickCol);
            vscode.postMessage({ command: 'copyToClipboard', text: sanitizeValue(val) });
            if (copyHintEl) {
                copyHintEl.textContent = 'Copied: ' + (val.length > 40 ? val.substring(0, 40) + '...' : val);
                setTimeout(() => { updateCopyHint(); }, 2000);
            }
        } else if (action === 'row') {
            const vals: string[] = [];
            for (let c = 0; c < columns.length; c++) {
                vals.push(sanitizeValue(getCellDisplayValue(displayedRows[clickRow], c)));
            }
            vscode.postMessage({ command: 'copyToClipboard', text: vals.join('\t') });
            if (copyHintEl) {
                copyHintEl.textContent = 'Copied row';
                setTimeout(() => { updateCopyHint(); }, 2000);
            }
        } else if (action === 'all') {
            const headers = columns.map(c => c.alias || c.logicalName);
            let text = headers.join('\t') + '\n';
            displayedRows.forEach(row => {
                const vals: string[] = [];
                for (let c = 0; c < columns.length; c++) {
                    vals.push(sanitizeValue(getCellDisplayValue(row, c)));
                }
                text += vals.join('\t') + '\n';
            });
            vscode.postMessage({ command: 'copyToClipboard', text: text.trim() });
            if (copyHintEl) {
                copyHintEl.textContent = 'Copied all ' + displayedRows.length + ' rows';
                setTimeout(() => { updateCopyHint(); }, 2000);
            }
        } else if (action === 'openRecord') {
            if (contextRecordUrl) vscode.postMessage({ command: 'openRecordUrl', url: contextRecordUrl });
        } else if (action === 'copyRecordUrl') {
            if (contextRecordUrl) {
                vscode.postMessage({ command: 'copyToClipboard', text: contextRecordUrl });
                if (copyHintEl) {
                    copyHintEl.textContent = 'Copied record URL';
                    setTimeout(() => { updateCopyHint(); }, 2000);
                }
            }
        } else if (action === 'filterToValue') {
            const cellText = getCellDisplayValue(displayedRows[clickRow], clickCol);
            filterBar.classList.add('visible');
            filterInput.value = cellText;
            filterInput.dispatchEvent(new Event('input'));
        }
        removeContextMenu();
    });
});
document.addEventListener('click', (e) => {
    if (contextMenu && !contextMenu.contains(e.target as Node)) removeContextMenu();
});
function removeContextMenu(): void {
    if (contextMenu) { contextMenu.remove(); contextMenu = null; }
}

document.getElementById('reconnect-refresh')!.addEventListener('click', function (e) {
    e.preventDefault();
    document.getElementById('reconnect-banner')!.style.display = 'none';
    vscode.postMessage({ command: 'refresh' });
});

// Signal ready
vscode.postMessage({ command: 'ready' });
