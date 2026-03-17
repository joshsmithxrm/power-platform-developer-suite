import * as vscode from 'vscode';

import { DaemonClient } from './daemonClient.js';
import type { ProfileInfo } from './types.js';
import { ProfileTreeDataProvider, ProfileTreeItem, getProfileId } from './views/profileTreeView.js';
import { ToolsTreeDataProvider } from './views/toolsTreeView.js';
import { registerProfileCommands } from './commands/profileCommands.js';
import { registerEnvironmentConfigCommand } from './commands/environmentConfigCommand.js';
import { registerBrowserCommands } from './commands/browserCommands.js';
import { registerEnvironmentDetailsCommand } from './commands/environmentDetailsCommand.js';
import { ExplainDocumentProvider } from './providers/explainDocumentProvider.js';
import { DataverseNotebookSerializer } from './notebooks/DataverseNotebookSerializer.js';
import { DataverseNotebookController } from './notebooks/DataverseNotebookController.js';
import {
    createNewNotebook, toggleCellLanguage, openCellInDataExplorer,
    exportCellResults, openQueryInNotebook,
} from './commands/notebookCommands.js';
import { DataverseCompletionProvider } from './providers/completionProvider.js';
import { QueryPanel } from './panels/QueryPanel.js';
import { SolutionsPanel } from './panels/SolutionsPanel.js';
import { ImportJobsPanel } from './panels/ImportJobsPanel.js';
import { PluginTracesPanel } from './panels/PluginTracesPanel.js';
import { MetadataBrowserPanel } from './panels/MetadataBrowserPanel.js';
import { migrateLegacyState } from './migration/legacyState.js';
import { registerDebugCommands } from './commands/debugCommands.js';
import { DaemonStatusBar } from './daemonStatusBar.js';
import { ProfileStatusBar } from './profileStatusBar.js';

let daemonClient: DaemonClient | undefined;
let logChannel: vscode.LogOutputChannel | undefined;

/**
 * Wraps a command handler with error logging. All unhandled exceptions
 * from command callbacks are logged to the PPDS output channel so they
 * appear in user-submitted log files, not just the dev console.
 */
// eslint-disable-next-line @typescript-eslint/no-explicit-any -- generic command wrapper accepts arbitrary args
function cmd(handler: (...args: any[]) => any): (...args: any[]) => Promise<void> {
    // eslint-disable-next-line @typescript-eslint/no-explicit-any -- generic command wrapper accepts arbitrary args
    return async (...args: any[]) => {
        try {
            // eslint-disable-next-line @typescript-eslint/no-unsafe-argument -- forwarding opaque command args
            await handler(...args);
        } catch (err) {
            const msg = err instanceof Error ? err.message : String(err);
            const stack = err instanceof Error ? err.stack : undefined;
            logChannel?.error(`Command error: ${msg}`);
            if (stack) logChannel?.debug(stack);
            vscode.window.showErrorMessage(`PPDS: ${msg}`);
        }
    };
}

/**
 * Registers commands for opening panel-based views (Data Explorer, Solutions, etc.).
 * Add new panel commands here when adding panels.
 */
function registerPanelCommands(
    context: vscode.ExtensionContext,
    client: DaemonClient,
): void {
    context.subscriptions.push(
        vscode.commands.registerCommand('ppds.dataExplorer', () => {
            QueryPanel.show(context.extensionUri, client);
        }),
        vscode.commands.registerCommand('ppds.openSolutions', () => {
            SolutionsPanel.show(context.extensionUri, client);
        }),
        vscode.commands.registerCommand('ppds.openImportJobs', () => {
            ImportJobsPanel.show(context.extensionUri, client);
        }),
        vscode.commands.registerCommand('ppds.openPluginTraces', () => {
            PluginTracesPanel.show(context.extensionUri, client);
        }),
        vscode.commands.registerCommand('ppds.openMetadataBrowser', () => {
            MetadataBrowserPanel.show(context.extensionUri, client);
        }),
        vscode.commands.registerCommand('ppds.openQueryInNotebook', (sql?: string) => {
            void openQueryInNotebook(sql ?? '');
        }),
        vscode.commands.registerCommand('ppds.openNotebooks', () => {
            void createNewNotebook();
        }),
        vscode.commands.registerCommand('ppds.showLogs', () => {
            logChannel?.show();
        }),
    );
}

