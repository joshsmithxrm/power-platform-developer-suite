# Extension & TUI QA Sweep — 2026-03-16

Comprehensive QA pass before scaling out panel implementation. Covers code integrity, interactive verification, parity comparison, and bug fixes.

## Summary

| Category | Findings | Fixed | Deferred |
|----------|----------|-------|----------|
| Bugs (GH issues) | 3 | 3 (#580, #581, #361) | 0 |
| Bugs (discovered) | 3 | 3 (startup race, unnamed profile status bar, env type border) | 0 |
| Silent errors | 7 | 7 (all logging now) | 0 |
| Architecture | 0 violations | — | — |
| Dead code | 1 (EnvironmentDetailsDialog unwired) | 1 (wired to Tools menu) | 0 |
| MCP guardrails | Missing | Added (session locking, allowlist, DML guard) | 0 |
| Constitution | Missing session laws | Added (SS1, SS2) | 0 |

## Commits on `qa/extension-tui-sweep`

1. `e144529` — fix: TDS keybinding conflict, tools state race, silent error logging
2. `8399399` — feat: TDS status fix, environment details dialog, profile status bar
3. `71e20e6` — feat(mcp): session locking, environment allowlist, and DML guard
4. `e9555fc` — fix(ext): profile status bar shows name for unnamed profiles

## Phase 1 — Code Integrity Audit

### Extension Architecture: CLEAN

- All Dataverse operations route through daemon JSON-RPC — no service bypasses
- Webview → Host → Daemon → Application Services chain is correct
- No direct query construction or business logic in extension code
- All 62 commands have working handlers

### TUI Architecture: CLEAN

- All operations route through Application Services — no direct Dataverse calls
- TDS toggle implementation correctly scoped to SqlQueryScreen
- HotkeyRegistry priority (Dialog > Screen > Global) is correct
- Status bar is fully interactive with mouse event handling

### Error Handling

**Fixed — 7 silent catches now log:**
- `extension.ts:200` — refreshToolsState catch (was silently disabling tools tree)
- `daemonClient.ts:819` — heartbeat error (was discarding error details)
- `QueryPanel.ts:154` — IntelliSense failure
- `QueryPanel.ts:223,269` — environment color fetch (2 locations)
- `SolutionsPanel.ts:150,173` — environment color fetch (2 locations)

## Phase 2 — Bug Fixes

### #580 — TDS Keybinding Conflict (FIXED)

**Root cause:** Terminals send the same keycode for `Ctrl+T` and `Ctrl+Shift+T`. The global-scope `Ctrl+T` (new tab) always matched before the screen-scope `Ctrl+Shift+T` (TDS toggle).

**Fix:** Changed TDS toggle to `F10`. Added comment documenting the terminal limitation. `SqlQueryScreen.cs:350`

### #581 — TDS Menu Toggle Status Label (FIXED)

**Root cause:** Terminal.Gui 1.x doesn't auto-repaint labels when their Text property changes via a menu item callback. The subsequent `NotifyMenuChanged()` → `RebuildMenuBar()` redraws the menu bar but doesn't propagate to the `_statusLabel` in the content area.

**Fix:** Added `_statusLabel.SetNeedsDisplay()` after setting text in `ToggleTdsEndpoint()`. `SqlQueryScreen.cs:861`

### #361 — Extension Profile Status Bar (IMPLEMENTED)

**Design decision:** Profile indicator only (not environment). Environment context is per-panel and already shown in panel toolbars. Adding environment to the global status bar would be ambiguous with multiple panels open.

**Implementation:** `profileStatusBar.ts` — 72 lines. Shows `$(account) ProfileName`, click to switch via `ppds.listProfiles`. Hidden when daemon not ready. Refreshes on `onDidChangeState`, `onDidReconnect`, and profile commands.

### Startup Race — "(no profile)" Bug (FIXED)

**Root cause:** `refreshToolsState()` was called immediately at extension activation, before the daemon finished starting. The daemon `authList()` call failed, the silent catch set `hasActiveProfile(false)`, and nothing ever retried.

**Fix:** Removed the immediate call. Added `client.onDidChangeState('ready')` listener that triggers `refreshToolsState()` + `profileTreeProvider.refresh()` when daemon becomes available. Also added `activeProfileIndex` fallback check.

### Unnamed Profile Status Bar (FIXED)

**Root cause:** `activeProfile` in the `auth/list` response is the profile's `Name` property, which is null for profiles created without an explicit name. The profile status bar only checked this field.

**Fix:** Fall back to `profiles.find(p => p.isActive)` and display `Profile ${index}` for unnamed profiles. Found during visual QA via `@webview-cdp`.

## Phase 3 — Interactive Verification

### Extension (via @webview-cdp)

| Surface | Status | Notes |
|---------|--------|-------|
| Profile status bar | PASS | Shows "Profile 2", click to switch |
| Tools tree (no profile) | PASS | No "(no profile)" suffix when profile active |
| Profiles tree | PASS | Shows 2 profiles, green checkmark on active |
| Data Explorer panel | PASS | Query execution, results table, filter, export, history |
| Data Explorer toolbar | PASS | Execute, Export, History, env picker present |
| Environment picker | PASS | Shows "DEV" with dropdown |
| SQL syntax highlighting | PASS | Keywords highlighted in editor |
| Query results | PASS | 5 rows, columns, Load More, execution time |
| Filter results | PASS | Typed "Test" → "Showing 8 of 10 rows", correctly filters across all columns |
| Load More | PASS | Loaded 10 → 20 rows with paging cookie, status updates correctly |
| Query history | PASS | Shows previous queries with timestamps, row counts, execution times |
| "..." overflow menu | PASS | Shows Load Query, Open in Notebook, Explain Query, TDS Read Replica |
| Error handling | PASS | Bad table name shows red error banner with Dataverse error message, status = "Error" |
| Multi-panel | PASS | Multiple Data Explorer panels open simultaneously with independent state |
| FetchXML toggle | PASS | SQL→FetchXML converts query correctly, FetchXML→SQL round-trips cleanly with reformatting |
| Export dropdown | PASS | Shows CSV, TSV, JSON, Copy to Clipboard, Save Query |
| Copy to Clipboard | PASS | Shows "Copied 5 rows to clipboard" notification |
| Explain Query | PASS | Opens "Execution Plan" document with FetchXmlScanNode tree and FetchXML source |
| Solutions panel | PASS | Lists 8 solutions (4 managed, 4 unmanaged), filter bar, env picker |
| Solution drill-down | PASS | Expands to show component types with counts (Entity, Plugin Assembly, etc.) |
| Toolbar env color | FIXED | Both borders now render: green top border (env type = development) and green left border (custom color). Fixed by falling back to `env/config/get → resolvedType` when `auth/who → environment.type` is null. |
| Daemon status bar | PASS | Shows checkmark + "PPDS" when ready |
| Extension logs | CLEAN | No PPDS errors in any session. All errors from GitHub Copilot (expected in test instance). |

### TUI (code audit — interactive testing deferred to user)

| Surface | Status | Notes |
|---------|--------|-------|
| TDS toggle (F10) | FIXED | New keybinding, menu shortcut text updated |
| TDS menu toggle | FIXED | SetNeedsDisplay ensures label repaints |
| Environment Details dialog | WIRED | Available via Tools > Environment Details |
| Status bar | VERIFIED (code) | Interactive profile/env selectors, color-coded |
| All dialogs reachable | VERIFIED (code) | 14/16 wired; DeviceCodeDialog used by ProfileCreation |

## Phase 4 — Parity Comparison

### Feature Matrix

| Feature | TUI | Extension | Notes |
|---------|-----|-----------|-------|
| Profile selection | Status bar click (Alt+P) | Sidebar tree + quick pick | Extension is better — more discoverable |
| Environment selection | Status bar click (Alt+E) | Per-panel picker | Extension is better — per-panel scoping |
| Environment details | Tools > Environment Details | Not implemented | TUI ahead — Extension could add as command |
| Environment config | Tools > Configure Environment | Not implemented (panel-level) | TUI ahead |
| Query execution | F5, menu | Execute button | Parity |
| FetchXML preview | Ctrl+Shift+F / F9 | Toolbar button | Parity |
| Query history | Ctrl+Shift+H / F8 | History button | Parity |
| Export | Ctrl+E | Export dropdown | Parity |
| TDS toggle | F10, menu | "..." > TDS Read Replica | Parity |
| Filter results | / key | Filter bar | Extension is better — always visible |
| Execution plan | Ctrl+Shift+E / F7 | "..." > Explain Query | Parity |
| Multi-tab/panel | Ctrl+T tabs | Multiple panel instances | Parity (different paradigms) |
| Keyboard shortcuts dialog | F1 | N/A (VS Code native) | Different by design |
| Solutions browser | Not implemented | Solutions panel | Extension ahead |
| Status bar profile | Full interactive bar | New profile indicator | TUI is richer but Extension is appropriate for VS Code |

### UX Assessment

**Extension strengths:**
- Per-panel environment scoping is excellent for multi-env workflows
- Toolbar buttons are discoverable without memorizing hotkeys
- Profile status bar is clean and unobtrusive
- Tab title shows profile + environment context

**Extension gaps:**
- No environment details command (TUI has it under Tools menu)

**TUI strengths:**
- Rich interactive status bar (click to switch profile/env)
- Comprehensive keyboard shortcuts for everything
- F-key alternatives for Linux terminal compatibility
- Environment details dialog is well-built

**TUI gaps:**
- No solutions browser (planned in panel-parity spec)
- Single query screen type (vs Extension's unlimited panels)
- Tab-based multi-env is less flexible than Extension's per-panel model
- Unnamed SPN profiles show raw application ID in selectors (Extension shows "Profile N" fallback)
- Keyboard shortcuts dialog doesn't scroll to show screen-specific bindings
- "Background operation failed" error on splash before profile selection

### Profile Management Comparison

| Feature | TUI | Extension |
|---------|-----|-----------|
| Profile selector | Dialog with list + 7 action buttons (Select, Create, Rename, Delete, Details, Clear All, Cancel) | Tree view + context menu items |
| Profile creation | Single-screen form: auth method radios, env URL field, Start Authentication button | 3-step wizard: method → name → authenticate |
| Profile naming | Not available during creation | Step 2 of wizard (optional for user auth, required for SPN) |
| Device code display | Inline dialog with code and verification URL | VS Code notification with "Open Browser" and "Copy Code" buttons |
| Environment selector | Dialog with discovered list, details preview, manual URL entry, 4 buttons | Tree view under each profile + "Enter URL manually" item |
| Env discovery | Shows helpful message for SPN profiles ("not supported for ClientSecret") | Only shows discovered envs for active profile |
| Context menus | Profile: 7 dialog buttons; Environment: 4 dialog buttons | Profile: 7 context menu items; Environment: 9 context menu items |

**Key differences:**
- TUI profile creation has **no name field** — profiles can only be named via Rename after creation. Extension has naming in the wizard. This explains the unnamed profile issue we've been seeing.
- Extension's 3-step wizard is more guided; TUI's single-screen form is faster for experienced users.
- Extension has more environment context menu actions (Open in Maker, Open in Dynamics, Test Connection, Copy URL) that TUI doesn't have.
- TUI's environment selector has a **Details preview pane** inline — you can see env details without opening a separate dialog. Extension requires expanding the tree.

### Recommendation

The two UIs are appropriately different. The Extension leverages VS Code's visual paradigm (panels, toolbars, sidebars). The TUI leverages terminal efficiency (hotkeys, menus, status bar). Don't try to make them identical — make them each excellent in their paradigm.

## Phase 5 — MCP Session Guardrails

Added to protect AI agents from accidental cross-environment operations:

- **Session locking:** Profile and environment resolved once at first tool invocation
- **`--profile`/`--environment`:** Lock to specific profile/env at startup
- **`--read-only`:** Disable all DML (INSERT/UPDATE/DELETE)
- **`--allowed-env`:** Restrict `ppds_env_select` to allowlisted URLs only
- **Constitution laws SS1/SS2:** Codified session independence across all surfaces

## Tooling Gaps Discovered

### webview-cdp multi-panel targeting

When multiple webview panels from the same extension are open, `--ext` targets the first match in DOM order, not the visible/focused one. The `connect` command shows targets by URL hash only — no panel titles, no indication of which is active.

**Impact:** Testing multi-panel scenarios requires manual `--target N` guessing.

**Proposed fix:** `connect` should show panel titles; `--ext` should prefer the focused webview; consider `--target active` flag.

## MCP Guardrail Verification

18 new unit tests covering:
- **McpSessionOptions.Parse:** All CLI arg combinations (--profile, --environment, --read-only, --allowed-env)
- **McpSessionOptions.IsEnvironmentAllowed:** Empty list, matching URL, non-matching, trailing slash normalization, case insensitivity
- **McpToolContext.IsReadOnly:** Default false, true when configured
- **McpToolContext.ValidateEnvironmentSwitch:** Throws on no allowlist, throws on non-allowed URL, succeeds on allowed
- **McpToolContext.EnvironmentUrlOverride:** Default null, returns configured value

Total MCP tests: 58 passed, 3 skipped (pre-existing).

## TUI Interactive Verification (via tui-verify)

| Feature | Status | Notes |
|---------|--------|-------|
| Splash screen | PASS | Title, menu bar, status bar all render correctly |
| Profile selector (Alt+P) | PASS | Shows all profiles with identity, select works |
| Environment auto-populate | PASS | Environment loads from profile default |
| Status bar | PASS | Shows "Profile: [2] ... Environment: PPDS Demo - Dev [DEV]" with clickable selectors |
| SQL Query screen | PASS | Opens via Tools menu, tab bar shows env name |
| Query execution (F5) | PASS | "Returned 100 rows in 552ms via Dataverse" |
| Results table | PASS | Virtual table with accountid, name columns, row count footer |
| **F10 TDS toggle** | **PASS** | Status changes to "Mode: TDS Read Replica (read-only, slight delay)", toggles back to "Mode: Dataverse (real-time)" |
| **Query menu TDS toggle** | **PASS** | Menu selection updates status label immediately (#581 fix verified) |
| Query menu items | PASS | All items present with correct shortcuts — F10 shown for TDS (not Ctrl+Shift+T) |
| Tab management (Ctrl+T/W) | PASS | New tab opens, close tab works, tab bar updates |
| **Environment Details dialog** | **PASS** | Shows URL, unique name, version, org ID, user ID, business unit ID, connected as — all loaded via connection pool |
| Tools menu | PASS | SQL Query, Environment Details, Configure Environment — all present with descriptions |
| FetchXML preview (F9) | PASS | Shows transpiled XML with proper formatting |
| History (F8) | PASS | Search box, timestamped queries with row counts |
| Keyboard shortcuts (F1) | PASS | Shows global bindings dynamically. Note: screen-specific bindings (F5-F10) may not be visible without scrolling — minor UX issue |
| About dialog | PASS | Version, tagline, docs URL, GitHub URL |
| Help menu | PASS | About, Keyboard Shortcuts items present |

### TUI UX Notes

- Profile selector shows application ID for unnamed SPN profiles (e.g., `[2] (3b039cb2-...)`) — functional but not pretty. Extension shows "Profile 2" via index fallback, which is cleaner.
- Keyboard shortcuts dialog doesn't scroll well — global bindings fill the visible area, screen-specific bindings (F5, F7, F8, F9, F10) require scrolling that doesn't seem to work with the current dialog layout.
- "Background operation failed" error on splash screen before profile selection — appears to be an update check that runs before auth is configured. Non-blocking but noisy.

TUI snapshot test infrastructure has a pre-existing dependency issue (`@microsoft/tui-test` not installed in worktree `node_modules`). Fixed by running `npm install` in the test directory. The engine warning (Node 22 vs required <21) is a known compatibility gap.

## Items NOT Fixed (Future Work)

- ~~Environment type border not rendering~~ — **Fixed** in commit `35ee5be`. Panels now fall back to `env/config/get → resolvedType` when `auth/who → environment.type` is null.
- Environment details command in Extension — low priority, TUI has it
- webview-cdp multi-panel targeting improvements
- TUI: Unnamed SPN profiles show raw application ID in profile selector (should use name or "Profile N" fallback)
- TUI: Keyboard shortcuts dialog doesn't scroll to screen-specific bindings
- TUI: "Background operation failed" error on splash before profile selection (update check without auth)
- TUI: Profile creation dialog has no name field — profiles must be renamed after creation
- TUI: Environment selector could add "Open in Maker" / "Open in Dynamics" actions (Extension has them)
- Extension: Environment context menu could add "Details" action (TUI has it inline)
- TUI snapshot test infrastructure — works after `npm install` but has Node engine warning
- Per-environment DML permissions in MCP — deferred
- MCP audit logging — deferred
