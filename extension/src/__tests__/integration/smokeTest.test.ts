import { describe, it, expect, vi, beforeEach } from 'vitest';

// Mock vscode module
vi.mock('vscode', () => {
    const disposable = { dispose: vi.fn() };
    const subscriptions: any[] = [];
    return {
        commands: {
            registerCommand: vi.fn(() => disposable),
        },
        window: {
            createTreeView: vi.fn(() => ({ ...disposable, onDidChangeVisibility: vi.fn() })),
            createOutputChannel: vi.fn(() => ({
                appendLine: vi.fn(),
                dispose: vi.fn(),
            })),
            createStatusBarItem: vi.fn(() => ({
                show: vi.fn(),
                hide: vi.fn(),
                dispose: vi.fn(),
                text: '',
                tooltip: '',
                command: '',
            })),
            createWebviewPanel: vi.fn(() => ({
                webview: {
                    html: '',
                    asWebviewUri: vi.fn((uri: any) => uri),
                    onDidReceiveMessage: vi.fn(),
                    postMessage: vi.fn(),
                    cspSource: 'https://test.csp',
                },
                onDidDispose: vi.fn(),
                dispose: vi.fn(),
            })),
            showInformationMessage: vi.fn(),
            showErrorMessage: vi.fn(),
            showWarningMessage: vi.fn(),
            showQuickPick: vi.fn(),
            showInputBox: vi.fn(),
            showSaveDialog: vi.fn(),
            showTextDocument: vi.fn(),
            showNotebookDocument: vi.fn(),
            activeNotebookEditor: undefined,
            onDidChangeActiveNotebookEditor: vi.fn(() => disposable),
        },
        workspace: {
            registerNotebookSerializer: vi.fn(() => disposable),
            openNotebookDocument: vi.fn(() => Promise.resolve({ notebookType: 'ppdsnb', metadata: {} })),
            openTextDocument: vi.fn(() => Promise.resolve({})),
            onDidOpenNotebookDocument: vi.fn(() => disposable),
            onDidCloseNotebookDocument: vi.fn(() => disposable),
            onDidChangeTextDocument: vi.fn(() => disposable),
            getConfiguration: vi.fn(() => ({
                get: vi.fn((key: string, defaultVal: any) => defaultVal),
            })),
            notebookDocuments: [],
            applyEdit: vi.fn(() => Promise.resolve(true)),
            fs: {
                writeFile: vi.fn(() => Promise.resolve()),
            },
        },
        languages: {
            registerCompletionItemProvider: vi.fn(() => disposable),
            setTextDocumentLanguage: vi.fn(),
        },
        notebooks: {
            createNotebookController: vi.fn(() => ({
                supportedLanguages: [],
                supportsExecutionOrder: false,
                executeHandler: null,
                interruptHandler: null,
                createNotebookCellExecution: vi.fn(() => ({
                    executionOrder: 0,
                    start: vi.fn(),
                    end: vi.fn(),
                    replaceOutput: vi.fn(),
                })),
                dispose: vi.fn(),
            })),
        },
        StatusBarAlignment: { Left: 1, Right: 2 },
        ViewColumn: { One: 1, Beside: 2 },
        TreeItem: class { constructor(label: string, collapsibleState?: number) {} },
        TreeItemCollapsibleState: { None: 0, Collapsed: 1, Expanded: 2 },
        ThemeIcon: class { constructor(id: string) {} },
        EventEmitter: class {
            event = vi.fn();
            fire = vi.fn();
            dispose = vi.fn();
        },
        NotebookCellKind: { Code: 2, Markup: 1 },
        NotebookCellData: class {
            kind: number;
            value: string;
            languageId: string;
            constructor(kind: number, value: string, languageId: string) {
                this.kind = kind;
                this.value = value;
                this.languageId = languageId;
            }
        },
        NotebookData: class {
            cells: any[];
            metadata: any;
            constructor(cells: any[]) {
                this.cells = cells;
                this.metadata = {};
            }
        },
        NotebookCellOutput: class {
            items: any[];
            constructor(items: any[]) { this.items = items; }
        },
        NotebookCellOutputItem: {
            text: (value: string, mime?: string) => ({ data: value, mime: mime ?? 'text/plain' }),
            error: (err: Error) => ({ data: err.message, mime: 'application/vnd.code.notebook.error' }),
        },
        NotebookEdit: {
            updateNotebookMetadata: vi.fn((metadata: any) => metadata),
        },
        WorkspaceEdit: class {
            set = vi.fn();
            replace = vi.fn();
        },
        Range: class {
            constructor(public start: any, public end: any) {}
        },
        Uri: {
            file: (path: string) => ({ fsPath: path, scheme: 'file' }),
            joinPath: (...args: any[]) => ({ fsPath: args.map((a: any) => a.fsPath ?? a).join('/') }),
            parse: (str: string) => ({ fsPath: str, scheme: 'https' }),
        },
        env: {
            openExternal: vi.fn(),
            clipboard: { writeText: vi.fn() },
        },
        ProgressLocation: { Notification: 15 },
        CompletionItem: class {
            label: string;
            kind: number;
            insertText?: string;
            detail?: string;
            documentation?: string;
            sortText?: string;
            constructor(label: string, kind: number) {
                this.label = label;
                this.kind = kind;
            }
        },
        CompletionItemKind: { Class: 7, Field: 5, Keyword: 14 },
        QuickPickItemKind: { Separator: -1 },
    };
});

