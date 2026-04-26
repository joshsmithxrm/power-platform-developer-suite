// query-panel.ts
// External webview script for the Data Explorer panel.
// Built by esbuild as IIFE for browser, loaded via <script src="...">.

import { escapeHtml, escapeAttr, sanitizeValue } from './shared/dom-utils.js';
import type { QueryPanelWebviewToHost, QueryPanelHostToWebview } from './shared/message-types.js';
import type { QueryResultResponse, QueryColumnInfo } from '../../types.js';
import { assertNever } from './shared/assert-never.js';
import { getVsCodeApi } from './shared/vscode-api.js';
import { FilterBar } from './shared/filter-bar.js';
import { installErrorHandler } from './shared/error-handler.js';
import { getSelectionRect as computeSelectionRect, isSingleCell as checkSingleCell } from './shared/selection-utils.js';
import type { CellCoord, SelectionRect } from './shared/selection-utils.js';

// Monaco is loaded as a global script before this module.
declare const monaco: typeof import('monaco-editor');

const vscode = getVsCodeApi<QueryPanelWebviewToHost>();
installErrorHandler((msg) => vscode.postMessage(msg as QueryPanelWebviewToHost));

// ── Monaco Editor initialization ──
let editor: import('monaco-editor').editor.IStandaloneCodeEditor | null = null;
try {
    const bodyClasses = document.body.className;
    const monacoTheme = (bodyClasses.includes('vscode-light') || bodyClasses.includes('vscode-high-contrast-light')) ? 'vs' : 'vs-dark';
    editor = monaco.editor.create(document.getElementById('sql-editor')!, {
        language: 'sql',
        theme: monacoTheme,
        value: '-- Enter your SQL query here\n',
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
let validationRequestId = 0;
let validationTimeout: ReturnType<typeof setTimeout> | undefined;
let validationResponseTimeout: ReturnType<typeof setTimeout> | undefined;
let manualOverride = false;

// ── Resize handle (editor height splitter) ──
const resizeHandle = document.getElementById('resize-handle')!;
const editorWrapper = document.querySelector('.editor-wrapper') as HTMLElement;
let resizing = false;
let startY = 0;
let startHeight = 0;

resizeHandle.addEventListener('mousedown', (e: MouseEvent) => {
    e.preventDefault();
    resizing = true;
    startY = e.clientY;
    startHeight = editorWrapper.offsetHeight;
    resizeHandle.classList.add('dragging');
    document.body.style.cursor = 'ns-resize';
    document.body.style.userSelect = 'none';
});

document.addEventListener('mousemove', (e: MouseEvent) => {
    if (!resizing) return;
    const delta = e.clientY - startY;
    const newHeight = Math.max(80, Math.min(startHeight + delta, 500));
    editorWrapper.style.height = newHeight + 'px';
    if (editor) editor.layout();
});

document.addEventListener('mouseup', () => {
    if (!resizing) return;
    resizing = false;
    resizeHandle.classList.remove('dragging');
    document.body.style.cursor = '';
    document.body.style.userSelect = '';
});

const executeBtn = document.getElementById('execute-btn') as HTMLElement;
const cancelBtn = document.getElementById('cancel-btn') as HTMLElement;
const clearBtn = document.getElementById('clear-btn') as HTMLElement;
const importBtn = document.getElementById('import-btn') as HTMLElement;
const exportBtn = document.getElementById('export-btn') as HTMLElement;
const historyBtn = document.getElementById('history-btn') as HTMLElement;
const moreBtn = document.getElementById('more-btn') as HTMLElement;
const langToggle = document.getElementById('lang-toggle') as HTMLElement;
const filterBar = document.getElementById('filter-bar') as HTMLElement;
const filterInput = document.getElementById('filter-input') as HTMLInputElement;
const filterCount = document.getElementById('filter-count') as HTMLElement;
const transpileWarningBanner = document.getElementById('transpile-warning-banner') as HTMLElement;
const dataSourceBanner = document.getElementById('data-source-banner') as HTMLElement;
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
let topN: number | null = null;
let useDistinct = false;

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
interface CellRichValue { text: string; url?: string | null; entityType?: string; entityId?: string }

let anchor: CellCoord | null = null;
let focus: CellCoord | null = null;
let displayedRows: Record<string, unknown>[] = [];
const copyHintEl = document.getElementById('copy-hint');

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
    if (checkSingleCell(anchor, focus)) {
        copyHintEl.textContent = 'Ctrl+C: copy value | Right-click: with header';
    } else {
        copyHintEl.textContent = 'Ctrl+C: copy with headers | Right-click: values only';
    }
}

function updateSelectionVisuals(): void {
    const rect = computeSelectionRect(anchor, focus);
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

// ── Top N / DISTINCT query modifier ──
function applyQueryModifiers(sql: string): string {
    if (!sql || currentLanguage === 'xml') return sql;
    let modified = sql;
    // Strip any existing modifier injected by this function (idempotent re-run)
    modified = modified.replace(/^SELECT\s+DISTINCT\s+TOP\s+\d+\s+/i, 'SELECT ');
    modified = modified.replace(/^SELECT\s+DISTINCT\s+/i, 'SELECT ');
    modified = modified.replace(/^SELECT\s+TOP\s+\d+\s+/i, 'SELECT ');

    const hasDistinct = useDistinct;
    const hasTopN = topN !== null && topN > 0;

    if (!hasDistinct && !hasTopN) return modified;

    // Rebuild SELECT prefix
    let prefix = 'SELECT ';
    if (hasDistinct && hasTopN) {
        prefix += 'DISTINCT TOP ' + topN + ' ';
    } else if (hasDistinct) {
        prefix += 'DISTINCT ';
    } else {
        prefix += 'TOP ' + topN + ' ';
    }

    return modified.replace(/^SELECT\s+/i, prefix);
}

// ── Language auto-detection ──
function detectLang(content: string): string {
    const trimmed = content.trimStart();
    return trimmed.startsWith('<') ? 'xml' : 'sql';
}

function updateLanguage(lang: string): void {
    if (lang !== currentLanguage) {
        currentLanguage = lang;
        if (editor) {
            monaco.editor.setModelLanguage(editor.getModel()!, lang);
            monaco.editor.setModelMarkers(editor.getModel()!, 'ppds', []);
        }
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
    // Clear stale markers and debounce re-validation
    const model = editor.getModel();
    if (model) monaco.editor.setModelMarkers(model, 'ppds', []);
    clearTimeout(validationTimeout);
    clearTimeout(validationResponseTimeout);
    const sql = editor.getValue();
    const language = currentLanguage;
    if (sql.trim().length >= 3) {
        validationTimeout = setTimeout(() => {
            validationRequestId++;
            vscode.postMessage({
                command: 'requestValidation',
                requestId: validationRequestId,
                sql,
                language,
            } as QueryPanelWebviewToHost);
            validationResponseTimeout = setTimeout(() => {
                if (model) monaco.editor.setModelMarkers(model, 'ppds', []);
            }, 3000);
        }, 300);
    }
});

// Language toggle pill — triggers conversion
langToggle.addEventListener('click', (e) => {
    const target = e.target;
    if (!(target instanceof Element)) return;
    const seg = target.closest<HTMLElement>('.lang-seg');
    if (!seg || seg.classList.contains('active')) return;
    const targetLang = seg.dataset['lang']!;
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
        if (sql) vscode.postMessage({ command: 'executeQuery', sql: applyQueryModifiers(sql), useTds, language: currentLanguage });
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
    if (sql) vscode.postMessage({ command: 'executeQuery', sql: applyQueryModifiers(sql), useTds, language: currentLanguage });
});
historyBtn.addEventListener('click', () => {
    vscode.postMessage({ command: 'showHistory' });
});

// ── Clear button ──
clearBtn.addEventListener('click', () => {
    if (editor) editor.setValue('');
    manualOverride = false;
    allRows = [];
    columns = [];
    displayedRows = [];
    pagingCookie = null;
    currentPage = 1;
    moreRecords = false;
    sortColumn = -1;
    lastEntityName = null;
    lastIsAggregate = false;
    clearSelection();
    resultsFilter.clear();
    resultsWrapper.innerHTML = '<div class="empty-state">Run a query to see results</div>';
    loadMoreBar.style.display = 'none';
    filterBar.classList.remove('visible');
    transpileWarningBanner.style.display = 'none';
    dataSourceBanner.style.display = 'none';
    statusText.textContent = 'Ready';
    rowCountEl.textContent = '';
    executionTimeEl.textContent = '';
});

// ── Import button ──
importBtn.addEventListener('click', () => {
    vscode.postMessage({ command: 'loadQueryFromFile' });
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
        { label: '', action: 'separator' },
        { label: 'DISTINCT', action: 'toggleDistinct', checked: useDistinct },
        { label: topN !== null ? 'Top N: ' + topN + '\u2026' : 'Top N\u2026', action: 'setTopN', checked: topN !== null },
    ]);
});

// ── Dropdown action handler ──
document.addEventListener('click', (e) => {
    const target = e.target;
    if (!(target instanceof Element)) return;
    const item = target.closest<HTMLElement>('.dropdown-item');
    if (!item || !activeDropdown) return;
    const action = item.dataset['action'];
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
        case 'toggleDistinct':
            useDistinct = !useDistinct;
            break;
        case 'setTopN': {
            const input = prompt(topN !== null ? 'Top N rows (leave blank to clear):' : 'Top N rows:', topN !== null ? String(topN) : '');
            if (input === null) break; // cancelled
            const trimmed = input.trim();
            if (trimmed === '') {
                topN = null;
            } else {
                const parsed = parseInt(trimmed, 10);
                if (!isNaN(parsed) && parsed > 0) {
                    topN = parsed;
                }
            }
            break;
        }
    }
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
    // Skip selection when clicking a link or the hover copy button
    if (target.closest('a[href]') || target.closest('.cell-copy-btn')) return;

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
// Also handles the hover copy button on record link cells.
resultsWrapper.addEventListener('click', (e) => {
    const target = e.target as HTMLElement;

    // Hover copy button on record link cells
    const copyBtn = target.closest<HTMLElement>('.cell-copy-btn');
    if (copyBtn) {
        e.preventDefault();
        e.stopPropagation();
        const val = copyBtn.dataset['copyValue'] ?? '';
        vscode.postMessage({ command: 'copyToClipboard', text: val });
        if (copyHintEl) {
            const truncated = val.length > 40 ? val.substring(0, 40) + '...' : val;
            copyHintEl.textContent = 'Copied: ' + truncated;
            setTimeout(() => { updateCopyHint(); }, 2000);
        }
        return;
    }

    const link = target.closest<HTMLAnchorElement>('a[href]');
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
const resultsFilter = new FilterBar<Record<string, unknown>>({
    input: filterInput,
    countEl: filterCount,
    getSearchableText: (row) => columns.map(col => {
        const key = col.alias || col.logicalName;
        const val = row[key];
        if (val === null || val === undefined) return '';
        if (typeof val === 'object' && val !== null && 'formatted' in val) {
            return String((val as Record<string, unknown>).formatted || (val as Record<string, unknown>).value || '');
        }
        return String(val);
    }),
    onFilter: (filtered) => renderTable(filtered),
    itemLabel: 'rows',
});

function hideFilter(): void {
    resultsFilter.clear();
}

// ── Message handling ──
window.addEventListener('message', (event: MessageEvent<QueryPanelHostToWebview>) => {
    const msg = event.data;
    // VS Code sends internal messages (e.g., vscodeScheduleAsyncWork) that lack 'command' — ignore them
    if (!msg || typeof msg !== 'object' || !('command' in msg)) return;
    switch (msg.command) {
        case 'queryResult':
            if (editor) monaco.editor.setModelMarkers(editor.getModel()!, 'ppds', []);
            handleQueryResult(msg.data);
            break;
        case 'appendResults':
            handleAppendResults(msg.data);
            break;
        case 'queryError':
            showError(msg.error);
            if (msg.diagnostics?.length) setDiagnosticMarkers(msg.diagnostics);
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
            updateEnvironmentDisplay(msg.profileName, msg.name);
            currentEnvironmentUrl = msg.url || null;
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
        case 'validationResult': {
            if (msg.requestId !== validationRequestId) break;
            clearTimeout(validationResponseTimeout);
            if (msg.diagnostics?.length) {
                setDiagnosticMarkers(msg.diagnostics);
            } else if (editor) {
                monaco.editor.setModelMarkers(editor.getModel()!, 'ppds', []);
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
    resultsFilter.setItems(allRows);
    updateStatus(data);
    loadMoreBar.style.display = moreRecords ? '' : 'none';
    filterBar.classList.add('visible');

    if (data.warnings && data.warnings.length > 0) {
        const msgs = data.warnings.map(w => escapeHtml(w)).join('<br>');
        transpileWarningBanner.innerHTML =
            '<span class="warning-icon codicon codicon-warning"></span>' +
            '<span class="warning-messages">' + msgs + '</span>' +
            '<button class="warning-dismiss" title="Dismiss" aria-label="Dismiss warnings">\u00D7</button>';
        transpileWarningBanner.style.display = '';
        const dismissBtn = transpileWarningBanner.querySelector('.warning-dismiss');
        if (dismissBtn) {
            dismissBtn.addEventListener('click', () => {
                transpileWarningBanner.style.display = 'none';
            }, { once: true });
        }
    } else {
        transpileWarningBanner.style.display = 'none';
    }

    if (data.dataSources && data.dataSources.length > 1) {
        const parts = data.dataSources.map(ds => {
            const tag = ds.isRemote ? 'remote' : 'local';
            return `<span class="data-source-label">${escapeHtml(ds.label)}</span> <span class="data-source-tag">(${tag})</span>`;
        });
        dataSourceBanner.innerHTML = 'Data from: ' + parts.join(' &middot; ');
        dataSourceBanner.style.display = '';
    } else {
        dataSourceBanner.style.display = 'none';
    }
}

function handleAppendResults(data: QueryResultResponse): void {
    resetExecuteBtn();
    const newRecords = data.records || [];
    allRows = allRows.concat(newRecords);
    pagingCookie = data.pagingCookie || null;
    currentPage++;
    moreRecords = data.moreRecords || false;
    resultsFilter.setItems(allRows);
    updateStatus(data);
    loadMoreBar.style.display = moreRecords ? '' : 'none';
}

function setDiagnosticMarkers(diagnostics: Array<{ start: number; length: number; severity: string; message: string }>): void {
    const model = editor?.getModel();
    if (!model || !diagnostics.length) return;
    const markers = diagnostics.map(d => {
        const startPos = model.getPositionAt(d.start);
        const endPos = model.getPositionAt(d.start + d.length);
        return {
            severity: d.severity === 'warning'
                ? monaco.MarkerSeverity.Warning
                : d.severity === 'info'
                    ? monaco.MarkerSeverity.Info
                    : monaco.MarkerSeverity.Error,
            startLineNumber: startPos.lineNumber,
            startColumn: startPos.column,
            endLineNumber: endPos.lineNumber,
            endColumn: endPos.column,
            message: d.message,
        };
    });
    monaco.editor.setModelMarkers(model, 'ppds', markers);
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
                // Copy value is the GUID/entity ID if available, otherwise the display text
                const copyVal = rich.entityId ?? rich.text;
                html += '<td class="cell-has-link" data-row="' + rowIdx + '" data-col="' + colIdx + '">' +
                    '<a href="' + escapeAttr(rich.url) + '" target="_blank">' + escapeHtml(rich.text) + '</a>' +
                    '<button class="cell-copy-btn" data-copy-value="' + escapeAttr(copyVal) + '" title="Copy ' + escapeAttr(copyVal) + '" tabindex="-1">' +
                    '<span class="codicon codicon-copy" style="font-size:10px;"></span>' +
                    '</button>' +
                    '</td>';
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
    const rect = computeSelectionRect(anchor, focus);
    if (!rect) return;

    const single = checkSingleCell(anchor, focus);
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
    const rect = computeSelectionRect(anchor, focus);
    if (!rect || clickRow < rect.minRow || clickRow > rect.maxRow ||
        clickCol < rect.minCol || clickCol > rect.maxCol) {
        anchor = { row: clickRow, col: clickCol };
        focus = { row: clickRow, col: clickCol };
        updateSelectionVisuals();
    }

    const single = checkSingleCell(anchor, focus);
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
            html += '<div class="context-menu-item" data-action="' + escapeAttr(item.action) + '" style="display:flex;align-items:center;">';
            html += '<span>' + escapeHtml(item.label) + '</span>';
            if (item.shortcut) html += '<span style="margin-left:auto;padding-left:24px;opacity:0.6;font-size:11px;">' + escapeHtml(item.shortcut) + '</span>';
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
