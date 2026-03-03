import * as vscode from 'vscode';
import type { DaemonClient } from '../daemonClient.js';
import type { ProfileInfo } from '../types.js';

/**
 * Tree item representing a single authentication profile.
 */
export class ProfileTreeItem extends vscode.TreeItem {
    constructor(
        public readonly profile: ProfileInfo,
    ) {
        const label = profile.name ?? `Profile ${profile.index}`;
        super(label, vscode.TreeItemCollapsibleState.None);

        this.description = profile.identity;
        this.tooltip = buildTooltip(profile);
        this.contextValue = 'profile';

        // Active profile gets a checkmark icon
        if (profile.isActive) {
            this.iconPath = new vscode.ThemeIcon('pass-filled');
        } else {
            this.iconPath = new vscode.ThemeIcon('account');
        }
    }
}

function buildTooltip(profile: ProfileInfo): string {
    const lines: string[] = [];
    lines.push(`Name: ${profile.name ?? '(unnamed)'}`);
    lines.push(`Identity: ${profile.identity}`);
    lines.push(`Auth Method: ${profile.authMethod}`);
    lines.push(`Cloud: ${profile.cloud}`);
    if (profile.environment) {
        lines.push(`Environment: ${profile.environment.displayName}`);
        lines.push(`URL: ${profile.environment.url}`);
    }
    if (profile.isActive) {
        lines.push('Status: Active');
    }
    return lines.join('\n');
}

/**
 * Tree data provider for the Profiles view in the PPDS activity bar.
 *
 * Calls daemonClient.authList() to populate the tree with authentication
 * profiles. The active profile is marked with a checkmark icon.
 */
export class ProfileTreeDataProvider
    implements vscode.TreeDataProvider<ProfileTreeItem>, vscode.Disposable {
    private readonly _onDidChangeTreeData = new vscode.EventEmitter<ProfileTreeItem | undefined | null | void>();
    readonly onDidChangeTreeData = this._onDidChangeTreeData.event;

    constructor(private readonly daemonClient: DaemonClient) {}

    /**
     * Triggers a refresh of the tree view by firing the change event.
     */
    refresh(): void {
        this._onDidChangeTreeData.fire();
    }

    getTreeItem(element: ProfileTreeItem): vscode.TreeItem {
        return element;
    }

    async getChildren(element?: ProfileTreeItem): Promise<ProfileTreeItem[]> {
        // Profile items have no children — this is a flat list
        if (element) {
            return [];
        }

        try {
            const result = await this.daemonClient.authList();
            if (result.profiles.length === 0) {
                return [];
            }
            return result.profiles.map(p => new ProfileTreeItem(p));
        } catch {
            // If the daemon isn't available, show empty tree.
            // The output channel already logs the error in daemonClient.
            return [];
        }
    }

    dispose(): void {
        this._onDidChangeTreeData.dispose();
    }
}
