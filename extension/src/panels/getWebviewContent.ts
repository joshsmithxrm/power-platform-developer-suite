import * as vscode from 'vscode';

/**
 * Generates HTML for a webview with the VS Code Toolkit loaded and a strict CSP.
 */
export function getWebviewContent(
    webview: vscode.Webview,
    extensionUri: vscode.Uri,
    bodyHtml: string,
    scriptUri?: vscode.Uri,
): string {
    const toolkitUri = webview.asWebviewUri(
        vscode.Uri.joinPath(extensionUri, 'node_modules', '@vscode', 'webview-ui-toolkit', 'dist', 'toolkit.min.js')
    );
    const nonce = getNonce();
    return `<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="UTF-8">
    <meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src ${webview.cspSource} 'unsafe-inline'; script-src 'nonce-${nonce}'; font-src ${webview.cspSource};">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <script type="module" nonce="${nonce}" src="${toolkitUri}"></script>
    ${scriptUri ? `<script type="module" nonce="${nonce}" src="${scriptUri}"></script>` : ''}
</head>
<body>${bodyHtml}</body>
</html>`;
}

export function getNonce(): string {
    let text = '';
    const possible = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789';
    for (let i = 0; i < 32; i++) {
        text += possible.charAt(Math.floor(Math.random() * possible.length));
    }
    return text;
}
