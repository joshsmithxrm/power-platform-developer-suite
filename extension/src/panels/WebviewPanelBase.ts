import * as vscode from 'vscode';
import type { DaemonClient } from '../daemonClient.js';

/**
 * Base class for webview panels with safe messaging.
 * Prevents "Webview is disposed" errors from async operations.
 */
export abstract class WebviewPanelBase implements vscode.Disposable {
    protected panel: vscode.WebviewPanel | undefined;
    protected disposables: vscode.Disposable[] = [];
    private _disposed = false;

    protected postMessage(message: unknown): void {
        this.panel?.webview.postMessage(message);
    }

    /**
     * Subscribes to daemon reconnect events. On reconnect, posts a
     * `daemonReconnected` message to the webview (shows the stale-data
     * banner) and calls the overridable `onDaemonReconnected` hook.
     */
    protected subscribeToDaemonReconnect(client: DaemonClient): void {
        this.disposables.push(
            client.onDidReconnect(() => {
                this.postMessage({ command: 'daemonReconnected' });
                this.onDaemonReconnected();
            })
        );
    }

    /** Override in subclasses to handle reconnection (e.g., auto-refresh). */
    protected onDaemonReconnected(): void {
        // Default: no-op
    }

    abstract getHtmlContent(webview: vscode.Webview): string;

    dispose(): void {
        if (this._disposed) return;
        this._disposed = true;

        this.panel?.dispose();

        for (const d of this.disposables) {
            d.dispose();
        }
        this.disposables = [];
    }
}
