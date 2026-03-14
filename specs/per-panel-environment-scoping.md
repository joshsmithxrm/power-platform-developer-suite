# Per-Panel Environment Scoping

**Status:** Draft
**Version:** 1.0
**Last Updated:** 2026-03-13
**Code:** [extension/src/](../extension/src/), [src/PPDS.Cli/Commands/Serve/Handlers/](../src/PPDS.Cli/Commands/Serve/Handlers/), [src/PPDS.Cli/Tui/](../src/PPDS.Cli/Tui/)

---

## Overview

Per-panel environment scoping replaces the global active-environment model with panel-level environment targeting. Each Data Explorer and Solutions panel has its own environment picker in the webview header, enabling side-by-side comparison of environments. The daemon RPC methods gain an optional `environmentUrl` parameter so panels can query any environment without changing the profile's saved environment. A related TUI fix removes the broken `CloseAllTabs` behavior on profile switch.

### Goals

- **Per-panel isolation**: Each Data Explorer and Solutions panel targets a specific environment, independent of other panels
- **Side-by-side comparison**: Open the same panel type against multiple environments simultaneously
- **Profile-centric model**: Profile is the top-level identity concept (auth identity + saved environment). Environment selection happens at the panel level, defaulting to the profile's saved environment
- **Backward-compatible daemon API**: The `environmentUrl` parameter is optional; omitting it falls back to the active profile's saved environment
- **Cross-interface consistency**: TUI tabs and VS Code panels follow the same model — panels/tabs persist across profile and environment switches

### Non-Goals

