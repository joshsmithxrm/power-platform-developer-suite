import * as vscode from 'vscode';

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
