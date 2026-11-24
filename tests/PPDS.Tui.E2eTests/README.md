# TUI End-to-End Tests

Visual snapshot tests for the PPDS TUI using [@microsoft/tui-test](https://github.com/microsoft/tui-test).

## Requirements

- **Node.js 26 LTS**, selected by the repository's `.nvmrc`
- PPDS CLI built (`dotnet build src/PPDS.Cli/PPDS.Cli.csproj`)

The legacy `@microsoft/tui-test` runner declares Node `>=16.6.0 <21.0.0`.
That range already excluded the previous Node 22 CI runtime and produces an
`EBADENGINE` warning on Node 26. Keep the warning visible and require the full
Windows TUI suite to pass; a passing suite is our compatibility evidence, not an
upstream support guarantee. Migrating to the newer TUI framework is separate work.

A scoped override pins the runner's native PTY dependency to
`@homebridge/node-pty-prebuilt-multiarch` 0.14.1. The previous 0.11.14 cannot install
its Windows native binary on Node 26. The replacement declares support for Node
20 through 26 and keeps the runner's PTY API. The version is pinned independently
of npm's mutable `latest` tag, and
[GitHub marks 0.14.1 as prerelease](https://github.com/homebridge/node-pty-prebuilt-multiarch/releases/tag/v0.14.1).

Playwright is pinned to 1.65.0, matching the current extension's locked release. The old
locked 1.57.0 installer hangs extracting browser downloads on Node 26; the newer release
includes the [upstream ZIP lifecycle fix](https://github.com/microsoft/playwright/pull/40747).

## Installing Node 26 (Windows)

If you don't have a Node version manager, install **nvm-windows**:

1. Download installer from: https://github.com/coreybutler/nvm-windows/releases
2. Run `nvm-setup.exe`
3. **Restart your terminal** after installation

Then install and use Node 26 after its LTS release:
```powershell
nvm install 26
nvm use 26
node --version  # Should show v26.x.x
```

## Setup

```bash
# Switch to the repository tooling version (if using nvm-windows)
nvm use 26

# Install dependencies
npm ci
```

## Running Tests

```bash
# Run tests
npm test

# Update snapshots after intentional UI changes
npm run test:update

# Run with visible terminal (debugging)
npm run test:headed
```

## How It Works

These tests:
1. Launch the actual `ppds` CLI in a pseudo-terminal (PTY)
2. Capture the terminal output as text
3. Compare against stored snapshots in `__snapshots__/`

This enables Claude to verify TUI visual changes without human inspection.

## Test Structure

- `tests/startup.spec.ts` - Main menu, keyboard shortcuts, navigation
- `tests/test-helpers.ts` - Utilities for launching PPDS

## Snapshots

Snapshots are stored in `tests/__snapshots__/` and should be committed.
Every command creates its own empty profile directory and removes it when the
runner exits. Real PPDS profiles, inherited credentials, and live-test options
are not passed to the test process. The goldens describe the signed-out state.

CI runs `npm test` to compare committed goldens; it does not regenerate them.
Use `npm run test:update` only after reviewing an intentional UI change, then
review every changed snapshot and rerun `npm test` before committing.

`npm test` fails on obsolete snapshots, in two forms:

- **A snapshot file with no spec**: `tests/__snapshots__/<name>.spec.ts.snap`
  with no `tests/<name>.spec.ts`, left behind when a spec is removed or
  renamed. It fails every test run, whatever else happened. tui-test does not
  report it and `test:update` does not remove it; delete it with `git rm`.
- **An unused entry** in the snapshot file of a spec that ran. When every test
  ran and passed, it is obsolete: remove it with `npm run test:update`. When a
  test was skipped (`test.skip`), did not run (`test.only`, a filter) or failed,
  the unused entries may be those tests' snapshots, so the run cannot tell
  which are obsolete. Run every test to decide, and do not run `test:update`
  then, because tui-test would delete those snapshots. A skipped or unrun test
  with unused entries fails the run; a failed run keeps its own status and
  prints the count as a note.

A run in which no test ran (a filter that matches no spec, or every selected
test skipped) also fails. The
commands that run no test, `--help`, `--version`, `help` and `show-trace`,
pass through with tui-test's own exit code.

## Launch failures

A test can fail before the application starts: on a Windows runner the
pseudo-terminal launch has failed with `Cannot create process, error code: 87`.
`npm test` preloads `tools/launch-retry-preload.mjs` into tui-test and its
workers, which retries only that launch call, at most 3 attempts per test
(pauses of 250 ms and 1 s). A launch that still fails fails the test with
`TUI launch failure: the application under test did not start after 3 attempts`.
No assertion is retried and tui-test's own test retry stays at 0. At the end
of the run, `npm test` prints how many launch attempts were retried and how
many tests failed to launch.

## Tool tests

`npm run test:tools` runs the `node:test` tests of `tools/`, including an end
to end check that injects launch failures into a real run of
`tests/startup.spec.ts` (it needs the CLI built; CI runs it before the
snapshot suite). On Windows it also makes the first launch in each worker a
real call with a working directory that does not exist, so process creation
fails in the native code, and checks that the next launch in the same worker
starts the TUI. Every test has a 2-minute limit (the end-to-end ones, 5).
