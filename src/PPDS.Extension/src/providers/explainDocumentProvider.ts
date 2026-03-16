import * as vscode from 'vscode';

/**
 * Virtual document provider for EXPLAIN output.
 * Documents are read-only and close without a save prompt.
 * URI format: ppds-explain:{counter}.{ext}
 */
export class ExplainDocumentProvider implements vscode.TextDocumentContentProvider {
    static readonly scheme = 'ppds-explain';
    static instance: ExplainDocumentProvider | undefined;

    private readonly _onDidChange = new vscode.EventEmitter<vscode.Uri>();
    readonly onDidChange = this._onDidChange.event;

    private readonly contents = new Map<string, string>();
    private counter = 0;
    private readonly _closeListener: vscode.Disposable;

    constructor() {
        ExplainDocumentProvider.instance = this;
        this._closeListener = vscode.workspace.onDidCloseTextDocument((doc) => {
            if (doc.uri.scheme === ExplainDocumentProvider.scheme) {
                this.contents.delete(doc.uri.toString());
            }
        });
    }

    provideTextDocumentContent(uri: vscode.Uri): string {
        return this.contents.get(uri.toString()) ?? '';
    }

    /**
     * Creates a new virtual document with the given content and opens it.
     * Returns the document URI.
     */
    async show(content: string, languageId: string): Promise<void> {
        const ext = languageId === 'xml' ? 'xml' : 'txt';
        const uri = vscode.Uri.parse(`${ExplainDocumentProvider.scheme}:Execution Plan ${++this.counter}.${ext}`);
        this.contents.set(uri.toString(), content);
        this._onDidChange.fire(uri);
        const doc = await vscode.workspace.openTextDocument(uri);
        await vscode.languages.setTextDocumentLanguage(doc, languageId);
        await vscode.window.showTextDocument(doc, { viewColumn: vscode.ViewColumn.Beside, preview: true });
    }

    dispose(): void {
        ExplainDocumentProvider.instance = undefined;
        this._closeListener.dispose();
        this._onDidChange.dispose();
    }
}
