import { describe, it, expect } from 'vitest';

import type {
    PluginTracesPanelWebviewToHost,
    PluginTracesPanelHostToWebview,
    PluginTraceViewDto,
    PluginTraceDetailViewDto,
    TraceFilterViewDto,
    TimelineNodeViewDto,
} from '../../panels/webview/shared/message-types.js';

describe('PluginTracesPanel message types', () => {
    describe('WebviewToHost', () => {
        it('covers all commands', () => {
            const messages: PluginTracesPanelWebviewToHost[] = [
                { command: 'ready' },
                { command: 'refresh' },
                { command: 'applyFilter', filter: { typeName: 'MyPlugin', hasException: true } },
                { command: 'selectTrace', id: 'trace-1' },
                { command: 'loadTimeline', correlationId: 'corr-1' },
                { command: 'deleteTraces', ids: ['id-1', 'id-2'] },
                { command: 'deleteOlderThan', days: 30 },
                { command: 'requestTraceLevel' },
                { command: 'setTraceLevel', level: 'All' },
                { command: 'setAutoRefresh', intervalSeconds: 10 },
                { command: 'setAutoRefresh', intervalSeconds: null },
                { command: 'requestEnvironmentList' },
                { command: 'copyToClipboard', text: 'copied text' },
                { command: 'webviewError', error: 'something broke', stack: 'Error\n  at ...' },
            ];
            expect(messages).toHaveLength(14);
        });

        it('webviewError stack is optional', () => {
            const msg: PluginTracesPanelWebviewToHost = {
                command: 'webviewError',
                error: 'test error',
            };
            expect(msg.command).toBe('webviewError');
        });
    });

    describe('HostToWebview', () => {
        it('covers all commands', () => {
            const messages: PluginTracesPanelHostToWebview[] = [
                { command: 'updateEnvironment', name: 'dev', envType: 'Sandbox', envColor: '#00ff00' },
                { command: 'tracesLoaded', traces: [] },
                {
                    command: 'traceDetailLoaded',
                    trace: {
                        id: 't-1',
                        typeName: 'MyPlugin',
                        messageName: 'Create',
                        primaryEntity: 'account',
                        mode: 'Synchronous',
                        operationType: 'Plugin',
                        depth: 1,
                        createdOn: '2026-03-17T00:00:00Z',
                        durationMs: 42,
                        hasException: false,
                        correlationId: 'corr-1',
                        constructorDurationMs: 5,
                        executionStartTime: '2026-03-17T00:00:00Z',
                        exceptionDetails: null,
                        messageBlock: '{"key":"value"}',
                        configuration: null,
                        secureConfiguration: null,
                        requestId: 'req-1',
                    },
                },
                { command: 'timelineLoaded', nodes: [] },
                { command: 'traceLevelLoaded', level: 'All', levelValue: 2 },
                { command: 'deleteComplete', deletedCount: 5 },
                { command: 'loading' },
                { command: 'error', message: 'something went wrong' },
                { command: 'daemonReconnected' },
            ];
            expect(messages).toHaveLength(9);
        });

        it('updateEnvironment accepts null envType and envColor', () => {
            const msg: PluginTracesPanelHostToWebview = {
                command: 'updateEnvironment',
                name: 'test',
                envType: null,
                envColor: null,
            };
            expect(msg.command).toBe('updateEnvironment');
        });
    });

    describe('PluginTraceViewDto', () => {
        it('has all required fields with correct types', () => {
            const dto: PluginTraceViewDto = {
                id: '00000000-0000-0000-0000-000000000001',
                typeName: 'Contoso.Plugins.AccountCreate',
                messageName: 'Create',
                primaryEntity: 'account',
                mode: 'Synchronous',
                operationType: 'Plugin',
                depth: 1,
                createdOn: '2026-03-17T12:00:00Z',
                durationMs: 150,
                hasException: false,
                correlationId: 'abc-123',
            };
            expect(dto.id).toBe('00000000-0000-0000-0000-000000000001');
            expect(dto.typeName).toBe('Contoso.Plugins.AccountCreate');
            expect(dto.depth).toBe(1);
            expect(dto.hasException).toBe(false);
            expect(dto.durationMs).toBe(150);
        });

        it('handles null fields gracefully', () => {
            const dto: PluginTraceViewDto = {
                id: '00000000-0000-0000-0000-000000000002',
                typeName: 'SomePlugin',
                messageName: null,
                primaryEntity: null,
                mode: 'Asynchronous',
                operationType: 'Plugin',
                depth: 2,
                createdOn: '2026-03-17T12:00:00Z',
                durationMs: null,
                hasException: true,
                correlationId: null,
            };
            expect(dto.messageName).toBeNull();
            expect(dto.primaryEntity).toBeNull();
            expect(dto.durationMs).toBeNull();
            expect(dto.correlationId).toBeNull();
        });
    });

    describe('PluginTraceDetailViewDto', () => {
        it('extends PluginTraceViewDto with additional fields', () => {
            const detail: PluginTraceDetailViewDto = {
                // Base PluginTraceViewDto fields
                id: 'detail-1',
                typeName: 'Contoso.Plugins.AccountCreate',
                messageName: 'Create',
                primaryEntity: 'account',
                mode: 'Synchronous',
                operationType: 'Plugin',
                depth: 1,
                createdOn: '2026-03-17T12:00:00Z',
                durationMs: 200,
                hasException: true,
                correlationId: 'corr-abc',
                // Extended fields
                constructorDurationMs: 10,
                executionStartTime: '2026-03-17T12:00:01Z',
                exceptionDetails: 'NullReferenceException: Object reference not set',
                messageBlock: '{"Target":{"Id":"..."}}',
                configuration: '<config>value</config>',
                secureConfiguration: null,
                requestId: 'req-xyz',
            };
            expect(detail.constructorDurationMs).toBe(10);
            expect(detail.exceptionDetails).toContain('NullReferenceException');
            expect(detail.secureConfiguration).toBeNull();
            expect(detail.requestId).toBe('req-xyz');
        });

        it('handles all-null extended fields', () => {
            const detail: PluginTraceDetailViewDto = {
                id: 'detail-2',
                typeName: 'SomePlugin',
                messageName: null,
                primaryEntity: null,
                mode: 'Asynchronous',
                operationType: 'Plugin',
                depth: 1,
                createdOn: '2026-03-17T12:00:00Z',
                durationMs: null,
                hasException: false,
                correlationId: null,
                constructorDurationMs: null,
                executionStartTime: null,
                exceptionDetails: null,
                messageBlock: null,
                configuration: null,
                secureConfiguration: null,
                requestId: null,
            };
            expect(detail.constructorDurationMs).toBeNull();
            expect(detail.executionStartTime).toBeNull();
            expect(detail.exceptionDetails).toBeNull();
            expect(detail.messageBlock).toBeNull();
            expect(detail.configuration).toBeNull();
            expect(detail.requestId).toBeNull();
        });
    });

    describe('TraceFilterViewDto', () => {
        it('all fields are optional', () => {
            const emptyFilter: TraceFilterViewDto = {};
            expect(emptyFilter).toEqual({});
        });

        it('accepts any combination of filter fields', () => {
            const partial: TraceFilterViewDto = {
                typeName: 'MyPlugin',
                hasException: true,
                minDurationMs: 100,
            };
            expect(partial.typeName).toBe('MyPlugin');
            expect(partial.hasException).toBe(true);
            expect(partial.minDurationMs).toBe(100);
        });

        it('accepts all filter fields', () => {
            const full: TraceFilterViewDto = {
                typeName: 'Contoso.Plugins.AccountCreate',
                messageName: 'Create',
                primaryEntity: 'account',
                mode: 'Synchronous',
                hasException: false,
                correlationId: 'corr-1',
                minDurationMs: 50,
                startDate: '2026-03-01T00:00:00Z',
                endDate: '2026-03-17T23:59:59Z',
            };
            expect(full.startDate).toBe('2026-03-01T00:00:00Z');
            expect(full.endDate).toBe('2026-03-17T23:59:59Z');
        });
    });

    describe('TimelineNodeViewDto', () => {
        it('has all required fields including children array', () => {
            const node: TimelineNodeViewDto = {
                traceId: 'trace-1',
                typeName: 'Contoso.Plugins.AccountCreate',
                messageName: 'Create',
                depth: 1,
                durationMs: 200,
                hasException: false,
                offsetPercent: 0,
                widthPercent: 100,
                hierarchyDepth: 0,
                children: [],
            };
            expect(node.traceId).toBe('trace-1');
            expect(node.offsetPercent).toBe(0);
            expect(node.widthPercent).toBe(100);
            expect(node.hierarchyDepth).toBe(0);
            expect(node.children).toHaveLength(0);
        });

        it('supports nested children', () => {
            const tree: TimelineNodeViewDto = {
                traceId: 'parent-1',
                typeName: 'ParentPlugin',
                messageName: 'Create',
                depth: 1,
                durationMs: 500,
                hasException: false,
                offsetPercent: 0,
                widthPercent: 100,
                hierarchyDepth: 0,
                children: [
                    {
                        traceId: 'child-1',
                        typeName: 'ChildPlugin',
                        messageName: 'Update',
                        depth: 2,
                        durationMs: 150,
                        hasException: false,
                        offsetPercent: 10,
                        widthPercent: 30,
                        hierarchyDepth: 1,
                        children: [
                            {
                                traceId: 'grandchild-1',
                                typeName: 'GrandchildPlugin',
                                messageName: null,
                                depth: 3,
                                durationMs: null,
                                hasException: true,
                                offsetPercent: 15,
                                widthPercent: 10,
                                hierarchyDepth: 2,
                                children: [],
                            },
                        ],
                    },
                ],
            };
            expect(tree.children).toHaveLength(1);
            expect(tree.children[0].children).toHaveLength(1);
            expect(tree.children[0].children[0].hasException).toBe(true);
            expect(tree.children[0].children[0].durationMs).toBeNull();
        });
    });
});
