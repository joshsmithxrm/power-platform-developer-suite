import * as vscode from 'vscode';

import type { DaemonClient } from '../daemonClient.js';

import { showEnvironmentPicker } from './environmentPicker.js';

/**
 * Base class for webview panels with safe messaging, lifecycle management,
 * and shared environment state.
 *
 * Subclasses create their `WebviewPanel` and call `initPanel(panel)` to wire:
 * - `onDidReceiveMessage` → `handleMessage()` (abstract, subclass implements)
 * - `onDidDispose` → `dispose()`
 *
 * Environment lifecycle is handled by `initializePanel()` and
 * `handleEnvironmentPickerClick()` — subclasses implement `onInitialized()`
 * and `onEnvironmentChanged()` hooks for panel-specific data loading.
 */
export abstract class WebviewPanelBase<
    TIncoming extends { command: string } = { command: string },
    TOutgoing extends { command: string } = { command: string; [key: string]: unknown },
> implements vscode.Disposable {
    protected panel: vscode.WebviewPanel | undefined;
    protected disposables: vscode.Disposable[] = [];
    private _disposed = false;
    private readonly _abortController = new AbortController();

    // ── Shared environment state ──────────────────────────────────────
    protected environmentUrl: string | undefined;
    protected environmentDisplayName: string | undefined;
    protected environmentType: string | null = null;
    protected environmentColor: string | null = null;
    protected environmentId: string | null = null;
    protected profileName: string | undefined;

    /** Human-readable panel label for the title bar (e.g., "Import Jobs"). */
    protected abstract readonly panelLabel: string;

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
                this.postMessage({ command: 'daemonReconnected' } as unknown as TOutgoing);
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

    /** Copy text to the system clipboard. Call from handleMessage for 'copyToClipboard'. */
    protected handleCopyToClipboard(text: string): void {
        void vscode.env.clipboard.writeText(text);
    }

    // ── Environment lifecycle ─────────────────────────────────────────

    /**
     * Resolve the environment ID (GUID) from the current environment URL.
     * Used for Maker Portal deep links. Maps via `daemon.envList()`.
     */
    protected async resolveEnvironmentId(daemon: DaemonClient): Promise<string | null> {
        if (!this.environmentUrl) return null;
        try {
            const normalise = (u: string): string => u.replace(/\/+$/, '').toLowerCase();
            const targetUrl = normalise(this.environmentUrl);
            const envResult = await daemon.envList();
            const match = envResult.environments.find(
                e => normalise(e.apiUrl) === targetUrl || (e.url && normalise(e.url) === targetUrl)
            );
            return match?.environmentId ?? null;
        } catch {
            return null;
        }
    }

    /**
     * Update the panel title bar with profile name, environment, and panel label.
     * @param panelId Numeric instance ID for multi-panel suffix
     * @param multipleInstances Whether to show the ` #N` suffix
     */
    protected updatePanelTitle(panelId: number, multipleInstances: boolean): void {
        if (!this.panel) return;
        const ctx = [this.profileName, this.environmentDisplayName].filter(Boolean).join(' \u00B7 ');
        const suffix = multipleInstances ? ` ${panelId}` : '';
        this.panel.title = ctx ? `${ctx} \u2014 ${this.panelLabel}${suffix}` : `${this.panelLabel}${suffix}`;
    }

    /**
     * Standard initialization flow: auth → env resolution → config → title → data load.
     * Called from subclass `handleMessage` when receiving the 'ready' message.
     */
    protected async initializePanel(daemon: DaemonClient, panelId: number, multipleInstances: boolean): Promise<void> {
        try {
            const who = await daemon.authWho();
            this.profileName = who.name ?? `Profile ${who.index}`;
            if (!this.environmentUrl && who.environment?.url) {
                this.environmentUrl = who.environment.url;
                this.environmentDisplayName = who.environment.displayName || who.environment.url;
            }
            this.environmentType = who.environment?.type ?? null;
            if (who.environment?.environmentId) {
                this.environmentId = who.environment.environmentId;
            } else {
                this.environmentId = await this.resolveEnvironmentId(daemon);
            }
            if (this.environmentUrl) {
                try {
                    const config = await daemon.envConfigGet(this.environmentUrl);
                    this.environmentColor = config.resolvedColor ?? null;
                    if (!this.environmentType) {
                        this.environmentType = config.resolvedType ?? null;
                    }
                } catch (err) {
                    // eslint-disable-next-line no-console -- non-critical: color accent unavailable
                    console.warn(`[PPDS] Failed to fetch environment config: ${err instanceof Error ? err.message : String(err)}`);
                    this.environmentColor = null;
                }
            }
            this.updatePanelTitle(panelId, multipleInstances);
            this.postMessage({
                command: 'updateEnvironment',
                name: this.environmentDisplayName ?? 'No environment',
                envType: this.environmentType,
                envColor: this.environmentColor,
            } as unknown as TOutgoing);
            await this.onInitialized();
        } catch (error) {
            const msg = error instanceof Error ? error.message : String(error);
            this.postMessage({ command: 'error', message: `Failed to initialize: ${msg}` } as unknown as TOutgoing);
        }
    }

    /**
     * Standard environment picker flow: pick → update state → config → title → data reload.
     * Called from subclass `handleMessage` when receiving 'requestEnvironmentList'.
     */
    protected async handleEnvironmentPickerClick(daemon: DaemonClient, panelId: number, multipleInstances: boolean): Promise<void> {
        const result = await showEnvironmentPicker(daemon, this.environmentUrl);
        if (!result) return;

        this.environmentUrl = result.url;
        this.environmentDisplayName = result.displayName;
        this.environmentType = result.type;
        this.environmentId = await this.resolveEnvironmentId(daemon);
        try {
            const config = await daemon.envConfigGet(result.url);
            this.environmentColor = config.resolvedColor ?? null;
            if (!this.environmentType && config.resolvedType) {
                this.environmentType = config.resolvedType;
            }
        } catch {
            this.environmentColor = null;
        }
        this.updatePanelTitle(panelId, multipleInstances);
        this.postMessage({
            command: 'updateEnvironment',
            name: result.displayName,
            envType: this.environmentType,
            envColor: this.environmentColor,
        } as unknown as TOutgoing);
        await this.onEnvironmentChanged();
    }

    /**
     * Hook called after initializePanel() completes environment setup.
     * Subclasses load their primary data here.
     */
    protected abstract onInitialized(): Promise<void>;

    /**
     * Hook called after environment picker changes the environment.
     * Subclasses reload data for the new environment here.
     */
    protected abstract onEnvironmentChanged(): Promise<void>;

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
