import { describe, expect, it } from 'vitest';

import {
    buildIsolatedProcessEnvironment,
    buildVsCodeLaunchArgs,
    minimumVersionFromEngineRange,
    resolveVsCodeVersion,
    startupAttempts,
} from '../../e2e/config.js';

describe('VS Code E2E configuration', () => {
    it.each([
        ['^1.116.0', '1.116.0'],
        ['~1.117.2', '1.117.2'],
        ['>=1.118.3', '1.118.3'],
        ['1.119.4', '1.119.4'],
    ])('extracts the minimum version from %s', (engineRange, expected) => {
        expect(minimumVersionFromEngineRange(engineRange)).toBe(expected);
    });

    it('rejects an engine range without a semantic version', () => {
        expect(() => minimumVersionFromEngineRange('latest')).toThrow(/minimum VS Code version/);
    });

    it('uses stable by default and resolves the declared minimum on request', () => {
        expect(resolveVsCodeVersion(undefined, '^1.116.0')).toBe('stable');
        expect(resolveVsCodeVersion('minimum', '^1.116.0')).toBe('1.116.0');
        expect(resolveVsCodeVersion(' 1.120.1 ', '^1.116.0')).toBe('1.120.1');
    });

    it('rejects unsupported version selectors before downloading VS Code', () => {
        expect(() => resolveVsCodeVersion('latest', '^1.116.0')).toThrow(/Unsupported VSCODE_E2E_VERSION/);
    });

    it('allows exactly one CI-only startup retry', () => {
        expect(startupAttempts('true')).toBe(2);
        expect(startupAttempts('false')).toBe(1);
        expect(startupAttempts(undefined)).toBe(1);
    });

    it('isolates VS Code state without disabling the development extension', () => {
        const args = buildVsCodeLaunchArgs({
            extensionPath: 'extension',
            extensionsDir: 'extensions',
            userDataDir: 'user-data',
            workspaceDir: 'workspace',
        });

        expect(args).toContain('--extensionDevelopmentPath=extension');
        expect(args).toContain('--extensions-dir=extensions');
        expect(args).toContain('--user-data-dir=user-data');
        expect(args).toContain('--disable-workspace-trust');
        expect(args).not.toContain('--disable-extensions');
        expect(args.at(-1)).toBe('workspace');
    });

    it('isolates PPDS configuration and removes inherited Dataverse credentials', () => {
        const environment = buildIsolatedProcessEnvironment({
            PATH: 'path',
            PPDS_CLIENT_SECRET: 'secret',
            PPDS_ENVIRONMENT_URL: 'https://example.crm.dynamics.com',
            PPDS_PROFILE: 'production',
        }, 'isolated-config');

        expect(environment).toEqual({
            PATH: 'path',
            PPDS_CONFIG_DIR: 'isolated-config',
        });
    });
});
