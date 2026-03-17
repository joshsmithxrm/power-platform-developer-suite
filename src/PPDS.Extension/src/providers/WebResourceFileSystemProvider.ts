import * as vscode from 'vscode';

import type { DaemonClient } from '../daemonClient.js';

import {
    parseWebResourceUri,
    createWebResourceUri,
    getLanguageId,
    isBinaryType,
    type WebResourceContentMode,
} from './webResourceUri.js';

interface ServerState {
    modifiedOn: string;
    lastKnownContent: Uint8Array;
}

/**
 * VS Code FileSystemProvider for Dataverse web resources.
 *
 * URI scheme: ppds-webresource:///{environmentId}/{webResourceId}/{filename}?mode=...
 *
 * Content modes:
 * - unpublished (default) — latest saved content, editable
 * - published — currently published content, read-only (for diff)
 * - server-current — fresh fetch bypassing cache (for conflict diff)
 * - local-pending — pending save content stored locally (for conflict diff)
 */
export class WebResourceFileSystemProvider implements vscode.FileSystemProvider, vscode.Disposable {
    // FSP event emitters (required by interface)
    private readonly _onDidChangeFile = new vscode.EventEmitter<vscode.FileChangeEvent[]>();
    readonly onDidChangeFile = this._onDidChangeFile.event;

    // Custom event for panel auto-refresh
    private readonly _onDidSaveWebResource = new vscode.EventEmitter<{ environmentId: string; webResourceId: string }>();
    readonly onDidSaveWebResource = this._onDidSaveWebResource.event;

    // State maps (keyed by "envId:resourceId")
    private readonly serverState = new Map<string, ServerState>();
    private readonly pendingFetches = new Map<string, Promise<Uint8Array>>();
    private readonly preFetchedContent = new Map<string, Uint8Array>();
    private readonly pendingSaveContent = new Map<string, Uint8Array>();

    // Environment URL mapping (envId -> environmentUrl for RPC calls)
    private readonly environmentUrls = new Map<string, string>();

    constructor(private readonly daemon: DaemonClient) {}

    dispose(): void {
        this._onDidChangeFile.dispose();
        this._onDidSaveWebResource.dispose();
        this.serverState.clear();
        this.pendingFetches.clear();
        this.preFetchedContent.clear();
        this.pendingSaveContent.clear();
    }

    // ── Public API ────────────────────────────────────────────────────────

    /**
     * Maps an environmentId to its URL for RPC calls.
     * Called by WebResourcesPanel when the panel opens or the environment changes.
     */
    registerEnvironment(environmentId: string, environmentUrl: string): void {
        this.environmentUrls.set(environmentId, environmentUrl);
    }

    // ── FileSystemProvider interface ──────────────────────────────────────

    watch(): vscode.Disposable {
        // No-op — we don't watch for external changes
        return new vscode.Disposable(() => {});
    }

    stat(uri: vscode.Uri): vscode.FileStat {
        const parsed = parseWebResourceUri(uri);
        const now = Date.now();

        // Determine permissions: read-only for published mode and binary types
        const isReadOnly = parsed.mode === 'published'
            || parsed.mode === 'server-current'
            || parsed.mode === 'local-pending';

        return {
            type: vscode.FileType.File,
            size: 0,
            ctime: now,
            mtime: now,
            permissions: isReadOnly ? vscode.FilePermission.Readonly : undefined,
        };
    }

    readDirectory(): [string, vscode.FileType][] {
        return [];
    }

    async readFile(uri: vscode.Uri): Promise<Uint8Array> {
        const parsed = parseWebResourceUri(uri);
        const key = `${parsed.environmentId}:${parsed.webResourceId}`;
        const envUrl = this.environmentUrls.get(parsed.environmentId);

        switch (parsed.mode) {
            case 'unpublished':
                return this.readUnpublished(key, parsed.webResourceId, envUrl);
            case 'published':
                return this.readPublished(parsed.webResourceId, envUrl);
            case 'server-current':
                return this.readServerCurrent(parsed.webResourceId, envUrl);
            case 'local-pending':
                return this.readLocalPending(key);
        }
    }

