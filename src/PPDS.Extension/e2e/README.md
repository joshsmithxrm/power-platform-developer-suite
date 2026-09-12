# Extension E2E tests

The suite launches the compiled extension in an isolated VS Code development host. `npm run test:e2e` always compiles the extension first.

`VSCODE_E2E_VERSION` controls the downloaded VS Code build:

- `minimum` resolves the minimum version from `package.json` `engines.vscode`.
- `stable` (the default) resolves the current stable release.
- An exact version such as `1.116.0` tests that release directly.
- `insiders` is available for investigation but is not part of CI.

CI runs both `minimum` and `stable` on Node 22 under `xvfb`. The harness uses a fresh user-data, extension, and workspace directory, and disables PPDS daemon auto-start so local state and credentials cannot affect smoke results.

Playwright assertion retries remain disabled. In CI only, the harness retries once when VS Code opens but the workbench fails to become ready within 60 seconds, a documented Electron UI-startup boundary. Test assertion failures are never retried. Traces and screenshots are retained on failure and uploaded by the workflow.

## Monitored ramp

The matrix initially uses step-level `continue-on-error` while Linux CI evidence is collected. Remove that setting after both the declared-minimum and stable lanes pass three consecutive runs, including at least one pull-request run and one `main` run, without consuming the startup retry. At that point `build-status` will make the matrix blocking. If a lane fails or consumes the retry, reset the consecutive-run count after correcting the cause; the ramp must not remain advisory permanently.
