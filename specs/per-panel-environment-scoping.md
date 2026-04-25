# Per-Panel Environment Scoping

**Status:** Approved
**Version:** 2.0
**Last Updated:** 2026-04-24
**Code:** [src/PPDS.Extension/src/](../src/PPDS.Extension/src/), [src/PPDS.Cli/Commands/Serve/Handlers/](../src/PPDS.Cli/Commands/Serve/Handlers/)
**Surfaces:** Extension

---

## Overview

Per-panel environment scoping gives each VS Code panel (Data Explorer, Solutions, Import Jobs, etc.) full control over its authentication profile and target environment. Panels are self-contained: a user can run a query against Profile A's dev environment in one Data Explorer while browsing solutions in Profile B's production environment in another. The daemon RPC methods accept optional `profileName` and `environmentUrl` parameters so panels can operate independently. A unified status bar item replaces the separate daemon and profile indicators, showing the global active profile and its default environment at a glance.

### Goals

- **Per-panel profile+environment isolation**: Each panel independently selects its profile and environment — panels are self-contained workspaces
- **Side-by-side comparison**: Open the same panel type against different profiles and environments simultaneously
- **Unified status bar**: Single status bar item showing daemon state, active profile, and default environment — replacing two separate items
- **Grouped picker UX**: Single-click combined profile+environment picker in each panel, with profiles as group headers and environments nested underneath
- **Clear profile context**: Command palette profile picker and panel pickers always communicate which profile is active and which environments belong to which profile
- **Backward-compatible daemon API**: `profileName` and `environmentUrl` parameters are optional; omitting them falls back to the active profile and its saved environment

### Non-Goals

