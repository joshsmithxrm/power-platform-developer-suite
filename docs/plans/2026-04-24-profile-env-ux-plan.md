# Implementation Plan: Profile/Environment UX v2.0

**Spec:** [specs/per-panel-environment-scoping.md](../../specs/per-panel-environment-scoping.md)
**Goal:** Per-panel profile+environment switching, unified status bar, grouped picker UX
**Date:** 2026-04-24
**Branch:** `feat/profile-env-ux`
**Expected HEAD:** `14f6f6a605e10d7dbf4d06f32dea72408c1f8102`
**Issues:** #887, #888, #903

---

## Architecture Summary

Three-layer implementation: (1) daemon RPC gains `profileName` parameter on `WithProfileAndEnvironmentAsync` and `EnvListAsync`, with profile-keyed discovery cache and device code attribution; (2) TypeScript `DaemonClient` threads `profileName` through all environment-scoped methods; (3) Extension UI replaces two status bar items with one `PpdsStatusBar`, replaces the environment-only picker with a grouped profile+environment picker, enhances the command palette profile list, and updates `WebviewPanelBase` to store per-panel `profileName`.

---

## File Map

### Create
- `src/PPDS.Extension/src/ppdsStatusBar.ts` — unified status bar item
- `src/PPDS.Extension/src/__tests__/ppdsStatusBar.test.ts` — unit tests

### Modify
- `src/PPDS.Cli/Commands/Serve/Handlers/RpcMethodHandler.cs` — `WithProfileAndEnvironmentAsync` gains `profileName`, `EnvListAsync` gains `profileName`, discovery cache keyed by profile, device code notification gains profile name
- `src/PPDS.Extension/src/daemonClient.ts` — all environment-scoped methods gain `profileName` parameter (positional, appended after `environmentUrl` for methods with positional params; added to params object for methods with object params), `envList` gains `profileName`
- `src/PPDS.Extension/src/panels/environmentPicker.ts` — rewrite as grouped profile+environment context picker
- `src/PPDS.Extension/src/panels/WebviewPanelBase.ts` — store `profileName` per panel, pass on RPC calls, update `initializePanel`, `handleEnvironmentPickerClick`, and `resolveEnvironmentId`
- `src/PPDS.Extension/src/panels/QueryPanel.ts` — update `requestEnvironmentList` handler to use `showContextPicker`, update `initEnvironment` to set `profileName`, pass `profileName` on all RPC calls
- `src/PPDS.Extension/src/panels/SolutionsPanel.ts` — pass `this.profileName` on all daemon RPC calls
- `src/PPDS.Extension/src/panels/ImportJobsPanel.ts` — pass `this.profileName` on all daemon RPC calls
- `src/PPDS.Extension/src/panels/PluginTracesPanel.ts` — pass `this.profileName` on all daemon RPC calls
- `src/PPDS.Extension/src/panels/ConnectionReferencesPanel.ts` — pass `this.profileName` on all daemon RPC calls
- `src/PPDS.Extension/src/panels/EnvironmentVariablesPanel.ts` — pass `this.profileName` on all daemon RPC calls
- `src/PPDS.Extension/src/panels/WebResourcesPanel.ts` — pass `this.profileName` on all daemon RPC calls
- `src/PPDS.Extension/src/panels/MetadataBrowserPanel.ts` — pass `this.profileName` on all daemon RPC calls
- `src/PPDS.Extension/src/panels/PluginsPanel.ts` — pass `this.profileName` on all daemon RPC calls
- `src/PPDS.Extension/src/commands/profileCommands.ts` — enhance `ppds.listProfiles` quick pick format
- `src/PPDS.Extension/src/extension.ts` — replace `DaemonStatusBar` + `ProfileStatusBar` instantiation with `PpdsStatusBar`
- `src/PPDS.Extension/src/__tests__/daemonClient.test.ts` — update tests for new `profileName` parameter

### Delete
- `src/PPDS.Extension/src/profileStatusBar.ts` — replaced by `ppdsStatusBar.ts`
- `src/PPDS.Extension/src/daemonStatusBar.ts` — replaced by `ppdsStatusBar.ts`

### Leave Alone
- `src/PPDS.Extension/src/notebooks/DataverseNotebookController.ts` — notebook status bar and `envList()` calls intentionally use active profile (no `profileName` needed)
- `src/PPDS.Extension/src/views/profileTreeView.ts` — tree view unchanged
- `src/PPDS.Extension/src/commands/browserCommands.ts` — `envList()` call intentionally uses active profile for environment discovery
- `src/PPDS.Extension/src/types.ts` — existing types sufficient (no new response types needed)

---

## Task Graph

