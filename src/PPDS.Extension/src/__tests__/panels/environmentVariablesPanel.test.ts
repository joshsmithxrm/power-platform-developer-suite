import { describe, it, expect } from 'vitest';

import type {
    EnvironmentVariablesPanelWebviewToHost,
    EnvironmentVariablesPanelHostToWebview,
    EnvironmentVariableViewDto,
} from '../../panels/webview/shared/message-types.js';

describe('EnvironmentVariablesPanel message types', () => {
    it('WebviewToHost covers all commands', () => {
        const messages: EnvironmentVariablesPanelWebviewToHost[] = [
            { command: 'ready' },
            { command: 'refresh' },
            { command: 'selectVariable', schemaName: 'test_var' },
            { command: 'editVariable', schemaName: 'test_var' },
            { command: 'saveVariable', schemaName: 'test_var', value: 'new_value' },
            { command: 'filterBySolution', solutionId: 'MySolution' },
            { command: 'requestSolutionList' },
            { command: 'exportDeploymentSettings' },
            { command: 'requestEnvironmentList' },
            { command: 'openInMaker' },
            { command: 'copyToClipboard', text: 'test' },
            { command: 'webviewError', error: 'test', stack: 'trace' },
        ];
        expect(messages).toHaveLength(12);
    });

    it('WebviewToHost filterBySolution accepts null for "All Solutions"', () => {
        const msg: EnvironmentVariablesPanelWebviewToHost = {
            command: 'filterBySolution',
            solutionId: null,
        };
        expect(msg.solutionId).toBeNull();
    });

    it('HostToWebview covers all commands', () => {
        const messages: EnvironmentVariablesPanelHostToWebview[] = [
            { command: 'updateEnvironment', name: 'test', envType: null, envColor: null },
            { command: 'loading' },
            { command: 'environmentVariablesLoaded', variables: [] },
            { command: 'environmentVariableDetailLoaded', detail: {
                schemaName: 'test_var',
                displayName: 'Test Variable',
                type: 'String',
                defaultValue: 'default',
                currentValue: 'current',
                isManaged: false,
                isRequired: true,
                hasOverride: true,
                isMissing: false,
                modifiedOn: null,
                description: 'A test variable',
            } },
            { command: 'editVariableDialog', schemaName: 'test_var', displayName: 'Test', type: 'String', currentValue: 'value' },
            { command: 'variableSaved', schemaName: 'test_var', success: true },
            { command: 'solutionListLoaded', solutions: [] },
            { command: 'deploymentSettingsExported', filePath: '/path/to/file.json' },
            { command: 'error', message: 'test' },
            { command: 'daemonReconnected' },
        ];
        expect(messages).toHaveLength(10);
    });

    it('EnvironmentVariableViewDto has all required fields', () => {
        const dto: EnvironmentVariableViewDto = {
            schemaName: 'test_ConnectionString',
            displayName: 'Connection String',
            type: 'String',
            defaultValue: 'Server=localhost',
            currentValue: 'Server=prod.database.com',
            isManaged: false,
            isRequired: true,
            hasOverride: true,
            isMissing: false,
            modifiedOn: '2026-03-16T00:00:00Z',
        };
        expect(dto.hasOverride).toBe(true);
        expect(dto.isMissing).toBe(false);
        expect(dto.type).toBe('String');
    });

    it('identifies missing required variables', () => {
        const dto: EnvironmentVariableViewDto = {
            schemaName: 'test_Required',
            displayName: 'Required Variable',
            type: 'Number',
            defaultValue: null,
            currentValue: null,
            isManaged: false,
            isRequired: true,
            hasOverride: false,
            isMissing: true,
            modifiedOn: null,
        };
        expect(dto.isMissing).toBe(true);
        expect(dto.isRequired).toBe(true);
        expect(dto.currentValue).toBeNull();
        expect(dto.defaultValue).toBeNull();
    });

    it('handles all variable types', () => {
        const types = ['String', 'Number', 'Boolean', 'JSON', 'DataSource'];
        for (const type of types) {
            const dto: EnvironmentVariableViewDto = {
                schemaName: `test_${type}`,
                displayName: type,
                type,
                defaultValue: null,
                currentValue: null,
                isManaged: false,
                isRequired: false,
                hasOverride: false,
                isMissing: false,
                modifiedOn: null,
            };
            expect(dto.type).toBe(type);
        }
    });

    it('handles empty/null fields gracefully', () => {
        const dto: EnvironmentVariableViewDto = {
            schemaName: 'test_minimal',
            displayName: null,
            type: 'String',
            defaultValue: null,
            currentValue: null,
            isManaged: false,
            isRequired: false,
            hasOverride: false,
            isMissing: false,
            modifiedOn: null,
        };
        expect(dto.displayName).toBeNull();
        expect(dto.schemaName).toBe('test_minimal');
    });
});
