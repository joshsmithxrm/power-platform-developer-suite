import { build } from 'esbuild';
import { runTests } from '@vscode/test-electron';
import { mkdtemp, mkdir, writeFile, readFile, rm, cp, copyFile } from 'node:fs/promises';
import { createHash } from 'node:crypto';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const planPath = process.argv[2];
if (!planPath || !path.isAbsolute(planPath)) throw new Error('An absolute reviewed plan JSON path is required.');
const plan = JSON.parse(await readFile(planPath, 'utf8'));
const normalize = value => {
    const url = new URL(value);
    if (url.protocol !== 'https:' || url.username || url.password || url.search || url.hash || url.pathname !== '/')
        throw new Error('Expected an HTTPS organization origin.');
    return url.origin.toLowerCase();
};
if (plan.confirmation !== 'read-only-two-environment-notebook-demo' || plan.targets?.length !== 2 ||
    new Set(plan.targets.map(t => normalize(t.url))).size !== 2 || plan.targets.some(t => !t.profileName))
    throw new Error('The plan must explicitly approve two distinct targets and their profiles.');
for (const key of ['candidateCliDirectory', 'hostDirectory', 'artifactDirectory', 'credentialConfigDirectory'])
    if (!path.isAbsolute(plan[key] || '')) throw new Error(`An absolute ${key} is required.`);
const sha = async file => createHash('sha256').update(await readFile(file)).digest('hex');
const metadataHashes = async () => Object.fromEntries(await Promise.all(['profiles.json', 'environments.json'].map(async file => {
    try { return [file, await sha(path.join(plan.credentialConfigDirectory, file))]; }
    catch (error) { if (error.code === 'ENOENT') return [file, null]; throw error; }
})));
if (await sha(path.join(plan.candidateCliDirectory, 'ppds.dll')) !== plan.candidateSha256.toLowerCase())
    throw new Error('Candidate CLI hash mismatch.');
const manifest = JSON.parse(await readFile(path.join(root, 'package.json'), 'utf8'));
const requested = process.env.VSCODE_E2E_VERSION || 'stable';
const version = requested === 'minimum' ? /\d+\.\d+\.\d+/.exec(manifest.engines.vscode)[0] : requested;
const sourceHashes = Object.fromEntries(await Promise.all([
    'src/daemonClient.ts', 'src/notebooks/DataverseNotebookController.ts',
    'src/notebooks/DataverseNotebookSerializer.ts', 'src/notebooks/notebookResultRenderer.ts',
].map(async file => [file, await sha(path.join(root, file))])));
if (process.argv[3] !== '--execute-approved') {
    console.log(JSON.stringify({ status: 'validated-not-executed', requested, version, sourceHashes,
        candidateSha256: plan.candidateSha256, targets: plan.targets }, null, 2));
    process.exit(0);
}
// Each invocation requires a new artifact directory; previous evidence is immutable.
const originalMetadataBefore = await metadataHashes();
await mkdir(plan.artifactDirectory, { recursive: false });
const temporaryRoot = await mkdtemp(path.join(tmpdir(), 'ppds-notebook-live-'));
const fixture = path.join(temporaryRoot, 'extension');
const workspace = path.join(temporaryRoot, 'workspace');
const userData = path.join(temporaryRoot, 'user-data');
const evidence = { status: 'started', requested, version, sourceHashes, candidateSha256: plan.candidateSha256,
    targets: plan.targets, bootstrap: 'test-only isolated metadata DI; unchanged production RPC handlers and query pipeline',
    originalMetadataBefore,
    cancellationBoundary: 'real RPC response delivery is deferred at the client, not a deliberately slow server query' };
