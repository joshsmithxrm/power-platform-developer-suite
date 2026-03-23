import { escapeHtml, escapeAttr } from './dom-utils.js';
import { SelectionManager } from './selection-manager.js';

/** Column definition for the DataTable. */
export interface DataTableColumn<T> {
    key: string;
    label: string;
    render: (item: T) => string;
    /** Raw value used for sorting. When provided, sort uses this instead of stripping HTML from render(). */
    sortValue?: (item: T) => string | number;
    className?: string;
    sortable?: boolean;
}

type SortDirection = 'asc' | 'desc' | 'none';

interface DataTableOptions<T> {
    container: HTMLElement;
    columns: DataTableColumn<T>[];
    getRowId: (item: T) => string;
    onRowClick?: (item: T) => void;
    defaultSortKey?: string;
    defaultSortDirection?: SortDirection;
    tableClass?: string;
    statusEl?: HTMLElement;
    formatStatus?: (items: T[]) => string;
    emptyMessage?: string;
    /** Called when the user copies a cell selection as TSV. Webviews must route through postMessage. */
    onCopy?: (text: string) => void;
    /** Approximate row height in pixels for virtual scroll (default: 28) */
    rowHeight?: number;
    /** Number of buffer rows above and below the visible area (default: 20) */
    bufferRows?: number;
}

/** Strip HTML tags to extract plain text (for tooltips and legacy sort). */
function stripHtml(html: string): string {
    return html.replace(/<[^>]*>/g, '');
}

export class DataTable<T> {
    private items: T[] = [];
    private sortedItems: T[] = [];
    private sortKey: string;
    private sortDirection: SortDirection;
    private selectedId: string | null = null;
    private readonly opts: DataTableOptions<T>;
    private selectionManager: SelectionManager | null = null;

    // Virtual scroll state
    private readonly rowHeight: number;
    private readonly bufferRows: number;
    private scrollContainer: HTMLElement | null = null;
    private spacer: HTMLElement | null = null;
    private tbody: HTMLElement | null = null;
    private visibleStart = 0;
    private visibleEnd = 0;
    private scrollRafId = 0;

    constructor(opts: DataTableOptions<T>) {
        this.opts = opts;
        this.sortKey = opts.defaultSortKey ?? opts.columns[0]?.key ?? '';
        // Map legacy 'desc'/'asc' default to our three-state (for backward compat, default 'desc' means active desc)
        this.sortDirection = opts.defaultSortDirection === 'asc' ? 'asc' : opts.defaultSortDirection === 'desc' ? 'desc' : 'none';
        this.rowHeight = opts.rowHeight ?? 28;
        this.bufferRows = opts.bufferRows ?? 20;

        // Keyboard: Enter on row (delegated, registered once)
        opts.container.addEventListener('keydown', (e) => {
            if (e.key !== 'Enter') return;
            const row = (e.target as HTMLElement).closest<HTMLElement>('.data-table-row');
            if (row) { e.preventDefault(); row.click(); }
        });
    }

    setItems(items: T[]): void {
        this.items = items;
        this.selectedId = null;
        this.render();
    }

    getItems(): T[] {
        return this.items;
    }

    clearSelection(): void {
        this.selectedId = null;
        this.opts.container
            .querySelectorAll<HTMLElement>('.data-table-row.selected')
            .forEach(r => r.classList.remove('selected'));
        if (this.selectionManager) {
            this.selectionManager.clearSelection();
        }
    }

    getSelectedId(): string | null {
        return this.selectedId;
    }