- Changing the notebook environment model (notebooks already have per-file environment metadata — correct as-is)
- Multi-profile panels (each panel uses the active profile's credentials; only the target environment varies)
- Rewriting the webview UI framework (we enhance the existing `WebviewPanelBase` + inline HTML pattern)
- Status bar environment or profile selectors (panels and sidebar handle everything)
- Per-panel profile selection (profile is session-level, managed in the sidebar)

---

## Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│                     VS Code Extension                            │
│                                                                  │
│  Sidebar                                                        │
│  ┌──────────────┐  ┌──────────────┐                             │
│  │ Profile Tree │  │  Tools Tree  │                             │
│  │  (CRUD +     │  │  (opens      │                             │
│  │   select)    │  │   panels)    │                             │
│  └──────────────┘  └──────┬───────┘                             │
│                           │ opens                               │
│            ┌──────────────┴──────────────┐                      │
│            ▼                             ▼                      │
│  ┌───────────────────┐     ┌───────────────────┐               │
│  │  Data Explorer    │     │  Solutions Panel   │               │
│  │  ┌─────────────┐  │     │  ┌─────────────┐  │               │
│  │  │[Contoso ▾]  │  │     │  │[Contoso ▾]  │  │               │
│  │  │ env picker  │  │     │  │ env picker  │  │               │
│  │  └──────┬──────┘  │     │  └──────┬──────┘  │               │
│  │         │         │     │         │         │               │
│  │  environmentUrl   │     │  environmentUrl   │               │
│  │  per RPC call     │     │  per RPC call     │               │
│  └─────────┬─────────┘     └─────────┬─────────┘               │
│            │                         │                          │
│  ┌─────────┴─────────────────────────┴─────────┐               │
│  │              Notebooks (.ppdsnb)             │               │
│  │         per-file env in metadata             │               │
│  │              (unchanged)                     │               │
│  └─────────────────────────────────────────────┘               │
└────────────┬─────────────────────────┬──────────────────────────┘
             │  JSON-RPC (stdio)       │
             ▼                         ▼
┌─────────────────────────────────────────────────────────────────┐
│                     ppds serve daemon                            │
│                                                                  │
│  RpcMethodHandler                                               │
│  ┌────────────────────────────────────────────────────────────┐ │
│  │ WithProfileAndEnvironmentAsync(environmentUrl?)            │ │
│  │   if environmentUrl provided → use it                      │ │
│  │   else → fall back to active profile's saved environment   │ │
│  └────────────────────────┬───────────────────────────────────┘ │
│                           │                                      │
│  DaemonConnectionPoolManager (unchanged)                        │
│  ┌────────────────────────┴───────────────────────────────────┐ │
│  │ ConcurrentDictionary<profileNames+envUrl, Pool>            │ │
│  │   Pool(dev.crm.dynamics.com)  ←── reused across panels    │ │
│  │   Pool(test.crm.dynamics.com) ←── reused across panels    │ │
│  └────────────────────────────────────────────────────────────┘ │
└─────────────────────────────────────────────────────────────────┘
```

### Components

| Component | Responsibility |
|-----------|----------------|
| `QueryPanel` (enhanced) | Data Explorer webview with environment picker in header; passes `environmentUrl` on every RPC call |
| `SolutionsPanel` (new) | Solutions webview panel replacing `SolutionsTreeDataProvider`; environment picker + hierarchical solution/component browsing |
| Environment picker helper | Shared function generating env picker HTML/JS for webview headers — fetches env list, renders dropdown, handles `environmentChanged` messages |
| `DaemonClient` (enhanced) | Query and solutions RPC methods gain optional `environmentUrl` parameter |
| `RpcMethodHandler` (enhanced) | New `WithProfileAndEnvironmentAsync` overload that accepts explicit `environmentUrl`, falling back to active profile's saved environment |
| `DaemonConnectionPoolManager` | Unchanged — already caches pools per profile+environment combination |

### Dependencies

- Depends on: [architecture.md](./architecture.md) (Application Services, error handling, DI)
- Depends on: [authentication.md](./authentication.md) (profile model, `AuthProfile.Environment`)
- Depends on: [connection-pooling.md](./connection-pooling.md) (`DaemonConnectionPoolManager` multi-env caching)

---

## Specification

### Core Requirements

1. **Per-call environment targeting**: Query RPC methods (`query/sql`, `query/fetch`, `query/export`, `query/explain`) and solutions RPC methods (`solutions/list`, `solutions/components`) accept an optional `environmentUrl` parameter. When provided, the daemon uses it instead of the active profile's saved environment.

2. **Environment picker in webview panels**: Data Explorer and Solutions panels display an environment dropdown in the webview header. Defaults to the active profile's saved environment. User can override to any discovered environment or enter a URL manually.

3. **Remove global environment selector**: The status bar environment item and associated commands (`ppds.selectEnvironment`, `ppds.environmentDetails`, `ppds.refreshEnvironments`) are removed. The `ppds.showEnvironmentInStatusBar` setting is removed.

4. **Solutions becomes a webview panel**: Replace `SolutionsTreeDataProvider` (sidebar tree view) with `SolutionsPanel` (webview panel like QueryPanel). Has its own environment picker, hierarchical solution/component browsing, and managed/unmanaged toggle.

5. **Sidebar simplification**: The `ppds` view container retains two views: `ppds.profiles` and `ppds.tools`. The `ppds.solutions` view is removed.

6. **Panel title includes environment**: Panel titles include the target environment for disambiguation, e.g., `Data Explorer #1 — Contoso Dev`.

7. **Legacy state cleanup**: On activation, run `migrateLegacyState()` once to clear the archived extension's `power-platform-dev-suite-environments` globalState key and any `panel-state-*` workspaceState keys. Gate with `ppds.legacyStateCleaned` globalState flag.

8. **TUI: Remove CloseAllTabs on profile switch**: Remove `CloseAllTabs()` calls from `TuiShell.ShowProfileSelector()` and `TuiShell.ShowProfileCreation()`. Remove the `TuiShell.CloseAllTabs()` private method and `TabManager.CloseAllTabs()` public method. Tabs persist across profile switches — if credentials can't reach a tab's environment, it errors on next query.

### Primary Flows

**Panel opens with default environment:**

1. User clicks "Data Explorer" or "Solutions" in Tools tree view
2. Panel creates, calls `daemon.authWho()` to get active profile's saved environment
3. Environment picker pre-selects that environment
4. All RPC calls include that environment URL

**User overrides environment in panel:**

1. User clicks environment picker dropdown in panel header
2. Panel calls `daemon.envList()` to populate options (+ "Enter URL manually...")
3. User selects a different environment
4. Panel stores the new `environmentUrl` in instance state
5. All subsequent RPC calls from that panel include the overridden `environmentUrl`
6. Panel title updates to reflect new environment name

**Daemon resolves environment on RPC call:**

1. RPC method receives request with optional `environmentUrl`
2. `WithProfileAndEnvironmentAsync`: if `environmentUrl` provided, use it with the active profile's credentials; else fall back to active profile's saved environment
3. `DaemonConnectionPoolManager.GetOrCreatePoolAsync` caches/reuses pool for that profile+environment combination
4. Query/solution operation executes against the resolved environment

**Profile switch (sidebar):**

1. User clicks a different profile in the sidebar profile tree view
2. Active profile changes (persisted to `profiles.json`)
3. Open panels remain open — they retain their environment URL
4. Next RPC call from any panel uses the new profile's credentials with that panel's environment URL
5. If credentials can't reach the environment, the panel shows an auth error with re-authenticate option

### Constraints

- A panel's environment override is ephemeral — not persisted. Closing and reopening defaults back to the active profile's saved environment.
- Environment override does not change the profile's saved environment. It is a panel-local concept.
- The active profile's auth identity (credentials) is always used. Panels cannot override the auth identity, only the target environment.
- Multiple panels can target the same environment simultaneously — pool reuse handles this efficiently.

### Validation Rules

| Field | Rule | Error |
|-------|------|-------|
| `environmentUrl` (RPC) | Must be a valid URL if provided, or omitted entirely | `Validation.InvalidValue` |
| `environmentUrl` (RPC) | Must be reachable with current profile's credentials | `Connection.EnvironmentNotFound` or `Auth.Expired` |
| Panel environment picker | Shows discovered environments + manual URL entry | Informational message if no environments discovered |

---

## Acceptance Criteria

| ID | Criterion | Test | Status |
|----|-----------|------|--------|
| AC-01 | `query/sql` accepts optional `environmentUrl` and executes against that environment instead of the profile's saved environment | TBD | 🔲 |
| AC-02 | `query/fetch` accepts optional `environmentUrl` and executes against that environment | TBD | 🔲 |
| AC-03 | `query/export` accepts optional `environmentUrl` and exports from that environment | TBD | 🔲 |
| AC-04 | `query/explain` accepts optional `environmentUrl` and transpiles against that environment | TBD | 🔲 |
| AC-05 | `solutions/list` accepts optional `environmentUrl` and lists solutions from that environment | TBD | 🔲 |
| AC-06 | `solutions/components` accepts optional `environmentUrl` and lists components from that environment | TBD | 🔲 |
| AC-07 | Omitting `environmentUrl` from any RPC method falls back to the active profile's saved environment (backward compatible) | TBD | 🔲 |
| AC-08 | Data Explorer panel displays environment picker in webview header, defaulting to active profile's saved environment | Manual | 🔲 |
| AC-09 | Changing the environment picker in Data Explorer causes subsequent queries to target the new environment | Manual | 🔲 |
| AC-10 | Data Explorer panel title includes the environment name (e.g., `Data Explorer #1 — Contoso Dev`) | Manual | 🔲 |
| AC-11 | Solutions panel is a webview panel (not a sidebar tree view) with its own environment picker | Manual | 🔲 |
| AC-12 | Solutions panel shows hierarchical solution/component browsing with managed/unmanaged toggle | Manual | 🔲 |
| AC-13 | The status bar environment selector is removed | Manual | 🔲 |
| AC-14 | The `ppds.solutions` sidebar view is removed; sidebar contains only Profiles and Tools | Manual | 🔲 |
| AC-15 | Opening a new panel defaults to the active profile's saved environment | Manual | 🔲 |
| AC-16 | Overriding a panel's environment does not change the profile's saved environment on disk | TBD | 🔲 |
| AC-17 | Two Data Explorer panels can target different environments simultaneously | Manual | 🔲 |
| AC-18 | Profile switch via sidebar does not close or dispose open panels | Manual | 🔲 |
| AC-19 | After profile switch, the next RPC call from a panel uses the new profile's credentials | Manual | 🔲 |
| AC-20 | `migrateLegacyState()` clears `power-platform-dev-suite-environments` globalState on first activation | TBD | 🔲 |
| AC-21 | `migrateLegacyState()` runs only once (gated by `ppds.legacyStateCleaned` flag) | TBD | 🔲 |
| AC-22 | TUI: `ShowProfileSelector()` does not call `CloseAllTabs()` — tabs persist across profile switch | Manual | 🔲 |
| AC-23 | TUI: `ShowProfileCreation()` does not call `CloseAllTabs()` — tabs persist after profile creation | Manual | 🔲 |
| AC-24 | TUI: `CloseAllTabs()` method removed from `TuiShell` and `TabManager` | Code review | 🔲 |
| AC-25 | Environment picker includes "Enter URL manually..." option for environments not in the discovery list | Manual | 🔲 |

### Edge Cases

| Scenario | Expected Behavior |
|----------|-------------------|
| Panel opens with no active profile | Panel shows message prompting user to select a profile in sidebar |
| Panel opens with profile that has no saved environment | Environment picker shows empty with prompt to select |
| Profile credentials can't reach panel's overridden environment | Auth error with re-authenticate option on next query |
| Two panels target the same environment | Both reuse the same cached connection pool |
| Panel environment override + profile switch | Panel retains its environment URL, uses new profile's credentials |
| Environment discovery returns empty list | Picker shows only "Enter URL manually..." option |
| `environmentUrl` parameter is malformed | `Validation.InvalidValue` RPC error |

---

## Core Types

### WithProfileAndEnvironmentAsync (new overload)

New variant of the existing `WithActiveProfileAsync` that accepts an explicit environment URL.

```csharp
private async Task<T> WithProfileAndEnvironmentAsync<T>(
    string? environmentUrl,
    Func<IServiceProvider, AuthProfile, EnvironmentInfo, CancellationToken, Task<T>> action,
    CancellationToken cancellationToken)
{
    // Load active profile
    var profile = collection.ActiveProfile
        ?? throw new RpcException(ErrorCodes.Auth.NoActiveProfile, "...");

    // Resolve environment: explicit URL wins, else profile's saved environment
    var resolvedUrl = environmentUrl ?? profile.Environment?.Url
        ?? throw new RpcException(ErrorCodes.Connection.EnvironmentNotFound, "...");

    // Use pool manager (caches per profile+environment)
    var serviceProvider = await _poolManager.GetOrCreateServiceProviderAsync(...);
    return await action(serviceProvider, profile, environment, cancellationToken);
}
```

### RPC Request DTOs (enhanced)

All query and solutions DTOs gain an optional `environmentUrl` field:

```csharp
// Example: QuerySqlRequest gains environmentUrl
public string? EnvironmentUrl { get; set; }
```

### DaemonClient methods (enhanced)

```typescript
// Example: querySql gains optional environmentUrl
async querySql(params: {
    sql: string;
    environmentUrl?: string;  // NEW — panel's target environment
    top?: number;
    // ... existing params
}): Promise<QueryResultResponse>
```

---

## API/Contracts

### Modified RPC Methods

| Method | New Parameter | Purpose |
|--------|---------------|---------|
| `query/sql` | `environmentUrl?: string` | Target environment for SQL query |
| `query/fetch` | `environmentUrl?: string` | Target environment for FetchXML query |
| `query/export` | `environmentUrl?: string` | Target environment for export |
| `query/explain` | `environmentUrl?: string` | Target environment for explain plan |
| `solutions/list` | `environmentUrl?: string` | Target environment for solution listing |
| `solutions/components` | `environmentUrl?: string` | Target environment for component listing |

All methods: if `environmentUrl` is omitted or null, behavior is identical to current (uses active profile's saved environment).

---

## Error Handling

### Error Types

| Error | Condition | Recovery |
|-------|-----------|----------|
| `Auth.NoActiveProfile` | No profile selected, panel can't determine credentials | Prompt user to select profile in sidebar |
| `Connection.EnvironmentNotFound` | No environment URL (neither explicit nor on profile) | Prompt user to select environment in panel picker |
| `Auth.Expired` | Profile credentials can't reach the target environment | Re-authenticate option (existing pattern in `QueryPanel.executeQuery`) |
| `Validation.InvalidValue` | `environmentUrl` is not a valid URL | Show error in panel |

### Recovery Strategies

- **Auth error after profile switch**: Panel shows re-auth prompt (existing pattern). User can re-authenticate or switch the panel's environment to one the new profile can reach.
- **Environment unreachable**: Panel shows error with the environment URL. User can change the environment via the picker.

---

## Design Decisions

### Why Per-Panel Environment Instead of Global?

**Context:** The original extension used a global active-environment model — one environment for everything. This prevented side-by-side comparison and made the Solutions view ambiguous (which environment's solutions?).

**Decision:** Each panel independently selects its target environment. No global environment concept.

**Alternatives considered:**
- Global environment with per-panel override: Confusing — two levels of state, unclear which applies
- Session-based daemon state: Daemon has no concept of panel sessions (single stdio connection)

**Consequences:**
- Positive: Side-by-side comparison, unambiguous panel context
- Positive: Stateless daemon — no session management
- Negative: Each panel must include an environment picker (shared helper mitigates this)

### Why No Status Bar Items?

**Context:** TUI has profile and environment selectors in its status bar. Should VS Code mirror this?

**Decision:** No status bar items. The sidebar profile tree view and per-panel environment pickers handle everything.

**Rationale:**
- Environment is a per-panel concept. With multiple panels on different environments, a single status bar environment indicator is inherently ambiguous.
- Profile management needs CRUD (create, rename, delete, invalidate) — the sidebar tree view handles this; a status bar quick pick can't.
- The sidebar profile tree already shows the active profile at a glance.
- TUI needs status bar selectors because it has no sidebar. VS Code has a sidebar.

**Consequences:**
- Positive: No ambiguous global state, less UI clutter
- Positive: Panel headers are self-contained — all context visible in the panel
- Negative: No at-a-glance profile/environment info when PPDS sidebar isn't visible (acceptable trade-off)

### Why Ephemeral Panel Overrides?

**Context:** When a panel overrides its environment, should that persist to the profile?

**Decision:** Panel environment overrides are ephemeral (in-memory only). They do not change the profile's saved environment on disk.

**Rationale:**
- Persisting would cause panels to fight over the profile's default when multiple panels target different environments.
- The profile's saved environment is the "default for new panels" — a deliberate choice, not a side-effect of opening a panel.
- Consistent with TUI: individual tabs are bound to an environment independently of the profile's saved environment.

**Consequences:**
- Positive: Opening a panel to peek at another environment doesn't change your defaults
- Positive: Predictable — new panels always use the profile's saved environment
- Negative: Closing and reopening a panel loses the override (acceptable — re-select is fast)

### Why Remove CloseAllTabs from TUI?

**Context:** `TuiShell.ShowProfileSelector()` and `ShowProfileCreation()` call `CloseAllTabs()` on profile switch. Testing shows this doesn't work as intended — tabs persist anyway. The desired behavior is that tabs should persist.

**Decision:** Remove `CloseAllTabs()` calls, remove the `TuiShell.CloseAllTabs()` method, and remove `TabManager.CloseAllTabs()`. Tabs persist across profile switches. If credentials can't reach a tab's environment, it errors on next query.

**Consequences:**
- Positive: Consistent behavior between TUI and VS Code — panels/tabs persist
- Positive: Removes dead code that doesn't function correctly
- Negative: Tabs on unreachable environments will error (acceptable — user can close or re-auth)

---

## Extension Points

### Adding a New Environment-Scoped Panel

1. **Create panel class** extending `WebviewPanelBase` in `extension/src/panels/`
2. **Include environment picker** using the shared helper function in the webview HTML header
3. **Handle `environmentChanged` message** from webview — update panel's `environmentUrl` instance variable
4. **Pass `environmentUrl`** on all daemon RPC calls from the panel
5. **Register in Tools tree view** as a new tool entry

---

## Configuration

| Setting | Type | Required | Default | Description |
|---------|------|----------|---------|-------------|
| `ppds.queryDefaultTop` | number | No | 100 | Default TOP value for queries (unchanged) |
| `ppds.autoStartDaemon` | boolean | No | true | Auto-start daemon (unchanged) |
| ~~`ppds.showEnvironmentInStatusBar`~~ | ~~boolean~~ | - | - | **Removed** — no status bar environment indicator |

---

## Related Specs

- [architecture.md](./architecture.md) - Application Services pattern, error handling, DI
- [authentication.md](./authentication.md) - Profile model, `AuthProfile.Environment`, credential providers
- [connection-pooling.md](./connection-pooling.md) - `DaemonConnectionPoolManager` multi-env pool caching

---

## Roadmap

- Per-panel profile selection (future: allow panels to use different credentials, not just different environments)
- Environment favorites/recent list in the picker
- Panel environment persistence in workspace state (optional: remember overrides across sessions)
- Environment health indicator in panel header (connected/disconnected/throttled)
