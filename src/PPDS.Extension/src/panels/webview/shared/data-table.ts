import { escapeHtml, escapeAttr } from './dom-utils.js';

/** Column definition for the DataTable. */
export interface DataTableColumn<T> {
    key: string;
    label: string;
    render: (item: T) => string;
    className?: string;
    sortable?: boolean;
}

export type SortDirection = 'asc' | 'desc';

export interface DataTableOptions<T> {
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
}

export class DataTable<T> {
    private items: T[] = [];
    private sortKey: string;
    private sortDirection: SortDirection;
    private selectedId: string | null = null;
    private readonly opts: DataTableOptions<T>;

    constructor(opts: DataTableOptions<T>) {
        this.opts = opts;
        this.sortKey = opts.defaultSortKey ?? opts.columns[0]?.key ?? '';
        this.sortDirection = opts.defaultSortDirection ?? 'desc';

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
    }

    getSelectedId(): string | null {
        return this.selectedId;
    }

    private render(): void {
        const { container, columns, getRowId, emptyMessage, statusEl, formatStatus } = this.opts;

        if (this.items.length === 0) {
            container.innerHTML = '<div class="empty-state">' + escapeHtml(emptyMessage ?? 'No data') + '</div>';
            if (statusEl) statusEl.textContent = emptyMessage ?? 'No data';
            return;
        }

        const sorted = this.sortItems();
        const tableClass = this.opts.tableClass ?? 'data-table';
        const indicator = (key: string): string =>
            this.sortKey === key ? (this.sortDirection === 'asc' ? ' \u25B2' : ' \u25BC') : '';

        let html = '<table class="' + escapeAttr(tableClass) + '">';
        html += '<thead><tr>';
        for (const col of columns) {
            const sortable = col.sortable !== false;
            html += '<th' +
                (sortable ? ' class="sortable" data-col="' + escapeAttr(col.key) + '"' : '') +
                (col.className ? ' style="width:' + escapeAttr(col.className) + '"' : '') +
                '>' + escapeHtml(col.label) + (sortable ? indicator(col.key) : '') + '</th>';
        }
        html += '</tr></thead><tbody>';

        for (const item of sorted) {
            const id = getRowId(item);
            html += '<tr class="data-table-row" data-id="' + escapeAttr(id) + '" tabindex="0">';
            for (const col of columns) {
                html += '<td>' + col.render(item) + '</td>';
            }
            html += '</tr>';
        }
        html += '</tbody></table>';
        container.innerHTML = html;

        // Sort click handlers
        container.querySelectorAll<HTMLElement>('.sortable').forEach(th => {
            th.addEventListener('click', () => {
                const col = th.dataset.col!;
                if (this.sortKey === col) {
                    this.sortDirection = this.sortDirection === 'asc' ? 'desc' : 'asc';
                } else {
                    this.sortKey = col;
                    this.sortDirection = 'asc';
                }
                this.render();
            });
        });

        // Row click handlers
        container.querySelectorAll<HTMLElement>('.data-table-row').forEach(row => {
            row.addEventListener('click', () => {
                this.selectedId = row.dataset.id!;
                container.querySelectorAll<HTMLElement>('.data-table-row.selected')
                    .forEach(r => r.classList.remove('selected'));
                row.classList.add('selected');
                const item = this.items.find(i => getRowId(i) === this.selectedId);
                if (item && this.opts.onRowClick) this.opts.onRowClick(item);
            });
        });

        // Status bar
        if (statusEl && formatStatus) {
            statusEl.textContent = formatStatus(this.items);
        }
    }

    private sortItems(): T[] {
        const col = this.opts.columns.find(c => c.key === this.sortKey);
        if (!col) return [...this.items];

        const sorted = [...this.items];
        sorted.sort((a, b) => {
            const aHtml = col.render(a);
            const bHtml = col.render(b);
            const aText = aHtml.replace(/<[^>]*>/g, '');
            const bText = bHtml.replace(/<[^>]*>/g, '');
            const cmp = aText.localeCompare(bText, undefined, { numeric: true });
            return this.sortDirection === 'asc' ? cmp : -cmp;
        });
        return sorted;
    }
}
