import { build } from 'esbuild';
import { runTests } from '@vscode/test-electron';
import { mkdtemp, mkdir, writeFile, readFile, rm, cp } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const manifest = JSON.parse(await readFile(path.join(root, 'package.json'), 'utf8'));
const requested = process.env.VSCODE_E2E_VERSION || 'stable';
const version = requested === 'minimum' ? /\d+\.\d+\.\d+/.exec(manifest.engines.vscode)[0] : requested;
const temporaryRoot = await mkdtemp(path.join(tmpdir(), 'ppds-notebook-host-'));
const fixture = path.join(temporaryRoot, 'extension');
const workspace = path.join(temporaryRoot, 'workspace');
const userData = path.join(temporaryRoot, 'user-data');
const artifacts = path.resolve(root, process.env.PPDS_NOTEBOOK_HOST_ARTIFACTS || 'test-results/notebook-host');
try {
    await Promise.all([fixture, workspace, path.join(userData, 'User')].map(dir => mkdir(dir, { recursive: true })));
    // A test-only extension loads the production controller/serializer with an in-memory
    // daemon. The production extension is never activated and no auth store is consulted.
    await writeFile(path.join(fixture, 'package.json'), JSON.stringify({
        name: 'notebook-host-fixture', publisher: 'ppds-test', version: '0.0.1',
        engines: manifest.engines, main: './activate.js', activationEvents: ['onStartupFinished'],
        contributes: { notebooks: manifest.contributes.notebooks, languages: manifest.contributes.languages },
    }));
    await writeFile(path.join(fixture, 'activate.js'), 'exports.activate = () => {};');
    await writeFile(path.join(userData, 'User', 'settings.json'), JSON.stringify({
        'ppds.autoStartDaemon': false, 'telemetry.telemetryLevel': 'off',
        'chat.disableAIFeatures': true, 'chat.agent.enabled': false, 'chat.plugins.enabled': false,
        'chat.plugins.marketplaces': [], 'chat.plugins.extraMarketplaces': {},
        'chat.customizations.migration.enabled': false, 'chat.customizations.userDataMigration.enabled': false,
        'chat.customizations.marketplace.enabled': false, 'chat.customizations.marketplace.sources.publicFeed.enabled': false,
        'chat.useClaudeMdFile': false, 'chat.useClaudeSkills': false, 'chat.useClaudeHooks': false,
        'workbench.startupEditor': 'none', 'security.workspace.trust.enabled': false,
        'notebook.output.textLineLimit': 100, 'files.hotExit': 'off',
    }));
    const testsPath = path.join(fixture, 'tests.cjs');
    await build({
        entryPoints: [path.join(root, 'e2e/notebook-host.ts')], outfile: testsPath,
        bundle: true, platform: 'node', format: 'cjs', external: ['vscode'], sourcemap: true,
    });
    // runTests merges with process.env, so removed connection variables must be undefined.
    const environment = {
        PPDS_CONFIG_DIR: path.join(temporaryRoot, 'ppds-config'),
        // Built-in VS Code plugin discovery can read outside user-data-dir even
        // with AI disabled. Give this child host an empty home and app-data roots.
        USERPROFILE: path.join(temporaryRoot, 'home'),
        APPDATA: path.join(temporaryRoot, 'app-data'),
        LOCALAPPDATA: path.join(temporaryRoot, 'local-app-data'),
        // Linux uses isolated application directories plus --user-data-dir. HOME
        // remains unchanged; the CI runner has no user-installed plugins.
        XDG_CONFIG_HOME: path.join(temporaryRoot, 'xdg-config'),
        XDG_CACHE_HOME: path.join(temporaryRoot, 'xdg-cache'),
        XDG_DATA_HOME: path.join(temporaryRoot, 'xdg-data'),
    };
    await Promise.all([environment.USERPROFILE, environment.APPDATA, environment.LOCALAPPDATA,
        environment.XDG_CONFIG_HOME, environment.XDG_CACHE_HOME, environment.XDG_DATA_HOME]
        .map(dir => mkdir(dir, { recursive: true })));
    for (const key of Object.keys(process.env)) {
        if (/^(PPDS_|DATAVERSE_)/.test(key)) environment[key] = undefined;
    }
    environment.PPDS_CONFIG_DIR = path.join(temporaryRoot, 'ppds-config');
    console.log(`Notebook host tests requested=${requested}, resolved=${version}`);
    await runTests({
        version, extensionDevelopmentPath: fixture, extensionTestsPath: testsPath,
        extensionTestsEnv: environment,
        launchArgs: [workspace, `--user-data-dir=${userData}`, `--extensions-dir=${path.join(temporaryRoot, 'extensions')}`,
            '--disable-extensions', '--disable-gpu', '--disable-telemetry', '--disable-updates',
            '--disable-extension=GitHub.copilot', '--disable-extension=GitHub.copilot-chat',
            '--disable-workspace-trust', '--skip-release-notes', '--skip-welcome', '--no-sandbox'],
    });
} catch (error) {
    try {
        await mkdir(artifacts, { recursive: true });
        await writeFile(path.join(artifacts, 'host-run.json'), JSON.stringify({
            requested, version, platform: process.platform, error: error instanceof Error ? error.message : String(error),
        }, null, 2));
        await cp(path.join(userData, 'logs'), path.join(artifacts, 'vscode-logs'), { recursive: true });
    } catch (diagnosticError) {
        console.error('Could not copy all notebook host diagnostics:', diagnosticError.message);
    }
    throw error;
} finally {
    // Only the exact directory returned by mkdtemp is removed; never a user's workspace.
    await rm(temporaryRoot, { recursive: true, force: true, maxRetries: 5, retryDelay: 200 });
}
