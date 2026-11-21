import { existsSync, mkdtempSync, readFileSync, readdirSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { basename, dirname, join, relative, resolve, sep } from 'node:path';
import { spawn as spawnChild } from 'node:child_process';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { LAUNCH_ATTEMPTS, LAUNCH_FAILURE_LABEL, summarizeLaunchLog } from './launch-retry.mjs';

const TUI_ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const TUI_TEST_ENTRY = join(TUI_ROOT, 'node_modules', '@microsoft', 'tui-test', 'index.js');

/** Preloaded into the tui-test runner and its workers: bounded retry of a failed launch. */
export const LAUNCH_RETRY_PRELOAD = pathToFileURL(join(TUI_ROOT, 'tools', 'launch-retry-preload.mjs')).href;

const SAFE_ENVIRONMENT = new Set([
    'PATH', 'PATHEXT', 'SYSTEMROOT', 'WINDIR', 'COMSPEC',
    'TEMP', 'TMP', 'TMPDIR', 'HOME', 'USERPROFILE', 'APPDATA', 'LOCALAPPDATA',
    'PROCESSOR_ARCHITECTURE', 'NUMBER_OF_PROCESSORS', 'TERM', 'COLORTERM',
    'LANG', 'LC_ALL', 'CI', 'PLAYWRIGHT_BROWSERS_PATH',
    'DOTNET_ROOT', 'DOTNET_ROOT_X64', 'DOTNET_ROOT_X86',
]);

/**
 * The environment of the tui-test runner. Only the safe runtime settings of
 * the parent pass. NODE_OPTIONS is never inherited: it is set here, to the
 * preloads only. tui-test's worker processes inherit this environment (not
 * the runner's command-line flags), so NODE_OPTIONS is how a preload reaches
 * the process that launches the TUI.
 */
export function isolatedEnvironment(parent, profileDirectory, {
    preloads = [],
    launchLog,
} = {}) {
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
    if (preloads.length > 0) {
        environment.NODE_OPTIONS = preloads.map((url) => `--import=${url}`).join(' ');
    }
    if (launchLog) {
        environment.PPDS_TUI_LAUNCH_LOG = launchLog;
    }
    return environment;
}

const ANSI = /\x1b\[[0-9;]*[A-Za-z]/g;

/**
 * Reads tui-test's closing summary ("tests: ..., N total" and, when there are
 * snapshots, "snapshots: ..., N obsolete, ..."). `found` is false when the
 * tests line is missing, so a caller can fail closed.
 */
export function parseTuiTestSummary(output) {
    const text = String(output ?? '').replace(ANSI, '');
    const tests = text.match(/^\s*tests: (.*), (\d+) total\s*$/m);
    const snapshots = text.match(/^\s*snapshots: (.*?), (\d+) total/m);
    const count = (line, label) => {
        const match = line?.match(new RegExp(`(\\d+) ${label}\\b`));
        return match ? Number(match[1]) : 0;
    };
    return {
        found: tests !== null,
        testsFailed: count(tests?.[1], 'failed'),
        testsSkipped: count(tests?.[1], 'skipped'),
        testsDidNotRun: count(tests?.[1], 'did not run'),
        testsTotal: tests ? Number(tests[2]) : 0,
        snapshotsObsolete: count(snapshots?.[1], 'obsolete'),
    };
}

// tui-test's commands and flags that run no test: commander's help and
// version output, and the trace viewer. They print no test summary.
const NON_TEST_FLAGS = new Set(['-h', '--help', '-v', '--version']);
const NON_TEST_COMMANDS = new Set(['help', 'show-trace']);

/** True for a tui-test invocation that runs no test, so it has no summary to check. */
export function isNonTestInvocation(args) {
    const options = args.includes('--') ? args.slice(0, args.indexOf('--')) : args;
    return NON_TEST_COMMANDS.has(options[0]) || options.some((arg) => NON_TEST_FLAGS.has(arg));
}

/**
 * Snapshot files with no spec. tui-test stores the snapshots of
 * `<dir>/<name>.spec.ts` in `<dir>/__snapshots__/<name>.spec.ts.snap` and only
 * counts the entries of files whose spec ran, so a file left behind by a
 * removed or renamed spec is never reported. Returns paths relative to `root`'s
 * parent, with forward slashes, sorted.
 */
export function findOrphanSnapshotFiles(root) {
    if (!existsSync(root)) return [];
    const base = dirname(root);
    const orphans = [];
    for (const entry of readdirSync(root, { recursive: true, withFileTypes: true })) {
        const directory = entry.parentPath ?? entry.path;
        if (!entry.isFile() || !entry.name.endsWith('.snap') || basename(directory) !== '__snapshots__') {
            continue;
        }
        const spec = join(dirname(directory), entry.name.slice(0, -'.snap'.length));
        if (!existsSync(spec)) {
            orphans.push(relative(base, join(directory, entry.name)).split(sep).join('/'));
        }
    }
    return orphans.sort();
}

/**
 * Decides the exit code from tui-test's own status and its summary, and
 * returns the lines to print. A run tui-test passed still fails when it
 * reports an obsolete snapshot, when a snapshot file has no spec, when no test
 * ran, or when its summary cannot be read.
 */
export function evaluateRun(status, summary, launches = { retried: 0, failed: 0, errors: [] },
    orphanSnapshotFiles = []) {
    const lines = [];
    if (launches.retried > 0) {
        lines.push(`TUI launch: ${launches.retried} failed launch attempt(s) were retried `
            + `(at most ${LAUNCH_ATTEMPTS} attempts per test; errors: ${launches.errors.join(' | ')}).`);
    }
    if (launches.failed > 0) {
        lines.push(`${LAUNCH_FAILURE_LABEL}: ${launches.failed} test(s) failed because the `
            + `application under test did not start after ${LAUNCH_ATTEMPTS} attempts. `
            + 'These are launch failures, not assertion failures.');
    }
    if (orphanSnapshotFiles.length > 0) {
        lines.push(`Obsolete snapshot files: ${orphanSnapshotFiles.length} snapshot file(s) have no spec `
            + `file: ${orphanSnapshotFiles.join(', ')}. The spec was removed or renamed; delete the `
            + 'file with `git rm` and commit (`npm run tui:test:update` does not remove it).');
    }
    if (status !== 0) {
        if (summary.snapshotsObsolete > 0) {
            lines.push(`Note: ${summary.snapshotsObsolete} stored snapshot(s) were not used by this run. `
                + 'A test that fails before its snapshot assertion leaves its snapshot unused, so this '
                + 'count includes those. Do not remove snapshots on the evidence of a failed run.');
        }
        return { code: status, lines };
    }
    if (!summary.found) {
        lines.push('TUI test runner: tui-test exited 0 but its summary ("tests: ... total") was not '
            + 'found, so obsolete snapshots could not be checked. Failing closed.');
        return { code: 1, lines };
    }
    if (summary.testsTotal - summary.testsDidNotRun <= 0) {
        lines.push(`No test ran (${summary.testsDidNotRun} did not run): the filter matched no test file. `
            + 'Failing.');
        return { code: 1, lines };
    }
    if (summary.snapshotsObsolete > 0) {
        const notRun = summary.testsSkipped + summary.testsDidNotRun;
        if (notRun > 0) {
            lines.push(`Unused snapshots: ${summary.snapshotsObsolete} stored snapshot(s) were not used by `
                + `this run, and ${summary.testsSkipped} test(s) were skipped and `
                + `${summary.testsDidNotRun} did not run (test.skip, test.only or a filter). The unused `
                + 'snapshots may belong to those tests, so this run cannot tell which are obsolete. Run '
                + 'every test, with no skip, only or filter, to decide. Do not run '
                + '`npm run tui:test:update` for this: it would delete the snapshots of tests that did not run.');
        } else {
            lines.push(`Obsolete snapshots: ${summary.snapshotsObsolete} stored snapshot(s) under `
                + 'tests/__snapshots__ were not used by any test, and every test ran and passed. Remove '
                + 'them with `npm run tui:test:update` and commit the result.');
        }
        return { code: 1, lines };
    }
    if (orphanSnapshotFiles.length > 0) {
        return { code: 1, lines };
    }
    return { code: 0, lines };
}

function runChild(spawn, command, args, options, stdout) {
    return new Promise((resolvePromise, reject) => {
        let output = '';
        let child;
        try {
            child = spawn(command, args, options);
        } catch (error) {
            reject(error);
            return;
        }
        child.stdout?.setEncoding?.('utf8');
        child.stdout?.on('data', (chunk) => {
            const text = String(chunk);
            output += text;
            stdout.write(text);
        });
        child.on('error', reject);
        child.on('close', (status, signal) => {
            if (signal) {
                reject(new Error('TUI runner terminated by ' + signal));
                return;
            }
            resolvePromise({ status: status ?? 1, output });
        });
    });
}

export async function runIsolatedTests(args, {
    environment = process.env,
    temporaryRoot = tmpdir(),
    spawn = spawnChild,
    preloads = [LAUNCH_RETRY_PRELOAD],
    stdout = process.stdout,
    stderr = process.stderr,
    testsRoot = join(TUI_ROOT, 'tests'),
} = {}) {
    const root = resolve(temporaryRoot);
    const profileDirectory = mkdtempSync(join(root, 'ppds-tui-fixture-'));
    let launchLogDirectory;
    try {
        launchLogDirectory = mkdtempSync(join(root, 'ppds-tui-launch-log-'));
        const launchLog = join(launchLogDirectory, 'launches.jsonl');
        const env = isolatedEnvironment(environment, profileDirectory, { preloads, launchLog });
        if (stdout.isTTY && env.FORCE_COLOR === undefined) env.FORCE_COLOR = '1';
        const { status, output } = await runChild(spawn, process.execPath, [TUI_TEST_ENTRY, ...args],
            { cwd: TUI_ROOT, env, stdio: ['inherit', 'pipe', 'inherit'] }, stdout);
        // Help, version and the trace viewer run no test: tui-test's status stands.
        if (isNonTestInvocation(args)) return status;
        const launches = summarizeLaunchLog(existsSync(launchLog) ? readFileSync(launchLog, 'utf8') : '');
        const { code, lines } = evaluateRun(status, parseTuiTestSummary(output), launches,
            findOrphanSnapshotFiles(testsRoot));
        for (const line of lines) stderr.write(line + '\n');
        return code;
    } finally {
        for (const owned of [profileDirectory, launchLogDirectory]) {
            if (owned === undefined) continue;
            if (dirname(owned) !== root) {
                throw new Error('Refusing cleanup outside the owned fixture directory');
            }
            rmSync(owned, { recursive: true, force: true });
        }
    }
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
    try {
        process.exitCode = await runIsolatedTests(process.argv.slice(2));
    } catch (error) {
        console.error('TUI test runner failed:', error.message);
        process.exitCode = 1;
    }
}