// Mock child_process and vscode-jsonrpc for DaemonClient
vi.mock('child_process', () => ({
    spawn: vi.fn(() => ({
        stdout: { on: vi.fn() },
        stdin: {},
        stderr: { on: vi.fn() },
        on: vi.fn(),
        removeListener: vi.fn(),
        kill: vi.fn(),
    })),
}));
vi.mock('vscode-jsonrpc/node', () => ({
    createMessageConnection: vi.fn(() => ({
        listen: vi.fn(),
        sendRequest: vi.fn(),
        onNotification: vi.fn(),
        dispose: vi.fn(),
    })),
    StreamMessageReader: vi.fn(),
    StreamMessageWriter: vi.fn(),
}));

describe('Extension Smoke Tests', () => {
    beforeEach(() => {
        vi.clearAllMocks();
        vi.resetModules();
    });

    it('exports activate and deactivate functions', async () => {
        const ext = await import('../../extension.js');
        expect(typeof ext.activate).toBe('function');
        expect(typeof ext.deactivate).toBe('function');
    });

    it('activate registers expected commands', async () => {
        const vscode = await import('vscode');
        const ext = await import('../../extension.js');

        const context = {
            subscriptions: [],
            extensionUri: { fsPath: '/test', scheme: 'file' },
        } as any;

        ext.activate(context);

        // Check that registerCommand was called for key commands
        const registerCommand = vscode.commands.registerCommand as any;
        const registeredCommands = registerCommand.mock.calls.map((c: any[]) => c[0]);

        expect(registeredCommands).toContain('ppds.dataExplorer');
        expect(registeredCommands).toContain('ppds.selectProfile');
        expect(registeredCommands).toContain('ppds.refreshProfiles');
        expect(registeredCommands).toContain('ppds.newNotebook');
        expect(registeredCommands).toContain('ppds.openSolutions');
        expect(registeredCommands).toContain('ppds.refreshSolutions');
    });

    it('activate registers notebook serializer', async () => {
        const vscode = await import('vscode');
        const ext = await import('../../extension.js');

        const context = {
            subscriptions: [],
            extensionUri: { fsPath: '/test', scheme: 'file' },
        } as any;

        ext.activate(context);

        expect(vscode.workspace.registerNotebookSerializer).toHaveBeenCalledWith(
            'ppdsnb',
            expect.any(Object),
            expect.objectContaining({ transientOutputs: true })
        );
    });

    it('activate registers completion providers', async () => {
        const vscode = await import('vscode');
        const ext = await import('../../extension.js');

        const context = {
            subscriptions: [],
            extensionUri: { fsPath: '/test', scheme: 'file' },
        } as any;

        ext.activate(context);

        // Should register for both SQL and FetchXML
        const calls = (vscode.languages.registerCompletionItemProvider as any).mock.calls;
        const languages = calls.map((c: any[]) => c[0]?.language);
        expect(languages).toContain('sql');
        expect(languages).toContain('fetchxml');
    });

    it('DaemonClient class can be imported', async () => {
        const { DaemonClient } = await import('../../daemonClient.js');
        expect(DaemonClient).toBeDefined();
        expect(typeof DaemonClient).toBe('function');
    });

    it('types module exports expected interfaces', async () => {
        // This just verifies the module loads without error
        const types = await import('../../types.js');
        expect(types).toBeDefined();
    });
});
