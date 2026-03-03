import * as vscode from 'vscode';
import type { DaemonClient, AuthWhoResponse } from '../daemonClient.js';
import type { ProfileInfo } from '../types.js';

/**
 * Registers all profile management commands and returns the disposables.
 *
 * Commands registered:
 * - ppds.selectProfile   — switch the active profile (tree context menu / quick pick)
 * - ppds.listProfiles    — quick-pick list of profiles (command palette)
 * - ppds.profileDetails  — read-only detail view for the active profile
 * - ppds.createProfile   — multi-step wizard for creating a new auth profile
 * - ppds.deleteProfile   — delete a profile with confirmation
 * - ppds.renameProfile   — rename a profile via input box
 * - ppds.refreshProfiles — refresh the profiles tree view
 */
export function registerProfileCommands(
    context: vscode.ExtensionContext,
    daemonClient: DaemonClient,
    refreshProfiles: () => void,
): void {

    // ── Device Code Handler (registered once, not per createProfile call) ─
    daemonClient.onDeviceCode(async ({ userCode, verificationUrl, message }) => {
        const action = await vscode.window.showInformationMessage(
            message || `Enter code: ${userCode}`,
            { modal: false },
            'Open Browser', 'Copy Code'
        );
        if (action === 'Open Browser') {
            await vscode.env.openExternal(vscode.Uri.parse(verificationUrl));
        } else if (action === 'Copy Code') {
            await vscode.env.clipboard.writeText(userCode);
        }
    });

    // ── Refresh Profiles ────────────────────────────────────────────────
    context.subscriptions.push(
        vscode.commands.registerCommand('ppds.refreshProfiles', () => {
            refreshProfiles();
        }),
    );

    // ── Select Profile (tree context menu / inline button) ──────────────
    context.subscriptions.push(
        vscode.commands.registerCommand('ppds.selectProfile', async (item: unknown) => {
            const profileItem = item as { profile?: { index: number; name: string | null } } | undefined;
            if (!profileItem?.profile) {
                return;
            }
            try {
                const { index, name } = profileItem.profile;
                await daemonClient.authSelect(name ? { name } : { index });
                refreshProfiles();
                vscode.window.showInformationMessage(`Switched to profile: ${name ?? `Profile ${index}`}`);
            } catch (error) {
                const message = error instanceof Error ? error.message : String(error);
                vscode.window.showErrorMessage(`Failed to select profile: ${message}`);
            }
        }),
    );

    // ── List Profiles (command palette quick pick) ──────────────────────

    interface ProfileQuickPickItem extends vscode.QuickPickItem {
        profile: ProfileInfo;
    }

    context.subscriptions.push(
        vscode.commands.registerCommand('ppds.listProfiles', async () => {
            try {
                const result = await daemonClient.authList();

                if (result.profiles.length === 0) {
                    vscode.window.showInformationMessage(
                        'No authentication profiles found. Use "ppds auth create" to create one.',
                    );
                    return;
                }

                const items: ProfileQuickPickItem[] = result.profiles.map(p => ({
                    label: p.name ?? `Profile ${p.index}`,
                    description: p.identity ?? undefined,
                    detail: p.environment
                        ? `${p.environment.displayName} (${p.authMethod})`
                        : (p.authMethod ?? undefined),
                    picked: p.isActive,
                    profile: p,
                }));

                const selected = await vscode.window.showQuickPick(items, {
                    title: 'Authentication Profiles',
                    placeHolder: result.activeProfile
                        ? `Active: ${result.activeProfile}`
                        : 'No active profile',
                });

                if (selected) {
                    try {
                        const p = selected.profile;
                        await daemonClient.authSelect(p.name ? { name: p.name } : { index: p.index });
                        refreshProfiles();
                        vscode.window.showInformationMessage(`Switched to profile: ${selected.label}`);
                    } catch (error) {
                        const message = error instanceof Error ? error.message : String(error);
                        vscode.window.showErrorMessage(`Failed to switch profile: ${message}`);
                    }
                }
            } catch (error) {
                const message = error instanceof Error ? error.message : String(error);
                vscode.window.showErrorMessage(`Failed to list profiles: ${message}`);
            }
        }),
    );

    // ── Profile Details ─────────────────────────────────────────────────
    context.subscriptions.push(
        vscode.commands.registerCommand('ppds.profileDetails', async () => {
            try {
                const who = await daemonClient.authWho();
                await showProfileDetails(who);
            } catch (error) {
                const message = error instanceof Error ? error.message : String(error);
                vscode.window.showErrorMessage(`Failed to get profile details: ${message}`);
            }
        }),
    );

    // ── Create Profile ──────────────────────────────────────────────────
    context.subscriptions.push(
        vscode.commands.registerCommand('ppds.createProfile', async () => {
            try {
                await runCreateProfileWizard(daemonClient, refreshProfiles);
            } catch (error) {
                const message = error instanceof Error ? error.message : String(error);
                vscode.window.showErrorMessage(`Failed to create profile: ${message}`);
            }
        }),
    );

    // ── Delete Profile ──────────────────────────────────────────────────
    context.subscriptions.push(
        vscode.commands.registerCommand('ppds.deleteProfile', async (item: unknown) => {
            try {
                await runDeleteProfile(item, daemonClient, refreshProfiles);
            } catch (error) {
                const message = error instanceof Error ? error.message : String(error);
                vscode.window.showErrorMessage(`Failed to delete profile: ${message}`);
            }
        }),
    );

    // ── Rename Profile ──────────────────────────────────────────────────
    context.subscriptions.push(
        vscode.commands.registerCommand('ppds.renameProfile', async (item: unknown) => {
            try {
                await runRenameProfile(item, daemonClient, refreshProfiles);
            } catch (error) {
                const message = error instanceof Error ? error.message : String(error);
                vscode.window.showErrorMessage(`Failed to rename profile: ${message}`);
            }
        }),
    );
}

