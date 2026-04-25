import * as vscode from 'vscode';

import type { DaemonClient, DaemonState } from './daemonClient.js';
import { withRpcTimeout } from './daemonClient.js';

interface ActiveProfileSummary {
    name: string | null;
    envName: string | null;
    authMethod: string | null;
}

/**
 * Single status bar item that conveys daemon state, the active profile, and the
 * profile's default environment. Replaces the previous DaemonStatusBar +
 * ProfileStatusBar pair so users see one unified PPDS indicator.
 */
export class PpdsStatusBar implements vscode.Disposable {
    private static readonly RPC_TIMEOUT_MS = 10_000;

    private readonly statusBarItem: vscode.StatusBarItem;
    private readonly disposables: vscode.Disposable[] = [];
    private readonly client: DaemonClient;
    private currentState: DaemonState;

    constructor(client: DaemonClient) {
        this.client = client;
        this.currentState = client.state;

        this.statusBarItem = vscode.window.createStatusBarItem(
            vscode.StatusBarAlignment.Left,
            50,
        );
        this.applyState(this.currentState);
        this.statusBarItem.show();

        this.disposables.push(
            client.onDidChangeState(state => {
                this.currentState = state;
                this.applyState(state);
            }),
            client.onDidReconnect(() => {
                if (this.currentState === 'ready') this.refresh();
            }),
            this.statusBarItem,
        );

        if (client.state === 'ready') {
            this.refresh();
        }
    }

    /**
     * Re-fetches the active profile summary and rerenders the bar. Safe to call
     * at any time — if the daemon is not in `ready` state the call is skipped.
     */
    refresh(): void {
        if (this.currentState !== 'ready') return;
        void withRpcTimeout(this.client.authList(), PpdsStatusBar.RPC_TIMEOUT_MS, 'authList')
            .then(result => {
                const active = result.profiles.find(p => p.isActive);
                let name: string | null = result.activeProfile;
                if (!name && active) name = active.name ?? `Profile ${active.index}`;

                const envName = active?.environment?.displayName ?? null;
                const authMethod = active?.authMethod ?? null;

                this.renderReady({ name, envName, authMethod });
            })
            .catch(() => {
                // Leave existing text on transient errors
            });
    }

    private applyState(state: DaemonState): void {
        switch (state) {
            case 'ready':
                this.statusBarItem.command = 'ppds.listProfiles';
                this.statusBarItem.backgroundColor = undefined;
                this.statusBarItem.text = '$(check) PPDS';
                this.statusBarItem.tooltip = 'PPDS Daemon: Connected';
                this.refresh();
                break;
            case 'starting':
            case 'reconnecting':
                this.statusBarItem.command = 'ppds.restartDaemon';
                this.statusBarItem.backgroundColor = undefined;
                this.statusBarItem.text = '$(sync~spin) PPDS';
                this.statusBarItem.tooltip = `PPDS Daemon: ${state === 'starting' ? 'Starting' : 'Reconnecting'}...`;
                break;
            case 'error':
                this.statusBarItem.command = 'ppds.restartDaemon';
                this.statusBarItem.backgroundColor = new vscode.ThemeColor('statusBarItem.errorBackground');
                this.statusBarItem.text = '$(error) PPDS';
                this.statusBarItem.tooltip = 'PPDS Daemon: Disconnected — click to restart';
                break;
            case 'stopped':
                this.statusBarItem.command = 'ppds.restartDaemon';
                this.statusBarItem.backgroundColor = undefined;
                this.statusBarItem.text = '$(circle-slash) PPDS';
                this.statusBarItem.tooltip = 'PPDS Daemon: Stopped';
                break;
        }
    }

    private renderReady(summary: ActiveProfileSummary): void {
        const { name, envName, authMethod } = summary;
        if (!name) {
            this.statusBarItem.text = '$(check) PPDS: No profile';
        } else if (envName) {
            this.statusBarItem.text = `$(check) PPDS: ${name} · ${envName}`;
        } else {
            this.statusBarItem.text = `$(check) PPDS: ${name}`;
        }

        const md = new vscode.MarkdownString();
        md.isTrusted = false;
        md.supportThemeIcons = true;
        md.appendMarkdown('**PPDS Daemon**: Connected\n\n');
        if (name) {
            md.appendMarkdown(`**Profile**: ${name}\n\n`);
        } else {
            md.appendMarkdown('**Profile**: _(none active)_\n\n');
        }
        md.appendMarkdown(`**Environment**: ${envName ?? '_(none selected)_'}\n\n`);
        md.appendMarkdown(`**Auth method**: ${authMethod ?? '_(unknown)_'}\n\n`);
        md.appendMarkdown('_Click to switch profile._');
        this.statusBarItem.tooltip = md;
    }

    dispose(): void {
        for (const d of this.disposables) d.dispose();
    }
}