```
Chunk 1: Daemon RPC (profileName + cache + device code)
    │
    ▼
Chunk 2: DaemonClient TypeScript (thread profileName)
    │
    ├──────────────┬──────────────┐
    ▼              ▼              ▼
Chunk 3:       Chunk 4:       Chunk 5:
Status Bar     Context        Command
(PpdsStatusBar) Picker        Palette
               + Panel Base
```

- Chunk 1 → Chunk 2 (sequential: TS client wraps C# RPC)
- Chunk 2 → Chunks 3, 4, 5 (parallel: all consume DaemonClient, touch disjoint files)

---

## Chunk 1: Daemon RPC — profileName Parameter + Discovery Cache + Device Code Attribution

**Depends-on:** None
**Parallel-safe-with:** None (foundation chunk)
**ACs covered:** AC-16, AC-17, AC-18, AC-19, AC-20, AC-21
**Files touched:** `src/PPDS.Cli/Commands/Serve/Handlers/RpcMethodHandler.cs`

### Task 1.1 — Add profileName to WithProfileAndEnvironmentAsync (ACs: AC-16, AC-17, AC-18)

**Files:** `src/PPDS.Cli/Commands/Serve/Handlers/RpcMethodHandler.cs`
**Depends-on:** None
**Mode:** alongside

- [x] Step 1: Read `RpcMethodHandler.cs` lines 3304-3370 to understand the existing `WithProfileAndEnvironmentAsync` overloads. There are two: the full 4-arg delegate overload (line 3308) and the convenience 2-arg delegate overload (line 3361). Both currently take `string? environmentUrl` as their first parameter.

- [x] Step 2: Add `string? profileName` as the first parameter to the full overload (line 3308). Update the profile resolution logic: if `profileName` is non-null/non-whitespace, call `collection.GetByNameOrIndex(profileName)` to load that profile. If null/whitespace, fall back to `collection.ActiveProfile` (existing behavior). Throw `RpcException(ErrorCodes.Auth.ProfileNotFound, $"Profile '{profileName}' not found")` if the named profile doesn't exist. Add `Auth.ProfileNotFound` to `ErrorCodes` if it doesn't exist.

- [x] Step 3: Add `string? profileName` as the first parameter to the convenience overload (line 3361). Forward to the full overload.

- [x] Step 4: Update ALL callers of `WithProfileAndEnvironmentAsync` to pass `profileName` as the first argument. Each RPC handler method (e.g., `QuerySqlAsync`, `SolutionsListAsync`) currently calls `WithProfileAndEnvironmentAsync(environmentUrl, ...)`. Update to `WithProfileAndEnvironmentAsync(profileName, environmentUrl, ...)`. The `profileName` value comes from the request DTO — add `string? ProfileName { get; set; }` with `[JsonPropertyName("profileName")]` to each request DTO that currently has `EnvironmentUrl`. Since all request DTOs follow the same pattern as `EnvironmentUrl`, this is a mechanical addition.

- [x] Step 5: Verify backward compatibility — when both `profileName` and `environmentUrl` are null/omitted, behavior is identical to current (uses active profile + saved environment). No existing callers break because all new parameters are optional.

- [x] Step 6: Commit: `feat(daemon): add profileName parameter to WithProfileAndEnvironmentAsync`

### Task 1.2 — Profile-keyed Discovery Cache for envList (ACs: AC-19, AC-20)

**Files:** `src/PPDS.Cli/Commands/Serve/Handlers/RpcMethodHandler.cs`
**Depends-on:** Task 1.1
**Mode:** alongside

- [x] Step 1: Read `RpcMethodHandler.cs` lines 71-76 to locate the existing discovery cache fields: `_discoveredEnvCache` (`List<EnvironmentInfo>?`) and `_discoveredEnvCacheExpiry` (`long`). Also read `EnvListAsync` (search for `[JsonRpcMethod("env/list")]`) to understand the current cache logic.

- [x] Step 2: Replace the two scalar cache fields with a `ConcurrentDictionary<string, (List<EnvironmentInfo> environments, long expiry)> _envCacheByProfile = new()`. The key is the profile's `Name ?? DisplayIdentifier` string (same key the pool manager uses).

- [x] Step 3: Add `string? profileName` parameter to `EnvListAsync`. Resolve the profile: if `profileName` provided, load via `collection.GetByNameOrIndex(profileName)`; else use `collection.ActiveProfile`. Use the resolved profile's name as the cache key.

- [x] Step 4: Update the cache read/write logic in `EnvListAsync` to use the profile-keyed dictionary instead of the scalar fields. Keep the same 5-minute TTL. The `Volatile.Read`/`Volatile.Write` pattern becomes `_envCacheByProfile.TryGetValue(key, out var cached)` for reads and `_envCacheByProfile[key] = (environments, expiry)` for writes (thread-safe via `ConcurrentDictionary`).

- [x] Step 5: Update the cache invalidation in `EnvSelectAsync` (currently `Volatile.Write(ref _discoveredEnvCache, null)`) to clear only the active profile's entry: `_envCacheByProfile.TryRemove(activeProfileKey, out _)`.

- [x] Step 6: Commit: `feat(daemon): profile-keyed discovery cache for envList`

### Task 1.3 — Device Code Attribution (ACs: AC-21)

**Files:** `src/PPDS.Cli/Commands/Serve/Handlers/RpcMethodHandler.cs`
**Depends-on:** Task 1.1
**Mode:** alongside

- [x] Step 1: Search for `DaemonDeviceCodeHandler` to find the device code callback creation. It's called at the `_poolManager.GetOrCreateServiceProviderAsync` callsite inside `WithProfileAndEnvironmentAsync`. The handler sends an `auth/deviceCode` notification via the RPC channel.

- [x] Step 2: Read the `DaemonDeviceCodeHandler` class (likely in `src/PPDS.Cli/Commands/Serve/Handlers/`). Find where it sends the notification — it should be an anonymous object or DTO with `userCode`, `verificationUrl`, `message` fields.

- [x] Step 3: Investigate pool caching behavior — read `DaemonConnectionPoolManager.GetOrCreateServiceProviderAsync` to determine whether the `deviceCodeCallback` is re-registered on every call or only on first pool creation. If the callback is set only at pool creation time, then a pool created before this change won't have `profileName` in its callback. In that case, pass `profileName` via the credential provider's re-auth flow (where the token refresh triggers a new device code challenge), not just the initial callback. If the callback IS re-registered per call, the simpler approach of passing `profileName` to `CreateCallback` is sufficient.

- [x] Step 4: Add `profileName` to the notification payload. The profile name is available in `WithProfileAndEnvironmentAsync` as `profile.Name ?? profile.DisplayIdentifier`. Pass it to `DaemonDeviceCodeHandler.CreateCallback` as a new `string? profileName` parameter, and include `ProfileName = profileName` in the notification object.

- [x] Step 5: Commit: `feat(daemon): include profileName in auth/deviceCode notifications`

---

## Chunk 2: DaemonClient TypeScript — Thread profileName

**Depends-on:** Chunk 1
**Parallel-safe-with:** None (bridge chunk between backend and UI)
**ACs covered:** AC-16, AC-17, AC-18, AC-19, AC-22
**Files touched:** `src/PPDS.Extension/src/daemonClient.ts`, `src/PPDS.Extension/src/__tests__/daemonClient.test.ts`

### Task 2.1 — Add profileName to DaemonClient Methods (ACs: AC-16, AC-17, AC-18, AC-19)

**Files:** `src/PPDS.Extension/src/daemonClient.ts`, `src/PPDS.Extension/src/__tests__/daemonClient.test.ts`
**Depends-on:** Chunk 1
**Mode:** alongside

- [x] Step 1: Read `daemonClient.ts` to understand the method signature patterns. There are two patterns in use:
  - **Positional params**: Methods like `solutionsList(filter?, includeManaged?, environmentUrl?, includeInternal?)` accept positional arguments but internally build a `params: Record<string, unknown>` object with `if (x !== undefined) params.x = x` guards before sending via `connection.sendRequest`.
  - **Object params**: Methods like `querySql(params: { sql: string; environmentUrl?: string; ... })` accept a single typed object.
  Both patterns ultimately send the same JSON-RPC payload. The approach for adding `profileName` differs by pattern type.

- [x] Step 2: For **positional-param methods**, add `profileName?: string` as the LAST parameter (after `environmentUrl`). Add `if (profileName !== undefined) params.profileName = profileName` in the method body alongside the existing `environmentUrl` guard. Methods to update: `solutionsList`, `solutionsComponents`, `importJobsList`, `importJobsGet`, `connectionReferencesList`, `connectionReferencesGet`, `connectionReferencesAnalyze`, `environmentVariablesList`, `environmentVariablesGet`, `environmentVariablesSet`, `environmentVariablesSyncDeploymentSettings`, `webResourcesList`, `webResourcesGet`, `webResourcesGetModifiedOn`, `webResourcesUpdate`, `webResourcesPublish`, `webResourcesPublishAll`, `pluginTracesList`, `pluginTracesGet`, `pluginTracesTimeline`, `pluginTracesDelete`, `pluginTracesTraceLevel`, `pluginTracesSetTraceLevel`, `pluginsList`, `pluginsGet`, `pluginsMessages`, `pluginsEntityAttributes`, `pluginsToggleStep`, `pluginsRegisterAssembly`, `pluginsRegisterPackage`, `pluginsRegisterStep`, `pluginsRegisterImage`, `pluginsUpdateStep`, `pluginsUpdateImage`, `pluginsUnregister`, `pluginsDownloadBinary`, `serviceEndpointsList`, `serviceEndpointsGet`, `serviceEndpointsRegister`, `serviceEndpointsUpdate`, `serviceEndpointsUnregister`, `customApisList`, `customApisGet`, `customApisRegister`, `customApisUpdate`, `customApisUnregister`, `customApisAddParameter`, `customApisUpdateParameter`, `customApisRemoveParameter`, `dataProvidersList`, `dataProvidersGet`, `dataProvidersRegister`, `dataProvidersUpdate`, `dataProvidersUnregister`, `dataSourcesList`, `dataSourcesGet`, `dataSourcesRegister`, `dataSourcesUpdate`, `dataSourcesUnregister`, `metadataEntities`, `metadataGlobalOptionSets`, `metadataGlobalOptionSet`, `metadataEntity`, `metadataCreateTable`, `metadataUpdateTable`, `metadataDeleteTable`, `metadataCreateColumn`, `metadataUpdateColumn`, `metadataDeleteColumn`, `metadataCreateOneToMany`, `metadataCreateManyToMany`, `metadataDeleteRelationship`, `metadataCreateGlobalChoice`, `metadataDeleteGlobalChoice`, `metadataCreateKey`, `metadataDeleteKey`, `queryComplete`, `queryHistoryList`, `queryHistoryDelete`.

- [x] Step 2b: For **object-param methods**, add `profileName?: string` to the params object type. Methods to update: `querySql`, `queryFetch`, `queryExport`, `queryExplain`.

- [x] Step 3: Add `profileName?: string` to `envList` as a third positional parameter: `envList(filter?: string, forceRefresh?: boolean, profileName?: string)`. Add `if (profileName !== undefined) params.profileName = profileName` guard in the method body. This keeps the existing positional signature compatible — no callers break. Callers that need `profileName` pass it explicitly as the third arg (e.g., `daemon.envList(undefined, undefined, this.profileName)`) or, for readability at call sites, a convenience overload pattern can be used.

- [x] Step 4: Update the `onDeviceCode` handler type to include `profileName: string` in the callback payload. Update any existing tests.

- [x] Step 5: Run `npm run ext:test` to verify no regressions.

- [x] Step 6: Commit: `feat(extension): add profileName parameter to all DaemonClient environment-scoped methods`

---

## Chunk 3: Unified Status Bar (PpdsStatusBar)

**Depends-on:** Chunk 2
**Parallel-safe-with:** Chunk 4, Chunk 5
**ACs covered:** AC-01, AC-02, AC-03, AC-04, AC-05, AC-06, AC-06a, AC-07, AC-08, AC-09
**Files touched:** `src/PPDS.Extension/src/ppdsStatusBar.ts` (create), `src/PPDS.Extension/src/extension.ts`, `src/PPDS.Extension/src/profileStatusBar.ts` (delete), `src/PPDS.Extension/src/daemonStatusBar.ts` (delete), `src/PPDS.Extension/src/__tests__/ppdsStatusBar.test.ts` (create)

### Task 3.1 — Create PpdsStatusBar (ACs: AC-01, AC-02, AC-03, AC-04, AC-05, AC-06, AC-06a, AC-07, AC-08, AC-09)

**Files:** `src/PPDS.Extension/src/ppdsStatusBar.ts`, `src/PPDS.Extension/src/__tests__/ppdsStatusBar.test.ts`
**Depends-on:** Chunk 2
**Mode:** alongside

- [x] Step 1: Create `src/PPDS.Extension/src/ppdsStatusBar.ts`. Export class `PpdsStatusBar implements vscode.Disposable`. Constructor takes `DaemonClient`. Creates a single `vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Left, 50)`.

- [x] Step 2: Implement state-dependent display. Subscribe to `client.onDidChangeState` and `client.onDidReconnect`. State transitions:
  - `ready`: fetch profile via `client.authList()` with 10s timeout. Set text to `$(check) PPDS: {name} · {envDisplayName}` if profile has env, `$(check) PPDS: {name}` if no env, `$(check) PPDS: No profile` if no active profile. Set command to `ppds.listProfiles`. Clear error background.
  - `starting` / `reconnecting`: text `$(sync~spin) PPDS`, tooltip `PPDS Daemon: Starting/Reconnecting...`, command `ppds.restartDaemon`, clear error background.
  - `error`: text `$(error) PPDS`, tooltip `PPDS Daemon: Disconnected — click to restart`, command `ppds.restartDaemon`, set `statusBarItem.backgroundColor = new vscode.ThemeColor('statusBarItem.errorBackground')`.
  - `stopped`: text `$(circle-slash) PPDS`, tooltip `PPDS Daemon: Stopped`, command `ppds.restartDaemon`, clear error background.

- [x] Step 3: Implement rich tooltip for ready state. Set `statusBarItem.tooltip` to a `vscode.MarkdownString` with profile name, environment name, auth method, and daemon state. For non-ready states, use plain string tooltips.

- [x] Step 4: Expose a `refresh()` method (public) so external callers (e.g., profile commands after switching) can trigger a re-fetch. Follow same pattern as existing `ProfileStatusBar.refresh()`.

- [x] Step 5: Implement `dispose()` — dispose all subscriptions and the status bar item. Follow `R1` and `R3` from constitution.

- [x] Step 6: Write unit tests in `src/PPDS.Extension/src/__tests__/ppdsStatusBar.test.ts`. Test each state transition (ready with profile+env, ready with profile only, ready with no profile, starting, reconnecting, error, stopped). Test click command varies by state. Test refresh fetches profile. Mock `DaemonClient` following existing test patterns.

- [x] Step 7: Run `npm run ext:test` to verify tests pass.

- [x] Step 8: Commit: `feat(extension): create unified PpdsStatusBar replacing DaemonStatusBar + ProfileStatusBar`

### Task 3.2 — Wire PpdsStatusBar in Extension Activation (ACs: AC-01)

**Files:** `src/PPDS.Extension/src/extension.ts`
**Depends-on:** Task 3.1
**Mode:** alongside

- [ ] Step 1: Read `extension.ts` lines 195-203 where `DaemonStatusBar` and `ProfileStatusBar` are instantiated.

- [ ] Step 2: Replace both instantiations with a single `const statusBar = new PpdsStatusBar(client)`. Update the import to use `PpdsStatusBar` from `./ppdsStatusBar.js`. Remove imports for `DaemonStatusBar` and `ProfileStatusBar`.

- [ ] Step 3: Delete `src/PPDS.Extension/src/profileStatusBar.ts` and `src/PPDS.Extension/src/daemonStatusBar.ts`.

- [ ] Step 4: Search for any other references to the deleted files (`profileStatusBar`, `daemonStatusBar`, `ProfileStatusBar`, `DaemonStatusBar`) in the codebase. Update or remove. Check test files — the smoke test at `src/PPDS.Extension/src/__tests__/integration/smokeTest.test.ts` may mock `createStatusBarItem`.

- [ ] Step 5: If there are callers that call `profileStatusBar.refresh()` (e.g., after profile switch in `profileCommands.ts`), update them to call the new `PpdsStatusBar.refresh()`. The status bar instance may need to be passed to `registerProfileCommands` or made accessible via a module-level reference.

- [ ] Step 6: Run `npm run ext:test` to verify no regressions.

- [ ] Step 7: Commit: `refactor(extension): wire PpdsStatusBar, delete DaemonStatusBar and ProfileStatusBar`

---

## Chunk 4: Grouped Context Picker + WebviewPanelBase

**Depends-on:** Chunk 2
**Parallel-safe-with:** Chunk 3, Chunk 5
**ACs covered:** AC-12, AC-13, AC-14, AC-15, AC-23, AC-24, AC-25, AC-26, AC-27, AC-28, AC-29
**Files touched:** `src/PPDS.Extension/src/panels/environmentPicker.ts`, `src/PPDS.Extension/src/panels/WebviewPanelBase.ts`, `src/PPDS.Extension/src/panels/QueryPanel.ts`, `src/PPDS.Extension/src/panels/SolutionsPanel.ts`, `src/PPDS.Extension/src/panels/ImportJobsPanel.ts`, `src/PPDS.Extension/src/panels/PluginTracesPanel.ts`, `src/PPDS.Extension/src/panels/ConnectionReferencesPanel.ts`, `src/PPDS.Extension/src/panels/EnvironmentVariablesPanel.ts`, `src/PPDS.Extension/src/panels/WebResourcesPanel.ts`, `src/PPDS.Extension/src/panels/MetadataBrowserPanel.ts`, `src/PPDS.Extension/src/panels/PluginsPanel.ts`

### Task 4.1 — Rewrite environmentPicker as Grouped Context Picker (ACs: AC-13, AC-26, AC-27, AC-28)

**Files:** `src/PPDS.Extension/src/panels/environmentPicker.ts`
**Depends-on:** Chunk 2
**Mode:** alongside

- [x] Step 1: Read current `environmentPicker.ts` to understand the existing `showEnvironmentPicker` function signature and return type. It returns `{ url: string; displayName: string; type: string | null } | undefined`.

- [x] Step 2: Create a new exported function `showContextPicker(daemon: DaemonClient, currentProfileName?: string, currentUrl?: string)` that returns `Promise<{ profileName: string; url: string; displayName: string; type: string | null } | undefined>`. This replaces `showEnvironmentPicker` with profile awareness.

- [x] Step 3: Implement `showContextPicker`:
  a. Call `daemon.authList()` to get all profiles.
  b. For each profile, call `daemon.envList(undefined, undefined, p.name ?? p.index.toString())` in parallel using `Promise.allSettled`. If a call fails, fall back to the profile's saved environment from `authList` response.
  c. Build `QuickPickItem[]` array: for each profile, add a `{ label: profileName + (isActive ? ' (active)' : ''), kind: vscode.QuickPickItemKind.Separator }` entry, then for each environment under that profile add `{ label: isCurrent ? '$(check) ' + friendlyName : friendlyName, description: isCurrent ? 'current' : undefined, detail: env.apiUrl + (env.region ? ' (' + env.region + ')' : ''), profileName, url: env.apiUrl, displayName: env.friendlyName, type: env.type ?? null }`.
  d. Add final `{ label: '$(link) Enter URL manually...', description: '', detail: 'Connect to an environment not in the list', profileName: '__manual__', url: '__manual__', displayName: '', type: null }`.
  e. Show quick pick with `title: 'Select Profile & Environment'`, `matchOnDetail: true`.
  f. If `__manual__` selected, show input box for URL (reuse existing validation logic), then ask which profile to use with that URL (secondary quick pick of profile names).
  g. Return `{ profileName, url, displayName, type }`.

- [x] Step 4: Update `getEnvironmentPickerHtml()` to show `ProfileName · EnvName` instead of just the environment name. Change the button text template: `<span id="env-picker-name">Loading...</span>` is updated by the webview when it receives `updateEnvironment` — this already works, just the label format changes in the host.

- [x] Step 5: Keep `showEnvironmentPicker` as a deprecated wrapper that calls `showContextPicker` and strips `profileName` from the result, for any callers that haven't been updated yet. Mark with `@deprecated`.

- [x] Step 6: Commit: `feat(extension): grouped profile+environment context picker replacing environment-only picker`

### Task 4.2 — Update WebviewPanelBase for Per-Panel Profile (ACs: AC-12, AC-14, AC-15, AC-23, AC-24, AC-25, AC-29)

**Files:** `src/PPDS.Extension/src/panels/WebviewPanelBase.ts`
**Depends-on:** Task 4.1
**Mode:** alongside

- [x] Step 1: Read `WebviewPanelBase.ts` lines 29-35 (shared environment state) and lines 156-226 (initializePanel + handleEnvironmentPickerClick).

- [x] Step 2: The `profileName` field already exists (line 35). Verify it's being used correctly. Update `initializePanel` (line 156): instead of calling `daemon.authWho()` (which always returns the active profile), call `daemon.authList()` and find the active profile to set `this.profileName`. This ensures the profile name is set from the list response, which includes all profile metadata.

- [x] Step 3: Update `handleEnvironmentPickerClick` (line 201): replace `showEnvironmentPicker(daemon, this.environmentUrl)` with `showContextPicker(daemon, this.profileName, this.environmentUrl)`. On result, update both `this.profileName` and `this.environmentUrl`. Update import.

- [x] Step 4: Update `updatePanelTitle` — it already uses `this.profileName` in the title format `profileName · envName — PanelLabel`. No change needed here, but verify the title updates correctly after a context picker switch.

- [x] Step 5: Update the `updateEnvironment` message posted to the webview (line 184-189) to include `profileName` so the webview can display it in the context picker button text. Add `profileName: this.profileName` to the message payload. Update the shared message type in `src/PPDS.Extension/src/panels/webview/shared/message-types.ts` if needed.

- [x] Step 6: Update `resolveEnvironmentId` (line 122): change `await daemon.envList()` to `await daemon.envList(undefined, undefined, this.profileName)` so environment ID resolution uses the panel's profile, not the active profile's discovery cache.

- [x] Step 7: **QueryPanel-specific updates** (`QueryPanel.ts`). QueryPanel bypasses the base class `initializePanel`/`handleEnvironmentPickerClick` with its own flows.

- [x] Step 8: **Standard panel subclass updates** — for each panel that uses `handleEnvironmentPickerClick` from the base class: verify no individual changes needed (base class handles picker+init). Then update each panel's `handleMessage` and data-loading methods to pass `this.profileName` alongside `this.environmentUrl` on daemon RPC calls.

- [x] Step 9: Run `npm run ext:test` to verify no regressions.

- [x] Step 10: Commit: `feat(extension): per-panel profile binding in WebviewPanelBase and all panel subclasses`

---

## Chunk 5: Enhanced Command Palette Profile Picker

**Depends-on:** Chunk 2
**Parallel-safe-with:** Chunk 3, Chunk 4
**ACs covered:** AC-10, AC-11, AC-11a
**Files touched:** `src/PPDS.Extension/src/commands/profileCommands.ts`

### Task 5.1 — Enhance ppds.listProfiles Quick Pick (ACs: AC-10, AC-11, AC-11a)

**Files:** `src/PPDS.Extension/src/commands/profileCommands.ts`
**Depends-on:** Chunk 2
**Mode:** alongside

- [x] Step 1: Read `profileCommands.ts` lines 68-119 — the `ppds.listProfiles` command handler. Current format: `label` = profile name, `description` = identity, `detail` = `envName (authMethod)`, `picked` = isActive.

- [x] Step 2: Update the quick pick item format:
  - `label`: `$(check) {name}` for active profile, `{name}` for inactive (replace `picked: p.isActive` with `$(check)` prefix)
  - `description`: `{environment.displayName}` (environment name, more prominent position)
  - `detail`: `{identity} · {authMethod}` (identity + auth method in detail line)
  - Remove `picked: p.isActive` (the `$(check)` prefix replaces it visually)

- [x] Step 3: Update the placeholder text from `Active: {activeProfile}` to `Select a profile to switch`.

- [x] Step 4: Verify the profile switch logic after selection still works (lines 103-118). No change needed — it uses `selected.profile` which is the same `ProfileInfo` object.

- [x] Step 5: After a successful profile switch, call `statusBar.refresh()` if the `PpdsStatusBar` instance is accessible. Check how the refresh callback is wired — the existing code calls `refreshProfiles()` which refreshes the tree view. The status bar refresh may need to be added to this callback chain. **Done in Chunk 3** — `extension.ts` `registerProfileCommands(...)` callback now invokes `statusBar.refresh()`.

- [x] Step 6: Run `npm run ext:test` to verify no regressions.

- [x] Step 7: Commit: `feat(extension): enhance command palette profile picker with $(check) prefix and prominent environment display`

---

## Verification Matrix

| Spec AC | Task | Test File / Method |
|---------|------|--------------------|
| AC-01 | Task 3.1, Task 3.2 | `ppdsStatusBar.test.ts` — single item created, old items deleted |
| AC-02 | Task 3.1 | `ppdsStatusBar.test.ts` — ready state with profile+env |
| AC-03 | Task 3.1 | `ppdsStatusBar.test.ts` — ready state, profile without env |
| AC-04 | Task 3.1 | `ppdsStatusBar.test.ts` — ready state, no active profile |
| AC-05 | Task 3.1 | `ppdsStatusBar.test.ts` — starting/reconnecting states |
| AC-06 | Task 3.1 | `ppdsStatusBar.test.ts` — error state with error background |
| AC-06a | Task 3.1 | `ppdsStatusBar.test.ts` — stopped state |
| AC-07 | Task 3.1 | `ppdsStatusBar.test.ts` — command is listProfiles in ready state |
| AC-08 | Task 3.1 | `ppdsStatusBar.test.ts` — command is restartDaemon in error/stopped |
| AC-09 | Task 3.1 | `ppdsStatusBar.test.ts` — tooltip contains profile, env, auth method |
| AC-10 | Task 5.1 | Manual — visual verification of $(check) prefix |
| AC-11 | Task 5.1 | Manual — environment name in description field |
| AC-11a | Task 5.1 | Manual — identity in detail field |
| AC-12 | Task 4.2 | Manual — context picker button shows ProfileName · EnvName |
| AC-13 | Task 4.1 | Manual — grouped quick pick with profile separators |
| AC-14 | Task 4.2 | Manual — selecting sets both profileName and environmentUrl |
| AC-15 | Task 4.2 | Manual — two panels with different profiles |
| AC-16 | Task 1.1, Task 2.1 | `daemonClient.test.ts` — profileName threaded to RPC |
| AC-17 | Task 1.1, Task 2.1 | Existing tests — environmentUrl still works |
| AC-18 | Task 1.1, Task 2.1 | `daemonClient.test.ts` — omitting both falls back to active profile |
| AC-19 | Task 1.2, Task 2.1 | `daemonClient.test.ts` — envList accepts profileName |
| AC-20 | Task 1.2 | Code review — ConcurrentDictionary keyed by profile |
| AC-21 | Task 1.3 | Code review — deviceCode notification includes profileName |
| AC-22 | Task 2.1 | Manual — notification shows profile name |
| AC-23 | Task 4.2 | Manual — new panel defaults to active profile |
| AC-24 | Task 4.2 | Manual — global switch doesn't affect open panels |
| AC-25 | Task 4.2 | Manual — panel title shows panel's own profile |
| AC-26 | Task 4.1 | Manual — SPN profile shows sparse env list |
| AC-27 | Task 4.1 | Manual — SPN auth failure shows clear error |
| AC-28 | Task 4.1 | Manual — "Enter URL manually..." option present |
| AC-29 | Task 4.2 | Manual — panel override doesn't change global state |

---

## Verification Steps

### VS-01: Unified status bar displays correctly across all daemon states
- [x] Mechanical: `npm run ext:test -- --grep "PpdsStatusBar"` passes
- [ ] Manual: Start extension, verify status bar shows `$(check) PPDS: ProfileName · EnvName`
- [ ] Manual: Stop daemon, verify status bar shows `$(error) PPDS`, click restarts daemon

### VS-02: Per-panel profile+environment switching works end-to-end
- [ ] Manual: Open Data Explorer, verify context picker shows `ProfileName · EnvName`
- [ ] Manual: Click context picker, verify grouped quick pick with profile separators
- [ ] Manual: Select environment under different profile, verify panel title updates and data loads
- [ ] Manual: Open second panel, select different profile, verify both panels work independently

### VS-03: Command palette profile picker enhanced
- [ ] Manual: Open command palette, run `PPDS: List Profiles`
- [ ] Manual: Verify active profile has `$(check)` prefix, environment in description, identity in detail

### VS-04: Backward compatibility preserved
- [x] Mechanical: `npm run ext:test` passes (all existing tests)
- [x] Mechanical: `dotnet test PPDS.sln --filter "Category!=Integration" -v q` passes

### VS-05: Device code attribution
- [ ] Manual: With expired token, trigger re-auth from a panel using a non-active profile
- [ ] Manual: Verify notification identifies the profile name

---

## Rollback Notes

- **Chunk 1**: Revert the C# changes — remove `profileName` from `WithProfileAndEnvironmentAsync` and `EnvListAsync`, restore scalar discovery cache. All callers continue to work with `null` profileName.
- **Chunk 2**: Revert `daemonClient.ts` — remove `profileName` from method signatures. Callers don't pass it.
- **Chunk 3**: Restore `DaemonStatusBar` and `ProfileStatusBar` from git, delete `PpdsStatusBar`, update `extension.ts` imports.
- **Chunk 4**: Restore `environmentPicker.ts` and `WebviewPanelBase.ts` from git. Revert `profileName` additions in all panel subclass RPC call sites: `QueryPanel.ts`, `SolutionsPanel.ts`, `ImportJobsPanel.ts`, `PluginTracesPanel.ts`, `ConnectionReferencesPanel.ts`, `EnvironmentVariablesPanel.ts`, `WebResourcesPanel.ts`, `MetadataBrowserPanel.ts`, `PluginsPanel.ts`. Panels go back to environment-only picking. Safe to revert independently — Chunk 2's `profileName` params become unused but optional.
- **Chunk 5**: Revert `profileCommands.ts` quick pick format changes.

Each chunk is independently revertable via `git revert` of its commits.

---

## Open Questions and Assumptions

1. **Assumption:** `ErrorCodes.Auth.ProfileNotFound` does not exist yet and needs to be added. If it already exists under a different name, use the existing code.
2. **Assumption:** The `DaemonDeviceCodeHandler` class is in `src/PPDS.Cli/Commands/Serve/Handlers/` and its notification payload is a simple anonymous object or DTO that can be extended with `profileName`. Task 1.3 Step 3 investigates whether the callback is re-registered per call or only at pool creation — the approach may differ based on the finding.
3. **Verified:** Panel subclasses all pass `environmentUrl` via `this.environmentUrl` in their RPC calls (not hardcoded or from other sources). Confirmed by inspection of all 9 panel files. Note: `QueryPanel` uses `this.environmentUrl` correctly but has its own init/picker flow (addressed explicitly in Task 4.2 Steps 7a-7c).

- [FLAKY: PPDS.Cli.Tests.Commands.Serve.Handlers.RpcMethodHandlerPathConstraintTests.ResolveWorkspacePath_DotDotEscape_Throws] (detected 2026-04-24, task Task 1.1, net9.0 only — passed on rerun)
