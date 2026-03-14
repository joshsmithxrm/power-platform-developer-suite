import type { DaemonClient } from '../daemonClient.js';

/**
 * Returns the CSS for the environment picker dropdown.
 */
export function getEnvironmentPickerCss(): string {
    return `
    .env-picker-container { display: flex; align-items: center; gap: 8px; }
    .env-picker-btn {
        display: flex; align-items: center; gap: 4px;
        background: var(--vscode-input-background);
        color: var(--vscode-input-foreground);
        border: 1px solid var(--vscode-input-border, var(--vscode-panel-border));
        padding: 2px 8px; border-radius: 2px; cursor: pointer;
        font-size: 12px; white-space: nowrap; max-width: 300px; overflow: hidden; text-overflow: ellipsis;
    }
    .env-picker-btn:hover { border-color: var(--vscode-focusBorder); }
    .env-picker-label { font-size: 11px; color: var(--vscode-descriptionForeground); }
    `;
}

/**
 * Returns the HTML for the environment picker button (placed in panel toolbar).
 * The button shows the current environment name and opens a picker on click.
 */
export function getEnvironmentPickerHtml(): string {
    return `
    <div class="env-picker-container">
        <span class="env-picker-label">Environment:</span>
        <button class="env-picker-btn" id="env-picker-btn" title="Click to change environment">
            <span id="env-picker-name">Loading...</span>
            <span>&#9662;</span>
        </button>
    </div>`;
}

/**
 * Returns the JavaScript for the environment picker (runs inside webview).
 * Handles click on the env picker button to request environment list from host.
 */
export function getEnvironmentPickerJs(): string {
    return `
    const envPickerBtn = document.getElementById('env-picker-btn');
    const envPickerName = document.getElementById('env-picker-name');

    envPickerBtn.addEventListener('click', () => {
        vscode.postMessage({ command: 'requestEnvironmentList' });
    });

    function updateEnvironmentDisplay(name) {
        envPickerName.textContent = name || 'No environment';
    }
    `;
}

/**
 * Represents an environment option for the picker.
 */
export interface EnvironmentOption {
    label: string;
    url: string;
    detail?: string;
    isCurrent?: boolean;
}

/**
 * Shows a VS Code QuickPick for environment selection.
 * Returns the selected environment URL and display name, or undefined if cancelled.
 */
export async function showEnvironmentPicker(
    daemon: DaemonClient,
    currentUrl?: string,
): Promise<{ url: string; displayName: string } | undefined> {
    const vscode = await import('vscode');

    let environments: EnvironmentOption[] = [];
    try {
        const result = await vscode.window.withProgress(
            { location: vscode.ProgressLocation.Notification, title: 'Loading environments...' },
            async () => daemon.envList(),
        );
        environments = result.environments.map(env => ({
            label: env.friendlyName,
            url: env.apiUrl,
            detail: env.region ? `${env.apiUrl} (${env.region})` : env.apiUrl,
            isCurrent: env.apiUrl === currentUrl,
        }));
    } catch {
        // Discovery failed — still allow manual entry
    }

    const items = environments.map(env => ({
        label: env.isCurrent ? `$(check) ${env.label}` : env.label,
        description: env.isCurrent ? 'current' : undefined,
        detail: env.detail,
        url: env.url,
        displayName: env.label,
    }));

    // Add manual entry option
    items.push({
        label: '$(link) Enter URL manually...',
        description: '',
        detail: 'Connect to an environment not in the list',
        url: '__manual__',
        displayName: '',
    });

    const selected = await vscode.window.showQuickPick(items, {
        title: 'Select Environment',
        placeHolder: 'Choose an environment for this panel',
        matchOnDetail: true,
    });

    if (!selected) return undefined;

    if (selected.url === '__manual__') {
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
        if (!url) return undefined;
        const trimmed = url.trim();
        return { url: trimmed, displayName: trimmed };
    }

    return { url: selected.url, displayName: selected.displayName };
}