// ── Profile Details ─────────────────────────────────────────────────────────

/**
 * Shows a read-only QuickPick detail view with all profile information
 * organized into logical sections.
 */
async function showProfileDetails(who: AuthWhoResponse): Promise<void> {
    const items: vscode.QuickPickItem[] = [];

    // ── Identity Section ────────────────────────────────────────────
    items.push({ label: 'Identity', kind: vscode.QuickPickItemKind.Separator });
    items.push({
        label: `$(person) ${who.name ?? '(unnamed)'}`,
        detail: who.username ?? who.applicationId ?? '(unknown)',
        description: 'Name',
    });
    items.push({
        label: `$(key) ${who.authMethod}`,
        description: 'Auth Method',
    });
    items.push({
        label: `$(cloud) ${who.cloud}`,
        description: 'Cloud',
    });
    if (who.tenantId) {
        items.push({
            label: `$(organization) ${who.tenantId}`,
            description: 'Tenant ID',
        });
    }
    if (who.objectId) {
        items.push({
            label: `$(tag) ${who.objectId}`,
            description: 'Object ID',
        });
    }

    // ── Token Section ───────────────────────────────────────────────
    items.push({ label: 'Token', kind: vscode.QuickPickItemKind.Separator });
    const tokenIcon = getTokenStatusIcon(who.tokenStatus);
    items.push({
        label: `${tokenIcon} ${who.tokenStatus ?? 'Unknown'}`,
        description: 'Token Status',
    });
    if (who.tokenExpiresOn) {
        const countdown = formatCountdown(who.tokenExpiresOn);
        items.push({
            label: `$(clock) ${formatDateTime(who.tokenExpiresOn)}`,
            description: countdown ? `Expires (${countdown})` : 'Expires',
        });
    }

    // ── Environment Section ─────────────────────────────────────────
    if (who.environment) {
        items.push({ label: 'Environment', kind: vscode.QuickPickItemKind.Separator });
        items.push({
            label: `$(globe) ${who.environment.displayName}`,
            description: 'Display Name',
        });
        items.push({
            label: `$(link) ${who.environment.url}`,
            description: 'URL',
        });
        if (who.environment.type) {
            items.push({
                label: `$(server) ${who.environment.type}`,
                description: 'Type',
            });
        }
        if (who.environment.region) {
            items.push({
                label: `$(location) ${who.environment.region}`,
                description: 'Region',
            });
        }
    }

    // ── Usage Section ───────────────────────────────────────────────
    items.push({ label: 'Usage', kind: vscode.QuickPickItemKind.Separator });
    if (who.createdAt) {
        items.push({
            label: `$(calendar) ${formatDateTime(who.createdAt)}`,
            description: 'Created',
        });
    }
    if (who.lastUsedAt) {
        items.push({
            label: `$(history) ${formatDateTime(who.lastUsedAt)}`,
            description: 'Last Used',
        });
    }

    await vscode.window.showQuickPick(items, {
        title: `Profile Details: ${who.name ?? `Profile ${who.index}`}`,
        placeHolder: 'Profile information (read-only)',
        canPickMany: false,
    });
}

