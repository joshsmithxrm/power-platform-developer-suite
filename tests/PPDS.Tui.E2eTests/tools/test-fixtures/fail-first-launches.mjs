// Test fixture, used only by launch-retry.tui.test.mjs. Preloaded before
// launch-retry-preload.mjs, it makes the raw node-pty spawn throw for the first
// `failures` calls in each process (query string, for example
// `fail-first-launches.mjs?failures=2`), the way process creation fails on a
// Windows runner. The launch retry then wraps this failing spawn.
import { createRequire } from 'node:module';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const TUI_ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '..', '..');
const pty = createRequire(join(TUI_ROOT, 'node_modules', '@microsoft', 'tui-test', 'package.json'))(
    '@homebridge/node-pty-prebuilt-multiarch');

const failures = Number(new URL(import.meta.url).searchParams.get('failures') ?? '0');
const original = pty.spawn;
let calls = 0;
pty.spawn = function failFirstLaunches(...args) {
    calls++;
    if (calls <= failures) {
        throw new Error(`Cannot create process, error code: 87 (injected failure ${calls} of ${failures})`);
    }
    return original.apply(this, args);
};
