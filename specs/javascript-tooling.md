# JavaScript Tooling

**Status:** Draft
**Code:** `.nvmrc` | `package.json` | `.github/workflows/` | `src/PPDS.Extension/esbuild.js` | `tests/ci/test_javascript_tooling.py`
**Surfaces:** Development | CI | Extension

## Runtime boundaries

Repository tools use the Node major selected by `.nvmrc`. CI, contributor setup,
extension packaging, and audit tooling share that selection. The prepared Node 26
switch must remain unmerged until Node 26 is released as LTS and the maintainer
approves it; calendar dates alone are not release evidence.

VS Code supplies the extension-host runtime. The extension remains compatible
with the declared minimum VS Code 1.116.0, whose Electron 39.8.7 embeds Node
22.22.1. Building with a newer Node version does not raise that minimum. The host
bundle explicitly targets Node 22.22.1; extension API typings remain on Node 22
through the separate typings alignment. Browser bundles retain their own target.

The primary extension build/check job keeps its `extension` name because the
major-dependency merge gate requires that evidence. A separate Node 22
compatibility job runs the extension tests. Both minimum and stable VS Code E2E
lanes remain blocking and build with the selected tooling runtime before running
the resulting extension in VS Code's own host.

## Acceptance criteria

| ID | Requirement | Test | Status |
|----|-------------|------|--------|
| AC-01 | Default tooling jobs use `.nvmrc`, and runtime-only configuration changes trigger their validation | `tests/ci/test_javascript_tooling.py::test_tooling_jobs_share_the_declared_runtime`, `test_runtime_changes_trigger_validation` | ✅ |
| AC-02 | The extension bundle targets Node 22.22.1 independently of the tooling version | `src/PPDS.Extension/tools/node-runtime.test.mjs` | ✅ |
| AC-03 | Node 22 unit compatibility and minimum/stable VS Code E2E results block the build gate; the primary extension check keeps its existing name | `tests/ci/test_javascript_tooling.py::test_compatibility_is_blocking`, `tests/ci/test_extension_e2e_workflow.py` | ✅ |
| AC-04 | The existing TUI runner can create and interact with its native PTY on the selected tooling runtime without replacing the framework | `tests/PPDS.Tui.E2eTests/tests/startup.spec.ts` | ✅ |
| AC-05 | The extension's declared and resolved top-level Node typings remain on major 22 independently of the tooling runtime | `tests/ci/test_javascript_tooling.py::test_extension_node_typings_preserve_the_host_contract` | ✅ |

## Native terminal dependency

The legacy TUI framework remains in place. Its nested native PTY dependency is
pinned to `@homebridge/node-pty-prebuilt-multiarch` 0.14.1 through a scoped npm
override. The previous 0.11.14 package has no Windows Node 26 ABI 147 prebuild,
and its source-build fallback fails with `spawn EINVAL`. The maintained 0.14.1
package declares Node `>=20 <27` and supports the same PTY API used by the runner.

The framework itself still declares Node `>=16.6.0 <21.0.0`, which already
excluded the previous Node 22 baseline. Do not suppress that engine warning or
mistake our runtime tests for an upstream support promise. The native package's
0.14.1 version is npm's `latest`, but its GitHub release is marked prerelease;
maintainer review must account for that discrepancy before adoption.

The TUI project's Playwright installer is aligned to 1.61.1, the release already
used by the extension. Its previous locked 1.57.0 release hangs extracting
browser archives on Node 26. Playwright's upstream fix
([microsoft/playwright#40747](https://github.com/microsoft/playwright/pull/40747))
corrects the ZIP stream destruction lifecycle. Keep the existing browser-install
step in TUI CI so a cold installation exercises this compatibility boundary.

## Validation before adoption

Run locked installs, typechecks, lint, production packaging, and extension tests
with the proposed Node runtime. Check the native TUI tooling and its full suite,
plus the root audit manifest commands. Confirm the Node 22 compatibility job and
both VS Code lanes in CI. A major tooling version is not evidence that shipped
Node APIs or the supported VS Code floor can change.
