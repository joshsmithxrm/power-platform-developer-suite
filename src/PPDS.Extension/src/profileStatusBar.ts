import * as vscode from 'vscode';

import type { DaemonClient, DaemonState } from './daemonClient.js';

/**
 * Status bar item that shows the currently active authentication profile.
 * Hidden when the daemon is not ready. Clicking it opens the profile quick pick
 * (`ppds.listProfiles`) to switch profiles.
 */
export class ProfileStatusBar implements vscode.Disposable {
    private readonly statusBarItem: vscode.StatusBarItem;
    private readonly disposables: vscode.Disposable[] = [];
    private readonly client: DaemonClient;

    constructor(client: DaemonClient) {
        this.client = client;

        this.statusBarItem = vscode.window.createStatusBarItem(
            vscode.StatusBarAlignment.Left,
            49, // Just right of the daemon status bar (priority 50)
        );
        this.statusBarItem.command = 'ppds.listProfiles';
        this.statusBarItem.tooltip = 'PPDS: Click to switch profile';

        // Start hidden — shown when daemon becomes ready
        this.updateForState(client.state);

        this.disposables.push(
            client.onDidChangeState(state => this.updateForState(state)),
            client.onDidReconnect(() => this.refresh()),
            this.statusBarItem,
        );

        // If already ready, fetch profile immediately
        if (client.state === 'ready') {
            this.refresh();
        }
    }

    /**
     * Fetches the current active profile from the daemon and updates the
     * status bar text. Safe to call at any time — errors are swallowed.
     */
    refresh(): void {
        void this.client.authList().then(result => {
            const name = result.activeProfile;
            this.statusBarItem.text = name
                ? `$(account) ${name}`
                : '$(account) No profile';
        }).catch(() => {
            // Daemon not ready or call failed — leave text as-is
        });
    }

    private updateForState(state: DaemonState): void {
        if (state === 'ready') {
            this.statusBarItem.show();
            this.refresh();
        } else {
            this.statusBarItem.hide();
        }
    }

    dispose(): void {
        for (const d of this.disposables) d.dispose();
    }
}
