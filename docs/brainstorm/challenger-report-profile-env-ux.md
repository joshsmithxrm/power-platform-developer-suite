# Challenger Findings: Profile & Environment UX Redesign

**Reviewed:** Design proposal for unified status bar, per-panel profile+environment switching (Option C grouped picker), daemon RPC changes, and panel independence model. Key source files: `src/PPDS.Extension/src/profileStatusBar.ts`, `src/PPDS.Extension/src/daemonStatusBar.ts`, `src/PPDS.Extension/src/panels/environmentPicker.ts`, `src/PPDS.Extension/src/panels/WebviewPanelBase.ts`, `src/PPDS.Extension/src/commands/profileCommands.ts`, `src/PPDS.Cli/Commands/Serve/Handlers/RpcMethodHandler.cs`, `src/PPDS.Cli/Infrastructure/DaemonConnectionPoolManager.cs`, `src/PPDS.Auth/Pooling/ProfileConnectionSource.cs`, `src/PPDS.Auth/Discovery/GlobalDiscoveryService.cs`
**Date:** 2026-04-24
**Summary:** 4 BLOCKERs, 5 WARNINGs, 4 SUGGESTIONs

## BLOCKERs

## [B1] — Discovery cache is singleton, not keyed by profile — `envList` for non-active profile returns wrong environments

**Evidence:** `RpcMethodHandler.cs` lines 71-76: the discovery cache (`_discoveredEnvCache`, `_discoveredEnvCacheExpiry`) is a single pair of fields on the handler instance. `EnvListAsync` (line 384) always calls `GlobalDiscoveryService.FromProfile(profile)` with the active profile. The proposed design adds an optional `profileName` parameter to `envList`, but the cache has no profile key.

**Issue:** If Panel A calls `envList` for "Dev Profile" (populating the cache), then Panel B calls `envList` for "Prod SPN" within 5 minutes, the cache returns Dev Profile's environments to Prod SPN's picker. Worse, the tagging logic (lines 451-458) would tag Dev Profile's discovered environments with Prod SPN's profile name in `environments.json`, corrupting the config store's profile-environment mapping. This is a data integrity issue, not just a stale-cache issue.

**Suggestion:** The design must address how `envList` cache isolation works per profile. The single-field cache must be replaced with a profile-keyed cache, or the cache must be invalidated when a different profile is requested.

## [B2] — SPN profiles cannot discover environments via Global Discovery — grouped picker will show empty or wrong environment list

**Evidence:** `GlobalDiscoveryService.cs` lines 64-79: `FromProfile` throws `NotSupportedException` for non-interactive auth methods. `GlobalDiscoveryService.SupportsGlobalDiscovery` (line 98-101) only returns true for `InteractiveBrowser` and `DeviceCode`. The `envList` handler catches this exception silently (line 435-439: "Discovery may fail for SPNs") and falls back to configured-only environments.

**Issue:** The grouped picker design shows profiles as separator headers with environments nested underneath. For an SPN profile, the environment list will contain only environments previously manually configured or tagged in `environments.json` for that profile — which may be zero entries if the user has never manually added one. The picker mockup shows multiple environments under each profile, but SPN profiles will typically show only their creation-time environment (or nothing). The design does not acknowledge this fundamental asymmetry between user-based and SPN profiles, nor does it explain how the picker should behave when an SPN profile's environment list is empty or contains only one entry.

**Suggestion:** The design must explicitly address the SPN discovery limitation. Consider: should the picker show a "manual URL entry" option per-profile? Should it indicate that discovery is unavailable? How does a user switch an SPN panel to a different environment if discovery returns nothing?

## [B3] — SPN token scoping prevents free environment switching — tokens are resource-scoped to a specific environment URL

**Evidence:** `ProfileConnectionSource.cs` lines 170-199: the seed client is created with `_provider.CreateServiceClientAsync(_environmentUrl, ...)`. For SPN auth methods (ClientSecret, CertificateFile, CertificateStore), the OAuth2 token request uses the environment URL as the resource/audience. `DaemonConnectionPoolManager.cs` line 306: `ProfileConnectionSource` is created with the explicit `environmentUrl` parameter. The pool manager caches by `profileName|environmentUrl` key (line 248-253).

**Issue:** The design assumes panels can freely switch environments for any profile. But for SPN profiles, the token is scoped to the environment URL provided at auth time. Switching an SPN panel from `contoso-dev.crm.dynamics.com` to `contoso-test.crm.dynamics.com` requires a completely new authentication flow — the pool manager will create a new `ProfileConnectionSource` for the new key, which triggers a fresh `ServiceClient` creation with the new URL. This works mechanically (the pool manager handles it), but the design does not address: (a) the SPN app registration must have permissions on the target environment, or the connection will fail; (b) there is no pre-flight check — the user picks an environment, the panel attempts connection, and it fails after several seconds of timeout; (c) the error message will be generic and won't explain that the SPN lacks permissions on the target environment.

