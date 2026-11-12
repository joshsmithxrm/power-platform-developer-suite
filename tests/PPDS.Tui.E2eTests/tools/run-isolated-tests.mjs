import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const TUI_ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const SAFE_ENVIRONMENT = new Set([
    'PATH', 'PATHEXT', 'SYSTEMROOT', 'WINDIR', 'COMSPEC',
    'TEMP', 'TMP', 'TMPDIR', 'HOME', 'USERPROFILE', 'APPDATA', 'LOCALAPPDATA',
    'PROCESSOR_ARCHITECTURE', 'NUMBER_OF_PROCESSORS', 'TERM', 'COLORTERM',
    'LANG', 'LC_ALL', 'CI', 'PLAYWRIGHT_BROWSERS_PATH',
    'DOTNET_ROOT', 'DOTNET_ROOT_X64', 'DOTNET_ROOT_X86',
]);

export function isolatedEnvironment(parent, profileDirectory) {
    const environment = {};
    for (const [key, value] of Object.entries(parent)) {
        if (value !== undefined && SAFE_ENVIRONMENT.has(key.toUpperCase())) {
            environment[key] = value;
        }
    }
    environment.PPDS_CONFIG_DIR = profileDirectory;
    environment.PPDS_FORCE_TUI = '1';
    environment.DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false';
    environment.DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1';
    return environment;
}

export function runIsolatedTests(args, {
    environment = process.env,
    temporaryRoot = tmpdir(),
    spawn = spawnSync,
} = {}) {
    const root = resolve(temporaryRoot);
    const profileDirectory = mkdtempSync(join(root, 'ppds-tui-fixture-'));
    try {
        const result = spawn(process.execPath,
            [join(TUI_ROOT, 'node_modules', '@microsoft', 'tui-test', 'index.js'), ...args],
            { cwd: TUI_ROOT, env: isolatedEnvironment(environment, profileDirectory), stdio: 'inherit' });
        if (result.error) throw result.error;
        if (result.signal) throw new Error('TUI runner terminated by ' + result.signal);
        return result.status ?? 1;
    } finally {
        if (dirname(profileDirectory) !== root) {
            throw new Error('Refusing cleanup outside the owned fixture directory');
        }
        rmSync(profileDirectory, { recursive: true, force: true });
    }
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
    try {
        process.exitCode = runIsolatedTests(process.argv.slice(2));
    } catch (error) {
        console.error('TUI test runner failed:', error.message);
        process.exitCode = 1;
    }
}
