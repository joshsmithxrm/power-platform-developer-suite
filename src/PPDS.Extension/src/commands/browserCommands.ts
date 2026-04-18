import * as vscode from 'vscode';

import type { DaemonClient } from '../daemonClient.js';
import type { EnvironmentTreeItem } from '../views/profileTreeView.js';
import { showErrorWithReport } from '../utils/errorNotify.js';

const MAKER_BASE_URL = 'https://make.powerapps.com';

export function buildMakerUrl(environmentId: string | null, path?: string): string {
    if (environmentId) {
        return `${MAKER_BASE_URL}/environments/${environmentId}${path ?? '/solutions'}`;
    }
    return MAKER_BASE_URL;
}

export function buildDynamicsUrl(environmentUrl: string): string {
    return environmentUrl.replace(/\/+$/, '');
}

/**
 * Resolves the environment ID, falling back to env/list lookup when the
 * tree item's cached data doesn't include it.
 */
async function resolveEnvironmentId(
    envUrl: string,
    envId: string | null,
    daemonClient: DaemonClient,
): Promise<string | null> {
    if (envId) return envId;

    try {
        const envResult = await daemonClient.envList();
        const normalise = (u: string): string => u.replace(/\/+$/, '').toLowerCase();
        const targetUrl = normalise(envUrl);
        const match = envResult.environments.find(
            e => normalise(e.apiUrl) === targetUrl || (e.url && normalise(e.url) === targetUrl)
        );
        return match?.environmentId ?? null;
    } catch {
        return null;
    }
}

/**
 * Registers browser navigation commands.
 *
 * Commands now accept EnvironmentTreeItem from tree context menu,
 * or fall back to the active profile's environment when invoked from command palette.
 */
export function registerBrowserCommands(
    context: vscode.ExtensionContext,
    daemonClient: DaemonClient,
): void {

    // ── Open in Maker Portal ─────────────────────────────────────────
    context.subscriptions.push(
        vscode.commands.registerCommand('ppds.openInMaker', async (item?: EnvironmentTreeItem) => {
            try {
                let envUrl: string;
                let envId: string | null;

                if (item?.envUrl) {
                    envUrl = item.envUrl;
                    envId = item.envEnvironmentId;
                } else {
                    // Fallback: use active profile's environment
                    const who = await daemonClient.authWho();
                    if (!who.environment) {
                        vscode.window.showInformationMessage('No environment selected.');
                        return;
                    }
                    envUrl = who.environment.url;
                    envId = who.environment.environmentId;
                }

                const environmentId = await resolveEnvironmentId(envUrl, envId, daemonClient);
                const url = buildMakerUrl(environmentId);

                if (!environmentId) {
                    vscode.window.showInformationMessage(
                        'Environment ID not available — opening Maker Portal home.'
                    );
                }

                await vscode.env.openExternal(vscode.Uri.parse(url));
            } catch (error) {
                const message = error instanceof Error ? error.message : String(error);
                void showErrorWithReport(`Failed to open Maker Portal: ${message}`);
            }
        }),
    );

    // ── Open in Dynamics 365 ──────────────────────────────────────────
    context.subscriptions.push(
        vscode.commands.registerCommand('ppds.openInDynamics', async (item?: EnvironmentTreeItem) => {
            try {
                let envUrl: string;

                if (item?.envUrl) {
                    envUrl = item.envUrl;
                } else {
                    const who = await daemonClient.authWho();
                    if (!who.environment?.url) {
                        vscode.window.showInformationMessage('No environment selected.');
                        return;
                    }
                    envUrl = who.environment.url;
                }

                const url = buildDynamicsUrl(envUrl);
                await vscode.env.openExternal(vscode.Uri.parse(url));
            } catch (error) {
                const message = error instanceof Error ? error.message : String(error);
                void showErrorWithReport(`Failed to open Dynamics 365: ${message}`);
            }
        }),
    );

    // ── Open Documentation ─────────────────────────────────────────────
    context.subscriptions.push(
        vscode.commands.registerCommand('ppds.openDocumentation', () => {
            void vscode.env.openExternal(vscode.Uri.parse('https://ppds.dev'));
        })
    );
}
