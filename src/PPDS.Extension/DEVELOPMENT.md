# Extension development guide

This guide is for people changing the PPDS VS Code extension. For what the extension does and how to use it, see [README.md](README.md). For repository setup, branches and the PR process, see [CONTRIBUTING.md](../../CONTRIBUTING.md). End-to-end test details are in [e2e/README.md](e2e/README.md).

## How the extension is put together

The extension has three parts:

- **Extension host** (`src/extension.ts` and everything it imports): Node.js code that registers commands, tree views and notebooks, and owns each webview panel's host-side class.
- **Webviews** (`src/panels/webview/*.ts`, `src/panels/styles/*.css`): browser code that renders a panel. It talks to its host class only by `postMessage`.
- **Daemon**: the `ppds serve` process from `src/PPDS.Cli`. `src/daemonClient.ts` spawns it and talks JSON-RPC over stdio. All Dataverse work happens in the daemon, which calls the same Application Services as the CLI, TUI and MCP server. The extension holds no business logic.

`DaemonClient` finds the `ppds` binary in this order:

1. `bin/ppds[.exe]` inside the extension (the bundled binary in marketplace builds, produced by `npm run bundle:cli`).
2. `../PPDS.Cli/bin/Debug/net8.0/ppds[.exe]`, the local debug build. It is shadow-copied to a temp folder first so `dotnet build` can overwrite the originals while the daemon is running.
3. `ppds` on `PATH` (the global dotnet tool).

So when you run the extension from source, it uses your local CLI debug build if one exists. Rebuild the CLI (`dotnet build src/PPDS.Cli/PPDS.Cli.csproj -f net8.0`) and run **PPDS: Restart Daemon** to pick up daemon changes.

## Build, run and debug

Run scripts from `src/PPDS.Extension/` (`npm run <script>`). Most also have a repository-root alias with the `ext:` prefix (`npm run ext:test` runs `npm run test` here): `compile`, `watch`, `package`, `lint`, `test`, `test:watch`, `test:e2e`, `local`, `local:install`, `local:uninstall`, `local:revert` and `release:test`. `lint:css`, `typecheck:all`, `test:notebook-host` and `dead-code` have no root alias; run them from `src/PPDS.Extension/`.

| Script | What it does |
|---|---|
| `compile` | Build all bundles into `dist/` with esbuild |
| `watch` | Rebuild on change |
| `package` | Production (minified) build |
| `lint` / `lint:css` | ESLint for `src`, Stylelint for `src/panels/styles` |
| `typecheck:all` | Type-check host (`tsconfig.json`), webview (`tsconfig.webview.json`) and E2E (`tsconfig.e2e.json`) code |
| `test` / `test:watch` | Vitest unit tests (`src/**/*.test.ts`, `tools/**/*.test.mjs`) |
| `test:e2e` | Playwright smoke tests in a real VS Code host; compiles first |
| `test:notebook-host` | Native notebook host tests (see `e2e/README.md`) |
| `dead-code` | knip unused-code check |
| `local` | Build a `-dev.N` versioned `.vsix` and install it into your VS Code; `package.json` is restored afterwards |
| `local:install` | Install the newest `.vsix` in this folder |
| `local:uninstall` / `local:revert` | Remove the local build / reinstall the marketplace version |
| `release:test` | Production build, package and install, as a release would |

Before opening a PR that touches the extension, run `lint`, `lint:css`, `typecheck:all` and `test`. CI runs `typecheck:all`, `lint`, `test` and the E2E suite; it does not run `lint:css`, so run that locally.

### Launch configurations (F5)

There are three sets, depending on what you open in VS Code:

