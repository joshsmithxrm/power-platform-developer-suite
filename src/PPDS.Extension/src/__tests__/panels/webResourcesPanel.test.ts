import { describe, it, expect } from 'vitest';

import type {
    WebResourcesPanelWebviewToHost,
    WebResourcesPanelHostToWebview,
    SolutionOptionDto,
} from '../../panels/webview/shared/message-types.js';

import type { WebResourceInfoDto } from '../../types.js';

describe('WebResourcesPanel message types', () => {
    describe('WebviewToHost', () => {
        it('covers all commands', () => {
            const messages: WebResourcesPanelWebviewToHost[] = [
                { command: 'ready' },
                { command: 'refresh' },
                { command: 'requestEnvironmentList' },
                { command: 'requestSolutionList' },
                { command: 'selectSolution', solutionId: '00000000-0000-0000-0000-000000000001' },
                { command: 'toggleTextOnly', textOnly: true },
                {
                    command: 'openWebResource',
                    id: 'wr-id-1',
                    name: 'scripts/main.js',
                    isTextType: true,
                    webResourceType: 3,
                },
                { command: 'publishSelected', ids: ['wr-id-1', 'wr-id-2'] },
                { command: 'publishAll' },
                { command: 'openInMaker' },
                { command: 'serverSearch', term: 'main.js' },
                { command: 'copyToClipboard', text: 'scripts/main.js' },
                { command: 'webviewError', error: 'test error', stack: 'Error\n  at ...' },
            ];
            expect(messages).toHaveLength(13);
        });

        it('selectSolution accepts null to clear the filter', () => {
            const msg: WebResourcesPanelWebviewToHost = {
                command: 'selectSolution',
                solutionId: null,
            };
            expect(msg.solutionId).toBeNull();
        });

        it('toggleTextOnly can show all resource types', () => {
            const showAll: WebResourcesPanelWebviewToHost = {
                command: 'toggleTextOnly',
                textOnly: false,
            };
            const textOnly: WebResourcesPanelWebviewToHost = {
                command: 'toggleTextOnly',
                textOnly: true,
            };
            expect(showAll.textOnly).toBe(false);
            expect(textOnly.textOnly).toBe(true);
        });

        it('openWebResource carries type metadata', () => {
            const msg: WebResourcesPanelWebviewToHost = {
                command: 'openWebResource',
                id: 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb',
                name: 'images/logo.png',
                isTextType: false,
                webResourceType: 6,
            };
            expect(msg.isTextType).toBe(false);
            expect(msg.webResourceType).toBe(6);
        });

        it('publishSelected accepts an empty ids array', () => {
            const msg: WebResourcesPanelWebviewToHost = {
                command: 'publishSelected',
                ids: [],
            };
            expect(msg.ids).toHaveLength(0);
        });

        it('webviewError stack is optional', () => {
            const msg: WebResourcesPanelWebviewToHost = {
                command: 'webviewError',
                error: 'Uncaught TypeError',
            };
            expect(msg.command).toBe('webviewError');
        });
    });

    describe('HostToWebview', () => {
        it('covers all commands', () => {
            const messages: WebResourcesPanelHostToWebview[] = [
                { command: 'updateEnvironment', name: 'dev', envType: 'Sandbox', envColor: '#00ff00' },
                { command: 'solutionListLoaded', solutions: [] },
                { command: 'webResourcesLoaded', resources: [], requestId: 1, totalCount: 0 },
                {
                    command: 'webResourcesPage',
                    resources: [],
                    requestId: 1,
                    loadedSoFar: 250,
                    totalCount: 1000,
                },
                { command: 'webResourcesLoadComplete', requestId: 1, totalCount: 1000 },
                { command: 'loading' },
                { command: 'error', message: 'load failed' },
                { command: 'publishResult', count: 3 },
                { command: 'daemonReconnected' },
            ];
            expect(messages).toHaveLength(9);
        });

        it('updateEnvironment accepts null envType and envColor', () => {
            const msg: WebResourcesPanelHostToWebview = {
                command: 'updateEnvironment',
                name: 'test',
                envType: null,
                envColor: null,
            };
            expect(msg.envType).toBeNull();
            expect(msg.envColor).toBeNull();
        });

        it('webResourcesLoaded resets the table with first page', () => {
            const msg: WebResourcesPanelHostToWebview = {
                command: 'webResourcesLoaded',
                resources: [
                    {
                        id: 'aaaa',
                        name: 'scripts/app.js',
                        type: 3,
                        typeName: 'Script (JScript)',
                        fileExtension: '.js',
                        isManaged: false,
                        isTextType: true,
                    },
                ],
                requestId: 5,
                totalCount: 250,
            };
            if (msg.command === 'webResourcesLoaded') {
                expect(msg.resources).toHaveLength(1);
                expect(msg.requestId).toBe(5);
                expect(msg.totalCount).toBe(250);
            }
        });

        it('webResourcesPage carries pagination progress', () => {
            const msg: WebResourcesPanelHostToWebview = {
                command: 'webResourcesPage',
                resources: [],
                requestId: 2,
                loadedSoFar: 500,
                totalCount: 2000,
            };
            if (msg.command === 'webResourcesPage') {
                expect(msg.loadedSoFar).toBe(500);
                expect(msg.totalCount).toBe(2000);
                expect(msg.requestId).toBe(2);
            }
        });

        it('publishResult carries error for failed publish', () => {
            const msg: WebResourcesPanelHostToWebview = {
                command: 'publishResult',
                count: 0,
                error: 'Publish failed: permission denied',
            };
            if (msg.command === 'publishResult') {
                expect(msg.count).toBe(0);
                expect(msg.error).toBe('Publish failed: permission denied');
            }
        });

        it('publishResult error is optional on success', () => {
            const msg: WebResourcesPanelHostToWebview = {
                command: 'publishResult',
                count: 5,
            };
            if (msg.command === 'publishResult') {
                expect(msg.count).toBe(5);
                expect(msg.error).toBeUndefined();
            }
        });
    });

    describe('WebResourceInfoDto', () => {
        it('has all required fields', () => {
            const dto: WebResourceInfoDto = {
                id: 'cccccccc-cccc-cccc-cccc-cccccccccccc',
                name: 'cr_/js/formscripts.js',
                type: 3,
                typeName: 'Script (JScript)',
                fileExtension: '.js',
                isManaged: false,
                isTextType: true,
            };
            expect(dto.name).toBe('cr_/js/formscripts.js');
            expect(dto.type).toBe(3);
            expect(dto.isTextType).toBe(true);
            expect(dto.isManaged).toBe(false);
        });

        it('handles optional display name and audit fields', () => {
            const dto: WebResourceInfoDto = {
                id: 'dddddddd-dddd-dddd-dddd-dddddddddddd',
                name: 'cr_/css/styles.css',
                displayName: 'Main Stylesheet',
                type: 2,
                typeName: 'Style Sheet (CSS)',
                fileExtension: '.css',
                isManaged: true,
                isTextType: true,
                createdBy: 'admin@contoso.com',
                createdOn: '2025-01-01T00:00:00Z',
                modifiedBy: 'dev@contoso.com',
                modifiedOn: '2026-03-01T00:00:00Z',
            };
            expect(dto.displayName).toBe('Main Stylesheet');
            expect(dto.modifiedBy).toBe('dev@contoso.com');
        });

        it('handles non-text resource types', () => {
            const dto: WebResourceInfoDto = {
                id: 'eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee',
                name: 'cr_/images/logo.png',
                type: 6,
                typeName: 'PNG format',
                fileExtension: '.png',
                isManaged: false,
                isTextType: false,
            };
            expect(dto.isTextType).toBe(false);
            expect(dto.type).toBe(6);
            expect(dto.displayName).toBeUndefined();
        });
    });

    describe('SolutionOptionDto', () => {
        it('has all required fields', () => {
            const dto: SolutionOptionDto = {
                id: '00000000-0000-0000-0000-000000000001',
                uniqueName: 'MySolution',
                friendlyName: 'My Solution',
            };
            expect(dto.id).toBe('00000000-0000-0000-0000-000000000001');
            expect(dto.uniqueName).toBe('MySolution');
            expect(dto.friendlyName).toBe('My Solution');
        });

        it('solutionListLoaded carries a list of solution options', () => {
            const msg: WebResourcesPanelHostToWebview = {
                command: 'solutionListLoaded',
                solutions: [
                    { id: '11111111-1111-1111-1111-111111111111', uniqueName: 'SolutionA', friendlyName: 'Solution A' },
                    { id: '22222222-2222-2222-2222-222222222222', uniqueName: 'SolutionB', friendlyName: 'Solution B' },
                ],
            };
            if (msg.command === 'solutionListLoaded') {
                expect(msg.solutions).toHaveLength(2);
                expect(msg.solutions[0].uniqueName).toBe('SolutionA');
            }
        });
    });
});