    async writeFile(uri: vscode.Uri, content: Uint8Array): Promise<void> {
        const parsed = parseWebResourceUri(uri);

        // Only unpublished mode is writable
        if (parsed.mode !== 'unpublished') {
            throw vscode.FileSystemError.NoPermissions(uri);
        }

        const key = `${parsed.environmentId}:${parsed.webResourceId}`;
        const envUrl = this.environmentUrls.get(parsed.environmentId);
        const textContent = new TextDecoder().decode(content);

        // No-change detection: skip save if content is identical to last known
        const existing = this.serverState.get(key);
        if (existing && this.bytesEqual(content, existing.lastKnownContent)) {
            return;
        }

        // Conflict detection (only if we have prior server state)
        if (existing) {
            const conflictDetected = await this.checkForConflict(
                key, parsed, envUrl, content, textContent,
            );
            if (conflictDetected) {
                return; // User chose to discard or cancelled
            }
        }

        // Save to server
        await this.daemon.webResourcesUpdate(parsed.webResourceId, textContent, envUrl);

        // Refresh cached state
        const { modifiedOn } = await this.daemon.webResourcesGetModifiedOn(
            parsed.webResourceId, envUrl,
        );
        this.serverState.set(key, {
            modifiedOn: modifiedOn ?? new Date().toISOString(),
            lastKnownContent: content,
        });

        // Fire events
        this._onDidChangeFile.fire([{ type: vscode.FileChangeType.Changed, uri }]);
        this._onDidSaveWebResource.fire({
            environmentId: parsed.environmentId,
            webResourceId: parsed.webResourceId,
        });

        // Non-modal notification with publish option
        const action = await vscode.window.showInformationMessage(
            `Saved: ${parsed.filename}`,
            'Publish',
        );
        if (action === 'Publish') {
            await this.daemon.webResourcesPublish([parsed.webResourceId], envUrl);
            vscode.window.showInformationMessage(`Published: ${parsed.filename}`);
        }
    }

    createDirectory(): void {
        throw vscode.FileSystemError.NoPermissions();
    }

    delete(): void {
        throw vscode.FileSystemError.NoPermissions();
    }

    rename(): void {
        throw vscode.FileSystemError.NoPermissions();
    }

    // ── Opening flow (called from panel) ─────────────────────────────────

    /**
     * Opens a web resource in a VS Code text editor.
     *
     * For text resources, fetches both published and unpublished content.
     * If they differ, offers a quick pick to choose which version to view.
     */
    async openWebResource(
        environmentId: string,
        environmentUrl: string,
        webResourceId: string,
        name: string,
        webResourceType: number,
    ): Promise<void> {
        // Register the environment mapping
        this.registerEnvironment(environmentId, environmentUrl);

        if (isBinaryType(webResourceType)) {
            vscode.window.showInformationMessage(
                `"${name}" is a binary web resource and cannot be edited in VS Code.`,
            );
            return;
        }

        // Fetch published + unpublished in parallel
        const [publishedResult, unpublishedResult] = await Promise.all([
            this.daemon.webResourcesGet(webResourceId, true, environmentUrl),
            this.daemon.webResourcesGet(webResourceId, false, environmentUrl),
        ]);

        const publishedContent = publishedResult.resource?.content ?? '';
        const unpublishedContent = unpublishedResult.resource?.content ?? '';

        // Track server state for conflict detection
        const key = `${environmentId}:${webResourceId}`;
        if (unpublishedResult.resource?.modifiedOn) {
            const encoded = new TextEncoder().encode(unpublishedContent);
            this.serverState.set(key, {
                modifiedOn: unpublishedResult.resource.modifiedOn,
                lastKnownContent: encoded,
            });
        }

        let mode: WebResourceContentMode = 'unpublished';

        if (publishedContent !== unpublishedContent) {
            const choice = await vscode.window.showQuickPick(
                [
                    { label: 'Edit Unpublished', description: 'Latest saved version (editable)', picked: true, mode: 'unpublished' as const },
                    { label: 'View Published (read-only)', description: 'Currently published version', mode: 'published' as const },
                ],
                { title: `"${name}" has unpublished changes`, placeHolder: 'Which version do you want to open?' },
            );
            if (!choice) return; // User cancelled
            mode = choice.mode;
        }

        // Pre-fetch content for instant delivery
        const content = mode === 'published' ? publishedContent : unpublishedContent;
        const encoded = new TextEncoder().encode(content);
        this.preFetchedContent.set(key, encoded);

        const uri = createWebResourceUri(environmentId, webResourceId, name, mode);
        const doc = await vscode.workspace.openTextDocument(uri);
        await vscode.window.showTextDocument(doc, { preview: false });

        // Set language mode from web resource type
        const languageId = getLanguageId(webResourceType);
        if (languageId) {
            await vscode.languages.setTextDocumentLanguage(doc, languageId);
        }
    }

