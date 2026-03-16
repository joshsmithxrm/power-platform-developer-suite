import { describe, it, expect } from 'vitest';

import type {
    ImportJobsPanelWebviewToHost,
    ImportJobsPanelHostToWebview,
    ImportJobViewDto,
} from '../../panels/webview/shared/message-types.js';

describe('ImportJobsPanel message types', () => {
    it('WebviewToHost covers all commands', () => {
        const messages: ImportJobsPanelWebviewToHost[] = [
            { command: 'ready' },
            { command: 'refresh' },
            { command: 'selectJob', id: 'test-id' },
            { command: 'requestEnvironmentList' },
            { command: 'openInMaker' },
            { command: 'copyToClipboard', text: 'test' },
            { command: 'webviewError', error: 'test', stack: 'trace' },
        ];
        expect(messages).toHaveLength(7);
    });

    it('HostToWebview covers all commands', () => {
        const messages: ImportJobsPanelHostToWebview[] = [
            { command: 'updateEnvironment', name: 'test', envType: null, envColor: null },
            { command: 'importJobsLoaded', jobs: [] },
            { command: 'importJobDetailLoaded', id: 'test', data: null },
            { command: 'loading' },
            { command: 'error', message: 'test' },
            { command: 'daemonReconnected' },
        ];
        expect(messages).toHaveLength(6);
    });

    it('ImportJobViewDto has all required fields', () => {
        const dto: ImportJobViewDto = {
            id: '00000000-0000-0000-0000-000000000001',
            solutionName: 'TestSolution',
            status: 'Succeeded',
            progress: 100,
            createdBy: 'admin@test.com',
            createdOn: '2026-03-16T00:00:00Z',
            startedOn: '2026-03-16T00:00:00Z',
            completedOn: '2026-03-16T00:01:00Z',
            duration: '1m 0s',
        };
        expect(dto.status).toBe('Succeeded');
        expect(dto.progress).toBe(100);
    });

    it('handles empty/null fields gracefully', () => {
        const dto: ImportJobViewDto = {
            id: '00000000-0000-0000-0000-000000000002',
            solutionName: null,
            status: 'In Progress',
            progress: 45,
            createdBy: null,
            createdOn: null,
            startedOn: null,
            completedOn: null,
            duration: null,
        };
        expect(dto.solutionName).toBeNull();
        expect(dto.status).toBe('In Progress');
    });
});
