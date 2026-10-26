# Extension E2E tests

The suite launches the compiled extension in an isolated VS Code development host. `npm run test:e2e` always compiles the extension first.

`VSCODE_E2E_VERSION` controls the downloaded VS Code build:

- `minimum` resolves the minimum version from `package.json` `engines.vscode`.
- `stable` (the default) resolves the current stable release.
- An exact version such as `1.116.0` tests that release directly.
- `insiders` is available for investigation but is not part of CI.

CI builds and drives both `minimum` and `stable` with the tooling version in the repository's `.nvmrc` under `xvfb`. VS Code supplies the extension-host runtime: the minimum supported VS Code 1.116.0 embeds Node 22.22.1. The host bundle targets that runtime, and a separate Node 22 unit-test job checks compatibility. The harness uses a fresh user-data, extension, and workspace directory, and disables PPDS daemon auto-start so local state and credentials cannot affect smoke results.

Playwright assertion retries remain disabled. In CI only, the harness retries once when VS Code opens but the workbench fails to become ready within 60 seconds, a documented Electron UI-startup boundary. Test assertion failures are never retried. Traces and screenshots are retained on failure and uploaded by the workflow.

## Blocking CI gate

The monitored ramp was promoted to a blocking gate after four consecutive clean declared-minimum/stable matrix runs, including pull-request and `main` runs, completed without consuming the startup retry. A failure in either lane now fails `build-status`. Traces and screenshots are still uploaded on failure so a blocking failure retains its diagnostics.
