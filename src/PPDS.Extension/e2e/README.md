# Extension E2E tests

The suite launches the compiled extension in an isolated VS Code development host. `npm run test:e2e` always compiles the extension first.

`VSCODE_E2E_VERSION` controls the downloaded VS Code build:

- `minimum` resolves the minimum version from `package.json` `engines.vscode`.
- `stable` (the default) resolves the current stable release.
- An exact version such as `1.116.0` tests that release directly.
- `insiders` is available for investigation but is not part of CI.

CI runs both `minimum` and `stable` on Node 22 under `xvfb`. The harness uses a fresh user-data, extension, and workspace directory, and disables PPDS daemon auto-start so local state and credentials cannot affect smoke results.

Playwright assertion retries remain disabled. In CI only, the harness retries once when VS Code opens but the workbench fails to become ready within 60 seconds, a documented Electron UI-startup boundary. Test assertion failures are never retried. Traces and screenshots are retained on failure and uploaded by the workflow.

## Blocking CI gate

The monitored ramp was promoted to a blocking gate after four consecutive clean declared-minimum/stable matrix runs, including pull-request and `main` runs, completed without consuming the startup retry. A failure in either lane now fails `build-status`. Traces and screenshots are still uploaded on failure so a blocking failure retains its diagnostics.

## Native notebook host tests

Run `npm run test:notebook-host` for the current stable VS Code host. Set
`VSCODE_E2E_VERSION=minimum` to test the minimum declared in `engines.vscode`.
The runner downloads the selected host and uses temporary workspace, user-data,
extension, and PPDS config directories. AI features are disabled in the temporary
settings, including customization migration and plugin discovery. Windows child
hosts receive temporary user-profile and app-data directories; Linux child hosts
receive temporary XDG application directories without overriding HOME. A test-only extension loads the production serializer and controller
with an in-memory daemon, so these tests require no Dataverse credentials and
make no live Dataverse requests.

Coverage includes concurrent native SQL/FetchXML executions, captured bindings
and authentication identity, output attribution and record links, scoped
interruption and queues, and legacy file loading plus saved-binding reload. Unit tests
cover picker cancellation and Production/Unknown acknowledgement dialogs.
Passing host tests does not substitute for an authorized live sandbox check.

The blocking `extension-e2e` CI matrix runs this suite under xvfb on both minimum
and stable hosts after the Playwright smoke suite, even if smoke assertions fail.
The native step has an eight-minute timeout inside the existing 25-minute job.
On failure, the seven-day diagnostics artifact includes `notebook-host.log`,
`notebook-host/host-run.json`, and available native VS Code logs from the temporary
profile. No live Dataverse credentials or fixtures are used.
