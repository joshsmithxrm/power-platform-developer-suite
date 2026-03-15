import * as vscode from 'vscode';

import type { DaemonClient } from '../daemonClient.js';

/**
 * Base class for webview panels with safe messaging and lifecycle management.
 *
 * Subclasses create their `WebviewPanel` and call `initPanel(panel)` to wire:
 * - `onDidReceiveMessage` → `handleMessage()` (abstract, subclass implements)
 * - `onDidDispose` → `dispose()`
 *
 * This eliminates per-panel boilerplate for lifecycle wiring.
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

    /**
     * Wire lifecycle listeners on a newly-created webview panel.
     * Call this from the subclass constructor after creating the panel
     * and setting its HTML content.
     */
    protected initPanel(panel: vscode.WebviewPanel): void {
        this.panel = panel;
        this.disposables.push(
            panel.webview.onDidReceiveMessage((msg: TIncoming) => {
                try {
                    const result = this.handleMessage(msg);
                    if (result instanceof Promise) {
                        result.catch((err: unknown) => {
                            const errMsg = err instanceof Error ? err.message : String(err);
                            // eslint-disable-next-line no-console -- unhandled message handler error
                            console.error(`[PPDS] Unhandled message error: ${errMsg}`);
                        });
                    }
                } catch (err) {
                    const errMsg = err instanceof Error ? err.message : String(err);
                    // eslint-disable-next-line no-console -- unhandled message handler error
                    console.error(`[PPDS] Unhandled message error: ${errMsg}`);
                }
            }),
            panel.onDidDispose(() => this.dispose()),
        );
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

    /**
     * Log a webview-side error and show it to the user.
     * Call from subclass `handleMessage` when receiving a `webviewError` message.
     */
    protected logWebviewError(error: string, stack?: string): void {
        // eslint-disable-next-line no-console -- forwarding webview errors to dev console for diagnostics
        console.error(`[PPDS Webview] ${error}`);
        // eslint-disable-next-line no-console -- forwarding webview errors to dev console for diagnostics
        if (stack) console.error(`[PPDS Webview Stack] ${stack}`);
        vscode.window.showErrorMessage(`PPDS: ${error}`);
    }

    /** Handle an incoming message from the webview. Subclasses implement their message switch here. */
    protected abstract handleMessage(message: TIncoming): Promise<void> | void;

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
