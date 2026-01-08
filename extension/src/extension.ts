import * as vscode from 'vscode';
import { DaemonClient } from './daemonClient';

let daemonClient: DaemonClient | undefined;

export function activate(context: vscode.ExtensionContext) {
    console.log('Power Platform Developer Suite is now active');

    // Create the daemon client
    daemonClient = new DaemonClient();
    context.subscriptions.push(daemonClient);

    // Register the list profiles command
    const listProfilesCmd = vscode.commands.registerCommand('ppds.listProfiles', async () => {
        try {
            const result = await daemonClient!.listProfiles();

            if (result.profiles.length === 0) {
                vscode.window.showInformationMessage('No authentication profiles found. Use "ppds auth create" to create one.');
                return;
            }

            // Show profiles in a quick pick
            const items = result.profiles.map(p => ({
                label: p.name ?? `Profile ${p.index}`,
                description: p.identity,
                detail: p.environment ? `${p.environment.displayName} (${p.authMethod})` : p.authMethod,
                picked: p.isActive
            }));

            const selected = await vscode.window.showQuickPick(items, {
                title: 'Authentication Profiles',
                placeHolder: result.activeProfile ? `Active: ${result.activeProfile}` : 'No active profile'
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
}

export function deactivate() {
    // Cleanup is handled by disposables
}