    private render(): void {
        const { container, columns, emptyMessage, statusEl, formatStatus } = this.opts;

        // Dispose previous selection manager
        if (this.selectionManager) {
            this.selectionManager.dispose();
            this.selectionManager = null;
        }

        if (this.items.length === 0) {
            container.innerHTML = '<div class="empty-state">' + escapeHtml(emptyMessage ?? 'No data') + '</div>';
            if (statusEl) statusEl.textContent = emptyMessage ?? 'No data';
            this.scrollContainer = null;
            this.spacer = null;
            this.tbody = null;
            return;
        }

        this.sortedItems = this.sortItems();
        const tableClass = this.opts.tableClass ?? 'data-table';

        // Build header
        let headerHtml = '<thead><tr>';
        for (let ci = 0; ci < columns.length; ci++) {
            const col = columns[ci];
            const sortable = col.sortable !== false;
            const classes: string[] = [];
            if (sortable) classes.push('sortable');

            let attrs = '';
            if (sortable) attrs += ' data-col="' + escapeAttr(col.key) + '"';
            if (col.className) attrs += ' style="width:' + escapeAttr(col.className) + '"';
            if (classes.length > 0) attrs += ' class="' + classes.join(' ') + '"';

            const ind = sortable ? this.sortIndicator(col.key) : '';
            headerHtml += '<th' + attrs + '>' + escapeHtml(col.label) + ind + '</th>';
        }
        headerHtml += '</tr></thead>';

        // Build scroll structure for virtual scrolling
        container.innerHTML = '';
        const wrapper = document.createElement('div');
        wrapper.className = 'data-table-scroll';
        wrapper.style.overflow = 'auto';
        wrapper.style.flex = '1';
        wrapper.style.minHeight = '0';

        const table = document.createElement('table');
        table.className = tableClass;
        table.innerHTML = headerHtml;

        const tbody = document.createElement('tbody');
        const spacer = document.createElement('tr');
        spacer.className = 'data-table-spacer-top';
        spacer.innerHTML = '<td colspan="' + columns.length + '" style="padding:0;border:none;"></td>';

        const spacerBottom = document.createElement('tr');
        spacerBottom.className = 'data-table-spacer-bottom';
        spacerBottom.innerHTML = '<td colspan="' + columns.length + '" style="padding:0;border:none;"></td>';

        tbody.appendChild(spacer);
        // Visible rows will be inserted between spacers
        tbody.appendChild(spacerBottom);

        table.appendChild(tbody);
        wrapper.appendChild(table);
        container.appendChild(wrapper);

        this.scrollContainer = wrapper;
        this.spacer = spacer;
        this.tbody = tbody as unknown as HTMLElement;

        // Initial render of visible rows
        this.visibleStart = 0;
        this.visibleEnd = 0;
        this.updateVisibleRows();

        // Scroll handler with requestAnimationFrame throttling
        wrapper.addEventListener('scroll', () => {
            if (this.scrollRafId) return;
            this.scrollRafId = requestAnimationFrame(() => {
                this.scrollRafId = 0;
                this.updateVisibleRows();
            });
        });

        // Sort click handlers (delegated on the table)
        table.addEventListener('click', (e) => {
            const th = (e.target as HTMLElement).closest<HTMLElement>('th.sortable');
            if (!th) return;
            const colKey = th.dataset['col'];
            if (!colKey) return;
            this.toggleSort(colKey);
        });

        // Row click handlers (delegated on tbody)
        tbody.addEventListener('click', (e) => {
            const target = e.target as HTMLElement;
            const row = target.closest<HTMLElement>('.data-table-row');
            if (!row) return;
            const id = row.dataset['id'];
            if (!id) return;

            this.selectedId = id;
            wrapper.querySelectorAll<HTMLElement>('.data-table-row.selected')
                .forEach(r => r.classList.remove('selected'));
            row.classList.add('selected');

            const item = this.sortedItems.find(i => this.opts.getRowId(i) === id);
            if (item && this.opts.onRowClick) this.opts.onRowClick(item);
        });

        // Wire up SelectionManager if onCopy is provided
        if (this.opts.onCopy) {
            this.selectionManager = new SelectionManager({
                container: wrapper,
                getColumnCount: () => this.opts.columns.length,
                getRowCount: () => this.sortedItems.length,
                getCellText: (row, col) => {
                    const item = this.sortedItems[row];
                    if (!item) return '';
                    const c = this.opts.columns[col];
                    if (!c) return '';
                    return stripHtml(c.render(item));
                },
                getHeaderLabel: (col) => this.opts.columns[col]?.label ?? '',
                onCopy: this.opts.onCopy,
            });
        }

        // Status bar
        if (statusEl && formatStatus) {
            statusEl.textContent = formatStatus(this.items);
        }
    }