function getTokenStatusIcon(status: string | null): string {
    switch (status?.toLowerCase()) {
        case 'valid':
        case 'active':
            return '$(pass-filled)';
        case 'expired':
        case 'invalid':
            return '$(error)';
        default:
            return '$(circle-outline)';
    }
}

function formatDateTime(iso: string): string {
    try {
        const date = new Date(iso);
        return date.toLocaleString();
    } catch {
        return iso;
    }
}

function formatCountdown(iso: string): string | null {
    try {
        const expiresAt = new Date(iso).getTime();
        const now = Date.now();
        const diffMs = expiresAt - now;
        if (diffMs <= 0) { return 'expired'; }
        const totalMinutes = Math.floor(diffMs / 60000);
        const hours = Math.floor(totalMinutes / 60);
        const minutes = totalMinutes % 60;
        if (hours > 0) { return `${hours}h ${minutes}m`; }
        return `${minutes}m`;
    } catch {
        return null;
    }
}

// ── Create Profile Wizard ───────────────────────────────────────────────────

interface AuthMethodOption extends vscode.QuickPickItem {
    authMethodId: string;
}

async function runCreateProfileWizard(
    daemonClient: DaemonClient,
    refreshProfiles: () => void,
): Promise<void> {
    // Step 1: Choose auth method
    const authMethods: AuthMethodOption[] = [
        {
            label: '$(browser) Device Code',
            description: 'Authenticate via a code displayed in the terminal',
            authMethodId: 'deviceCode',
        },
        {
            label: '$(globe) Interactive Browser',
            description: 'Opens a browser window for authentication',
            authMethodId: 'interactive',
        },
        {
            label: '$(lock) Client Secret',
            description: 'Service principal with client secret',
            authMethodId: 'clientSecret',
        },
        {
            label: '$(file) Certificate File',
            description: 'Service principal with certificate file (.pfx)',
            authMethodId: 'certificateFile',
        },
        // Certificate Store is only available on Windows
        ...(process.platform === 'win32' ? [{
            label: '$(shield) Certificate Store',
            description: 'Service principal with certificate from Windows certificate store',
            authMethodId: 'certificateStore',
        }] : []),
        {
            label: '$(account) Username & Password',
            description: 'Resource owner password credentials (legacy)',
            authMethodId: 'usernamePassword',
        },
    ];

    const selectedMethod = await vscode.window.showQuickPick(authMethods, {
        title: 'Create Profile (Step 1/3): Authentication Method',
        placeHolder: 'Select an authentication method',
    });

    if (!selectedMethod) {
        return; // User cancelled
    }

    // Step 2: Profile name
    const isSPN = ['clientSecret', 'certificateFile', 'certificateStore'].includes(
        selectedMethod.authMethodId,
    );

    const profileName = await vscode.window.showInputBox({
        title: 'Create Profile (Step 2/3): Profile Name',
        prompt: isSPN ? 'Enter a name for this profile (required for service principals)' : 'Enter a name for this profile (optional)',
        placeHolder: 'e.g., Dev Environment, Production SPN',
        validateInput: (value) => {
            if (isSPN && !value.trim()) {
                return 'Profile name is required for service principal authentication';
            }
            return undefined;
        },
    });

    if (profileName === undefined) {
        return; // User cancelled
    }

    // Step 3: Method-specific parameters
    const params = await collectAuthMethodParams(selectedMethod.authMethodId);
    if (!params) {
        return; // User cancelled during param collection
    }

    // Create the profile via daemon with progress indicator
    await vscode.window.withProgress(
        {
            location: vscode.ProgressLocation.Notification,
            title: 'Creating authentication profile...',
            cancellable: false,
        },
        async () => {
            await daemonClient.profilesCreate({
                name: profileName || undefined,
                authMethod: selectedMethod.authMethodId,
                ...params,
            });
        },
    );

    refreshProfiles();
    vscode.window.showInformationMessage(
        `Profile created successfully (${selectedMethod.authMethodId}, Name: "${profileName || '(auto)'}")`,
    );

    // Auto-launch environment selector so user can pick an environment right away
    await vscode.commands.executeCommand('ppds.selectEnvironment');
}

