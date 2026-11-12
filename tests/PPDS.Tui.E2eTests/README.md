# TUI End-to-End Tests

Visual snapshot tests for the PPDS TUI using [@microsoft/tui-test](https://github.com/microsoft/tui-test).

## Requirements

- **Node.js 20 LTS** (required by @microsoft/tui-test dependencies)
- PPDS CLI built (`dotnet build src/PPDS.Cli/PPDS.Cli.csproj`)

## Installing Node 20 (Windows)

If you don't have a Node version manager, install **nvm-windows**:

1. Download installer from: https://github.com/coreybutler/nvm-windows/releases
2. Run `nvm-setup.exe`
3. **Restart your terminal** after installation

Then install and use Node 20:
```powershell
nvm install 20
nvm use 20
node --version  # Should show v20.x.x
```

## Setup

```bash
# Switch to Node 20 (if using nvm)
nvm use 20

# Install dependencies
npm install
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
