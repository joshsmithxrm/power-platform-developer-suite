import { appendFileSync } from 'node:fs';

/**
 * Bounded retry of a failed launch of the application under test.
 *
 * tui-test starts the TUI with node-pty's `spawn` before the test body runs.
 * When process creation fails (on Windows, for example, "Cannot create
 * process, error code: 87"), `spawn` throws and the application never
 * started, so no assertion has run. Only that call is retried here: a test
 * body and its assertions are never retried, and a launch that still fails is
 * reported as a launch failure, not as a test failure of the screen.
 */

/** Total launch attempts per test, the first included. */
export const LAUNCH_ATTEMPTS = 3;

/** Pause before the second and the third attempt, in milliseconds. */
export const LAUNCH_RETRY_DELAYS_MS = Object.freeze([250, 1000]);

/** Starts every final launch-failure message; the runner summary searches for it. */
export const LAUNCH_FAILURE_LABEL = 'TUI launch failure';

const INSTALLED = Symbol.for('ppds.tui-e2e.launch-retry');

export class TuiLaunchError extends Error {
    constructor(message, options) {
        super(message, options);
        this.name = 'TuiLaunchError';
    }
}

/** Blocks the current thread; `spawn` is synchronous, so the pause must be too. */
export function sleepSync(milliseconds) {
    Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, milliseconds);
}

function describeError(error) {
    return error instanceof Error ? error.message : String(error);
}

/**
 * Wraps a synchronous spawn function. Each attempt that throws is recorded;
 * after the last one a TuiLaunchError is thrown with the last error as cause.
 * Anything the spawned program does after `spawn` returns is not retried.
 */
export function withLaunchRetry(spawn, {
    attempts = LAUNCH_ATTEMPTS,
    delaysMs = LAUNCH_RETRY_DELAYS_MS,
    sleep = sleepSync,
    record = () => {},
} = {}) {
    if (!Number.isInteger(attempts) || attempts < 1 || attempts > 5) {
        throw new RangeError('attempts must be an integer from 1 to 5');
    }
    return function spawnWithLaunchRetry(file, args, options) {
        for (let attempt = 1; ; attempt++) {
            try {
                return spawn.call(this, file, args, options);
            } catch (error) {
                const last = attempt >= attempts;
                record({
                    event: last ? 'failed' : 'retry',
                    attempt,
                    attempts,
                    file: String(file),
                    error: describeError(error),
                });
                if (last) {
                    throw new TuiLaunchError(
                        `${LAUNCH_FAILURE_LABEL}: the application under test did not start `
                        + `after ${attempts} attempt${attempts === 1 ? '' : 's'} (${file}). `
                        + 'No assertion ran; this is not a failure of the screen under test. '
                        + `Last error: ${describeError(error)}`,
                        { cause: error });
                }
                sleep(delaysMs[Math.min(attempt - 1, delaysMs.length - 1)] ?? 0);
            }
        }
    };
}

/** Replaces `pty.spawn` once; a second call leaves the first wrapper in place. */
export function installLaunchRetry(pty, options) {
    if (pty[INSTALLED]) return false;
    const original = pty.spawn;
    if (typeof original !== 'function') {
        throw new TypeError('node-pty module has no spawn function');
    }
    pty.spawn = withLaunchRetry(original, options);
    Object.defineProperty(pty, INSTALLED, { value: true });
    return true;
}

/** Appends one JSON line per attempt record; the runner reads the file after the run. */
export function launchLogRecorder(logPath) {
    if (!logPath) return () => {};
    return (entry) => {
        try {
            appendFileSync(logPath, JSON.stringify({ pid: process.pid, ...entry }) + '\n');
        } catch {
            // The log is a report, never a reason to fail a launch.
        }
    };
}

/** Parses the runner's launch log text into counts for the summary. */
export function summarizeLaunchLog(text) {
    const summary = { retried: 0, failed: 0, errors: [] };
    for (const line of (text ?? '').split('\n')) {
        if (!line.trim()) continue;
        let entry;
        try {
            entry = JSON.parse(line);
        } catch {
            continue;
        }
        if (entry.event === 'retry') summary.retried++;
        if (entry.event === 'failed') summary.failed++;
        if (entry.error && !summary.errors.includes(entry.error)) summary.errors.push(entry.error);
    }
    return summary;
}