interface AuthParams {
    environmentUrl?: string;
    applicationId?: string;
    clientSecret?: string;
    tenantId?: string;
    certificatePath?: string;
    certificatePassword?: string;
    certificateThumbprint?: string;
    username?: string;
    password?: string;
}

async function collectAuthMethodParams(authMethodId: string): Promise<AuthParams | null> {
    const params: AuthParams = {};

    // Service principals always need an environment URL upfront.
    // User-based flows (deviceCode, interactive) can set it later via environment selector.
    const isUserBased = authMethodId === 'deviceCode' || authMethodId === 'interactive';

    const envUrl = await vscode.window.showInputBox({
        title: 'Create Profile (Step 3): Environment URL',
        prompt: isUserBased
            ? 'Enter the Dataverse environment URL (optional — you can select one after)'
            : 'Enter the Dataverse environment URL',
        placeHolder: 'https://org.crm.dynamics.com',
        validateInput: (value) => {
            if (!value.trim()) {
                return isUserBased ? undefined : 'Environment URL is required';
            }
            try {
                new URL(value);
            } catch {
                return 'Enter a valid URL (e.g., https://org.crm.dynamics.com)';
            }
            return undefined;
        },
    });

    if (envUrl === undefined) {
        return null;
    }
    if (envUrl.trim()) {
        params.environmentUrl = envUrl;
    }

    switch (authMethodId) {
        case 'deviceCode':
        case 'interactive':
            // No additional params needed for user-based auth
            break;

        case 'clientSecret': {
            const appId = await vscode.window.showInputBox({
                title: 'Create Profile: Application (Client) ID',
                prompt: 'Enter the Azure AD application (client) ID',
                placeHolder: 'xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx',
                validateInput: (value) => value.trim() ? undefined : 'Application ID is required',
            });
            if (appId === undefined) { return null; }
            params.applicationId = appId;

            const secret = await vscode.window.showInputBox({
                title: 'Create Profile: Client Secret',
                prompt: 'Enter the client secret',
                password: true,
                validateInput: (value) => value.trim() ? undefined : 'Client secret is required',
            });
            if (secret === undefined) { return null; }
            params.clientSecret = secret;

            const tenantId = await vscode.window.showInputBox({
                title: 'Create Profile: Tenant ID',
                prompt: 'Enter the Azure AD tenant ID',
                placeHolder: 'xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx',
                validateInput: (value) => value.trim() ? undefined : 'Tenant ID is required for service principal authentication',
            });
            if (tenantId === undefined) { return null; }
            params.tenantId = tenantId;
            break;
        }

        case 'certificateFile': {
            const appId = await vscode.window.showInputBox({
                title: 'Create Profile: Application (Client) ID',
                prompt: 'Enter the Azure AD application (client) ID',
                placeHolder: 'xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx',
                validateInput: (value) => value.trim() ? undefined : 'Application ID is required',
            });
            if (appId === undefined) { return null; }
            params.applicationId = appId;

            const certPath = await vscode.window.showInputBox({
                title: 'Create Profile: Certificate File Path',
                prompt: 'Enter the path to the certificate file (.pfx)',
                placeHolder: '/path/to/certificate.pfx',
                validateInput: (value) => value.trim() ? undefined : 'Certificate path is required',
            });
            if (certPath === undefined) { return null; }
            params.certificatePath = certPath;

            const certPassword = await vscode.window.showInputBox({
                title: 'Create Profile: Certificate Password',
                prompt: 'Enter the certificate password (leave empty if none)',
                password: true,
            });
            if (certPassword === undefined) { return null; }
            if (certPassword) {
                params.certificatePassword = certPassword;
            }

            const tenantId = await vscode.window.showInputBox({
                title: 'Create Profile: Tenant ID',
                prompt: 'Enter the Azure AD tenant ID',
                placeHolder: 'xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx',
                validateInput: (value) => value.trim() ? undefined : 'Tenant ID is required for service principal authentication',
            });
            if (tenantId === undefined) { return null; }
            params.tenantId = tenantId;
            break;
        }

        case 'certificateStore': {
            const appId = await vscode.window.showInputBox({
                title: 'Create Profile: Application (Client) ID',
                prompt: 'Enter the Azure AD application (client) ID',
                placeHolder: 'xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx',
                validateInput: (value) => value.trim() ? undefined : 'Application ID is required',
            });
            if (appId === undefined) { return null; }
            params.applicationId = appId;

            const thumbprint = await vscode.window.showInputBox({
                title: 'Create Profile: Certificate Thumbprint',
                prompt: 'Enter the certificate thumbprint from the Windows certificate store',
                placeHolder: 'A1B2C3D4E5F6...',
                validateInput: (value) => value.trim() ? undefined : 'Certificate thumbprint is required',
            });
            if (thumbprint === undefined) { return null; }
            params.certificateThumbprint = thumbprint;

            const tenantId = await vscode.window.showInputBox({
                title: 'Create Profile: Tenant ID',
                prompt: 'Enter the Azure AD tenant ID',
                placeHolder: 'xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx',
                validateInput: (value) => value.trim() ? undefined : 'Tenant ID is required for service principal authentication',
            });
            if (tenantId === undefined) { return null; }
            params.tenantId = tenantId;
            break;
        }

        case 'usernamePassword': {
            const username = await vscode.window.showInputBox({
                title: 'Create Profile: Username',
                prompt: 'Enter the username (email)',
                placeHolder: 'user@org.onmicrosoft.com',
                validateInput: (value) => value.trim() ? undefined : 'Username is required',
            });
            if (username === undefined) { return null; }
            params.username = username;

            const password = await vscode.window.showInputBox({
                title: 'Create Profile: Password',
                prompt: 'Enter the password',
                password: true,
                validateInput: (value) => value.trim() ? undefined : 'Password is required',
            });
            if (password === undefined) { return null; }
            params.password = password;
            break;
        }
    }

    return params;
}

