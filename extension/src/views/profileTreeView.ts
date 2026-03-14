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

        if (profile.environment) {
            this.description = profile.environment.displayName;
        } else {
            this.description = '(no environment)';
        }

        this.tooltip = buildTooltip(profile);
        this.contextValue = 'profile';

        if (profile.isActive) {
            this.iconPath = new vscode.ThemeIcon('pass-filled');
        } else {
            this.iconPath = new vscode.ThemeIcon('account');
        }
    }
}

/**
 * Tree item representing an environment under a profile.
 * Right-click for context menu: Set as Default, Open Data Explorer, Open Maker, etc.
 * Click non-active env to set as default.
 */
export class EnvironmentTreeItem extends vscode.TreeItem {
    constructor(
        public readonly envUrl: string,
        public readonly envDisplayName: string,
        public readonly envEnvironmentId: string | null,
        public readonly isActive: boolean,
        public readonly source: string,
    ) {
        super(envDisplayName, vscode.TreeItemCollapsibleState.None);

        this.description = envUrl;
        this.tooltip = `${envDisplayName}\n${envUrl}${this.isActive ? '\n(default)' : ''}`;
        // Use 'environment' for all — package.json handles remove visibility
        this.contextValue = 'environment';

        if (isActive) {
            this.iconPath = new vscode.ThemeIcon('star-full');
        } else {
            this.iconPath = new vscode.ThemeIcon('circle-outline');
            // Pass plain data to avoid circular reference (this.command.arguments[0] → this)
            this.command = {
                command: 'ppds.setDefaultEnvironment',
                title: 'Set as Default',
                arguments: [{ envUrl, envDisplayName }],
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
 * Second level: environments — saved (starred), discovered, and configured.
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
        const savedUrl = profile.environment?.url?.replace(/\/+$/, '').toLowerCase();

        // For the active profile, env/list returns discovered + configured (merged by daemon).
        // For non-active profiles, we can only show the saved environment.
        if (profile.isActive) {
            try {
                const result = await this.daemonClient.envList();
                for (const env of result.environments) {
                    // Only show discovered environments in the tree — configured-only
                    // entries from environments.json may be from other tenants/profiles
                    if (env.source === 'configured') continue;

                    const normalizedUrl = env.apiUrl.replace(/\/+$/, '').toLowerCase();
                    const isDefault = savedUrl != null && normalizedUrl === savedUrl;
                    items.push(new EnvironmentTreeItem(
                        env.apiUrl,
                        env.friendlyName,
                        env.environmentId,
                        isDefault,
                        env.source,
                    ));
                }

                // If the saved env wasn't in the list (shouldn't happen but defensive), add it
                if (savedUrl && !items.some(i => i instanceof EnvironmentTreeItem && i.isActive)) {
                    items.unshift(new EnvironmentTreeItem(
                        profile.environment!.url,
                        profile.environment!.displayName,
                        profile.environment!.environmentId ?? null,
                        true,
                        'saved',
                    ));
                }
            } catch (err) {
                const msg = err instanceof Error ? err.message : String(err);
                this.log.warn(`Failed to list environments: ${msg}`);

                // Fallback: show saved environment only
                if (profile.environment) {
                    items.push(new EnvironmentTreeItem(
                        profile.environment.url,
                        profile.environment.displayName,
                        profile.environment.environmentId ?? null,
                        true,
                        'saved',
                    ));
                }
            }

            items.push(new ManualUrlTreeItem());
        } else {
            // Non-active profile: show saved environment only
            if (profile.environment) {
                items.push(new EnvironmentTreeItem(
                    profile.environment.url,
                    profile.environment.displayName,
                    profile.environment.environmentId ?? null,
                    true,
                    'saved',
                ));
            }
            items.push(new ManualUrlTreeItem());
        }

        // Sort: active/default first, then alphabetical
        const envItems = items.filter(i => i instanceof EnvironmentTreeItem) as EnvironmentTreeItem[];
        const otherItems = items.filter(i => !(i instanceof EnvironmentTreeItem));
        envItems.sort((a, b) => {
            if (a.isActive && !b.isActive) return -1;
            if (!a.isActive && b.isActive) return 1;
            return a.envDisplayName.localeCompare(b.envDisplayName);
        });

        return [...envItems, ...otherItems];
    }

    dispose(): void {
        this._onDidChangeTreeData.dispose();
    }
}
