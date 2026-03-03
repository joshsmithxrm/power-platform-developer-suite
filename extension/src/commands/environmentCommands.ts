import * as vscode from 'vscode';
import type { DaemonClient } from '../daemonClient.js';

/**
 * Registers all environment management commands and returns the environment
 * status bar item so the caller can add it to disposables.
 *
 * Commands registered:
 * - ppds.selectEnvironment   - pick from available environments
 * - ppds.environmentDetails  - show details of the current environment
 * - ppds.refreshEnvironments - re-fetch and update the status bar display
 */
export function registerEnvironmentCommands(
    context: vscode.ExtensionContext,
    daemonClient: DaemonClient,
    onEnvironmentChanged: () => void,
): vscode.StatusBarItem {

    // ── Status Bar Item ──────────────────────────────────────────────────
    const envStatusBar = vscode.window.createStatusBarItem(
        vscode.StatusBarAlignment.Left,
        100,
    );
    envStatusBar.command = 'ppds.selectEnvironment';
    envStatusBar.text = '$(cloud) No environment';
    envStatusBar.tooltip = 'Click to select Dataverse environment';
    envStatusBar.show();
    context.subscriptions.push(envStatusBar);

    // ── Helpers ──────────────────────────────────────────────────────────

    /**
     * Fetches the current environment from auth/who and updates the
     * status bar text accordingly.
     */
    async function updateStatusBar(): Promise<void> {
        try {
            const who = await daemonClient.authWho();
            if (who.environment) {
                envStatusBar.text = `$(cloud) ${who.environment.displayName}`;
                envStatusBar.tooltip = who.environment.url
                    ? `${who.environment.displayName} - ${who.environment.url}`
                    : who.environment.displayName;
            } else {
                envStatusBar.text = '$(cloud) No environment';
                envStatusBar.tooltip = 'Click to select Dataverse environment';
            }
        } catch {
            envStatusBar.text = '$(cloud) No environment';
            envStatusBar.tooltip = 'Click to select Dataverse environment';
        }
    }

    // ── Select Environment ───────────────────────────────────────────────
    context.subscriptions.push(
        vscode.commands.registerCommand('ppds.selectEnvironment', async () => {
            try {
                const environments = await vscode.window.withProgress(
                    {
                        location: vscode.ProgressLocation.Notification,
                        title: 'Loading environments...',
                        cancellable: false,
                    },
                    async () => {
                        const result = await daemonClient.envList();
                        return result.environments;
                    },
                );

                if (environments.length === 0) {
                    vscode.window.showInformationMessage(
                        'No environments found. Ensure you have an active authentication profile.',
                    );
                    return;
                }

                const configureButton: vscode.QuickInputButton = {
                    iconPath: new vscode.ThemeIcon('gear'),
                    tooltip: 'Configure environment',
                };

                const items = environments.map(env => ({
                    label: env.friendlyName,
                    description: env.type ? `[${env.type}]` : undefined,
                    detail: env.region
                        ? `${env.apiUrl} (${env.region})`
                        : env.apiUrl,
                    picked: env.isActive,
                    apiUrl: env.apiUrl,
                    buttons: [configureButton],
                }));

                const selected = await new Promise<typeof items[number] | undefined>(resolve => {
                    const quickPick = vscode.window.createQuickPick<typeof items[number]>();
                    quickPick.title = 'Select Dataverse Environment';
                    quickPick.placeholder = 'Choose an environment to connect to';
                    quickPick.matchOnDescription = true;
                    quickPick.matchOnDetail = true;
                    quickPick.items = items;

                    quickPick.onDidTriggerItemButton(async (e) => {
                        quickPick.hide();
                        await vscode.commands.executeCommand('ppds.configureEnvironment');
                    });

                    quickPick.onDidAccept(() => {
                        resolve(quickPick.selectedItems[0]);
                        quickPick.hide();
                    });
                    quickPick.onDidHide(() => {
                        resolve(undefined);
                        quickPick.dispose();
                    });

                    quickPick.show();
                });

                if (!selected) {
                    return; // User cancelled
                }

                await daemonClient.envSelect(selected.apiUrl);

                envStatusBar.text = `$(cloud) ${selected.label}`;
                envStatusBar.tooltip = selected.detail ?? selected.label;

                onEnvironmentChanged();

                vscode.window.showInformationMessage(
                    `Connected to environment: ${selected.label}`,
                );
            } catch (error) {
                const message = error instanceof Error ? error.message : String(error);
                vscode.window.showErrorMessage(`Failed to select environment: ${message}`);
            }
        }),
    );

    // ── Environment Details ──────────────────────────────────────────────
    context.subscriptions.push(
        vscode.commands.registerCommand('ppds.environmentDetails', async () => {
            try {
                const who = await daemonClient.authWho();

                if (!who.environment) {
                    vscode.window.showInformationMessage(
                        'No environment is currently selected. Use "PPDS: Select Environment" to choose one.',
                    );
                    return;
                }

                const env = who.environment;
                const items: vscode.QuickPickItem[] = [];

                items.push({ label: 'Environment', kind: vscode.QuickPickItemKind.Separator });
                items.push({
                    label: `$(globe) ${env.displayName}`,
                    description: 'Display Name',
                });
                items.push({
                    label: `$(link) ${env.url}`,
                    description: 'URL',
                });
                if (env.type) {
                    items.push({
                        label: `$(server) ${env.type}`,
                        description: 'Type',
                    });
                }
                if (env.region) {
                    items.push({
                        label: `$(location) ${env.region}`,
                        description: 'Region',
                    });
                }
                if (env.uniqueName) {
                    items.push({
                        label: `$(tag) ${env.uniqueName}`,
                        description: 'Unique Name',
                    });
                }
                if (env.environmentId) {
                    items.push({
                        label: `$(key) ${env.environmentId}`,
                        description: 'Environment ID',
                    });
                }
                if (env.organizationId) {
                    items.push({
                        label: `$(organization) ${env.organizationId}`,
                        description: 'Organization ID',
                    });
                }

                await vscode.window.showQuickPick(items, {
                    title: `Environment Details: ${env.displayName}`,
                    placeHolder: 'Environment information (read-only)',
                    canPickMany: false,
                });
            } catch (error) {
                const message = error instanceof Error ? error.message : String(error);
                vscode.window.showErrorMessage(`Failed to get environment details: ${message}`);
            }
        }),
    );

    // ── Refresh Environments ─────────────────────────────────────────────
    context.subscriptions.push(
        vscode.commands.registerCommand('ppds.refreshEnvironments', async () => {
            try {
                await updateStatusBar();
            } catch (error) {
                const message = error instanceof Error ? error.message : String(error);
                vscode.window.showErrorMessage(`Failed to refresh environments: ${message}`);
            }
        }),
    );

    // Kick off an initial status bar update (fire-and-forget)
    void updateStatusBar();

    return envStatusBar;
}