export function activate(context: vscode.ExtensionContext): void {
    // eslint-disable-next-line no-console -- startup diagnostic before LogOutputChannel exists
    console.log('Power Platform Developer Suite is now active');

    // ── Gate debug commands — only visible when running in dev mode (F5) ──
    const isDevelopment = context.extensionMode === vscode.ExtensionMode.Development;
    void vscode.commands.executeCommand('setContext', 'ppds.isDevelopment', isDevelopment);

    // ── Legacy State Migration ────────────────────────────────────────
    migrateLegacyState(context);

    // Read extension settings
    const config = vscode.workspace.getConfiguration('ppds');

    // Create structured log channel
    logChannel = vscode.window.createOutputChannel('PPDS', { log: true });
    context.subscriptions.push(logChannel);

    // Global unhandled rejection logging — catches async errors that escape
    // individual command handlers so they appear in the PPDS log for diagnostics
    const rejectionHandler = (reason: unknown): void => {
        const msg = reason instanceof Error ? reason.message : String(reason);
        const stack = reason instanceof Error ? reason.stack : undefined;
        logChannel?.error(`Unhandled rejection: ${msg}`);
        if (stack) logChannel?.debug(stack);
    };
    process.on('unhandledRejection', rejectionHandler);
    context.subscriptions.push({ dispose: () => process.removeListener('unhandledRejection', rejectionHandler) });

    void vscode.commands.executeCommand('setContext', 'ppds.daemonState', 'starting');
    void vscode.commands.executeCommand('setContext', 'ppds.profileCount', 0);

    // Create the daemon client
    daemonClient = new DaemonClient(context.extensionPath, logChannel);
    const client = daemonClient; // Local const for type narrowing in closures
    context.subscriptions.push(client);

    // ── Daemon Status Bar ────────────────────────────────────────────
    const statusBar = new DaemonStatusBar(client);
    context.subscriptions.push(statusBar);

    // ── Profile Status Bar ───────────────────────────────────────────
    const profileStatusBar = new ProfileStatusBar(client);
    context.subscriptions.push(profileStatusBar);

    // ── Virtual Document Provider (EXPLAIN output) ────────────────────
    const explainProvider = new ExplainDocumentProvider();
    context.subscriptions.push(
        vscode.workspace.registerTextDocumentContentProvider(ExplainDocumentProvider.scheme, explainProvider),
        explainProvider,
    );

    // ── Restart Daemon Command ───────────────────────────────────────
    context.subscriptions.push(
        vscode.commands.registerCommand('ppds.restartDaemon', async () => {
            try {
                await client.restart();
                vscode.window.showInformationMessage('PPDS daemon restarted.');
            } catch (err) {
                const msg = err instanceof Error ? err.message : String(err);
                vscode.window.showErrorMessage(`Failed to restart daemon: ${msg}`);
            }
        })
    );

    // Auto-start daemon if configured (default: true)
    if (config.get<boolean>('autoStartDaemon', true)) {
        void client.start().catch(err => {
            const msg = err instanceof Error ? err.message : String(err);
            vscode.window.showErrorMessage(`PPDS daemon failed to start: ${msg}`);
        });
    }

    // ── Shared State Tracker (mutable, updated by tree provider) ────────
    const extensionState = { daemonState: 'starting', profileCount: 0 };

    // ── Profile Tree View ────────────────────────────────────────────────
    const profileTreeProvider = new ProfileTreeDataProvider(client, logChannel, context.globalState, extensionState);
    const profileTreeView = vscode.window.createTreeView('ppds.profiles', {
        treeDataProvider: profileTreeProvider,
        showCollapseAll: false,
    });
    context.subscriptions.push(profileTreeView, profileTreeProvider);

    // ── Profile expansion persistence ────────────────────────────────────
    context.subscriptions.push(
        profileTreeView.onDidExpandElement(e => {
            if (e.element instanceof ProfileTreeItem && e.element.id) {
                const ids = new Set(context.globalState.get<string[]>('ppds.profiles.expandedIds') ?? []);
                ids.add(e.element.id);
                void context.globalState.update('ppds.profiles.expandedIds', [...ids]);
            }
        }),
        profileTreeView.onDidCollapseElement(e => {
            if (e.element instanceof ProfileTreeItem && e.element.id) {
                const ids = new Set(context.globalState.get<string[]>('ppds.profiles.expandedIds') ?? []);
                ids.delete(e.element.id);
                void context.globalState.update('ppds.profiles.expandedIds', [...ids]);
            }
        }),
    );

    // ── Profile Move Commands ────────────────────────────────────────────
    context.subscriptions.push(
        vscode.commands.registerCommand('ppds.moveProfileUp', cmd(async (item: { profile: ProfileInfo }) => {
            if (!item?.profile) return;
            const profileId = getProfileId(item.profile);
            await profileTreeProvider.moveProfile(profileId, 'up');
        })),
        vscode.commands.registerCommand('ppds.moveProfileDown', cmd(async (item: { profile: ProfileInfo }) => {
            if (!item?.profile) return;
            const profileId = getProfileId(item.profile);
            await profileTreeProvider.moveProfile(profileId, 'down');
        })),
    );

    // ── Tools Tree View ──────────────────────────────────────────────────
    const toolsTreeProvider = new ToolsTreeDataProvider();
    const toolsTreeView = vscode.window.createTreeView('ppds.tools', {
        treeDataProvider: toolsTreeProvider,
        showCollapseAll: false,
    });
    context.subscriptions.push(toolsTreeView, toolsTreeProvider);

    // Sync tools tree disabled state with profile availability
    const refreshToolsState = (): void => {
        void client.authList().then(result => {
            toolsTreeProvider.setHasActiveProfile(
                result.activeProfile !== null || result.activeProfileIndex !== null);
        }).catch((err: unknown) => {
            logChannel?.warn(`Failed to refresh tools state: ${err instanceof Error ? err.message : String(err)}`);
            toolsTreeProvider.setHasActiveProfile(false);
        });
    };

    // Refresh tools state when daemon becomes ready (fixes startup race where
    // the initial call fires before the daemon is connected)
    context.subscriptions.push(
        client.onDidChangeState(state => {
            if (state === 'ready') {
                refreshToolsState();
                profileTreeProvider.refresh();
            }
        }),
    );

    // ── Profile Commands ────────────────────────────────────────────────
    registerProfileCommands(context, client, () => { profileTreeProvider.refresh(); refreshToolsState(); profileStatusBar.refresh(); });

    // ── Environment Commands (tree context menu) ────────────────────────
    context.subscriptions.push(
        // Set as Default — persists environment to profile
        vscode.commands.registerCommand('ppds.setDefaultEnvironment', async (item: { envUrl: string; envDisplayName: string }) => {
            if (!item?.envUrl) return;
            try {
                await client.envSelect(item.envUrl);
                profileTreeProvider.refresh();
                refreshToolsState();
                vscode.window.showInformationMessage(`Default environment set to ${item.envDisplayName}`);
            } catch (err) {
                const msg = err instanceof Error ? err.message : String(err);
                vscode.window.showErrorMessage(`Failed to set environment: ${msg}`);
            }
        }),

        // Open Data Explorer targeting this environment
        vscode.commands.registerCommand('ppds.openDataExplorerForEnv', cmd((item: { envUrl: string; envDisplayName: string }) => {
            if (!item?.envUrl) return;
            QueryPanel.show(context.extensionUri, client, undefined, item.envUrl, item.envDisplayName);
        })),

        // Open Solutions targeting this environment
        vscode.commands.registerCommand('ppds.openSolutionsForEnv', cmd((item: { envUrl: string; envDisplayName: string }) => {
            if (!item?.envUrl) return;
            SolutionsPanel.show(context.extensionUri, client, item.envUrl, item.envDisplayName);
        })),

        // Open Import Jobs targeting this environment
        vscode.commands.registerCommand('ppds.openImportJobsForEnv', cmd((item: { envUrl: string; envDisplayName: string }) => {
            if (!item?.envUrl) return;
            ImportJobsPanel.show(context.extensionUri, client, item.envUrl, item.envDisplayName);
        })),

        // Open Plugin Traces targeting this environment
        vscode.commands.registerCommand('ppds.openPluginTracesForEnv', cmd((item: { envUrl: string; envDisplayName: string }) => {
            if (!item?.envUrl) return;
            PluginTracesPanel.show(context.extensionUri, client, item.envUrl, item.envDisplayName);
        })),

        // Copy environment URL to clipboard
        vscode.commands.registerCommand('ppds.copyEnvironmentUrl', async (item: { envUrl: string }) => {
            if (!item?.envUrl) return;
            await vscode.env.clipboard.writeText(item.envUrl);
            vscode.window.showInformationMessage('Environment URL copied to clipboard');
        }),

        // Test connection to environment (uses a lightweight query against the target env)
        vscode.commands.registerCommand('ppds.testConnection', async (item: { envUrl: string; envDisplayName: string }) => {
            if (!item?.envUrl) return;
            await vscode.window.withProgress(
                { location: vscode.ProgressLocation.Notification, title: `Testing connection to ${item.envDisplayName}\u2026` },
                async () => {
                    try {
                        const result = await client.querySql({
                            sql: 'SELECT TOP 1 name FROM organization',
                            top: 1,
                            environmentUrl: item.envUrl,
                        });
                        const orgName = String(result.records[0]?.['name'] ?? 'unknown');
                        vscode.window.showInformationMessage(
                            `Connection successful \u2014 ${orgName} (${result.executionTimeMs}ms)`,
                        );
                    } catch (err) {
                        const msg = err instanceof Error ? err.message : String(err);
                        vscode.window.showErrorMessage(`Connection failed: ${msg}`);
                    }
                },
            );
        }),

        // Remove configured environment from environments.json
        vscode.commands.registerCommand('ppds.removeEnvironment', async (item: { envUrl: string; envDisplayName: string; source: string }) => {
            if (!item?.envUrl) return;
            const confirm = await vscode.window.showWarningMessage(
                `Remove "${item.envDisplayName}" from saved environments?`,
                { modal: true },
                'Remove',
            );
            if (confirm !== 'Remove') return;
            try {
                await client.envConfigRemove(item.envUrl);
                profileTreeProvider.refresh();
            } catch (err) {
                const msg = err instanceof Error ? err.message : String(err);
                vscode.window.showErrorMessage(`Failed to remove environment: ${msg}`);
            }
        }),

        // Manual URL entry — saves to environments.json + sets as default
        vscode.commands.registerCommand('ppds.switchProfileEnvironmentManual', async () => {
            const url = await vscode.window.showInputBox({
                title: 'Dataverse Environment URL',
                prompt: 'Enter the full URL (e.g., https://myorg.crm.dynamics.com)',
                placeHolder: 'https://myorg.crm.dynamics.com',
                ignoreFocusOut: true,
                validateInput: (value) => {
                    if (!value.trim()) return 'URL is required';
                    try { new URL(value.trim()); return undefined; }
                    catch { return 'Enter a valid URL'; }
                },
            });
            if (!url) return;
            const trimmedUrl = url.trim();
            try {
                // Save to environments.json so it persists
                await client.envConfigSet({ environmentUrl: trimmedUrl });
                // Set as default
                await client.envSelect(trimmedUrl);
                profileTreeProvider.refresh();
                refreshToolsState();
                vscode.window.showInformationMessage(`Environment set to ${trimmedUrl}`);
            } catch (err) {
                const msg = err instanceof Error ? err.message : String(err);
                vscode.window.showErrorMessage(`Failed to set environment: ${msg}`);
            }
        }),
    );

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

    // ── Environment Details Command ─────────────────────────────────
    registerEnvironmentDetailsCommand(context, client);

    // ── Debug / Diagnostic Commands ──────────────────────────────────
    registerDebugCommands(context, client, profileTreeProvider, extensionState, {
        queryPanels: () => QueryPanel.instanceCount,
        solutionsPanels: () => SolutionsPanel.instanceCount,
        importJobsPanels: () => ImportJobsPanel.instanceCount,
        pluginTracesPanels: () => PluginTracesPanel.instanceCount,
        metadataBrowserPanels: () => MetadataBrowserPanel.instanceCount,
    });

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

    // ── Panel Commands ───────────────────────────────────────────────
    registerPanelCommands(context, client);
}

export function deactivate(): void {
    // Cleanup is handled by disposables
}