    private updateVisibleRows(): void {
        if (!this.scrollContainer || !this.tbody || !this.spacer) return;

        const { columns } = this.opts;
        const totalRows = this.sortedItems.length;
        const scrollTop = this.scrollContainer.scrollTop;
        const clientHeight = this.scrollContainer.clientHeight;

        // Account for thead height
        const thead = this.scrollContainer.querySelector('thead');
        const theadHeight = thead ? thead.offsetHeight : 0;
        const adjustedScrollTop = Math.max(0, scrollTop - theadHeight);

        const firstVisible = Math.floor(adjustedScrollTop / this.rowHeight);
        const lastVisible = Math.min(
            totalRows - 1,
            Math.ceil((adjustedScrollTop + clientHeight) / this.rowHeight)
        );

        const start = Math.max(0, firstVisible - this.bufferRows);
        const end = Math.min(totalRows - 1, lastVisible + this.bufferRows);

        // Skip if the range hasn't changed
        if (start === this.visibleStart && end === this.visibleEnd) return;

        this.visibleStart = start;
        this.visibleEnd = end;

        // Remove existing data rows (keep spacers)
        const existingRows = this.tbody.querySelectorAll('.data-table-row');
        existingRows.forEach(r => r.remove());

        // Set top spacer height
        const topSpacerTd = this.spacer.querySelector('td');
        if (topSpacerTd) {
            topSpacerTd.style.height = (start * this.rowHeight) + 'px';
        }

        // Set bottom spacer height
        const bottomSpacer = this.tbody.querySelector('.data-table-spacer-bottom td');
        if (bottomSpacer) {
            const bottomHeight = Math.max(0, (totalRows - end - 1) * this.rowHeight);
            (bottomSpacer as HTMLElement).style.height = bottomHeight + 'px';
        }

        // Build and insert visible rows
        const fragment = document.createDocumentFragment();
        for (let i = start; i <= end; i++) {
            const item = this.sortedItems[i];
            const id = this.opts.getRowId(item);
            const tr = document.createElement('tr');
            tr.className = 'data-table-row';
            if (id === this.selectedId) tr.className += ' selected';
            tr.dataset['id'] = id;
            tr.tabIndex = 0;

            for (let ci = 0; ci < columns.length; ci++) {
                const col = columns[ci];
                const td = document.createElement('td');
                td.dataset['row'] = String(i);
                td.dataset['col'] = String(ci);

                const rendered = col.render(item);
                td.innerHTML = rendered;

                // Cell tooltip (CC-08): set title to plain text content
                const plainText = stripHtml(rendered);
                if (plainText) {
                    td.title = plainText;
                }

                tr.appendChild(td);
            }

            fragment.appendChild(tr);
        }

        // Insert before bottom spacer
        const bottomSpacerRow = this.tbody.querySelector('.data-table-spacer-bottom');
        if (bottomSpacerRow) {
            this.tbody.insertBefore(fragment, bottomSpacerRow);
        } else {
            this.tbody.appendChild(fragment);
        }

        // Refresh cell selection visuals if active
        if (this.selectionManager) {
            this.selectionManager.refreshVisuals();
        }
    }

    private sortIndicator(key: string): string {
        if (this.sortKey !== key || this.sortDirection === 'none') return '';
        return this.sortDirection === 'asc' ? ' \u25B2' : ' \u25BC';
    }

    private toggleSort(colKey: string): void {
        if (this.sortKey === colKey) {
            // Three-state toggle: asc -> desc -> none
            if (this.sortDirection === 'asc') {
                this.sortDirection = 'desc';
            } else if (this.sortDirection === 'desc') {
                this.sortDirection = 'none';
            } else {
                this.sortDirection = 'asc';
            }
        } else {
            this.sortKey = colKey;
            this.sortDirection = 'asc';
        }
        this.render();
    }

    private sortItems(): T[] {
        if (this.sortDirection === 'none') return [...this.items];

        const col = this.opts.columns.find(c => c.key === this.sortKey);
        if (!col) return [...this.items];

        const sorted = [...this.items];
        const hasSortValue = typeof col.sortValue === 'function';

        sorted.sort((a, b) => {
            let cmp: number;
            if (hasSortValue) {
                const aVal = col.sortValue!(a);
                const bVal = col.sortValue!(b);
                if (typeof aVal === 'number' && typeof bVal === 'number') {
                    cmp = aVal - bVal;
                } else {
                    cmp = String(aVal).localeCompare(String(bVal), undefined, { numeric: true });
                }
            } else {
                const aHtml = col.render(a);
                const bHtml = col.render(b);
                const aText = stripHtml(aHtml);
                const bText = stripHtml(bHtml);
                cmp = aText.localeCompare(bText, undefined, { numeric: true });
            }
            return this.sortDirection === 'asc' ? cmp : -cmp;
        });
        return sorted;
    }
}

// ── Shared utility functions ─────────────────────────────────────────────────

/**
 * Format a status count string for the "X of Y" pattern (Constitution I4 compliance).
 *
 * Returns:
 * - "50 import jobs" when filtered === total
 * - "12 of 50 import jobs (filtered)" when search active
 * - "50 of 251 import jobs (showing first 50)" when truncated
 */
export function formatStatusCount(
    filtered: number,
    total: number,
    noun: string,
    filters?: string[],
): string {
    // Pluralize: add 's' unless exactly 1
    const pluralNoun = total === 1 ? noun : noun + 's';
    const filteredPluralNoun = filtered === 1 ? noun : noun + 's';

    if (filtered === total) {
        return filtered + ' ' + filteredPluralNoun;
    }

    // Check if filters include a truncation indicator
    if (filters && filters.some(f => f.toLowerCase().includes('truncat') || f.toLowerCase().includes('first'))) {
        return filtered + ' of ' + total + ' ' + pluralNoun + ' (showing first ' + filtered + ')';
    }

    return filtered + ' of ' + total + ' ' + pluralNoun + ' (filtered)';
}
