import { describe, it, expect } from 'vitest';

import type {
    ConnectionReferencesPanelWebviewToHost,
    ConnectionReferencesPanelHostToWebview,
    ConnectionReferenceViewDto,
    ConnectionReferenceDetailViewDto,
    ConnectionReferencesAnalyzeViewDto,
} from '../../panels/webview/shared/message-types.js';

describe('ConnectionReferencesPanel message types', () => {
    it('WebviewToHost covers all commands', () => {
        const messages: ConnectionReferencesPanelWebviewToHost[] = [
            { command: 'ready' },
            { command: 'refresh' },
            { command: 'selectReference', logicalName: 'cr_test' },
            { command: 'analyze' },
            { command: 'filterBySolution', solutionId: 'MySolution' },
            { command: 'setIncludeInactive', includeInactive: true },
            { command: 'requestSolutionList' },
            { command: 'requestEnvironmentList' },
            { command: 'openInMaker' },
            { command: 'openFlowInMaker', url: 'https://make.powerautomate.com/environments/env1/flows/flow1/details' },
            { command: 'syncDeploymentSettings' },
            { command: 'copyToClipboard', text: 'test' },
            { command: 'webviewError', error: 'test', stack: 'trace' },
        ];
        expect(messages).toHaveLength(13);
    });

    it('WebviewToHost filterBySolution accepts null for "All Solutions"', () => {
        const msg: ConnectionReferencesPanelWebviewToHost = {
            command: 'filterBySolution',
            solutionId: null,
        };
        expect(msg.solutionId).toBeNull();
    });

    it('HostToWebview covers all commands', () => {
        const messages: ConnectionReferencesPanelHostToWebview[] = [
            { command: 'updateEnvironment', name: 'test', envType: null, envColor: null },
            { command: 'loading' },
            { command: 'connectionReferencesLoaded', references: [], totalCount: 0, filtersApplied: [] },
            { command: 'connectionReferenceDetailLoaded', environmentId: null, detail: {
                logicalName: 'cr_test',
                displayName: 'Test',
                connectorId: null,
                connectionId: null,
                isManaged: false,
                modifiedOn: null,
                connectionStatus: 'Connected',
                connectorDisplayName: null,
                description: null,
                isBound: true,
                createdOn: null,
                flows: [],
                connectionOwner: null,
                connectionIsShared: null,
            } },
            { command: 'analyzeResult', result: { orphanedReferences: [], orphanedFlows: [], totalReferences: 0, totalFlows: 0 } },
            { command: 'solutionListLoaded', solutions: [] },
            { command: 'deploymentSettingsSynced', filePath: '/path/to/file.json', envVars: { added: 1, removed: 0, preserved: 2 }, connectionRefs: { added: 0, removed: 0, preserved: 1 } },
            { command: 'error', message: 'test' },
            { command: 'daemonReconnected' },
        ];
        expect(messages).toHaveLength(9);
    });

    it('ConnectionReferenceViewDto has all required fields', () => {
        const dto: ConnectionReferenceViewDto = {
            logicalName: 'cr_shared_test',
            displayName: 'Test Connection Reference',
            connectorId: '/providers/Microsoft.PowerApps/apis/shared_test',
            connectionId: '00000000-0000-0000-0000-000000000001',
            isManaged: true,
            modifiedOn: '2026-03-16T00:00:00Z',
            connectionStatus: 'Connected',
            connectorDisplayName: 'Test Connector',
        };
        expect(dto.connectionStatus).toBe('Connected');
        expect(dto.isManaged).toBe(true);
    });

    it('ConnectionReferenceDetailViewDto extends base with flows including flowId', () => {
        const dto: ConnectionReferenceDetailViewDto = {
            logicalName: 'cr_shared_test',
            displayName: 'Test',
            connectorId: null,
            connectionId: null,
            isManaged: false,
            modifiedOn: null,
            connectionStatus: 'N/A',
            connectorDisplayName: null,
            description: 'A test connection reference',
            isBound: false,
            createdOn: '2026-01-01T00:00:00Z',
            flows: [
                { flowId: '11111111-1111-1111-1111-111111111111', uniqueName: 'flow_1', displayName: 'My Flow', state: 'Active' },
            ],
            connectionOwner: 'admin@test.com',
            connectionIsShared: true,
        };
        expect(dto.flows).toHaveLength(1);
        expect(dto.flows[0].flowId).toBe('11111111-1111-1111-1111-111111111111');
        expect(dto.connectionStatus).toBe('N/A');
    });

    it('ConnectionReferencesAnalyzeViewDto reports orphans', () => {
        const dto: ConnectionReferencesAnalyzeViewDto = {
            orphanedReferences: [
                { logicalName: 'cr_orphan', displayName: 'Orphan', connectorId: null },
            ],
            orphanedFlows: [
                { uniqueName: 'flow_orphan', displayName: 'Orphan Flow', missingReference: 'cr_missing' },
            ],
            totalReferences: 5,
            totalFlows: 3,
        };
        expect(dto.orphanedReferences).toHaveLength(1);
        expect(dto.orphanedFlows).toHaveLength(1);
    });

    it('handles empty/null fields gracefully', () => {
        const dto: ConnectionReferenceViewDto = {
            logicalName: 'cr_minimal',
            displayName: null,
            connectorId: null,
            connectionId: null,
            isManaged: false,
            modifiedOn: null,
            connectionStatus: 'Unknown',
            connectorDisplayName: null,
        };
        expect(dto.displayName).toBeNull();
        expect(dto.connectionStatus).toBe('Unknown');
    });

    it('ConnectionReferenceViewDto supports optional flowCount and hasHealthWarning', () => {
        const dto: ConnectionReferenceViewDto = {
            logicalName: 'cr_health_test',
            displayName: 'Health Test',
            connectorId: null,
            connectionId: null,
            isManaged: false,
            modifiedOn: null,
            connectionStatus: 'Unbound',
            connectorDisplayName: null,
            flowCount: 3,
            hasHealthWarning: true,
        };
        expect(dto.flowCount).toBe(3);
        expect(dto.hasHealthWarning).toBe(true);
    });
});
