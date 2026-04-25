import { describe, it, expect, vi, beforeEach } from 'vitest';

// ── Mock: vscode ────────────────────────────────────────────────────────────

interface MockStatusBarItem {
    show: ReturnType<typeof vi.fn>;
    hide: ReturnType<typeof vi.fn>;
    dispose: ReturnType<typeof vi.fn>;
    text: string;
    tooltip: unknown;
    command: string | undefined;
    backgroundColor: unknown;
}

const mockStatusBarItem: MockStatusBarItem = {
    show: vi.fn(),
    hide: vi.fn(),
    dispose: vi.fn(),
    text: '',
    tooltip: '',
    command: undefined,
    backgroundColor: undefined,
};

class MockMarkdownString {
    public value = '';
    public isTrusted = false;
    public supportThemeIcons = false;
    appendMarkdown(s: string): this { this.value += s; return this; }
}

class MockThemeColor {
    constructor(public id: string) {}
}

vi.mock('vscode', () => ({
    window: {
        createStatusBarItem: vi.fn(() => mockStatusBarItem),
    },
    StatusBarAlignment: { Left: 1, Right: 2 },
    MarkdownString: MockMarkdownString,
    ThemeColor: MockThemeColor,
}));

// ── Helpers ─────────────────────────────────────────────────────────────────

import type { DaemonClient, DaemonState } from '../daemonClient.js';
import type { AuthListResponse } from '../types.js';

function makeClient(initialState: DaemonState, authList: AuthListResponse): {
    client: DaemonClient;
    authListMock: ReturnType<typeof vi.fn>;
    fireState: (s: DaemonState) => void;
    fireReconnect: () => void;
} {
    const stateListeners: Array<(s: DaemonState) => void> = [];
    const reconnectListeners: Array<() => void> = [];
    const authListMock = vi.fn().mockResolvedValue(authList);
    const client = {
        state: initialState,
        authList: authListMock,
        onDidChangeState: (h: (s: DaemonState) => void) => {
            stateListeners.push(h);
            return { dispose: vi.fn() };
        },
        onDidReconnect: (h: () => void) => {
            reconnectListeners.push(h);
            return { dispose: vi.fn() };
        },
    } as unknown as DaemonClient;
    return {
        client,
        authListMock,
        fireState: s => { (client as { state: DaemonState }).state = s; for (const l of stateListeners) l(s); },
        fireReconnect: () => { for (const l of reconnectListeners) l(); },
    };
}

const flush = () => new Promise<void>(resolve => setImmediate(resolve));

// ── Tests ───────────────────────────────────────────────────────────────────

