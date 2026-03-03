import * as vscode from 'vscode';
import { DaemonClient } from './daemonClient.js';
import { ProfileTreeDataProvider } from './views/profileTreeView.js';
import { ToolsTreeDataProvider } from './views/toolsTreeView.js';

let daemonClient: DaemonClient | undefined;

export function activate(context: vscode.ExtensionContext) {
    console.log('Power Platform Developer Suite is now active');

    // Create the daemon client
    daemonClient = new DaemonClient();
    context.subscriptions.push(daemonClient);

    // ── Profile Tree View ────────────────────────────────────────────────
    const profileTreeProvider = new ProfileTreeDataProvider(daemonClient);
    const profileTreeView = vscode.window.createTreeView('ppds.profiles', {
        treeDataProvider: profileTreeProvider,
        showCollapseAll: false,
    });
    context.subscriptions.push(profileTreeView);

    // ── Tools Tree View ──────────────────────────────────────────────────
    const toolsTreeProvider = new ToolsTreeDataProvider();
    const toolsTreeView = vscode.window.createTreeView('ppds.tools', {
        treeDataProvider: toolsTreeProvider,
        showCollapseAll: false,
    });
    context.subscriptions.push(toolsTreeView);

    // ── Commands ─────────────────────────────────────────────────────────

    // Refresh profiles tree
    const refreshProfilesCmd = vscode.commands.registerCommand('ppds.refreshProfiles', () => {
        profileTreeProvider.refresh();
    });
    context.subscriptions.push(refreshProfilesCmd);

    // Select a profile (from context menu or inline button)
    const selectProfileCmd = vscode.commands.registerCommand('ppds.selectProfile', async (item: unknown) => {
        // item comes from the tree view context menu
        const profileItem = item as { profile?: { index: number; name: string | null } } | undefined;
        if (!profileItem?.profile) {
            return;
        }
        try {
            const { index, name } = profileItem.profile;
            await daemonClient!.authSelect(name ? { name } : { index });
            profileTreeProvider.refresh();
            vscode.window.showInformationMessage(`Switched to profile: ${name ?? `Profile ${index}`}`);
        } catch (error) {
            const message = error instanceof Error ? error.message : String(error);
            vscode.window.showErrorMessage(`Failed to select profile: ${message}`);
        }
    });
    context.subscriptions.push(selectProfileCmd);

    // List profiles command (quick pick) — kept for command palette access
    const listProfilesCmd = vscode.commands.registerCommand('ppds.listProfiles', async () => {
        try {
            const result = await daemonClient!.authList();

            if (result.profiles.length === 0) {
                vscode.window.showInformationMessage('No authentication profiles found. Use "ppds auth create" to create one.');
                return;
            }

            // Show profiles in a quick pick
            const items = result.profiles.map(p => ({
                label: p.name ?? `Profile ${p.index}`,
                description: p.identity,
                detail: p.environment ? `${p.environment.displayName} (${p.authMethod})` : p.authMethod,
                picked: p.isActive,
            }));

            const selected = await vscode.window.showQuickPick(items, {
                title: 'Authentication Profiles',
                placeHolder: result.activeProfile ? `Active: ${result.activeProfile}` : 'No active profile',
            });

            if (selected) {
                vscode.window.showInformationMessage(`Selected profile: ${selected.label}`);
            }
        } catch (error) {
            const message = error instanceof Error ? error.message : String(error);
            vscode.window.showErrorMessage(`Failed to list profiles: ${message}`);
        }
    });
    context.subscriptions.push(listProfilesCmd);

    // Placeholder commands for tools tree items (will be implemented in later tasks)
    const openDataExplorerCmd = vscode.commands.registerCommand('ppds.openDataExplorer', () => {
        vscode.window.showInformationMessage('Data Explorer will be available in a future update.');
    });
    context.subscriptions.push(openDataExplorerCmd);

    const openNotebooksCmd = vscode.commands.registerCommand('ppds.openNotebooks', () => {
        vscode.window.showInformationMessage('Notebooks will be available in a future update.');
    });
    context.subscriptions.push(openNotebooksCmd);

    const openSolutionsCmd = vscode.commands.registerCommand('ppds.openSolutions', () => {
        vscode.window.showInformationMessage('Solutions will be available in a future update.');
    });
    context.subscriptions.push(openSolutionsCmd);
}

export function deactivate() {
    // Cleanup is handled by disposables
}
