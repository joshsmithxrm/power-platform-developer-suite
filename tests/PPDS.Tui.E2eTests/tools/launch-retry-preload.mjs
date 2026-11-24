// Preloaded (through NODE_OPTIONS --import, set by run-isolated-tests.mjs) into
// the tui-test runner and each of its worker processes. It wraps the spawn
// function of the node-pty module that tui-test loads, so a failed launch of
// the application under test is retried; see launch-retry.mjs.
import { createRequire } from 'node:module';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { installLaunchRetry, launchLogRecorder } from './launch-retry.mjs';

const TUI_ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '..');
// Resolve node-pty from tui-test's own package so this is the same module
// instance (the same CommonJS exports object) that tui-test's terminal uses.
const requireFromTuiTest = createRequire(
    join(TUI_ROOT, 'node_modules', '@microsoft', 'tui-test', 'package.json'));
const pty = requireFromTuiTest('@homebridge/node-pty-prebuilt-multiarch');

installLaunchRetry(pty, { record: launchLogRecorder(process.env.PPDS_TUI_LAUNCH_LOG) });
