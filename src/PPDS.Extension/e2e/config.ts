import * as fs from 'fs';

export const DEFAULT_VSCODE_E2E_VERSION = 'stable';

interface ExtensionManifest {
    engines?: {
        vscode?: string;
    };
}

export interface VsCodeLaunchPaths {
    extensionPath: string;
    extensionsDir: string;
    userDataDir: string;
    workspaceDir: string;
}

const DATAVERSE_CONNECTION_ENVIRONMENT_VARIABLES = [
    'PPDS_CLIENT_ID',
    'PPDS_CLIENT_SECRET',
    'PPDS_CLOUD',
    'PPDS_ENVIRONMENT_URL',
    'PPDS_PROFILE',
    'PPDS_SPN_SECRET',
    'PPDS_TENANT_ID',
    'PPDS_TEST_CLIENT_SECRET',
];

export function minimumVersionFromEngineRange(engineRange: string): string {
    const match = /(?:\^|~|>=)?\s*(\d+\.\d+\.\d+)/.exec(engineRange);
    if (!match) {
        throw new Error(`Unable to determine the minimum VS Code version from engines.vscode: ${engineRange}`);
    }

    return match[1];
}

export function readDeclaredVsCodeEngine(manifestPath: string): string {
    const manifest = JSON.parse(fs.readFileSync(manifestPath, 'utf8')) as ExtensionManifest;
    const engineRange = manifest.engines?.vscode;
    if (!engineRange) {
        throw new Error(`Extension manifest has no engines.vscode value: ${manifestPath}`);
    }

    return engineRange;
}

export function resolveVsCodeVersion(requestedVersion: string | undefined, engineRange: string): string {
    const version = requestedVersion?.trim() || DEFAULT_VSCODE_E2E_VERSION;
    if (version === 'minimum') {
        return minimumVersionFromEngineRange(engineRange);
    }

    if (version === 'stable' || version === 'insiders' || /^\d+\.\d+\.\d+$/.test(version)) {
        return version;
    }

    throw new Error(
        `Unsupported VSCODE_E2E_VERSION "${version}". Use minimum, stable, insiders, or an exact version.`,
    );
}

export function startupAttempts(ciValue: string | undefined): number {
    return ciValue === 'true' ? 2 : 1;
}

export function buildIsolatedProcessEnvironment(
    baseEnvironment: NodeJS.ProcessEnv,
    ppdsConfigDir: string,
): Record<string, string> {
    const environment = Object.fromEntries(
        Object.entries(baseEnvironment).filter((entry): entry is [string, string] => entry[1] !== undefined),
    );
    environment.PPDS_CONFIG_DIR = ppdsConfigDir;
    for (const variable of DATAVERSE_CONNECTION_ENVIRONMENT_VARIABLES) {
        delete environment[variable];
    }

    return environment;
}

export function buildVsCodeLaunchArgs(paths: VsCodeLaunchPaths): string[] {
    return [
        `--extensionDevelopmentPath=${paths.extensionPath}`,
        `--user-data-dir=${paths.userDataDir}`,
        `--extensions-dir=${paths.extensionsDir}`,
        '--disable-crash-reporter',
        '--disable-gpu',
        '--disable-telemetry',
        '--disable-updates',
        '--disable-workspace-trust',
        '--no-sandbox',
        '--skip-release-notes',
        '--skip-welcome',
        paths.workspaceDir,
    ];
}
