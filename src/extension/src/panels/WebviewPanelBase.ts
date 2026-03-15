import * as vscode from 'vscode';

import type { DaemonClient } from '../daemonClient.js';

/**
 * Base class for webview panels with safe messaging.
 * Prevents "Webview is disposed" errors from async operations.
 */
export abstract class WebviewPanelBase<
    TIncoming extends { command: string } = { command: string },
    TOutgoing extends { command: string } = { command: string; [key: string]: unknown },
> implements vscode.Disposable {
    protected panel: vscode.WebviewPanel | undefined;
    protected disposables: vscode.Disposable[] = [];
    private _disposed = false;
    private readonly _abortController = new AbortController();

    /** Fires when the panel is disposed. Pass to async operations so they can bail out early. */
    protected get abortSignal(): AbortSignal {
        return this._abortController.signal;
    }

    protected postMessage(message: TOutgoing): void {
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
                // Cast required: `daemonReconnected` is a shared protocol command that
                // every panel's TOutgoing includes, but TypeScript can't verify that
                // structurally from the base class.
                this.postMessage({ command: 'daemonReconnected' } as TOutgoing);
                this.onDaemonReconnected();
            })
        );
    }

    /** Override in subclasses to handle reconnection (e.g., auto-refresh). */
    protected onDaemonReconnected(): void {
        // Default: no-op
    }

    /** Override in subclasses to handle incoming messages from the webview. */
     
    protected handleMessage(_message: TIncoming): void {
        // Default: no-op. Subclasses override to handle typed incoming messages.
    }

    abstract getHtmlContent(webview: vscode.Webview): string;

    dispose(): void {
        if (this._disposed) return;
        this._disposed = true;

        this._abortController.abort();

        this.panel?.dispose();

        for (const d of this.disposables) {
            d.dispose();
        }
        this.disposables = [];
    }
}