| You open | Configurations |
|---|---|
| `ppds.code-workspace` (recommended) | `Extension: Run`, `Extension: Run (Watch Mode)`, `Extension: Run (No Build)`, `Extension: Tests`, `Extension: Open Folder`, `.NET: Debug Daemon`, and the compound `Full-Stack: Daemon + Extension` for debugging both sides of an RPC call |
| The repository root | `Run Extension` (runs the `Build All` task: CLI debug build, then `npm: compile`), `Run Extension (Watch Mode)`, `Run Extension (No Build)`, `Run Extension (Open Folder)`, `Run Extension Tests` |
| `src/PPDS.Extension/` | The same `Run Extension*` set, extension only (see `.vscode/README.md`) |

Use watch mode while iterating, then reload the Extension Development Host window to pick up changes.

### Where to look when something goes wrong

- **PPDS: Show Logs** opens the `PPDS` log channel. Daemon stderr is forwarded there with its log level.
- The **PPDS Debug** commands (`Daemon Status`, `Extension State`, `Tree View State`, `Panel State`) report internal state, including how many instances of each panel are open.
- Webview errors are caught by `installErrorHandler` and sent to the host as `webviewError`; the host logs them to the developer console and shows a notification. **Developer: Open Webview Developer Tools** inspects the webview DOM.
- `tools/webview-cdp.mjs` drives a VS Code instance over the Chrome DevTools Protocol, which is useful for scripted screenshots: `node tools/webview-cdp.mjs launch --build`, then `command "PPDS: Open Solutions"`, `screenshot <file>`, `eval '<js>' --ext power-platform-developer-suite`, `close`.

### Other surfaces

The TUI has its own snapshot tests: `npm run tui:test` and `npm run tui:test:update` from the repository root. .NET test commands are in [CONTRIBUTING.md](../../CONTRIBUTING.md).

## Rules that are not obvious from the code

