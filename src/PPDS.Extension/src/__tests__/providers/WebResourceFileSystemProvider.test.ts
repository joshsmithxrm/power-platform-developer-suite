import { describe, it, expect, vi, beforeEach } from 'vitest';

// ── Hoisted mocks (required for vi.mock factory) ────────────────────────────

const {
    mockFireOnDidChangeFile,
    mockFireOnDidSaveWebResource,
    mockShowInformationMessage,
    mockShowWarningMessage,
    mockShowQuickPick,
    mockOpenTextDocument,
    mockShowTextDocument,
    mockSetTextDocumentLanguage,
    mockExecuteCommand,
    emitterState,
} = vi.hoisted(() => ({
    mockFireOnDidChangeFile: vi.fn(),
    mockFireOnDidSaveWebResource: vi.fn(),
    mockShowInformationMessage: vi.fn(),
    mockShowWarningMessage: vi.fn(),
    mockShowQuickPick: vi.fn(),
    mockOpenTextDocument: vi.fn(),
    mockShowTextDocument: vi.fn(),
    mockSetTextDocumentLanguage: vi.fn(),
    mockExecuteCommand: vi.fn(),
    emitterState: { index: 0 },
}));

// ── Mock: vscode ────────────────────────────────────────────────────────────

vi.mock('vscode', () => ({
    Uri: {
        from(components: { scheme: string; path: string; query: string }) {
            return {
                scheme: components.scheme,
                path: components.path,
                query: components.query,
                toString() {
                    const q = this.query ? `?${this.query}` : '';
                    return `${this.scheme}://${this.path}${q}`;
                },
            };
        },
    },
    FileType: { File: 1, Directory: 2 },
    FilePermission: { Readonly: 1 },
    FileChangeType: { Changed: 2 },
    EventEmitter: class {
        event = vi.fn();
        fire: ReturnType<typeof vi.fn>;
        dispose = vi.fn();
        constructor() {
            // First emitter = onDidChangeFile, second = onDidSaveWebResource
            this.fire = emitterState.index++ === 0
                ? mockFireOnDidChangeFile
                : mockFireOnDidSaveWebResource;
        }
    },
    Disposable: class {
        _dispose: () => void;
        constructor(dispose: () => void) { this._dispose = dispose; }
        dispose() { this._dispose(); }
    },
    FileSystemError: {
        NoPermissions: (uri?: any) => new Error(`NoPermissions: ${uri?.toString?.() ?? ''}`),
        FileNotFound: (uri?: any) => new Error(`FileNotFound: ${uri?.toString?.() ?? ''}`),
    },
    window: {
        showInformationMessage: mockShowInformationMessage,
        showWarningMessage: mockShowWarningMessage,
        showQuickPick: mockShowQuickPick,
        showTextDocument: mockShowTextDocument,
    },
    workspace: {
        openTextDocument: mockOpenTextDocument,
    },
    languages: {
        setTextDocumentLanguage: mockSetTextDocumentLanguage,
    },
    commands: {
        executeCommand: mockExecuteCommand,
    },
}));

// ── Import after mocks ──────────────────────────────────────────────────────

import { WebResourceFileSystemProvider } from '../../providers/WebResourceFileSystemProvider.js';

// ── Helpers ─────────────────────────────────────────────────────────────────

function makeDaemon() {
    return {
        webResourcesGet: vi.fn(),
        webResourcesUpdate: vi.fn(),
        webResourcesGetModifiedOn: vi.fn(),
        webResourcesPublish: vi.fn(),
    };
}

function makeUri(path: string, query = '') {
    return {
        scheme: 'ppds-webresource',
        path,
        query,
        toString() {
            const q = this.query ? `?${this.query}` : '';
            return `${this.scheme}://${this.path}${q}`;
        },
    };
}

// ── Tests ───────────────────────────────────────────────────────────────────

