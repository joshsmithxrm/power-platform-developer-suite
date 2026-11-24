import assert from 'node:assert/strict';
import { mkdtempSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import test from 'node:test';
import {
    LAUNCH_ATTEMPTS, LAUNCH_FAILURE_LABEL, LAUNCH_RETRY_DELAYS_MS, TuiLaunchError,
    installLaunchRetry, launchLogRecorder, summarizeLaunchLog, withLaunchRetry,
} from './launch-retry.mjs';

/** A spawn that throws for its first `failures` calls, then returns a terminal. */
function failingSpawn(failures, message = 'Cannot create process, error code: 87') {
    const calls = [];
    const spawn = (file, args, options) => {
        calls.push({ file, args, options });
        if (calls.length <= failures) throw new Error(message);
        return { terminal: calls.length };
    };
    return { spawn, calls };
}

function harness(failures) {
    const { spawn, calls } = failingSpawn(failures);
    const sleeps = [];
    const records = [];
    const wrapped = withLaunchRetry(spawn, {
        sleep: (ms) => sleeps.push(ms),
        record: (entry) => records.push(entry),
    });
    return { wrapped, calls, sleeps, records };
}

test('policy: three attempts in total, with short pauses between them', () => {
    assert.equal(LAUNCH_ATTEMPTS, 3);
    assert.deepEqual(LAUNCH_RETRY_DELAYS_MS, [250, 1000]);
});

test('a launch that succeeds first time is called once, with its arguments', () => {
    const { wrapped, calls, sleeps, records } = harness(0);
    assert.deepEqual(wrapped('ppds', ['interactive'], { cols: 80 }), { terminal: 1 });
    assert.deepEqual(calls, [{ file: 'ppds', args: ['interactive'], options: { cols: 80 } }]);
    assert.deepEqual(sleeps, []);
    assert.deepEqual(records, []);
});

for (const failures of [1, 2]) {
    test(`a launch that fails ${failures} time(s) is retried and the terminal returned`, () => {
        const { wrapped, calls, sleeps, records } = harness(failures);
        assert.deepEqual(wrapped('ppds', ['interactive'], {}), { terminal: failures + 1 });
        assert.equal(calls.length, failures + 1);
        assert.deepEqual(sleeps, LAUNCH_RETRY_DELAYS_MS.slice(0, failures));
        assert.deepEqual(records.map((r) => [r.event, r.attempt, r.attempts]),
            Array.from({ length: failures }, (_, i) => ['retry', i + 1, 3]));
        assert.match(records[0].error, /error code: 87/);
    });
}

test('a launch that keeps failing stops after three attempts with a launch-failure message', () => {
    const { wrapped, calls, sleeps, records } = harness(Infinity);
    let thrown;
    assert.throws(() => wrapped('ppds', ['interactive'], {}), (error) => {
        thrown = error;
        return true;
    });
    assert.equal(calls.length, 3);
    assert.deepEqual(sleeps, [250, 1000]);
    assert.ok(thrown instanceof TuiLaunchError);
    assert.ok(thrown.message.startsWith(`${LAUNCH_FAILURE_LABEL}: the application under test did not start after 3 attempts (ppds).`));
    assert.match(thrown.message, /No assertion ran; this is not a failure of the screen under test\./);
    assert.match(thrown.message, /Last error: Cannot create process, error code: 87$/);
    assert.equal(thrown.cause.message, 'Cannot create process, error code: 87');
    assert.deepEqual(records.map((r) => r.event), ['retry', 'retry', 'failed']);
});

test('tui-test itself never retries a test, so a failed assertion is not retried', () => {
    const config = readFileSync(new URL('../tui-test.config.ts', import.meta.url), 'utf8');
    assert.match(config, /^\s*retries: 0,$/m);
    assert.doesNotMatch(config.replace(/^\s*retries: 0,$/m, ''), /retries/);
});

test('attempts are bounded', () => {
    for (const attempts of [0, 6, 1.5, Infinity]) {
        assert.throws(() => withLaunchRetry(() => {}, { attempts }), RangeError);
    }
    const { spawn, calls } = failingSpawn(Infinity);
    assert.throws(() => withLaunchRetry(spawn, { attempts: 1, sleep() {} })('ppds'),
        /did not start after 1 attempt \(ppds\)/);
    assert.equal(calls.length, 1);
});

test('install wraps the module spawn once and keeps the receiver', () => {
    let failures = 1;
    const pty = {
        marker: 'module',
        spawn(file) {
            if (failures-- > 0) throw new Error('spawn failed');
            return { file, self: this.marker };
        },
    };
    assert.equal(installLaunchRetry(pty, { sleep() {} }), true);
    const first = pty.spawn;
    assert.equal(installLaunchRetry(pty, { sleep() {} }), false);
    assert.equal(pty.spawn, first);
    assert.deepEqual(pty.spawn('ppds'), { file: 'ppds', self: 'module' });
    assert.throws(() => installLaunchRetry({}), TypeError);
});

test('the launch log records each attempt and is summarised for the runner', () => {
    const root = mkdtempSync(join(tmpdir(), 'ppds-tui-launch-log-test-'));
    try {
        const log = join(root, 'launches.jsonl');
        const record = launchLogRecorder(log);
        const flaky = withLaunchRetry(failingSpawn(1).spawn, { sleep() {}, record });
        flaky('ppds');
        const broken = withLaunchRetry(failingSpawn(Infinity, 'boom').spawn, { sleep() {}, record });
        assert.throws(() => broken('ppds'), TuiLaunchError);
        const summary = summarizeLaunchLog(readFileSync(log, 'utf8'));
        assert.deepEqual(summary, {
            retried: 3, failed: 1,
            errors: ['Cannot create process, error code: 87', 'boom'],
        });
    } finally {
        rmSync(root, { recursive: true, force: true });
    }
});

test('without a log path, recording does nothing and a bad log line is ignored', () => {
    assert.doesNotThrow(() => launchLogRecorder(undefined)({ event: 'retry' }));
    assert.deepEqual(summarizeLaunchLog('not json\n\n{"event":"retry","error":"x"}\n'),
        { retried: 1, failed: 0, errors: ['x'] });
    assert.deepEqual(summarizeLaunchLog(''), { retried: 0, failed: 0, errors: [] });
});
