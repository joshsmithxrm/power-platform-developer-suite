---
paths: ["src/PPDS.Extension/**"]
---

# Extension Conventions

Detail, reasons and the new-panel recipe are in `src/PPDS.Extension/DEVELOPMENT.md`. Read it before adding a panel, a message command or a daemon method.

- Webview scripts are external TypeScript under `src/panels/webview/` (esbuild IIFE bundles); VS Code silently drops inline scripts over about 32KB. Panel CSS lives in `src/panels/styles/{name}-panel.css` and starts with `@import './shared.css';`.
- Messages are discriminated unions in `src/panels/webview/shared/message-types.ts`; both the host `handleMessage` switch and the webview listener end with `assertNever`.
- Host panels extend `WebviewPanelBase` and use its `initializePanel` / `handleEnvironmentPickerClick` flows; pass `this.environmentUrl` and `this.profileName` to every daemon call.
- Talk to the daemon only through `daemonClient.ts`; no business logic in the extension.
- Escape untrusted data with `escapeHtml` / `escapeAttr` from `dom-utils.ts` before `innerHTML`; use `getNonce()` for CSP nonces.
- Keep the toolbar order and shared classes described in DEVELOPMENT.md ("Styling and theming"); the content wrapper is `class="content"`.
- New panels need entries in the `builds` array in `esbuild.js`, a command in `package.json`, registration in `src/extension.ts`, and an `instanceCount` in the `registerDebugCommands` call.
- The `PPDS` LogOutputChannel writes to `exthost/<extId>/PPDS.log`, not a numbered `N-PPDS.log`; check that location when looking for logs on disk.
