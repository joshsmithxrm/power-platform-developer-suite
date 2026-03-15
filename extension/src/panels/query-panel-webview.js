// query-panel-webview.js
// External webview script for the Data Explorer panel.
// Extracted from inline <script> to avoid VS Code's ~32KB inline script limit.
// Built by esbuild as IIFE for browser, loaded via <script src="...">.

/* global acquireVsCodeApi, monaco */

window.__ppds_errors = [];
window.onerror = function(msg, src, line, col, err) {
    window.__ppds_errors.push({msg: String(msg), src: String(src), line: line, col: col, stack: err && err.stack ? err.stack.substring(0, 500) : ''});
    var el = document.getElementById('sql-editor');
    if (el) el.setAttribute('data-error', String(msg));
};

(function() {
    const vscode = acquireVsCodeApi();

    // ── Monaco Editor initialization ──
    var editor = null;
    try {
        var bodyClasses = document.body.className;
        var monacoTheme = (bodyClasses.includes('vscode-light') || bodyClasses.includes('vscode-high-contrast-light')) ? 'vs' : 'vs-dark';
        editor = monaco.editor.create(document.getElementById('sql-editor'), {
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
    } catch (monacoError) {
        var errMsg = monacoError instanceof Error ? monacoError.message : String(monacoError);
        var errStack = monacoError instanceof Error ? monacoError.stack : undefined;
        console.error('[PPDS] Monaco editor failed to initialize:', errMsg);
        vscode.postMessage({ command: 'webviewError', error: 'Monaco init failed: ' + errMsg, stack: errStack });
        document.getElementById('sql-editor').textContent = 'Editor failed to load: ' + errMsg;
    }

    let currentLanguage = 'sql';
    let manualOverride = false;

    const executeBtn = document.getElementById('execute-btn');
    const cancelBtn = document.getElementById('cancel-btn');
    const fetchxmlBtn = document.getElementById('fetchxml-btn');
    const explainBtn = document.getElementById('explain-btn');
    const exportBtn = document.getElementById('export-btn');
    const historyBtn = document.getElementById('history-btn');
    const notebookBtn = document.getElementById('notebook-btn');
    const tdsBtn = document.getElementById('tds-btn');
    const langBtn = document.getElementById('lang-btn');
    const filterBtn = document.getElementById('filter-btn');
    const filterBar = document.getElementById('filter-bar');
    const filterInput = document.getElementById('filter-input');
    const filterCount = document.getElementById('filter-count');
    const resultsWrapper = document.getElementById('results-wrapper');
    const emptyState = document.getElementById('empty-state');
    const loadMoreBar = document.getElementById('load-more-bar');
    const loadMoreBtn = document.getElementById('load-more-btn');
    const statusText = document.getElementById('status-text');
    const rowCountEl = document.getElementById('row-count');
    const executionTimeEl = document.getElementById('execution-time');

    let allRows = [];
    let columns = [];
    let pagingCookie = null;
    let currentPage = 1;
    let moreRecords = false;
    let sortColumn = -1;
    let sortAsc = true;
    let useTds = false;
    let isExecuting = false;

    // ── Record URL state ──
    var lastEntityName = null;
    var lastIsAggregate = false;
    var currentEnvironmentUrl = null;

    function getRecordId(row) {
        if (!lastEntityName || !row) return null;
        var idKey = lastEntityName + 'id';
        return row[idKey] || null;
    }

    function buildRecordUrl(entityName, recordId) {
        if (!currentEnvironmentUrl || !entityName || !recordId) return null;
        var baseUrl = currentEnvironmentUrl.replace(/\/+$/, '');
        return baseUrl + '/main.aspx?pagetype=entityrecord&etn=' +
            encodeURIComponent(entityName) + '&id=' + encodeURIComponent(recordId);
    }

    // ── Selection state (anchor+focus rectangle) ──
    let anchor = null;   // {row, col} or null
    let focus = null;    // {row, col} or null
    let isDragging = false;
    let displayedRows = []; // tracks the rows array last passed to renderTable
    const copyHintEl = document.getElementById('copy-hint');

    // ── Selection utilities ──
    function getSelectionRect() {
        if (!anchor || !focus) return null;
        return {
            minRow: Math.min(anchor.row, focus.row),
            maxRow: Math.max(anchor.row, focus.row),
            minCol: Math.min(anchor.col, focus.col),
            maxCol: Math.max(anchor.col, focus.col),
        };
    }

    function isSingleCell() {
        if (!anchor || !focus) return false;
        return anchor.row === focus.row && anchor.col === focus.col;
    }

    function clearSelection() {
        anchor = null;
        focus = null;
        const tbody = resultsWrapper.querySelector('tbody');
        if (tbody) tbody.classList.remove('all-selected');
        resultsWrapper.querySelectorAll('td').forEach(td => {
            td.classList.remove('cell-selected', 'cell-selected-top', 'cell-selected-bottom', 'cell-selected-left', 'cell-selected-right');
        });
        if (copyHintEl) copyHintEl.textContent = '';
    }

    function isGuid(val) {
        return /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(val);
    }

    function isUrl(val) {
        return /^https?:\/\/.+/i.test(val);
    }

    function escapeAttr(str) {
        if (str === null || str === undefined) return '';
        return String(str).replace(/&/g, '&amp;').replace(/"/g, '&quot;')
            .replace(/'/g, '&#039;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
    }

    function getCellDisplayValue(row, colIdx) {
        return getCellRichValue(row, colIdx).text;
    }

    /** Returns { text, url?, entityType?, entityId? } for rendering and context menu */
    function getCellRichValue(row, colIdx) {
        const col = columns[colIdx];
        const key = col.alias || col.logicalName;
        const rawVal = row ? row[key] : undefined;
        if (rawVal === null || rawVal === undefined) return { text: '' };

        // Structured lookup value — clickable link to target record
        if (typeof rawVal === 'object' && rawVal !== null && 'entityId' in rawVal) {
            const text = String(rawVal.formatted || rawVal.value || '');
            if (currentEnvironmentUrl && rawVal.entityType && rawVal.entityId) {
                return {
                    text,
                    url: buildRecordUrl(rawVal.entityType, rawVal.entityId),
                    entityType: rawVal.entityType,
                    entityId: rawVal.entityId,
                };
            }
            return { text };
        }

        // Structured formatted value (optionsets, booleans)
        if (typeof rawVal === 'object' && rawVal !== null && 'formatted' in rawVal) {
            return { text: String(rawVal.formatted || rawVal.value || '') };
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

    function sanitizeValue(val) {
        return val.replace(/\t/g, ' ').replace(/\r?\n/g, ' ');
    }

    function updateCopyHint() {
        if (!copyHintEl) return;
        if (!anchor) { copyHintEl.textContent = ''; return; }
        if (isSingleCell()) {
            copyHintEl.textContent = 'Ctrl+C: copy value | Right-click: with header';
        } else {
            copyHintEl.textContent = 'Ctrl+C: copy with headers | Right-click: values only';
        }
    }

    function updateSelectionVisuals() {
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
    function detectLang(content) {
        const trimmed = content.trimStart();
        return trimmed.startsWith('<') ? 'xml' : 'sql';
    }

    function updateLanguage(lang) {
        if (lang !== currentLanguage) {
            currentLanguage = lang;
            if (editor) monaco.editor.setModelLanguage(editor.getModel(), lang);
            langBtn.textContent = lang === 'xml' ? 'FetchXML' : 'SQL';
        }
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

    langBtn.addEventListener('click', () => {
        manualOverride = true;
        updateLanguage(currentLanguage === 'sql' ? 'xml' : 'sql');
    });

    // ── Monaco clipboard bridge ──
    // VS Code webview sandbox blocks Monaco's native clipboard access.
    // Route copy/paste through postMessage to the host extension.
    if (editor) editor.addAction({
        id: 'ppds.editorCopy',
        label: 'Copy',
        keybindings: [monaco.KeyMod.CtrlCmd | monaco.KeyCode.KeyC],
        run: (ed) => {
            const selection = ed.getModel().getValueInRange(ed.getSelection());
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
            const sel = ed.getSelection();
            const text = ed.getModel().getValueInRange(sel);
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
        run: (ed) => {
            vscode.postMessage({ command: 'requestClipboard' });
        },
    });

    // ── Monaco keybinding: Escape to cancel query ──
    if (editor) editor.addCommand(monaco.KeyCode.Escape, function() {
        if (isExecuting) {
            vscode.postMessage({ command: 'cancelQuery' });
        }
    });

    // ── Cancel button handler ──
    cancelBtn.addEventListener('click', function() {
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
    const pendingCompletions = new Map();

    function requestCompletions(model, position, language) {
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

    function mapKind(kind) {
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
    fetchxmlBtn.addEventListener('click', () => {
        const sql = editor ? editor.getValue().trim() : '';
        if (sql) vscode.postMessage({ command: 'showFetchXml', sql });
    });
    explainBtn.addEventListener('click', () => {
        const sql = editor ? editor.getValue().trim() : '';
        if (sql) vscode.postMessage({ command: 'explainQuery', sql });
    });
    exportBtn.addEventListener('click', () => {
        vscode.postMessage({ command: 'exportResults' });
    });
    historyBtn.addEventListener('click', () => {
        vscode.postMessage({ command: 'showHistory' });
    });
    notebookBtn.addEventListener('click', () => {
        const sql = editor ? editor.getValue().trim() : '';
        if (sql) vscode.postMessage({ command: 'openInNotebook', sql });
    });
    tdsBtn.addEventListener('click', () => {
        useTds = !useTds;
        tdsBtn.textContent = useTds ? 'TDS: On' : 'TDS: Off';
        tdsBtn.setAttribute('appearance', useTds ? 'primary' : 'secondary');
    });

    // ── Environment picker ──
    const envPickerBtn = document.getElementById('env-picker-btn');
    const envPickerName = document.getElementById('env-picker-name');

    envPickerBtn.addEventListener('click', () => {
        vscode.postMessage({ command: 'requestEnvironmentList' });
    });

    function updateEnvironmentDisplay(name) {
        envPickerName.textContent = name || 'No environment';
    }

    loadMoreBtn.addEventListener('click', () => {
        if (pagingCookie) {
            vscode.postMessage({ command: 'loadMore', pagingCookie, page: currentPage + 1 });
        }
    });

    // ── Click + drag selection ──
    resultsWrapper.addEventListener('mousedown', (e) => {
        const th = e.target.closest('th[data-col]');
        if (th) {
            const col = parseInt(th.dataset.col);
            if (sortColumn === col) { sortAsc = !sortAsc; }
            else { sortColumn = col; sortAsc = true; }
            clearSelection();
            sortAndRender();
            return;
        }
        // Skip selection when clicking a link — let the click handler open it
        if (e.target.closest('a[href]')) return;

        const td = e.target.closest('td[data-row]');
        if (!td) return;

        // Remove focus from Monaco so Ctrl+C goes to our handler, not Monaco's
        if (editor && editor.hasTextFocus() && document.activeElement && document.activeElement.blur) {
            document.activeElement.blur();
        }

        const row = parseInt(td.dataset.row);
        const col = parseInt(td.dataset.col);

        if (e.shiftKey && anchor) {
            // Shift+click: extend selection from anchor
            focus = { row, col };
        } else {
            // Normal click: new single-cell selection + start drag tracking
            anchor = { row, col };
            focus = { row, col };
            isDragging = true;
            const tbody = resultsWrapper.querySelector('tbody');
            if (tbody) tbody.classList.add('selecting');

            const onMouseMove = (ev) => {
                const target = ev.target.closest ? ev.target.closest('td[data-row]') : null;
                if (target) {
                    focus = { row: parseInt(target.dataset.row), col: parseInt(target.dataset.col) };
                    updateSelectionVisuals();
                }
            };
            const onMouseUp = () => {
                isDragging = false;
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
        const link = e.target.closest('a[href]');
        if (link) {
            e.preventDefault();
            e.stopPropagation();
            vscode.postMessage({ command: 'openRecordUrl', url: link.getAttribute('href') });
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
        // Filter toggle
        if (e.key === '/' && (!editor || !editor.hasTextFocus()) && document.activeElement !== filterInput) {
            e.preventDefault();
            toggleFilter();
            return;
        }

        // Escape: filter bar takes precedence, then selection
        if (e.key === 'Escape') {
            if (filterBar.classList.contains('visible')) {
                hideFilter();
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
    filterBtn.addEventListener('click', toggleFilter);
    function toggleFilter() {
        if (filterBar.classList.contains('visible')) {
            hideFilter();
        } else {
            filterBar.classList.add('visible');
            filterInput.focus();
        }
    }
    function hideFilter() {
        filterBar.classList.remove('visible');
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
                const str = typeof val === 'object' && 'formatted' in val ? String(val.formatted || val.value || '') : String(val);
                return str.toLowerCase().includes(term);
            })
        );
        renderTable(filtered);
        filterCount.textContent = 'Showing ' + filtered.length + ' of ' + allRows.length + ' rows';
    });

    // ── Message handling ──
    window.addEventListener('message', (event) => {
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
                document.getElementById('reconnect-banner').style.display = '';
                break;
        }
    });

    function resetExecuteBtn() {
        isExecuting = false;
        cancelBtn.style.display = 'none';
        executeBtn.style.display = '';
    }

    function handleQueryResult(data) {
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
    }

    function handleAppendResults(data) {
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

    function showError(error) {
        resetExecuteBtn();
        resultsWrapper.innerHTML = '<div class="error-state">' + escapeHtml(error) + '</div>';
        statusText.textContent = 'Error';
        loadMoreBar.style.display = 'none';
    }

    function updateStatus(data) {
        statusText.textContent = 'Ready';
        rowCountEl.textContent = allRows.length + ' row' + (allRows.length !== 1 ? 's' : '') + (moreRecords ? ' (more available)' : '');
        executionTimeEl.textContent = data.executionTimeMs ? 'in ' + data.executionTimeMs + 'ms' : '';
    }

    // ── Rendering ──
    function renderTable(rows) {
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
            columns.forEach((col, colIdx) => {
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

    function sortAndRender() {
        if (sortColumn < 0) return;
        const key = columns[sortColumn].alias || columns[sortColumn].logicalName;
        const sorted = [...allRows].sort((a, b) => {
            let va = a[key], vb = b[key];
            if (va && typeof va === 'object' && 'formatted' in va) va = va.formatted || va.value;
            if (vb && typeof vb === 'object' && 'formatted' in vb) vb = vb.formatted || vb.value;
            if (va === null || va === undefined) return 1;
            if (vb === null || vb === undefined) return -1;
            if (typeof va === 'number' && typeof vb === 'number') return sortAsc ? va - vb : vb - va;
            return sortAsc ? String(va).localeCompare(String(vb)) : String(vb).localeCompare(String(va));
        });
        renderTable(sorted);
    }

    function copySelection(invertHeaders) {
        if (!anchor) return;
        const rect = getSelectionRect();
        if (!rect) return;

        const single = isSingleCell();
        const withHeaders = single ? invertHeaders : !invertHeaders;

        let text = '';

        if (withHeaders) {
            const headers = [];
            for (let c = rect.minCol; c <= rect.maxCol; c++) {
                headers.push(columns[c].alias || columns[c].logicalName);
            }
            text += headers.join('\t') + '\n';
        }

        for (let r = rect.minRow; r <= rect.maxRow; r++) {
            const vals = [];
            for (let c = rect.minCol; c <= rect.maxCol; c++) {
                vals.push(sanitizeValue(getCellDisplayValue(displayedRows[r], c)));
            }
            text += vals.join('\t') + '\n';
        }

        vscode.postMessage({ command: 'copyToClipboard', text: text.trim() });
        showCopyFeedback(rect, single, withHeaders);
    }

    function showCopyFeedback(rect, single, withHeaders) {
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
    let contextMenu = null;
    document.addEventListener('contextmenu', (e) => {
        const td = e.target.closest('td[data-row]');
        if (!td) return;
        e.preventDefault();
        removeContextMenu();

        const clickRow = parseInt(td.dataset.row);
        const clickCol = parseInt(td.dataset.col);

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
        var cellRich = getCellRichValue(displayedRows[clickRow], clickCol);
        var contextRecordUrl = null;
        var contextRecordLabel = 'Open Record in Dynamics';
        if (cellRich.url && cellRich.entityType) {
            contextRecordUrl = cellRich.url;
            contextRecordLabel = 'Open ' + escapeHtml(cellRich.entityType) + ' Record';
        } else {
            var rowPkId = getRecordId(displayedRows[clickRow]);
            if (lastEntityName && rowPkId && !lastIsAggregate) {
                contextRecordUrl = buildRecordUrl(lastEntityName, rowPkId);
            }
        }

        const items = [
            { label: 'Copy', shortcut: 'Ctrl+C', action: 'copy' },
            { label: inverseLabel, shortcut: '', action: 'copyInverse' },
            { label: 'separator', shortcut: '', action: 'separator' },
            { label: 'Copy Cell Value', shortcut: '', action: 'cell' },
            { label: 'Copy Row', shortcut: '', action: 'row' },
            { label: 'Copy All Results', shortcut: '', action: 'all' },
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
            const actionEl = ev.target.closest('[data-action]');
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
                const vals = [];
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
                    const vals = [];
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
            }
            removeContextMenu();
        });
    });
    document.addEventListener('click', (e) => {
        if (contextMenu && !contextMenu.contains(e.target)) removeContextMenu();
    });
    function removeContextMenu() {
        if (contextMenu) { contextMenu.remove(); contextMenu = null; }
    }

    function escapeHtml(str) {
        if (str === null || str === undefined) return '';
        const s = String(str);
        return s.replace(/&/g, '&amp;').replace(/</g, '&lt;')
            .replace(/>/g, '&gt;').replace(/"/g, '&quot;')
            .replace(/'/g, '&#039;');
    }

    document.getElementById('reconnect-refresh').addEventListener('click', function(e) {
        e.preventDefault();
        document.getElementById('reconnect-banner').style.display = 'none';
        vscode.postMessage({ command: 'refresh' });
    });

    // Signal ready
    vscode.postMessage({ command: 'ready' });
})();
