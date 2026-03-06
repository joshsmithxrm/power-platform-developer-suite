import * as vscode from 'vscode';
import { DaemonClient } from './daemonClient.js';
import { ProfileTreeDataProvider } from './views/profileTreeView.js';
import { ToolsTreeDataProvider } from './views/toolsTreeView.js';
import { registerProfileCommands } from './commands/profileCommands.js';
import { registerEnvironmentCommands } from './commands/environmentCommands.js';
import { registerEnvironmentConfigCommand } from './commands/environmentConfigCommand.js';
import { DataverseNotebookSerializer } from './notebooks/DataverseNotebookSerializer.js';
import { DataverseNotebookController } from './notebooks/DataverseNotebookController.js';
import {
    createNewNotebook, toggleCellLanguage, openCellInDataExplorer,
    exportCellResults, openQueryInNotebook,
} from './commands/notebookCommands.js';
import { DataverseCompletionProvider } from './providers/completionProvider.js';
import { QueryPanel } from './panels/QueryPanel.js';
import { SolutionsTreeDataProvider } from './views/solutionsTreeView.js';

let daemonClient: DaemonClient | undefined;
let logChannel: vscode.LogOutputChannel | undefined;

export function activate(context: vscode.ExtensionContext) {
    console.log('Power Platform Developer Suite is now active');

    // Read extension settings
    const config = vscode.workspace.getConfiguration('ppds');

    // Create structured log channel
    logChannel = vscode.window.createOutputChannel('PPDS', { log: true });
    context.subscriptions.push(logChannel);

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
    const profileTreeProvider = new ProfileTreeDataProvider(client);
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
    // Declared before environment commands so the onEnvironmentChanged callback can sync it
    const notebookController = new DataverseNotebookController(client);
    context.subscriptions.push(notebookController);

    // ── Environment Commands ─────────────────────────────────────────────
    const envStatusBar = registerEnvironmentCommands(context, client, () => {
        profileTreeProvider.refresh();
        refreshToolsState();
        // Sync notebook controller with new environment
        client.envWho().then(who => {
            if (who.url) notebookController.updateEnvironment(who.url);
        }).catch(() => { /* environment may not be selected yet */ });
    });

    // Respect showEnvironmentInStatusBar setting
    if (!config.get<boolean>('showEnvironmentInStatusBar', true)) {
        envStatusBar.hide();
    }

    // ── Environment Config Command ────────────────────────────────────
    registerEnvironmentConfigCommand(context, client, () => profileTreeProvider.refresh());

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
    // Register IntelliSense for both SQL and FetchXML.
    // The language is forwarded to the daemon which routes to the appropriate engine.
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

    // ── Solutions Tree View ─────────────────────────────────────────────
    const solutionsTreeProvider = new SolutionsTreeDataProvider(client);
    const solutionsTreeView = vscode.window.createTreeView('ppds.solutions', {
        treeDataProvider: solutionsTreeProvider,
        showCollapseAll: true,
    });
    context.subscriptions.push(solutionsTreeView, solutionsTreeProvider);

    context.subscriptions.push(
        vscode.commands.registerCommand('ppds.openSolutions', () => {
            void vscode.commands.executeCommand('ppds.solutions.focus');
        }),
        vscode.commands.registerCommand('ppds.refreshSolutions', () => {
            solutionsTreeProvider.refresh();
        }),
        vscode.commands.registerCommand('ppds.toggleManagedSolutions', () => {
            solutionsTreeProvider.toggleManaged();
            const state = solutionsTreeProvider.getIncludeManaged() ? 'shown' : 'hidden';
            vscode.window.showInformationMessage(`Managed solutions: ${state}`);
        }),
    );
}

export function deactivate() {
    // Cleanup is handled by disposables
}
