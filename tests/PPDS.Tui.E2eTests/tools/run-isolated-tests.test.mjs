import assert from 'node:assert/strict';
import { EventEmitter } from 'node:events';
import { appendFileSync, existsSync, mkdirSync, mkdtempSync, readdirSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { PassThrough } from 'node:stream';
import test from 'node:test';
import {
    LAUNCH_RETRY_PRELOAD, evaluateRun, findOrphanSnapshotFiles, isNonTestInvocation, isolatedEnvironment,
    parseTuiTestSummary, runIsolatedTests,
} from './run-isolated-tests.mjs';

const PASSING_OUTPUT = '\n  tests: 36 passed, 36 total\n  snapshots: 14 passed, 14 total \n\n';
// The summary of the run in issue #1769, with tui-test's colours.
const ISSUE_OUTPUT = '\n  tests: \x1b[31m1 failed\x1b[39m, \x1b[32m35 passed\x1b[39m, 36 total\n'
    + '  snapshots: \x1b[32m13 passed\x1b[39m, \x1b[33m1 obsolete\x1b[39m, 14 total '
    + '\x1b[2m(Inspect your code changes or use the `-u` flag to update them.)\x1b[22m\n\n';
// tui-test's summary with a skipped snapshot test (as in review: `test.skip`, filter `import-jobs`).
const SKIPPED_OUTPUT = '\n  tests: 32 did not run, 1 skipped, 3 passed, 36 total\n'
    + '  snapshots: 1 passed, 1 obsolete, 2 total \n\n';
const NOTHING_RAN_OUTPUT = '\n  tests: 36 did not run, 36 total\n\n';
const OBSOLETE_OUTPUT = '\n  tests: 36 passed, 36 total\n  snapshots: 14 passed, 1 obsolete, 15 total \n\n';

/** A child-process stand-in: writes `output` to its stdout, then closes with `status`. */
function fakeChild({ status = 0, output = PASSING_OUTPUT, signal = null, onSpawn } = {}) {
    const calls = [];
    const spawn = (executable, args, options) => {
        calls.push({ executable, args, options });
        onSpawn?.(options);
        const child = new EventEmitter();
        child.stdout = new PassThrough();
        setImmediate(() => {
            child.stdout.end(output);
            child.stdout.on('end', () => child.emit('close', signal ? null : status, signal));
            child.stdout.resume();
        });
        return child;
    };
    return { spawn, calls };
}

function sink() {
    const chunks = [];
    return { write: (text) => { chunks.push(String(text)); return true; }, text: () => chunks.join('') };
}

test('only safe runtime settings reach the isolated runner; NODE_OPTIONS is set, not inherited', () => {
    const parent = {
        Path: 'safe-path', SystemRoot: 'windows', PLAYWRIGHT_BROWSERS_PATH: 'browsers',
        PPDS_CONFIG_DIR: 'real-profile', PPDS_PROFILE: 'real', PPDS_LIVE_TESTS: '1',
        AZURE_CLIENT_SECRET: 'secret', GH_TOKEN: 'token', NODE_OPTIONS: '--require injected.js',
        DOTNET_GENERATE_ASPNET_CERTIFICATE: 'true', PPDS_TUI_LAUNCH_LOG: 'inherited-log',
    };
    assert.deepEqual(isolatedEnvironment(parent, 'owned-fixture'), {
        Path: 'safe-path', SystemRoot: 'windows', PLAYWRIGHT_BROWSERS_PATH: 'browsers',
        PPDS_CONFIG_DIR: 'owned-fixture', PPDS_FORCE_TUI: '1',
        DOTNET_GENERATE_ASPNET_CERTIFICATE: 'false', DOTNET_SKIP_FIRST_TIME_EXPERIENCE: '1',
    });
    const withPreloads = isolatedEnvironment(parent, 'owned-fixture', {
        preloads: ['file:///a/one.mjs', 'file:///b/two.mjs'], launchLog: 'owned-log',
    });
    assert.equal(withPreloads.NODE_OPTIONS, '--import=file:///a/one.mjs --import=file:///b/two.mjs');
    assert.equal(withPreloads.PPDS_TUI_LAUNCH_LOG, 'owned-log');
    assert.equal(withPreloads.AZURE_CLIENT_SECRET, undefined);
});

test('the runner preloads the launch retry into tui-test and its workers by default', async () => {
    const { spawn, calls } = fakeChild();
    assert.equal(await runIsolatedTests([], { spawn, stdout: sink(), stderr: sink() }), 0);
    assert.equal(calls[0].options.env.NODE_OPTIONS, `--import=${LAUNCH_RETRY_PRELOAD}`);
    assert.match(LAUNCH_RETRY_PRELOAD, /^file:\/\/.*\/tools\/launch-retry-preload\.mjs$/);
    assert.deepEqual(calls[0].options.stdio, ['inherit', 'pipe', 'inherit']);
});

test('runner owns and removes its empty fixtures on success and failure', async () => {
    const root = mkdtempSync(join(tmpdir(), 'ppds-tui-launcher-test-'));
    const fixtures = [];
    const sentinel = join(root, 'unrelated.txt');
    writeFileSync(sentinel, 'retain');
    try {
        for (const status of [0, 14]) {
            const { spawn, calls } = fakeChild({
                status,
                onSpawn(options) {
                    const fixture = options.env.PPDS_CONFIG_DIR;
                    fixtures.push(fixture);
                    assert.deepEqual(readdirSync(fixture), []);
                    writeFileSync(join(fixture, 'tui-state.json'), '{}');
                    appendFileSync(options.env.PPDS_TUI_LAUNCH_LOG, '');
                },
            });
            assert.equal(await runIsolatedTests(['--some-option'], {
                temporaryRoot: root,
                environment: { PPDS_CONFIG_DIR: 'unrelated-profile' },
                spawn, stdout: sink(), stderr: sink(),
            }), status);
            assert.equal(calls[0].executable, process.execPath);
            assert.equal(calls[0].args.at(-1), '--some-option');
            assert.equal(existsSync(fixtures.at(-1)), false);
        }
        assert.notEqual(fixtures[0], fixtures[1]);
        assert.deepEqual(readdirSync(root), ['unrelated.txt']);
    } finally {
        rmSync(root, { recursive: true, force: true });
    }
});

test('a runner that cannot be started removes only the owned fixtures', async () => {
    const root = mkdtempSync(join(tmpdir(), 'ppds-tui-launcher-test-'));
    try {
        await assert.rejects(runIsolatedTests([], {
            temporaryRoot: root, environment: {}, stdout: sink(), stderr: sink(),
            spawn() { throw new Error('synthetic runner start failure'); },
        }), /synthetic runner start failure/);
        assert.deepEqual(readdirSync(root), []);
    } finally {
        rmSync(root, { recursive: true, force: true });
    }
});

test('a runner terminated by a signal is an error', async () => {
    const { spawn } = fakeChild({ signal: 'SIGTERM' });
    await assert.rejects(runIsolatedTests([], { spawn, stdout: sink(), stderr: sink() }),
        /TUI runner terminated by SIGTERM/);
});

test('tui-test output is passed through as it arrives', async () => {
    const out = sink();
    const { spawn } = fakeChild({ output: 'progress line\n' + PASSING_OUTPUT });
    await runIsolatedTests([], { spawn, stdout: out, stderr: sink() });
    assert.equal(out.text(), 'progress line\n' + PASSING_OUTPUT);
});

test('the summary is read with or without colour', () => {
    const none = { testsFailed: 0, testsSkipped: 0, testsDidNotRun: 0 };
    assert.deepEqual(parseTuiTestSummary(PASSING_OUTPUT),
        { ...none, found: true, testsTotal: 36, snapshotsObsolete: 0 });
    assert.deepEqual(parseTuiTestSummary(ISSUE_OUTPUT),
        { ...none, found: true, testsFailed: 1, testsTotal: 36, snapshotsObsolete: 1 });
    assert.deepEqual(parseTuiTestSummary('no summary\n'),
        { ...none, found: false, testsTotal: 0, snapshotsObsolete: 0 });
    assert.deepEqual(parseTuiTestSummary(SKIPPED_OUTPUT),
        { ...none, found: true, testsSkipped: 1, testsDidNotRun: 32, testsTotal: 36, snapshotsObsolete: 1 });
    // A suite without snapshots prints no snapshots line.
    assert.equal(parseTuiTestSummary('  tests: 3 passed, 3 total\n').snapshotsObsolete, 0);
});

test('an obsolete snapshot fails a run that tui-test passed', async () => {
    const err = sink();
    const { spawn } = fakeChild({ status: 0, output: OBSOLETE_OUTPUT });
    assert.equal(await runIsolatedTests([], { spawn, stdout: sink(), stderr: err }), 1);
    assert.match(err.text(), /^Obsolete snapshots: 1 stored snapshot\(s\) under tests\/__snapshots__ were not used by any test, and every test ran and passed\./m);
    assert.match(err.text(), /npm run tui:test:update/);
});

test('a failed run keeps its status and says unused snapshots include those of failed tests', () => {
    const { code, lines } = evaluateRun(1, parseTuiTestSummary(ISSUE_OUTPUT));
    assert.equal(code, 1);
    assert.equal(lines.length, 1);
    assert.match(lines[0], /^Note: 1 stored snapshot\(s\) were not used by this run\. A test that fails before its snapshot assertion/);
});

test('a passing run without a readable summary fails closed', () => {
    const { code, lines } = evaluateRun(0, parseTuiTestSummary(''));
    assert.equal(code, 1);
    assert.match(lines[0], /summary .* was not found, so obsolete snapshots could not be checked\. Failing closed\./);
});

test('a clean run passes and prints nothing extra', () => {
    assert.deepEqual(evaluateRun(0, parseTuiTestSummary(PASSING_OUTPUT)), { code: 0, lines: [] });
});

test('launch retries and launch failures are summarised from the launch log', async () => {
    const err = sink();
    const log = [
        { event: 'retry', attempt: 1, attempts: 3, error: 'Cannot create process, error code: 87' },
        { event: 'retry', attempt: 2, attempts: 3, error: 'Cannot create process, error code: 87' },
        { event: 'failed', attempt: 3, attempts: 3, error: 'Cannot create process, error code: 87' },
    ].map((entry) => JSON.stringify(entry)).join('\n') + '\n';
    const { spawn } = fakeChild({
        status: 1,
        output: ISSUE_OUTPUT,
        onSpawn(options) { appendFileSync(options.env.PPDS_TUI_LAUNCH_LOG, log); },
    });
    assert.equal(await runIsolatedTests([], { spawn, stdout: sink(), stderr: err }), 1);
    const lines = err.text().trimEnd().split('\n');
    assert.equal(lines[0], 'TUI launch: 2 failed launch attempt(s) were retried (at most 3 attempts per test; '
        + 'errors: Cannot create process, error code: 87).');
    assert.equal(lines[1], 'TUI launch failure: 1 test(s) failed because the application under test did not '
        + 'start after 3 attempts. These are launch failures, not assertion failures.');
    assert.match(lines[2], /^Note: 1 stored snapshot/);
});

test('a retried launch that then started does not change a passing result', () => {
    const { code, lines } = evaluateRun(0, parseTuiTestSummary(PASSING_OUTPUT),
        { retried: 1, failed: 0, errors: ['x'] });
    assert.equal(code, 0);
    assert.equal(lines.length, 1);
    assert.match(lines[0], /^TUI launch: 1 failed launch attempt\(s\) were retried/);
});

test('a skipped or unrun test makes unused snapshots undecidable, and the update command is not advised', () => {
    for (const output of [SKIPPED_OUTPUT,
        '\n  tests: 1 skipped, 35 passed, 36 total\n  snapshots: 13 passed, 1 obsolete, 14 total \n']) {
        const { code, lines } = evaluateRun(0, parseTuiTestSummary(output));
        assert.equal(code, 1);
        assert.equal(lines.length, 1);
        assert.match(lines[0], /^Unused snapshots: 1 stored snapshot\(s\) were not used by this run, and 1 test\(s\) were skipped/);
        assert.match(lines[0], /this run cannot tell which are obsolete/);
        assert.match(lines[0], /Do not run `npm run tui:test:update` for this/);
        assert.doesNotMatch(lines[0], /every test (ran|passed)|Remove them/);
    }
});

test('a run in which no test ran fails', () => {
    const { code, lines } = evaluateRun(0, parseTuiTestSummary(NOTHING_RAN_OUTPUT));
    assert.equal(code, 1);
    assert.deepEqual(lines, ['No test ran (36 did not run): the filter matched no test file. Failing.']);
    // A run in which only skipped tests ran is not "no test ran".
    assert.equal(evaluateRun(0, parseTuiTestSummary('  tests: 2 skipped, 2 total\n')).code, 0);
});

test('help, version and the trace viewer are passed through without a summary check', async () => {
    for (const args of [['--version'], ['-v'], ['--help'], ['-h'], ['help'], ['show-trace', 'trace-file'],
        ['import-jobs', '--help']]) {
        assert.equal(isNonTestInvocation(args), true, args.join(' '));
        const err = sink();
        const { spawn } = fakeChild({ status: 0, output: '0.0.1-rc.5\n' });
        assert.equal(await runIsolatedTests(args, { spawn, stdout: sink(), stderr: err }), 0, args.join(' '));
        assert.equal(err.text(), '');
    }
    for (const args of [[], ['import-jobs'], ['-u'], ['--updateSnapshot'], ['-t'], ['--', '--help'],
        ['help-screen']]) {
        assert.equal(isNonTestInvocation(args), false, args.join(' '));
    }
    // A real run without a summary still fails closed.
    const { spawn } = fakeChild({ status: 0, output: 'no summary\n' });
    assert.equal(await runIsolatedTests(['import-jobs'], { spawn, stdout: sink(), stderr: sink() }), 1);
});

function snapshotTree(files) {
    const root = mkdtempSync(join(tmpdir(), 'ppds-tui-snapshots-test-'));
    const tests = join(root, 'tests');
    for (const file of files) {
        mkdirSync(join(tests, file, '..'), { recursive: true });
        writeFileSync(join(tests, file), '');
    }
    return { root, tests };
}

test('a snapshot file whose spec is gone is found; a file with its spec is not', () => {
    const { root, tests } = snapshotTree([
        'import-jobs.spec.ts', '__snapshots__/import-jobs.spec.ts.snap',
        '__snapshots__/ghost-screen.spec.ts.snap',
        'nested/inner.spec.ts', 'nested/__snapshots__/inner.spec.ts.snap',
        'nested/__snapshots__/gone.spec.ts.snap',
        '__snapshots__/notes.txt', 'loose.spec.ts.snap',
    ]);
    try {
        assert.deepEqual(findOrphanSnapshotFiles(tests), [
            'tests/__snapshots__/ghost-screen.spec.ts.snap',
            'tests/nested/__snapshots__/gone.spec.ts.snap',
        ]);
        assert.deepEqual(findOrphanSnapshotFiles(join(root, 'missing')), []);
    } finally {
        rmSync(root, { recursive: true, force: true });
    }
});

test('the committed snapshot files all have their spec', () => {
    assert.deepEqual(findOrphanSnapshotFiles(new URL('../tests', import.meta.url).pathname
        .replace(/^\/([A-Za-z]:)/, '$1')), []);
});

test('a snapshot file without a spec fails a passing run, and is reported in a failed one', async () => {
    const { root, tests } = snapshotTree(['__snapshots__/ghost-screen.spec.ts.snap']);
    try {
        for (const status of [0, 3]) {
            const err = sink();
            const { spawn } = fakeChild({ status, output: PASSING_OUTPUT });
            assert.equal(await runIsolatedTests([], { spawn, stdout: sink(), stderr: err, testsRoot: tests }),
                status === 0 ? 1 : status);
            assert.equal(err.text(), 'Obsolete snapshot files: 1 snapshot file(s) have no spec file: '
                + 'tests/__snapshots__/ghost-screen.spec.ts.snap. The spec was removed or renamed; delete '
                + 'the file with `git rm` and commit (`npm run tui:test:update` does not remove it).\n');
        }
    } finally {
        rmSync(root, { recursive: true, force: true });
    }
});
