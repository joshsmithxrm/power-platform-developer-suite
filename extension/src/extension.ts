import * as vscode from 'vscode';
import { DaemonClient } from './daemonClient.js';
import { ProfileTreeDataProvider } from './views/profileTreeView.js';
import { ToolsTreeDataProvider } from './views/toolsTreeView.js';
import { registerProfileCommands } from './commands/profileCommands.js';
import { registerEnvironmentConfigCommand } from './commands/environmentConfigCommand.js';
import { registerBrowserCommands } from './commands/browserCommands.js';
import { DataverseNotebookSerializer } from './notebooks/DataverseNotebookSerializer.js';
import { DataverseNotebookController } from './notebooks/DataverseNotebookController.js';
import {
    createNewNotebook, toggleCellLanguage, openCellInDataExplorer,
    exportCellResults, openQueryInNotebook,
} from './commands/notebookCommands.js';
import { DataverseCompletionProvider } from './providers/completionProvider.js';
import { QueryPanel } from './panels/QueryPanel.js';
import { SolutionsPanel } from './panels/SolutionsPanel.js';
import { migrateLegacyState } from './migration/legacyState.js';

let daemonClient: DaemonClient | undefined;
let logChannel: vscode.LogOutputChannel | undefined;

export function activate(context: vscode.ExtensionContext) {
    console.log('Power Platform Developer Suite is now active');

    // ── Legacy State Migration ────────────────────────────────────────
    migrateLegacyState(context);

    // Read extension settings
    const config = vscode.workspace.getConfiguration('ppds');

    // Create structured log channel
    logChannel = vscode.window.createOutputChannel('PPDS', { log: true });
    context.subscriptions.push(logChannel);

    void vscode.commands.executeCommand('setContext', 'ppds.daemonState', 'starting');
    void vscode.commands.executeCommand('setContext', 'ppds.profileCount', 0);

    // Create the daemon client
    daemonClient = new DaemonClient(context.extensionPath, logChannel);
    const client = daemonClient; // Local const for type narrowing in closures
    context.subscriptions.push(client);

    // Auto-start daemon if configured (default: true)
    if (config.get<boolean>('autoStartDaemon', true)) {
        void client.start().catch(err => {
            const msg = err instanceof Error ? err.message : String(err);
            vscode.window.showErrorMessage(`PPDS daemon failed to start: ${msg}`);
        });
    }

    // ── Profile Tree View ────────────────────────────────────────────────
    const profileTreeProvider = new ProfileTreeDataProvider(client, logChannel);
    const profileTreeView = vscode.window.createTreeView('ppds.profiles', {
        treeDataProvider: profileTreeProvider,
        showCollapseAll: false,
    });
    context.subscriptions.push(profileTreeView, profileTreeProvider);

    // ── Tools Tree View ──────────────────────────────────────────────────
    const toolsTreeProvider = new ToolsTreeDataProvider();
    const toolsTreeView = vscode.window.createTreeView('ppds.tools', {
        treeDataProvider: toolsTreeProvider,
        showCollapseAll: false,
    });
    context.subscriptions.push(toolsTreeView, toolsTreeProvider);

    // Sync tools tree disabled state with profile availability
    const refreshToolsState = () => {
        client.authList().then(result => {
            toolsTreeProvider.setHasActiveProfile(result.activeProfile !== null);
        }).catch(() => {
            toolsTreeProvider.setHasActiveProfile(false);
        });
    };
    refreshToolsState();

    // ── Profile Commands ────────────────────────────────────────────────
    registerProfileCommands(context, client, () => { profileTreeProvider.refresh(); refreshToolsState(); });

    // ── Notebook Serializer ───────────────────────────────────────────
    context.subscriptions.push(
        vscode.workspace.registerNotebookSerializer('ppdsnb', new DataverseNotebookSerializer(), {
            transientOutputs: true  // Don't persist cell outputs — re-execute to get results
        })
    );

    // ── Notebook Controller ───────────────────────────────────────────
    const notebookController = new DataverseNotebookController(client);
    context.subscriptions.push(notebookController);

    // ── Environment Config Command ────────────────────────────────────
    registerEnvironmentConfigCommand(context, client, () => profileTreeProvider.refresh());

    // ── Browser Commands ──────────────────────────────────────────────
    registerBrowserCommands(context, client);

    // Register environment selection command for notebooks
    context.subscriptions.push(
        vscode.commands.registerCommand('ppds.selectNotebookEnvironment', () => notebookController.selectEnvironment())
    );

    // ── Notebook Commands ─────────────────────────────────────────────
    context.subscriptions.push(
        vscode.commands.registerCommand('ppds.newNotebook', createNewNotebook),
        vscode.commands.registerCommand('ppds.toggleNotebookCellLanguage', () => toggleCellLanguage(client)),
        vscode.commands.registerCommand('ppds.openCellInDataExplorer', () => {
            openCellInDataExplorer((sql: string) => {
                QueryPanel.show(context.extensionUri, client, sql);
            });
        }),
        vscode.commands.registerCommand('ppds.exportCellResultsCsv', () => exportCellResults(notebookController, 'csv')),
        vscode.commands.registerCommand('ppds.exportCellResultsJson', () => exportCellResults(notebookController, 'json')),
    );

    // ── IntelliSense Completion Provider ──────────────────────────────
    const completionProvider = new DataverseCompletionProvider(client);
    context.subscriptions.push(
        vscode.languages.registerCompletionItemProvider({ language: 'sql' }, completionProvider, ' ', ',', '.'),
        vscode.languages.registerCompletionItemProvider({ language: 'fetchxml' }, completionProvider, ' ', '<', '"'),
    );

    // ── Data Explorer ─────────────────────────────────────────────────
    context.subscriptions.push(
        vscode.commands.registerCommand('ppds.dataExplorer', () => {
            QueryPanel.show(context.extensionUri, client);
        }),
        vscode.commands.registerCommand('ppds.openQueryInNotebook', (sql?: string) => {
            openQueryInNotebook(sql ?? '');
        }),
    );

    const openNotebooksCmd = vscode.commands.registerCommand('ppds.openNotebooks', () => {
        void createNewNotebook();
    });
    context.subscriptions.push(openNotebooksCmd);

    // ── Solutions Panel ─────────────────────────────────────────────────
    context.subscriptions.push(
        vscode.commands.registerCommand('ppds.openSolutions', () => {
            SolutionsPanel.show(context.extensionUri, client);
        }),
    );

    // ── Show Logs ───────────────────────────────────────────────────────
    context.subscriptions.push(
        vscode.commands.registerCommand('ppds.showLogs', () => {
            logChannel?.show();
        }),
    );
}

export function deactivate() {
    // Cleanup is handled by disposables
}
