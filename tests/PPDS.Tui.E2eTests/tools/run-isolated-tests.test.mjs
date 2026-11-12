import assert from 'node:assert/strict';
import { existsSync, mkdtempSync, readdirSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import test from 'node:test';
import { isolatedEnvironment, runIsolatedTests } from './run-isolated-tests.mjs';

test('only safe runtime settings reach the isolated runner', () => {
    const result = isolatedEnvironment({
        Path: 'safe-path', SystemRoot: 'windows', PLAYWRIGHT_BROWSERS_PATH: 'browsers',
        PPDS_CONFIG_DIR: 'real-profile', PPDS_PROFILE: 'real', PPDS_LIVE_TESTS: '1',
        AZURE_CLIENT_SECRET: 'secret', GH_TOKEN: 'token', NODE_OPTIONS: '--require injected.js',
        DOTNET_GENERATE_ASPNET_CERTIFICATE: 'true',
    }, 'owned-fixture');
    assert.deepEqual(result, {
        Path: 'safe-path', SystemRoot: 'windows', PLAYWRIGHT_BROWSERS_PATH: 'browsers',
        PPDS_CONFIG_DIR: 'owned-fixture', PPDS_FORCE_TUI: '1',
        DOTNET_GENERATE_ASPNET_CERTIFICATE: 'false', DOTNET_SKIP_FIRST_TIME_EXPERIENCE: '1',
    });
});

test('runner owns and removes its empty fixture on success and failure', () => {
    const root = mkdtempSync(join(tmpdir(), 'ppds-tui-launcher-test-'));
    const fixtures = [];
    const sentinel = join(root, 'unrelated.txt');
    writeFileSync(sentinel, 'retain');
    try {
        for (const status of [0, 14]) {
            assert.equal(runIsolatedTests(['--some-option'], {
                temporaryRoot: root,
                environment: { PPDS_CONFIG_DIR: 'unrelated-profile' },
                spawn(executable, args, options) {
                    assert.equal(executable, process.execPath);
                    assert.equal(args.at(-1), '--some-option');
                    const fixture = options.env.PPDS_CONFIG_DIR;
                    fixtures.push(fixture);
                    assert.deepEqual(readdirSync(fixture), []);
                    writeFileSync(join(fixture, 'tui-state.json'), '{}');
                    return { status };
                },
            }), status);
            assert.equal(existsSync(fixtures.at(-1)), false);
        }
        assert.notEqual(fixtures[0], fixtures[1]);
        assert.equal(existsSync(sentinel), true);
    } finally {
        rmSync(root, { recursive: true, force: true });
    }
});

test('launch failures remove only the owned fixture', () => {
    const root = mkdtempSync(join(tmpdir(), 'ppds-tui-launcher-test-'));
    try {
        assert.throws(() => runIsolatedTests([], {
            temporaryRoot: root, environment: {},
            spawn() { return { error: new Error('synthetic launch failure') }; },
        }), /synthetic launch failure/);
        assert.deepEqual(readdirSync(root), []);
    } finally {
        rmSync(root, { recursive: true, force: true });
    }
});
