// End-to-end check of the launch retry through the real tui-test runner and the
// real TUI: a fixture preload makes node-pty's spawn fail inside tui-test's
// worker processes, and the run must show the retry (or the launch failure).
// Needs the CLI built for net10.0 (see README.md). In CI a missing build fails.
import assert from 'node:assert/strict';
import { existsSync } from 'node:fs';
import { platform } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import test from 'node:test';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { LAUNCH_RETRY_PRELOAD, runIsolatedTests } from './run-isolated-tests.mjs';

const TUI_ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const REPO_ROOT = resolve(TUI_ROOT, '..', '..');
const PPDS = join(REPO_ROOT, 'src', 'PPDS.Cli', 'bin', 'Debug', 'net10.0',
    platform() === 'win32' ? 'ppds.exe' : 'ppds');
const FAULT = pathToFileURL(join(TUI_ROOT, 'tools', 'test-fixtures', 'fail-first-launches.mjs')).href;
// startup.spec.ts: three tests, no snapshots. tui-test doubles every backslash
// in a filter (for Windows paths), so the dot is not escaped.
const SPEC_FILTER = 'startup.spec';

const built = existsSync(PPDS);
if (!built && process.env.CI) {
    throw new Error(`The CLI under test is not built (${PPDS}); build it before the tool tests.`);
}

function sink() {
    const chunks = [];
    return { write: (text) => { chunks.push(String(text)); return true; }, text: () => chunks.join('') };
}

async function run(failures, mode = 'throw') {
    const stdout = sink();
    const stderr = sink();
    const code = await runIsolatedTests([SPEC_FILTER], {
        preloads: [`${FAULT}?failures=${failures}&mode=${mode}`, LAUNCH_RETRY_PRELOAD],
        stdout, stderr,
    });
    return { code, out: stdout.text().replace(/\x1b\[[0-9;]*[A-Za-z]/g, ''), err: stderr.text() };
}

test('two failed launches per worker are retried and every test passes', { skip: !built && 'CLI not built', timeout: 300_000 }, async () => {
    const { code, out, err } = await run(2);
    assert.equal(code, 0, out + err);
    assert.match(out, /^\s*tests: \d+ did not run, 3 passed, \d+ total$/m);
    // Each worker process that ran a test failed its first two launches.
    const retried = Number(err.match(/^TUI launch: (\d+) failed launch attempt\(s\) were retried/m)?.[1]);
    assert.ok(retried >= 2 && retried % 2 === 0, err);
    assert.match(err, /injected failure 1 of 2/);
    assert.doesNotMatch(err, /TUI launch failure/);
});

test('a launch that never succeeds fails each test once, as a launch failure', { skip: !built && 'CLI not built', timeout: 300_000 }, async () => {
    const { code, out, err } = await run(1000);
    assert.notEqual(code, 0);
    assert.match(out, /^\s*tests: 3 failed, \d+ did not run, \d+ total$/m);
    const messages = out.match(/TUI launch failure: the application under test did not start after 3 attempts/g) ?? [];
    assert.ok(messages.length >= 3, out);
    assert.match(out, /Last error: Cannot create process, error code: 87 \(injected failure \d+ of 1000\)/);
    // tui-test runs each test once: no test-level retry.
    assert.doesNotMatch(out, /Retry #/);
    assert.match(err, /^TUI launch failure: 3 test\(s\) failed because the application under test did not start after 3 attempts\./m);
});

// Windows only: the first launch in each worker is a real call to node-pty with
// a working directory that does not exist, so CreateProcess itself fails in the
// native code (not an error made up by the fixture); the second is a normal
// launch in the same worker process. Proves that a real process-creation
// failure is retried and that the next attempt in that worker starts the TUI.
// On Linux a missing working directory does not fail spawn (the child process
// fails later), so there is nothing to prove there.
const onWindows = platform() === 'win32';
test('a real process-creation failure on Windows is retried and the next launch starts the TUI', {
    skip: (!onWindows && 'Windows only: on Linux a missing working directory does not fail spawn')
        || (!built && 'CLI not built'),
    timeout: 300_000,
}, async () => {
    const { code, out, err } = await run(1, 'missing-cwd');
    assert.equal(code, 0, out + err);
    assert.match(out, /^\s*tests: \d+ did not run, 3 passed, \d+ total$/m);
    const summary = err.match(/^TUI launch: (\d+) failed launch attempt\(s\) were retried .*errors: (.*)\)\.$/m);
    assert.ok(summary, err);
    assert.ok(Number(summary[1]) >= 1, err);
    // The retried error came from node-pty, not from the fixture.
    assert.doesNotMatch(summary[2], /fault fixture|injected/, err);
    assert.doesNotMatch(err, /TUI launch failure/);
});
