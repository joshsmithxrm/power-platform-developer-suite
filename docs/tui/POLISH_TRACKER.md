# TUI Polish Tracker

## How to Use

This file tracks incremental UX improvements during TUI development iterations.

- Add feedback items under "Open" with `- [ ]` checkbox and date
- Mark items complete by moving to "Done" section
- For bugs/features needing discussion, create a GitHub Issue instead
- **This file is deleted during pre-PR** - it's iteration scaffolding, not permanent docs

## When to Use Tracker vs Issues

| Scope | Tool | Examples |
|-------|------|----------|
| Quick polish (<30min) | Tracker | Colors, spacing, text |
| Small UX fix (<1hr) | Tracker | Shortcuts, tooltips |
| Bug with impact | GitHub Issue | Crashes, data loss |
| New feature | GitHub Issue | New screens |
| Architecture | GitHub Issue | Service refactors |

**Rule:** Can fix in current session without discussion? Use Tracker. Otherwise create Issue.

## Status

**Phase:** MVP Debugging
**Last Updated:** 2026-01-07

## Open Feedback

- [ ] **Pool invalidated unexpectedly after profile switch** (reported: 2026-01-07)
  - User switches profile, pool re-warms successfully, then pool is INVALIDATED again
  - SQL query fails with "No environment selected" because `_environmentUrl` is null
  - Debug log shows two unexplained invalidations:
    ```
    [15:27:03] Connection pool warmed successfully
    [15:27:10] Invalidating connection pool... (7s later - WHY?)
    [15:27:24] New profile connection warmed successfully
    [15:27:28] Invalidating connection pool... (4s later - WHY?)
    ```
  - Suspect: Race condition with `EnvironmentChanged` firing on ThreadPool vs UI thread
  - Suspect: Something calling `InvalidateAsync()` or `SetEnvironmentAsync()` unexpectedly
  - Files to investigate:
    - `InteractiveSession.cs` (InvalidateAsync, SetActiveProfileAsync)
    - `MainWindow.cs` (profile/env selector handling)
    - `SqlQueryScreen.cs` (OnEnvironmentChanged, _environmentUrl lifecycle)

## Done

- [x] Token cache reuse across sessions (fixed: 2026-01-07, ADR-0027)
  - Root cause: `HomeAccountId` not persisted after auth, so MSAL couldn't find cached account
  - Added `ProfileStore.UpdateProfileAsync()` for partial profile updates
  - `ProfileConnectionSource` now takes callback to persist HomeAccountId after auth
  - `ProfileServiceFactory` wires callback to save updated profile
  - Result: Auth once in CLI/TUI, no re-prompt in subsequent sessions

- [x] Profile switch re-warms pool with new credentials (fixed: 2026-01-07)
  - Added `InteractiveSession.SetActiveProfileAsync()` that updates profile name and re-warms pool
  - `MainWindow.SetActiveProfileAsync()` now calls session method instead of just InvalidateAsync
  - `_profileName` changed from readonly to mutable to support profile switching

- [x] Ctrl+Q quit no longer hangs (fixed: 2026-01-07)
  - Added 2s timeout to ServiceProvider disposal in InteractiveSession
  - DataverseConnectionPool.DisposeAsync now uses timeouts for validation task and source disposal
  - Client disposal now fire-and-forget to avoid blocking on ServiceClient.Dispose()


- [x] Connection pool warming - InitializeAsync() warms pool at TUI startup (fixed: 2026-01-07)
- [x] Environment change events - SetEnvironmentAsync() + EnvironmentChanged event (fixed: 2026-01-07)
- [x] Debug logging - TuiDebugLog writes to ~/.ppds/tui-debug.log (fixed: 2026-01-07)
- [x] Deadlock fix - Replaced nested Application.Run() with MessageBox.Query() (fixed: 2026-01-07)
- [x] SQL query error - Removed PageNumber=1 that conflicted with TOP (fixed: 2026-01-07)
- [x] Status updates - Granular status ("Connecting...", "Executing...") (fixed: 2026-01-07)
- [x] DI consistency - All services obtained via InteractiveSession (fixed: 2026-01-07)
- [x] Silent failures - History save errors now show status bar warning (fixed: 2026-01-07)
- [x] Error messages - PpdsException.UserMessage used for user-facing errors (fixed: 2026-01-07)
- [x] Dark theme - Modern dark theme with cyan accents (fixed: 2026-01-07)
- [x] Environment-aware status bar - Production=red, Dev=green, Sandbox=yellow (fixed: 2026-01-07)
- [x] Disabled menu items - Coming Soon suffix, grayed out (fixed: 2026-01-07)
- [x] Theme service tests - TuiThemeServiceTests with environment detection (fixed: 2026-01-07)
- [x] Session tests - InteractiveSessionTests for lifecycle/services (fixed: 2026-01-07)

## Design Decisions

- **Primary accent:** Cyan (stands out on dark, not harsh)
- **Status bar colors:** Environment-aware for safety (prod=red warning, dev=green safe)
- **Menu items:** Disabled with "(Coming Soon)" suffix rather than showing error dialogs
- **Local services:** Transient instances per call, but share underlying ProfileStore singleton
