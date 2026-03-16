import * as vscode from 'vscode';

import type { DaemonClient, DaemonState } from './daemonClient.js';

export class DaemonStatusBar implements vscode.Disposable {
    private readonly statusBarItem: vscode.StatusBarItem;
    private readonly disposables: vscode.Disposable[] = [];

    constructor(client: DaemonClient) {
        this.statusBarItem = vscode.window.createStatusBarItem(
            vscode.StatusBarAlignment.Left,
            50
        );
        this.statusBarItem.command = 'ppds.restartDaemon';
        this.updateState(client.state);
        this.statusBarItem.show();

        this.disposables.push(
            client.onDidChangeState(state => this.updateState(state)),
            this.statusBarItem,
        );
    }

    private updateState(state: DaemonState): void {
        switch (state) {
            case 'ready':
                this.statusBarItem.text = '$(check) PPDS';
                this.statusBarItem.tooltip = 'PPDS Daemon: Connected';
                this.statusBarItem.backgroundColor = undefined;
                break;
            case 'starting':
            case 'reconnecting':
                this.statusBarItem.text = '$(sync~spin) PPDS';
                this.statusBarItem.tooltip = `PPDS Daemon: ${state === 'starting' ? 'Starting' : 'Reconnecting'}...`;
                this.statusBarItem.backgroundColor = undefined;
                break;
            case 'error':
                this.statusBarItem.text = '$(error) PPDS';
                this.statusBarItem.tooltip = 'PPDS Daemon: Disconnected — click to restart';
                this.statusBarItem.backgroundColor = new vscode.ThemeColor('statusBarItem.errorBackground');
                break;
            case 'stopped':
                this.statusBarItem.text = '$(circle-slash) PPDS';
                this.statusBarItem.tooltip = 'PPDS Daemon: Stopped';
                this.statusBarItem.backgroundColor = undefined;
                break;
        }
    }

    dispose(): void {
        for (const d of this.disposables) d.dispose();
    }
}