**Suggestion:** The design must specify error handling for cross-environment SPN authentication failures. A pre-flight permissions check or at minimum a clear error message ("This service principal does not have access to the selected environment") is needed.

## [B4] — Device code flow on non-active profile creates ambiguous UX — notification doesn't identify which profile/panel triggered it

**Evidence:** `DaemonDeviceCodeHandler.cs` lines 17-42: the callback sends an `auth/deviceCode` notification with `userCode`, `verificationUrl`, and `message` — no profile name or panel identifier. `profileCommands.ts` lines 26-39: the extension handler shows a generic `showInformationMessage` with the message text.

**Issue:** When a panel using a non-active profile triggers re-authentication (e.g., token expired), the device code notification fires on the shared RPC channel. The user sees a generic "Enter code: XXXXX" message with no indication of which panel or profile triggered it. If two panels for different profiles both need re-auth simultaneously, two device code notifications arrive and the user cannot determine which code belongs to which profile. Completing the wrong one will authenticate the wrong profile. The current design works because only the active profile can trigger device code flow, but per-panel profiles break this invariant.

**Suggestion:** The design must specify how device code notifications are attributed to specific profiles and panels when multiple profiles may need re-authentication simultaneously.

## WARNINGs

## [W1] — `initializePanel` calls `authWho` which returns active profile only — per-panel profile binding has no initialization path

**Evidence:** `WebviewPanelBase.ts` lines 156-195: `initializePanel` calls `daemon.authWho()` to get `profileName` and environment. `RpcMethodHandler.cs` lines 257-312: `AuthWhoAsync` always returns `collection.ActiveProfile`. There is no `profileName` parameter on `auth/who`.

**Issue:** The design proposes per-panel profile binding, but the initialization flow always reads the active profile. For the design to work, either `auth/who` needs a `profileName` parameter (not mentioned), or `initializePanel` must be refactored to accept a profile name and skip the `authWho` call. The design describes RPC changes for `WithProfileAndEnvironmentAsync` and `envList` but does not mention `auth/who`, which is the first RPC call in the panel lifecycle.

**Suggestion:** Enumerate all RPC methods that assume "active profile" and determine which ones need a `profileName` parameter. `auth/who` is one; there may be others called during panel data loading.

## [W2] — Pool manager creates separate service providers per profile+environment — resource consumption scales with O(profiles x environments)

**Evidence:** `DaemonConnectionPoolManager.cs` lines 282-346: `CreatePoolEntryAsync` builds a full `ServiceProvider` with connection pool (52 max connections), credential store, and all application services for each unique profile+environment combination. Line 39: `MaxPoolSizePerProfile = 52`.

**Issue:** If users have 3 profiles each pointing at 3 environments, that's 9 cached `ServiceProvider` instances, each potentially holding up to 52 `ServiceClient` connections. With per-panel profile switching, the combinatorial explosion is more likely. The design does not address: memory pressure from multiple service providers, whether pools should be evicted on LRU basis, or whether there should be a cap on concurrent pools.

**Suggestion:** The design should acknowledge the resource scaling implications and specify whether any eviction or cap policy is needed when panels can create arbitrary profile+environment combinations.

## [W3] — Panel title shows `profileName · envName` but profileName comes from `authWho` which returns the display name of the active profile — stale after profile rename

**Evidence:** `WebviewPanelBase.ts` line 159: `this.profileName = who.name ?? 'Profile ${who.index}'`. Line 147-149: title uses `this.profileName`. Profile rename (`profileCommands.ts` lines 712-756) calls `refreshProfiles()` which refreshes the tree view, but does not notify open panels.

**Issue:** If a user renames a profile while panels are open using that profile, the panel titles become stale. The design does not specify whether panel titles should reactively update on profile rename events. This is a pre-existing issue that per-panel profile binding makes worse, since panels now visually depend on the profile name in their title.

**Suggestion:** Specify whether panels should listen for profile change events and update their titles accordingly.

## [W4] — Grouped picker (Option C) QuickPick API limitations — separators are not selectable and nesting is flat

**Evidence:** The design shows a grouped picker with profile names as separators and environments indented underneath. VS Code's `QuickPickItem` supports `QuickPickItemKind.Separator` for visual grouping (already used in `profileCommands.ts` line 215), but separators are not selectable items. The environments are flat items indented with text formatting.

