// Test fixture, used only by launch-retry.tui.test.mjs. Preloaded before
// launch-retry-preload.mjs, it makes the first `failures` node-pty spawn calls
// in each process fail; the launch retry then wraps this spawn. The query
// string selects how (for example `fail-first-launches.mjs?failures=2`):
//
// - mode=throw (default): throw an error shaped like the Windows runner's
//   "Cannot create process, error code: 87" without calling node-pty.
// - mode=missing-cwd: call the real node-pty spawn with a working directory
//   that does not exist, so process creation itself fails in the native code
//   (Windows: CreateProcess). If that call starts a process instead, the
//   process is killed and the fixture throws a message the test rejects.
import { existsSync } from 'node:fs';
import { createRequire } from 'node:module';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const TUI_ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '..', '..');
const pty = createRequire(join(TUI_ROOT, 'node_modules', '@microsoft', 'tui-test', 'package.json'))(
    '@homebridge/node-pty-prebuilt-multiarch');

const query = new URL(import.meta.url).searchParams;
const failures = Number(query.get('failures') ?? '0');
const mode = query.get('mode') ?? 'throw';
if (!['throw', 'missing-cwd'].includes(mode)) throw new Error(`unknown fault mode ${mode}`);

const original = pty.spawn;
let calls = 0;
pty.spawn = function failFirstLaunches(file, args, options) {
    calls++;
    if (calls > failures) return original.call(this, file, args, options);
    if (mode === 'throw') {
        throw new Error(`Cannot create process, error code: 87 (injected failure ${calls} of ${failures})`);
    }
    const missing = join(tmpdir(), `ppds-tui-missing-cwd-${process.pid}-${calls}-${Date.now()}`);
    if (existsSync(missing)) throw new Error(`fault fixture: ${missing} exists`);
    const terminal = original.call(this, file, args, { ...options, cwd: missing });
    try { terminal.kill(); } catch { /* already gone */ }
    throw new Error('fault fixture: a launch with a missing working directory unexpectedly started');
};
