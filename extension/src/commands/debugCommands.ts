import * as vscode from 'vscode';

import type { DaemonClient } from '../daemonClient.js';
import type { ProfileTreeDataProvider } from '../views/profileTreeView.js';

// ── Diagnostic data shapes ──────────────────────────────────────────────────

interface DaemonStatus {
    state: 'ready' | 'stopped';
    processId: number | null;
}

interface ExtensionState {
    daemonState: string;
    profileCount: number;
}

interface TreeViewState {
    children: Array<{
        label: string;
        id: string | undefined;
        description: string | undefined;
        contextValue: string | undefined;
    }>;
}

interface PanelState {
    queryPanels: number;
    solutionsPanels: number;
}

// ── Pure diagnostic functions ───────────────────────────────────────────────

/**
 * Returns the current daemon connection state and child process ID.
 */
export function getDaemonStatus(daemon: DaemonClient): DaemonStatus {
    return {
        state: daemon.isReady() ? 'ready' : 'stopped',
        processId: daemon.getProcessId(),
    };
}

/**
 * Returns high-level extension state: daemon state and profile count.
 * The state values come from VS Code context keys set by the profile tree.
 */
export function getExtensionState(state: { daemonState: string; profileCount: number }): ExtensionState {
    return {
        daemonState: state.daemonState,
        profileCount: state.profileCount,
    };
}

/**
 * Serializes the profile tree data provider's top-level children to JSON.
 * Calls getChildren(undefined) to get root-level items.
 */
export async function getTreeViewState(provider: ProfileTreeDataProvider): Promise<TreeViewState> {
    const children = await provider.getChildren(undefined);
    return {
        children: children.map(child => ({
            label: typeof child.label === 'string' ? child.label : (child.label as vscode.TreeItemLabel)?.label ?? '',
            id: child.id,
            description: typeof child.description === 'string' ? child.description : undefined,
            contextValue: child.contextValue,
        })),
    };
}

/**
 * Returns the number of open Query and Solutions panels.
 */
export function getPanelState(counts: { queryPanels: number; solutionsPanels: number }): PanelState {
    return {
        queryPanels: counts.queryPanels,
        solutionsPanels: counts.solutionsPanels,
    };
}

// ── Command registration ────────────────────────────────────────────────────

/**
 * Registers ppds.debug.* commands for AI-driven diagnostic inspection.
 *
 * Each command returns structured JSON (not void) so that
 * `vscode.commands.executeCommand()` callers — including MCP tools —
 * can read the result programmatically.
 */
export function registerDebugCommands(
    context: { subscriptions: { push: (d: vscode.Disposable) => void } },
    daemon: DaemonClient,
    profileTreeProvider: ProfileTreeDataProvider,
    extensionState: { daemonState: string; profileCount: number },
    panelCounts: { queryPanelCount: () => number; solutionsPanelCount: () => number },
): void {
    context.subscriptions.push(
        vscode.commands.registerCommand('ppds.debug.daemonStatus', () => {
            return getDaemonStatus(daemon);
        }),
    );

    context.subscriptions.push(
        vscode.commands.registerCommand('ppds.debug.extensionState', () => {
            return getExtensionState(extensionState);
        }),
    );

    context.subscriptions.push(
        vscode.commands.registerCommand('ppds.debug.treeViewState', async () => {
            return getTreeViewState(profileTreeProvider);
        }),
    );

    context.subscriptions.push(
        vscode.commands.registerCommand('ppds.debug.panelState', () => {
            return getPanelState({
                queryPanels: panelCounts.queryPanelCount(),
                solutionsPanels: panelCounts.solutionsPanelCount(),
            });
        }),
    );
}
