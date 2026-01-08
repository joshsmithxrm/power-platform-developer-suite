# TUI Polish Tracker

## How to Use

This file tracks incremental UX improvements during TUI development iterations.

- Add feedback items under "Open" with `- [ ]` checkbox and date
- Mark items complete by moving to "Done" section
- For bugs/features needing discussion, create a GitHub Issue instead
- **This file is deleted during pre-PR** - it's iteration scaffolding, not permanent docs

## Status

**Phase:** MVP Polish
**Last Updated:** 2026-01-07

## Open Feedback

- [ ] **Silent auth not working - browser prompt on every TUI startup** (reported: 2026-01-07)
  - **Symptom:** Running `ppds` prompts for browser auth every time instead of using cached token
  - **Evidence:** HomeAccountId persisted in profiles.json, msal_token_cache.bin exists
  - **Debug log:** 13-second delay during WarmPoolAsync = interactive auth happened
  - **Root cause:** Unknown - `AcquireTokenSilent` failing silently, falling back to interactive
  - **Next steps:**
    1. Add diagnostic logging to `InteractiveBrowserCredentialProvider.GetTokenAsync()`
    2. Add logging to `MsalAccountHelper.FindAccountAsync()`
    3. Diagnose why account lookup or silent auth is failing
    4. Fix root cause
  - **Key files:**
    - `src/PPDS.Auth/Credentials/InteractiveBrowserCredentialProvider.cs`
    - `src/PPDS.Auth/Credentials/MsalAccountHelper.cs`
    - `src/PPDS.Auth/Credentials/MsalClientBuilder.cs`

## Done

- [x] **True pool warming at startup** (fixed: 2026-01-07, #292)
  - Added `WarmPoolAsync()` that calls `pool.EnsureInitializedAsync()` during startup
  - Auth now triggers at TUI launch, not first query
  - Note: Auth still happening but now at correct time (startup vs first query)

- [x] **Pool invalidated unexpectedly after profile/environment switch** (fixed: 2026-01-07, ADR-0018)
  - Root cause: `MainWindow.SetEnvironmentAsync()` called `InvalidateAsync()` directly
  - Fix: Now calls `_session.SetEnvironmentAsync()` which fires `EnvironmentChanged` event
  - Added session isolation - TUI switches don't update global profiles.json

- [x] **Documentation showed `ppds -i` instead of bare `ppds`** (fixed: 2026-01-07)
  - Fixed CLAUDE.md, ADR-0018, ADR-0024, ADR-0028, README.md
  - Bare `ppds` is the primary TUI experience

- [x] Token cache reuse across sessions (ADR-0027) - HomeAccountId persistence
- [x] Profile switch re-warms pool with new credentials
- [x] Ctrl+Q quit no longer hangs (disposal timeouts)
- [x] Connection pool warming at startup
- [x] Environment change events
- [x] Debug logging to ~/.ppds/tui-debug.log
- [x] Deadlock fix (MessageBox.Query vs nested Application.Run)
- [x] SQL query error fix (PageNumber conflict with TOP)
- [x] Granular status updates
- [x] DI consistency via InteractiveSession
- [x] Silent failure handling (history save errors show warning)
- [x] PpdsException.UserMessage for user-facing errors
- [x] Dark theme with cyan accents
- [x] Environment-aware status bar colors

## Design Decisions

- **Primary accent:** Cyan (stands out on dark, not harsh)
- **Status bar colors:** Environment-aware for safety (prod=red, dev=green)
- **Session isolation:** TUI switches are session-only, use `ppds auth select` for global
- **Pool warming:** Eager at startup via `EnsureInitializedAsync()`
