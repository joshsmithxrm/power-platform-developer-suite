import * as vscode from 'vscode';
import type { DaemonClient } from '../daemonClient.js';

/**
 * VS Code CompletionItemProvider that delegates to the daemon's query/complete
 * RPC endpoint for SQL IntelliSense.
 *
 * Note: The daemon's query/complete endpoint only supports SQL. FetchXML
 * completion is not yet implemented on the daemon side, so we guard against
 * sending FetchXML documents to the SQL completion engine.
 */
export class DataverseCompletionProvider implements vscode.CompletionItemProvider {
    constructor(private readonly daemon: DaemonClient) {}

    async provideCompletionItems(
        document: vscode.TextDocument,
        position: vscode.Position,
        token: vscode.CancellationToken,
        _context: vscode.CompletionContext
    ): Promise<vscode.CompletionItem[] | null> {
        // Only SQL is supported by the daemon's query/complete endpoint.
        // FetchXML documents must not be sent as the sql parameter.
        if (document.languageId !== 'sql') return null;

        if (token.isCancellationRequested) return null;

        const text = document.getText();
        const offset = document.offsetAt(position);

        try {
            const result = await this.daemon.queryComplete({ sql: text, cursorOffset: offset });

            if (token.isCancellationRequested) return null;

            return result.items.map((item, index) => {
                const kind = item.kind === 'entity' ? vscode.CompletionItemKind.Class
                    : item.kind === 'attribute' ? vscode.CompletionItemKind.Field
                    : vscode.CompletionItemKind.Keyword;

                const completion = new vscode.CompletionItem(item.label, kind);
                completion.insertText = item.insertText;
                completion.detail = item.detail ?? undefined;
                completion.documentation = item.description ?? undefined;
                completion.sortText = String(item.sortOrder).padStart(4, '0') + String(index).padStart(4, '0');
                return completion;
            });
        } catch {
            return null; // Silently fail — no completions if daemon unavailable
        }
    }
}
