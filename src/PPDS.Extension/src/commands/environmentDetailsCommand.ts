import * as vscode from 'vscode';

import type { DaemonClient } from '../daemonClient.js';
import type { EnvironmentTreeItem } from '../views/profileTreeView.js';
import { showErrorWithReport } from '../utils/errorNotify.js';

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
                // Determine the target environment URL
                let targetUrl: string;
                if (item?.envUrl) {
                    targetUrl = item.envUrl;
                } else {
                    const who = await daemonClient.authWho();
                    if (!who.environment?.url) {
                        vscode.window.showInformationMessage('No environment selected.');
                        return;
                    }
                    targetUrl = who.environment.url;
                }

                // envWho() queries the live Dataverse connection, which is
                // always the active environment. Verify the target matches.
                const activeEnv = await daemonClient.envWho();
                const normalise = (u: string): string => u.replace(/\/+$/, '').toLowerCase();

                if (normalise(activeEnv.url) !== normalise(targetUrl)) {
                    // Non-active environment — show what we know from the tree item
                    const lines = [
                        `Environment: ${item?.envDisplayName ?? targetUrl}`,
                        `URL: ${targetUrl}`,
                        '',
                        'Full details (version, org ID, user) are only available for the active environment.',
                    ];
                    const selection = await vscode.window.showInformationMessage(
                        lines.join('\n'),
                        { modal: true },
                        'Copy to Clipboard',
                    );
                    if (selection === 'Copy to Clipboard') {
                        void vscode.env.clipboard.writeText(lines.join('\n'));
                    }
                    return;
                }

                const lines = [
                    `Environment: ${activeEnv.organizationName} (${activeEnv.url})`,
                    `Unique Name: ${activeEnv.uniqueName}`,
                    `Version: ${activeEnv.version}`,
                    `Organization ID: ${activeEnv.organizationId}`,
                    `User ID: ${activeEnv.userId}`,
                    `Business Unit ID: ${activeEnv.businessUnitId}`,
                    `Connected As: ${activeEnv.connectedAs}`,
                ];

                if (activeEnv.environmentType) {
                    lines.push(`Type: ${activeEnv.environmentType}`);
                }

                const selection = await vscode.window.showInformationMessage(
                    lines.join('\n'),
                    { modal: true },
                    'Copy to Clipboard',
                );
                if (selection === 'Copy to Clipboard') {
                    void vscode.env.clipboard.writeText(lines.join('\n'));
                }
            } catch (error) {
                const message = error instanceof Error ? error.message : String(error);
                void showErrorWithReport(`Failed to get environment details: ${message}`);
            }
        }),
    );
}