    // ── Private helpers ──────────────────────────────────────────────────

    private async readUnpublished(
        key: string,
        webResourceId: string,
        envUrl: string | undefined,
    ): Promise<Uint8Array> {
        // Check pre-fetched content first (set by openWebResource for instant delivery)
        const preFetched = this.preFetchedContent.get(key);
        if (preFetched) {
            this.preFetchedContent.delete(key);
            return preFetched;
        }

        // Deduplicate concurrent fetches for the same resource
        const pendingKey = `unpublished:${key}`;
        const pending = this.pendingFetches.get(pendingKey);
        if (pending) {
            return pending;
        }

        const fetchPromise = (async (): Promise<Uint8Array> => {
            try {
                const result = await this.daemon.webResourcesGet(webResourceId, false, envUrl);
                const content = result.resource?.content ?? '';
                const encoded = new TextEncoder().encode(content);

                // Update server state for conflict detection
                if (result.resource?.modifiedOn) {
                    this.serverState.set(key, {
                        modifiedOn: result.resource.modifiedOn,
                        lastKnownContent: encoded,
                    });
                }

                return encoded;
            } finally {
                this.pendingFetches.delete(pendingKey);
            }
        })();

        this.pendingFetches.set(pendingKey, fetchPromise);
        return fetchPromise;
    }

    private async readPublished(
        webResourceId: string,
        envUrl: string | undefined,
    ): Promise<Uint8Array> {
        const result = await this.daemon.webResourcesGet(webResourceId, true, envUrl);
        const content = result.resource?.content ?? '';
        return new TextEncoder().encode(content);
    }

    private async readServerCurrent(
        webResourceId: string,
        envUrl: string | undefined,
    ): Promise<Uint8Array> {
        // Fresh fetch — no caching, bypasses everything
        const result = await this.daemon.webResourcesGet(webResourceId, false, envUrl);
        const content = result.resource?.content ?? '';
        return new TextEncoder().encode(content);
    }

    private readLocalPending(key: string): Uint8Array {
        const content = this.pendingSaveContent.get(key);
        if (!content) {
            throw vscode.FileSystemError.FileNotFound();
        }
        return content;
    }

