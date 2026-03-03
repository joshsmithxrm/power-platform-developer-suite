import * as vscode from 'vscode';

/**
 * Tree item representing a tool entry in the Tools view.
 */
export class ToolTreeItem extends vscode.TreeItem {
    constructor(
        label: string,
        public readonly commandId: string,
        iconId: string,
    ) {
        super(label, vscode.TreeItemCollapsibleState.None);
        this.iconPath = new vscode.ThemeIcon(iconId);
        this.command = {
            command: commandId,
            title: label,
        };
    }
}

/**
 * Static tree data provider for the Tools view in the PPDS activity bar.
 *
 * Displays a fixed set of tool entries: Data Explorer, Notebooks, and
 * Solutions. Each item opens the corresponding panel or command when clicked.
 */
export class ToolsTreeDataProvider implements vscode.TreeDataProvider<ToolTreeItem> {
    private static readonly tools: { label: string; commandId: string; icon: string }[] = [
        { label: 'Data Explorer', commandId: 'ppds.openDataExplorer', icon: 'database' },
        { label: 'Notebooks', commandId: 'ppds.openNotebooks', icon: 'notebook' },
        { label: 'Solutions', commandId: 'ppds.openSolutions', icon: 'package' },
    ];

    getTreeItem(element: ToolTreeItem): vscode.TreeItem {
        return element;
    }

    getChildren(element?: ToolTreeItem): ToolTreeItem[] {
        // Tool items have no children — this is a flat list
        if (element) {
            return [];
        }

        return ToolsTreeDataProvider.tools.map(
            t => new ToolTreeItem(t.label, t.commandId, t.icon),
        );
    }
}
