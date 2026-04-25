import type * as vscode from 'vscode';

import type { DaemonClient } from '../daemonClient.js';
import type { ProfileInfo, EnvironmentInfo } from '../types.js';

/**
 * Returns the HTML for the environment picker button (placed in panel toolbar).
 * The button shows the current profile + environment label and opens a picker on click.
 */
export function getEnvironmentPickerHtml(): string {
    return `
    <div class="env-picker-container">
        <span class="env-picker-label">Context:</span>
        <button class="env-picker-btn" id="env-picker-btn" title="Click to change profile and environment">
            <span id="env-picker-name">Loading...</span>
            <span>&#9662;</span>
        </button>
    </div>`;
}

/**
 * Result returned by {@link showContextPicker} on a successful selection.
 */
export interface ContextPickerResult {
    profileName: string;
    url: string;
    displayName: string;
    type: string | null;
}

interface ProfileEnvironmentBucket {
    profile: ProfileInfo;
    environments: EnvironmentInfo[];
    discoveryFailed: boolean;
}

/**
 * Profile- and environment-aware picker. Lists every profile's environments grouped
 * under a profile separator so users can pick a (profile, environment) pair in one click.
 *
 * Falls back to each profile's saved environment when discovery fails (for SPN profiles
 * where Global Discovery may not be reachable).
 */
export async function showContextPicker(
    daemon: DaemonClient,
    currentProfileName?: string,
    currentUrl?: string,
): Promise<ContextPickerResult | undefined> {
    const vscode = await import('vscode');

    const auth = await vscode.window.withProgress(
        { location: vscode.ProgressLocation.Notification, title: 'Loading profiles and environments...' },
        async () => daemon.authList(),
    );

    if (auth.profiles.length === 0) {
        await vscode.window.showInformationMessage(
            'No authentication profiles found. Use "ppds auth create" to create one.',
        );
        return undefined;
    }

    const buckets = await loadEnvironmentsForProfiles(daemon, auth.profiles);

    type Item = vscode.QuickPickItem & { profileName?: string; url?: string; displayName?: string; type?: string | null; manual?: boolean };
    const items: Item[] = [];
    const activeProfileName = auth.activeProfile;

    for (const bucket of buckets) {
        const pName = bucket.profile.name ?? `Profile ${bucket.profile.index}`;
        const isActive = activeProfileName != null && pName === activeProfileName;
        const headerLabel = isActive ? `${pName} (active)` : pName;
        items.push({
            label: bucket.discoveryFailed ? `${headerLabel} — discovery unavailable` : headerLabel,
            kind: vscode.QuickPickItemKind.Separator,
        });

        if (bucket.environments.length === 0) {
            items.push({
                label: bucket.discoveryFailed
                    ? '$(warning) Discovery failed — saved environment unavailable'
                    : '$(info) No environments configured',
                description: bucket.profile.environment?.displayName ?? undefined,
                detail: bucket.profile.environment?.url ?? undefined,
                profileName: pName,
                url: bucket.profile.environment?.url ?? '',
                displayName: bucket.profile.environment?.displayName ?? '',
                type: null,
            });
            continue;
        }

        for (const env of bucket.environments) {
            const friendly = env.friendlyName || env.apiUrl;
            const isCurrent = currentProfileName === pName && currentUrl != null && env.apiUrl === currentUrl;
            items.push({
                label: isCurrent ? `$(check) ${friendly}` : friendly,
                description: isCurrent ? 'current' : undefined,
                detail: env.region ? `${env.apiUrl} (${env.region})` : env.apiUrl,
                profileName: pName,
                url: env.apiUrl,
                displayName: friendly,
                type: env.type ?? null,
            });
        }
    }

    items.push({
        label: '$(link) Enter URL manually...',
        description: '',
        detail: 'Connect to an environment not in the list',
        manual: true,
    });

    const selected = await vscode.window.showQuickPick(items, {
        title: 'Select Profile & Environment',
        placeHolder: 'Choose a profile and environment for this panel',
        matchOnDetail: true,
    });
    if (!selected) return undefined;

    if (selected.manual) {
        return promptManualUrl(daemon, vscode, auth.profiles);
    }

    if (!selected.url || !selected.profileName) return undefined;

    return {
        profileName: selected.profileName,
        url: selected.url,
        displayName: selected.displayName ?? selected.url,
        type: selected.type ?? null,
    };
}

async function loadEnvironmentsForProfiles(
    daemon: DaemonClient,
    profiles: ProfileInfo[],
): Promise<ProfileEnvironmentBucket[]> {
    const promises = profiles.map(async profile => {
        const profileKey = profile.name ?? String(profile.index);
        try {
            const result = await daemon.envList(undefined, undefined, profileKey);
            return { profile, environments: result.environments, discoveryFailed: false };
        } catch {
            const fallback: EnvironmentInfo[] = [];
            if (profile.environment?.url) {
                fallback.push({
                    id: '',
                    environmentId: profile.environment.environmentId ?? null,
                    friendlyName: profile.environment.displayName,
                    uniqueName: '',
                    apiUrl: profile.environment.url,
                    url: profile.environment.url,
                    type: null,
                    state: 'Unknown',
                    region: null,
                    version: null,
                    isActive: false,
                    source: 'configured',
                });
            }
            return { profile, environments: fallback, discoveryFailed: true };
        }
    });
    return Promise.all(promises);
}

async function promptManualUrl(
    daemon: DaemonClient,
    vscode: typeof import('vscode'),
    profiles: ProfileInfo[],
): Promise<ContextPickerResult | undefined> {
    const url = await vscode.window.showInputBox({
        title: 'Dataverse Environment URL',
        prompt: 'Enter the full URL (e.g., https://myorg.crm.dynamics.com)',
        placeHolder: 'https://myorg.crm.dynamics.com',
        ignoreFocusOut: true,
        validateInput: value => {
            if (!value.trim()) return 'URL is required';
            try { new URL(value.trim()); return undefined; }
            catch { return 'Enter a valid URL'; }
        },
    });
    if (!url) return undefined;
    const trimmed = url.trim();

    const profileItems = profiles.map(p => ({
        label: p.name ?? `Profile ${p.index}`,
        description: p.identity ?? undefined,
        profileName: p.name ?? String(p.index),
    }));
    const chosen = await vscode.window.showQuickPick(profileItems, {
        title: 'Connect with which profile?',
        placeHolder: 'Pick the profile that should authenticate against this URL',
        ignoreFocusOut: true,
    });
    if (!chosen) return undefined;

    try { await daemon.envConfigSet({ environmentUrl: trimmed }); } catch { /* best-effort */ }
    return { profileName: chosen.profileName, url: trimmed, displayName: trimmed, type: null };
}

/**
 * Backwards-compatible wrapper for the legacy environment-only picker.
 * @deprecated Use {@link showContextPicker} so the caller also receives the selected profile.
 */
export async function showEnvironmentPicker(
    daemon: DaemonClient,
    currentUrl?: string,
): Promise<{ url: string; displayName: string; type: string | null } | undefined> {
    const result = await showContextPicker(daemon, undefined, currentUrl);
    if (!result) return undefined;
    return { url: result.url, displayName: result.displayName, type: result.type };
}
