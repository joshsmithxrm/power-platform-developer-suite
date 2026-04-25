# Challenger Findings: Profile/Environment UX Plan (Verification Pass)

**Reviewed:** `docs/plans/2026-04-24-profile-env-ux-plan.md`, `specs/per-panel-environment-scoping.md`
**Date:** 2026-04-24
**Mode:** Scoped verification of 7 original findings + new-issue scan
**Summary:** 0 BLOCKERs, 2 WARNINGs, 1 SUGGESTION

---

## Original Finding Verification

### B1 (QueryPanel bypasses base class) — FIXED

**Evidence:** Task 4.2 Step 7 (plan lines 272-275) now contains explicit sub-steps 7a, 7b, and 7c addressing QueryPanel's `initEnvironment()`, `requestEnvironmentList` handler, and all RPC call sites. The plan references correct line numbers: `initEnvironment` at line 261 (confirmed: actual code line 261), `requestEnvironmentList` at line 214 (confirmed: actual code line 214), and enumerates specific RPC call sites including `queryFetch`, `querySql`, `queryExplain`, `queryComplete`, `queryExport` via `buildExportParams`, and `loadMore`.

**Verdict:** The fix is present, specific, and references correct code locations. FIXED.

### B2 (Missing ~15 files) — FIXED

**Evidence:** The File Map's Modify section (plan lines 29-41) now lists all 9 panel subclass files: `QueryPanel.ts`, `SolutionsPanel.ts`, `ImportJobsPanel.ts`, `PluginTracesPanel.ts`, `ConnectionReferencesPanel.ts`, `EnvironmentVariablesPanel.ts`, `WebResourcesPanel.ts`, `MetadataBrowserPanel.ts`, `PluginsPanel.ts`. Confirmed via glob that these are exactly the 9 `*Panel.ts` files in `src/PPDS.Extension/src/panels/`. The Chunk 4 Files touched list (plan line 227) also includes all 9 panels.

**Verdict:** FIXED.

### B3 (DaemonClient method signatures incorrectly described) — FIXED

**Evidence:** Task 2.1 Step 1 (plan lines 148-151) now explicitly describes both patterns: positional-param methods (e.g., `solutionsList(filter?, includeManaged?, environmentUrl?, includeInternal?)`) and object-param methods (e.g., `querySql(params: { sql: string; environmentUrl?: string; ... })`). Step 2 handles positional-param methods by appending `profileName` as the last parameter. Step 2b handles object-param methods by adding `profileName` to the params type. Verified against actual code: `solutionsList` at line 818 is indeed positional, `querySql` at line 626 is indeed object-based.

**Verdict:** FIXED.

### W1 (Chunk 4 rollback) — FIXED

**Evidence:** Rollback notes for Chunk 4 (plan line 388) now explicitly enumerate all 9 panel subclass files by name.

**Verdict:** FIXED.

### W2 (resolveEnvironmentId missing profileName) — FIXED

**Evidence:** Task 4.2 Step 6 (plan line 270) explicitly addresses changing `await daemon.envList()` to `await daemon.envList({ profileName: this.profileName })` in `resolveEnvironmentId`. Confirmed the actual code at `WebviewPanelBase.ts` line 127 currently calls `daemon.envList()` without parameters — the plan correctly identifies this call site.

**Verdict:** FIXED.

### W3 (Device code callback pool caching) — FIXED

**Evidence:** Task 1.3 Step 3 (plan lines 127-128) is now an explicit investigation step: "read `DaemonConnectionPoolManager.GetOrCreateServiceProviderAsync` to determine whether the `deviceCodeCallback` is re-registered on every call or only on first pool creation." It describes both possible outcomes and the approach for each case. Confirmed from actual code (line 3347): `DaemonDeviceCodeHandler.CreateCallback(_rpc)` is passed to `GetOrCreateServiceProviderAsync` — the investigation is warranted and the branching approach is sound.

**Verdict:** FIXED.

### S4 (Assumption 3 verified) — FIXED

**Evidence:** Open Questions and Assumptions item 3 (plan line 399) is now marked "Verified" and includes the note about QueryPanel's separate flow being explicitly addressed in Task 4.2 Steps 7a-7c.

**Verdict:** FIXED.

---

## New Findings

## BLOCKERs

(none)