try {
    await Promise.all([fixture, workspace, path.join(userData, 'User')].map(dir => mkdir(dir, { recursive: true })));
    const bin = path.join(fixture, 'bin');
    await cp(plan.candidateCliDirectory, bin, { recursive: true });
    for (const suffix of ['.dll', '.deps.json', '.runtimeconfig.json'])
        await copyFile(path.join(plan.hostDirectory, `PPDS.NotebookLiveHost${suffix}`), path.join(bin, `PPDS.NotebookLiveHost${suffix}`));
    const executable = process.platform === 'win32' ? '.exe' : '';
    await copyFile(path.join(plan.hostDirectory, `PPDS.NotebookLiveHost${executable}`), path.join(bin, `ppds${executable}`));
    evidence.hostSha256 = await sha(path.join(bin, 'PPDS.NotebookLiveHost.dll'));
    await writeFile(path.join(fixture, 'package.json'), JSON.stringify({
        name: 'notebook-live-fixture', publisher: 'ppds-test', version: '0.0.1', engines: manifest.engines,
        main: './activate.js', activationEvents: ['onStartupFinished'],
        contributes: { notebooks: manifest.contributes.notebooks, languages: manifest.contributes.languages },
    }));
    await writeFile(path.join(fixture, 'activate.js'), 'exports.activate = () => {};');
    await writeFile(path.join(userData, 'User', 'settings.json'), JSON.stringify({
        'telemetry.telemetryLevel': 'off', 'chat.disableAIFeatures': true, 'chat.agent.enabled': false,
        'chat.plugins.enabled': false, 'chat.plugins.marketplaces': [], 'chat.plugins.extraMarketplaces': {},
        'chat.customizations.migration.enabled': false, 'chat.customizations.userDataMigration.enabled': false,
        'chat.customizations.marketplace.enabled': false, 'chat.customizations.marketplace.sources.publicFeed.enabled': false,
        'chat.useClaudeMdFile': false, 'chat.useClaudeSkills': false, 'chat.useClaudeHooks': false,
        'workbench.startupEditor': 'none', 'security.workspace.trust.enabled': false, 'files.hotExit': 'off',
    }));
    const testsPath = path.join(fixture, 'tests.cjs');
    await build({ entryPoints: [path.join(root, 'e2e/notebook-live.ts')], outfile: testsPath,
        bundle: true, platform: 'node', format: 'cjs', external: ['vscode'], sourcemap: true });
    const environment = {};
    for (const key of Object.keys(process.env))
        if (/^(PPDS_|DATAVERSE_|POWERPLATFORM_|AZURE_)/.test(key)) environment[key] = undefined;
    Object.assign(environment, {
        PPDS_CONFIG_DIR: plan.credentialConfigDirectory,
        PPDS_NOTEBOOK_LIVE_PLAN: planPath,
        PPDS_NOTEBOOK_LIVE_METADATA: path.join(temporaryRoot, 'metadata'),
        PPDS_NOTEBOOK_LIVE_FIXTURE: fixture,
        // Suppress browser auth. Missing cached auth fails; never perform a new login.
        CI: 'true', USERPROFILE: path.join(temporaryRoot, 'home'), APPDATA: path.join(temporaryRoot, 'app-data'),
        LOCALAPPDATA: path.join(temporaryRoot, 'local-app-data'), XDG_CONFIG_HOME: path.join(temporaryRoot, 'xdg-config'),
        XDG_CACHE_HOME: path.join(temporaryRoot, 'xdg-cache'), XDG_DATA_HOME: path.join(temporaryRoot, 'xdg-data'),
    });
    await Promise.all(['USERPROFILE', 'APPDATA', 'LOCALAPPDATA', 'XDG_CONFIG_HOME', 'XDG_CACHE_HOME', 'XDG_DATA_HOME']
        .map(key => mkdir(environment[key], { recursive: true })));
    await runTests({ version, extensionDevelopmentPath: fixture, extensionTestsPath: testsPath,
        extensionTestsEnv: environment, launchArgs: [workspace, `--user-data-dir=${userData}`,
            `--extensions-dir=${path.join(temporaryRoot, 'extensions')}`, '--disable-extensions', '--disable-gpu',
            '--disable-telemetry', '--disable-updates', '--disable-extension=GitHub.copilot',
            '--disable-extension=GitHub.copilot-chat', '--disable-workspace-trust', '--skip-welcome', '--no-sandbox'] });
    evidence.status = 'passed';
} catch (error) {
    evidence.status = 'failed';
    evidence.error = error instanceof Error ? error.message : String(error);
    throw error;
} finally {
    let metadataProblem = false;
    try {
        evidence.originalMetadataAfter = await metadataHashes();
        metadataProblem = JSON.stringify(evidence.originalMetadataBefore) !== JSON.stringify(evidence.originalMetadataAfter);
        if (metadataProblem) evidence.metadataWarning = 'Original metadata changed; investigate without restoring over concurrent edits.';
    } catch {
        metadataProblem = true;
        evidence.metadataWarning = 'Original metadata could not be re-read; investigate without restoring it.';
    }
    if (metadataProblem) evidence.status = 'failed';
    // Only our mkdtemp directory is removed. Never delete a configured source/artifact directory.
    try { await rm(temporaryRoot, { recursive: true, force: true, maxRetries: 5, retryDelay: 200 }); evidence.temporaryCleanup = 'completed'; }
    catch (error) { evidence.status = 'failed'; evidence.temporaryCleanup = error.message; }
    // Raw auth/VS Code logs can contain unrelated metadata, so copy only purpose-built evidence.
    await writeFile(path.join(plan.artifactDirectory, 'host-run.json'), JSON.stringify(evidence, null, 2));
    if (metadataProblem || evidence.temporaryCleanup !== 'completed') throw new Error('Live notebook metadata/temporary cleanup verification failed.');
}
