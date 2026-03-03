import { describe, it, expect, vi, beforeEach } from 'vitest';

// ── Mock: vscode ────────────────────────────────────────────────────────────

const mockOutputChannel = {
    appendLine: vi.fn(),
    dispose: vi.fn(),
};

vi.mock('vscode', () => ({
    window: {
        createOutputChannel: vi.fn(() => mockOutputChannel),
        showErrorMessage: vi.fn(),
    },
}));

// ── Mock: vscode-jsonrpc/node ───────────────────────────────────────────────

const mockConnection = {
    listen: vi.fn(),
    sendRequest: vi.fn(),
    onNotification: vi.fn(),
    dispose: vi.fn(),
};

vi.mock('vscode-jsonrpc/node', () => ({
    createMessageConnection: vi.fn(() => mockConnection),
    StreamMessageReader: vi.fn(),
    StreamMessageWriter: vi.fn(),
}));

// ── Mock: child_process ─────────────────────────────────────────────────────

const mockProcess = {
    stdout: { on: vi.fn() },
    stdin: { on: vi.fn() },
    stderr: { on: vi.fn() },
    on: vi.fn(),
    kill: vi.fn(),
};

vi.mock('child_process', () => ({
    spawn: vi.fn(() => mockProcess),
}));

// ── Import after mocks ─────────────────────────────────────────────────────

import { DaemonClient } from '../daemonClient.js';

