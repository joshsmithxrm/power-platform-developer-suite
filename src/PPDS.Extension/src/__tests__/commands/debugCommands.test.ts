import { describe, it, expect, vi, beforeEach } from 'vitest';

const {
    mockRegisterCommand,
} = vi.hoisted(() => ({
    mockRegisterCommand: vi.fn(),
}));

vi.mock('vscode', () => ({
    commands: {
        registerCommand: mockRegisterCommand,
    },
}));

import {
    getDaemonStatus,
    getExtensionState,
    getTreeViewState,
    getPanelState,
    registerDebugCommands,
} from '../../commands/debugCommands.js';

describe('debugCommands', () => {
    beforeEach(() => {
        vi.clearAllMocks();
    });

    // ── getDaemonStatus ──────────────────────────────────────────────

    describe('getDaemonStatus', () => {
        it('returns ready state with process ID when daemon is connected', () => {
            const daemon = {
                isReady: () => true,
                getProcessId: () => 12345,
            };
            const result = getDaemonStatus(daemon as any);
            expect(result).toEqual({ state: 'ready', processId: 12345 });
        });

        it('returns stopped state with null process ID when daemon is not connected', () => {
            const daemon = {
                isReady: () => false,
                getProcessId: () => null,
            };
            const result = getDaemonStatus(daemon as any);
            expect(result).toEqual({ state: 'stopped', processId: null });
        });
    });

    // ── getExtensionState ────────────────────────────────────────────

    describe('getExtensionState', () => {
        it('returns daemon state and profile count', () => {
            const result = getExtensionState({ daemonState: 'ready', profileCount: 3 });
            expect(result).toEqual({ daemonState: 'ready', profileCount: 3 });
        });

        it('returns error state with zero profiles', () => {
            const result = getExtensionState({ daemonState: 'error', profileCount: 0 });
            expect(result).toEqual({ daemonState: 'error', profileCount: 0 });
        });
    });

    // ── getTreeViewState ─────────────────────────────────────────────

    describe('getTreeViewState', () => {
        it('serializes profile tree children to JSON', async () => {
            const mockChildren = [
                {
                    label: 'Dev Profile',
                    id: 'profile://user@test.com//DeviceCode//Commercial',
                    description: 'org.crm.dynamics.com',
                    contextValue: 'profile',
                },
                {
                    label: 'Prod Profile',
                    id: 'profile://admin@test.com//ClientSecret//Commercial',
                    description: '(no environment)',
                    contextValue: 'profile',
                },
            ];

            const provider = {
                getChildren: vi.fn().mockResolvedValue(mockChildren),
            };

            const result = await getTreeViewState(provider as any);
            expect(provider.getChildren).toHaveBeenCalledWith(undefined);
            expect(result).toEqual({
                children: [
                    {
                        label: 'Dev Profile',
                        id: 'profile://user@test.com//DeviceCode//Commercial',
                        description: 'org.crm.dynamics.com',
                        contextValue: 'profile',
                    },
                    {
                        label: 'Prod Profile',
                        id: 'profile://admin@test.com//ClientSecret//Commercial',
                        description: '(no environment)',
                        contextValue: 'profile',
                    },
                ],
            });
        });

        it('returns empty children array when tree is empty', async () => {
            const provider = {
                getChildren: vi.fn().mockResolvedValue([]),
            };

            const result = await getTreeViewState(provider as any);
            expect(result).toEqual({ children: [] });
        });
    });

    // ── getPanelState ────────────────────────────────────────────────

    describe('getPanelState', () => {
        it('returns panel instance counts', () => {
            const result = getPanelState({ queryPanels: () => 2, solutionsPanels: () => 1 });
            expect(result).toEqual({ queryPanels: 2, solutionsPanels: 1 });
        });

        it('returns zero counts when no panels are open', () => {
            const result = getPanelState({ queryPanels: () => 0, solutionsPanels: () => 0 });
            expect(result).toEqual({ queryPanels: 0, solutionsPanels: 0 });
        });

        it('handles dynamic panel types', () => {
            const result = getPanelState({
                queryPanels: () => 1,
                solutionsPanels: () => 2,
                metadataPanels: () => 3,
            });
            expect(result).toEqual({ queryPanels: 1, solutionsPanels: 2, metadataPanels: 3 });
        });
    });

    // ── registerDebugCommands ────────────────────────────────────────

    describe('registerDebugCommands', () => {
        it('registers all four debug commands', () => {
            const mockContext = {
                subscriptions: { push: vi.fn() },
            };
            const mockDaemon = {
                isReady: () => true,
                getProcessId: () => 42,
            };
            const mockProvider = {
                getChildren: vi.fn().mockResolvedValue([]),
            };

            registerDebugCommands(
                mockContext as any,
                mockDaemon as any,
                mockProvider as any,
                { daemonState: 'ready', profileCount: 1 },
                { queryPanels: () => 0, solutionsPanels: () => 0 },
            );

            expect(mockRegisterCommand).toHaveBeenCalledTimes(4);

            const registeredCommands = mockRegisterCommand.mock.calls.map(
                (call: unknown[]) => call[0],
            );
            expect(registeredCommands).toContain('ppds.debug.daemonStatus');
            expect(registeredCommands).toContain('ppds.debug.extensionState');
            expect(registeredCommands).toContain('ppds.debug.treeViewState');
            expect(registeredCommands).toContain('ppds.debug.panelState');
        });

        it('pushes disposables into context.subscriptions', () => {
            const pushFn = vi.fn();
            const mockContext = {
                subscriptions: { push: pushFn },
            };
            const mockDaemon = {
                isReady: () => true,
                getProcessId: () => null,
            };
            const mockProvider = {
                getChildren: vi.fn().mockResolvedValue([]),
            };

            mockRegisterCommand.mockReturnValue({ dispose: vi.fn() });

            registerDebugCommands(
                mockContext as any,
                mockDaemon as any,
                mockProvider as any,
                { daemonState: 'starting', profileCount: 0 },
                { queryPanels: () => 0, solutionsPanels: () => 0 },
            );

            // Should push all 4 disposables
            expect(pushFn).toHaveBeenCalledTimes(4);
        });

        it('daemonStatus command handler returns correct JSON', async () => {
            const mockContext = {
                subscriptions: { push: vi.fn() },
            };
            const mockDaemon = {
                isReady: () => true,
                getProcessId: () => 9999,
            };
            const mockProvider = {
                getChildren: vi.fn().mockResolvedValue([]),
            };

            registerDebugCommands(
                mockContext as any,
                mockDaemon as any,
                mockProvider as any,
                { daemonState: 'ready', profileCount: 2 },
                { queryPanels: () => 1, solutionsPanels: () => 0 },
            );

            // Find the daemonStatus handler and invoke it
            const daemonStatusCall = mockRegisterCommand.mock.calls.find(
                (call: unknown[]) => call[0] === 'ppds.debug.daemonStatus',
            );
            expect(daemonStatusCall).toBeDefined();
            const handler = daemonStatusCall![1] as () => unknown;
            const result = handler();
            expect(result).toEqual({ state: 'ready', processId: 9999 });
        });

        it('panelState command handler returns live counts', () => {
            const mockContext = {
                subscriptions: { push: vi.fn() },
            };
            const mockDaemon = {
                isReady: () => false,
                getProcessId: () => null,
            };
            const mockProvider = {
                getChildren: vi.fn().mockResolvedValue([]),
            };

            let qCount = 0;
            let sCount = 0;

            registerDebugCommands(
                mockContext as any,
                mockDaemon as any,
                mockProvider as any,
                { daemonState: 'error', profileCount: 0 },
                { queryPanels: () => qCount, solutionsPanels: () => sCount },
            );

            const panelStateCall = mockRegisterCommand.mock.calls.find(
                (call: unknown[]) => call[0] === 'ppds.debug.panelState',
            );
            const handler = panelStateCall![1] as () => unknown;

            // Initially zero
            expect(handler()).toEqual({ queryPanels: 0, solutionsPanels: 0 });

            // Simulate panels opening
            qCount = 3;
            sCount = 2;
            expect(handler()).toEqual({ queryPanels: 3, solutionsPanels: 2 });
        });
    });
});
