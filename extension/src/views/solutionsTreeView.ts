import * as vscode from 'vscode';
import type { DaemonClient } from '../daemonClient.js';
import type { SolutionInfoDto, SolutionComponentInfoDto } from '../types.js';

type SolutionTreeElement = SolutionTreeItem | ComponentGroupTreeItem | ComponentTreeItem;

/**
 * Tree item representing a Dataverse solution.
 * Expands to show component type groups.
 */
export class SolutionTreeItem extends vscode.TreeItem {
    constructor(public readonly solution: SolutionInfoDto) {
        super(solution.friendlyName, vscode.TreeItemCollapsibleState.Collapsed);

        this.description = solution.version ?? '';
        this.tooltip = buildSolutionTooltip(solution);
        this.contextValue = 'solution';
        this.iconPath = new vscode.ThemeIcon(
            solution.isManaged ? 'lock' : 'package',
        );
    }
}

/**
 * Tree item representing a group of components of the same type within a solution.
 * E.g., "Entities (5)", "Web Resources (12)".
 */
export class ComponentGroupTreeItem extends vscode.TreeItem {
    constructor(
        public readonly solutionUniqueName: string,
        public readonly componentTypeName: string,
        public readonly components: SolutionComponentInfoDto[],
    ) {
        super(
            `${componentTypeName} (${components.length})`,
            vscode.TreeItemCollapsibleState.Collapsed,
        );
        this.contextValue = 'componentGroup';
        this.iconPath = new vscode.ThemeIcon(getComponentIcon(componentTypeName));
    }
}

/**
 * Tree item representing a single solution component.
 */
export class ComponentTreeItem extends vscode.TreeItem {
    constructor(public readonly component: SolutionComponentInfoDto) {
        super(component.componentTypeName, vscode.TreeItemCollapsibleState.None);

        // Use objectId as the label since componentTypeName is the type, not the name
        this.label = component.objectId;
        this.description = component.isMetadata ? 'metadata' : '';
        this.contextValue = 'component';
        this.iconPath = new vscode.ThemeIcon('symbol-misc');
    }
}

function buildSolutionTooltip(s: SolutionInfoDto): string {
    const lines: string[] = [];
    lines.push(`Name: ${s.friendlyName}`);
    lines.push(`Unique Name: ${s.uniqueName}`);
    if (s.version) lines.push(`Version: ${s.version}`);
    if (s.publisherName) lines.push(`Publisher: ${s.publisherName}`);
    lines.push(`Managed: ${s.isManaged ? 'Yes' : 'No'}`);
    if (s.description) lines.push(`Description: ${s.description}`);
    if (s.modifiedOn) lines.push(`Modified: ${new Date(s.modifiedOn).toLocaleString()}`);
    if (s.installedOn) lines.push(`Installed: ${new Date(s.installedOn).toLocaleString()}`);
    return lines.join('\n');
}

function getComponentIcon(typeName: string): string {
    const lower = typeName.toLowerCase();
    if (lower.includes('entity') || lower.includes('table')) return 'symbol-class';
    if (lower.includes('web resource')) return 'file-code';
    if (lower.includes('plugin') || lower.includes('assembly')) return 'extensions';
    if (lower.includes('workflow') || lower.includes('flow')) return 'git-merge';
    if (lower.includes('role') || lower.includes('security')) return 'shield';
    if (lower.includes('form')) return 'layout';
    if (lower.includes('view') || lower.includes('saved query')) return 'eye';
    if (lower.includes('chart')) return 'graph';
    if (lower.includes('dashboard')) return 'dashboard';
    if (lower.includes('sitemap') || lower.includes('app')) return 'browser';
    if (lower.includes('option') || lower.includes('picklist')) return 'list-flat';
    if (lower.includes('relationship')) return 'git-compare';
    return 'symbol-misc';
}

/**
 * Tree data provider for the Solutions view in the PPDS activity bar.
 *
 * Top level: solutions from the active environment.
 * Second level: component type groups (e.g., "Entities (5)").
 * Third level: individual components within each group.
 */
export class SolutionsTreeDataProvider
    implements vscode.TreeDataProvider<SolutionTreeElement>, vscode.Disposable {

    private readonly _onDidChangeTreeData = new vscode.EventEmitter<SolutionTreeElement | undefined | void>();
    readonly onDidChangeTreeData = this._onDidChangeTreeData.event;

    private includeManaged = false;

    constructor(private readonly daemon: DaemonClient) {}

    refresh(): void {
        this._onDidChangeTreeData.fire();
    }

    toggleManaged(): void {
        this.includeManaged = !this.includeManaged;
        this.refresh();
    }

    getIncludeManaged(): boolean {
        return this.includeManaged;
    }

    getTreeItem(element: SolutionTreeElement): vscode.TreeItem {
        return element;
    }

    async getChildren(element?: SolutionTreeElement): Promise<SolutionTreeElement[]> {
        if (!element) {
            return this.getSolutions();
        }

        if (element instanceof SolutionTreeItem) {
            return this.getComponentGroups(element.solution.uniqueName);
        }

        if (element instanceof ComponentGroupTreeItem) {
            return element.components.map(c => new ComponentTreeItem(c));
        }

        return [];
    }

    private async getSolutions(): Promise<SolutionTreeItem[]> {
        try {
            const result = await this.daemon.solutionsList(undefined, this.includeManaged);
            return result.solutions.map(s => new SolutionTreeItem(s));
        } catch {
            return [];
        }
    }

    private async getComponentGroups(solutionUniqueName: string): Promise<ComponentGroupTreeItem[]> {
        try {
            const result = await this.daemon.solutionsComponents(solutionUniqueName);
            // Group components by type name
            const groups = new Map<string, SolutionComponentInfoDto[]>();
            for (const component of result.components) {
                const typeName = component.componentTypeName;
                const group = groups.get(typeName);
                if (group) {
                    group.push(component);
                } else {
                    groups.set(typeName, [component]);
                }
            }

            // Sort groups by name, return as tree items
            return Array.from(groups.entries())
                .sort(([a], [b]) => a.localeCompare(b))
                .map(([typeName, components]) =>
                    new ComponentGroupTreeItem(solutionUniqueName, typeName, components),
                );
        } catch {
            return [];
        }
    }

    dispose(): void {
        this._onDidChangeTreeData.dispose();
    }
}
