import * as vscode from 'vscode';
import type { DaemonClient } from '../daemonClient.js';
import type { ProfileInfo } from '../types.js';

const MAKER_BASE_URL = 'https://make.powerapps.com';

export function buildMakerUrl(environmentId: string | null): string {
    if (environmentId) {
        return `${MAKER_BASE_URL}/environments/${environmentId}/solutions`;
    }
    return MAKER_BASE_URL;
}

export function buildDynamicsUrl(environmentUrl: string): string {
    return environmentUrl.replace(/\/+$/, '');
}

/**
 * Shows a quick pick of profiles that have environments, pre-selecting the active one.
 * Returns the selected profile, or undefined if cancelled.
 */
async function pickProfileWithEnvironment(daemonClient: DaemonClient): Promise<ProfileInfo | undefined> {
    const result = await daemonClient.authList();

    const profilesWithEnv = result.profiles.filter(p => p.environment != null);

    if (profilesWithEnv.length === 0) {
        vscode.window.showInformationMessage('No profiles have an environment selected. Use "PPDS: Select Environment" first.');
        return undefined;
    }

    interface ProfileQuickPickItem extends vscode.QuickPickItem {
        profile: ProfileInfo;
    }

    const items: ProfileQuickPickItem[] = profilesWithEnv.map(p => ({
        label: p.name ?? `Profile ${p.index}`,
        description: p.environment!.displayName,
        detail: p.environment!.url,
        picked: p.isActive,
        profile: p,
    }));

    const selected = await vscode.window.showQuickPick(items, {
        title: 'Select Profile',
        placeHolder: 'Choose which environment to open',
        ignoreFocusOut: true,
    });

    return selected?.profile;
}

/**
 * Resolves the target profile: uses the tree item's profile if provided,
 * otherwise shows a quick pick.
 */
async function resolveProfile(
    item: unknown,
    daemonClient: DaemonClient,
): Promise<ProfileInfo | undefined> {
    // If invoked from tree context menu, item has the profile
    const profileItem = item as { profile?: ProfileInfo } | undefined;
    if (profileItem?.profile) {
        return profileItem.profile;
    }

    // Otherwise show picker
    return pickProfileWithEnvironment(daemonClient);
}

/**
 * Registers browser navigation commands and returns the disposables.
 *
 * Commands registered:
 * - ppds.openInMaker    — open Maker Portal for a profile's environment
 * - ppds.openInDynamics — open Dynamics 365 for a profile's environment
 */
export function registerBrowserCommands(
    context: vscode.ExtensionContext,
    daemonClient: DaemonClient,
): void {

    // ── Open in Maker Portal ─────────────────────────────────────────
    context.subscriptions.push(
        vscode.commands.registerCommand('ppds.openInMaker', async (item?: unknown) => {
            try {
                const profile = await resolveProfile(item, daemonClient);
                if (!profile) return;

                if (!profile.environment) {
                    vscode.window.showInformationMessage(
                        `No environment selected for "${profile.name ?? `Profile ${profile.index}`}".`
                    );
                    return;
                }

                const url = buildMakerUrl(profile.environment.environmentId ?? null);

                if (!profile.environment.environmentId) {
                    vscode.window.showInformationMessage(
                        'Environment ID not available — opening Maker Portal home. Select the environment manually.'
                    );
                }

                await vscode.env.openExternal(vscode.Uri.parse(url));
            } catch (error) {
                const message = error instanceof Error ? error.message : String(error);
                vscode.window.showErrorMessage(`Failed to open Maker Portal: ${message}`);
            }
        }),
    );

    // ── Open in Dynamics 365 ──────────────────────────────────────────
    context.subscriptions.push(
        vscode.commands.registerCommand('ppds.openInDynamics', async (item?: unknown) => {
            try {
                const profile = await resolveProfile(item, daemonClient);
                if (!profile) return;

                if (!profile.environment) {
                    vscode.window.showInformationMessage(
                        `No environment selected for "${profile.name ?? `Profile ${profile.index}`}".`
                    );
                    return;
                }

                const url = buildDynamicsUrl(profile.environment.url);
                await vscode.env.openExternal(vscode.Uri.parse(url));
            } catch (error) {
                const message = error instanceof Error ? error.message : String(error);
                vscode.window.showErrorMessage(`Failed to open Dynamics 365: ${message}`);
            }
        }),
    );
}