## WARNINGs

### [W1-NEW] — envList signature change from positional to object params breaks existing callers

**Evidence:** Task 2.1 Step 3 (plan line 157) describes changing `envList` from its current positional signature `envList(filter?: string, forceRefresh?: boolean)` (confirmed at `daemonClient.ts` line 537) to an object-param signature `envList(params?: { profileName?: string; filter?: string; forceRefresh?: boolean })`. This is a breaking signature change. Existing callers that pass positional args will break at compile time:
- `daemonClient.test.ts` line 299: `client.envList('prod')` — a string is not assignable to the new object param type
- Any call passing `filter` or `forceRefresh` positionally

No-arg callers (`envList()`) will still compile because the object param is optional. However, the plan's Task 2.1 does not call out this as a breaking change or enumerate which callers need updating for the `envList` signature specifically. Step 4 mentions updating tests but only for the `onDeviceCode` handler type, not for `envList`.

Additionally, `resolveEnvironmentId` in Task 4.2 Step 6 and the `showContextPicker` in Task 4.1 Step 3b both use the new object-param syntax (`daemon.envList({ profileName })`), confirming the plan intends the object-param signature. But the existing callers in `profileCommands.ts` (line 459), `browserCommands.ts` (line 33), `environmentPicker.ts` (line 43), `profileTreeView.ts` (line 163), and `DataverseNotebookController.ts` (line 81) are not mentioned as needing updates even though some pass positional filter args.

**Issue:** The plan does not flag the `envList` positional-to-object migration as a breaking change or enumerate which callers must be updated. An implementer could change the signature and discover compile errors in files not covered by the plan.

**Suggestion:** Either (a) add explicit steps to update all `envList` callers that pass positional arguments (at minimum the test file), or (b) keep `envList` with positional params and add `profileName` as a third positional param for consistency with the other positional-param methods. If choosing (a), add `browserCommands.ts`, `profileTreeView.ts`, and `DataverseNotebookController.ts` to the File Map's Leave Alone or Modify section as appropriate.

### [W2-NEW] — QueryPanel `queryComplete` currently omits `environmentUrl`; plan's instruction conflates bug fix with new work

**Evidence:** Task 4.2 Step 7c (plan line 275) says to "add `environmentUrl: this.environmentUrl, profileName: this.profileName` to the params object" for `queryComplete` at line 146. Reading the actual code at QueryPanel.ts lines 146-150, the `queryComplete` call currently passes `{ sql: message.sql, cursorOffset: message.cursorOffset, language: message.language }` — it does NOT pass `environmentUrl` today.

**Issue:** The plan treats adding `environmentUrl` as if it were routine alongside `profileName`, but this is actually fixing a pre-existing bug where completions may run against the wrong environment. An implementer following the plan might not realize that `environmentUrl` was previously missing (not just unlisted). If there was a reason `environmentUrl` was intentionally omitted from `queryComplete` (e.g., completions are environment-agnostic), adding it could change behavior. The plan should be explicit about this being a deliberate fix.

**Suggestion:** Add a note in Step 7c clarifying that `queryComplete` does not currently pass `environmentUrl` and that adding it (alongside `profileName`) is an intentional correction to ensure completions resolve against the panel's target environment.

## SUGGESTIONs

### [S1-NEW] — File Map does not account for `browserCommands.ts` or `DataverseNotebookController.ts`

**Evidence:** `src/PPDS.Extension/src/commands/browserCommands.ts` line 33 calls `daemonClient.envList()`. `src/PPDS.Extension/src/notebooks/DataverseNotebookController.ts` line 81 calls `this.daemon.envList()`. Neither file appears in the File Map (Modify, Leave Alone, or Delete sections). The notebook controller is explicitly called out in the Leave Alone section but only for its status bar — not for its `envList` usage. `browserCommands.ts` is entirely absent.

**Issue:** Minor completeness gap. Both files use no-arg `envList()` calls that will continue to compile under the new object-param signature (since the param is optional). They intentionally use the active profile's discovery, so no `profileName` is needed. But the File Map should acknowledge their existence for traceability.

**Suggestion:** Add `browserCommands.ts` and `DataverseNotebookController.ts` to the Leave Alone section with a note that they intentionally use the active profile for environment discovery (no `profileName` needed).