describe('PpdsStatusBar', () => {
    beforeEach(() => {
        mockStatusBarItem.text = '';
        mockStatusBarItem.tooltip = '';
        mockStatusBarItem.command = undefined;
        mockStatusBarItem.backgroundColor = undefined;
        vi.clearAllMocks();
    });

    it('shows ready state with profile + environment', async () => {
        const { client } = makeClient('ready', {
            activeProfile: 'dev',
            activeProfileIndex: 0,
            profiles: [{
                index: 0, name: 'dev', identity: 'a@b', authMethod: 'Interactive',
                cloud: 'Public', environment: { url: 'https://x', displayName: 'Dev Org', environmentId: null },
                isActive: true, createdAt: null, lastUsedAt: null,
            }],
        });
        const { PpdsStatusBar } = await import('../ppdsStatusBar.js');
        const bar = new PpdsStatusBar(client);
        await flush();

        expect(mockStatusBarItem.text).toBe('$(check) PPDS: dev · Dev Org');
        expect(mockStatusBarItem.command).toBe('ppds.listProfiles');
        expect(mockStatusBarItem.backgroundColor).toBeUndefined();
        bar.dispose();
    });

    it('shows ready state with profile only when no environment', async () => {
        const { client } = makeClient('ready', {
            activeProfile: 'dev',
            activeProfileIndex: 0,
            profiles: [{
                index: 0, name: 'dev', identity: 'a@b', authMethod: 'Interactive',
                cloud: 'Public', environment: null,
                isActive: true, createdAt: null, lastUsedAt: null,
            }],
        });
        const { PpdsStatusBar } = await import('../ppdsStatusBar.js');
        const bar = new PpdsStatusBar(client);
        await flush();

        expect(mockStatusBarItem.text).toBe('$(check) PPDS: dev');
        bar.dispose();
    });

    it('shows ready state with no active profile', async () => {
        const { client } = makeClient('ready', {
            activeProfile: null,
            activeProfileIndex: null,
            profiles: [],
        });
        const { PpdsStatusBar } = await import('../ppdsStatusBar.js');
        const bar = new PpdsStatusBar(client);
        await flush();

        expect(mockStatusBarItem.text).toBe('$(check) PPDS: No profile');
        bar.dispose();
    });

    it('uses the listProfiles command in ready state', async () => {
        const { client } = makeClient('ready', { activeProfile: null, activeProfileIndex: null, profiles: [] });
        const { PpdsStatusBar } = await import('../ppdsStatusBar.js');
        const bar = new PpdsStatusBar(client);
        await flush();
        expect(mockStatusBarItem.command).toBe('ppds.listProfiles');
        bar.dispose();
    });

    it('shows starting state with spinner', async () => {
        const { client } = makeClient('starting', { activeProfile: null, activeProfileIndex: null, profiles: [] });
        const { PpdsStatusBar } = await import('../ppdsStatusBar.js');
        const bar = new PpdsStatusBar(client);
        expect(mockStatusBarItem.text).toBe('$(sync~spin) PPDS');
        expect(mockStatusBarItem.tooltip).toBe('PPDS Daemon: Starting...');
        expect(mockStatusBarItem.command).toBe('ppds.restartDaemon');
        expect(mockStatusBarItem.backgroundColor).toBeUndefined();
        bar.dispose();
    });

    it('shows reconnecting state with spinner', async () => {
        const { client } = makeClient('reconnecting', { activeProfile: null, activeProfileIndex: null, profiles: [] });
        const { PpdsStatusBar } = await import('../ppdsStatusBar.js');
        const bar = new PpdsStatusBar(client);
        expect(mockStatusBarItem.text).toBe('$(sync~spin) PPDS');
        expect(mockStatusBarItem.tooltip).toBe('PPDS Daemon: Reconnecting...');
        bar.dispose();
    });

    it('shows error state with error background and restartDaemon command', async () => {
        const { client } = makeClient('error', { activeProfile: null, activeProfileIndex: null, profiles: [] });
        const { PpdsStatusBar } = await import('../ppdsStatusBar.js');
        const bar = new PpdsStatusBar(client);
        expect(mockStatusBarItem.text).toBe('$(error) PPDS');
        expect(mockStatusBarItem.command).toBe('ppds.restartDaemon');
        expect(mockStatusBarItem.backgroundColor).toBeInstanceOf(MockThemeColor);
        expect((mockStatusBarItem.backgroundColor as MockThemeColor).id).toBe('statusBarItem.errorBackground');
        bar.dispose();
    });

    it('shows stopped state with restartDaemon command', async () => {
        const { client } = makeClient('stopped', { activeProfile: null, activeProfileIndex: null, profiles: [] });
        const { PpdsStatusBar } = await import('../ppdsStatusBar.js');
        const bar = new PpdsStatusBar(client);
        expect(mockStatusBarItem.text).toBe('$(circle-slash) PPDS');
        expect(mockStatusBarItem.command).toBe('ppds.restartDaemon');
        expect(mockStatusBarItem.backgroundColor).toBeUndefined();
        bar.dispose();
    });

    it('builds rich markdown tooltip in ready state', async () => {
        const { client } = makeClient('ready', {
            activeProfile: 'dev',
            activeProfileIndex: 0,
            profiles: [{
                index: 0, name: 'dev', identity: 'a@b', authMethod: 'ClientSecret',
                cloud: 'Public', environment: { url: 'https://x', displayName: 'Dev Org', environmentId: null },
                isActive: true, createdAt: null, lastUsedAt: null,
            }],
        });
        const { PpdsStatusBar } = await import('../ppdsStatusBar.js');
        const bar = new PpdsStatusBar(client);
        await flush();

        const tooltip = mockStatusBarItem.tooltip as MockMarkdownString;
        expect(tooltip).toBeInstanceOf(MockMarkdownString);
        expect(tooltip.value).toContain('**Profile**: dev');
        expect(tooltip.value).toContain('**Environment**: Dev Org');
        expect(tooltip.value).toContain('**Auth method**: ClientSecret');
        bar.dispose();
    });

    it('refresh re-fetches profile when called externally', async () => {
        const { client, authListMock } = makeClient('ready', {
            activeProfile: 'dev',
            activeProfileIndex: 0,
            profiles: [{
                index: 0, name: 'dev', identity: 'a@b', authMethod: 'Interactive',
                cloud: 'Public', environment: { url: 'https://x', displayName: 'Dev', environmentId: null },
                isActive: true, createdAt: null, lastUsedAt: null,
            }],
        });
        const { PpdsStatusBar } = await import('../ppdsStatusBar.js');
        const bar = new PpdsStatusBar(client);
        await flush();
        authListMock.mockClear();
        bar.refresh();
        expect(authListMock).toHaveBeenCalledOnce();
        bar.dispose();
    });

    it('skips refresh when daemon is not ready', async () => {
        const { client, authListMock } = makeClient('starting', { activeProfile: null, activeProfileIndex: null, profiles: [] });
        const { PpdsStatusBar } = await import('../ppdsStatusBar.js');
        const bar = new PpdsStatusBar(client);
        bar.refresh();
        expect(authListMock).not.toHaveBeenCalled();
        bar.dispose();
    });

    it('transitions from error to ready and refreshes profile', async () => {
        const { client, fireState } = makeClient('error', {
            activeProfile: 'p1',
            activeProfileIndex: 0,
            profiles: [{
                index: 0, name: 'p1', identity: 'a@b', authMethod: 'Interactive',
                cloud: 'Public', environment: { url: 'https://x', displayName: 'E', environmentId: null },
                isActive: true, createdAt: null, lastUsedAt: null,
            }],
        });
        const { PpdsStatusBar } = await import('../ppdsStatusBar.js');
        const bar = new PpdsStatusBar(client);
        expect(mockStatusBarItem.text).toBe('$(error) PPDS');

        fireState('ready');
        await flush();
        expect(mockStatusBarItem.text).toBe('$(check) PPDS: p1 · E');
        expect(mockStatusBarItem.command).toBe('ppds.listProfiles');
        bar.dispose();
    });
});
