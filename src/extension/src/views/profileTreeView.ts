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
        expandedIds?: ReadonlySet<string>,
    ) {
        const label = profile.name ?? `Profile ${profile.index}`;
        const stableId = `profile://${profile.identity}//${profile.authMethod}//${profile.cloud}`;
        const isExpanded = expandedIds?.has(stableId) ?? false;
        super(label, isExpanded ? vscode.TreeItemCollapsibleState.Expanded : vscode.TreeItemCollapsibleState.Collapsed);

        // Stable ID for VS Code's built-in expansion state persistence
        this.id = stableId;

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

export function getProfileId(profile: ProfileInfo): string {
    return `profile://${profile.identity}//${profile.authMethod}//${profile.cloud}`;
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
        private readonly globalState?: vscode.Memento,
        private readonly stateTracker?: { daemonState: string; profileCount: number },
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
            if (this.stateTracker) {
                this.stateTracker.daemonState = 'ready';
                this.stateTracker.profileCount = result.profiles.length;
            }
            if (result.profiles.length === 0) {
                return [];
            }
            const rawExpandedIds = this.globalState?.get<string[]>('ppds.profiles.expandedIds') ?? [];
            const expandedIds = new Set(rawExpandedIds);
            this.log.info(`[expand-debug] getProfiles: globalState expandedIds=[${rawExpandedIds.join(', ')}]`);
            const items = result.profiles.map(p => {
                const stableId = `profile://${p.identity}//${p.authMethod}//${p.cloud}`;
                const isExpanded = expandedIds.has(stableId);
                this.log.info(`[expand-debug] getProfiles: profile="${p.name ?? p.index}" stableId="${stableId}" isExpanded=${isExpanded} collapsibleState=${isExpanded ? 'Expanded' : 'Collapsed'}`);
                return new ProfileTreeItem(p, expandedIds);
            });

            // Apply user-defined sort order from globalState
            const sortOrder = this.globalState?.get<Record<string, number>>('ppds.profiles.sortOrder');
            if (sortOrder && Object.keys(sortOrder).length > 0) {
                items.sort((a, b) => {
                    const orderA = sortOrder[getProfileId(a.profile)] ?? Number.MAX_SAFE_INTEGER;
                    const orderB = sortOrder[getProfileId(b.profile)] ?? Number.MAX_SAFE_INTEGER;
                    return orderA - orderB;
                });
            }

            return items;
        } catch (err) {
            const msg = err instanceof Error ? err.message : String(err);
            this.log.error(`Failed to list profiles: ${msg}`);
            void vscode.commands.executeCommand('setContext', 'ppds.daemonState', 'error');
            void vscode.commands.executeCommand('setContext', 'ppds.profileCount', 0);
            if (this.stateTracker) {
                this.stateTracker.daemonState = 'error';
                this.stateTracker.profileCount = 0;
            }
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
        const envItems = items.filter(i => i instanceof EnvironmentTreeItem);
        const otherItems = items.filter(i => !(i instanceof EnvironmentTreeItem));
        envItems.sort((a, b) => {
            if (a.isActive && !b.isActive) return -1;
            if (!a.isActive && b.isActive) return 1;
            return a.envDisplayName.localeCompare(b.envDisplayName);
        });

        return [...envItems, ...otherItems];
    }

    /**
     * Swaps the sort position of a profile with its neighbor.
     * Consolidates the sort logic that was duplicated in extension.ts.
     */
    async moveProfile(profileId: string, direction: 'up' | 'down'): Promise<void> {
        const sortOrder = this.globalState?.get<Record<string, number>>('ppds.profiles.sortOrder') ?? {};
        const profiles = await this.daemonClient.authList();
        const sorted = profiles.profiles.map(p => ({ id: getProfileId(p), profile: p }));

        sorted.sort((a, b) => {
            const orderA = sortOrder[a.id] ?? a.profile.index;
            const orderB = sortOrder[b.id] ?? b.profile.index;
            return orderA - orderB;
        });

        const targetIdx = sorted.findIndex(i => i.id === profileId);
        const swapIdx = direction === 'up' ? targetIdx - 1 : targetIdx + 1;

        if (targetIdx < 0 || swapIdx < 0 || swapIdx >= sorted.length) return;

        const newOrder: Record<string, number> = {};
        sorted.forEach((it, idx) => { newOrder[it.id] = idx; });
        newOrder[sorted[targetIdx].id] = swapIdx;
        newOrder[sorted[swapIdx].id] = targetIdx;

        await this.globalState?.update('ppds.profiles.sortOrder', newOrder);
        this.refresh();
    }

    dispose(): void {
        this._onDidChangeTreeData.dispose();
    }
}
