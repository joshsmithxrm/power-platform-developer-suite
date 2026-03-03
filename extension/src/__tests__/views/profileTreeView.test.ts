import { describe, it, expect, vi, beforeEach } from 'vitest';
import type { ProfileInfo } from '../../types.js';

// ── Mock: vscode ─────────────────────────────────────────────────────────────

const mockFireFn = vi.fn();

vi.mock('vscode', () => ({
    TreeItem: class TreeItem {
        label: string;
        collapsibleState: number;
        description?: string;
        tooltip?: string;
        contextValue?: string;
        iconPath?: unknown;
        constructor(label: string, collapsibleState: number) {
            this.label = label;
            this.collapsibleState = collapsibleState;
        }
    },
    TreeItemCollapsibleState: { None: 0, Collapsed: 1, Expanded: 2 },
    ThemeIcon: class ThemeIcon {
        id: string;
        constructor(id: string) { this.id = id; }
    },
    EventEmitter: class EventEmitter {
        event = vi.fn();
        fire = mockFireFn;
        dispose = vi.fn();
    },
}));

// ── Import after mocks ────────────────────────────────────────────────────────

import {
    ProfileTreeItem,
    ProfileTreeDataProvider,
} from '../../views/profileTreeView.js';

// ── Helpers ───────────────────────────────────────────────────────────────────

function makeProfile(overrides: Partial<ProfileInfo> = {}): ProfileInfo {
    return {
        index: 0,
        name: 'dev',
        identity: 'user@example.com',
        authMethod: 'DeviceCode',
        cloud: 'Public',
        environment: { url: 'https://dev.crm.dynamics.com', displayName: 'Dev Org' },
        isActive: false,
        createdAt: '2026-01-01T00:00:00Z',
        lastUsedAt: '2026-03-01T00:00:00Z',
        ...overrides,
    };
}

function makeDaemonClient(profiles: ProfileInfo[] = []) {
    return {
        authList: vi.fn().mockResolvedValue({ activeProfile: null, activeProfileIndex: null, profiles }),
    };
}

// ── ProfileTreeItem tests ─────────────────────────────────────────────────────

describe('ProfileTreeItem', () => {
    it('uses profile name as label', () => {
        const profile = makeProfile({ name: 'production' });
        const item = new ProfileTreeItem(profile);
        expect(item.label).toBe('production');
    });

    it('falls back to index-based label when name is null', () => {
        const profile = makeProfile({ name: null, index: 3 });
        const item = new ProfileTreeItem(profile);
        expect(item.label).toBe('Profile 3');
    });

    it('sets description to identity', () => {
        const profile = makeProfile({ identity: 'admin@contoso.com' });
        const item = new ProfileTreeItem(profile);
        expect(item.description).toBe('admin@contoso.com');
    });

    it('sets contextValue to "profile"', () => {
        const profile = makeProfile();
        const item = new ProfileTreeItem(profile);
        expect(item.contextValue).toBe('profile');
    });

    it('active profile gets pass-filled icon', () => {
        const profile = makeProfile({ isActive: true });
        const item = new ProfileTreeItem(profile);
        expect((item.iconPath as any).id).toBe('pass-filled');
    });

    it('inactive profile gets account icon', () => {
        const profile = makeProfile({ isActive: false });
        const item = new ProfileTreeItem(profile);
        expect((item.iconPath as any).id).toBe('account');
    });

    it('tooltip contains all profile fields', () => {
        const profile = makeProfile({
            name: 'dev',
            identity: 'dev@example.com',
            authMethod: 'ClientSecret',
            cloud: 'Public',
            environment: { url: 'https://dev.crm.dynamics.com', displayName: 'Dev Org' },
            isActive: true,
        });
        const item = new ProfileTreeItem(profile);
        expect(item.tooltip).toContain('dev');
        expect(item.tooltip).toContain('dev@example.com');
        expect(item.tooltip).toContain('ClientSecret');
        expect(item.tooltip).toContain('Public');
        expect(item.tooltip).toContain('Dev Org');
        expect(item.tooltip).toContain('Active');
    });

    it('tooltip handles null environment', () => {
        const profile = makeProfile({ environment: null });
        const item = new ProfileTreeItem(profile);
        // Should not throw; environment section simply omitted
        expect(item.tooltip).toBeTruthy();
        expect(item.tooltip).not.toContain('Environment:');
    });

    it('tooltip handles null name', () => {
        const profile = makeProfile({ name: null });
        const item = new ProfileTreeItem(profile);
        expect(item.tooltip).toContain('(unnamed)');
    });
});

// ── ProfileTreeDataProvider tests ─────────────────────────────────────────────

describe('ProfileTreeDataProvider', () => {
    beforeEach(() => {
        vi.clearAllMocks();
    });

    it('getTreeItem returns the element unchanged', async () => {
        const daemon = makeDaemonClient();
        const provider = new ProfileTreeDataProvider(daemon as any);
        const profile = makeProfile();
        const item = new ProfileTreeItem(profile);
        expect(provider.getTreeItem(item)).toBe(item);
    });

    it('getChildren returns empty array for a child element (flat list)', async () => {
        const daemon = makeDaemonClient();
        const provider = new ProfileTreeDataProvider(daemon as any);
        const profile = makeProfile();
        const item = new ProfileTreeItem(profile);
        const children = await provider.getChildren(item);
        expect(children).toEqual([]);
    });

    it('getChildren returns ProfileTreeItems for each profile', async () => {
        const profiles = [
            makeProfile({ index: 0, name: 'dev', isActive: false }),
            makeProfile({ index: 1, name: 'prod', isActive: true }),
        ];
        const daemon = makeDaemonClient(profiles);
        const provider = new ProfileTreeDataProvider(daemon as any);

        const children = await provider.getChildren();

        expect(daemon.authList).toHaveBeenCalledOnce();
        expect(children).toHaveLength(2);
        expect(children[0]).toBeInstanceOf(ProfileTreeItem);
        expect(children[0].label).toBe('dev');
        expect(children[1].label).toBe('prod');
    });

    it('getChildren returns empty array when no profiles', async () => {
        const daemon = makeDaemonClient([]);
        const provider = new ProfileTreeDataProvider(daemon as any);

        const children = await provider.getChildren();

        expect(children).toEqual([]);
    });

    it('getChildren returns empty array when daemon throws', async () => {
        const daemon = { authList: vi.fn().mockRejectedValue(new Error('daemon not available')) };
        const provider = new ProfileTreeDataProvider(daemon as any);

        const children = await provider.getChildren();

        expect(children).toEqual([]);
    });

    it('refresh fires onDidChangeTreeData event', () => {
        const daemon = makeDaemonClient();
        const provider = new ProfileTreeDataProvider(daemon as any);

        provider.refresh();

        expect(mockFireFn).toHaveBeenCalledOnce();
    });

    it('dispose cleans up the event emitter', () => {
        const daemon = makeDaemonClient();
        const provider = new ProfileTreeDataProvider(daemon as any);

        // Should not throw
        expect(() => provider.dispose()).not.toThrow();
    });
});
