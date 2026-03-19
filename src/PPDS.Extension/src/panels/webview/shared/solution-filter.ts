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
}

export class SolutionFilter {
    private container: HTMLElement;
    private options: SolutionFilterOptions;
    private solutions: SolutionOption[] = [];
    private selectedId: string | null = null;
    private readonly storageKey: string;

    constructor(container: HTMLElement, options: SolutionFilterOptions) {
        this.container = container;
        this.options = options;
        this.storageKey = options.storageKey ?? 'solutionFilter';

        // Restore persisted selection
        const state = options.getState();
        if (state && typeof state[this.storageKey] === 'string') {
            this.selectedId = state[this.storageKey] as string;
        }

        this.render();
    }

    setSolutions(solutions: SolutionOption[]): void {
        this.solutions = solutions;
        // Validate persisted selection still exists
        if (this.selectedId && !solutions.find(s => s.id === this.selectedId)) {
            this.selectedId = null;
            this.persist();
        }
        this.render();
    }

    getSelectedId(): string | null {
        return this.selectedId;
    }

    private render(): void {
        let html = '<select class="solution-filter-select" title="Filter by solution">';
        html += '<option value="">' + escapeHtml('All Solutions') + '</option>';
        for (const s of this.solutions) {
            const selected = s.id === this.selectedId ? ' selected' : '';
            html += '<option value="' + escapeAttr(s.id) + '"' + selected + '>'
                + escapeHtml(s.friendlyName) + '</option>';
        }
        html += '</select>';
        this.container.innerHTML = html;

        const select = this.container.querySelector('select');
        if (select) {
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