- Changing the notebook environment model (notebooks already have per-file environment metadata — correct as-is)
- Rewriting the webview UI framework (we enhance the existing `WebviewPanelBase` + inline HTML pattern)
- TUI per-tab profile switching (tracked separately in #927)
- Panel state persistence across VS Code restart (panel profile+environment bindings are ephemeral)

---

## Architecture

```
┌──────────────────────────────────────────────────────────────────────────┐
│                          VS Code Extension                                │
│                                                                           │
│  Status Bar                                                              │
│  ┌──────────────────────────────────────────────────────────────┐        │
│  │ $(check) PPDS: ProfileName · EnvName                         │        │
│  │ click → profile quick pick (global switch)                   │        │
│  │ error state → click restarts daemon                          │        │
│  └──────────────────────────────────────────────────────────────┘        │
│                                                                           │
│  Panels (each self-contained)                                            │
│  ┌──────────────────────┐    ┌──────────────────────┐                    │
│  │  Data Explorer       │    │  Solutions            │                    │
│  │  ┌────────────────┐  │    │  ┌────────────────┐  │                    │
│  │  │[Profile A ·    │  │    │  │[Profile B ·    │  │                    │
│  │  │ Contoso Dev ▾] │  │    │  │ Fabrikam Prd▾] │  │                    │
│  │  │ context picker │  │    │  │ context picker │  │                    │
│  │  └──────┬─────────┘  │    │  └──────┬─────────┘  │                    │
│  │         │             │    │         │             │                    │
│  │  profileName +        │    │  profileName +        │                    │
│  │  environmentUrl       │    │  environmentUrl       │                    │
│  │  per RPC call         │    │  per RPC call         │                    │
│  └─────────┬─────────────┘    └─────────┬─────────────┘                    │
│            │                            │                                 │
└────────────┼────────────────────────────┼─────────────────────────────────┘
             │  JSON-RPC (stdio)          │
             ▼                            ▼
┌──────────────────────────────────────────────────────────────────────────┐
│                          ppds serve daemon                                │
│                                                                           │
│  RpcMethodHandler                                                        │
│  ┌────────────────────────────────────────────────────────────────────┐  │
│  │ WithProfileAndEnvironmentAsync(profileName?, environmentUrl?)      │  │
│  │   if profileName provided → load that profile                      │  │
│  │   else → use active profile                                        │  │
│  │   if environmentUrl provided → use it                              │  │
│  │   else → fall back to resolved profile's saved environment         │  │
│  └────────────────────────────┬───────────────────────────────────────┘  │
│                                │                                          │
│  DaemonConnectionPoolManager                                             │
│  ┌────────────────────────────┴───────────────────────────────────────┐  │
│  │ ConcurrentDictionary<profileName|envUrl, Pool>                     │  │
│  │   Pool(ProfileA|dev.crm.dynamics.com)  ←── reused across panels   │  │
│  │   Pool(ProfileB|prod.crm.dynamics.com) ←── reused across panels   │  │
│  └────────────────────────────────────────────────────────────────────┘  │
└──────────────────────────────────────────────────────────────────────────┘
```

### Components

| Component | Responsibility |
|-----------|----------------|
| `PpdsStatusBar` (new, replaces `DaemonStatusBar` + `ProfileStatusBar`) | Unified status bar item — daemon state, active profile name, default environment name. Click behavior varies by state. |
| Context picker helper (enhanced `environmentPicker.ts`) | Grouped quick pick showing all profiles as separators with their environments nested underneath. Returns selected `{ profileName, environmentUrl, displayName }`. |
| `WebviewPanelBase` (enhanced) | Stores per-panel `profileName` + `environmentUrl`. Passes both on every RPC call. Title format: `ProfileName · EnvName — PanelLabel`. |
| `DaemonClient` (enhanced) | All environment-scoped RPC methods gain optional `profileName` parameter alongside existing `environmentUrl`. `envList` gains optional `profileName`. |
| `RpcMethodHandler` (enhanced) | `WithProfileAndEnvironmentAsync` gains optional `profileName` — loads specified profile instead of active profile when provided. `EnvListAsync` gains `profileName` with per-profile discovery cache. |
| `DaemonConnectionPoolManager` | Unchanged — already caches pools per profile+environment combination. |

### Dependencies

- Depends on: [architecture.md](./architecture.md) (Application Services, error handling, DI)
- Depends on: [authentication.md](./authentication.md) (profile model, `AuthProfile.Environment`)
- Depends on: [connection-pooling.md](./connection-pooling.md) (`DaemonConnectionPoolManager` multi-env caching)

---

## Specification

### Core Requirements

1. **Per-panel profile+environment targeting**: All environment-scoped RPC methods accept optional `profileName` and `environmentUrl` parameters. When `profileName` is provided, the daemon loads and authenticates with that profile. When `environmentUrl` is provided, the daemon targets that environment. Both fall back to the active profile and its saved environment when omitted.

2. **Grouped context picker in panels**: Each panel displays a context button in the toolbar showing `ProfileName · EnvName`. Clicking opens a single quick pick with profiles as separator headers and their environments nested underneath. Selecting an environment sets both the panel's profile and environment. A single "Enter URL manually..." option appears at the bottom of the picker.

3. **Unified status bar item**: Replace `DaemonStatusBar` and `ProfileStatusBar` with a single `PpdsStatusBar`. Shows daemon state, active profile name, and active profile's default environment. Click behavior is state-dependent: ready → profile quick pick, error/stopped → restart daemon.

4. **Enhanced command palette profile picker**: `ppds.listProfiles` quick pick marks the active profile with `$(check)` prefix, shows environment name in the `description` field (more prominent), and shows identity in `detail`.

5. **Profile-keyed discovery cache**: `envList` RPC gains optional `profileName` parameter. The discovery cache is keyed by profile name so concurrent `envList` calls for different profiles return correct results.

6. **Device code attribution**: `auth/deviceCode` notifications include the profile name so the user knows which profile triggered re-authentication when multiple panels use different profiles.

7. **Panel title includes profile and environment**: Panel titles use format `ProfileName · EnvName — PanelLabel` (existing format, now reflecting the panel's own profile rather than always the active profile).

### Primary Flows

**Panel opens with defaults:**

1. User clicks "Data Explorer" in Tools tree view
2. Panel creates, reads active profile name and default environment from `daemon.authList()`
3. Stores `profileName` and `environmentUrl` as panel instance state
4. Context picker button shows `ProfileName · EnvName`
5. All RPC calls include both `profileName` and `environmentUrl`

**User switches profile+environment via grouped picker:**

1. User clicks the context picker button in the panel toolbar
2. Panel calls `daemon.authList()` to get all profiles, then `daemon.envList({ profileName })` per profile. Calls are parallelized; the picker shows immediately with profile groups populated as responses arrive. If `envList` fails for a profile, that group shows its saved/configured environments only (no discovery).
3. Quick pick displays profiles as separator headers, environments nested underneath:
   ```
   Dev Profile (active)
     $(check) Contoso Dev          ← current for this panel
     Contoso Test
     Contoso Prod
   Prod SPN
     Fabrikam Prod
   $(link) Enter URL manually...
   ```
4. User selects an environment under a different profile
5. Panel updates `profileName` and `environmentUrl` in instance state
6. Panel title updates, data reloads using new profile's credentials against new environment

**User switches global active profile via status bar:**

1. User clicks the unified status bar item (when daemon is ready)
2. Profile quick pick opens — same as today's `ppds.listProfiles` but enhanced with `$(check)` prefix and prominent environment display
3. User selects a different profile
4. Global active profile changes (persisted to `profiles.json`)
5. Status bar updates to show new profile name and environment
6. Open panels are **unaffected** — they retain their own `profileName` and `environmentUrl`
7. New panels opened after the switch default to the new active profile

**Daemon resolves profile and environment on RPC call:**

1. RPC method receives request with optional `profileName` and `environmentUrl`
2. `WithProfileAndEnvironmentAsync`:
   - If `profileName` provided → load that profile from `ProfileStore`
   - Else → use `collection.ActiveProfile`
   - If `environmentUrl` provided → use it
   - Else → use resolved profile's saved environment
3. `DaemonConnectionPoolManager.GetOrCreatePoolAsync` caches/reuses pool for that profile+environment key
4. Operation executes against the resolved profile and environment

### SPN Profile Behavior

SPN (service principal) profiles differ from interactive profiles in two ways that affect the picker UX:

1. **No environment discovery**: `GlobalDiscoveryService` only supports `InteractiveBrowser` and `DeviceCode` auth methods. SPN profiles show only their configured environment plus any manually-saved environments in `environments.json`. The picker handles this gracefully — sparse environment lists are expected.

2. **Resource-scoped tokens**: SPN tokens are scoped to the environment URL used at auth time. Switching an SPN panel to a different environment creates a new connection pool with fresh authentication. If the SPN app registration lacks permissions on the target environment, the connection fails. Error message: "This service principal does not have access to the selected environment."

The "Enter URL manually..." option appears for all profiles, enabling SPN users to target environments not in the sparse list.

### Constraints

- A panel's profile+environment binding is ephemeral — not persisted. Closing and reopening defaults back to the global active profile and its saved environment.
- Panel profile+environment selection does not change the global active profile or any profile's saved environment on disk.
- Multiple panels can use different profiles simultaneously — the connection pool manager handles concurrent profile+environment combinations.
- Each panel within the extension is an independent session in Constitution SS1 terms: "Running sessions are independent — changing the active profile or environment in one surface does not affect other running surfaces." The status bar's active profile is the default for new sessions (new panels), not a live binding to existing panels.

### Validation Rules

| Field | Rule | Error |
|-------|------|-------|
| `profileName` (RPC) | Must match an existing profile name or index, or be omitted | `Auth.ProfileNotFound` |
| `environmentUrl` (RPC) | Must be a valid URL if provided, or omitted entirely | `Validation.InvalidValue` |
| `environmentUrl` (RPC) | Must be reachable with the resolved profile's credentials | `Connection.EnvironmentNotFound` or `Auth.Expired` |
| Panel context picker | Shows discovered + saved environments per profile. SPN profiles may show sparse lists. | Informational — "Enter URL manually..." always available |

---

## Acceptance Criteria

| ID | Criterion | Test | Status |
|----|-----------|------|--------|
| AC-01 | `PpdsStatusBar` replaces `DaemonStatusBar` and `ProfileStatusBar` as a single left-aligned status bar item | TBD | 🔲 |
| AC-02 | Status bar shows `$(check) PPDS: ProfileName · EnvName` when daemon is ready and a profile with environment is active | TBD | 🔲 |
| AC-03 | Status bar shows `$(check) PPDS: ProfileName` when profile has no default environment | TBD | 🔲 |
| AC-04 | Status bar shows `$(check) PPDS: No profile` when no profile is active | TBD | 🔲 |
| AC-05 | Status bar shows `$(sync~spin) PPDS` during starting/reconnecting states | TBD | 🔲 |
| AC-06 | Status bar shows `$(error) PPDS` with error background during error state | TBD | 🔲 |
| AC-06a | Status bar shows `$(circle-slash) PPDS` when daemon is stopped | TBD | 🔲 |
| AC-07 | Clicking status bar in ready state opens profile quick pick (`ppds.listProfiles`) | TBD | 🔲 |
| AC-08 | Clicking status bar in error/stopped state runs `ppds.restartDaemon` | TBD | 🔲 |
| AC-09 | Status bar tooltip shows profile name, environment, auth method, and daemon state | TBD | 🔲 |
| AC-10 | `ppds.listProfiles` quick pick shows active profile with `$(check)` prefix in label | TBD | 🔲 |
| AC-11 | `ppds.listProfiles` quick pick shows environment name in `description` field | TBD | 🔲 |
| AC-11a | `ppds.listProfiles` quick pick shows identity (UPN or app ID) in `detail` field | TBD | 🔲 |
| AC-12 | Panel context picker button shows `ProfileName · EnvName` in toolbar | Manual | 🔲 |
| AC-13 | Panel context picker opens grouped quick pick with profiles as separators and environments nested underneath | Manual | 🔲 |
| AC-14 | Selecting an environment in the grouped picker sets both `profileName` and `environmentUrl` on the panel | Manual | 🔲 |
| AC-15 | Two panels can simultaneously use different profiles (e.g., Panel A on Profile 1, Panel B on Profile 2) | Manual | 🔲 |
| AC-16 | RPC methods accept optional `profileName` parameter; when provided, the daemon authenticates with that profile | TBD | 🔲 |
| AC-17 | RPC methods accept optional `environmentUrl` parameter; when provided, the daemon targets that environment | TBD | 🔲 |
| AC-18 | Omitting both `profileName` and `environmentUrl` falls back to active profile and its saved environment (backward compatible) | TBD | 🔲 |
| AC-19 | `env/list` accepts optional `profileName` and returns environments discoverable by that profile | TBD | 🔲 |
| AC-20 | Discovery cache is keyed by profile name — concurrent `envList` calls for different profiles return correct results | TBD | 🔲 |
| AC-21 | `auth/deviceCode` notifications include the profile name that triggered the flow | TBD | 🔲 |
| AC-22 | Device code notification in VS Code shows which profile needs re-authentication | Manual | 🔲 |
| AC-23 | Opening a new panel defaults to the global active profile and its saved environment | Manual | 🔲 |
| AC-24 | Switching the global active profile via status bar does not affect open panels | Manual | 🔲 |
| AC-25 | Panel title format is `ProfileName · EnvName — PanelLabel`, reflecting the panel's own profile | Manual | 🔲 |
| AC-26 | SPN profiles show their configured environment plus manually-saved environments in the picker (no discovery) | Manual | 🔲 |
| AC-27 | SPN auth failure on cross-environment switch shows clear error: profile lacks permissions on target environment | Manual | 🔲 |
| AC-28 | Panel context picker includes "Enter URL manually..." option | Manual | 🔲 |
| AC-29 | Overriding a panel's profile+environment does not change the global active profile or any profile's saved environment on disk | TBD | 🔲 |

### Edge Cases

| Scenario | Expected Behavior |
|----------|-------------------|
| Panel opens with no active profile | Panel shows message prompting user to create or select a profile |
| Panel opens with profile that has no saved environment | Context picker shows profile name only, prompts to select environment |
| Profile referenced by panel is deleted | Next RPC call returns `Auth.ProfileNotFound`; panel shows error with option to select a different profile |
| Profile referenced by panel is renamed | Panel title becomes stale until next picker interaction; RPC calls fail with `Auth.ProfileNotFound` until panel re-selects |
| Two panels trigger device code re-auth for different profiles | Two notifications appear, each identifying the profile name |
| SPN panel switches to environment where app registration lacks permissions | Clear error message identifying the SPN and target environment |
| `envList` called for SPN profile | Returns configured/saved environments only (no discovery), plus "Enter URL manually..." |
| Many profiles (5+) with many environments (10+ each) | Grouped picker is scrollable; profiles as separators provide visual structure |
| `envList` fails for one profile during picker population | That profile's group shows saved/configured environments only; other profiles unaffected; no blocking error |
| Panel profile+environment override + global profile switch | Panel retains its own profile+environment; unaffected by global switch |

---

## Core Types

### WithProfileAndEnvironmentAsync (enhanced)

Enhanced to accept optional `profileName` alongside `environmentUrl`.

```csharp
private async Task<T> WithProfileAndEnvironmentAsync<T>(
    string? profileName,
    string? environmentUrl,
    Func<IServiceProvider, AuthProfile, EnvironmentInfo, CancellationToken, Task<T>> action,
    CancellationToken cancellationToken)
{
    var store = _authServices.GetRequiredService<ProfileStore>();
    var collection = await store.LoadAsync(cancellationToken);

    // Resolve profile: explicit name wins, else active profile
    var profile = !string.IsNullOrWhiteSpace(profileName)
        ? collection.GetByNameOrIndex(profileName)
            ?? throw new RpcException(ErrorCodes.Auth.ProfileNotFound, $"Profile '{profileName}' not found")
        : collection.ActiveProfile
            ?? throw new RpcException(ErrorCodes.Auth.NoActiveProfile, "No active profile configured");

    // Resolve environment: explicit URL wins, else profile's saved environment
    var resolvedUrl = environmentUrl ?? profile.Environment?.Url
        ?? throw new RpcException(ErrorCodes.Connection.EnvironmentNotFound, "...");

    var resolvedEnvironment = profile.Environment?.Url?.Equals(resolvedUrl, StringComparison.OrdinalIgnoreCase) == true
        ? profile.Environment
        : new EnvironmentInfo { Url = resolvedUrl, DisplayName = resolvedUrl };

    var serviceProvider = await _poolManager.GetOrCreateServiceProviderAsync(
        new[] { profile.Name ?? profile.DisplayIdentifier },
        resolvedUrl, ...);

    return await action(serviceProvider, profile, resolvedEnvironment, cancellationToken);
}
```

### Profile-keyed discovery cache

Replace the single `_discoveredEnvCache` / `_discoveredEnvCacheExpiry` fields with a `ConcurrentDictionary` keyed by profile name:

```csharp
private readonly ConcurrentDictionary<string, (EnvListResponse response, DateTime expiry)> _envCacheByProfile = new();
```

### DaemonClient methods (enhanced)

```typescript
// All environment-scoped methods gain optional profileName
async querySql(params: {
    sql: string;
    profileName?: string;      // NEW — panel's profile
    environmentUrl?: string;   // existing — panel's target environment
    top?: number;
}): Promise<QueryResultResponse>

// envList gains profileName
async envList(params?: {
    profileName?: string;      // NEW — discover for this profile
}): Promise<EnvListResponse>
```

### PpdsStatusBar

Replaces `DaemonStatusBar` and `ProfileStatusBar`:

```typescript
export class PpdsStatusBar implements vscode.Disposable {
    // Single status bar item, left-aligned, priority 50
    // Command varies by daemon state:
    //   ready → 'ppds.listProfiles'
    //   error/stopped → 'ppds.restartDaemon'
    // Text varies by state:
    //   ready + profile + env → '$(check) PPDS: ProfileName · EnvName'
    //   ready + profile, no env → '$(check) PPDS: ProfileName'
    //   ready, no profile → '$(check) PPDS: No profile'
    //   starting/reconnecting → '$(sync~spin) PPDS'
    //   error → '$(error) PPDS'
    //   stopped → '$(circle-slash) PPDS'
}
```

---

## API/Contracts

### Modified RPC Methods

| Method | New Parameter | Purpose |
|--------|---------------|---------|
| `query/sql` | `profileName?: string` | Authenticate with this profile |
| `query/fetch` | `profileName?: string` | Authenticate with this profile |
| `query/export` | `profileName?: string` | Authenticate with this profile |
| `query/explain` | `profileName?: string` | Authenticate with this profile |
| `solutions/list` | `profileName?: string` | Authenticate with this profile |
| `solutions/components` | `profileName?: string` | Authenticate with this profile |
| `env/list` | `profileName?: string` | Discover environments for this profile |
| `auth/deviceCode` (notification) | `profileName: string` | Identifies which profile triggered the flow |
| All above + existing | `environmentUrl?: string` | Target environment (existing, unchanged) |

All methods: if `profileName` is omitted or null, behavior is identical to current (uses active profile). If `environmentUrl` is omitted or null, uses resolved profile's saved environment.

---

## Error Handling

### Error Types

| Error | Condition | Recovery |
|-------|-----------|----------|
| `Auth.NoActiveProfile` | No profile selected and no `profileName` provided | Prompt user to create or select a profile |
| `Auth.ProfileNotFound` | `profileName` doesn't match any existing profile (deleted or renamed) | Panel shows error with option to re-select via context picker |
| `Connection.EnvironmentNotFound` | No environment URL (neither explicit nor on resolved profile) | Prompt user to select environment via context picker |
| `Auth.Expired` | Profile credentials can't reach the target environment | Re-authenticate option; device code notification identifies the profile |
| `Auth.InsufficientPermissions` | SPN app registration lacks permissions on target environment | Clear error: "Service principal '{name}' does not have access to {environment}" |
| `Validation.InvalidValue` | `environmentUrl` is not a valid URL | Show error in panel |

### Recovery Strategies

- **Profile deleted while panel references it**: Panel gets `Auth.ProfileNotFound` on next RPC call. Shows error with button to open context picker and select a valid profile.
- **SPN cross-environment auth failure**: Panel shows specific error identifying that the SPN lacks permissions. User can switch to an environment the SPN can reach, or use a different profile.
- **Concurrent device code flows**: Each notification includes the profile name. User can distinguish which code belongs to which profile.

---

## Design Decisions

### Why Per-Panel Profile+Environment Instead of Global Profile?

**Context:** The original design (v1.0 of this spec) used a global active-profile model where all panels shared the active profile's credentials. Environment could vary per panel, but profile could not. Users reported this as friction (#887): "If someone wants to switch from one profile to another on their panel, why would we prevent that?"

**Decision:** Each panel independently selects both its profile and its environment. The global active profile is the default for new panels, not a binding for existing panels.

**Alternatives considered:**
- Global profile with per-panel environment only (v1.0 design): Prevents side-by-side comparison across profiles. User must switch global profile to see a different profile's environments.
- Per-panel profile dropdown + separate environment dropdown: Two controls take more toolbar space and require two interactions to switch context.

**Consequences:**
- Positive: Panels are fully self-contained — no barriers to multi-profile workflows
- Positive: Side-by-side comparison across profiles and environments simultaneously
- Negative: Daemon RPC gains `profileName` parameter on all environment-scoped methods
- Negative: Discovery cache must be profile-keyed (simple `ConcurrentDictionary` change)
- Negative: Device code notifications must identify the profile (simple payload addition)

### Why a Unified Status Bar Item?

**Context:** The extension had two separate status bar items: `DaemonStatusBar` (priority 50, shows `$(check) PPDS`) and `ProfileStatusBar` (priority 49, shows `$(account) ProfileName`). They were adjacent but visually disconnected, and neither showed environment info (#888).

**Decision:** Merge into a single `PpdsStatusBar` item that combines daemon state, profile name, and environment name. Click behavior is state-dependent.

**Alternatives considered:**
- Keep separate items but add environment to profile item: Still visually disconnected, two items competing for attention
- Three separate items (daemon + profile + environment): Too much status bar real estate, environment is ambiguous when panels have different environments

**Consequences:**
- Positive: Single glanceable indicator for all PPDS state
- Positive: State-dependent click (restart daemon vs switch profile) is intuitive
- Negative: Loses the always-visible restart button — but restart is only needed in error state, and the merged item handles that case

### Why Option C (Grouped Picker) Instead of Two Dropdowns?

**Context:** The panel needs to let users select both a profile and an environment. Options considered: (A) two separate dropdowns, (B) two-step sequential quick pick, (C) single grouped quick pick.

**Decision:** Option C — single grouped quick pick with profiles as separator headers and environments nested underneath.

**Rationale:**
- One click to see everything, one click to switch — minimum friction
- Uses VS Code's native `QuickPickItemKind.Separator` for profile grouping — no custom UI needed
- Naturally solves #903 (unclear profile context) because profiles are explicit group headers
- Compact toolbar footprint — single button showing `ProfileName · EnvName`

**Alternatives considered:**
- Two dropdowns (Option A): Takes more toolbar space, two interactions to switch both profile and environment
- Two-step picker (Option B): Hidden second step — user must click twice and the first step doesn't show environments

**Consequences:**
- Positive: Lowest friction, highest discoverability
- Positive: Works for sparse SPN lists (1 environment) and rich interactive lists (10+ environments)
- Negative: Long list for users with many profiles+environments (mitigated by scrolling and type-to-filter)

### Why Ephemeral Panel Bindings?

**Context:** When a panel selects a profile+environment, should that persist across VS Code restarts?

**Decision:** Panel profile+environment bindings are ephemeral (in-memory only). Closing and reopening defaults back to the global active profile.

**Rationale:**
- Persisting would create stale references to renamed/deleted profiles
- The global active profile is the "default for new panels" — a deliberate choice, not a side-effect
- Re-selecting is fast with the grouped picker

**Consequences:**
- Positive: No stale state, no cleanup on profile deletion
- Positive: Predictable — new panels always use the global active profile
- Negative: Users must re-select after restart (acceptable — one click)

---

## Extension Points

### Adding a New Profile+Environment-Scoped Panel

1. **Create panel class** extending `WebviewPanelBase` in `src/PPDS.Extension/src/panels/`
2. **Include context picker** using the shared helper function in the webview HTML header
3. **Handle `contextChanged` message** from webview — update panel's `profileName` and `environmentUrl`
4. **Pass `profileName` and `environmentUrl`** on all daemon RPC calls from the panel
5. **Register in Tools tree view** as a new tool entry

---

## Configuration

No new settings. Existing settings unchanged:

| Setting | Type | Required | Default | Description |
|---------|------|----------|---------|-------------|
| `ppds.queryDefaultTop` | number | No | 100 | Default TOP value for queries (unchanged) |
| `ppds.autoStartDaemon` | boolean | No | true | Auto-start daemon (unchanged) |

---

## Related Specs

- [architecture.md](./architecture.md) - Application Services pattern, error handling, DI
- [authentication.md](./authentication.md) - Profile model, `AuthProfile.Environment`, credential providers
- [connection-pooling.md](./connection-pooling.md) - `DaemonConnectionPoolManager` multi-env pool caching

---

## Changelog

| Date | Change |
|------|--------|
| 2026-03-13 | v1.0 — Initial spec: per-panel environment scoping, environment-only override |
| 2026-04-24 | v2.0 — Per-panel profile+environment scoping, unified status bar, grouped picker (#887, #888, #903) |

---

## Roadmap

- Environment favorites/recent list in the picker
- Panel profile+environment persistence in workspace state (optional: remember overrides across sessions)
- Environment health indicator in panel header (connected/disconnected/throttled)
- TUI per-tab profile switching (#927)
