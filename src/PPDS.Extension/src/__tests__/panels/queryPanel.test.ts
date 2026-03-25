import { describe, it, expect } from 'vitest';

import type {
    QueryPanelWebviewToHost,
    QueryPanelHostToWebview,
} from '../../panels/webview/shared/message-types.js';

import type {
    QueryResultResponse,
    CompletionItemDto,
} from '../../types.js';

describe('QueryPanel message types', () => {
    describe('WebviewToHost', () => {
        it('covers all commands', () => {
            const messages: QueryPanelWebviewToHost[] = [
                { command: 'ready' },
                { command: 'executeQuery', sql: 'SELECT name FROM account', useTds: false, language: 'sql' },
                { command: 'showFetchXml', sql: 'SELECT name FROM account' },
                { command: 'loadMore', pagingCookie: 'abc', page: 2 },
                { command: 'explainQuery', sql: 'SELECT name FROM account' },
                { command: 'exportResults', format: 'csv' },
                { command: 'saveQuery', sql: 'SELECT 1', language: 'sql' },
                { command: 'loadQueryFromFile' },
                { command: 'openInNotebook', sql: 'SELECT name FROM account' },
                { command: 'showHistory' },
                { command: 'copyToClipboard', text: 'text' },
                { command: 'openRecordUrl', url: 'https://org.crm.dynamics.com' },
                { command: 'requestClipboard' },
                { command: 'requestCompletions', requestId: 1, sql: 'SELECT ', cursorOffset: 7, language: 'sql' },
                { command: 'webviewError', error: 'test', stack: 'trace' },
                { command: 'cancelQuery' },
                { command: 'convertQuery', sql: 'SELECT name FROM account', fromLanguage: 'sql', toLanguage: 'xml' },
                { command: 'refresh' },
                { command: 'requestEnvironmentList' },
            ];
            expect(messages).toHaveLength(19);
        });

        it('executeQuery has optional useTds and language', () => {
            const minimal: QueryPanelWebviewToHost = {
                command: 'executeQuery',
                sql: 'SELECT 1',
            };
            const full: QueryPanelWebviewToHost = {
                command: 'executeQuery',
                sql: 'SELECT 1',
                useTds: true,
                language: 'xml',
            };
            expect(minimal.command).toBe('executeQuery');
            expect(full.command).toBe('executeQuery');
        });

        it('exportResults format is optional', () => {
            const withFormat: QueryPanelWebviewToHost = {
                command: 'exportResults',
                format: 'json',
            };
            const withoutFormat: QueryPanelWebviewToHost = {
                command: 'exportResults',
            };
            expect(withFormat.command).toBe('exportResults');
            expect(withoutFormat.command).toBe('exportResults');
        });

        it('webviewError stack is optional', () => {
            const msg: QueryPanelWebviewToHost = {
                command: 'webviewError',
                error: 'test',
            };
            expect(msg.command).toBe('webviewError');
        });

        it('convertQuery specifies source and target language', () => {
            const msg: QueryPanelWebviewToHost = {
                command: 'convertQuery',
                sql: '<fetch><entity name="account"/></fetch>',
                fromLanguage: 'xml',
                toLanguage: 'sql',
            };
            expect(msg.command).toBe('convertQuery');
        });
    });

    describe('HostToWebview', () => {
        it('covers all commands', () => {
            const sampleResult: QueryResultResponse = {
                success: true,
                entityName: 'account',
                columns: [{ logicalName: 'name', alias: null, displayName: 'Name', dataType: 'String', linkedEntityAlias: null }],
                records: [{ name: 'Contoso' }],
                count: 1,
                totalCount: 100,
                moreRecords: true,
                pagingCookie: 'cookie',
                pageNumber: 1,
                isAggregate: false,
                executedFetchXml: null,
                executionTimeMs: 42,
                queryMode: 'dataverse',
            };

            const messages: QueryPanelHostToWebview[] = [
                { command: 'loadQuery', sql: 'SELECT 1' },
                { command: 'updateEnvironment', name: 'dev', url: 'https://dev.crm.dynamics.com', envType: 'Sandbox', envColor: '#00ff00' },
                { command: 'executionStarted' },
                { command: 'queryResult', data: sampleResult },
                { command: 'queryCancelled' },
                { command: 'queryError', error: 'Something failed' },
                { command: 'appendResults', data: sampleResult },
                { command: 'clipboardContent', text: 'pasted' },
                { command: 'completionResult', requestId: 1, items: [] },
                { command: 'daemonReconnected' },
                { command: 'queryConverted', content: '<fetch/>', language: 'xml' },
                { command: 'conversionFailed', error: 'parse error', language: 'xml' },
            ];
            expect(messages).toHaveLength(12);
        });

        it('updateEnvironment includes url field and accepts nulls', () => {
            const msg: QueryPanelHostToWebview = {
                command: 'updateEnvironment',
                name: 'No env',
                url: null,
                envType: null,
                envColor: null,
            };
            expect(msg.command).toBe('updateEnvironment');
        });
    });

    describe('QueryResultResponse', () => {
        it('has all required fields', () => {
            const result: QueryResultResponse = {
                success: true,
                entityName: 'contact',
                columns: [
                    { logicalName: 'fullname', alias: null, displayName: 'Full Name', dataType: 'String', linkedEntityAlias: null },
                    { logicalName: 'emailaddress1', alias: 'email', displayName: 'Email', dataType: 'String', linkedEntityAlias: null },
                ],
                records: [
                    { fullname: 'John Doe', email: 'john@contoso.com' },
                ],
                count: 1,
                totalCount: 500,
                moreRecords: true,
                pagingCookie: 'abc123',
                pageNumber: 1,
                isAggregate: false,
                executedFetchXml: '<fetch><entity name="contact"/></fetch>',
                executionTimeMs: 150,
                queryMode: 'tds',
            };
            expect(result.success).toBe(true);
            expect(result.columns).toHaveLength(2);
            expect(result.moreRecords).toBe(true);
            expect(result.queryMode).toBe('tds');
        });

        it('supports aggregate queries', () => {
            const result: QueryResultResponse = {
                success: true,
                entityName: null,
                columns: [{ logicalName: 'cnt', alias: 'cnt', displayName: null, dataType: 'Integer', linkedEntityAlias: null }],
                records: [{ cnt: 42 }],
                count: 1,
                totalCount: null,
                moreRecords: false,
                pagingCookie: null,
                pageNumber: 1,
                isAggregate: true,
                executedFetchXml: null,
                executionTimeMs: 30,
                queryMode: 'dataverse',
            };
            expect(result.isAggregate).toBe(true);
            expect(result.totalCount).toBeNull();
        });

        it('supports optional dataSources, appliedHints, and warnings', () => {
            const result: QueryResultResponse = {
                success: true,
                entityName: 'account',
                columns: [],
                records: [],
                count: 0,
                totalCount: 0,
                moreRecords: false,
                pagingCookie: null,
                pageNumber: 1,
                isAggregate: false,
                executedFetchXml: null,
                executionTimeMs: 5,
                queryMode: null,
                dataSources: [{ label: 'Dataverse', isRemote: false }],
                appliedHints: ['NOLOCK'],
                warnings: ['Query returned 0 rows'],
            };
            expect(result.dataSources).toHaveLength(1);
            expect(result.appliedHints).toHaveLength(1);
            expect(result.warnings).toHaveLength(1);
        });
    });

    describe('CompletionItemDto', () => {
        it('has all required fields', () => {
            const item: CompletionItemDto = {
                label: 'accountid',
                insertText: 'accountid',
                kind: 'Field',
                detail: 'Uniqueidentifier',
                description: 'Primary key for account',
                sortOrder: 1,
            };
            expect(item.label).toBe('accountid');
            expect(item.kind).toBe('Field');
            expect(item.sortOrder).toBe(1);
        });

        it('handles null detail and description', () => {
            const item: CompletionItemDto = {
                label: 'name',
                insertText: 'name',
                kind: 'Field',
                detail: null,
                description: null,
                sortOrder: 0,
            };
            expect(item.detail).toBeNull();
            expect(item.description).toBeNull();
        });
    });
});