describe('DaemonClient', () => {
    let client: DaemonClient;

    beforeEach(() => {
        vi.clearAllMocks();
        client = new DaemonClient();
    });

    describe('ensureConnected', () => {
        it('should start the daemon on first RPC call', async () => {
            const mockResult = {
                activeProfile: null,
                activeProfileIndex: null,
                profiles: [],
            };
            mockConnection.sendRequest.mockResolvedValueOnce(mockResult);

            await client.authList();

            // start() was called, which calls spawn and createMessageConnection
            const { spawn } = await import('child_process');
            expect(spawn).toHaveBeenCalledWith('ppds', ['serve'], {
                stdio: ['pipe', 'pipe', 'pipe'],
            });
            expect(mockConnection.listen).toHaveBeenCalled();
        });

        it('should not spawn multiple daemon processes on concurrent calls', async () => {
            const mockAuthListResult = {
                activeProfile: null,
                activeProfileIndex: null,
                profiles: [],
            };
            const mockAuthWhoResult = {
                index: 0,
                name: 'test',
                authMethod: 'DeviceCode',
                cloud: 'Public',
                tenantId: 'tenant-id',
                username: 'user@example.com',
                objectId: null,
                applicationId: null,
                tokenExpiresOn: null,
                tokenStatus: null,
                environment: null,
                createdAt: null,
                lastUsedAt: null,
            };
            // Route mock responses by RPC method name so ordering doesn't matter
            mockConnection.sendRequest.mockImplementation((method: string) => {
                if (method === 'auth/list') return Promise.resolve(mockAuthListResult);
                if (method === 'auth/who') return Promise.resolve(mockAuthWhoResult);
                return Promise.reject(new Error(`Unexpected method: ${method}`));
            });

            // Call two methods concurrently before the daemon is connected
            const [result1, result2] = await Promise.all([
                client.authList(),
                client.authWho(),
            ]);

            // Both calls should succeed
            expect(result1).toEqual(mockAuthListResult);
            expect(result2).toEqual(mockAuthWhoResult);

            // start() should only have been called once (spawn called once)
            const { spawn } = await import('child_process');
            expect(spawn).toHaveBeenCalledTimes(1);

            // Both RPC requests should have been sent
            expect(mockConnection.sendRequest).toHaveBeenCalledTimes(2);
        });

        it('should not restart if already connected', async () => {
            const mockResult = {
                activeProfile: null,
                activeProfileIndex: null,
                profiles: [],
            };
            mockConnection.sendRequest.mockResolvedValue(mockResult);

            // Call twice
            await client.authList();
            await client.authList();

            // spawn should only be called once
            const { spawn } = await import('child_process');
            expect(spawn).toHaveBeenCalledTimes(1);
        });
    });

    describe('authList', () => {
        it('should call auth/list and return result', async () => {
            const mockResult = {
                activeProfile: 'test-profile',
                activeProfileIndex: 0,
                profiles: [
                    {
                        index: 0,
                        name: 'test-profile',
                        identity: 'user@example.com',
                        authMethod: 'DeviceCode',
                        cloud: 'Public',
                        environment: { url: 'https://org.crm.dynamics.com', displayName: 'Test Org' },
                        isActive: true,
                        createdAt: '2026-01-01T00:00:00Z',
                        lastUsedAt: '2026-03-01T00:00:00Z',
                    },
                ],
            };
            mockConnection.sendRequest.mockResolvedValueOnce(mockResult);

            const result = await client.authList();

            expect(mockConnection.sendRequest).toHaveBeenCalledWith('auth/list');
            expect(result).toEqual(mockResult);
            expect(result.profiles).toHaveLength(1);
            expect(mockOutputChannel.appendLine).toHaveBeenCalledWith('Calling auth/list...');
        });
    });

    describe('authWho', () => {
        it('should call auth/who and return result', async () => {
            const mockResult = {
                index: 0,
                name: 'test',
                authMethod: 'DeviceCode',
                cloud: 'Public',
                tenantId: 'tenant-id',
                username: 'user@example.com',
                objectId: null,
                applicationId: null,
                tokenExpiresOn: null,
                tokenStatus: null,
                environment: null,
                createdAt: null,
                lastUsedAt: null,
            };
            mockConnection.sendRequest.mockResolvedValueOnce(mockResult);

            const result = await client.authWho();

            expect(mockConnection.sendRequest).toHaveBeenCalledWith('auth/who');
            expect(result).toEqual(mockResult);
            expect(mockOutputChannel.appendLine).toHaveBeenCalledWith('Calling auth/who...');
        });
    });

    describe('authSelect', () => {
        it('should call auth/select with index param', async () => {
            const mockResult = {
                index: 1,
                name: 'prod',
                identity: 'admin@example.com',
                environment: 'https://org.crm.dynamics.com',
            };
            mockConnection.sendRequest.mockResolvedValueOnce(mockResult);

            const result = await client.authSelect({ index: 1 });

            expect(mockConnection.sendRequest).toHaveBeenCalledWith('auth/select', { index: 1 });
            expect(result).toEqual(mockResult);
        });

        it('should call auth/select with name param', async () => {
            const mockResult = {
                index: 0,
                name: 'dev',
                identity: 'dev@example.com',
                environment: null,
            };
            mockConnection.sendRequest.mockResolvedValueOnce(mockResult);

            const result = await client.authSelect({ name: 'dev' });

            expect(mockConnection.sendRequest).toHaveBeenCalledWith('auth/select', { name: 'dev' });
            expect(result).toEqual(mockResult);
        });
    });

    describe('envList', () => {
        it('should call env/list without filter', async () => {
            const mockResult = {
                filter: null,
                environments: [],
            };
            mockConnection.sendRequest.mockResolvedValueOnce(mockResult);

            const result = await client.envList();

            expect(mockConnection.sendRequest).toHaveBeenCalledWith('env/list', {});
            expect(result).toEqual(mockResult);
        });

        it('should call env/list with filter', async () => {
            const mockResult = {
                filter: 'prod',
                environments: [
                    {
                        id: '123',
                        environmentId: 'env-123',
                        friendlyName: 'Production',
                        uniqueName: 'org_prod',
                        apiUrl: 'https://org.crm.dynamics.com/api/data/v9.2',
                        url: 'https://org.crm.dynamics.com',
                        type: 'Production',
                        state: 'Ready',
                        region: 'NA',
                        version: '9.2.0.0',
                        isActive: true,
                    },
                ],
            };
            mockConnection.sendRequest.mockResolvedValueOnce(mockResult);

            const result = await client.envList('prod');

            expect(mockConnection.sendRequest).toHaveBeenCalledWith('env/list', { filter: 'prod' });
            expect(result.environments).toHaveLength(1);
        });
    });

    describe('envSelect', () => {
        it('should call env/select with environment param', async () => {
            const mockResult = {
                url: 'https://org.crm.dynamics.com',
                displayName: 'Production',
                uniqueName: 'org_prod',
                environmentId: 'env-123',
                resolutionMethod: 'url',
            };
            mockConnection.sendRequest.mockResolvedValueOnce(mockResult);

            const result = await client.envSelect('https://org.crm.dynamics.com');

            expect(mockConnection.sendRequest).toHaveBeenCalledWith('env/select', {
                environment: 'https://org.crm.dynamics.com',
            });
            expect(result).toEqual(mockResult);
        });
    });

    describe('querySql', () => {
        it('should call query/sql with params', async () => {
            const mockResult = {
                success: true,
                entityName: 'account',
                columns: [{ logicalName: 'name', alias: null, displayName: 'Name', dataType: 'String', linkedEntityAlias: null }],
                records: [{ name: 'Contoso' }],
                count: 1,
                totalCount: null,
                moreRecords: false,
                pagingCookie: null,
                pageNumber: 1,
                isAggregate: false,
                executedFetchXml: null,
                executionTimeMs: 42,
            };
            mockConnection.sendRequest.mockResolvedValueOnce(mockResult);

            const params = { sql: 'SELECT name FROM account', top: 10 };
            const result = await client.querySql(params);

            expect(mockConnection.sendRequest).toHaveBeenCalledWith('query/sql', params);
            expect(result.count).toBe(1);
            expect(result.records[0]).toEqual({ name: 'Contoso' });
        });
    });

    describe('queryFetch', () => {
        it('should call query/fetch with params', async () => {
            const mockResult = {
                success: true,
                entityName: 'contact',
                columns: [],
                records: [],
                count: 0,
                totalCount: null,
                moreRecords: false,
                pagingCookie: null,
                pageNumber: 1,
                isAggregate: false,
                executedFetchXml: '<fetch><entity name="contact" /></fetch>',
                executionTimeMs: 10,
            };
            mockConnection.sendRequest.mockResolvedValueOnce(mockResult);

            const params = { fetchXml: '<fetch><entity name="contact" /></fetch>' };
            const result = await client.queryFetch(params);

            expect(mockConnection.sendRequest).toHaveBeenCalledWith('query/fetch', params);
            expect(result.success).toBe(true);
        });
    });

    describe('envWho', () => {
        it('should call env/who and return result', async () => {
            const mockResult = {
                connectedAs: 'user@example.com',
                organizationName: 'Test Org',
                organizationId: 'org-123',
                environmentId: 'env-456',
                userId: 'user-789',
                businessUnitId: 'bu-001',
            };
            mockConnection.sendRequest.mockResolvedValueOnce(mockResult);

            const result = await client.envWho();

            expect(mockConnection.sendRequest).toHaveBeenCalledWith('env/who');
            expect(result).toEqual(mockResult);
            expect(mockOutputChannel.appendLine).toHaveBeenCalledWith('Calling env/who...');
        });
    });

    describe('envConfigGet', () => {
        it('should call env/config/get with environmentUrl', async () => {
            const mockResult = {
                label: 'Production',
                type: 'Production',
                color: '#FF0000',
            };
            mockConnection.sendRequest.mockResolvedValueOnce(mockResult);

            const result = await client.envConfigGet('https://org.crm.dynamics.com');

            expect(mockConnection.sendRequest).toHaveBeenCalledWith('env/config/get', {
                environmentUrl: 'https://org.crm.dynamics.com',
            });
            expect(result).toEqual(mockResult);
        });

        it('should handle null config values', async () => {
            const mockResult = {
                label: null,
                type: null,
                color: null,
            };
            mockConnection.sendRequest.mockResolvedValueOnce(mockResult);

            const result = await client.envConfigGet('https://dev.crm.dynamics.com');

            expect(mockConnection.sendRequest).toHaveBeenCalledWith('env/config/get', {
                environmentUrl: 'https://dev.crm.dynamics.com',
            });
            expect(result).toEqual(mockResult);
        });
    });

    describe('envConfigSet', () => {
        it('should call env/config/set with all params', async () => {
            const mockResult = {
                saved: true,
            };
            mockConnection.sendRequest.mockResolvedValueOnce(mockResult);

            const params = {
                environmentUrl: 'https://org.crm.dynamics.com',
                label: 'Production',
                type: 'Production',
                color: '#FF0000',
            };
            const result = await client.envConfigSet(params);

            expect(mockConnection.sendRequest).toHaveBeenCalledWith('env/config/set', params);
            expect(result.saved).toBe(true);
        });

        it('should call env/config/set with only required params', async () => {
            const mockResult = {
                saved: true,
            };
            mockConnection.sendRequest.mockResolvedValueOnce(mockResult);

            const params = {
                environmentUrl: 'https://org.crm.dynamics.com',
            };
            const result = await client.envConfigSet(params);

            expect(mockConnection.sendRequest).toHaveBeenCalledWith('env/config/set', params);
            expect(result.saved).toBe(true);
        });
    });

    describe('queryComplete', () => {
        it('should call query/complete with sql and cursorOffset', async () => {
            const mockResult = {
                items: [
                    { label: 'account', kind: 'table' },
                    { label: 'accountid', kind: 'column' },
                ],
            };
            mockConnection.sendRequest.mockResolvedValueOnce(mockResult);

            const params = { sql: 'SELECT * FROM acc', cursorOffset: 18 };
            const result = await client.queryComplete(params);

            expect(mockConnection.sendRequest).toHaveBeenCalledWith('query/complete', params);
            expect(result.items).toHaveLength(2);
        });
    });

    describe('queryHistoryList', () => {
        it('should call query/history/list without params', async () => {
            const mockResult = {
                entries: [],
            };
            mockConnection.sendRequest.mockResolvedValueOnce(mockResult);

            const result = await client.queryHistoryList();

            expect(mockConnection.sendRequest).toHaveBeenCalledWith('query/history/list', {});
            expect(result.entries).toHaveLength(0);
        });

        it('should call query/history/list with search and limit', async () => {
            const mockResult = {
                entries: [
                    { id: 'h-1', sql: 'SELECT name FROM account', executedAt: '2026-03-01T00:00:00Z' },
                ],
            };
            mockConnection.sendRequest.mockResolvedValueOnce(mockResult);

            const result = await client.queryHistoryList('account', 10);

            expect(mockConnection.sendRequest).toHaveBeenCalledWith('query/history/list', {
                search: 'account',
                limit: 10,
            });
            expect(result.entries).toHaveLength(1);
        });

        it('should call query/history/list with only search', async () => {
            const mockResult = {
                entries: [],
            };
            mockConnection.sendRequest.mockResolvedValueOnce(mockResult);

            const result = await client.queryHistoryList('contact');

            expect(mockConnection.sendRequest).toHaveBeenCalledWith('query/history/list', {
                search: 'contact',
            });
            expect(result.entries).toHaveLength(0);
        });
    });

    describe('queryHistoryDelete', () => {
        it('should call query/history/delete with id', async () => {
            const mockResult = {
                deleted: true,
            };
            mockConnection.sendRequest.mockResolvedValueOnce(mockResult);

            const result = await client.queryHistoryDelete('h-123');

            expect(mockConnection.sendRequest).toHaveBeenCalledWith('query/history/delete', {
                id: 'h-123',
            });
            expect(result.deleted).toBe(true);
        });
    });

    describe('queryExport', () => {
        it('should call query/export with params', async () => {
            const mockResult = {
                content: 'name\nContoso\nAdventureWorks',
                format: 'csv',
                rowCount: 2,
            };
            mockConnection.sendRequest.mockResolvedValueOnce(mockResult);

            const params = { sql: 'SELECT name FROM account', format: 'csv', includeHeaders: true, top: 100 };
            const result = await client.queryExport(params);

            expect(mockConnection.sendRequest).toHaveBeenCalledWith('query/export', params);
            expect(result.rowCount).toBe(2);
            expect(result.format).toBe('csv');
        });

        it('should call query/export with only required params', async () => {
            const mockResult = {
                content: '{"records":[]}',
                format: 'json',
                rowCount: 0,
            };
            mockConnection.sendRequest.mockResolvedValueOnce(mockResult);

            const params = { sql: 'SELECT name FROM contact' };
            const result = await client.queryExport(params);

            expect(mockConnection.sendRequest).toHaveBeenCalledWith('query/export', params);
            expect(result.rowCount).toBe(0);
        });
    });

    describe('queryExplain', () => {
        it('should call query/explain with sql', async () => {
            const mockResult = {
                fetchXml: '<fetch><entity name="account"><attribute name="name" /></entity></fetch>',
                format: 'fetchxml',
            };
            mockConnection.sendRequest.mockResolvedValueOnce(mockResult);

            const result = await client.queryExplain('SELECT name FROM account');

            expect(mockConnection.sendRequest).toHaveBeenCalledWith('query/explain', {
                sql: 'SELECT name FROM account',
            });
            expect(result.format).toBe('fetchxml');
        });
    });

    describe('profilesCreate', () => {
        it('should call profiles/create with params', async () => {
            const mockResult = {
                index: 1,
                name: 'new-profile',
            };
            mockConnection.sendRequest.mockResolvedValueOnce(mockResult);

            const params = {
                name: 'new-profile',
                authMethod: 'ClientSecret',
                environmentUrl: 'https://org.crm.dynamics.com',
                applicationId: 'app-123',
                clientSecret: 'secret-456',
                tenantId: 'tenant-789',
            };
            const result = await client.profilesCreate(params);

            expect(mockConnection.sendRequest).toHaveBeenCalledWith('profiles/create', params);
            expect(result.index).toBe(1);
            expect(result.name).toBe('new-profile');
        });

        it('should call profiles/create with minimal params', async () => {
            const mockResult = {
                index: 0,
                name: null,
            };
            mockConnection.sendRequest.mockResolvedValueOnce(mockResult);

            const params = {
                authMethod: 'DeviceCode',
            };
            const result = await client.profilesCreate(params);

            expect(mockConnection.sendRequest).toHaveBeenCalledWith('profiles/create', params);
            expect(result.index).toBe(0);
        });
    });

    describe('profilesDelete', () => {
        it('should call profiles/delete with index', async () => {
            const mockResult = {
                deleted: true,
            };
            mockConnection.sendRequest.mockResolvedValueOnce(mockResult);

            const result = await client.profilesDelete({ index: 1 });

            expect(mockConnection.sendRequest).toHaveBeenCalledWith('profiles/delete', { index: 1 });
            expect(result.deleted).toBe(true);
        });

        it('should call profiles/delete with name', async () => {
            const mockResult = {
                deleted: true,
            };
            mockConnection.sendRequest.mockResolvedValueOnce(mockResult);

            const result = await client.profilesDelete({ name: 'old-profile' });

            expect(mockConnection.sendRequest).toHaveBeenCalledWith('profiles/delete', { name: 'old-profile' });
            expect(result.deleted).toBe(true);
        });
    });

    describe('profilesRename', () => {
        it('should call profiles/rename with currentName and newName', async () => {
            const mockResult = {
                previousName: 'old-name',
                newName: 'new-name',
            };
            mockConnection.sendRequest.mockResolvedValueOnce(mockResult);

            const result = await client.profilesRename('old-name', 'new-name');

            expect(mockConnection.sendRequest).toHaveBeenCalledWith('profiles/rename', {
                currentName: 'old-name',
                newName: 'new-name',
            });
            expect(result.previousName).toBe('old-name');
            expect(result.newName).toBe('new-name');
        });
    });

    describe('profilesInvalidate', () => {
        it('should call profiles/invalidate with profile name', async () => {
            const mockResult = {
                profileName: 'test-profile',
                invalidated: true,
            };
            mockConnection.sendRequest.mockResolvedValueOnce(mockResult);

            const result = await client.profilesInvalidate('test-profile');

            expect(mockConnection.sendRequest).toHaveBeenCalledWith('profiles/invalidate', {
                profileName: 'test-profile',
            });
            expect(result.invalidated).toBe(true);
        });
    });

    describe('solutionsList', () => {
        it('should call solutions/list without params', async () => {
            const mockResult = {
                solutions: [],
            };
            mockConnection.sendRequest.mockResolvedValueOnce(mockResult);

            const result = await client.solutionsList();

            expect(mockConnection.sendRequest).toHaveBeenCalledWith('solutions/list', {});
            expect(result.solutions).toHaveLength(0);
        });

        it('should call solutions/list with filter and includeManaged', async () => {
            const mockResult = {
                solutions: [
                    {
                        id: 'sol-123',
                        uniqueName: 'MySolution',
                        friendlyName: 'My Solution',
                        version: '1.0.0.0',
                        isManaged: false,
                        publisherName: 'TestPublisher',
                        description: 'A test solution',
                    },
                ],
            };
            mockConnection.sendRequest.mockResolvedValueOnce(mockResult);

            const result = await client.solutionsList('My', true);

            expect(mockConnection.sendRequest).toHaveBeenCalledWith('solutions/list', {
                filter: 'My',
                includeManaged: true,
            });
            expect(result.solutions).toHaveLength(1);
        });
    });

    describe('schemaEntities', () => {
        it('should call schema/entities and return result', async () => {
            const mockResult = {
                entities: [
                    { logicalName: 'account', displayName: 'Account', schemaName: 'Account' },
                    { logicalName: 'contact', displayName: 'Contact', schemaName: 'Contact' },
                ],
            };
            mockConnection.sendRequest.mockResolvedValueOnce(mockResult);

            const result = await client.schemaEntities();

            expect(mockConnection.sendRequest).toHaveBeenCalledWith('schema/entities');
            expect(result.entities).toHaveLength(2);
            expect(result.entities[0].logicalName).toBe('account');
        });
    });

    describe('schemaAttributes', () => {
        it('should call schema/attributes with entity name', async () => {
            const mockResult = {
                entityName: 'account',
                attributes: [
                    { logicalName: 'name', displayName: 'Account Name', attributeType: 'String' },
                    { logicalName: 'accountid', displayName: 'Account', attributeType: 'Uniqueidentifier' },
                ],
            };
            mockConnection.sendRequest.mockResolvedValueOnce(mockResult);

            const result = await client.schemaAttributes('account');

            expect(mockConnection.sendRequest).toHaveBeenCalledWith('schema/attributes', {
                entity: 'account',
            });
            expect(result.entityName).toBe('account');
            expect(result.attributes).toHaveLength(2);
        });
    });

    describe('onDeviceCode', () => {
        it('should register notification handler on connection', async () => {
            // Must start the daemon first so connection exists
            mockConnection.sendRequest.mockResolvedValueOnce({
                activeProfile: null,
                activeProfileIndex: null,
                profiles: [],
            });
            await client.authList(); // triggers start()

            const handler = vi.fn();
            client.onDeviceCode(handler);

            expect(mockConnection.onNotification).toHaveBeenCalledWith('auth/deviceCode', handler);
        });

        it('should queue handler if called before connection is established', async () => {
            const handler = vi.fn();

            // Should NOT throw — queues for deferred registration
            expect(() => client.onDeviceCode(handler)).not.toThrow();

            // Handler not yet registered (no connection)
            expect(mockConnection.onNotification).not.toHaveBeenCalled();

            // Now connect — handler should be flushed
            mockConnection.sendRequest.mockResolvedValueOnce({ profiles: [] });
            await client.authList();

            expect(mockConnection.onNotification).toHaveBeenCalledWith('auth/deviceCode', handler);
        });
    });

    describe('dispose', () => {
        it('should clean up connection and process', async () => {
            // Start the daemon first
            mockConnection.sendRequest.mockResolvedValueOnce({
                activeProfile: null,
                activeProfileIndex: null,
                profiles: [],
            });
            await client.authList();

            client.dispose();

            expect(mockConnection.dispose).toHaveBeenCalled();
            expect(mockProcess.kill).toHaveBeenCalled();
            expect(mockOutputChannel.dispose).toHaveBeenCalled();
        });

        it('should handle dispose when not connected', () => {
            // Should not throw
            client.dispose();

            expect(mockOutputChannel.dispose).toHaveBeenCalled();
            expect(mockConnection.dispose).not.toHaveBeenCalled();
            expect(mockProcess.kill).not.toHaveBeenCalled();
        });
    });
});
