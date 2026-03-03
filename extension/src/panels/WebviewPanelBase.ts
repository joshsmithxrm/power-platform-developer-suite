import * as vscode from 'vscode';

/**
 * Base class for webview panels with safe messaging.
 * Prevents "Webview is disposed" errors from async operations.
 */
export abstract class WebviewPanelBase implements vscode.Disposable {
    protected panel: vscode.WebviewPanel | undefined;
    protected disposables: vscode.Disposable[] = [];

    protected postMessage(message: unknown): void {
        this.panel?.webview.postMessage(message);
    }

    abstract getHtmlContent(webview: vscode.Webview): string;

    dispose(): void {
        this.panel?.dispose();
        this.panel = undefined;
        for (const d of this.disposables) {
            d.dispose();
        }
        this.disposables = [];
    }
}