    /**
     * Checks for conflict before saving. Returns true if the save should be aborted
     * (user chose to discard or cancel), false if the save should proceed.
     */
    private async checkForConflict(
        key: string,
        parsed: { environmentId: string; webResourceId: string; filename: string },
        envUrl: string | undefined,
        content: Uint8Array,
        textContent: string,
    ): Promise<boolean> {
        const existing = this.serverState.get(key);
        if (!existing) return false;

        const { modifiedOn } = await this.daemon.webResourcesGetModifiedOn(
            parsed.webResourceId, envUrl,
        );

        if (!modifiedOn || modifiedOn === existing.modifiedOn) {
            return false; // No conflict
        }

        // Conflict detected — show modal
        const choice = await vscode.window.showWarningMessage(
            `"${parsed.filename}" was modified on the server since you opened it.`,
            { modal: true },
            'Compare First',
            'Overwrite',
            'Discard My Work',
        );

        if (choice === 'Overwrite') {
            return false; // Proceed with save
        }

        if (choice === 'Discard My Work') {
            // Reload from server by firing a change event
            const uri = createWebResourceUri(
                parsed.environmentId,
                parsed.webResourceId,
                parsed.filename,
                'unpublished',
            );
            // Clear server state so next readFile fetches fresh
            this.serverState.delete(key);
            this._onDidChangeFile.fire([{ type: vscode.FileChangeType.Changed, uri }]);
            return true; // Abort save
        }

        if (choice === 'Compare First') {
            // Store local content for the diff view
            this.pendingSaveContent.set(key, content);

            try {
                const serverUri = createWebResourceUri(
                    parsed.environmentId,
                    parsed.webResourceId,
                    parsed.filename,
                    'server-current',
                );
                const localUri = createWebResourceUri(
                    parsed.environmentId,
                    parsed.webResourceId,
                    parsed.filename,
                    'local-pending',
                );

                await vscode.commands.executeCommand(
                    'vscode.diff',
                    serverUri,
                    localUri,
                    `Server \u2194 Local: ${parsed.filename}`,
                );

                // Show resolution modal after diff opens
                const resolution = await vscode.window.showWarningMessage(
                    'How would you like to resolve this conflict?',
                    { modal: true },
                    'Save My Version',
                    'Use Server Version',
                    'Cancel',
                );

                if (resolution === 'Save My Version') {
                    // Proceed with save — caller will continue to the save step
                    // Update textContent won't change since we already have it
                    await this.daemon.webResourcesUpdate(
                        parsed.webResourceId, textContent, envUrl,
                    );
                    const freshModifiedOn = await this.daemon.webResourcesGetModifiedOn(
                        parsed.webResourceId, envUrl,
                    );
                    this.serverState.set(key, {
                        modifiedOn: freshModifiedOn.modifiedOn ?? new Date().toISOString(),
                        lastKnownContent: content,
                    });
                    const uri = createWebResourceUri(
                        parsed.environmentId,
                        parsed.webResourceId,
                        parsed.filename,
                        'unpublished',
                    );
                    this._onDidChangeFile.fire([{ type: vscode.FileChangeType.Changed, uri }]);
                    this._onDidSaveWebResource.fire({
                        environmentId: parsed.environmentId,
                        webResourceId: parsed.webResourceId,
                    });
                    vscode.window.showInformationMessage(`Saved: ${parsed.filename}`);
                    return true; // Abort the outer save (we already saved here)
                }

                if (resolution === 'Use Server Version') {
                    // Discard local changes, reload from server
                    this.serverState.delete(key);
                    const uri = createWebResourceUri(
                        parsed.environmentId,
                        parsed.webResourceId,
                        parsed.filename,
                        'unpublished',
                    );
                    this._onDidChangeFile.fire([{ type: vscode.FileChangeType.Changed, uri }]);
                    return true; // Abort save
                }

                // Cancel
                return true; // Abort save
            } finally {
                this.pendingSaveContent.delete(key);
            }
        }

        // User dismissed the dialog (clicked X / pressed Escape)
        return true; // Abort save
    }

    private bytesEqual(a: Uint8Array, b: Uint8Array): boolean {
        if (a.length !== b.length) return false;
        for (let i = 0; i < a.length; i++) {
            if (a[i] !== b[i]) return false;
        }
        return true;
    }
}
