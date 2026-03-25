import { describe, it, expect } from 'vitest';

import type {
    SolutionsPanelWebviewToHost,
    SolutionsPanelHostToWebview,
    SolutionViewDto,
    ComponentGroupDto,
} from '../../panels/webview/shared/message-types.js';

describe('SolutionsPanel message types', () => {
    describe('WebviewToHost', () => {
        it('covers all commands', () => {
            // Type annotations ensure exhaustiveness at compile time.
            // This test documents the message contract for each direction.
            const messages: SolutionsPanelWebviewToHost[] = [
                { command: 'ready' },
                { command: 'requestEnvironmentList' },
                { command: 'refresh' },
                { command: 'expandSolution', uniqueName: 'MySolution' },
                { command: 'collapseSolution', uniqueName: 'MySolution' },
                { command: 'copyToClipboard', text: 'copied text' },
                { command: 'openInMaker' },
                { command: 'setVisibilityFilter', includeInternal: true },
                { command: 'webviewError', error: 'test error', stack: 'Error\n  at ...' },
            ];
            messages.forEach(msg => {
                expect(msg).toHaveProperty('command');
            });
        });

        it('openInMaker solutionId is optional', () => {
            const withSolution: SolutionsPanelWebviewToHost = {
                command: 'openInMaker',
                solutionId: '00000000-0000-0000-0000-000000000001',
            };
            const withoutSolution: SolutionsPanelWebviewToHost = {
                command: 'openInMaker',
            };
            expect(withSolution.command).toBe('openInMaker');
            expect(withoutSolution.command).toBe('openInMaker');
        });

        it('setVisibilityFilter toggles internal solution visibility', () => {
            const showAll: SolutionsPanelWebviewToHost = {
                command: 'setVisibilityFilter',
                includeInternal: true,
            };
            const visibleOnly: SolutionsPanelWebviewToHost = {
                command: 'setVisibilityFilter',
                includeInternal: false,
            };
            expect(showAll.includeInternal).toBe(true);
            expect(visibleOnly.includeInternal).toBe(false);
        });

        it('webviewError stack is optional', () => {
            const msg: SolutionsPanelWebviewToHost = {
                command: 'webviewError',
                error: 'Something went wrong',
            };
            expect(msg.command).toBe('webviewError');
        });
    });

    describe('HostToWebview', () => {
        it('covers all commands', () => {
            // Type annotations ensure exhaustiveness at compile time.
            // This test documents the message contract for each direction.
            const messages: SolutionsPanelHostToWebview[] = [
                { command: 'updateEnvironment', name: 'dev', envType: 'Sandbox', envColor: '#00ff00' },
                {
                    command: 'solutionsLoaded',
                    solutions: [],
                    totalCount: 0,
                    filtersApplied: [],
                },
                { command: 'componentsLoading', uniqueName: 'MySolution' },
                { command: 'componentsLoaded', uniqueName: 'MySolution', groups: [] },
                { command: 'loading' },
                { command: 'error', message: 'load failed' },
                { command: 'daemonReconnected' },
            ];
            messages.forEach(msg => {
                expect(msg).toHaveProperty('command');
            });
        });

        it('updateEnvironment accepts null envType and envColor', () => {
            const msg: SolutionsPanelHostToWebview = {
                command: 'updateEnvironment',
                name: 'prod',
                envType: null,
                envColor: null,
            };
            expect(msg.envType).toBeNull();
            expect(msg.envColor).toBeNull();
        });

        it('solutionsLoaded carries filter metadata', () => {
            const msg: SolutionsPanelHostToWebview = {
                command: 'solutionsLoaded',
                solutions: [],
                totalCount: 100,
                filtersApplied: ['managed', 'visible'],
            };
            if (msg.command === 'solutionsLoaded') {
                expect(msg.totalCount).toBe(100);
                expect(msg.filtersApplied).toHaveLength(2);
            }
        });

        it('componentsLoaded carries grouped component data', () => {
            const msg: SolutionsPanelHostToWebview = {
                command: 'componentsLoaded',
                uniqueName: 'MySolution',
                groups: [
                    {
                        typeName: 'Entity',
                        components: [
                            {
                                objectId: '11111111-1111-1111-1111-111111111111',
                                isMetadata: false,
                                logicalName: 'account',
                                schemaName: 'Account',
                                displayName: 'Account',
                                rootComponentBehavior: 0,
                            },
                        ],
                    },
                ],
            };
            if (msg.command === 'componentsLoaded') {
                expect(msg.uniqueName).toBe('MySolution');
                expect(msg.groups).toHaveLength(1);
                expect(msg.groups[0].components).toHaveLength(1);
            }
        });
    });

    describe('SolutionViewDto', () => {
        it('has all required fields', () => {
            const dto: SolutionViewDto = {
                id: '00000000-0000-0000-0000-000000000001',
                uniqueName: 'MySolution',
                friendlyName: 'My Solution',
                version: '1.0.0.0',
                publisherName: 'Contoso',
                isManaged: false,
                description: 'A test solution',
                createdOn: '2026-01-01T00:00:00Z',
                modifiedOn: '2026-03-01T00:00:00Z',
                installedOn: null,
                isVisible: true,
                isApiManaged: false,
            };
            expect(dto.uniqueName).toBe('MySolution');
            expect(dto.isManaged).toBe(false);
            expect(dto.isVisible).toBe(true);
            expect(dto.isApiManaged).toBe(false);
        });

        it('handles null date fields for unmanaged solutions', () => {
            const dto: SolutionViewDto = {
                id: '00000000-0000-0000-0000-000000000002',
                uniqueName: 'UnmanagedSolution',
                friendlyName: 'Unmanaged Solution',
                version: '1.0.0.0',
                publisherName: 'Publisher',
                isManaged: false,
                description: '',
                createdOn: null,
                modifiedOn: null,
                installedOn: null,
                isVisible: true,
                isApiManaged: false,
            };
            expect(dto.createdOn).toBeNull();
            expect(dto.modifiedOn).toBeNull();
            expect(dto.installedOn).toBeNull();
        });

        it('marks managed solutions with installedOn date', () => {
            const dto: SolutionViewDto = {
                id: '00000000-0000-0000-0000-000000000003',
                uniqueName: 'ManagedSolution',
                friendlyName: 'Managed Solution',
                version: '2.5.0.100',
                publisherName: 'ISV Publisher',
                isManaged: true,
                description: 'An installed managed solution',
                createdOn: '2025-06-01T00:00:00Z',
                modifiedOn: '2025-12-01T00:00:00Z',
                installedOn: '2026-01-15T00:00:00Z',
                isVisible: true,
                isApiManaged: false,
            };
            expect(dto.isManaged).toBe(true);
            expect(dto.installedOn).toBe('2026-01-15T00:00:00Z');
        });

        it('handles internal (hidden) solutions with isVisible false', () => {
            const dto: SolutionViewDto = {
                id: '00000000-0000-0000-0000-000000000004',
                uniqueName: 'InternalSolution',
                friendlyName: 'Internal Solution',
                version: '1.0.0.0',
                publisherName: 'Microsoft',
                isManaged: true,
                description: '',
                createdOn: null,
                modifiedOn: null,
                installedOn: null,
                isVisible: false,
                isApiManaged: true,
            };
            expect(dto.isVisible).toBe(false);
            expect(dto.isApiManaged).toBe(true);
        });
    });

    describe('ComponentGroupDto', () => {
        it('groups components by type name with all fields', () => {
            const dto: ComponentGroupDto = {
                typeName: 'Attribute',
                components: [
                    {
                        objectId: 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa',
                        isMetadata: true,
                        logicalName: 'cr_name',
                        schemaName: 'cr_name',
                        displayName: 'Custom Name',
                        rootComponentBehavior: 0,
                    },
                ],
            };
            expect(dto.typeName).toBe('Attribute');
            expect(dto.components).toHaveLength(1);
            expect(dto.components[0].isMetadata).toBe(true);
        });

        it('component optional fields can be omitted', () => {
            const dto: ComponentGroupDto = {
                typeName: 'PluginAssembly',
                components: [
                    {
                        objectId: 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb',
                        isMetadata: false,
                        rootComponentBehavior: 2,
                    },
                ],
            };
            expect(dto.components[0].logicalName).toBeUndefined();
            expect(dto.components[0].schemaName).toBeUndefined();
            expect(dto.components[0].displayName).toBeUndefined();
        });

        it('handles empty components array', () => {
            const dto: ComponentGroupDto = {
                typeName: 'Entity',
                components: [],
            };
            expect(dto.components).toHaveLength(0);
        });
    });
});