1. **Webview scripts are external TypeScript files**, under `src/panels/webview/`, bundled by esbuild as browser IIFE bundles into `dist/`. VS Code silently drops inline `<script>` blocks larger than about 32KB, so inline scripts break without an error once a panel grows. The two accepted exceptions are tiny ones that do not grow: the Monaco worker URL setter in `QueryPanel.ts` and the per-cell notebook output script in `src/notebooks/virtualScrollScript.ts`.
2. **Panel CSS is in external files** under `src/panels/styles/`, each starting with `@import './shared.css';`. External CSS is linted by Stylelint and shared across panels. The CSP allows inline styles only because `@vscode/webview-ui-toolkit` needs them; existing templates use inline `style` for initial `display: none` and the reconnect banner, and new styling belongs in the CSS file.
3. **Every message is typed** with a discriminated union in `src/panels/webview/shared/message-types.ts`: `{Name}PanelWebviewToHost` and `{Name}PanelHostToWebview`. Both the host `handleMessage` switch and the webview `message` listener end with `default: assertNever(message)`. Adding a variant then fails to compile until both sides handle it, and removing one fails wherever it is still used.
4. **Panels never spawn processes or call Dataverse.** They call `DaemonClient` methods. If an operation needs new data, add an RPC method backed by an Application Service (see [Adding a daemon method](#adding-a-daemon-method)).
5. **Escape untrusted data** before it reaches `innerHTML`: use `escapeHtml` / `escapeAttr` (and `cssEscape` for selectors) from `shared/dom-utils.ts`. Record values come from Dataverse and must be treated as untrusted.
6. **Use `getNonce()`** from `webviewUtils.ts` for the CSP script nonce.

## Adding a panel

Copy an existing panel rather than starting from scratch. `EnvironmentVariablesPanel` is a compact data-table panel with a detail pane; `ImportJobsPanel` is a simpler table; `QueryPanel` (Data Explorer) is the most complex. Discover the current panels with `src/panels/*Panel.ts`, `src/panels/webview/*-panel.ts` and `src/panels/styles/*-panel.css`.

| File | Name | Example |
|---|---|---|
| Host class | `src/panels/{Name}Panel.ts` | `PluginTracesPanel.ts` |
| Webview entry | `src/panels/webview/{name}-panel.ts` | `plugin-traces-panel.ts` |
| Panel CSS | `src/panels/styles/{name}-panel.css` | `plugin-traces-panel.css` |
| Built output | `dist/{name}-panel.js`, `dist/{name}-panel.css` | `dist/plugin-traces-panel.js` |

Steps:

1. **Message types.** Add `{Name}PanelWebviewToHost` and `{Name}PanelHostToWebview` to `message-types.ts`. Every panel includes the shared commands the base class and shared components rely on: `ready`, `requestEnvironmentList`, `copyToClipboard` and `webviewError` from the webview; `updateEnvironment` and `daemonReconnected` from the host, plus `error` for failures (the base class posts it if initialization fails). The Data Explorer (`QueryPanel`) is the exception: it has no `error` variant and reports failures with `queryError`.
2. **Host class.** Extend `WebviewPanelBase<TIncoming, TOutgoing>`. In the constructor, create the `WebviewPanel` (with `localResourceRoots` for `dist` and `node_modules`), set `webview.html = this.getHtmlContent(...)`, then call `this.initPanel(panel)` and `this.subscribeToDaemonReconnect(this.daemon)`. Implement:
   - `panelLabel`: the title text. `updatePanelTitle` builds the full title as `profile · environment — Label`; do not set titles yourself.
   - `handleMessage(message)`: the typed switch. `ready` calls `this.initializePanel(daemon, panelId, multipleInstances)`; `requestEnvironmentList` calls `this.handleEnvironmentPickerClick(...)`; `copyToClipboard` and `webviewError` call `handleCopyToClipboard` and `logWebviewError`.
   - `onInitialized()` and `onEnvironmentChanged()`: load the panel's data.
   - `onDaemonReconnected()` (optional): re-fetch data.
   - `getHtmlContent(webview)`: HTML structure only, linking `dist/{name}-panel.css` and `dist/{name}-panel.js` with the nonce.

   Existing panels keep a static `instances` list with a `MAX_PANELS` cap, a static `show(...)` factory and a static `instanceCount` getter; follow the same shape.
3. **Webview script.** Get the API with `getVsCodeApi<{Name}PanelWebviewToHost>()`, call `installErrorHandler((msg) => vscode.postMessage(msg))`, add the typed `message` listener, and post `{ command: 'ready' }` last.
4. **CSS.** Create the panel CSS file starting with `@import './shared.css';`.
5. **esbuild.** Add two entries to the `builds` array in `esbuild.js`: the webview script (`format: 'iife'`, `platform: 'browser'`) and the CSS file. Copy an existing pair; nothing else in the file needs to change.
6. **Command.** Add the command to `contributes.commands` in `package.json` (category `PPDS`, title `Open {Name}`), and a `...ForEnv` variant in the `ppds.profiles` environment context menu if the panel can target an environment. Register both in `src/extension.ts`, and add the panel's `instanceCount` to the `registerDebugCommands` call so **PPDS Debug: Panel State** reports it.
7. **Check it.** Run the build and lint scripts above, then open the panel and check it in both light and dark themes. A passing type-check does not show that a panel renders correctly.

## Daemon lifecycle, cancellation and environment scoping

**Reconnect.** Every `DaemonClient` method calls `ensureConnected()`, which starts the daemon if it is not running. A heartbeat (`auth/list` every 30 seconds) kills the daemon after three consecutive failures; the next call restarts it and fires `onDidReconnect`. `subscribeToDaemonReconnect` turns that into a `daemonReconnected` message to the webview, where most panels show a "Connection restored. Data may be stale." banner, and calls `onDaemonReconnected()` so the panel can refresh. Data the panel loaded before the restart may be out of date, which is why both happen.

**Cancellation.** Long-running daemon methods take an optional `CancellationToken` from `vscode-jsonrpc` as their last argument. Keep one `CancellationTokenSource` per operation, cancel the previous one before starting a new call, and cancel and dispose it in `dispose()`. After the await, check that the token is still the current one before using the result, so a slower earlier call cannot overwrite a newer one. `QueryPanel.ts` (`queryCts`) shows the full pattern; `EnvironmentVariablesPanel.syncDeploymentSettings` shows linking the token to a cancellable progress notification and a timeout. `WebviewPanelBase.abortSignal` fires on dispose for work that is not an RPC call.

**Environment scoping.** Each panel targets its own environment, not the globally active one. The base class keeps `environmentUrl`, `profileName`, `environmentId`, `environmentType` and `environmentColor`. A panel opened from an environment's context menu starts with that environment; otherwise `initializePanel` uses the active profile's environment. Pass `this.environmentUrl` and `this.profileName` to every daemon call. The picker flow (`handleEnvironmentPickerClick`) updates this state, re-sends `updateEnvironment`, and calls `onEnvironmentChanged()`, where the panel resets environment-specific state (such as a solution filter) and reloads. `environmentId` is resolved for Maker portal links (`buildMakerUrl` in `src/commands/browserCommands.ts`).

## Adding a daemon method

1. Put the logic in an Application Service under `src/PPDS.Cli/Services/` if it does not exist yet. The service is the single code path for CLI, TUI, daemon and MCP.
2. Add a handler in the matching partial class file `src/PPDS.Cli/Commands/Serve/Handlers/RpcMethodHandler.{Domain}.cs`, decorated with `[JsonRpcMethod("{domain}/{operation}")]`. Handlers only map parameters, call the service (most use `WithProfileAndEnvironmentAsync(profileName, environmentUrl, ...)`), and map results to response DTOs. Accept a `CancellationToken`.
3. Add a typed method to `src/daemonClient.ts` that builds a params object, omitting undefined values, and calls `sendRequest`. Methods whose handler takes a single DTO parameter must use a `RequestType` with `ParameterStructures.byPosition` (see the `RPC_QUERY_*` constants): StreamJsonRpc reads a JSON object as separate named arguments, so a DTO-shaped handler needs positional encoding.

## Styling and theming

Every panel has a `.toolbar` at the top and a `.status-bar` at the bottom, both styled in `shared.css`, with the panel's content between them. Most list panels wrap that content in `<div class="content">`, styled in the panel's own CSS file (`shared.css` has no `.content` rule); `QueryPanel` and `MetadataBrowserPanel` use their own layouts. Use the shared `.empty-state`, `.error-state` and `.loading-state` classes rather than new ones.

**Toolbar order**, left to right:

```
Refresh | panel actions | Maker Portal | spacer | Search | filters/toggles | Sort | environment picker
```

The spacer is `<span class="toolbar-spacer"></span>`; search is `<input class="toolbar-search">`; checkbox toggles use `.toolbar-checkbox`; the environment picker is `${getEnvironmentPickerHtml()}` and is always last. These classes are defined in `shared.css`. Tool-type panels such as Data Explorer may put their primary action first instead of Refresh and leave out Search.

**Reusable patterns.** Check these before writing new CSS. Copy the rules into your panel's CSS; do not `@import` another panel's file.

| Pattern | Where |
|---|---|
| Sortable data table with selection and status badges | `shared/data-table.ts` (`DataTable<T>`), used by most list panels |
| Debounced text filter with count | `shared/filter-bar.ts` (`FilterBar<T>`), styles `.filter-bar` in `query-panel.css` |
| Solution picker with persisted choice | `shared/solution-filter.ts` (`SolutionFilter`) |
| Tree/list with expand chevrons, detail cards | `.solution-list`, `.detail-card`, `.component-detail-card` in `solutions-panel.css` |
| Dropdown and context menus | `.dropdown-menu`, `.context-menu` in `query-panel.css` |
| Detail pane below a table | `.detail-pane` in `environment-variables-panel.css`, `plugin-traces-panel.css` |
| Expandable detail row inside a table | `.cr-inline-detail` in `connection-references-panel.css` |

A detail pane sits under the table, is hidden until a row is selected, and closes with its close button or Escape.

**Values.** Use `var(--vscode-*)` theme tokens for colours, so panels follow the user's theme; a hex value is acceptable only as a token fallback. Most panel text is 12px (including the toolbar search and status bar), with 11px for labels, badges and table headers and 13px in a few places; panels use a 2px border radius on inputs and badges (4px on menus). Match them rather than adding new sizes.

**Environment accents.** `initializePanel` and the picker send `updateEnvironment` with `envType` and `envColor` (from the daemon's `env/config/get`). The webview sets lower-cased `data-env-type` and `data-env-color` attributes on `.toolbar`, and `shared.css` styles them:

- `data-env-type` (`production`, `sandbox`, `development`, `test`, `trial`) sets a 3px top border from theme tokens: red for production, yellow for sandbox and test, green for development, blue for trial.
- `data-env-color` sets a 4px left border from a fixed list of named colours (`red`, `green`, `yellow`, `cyan`, `blue`, `gray`, `brown`, `white` and the `bright*` variants), matching the colour names users configure for environments.

## Naming RPC methods and MCP tools

- **RPC methods**: `{domain}/{operation}`, with a camelCase domain: `connectionReferences/list`, `environmentVariables/syncDeploymentSettings`, `importJobs/get`. Sub-resources add a segment: `env/config/get`, `query/history/list`.
- **MCP tools** (`src/PPDS.Mcp/Tools/`): `ppds_{domain}_{operation}` in snake_case, set with `[McpServerTool(Name = "...")]`: `ppds_connection_references_list`, `ppds_auth_who`. MCP tools cover reads and queries and some writes (for example `ppds_metadata_create_table`, `ppds_environment_variables_set`, `ppds_forms_publish`); UI-only actions such as opening a browser or copying to the clipboard do not need one.
- **Daemon client methods**: the RPC name in camelCase: `environmentVariables/list` becomes `environmentVariablesList`.

## Panels and cross-panel consistency

| Panel | Command (palette title) | Host class |
|---|---|---|
| Data Explorer | PPDS: Open Data Explorer | `QueryPanel` |
| Solutions | PPDS: Open Solutions | `SolutionsPanel` |
| Import Jobs | PPDS: Open Import Jobs | `ImportJobsPanel` |
| Plugin Traces | PPDS: Open Plugin Traces | `PluginTracesPanel` |
| Metadata Browser | PPDS: Open Metadata Browser | `MetadataBrowserPanel` |
| Connection References | PPDS: Open Connection References | `ConnectionReferencesPanel` |
| Environment Variables | PPDS: Open Environment Variables | `EnvironmentVariablesPanel` |
| Web Resources | PPDS: Open Web Resources | `WebResourcesPanel` |
| Plugin Registration | PPDS: Open Plugin Registration | `PluginsPanel` |

Each panel also has a `...ForEnv` command (for example `ppds.openDataExplorerForEnv`) in the environment context menu of the Profiles view, which opens the panel scoped to that environment.

Users move between panels constantly, so differences read as bugs. When you add or change a panel, compare it with the others on:

- search box, solution filter, refresh button, export, and Maker Portal link
- sortable columns and selection/copy behaviour
- keyboard behaviour (for example, Escape closes a detail pane)
- loading indicator, empty and error states, and the stale-data banner after a daemon reconnect
- timestamp format (use `formatDate` / `formatDateTime` from `dom-utils.ts`)
- toolbar order and environment accents, as described above

Where a panel lacks a feature the others have, either add it or note why it does not apply. The equivalent TUI screen (`src/PPDS.Cli/Tui/Screens/`) should offer the same data, filters, sort options, export formats and drill-down paths; the two look different but call the same services.
