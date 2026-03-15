/**
 * Shared filter bar utility for webview panels.
 * Provides debounced text filtering with result count display.
 */

export interface FilterBarOptions<T> {
    /** The text input element */
    input: HTMLInputElement;
    /** The count display element */
    countEl: HTMLElement;
    /** Debounce delay in ms (default: 150) */
    debounceMs?: number;
    /** Extract searchable strings from an item */
    getSearchableText: (item: T) => string[];
    /** Called with filtered results */
    onFilter: (filtered: T[], total: number) => void;
    /** Label for count display (default: 'rows') */
    itemLabel?: string;
}

export class FilterBar<T> {
    private items: T[] = [];
    private debounceTimer: ReturnType<typeof setTimeout> | null = null;
    private readonly opts: Required<Pick<FilterBarOptions<T>, 'debounceMs' | 'itemLabel'>> & FilterBarOptions<T>;

    constructor(options: FilterBarOptions<T>) {
        this.opts = {
            debounceMs: 150,
            itemLabel: 'rows',
            ...options,
        };
        this.opts.input.addEventListener('input', () => this.onInput());
    }

    /** Update the data set and re-apply current filter */
    setItems(items: T[]): void {
        this.items = items;
        this.apply();
    }

    /** Clear the filter input and reset */
    clear(): void {
        this.opts.input.value = '';
        this.opts.countEl.textContent = '';
        this.opts.onFilter(this.items, this.items.length);
    }

    /** Focus the filter input */
    focus(): void {
        this.opts.input.focus();
    }

    private onInput(): void {
        if (this.debounceTimer) clearTimeout(this.debounceTimer);
        this.debounceTimer = setTimeout(() => this.apply(), this.opts.debounceMs);
    }

    private apply(): void {
        const term = this.opts.input.value.toLowerCase();
        if (!term) {
            this.opts.countEl.textContent = '';
            this.opts.onFilter(this.items, this.items.length);
            return;
        }
        const filtered = this.items.filter(item =>
            this.opts.getSearchableText(item).some(text => text.toLowerCase().includes(term))
        );
        this.opts.countEl.textContent = `Showing ${filtered.length} of ${this.items.length} ${this.opts.itemLabel}`;
        this.opts.onFilter(filtered, this.items.length);
    }
}
