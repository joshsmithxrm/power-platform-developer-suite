import * as vscode from 'vscode';

import type { DaemonClient } from '../daemonClient.js';
import type { EnvironmentTreeItem } from '../views/profileTreeView.js';

/**
 * Registers the environment details command.
 *
 * Shows WhoAmI-based environment details (URL, version, org ID, user ID,
 * connected as) via an information message. Accepts an optional
 * EnvironmentTreeItem from the tree context menu, falling back to the
 * active profile's environment.
 */
export function registerEnvironmentDetailsCommand(
    context: vscode.ExtensionContext,
    daemonClient: DaemonClient,
): void {
    context.subscriptions.push(
        vscode.commands.registerCommand('ppds.environmentDetails', async (item?: EnvironmentTreeItem) => {
            try {
                // Guard: ensure an environment is available
                if (!item?.envUrl) {
                    const who = await daemonClient.authWho();
                    if (!who.environment?.url) {
                        vscode.window.showInformationMessage('No environment selected.');
                        return;
                    }
                }

                const details = await daemonClient.envWho();

                const lines = [
                    `Environment: ${details.organizationName} (${details.url})`,
                    `Unique Name: ${details.uniqueName}`,
                    `Version: ${details.version}`,
                    `Organization ID: ${details.organizationId}`,
                    `User ID: ${details.userId}`,
                    `Business Unit ID: ${details.businessUnitId}`,
                    `Connected As: ${details.connectedAs}`,
                ];

                if (details.environmentType) {
                    lines.push(`Type: ${details.environmentType}`);
                }

                await vscode.window.showInformationMessage(
                    lines.join('\n'),
                    { modal: true },
                    'Copy to Clipboard',
                ).then(selection => {
                    if (selection === 'Copy to Clipboard') {
                        void vscode.env.clipboard.writeText(lines.join('\n'));
                    }
                });
            } catch (error) {
                const message = error instanceof Error ? error.message : String(error);
                vscode.window.showErrorMessage(`Failed to get environment details: ${message}`);
            }
        }),
    );
}