// ── Delete Profile ──────────────────────────────────────────────────────────

async function runDeleteProfile(
    item: unknown,
    daemonClient: DaemonClient,
    refreshProfiles: () => void,
): Promise<void> {
    const profileItem = item as { profile?: { index: number; name: string | null; isActive?: boolean } } | undefined;
    if (!profileItem?.profile) {
        vscode.window.showWarningMessage('No profile selected. Use the context menu on a profile to delete it.');
        return;
    }

    const { name, index, isActive } = profileItem.profile;
    const displayName = name ?? `Profile ${index}`;

    const warningMessage = isActive
        ? `This is your active profile. Deleting "${displayName}" will sign you out. This cannot be undone.`
        : `Are you sure you want to delete profile "${displayName}"? This cannot be undone.`;

    const confirm = await vscode.window.showWarningMessage(
        warningMessage,
        { modal: true },
        'Delete',
    );

    if (confirm !== 'Delete') {
        return;
    }

    await daemonClient.profilesDelete(name ? { name } : { index });
    refreshProfiles();

    vscode.window.showInformationMessage(`Profile "${displayName}" deleted.`);
}

// ── Rename Profile ──────────────────────────────────────────────────────────

async function runRenameProfile(
    item: unknown,
    daemonClient: DaemonClient,
    refreshProfiles: () => void,
): Promise<void> {
    const profileItem = item as { profile?: { index: number; name: string | null } } | undefined;
    if (!profileItem?.profile) {
        vscode.window.showWarningMessage('No profile selected. Use the context menu on a profile to rename it.');
        return;
    }

    const { name, index } = profileItem.profile;
    const displayName = name ?? `Profile ${index}`;

    const newName = await vscode.window.showInputBox({
        title: `Rename Profile: ${displayName}`,
        prompt: 'Enter a new name for this profile',
        value: name ?? '',
        placeHolder: 'New profile name',
        validateInput: (value) => {
            if (!value.trim()) {
                return 'Profile name cannot be empty';
            }
            if (value.trim() === name) {
                return 'New name must be different from the current name';
            }
            return undefined;
        },
    });

    if (newName === undefined) {
        return; // User cancelled
    }

    // The daemon's profiles/rename RPC only accepts currentName as a string.
    // ProfileStore.GetByNameOrIndex supports plain index strings (e.g. "0"),
    // so passing index.toString() for unnamed profiles resolves correctly on
    // the C# side. Long-term, the daemon should expose an index-based overload.
    const currentName = name ?? index.toString();
    await daemonClient.profilesRename(currentName, newName.trim());
    refreshProfiles();

    vscode.window.showInformationMessage(`Profile renamed: "${displayName}" -> "${newName.trim()}"`);
}