**Issue:** Several UX failure modes: (1) With many profiles (5+) each having many environments (10+), the picker becomes very long with no collapsing — users must scroll through all profile sections. (2) There is no way to select a profile without selecting an environment — what if the user wants to switch profile but keep the current environment? The design doesn't address this case. (3) The visual hierarchy relies on text indentation (leading spaces in labels), which is fragile — VS Code may trim leading whitespace or render it inconsistently across themes. (4) The `$(check)` icon on the current selection must account for the fact that different panels may have different current selections — which panel's "current" is shown?

**Suggestion:** Test the grouped picker with realistic data volumes (5 profiles, 10+ environments each) to verify scrollability. Define behavior when a user selects a profile header (if even possible). Clarify whose "current" selection the checkmark represents.

## [W5] — `WithProfileAndEnvironmentAsync` loads profile by active profile — adding `profileName` requires `collection.GetByNameOrIndex` but profile lookup by name has collision risk

**Evidence:** `RpcMethodHandler.cs` lines 3308-3352: `WithProfileAndEnvironmentAsync` always uses `collection.ActiveProfile`. The design proposes adding an optional `profileName` parameter to load a different profile. `DaemonConnectionPoolManager.cs` line 298: `collection.GetByNameOrIndex(profileName)` is the lookup method.

**Issue:** Profile names are user-defined strings. The design does not address: (a) what happens if a profile is deleted while a panel still references it by name — the panel would get a null profile on next RPC call; (b) whether profile name is a stable identifier — users can rename profiles, breaking the panel's stored reference; (c) unnamed profiles are referenced by index, but indices can shift when profiles are deleted. The panel stores `profileName` as a string, but this is not a stable key.

**Suggestion:** Define the stable identity for per-panel profile binding. Consider whether profile index, name, or a generated GUID should be the panel's stored reference. Specify error handling when the stored profile reference becomes invalid.

## SUGGESTIONs

## [S1] — Merging daemon + profile status bar items loses the restart-daemon click action

**Evidence:** `daemonStatusBar.ts` lines 25-27: clicking the daemon status bar when in `error` state runs `ppds.restartDaemon`. `profileStatusBar.ts` line 23: clicking runs `ppds.listProfiles`. The design proposes merging these into a single status bar item: `$(check) PPDS: ProfileName · EnvName`.

**Issue:** A single status bar item can have only one `command`. The merged item's click is proposed to open the profile quick pick. The restart-daemon affordance (critical when daemon crashes) would need to move elsewhere or be conditionally set based on state. The design doesn't specify where restart goes.

**Suggestion:** Specify the click behavior for each daemon state (ready, starting, reconnecting, error, stopped) in the merged status bar item. Ensure the restart affordance is not lost.

## [S2] — No mention of panel state persistence across VS Code restart

**Evidence:** The design says panels store `profileName` + `environmentUrl` independently. `WebviewPanelBase.ts` stores these as in-memory instance fields (lines 30-35). VS Code panels are destroyed on window close.

**Issue:** If a user configures Panel A to use "Dev Profile · Contoso Dev" and Panel B to use "Prod SPN · Fabrikam Prod", then closes and reopens VS Code, all panels reset to the active profile. The design does not specify whether per-panel profile+environment state should be persisted (e.g., in workspace state or memento).

**Suggestion:** Clarify whether per-panel profile+environment binding is ephemeral (reset on restart) or persistent. If ephemeral, document this as expected behavior.

## [S3] — Design does not address the interaction between per-panel profiles and the Solutions panel's "active/all" filter

**Evidence:** The Solutions panel (and potentially other panels) may have filters or operations that are profile-dependent — e.g., showing solutions for the connected environment. If two panels for the same feature (e.g., two Solutions panels) are open with different profiles, commands like "Sync Deployment Settings" would need to know which profile+environment to target.

**Issue:** Global commands registered in the command palette (like profile-related operations) may not have context about which panel invoked them when panels can have different profiles.

**Suggestion:** Consider whether any panel-level commands need to be scoped to the panel's profile+environment context rather than the global active profile.

## [S4] — Constitution SS1 alignment is asserted but not fully analyzed

**Evidence:** The design cites SS1: "Running sessions are independent — changing the active profile or environment in one surface does not affect other running surfaces." It claims panels-as-sessions aligns with this.

**Issue:** SS1 speaks of "surfaces" (CLI, TUI, Extension, MCP), not panels within a single surface. Panels within the Extension are sub-units of a single surface. The design extends SS1's inter-surface independence to intra-surface independence. While the product owner's rationale ("panels are panels, they need to be self contained") justifies this, the design should acknowledge it is extending SS1's scope, not merely complying with it. This matters because other SS1-adjacent behaviors (e.g., "persisted active profile is a default for new sessions") need to be re-evaluated — does "new session" mean "new panel" now?

**Suggestion:** Explicitly state whether each panel is a "session" in Constitution terms, and reconcile the "default for new sessions" language with the per-panel model.
