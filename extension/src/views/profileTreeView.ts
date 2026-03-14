import * as vscode from 'vscode';
import type { DaemonClient } from '../daemonClient.js';
import type { ProfileInfo } from '../types.js';

type ProfileTreeElement = ProfileTreeItem | EnvironmentTreeItem | ManualUrlTreeItem;

/**
 * Tree item representing a single authentication profile.
 * Expandable to show environment children.
 */
export class ProfileTreeItem extends vscode.TreeItem {
    constructor(
        public readonly profile: ProfileInfo,
    ) {
        const label = profile.name ?? `Profile ${profile.index}`;
        super(label, vscode.TreeItemCollapsibleState.Collapsed);

        // Show environment name in the description if set
        if (profile.environment) {
            this.description = profile.environment.displayName;
        } else {
            this.description = '(no environment)';
        }

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

/**
 * Tree item representing an environment under a profile.
 * Click to switch the profile's saved environment.
 */
export class EnvironmentTreeItem extends vscode.TreeItem {
    constructor(
        public readonly profileIndex: number,
        public readonly envUrl: string,
        public readonly envDisplayName: string,
        public readonly isActive: boolean,
    ) {
        super(envDisplayName, vscode.TreeItemCollapsibleState.None);

        this.description = envUrl;
        this.tooltip = `${envDisplayName}\n${envUrl}`;
        this.contextValue = 'environment';

        if (isActive) {
            this.iconPath = new vscode.ThemeIcon('star-full');
        } else {
            this.iconPath = new vscode.ThemeIcon('circle-outline');
            this.command = {
                command: 'ppds.switchProfileEnvironment',
                title: 'Switch Environment',
                arguments: [envUrl, envDisplayName],
            };
        }
    }
}

/**
 * Tree item for "Enter URL manually..." under a profile.
 */
export class ManualUrlTreeItem extends vscode.TreeItem {
    constructor() {
        super('Enter URL manually...', vscode.TreeItemCollapsibleState.None);
        this.iconPath = new vscode.ThemeIcon('link');
        this.contextValue = 'manualUrl';
        this.command = {
            command: 'ppds.switchProfileEnvironmentManual',
            title: 'Enter URL',
        };
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
 * Top level: authentication profiles (expandable).
 * Second level: environments — saved environment (starred) + discovered environments.
 * Clicking a non-active environment switches the profile's default.
 */
export class ProfileTreeDataProvider
    implements vscode.TreeDataProvider<ProfileTreeElement>, vscode.Disposable {
    private readonly _onDidChangeTreeData = new vscode.EventEmitter<ProfileTreeElement | undefined | null | void>();
    readonly onDidChangeTreeData = this._onDidChangeTreeData.event;

    constructor(
        private readonly daemonClient: DaemonClient,
        private readonly log: vscode.LogOutputChannel,
    ) {}

    refresh(): void {
        this._onDidChangeTreeData.fire();
    }

    getTreeItem(element: ProfileTreeElement): vscode.TreeItem {
        return element;
    }

    async getChildren(element?: ProfileTreeElement): Promise<ProfileTreeElement[]> {
        if (!element) {
            return this.getProfiles();
        }

        if (element instanceof ProfileTreeItem) {
            return this.getEnvironments(element.profile);
        }

        return [];
    }

    private async getProfiles(): Promise<ProfileTreeItem[]> {
        try {
            const result = await this.daemonClient.authList();
            void vscode.commands.executeCommand('setContext', 'ppds.daemonState', 'ready');
            void vscode.commands.executeCommand('setContext', 'ppds.profileCount', result.profiles.length);
            if (result.profiles.length === 0) {
                return [];
            }
            return result.profiles.map(p => new ProfileTreeItem(p));
        } catch (err) {
            const msg = err instanceof Error ? err.message : String(err);
            this.log.error(`Failed to list profiles: ${msg}`);
            void vscode.commands.executeCommand('setContext', 'ppds.daemonState', 'error');
            void vscode.commands.executeCommand('setContext', 'ppds.profileCount', 0);
            return [];
        }
    }

    private async getEnvironments(profile: ProfileInfo): Promise<ProfileTreeElement[]> {
        const items: ProfileTreeElement[] = [];
        const savedUrl = profile.environment?.url;

        // Show saved environment first (starred)
        if (profile.environment) {
            items.push(new EnvironmentTreeItem(
                profile.index,
                profile.environment.url,
                profile.environment.displayName,
                true,
            ));
        }

        // Discover available environments (only for active profile — needs auth)
        if (profile.isActive) {
            try {
                const result = await this.daemonClient.envList();
                for (const env of result.environments) {
                    // Skip the saved environment (already shown above)
                    if (savedUrl && env.apiUrl.toLowerCase() === savedUrl.toLowerCase()) {
                        continue;
                    }
                    items.push(new EnvironmentTreeItem(
                        profile.index,
                        env.apiUrl,
                        env.friendlyName,
                        false,
                    ));
                }
            } catch (err) {
                const msg = err instanceof Error ? err.message : String(err);
                this.log.warn(`Failed to discover environments: ${msg}`);
            }

            items.push(new ManualUrlTreeItem());
        }

        if (items.length === 0) {
            // Non-active profile with no saved environment
            items.push(new ManualUrlTreeItem());
        }

        return items;
    }

    dispose(): void {
        this._onDidChangeTreeData.dispose();
    }
}
