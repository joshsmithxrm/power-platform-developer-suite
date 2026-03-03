import * as vscode from 'vscode';
import { DaemonClient } from './daemonClient.js';
import { ProfileTreeDataProvider } from './views/profileTreeView.js';
import { ToolsTreeDataProvider } from './views/toolsTreeView.js';
import { registerProfileCommands } from './commands/profileCommands.js';
import { registerEnvironmentCommands } from './commands/environmentCommands.js';
import { registerEnvironmentConfigCommand } from './commands/environmentConfigCommand.js';
import { DataverseNotebookSerializer } from './notebooks/DataverseNotebookSerializer.js';

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

    // ── Profile Commands ────────────────────────────────────────────────
    registerProfileCommands(context, daemonClient, () => profileTreeProvider.refresh());

    // ── Environment Commands ─────────────────────────────────────────────
    registerEnvironmentCommands(context, daemonClient, () => profileTreeProvider.refresh());

    // ── Environment Config Command ────────────────────────────────────
    registerEnvironmentConfigCommand(context, daemonClient, () => profileTreeProvider.refresh());

    // ── Notebook Serializer ───────────────────────────────────────────
    context.subscriptions.push(
        vscode.workspace.registerNotebookSerializer('ppdsnb', new DataverseNotebookSerializer(), {
            transientOutputs: true  // Don't persist cell outputs — re-execute to get results
        })
    );

    // ── Placeholder commands for tools tree items ───────────────────────
    // (will be implemented in later tasks)

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
