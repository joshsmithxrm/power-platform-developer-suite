import { escapeHtml, escapeAttr } from './dom-utils.js';

interface SolutionOption {
    id: string;
    uniqueName: string;
    friendlyName: string;
}

interface SolutionFilterOptions {
    onChange: (solutionId: string | null) => void;
    getState: () => Record<string, unknown> | undefined;
    setState: (state: Record<string, unknown>) => void;
    storageKey?: string;
    /** If provided, the filter defaults to this ID instead of null (All). */
    defaultValue?: string;
    /** If true, the "(No filter)" option is not shown. */
    hideAll?: boolean;
}

export class SolutionFilter {
    private container: HTMLElement;
    private options: SolutionFilterOptions;
    private solutions: SolutionOption[] = [];
    private selectedId: string | null;
    private storageKey: string;

    constructor(container: HTMLElement, options: SolutionFilterOptions) {
        this.container = container;
        this.options = options;
        this.storageKey = options.storageKey ?? 'solutionFilter';

        // Restore persisted selection; fall back to defaultValue if nothing persisted
        const state = options.getState();
        if (state && typeof state[this.storageKey] === 'string') {
            this.selectedId = (state[this.storageKey] as string) || null;
        } else {
            this.selectedId = options.defaultValue ?? null;
        }

        this.render();
    }

    setSolutions(solutions: SolutionOption[]): void {
        this.solutions = solutions;
        // Validate persisted selection still exists.
        // For a defaultValue that is a GUID (e.g. Default Solution), it may not be in
        // the list yet — keep it selected until we confirm it isn't present.
        if (this.selectedId && !solutions.find(s => s.id === this.selectedId)) {
            // If it was the configured default, keep it — the list may not include it.
            if (this.selectedId !== this.options.defaultValue) {
                this.selectedId = this.options.defaultValue ?? null;
                this.persist();
            }
        }
        this.render();
    }

    getSelectedId(): string | null {
        return this.selectedId;
    }

    setSelectedId(id: string | null): void {
        if (id === this.selectedId) return;
        this.selectedId = id;
        this.persist();
        this.render();
    }

    /**
     * Update the storage key suffix for per-environment persistence (CR-09).
     * Call this when the environment changes, before setSolutions().
     * @param envKey A stable per-environment discriminator (e.g. env URL).
     */
    setEnvironmentKey(envKey: string): void {
        // Build a composite storage key so each environment has its own persisted value
        const baseKey = this.options.storageKey ?? 'solutionFilter';
        const composite = baseKey + '.env.' + envKey;
        this.storageKey = composite;

        // Restore per-env persisted selection
        const state = this.options.getState();
        if (state && typeof state[composite] === 'string') {
            this.selectedId = (state[composite]) || null;
        } else {
            // New environment: start from default
            this.selectedId = this.options.defaultValue ?? null;
        }
    }

    private render(): void {
        let html = '<select class="solution-filter-select" title="Filter by solution">';
        if (!this.options.hideAll) {
            html += '<option value="">' + escapeHtml('(No filter)') + '</option>';
        }
        for (const s of this.solutions) {
            const selected = s.id === this.selectedId ? ' selected' : '';
            html += '<option value="' + escapeAttr(s.id) + '"' + selected + '>'
                + escapeHtml(s.friendlyName) + '</option>';
        }
        html += '</select>';
        this.container.innerHTML = html;

        const select = this.container.querySelector('select');
        if (select) {
            // If the currently selected ID isn't in the list, add a placeholder option
            if (this.selectedId && !this.solutions.find(s => s.id === this.selectedId)) {
                const opt = document.createElement('option');
                opt.value = this.selectedId;
                opt.selected = true;
                opt.textContent = this.selectedId === 'fd140aaf-4df4-11dd-bd17-0019b9312238'
                    ? 'Default Solution'
                    : this.selectedId;
                select.insertBefore(opt, select.firstChild);
            }
            select.addEventListener('change', () => {
                this.selectedId = select.value || null;
                this.persist();
                this.options.onChange(this.selectedId);
            });
        }
    }

    private persist(): void {
        const state = this.options.getState() ?? {};
        state[this.storageKey] = this.selectedId ?? '';
        this.options.setState(state);
    }
}
