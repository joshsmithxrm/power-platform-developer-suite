import * as vscode from 'vscode';

/**
 * Tree item representing a tool entry in the Tools view.
 */
export class ToolTreeItem extends vscode.TreeItem {
    constructor(
        label: string,
        public readonly commandId: string,
        iconId: string,
        disabled: boolean,
    ) {
        super(label, vscode.TreeItemCollapsibleState.None);
        this.iconPath = new vscode.ThemeIcon(iconId);
        if (disabled) {
            this.description = '(no profile)';
            // No command — clicking does nothing when disabled
        } else {
            this.command = {
                command: commandId,
                title: label,
            };
        }
    }
}

/**
 * Static tree data provider for the Tools view in the PPDS activity bar.
 *
 * Displays a fixed set of tool entries: Data Explorer, Notebooks, and
 * Solutions. Each item opens the corresponding panel or command when clicked.
 * Items are disabled (grayed out) when no profile is active.
 */
export class ToolsTreeDataProvider
    implements vscode.TreeDataProvider<ToolTreeItem>, vscode.Disposable {
    private static readonly tools: { label: string; commandId: string; icon: string }[] = [
        { label: 'Data Explorer', commandId: 'ppds.dataExplorer', icon: 'database' },
        { label: 'Notebooks', commandId: 'ppds.openNotebooks', icon: 'notebook' },
        { label: 'Solutions', commandId: 'ppds.openSolutions', icon: 'package' },
    ];

    private readonly _onDidChangeTreeData = new vscode.EventEmitter<ToolTreeItem | undefined | void>();
    readonly onDidChangeTreeData = this._onDidChangeTreeData.event;

    private hasActiveProfile = false;

    /** Update the disabled state based on whether a profile is active. */
    setHasActiveProfile(hasProfile: boolean): void {
        if (this.hasActiveProfile !== hasProfile) {
            this.hasActiveProfile = hasProfile;
            this._onDidChangeTreeData.fire();
        }
    }

    getTreeItem(element: ToolTreeItem): vscode.TreeItem {
        return element;
    }

    getChildren(element?: ToolTreeItem): ToolTreeItem[] {
        // Tool items have no children — this is a flat list
        if (element) {
            return [];
        }

        return ToolsTreeDataProvider.tools.map(
            t => new ToolTreeItem(t.label, t.commandId, t.icon, !this.hasActiveProfile),
        );
    }

    dispose(): void {
        this._onDidChangeTreeData.dispose();
    }
}