describe('WebResourceFileSystemProvider', () => {
    beforeEach(() => {
        vi.clearAllMocks();
        emitterState.index = 0;
    });

    // ── watch ───────────────────────────────────────────────────────────

    describe('watch', () => {
        it('returns a disposable', () => {
            const daemon = makeDaemon();
            const fsp = new WebResourceFileSystemProvider(daemon as any);
            const disposable = fsp.watch(makeUri('/e/w/f.js') as any, { recursive: false, excludes: [] });
            expect(disposable).toBeDefined();
            expect(typeof disposable.dispose).toBe('function');
        });
    });

    // ── stat ────────────────────────────────────────────────────────────

    describe('stat', () => {
        it('returns a File type', () => {
            const daemon = makeDaemon();
            const fsp = new WebResourceFileSystemProvider(daemon as any);
            const stat = fsp.stat(makeUri('/env/wr/file.js') as any);
            expect(stat.type).toBe(1); // FileType.File
        });

        it('returns no permissions for unpublished mode', () => {
            const daemon = makeDaemon();
            const fsp = new WebResourceFileSystemProvider(daemon as any);
            const stat = fsp.stat(makeUri('/env/wr/file.js') as any);
            expect(stat.permissions).toBeUndefined();
        });

        it('returns Readonly for published mode', () => {
            const daemon = makeDaemon();
            const fsp = new WebResourceFileSystemProvider(daemon as any);
            const stat = fsp.stat(makeUri('/env/wr/file.js', 'mode=published') as any);
            expect(stat.permissions).toBe(1); // FilePermission.Readonly
        });

        it('returns Readonly for server-current mode', () => {
            const daemon = makeDaemon();
            const fsp = new WebResourceFileSystemProvider(daemon as any);
            const stat = fsp.stat(makeUri('/env/wr/file.js', 'mode=server-current') as any);
            expect(stat.permissions).toBe(1);
        });

        it('returns Readonly for local-pending mode', () => {
            const daemon = makeDaemon();
            const fsp = new WebResourceFileSystemProvider(daemon as any);
            const stat = fsp.stat(makeUri('/env/wr/file.js', 'mode=local-pending') as any);
            expect(stat.permissions).toBe(1);
        });
    });

    // ── readDirectory ───────────────────────────────────────────────────

    describe('readDirectory', () => {
        it('returns empty array', () => {
            const daemon = makeDaemon();
            const fsp = new WebResourceFileSystemProvider(daemon as any);
            expect(fsp.readDirectory(makeUri('/') as any)).toEqual([]);
        });
    });

    // ── readFile ────────────────────────────────────────────────────────

    describe('readFile', () => {
        it('fetches unpublished content from daemon', async () => {
            const daemon = makeDaemon();
            daemon.webResourcesGet.mockResolvedValue({
                resource: { content: 'console.log("hi");', modifiedOn: '2026-01-01' },
            });
            const fsp = new WebResourceFileSystemProvider(daemon as any);
            fsp.registerEnvironment('env-1', 'https://org.crm.dynamics.com');

            const uri = makeUri('/env-1/wr-1/file.js');
            const result = await fsp.readFile(uri as any);

            expect(daemon.webResourcesGet).toHaveBeenCalledWith('wr-1', false, 'https://org.crm.dynamics.com');
            const text = new TextDecoder().decode(result);
            expect(text).toBe('console.log("hi");');
        });

        it('fetches published content from daemon', async () => {
            const daemon = makeDaemon();
            daemon.webResourcesGet.mockResolvedValue({
                resource: { content: 'published content' },
            });
            const fsp = new WebResourceFileSystemProvider(daemon as any);
            fsp.registerEnvironment('env-1', 'https://org.crm.dynamics.com');

            const uri = makeUri('/env-1/wr-1/file.js', 'mode=published');
            const result = await fsp.readFile(uri as any);

            expect(daemon.webResourcesGet).toHaveBeenCalledWith('wr-1', true, 'https://org.crm.dynamics.com');
            const text = new TextDecoder().decode(result);
            expect(text).toBe('published content');
        });

        it('fetches server-current content bypassing cache', async () => {
            const daemon = makeDaemon();
            daemon.webResourcesGet.mockResolvedValue({
                resource: { content: 'server content' },
            });
            const fsp = new WebResourceFileSystemProvider(daemon as any);
            fsp.registerEnvironment('env-1', 'https://org.crm.dynamics.com');

            const uri = makeUri('/env-1/wr-1/file.js', 'mode=server-current');
            const result = await fsp.readFile(uri as any);

            expect(daemon.webResourcesGet).toHaveBeenCalledWith('wr-1', false, 'https://org.crm.dynamics.com');
            const text = new TextDecoder().decode(result);
            expect(text).toBe('server content');
        });

        it('returns empty content when resource is null', async () => {
            const daemon = makeDaemon();
            daemon.webResourcesGet.mockResolvedValue({ resource: null });
            const fsp = new WebResourceFileSystemProvider(daemon as any);

            const uri = makeUri('/env-1/wr-1/file.js');
            const result = await fsp.readFile(uri as any);

            const text = new TextDecoder().decode(result);
            expect(text).toBe('');
        });

        it('throws for local-pending mode when no pending content', async () => {
            const daemon = makeDaemon();
            const fsp = new WebResourceFileSystemProvider(daemon as any);

            const uri = makeUri('/env-1/wr-1/file.js', 'mode=local-pending');
            await expect(fsp.readFile(uri as any)).rejects.toThrow('FileNotFound');
        });

        it('deduplicates concurrent unpublished fetches for the same resource', async () => {
            const daemon = makeDaemon();
            daemon.webResourcesGet.mockResolvedValue({
                resource: { content: 'content', modifiedOn: '2026-01-01' },
            });
            const fsp = new WebResourceFileSystemProvider(daemon as any);

            const uri = makeUri('/env-1/wr-1/file.js');
            const [result1, result2] = await Promise.all([
                fsp.readFile(uri as any),
                fsp.readFile(uri as any),
            ]);

            // Only one fetch to daemon despite two concurrent reads
            expect(daemon.webResourcesGet).toHaveBeenCalledTimes(1);
            expect(new TextDecoder().decode(result1)).toBe('content');
            expect(new TextDecoder().decode(result2)).toBe('content');
        });
    });

    // ── writeFile ───────────────────────────────────────────────────────

    describe('writeFile', () => {
        it('throws NoPermissions for published mode', async () => {
            const daemon = makeDaemon();
            const fsp = new WebResourceFileSystemProvider(daemon as any);

            const uri = makeUri('/env-1/wr-1/file.js', 'mode=published');
            const content = new TextEncoder().encode('new content');

            await expect(fsp.writeFile(uri as any, content)).rejects.toThrow('NoPermissions');
        });

        it('saves content via daemon for unpublished mode', async () => {
            const daemon = makeDaemon();
            daemon.webResourcesUpdate.mockResolvedValue(undefined);
            daemon.webResourcesGetModifiedOn.mockResolvedValue({ modifiedOn: '2026-01-02' });
            mockShowInformationMessage.mockResolvedValue(undefined);
            const fsp = new WebResourceFileSystemProvider(daemon as any);
            fsp.registerEnvironment('env-1', 'https://org.crm.dynamics.com');

            const uri = makeUri('/env-1/wr-1/file.js');
            const content = new TextEncoder().encode('new content');
            await fsp.writeFile(uri as any, content);

            expect(daemon.webResourcesUpdate).toHaveBeenCalledWith(
                'wr-1', 'new content', 'https://org.crm.dynamics.com',
            );
        });

        it('fires change and save events after successful write', async () => {
            const daemon = makeDaemon();
            daemon.webResourcesUpdate.mockResolvedValue(undefined);
            daemon.webResourcesGetModifiedOn.mockResolvedValue({ modifiedOn: '2026-01-02' });
            mockShowInformationMessage.mockResolvedValue(undefined);
            const fsp = new WebResourceFileSystemProvider(daemon as any);
            fsp.registerEnvironment('env-1', 'https://org.crm.dynamics.com');

            const uri = makeUri('/env-1/wr-1/file.js');
            const content = new TextEncoder().encode('new content');
            await fsp.writeFile(uri as any, content);

            expect(mockFireOnDidChangeFile).toHaveBeenCalled();
            expect(mockFireOnDidSaveWebResource).toHaveBeenCalledWith({
                environmentId: 'env-1',
                webResourceId: 'wr-1',
            });
        });

        it('skips save when content is identical to last known', async () => {
            const daemon = makeDaemon();
            daemon.webResourcesGet.mockResolvedValue({
                resource: { content: 'existing content', modifiedOn: '2026-01-01' },
            });
            daemon.webResourcesUpdate.mockResolvedValue(undefined);
            daemon.webResourcesGetModifiedOn.mockResolvedValue({ modifiedOn: '2026-01-01' });
            mockShowInformationMessage.mockResolvedValue(undefined);
            const fsp = new WebResourceFileSystemProvider(daemon as any);
            fsp.registerEnvironment('env-1', 'https://org.crm.dynamics.com');

            // First read to populate server state
            const readUri = makeUri('/env-1/wr-1/file.js');
            await fsp.readFile(readUri as any);

            // Now write the exact same content
            const writeUri = makeUri('/env-1/wr-1/file.js');
            const sameContent = new TextEncoder().encode('existing content');
            await fsp.writeFile(writeUri as any, sameContent);

            // Should not have called update because content is identical
            expect(daemon.webResourcesUpdate).not.toHaveBeenCalled();
        });

        it('publishes when user clicks Publish in notification', async () => {
            const daemon = makeDaemon();
            daemon.webResourcesUpdate.mockResolvedValue(undefined);
            daemon.webResourcesGetModifiedOn.mockResolvedValue({ modifiedOn: '2026-01-02' });
            daemon.webResourcesPublish.mockResolvedValue(undefined);
            // First call is "Saved" notification, user clicks "Publish"
            mockShowInformationMessage
                .mockResolvedValueOnce('Publish')
                .mockResolvedValueOnce(undefined);
            const fsp = new WebResourceFileSystemProvider(daemon as any);
            fsp.registerEnvironment('env-1', 'https://org.crm.dynamics.com');

            const uri = makeUri('/env-1/wr-1/file.js');
            const content = new TextEncoder().encode('new content');
            await fsp.writeFile(uri as any, content);

            expect(daemon.webResourcesPublish).toHaveBeenCalledWith(
                ['wr-1'], 'https://org.crm.dynamics.com',
            );
        });
    });

    // ── createDirectory / delete / rename ───────────────────────────────

    describe('unsupported operations', () => {
        it('createDirectory throws NoPermissions', () => {
            const daemon = makeDaemon();
            const fsp = new WebResourceFileSystemProvider(daemon as any);
            expect(() => fsp.createDirectory(makeUri('/foo') as any)).toThrow('NoPermissions');
        });

        it('delete throws NoPermissions', () => {
            const daemon = makeDaemon();
            const fsp = new WebResourceFileSystemProvider(daemon as any);
            expect(() => fsp.delete(makeUri('/foo') as any)).toThrow('NoPermissions');
        });

        it('rename throws NoPermissions', () => {
            const daemon = makeDaemon();
            const fsp = new WebResourceFileSystemProvider(daemon as any);
            expect(() => fsp.rename(makeUri('/old') as any, makeUri('/new') as any)).toThrow('NoPermissions');
        });
    });

    // ── openWebResource ─────────────────────────────────────────────────

    describe('openWebResource', () => {
        it('shows info message for binary types', async () => {
            const daemon = makeDaemon();
            const fsp = new WebResourceFileSystemProvider(daemon as any);

            await fsp.openWebResource('env-1', 'https://org.crm.dynamics.com', 'wr-1', 'image.png', 5);

            expect(mockShowInformationMessage).toHaveBeenCalledWith(
                expect.stringContaining('binary web resource'),
            );
            expect(daemon.webResourcesGet).not.toHaveBeenCalled();
        });

        it('opens editor for text resources when published equals unpublished', async () => {
            const daemon = makeDaemon();
            daemon.webResourcesGet.mockResolvedValue({
                resource: { content: 'same content', modifiedOn: '2026-01-01' },
            });
            const mockDoc = { uri: makeUri('/env-1/wr-1/script.js') };
            mockOpenTextDocument.mockResolvedValue(mockDoc);
            mockShowTextDocument.mockResolvedValue(undefined);
            mockSetTextDocumentLanguage.mockResolvedValue(undefined);
            const fsp = new WebResourceFileSystemProvider(daemon as any);

            await fsp.openWebResource('env-1', 'https://org.crm.dynamics.com', 'wr-1', 'script.js', 3);

            // Both published and unpublished fetched
            expect(daemon.webResourcesGet).toHaveBeenCalledTimes(2);
            // No quick pick since content is the same
            expect(mockShowQuickPick).not.toHaveBeenCalled();
            // Document opened
            expect(mockOpenTextDocument).toHaveBeenCalled();
            expect(mockShowTextDocument).toHaveBeenCalledWith(mockDoc, { preview: false });
            // Language set to javascript for type 3
            expect(mockSetTextDocumentLanguage).toHaveBeenCalledWith(mockDoc, 'javascript');
        });

        it('shows quick pick when published differs from unpublished', async () => {
            const daemon = makeDaemon();
            daemon.webResourcesGet
                .mockResolvedValueOnce({ resource: { content: 'published v1' } })   // published
                .mockResolvedValueOnce({ resource: { content: 'unpublished v2', modifiedOn: '2026-01-01' } }); // unpublished
            mockShowQuickPick.mockResolvedValue({ mode: 'unpublished' });
            const mockDoc = { uri: makeUri('/env-1/wr-1/script.js') };
            mockOpenTextDocument.mockResolvedValue(mockDoc);
            mockShowTextDocument.mockResolvedValue(undefined);
            mockSetTextDocumentLanguage.mockResolvedValue(undefined);
            const fsp = new WebResourceFileSystemProvider(daemon as any);

            await fsp.openWebResource('env-1', 'https://org.crm.dynamics.com', 'wr-1', 'script.js', 3);

            expect(mockShowQuickPick).toHaveBeenCalled();
            expect(mockOpenTextDocument).toHaveBeenCalled();
        });

        it('does nothing when user cancels quick pick', async () => {
            const daemon = makeDaemon();
            daemon.webResourcesGet
                .mockResolvedValueOnce({ resource: { content: 'published v1' } })
                .mockResolvedValueOnce({ resource: { content: 'unpublished v2', modifiedOn: '2026-01-01' } });
            mockShowQuickPick.mockResolvedValue(undefined); // user cancelled
            const fsp = new WebResourceFileSystemProvider(daemon as any);

            await fsp.openWebResource('env-1', 'https://org.crm.dynamics.com', 'wr-1', 'script.js', 3);

            expect(mockOpenTextDocument).not.toHaveBeenCalled();
        });

        it('does not set language for types without a languageId', async () => {
            const daemon = makeDaemon();
            daemon.webResourcesGet.mockResolvedValue({
                resource: { content: '<xsl:stylesheet/>', modifiedOn: '2026-01-01' },
            });
            const mockDoc = { uri: makeUri('/env-1/wr-1/transform.xslt') };
            mockOpenTextDocument.mockResolvedValue(mockDoc);
            mockShowTextDocument.mockResolvedValue(undefined);
            mockSetTextDocumentLanguage.mockResolvedValue(undefined);
            const fsp = new WebResourceFileSystemProvider(daemon as any);

            // Type 9 = XSL, has languageId 'xsl'
            await fsp.openWebResource('env-1', 'https://org.crm.dynamics.com', 'wr-1', 'transform.xslt', 9);
            expect(mockSetTextDocumentLanguage).toHaveBeenCalledWith(mockDoc, 'xsl');
        });
    });

    // ── registerEnvironment ─────────────────────────────────────────────

    describe('registerEnvironment', () => {
        it('stores environment URL for use in readFile', async () => {
            const daemon = makeDaemon();
            daemon.webResourcesGet.mockResolvedValue({
                resource: { content: 'content', modifiedOn: '2026-01-01' },
            });
            const fsp = new WebResourceFileSystemProvider(daemon as any);

            fsp.registerEnvironment('env-xyz', 'https://xyz.crm.dynamics.com');

            const uri = makeUri('/env-xyz/wr-1/file.js');
            await fsp.readFile(uri as any);

            expect(daemon.webResourcesGet).toHaveBeenCalledWith('wr-1', false, 'https://xyz.crm.dynamics.com');
        });
    });

    // ── dispose ─────────────────────────────────────────────────────────

    describe('dispose', () => {
        it('does not throw', () => {
            const daemon = makeDaemon();
            const fsp = new WebResourceFileSystemProvider(daemon as any);
            expect(() => fsp.dispose()).not.toThrow();
        });
    });
});
